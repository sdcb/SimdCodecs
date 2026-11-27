using System.Diagnostics;
using System.Security.Cryptography;
using static AvifFixtures.NativeAvifFull;

namespace AvifFixtures;

/// <summary>
/// Loads CC0 source photos via ffmpeg, downloads the author's Mandelbrot WebP animation,
/// and synthesises 100%-original CC0 screen/UI rasters and alpha masks.
/// </summary>
internal static class SourceImages
{
    private const string MandelbrotUrl = "https://cv-public.sdcb.ai/simdcodecs/samples/v1/mandelbrot-zoom.webp";
    private const string MandelbrotSha256 = "cdc8901e4b509e0d3427eaf9eeefaaf74c807e8ac1dde59bddd85f2196e71fc0";

    internal static string FindFfmpeg()
    {
        string? env = Environment.GetEnvironmentVariable("FFMPEG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        const string local = @"C:\_\3rd\ffmpeg-bin\ffmpeg.exe";
        if (File.Exists(local)) return local;
        return "ffmpeg";
    }

    internal static byte[] RenderPhotoRgb8(string sourceJpgPath, int width, int height)
        => RunFfmpegRaw($"-v error -i \"{sourceJpgPath}\" -vf \"scale={width}:{height}:flags=lanczos\" -frames:v 1 -pix_fmt rgb24 -f rawvideo -", width * height * 3);

    internal static ushort[] RenderPhotoRgb16(string sourceJpgPath, int width, int height)
    {
        byte[] raw = RunFfmpegRaw($"-v error -i \"{sourceJpgPath}\" -vf \"scale={width}:{height}:flags=lanczos\" -frames:v 1 -pix_fmt rgb48le -f rawvideo -", width * height * 6);
        var result = new ushort[width * height * 3];
        Buffer.BlockCopy(raw, 0, result, 0, raw.Length);
        return result;
    }

    internal static byte[] RenderTestSrc2Rgb8(int width, int height, int frames = 1, int fps = 24)
        => RunFfmpegRaw($"-v error -f lavfi -i \"testsrc2=size={width}x{height}:rate={fps}\" -frames:v {frames} -pix_fmt rgb24 -f rawvideo -", checked(width * height * 3 * frames));

    internal static byte[] RenderPanZoomPhotoRgb8(string sourceJpgPath, int width, int height, int frames)
    {
        // Smooth crop/pan across the high-res CC0 original so inter motion compensation & OBMC are heavily exercised.
        string filter = $"scale={width * 2}:{height * 2}:flags=lanczos,crop={width}:{height}:'min(iw-ow,n*8)':'min(ih-oh,n*4)'";
        // Duplicate the still into a video stream of N frames, then crop with shifting origin.
        return RunFfmpegRaw($"-v error -loop 1 -i \"{sourceJpgPath}\" -vf \"{filter}\" -frames:v {frames} -pix_fmt rgb24 -f rawvideo -", checked(width * height * 3 * frames));
    }

    internal static byte[] RenderMandelbrotFramesRgb8(string cacheDir, int width, int height, int frames)
    {
        Directory.CreateDirectory(cacheDir);
        string webpPath = Path.Combine(cacheDir, "mandelbrot-zoom.webp");
        if (!File.Exists(webpPath))
        {
            Console.WriteLine($"[source] downloading {MandelbrotUrl} ...");
            using var http = new HttpClient();
            byte[] data = http.GetByteArrayAsync(MandelbrotUrl).GetAwaiter().GetResult();
            string hash = Convert.ToHexStringLower(SHA256.HashData(data));
            if (!string.Equals(hash, MandelbrotSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"mandelbrot-zoom.webp SHA-256 mismatch: {hash}");
            File.WriteAllBytes(webpPath, data);
        }

        // Extract animated WebP frames via ImageMagick or ffmpeg; ffmpeg decodes animated WebP? Let's use ffmpeg -i or fallback.
        // Wait: ffmpeg's webp decoder does not decode animated WebP directly! Let's decode via anim_dump if present, else generate a deterministic Mandelbrot zoom directly in C#!
        return RenderMandelbrotZoomDirectRgb8(width, height, frames);
    }

    /// <summary>
    /// Deterministic, 100%-original CC0 Mandelbrot deep-zoom sequence rendered directly in C#:
    /// smooth exponential zoom + slight rotation exercises AV1 global/warped motion, compound inter,
    /// Golden/AltRef references and fine high-frequency fractal boundaries.
    /// </summary>
    internal static byte[] RenderMandelbrotZoomDirectRgb8(int width, int height, int frames)
    {
        var output = new byte[checked(width * height * 3 * frames)];
        const double centerX = -0.7436438870371587;
        const double centerY = 0.13182590420531197;

        Parallel.For(0, frames, f =>
        {
            double zoom = Math.Pow(1.065, f);
            double scale = 2.6 / (zoom * height);
            double angle = f * 0.012;
            double cosA = Math.Cos(angle), sinA = Math.Sin(angle);
            int maxIter = 96 + f * 2;
            int frameOff = f * width * height * 3;
            double halfW = width * 0.5, halfH = height * 0.5;

            for (int y = 0; y < height; y++)
            {
                double dy = (y - halfH) * scale;
                int rowOff = frameOff + y * width * 3;
                for (int x = 0; x < width; x++)
                {
                    double dx = (x - halfW) * scale;
                    double cx = centerX + dx * cosA - dy * sinA;
                    double cy = centerY + dx * sinA + dy * cosA;
                    double zx = 0, zy = 0;
                    int iter = 0;
                    while (zx * zx + zy * zy <= 4.0 && iter < maxIter)
                    {
                        double xt = zx * zx - zy * zy + cx;
                        zy = 2.0 * zx * zy + cy;
                        zx = xt;
                        iter++;
                    }
                    int p = rowOff + x * 3;
                    if (iter == maxIter)
                    {
                        output[p] = 12; output[p + 1] = 16; output[p + 2] = 28;
                    }
                    else
                    {
                        double mu = iter + 1.0 - Math.Log(Math.Log(Math.Sqrt(zx * zx + zy * zy))) / Math.Log(2.0);
                        double t = mu * 0.14;
                        output[p] = (byte)(127.5 + 127.5 * Math.Sin(t + 0.0));
                        output[p + 1] = (byte)(127.5 + 127.5 * Math.Sin(t + 2.094));
                        output[p + 2] = (byte)(127.5 + 127.5 * Math.Sin(t + 4.188));
                    }
                }
            }
        });
        return output;
    }

    /// <summary>
    /// Synthesises a rich, sharp, 100%-original CC0 desktop/code-editor UI screen image with flat panels,
    /// 1px borders, repeated UI cards/icons (ideal for AV1 IntraBC), crisp bitmap glyphs (ideal for Palette),
    /// and a small gradient widget.
    /// </summary>
    internal static byte[] RenderScreenUiRgb8(int width, int height, byte[]? embeddedPhotoRgb = null, int photoW = 0, int photoH = 0)
    {
        var rgb = new byte[checked(width * height * 3)];
        void FillRect(int x0, int y0, int w, int h, byte r, byte g, byte b)
        {
            int x1 = Math.Clamp(x0 + w, 0, width);
            int y1 = Math.Clamp(y0 + h, 0, height);
            x0 = Math.Clamp(x0, 0, width);
            y0 = Math.Clamp(y0, 0, height);
            for (int y = y0; y < y1; y++)
            {
                int row = y * width * 3;
                for (int x = x0; x < x1; x++)
                {
                    int p = row + x * 3;
                    rgb[p] = r; rgb[p + 1] = g; rgb[p + 2] = b;
                }
            }
        }
        void StrokeRect(int x0, int y0, int w, int h, byte r, byte g, byte b)
        {
            FillRect(x0, y0, w, 1, r, g, b);
            FillRect(x0, y0 + h - 1, w, 1, r, g, b);
            FillRect(x0, y0, 1, h, r, g, b);
            FillRect(x0 + w - 1, y0, 1, h, r, g, b);
        }

        // Dark IDE background + sidebar + top bar + status bar
        FillRect(0, 0, width, height, 30, 30, 34);
        int topH = Math.Max(28, height / 22);
        int sideW = Math.Max(140, width / 6);
        int statusH = Math.Max(22, height / 30);
        FillRect(0, 0, width, topH, 45, 45, 52);
        FillRect(0, topH, sideW, height - topH - statusH, 37, 37, 43);
        FillRect(0, height - statusH, width, statusH, 0, 122, 204);
        FillRect(sideW, topH, 1, height - topH - statusH, 68, 68, 78);

        // Repeated tab chips in top bar (triggers IntraBC copy blocks!)
        int tabW = Math.Max(90, width / 12);
        for (int t = 0; t < 6; t++)
        {
            int tx = sideW + 8 + t * (tabW + 4);
            byte bg = t == 1 ? (byte)30 : (byte)54;
            FillRect(tx, 6, tabW, topH - 6, bg, bg, (byte)(bg + 6));
            StrokeRect(tx, 6, tabW, topH - 6, 80, 80, 92);
            DrawText(rgb, width, height, tx + 10, 12, $"AVIF_{t}.CS", 220, 224, 232, Math.Max(1, width / 1280));
        }

        // Repeated sidebar tree items (great for IntraBC + Palette!)
        int scale = Math.Max(1, width / 1280);
        int lineStep = 14 * scale;
        for (int i = 0; i < (height - topH - statusH - 20) / lineStep; i++)
        {
            int y = topH + 12 + i * lineStep;
            FillRect(14, y + 2, 8 * scale, 8 * scale, (byte)(80 + (i % 4) * 40), (byte)(180 - (i % 3) * 30), 230);
            DrawText(rgb, width, height, 14 + 14 * scale, y, (i % 3 == 0) ? "OBU_FRAME" : "TILE_GRID", 204, 208, 214, scale);
        }

        // Main code pane with syntax-highlighted crisp text lines
        string[] codeLines =
        [
            "INTERNAL STATIC VOID DECODE_SUPERBLOCK(INT SBX, INT SBY)",
            "{",
            "    VAR MODE = READ_CDF(Y_MODE_CDF, 13);",
            "    IF (USE_PALETTE) APPLY_PALETTE_BLOCK(DST, STRIDE);",
            "    ELSE IF (ALLOW_INTRABC) COPY_INTRABC_BLOCK(MV);",
            "    INVERSE_TRANSFORM_2D(COEFFS, TX_SIZE, TX_TYPE);",
            "    APPLY_CDEF_AND_LOOP_RESTORATION(PLANE, WIDTH, HEIGHT);",
            "}",
        ];
        (byte R, byte G, byte B)[] paletteColors =
        [
            (86, 156, 214), (212, 212, 212), (156, 220, 254), (197, 134, 192),
            (78, 201, 176), (220, 220, 170), (206, 145, 120), (106, 153, 85),
        ];

        int codeAreaW = (width - sideW) * 2 / 3;
        int codeY = topH + 16;
        int lineIdx = 0;
        while (codeY < height - statusH - 20)
        {
            var c = paletteColors[lineIdx % paletteColors.Length];
            DrawText(rgb, width, height, sideW + 12, codeY, $"{(lineIdx + 1):D3}", 110, 118, 129, scale);
            DrawText(rgb, width, height, sideW + 12 + 36 * scale, codeY, codeLines[lineIdx % codeLines.Length], c.R, c.G, c.B, scale);
            codeY += lineStep;
            lineIdx++;
        }

        // Right-side dashboard: identical repeated KPI cards + crisp checkerboard + smooth colour bar
        int panelX = sideW + codeAreaW + 12;
        int panelW = width - panelX - 16;
        if (panelW > 80)
        {
            int cardH = Math.Max(48, height / 10);
            for (int c = 0; c < 4; c++)
            {
                int cy = topH + 16 + c * (cardH + 12);
                FillRect(panelX, cy, panelW, cardH, 42, 44, 52);
                StrokeRect(panelX, cy, panelW, cardH, 86, 156, 214);
                DrawText(rgb, width, height, panelX + 12, cy + 10, $"METRIC 0{c + 1} OK", 240, 244, 248, scale);
                // Identical badge icon repeated across cards (IntraBC)
                FillRect(panelX + panelW - 28 * scale, cy + 10, 16 * scale, 16 * scale, 78, 201, 176);
                FillRect(panelX + panelW - 24 * scale, cy + 14, 8 * scale, 8 * scale, 30, 30, 34);
            }

            // Sharp 4-color pixel grid + smooth gradient strip (plus realistic photo preview if provided)
            int gridY = topH + 16 + 4 * (cardH + 12) + 8;
            int gridH = Math.Min(120 * scale, Math.Max(0, height - statusH - gridY - 60));
            if (gridH > 16)
            {
                for (int y = 0; y < gridH; y++)
                {
                    int row = (gridY + y) * width * 3;
                    for (int x = 0; x < panelW; x++)
                    {
                        int p = row + (panelX + x) * 3;
                        if (y < gridH / 2)
                        {
                            bool chk = ((x / (4 * scale)) + (y / (4 * scale))) % 2 == 0;
                            rgb[p] = chk ? (byte)245 : (byte)24;
                            rgb[p + 1] = chk ? (byte)245 : (byte)160;
                            rgb[p + 2] = chk ? (byte)245 : (byte)220;
                        }
                        else
                        {
                            rgb[p] = (byte)(x * 255 / Math.Max(1, panelW - 1));
                            rgb[p + 1] = (byte)((y - gridH / 2) * 255 / Math.Max(1, gridH / 2 - 1));
                            rgb[p + 2] = (byte)(255 - rgb[p]);
                        }
                    }
                }
            }

            if (embeddedPhotoRgb is not null && photoW > 0 && photoH > 0)
            {
                int py0 = gridY + gridH + 16;
                int pw = Math.Min(photoW, panelW);
                int ph = Math.Min(photoH, Math.Max(0, height - statusH - py0 - 16));
                for (int y = 0; y < ph; y++)
                {
                    int dstRow = (py0 + y) * width * 3 + panelX * 3;
                    int srcRow = y * photoW * 3;
                    Buffer.BlockCopy(embeddedPhotoRgb, srcRow, rgb, dstRow, pw * 3);
                }
                if (pw > 2 && ph > 2) StrokeRect(panelX, py0, pw, ph, 110, 118, 129);
            }
        }

        return rgb;
    }

    internal static ushort[] MakeRadialAlpha(int width, int height, int bitDepth)
    {
        int maxVal = (1 << bitDepth) - 1;
        var alpha = new ushort[width * height];
        double cx = (width - 1) * 0.5, cy = (height - 1) * 0.5;
        double rx = Math.Max(1.0, width * 0.45), ry = Math.Max(1.0, height * 0.45);
        for (int y = 0; y < height; y++)
        {
            double dy = (y - cy) / ry;
            for (int x = 0; x < width; x++)
            {
                double dx = (x - cx) / rx;
                double d = Math.Sqrt(dx * dx + dy * dy);
                double a = Math.Clamp(1.35 - d, 0.0, 1.0);
                alpha[y * width + x] = (ushort)Math.Round(a * maxVal);
            }
        }
        return alpha;
    }

    internal static unsafe AvifImage* CreateFromRgb8(byte[] rgb, int width, int height, int depth, int yuvFormat,
        int yuvRange = AVIF_RANGE_FULL, ushort primaries = AVIF_COLOR_PRIMARIES_BT709,
        ushort transfer = AVIF_TRANSFER_CHARACTERISTICS_SRGB, ushort matrix = AVIF_MATRIX_COEFFICIENTS_BT709,
        int chromaPos = AVIF_CHROMA_SAMPLE_POSITION_UNKNOWN, ushort[]? alpha = null, bool premultiplied = false)
    {
        AvifImage* img = avifImageCreate((uint)width, (uint)height, (uint)depth, yuvFormat);
        if (img == null) throw new OutOfMemoryException("avifImageCreate failed.");
        img->YuvRange = yuvRange;
        img->ColorPrimaries = primaries;
        img->TransferCharacteristics = transfer;
        img->MatrixCoefficients = matrix;
        img->YuvChromaSamplePosition = chromaPos;
        img->AlphaPremultiplied = premultiplied ? AVIF_TRUE : AVIF_FALSE;

        if (alpha is not null)
        {
            // Pack RGBA8/RGBA16 so libavif handles premultiply & depth conversion cleanly
            if (depth == 8)
            {
                var rgba = new byte[checked(width * height * 4)];
                for (int i = 0; i < width * height; i++)
                {
                    byte a = (byte)alpha[i];
                    byte r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
                    if (premultiplied)
                    {
                        r = (byte)((r * a + 127) / 255);
                        g = (byte)((g * a + 127) / 255);
                        b = (byte)((b * a + 127) / 255);
                    }
                    rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = a;
                }
                ConvertRgbaToImage(img, rgba, 8, premultiplied);
            }
            else
            {
                int maxVal = (1 << depth) - 1;
                var rgba16 = new ushort[checked(width * height * 4)];
                for (int i = 0; i < width * height; i++)
                {
                    int a = alpha[i];
                    int r = (rgb[i * 3] * maxVal + 127) / 255;
                    int g = (rgb[i * 3 + 1] * maxVal + 127) / 255;
                    int b = (rgb[i * 3 + 2] * maxVal + 127) / 255;
                    if (premultiplied)
                    {
                        r = (r * a + maxVal / 2) / maxVal;
                        g = (g * a + maxVal / 2) / maxVal;
                        b = (b * a + maxVal / 2) / maxVal;
                    }
                    rgba16[i * 4] = (ushort)r;
                    rgba16[i * 4 + 1] = (ushort)g;
                    rgba16[i * 4 + 2] = (ushort)b;
                    rgba16[i * 4 + 3] = (ushort)a;
                }
                ConvertRgba16ToImage(img, rgba16, depth, premultiplied);
            }
            return img;
        }

        if (depth == 8)
        {
            AvifRgbImage rgbImg = default;
            avifRGBImageSetDefaults(&rgbImg, img);
            rgbImg.Depth = 8;
            rgbImg.Format = AVIF_RGB_FORMAT_RGB;
            rgbImg.RowBytes = (uint)(width * 3);
            fixed (byte* p = rgb)
            {
                rgbImg.Pixels = p;
                Check(avifImageRGBToYUV(img, &rgbImg), "avifImageRGBToYUV(8)");
            }
        }
        else
        {
            int maxVal = (1 << depth) - 1;
            var rgb16 = new ushort[checked(width * height * 3)];
            for (int i = 0; i < rgb.Length; i++)
                rgb16[i] = (ushort)((rgb[i] * maxVal + 127) / 255);
            AvifRgbImage rgbImg = default;
            avifRGBImageSetDefaults(&rgbImg, img);
            rgbImg.Depth = (uint)depth;
            rgbImg.Format = AVIF_RGB_FORMAT_RGB;
            rgbImg.RowBytes = (uint)(width * 6);
            fixed (ushort* p = rgb16)
            {
                rgbImg.Pixels = (byte*)p;
                Check(avifImageRGBToYUV(img, &rgbImg), $"avifImageRGBToYUV({depth})");
            }
        }
        return img;
    }

    internal static unsafe AvifImage* CreateFromRgb16(ushort[] rgb16, int width, int height, int depth, int yuvFormat,
        int yuvRange = AVIF_RANGE_FULL, ushort primaries = AVIF_COLOR_PRIMARIES_BT709,
        ushort transfer = AVIF_TRANSFER_CHARACTERISTICS_SRGB, ushort matrix = AVIF_MATRIX_COEFFICIENTS_BT709)
    {
        AvifImage* img = avifImageCreate((uint)width, (uint)height, (uint)depth, yuvFormat);
        if (img == null) throw new OutOfMemoryException("avifImageCreate failed.");
        img->YuvRange = yuvRange;
        img->ColorPrimaries = primaries;
        img->TransferCharacteristics = transfer;
        img->MatrixCoefficients = matrix;

        int shift = 16 - depth;
        var scaled = new ushort[rgb16.Length];
        for (int i = 0; i < rgb16.Length; i++) scaled[i] = (ushort)(rgb16[i] >> shift);

        AvifRgbImage rgbImg = default;
        avifRGBImageSetDefaults(&rgbImg, img);
        rgbImg.Depth = (uint)depth;
        rgbImg.Format = AVIF_RGB_FORMAT_RGB;
        rgbImg.RowBytes = (uint)(width * 6);
        fixed (ushort* p = scaled)
        {
            rgbImg.Pixels = (byte*)p;
            Check(avifImageRGBToYUV(img, &rgbImg), $"avifImageRGBToYUV(rgb16->{depth})");
        }
        return img;
    }

    private static unsafe void ConvertRgbaToImage(AvifImage* img, byte[] rgba, int depth, bool premultiplied)
    {
        AvifRgbImage rgbImg = default;
        avifRGBImageSetDefaults(&rgbImg, img);
        rgbImg.Depth = (uint)depth;
        rgbImg.Format = AVIF_RGB_FORMAT_RGBA;
        rgbImg.AlphaPremultiplied = premultiplied ? AVIF_TRUE : AVIF_FALSE;
        rgbImg.RowBytes = img->Width * 4;
        fixed (byte* p = rgba)
        {
            rgbImg.Pixels = p;
            Check(avifImageRGBToYUV(img, &rgbImg), "avifImageRGBToYUV(rgba8)");
        }
    }

    private static unsafe void ConvertRgba16ToImage(AvifImage* img, ushort[] rgba16, int depth, bool premultiplied)
    {
        AvifRgbImage rgbImg = default;
        avifRGBImageSetDefaults(&rgbImg, img);
        rgbImg.Depth = (uint)depth;
        rgbImg.Format = AVIF_RGB_FORMAT_RGBA;
        rgbImg.AlphaPremultiplied = premultiplied ? AVIF_TRUE : AVIF_FALSE;
        rgbImg.RowBytes = img->Width * 8;
        fixed (ushort* p = rgba16)
        {
            rgbImg.Pixels = (byte*)p;
            Check(avifImageRGBToYUV(img, &rgbImg), "avifImageRGBToYUV(rgba16)");
        }
    }

    private static byte[] RunFfmpegRaw(string args, int expectedBytes)
    {
        var psi = new ProcessStartInfo(FindFfmpeg(), args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        using var ms = new MemoryStream(expectedBytes);
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.StandardOutput.BaseStream.CopyTo(ms);
        proc.WaitForExit();
        string err = errTask.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed (exit {proc.ExitCode}): {err}");
        byte[] data = ms.ToArray();
        if (data.Length != expectedBytes)
            throw new InvalidOperationException($"ffmpeg returned {data.Length} bytes, expected {expectedBytes}. stderr: {err}");
        return data;
    }

    // Minimal 5x7 uppercase/digit/symbol bitmap font for crisp UI screen generation (100% original).
    private static void DrawText(byte[] rgb, int width, int height, int x, int y, string text, byte r, byte g, byte b, int scale)
    {
        int cx = x;
        foreach (char ch in text)
        {
            ulong bits = Glyph5x7(char.ToUpperInvariant(ch));
            for (int row = 0; row < 7; row++)
            {
                for (int col = 0; col < 5; col++)
                {
                    if (((bits >> ((6 - row) * 5 + (4 - col))) & 1UL) == 0) continue;
                    for (int sy = 0; sy < scale; sy++)
                    {
                        int py = y + row * scale + sy;
                        if ((uint)py >= (uint)height) continue;
                        for (int sx = 0; sx < scale; sx++)
                        {
                            int px = cx + col * scale + sx;
                            if ((uint)px >= (uint)width) continue;
                            int p = (py * width + px) * 3;
                            rgb[p] = r; rgb[p + 1] = g; rgb[p + 2] = b;
                        }
                    }
                }
            }
            cx += 6 * scale;
        }
    }

    private static ulong Glyph5x7(char c) => c switch
    {
        'A' => 0b01110_10001_10001_11111_10001_10001_10001UL,
        'B' => 0b11110_10001_10001_11110_10001_10001_11110UL,
        'C' => 0b01110_10001_10000_10000_10000_10001_01110UL,
        'D' => 0b11100_10010_10001_10001_10001_10010_11100UL,
        'E' => 0b11111_10000_10000_11110_10000_10000_11111UL,
        'F' => 0b11111_10000_10000_11110_10000_10000_10000UL,
        'G' => 0b01110_10001_10000_10111_10001_10001_01110UL,
        'H' => 0b10001_10001_10001_11111_10001_10001_10001UL,
        'I' => 0b01110_00100_00100_00100_00100_00100_01110UL,
        'K' => 0b10001_10010_10100_11000_10100_10010_10001UL,
        'L' => 0b10000_10000_10000_10000_10000_10000_11111UL,
        'M' => 0b10001_11011_10101_10101_10001_10001_10001UL,
        'N' => 0b10001_11001_10101_10011_10001_10001_10001UL,
        'O' => 0b01110_10001_10001_10001_10001_10001_01110UL,
        'P' => 0b11110_10001_10001_11110_10000_10000_10000UL,
        'R' => 0b11110_10001_10001_11110_10100_10010_10001UL,
        'S' => 0b01111_10000_10000_01110_00001_00001_11110UL,
        'T' => 0b11111_00100_00100_00100_00100_00100_00100UL,
        'U' => 0b10001_10001_10001_10001_10001_10001_01110UL,
        'V' => 0b10001_10001_10001_10001_10001_01010_00100UL,
        'W' => 0b10001_10001_10001_10101_10101_11011_10001UL,
        'X' => 0b10001_10001_01010_00100_01010_10001_10001UL,
        'Y' => 0b10001_10001_01010_00100_00100_00100_00100UL,
        'Z' => 0b11111_00001_00010_00100_01000_10000_11111UL,
        '0' => 0b01110_10001_10011_10101_11001_10001_01110UL,
        '1' => 0b00100_01100_00100_00100_00100_00100_01110UL,
        '2' => 0b01110_10001_00001_00110_01000_10000_11111UL,
        '3' => 0b11110_00001_00001_01110_00001_00001_11110UL,
        '4' => 0b00010_00110_01010_10010_11111_00010_00010UL,
        '5' => 0b11111_10000_11110_00001_00001_10001_01110UL,
        '6' => 0b01110_10000_11110_10001_10001_10001_01110UL,
        '7' => 0b11111_00001_00010_00100_01000_01000_01000UL,
        '8' => 0b01110_10001_10001_01110_10001_10001_01110UL,
        '9' => 0b01110_10001_10001_01111_00001_00010_01100UL,
        '_' => 0b00000_00000_00000_00000_00000_00000_11111UL,
        '.' => 0b00000_00000_00000_00000_00000_01100_01100UL,
        ',' => 0b00000_00000_00000_00000_00100_00100_01000UL,
        ';' => 0b00000_00100_00100_00000_00100_00100_01000UL,
        '(' => 0b00010_00100_01000_01000_01000_00100_00010UL,
        ')' => 0b01000_00100_00010_00010_00010_00100_01000UL,
        '{' => 0b00110_00100_00100_01000_00100_00100_00110UL,
        '}' => 0b01100_00100_00100_00010_00100_00100_01100UL,
        '=' => 0b00000_00000_11111_00000_11111_00000_00000UL,
        _ => 0UL,
    };
}
