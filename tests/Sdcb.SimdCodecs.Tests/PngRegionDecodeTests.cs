using System.Buffers.Binary;
using System.Reflection;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="DecodeOptions.Region"/> and <see cref="DecodeOptions.ScaleDenominator"/> on a non-interlaced PNG decode a
/// window of rows (<c>PngReader.DecodeWindow</c>), box-filtering as they go and stopping after the last row needed. The
/// contract is exact: the result equals the generic path, the whole image decoded in <see cref="PngImageDecoder.TryDecode"/>'s
/// layout, then <see cref="BoxDownscaler.Downscale"/> (or a crop), then <see cref="PixelConverter"/> for
/// <see cref="PngImageDecoder.TryDecodeInto"/>, for every colour type, depth, tRNS, scale, region and pixel format.
/// </summary>
public sealed class PngRegionDecodeTests
{
    private const int W = 37;
    private const int H = 29;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly PixelFormat[] AllFormats =
    [
        .. typeof(PixelFormat).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(PixelFormat))
            .Select(p => (PixelFormat)p.GetValue(null)!),
    ];

    public static TheoryData<string> Files() => [.. Corpus().Keys];

    private static Dictionary<string, byte[]>? _corpus;

    private static Dictionary<string, byte[]> Corpus()
    {
        if (_corpus is not null) return _corpus;
        var files = new Dictionary<string, byte[]>();
        // ImageMagick (libpng) writes each colour type and depth: foreign bytes, its own filter choices.
        (int ColorType, int Depth)[] layouts =
            [(0, 1), (0, 2), (0, 4), (0, 8), (0, 16), (2, 8), (2, 16), (3, 1), (3, 2), (3, 4), (3, 8), (4, 8), (4, 16), (6, 8), (6, 16)];
        foreach (var (colorType, depth) in layouts)
            files[$"magick ct{colorType} d{depth}"] = PngRgba8ExpansionTests.Encode(colorType, depth, false, W, H);
        files["magick ct3 d8 palette alpha"] = PngRgba8ExpansionTests.Encode(3, 8, true, W, H);
        files["magick ct0 d8 1x1"] = PngRgba8ExpansionTests.Encode(0, 8, false, 1, 1);
        files["magick ct6 d8 1x40"] = PngRgba8ExpansionTests.Encode(6, 8, false, 1, 40);
        files["magick ct2 d16 40x1"] = PngRgba8ExpansionTests.Encode(2, 16, false, 40, 1);
        // Adam7 takes the generic path, which must still give the same result.
        files["magick ct6 d8 interlaced"] = PngRgba8ExpansionTests.Encode(6, 8, false, W, H, interlace: true);

        // A tRNS transparent colour, the first pixel's, so some pixels match it.
        foreach (var (colorType, depth) in new[] { (0, 1), (0, 2), (0, 4), (0, 8), (0, 16), (2, 8), (2, 16) })
        {
            var plain = PngRgba8ExpansionTests.Encode(colorType, depth, false, W, H);
            var image = PngReader.Decode(plain, Ct);
            var channels = colorType == 0 ? 1 : 3;
            var bytesPerSample = image.BitDepth / 8;
            var key = new byte[2 * channels];
            for (var c = 0; c < channels; c++)
            {
                int v = bytesPerSample == 2
                    ? BinaryPrimitives.ReadUInt16BigEndian(image.Pixels.AsSpan(2 * c))
                    : image.Pixels[c];
                if (depth < 8)
                    v = v * ((1 << depth) - 1) / 255; // the unpacked level back to the file's depth
                BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(2 * c), (ushort)v);
            }
            files[$"magick ct{colorType} d{depth} tRNS"] = PngTransparentColorTests.WithChunk(plain, "tRNS"u8, key);
        }

        // Our own encoder, with its filter choices.
        var random = new Random(5);
        var rgba = new byte[W * H * 4];
        random.NextBytes(rgba);
        for (var i = 0; i < W * H; i++)
            rgba[4 * i + 3] = (byte)(i % 5 == 0 ? 0 : rgba[4 * i + 3]);
        files["ours rgba8"] = PngWriter.Encode(rgba, W, H);
        var rgb = new byte[W * H * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)((i * 7) ^ (i / 97));
        files["ours rgb8"] = PngWriter.EncodeRgb8(rgb, W, H);
        var gray16 = new ushort[W * H];
        for (var i = 0; i < gray16.Length; i++) gray16[i] = (ushort)(i * 977);
        files["ours gray16"] = PngWriter.EncodeGray16(gray16, W, H);
        return _corpus = files;
    }

    // Regions of the scaled image: the whole of it, corners, the last row and column, a middle block.
    private static IEnumerable<PixelRect?> Regions(int width, int height)
    {
        yield return null;
        yield return new PixelRect(0, 0, width, height);
        yield return new PixelRect(0, 0, 1, 1);
        yield return new PixelRect(width - 1, height - 1, 1, 1);
        yield return new PixelRect(0, height - 1, width, 1);
        yield return new PixelRect(width - 1, 0, 1, height);
        yield return new PixelRect(width / 3, height / 3, Math.Max(1, width / 2), Math.Max(1, height / 3));
    }

    // The generic path's result: the whole image, box-filtered and cropped.
    private static RasterImage Reference(IDecodedImage full, int factor, PixelRect? region)
    {
        if (factor > 1)
            return BoxDownscaler.Downscale(full, factor, region);
        return RasterImage.Crop(full, region ?? new PixelRect(0, 0, full.Width, full.Height));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void TryDecode_EqualsTheGenericPath(string name)
    {
        var png = Corpus()[name];
        PngImageDecoder.TryDecode(png, out var full, cancellationToken: Ct).ShouldBeTrue();
        using var _ = full;
        foreach (var factor in new[] { 1, 2, 4, 8, 16 })
        {
            var (sw, sh) = ((full!.Width + factor - 1) / factor, (full.Height + factor - 1) / factor);
            foreach (var region in Regions(sw, sh))
            {
                if (factor == 1 && region is null) continue;
                using var expected = Reference(full, factor, region);
                var options = new DecodeOptions { ScaleDenominator = factor, Region = region };
                PngImageDecoder.TryDecode(png, out var actual, options, Ct).ShouldBeTrue();
                using (actual)
                {
                    var what = $"{name} 1/{factor} {region}";
                    (actual!.Width, actual.Height, actual.Channels, actual.SampleFormat, actual.BitDepth)
                        .ShouldBe((expected.Width, expected.Height, expected.Channels, expected.SampleFormat, expected.BitDepth), what);
                    actual.ColorEncoding.ShouldBe(expected.ColorEncoding, what);
                    actual.Pixels.ToArray().ShouldBe(expected.Pixels.ToArray(), what);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void TryDecodeInto_EqualsTheGenericPath_ForEveryFormat(string name)
    {
        var png = Corpus()[name];
        PngImageDecoder.TryDecode(png, out var full, cancellationToken: Ct).ShouldBeTrue();
        using var _ = full;
        foreach (var factor in new[] { 1, 2, 4, 8, 16 })
        {
            var (sw, sh) = ((full!.Width + factor - 1) / factor, (full.Height + factor - 1) / factor);
            foreach (var region in Regions(sw, sh))
            {
                if (factor == 1 && region is null) continue;
                using var reference = Reference(full, factor, region);
                var options = new DecodeOptions { ScaleDenominator = factor, Region = region };
                foreach (var format in AllFormats)
                {
                    // Padded rows: the bytes between them stay untouched.
                    var stride = reference.Width * format.BytesPerPixel + 3;
                    var size = stride * reference.Height;
                    var expected = new byte[size];
                    var actual = new byte[size];
                    expected.AsSpan().Fill(0xCD);
                    actual.AsSpan().Fill(0xCD);
                    PixelConverter.TryConvert(reference, expected, stride, format).ShouldBeTrue();
                    var what = $"{name} 1/{factor} {region} {format}";
                    PngImageDecoder.TryDecodeInto(png, actual, stride, format, options, Ct).ShouldBeTrue(what);
                    actual.ShouldBe(expected, what);
                }
            }
        }
    }

    [Fact]
    public void TryDecodeInto_TooSmallDestination_IsFalse()
    {
        var png = Corpus()["magick ct6 d8"];
        var options = new DecodeOptions { ScaleDenominator = 4, Region = new PixelRect(1, 1, 3, 2) };
        PngImageDecoder.TryDecodeInto(png, new byte[3 * 2 * 4 - 1], 0, PixelFormat.Rgba32, options, Ct).ShouldBeFalse();
        PngImageDecoder.TryDecodeInto(png, new byte[3 * 2 * 4], 0, PixelFormat.Rgba32, options, Ct).ShouldBeTrue();
    }

    [Fact]
    public void ARegionOutsideTheScaledImage_Throws()
    {
        var png = Corpus()["magick ct2 d8"];
        var options = new DecodeOptions { ScaleDenominator = 4, Region = new PixelRect(9, 0, 2, 1) }; // 10 columns at 1/4
        Should.Throw<ArgumentException>(() => PngImageDecoder.TryDecode(png, out _, options, Ct));
        Should.Throw<ArgumentException>(() => PngImageDecoder.TryDecodeInto(png, new byte[64], 0, PixelFormat.Rgba32, options, Ct));
    }

    [Fact]
    public void TryDecode_RentsFromThePool()
    {
        var png = Corpus()["magick ct6 d8"];
        PngImageDecoder.TryDecode(png, out var full, cancellationToken: Ct).ShouldBeTrue();
        using var _ = full;
        using var expected = BoxDownscaler.Downscale(full!, 2);
        var pool = new CountingPool();
        PngImageDecoder.TryDecode(png, out var actual, new DecodeOptions { ScaleDenominator = 2, Pool = pool }, Ct).ShouldBeTrue();
        pool.Rented.ShouldBe(1);
        actual!.Pixels.ToArray().ShouldBe(expected.Pixels.ToArray());
        actual.Dispose();
        pool.Returned.ShouldBe(1);
    }

    // ---- Early stop -------------------------------------------------------------------------------------------------

    private const int StopW = 16;
    private const int StopH = 12;
    private const int CorruptRow = 7;

    // 8-bit greyscale, filter None. One stored deflate block holds the rows above CorruptRow and the first bytes of it;
    // the next block has the reserved block type (11), so inflating row CorruptRow fails while every row above it is
    // intact. (The good block ends inside a row: zlib reads a block header as soon as the previous block is done, so a
    // block boundary at the end of the last row read would already meet the bad header.)
    private static byte[] StoredPngWithCorruptRow(out byte[] gray)
    {
        gray = new byte[StopW * StopH];
        for (var i = 0; i < gray.Length; i++) gray[i] = (byte)(i * 13 + 1);
        var raw = new List<byte>();
        for (var y = 0; y < StopH; y++)
        {
            raw.Add(0);
            raw.AddRange(gray.AsSpan(y * StopW, StopW).ToArray());
        }
        var good = CorruptRow * (StopW + 1) + 5;
        var z = new List<byte> { 0x78, 0x01, 0x00, (byte)good, (byte)(good >> 8), (byte)~good, (byte)(~good >> 8) };
        z.AddRange(raw.Take(good));
        z.Add(0x07); // BFINAL 1, BTYPE 11: reserved
        z.AddRange(raw.Skip(good)); // never inflated
        z.AddRange([0, 0, 0, 0]);   // a stand-in Adler-32, never reached

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, StopW);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), StopH);
        ihdr[8] = 8; // depth 8, colour type 0, no interlace
        Chunk(ms, "IHDR"u8, ihdr);
        Chunk(ms, "IDAT"u8, [.. z]);
        Chunk(ms, "IEND"u8, []);
        return ms.ToArray();

        static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            Span<byte> u32 = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(u32, data.Length);
            s.Write(u32);
            s.Write(type);
            s.Write(data);
            BinaryPrimitives.WriteUInt32BigEndian(u32, PngCrc32.Compute(type, data));
            s.Write(u32);
        }
    }

    [Fact]
    public void CorruptImageDataBelowTheRegion_IsNeverRead()
    {
        var png = StoredPngWithCorruptRow(out var gray);
        // The whole image does not decode: the stream is corrupt.
        PngImageDecoder.TryDecode(png, out _, cancellationToken: Ct).ShouldBeFalse();

        // A region that ends above the corrupt row decodes; the decode stops after the region's last row.
        var region = new PixelRect(3, 2, 9, CorruptRow - 2);
        PngImageDecoder.TryDecode(png, out var image, new DecodeOptions { Region = region }, Ct).ShouldBeTrue();
        using (image)
        {
            var expected = new byte[region.Width * region.Height];
            for (var y = 0; y < region.Height; y++)
                gray.AsSpan((region.Y + y) * StopW + region.X, region.Width).CopyTo(expected.AsSpan(y * region.Width));
            image!.Pixels.ToArray().ShouldBe(expected);
        }
        var rgba = new byte[region.Width * region.Height * 4];
        PngImageDecoder.TryDecodeInto(png, rgba, 0, PixelFormat.Rgba32, new DecodeOptions { Region = region }, Ct).ShouldBeTrue();
        rgba[4 * (region.Width * region.Height - 1)].ShouldBe(gray[(region.Y + region.Height - 1) * StopW + region.X + region.Width - 1]);

        // At 1/4 the first output row averages rows 0..3, above the corrupt row; at 1/8 rows 0..7, which reach it.
        PngImageDecoder.TryDecode(png, out var scaled, new DecodeOptions { ScaleDenominator = 4, Region = new PixelRect(0, 0, 4, 1) }, Ct)
            .ShouldBeTrue();
        scaled!.Dispose();
        PngImageDecoder.TryDecode(png, out _, new DecodeOptions { ScaleDenominator = 8, Region = new PixelRect(0, 0, 1, 1) }, Ct)
            .ShouldBeFalse();
    }

    [Fact]
    public void CorruptImageDataInsideOrAboveTheRegion_IsInvalidData()
    {
        var png = StoredPngWithCorruptRow(out _);
        var header = PngReader.ReadHeader(png, out var idat);
        var format = PngReader.FidelityFormat(header);
        foreach (var rect in new[] { new PixelRect(0, CorruptRow, 4, 1), new PixelRect(0, CorruptRow + 2, StopW, 3) })
        {
            Should.Throw<InvalidDataException>(() =>
                PngReader.DecodeWindow(png, header, idat, rect, 1, new byte[rect.Width * rect.Height], 0, format, Ct));
            PngImageDecoder.TryDecode(png, out _, new DecodeOptions { Region = rect }, Ct).ShouldBeFalse();
            PngImageDecoder.TryDecodeInto(png, new byte[rect.Width * rect.Height], 0, PixelFormat.Gray8,
                new DecodeOptions { Region = rect }, Ct).ShouldBeFalse();
        }
    }

    [Fact]
    public void AStreamThatRunsOnPastTheLastRow_FailsWhenTheRegionReachesTheBottom()
    {
        // A valid file with a byte too many in its image data: the full decode refuses it; so does a window down to the
        // last row, which checks the stream's end; one that stops above it does not see it.
        var good = PngRgba8ExpansionTests.Encode(0, 8, false, W, H);
        var header = PngReader.ReadHeader(good, out var idat);
        var raw = PngReader.InflateAndUnfilter(good, idat, W, H, 8, 0, Ct);
        var filtered = new byte[(W + 1) * H + 1];
        for (var y = 0; y < H; y++)
            raw.AsSpan(y * W, W).CopyTo(filtered.AsSpan(y * (W + 1) + 1));
        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(filtered);
        var png = ReplaceIdat(good, compressed.ToArray());

        PngImageDecoder.TryDecode(png, out _, cancellationToken: Ct).ShouldBeFalse();
        PngImageDecoder.TryDecode(png, out _, new DecodeOptions { ScaleDenominator = 2 }, Ct).ShouldBeFalse();
        PngImageDecoder.TryDecode(png, out _, new DecodeOptions { Region = new PixelRect(0, H - 1, 1, 1) }, Ct).ShouldBeFalse();
        PngImageDecoder.TryDecode(png, out var top, new DecodeOptions { Region = new PixelRect(0, 0, W, H - 1) }, Ct).ShouldBeTrue();
        using (top)
            top!.Pixels.ToArray().ShouldBe(raw.AsSpan(0, W * (H - 1)).ToArray());
        header.Interlaced.ShouldBeFalse();
    }

    private static byte[] ReplaceIdat(byte[] png, byte[] zlib)
    {
        using var ms = new MemoryStream();
        ms.Write(png.AsSpan(0, 8));
        var pos = 8;
        var wrote = false;
        while (pos < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = png.AsSpan(pos + 4, 4);
            if (type.SequenceEqual("IDAT"u8))
            {
                if (!wrote)
                {
                    var u32 = new byte[4];
                    BinaryPrimitives.WriteInt32BigEndian(u32, zlib.Length);
                    ms.Write(u32);
                    ms.Write("IDAT"u8);
                    ms.Write(zlib);
                    BinaryPrimitives.WriteUInt32BigEndian(u32, PngCrc32.Compute("IDAT"u8, zlib));
                    ms.Write(u32);
                    wrote = true;
                }
            }
            else
            {
                ms.Write(png.AsSpan(pos, 12 + length));
            }
            pos += 12 + length;
        }
        return ms.ToArray();
    }

    // ---- Memory -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ARegionOfALargeImage_AllocatesRowsNotTheImage()
    {
        const int side = 1024;
        var rgba = new byte[side * side * 4];
        new Random(3).NextBytes(rgba);
        var png = PngWriter.Encode(rgba, side, side);
        var options = new DecodeOptions { ScaleDenominator = 8, Region = new PixelRect(10, 10, 20, 20) };
        PngImageDecoder.TryDecode(png, out var warm, options, Ct).ShouldBeTrue(); // JIT and pools first
        warm!.Dispose();

        var before = GC.GetAllocatedBytesForCurrentThread();
        PngImageDecoder.TryDecode(png, out var image, options, Ct).ShouldBeTrue();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        image!.Dispose();
        // The full decode holds 4 MiB of samples; this holds the 20×20 output and a few rows.
        allocated.ShouldBeLessThan(256 * 1024);
    }

    private sealed class CountingPool : System.Buffers.ArrayPool<byte>
    {
        public int Rented, Returned;
        public override byte[] Rent(int minimumLength) { Rented++; return new byte[minimumLength]; }
        public override void Return(byte[] array, bool clearArray = false) => Returned++;
    }
}
