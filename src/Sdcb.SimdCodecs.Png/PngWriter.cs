using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// Pure-managed PNG writer. Emits a fully-conformant PNG with adaptive
/// per-row filter selection (libpng's "minimum sum of absolute values"
/// heuristic over filters 0/Sub/Up/Average/Paeth) and a caller-settable
/// deflate level. Takes eight pixel layouts — 8- and 16-bit grayscale,
/// grayscale + alpha, RGB and RGBA — and writes them as they are, or, with
/// <see cref="PngWriteOptions.LosslessReduction"/>, in the smallest exact PNG
/// layout (indexed colour, sub-byte greyscale, a tRNS transparent colour, ...).
/// Metadata chunks come from <see cref="PngWriteOptions"/>. No interlacing.
///
/// <para>An RGBA caller whose image is opaque should set
/// <see cref="PngWriteOptions.DiscardAlpha"/> rather than repack first: the
/// alpha is dropped during the per-row gather the encoder performs anyway, so
/// it costs nothing and takes a quarter off every pass that follows.</para>
///
/// Used by both production code ("save my <c>RgbaImage</c> render to
/// disk") and the test suite, where committed baselines for golden-image
/// regression tests live as PNGs. Those are read back in-family via
/// <see cref="PngReader"/> (or, in <c>PngWriterBitDepthTests</c>, a hand-rolled
/// in-test reader that avoids any third-party decoder dependency).
///
/// The filter encoders below are the dual of <see cref="PngPredictor"/>
/// (PDF/TIFF code path's PNG row unfilter): same Sub / Up / Average / Paeth
/// formulas with the signs flipped.
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Encode an 8-bit RGBA pixel buffer (row-major, no padding) as a PNG.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height) =>
        Encode(rgba, width, height, iccProfile: default);

    /// <summary>
    /// Encode an 8-bit RGBA pixel buffer (row-major, no padding) as a PNG and
    /// optionally embed an ICC profile via an <c>iCCP</c> chunk. Pass an empty
    /// span for <paramref name="iccProfile"/> to omit the chunk (identical
    /// output to the simpler <see cref="Encode(ReadOnlySpan{byte}, int, int)"/>
    /// overload). <see cref="Sdcb.SimdCodecs.Color.Icc.IccProfiles.SRgbV4"/> is the
    /// pre-bundled sRGB v4 profile bytes for callers that want colour-managed
    /// output without supplying their own profile.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, ReadOnlySpan<byte> iccProfile)
    {
        ValidateSize(rgba.Length, width, height, bytesPerPixel: 4);
        return EncodeCore(rgba, width, height, sampleBytes: 1, srcChannels: 4, dstChannels: 4,
            new PngWriteOptions { IccProfile = iccProfile.IsEmpty ? null : iccProfile.ToArray() });
    }

    /// <summary>
    /// Encode an 8-bit RGBA buffer with the full <see cref="PngWriteOptions"/>
    /// metadata set — iCCP / sRGB / gAMA / cHRM / eXIf plus the PNG-3 HDR
    /// signaling chunks (cICP / mDCv / cLLI). Use this overload for color-
    /// managed or HDR PNG output; the simpler overloads above are
    /// convenience wrappers.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, PngWriteOptions options)
    {
        ValidateSize(rgba.Length, width, height, bytesPerPixel: 4);
        return EncodeCore(rgba, width, height, sampleBytes: 1, srcChannels: 4,
            dstChannels: options.DiscardAlpha ? 3 : 4, options);
    }

    /// <summary>
    /// Encode a packed 8-bit RGB buffer (row-major, three bytes per pixel, no alpha) as a
    /// colour-type-2 PNG. <see cref="PngReader"/> has always accepted colour type 2; this is the
    /// writer side of it. A caller holding an RGBA buffer wants
    /// <see cref="PngWriteOptions.DiscardAlpha"/> instead, which reaches the same file without
    /// repacking the pixels first.
    /// </summary>
    public static byte[] EncodeRgb8(ReadOnlySpan<byte> rgb, int width, int height, ReadOnlySpan<byte> iccProfile = default)
    {
        ValidateSize(rgb.Length, width, height, bytesPerPixel: 3);
        return EncodeCore(rgb, width, height, sampleBytes: 1, srcChannels: 3, dstChannels: 3,
            new PngWriteOptions { IccProfile = iccProfile.IsEmpty ? null : iccProfile.ToArray() });
    }

    /// <summary>EncodeRgb8 with full <see cref="PngWriteOptions"/>.</summary>
    public static byte[] EncodeRgb8(ReadOnlySpan<byte> rgb, int width, int height, PngWriteOptions options)
    {
        ValidateSize(rgb.Length, width, height, bytesPerPixel: 3);
        return EncodeCore(rgb, width, height, sampleBytes: 1, srcChannels: 3, dstChannels: 3, options);
    }

    /// <summary>
    /// Encode an 8-bit grayscale buffer (row-major, one byte per pixel).
    /// Useful for low-bit-depth mask / heat-map output where the alpha channel
    /// of <see cref="Encode(ReadOnlySpan{byte},int,int)"/> would just be a
    /// constant 0xFF.
    /// </summary>
    public static byte[] EncodeGray8(ReadOnlySpan<byte> gray, int width, int height, ReadOnlySpan<byte> iccProfile = default)
    {
        ValidateSize(gray.Length, width, height, bytesPerPixel: 1);
        return EncodeCore(gray, width, height, sampleBytes: 1, srcChannels: 1, dstChannels: 1,
            new PngWriteOptions { IccProfile = iccProfile.IsEmpty ? null : iccProfile.ToArray() });
    }

    /// <summary>EncodeGray8 with full <see cref="PngWriteOptions"/>.</summary>
    public static byte[] EncodeGray8(ReadOnlySpan<byte> gray, int width, int height, PngWriteOptions options)
    {
        ValidateSize(gray.Length, width, height, bytesPerPixel: 1);
        return EncodeCore(gray, width, height, sampleBytes: 1, srcChannels: 1, dstChannels: 1, options);
    }

    /// <summary>
    /// Encode a 16-bit grayscale buffer (row-major, one <see cref="ushort"/>
    /// per pixel, system-endian on input). The PNG spec mandates big-endian
    /// sample order on disk, so the bytes are swapped internally before
    /// filtering — callers pass their <c>ushort[]</c> as-is. Used by the
    /// FITS-grayscale preview path so 16-bit stretches don't lose precision
    /// the way an 8-bit downsample would.
    /// </summary>
    public static byte[] EncodeGray16(ReadOnlySpan<ushort> gray, int width, int height, ReadOnlySpan<byte> iccProfile = default)
    {
        if (gray.Length != width * height)
            throw new ArgumentException("gray length must equal width*height");
        return EncodeCore(MemoryMarshal.AsBytes(gray), width, height, sampleBytes: 2,
            srcChannels: 1, dstChannels: 1,
            new PngWriteOptions { IccProfile = iccProfile.IsEmpty ? null : iccProfile.ToArray() });
    }

    /// <summary>EncodeGray16 with full <see cref="PngWriteOptions"/>.</summary>
    public static byte[] EncodeGray16(ReadOnlySpan<ushort> gray, int width, int height, PngWriteOptions options)
    {
        if (gray.Length != width * height)
            throw new ArgumentException("gray length must equal width*height");
        return EncodeCore(MemoryMarshal.AsBytes(gray), width, height, sampleBytes: 2,
            srcChannels: 1, dstChannels: 1, options);
    }

    /// <summary>
    /// Encode a 16-bit RGBA buffer (row-major, four <see cref="ushort"/>s per
    /// pixel: R, G, B, A; system-endian on input). The PNG spec mandates
    /// big-endian sample order on disk so the bytes are swapped internally
    /// before filtering. Useful when the source is a 16-bit stretched float
    /// channel and an 8-bit quantise would crush gradients.
    /// </summary>
    public static byte[] EncodeRgba16(ReadOnlySpan<ushort> rgba, int width, int height, ReadOnlySpan<byte> iccProfile = default)
    {
        if (rgba.Length != width * height * 4)
            throw new ArgumentException("rgba length must equal width*height*4");
        return EncodeCore(MemoryMarshal.AsBytes(rgba), width, height, sampleBytes: 2,
            srcChannels: 4, dstChannels: 4,
            new PngWriteOptions { IccProfile = iccProfile.IsEmpty ? null : iccProfile.ToArray() });
    }

    /// <summary>EncodeRgba16 with full <see cref="PngWriteOptions"/> — the HDR PNG entry point.</summary>
    public static byte[] EncodeRgba16(ReadOnlySpan<ushort> rgba, int width, int height, PngWriteOptions options)
    {
        if (rgba.Length != width * height * 4)
            throw new ArgumentException("rgba length must equal width*height*4");
        return EncodeCore(MemoryMarshal.AsBytes(rgba), width, height, sampleBytes: 2,
            srcChannels: 4, dstChannels: options.DiscardAlpha ? 3 : 4, options);
    }

    /// <summary>
    /// Encode a packed 16-bit RGB buffer (row-major, three <see cref="ushort"/>s per pixel,
    /// system-endian on input) as a colour-type-2 PNG. The samples are byte-swapped into the spec's
    /// network order one row at a time, so no second copy of the image is ever materialised.
    /// </summary>
    public static byte[] EncodeRgb16(ReadOnlySpan<ushort> rgb, int width, int height, ReadOnlySpan<byte> iccProfile = default)
    {
        if (rgb.Length != width * height * 3)
            throw new ArgumentException("rgb length must equal width*height*3");
        return EncodeCore(MemoryMarshal.AsBytes(rgb), width, height, sampleBytes: 2,
            srcChannels: 3, dstChannels: 3,
            new PngWriteOptions { IccProfile = iccProfile.IsEmpty ? null : iccProfile.ToArray() });
    }

    /// <summary>EncodeRgb16 with full <see cref="PngWriteOptions"/>.</summary>
    public static byte[] EncodeRgb16(ReadOnlySpan<ushort> rgb, int width, int height, PngWriteOptions options)
    {
        if (rgb.Length != width * height * 3)
            throw new ArgumentException("rgb length must equal width*height*3");
        return EncodeCore(MemoryMarshal.AsBytes(rgb), width, height, sampleBytes: 2,
            srcChannels: 3, dstChannels: 3, options);
    }

    /// <summary>
    /// Encode an 8-bit greyscale + alpha buffer (row-major, two bytes per pixel: grey, alpha) as a
    /// colour-type-4 PNG.
    /// </summary>
    public static byte[] EncodeGrayAlpha8(ReadOnlySpan<byte> grayAlpha, int width, int height, ReadOnlySpan<byte> iccProfile = default) =>
        EncodeGrayAlpha8(grayAlpha, width, height, new PngWriteOptions { IccProfile = iccProfile.IsEmpty ? null : iccProfile.ToArray() });

    /// <summary>EncodeGrayAlpha8 with full <see cref="PngWriteOptions"/>.</summary>
    public static byte[] EncodeGrayAlpha8(ReadOnlySpan<byte> grayAlpha, int width, int height, PngWriteOptions options)
    {
        ValidateSize(grayAlpha.Length, width, height, bytesPerPixel: 2);
        return EncodeCore(grayAlpha, width, height, sampleBytes: 1, srcChannels: 2,
            dstChannels: options.DiscardAlpha ? 1 : 2, options);
    }

    /// <summary>
    /// Encode a 16-bit greyscale + alpha buffer (row-major, two <see cref="ushort"/>s per pixel: grey,
    /// alpha; system-endian on input) as a colour-type-4 PNG.
    /// </summary>
    public static byte[] EncodeGrayAlpha16(ReadOnlySpan<ushort> grayAlpha, int width, int height, ReadOnlySpan<byte> iccProfile = default) =>
        EncodeGrayAlpha16(grayAlpha, width, height, new PngWriteOptions { IccProfile = iccProfile.IsEmpty ? null : iccProfile.ToArray() });

    /// <summary>EncodeGrayAlpha16 with full <see cref="PngWriteOptions"/>.</summary>
    public static byte[] EncodeGrayAlpha16(ReadOnlySpan<ushort> grayAlpha, int width, int height, PngWriteOptions options)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("width and height must be positive");
        if (grayAlpha.Length != (long)width * height * 2)
            throw new ArgumentException("grayAlpha length must equal width*height*2");
        return EncodeCore(MemoryMarshal.AsBytes(grayAlpha), width, height, sampleBytes: 2, srcChannels: 2,
            dstChannels: options.DiscardAlpha ? 1 : 2, options);
    }

    /// <summary>
    /// Encode <paramref name="rgba"/> as a PNG and write it to
    /// <paramref name="path"/>.
    /// </summary>
    public static void Save(string path, ReadOnlySpan<byte> rgba, int width, int height)
    {
        var png = Encode(rgba, width, height);
        File.WriteAllBytes(path, png);
    }

    /// <summary>
    /// Encode <paramref name="rgba"/> with an embedded ICC profile and write
    /// it to <paramref name="path"/>.
    /// </summary>
    public static void Save(string path, ReadOnlySpan<byte> rgba, int width, int height, ReadOnlySpan<byte> iccProfile)
    {
        var png = Encode(rgba, width, height, iccProfile);
        File.WriteAllBytes(path, png);
    }

    private static void ValidateSize(int actualBytes, int width, int height, int bytesPerPixel)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("width and height must be positive");
        var expected = width * height * bytesPerPixel;
        if (actualBytes != expected)
            throw new ArgumentException($"pixel buffer length must equal width*height*{bytesPerPixel}");
    }

    /// <summary>
    /// PNG bytes for a source of <paramref name="srcChannels"/> samples of <paramref name="sampleBytes"/>
    /// each per pixel (1 grey, 2 grey + alpha, 3 RGB, 4 RGBA), written as its first
    /// <paramref name="dstChannels"/> channels or in the layout <see cref="PngEncodePlan.Analyze"/> picks.
    /// </summary>
    /// <param name="samples">
    /// The pixels in HOST order and HOST layout, <paramref name="srcChannels"/> samples per pixel.
    /// No pre-conditioning is required or wanted: the byte swap 16-bit PNG needs, and the discard of
    /// a fourth channel when <paramref name="dstChannels"/> is three, both happen a row at a time
    /// inside <see cref="WriteIdatChunk"/>.
    /// </param>
    /// <param name="srcChannels">Samples per pixel in <paramref name="samples"/>.</param>
    /// <param name="dstChannels">
    /// Channels of the source to write: equal to <paramref name="srcChannels"/> except when dropping
    /// alpha (4 in, 3 out; 2 in, 1 out).
    /// </param>
    private static byte[] EncodeCore(ReadOnlySpan<byte> samples, int width, int height,
        int sampleBytes, int srcChannels, int dstChannels, PngWriteOptions options)
    {
        // Refuse a bad keyword before the pixels are deflated, not after: the text is written last.
        if (options.Text is { Count: > 0 } textToCheck)
        {
            foreach (var entry in textToCheck)
                ValidateKeyword(entry.Keyword);
        }

        PngEncodePlan plan;
        if (options.LosslessReduction)
        {
            if (options.TransparentColor is not null)
                throw new ArgumentException(
                    "TransparentColor and LosslessReduction do not combine: the reduction finds a transparent colour itself",
                    nameof(PngWriteOptions.TransparentColor));
            plan = PngEncodePlan.Analyze(samples, width, height, dstChannels, srcChannels, sampleBytes,
                keepColourModel: options.IccProfile is { Length: > 0 });
        }
        else
        {
            plan = PngEncodePlan.Direct(dstChannels, srcChannels, sampleBytes, options.TransparentColor);
        }

        using var ms = new MemoryStream();
        ms.Write(Signature);

        // IHDR: width, height, bit depth, color type, compression (0=deflate),
        // filter (0=adaptive), interlace (0).
        Span<byte> ihdr = stackalloc byte[13];
        WriteBE(ihdr.Slice(0, 4), (uint)width);
        WriteBE(ihdr.Slice(4, 4), (uint)height);
        ihdr[8] = plan.BitDepth;
        ihdr[9] = plan.ColorType;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(ms, "IHDR"u8, ihdr);

        // --- Ancillary chunks — all must precede IDAT per PNG spec §5.6 -------
        // PNG-3 HDR signaling (cICP / mDCv / cLLI) goes first because some
        // pedantic readers expect them very early; their order among each
        // other isn't constrained.
        if (options.Cicp is not null)
        {
            Span<byte> buf = stackalloc byte[4];
            options.Cicp.Write(buf);
            WriteChunk(ms, "cICP"u8, buf);
        }
        if (options.Mdcv is not null)
        {
            Span<byte> buf = stackalloc byte[24];
            options.Mdcv.Write(buf);
            // Canonical PNG-3 spec chunk type is "mDCV" (uppercase V = unsafe-to-copy
            // ancillary chunk; mastering display metadata becomes invalid if pixel
            // data is recoloured, per PNG chunk-naming convention). Pre-final drafts
            // used "mDCv" -- the reader accepts both.
            WriteChunk(ms, "mDCV"u8, buf);
        }
        if (options.Clli is not null)
        {
            Span<byte> buf = stackalloc byte[8];
            options.Clli.Write(buf);
            WriteChunk(ms, "cLLI"u8, buf);
        }

        // iCCP and sRGB are mutually exclusive (PNG spec §11.3.3.3); when both
        // are populated, prefer iCCP — the actual profile carries more info.
        if (options.IccProfile is { Length: > 0 } icc)
        {
            // Keyword from options (Latin-1, 1..79 bytes); defaults to "ICC profile".
            WriteIccpChunk(ms, System.Text.Encoding.Latin1.GetBytes(options.IccProfileName), icc);
        }
        else if (options.SrgbRenderingIntent is { } intent)
        {
            Span<byte> buf = stackalloc byte[1] { intent };
            WriteChunk(ms, "sRGB"u8, buf);
        }

        if (options.Gamma is { } gamma)
        {
            Span<byte> buf = stackalloc byte[4];
            WriteBE(buf, (uint)Math.Round(gamma * 100_000.0));
            WriteChunk(ms, "gAMA"u8, buf);
        }
        if (options.Chromaticity is not null)
        {
            Span<byte> buf = stackalloc byte[32];
            options.Chromaticity.Write(buf);
            WriteChunk(ms, "cHRM"u8, buf);
        }
        if (options.PhysicalDimensions is { } phys)
        {
            Span<byte> buf = stackalloc byte[9];
            WriteBE(buf, phys.PixelsPerUnitX);
            WriteBE(buf[4..], phys.PixelsPerUnitY);
            buf[8] = phys.UnitIsMetre ? (byte)1 : (byte)0;
            WriteChunk(ms, "pHYs"u8, buf);
        }

        // PLTE after the colour-space chunks, then tRNS after PLTE, both before the image data (§5.6).
        if (plan.Palette is { } palette)
            WriteChunk(ms, "PLTE"u8, palette);
        if (plan.Transparency is { } transparency)
            WriteChunk(ms, "tRNS"u8, transparency);

        WriteIdatChunk(ms, samples, width, height, plan, options);

        // eXIf is allowed before OR after IDAT per the PNG extensions; we put
        // it after so the pixel-critical chunks aren't pushed further from
        // the header. Mirror chunk consumers expect either order.
        if (options.Exif is { Length: > 0 } exif)
            WriteChunk(ms, "eXIf"u8, exif);

        // Text after the image data too: the spec allows it anywhere between IHDR and IEND, and a reader that stops
        // at the pixels has what it needs before reaching it.
        if (options.Text is { Count: > 0 } text)
        {
            foreach (var entry in text)
                WriteItxtChunk(ms, entry);
        }

        WriteChunk(ms, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    /// <summary>
    /// Gather, filter and deflate every scanline straight into <paramref name="ms"/>. We back-patch
    /// the IDAT length field once we know the IDAT size, and compute the chunk's CRC over
    /// [type + data] straight from the MemoryStream backing buffer.
    /// </summary>
    /// <remarks>
    /// <para><b>Everything here is per ROW, and that is the point.</b> The 16-bit entry points used
    /// to hand this method a whole second copy of the image, byte-swapped up front by
    /// <c>ToBigEndianBytes</c> — 250 MB for a 31 MP RGBA16 frame, allocated so it could be read
    /// once, sequentially, and thrown away. A row is a few tens of kilobytes, stays in cache between
    /// the gather and the filter loop, and comes out of the pool.</para>
    /// <para>The five candidate filters are still all computed, because libpng's heuristic is a
    /// comparison and there is nothing to compare without them; what is gone is the second pass that
    /// used to SCORE them. <see cref="FilterRow"/> returns the score of the row it just wrote, from
    /// the bytes while they are still in registers.</para>
    /// </remarks>
    private static void WriteIdatChunk(MemoryStream ms, ReadOnlySpan<byte> samples, int width, int height,
        PngEncodePlan plan, PngWriteOptions options)
    {
        var stride = plan.RowBytes(width);

        long lengthFieldPos = ms.Position;
        Span<byte> placeholder = stackalloc byte[4];
        ms.Write(placeholder);
        long typeAndDataStart = ms.Position;
        ms.Write("IDAT"u8);

        var fragments = EffectiveFragmentCount(options.ParallelFragments, height, stride);
        if (fragments > 1)
        {
            WriteFragmentedZlibStream(ms, samples, width, height, plan, options.CompressionLevel, fragments);
        }
        else
        {
            // Scope the ZLibStream so its trailing zlib bytes are flushed before we
            // measure ms.Position to compute the chunk length.
            using var z = new ZLibStream(ms, options.CompressionLevel, leaveOpen: true);
            CompressRows(z, samples, 0, height, width, plan, options.CompressionLevel, checksum: false);
        }

        long idatEnd = ms.Position;
        long idatDataLength = idatEnd - typeAndDataStart - 4; // -4 for "IDAT" type
        Span<byte> lenBuf = stackalloc byte[4];
        WriteBE(lenBuf, (uint)idatDataLength);
        ms.Position = lengthFieldPos;
        ms.Write(lenBuf);
        ms.Position = idatEnd;

        var crcSpan = ms.GetBuffer().AsSpan((int)typeAndDataStart, (int)(idatEnd - typeAndDataStart));
        Span<byte> crcBuf = stackalloc byte[4];
        WriteBE(crcBuf, Crc32(crcSpan, ReadOnlySpan<byte>.Empty));
        ms.Write(crcBuf);
    }

    /// <summary>
    /// A fragment carrying less payload than this is not worth its own deflate stream, so
    /// <see cref="PngWriteOptions.ParallelFragments"/> is reduced until every fragment clears it.
    /// </summary>
    public const int MinimumParallelFragmentBytes = 256 * 1024;

    /// <summary>
    /// How many fragments this image can usefully be split into: what the caller asked for, reduced
    /// so that every fragment holds at least two rows and at least
    /// <see cref="MinimumParallelFragmentBytes"/>.
    /// </summary>
    private static int EffectiveFragmentCount(int requested, int height, int stride)
    {
        if (requested <= 1)
        {
            return 1;
        }

        var payload = (long)height * (stride + 1);
        var byBytes = (int)(payload / MinimumParallelFragmentBytes);
        var byRows = height / 2;
        return Math.Max(1, Math.Min(requested, Math.Min(byBytes, byRows)));
    }

    /// <summary>
    /// Filter rows <c>[firstRow, firstRow + rowCount)</c> and write each one to
    /// <paramref name="destination"/> as PNG wants it: the chosen filter's number, then the filtered
    /// scanline. Returns the Adler-32 of exactly those bytes when <paramref name="checksum"/> is set,
    /// which is what the fragmented path needs and the single-stream path gets from
    /// <see cref="ZLibStream"/> for free.
    /// <para>At <see cref="CompressionLevel.NoCompression"/> every row is written with filter None.
    /// The filters exist to make rows compressible, and stored blocks compress nothing: the file is
    /// the same size whatever the filters, so choosing among five per row is pure cost (it made
    /// storing slower than deflating). Unfiltered stored rows also leave the samples readable as
    /// they are in the file.</para>
    /// </summary>
    /// <remarks>
    /// <b>A band can start anywhere</b>, because the row above it is a row of the SOURCE image rather
    /// than anything the previous band produced. PNG's filters predict from the unfiltered values
    /// above, so band <c>k</c> needs no output, no state and no ordering from band <c>k-1</c>: it
    /// re-reads one source row and proceeds. That is the property that makes the parallel path
    /// possible at all, and it is a fact about the format rather than anything arranged here.
    /// </remarks>
    private static uint CompressRows(Stream destination, ReadOnlySpan<byte> samples,
        int firstRow, int rowCount, int width, PngEncodePlan plan, CompressionLevel level, bool checksum)
    {
        // Indexed and sub-byte rows are written unfiltered too (PngEncodePlan.Filtered).
        var store = level == CompressionLevel.NoCompression || !plan.Filtered;
        var stride = plan.RowBytes(width);
        var srcStride = plan.SourceRowBytes(width);
        var bpp = plan.FilterBpp;

        // current row + previous row + five filter candidates, in one rent.
        var scratch = ArrayPool<byte>.Shared.Rent(checked(stride * 7));
        try
        {
            var current = scratch.AsSpan(0, stride);
            var prevRow = scratch.AsSpan(stride, stride);
            var candidates = scratch.AsSpan(2 * stride, 5 * stride);

            if (firstRow == 0)
            {
                prevRow.Clear();               // row -1 of the IMAGE is all zeros per spec
            }
            else
            {
                plan.Fill(samples.Slice((firstRow - 1) * srcStride, srcStride), prevRow, width);
            }

            uint a = 1, b = 0;
            Span<byte> filterByte = stackalloc byte[1];
            for (var y = firstRow; y < firstRow + rowCount; y++)
            {
                plan.Fill(samples.Slice(y * srcStride, srcStride), current, width);

                if (store)
                {
                    filterByte[0] = 0;
                    destination.Write(filterByte);
                    destination.Write(current);
                    if (checksum)
                    {
                        AdlerUpdate(ref a, ref b, filterByte);
                        AdlerUpdate(ref a, ref b, current);
                    }
                    continue;  // no filter reads the row above, so it need not be kept
                }

                var bestFilter = 0;
                var bestSum = long.MaxValue;
                for (var candidate = 0; candidate < 5; candidate++)
                {
                    var sum = FilterRow(current, prevRow, candidates.Slice(candidate * stride, stride),
                        candidate, bpp);
                    if (sum < bestSum)
                    {
                        bestSum = sum;
                        bestFilter = candidate;
                    }
                }

                filterByte[0] = (byte)bestFilter;
                var chosen = candidates.Slice(bestFilter * stride, stride);
                destination.Write(filterByte);
                destination.Write(chosen);

                if (checksum)
                {
                    AdlerUpdate(ref a, ref b, filterByte);
                    AdlerUpdate(ref a, ref b, chosen);
                }

                // The filter formulas reference the ORIGINAL values of the row above, not the
                // encoded ones, so this row becomes the next one's "prev". Swapping the two spans
                // says that without copying a row to say it. (A tuple swap cannot: a Span is a
                // ref struct and may not be a tuple element.)
                var reuse = prevRow;
                prevRow = current;
                current = reuse;
            }

            return (b << 16) | a;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// Write the IDAT's zlib stream as <paramref name="fragments"/> independently deflated pieces,
    /// compressed in parallel and concatenated.
    /// </summary>
    /// <remarks>
    /// <para>Three details make the concatenation legal. Each fragment is ended with
    /// <see cref="Stream.Flush"/>, which emits a sync flush (an empty NON-final stored block) and
    /// leaves the fragment byte-aligned — the length is taken at that moment, because disposing
    /// the <see cref="DeflateStream"/> appends a FINAL block, and a fragment carrying one stops every
    /// decoder at the first join. A final empty block is appended once, after the last fragment. And
    /// the zlib header and trailing Adler-32 are written by hand, the checksum being the combination
    /// of the fragments' own, since no single <see cref="ZLibStream"/> saw the whole payload.</para>
    /// <para>The pinning is what lets a span reach the worker threads at all: a
    /// <see cref="ReadOnlySpan{T}"/> cannot be captured, and copying the image to get something that
    /// can would reintroduce the whole-image buffer this encoder was recently rid of. The
    /// <c>fixed</c> scope strictly encloses the parallel region, so nothing moves under a worker.</para>
    /// </remarks>
    private static unsafe void WriteFragmentedZlibStream(MemoryStream ms, ReadOnlySpan<byte> samples,
        int width, int height, PngEncodePlan plan, CompressionLevel level, int fragments)
    {
        var stride = plan.RowBytes(width);
        var rowsPerFragment = (height + fragments - 1) / fragments;
        var deflated = new byte[fragments][];
        var lengths = new int[fragments];
        var checksums = new uint[fragments];
        var payloadLengths = new long[fragments];

        fixed (byte* pinned = samples)
        {
            var origin = (nint)pinned;
            var total = samples.Length;

            Parallel.For(0, fragments, index =>
            {
                var firstRow = index * rowsPerFragment;
                var rowCount = Math.Min(rowsPerFragment, height - firstRow);
                if (rowCount <= 0)
                {
                    deflated[index] = [];
                    checksums[index] = 1;      // the Adler-32 of nothing
                    return;
                }

                var view = new ReadOnlySpan<byte>((byte*)origin, total);
                using var buffer = new MemoryStream(rowCount * (stride + 1) / 2);
                var d = new DeflateStream(buffer, level, leaveOpen: true);
                checksums[index] = CompressRows(d, view, firstRow, rowCount, width, plan, level, checksum: true);

                d.Flush();
                lengths[index] = (int)buffer.Length;   // BEFORE Dispose writes the final block
                d.Dispose();

                deflated[index] = buffer.GetBuffer();
                payloadLengths[index] = (long)rowCount * (stride + 1);
            });
        }

        WriteZlibHeader(ms, level);
        for (var i = 0; i < fragments; i++)
        {
            ms.Write(deflated[i], 0, lengths[i]);
        }

        // BFINAL=1 with a fixed-Huffman block holding nothing but end-of-block. Two bytes, and the
        // only thing standing between a pile of sync-flushed fragments and a complete deflate stream.
        ms.WriteByte(0x03);
        ms.WriteByte(0x00);

        var adler = checksums[0];
        for (var i = 1; i < fragments; i++)
        {
            adler = AdlerCombine(adler, checksums[i], payloadLengths[i]);
        }

        Span<byte> adlerBuf = stackalloc byte[4];
        WriteBE(adlerBuf, adler);
        ms.Write(adlerBuf);
    }

    /// <summary>
    /// The two-byte zlib header: deflate with a 32K window, the level's own FLEVEL hint, and the
    /// FCHECK bits that make the pair divisible by 31 (RFC 1950 s2.2).
    /// </summary>
    private static void WriteZlibHeader(Stream destination, CompressionLevel level)
    {
        const int Cmf = 0x78;              // compression method 8 (deflate), window 32K
        var flevel = level switch
        {
            CompressionLevel.NoCompression => 0,
            CompressionLevel.Fastest => 1,
            CompressionLevel.SmallestSize => 3,
            _ => 2,
        };

        var header = (Cmf << 8) | (flevel << 6);
        header += 31 - (header % 31);
        destination.WriteByte((byte)(header >> 8));
        destination.WriteByte((byte)header);
    }

    /// <summary>
    /// Fold <paramref name="data"/> into a running Adler-32 (RFC 1950 s9), reducing only every
    /// <c>Nmax</c> bytes rather than on every one.
    /// </summary>
    private static void AdlerUpdate(ref uint a, ref uint b, ReadOnlySpan<byte> data)
    {
        const uint Base = 65521;
        const int Nmax = 5552;             // the most iterations that cannot overflow a uint

        var at = 0;
        while (at < data.Length)
        {
            var run = Math.Min(Nmax, data.Length - at);
            for (var i = 0; i < run; i++)
            {
                a += data[at + i];
                b += a;
            }

            a %= Base;
            b %= Base;
            at += run;
        }
    }

    /// <summary>
    /// zlib's <c>adler32_combine</c>: the checksum of two payloads concatenated, from their own
    /// checksums and the length of the second. This is what lets fragments be checksummed on the
    /// threads that produced them instead of in a serial pass over the whole payload afterwards.
    /// </summary>
    private static uint AdlerCombine(uint first, uint second, long secondLength)
    {
        const uint Base = 65521;

        var rem = (uint)(secondLength % Base);
        var sum1 = first & 0xFFFF;
        var sum2 = (uint)((rem * (ulong)sum1) % Base);

        sum1 += (second & 0xFFFF) + Base - 1;
        sum2 += ((first >> 16) & 0xFFFF) + ((second >> 16) & 0xFFFF) + Base - rem;

        if (sum1 >= Base) { sum1 -= Base; }
        if (sum1 >= Base) { sum1 -= Base; }
        if (sum2 >= Base << 1) { sum2 -= Base << 1; }
        if (sum2 >= Base) { sum2 -= Base; }

        return sum1 | (sum2 << 16);
    }

    /// <summary>
    /// Copy one source scanline into PNG sample order: big-endian for 16-bit samples (the spec's
    /// network order), dropping the last channel (alpha) when <paramref name="dstChannels"/> is one
    /// fewer than <paramref name="srcChannels"/>.
    /// </summary>
    internal static void FillRow(ReadOnlySpan<byte> src, Span<byte> dst,
        int srcChannels, int dstChannels, int sampleBytes)
    {
        if (sampleBytes == 1)
        {
            if (srcChannels == dstChannels)
            {
                src.CopyTo(dst);
                return;
            }

            if (dstChannels == 1)
            {
                for (int s = 0, d = 0; d < dst.Length; s += srcChannels, d++)
                    dst[d] = src[s];
                return;
            }

            for (int s = 0, d = 0; d < dst.Length; s += srcChannels, d += dstChannels)
            {
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
            }

            return;
        }

        var samples = MemoryMarshal.Cast<byte, ushort>(src);
        if (srcChannels == dstChannels)
        {
            if (BitConverter.IsLittleEndian)
            {
                // The BCL's span overload is vectorised; the per-sample WriteUInt16BigEndian loop
                // this replaced was measurably its own phase of a large encode.
                BinaryPrimitives.ReverseEndianness(samples, MemoryMarshal.Cast<byte, ushort>(dst));
            }
            else
            {
                src.CopyTo(dst);
            }

            return;
        }

        for (int s = 0, d = 0; d < dst.Length; s += srcChannels, d += dstChannels * 2)
        {
            for (var c = 0; c < dstChannels; c++)
            {
                var v = samples[s + c];
                dst[d + (c * 2)] = (byte)(v >> 8);
                dst[d + (c * 2) + 1] = (byte)v;
            }
        }
    }

    /// <summary>
    /// Emit an iCCP chunk for the given keyword + raw ICC profile bytes. The
    /// profile is zlib-deflated inline (the PNG spec mandates compression
    /// method 0 = zlib) and a single CRC32 is computed over [type + payload]
    /// straight from the MemoryStream backing buffer to avoid an extra copy.
    /// </summary>
    private static void WriteIccpChunk(MemoryStream ms, ReadOnlySpan<byte> keyword, ReadOnlySpan<byte> rawProfile)
    {
        // Reserve length, stream the payload, patch the length once we know it.
        long lengthFieldPos = ms.Position;
        Span<byte> placeholder = stackalloc byte[4];
        ms.Write(placeholder);
        long typeAndDataStart = ms.Position;
        ms.Write("iCCP"u8);

        ms.Write(keyword);
        ms.WriteByte(0); // null separator between keyword and method
        ms.WriteByte(0); // compression method = 0 (zlib/deflate)

        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(rawProfile);

        long end = ms.Position;
        long dataLength = end - typeAndDataStart - 4; // -4 for "iCCP" type bytes
        Span<byte> lenBuf = stackalloc byte[4];
        WriteBE(lenBuf, (uint)dataLength);
        ms.Position = lengthFieldPos;
        ms.Write(lenBuf);
        ms.Position = end;

        // CRC over [type + payload] read directly from the underlying buffer.
        var crcSpan = ms.GetBuffer().AsSpan((int)typeAndDataStart, (int)(end - typeAndDataStart));
        Span<byte> crcBuf = stackalloc byte[4];
        WriteBE(crcBuf, Crc32(crcSpan, ReadOnlySpan<byte>.Empty));
        ms.Write(crcBuf);
    }

    /// <summary>The text length, in UTF-8 bytes, above which an <c>iTXt</c> chunk's text is deflated.</summary>
    public const int TextCompressionThreshold = 1024;

    /// <summary>
    /// One <c>iTXt</c> chunk (PNG spec 11.3.4.5): the keyword in Latin-1 and a null, the compression flag and
    /// method, an empty language tag and an empty translated keyword (each null-terminated), then the text in
    /// UTF-8, deflated when long.
    /// </summary>
    private static void WriteItxtChunk(Stream output, PngText entry)
    {
        var text = System.Text.Encoding.UTF8.GetBytes(entry.Text);
        var compress = text.Length > TextCompressionThreshold;
        using var body = new MemoryStream();
        body.Write(System.Text.Encoding.Latin1.GetBytes(entry.Keyword));
        body.WriteByte(0);                            // keyword terminator
        body.WriteByte(compress ? (byte)1 : (byte)0); // compression flag
        body.WriteByte(0);                            // compression method 0 (zlib)
        body.WriteByte(0);                            // empty language tag
        body.WriteByte(0);                            // empty translated keyword
        if (compress)
        {
            using var z = new ZLibStream(body, CompressionLevel.Optimal, leaveOpen: true);
            z.Write(text);
        }
        else
        {
            body.Write(text);
        }
        WriteChunk(output, "iTXt"u8, body.GetBuffer().AsSpan(0, (int)body.Length));
    }

    private static void ValidateKeyword(string keyword)
    {
        if (!PngText.IsValidKeyword(keyword))
            throw new ArgumentException(
                $"'{keyword}' is not a PNG text keyword: 1 to 79 Latin-1 printable characters, no leading, trailing or doubled space",
                nameof(PngWriteOptions.Text));
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        WriteBE(lenBuf, (uint)data.Length);
        output.Write(lenBuf);
        output.Write(type);
        output.Write(data);

        // CRC32 over type + data, big-endian.
        var crc = Crc32(type, data);
        Span<byte> crcBuf = stackalloc byte[4];
        WriteBE(crcBuf, crc);
        output.Write(crcBuf);
    }

    private static void WriteBE(Span<byte> dst, uint value)
    {
        dst[0] = (byte)(value >> 24);
        dst[1] = (byte)(value >> 16);
        dst[2] = (byte)(value >> 8);
        dst[3] = (byte)value;
    }

    /// <summary>
    /// Apply a PNG filter to one scanline. <paramref name="filterType"/>:
    /// 0=None, 1=Sub (subtract left neighbour), 2=Up (subtract pixel above),
    /// 3=Average (subtract floor((left+above)/2)), 4=Paeth.
    /// </summary>
    /// <summary>
    /// Filter one scanline with <paramref name="filterType"/> into <paramref name="dst"/>, and
    /// return libpng's "minsum" selection score for it: the sum of the filtered bytes read as signed
    /// (so 0xFF — 1, 0x80 — 128), where a smaller score predicts better deflate.
    /// </summary>
    /// <remarks>
    /// Scoring belongs HERE, not in a pass of its own. It used to be a separate <c>SumAbsSigned</c>
    /// sweep over all five finished candidate rows, re-reading five rows' worth of bytes to total a
    /// number each of those bytes had already been in a register for. On a 31 MP RGBA16 frame that
    /// second sweep measured 1800 ms of a 7311 ms encode — a quarter of the whole thing, for
    /// arithmetic that is free when it rides along with the subtraction.
    /// </remarks>
    private static long FilterRow(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> prev,
        Span<byte> dst, int filterType, int bpp)
    {
        long sum = 0;
        switch (filterType)
        {
            case 0:
                raw.CopyTo(dst);
                for (int i = 0; i < raw.Length; i++)
                {
                    int s = (sbyte)raw[i];
                    sum += (s + (s >> 31)) ^ (s >> 31);
                }
                break;
            case 1:
                for (int i = 0; i < bpp; i++)
                {
                    dst[i] = raw[i];
                    int s = (sbyte)raw[i];
                    sum += (s + (s >> 31)) ^ (s >> 31);
                }
                for (int i = bpp; i < raw.Length; i++)
                {
                    var v = (byte)(raw[i] - raw[i - bpp]);
                    dst[i] = v;
                    int s = (sbyte)v;
                    sum += (s + (s >> 31)) ^ (s >> 31);
                }
                break;
            case 2:
                for (int i = 0; i < raw.Length; i++)
                {
                    var v = (byte)(raw[i] - prev[i]);
                    dst[i] = v;
                    int s = (sbyte)v;
                    sum += (s + (s >> 31)) ^ (s >> 31);
                }
                break;
            case 3:
                for (int i = 0; i < raw.Length; i++)
                {
                    int left = i >= bpp ? raw[i - bpp] : 0;
                    int above = prev[i];
                    var v = (byte)(raw[i] - ((left + above) / 2));
                    dst[i] = v;
                    int s = (sbyte)v;
                    sum += (s + (s >> 31)) ^ (s >> 31);
                }
                break;
            case 4:
                for (int i = 0; i < raw.Length; i++)
                {
                    int left = i >= bpp ? raw[i - bpp] : 0;
                    int above = prev[i];
                    int upperLeft = i >= bpp ? prev[i - bpp] : 0;
                    var v = (byte)(raw[i] - PngPredictor.PaethPredictor(left, above, upperLeft));
                    dst[i] = v;
                    int s = (sbyte)v;
                    sum += (s + (s >> 31)) ^ (s >> 31);
                }
                break;
        }

        return sum;
    }

    /// <summary>
    /// Standard PNG CRC32 (polynomial 0xEDB88320, IEEE 802.3). Computed on
    /// the concatenation of <paramref name="a"/> and <paramref name="b"/>
    /// without materializing either span.
    /// </summary>
    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        PngCrc32.Compute(a, b);
}
