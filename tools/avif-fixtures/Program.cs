using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AvifFixtures;
using static AvifFixtures.NativeAvifFull;

unsafe
{
    string repoRoot = FindRepoRoot();
    string originalsDir = Path.Combine(repoRoot, "benchmarks", "Sdcb.SimdCodecs.Jpeg.Benchmarks", ".cache", "corpus", "originals");
    string outDir = Path.Combine(repoRoot, "tests", "Sdcb.SimdCodecs.Tests", "Fixtures", "avif");
    string workDir = Path.Combine(repoRoot, "tools", "avif-fixtures", "out");
    Directory.CreateDirectory(outDir);
    Directory.CreateDirectory(workDir);

    // Optional filter on command line for quick single-fixture iteration, e.g.: dotnet run -- grain
    string? filter = args.Length > 0 ? args[0] : null;

    byte* codecBuf = stackalloc byte[256];
    avifCodecVersions(codecBuf);
    Console.WriteLine($"Generator: libavif {Marshal.PtrToStringUTF8((nint)avifVersion())} ({Marshal.PtrToStringUTF8((nint)codecBuf)})");
    Console.WriteLine($"Output:    {outDir}");

    string fuji = Path.Combine(originalsDir, "sunrise-fuji.jpg");
    string almsee = Path.Combine(originalsDir, "landscape-almsee.jpg");
    string mangrove = Path.Combine(originalsDir, "foliage-mangrove.jpg");
    string teatro = Path.Combine(originalsDir, "architecture-teatro.jpg");
    string night = Path.Combine(originalsDir, "night-alexanderplatz.jpg");
    string carter = Path.Combine(originalsDir, "portrait-carter.jpg");
    byte[] srgbIcc = File.ReadAllBytes(Path.Combine(repoRoot, "src", "Sdcb.SimdCodecs.Color.Icc", "sRGB-v4.icc"));
    byte[] sampleExif = File.ReadAllBytes(Path.Combine(repoRoot, "tests", "Sdcb.SimdCodecs.Tests", "Fixtures", "webp", "metadata.exif"));
    byte[] sampleXmp = File.ReadAllBytes(Path.Combine(repoRoot, "tests", "Sdcb.SimdCodecs.Tests", "Fixtures", "webp", "metadata.xmp"));

    var records = new List<FixtureRecord>();
    int threads = Math.Max(1, Environment.ProcessorCount);

    void Build(string group, string fileName, string notes, Func<byte[]> produce)
    {
        if (filter is not null && !fileName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return;

        string fullPath = Path.Combine(outDir, fileName);
        var sw = Stopwatch.StartNew();
        Console.Write($"[{group}] {fileName,-48} ... ");
        byte[] bytes = produce();
        File.WriteAllBytes(fullPath, bytes);

        var inspected = ObuInspector.InspectAvif(bytes);
        var verified = VerifyWithDav1d(bytes, fileName, out string? dav1dNote);
        string fileSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        sw.Stop();

        Console.WriteLine($"{bytes.Length / 1024.0,7:F1} KB  {sw.Elapsed.TotalSeconds,5:F1}s  {(verified.Ok ? "dav1d:OK" : $"dav1d:{dav1dNote}")}  [{inspected.FeatureTags()}]");
        records.Add(new FixtureRecord(group, fileName, bytes.Length, fileSha, verified, inspected, notes));
    }

    // =========================================================================
    // GROUP A: High-resolution performance & peak-memory benchmarks (8 files)
    // =========================================================================

    Build("A", "perf-photo-mangrove-8192x6144-grid-8b.avif", "50MP 16x12 grid, 8-bit 4:2:0, landscape texture", () =>
    {
        byte[] rgb = SourceImages.RenderPhotoRgb8(mangrove, 8192, 6144);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 8192, 6144, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL);
        try { return EncodeGrid(img, 16, 12, AVIF_CODEC_CHOICE_AOM, quality: 78, speed: 6, threads: threads); }
        finally { avifImageDestroy(img); }
    });

    Build("A", "perf-photo-almsee-4096x3072-grid-10b.avif", "12.6MP 8x6 grid, 10-bit 4:2:0, ICC profile", () =>
    {
        ushort[] rgb16 = SourceImages.RenderPhotoRgb16(almsee, 4096, 3072);
        AvifImage* img = SourceImages.CreateFromRgb16(rgb16, 4096, 3072, 10, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL);
        try
        {
            fixed (byte* icc = srgbIcc) Check(avifImageSetProfileICC(img, icc, (nuint)srgbIcc.Length), "ICC");
            return EncodeGrid(img, 8, 6, AVIF_CODEC_CHOICE_AOM, quality: 86, speed: 6, threads: threads);
        }
        finally { avifImageDestroy(img); }
    });

    Build("A", "perf-photo-teatro-4096x2632-tiles-svt.avif", "10.8MP single-item 4x4 tiles, SVT-AV1, 8-bit 4:2:0", () =>
    {
        byte[] rgb = SourceImages.RenderPhotoRgb8(teatro, 4096, 2632);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 4096, 2632, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_LIMITED);
        try
        {
            return EncodeStill(img, AVIF_CODEC_CHOICE_SVT, quality: 89, speed: 6, threads: threads, tileRowsLog2: 2, tileColsLog2: 2);
        }
        finally { avifImageDestroy(img); }
    });

    Build("A", "perf-photo-fuji-2560x1440-lossless-444.avif", "2.5K 8-bit 4:4:4 lossless (GBR identity, WHT)", () =>
    {
        byte[] rgb = SourceImages.RenderPhotoRgb8(fuji, 2560, 1440);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 2560, 1440, 8, AVIF_PIXEL_FORMAT_YUV444, AVIF_RANGE_FULL,
            AVIF_COLOR_PRIMARIES_BT709, AVIF_TRANSFER_CHARACTERISTICS_SRGB, AVIF_MATRIX_COEFFICIENTS_IDENTITY);
        try
        {
            return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 100, speed: 6, threads: threads, tileRowsLog2: 1, tileColsLog2: 1);
        }
        finally { avifImageDestroy(img); }
    });

    Build("A", "perf-screen-ui-3840x2160-444-8b.avif", "4K UI/code screen + photo preview, 8-bit 4:4:4, Palette + IntraBC", () =>
    {
        byte[] preview = SourceImages.RenderPhotoRgb8(mangrove, 1024, 680);
        byte[] rgb = SourceImages.RenderScreenUiRgb8(3840, 2160, preview, 1024, 680);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 3840, 2160, 8, AVIF_PIXEL_FORMAT_YUV444, AVIF_RANGE_FULL);
        try
        {
            return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 92, speed: 6, threads: threads, tileRowsLog2: 1, tileColsLog2: 2,
                csOptions: [("enable-palette", "1"), ("enable-intrabc", "1"), ("tune", "ssim")]);
        }
        finally { avifImageDestroy(img); }
    });

    Build("A", "perf-photo-night-3840x2160-grain-10b.avif", "4K night scene, 10-bit 4:2:0 + Film Grain synthesis", () =>
    {
        ushort[] rgb16 = SourceImages.RenderPhotoRgb16(night, 3840, 2160);
        AvifImage* img = SourceImages.CreateFromRgb16(rgb16, 3840, 2160, 10, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_LIMITED);
        try
        {
            return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 88, speed: 6, threads: threads, tileRowsLog2: 1, tileColsLog2: 1,
                csOptions: [("film-grain-test", "1")]);
        }
        finally { avifImageDestroy(img); }
    });

    Build("A", "perf-photo-carter-3840x2560-p2-12b-422.avif", "10MP portrait, Profile 2, 12-bit 4:2:2", () =>
    {
        ushort[] rgb16 = SourceImages.RenderPhotoRgb16(carter, 3840, 2560);
        AvifImage* img = SourceImages.CreateFromRgb16(rgb16, 3840, 2560, 12, AVIF_PIXEL_FORMAT_YUV422, AVIF_RANGE_FULL);
        try
        {
            return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 90, speed: 6, threads: threads, tileRowsLog2: 1, tileColsLog2: 1);
        }
        finally { avifImageDestroy(img); }
    });

    Build("A", "perf-photo-fuji-2560x1440-alpha-10b.avif", "2.5K landscape + 10-bit straight auxl alpha", () =>
    {
        byte[] rgb = SourceImages.RenderPhotoRgb8(fuji, 2560, 1440);
        ushort[] alpha = SourceImages.MakeRadialAlpha(2560, 1440, 10);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 2560, 1440, 10, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL, alpha: alpha);
        try
        {
            return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 90, speed: 6, threads: threads, qualityAlpha: 92, tileRowsLog2: 1, tileColsLog2: 1);
        }
        finally { avifImageDestroy(img); }
    });

    // =========================================================================
    // GROUP B: AV1 Profile x BitDepth x Subsampling & Syntax Tools (16 files)
    // =========================================================================

    byte[] fuji1080 = SourceImages.RenderPhotoRgb8(fuji, 1920, 1080);
    byte[] almsee1080 = SourceImages.RenderPhotoRgb8(almsee, 1920, 1080);
    byte[] ui1080 = SourceImages.RenderScreenUiRgb8(1920, 1080);
    byte[] carterMono = SourceImages.RenderPhotoRgb8(carter, 1440, 1680);

    Build("B", "photo-fuji-1920x1080-p0-8b-420-aom.avif", "Profile 0, 8-bit 4:2:0, BT.709 limited, libaom", () =>
    {
        AvifImage* img = SourceImages.CreateFromRgb8(fuji1080, 1920, 1080, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_LIMITED);
        try { return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 72, speed: 5, threads: threads); }
        finally { avifImageDestroy(img); }
    });

    Build("B", "photo-fuji-1920x1080-p0-8b-420-svt.avif", "Profile 0, 8-bit 4:2:0, BT.709 full, SVT-AV1", () =>
    {
        AvifImage* img = SourceImages.CreateFromRgb8(fuji1080, 1920, 1080, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL);
        try { return EncodeStill(img, AVIF_CODEC_CHOICE_SVT, quality: 72, speed: 6, threads: threads); }
        finally { avifImageDestroy(img); }
    });

    Build("B", "photo-fuji-1920x1080-p0-10b-420-aom.avif", "Profile 0, 10-bit 4:2:0, BT.709 full, libaom", () =>
    {
        AvifImage* img = SourceImages.CreateFromRgb8(fuji1080, 1920, 1080, 10, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL);
        try { return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 74, speed: 5, threads: threads); }
        finally { avifImageDestroy(img); }
    });

    Build("B", "photo-carter-1440x1680-p0-8b-400-mono.avif", "Profile 0, 8-bit 4:0:0 monochrome", () =>
    {
        AvifImage* img = SourceImages.CreateFromRgb8(carterMono, 1440, 1680, 8, AVIF_PIXEL_FORMAT_YUV400, AVIF_RANGE_FULL);
        try { return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 76, speed: 5, threads: threads); }
        finally { avifImageDestroy(img); }
    });

    Build("B", "photo-carter-1440x1680-p0-10b-400-mono.avif", "Profile 0, 10-bit 4:0:0 monochrome", () =>
    {
        AvifImage* img = SourceImages.CreateFromRgb8(carterMono, 1440, 1680, 10, AVIF_PIXEL_FORMAT_YUV400, AVIF_RANGE_LIMITED);
        try { return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 76, speed: 5, threads: threads); }
        finally { avifImageDestroy(img); }
    });

    Build("B", "screen-ui-1920x1080-p1-8b-444-gbr.avif", "Profile 1, 8-bit 4:4:4, Identity GBR (mc=0)", () =>
    {
        AvifImage* img = SourceImages.CreateFromRgb8(ui1080, 1920, 1080, 8, AVIF_PIXEL_FORMAT_YUV444, AVIF_RANGE_FULL,
            AVIF_COLOR_PRIMARIES_BT709, AVIF_TRANSFER_CHARACTERISTICS_SRGB, AVIF_MATRIX_COEFFICIENTS_IDENTITY);
        try { return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 82, speed: 5, threads: threads); }
        finally { avifImageDestroy(img); }
    });

    Build("B", "screen-ui-1920x1080-p1-10b-444-bt709.avif", "Profile 1, 10-bit 4:4:4, BT.709", () =>
    {
        AvifImage* img = SourceImages.CreateFromRgb8(ui1080, 1920, 1080, 10, AVIF_PIXEL_FORMAT_YUV444, AVIF_RANGE_FULL);
        try { return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 80, speed: 5, threads: threads); }
        finally { avifImageDestroy(img); }
    });

    Build("B", "photo-almsee-1920x1080-p2-8b-422.avif", "Profile 2, 8-bit 4:2:2", () =>
    {
        AvifImage* img = SourceImages.CreateFromRgb8(almsee1080, 1920, 1080, 8, AVIF_PIXEL_FORMAT_YUV422, AVIF_RANGE_FULL);
        try { return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 76, speed: 5, threads: threads); }
        finally { avifImageDestroy(img); }
    });

    Build("B", "photo-almsee-1920x1080-p2-12b-420.avif", "Profile 2, 12-bit 4:2:0", () =>
    {
        ushort[] rgb16 = SourceImages.RenderPhotoRgb16(almsee, 1920, 1080);
        AvifImage* img = SourceImages.CreateFromRgb16(rgb16, 1920, 1080, 12, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL);
        try { return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 78, speed: 5, threads: threads); }
        finally { avifImageDestroy(img); }
    });

    Build("B", "photo-almsee-960x540-p2-12b-444-lossless.avif", "Profile 2, 12-bit 4:4:4 lossless", () =>
    {
        ushort[] rgb16 = SourceImages.RenderPhotoRgb16(almsee, 960, 540);
        AvifImage* img = SourceImages.CreateFromRgb16(rgb16, 960, 540, 12, AVIF_PIXEL_FORMAT_YUV444, AVIF_RANGE_FULL,
            AVIF_COLOR_PRIMARIES_BT709, AVIF_TRANSFER_CHARACTERISTICS_SRGB, AVIF_MATRIX_COEFFICIENTS_IDENTITY);
        try { return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 100, speed: 6, threads: threads); }
        finally { avifImageDestroy(img); }
    });

    Build("B", "screen-text-1280x720-palette-intrabc.avif", "Screen content: Palette + IntraBC forced on", () =>
    {
        byte[] rgb = SourceImages.RenderScreenUiRgb8(1280, 720);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 1280, 720, 8, AVIF_PIXEL_FORMAT_YUV444, AVIF_RANGE_FULL);
        try
        {
            return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 82, speed: 4, threads: threads,
                csOptions: [("enable-palette", "1"), ("enable-intrabc", "1"), ("enable-cdef", "0"), ("enable-restoration", "0")]);
        }
        finally { avifImageDestroy(img); }
    });

    Build("B", "photo-mangrove-1600x1200-superres.avif", "AV1 Superres horizontal upscaling (12/8, SVT-AV1)", () =>
    {
        byte[] rgb = SourceImages.RenderPhotoRgb8(mangrove, 1600, 1200);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 1600, 1200, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL);
        try
        {
            return EncodeStill(img, AVIF_CODEC_CHOICE_SVT, quality: 76, speed: 6, threads: threads,
                csOptions: [("superres-mode", "1"), ("superres-denom", "12"), ("superres-kf-denom", "12")]);
        }
        finally { avifImageDestroy(img); }
    });

    Build("B", "photo-night-1280x720-filmgrain-8b.avif", "8-bit Film Grain synthesis (apply_grain=1, luma+chroma)", () =>
    {
        byte[] rgb = SourceImages.RenderPhotoRgb8(night, 1280, 720);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 1280, 720, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_LIMITED);
        try
        {
            return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 76, speed: 5, threads: threads,
                csOptions: [("film-grain-test", "3")]);
        }
        finally { avifImageDestroy(img); }
    });

    Build("B", "photo-mangrove-1280x960-seg-deltaq-qm.avif", "Segmentation + Quantization Matrix (aq-mode=1, qm=1)", () =>
    {
        byte[] rgb = SourceImages.RenderPhotoRgb8(mangrove, 1280, 960);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 1280, 960, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL);
        try
        {
            return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 68, speed: 4, threads: threads,
                csOptions: [("enable-qm", "1"), ("qm-min", "0"), ("qm-max", "15"), ("aq-mode", "1"), ("deltaq-mode", "0")],
                useNonReducedHeader: true);
        }
        finally { avifImageDestroy(img); }
    });

    Build("B", "screen-testsrc2-1280x720-lr-wiener-sgr.avif", "CDEF + Loop Restoration (Wiener & SGR switchable)", () =>
    {
        byte[] rgb = SourceImages.RenderTestSrc2Rgb8(1280, 720);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 1280, 720, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_LIMITED);
        try
        {
            return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 60, speed: 4, threads: threads,
                csOptions: [("enable-cdef", "1"), ("enable-restoration", "1")]);
        }
        finally { avifImageDestroy(img); }
    });

    Build("B", "photo-fuji-1280x720-chroma-colocated.avif", "4:2:0 chroma_sample_position = COLOCATED (2), BT.601", () =>
    {
        byte[] rgb = SourceImages.RenderPhotoRgb8(fuji, 1280, 720);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 1280, 720, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_LIMITED,
            AVIF_COLOR_PRIMARIES_BT601, AVIF_TRANSFER_CHARACTERISTICS_BT601, AVIF_MATRIX_COEFFICIENTS_BT601,
            chromaPos: AVIF_CHROMA_SAMPLE_POSITION_COLOCATED);
        try { return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 74, speed: 5, threads: threads); }
        finally { avifImageDestroy(img); }
    });

    // =========================================================================
    // GROUP C: Container, Transforms, HDR, Gain Map, Metadata, Edge Sizes (13 files)
    // =========================================================================

    Build("C", "container-mangrove-1599x1199-odd-clap.avif", "Odd canvas 1600x1200 cropped to 1599x1199 via clap", () =>
    {
        byte[] rgb = SourceImages.RenderPhotoRgb8(mangrove, 1600, 1200);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 1600, 1200, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL);
        try
        {
            var crop = new AvifCropRect { X = 0, Y = 0, Width = 1599, Height = 1199 };
            AvifDiagnostics diag = default;
            if (avifCleanApertureBoxFromCropRect(&img->Clap, &crop, 1600, 1200, &diag) == AVIF_FALSE)
                throw new InvalidOperationException("clap conversion failed");
            img->TransformFlags = AVIF_TRANSFORM_CLAP;
            return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 72, speed: 5, threads: threads);
        }
        finally { avifImageDestroy(img); }
    });

    Build("C", "container-fuji-1280x720-clap-irot-imir.avif", "Combined clap + irot(90 CCW) + imir(axis=1)", () =>
    {
        byte[] rgb = SourceImages.RenderPhotoRgb8(fuji, 1280, 720);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 1280, 720, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL);
        try
        {
            var crop = new AvifCropRect { X = 64, Y = 36, Width = 1152, Height = 648 };
            AvifDiagnostics diag = default;
            if (avifCleanApertureBoxFromCropRect(&img->Clap, &crop, 1280, 720, &diag) == AVIF_FALSE)
                throw new InvalidOperationException("clap conversion failed");
            img->IrotAngle = 1; // 90 deg CCW
            img->ImirAxis = 1;  // horizontal flip
            img->TransformFlags = AVIF_TRANSFORM_CLAP | AVIF_TRANSFORM_IROT | AVIF_TRANSFORM_IMIR;
            return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 74, speed: 5, threads: threads);
        }
        finally { avifImageDestroy(img); }
    });

    Build("C", "container-grid-1500x990-3x2-crop.avif", "3x2 grid with non-multiple bottom/right edge crop", () =>
    {
        // libavif AddImageGrid requires equal-sized cells (512x512 => 1536x1024), or last col/row equal with canvas cropped via clap/grid size:
        // Encode 1536x1024 as 3x2 grid of 512x512 cells, with clap cropping to 1500x990.
        byte[] rgb = SourceImages.RenderPhotoRgb8(almsee, 1536, 1024);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 1536, 1024, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL);
        try
        {
            var crop = new AvifCropRect { X = 0, Y = 0, Width = 1500, Height = 990 };
            AvifDiagnostics diag = default;
            avifCleanApertureBoxFromCropRect(&img->Clap, &crop, 1536, 1024, &diag);
            img->TransformFlags = AVIF_TRANSFORM_CLAP;
            return EncodeGrid(img, 3, 2, AVIF_CODEC_CHOICE_AOM, quality: 74, speed: 5, threads: threads);
        }
        finally { avifImageDestroy(img); }
    });

    Build("C", "container-grid-alpha-1024x768-prem.avif", "2x2 grid + 2x2 grid auxl alpha + premultiplied (prem)", () =>
    {
        byte[] rgb = SourceImages.RenderPhotoRgb8(fuji, 1024, 768);
        ushort[] alpha = SourceImages.MakeRadialAlpha(1024, 768, 8);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 1024, 768, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL, alpha: alpha, premultiplied: true);
        try
        {
            return EncodeGrid(img, 2, 2, AVIF_CODEC_CHOICE_AOM, quality: 76, speed: 5, threads: threads, qualityAlpha: 80);
        }
        finally { avifImageDestroy(img); }
    });

    Build("C", "container-iovl-overlay-800x600.avif", "ISO 23008-12 iovl overlay derived image (3 av01 layers)", () =>
    {
        byte[] l0 = EncodeSingleRgb8(SourceImages.RenderPhotoRgb8(fuji, 480, 360), 480, 360, 74);
        byte[] l1 = EncodeSingleRgb8(SourceImages.RenderScreenUiRgb8(400, 300), 400, 300, 80);
        byte[] l2 = EncodeSingleRgb8(SourceImages.RenderTestSrc2Rgb8(240, 180), 240, 180, 74);
        var (o0, c0, p0) = IsobmffPatcher.ExtractPrimaryAv1(l0);
        var (o1, c1, p1) = IsobmffPatcher.ExtractPrimaryAv1(l1);
        var (o2, c2, p2) = IsobmffPatcher.ExtractPrimaryAv1(l2);
        return IsobmffPatcher.BuildIovlOverlay(800, 600, [
            (o0, 480, 360, 20, 20, c0, p0),
            (o1, 400, 300, 360, 140, c1, p1),
            (o2, 240, 180, 120, 380, c2, p2),
        ]);
    });

    Build("C", "container-iden-chain-640x480.avif", "ISO 23008-12 iden derived image + irot/imir", () =>
    {
        byte[] baseAvif = EncodeSingleRgb8(SourceImages.RenderPhotoRgb8(teatro, 640, 480), 640, 480, 76);
        var (obu, av1c, pixi) = IsobmffPatcher.ExtractPrimaryAv1(baseAvif);
        return IsobmffPatcher.BuildIdenDerived(obu, 640, 480, av1c, pixi);
    });

    Build("C", "container-tiny-1x1.avif", "Edge size 1x1 (SIMD tail guard)", () =>
    {
        byte[] rgb = [180, 90, 45];
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 1, 1, 8, AVIF_PIXEL_FORMAT_YUV444, AVIF_RANGE_FULL);
        try { return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 90, speed: 6, threads: 1); }
        finally { avifImageDestroy(img); }
    });

    Build("C", "container-tiny-3x7.avif", "Edge odd size 3x7 4:2:0 + alpha (SIMD tail guard)", () =>
    {
        var rgb = new byte[3 * 7 * 3];
        var alpha = new ushort[3 * 7];
        for (int i = 0; i < 21; i++)
        {
            rgb[i * 3] = (byte)(i * 12);
            rgb[i * 3 + 1] = (byte)(255 - i * 11);
            rgb[i * 3 + 2] = (byte)(80 + i * 7);
            alpha[i] = (ushort)(40 + i * 10);
        }
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 3, 7, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL, alpha: alpha);
        try { return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 90, speed: 6, threads: 1, qualityAlpha: 90); }
        finally { avifImageDestroy(img); }
    });

    Build("C", "color-testsrc2-1280x720-pq-10b-nclx.avif", "HDR BT.2020 PQ (16) 10-bit, nclx colr + clli", () =>
    {
        byte[] rgb = SourceImages.RenderTestSrc2Rgb8(1280, 720);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 1280, 720, 10, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_LIMITED,
            AVIF_COLOR_PRIMARIES_BT2020, AVIF_TRANSFER_CHARACTERISTICS_PQ, AVIF_MATRIX_COEFFICIENTS_BT2020_NCL);
        try
        {
            img->Clli = new AvifContentLightLevelInformationBox { MaxCll = 1000, MaxPall = 400 };
            return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 72, speed: 5, threads: threads);
        }
        finally { avifImageDestroy(img); }
    });

    Build("C", "color-testsrc2-1280x720-hlg-10b-vui.avif", "HDR BT.2020 HLG (18) 10-bit, AV1 sequence header only (no colr box)", () =>
    {
        byte[] rgb = SourceImages.RenderTestSrc2Rgb8(1280, 720);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 1280, 720, 10, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_LIMITED,
            AVIF_COLOR_PRIMARIES_BT2020, AVIF_TRANSFER_CHARACTERISTICS_HLG, AVIF_MATRIX_COEFFICIENTS_BT2020_NCL);
        try
        {
            byte[] withColr = EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: 72, speed: 5, threads: threads);
            return IsobmffPatcher.StripColrBoxInPlace(withColr);
        }
        finally { avifImageDestroy(img); }
    });

    Build("C", "color-fuji-1280x720-icc-exif-xmp-thmb.avif", "Full metadata: ICC + Exif + XMP + thmb thumbnail item", () =>
    {
        byte[] primary = EncodeSingleRgb8(SourceImages.RenderPhotoRgb8(fuji, 1280, 720), 1280, 720, 76);
        byte[] thumb = EncodeSingleRgb8(SourceImages.RenderPhotoRgb8(fuji, 320, 180), 320, 180, 68);
        var (pObu, pAv1c, pPixi) = IsobmffPatcher.ExtractPrimaryAv1(primary);
        var (tObu, tAv1c, tPixi) = IsobmffPatcher.ExtractPrimaryAv1(thumb);
        return IsobmffPatcher.BuildWithThumbnailAndMetadata(pObu, 1280, 720, pAv1c, pPixi, tObu, 320, 180, tAv1c, tPixi, srgbIcc, sampleExif, sampleXmp);
    });

    Build("C", "color-fuji-1280x720-gainmap-tmap.avif", "ISO 21496-1 Gain Map (tmap): SDR base + HDR gain map", () =>
    {
        byte[] sdrRgb = SourceImages.RenderPhotoRgb8(fuji, 1280, 720);
        // Synthesise a bright HDR alternate image (boosted highlights in linear-ish space, tagged BT.2020 PQ 10-bit).
        var hdrRgb16 = new ushort[1280 * 720 * 3];
        for (int i = 0; i < sdrRgb.Length; i++)
        {
            double s = sdrRgb[i] / 255.0;
            double boosted = Math.Min(1.0, Math.Pow(s, 0.72) * 0.92 + 0.05);
            hdrRgb16[i] = (ushort)Math.Round(boosted * 65535.0);
        }
        AvifImage* baseImg = SourceImages.CreateFromRgb8(sdrRgb, 1280, 720, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL,
            AVIF_COLOR_PRIMARIES_BT709, AVIF_TRANSFER_CHARACTERISTICS_SRGB, AVIF_MATRIX_COEFFICIENTS_BT709);
        AvifImage* altImg = SourceImages.CreateFromRgb16(hdrRgb16, 1280, 720, 10, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL,
            AVIF_COLOR_PRIMARIES_BT2020, AVIF_TRANSFER_CHARACTERISTICS_PQ, AVIF_MATRIX_COEFFICIENTS_BT2020_NCL);
        AvifGainMap* gm = avifGainMapCreate();
        try
        {
            gm->Image = avifImageCreate(640, 360, 8, AVIF_PIXEL_FORMAT_YUV420);
            AvifDiagnostics diag = default;
            Check(avifImageComputeGainMap(baseImg, altImg, gm, &diag), "avifImageComputeGainMap", &diag);
            baseImg->GainMap = gm; // ownership transferred to baseImg
            gm = null;
            return EncodeStill(baseImg, AVIF_CODEC_CHOICE_AOM, quality: 76, speed: 5, threads: threads, qualityGainMap: 70);
        }
        finally
        {
            if (gm != null) avifImageDestroy(gm->Image);
            avifImageDestroy(altImg);
            avifImageDestroy(baseImg);
        }
    });

    Build("C", "container-layered-a1lx-a1op-1280x720.avif", "Progressive 3-layer scalable AVIF (a1lx + a1op)", () =>
    {
        byte[] rgb = SourceImages.RenderPhotoRgb8(fuji, 1280, 720);
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, 1280, 720, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL);
        AvifEncoder* enc = avifEncoderCreate();
        AvifRWData output = default;
        try
        {
            enc->CodecChoice = AVIF_CODEC_CHOICE_AOM;
            enc->MaxThreads = threads;
            enc->Speed = 5;
            enc->ExtraLayerCount = 2; // 3 layers total
            int[] qualities = [30, 58, 82];
            for (int l = 0; l < 3; l++)
            {
                enc->Quality = qualities[l];
                Check(avifEncoderAddImage(enc, img, 1, AVIF_ADD_IMAGE_FLAG_NONE), $"layered AddImage {l}", &enc->Diag);
            }
            Check(avifEncoderFinish(enc, &output), "layered Finish", &enc->Diag);
            return new ReadOnlySpan<byte>(output.Data, (int)output.Size).ToArray();
        }
        finally
        {
            avifRWDataFree(&output);
            avifEncoderDestroy(enc);
            avifImageDestroy(img);
        }
    });

    // =========================================================================
    // GROUP D: Animated AVIF sequences (avis) — Inter tools + FPS perf (5 files)
    // =========================================================================

    Build("D", "anim-mandelbrot-1280x720-60f-inter.avif", "60f 720p Mandelbrot zoom, 8-bit 4:2:0, Warped/Global/Compound Inter", () =>
    {
        byte[] frames = SourceImages.RenderMandelbrotZoomDirectRgb8(1280, 720, 60);
        return EncodeSequence(frames, 1280, 720, 60, depth: 8, yuvFormat: AVIF_PIXEL_FORMAT_YUV420,
            quality: 86, speed: 6, threads: threads, timescale: 1000, frameDurationsMs: null, defaultDurationMs: 50,
            repetitionCount: AVIF_REPETITION_COUNT_INFINITE,
            csOptions: [("enable-warped-motion", "1"), ("enable-global-motion", "1"), ("enable-obmc", "1")]);
    });

    Build("D", "anim-photo-fuji-1920x1080-30f-10b.avif", "30f 1080p landscape pan, 10-bit 4:2:0 Inter + OBMC", () =>
    {
        byte[] frames = SourceImages.RenderPanZoomPhotoRgb8(fuji, 1920, 1080, 30);
        return EncodeSequence(frames, 1920, 1080, 30, depth: 10, yuvFormat: AVIF_PIXEL_FORMAT_YUV420,
            quality: 86, speed: 6, threads: threads, timescale: 1000, frameDurationsMs: null, defaultDurationMs: 40,
            repetitionCount: AVIF_REPETITION_COUNT_INFINITE,
            csOptions: [("enable-obmc", "1"), ("enable-warped-motion", "1")]);
    });

    Build("D", "anim-screen-testsrc2-1280x720-24f-444.avif", "24f screen animation, 8-bit 4:4:4 + Inter-Intra", () =>
    {
        byte[] frames = SourceImages.RenderTestSrc2Rgb8(1280, 720, frames: 24, fps: 24);
        return EncodeSequence(frames, 1280, 720, 24, depth: 8, yuvFormat: AVIF_PIXEL_FORMAT_YUV444,
            quality: 80, speed: 6, threads: threads, timescale: 1000, frameDurationsMs: null, defaultDurationMs: 42,
            repetitionCount: AVIF_REPETITION_COUNT_INFINITE,
            csOptions: [("enable-interintra-comp", "1")]);
    });

    Build("D", "anim-alpha-640x360-24f-varfps.avif", "24f alpha track animation, variable durations, repeat=3", () =>
    {
        byte[] frames = SourceImages.RenderTestSrc2Rgb8(640, 360, frames: 24, fps: 12);
        var durations = Enumerable.Range(0, 24).Select(i => (ulong)(i % 3 == 0 ? 120 : 60)).ToArray();
        return EncodeAlphaSequence(frames, 640, 360, 24, quality: 78, qualityAlpha: 80, speed: 6, threads: threads,
            timescale: 1000, frameDurationsMs: durations, repetitionCount: 2);
    });

    Build("D", "anim-alltools-320x240-8f.avif", "8f fast regression: Wedge + DiffWtd + OBMC + Warped + Compound", () =>
    {
        byte[] frames = SourceImages.RenderMandelbrotZoomDirectRgb8(320, 240, 8);
        return EncodeSequence(frames, 320, 240, 8, depth: 8, yuvFormat: AVIF_PIXEL_FORMAT_YUV420,
            quality: 70, speed: 4, threads: threads, timescale: 1000, frameDurationsMs: null, defaultDurationMs: 100,
            repetitionCount: AVIF_REPETITION_COUNT_INFINITE,
            csOptions: [
                ("enable-warped-motion", "1"), ("enable-obmc", "1"), ("enable-interintra-comp", "1"),
                ("enable-diff-wtd-comp", "1"), ("enable-dist-wtd-comp", "1"), ("enable-masked-comp", "1")
            ]);
    });

    // =========================================================================
    // Write summary report & avif-golden.tsv (only when full run without filter)
    // =========================================================================
    long totalBytes = records.Sum(r => r.Bytes);
    Console.WriteLine();
    Console.WriteLine($"Generated {records.Count} fixtures, total = {totalBytes / (1024.0 * 1024.0):F2} MB ({totalBytes:N0} bytes)");
    foreach (var grp in records.GroupBy(r => r.Group))
        Console.WriteLine($"  Group {grp.Key}: {grp.Count(),2} files, {grp.Sum(x => x.Bytes) / (1024.0 * 1024.0),6:F2} MB");

    if (filter is null)
    {
        WriteGoldenTsv(Path.Combine(outDir, "avif-golden.tsv"), records);
        WriteManifestJson(Path.Combine(workDir, "manifest.tsv"), records);
        Console.WriteLine($"Wrote {Path.Combine(outDir, "avif-golden.tsv")}");
        Console.WriteLine($"Wrote {Path.Combine(workDir, "manifest.tsv")}");
    }

    // ── Local helpers ────────────────────────────────────────────────────────

    byte[] EncodeSingleRgb8(byte[] rgb, int w, int h, int q)
    {
        AvifImage* img = SourceImages.CreateFromRgb8(rgb, w, h, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL);
        try { return EncodeStill(img, AVIF_CODEC_CHOICE_AOM, quality: q, speed: 6, threads: threads); }
        finally { avifImageDestroy(img); }
    }

    byte[] EncodeStill(AvifImage* img, int codec, int quality, int speed, int threads,
        int qualityAlpha = -1, int qualityGainMap = -1, int tileRowsLog2 = 0, int tileColsLog2 = 0,
        (int N, int D)? scalingH = null, (int N, int D)? scalingV = null,
        (string Key, string Value)[]? csOptions = null, bool useNonReducedHeader = false)
    {
        AvifEncoder* enc = avifEncoderCreate();
        AvifRWData output = default;
        try
        {
            enc->CodecChoice = codec;
            enc->MaxThreads = threads;
            enc->Speed = speed;
            enc->Quality = quality;
            if (qualityAlpha >= 0) enc->QualityAlpha = qualityAlpha;
            if (qualityGainMap >= 0) enc->QualityGainMap = qualityGainMap;
            enc->TileRowsLog2 = tileRowsLog2;
            enc->TileColsLog2 = tileColsLog2;
            if (scalingH is { } sh) enc->ScalingMode.Horizontal = new AvifFraction { N = sh.N, D = sh.D };
            if (scalingV is { } sv) enc->ScalingMode.Vertical = new AvifFraction { N = sv.N, D = sv.D };
            if (csOptions is not null)
                foreach (var (k, v) in csOptions)
                    Check(avifEncoderSetCodecSpecificOption(enc, k, v), $"csOption {k}={v}", &enc->Diag);

            if (useNonReducedHeader)
            {
                Check(avifEncoderAddImage(enc, img, 1, AVIF_ADD_IMAGE_FLAG_NONE), "avifEncoderAddImage", &enc->Diag);
                Check(avifEncoderFinish(enc, &output), "avifEncoderFinish", &enc->Diag);
            }
            else
            {
                Check(avifEncoderWrite(enc, img, &output), "avifEncoderWrite", &enc->Diag);
            }
            return new ReadOnlySpan<byte>(output.Data, (int)output.Size).ToArray();
        }
        finally
        {
            avifRWDataFree(&output);
            avifEncoderDestroy(enc);
        }
    }

    byte[] EncodeGrid(AvifImage* full, int cols, int rows, int codec, int quality, int speed, int threads, int qualityAlpha = -1)
    {
        if (full->Width % cols != 0 || full->Height % rows != 0)
            throw new ArgumentException($"Grid {cols}x{rows} must divide {full->Width}x{full->Height}.");
        uint cw = full->Width / (uint)cols, ch = full->Height / (uint)rows;
        int cellCount = cols * rows;
        var cells = new nint[cellCount];
        AvifEncoder* enc = avifEncoderCreate();
        AvifRWData output = default;
        try
        {
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    AvifImage* cell = avifImageCreate(cw, ch, full->Depth, full->YuvFormat);
                    var rect = new AvifCropRect { X = (uint)c * cw, Y = (uint)r * ch, Width = cw, Height = ch };
                    Check(avifImageSetViewRect(cell, full, &rect), "avifImageSetViewRect");
                    if (r == 0 && c == 0)
                    {
                        cell->TransformFlags = full->TransformFlags;
                        cell->Clap = full->Clap;
                        cell->IrotAngle = full->IrotAngle;
                        cell->ImirAxis = full->ImirAxis;
                        if (full->Icc.Size > 0)
                            Check(avifImageSetProfileICC(cell, full->Icc.Data, full->Icc.Size), "grid ICC");
                    }
                    cells[r * cols + c] = (nint)cell;
                }
            }

            enc->CodecChoice = codec;
            enc->MaxThreads = threads;
            enc->Speed = speed;
            enc->Quality = quality;
            if (qualityAlpha >= 0) enc->QualityAlpha = qualityAlpha;
            fixed (nint* ptr = cells)
            {
                Check(avifEncoderAddImageGrid(enc, (uint)cols, (uint)rows, (AvifImage**)ptr, AVIF_ADD_IMAGE_FLAG_SINGLE), "AddImageGrid", &enc->Diag);
            }
            Check(avifEncoderFinish(enc, &output), "Grid Finish", &enc->Diag);
            return new ReadOnlySpan<byte>(output.Data, (int)output.Size).ToArray();
        }
        finally
        {
            avifRWDataFree(&output);
            avifEncoderDestroy(enc);
            foreach (nint p in cells) if (p != 0) avifImageDestroy((AvifImage*)p);
        }
    }

    byte[] EncodeSequence(byte[] allFramesRgb, int w, int h, int frameCount, int depth, int yuvFormat,
        int quality, int speed, int threads, ulong timescale, ulong[]? frameDurationsMs, ulong defaultDurationMs,
        int repetitionCount, (string Key, string Value)[]? csOptions = null)
    {
        AvifEncoder* enc = avifEncoderCreate();
        AvifRWData output = default;
        try
        {
            enc->CodecChoice = AVIF_CODEC_CHOICE_AOM;
            enc->MaxThreads = threads;
            enc->Speed = speed;
            enc->Quality = quality;
            enc->Timescale = timescale;
            enc->RepetitionCount = repetitionCount;
            enc->KeyframeInterval = 30;
            if (csOptions is not null)
                foreach (var (k, v) in csOptions)
                    Check(avifEncoderSetCodecSpecificOption(enc, k, v), $"seq csOption {k}={v}", &enc->Diag);

            int frameBytes = w * h * 3;
            var frameBuf = new byte[frameBytes];
            for (int f = 0; f < frameCount; f++)
            {
                Buffer.BlockCopy(allFramesRgb, f * frameBytes, frameBuf, 0, frameBytes);
                AvifImage* img = SourceImages.CreateFromRgb8(frameBuf, w, h, depth, yuvFormat, AVIF_RANGE_LIMITED);
                try
                {
                    ulong dur = frameDurationsMs is not null ? frameDurationsMs[f] : defaultDurationMs;
                    Check(avifEncoderAddImage(enc, img, dur, AVIF_ADD_IMAGE_FLAG_NONE), $"seq AddImage {f}", &enc->Diag);
                }
                finally { avifImageDestroy(img); }
            }
            Check(avifEncoderFinish(enc, &output), "seq Finish", &enc->Diag);
            return new ReadOnlySpan<byte>(output.Data, (int)output.Size).ToArray();
        }
        finally
        {
            avifRWDataFree(&output);
            avifEncoderDestroy(enc);
        }
    }

    byte[] EncodeAlphaSequence(byte[] allFramesRgb, int w, int h, int frameCount,
        int quality, int qualityAlpha, int speed, int threads, ulong timescale, ulong[] frameDurationsMs, int repetitionCount)
    {
        AvifEncoder* enc = avifEncoderCreate();
        AvifRWData output = default;
        try
        {
            enc->CodecChoice = AVIF_CODEC_CHOICE_AOM;
            enc->MaxThreads = threads;
            enc->Speed = speed;
            enc->Quality = quality;
            enc->QualityAlpha = qualityAlpha;
            enc->Timescale = timescale;
            enc->RepetitionCount = repetitionCount;

            int frameBytes = w * h * 3;
            var frameBuf = new byte[frameBytes];
            var alpha = new ushort[w * h];
            for (int f = 0; f < frameCount; f++)
            {
                Buffer.BlockCopy(allFramesRgb, f * frameBytes, frameBuf, 0, frameBytes);
                double cx = w * (0.25 + 0.5 * f / Math.Max(1, frameCount - 1));
                double cy = h * 0.5;
                double rad = h * 0.38;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        double d = double.Hypot(x - cx, y - cy) / rad;
                        alpha[y * w + x] = (ushort)Math.Round(Math.Clamp(1.3 - d, 0.0, 1.0) * 255.0);
                    }
                }
                AvifImage* img = SourceImages.CreateFromRgb8(frameBuf, w, h, 8, AVIF_PIXEL_FORMAT_YUV420, AVIF_RANGE_FULL, alpha: alpha);
                try
                {
                    Check(avifEncoderAddImage(enc, img, frameDurationsMs[f], AVIF_ADD_IMAGE_FLAG_NONE), $"alphaSeq AddImage {f}", &enc->Diag);
                }
                finally { avifImageDestroy(img); }
            }
            Check(avifEncoderFinish(enc, &output), "alphaSeq Finish", &enc->Diag);
            return new ReadOnlySpan<byte>(output.Data, (int)output.Size).ToArray();
        }
        finally
        {
            avifRWDataFree(&output);
            avifEncoderDestroy(enc);
        }
    }

    static Dav1dVerification VerifyWithDav1d(byte[] avif, string fileName, out string? errorNote)
    {
        errorNote = null;
        AvifDecoder* dec = avifDecoderCreate();
        try
        {
            dec->CodecChoice = AVIF_CODEC_CHOICE_DAV1D;
            dec->MaxThreads = Math.Max(1, Environment.ProcessorCount);
            dec->StrictFlags = 0;
            dec->ImageContentToDecode = AVIF_IMAGE_CONTENT_ALL;

            fixed (byte* src = avif)
            {
                int r = avifDecoderSetIOMemory(dec, src, (nuint)avif.Length);
                if (r != AVIF_RESULT_OK) { errorNote = ErrText(r, dec); return new Dav1dVerification(false, 0, 0, 0, "?", false, 0, ""); }
                r = avifDecoderParse(dec);
                if (r != AVIF_RESULT_OK) { errorNote = ErrText(r, dec); return new Dav1dVerification(false, 0, 0, 0, "?", false, 0, ""); }

                int frameCount = Math.Max(1, dec->ImageCount);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                Span<byte> hdr = stackalloc byte[12];
                int w = 0, h = 0, depth = 0, yuvFmt = 0;
                bool hasAlpha = dec->AlphaPresent != 0;

                for (int f = 0; f < frameCount; f++)
                {
                    r = avifDecoderNextImage(dec);
                    if (r != AVIF_RESULT_OK) { errorNote = $"frame {f}: {ErrText(r, dec)}"; return new Dav1dVerification(false, w, h, depth, FmtName(yuvFmt), hasAlpha, f, ""); }
                    AvifImage* img = dec->Image;
                    w = (int)img->Width;
                    h = (int)img->Height;
                    depth = (int)img->Depth;
                    yuvFmt = img->YuvFormat;
                    int sampleBytes = depth > 8 ? 2 : 1;

                    int[] channels = yuvFmt == AVIF_PIXEL_FORMAT_YUV400 ? [AVIF_CHAN_Y] : [AVIF_CHAN_Y, AVIF_CHAN_U, AVIF_CHAN_V];
                    foreach (int ch in channels)
                        HashPlane(sha, img, ch, sampleBytes, hdr);
                    if (hasAlpha && avifImagePlane(img, AVIF_CHAN_A) != null)
                        HashPlane(sha, img, AVIF_CHAN_A, sampleBytes, hdr);
                }

                string digest = Convert.ToHexStringLower(sha.GetCurrentHash());
                return new Dav1dVerification(true, w, h, depth, FmtName(yuvFmt), hasAlpha, frameCount, digest);
            }
        }
        finally { avifDecoderDestroy(dec); }
    }

    static void HashPlane(IncrementalHash sha, AvifImage* img, int ch, int sampleBytes, Span<byte> hdr)
    {
        byte* plane = avifImagePlane(img, ch);
        uint pw = avifImagePlaneWidth(img, ch);
        uint ph = avifImagePlaneHeight(img, ch);
        uint stride = avifImagePlaneRowBytes(img, ch);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[0..4], (int)pw);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[4..8], (int)ph);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[8..12], sampleBytes);
        sha.AppendData(hdr);
        int rowBytes = (int)pw * sampleBytes;
        for (uint y = 0; y < ph; y++)
            sha.AppendData(new ReadOnlySpan<byte>(plane + (long)y * stride, rowBytes));
    }

    static string ErrText(int r, AvifDecoder* dec)
    {
        string name = Marshal.PtrToStringUTF8((nint)avifResultToString(r)) ?? r.ToString();
        string diag = Marshal.PtrToStringUTF8((nint)dec->Diag.Error) ?? "";
        return string.IsNullOrEmpty(diag) ? name : $"{name} ({diag})";
    }

    static string FmtName(int fmt) => fmt switch
    {
        AVIF_PIXEL_FORMAT_YUV444 => "444",
        AVIF_PIXEL_FORMAT_YUV422 => "422",
        AVIF_PIXEL_FORMAT_YUV420 => "420",
        AVIF_PIXEL_FORMAT_YUV400 => "400",
        _ => "none",
    };

    static void WriteGoldenTsv(string path, List<FixtureRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AVIF decode golden: file, width, height, depth, subsampling, hasAlpha, frames, SHA-256 of decoded Y/Cb/Cr(/A) planes across all frames.");
        sb.AppendLine("# Recorded from libavif 1.4.2 + dav1d 1.5.5 (see tests/Sdcb.SimdCodecs.Tests/Oracle/avif/README.md).");
        foreach (var r in records.Where(x => x.Dav1d.Ok).OrderBy(x => x.FileName, StringComparer.Ordinal))
        {
            var d = r.Dav1d;
            sb.AppendLine($"{r.FileName}\t{d.Width}\t{d.Height}\t{d.Depth}\t{d.Subsampling}\t{(d.HasAlpha ? 1 : 0)}\t{d.Frames}\t{d.PlanesSha256}");
        }
        File.WriteAllText(path, sb.ToString());
    }

    static void WriteManifestJson(string path, List<FixtureRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# group\tfile\tbytes\tfile_sha256\tdav1d_ok\tw\th\tdepth\tsub\talpha\tframes\tfeatures\tnotes");
        foreach (var r in records)
        {
            var d = r.Dav1d;
            sb.AppendLine($"{r.Group}\t{r.FileName}\t{r.Bytes}\t{r.FileSha256}\t{(d.Ok ? "OK" : "SKIP")}\t{d.Width}\t{d.Height}\t{d.Depth}\t{d.Subsampling}\t{(d.HasAlpha ? 1 : 0)}\t{d.Frames}\t{r.Obu.FeatureTags()}\t{r.Notes}");
        }
        File.WriteAllText(path, sb.ToString());
    }

    static string FindRepoRoot()
    {
        string dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 8; i++)
        {
            if (File.Exists(Path.Combine(dir, "SimdCodecs.slnx"))) return dir;
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent is null) break;
            dir = parent;
        }
        throw new DirectoryNotFoundException("Could not find SimdCodecs.slnx.");
    }
}

internal readonly record struct Dav1dVerification(bool Ok, int Width, int Height, int Depth, string Subsampling, bool HasAlpha, int Frames, string PlanesSha256);
internal sealed record FixtureRecord(string Group, string FileName, long Bytes, string FileSha256, Dav1dVerification Dav1d, ObuInspector.ObuSummary Obu, string Notes);
