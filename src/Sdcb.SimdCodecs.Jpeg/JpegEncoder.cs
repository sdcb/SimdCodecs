using static Sdcb.SimdCodecs.Jpeg.JpegEncoderTables;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Pure-managed baseline JPEG (ITU-T T.81) encoder. Sequential DCT, fixed Annex K
/// Huffman tables, 4:4:4 or 4:2:0 chroma subsampling, quality 1..100.
///
/// <para>
/// This is a faithful C# port of the JPEG writer in <c>stb_image_write.h</c>
/// (Sean Barrett / Jon Olick's <c>jo_jpeg</c>, public domain / MIT) — the same
/// clean-room-against-a-reference discipline the <see cref="JpegDecoder"/> was
/// built with. Output is <b>byte-for-byte identical</b> to that reference for the
/// same pixels, quality, and (quality-derived) subsampling; the encoder tests pin
/// this against the reference binary and freeze it as a committed golden digest.
/// The AAN float forward-DCT, the fixed-point-free quantisation rounding, and the
/// bit writer are reproduced operation-for-operation so the floating-point results
/// match across platforms (no FMA contraction — plain multiply-add throughout).
/// </para>
/// </summary>
public static class JpegEncoder
{
    /// <summary>
    /// Encodes an interleaved 8-bit raster to a baseline JPEG byte stream.
    /// </summary>
    /// <param name="pixels">Interleaved, row-major, tightly-packed 8-bit samples,
    /// <c>width * height * channels</c> bytes.</param>
    /// <param name="width">Pixel width, 1..65535.</param>
    /// <param name="height">Pixel height, 1..65535.</param>
    /// <param name="channels">Samples per pixel: 1 = grayscale, 2 = gray+alpha
    /// (alpha ignored), 3 = RGB, 4 = RGBA (alpha ignored). Grayscale is replicated
    /// across the colour channels; output is always 3-component YCbCr (matching the
    /// reference encoder — a true 1-component grayscale JPEG is a later milestone).</param>
    /// <param name="options">Quality + subsampling; null uses the defaults (quality 90).</param>
    /// <returns>A complete JPEG byte stream (SOI … EOI).</returns>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> is too small for the geometry.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension, channel count, or quality is out of range.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int channels, JpegEncodeOptions? options = null)
    {
        if (width is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be 1..65535.");
        if (height is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be 1..65535.");
        if (channels is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be 1..4.");
        options ??= new JpegEncodeOptions();
        if (options.Quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(options), options.Quality, "Quality must be 1..100.");

        var exif = ValidateExif(options.Exif);
        var xmp = options.Xmp is { } xmpOptions ? BuildXmpPacket(xmpOptions) : default;

        var required = (long)width * height * channels;
        if (pixels.Length < required)
            throw new ArgumentException($"Pixel buffer too small: {pixels.Length} < {required} (width*height*channels).", nameof(pixels));

        // Subsample decision mirrors stbiw: quality-derived under Auto (using the
        // ORIGINAL quality, before the table remap below), else forced.
        var subsample = options.Subsampling switch
        {
            JpegSubsampling.Chroma444 => false,
            JpegSubsampling.Chroma420 => true,
            _ => options.Quality <= 90,
        };

        // libjpeg-style quality → quant-table scale.
        var q = options.Quality;
        var scale = q < 50 ? 5000 / q : 200 - q * 2;

        Span<byte> yTable = stackalloc byte[64];
        Span<byte> uvTable = stackalloc byte[64];
        Span<float> fdtblY = stackalloc float[64];
        Span<float> fdtblUv = stackalloc float[64];
        BuildTables(scale, yTable, uvTable, fdtblY, fdtblUv);

        var w = new JpegWriter(EstimateCapacity(width, height));
        WriteHeaders(w, width, height, subsample, yTable, uvTable, exif.Span, xmp);
        EncodeScan(w, pixels, width, height, channels, subsample, fdtblY, fdtblUv);

        // Flush the entropy stream with the reference's 1-fill and close with EOI.
        w.WriteBits(0x7F, 7);
        w.Put(0xFF);
        w.Put(0xD9);
        return w.ToArray();
    }

    /// <summary>Checks the EXIF blob before anything is written; returns it, or empty for "no segment".</summary>
    private static ReadOnlyMemory<byte> ValidateExif(ReadOnlyMemory<byte>? exif)
    {
        if (exif is not { } blob) return default;
        if (blob.Length > JpegEncodeOptions.MaxExifBytes)
            throw new ArgumentException($"EXIF blob is {blob.Length} bytes; one APP1 segment holds at most {JpegEncodeOptions.MaxExifBytes}.", "options");
        var s = blob.Span;
        var tiffHeader = s.Length >= 8
            && ((s[0] == (byte)'I' && s[1] == (byte)'I' && s[2] == 42 && s[3] == 0)
                || (s[0] == (byte)'M' && s[1] == (byte)'M' && s[2] == 0 && s[3] == 42));
        if (!tiffHeader)
            throw new ArgumentException("EXIF blob must be a TIFF structure (II*\\0 or MM\\0* header, at least 8 bytes), without the Exif\\0\\0 signature.", "options");
        return blob;
    }

    /// <summary>Builds the XMP packet before anything is written; a bad packet is an <see cref="ArgumentException"/> on <c>options</c>.</summary>
    private static byte[] BuildXmpPacket(JpegXmp xmp)
    {
        try
        {
            return xmp.BuildPacket();
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(ex.Message, "options", ex);
        }
    }

    private static int EstimateCapacity(int width, int height) =>
        1024 + width * height / 2; // headers + a generous entropy guess; the writer grows as needed.

    // -------------------------------------------------------------- output + bit writer

    /// <summary>Growable byte sink with the reference encoder's MSB-first bit packer
    /// and 0xFF → 0xFF 0x00 stuffing. Header writes bypass the bit buffer; entropy
    /// data goes through <see cref="WriteBits"/>.</summary>
    private sealed class JpegWriter(int capacity)
    {
        private byte[] _buf = new byte[Math.Max(64, capacity)];
        private int _count;
        private int _bitBuf;
        private int _bitCnt;

        public void Put(byte b)
        {
            if (_count == _buf.Length) Grow(_count + 1);
            _buf[_count++] = b;
        }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            if (_count + bytes.Length > _buf.Length) Grow(_count + bytes.Length);
            bytes.CopyTo(_buf.AsSpan(_count));
            _count += bytes.Length;
        }

        public void WriteBits(int code, int size)
        {
            // Faithful to stbiw: accumulate MSB-first in a 24-bit window, flush whole
            // bytes, stuffing a zero after any 0xFF so it can't be read as a marker.
            _bitCnt += size;
            _bitBuf |= code << (24 - _bitCnt);
            while (_bitCnt >= 8)
            {
                var c = (byte)((_bitBuf >> 16) & 255);
                Put(c);
                if (c == 255) Put(0);
                _bitBuf <<= 8;
                _bitCnt -= 8;
            }
        }

        public byte[] ToArray() => _buf.AsSpan(0, _count).ToArray();

        private void Grow(int needed)
        {
            var cap = _buf.Length * 2;
            if (cap < needed) cap = needed;
            Array.Resize(ref _buf, cap);
        }
    }

    // -------------------------------------------------------------- quant tables

    // Annex K example quantisation tables (luma / chroma), in raster (natural) order.
    private static ReadOnlySpan<int> Yqt =>
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static ReadOnlySpan<int> Uvqt =>
    [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    ];

    // AAN scale factors. Computed at RUNTIME as genuine single-precision products
    // (never a compile-time double-round) so the values match the reference C
    // compiler's correctly-rounded float const-folding bit-for-bit.
    private static readonly float[] Aasf = BuildAasf();

    private static float[] BuildAasf()
    {
        ReadOnlySpan<float> b = [1.0f, 1.387039845f, 1.306562965f, 1.175875602f, 1.0f, 0.785694958f, 0.541196100f, 0.275899379f];
        var a = new float[8];
        for (var i = 0; i < 8; i++) a[i] = b[i] * 2.828427125f;
        return a;
    }

    private static void BuildTables(int scale, Span<byte> yTable, Span<byte> uvTable, Span<float> fdtblY, Span<float> fdtblUv)
    {
        for (var i = 0; i < 64; i++)
        {
            var yti = (Yqt[i] * scale + 50) / 100;
            yTable[ZigZag[i]] = (byte)(yti < 1 ? 1 : yti > 255 ? 255 : yti);
            var uvti = (Uvqt[i] * scale + 50) / 100;
            uvTable[ZigZag[i]] = (byte)(uvti < 1 ? 1 : uvti > 255 ? 255 : uvti);
        }

        for (int row = 0, k = 0; row < 8; row++)
        {
            for (var col = 0; col < 8; col++, k++)
            {
                fdtblY[k] = 1f / (yTable[ZigZag[k]] * Aasf[row] * Aasf[col]);
                fdtblUv[k] = 1f / (uvTable[ZigZag[k]] * Aasf[row] * Aasf[col]);
            }
        }
    }

    // -------------------------------------------------------------- headers

    private static void WriteHeaders(JpegWriter w, int width, int height, bool subsample, ReadOnlySpan<byte> yTable, ReadOnlySpan<byte> uvTable, ReadOnlySpan<byte> exif, ReadOnlySpan<byte> xmp)
    {
        // SOI + APP0 JFIF.
        ReadOnlySpan<byte> head0a =
        [
            0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
        ];
        // The DQT marker/length and the first table's Pq/Tq byte.
        ReadOnlySpan<byte> head0b = [0xFF, 0xDB, 0, 0x84, 0];
        // SOS: 3 components, DC/AC table selectors, Ss=0 Se=0x3F Ah/Al=0.
        ReadOnlySpan<byte> head2 = [0xFF, 0xDA, 0, 0xC, 3, 1, 0, 2, 0x11, 3, 0x11, 0, 0x3F, 0];
        // SOF0 (frame) + the DHT marker/length and the first table's Tc/Th byte.
        ReadOnlySpan<byte> head1 =
        [
            0xFF, 0xC0, 0, 0x11, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
            3, 1, (byte)(subsample ? 0x22 : 0x11), 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xC4, 0x01, 0xA2, 0,
        ];

        w.Write(head0a);
        if (!exif.IsEmpty)
        {
            // APP1 "Exif\0\0" + the TIFF blob; the length field counts itself (already range-checked in Encode).
            var length = 2 + 6 + exif.Length;
            w.Put(0xFF);
            w.Put(0xE1);
            w.Put((byte)(length >> 8));
            w.Put((byte)length);
            w.Write("Exif\0\0"u8);
            w.Write(exif);
        }
        if (!xmp.IsEmpty)
        {
            // APP1 XMP identifier (NUL included) + the packet; the length was range-checked when it was built.
            ReadOnlySpan<byte> id = "http://ns.adobe.com/xap/1.0/\0"u8;
            var length = 2 + id.Length + xmp.Length;
            w.Put(0xFF);
            w.Put(0xE1);
            w.Put((byte)(length >> 8));
            w.Put((byte)length);
            w.Write(id);
            w.Write(xmp);
        }
        w.Write(head0b);
        w.Write(yTable);
        w.Put(1);
        w.Write(uvTable);
        w.Write(head1);
        w.Write(StdDcLuminanceNrcodes[1..]);
        w.Write(StdDcLuminanceValues);
        w.Put(0x10); // AC luminance table id
        w.Write(StdAcLuminanceNrcodes[1..]);
        w.Write(StdAcLuminanceValues);
        w.Put(1); // DC chrominance table id
        w.Write(StdDcChrominanceNrcodes[1..]);
        w.Write(StdDcChrominanceValues);
        w.Put(0x11); // AC chrominance table id
        w.Write(StdAcChrominanceNrcodes[1..]);
        w.Write(StdAcChrominanceValues);
        w.Write(head2);
    }

    // -------------------------------------------------------------- scan

    private static void EncodeScan(JpegWriter w, ReadOnlySpan<byte> data, int width, int height, int channels, bool subsample, ReadOnlySpan<float> fdtblY, ReadOnlySpan<float> fdtblUv)
    {
        // comp == 2 is gray+alpha (alpha ignored); gray replicates into all channels.
        var ofsG = channels > 2 ? 1 : 0;
        var ofsB = channels > 2 ? 2 : 0;
        int dcY = 0, dcU = 0, dcV = 0;

        if (subsample)
        {
            Span<float> yBlk = stackalloc float[256];
            Span<float> uBlk = stackalloc float[256];
            Span<float> vBlk = stackalloc float[256];
            Span<float> subU = stackalloc float[64];
            Span<float> subV = stackalloc float[64];
            for (var y = 0; y < height; y += 16)
            {
                for (var x = 0; x < width; x += 16)
                {
                    for (int row = y, pos = 0; row < y + 16; row++)
                    {
                        var clampedRow = row < height ? row : height - 1;
                        var baseP = clampedRow * width * channels;
                        for (var col = x; col < x + 16; col++, pos++)
                        {
                            var p = baseP + (col < width ? col : width - 1) * channels;
                            float r = data[p], g = data[p + ofsG], b = data[p + ofsB];
                            yBlk[pos] = 0.29900f * r + 0.58700f * g + 0.11400f * b - 128f;
                            uBlk[pos] = -0.16874f * r - 0.33126f * g + 0.50000f * b;
                            vBlk[pos] = 0.50000f * r - 0.41869f * g - 0.08131f * b;
                        }
                    }

                    dcY = ProcessDu(w, yBlk, 0, 16, fdtblY, dcY, YdcHt, YacHt);
                    dcY = ProcessDu(w, yBlk, 8, 16, fdtblY, dcY, YdcHt, YacHt);
                    dcY = ProcessDu(w, yBlk, 128, 16, fdtblY, dcY, YdcHt, YacHt);
                    dcY = ProcessDu(w, yBlk, 136, 16, fdtblY, dcY, YdcHt, YacHt);

                    for (int yy = 0, pos = 0; yy < 8; yy++)
                    {
                        for (var xx = 0; xx < 8; xx++, pos++)
                        {
                            var j = yy * 32 + xx * 2;
                            subU[pos] = (uBlk[j] + uBlk[j + 1] + uBlk[j + 16] + uBlk[j + 17]) * 0.25f;
                            subV[pos] = (vBlk[j] + vBlk[j + 1] + vBlk[j + 16] + vBlk[j + 17]) * 0.25f;
                        }
                    }

                    dcU = ProcessDu(w, subU, 0, 8, fdtblUv, dcU, UvdcHt, UvacHt);
                    dcV = ProcessDu(w, subV, 0, 8, fdtblUv, dcV, UvdcHt, UvacHt);
                }
            }
        }
        else
        {
            Span<float> yBlk = stackalloc float[64];
            Span<float> uBlk = stackalloc float[64];
            Span<float> vBlk = stackalloc float[64];
            for (var y = 0; y < height; y += 8)
            {
                for (var x = 0; x < width; x += 8)
                {
                    for (int row = y, pos = 0; row < y + 8; row++)
                    {
                        var clampedRow = row < height ? row : height - 1;
                        var baseP = clampedRow * width * channels;
                        for (var col = x; col < x + 8; col++, pos++)
                        {
                            var p = baseP + (col < width ? col : width - 1) * channels;
                            float r = data[p], g = data[p + ofsG], b = data[p + ofsB];
                            yBlk[pos] = 0.29900f * r + 0.58700f * g + 0.11400f * b - 128f;
                            uBlk[pos] = -0.16874f * r - 0.33126f * g + 0.50000f * b;
                            vBlk[pos] = 0.50000f * r - 0.41869f * g - 0.08131f * b;
                        }
                    }

                    dcY = ProcessDu(w, yBlk, 0, 8, fdtblY, dcY, YdcHt, YacHt);
                    dcU = ProcessDu(w, uBlk, 0, 8, fdtblUv, dcU, UvdcHt, UvacHt);
                    dcV = ProcessDu(w, vBlk, 0, 8, fdtblUv, dcV, UvdcHt, UvacHt);
                }
            }
        }
    }

    // The AAN forward DCT, in place over 8 samples at cdu[offset + i*step].
    private static void Dct(Span<float> cdu, int offset, int step)
    {
        float d0 = cdu[offset], d1 = cdu[offset + step], d2 = cdu[offset + step * 2], d3 = cdu[offset + step * 3];
        float d4 = cdu[offset + step * 4], d5 = cdu[offset + step * 5], d6 = cdu[offset + step * 6], d7 = cdu[offset + step * 7];

        var tmp0 = d0 + d7;
        var tmp7 = d0 - d7;
        var tmp1 = d1 + d6;
        var tmp6 = d1 - d6;
        var tmp2 = d2 + d5;
        var tmp5 = d2 - d5;
        var tmp3 = d3 + d4;
        var tmp4 = d3 - d4;

        // Even part
        var tmp10 = tmp0 + tmp3;
        var tmp13 = tmp0 - tmp3;
        var tmp11 = tmp1 + tmp2;
        var tmp12 = tmp1 - tmp2;

        d0 = tmp10 + tmp11;
        d4 = tmp10 - tmp11;

        var z1 = (tmp12 + tmp13) * 0.707106781f;
        d2 = tmp13 + z1;
        d6 = tmp13 - z1;

        // Odd part
        tmp10 = tmp4 + tmp5;
        tmp11 = tmp5 + tmp6;
        tmp12 = tmp6 + tmp7;

        var z5 = (tmp10 - tmp12) * 0.382683433f;
        var z2 = tmp10 * 0.541196100f + z5;
        var z4 = tmp12 * 1.306562965f + z5;
        var z3 = tmp11 * 0.707106781f;

        var z11 = tmp7 + z3;
        var z13 = tmp7 - z3;

        cdu[offset + step * 5] = z13 + z2;
        cdu[offset + step * 3] = z13 - z2;
        cdu[offset + step] = z11 + z4;
        cdu[offset + step * 7] = z11 - z4;
        cdu[offset] = d0;
        cdu[offset + step * 2] = d2;
        cdu[offset + step * 4] = d4;
        cdu[offset + step * 6] = d6;
    }

    // Magnitude-category encoding of a coefficient (JPEG "SSSS" + the value bits).
    private static void CalcBits(int val, out int code, out int size)
    {
        var tmp1 = val < 0 ? -val : val;
        val = val < 0 ? val - 1 : val;
        size = 1;
        while ((tmp1 >>= 1) != 0) size++;
        code = val & ((1 << size) - 1);
    }

    private static int ProcessDu(JpegWriter w, Span<float> cdu, int cduOffset, int stride, ReadOnlySpan<float> fdtbl, int dc, ReadOnlySpan<ushort> htDc, ReadOnlySpan<ushort> htAc)
    {
        // DCT the 8 rows, then the 8 columns, in place.
        for (var i = 0; i < 8; i++)
            Dct(cdu, cduOffset + i * stride, 1);
        for (var i = 0; i < 8; i++)
            Dct(cdu, cduOffset + i, stride);

        // Quantise / descale / zig-zag.
        Span<int> du = stackalloc int[64];
        for (int yy = 0, j = 0; yy < 8; yy++)
        {
            for (var xx = 0; xx < 8; xx++, j++)
            {
                var v = cdu[cduOffset + yy * stride + xx] * fdtbl[j];
                du[ZigZag[j]] = (int)(v < 0 ? v - 0.5f : v + 0.5f);
            }
        }

        // DC: encode the difference from the running predictor.
        var diff = du[0] - dc;
        if (diff == 0)
        {
            w.WriteBits(htDc[0], htDc[1]);
        }
        else
        {
            CalcBits(diff, out var code, out var size);
            w.WriteBits(htDc[size * 2], htDc[size * 2 + 1]);
            w.WriteBits(code, size);
        }

        // AC: run-length of zeros + magnitude category, EOB when the tail is zero.
        var end0Pos = 63;
        while (end0Pos > 0 && du[end0Pos] == 0) end0Pos--;
        if (end0Pos == 0)
        {
            w.WriteBits(htAc[0], htAc[1]); // EOB (0x00)
            return du[0];
        }

        for (var i = 1; i <= end0Pos; i++)
        {
            var startPos = i;
            while (du[i] == 0 && i <= end0Pos) i++;
            var nrZeroes = i - startPos;
            if (nrZeroes >= 16)
            {
                var lng = nrZeroes >> 4;
                for (var m = 1; m <= lng; m++)
                    w.WriteBits(htAc[0xF0 * 2], htAc[0xF0 * 2 + 1]); // ZRL (16 zeroes)
                nrZeroes &= 15;
            }

            CalcBits(du[i], out var code, out var size);
            var idx = ((nrZeroes << 4) + size) * 2;
            w.WriteBits(htAc[idx], htAc[idx + 1]);
            w.WriteBits(code, size);
        }

        if (end0Pos != 63)
            w.WriteBits(htAc[0], htAc[1]); // EOB

        return du[0];
    }

    // -------------------------------------------------------------- zig-zag + Annex K tables

    private static ReadOnlySpan<byte> ZigZag =>
    [
        0, 1, 5, 6, 14, 15, 27, 28, 2, 4, 7, 13, 16, 26, 29, 42, 3, 8, 12, 17, 25, 30, 41, 43, 9, 11, 18,
        24, 31, 40, 44, 53, 10, 19, 23, 32, 39, 45, 52, 54, 20, 22, 33, 38, 46, 51, 55, 60, 21, 34, 37, 47, 50, 56, 59, 61, 35, 36, 48, 49, 57, 58, 62, 63,
    ];

    private static ReadOnlySpan<byte> StdDcLuminanceNrcodes => [0, 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static ReadOnlySpan<byte> StdDcLuminanceValues => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static ReadOnlySpan<byte> StdAcLuminanceNrcodes => [0, 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];
    private static ReadOnlySpan<byte> StdAcLuminanceValues =>
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
        0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
        0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    ];

    private static ReadOnlySpan<byte> StdDcChrominanceNrcodes => [0, 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static ReadOnlySpan<byte> StdDcChrominanceValues => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static ReadOnlySpan<byte> StdAcChrominanceNrcodes => [0, 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
    private static ReadOnlySpan<byte> StdAcChrominanceValues =>
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
        0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    ];
}
