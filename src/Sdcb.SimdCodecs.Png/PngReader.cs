using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;

namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// Pure-managed PNG decoder — the dual of <see cref="PngWriter"/>. Parses
/// the PNG chunked container, validates CRC32 on every chunk, decompresses
/// the IDAT stream via <see cref="ZLibStream"/> a row at a time straight into
/// the samples, and defilters each row there with <see cref="PngPredictor"/>. Preserves
/// metadata that <c>Sdcb.SimdCodecs.StbImage</c> discards: ICC profile, sRGB
/// declaration, gAMA / cHRM color hints, and EXIF.
/// </summary>
/// <remarks>
/// <para>Scope:</para>
/// <list type="bullet">
///   <item>Every bit depth PNG allows: 8 and 16 per sample, and 1, 2 and 4 for greyscale and
///   indexed, whose samples are unpacked to one byte each (see <see cref="PngImage.BitDepth"/>).</item>
///   <item>Color types 0 (Gray), 2 (RGB), 3 (Indexed, PLTE + tRNS), 4 (Gray + Alpha), 6 (RGBA).</item>
///   <item>Non-interlaced only (<c>InterlaceMethod = 0</c>).</item>
/// </list>
/// <para>Adam7 interlacing is deferred to a follow-up. APNG and PNG-3 HDR
/// chunks (<c>cICP</c> / <c>mDCv</c> / <c>cLLI</c>) will layer on top of
/// this decoder.</para>
/// </remarks>
public static class PngReader
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The most a <c>zTXt</c> or compressed <c>iTXt</c> chunk may inflate to, in bytes (8 MiB, libpng's default
    /// chunk limit). A few kilobytes of deflate can claim gigabytes, so inflation stops here and the chunk is
    /// skipped, as a malformed text chunk is.
    /// </summary>
    public const int MaxInflatedTextBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The most an <c>iCCP</c> profile may inflate to, in bytes (16 MiB; real profiles are kilobytes to a few
    /// megabytes). Past it the decode fails with <see cref="InvalidDataException"/>, as a malformed <c>iCCP</c> does.
    /// </summary>
    public const int MaxInflatedIccProfileBytes = 16 * 1024 * 1024;

    /// <summary>
    /// <see cref="Decode"/> without the exceptions: false for a stream that is not a PNG, is corrupt,
    /// or uses a feature this reader does not cover yet (Adam7 interlace). It makes no
    /// judgement about the colour signalling, so it is the shared first step of every projection — the
    /// RGBA8 display ones in <see cref="PngImageDecoder"/>, which refuse cICP PQ/HLG, and a float one
    /// that would decode those.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out PngImage? image)
    {
        try
        {
            image = Decode(data);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            image = null;
            return false;
        }
    }

    /// <summary>
    /// Parse a PNG byte stream into a <see cref="PngImage"/>. The returned
    /// <see cref="PngImage.Pixels"/> is unfiltered raw sample data in row-major
    /// order; 16-bit samples are returned in PNG's big-endian byte order to
    /// preserve bit patterns exactly (see <see cref="PngImage.AsUInt16Samples"/>
    /// for a native-endian view).
    /// </summary>
    public static PngImage Decode(ReadOnlySpan<byte> data)
    {
        var header = ReadHeader(data, out var idat);
        return header with
        {
            Pixels = InflateAndUnfilter(data, idat, header.Width, header.Height, header.SourceBitDepth, header.ColorType),
        };
    }

    /// <summary>
    /// Every chunk but the image data: the returned <see cref="PngImage"/> carries the header and the
    /// metadata with empty <see cref="PngImage.Pixels"/>, and <paramref name="idat"/> says where the IDAT
    /// payloads lie in <paramref name="data"/>. Lets a caller look at the metadata (the cICP refusal of
    /// the RGBA8 projection) before deciding how, or whether, to decode the pixels.
    /// </summary>
    internal static PngImage ReadHeader(ReadOnlySpan<byte> data, out List<(int Offset, int Length)> idat)
    {
        if (data.Length < Signature.Length || !data[..Signature.Length].SequenceEqual(Signature))
            throw new InvalidDataException("PNG signature missing — not a PNG file");

        int pos = Signature.Length;
        int? width = null, height = null, bitDepth = null, colorType = null,
             compressionMethod = null, filterMethod = null, interlaceMethod = null;
        // Where each IDAT's payload lies in the input: read in place, never copied out.
        var idatChunks = new List<(int Offset, int Length)>();
        byte[]? palette = null;             // PLTE: 3 bytes (RGB) per entry
        byte[]? paletteAlpha = null;        // tRNS (indexed meaning): 1 alpha byte per entry
        byte[]? iccProfileRaw = null;       // zlib-deflated, as on disk
        string? iccProfileName = null;
        byte[]? rawExif = null;
        List<PngText>? text = null;
        bool sawSrgb = false;
        byte srgbRenderingIntent = 0;
        uint? gamma100k = null;
        ChromaticityChunk? chrm = null;
        CicpChunk? cicp = null;
        MdcvChunk? mdcv = null;
        ClliChunk? clli = null;

        while (pos < data.Length)
        {
            if (pos + 8 > data.Length)
                throw new InvalidDataException($"Truncated chunk header at byte {pos}");

            uint dataLen = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
            // Spec says lengths are capped at 2^31-1; reject anything larger so we don't
            // wrap when adding to a position.
            if (dataLen > int.MaxValue)
                throw new InvalidDataException($"Chunk length {dataLen} exceeds int.MaxValue");
            int len = (int)dataLen;

            if (pos + 8 + len + 4 > data.Length)
                throw new InvalidDataException($"Chunk at byte {pos} extends past end of file");

            var typeBytes = data.Slice(pos + 4, 4);
            var chunkData = data.Slice(pos + 8, len);
            var declaredCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos + 8 + len, 4));
            var computedCrc = Crc32(typeBytes, chunkData);
            if (declaredCrc != computedCrc)
                throw new InvalidDataException(
                    $"CRC mismatch on chunk '{TypeAscii(typeBytes)}' at byte {pos}: " +
                    $"declared 0x{declaredCrc:X8}, computed 0x{computedCrc:X8}");

            // --- chunk dispatch -----------------------------------------------------
            // Critical chunks (uppercase first letter) must be understood; ancillary
            // chunks (lowercase first letter) can be skipped if unrecognised.
            if (typeBytes.SequenceEqual("IHDR"u8))
            {
                if (width is not null)
                    throw new InvalidDataException("Multiple IHDR chunks");
                if (len != 13)
                    throw new InvalidDataException($"IHDR length must be 13, got {len}");
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4));
                bitDepth = chunkData[8];
                colorType = chunkData[9];
                compressionMethod = chunkData[10];
                filterMethod = chunkData[11];
                interlaceMethod = chunkData[12];

                if (width <= 0 || height <= 0)
                    throw new InvalidDataException($"IHDR dimensions must be positive, got {width}×{height}");
                if (compressionMethod != 0)
                    throw new InvalidDataException($"Unsupported compression method {compressionMethod} (PNG only defines 0)");
                if (filterMethod != 0)
                    throw new InvalidDataException($"Unsupported filter method {filterMethod} (PNG only defines 0)");
                if (interlaceMethod != 0)
                    throw new NotSupportedException("Adam7 interlacing not yet supported (InterlaceMethod=1)");
                ValidateColorTypeAndBitDepth(colorType.Value, bitDepth.Value);
            }
            else if (typeBytes.SequenceEqual("IDAT"u8))
            {
                if (width is null)
                    throw new InvalidDataException("IDAT before IHDR");
                idatChunks.Add((pos + 8, len));
            }
            else if (typeBytes.SequenceEqual("IEND"u8))
            {
                pos += 8 + len + 4;
                break;
            }
            else if (typeBytes.SequenceEqual("PLTE"u8))
            {
                if (len == 0 || len % 3 != 0)
                    throw new InvalidDataException($"PLTE length must be a positive multiple of 3, got {len}");
                palette = chunkData.ToArray();
            }
            else if (typeBytes.SequenceEqual("tRNS"u8))
            {
                // For indexed color (the only case this reader expands), tRNS is one
                // alpha byte per palette entry, in palette order; entries past its end
                // are fully opaque. For truecolor/gray tRNS carries a single transparent
                // sample — not handled here (only the indexed meaning is surfaced).
                paletteAlpha = chunkData.ToArray();
            }
            else if (typeBytes.SequenceEqual("iCCP"u8))
            {
                // iCCP layout: keyword (1..79 bytes) + null + compression method (1 byte) + compressed profile.
                var nullIdx = chunkData.IndexOf((byte)0);
                if (nullIdx <= 0)
                    throw new InvalidDataException("iCCP chunk missing keyword/profile separator");
                iccProfileName = System.Text.Encoding.Latin1.GetString(chunkData[..nullIdx]);
                if (nullIdx + 1 >= chunkData.Length)
                    throw new InvalidDataException("iCCP chunk truncated after keyword");
                if (chunkData[nullIdx + 1] != 0)
                    throw new InvalidDataException(
                        $"iCCP compression method must be 0, got {chunkData[nullIdx + 1]}");
                iccProfileRaw = DeflateInflate(chunkData[(nullIdx + 2)..], MaxInflatedIccProfileBytes)
                    ?? throw new InvalidDataException(
                        $"iCCP profile inflates past {MaxInflatedIccProfileBytes} bytes");
            }
            else if (typeBytes.SequenceEqual("sRGB"u8))
            {
                if (len != 1)
                    throw new InvalidDataException($"sRGB chunk length must be 1, got {len}");
                sawSrgb = true;
                srgbRenderingIntent = chunkData[0];
            }
            else if (typeBytes.SequenceEqual("gAMA"u8))
            {
                if (len != 4)
                    throw new InvalidDataException($"gAMA chunk length must be 4, got {len}");
                gamma100k = BinaryPrimitives.ReadUInt32BigEndian(chunkData);
            }
            else if (typeBytes.SequenceEqual("cHRM"u8))
            {
                if (len != 32)
                    throw new InvalidDataException($"cHRM chunk length must be 32, got {len}");
                chrm = ChromaticityChunk.Read(chunkData);
            }
            else if (typeBytes.SequenceEqual("eXIf"u8))
            {
                rawExif = chunkData.ToArray();
            }
            else if (typeBytes.SequenceEqual("tEXt"u8) || typeBytes.SequenceEqual("zTXt"u8) || typeBytes.SequenceEqual("iTXt"u8))
            {
                // Text is never critical: a malformed text chunk is skipped, not a failed decode.
                if (TryReadText(typeBytes, chunkData) is { } entry)
                    (text ??= []).Add(entry);
            }
            else if (typeBytes.SequenceEqual("cICP"u8))
            {
                cicp = CicpChunk.Read(chunkData);
            }
            else if (typeBytes.SequenceEqual("mDCV"u8)
                  || typeBytes.SequenceEqual("mDCv"u8))
            {
                // Canonical PNG-3 spec name is "mDCV" (uppercase V = not safe to copy).
                // Pre-final-spec drafts and some early implementations used the lowercase
                // "mDCv" form; accept both on read so we round-trip those legacy files
                // correctly. The writer emits the canonical "mDCV".
                mdcv = MdcvChunk.Read(chunkData);
            }
            else if (typeBytes.SequenceEqual("cLLI"u8))
            {
                clli = ClliChunk.Read(chunkData);
            }
            // Any other chunk — silently skip. PNG is forwards-compatible by design:
            // ancillary unknown chunks are explicitly permitted, and critical unknown
            // chunks have a leading uppercase letter we'd need to reject. We defer the
            // critical-chunk rejection until we encounter a real one.

            pos += 8 + len + 4;
        }

        if (width is null) throw new InvalidDataException("No IHDR chunk found");
        if (idatChunks.Count == 0) throw new InvalidDataException("No IDAT chunks found");
        if (colorType == 3 && palette is null)
            throw new InvalidDataException("Indexed-color PNG missing required PLTE chunk");

        // The IDAT chunks are one zlib stream — the spec lets a producer split IDATs anywhere, even
        // mid-deflate-token — inflated by the caller, a row at a time.
        idat = idatChunks;
        return new PngImage
        {
            Width = width.Value,
            Height = height!.Value,
            // A sub-byte image's samples come back unpacked, one byte each: they are 8-bit samples now.
            BitDepth = bitDepth!.Value < 8 ? 8 : bitDepth.Value,
            SourceBitDepth = bitDepth.Value,
            ColorType = colorType!.Value,
            Pixels = [],
            Palette = palette,
            PaletteAlpha = paletteAlpha,
            IccProfile = iccProfileRaw,
            IccProfileName = iccProfileName,
            SrgbRenderingIntent = sawSrgb ? srgbRenderingIntent : null,
            Gamma = gamma100k.HasValue ? gamma100k.Value / 100_000.0 : null,
            Chromaticity = chrm,
            Exif = rawExif,
            Text = text is null ? [] : [.. text],
            Cicp = cicp,
            Mdcv = mdcv,
            Clli = clli,
        };
    }

    /// <summary>
    /// Validate that the (color type, bit depth) combo is one PNG allows.
    /// Spec table 11.1: not every depth is legal with every color type.
    /// </summary>
    private static void ValidateColorTypeAndBitDepth(int colorType, int bitDepth)
    {
        bool ok = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16, // Gray
            2 => bitDepth is 8 or 16,                // RGB
            3 => bitDepth is 1 or 2 or 4 or 8,       // Indexed (palette)
            4 => bitDepth is 8 or 16,                // Gray + Alpha
            6 => bitDepth is 8 or 16,                // RGBA
            _ => false
        };
        if (!ok)
            throw new InvalidDataException(
                $"Invalid (ColorType={colorType}, BitDepth={bitDepth}) combination per PNG spec");
    }

    /// <summary>
    /// Samples per pixel as a function of color type.
    /// Gray=1, RGB=3, Indexed=1, GrayAlpha=2, RGBA=4.
    /// </summary>
    internal static int SamplesPerPixel(int colorType) => colorType switch
    {
        0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4,
        _ => throw new InvalidDataException($"Unknown color type {colorType}")
    };

    /// <summary>
    /// Inflates the IDAT stream and runs the row-defilter, returning the raw sample bytes in row-major
    /// order. 16-bit samples stay in PNG's network (big-endian) byte order so the returned buffer is
    /// byte-identical to what the encoder fed in.
    /// <para>The samples are the only buffer the size of the image: the compressed bytes are read where
    /// they lie in <paramref name="data"/>, and each row is inflated straight into its place in the result
    /// and unfiltered there against the row above. It used to copy every IDAT out, concatenate the copies,
    /// copy that again into a stream, inflate into a growing stream, copy that out, and unfilter the copy
    /// into a new array: the image's bytes three or four times over before the samples existed.</para>
    /// </summary>
    private static unsafe byte[] InflateAndUnfilter(ReadOnlySpan<byte> data, List<(int Offset, int Length)> idat,
        int width, int height, int bitDepth, int colorType)
    {
        if (bitDepth < 8)
        {
            return InflateAndUnfilterSubByte(data, idat, width, height, bitDepth, colorType);
        }
        var bytesPerPixel = SamplesPerPixel(colorType) * (bitDepth / 8);
        var rowBytes = checked(width * bytesPerPixel);
        var pixels = new byte[checked((long)rowBytes * height)];
        var firstRowAbove = new byte[rowBytes];

        fixed (byte* pinned = data)
        {
            using var rows = new RowInflater(pinned, idat, rowBytes, height, width, bytesPerPixel);
            for (var y = 0; y < height; y++)
            {
                var row = pixels.AsSpan(y * rowBytes, rowBytes);
                var filterType = rows.Read(row);

                // PngPredictor's bpp argument is the "bytes per pixel" rounded up to at
                // least 1 — for 8-bit single-channel images that's 1; for 16-bit RGBA
                // it's 8. The filter formulas only need the "left neighbour" offset, so
                // bytesPerPixel here matches PNG spec §9.2.
                var above = y == 0 ? firstRowAbove : pixels.AsSpan((y - 1) * rowBytes, rowBytes);
                PngPredictor.UnfilterRow(filterType, row, above, bytesPerPixel);
            }
            rows.Finish();
        }

        return pixels;
    }

    /// <summary>
    /// <see cref="InflateAndUnfilter"/> for 1-, 2- and 4-bit greyscale and indexed images (one sample per pixel, packed
    /// most significant bits first, each row padded to a whole byte). A packed row is inflated and unfiltered against
    /// the packed row above, with a filter step of one byte as PNG §9.2 sets for depths under 8, then unpacked to one
    /// byte per sample: an index as it is, a grey level scaled to the full 0-255 range (a 4-bit 0xF is 255, a 1-bit 1 is
    /// 255) so the result reads as 8-bit greyscale. Emoji fonts' CBDT bitmaps use 4-bit indexed PNGs (Noto Color
    /// Emoji's U+1F6D1), which the reader refused.
    /// </summary>
    private static unsafe byte[] InflateAndUnfilterSubByte(ReadOnlySpan<byte> data, List<(int Offset, int Length)> idat,
        int width, int height, int bitDepth, int colorType)
    {
        var packedBytes = checked((int)(((long)width * bitDepth + 7) / 8));
        var pixels = new byte[checked((long)width * height)];
        var row = new byte[packedBytes];
        var above = new byte[packedBytes];

        fixed (byte* pinned = data)
        {
            using var rows = new RowInflater(pinned, idat, packedBytes, height, width, 1);
            for (var y = 0; y < height; y++)
            {
                PngPredictor.UnfilterRow(rows.Read(row), row, above, 1);
                PngRgba8Expander.UnpackSubByte(row, pixels.AsSpan(y * width, width), width, bitDepth, scaleGray: colorType == 0);
                (row, above) = (above, row);
            }
            rows.Finish();
        }

        return pixels;
    }

    /// <summary>
    /// Decodes the image data of a stream <see cref="ReadHeader"/> has read straight to 8-bit RGBA in
    /// <paramref name="destination"/> (<c>Width * Height * 4</c> bytes, tightly packed), with the mapping
    /// of <see cref="PngImage.ExpandToRgba8"/>. No buffer the size of the image is allocated: each row is
    /// inflated and unfiltered in a pooled row buffer and expanded into its place in the destination at
    /// once, while it is still in cache. 8-bit RGBA, whose samples already are the destination's layout,
    /// is inflated and unfiltered in the destination itself.
    /// </summary>
    internal static unsafe void DecodeRgba8(ReadOnlySpan<byte> data, PngImage header, List<(int Offset, int Length)> idat,
        Span<byte> destination)
    {
        int width = header.Width, height = header.Height, bitDepth = header.SourceBitDepth;
        var dstStride = checked(width * 4);
        if (destination.Length < (long)dstStride * height)
            throw new ArgumentException($"Destination too small: {destination.Length} < {(long)dstStride * height} (Width*Height*4).", nameof(destination));

        var expander = new PngRgba8Expander(header.ColorType, bitDepth, header.Palette, header.PaletteAlpha);
        var rowBytes = expander.RowBytes(width);
        var bytesPerPixel = bitDepth < 8 ? 1 : SamplesPerPixel(header.ColorType) * (bitDepth / 8);

        fixed (byte* pinned = data)
        {
            using var rows = new RowInflater(pinned, idat, rowBytes, height, width, bytesPerPixel);
            if (header.ColorType == 6 && bitDepth == 8)
            {
                var firstRowAbove = new byte[rowBytes];
                for (var y = 0; y < height; y++)
                {
                    var row = destination.Slice(y * dstStride, dstStride);
                    var above = y == 0 ? firstRowAbove : destination.Slice((y - 1) * dstStride, dstStride);
                    PngPredictor.UnfilterRow(rows.Read(row), row, above, bytesPerPixel);
                }
            }
            else
            {
                var scratch = ArrayPool<byte>.Shared.Rent(checked(2 * rowBytes));
                try
                {
                    var current = scratch.AsSpan(0, rowBytes);
                    var above = scratch.AsSpan(rowBytes, rowBytes);
                    above.Clear();
                    for (var y = 0; y < height; y++)
                    {
                        PngPredictor.UnfilterRow(rows.Read(current), current, above, bytesPerPixel);
                        expander.Expand(current, destination.Slice(y * dstStride, dstStride), width);
                        var swap = above;
                        above = current;
                        current = swap;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(scratch);
                }
            }
            rows.Finish();
        }
    }

    /// <summary>
    /// The IDAT zlib stream read one scanline at a time: <see cref="Read"/> fills a row and returns its
    /// filter type, <see cref="Finish"/> rejects a stream with bytes past the last row. A stream that ends
    /// early, or runs on, is an <see cref="InvalidDataException"/> naming the expected length. Only valid
    /// inside the <c>fixed</c> scope that pinned the input.
    /// </summary>
    private sealed unsafe class RowInflater(byte* data, List<(int Offset, int Length)> idat,
        int rowBytes, int height, int width, int bytesPerPixel) : IDisposable
    {
        // ZLibStream is the .NET 6+ wrapper that handles the 2-byte zlib header
        // + Adler32 trailer around the raw deflate bitstream.
        private readonly ZLibStream _z = new(new IdatStream(data, idat), CompressionMode.Decompress);
        private readonly byte[] _filterType = new byte[1];
        private long _inflated;
        private int _row;

        private long Expected => ((long)rowBytes + 1) * height;

        public byte Read(Span<byte> row)
        {
            _inflated += ReadFully(_z, _filterType);
            _inflated += ReadFully(_z, row[..rowBytes]);
            _row++;
            if (_inflated != (long)_row * (rowBytes + 1))
                throw LengthMismatch(_inflated, Expected, width, height, bytesPerPixel);
            return _filterType[0];
        }

        public void Finish()
        {
            // A byte past the last row is as much a mismatch as a row short.
            if (_z.Read(_filterType) != 0)
                throw LengthMismatch(Expected + 1, Expected, width, height, bytesPerPixel);
        }

        public void Dispose() => _z.Dispose();
    }

    private static InvalidDataException LengthMismatch(long inflated, long expected, int width, int height, int bytesPerPixel) =>
        new((inflated > expected ? $"Inflated stream runs past the expected {expected} bytes " : $"Inflated stream length {inflated} doesn't match expected {expected} ") +
            $"({width}×{height} × {bytesPerPixel} bytes-per-pixel + 1 filter byte per row)");

    /// <summary>Reads until <paramref name="buffer"/> is full or the stream ends; the count read.</summary>
    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// The IDAT payloads, in order, as one read-only stream over the pinned input, so the zlib stream they
    /// make up together is inflated without first being copied into one array. Only valid inside the
    /// <c>fixed</c> scope that pinned the input.
    /// </summary>
    private sealed unsafe class IdatStream(byte* data, List<(int Offset, int Length)> chunks) : Stream
    {
        private int _chunk;
        private int _offsetInChunk;

        public override int Read(Span<byte> buffer)
        {
            while (_chunk < chunks.Count)
            {
                var (offset, length) = chunks[_chunk];
                var left = length - _offsetInChunk;
                if (left == 0)
                {
                    _chunk++;
                    _offsetInChunk = 0;
                    continue;
                }

                var n = Math.Min(left, buffer.Length);
                new ReadOnlySpan<byte>(data + offset + _offsetInChunk, n).CopyTo(buffer);
                _offsetInChunk += n;
                return n;
            }
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// One text chunk as a <see cref="PngText"/> (PNG spec 11.3.4): <c>tEXt</c> keyword and Latin-1 text,
    /// <c>zTXt</c> keyword and deflated Latin-1 text, <c>iTXt</c> keyword, flags, language and translated keyword,
    /// then UTF-8 text, deflated when its flag says so. Null for a chunk that does not parse.
    /// </summary>
    private static PngText? TryReadText(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var keywordEnd = data.IndexOf((byte)0);
        if (keywordEnd is <= 0 or > PngText.MaxKeywordLength)
            return null;
        var keyword = System.Text.Encoding.Latin1.GetString(data[..keywordEnd]);
        var rest = data[(keywordEnd + 1)..];
        try
        {
            if (type.SequenceEqual("tEXt"u8))
                return new PngText(keyword, System.Text.Encoding.Latin1.GetString(rest));
            if (type.SequenceEqual("zTXt"u8))
                return rest.Length >= 1 && rest[0] == 0 && DeflateInflate(rest[1..], MaxInflatedTextBytes) is { } latin1
                    ? new PngText(keyword, System.Text.Encoding.Latin1.GetString(latin1))
                    : null;

            // iTXt: compression flag, compression method, language tag and null, translated keyword and null, text.
            // The flag is 0 or 1 and nothing else; any other value is malformed, not uncompressed.
            if (rest.Length < 2 || rest[0] > 1 || rest[1] != 0)
                return null;
            var compressed = rest[0] == 1;
            var afterFlags = rest[2..];
            var languageEnd = afterFlags.IndexOf((byte)0);
            if (languageEnd < 0)
                return null;
            var afterLanguage = afterFlags[(languageEnd + 1)..];
            var translatedEnd = afterLanguage.IndexOf((byte)0);
            if (translatedEnd < 0)
                return null;
            var payload = afterLanguage[(translatedEnd + 1)..];
            if (!compressed)
                return new PngText(keyword, System.Text.Encoding.UTF8.GetString(payload));
            return DeflateInflate(payload, MaxInflatedTextBytes) is { } utf8
                ? new PngText(keyword, System.Text.Encoding.UTF8.GetString(utf8))
                : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Inflate a deflate-compressed byte stream (zlib-wrapped). PNG IDAT
    /// payload is the concatenation of zero or more zlib streams — but the
    /// spec mandates exactly one logical zlib stream across all IDATs, so
    /// concat-then-inflate works.
    /// Null once the output would pass <paramref name="maxBytes"/>: reading stops there, so a
    /// small stream that claims gigabytes costs at most the cap.
    /// </summary>
    private static byte[]? DeflateInflate(ReadOnlySpan<byte> compressed, int maxBytes)
    {
        // Copy the span into a backing array so we can wrap it in a stream.
        // ZLibStream is the .NET 6+ wrapper that handles the 2-byte zlib header
        // + Adler32 trailer around the raw deflate bitstream.
        using var input = new MemoryStream(compressed.ToArray(), writable: false);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = z.Read(buffer)) > 0)
        {
            if (output.Length + read > maxBytes)
                return null;
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string TypeAscii(ReadOnlySpan<byte> typeBytes) =>
        System.Text.Encoding.ASCII.GetString(typeBytes);

    // ---------------- CRC32 (same polynomial as PngWriter.Crc32) -----------------

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (int k = 0; k < 8; k++)
                c = ((c & 1) != 0) ? 0xEDB88320u ^ (c >> 1) : (c >> 1);
            t[n] = c;
        }
        return t;
    }
}

/// <summary>
/// Decoded PNG image — sample data plus the ancillary metadata
/// <see cref="PngReader"/> extracted from chunks like <c>iCCP</c> / <c>sRGB</c>
/// / <c>gAMA</c> / <c>cHRM</c> / <c>eXIf</c>.
/// </summary>
public sealed record PngImage
{
    /// <summary>Image width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Image height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>
    /// Bits per sample in <see cref="Pixels"/>: 8 or 16. A 1-, 2- or 4-bit image's samples are unpacked to one byte
    /// each (a grey level scaled to 0-255, an index as it is), so it reads 8 here; IHDR's own depth is
    /// <see cref="SourceBitDepth"/>.
    /// </summary>
    public required int BitDepth { get; init; }

    /// <summary>Bits per sample as declared in IHDR: 1, 2, 4, 8 or 16; <see cref="BitDepth"/> for an image not read from a file.</summary>
    public int SourceBitDepth { get => field == 0 ? BitDepth : field; init; }

    /// <summary>PNG color type: 0=Gray, 2=RGB, 3=Indexed, 4=Gray+Alpha, 6=RGBA.</summary>
    public required int ColorType { get; init; }

    /// <summary>
    /// Raw unfiltered sample bytes in row-major order. For 16-bit images the
    /// bytes are in PNG's big-endian network order — see <see cref="AsUInt16Samples"/>
    /// for a native-endian convenience view.
    /// </summary>
    public required byte[] Pixels { get; init; }

    /// <summary>
    /// Palette (<c>PLTE</c>) for indexed-color images (<see cref="ColorType"/> == 3):
    /// RGB triples, 3 bytes per entry, in palette-index order. Null for non-indexed
    /// images. Each byte in <see cref="Pixels"/> is a single index into this palette
    /// (the reader keeps indexed pixels un-expanded to stay faithful to the file's
    /// declared <see cref="ColorType"/> — expand via palette lookup at the consumer).
    /// </summary>
    public byte[]? Palette { get; init; }

    /// <summary>
    /// Per-palette-entry alpha (<c>tRNS</c>) for indexed-color images: one byte per
    /// entry, in palette order. May be shorter than <see cref="Palette"/> — entries
    /// past its end are fully opaque (255). Null when an indexed image has no tRNS
    /// chunk (all opaque) or when the image is not indexed.
    /// </summary>
    public byte[]? PaletteAlpha { get; init; }

    /// <summary>Decompressed ICC profile bytes from the <c>iCCP</c> chunk, or null if absent.</summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>Keyword from the <c>iCCP</c> chunk (e.g. "ICC Profile" or a custom name).</summary>
    public string? IccProfileName { get; init; }

    /// <summary>Rendering intent from the <c>sRGB</c> chunk, or null if absent. 0..3 per PNG spec.</summary>
    public byte? SrgbRenderingIntent { get; init; }

    /// <summary>Decoded gamma (e.g. 0.45455 for sRGB) from the <c>gAMA</c> chunk, or null if absent.</summary>
    public double? Gamma { get; init; }

    /// <summary>Chromaticity values from the <c>cHRM</c> chunk, or null if absent.</summary>
    public ChromaticityChunk? Chromaticity { get; init; }

    /// <summary>Raw EXIF blob from the <c>eXIf</c> chunk, or null if absent.</summary>
    public byte[]? Exif { get; init; }

    /// <summary>
    /// Every text chunk (<c>tEXt</c>, <c>zTXt</c>, <c>iTXt</c>) in file order, one <see cref="PngText"/> each;
    /// empty when there are none. A text chunk that does not parse is left out, never a failed decode.
    /// </summary>
    public IReadOnlyList<PngText> Text { get; init; } = [];

    /// <summary>PNG-3 <c>cICP</c> Coding-Independent Code Points (HDR color signaling), or null if absent.</summary>
    public CicpChunk? Cicp { get; init; }

    /// <summary>PNG-3 <c>mDCv</c> Mastering Display Color Volume, or null if absent.</summary>
    public MdcvChunk? Mdcv { get; init; }

    /// <summary>PNG-3 <c>cLLI</c> Content Light Level Information, or null if absent.</summary>
    public ClliChunk? Clli { get; init; }

    /// <summary>True if any PNG-3 HDR signaling chunk (cICP / mDCv / cLLI) is present.</summary>
    public bool HasHdrSignaling => Cicp is not null || Mdcv is not null || Clli is not null;

    /// <summary>
    /// Sample count per row (<see cref="Width"/> × samples-per-pixel for the color type).
    /// Useful for unpacking <see cref="Pixels"/> without re-deriving the layout.
    /// </summary>
    public int SamplesPerPixel => PngReader.SamplesPerPixel(ColorType);

    /// <summary>
    /// Returns <see cref="Pixels"/> as a <c>ushort[]</c> in host-native endian.
    /// Only valid for 16-bit images; throws otherwise.
    /// </summary>
    public ushort[] AsUInt16Samples()
    {
        if (BitDepth != 16)
            throw new InvalidOperationException($"AsUInt16Samples is only valid for 16-bit images; this is {BitDepth}-bit");
        var n = Pixels.Length / 2;
        var result = new ushort[n];
        for (var i = 0; i < n; i++)
            result[i] = BinaryPrimitives.ReadUInt16BigEndian(Pixels.AsSpan(i * 2, 2));
        return result;
    }

    /// <summary>
    /// Expand the raw <see cref="Pixels"/> samples into tightly-packed 8-bit RGBA
    /// (row-major, 4 bytes per pixel, top-down) — the layout most GPU / UI consumers
    /// want. Handles every color type the decoder produces: greyscale (0), RGB (2),
    /// indexed (3, via <see cref="Palette"/> + optional <see cref="PaletteAlpha"/>),
    /// greyscale + alpha (4), and RGBA (6). 16-bit samples are truncated to their high
    /// byte (<see cref="Pixels"/> holds 16-bit values big-endian, so the byte at the
    /// sample offset already IS the high byte).
    /// </summary>
    /// <remarks>
    /// The faithful-decode counterpart to the "give me display pixels" need:
    /// <see cref="PngReader.Decode"/> keeps samples in their declared color type to
    /// preserve the file exactly; this collapses them to RGBA8 on demand, so consumers
    /// don't each re-implement the big-endian truncation and palette lookup.
    /// </remarks>
    /// <exception cref="InvalidDataException">
    /// <see cref="ColorType"/> is not one of the five PNG color types (0/2/3/4/6), the
    /// image is indexed but carries no <see cref="Palette"/>, or a pixel indexes past
    /// the palette's end.
    /// </exception>
    public byte[] ToRgba8()
    {
        var dst = new byte[Width * Height * 4];
        ExpandToRgba8(dst);
        return dst;
    }

    /// <summary>
    /// Expand the raw <see cref="Pixels"/> samples into a caller-provided 8-bit RGBA
    /// destination (row-major, 4 bytes per pixel, top-down) - identical mapping to
    /// <see cref="ToRgba8()"/> but writing into <paramref name="destination"/> instead
    /// of allocating a result, so a codec facade can decode straight into a pooled or
    /// UI-owned buffer. <paramref name="destination"/> must be at least
    /// <c>Width * Height * 4</c> bytes.
    /// </summary>
    public void ExpandToRgba8(Span<byte> destination)
    {
        var w = Width;
        var h = Height;
        if (destination.Length < (long)w * h * 4)
            throw new ArgumentException($"Destination too small: {destination.Length} < {(long)w * h * 4} (Width*Height*4).", nameof(destination));

        // Pixels holds unpacked samples: a sub-byte image's are 8-bit here (BitDepth), its grey already scaled.
        var expander = new PngRgba8Expander(ColorType, BitDepth, Palette, PaletteAlpha);
        var rowBytes = expander.RowBytes(w);
        for (var y = 0; y < h; y++)
            expander.Expand(Pixels.AsSpan(y * rowBytes, rowBytes), destination.Slice(y * w * 4, w * 4), w);
    }
}

/// <summary>
/// PNG <c>cHRM</c> chunk payload: chromaticity coordinates of the white point
/// and three primaries, encoded as fixed-point (×100000) per PNG spec §11.3.3.5.
/// </summary>
public sealed record ChromaticityChunk(
    double WhiteX, double WhiteY,
    double RedX, double RedY,
    double GreenX, double GreenY,
    double BlueX, double BlueY)
{
    internal static ChromaticityChunk Read(ReadOnlySpan<byte> chunkData)
    {
        // Local helper can't close over a ReadOnlySpan<byte>; inline the eight reads.
        var wx = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(0, 4))  / 100_000.0;
        var wy = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4))  / 100_000.0;
        var rx = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(8, 4))  / 100_000.0;
        var ry = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(12, 4)) / 100_000.0;
        var gx = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(16, 4)) / 100_000.0;
        var gy = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(20, 4)) / 100_000.0;
        var bx = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(24, 4)) / 100_000.0;
        var by = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(28, 4)) / 100_000.0;
        return new ChromaticityChunk(wx, wy, rx, ry, gx, gy, bx, by);
    }

    internal void Write(Span<byte> dst)
    {
        // dst must be exactly 32 bytes; caller writes the chunk wrapper.
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(0, 4),  (uint)Math.Round(WhiteX * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(4, 4),  (uint)Math.Round(WhiteY * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(8, 4),  (uint)Math.Round(RedX   * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(12, 4), (uint)Math.Round(RedY   * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(16, 4), (uint)Math.Round(GreenX * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(20, 4), (uint)Math.Round(GreenY * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(24, 4), (uint)Math.Round(BlueX  * 100_000.0));
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(28, 4), (uint)Math.Round(BlueY  * 100_000.0));
    }
}

/// <summary>
/// PNG-3 <c>cICP</c> chunk — Coding-Independent Code Points (4 bytes).
/// Identifies the colour space + transfer function of the image's sample
/// values using H.273 / ITU CICP numbering. This is how PNG-3 declares
/// HDR — pixel data stays 16-bit integer, but cICP says e.g. "BT.2020
/// primaries + PQ transfer function" to flag it as HDR10.
/// </summary>
/// <param name="ColorPrimaries">H.273 §8.1 colour primaries codepoint.</param>
/// <param name="TransferFunction">H.273 §8.2 transfer characteristics codepoint.</param>
/// <param name="MatrixCoefficients">H.273 §8.3 matrix codepoint. PNG-3 §11.3.2.6 requires <see cref="Sdcb.SimdCodecs.Abstractions.MatrixCoefficients.Identity"/> (RGB) — PNG doesn't carry YCbCr.</param>
/// <param name="VideoFullRangeFlag">PNG-3 requires <c>true</c> (full range 0..2^N-1).</param>
public sealed record CicpChunk(
    Sdcb.SimdCodecs.Abstractions.ColorPrimaries ColorPrimaries,
    Sdcb.SimdCodecs.Abstractions.TransferFunction TransferFunction,
    Sdcb.SimdCodecs.Abstractions.MatrixCoefficients MatrixCoefficients,
    bool VideoFullRangeFlag)
{
    internal static CicpChunk Read(ReadOnlySpan<byte> chunkData)
    {
        if (chunkData.Length != 4)
            throw new InvalidDataException($"cICP chunk length must be 4, got {chunkData.Length}");
        return new CicpChunk(
            ColorPrimaries:       (Sdcb.SimdCodecs.Abstractions.ColorPrimaries)chunkData[0],
            TransferFunction:     (Sdcb.SimdCodecs.Abstractions.TransferFunction)chunkData[1],
            MatrixCoefficients:   (Sdcb.SimdCodecs.Abstractions.MatrixCoefficients)chunkData[2],
            VideoFullRangeFlag:   chunkData[3] != 0);
    }

    internal void Write(Span<byte> dst)
    {
        dst[0] = (byte)ColorPrimaries;
        dst[1] = (byte)TransferFunction;
        dst[2] = (byte)MatrixCoefficients;
        dst[3] = (byte)(VideoFullRangeFlag ? 1 : 0);
    }

    /// <summary>BT.2020 + PQ (SMPTE 2084) — the standard "HDR10" signalling.</summary>
    public static CicpChunk Hdr10Pq => new(
        Sdcb.SimdCodecs.Abstractions.ColorPrimaries.BT2020,
        Sdcb.SimdCodecs.Abstractions.TransferFunction.Pq,
        Sdcb.SimdCodecs.Abstractions.MatrixCoefficients.Identity,
        true);

    /// <summary>BT.2020 + HLG (ARIB STD-B67) — broadcast HDR.</summary>
    public static CicpChunk Bt2020Hlg => new(
        Sdcb.SimdCodecs.Abstractions.ColorPrimaries.BT2020,
        Sdcb.SimdCodecs.Abstractions.TransferFunction.Hlg,
        Sdcb.SimdCodecs.Abstractions.MatrixCoefficients.Identity,
        true);

    /// <summary>
    /// sRGB primaries + PQ transfer — "narrow-gamut HDR". Non-canonical for
    /// HDR10 (which is BT.2020 + PQ) but valid PNG-3 / ICC v4.4; sidesteps
    /// the gamut-mismatch desaturation that consumer HDR displays sometimes
    /// produce on BT.2020-encoded content displayed without a correct
    /// inverse-gamut tonemap. Use when the source content is sRGB-saturation
    /// and you want PQ HDR luminance without re-mapping primaries.
    /// </summary>
    public static CicpChunk SrgbPq => new(
        Sdcb.SimdCodecs.Abstractions.ColorPrimaries.BT709,
        Sdcb.SimdCodecs.Abstractions.TransferFunction.Pq,
        Sdcb.SimdCodecs.Abstractions.MatrixCoefficients.Identity,
        true);

    /// <summary>sRGB primaries + sRGB transfer — explicit signal of SDR sRGB.</summary>
    public static CicpChunk Srgb => new(
        Sdcb.SimdCodecs.Abstractions.ColorPrimaries.BT709,
        Sdcb.SimdCodecs.Abstractions.TransferFunction.Srgb,
        Sdcb.SimdCodecs.Abstractions.MatrixCoefficients.Identity,
        true);
}

/// <summary>
/// PNG-3 <c>mDCv</c> chunk — Mastering Display Color Volume (24 bytes).
/// Tells the renderer what reference display the content was mastered on,
/// so playback can tone-map appropriately. Mirrors the SMPTE ST 2086 /
/// HEVC SEI "mastering display color volume" payload.
/// </summary>
/// <param name="RedX">Raw u16; primaries are in units of 0.00002 (so 35400 ≈ 0.708).</param>
/// <param name="MaxLuminanceUnits">Raw u32; units of 0.0001 cd/m² (10000000 = 1000 cd/m²).</param>
public sealed record MdcvChunk(
    ushort RedX, ushort RedY,
    ushort GreenX, ushort GreenY,
    ushort BlueX, ushort BlueY,
    ushort WhitePointX, ushort WhitePointY,
    uint MaxLuminanceUnits,
    uint MinLuminanceUnits)
{
    internal static MdcvChunk Read(ReadOnlySpan<byte> chunkData)
    {
        if (chunkData.Length != 24)
            throw new InvalidDataException($"mDCv chunk length must be 24, got {chunkData.Length}");
        return new MdcvChunk(
            RedX:        BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(0, 2)),
            RedY:        BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(2, 2)),
            GreenX:      BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(4, 2)),
            GreenY:      BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(6, 2)),
            BlueX:       BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(8, 2)),
            BlueY:       BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(10, 2)),
            WhitePointX: BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(12, 2)),
            WhitePointY: BinaryPrimitives.ReadUInt16BigEndian(chunkData.Slice(14, 2)),
            MaxLuminanceUnits: BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(16, 4)),
            MinLuminanceUnits: BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(20, 4)));
    }

    internal void Write(Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(0, 2),  RedX);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(2, 2),  RedY);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(4, 2),  GreenX);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(6, 2),  GreenY);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(8, 2),  BlueX);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(10, 2), BlueY);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(12, 2), WhitePointX);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(14, 2), WhitePointY);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(16, 4), MaxLuminanceUnits);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(20, 4), MinLuminanceUnits);
    }
}

/// <summary>
/// PNG-3 <c>cLLI</c> chunk — Content Light Level Information (8 bytes).
/// Describes the brightest pixel (<c>MaxCll</c>) and brightest average frame
/// (<c>MaxFall</c>) the content actually contains, so the renderer can
/// pre-allocate tone-mapping headroom. Both values are raw u32 in units of
/// 0.0001 cd/m² (i.e., divide by 10000 to get nits).
/// </summary>
public sealed record ClliChunk(uint MaxCllUnits, uint MaxFallUnits)
{
    internal static ClliChunk Read(ReadOnlySpan<byte> chunkData)
    {
        if (chunkData.Length != 8)
            throw new InvalidDataException($"cLLI chunk length must be 8, got {chunkData.Length}");
        return new ClliChunk(
            MaxCllUnits:  BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(0, 4)),
            MaxFallUnits: BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4)));
    }

    internal void Write(Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(0, 4), MaxCllUnits);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(4, 4), MaxFallUnits);
    }
}
