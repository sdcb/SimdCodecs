using System.Runtime.InteropServices;
using ImageMagick;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="TiffReader.ReadSegment(ReadOnlySpan{byte}, int, int, Span{byte})"/>: one strip or tile of a
/// page, read alone, must equal the same rectangle of the whole-page read -- bit for bit, from a span,
/// from a seekable stream and from a file path -- while touching only that segment's bytes.
/// </summary>
/// <remarks>
/// The whole-page read (<see cref="TiffReader.Read(ReadOnlySpan{byte})"/>) is the reference, and it is
/// itself pinned against libtiff elsewhere, so what is checked here is only that the on-demand path has
/// not drifted from it: planes interleaved, predictor undone at tile width, edge padding dropped, byte
/// order swapped. Sizes are chosen so neither axis divides by the tile or strip size.
/// </remarks>
public sealed class TiffSegmentReadTests
{
    private const int Width = 70;
    private const int Height = 50;

    public enum Kind { U8, U16, U32, F32 }

    public static IEnumerable<object[]> LibTiffCases()
    {
        foreach (var tiled in new[] { false, true })
        foreach (var planar in new[] { false, true })
        foreach (var spp in new[] { 1, 3 })
        foreach (var kind in Enum.GetValues<Kind>())
        foreach (var (compression, predictor) in new[]
        {
            (CompressionMethod.NoCompression, 1),
            (CompressionMethod.RLE, 1),
            (CompressionMethod.LZW, 1),
            (CompressionMethod.LZW, 2),
            (CompressionMethod.Zip, 1),
            (CompressionMethod.Zip, 2),
        })
        {
            yield return [tiled, planar, spp, kind, compression, predictor, false];
        }

        // Big-endian files: the swap has to happen per segment too.
        foreach (var tiled in new[] { false, true })
        foreach (var planar in new[] { false, true })
        foreach (var kind in new[] { Kind.U16, Kind.F32 })
        foreach (var predictor in new[] { 1, 2 })
        {
            yield return [tiled, planar, 3, kind, CompressionMethod.Zip, predictor, true];
        }
    }

    [Theory]
    [MemberData(nameof(LibTiffCases))]
    public void EverySegment_EqualsTheWholePageRead(bool tiled, bool planar, int spp, Kind kind,
        CompressionMethod compression, int predictor, bool bigEndian)
    {
        var tiff = WriteWithLibTiff(spp, kind, tiled, planar, compression, predictor, bigEndian);

        // Premises: libtiff wrote the file the case names, or this proves nothing about that axis.
        tiff[0].ShouldBe((byte)(bigEndian ? 'M' : 'I'));
        var layout = TiffReader.ReadLayout(tiff);
        layout.IsTiled.ShouldBe(tiled);
        layout.IsPlanar.ShouldBe(planar && spp > 1);
        layout.SamplesPerPixel.ShouldBe(spp);
        layout.SegmentCount.ShouldBeGreaterThan(1);
        if (tiled)
        {
            layout.TileWidth.ShouldBe(32);
            layout.TileHeight.ShouldBe(16);
            (Width % layout.TileWidth).ShouldNotBe(0);
            (Height % layout.TileHeight).ShouldNotBe(0);
        }
        else
        {
            layout.RowsPerStrip.ShouldBe(7);
            (Height % layout.RowsPerStrip).ShouldNotBe(0);
        }

        AssertAllSegmentsMatch(tiff, layout);
    }

    [Theory]
    [InlineData(false, TiffCompression.Uncompressed, 1, 8)]
    [InlineData(false, TiffCompression.Deflate, 3, 16)]
    [InlineData(true, TiffCompression.Uncompressed, 3, 8)]
    [InlineData(true, TiffCompression.Deflate, 1, 16)]
    [InlineData(true, TiffCompression.Deflate, 1, 32)]
    [InlineData(true, TiffCompression.Uncompressed, 3, 32)]
    public async Task WriterOutput_EverySegmentEqualsTheWholePageRead(bool tiled, TiffCompression compression,
        int spp, int bits)
    {
        var tiff = await WriteWithWriterAsync(Width, Height, spp, bits, compression, tiled);
        var layout = TiffReader.ReadLayout(tiff);
        layout.IsTiled.ShouldBe(tiled);
        AssertAllSegmentsMatch(tiff, layout);
    }

    [Fact]
    public async Task MultiPageFile_ReadsPageOne()
    {
        var first = Pattern(20, 12, 1, 8);
        var second = Pattern(Width, Height, 3, 16);
        using var ms = new MemoryStream();
        await using (var writer = TiffWriter.Create(ms))
        {
            await writer.AddPageAsync(first, 20, 12, Options(1, 8, TiffCompression.Uncompressed, tiled: false), TestContext.Current.CancellationToken);
            await writer.AddPageAsync(second, Width, Height, Options(3, 16, TiffCompression.Deflate, tiled: true), TestContext.Current.CancellationToken);
            await writer.FlushAsync(TestContext.Current.CancellationToken);
        }
        var tiff = ms.ToArray();

        var layout = TiffReader.ReadLayout(tiff, pageIndex: 1);
        layout.PageIndex.ShouldBe(1);
        layout.Width.ShouldBe(Width);
        layout.IsTiled.ShouldBeTrue();
        TiffReader.ReadLayout(tiff, 0).IsTiled.ShouldBeFalse();

        AssertAllSegmentsMatch(tiff, layout);
        Should.Throw<ArgumentOutOfRangeException>(() => TiffReader.ReadLayout(tiff, 2));
    }

    [Fact]
    public async Task SegmentAtAndRectangle_AgreeWithEachOther()
    {
        var tiff = await WriteWithWriterAsync(Width, Height, 1, 16, TiffCompression.Deflate, tiled: true);
        var layout = TiffReader.ReadLayout(tiff);

        var covered = new int[Width * Height];
        for (var i = 0; i < layout.SegmentCount; i++)
        {
            var r = layout.SegmentRectangle(i);
            r.X.ShouldBeGreaterThanOrEqualTo(0);
            (r.X + r.Width).ShouldBeLessThanOrEqualTo(Width);
            (r.Y + r.Height).ShouldBeLessThanOrEqualTo(Height);
            for (var y = r.Y; y < r.Y + r.Height; y++)
            for (var x = r.X; x < r.X + r.Width; x++)
            {
                covered[(y * Width) + x]++;
                layout.SegmentAt(x, y).ShouldBe(i);
            }
        }
        covered.ShouldAllBe(c => c == 1, "segments tile the image exactly once");
        Should.Throw<ArgumentOutOfRangeException>(() => layout.SegmentAt(Width, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => layout.SegmentAt(0, -1));
    }

    [Fact]
    public async Task BadArguments_ThrowTheDocumentedExceptions()
    {
        var tiff = await WriteWithWriterAsync(Width, Height, 3, 8, TiffCompression.Deflate, tiled: true);
        var layout = TiffReader.ReadLayout(tiff);
        var big = new byte[layout.SegmentBytes(0)];

        Should.Throw<ArgumentOutOfRangeException>(() => TiffReader.ReadSegment(tiff, 0, -1, big));
        Should.Throw<ArgumentOutOfRangeException>(() => TiffReader.ReadSegment(tiff, 0, layout.SegmentCount, big));
        Should.Throw<ArgumentOutOfRangeException>(() => TiffReader.ReadSegment(tiff, 5, 0, big));

        var small = new byte[big.Length - 1];
        var ex = Should.Throw<ArgumentException>(() => TiffReader.ReadSegment(tiff, 0, 0, small));
        ex.Message.ShouldContain(big.Length.ToString());
        Should.Throw<ArgumentException>(() => TiffReader.ReadSegment(new NonSeekable(tiff), 0, 0, big));
    }

    [Fact]
    public async Task TruncatedFile_ThrowsInsteadOfReadingPastTheEnd()
    {
        var tiff = await WriteWithWriterAsync(Width, Height, 1, 8, TiffCompression.Uncompressed, tiled: false);
        var layout = TiffReader.ReadLayout(tiff);
        var last = layout.SegmentCount - 1;
        // Cut inside the last segment's bytes, wherever the writer put them relative to the IFD.
        var extent = SegmentExtent(layout, last)[0];
        var cut = tiff.AsSpan(0, (int)(extent.Start + extent.Length - 3)).ToArray();
        Should.Throw<InvalidDataException>(() =>
            TiffReader.ReadSegment(new MemoryStream(cut), 0, last, new byte[layout.SegmentBytes(last)]));
    }

    // ---- the comparison ------------------------------------------------------

    private static void AssertAllSegmentsMatch(byte[] tiff, TiffPageLayout layout)
    {
        var whole = TiffReader.Read(tiff).Pages[layout.PageIndex].Pixels;
        var bpp = layout.BytesPerPixel;
        var path = Path.Combine(Path.GetTempPath(), $"segread-{Guid.NewGuid():N}.tif");
        File.WriteAllBytes(path, tiff);
        try
        {
            for (var i = 0; i < layout.SegmentCount; i++)
            {
                var rect = layout.SegmentRectangle(i);
                var expected = new byte[rect.Width * rect.Height * bpp];
                for (var row = 0; row < rect.Height; row++)
                    whole.AsSpan((((rect.Y + row) * layout.Width) + rect.X) * bpp, rect.Width * bpp)
                        .CopyTo(expected.AsSpan(row * rect.Width * bpp));
                layout.SegmentBytes(i).ShouldBe(expected.Length);

                var label = $"segment {i} ({rect})";

                var fromSpan = Poisoned(expected.Length + 5);
                TiffReader.ReadSegment(tiff, layout.PageIndex, i, fromSpan).ShouldBe(rect);
                fromSpan.AsSpan(0, expected.Length).ToArray().ShouldBe(expected, label + " from span");
                fromSpan.AsSpan(expected.Length).ToArray().ShouldAllBe(b => b == 0xEE, label + " wrote past its rectangle");

                var viaLayout = new byte[expected.Length];
                TiffReader.ReadSegment(tiff, layout, i, viaLayout);
                viaLayout.ShouldBe(expected, label + " from span with layout");

                var counting = new CountingStream(tiff);
                var fromStream = new byte[expected.Length];
                TiffReader.ReadSegment(counting, layout.PageIndex, i, fromStream);
                fromStream.ShouldBe(expected, label + " from stream");
                AssertTouchedOnlyThisSegment(counting, layout, i, label);

                var withLayout = new CountingStream(tiff);
                TiffReader.ReadSegment(withLayout, layout, i, fromStream);
                fromStream.ShouldBe(expected, label + " from stream with layout");
                AssertTouchedOnlyThisSegment(withLayout, layout, i, label);
                withLayout.BytesRead.ShouldBeLessThanOrEqualTo(SegmentExtent(layout, i).Sum(e => e.Length),
                    "with a layout in hand only the segment itself is read");

                var fromFile = new byte[expected.Length];
                TiffReader.ReadSegment(path, layout.PageIndex, i, fromFile);
                fromFile.ShouldBe(expected, label + " from file");
                TiffReader.ReadSegment(path, layout, i, fromFile);
                fromFile.ShouldBe(expected, label + " from file with layout");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static List<(long Start, long Length)> SegmentExtent(TiffPageLayout layout, int segment)
    {
        var geometry = layout.Geometry;
        var down = segment / geometry.SegmentsAcross;
        var across = segment % geometry.SegmentsAcross;
        var list = new List<(long, long)>();
        for (var plane = 0; plane < geometry.PlaneCount; plane++)
        {
            var index = geometry.SegmentIndex(plane, down, across);
            list.Add((layout.Offsets[index], layout.ByteCounts[index]));
        }
        return list;
    }

    /// <summary>
    /// The stream reads are the header, directories and tag arrays (all outside any segment's bytes)
    /// plus this segment's own bytes: no read may overlap the byte range of any OTHER segment, and
    /// every byte of this one must have been fetched.
    /// </summary>
    private static void AssertTouchedOnlyThisSegment(CountingStream stream, TiffPageLayout layout, int segment, string label)
    {
        var mine = SegmentExtent(layout, segment);
        var geometry = layout.Geometry;
        var mineIndexes = new HashSet<int>();
        var down = segment / geometry.SegmentsAcross;
        var across = segment % geometry.SegmentsAcross;
        for (var plane = 0; plane < geometry.PlaneCount; plane++)
            mineIndexes.Add(geometry.SegmentIndex(plane, down, across));

        for (var index = 0; index < layout.Offsets.Length; index++)
        {
            if (mineIndexes.Contains(index) || layout.ByteCounts[index] == 0) continue;
            var start = (long)layout.Offsets[index];
            var end = start + layout.ByteCounts[index];
            foreach (var (readStart, readLength) in stream.Reads)
                (readStart < end && readStart + readLength > start).ShouldBeFalse(
                    $"{label}: read [{readStart}, +{readLength}) overlaps segment {index} that was not asked for");
        }

        foreach (var (start, length) in mine)
        {
            var covered = new bool[length];
            foreach (var (readStart, readLength) in stream.Reads)
                for (var p = Math.Max(readStart, start); p < Math.Min(readStart + readLength, start + length); p++)
                    covered[p - start] = true;
            covered.ShouldAllBe(c => c, $"{label}: not every byte of the segment was read");
        }

        stream.BytesRead.ShouldBeLessThan(stream.Length, $"{label}: the whole file was read");
    }

    // ---- fixtures ------------------------------------------------------------

    private static byte[] Poisoned(int length)
    {
        var bytes = new byte[length];
        bytes.AsSpan().Fill(0xEE);
        return bytes;
    }

    private static byte[] Pattern(int width, int height, int spp, int bits)
    {
        var bytesPerSample = bits / 8;
        var bytes = new byte[width * height * spp * bytesPerSample];
        for (var i = 0; i < width * height * spp; i++)
        {
            var v = (uint)(((i * 2654435761u) >> 8) & 0xFFFF);
            switch (bits)
            {
                case 8: bytes[i] = (byte)(v >> 8); break;
                case 16: BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), (ushort)v); break;
                default: BitConverter.TryWriteBytes(bytes.AsSpan(i * 4, 4), v / 65535f); break;
            }
        }
        return bytes;
    }

    private static TiffPageOptions Options(int spp, int bits, TiffCompression compression, bool tiled) => new()
    {
        SamplesPerPixel = spp,
        BitsPerSample = bits,
        Photometric = spp == 1 ? TiffPhotometric.MinIsBlack : TiffPhotometric.Rgb,
        SampleFormat = bits == 32 ? TiffSampleFormat.IeeeFloat : TiffSampleFormat.Uint,
        Compression = compression,
        Layout = tiled ? TiffLayout.Tiled : TiffLayout.Strip,
        TileWidth = 32,
        TileHeight = 16,
        RowsPerStrip = 7,
    };

    private static async Task<byte[]> WriteWithWriterAsync(int width, int height, int spp, int bits,
        TiffCompression compression, bool tiled)
    {
        using var ms = new MemoryStream();
        await using (var writer = TiffWriter.Create(ms))
        {
            await writer.AddPageAsync(Pattern(width, height, spp, bits), width, height,
                Options(spp, bits, compression, tiled));
            await writer.FlushAsync();
        }
        return ms.ToArray();
    }

    private static byte[] WriteWithLibTiff(int spp, Kind kind, bool tiled, bool planar,
        CompressionMethod compression, int predictor, bool bigEndian)
    {
        var mapping = spp == 1 ? "I" : "RGB";
        var bits = kind switch { Kind.U8 => 8, Kind.U16 => 16, _ => 32 };
        var bytes = Pattern(Width, Height, spp, bits);
        var settingsFor = (uint)Width;
        using var image = new MagickImage();
        switch (kind)
        {
            case Kind.U8:
                image.ReadPixels(bytes, new PixelReadSettings(settingsFor, (uint)Height, StorageType.Char, mapping));
                break;
            case Kind.U16:
                image.ReadPixels(bytes, new PixelReadSettings(settingsFor, (uint)Height, StorageType.Short, mapping));
                break;
            case Kind.U32:
            {
                // Values scaled to the full 32-bit range; whatever Magick quantises them to is what the file holds.
                var ints = MemoryMarshal.Cast<byte, uint>(bytes);
                var scaled = new uint[ints.Length];
                for (var i = 0; i < scaled.Length; i++) scaled[i] = (ints[i] & 0xFFFF) * 65537u;
                image.ReadPixels(MemoryMarshal.AsBytes<uint>(scaled).ToArray(),
                    new PixelReadSettings(settingsFor, (uint)Height, StorageType.Int32, mapping));
                break;
            }
            default:
                image.ReadPixels(bytes, new PixelReadSettings(settingsFor, (uint)Height, StorageType.Float, mapping));
                break;
        }

        if (spp == 1) image.ColorSpace = ColorSpace.Gray;
        image.Depth = (uint)bits;
        image.Settings.Interlace = planar ? Interlace.Plane : Interlace.NoInterlace;
        image.Settings.Compression = compression;
        image.Settings.SetDefine(MagickFormat.Tiff, "predictor", predictor.ToString());
        if (tiled)
            image.Settings.SetDefine(MagickFormat.Tiff, "tile-geometry", "32x16");
        else
            image.Settings.SetDefine(MagickFormat.Tiff, "rows-per-strip", "7");
        if (kind == Kind.F32)
            image.Settings.SetDefine("quantum:format", "floating-point");
        if (bigEndian)
            image.Settings.SetDefine(MagickFormat.Tiff, "endian", "msb");

        return image.ToByteArray(MagickFormat.Tiff);
    }

    // ---- streams ---------------------------------------------------------------

    private sealed class NonSeekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    /// <summary>A seekable read-only stream that remembers every (offset, length) it was asked to deliver.</summary>
    private sealed class CountingStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public List<(long Start, long Length)> Reads { get; } = [];
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var start = _inner.Position;
            var n = _inner.Read(buffer);
            if (n > 0)
            {
                Reads.Add((start, n));
                BytesRead += n;
            }
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
