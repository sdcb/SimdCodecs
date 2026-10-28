using System.Buffers.Binary;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Gif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The decisions the GIF89a specification leaves to a decoder (the table in the package README), each
/// pinned on a hand-assembled file. <see cref="GifStream"/> writes the blocks; its LZW data is single-pixel
/// codes only, which every decoder must read the same way and which keep the decoder's code table in a
/// state the test controls (a full table, a cut-off stream, an undefined code).
/// </summary>
public sealed class GifDecisionTests
{
    /// <summary>Writes a GIF block by block.</summary>
    internal sealed class GifStream
    {
        private readonly MemoryStream _out = new();

        public GifStream(int width, int height, byte[]? globalPalette = null)
        {
            _out.Write("GIF89a"u8);
            U16(width);
            U16(height);
            _out.WriteByte(globalPalette is null ? (byte)0 : (byte)(0x80 | TableBits(globalPalette)));
            _out.WriteByte(0); // background colour index
            _out.WriteByte(0); // pixel aspect ratio
            if (globalPalette is not null) _out.Write(globalPalette);
        }

        public GifStream Control(int disposal = 1, int delay = 0, int? transparent = null)
        {
            _out.Write([0x21, 0xF9, 4, (byte)((disposal << 2) | (transparent is null ? 0 : 1))]);
            U16(delay);
            _out.Write([(byte)(transparent ?? 0), 0]);
            return this;
        }

        public GifStream Loop(int count)
        {
            _out.Write([0x21, 0xFF, 11, .. "NETSCAPE2.0"u8, 3, 1]);
            U16(count);
            _out.WriteByte(0);
            return this;
        }

        public GifStream PlainText()
        {
            _out.Write([0x21, 0x01, 12, .. new byte[12], 3, (byte)'a', (byte)'b', (byte)'c', 0]);
            return this;
        }

        public GifStream Image(int x, int y, int width, int height, byte[] indices, int minCodeSize = 8,
            byte[]? localPalette = null, bool interlaced = false, bool clearWhenFull = true, byte[]? codes = null)
        {
            _out.WriteByte(0x2C);
            U16(x);
            U16(y);
            U16(width);
            U16(height);
            _out.WriteByte((byte)((localPalette is null ? 0 : 0x80 | TableBits(localPalette)) | (interlaced ? 0x40 : 0)));
            if (localPalette is not null) _out.Write(localPalette);
            _out.WriteByte((byte)minCodeSize);
            SubBlocks(codes ?? Literals(indices, minCodeSize, clearWhenFull));
            return this;
        }

        public GifStream Raw(params byte[] bytes)
        {
            _out.Write(bytes);
            return this;
        }

        public byte[] Trailer()
        {
            _out.WriteByte(0x3B);
            return _out.ToArray();
        }

        public byte[] NoTrailer() => _out.ToArray();

        private void SubBlocks(byte[] data)
        {
            for (var pos = 0; pos < data.Length; pos += 255)
            {
                var n = Math.Min(255, data.Length - pos);
                _out.WriteByte((byte)n);
                _out.Write(data, pos, n);
            }
            _out.WriteByte(0);
        }

        private void U16(int v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v);
            _out.Write(b);
        }

        private static int TableBits(byte[] palette)
        {
            var entries = palette.Length / 3;
            (entries is >= 2 and <= 256 && (entries & (entries - 1)) == 0).ShouldBeTrue("a colour table has 2^n entries");
            return System.Numerics.BitOperations.Log2((uint)entries) - 1;
        }
    }

    /// <summary>
    /// A code stream of one code per pixel: a clear code, then each index at the width the decoder expects,
    /// tracking its table as it grows (every code after the first adds an entry). With
    /// <paramref name="clearWhenFull"/> it clears when the table reaches 4096 codes, as encoders do; without,
    /// it carries on with 12-bit codes into a full table. Ends with the end-of-information code.
    /// </summary>
    internal static byte[] Literals(ReadOnlySpan<byte> indices, int minCodeSize, bool clearWhenFull = true, bool end = true)
    {
        var bits = new BitWriter();
        int clear = 1 << minCodeSize, size = minCodeSize + 1, next = clear + 2;
        var first = true;
        bits.Write(clear, size);
        foreach (var index in indices)
        {
            index.ShouldBeLessThan((byte)Math.Min(255, clear), "a literal index fits the code size");
            bits.Write(index, size);
            if (first)
            {
                first = false;
                continue;
            }
            if (next < 4096)
            {
                next++;
                if (next == 1 << size && size < 12) size++;
            }
            if (next == 4096 && clearWhenFull)
            {
                bits.Write(clear, size);
                (size, next, first) = (minCodeSize + 1, clear + 2, true);
            }
        }
        if (end) bits.Write(clear + 1, size);
        return bits.ToArray();
    }

    internal sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private ulong _acc;
        private int _count;

        public void Write(int code, int size)
        {
            _acc |= (ulong)code << _count;
            _count += size;
            while (_count >= 8)
            {
                _bytes.Add((byte)_acc);
                _acc >>= 8;
                _count -= 8;
            }
        }

        public byte[] ToArray() => _count > 0 ? [.. _bytes, (byte)_acc] : [.. _bytes];
    }

    // A palette of `count` distinct, recognisable colours: entry i is (i, 255 - i, i * 7).
    internal static byte[] Palette(int count)
    {
        var p = new byte[count * 3];
        for (var i = 0; i < count; i++)
            (p[3 * i], p[3 * i + 1], p[3 * i + 2]) = ((byte)i, (byte)(255 - i), (byte)(i * 7));
        return p;
    }

    private static uint Rgba(int index) => (uint)(index | ((255 - index) << 8) | ((byte)(index * 7) << 16) | (255 << 24));

    private const uint OpaqueBlack = 0xFF000000;

    // Every frame of a GIF, as RGBA pixels read as little-endian uints (R in the low byte).
    private static uint[][] Frames(byte[] gif)
    {
        SimdCodecs.TryOpenFrames(gif, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.TryGetFrameInfo(0, out var info).ShouldBeTrue();
            var frames = new uint[reader.FrameCount][];
            for (var i = 0; i < frames.Length; i++)
            {
                var rgba = new byte[info.Info.Width * info.Info.Height * 4];
                reader.TryDecodeFrameInto(i, rgba, 0, ImagePixelFormat.Rgba32).ShouldBeTrue($"frame {i}");
                frames[i] = [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(rgba)];
            }
            return frames;
        }
    }

    private static byte[] Indices(int count, int modulo, int offset = 0) =>
        [.. Enumerable.Range(0, count).Select(i => (byte)((i + offset) % modulo))];

    [Fact]
    public void SingleLiteralCodesDecodeToTheirPixels()
    {
        // The helper itself: a still of every index decodes to the palette, at code sizes 2..8.
        foreach (var bits in new[] { 2, 3, 5, 8 })
        {
            var colours = 1 << bits;
            var indices = Indices(13 * 7, colours);
            var gif = new GifStream(13, 7, Palette(colours)).Image(0, 0, 13, 7, indices, minCodeSize: bits).Trailer();
            Frames(gif).Single().ShouldBe(indices.Select(i => Rgba(i)).ToArray(), $"min code size {bits}");
        }
    }

    [Fact]
    public void ACanvasStartsAndIsClearedToTransparentBlack()
    {
        var gif = new GifStream(4, 2, Palette(4))
            .Control(disposal: 2).Image(1, 0, 2, 1, [1, 2], minCodeSize: 2)
            .Control().Image(0, 1, 1, 1, [3], minCodeSize: 2)
            .Trailer();
        var frames = Frames(gif);
        frames[0].ShouldBe([0, Rgba(1), Rgba(2), 0, 0, 0, 0, 0]);
        // The first image disposed to background: cleared to transparent, not to the background index 0.
        frames[1].ShouldBe([0, 0, 0, 0, Rgba(3), 0, 0, 0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    public void UndefinedDisposalsKeepTheFrame(int disposal)
    {
        var gif = new GifStream(2, 1, Palette(4))
            .Control(disposal: disposal).Image(0, 0, 1, 1, [1], minCodeSize: 2)
            .Control().Image(1, 0, 1, 1, [2], minCodeSize: 2)
            .Trailer();
        Frames(gif)[1].ShouldBe([Rgba(1), Rgba(2)]);
    }

    [Fact]
    public void RestoringToPreviousUndoesOnlyThatFrame()
    {
        var gif = new GifStream(3, 1, Palette(4))
            .Control().Image(0, 0, 3, 1, [1, 1, 1], minCodeSize: 2)
            .Control(disposal: 3).Image(1, 0, 2, 1, [2, 2], minCodeSize: 2)
            .Control().Image(2, 0, 1, 1, [3], minCodeSize: 2)
            .Trailer();
        var frames = Frames(gif);
        frames[1].ShouldBe([Rgba(1), Rgba(2), Rgba(2)]);
        frames[2].ShouldBe([Rgba(1), Rgba(1), Rgba(3)]);
    }

    [Fact]
    public void ImagesPastTheCanvasAreClipped()
    {
        var gif = new GifStream(3, 2, Palette(4))
            .Control().Image(2, 1, 3, 2, [1, 2, 3, 3, 2, 1], minCodeSize: 2)  // one pixel on the canvas
            .Control().Image(5, 0, 2, 1, [3, 3], minCodeSize: 2)              // none
            .Control().Image(0, 9, 1, 1, [3], minCodeSize: 2)                 // none
            .Trailer();
        var frames = Frames(gif);
        frames[0].ShouldBe([0, 0, 0, 0, 0, Rgba(1)]);
        frames[2].ShouldBe(frames[0]);
    }

    [Fact]
    public void AnEmptyLogicalScreenIsTheImagesBoundingBox()
    {
        var gif = new GifStream(0, 0, Palette(4))
            .Image(0, 0, 2, 1, [1, 2], minCodeSize: 2)
            .Image(1, 1, 3, 1, [3, 3, 3], minCodeSize: 2)
            .Trailer();
        GifDecoder.ReadInfo(gif).ShouldBe(new GifInfo(4, 2, 2, true, null));
    }

    [Fact]
    public void MissingColourTablesAndIndicesPastTheTableAreOpaqueBlack()
    {
        // No colour table at all.
        var none = new GifStream(2, 1).Image(0, 0, 2, 1, [0, 1], minCodeSize: 2).Trailer();
        Frames(none).Single().ShouldBe([OpaqueBlack, OpaqueBlack]);

        // A 4-entry table, indices up to 7 (code size 3).
        var past = new GifStream(4, 1, Palette(4)).Image(0, 0, 4, 1, [2, 3, 4, 7], minCodeSize: 3).Trailer();
        Frames(past).Single().ShouldBe([Rgba(2), Rgba(3), OpaqueBlack, OpaqueBlack]);

        // A local table replaces the global one for its image only.
        var local = new GifStream(2, 1, Palette(4))
            .Image(0, 0, 1, 1, [1], minCodeSize: 2, localPalette: [9, 9, 9, 8, 8, 8])
            .Image(1, 0, 1, 1, [1], minCodeSize: 2)
            .Trailer();
        Frames(local)[1].ShouldBe([0xFF080808u, Rgba(1)]);
    }

    [Fact]
    public void TheTransparentIndexLeavesTheCanvas()
    {
        var gif = new GifStream(3, 1, Palette(4))
            .Control().Image(0, 0, 3, 1, [1, 2, 3], minCodeSize: 2)
            .Control(transparent: 2).Image(0, 0, 3, 1, [2, 2, 0], minCodeSize: 2)
            .Trailer();
        Frames(gif)[1].ShouldBe([Rgba(1), Rgba(2), Rgba(0)]);
    }

    [Fact]
    public void CutOffImageDataIsDrawnAsFarAsItGoes()
    {
        var indices = Indices(5 * 4, 4, offset: 1);
        // A code stream that stops after 6 pixels, without an end code, then the file ends. Six codes after
        // the clear (3 bits for clear and the first three, then 4) end on a byte boundary, so no padding
        // bits are left to read as one more code.
        var codes = Literals(indices.AsSpan(0, 6), 2, end: false);
        codes.Length.ShouldBe(3);
        var gif = new GifStream(5, 4, Palette(4)).Control(transparent: 3).Image(0, 0, 5, 4, indices, minCodeSize: 2, codes: codes).NoTrailer();
        var frame = Frames(gif).Single();
        for (var i = 0; i < 20; i++)
            frame[i].ShouldBe(i < 6 && indices[i] != 3 ? Rgba(indices[i]) : 0u, $"pixel {i}");
    }

    [Fact]
    public void AnOpaqueGifCutShortIsBlackWhereTheDataStops()
    {
        var indices = Indices(4 * 2, 4, offset: 1);
        var codes = Literals(indices.AsSpan(0, 4), 2, end: false); // clear + 4 codes: 16 bits exactly
        codes.Length.ShouldBe(2);
        var gif = new GifStream(4, 2, Palette(4)).Image(0, 0, 4, 2, indices, minCodeSize: 2, codes: codes).NoTrailer();
        SimdCodecs.TryReadInfo(gif, out var info).ShouldBeTrue();
        info.Channels.ShouldBe(3);
        Frames(gif).Single().ShouldBe([Rgba(1), Rgba(2), Rgba(3), Rgba(0), OpaqueBlack, OpaqueBlack, OpaqueBlack, OpaqueBlack]);
    }

    [Fact]
    public void AnUndefinedCodeEndsTheImage()
    {
        // Clear, then 1, 2, 3 at 3 bits: entries 6 and 7 are defined and codes grow to 4 bits, so the next
        // free code is 8 (the KwKwK case, legal) and 9, the first past it, is not defined. (At 3 bits no
        // code can be undefined: 7 is always the next one.)
        var bits = new BitWriter();
        foreach (var code in new[] { 4, 1, 2, 3 }) bits.Write(code, 3);
        foreach (var code in new[] { 9, 1, 2 }) bits.Write(code, 4);
        var gif = new GifStream(5, 1, Palette(4)).Image(0, 0, 5, 1, [], minCodeSize: 2, codes: bits.ToArray()).Trailer();
        // The GIF is opaque (one image over the canvas, no transparent index), so what is left is opaque black.
        Frames(gif).Single().ShouldBe([Rgba(1), Rgba(2), Rgba(3), OpaqueBlack, OpaqueBlack]);
    }

    [Fact]
    public void AFullTableWithoutAClearKeepsDecoding()
    {
        // 6000 pixels at code size 8: the table fills after 3839 codes, and the rest are 12-bit codes into a
        // frozen table.
        var indices = Indices(100 * 60, 251, offset: 3);
        var gif = new GifStream(100, 60, Palette(256)).Image(0, 0, 100, 60, indices, clearWhenFull: false).Trailer();
        Frames(gif).Single().ShouldBe(indices.Select(i => Rgba(i)).ToArray());
    }

    [Fact]
    public void TheFileMayEndAnywhereAfterItsFirstImage()
    {
        var gif = new GifStream(2, 1, Palette(4))
            .Control(delay: 5).Image(0, 0, 2, 1, [1, 2], minCodeSize: 2)
            .Control(delay: 6).Image(0, 0, 2, 1, [3, 3], minCodeSize: 2)
            .NoTrailer();
        GifDecoder.ReadInfo(gif).FrameCount.ShouldBe(2);
        // An unknown block (0x99) ends the file as well: the image after it is not read.
        var unknown = new GifStream(2, 1, Palette(4))
            .Image(0, 0, 2, 1, [1, 2], minCodeSize: 2).Raw(0x99, 1, 2, 3)
            .Image(0, 0, 2, 1, [3, 3], minCodeSize: 2).Trailer();
        GifDecoder.ReadInfo(unknown).FrameCount.ShouldBe(1);
        // Without an image the file is refused.
        SimdCodecs.TryReadInfo(new GifStream(2, 1, Palette(4)).Trailer(), out _).ShouldBeFalse();
    }

    [Fact]
    public void DelaysAreAsStoredAndAPlainTextBlockTakesItsControl()
    {
        var gif = new GifStream(1, 1, Palette(4))
            .Control(delay: 0).Image(0, 0, 1, 1, [1], minCodeSize: 2)
            .Control(delay: 77, transparent: 1).PlainText()          // the plain text consumes this control
            .Image(0, 0, 1, 1, [1], minCodeSize: 2)                  // so this image has none
            .Trailer();
        SimdCodecs.TryOpenFrames(gif, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.FrameCount.ShouldBe(2);
            reader.LoopCount.ShouldBe(1); // no NETSCAPE2.0: one play
            reader.TryGetFrameInfo(0, out var first).ShouldBeTrue();
            first.Duration.ShouldBe(TimeSpan.Zero);
            reader.TryGetFrameInfo(1, out var second).ShouldBeTrue();
            second.Duration.ShouldBe(TimeSpan.Zero);
        }
        Frames(gif)[1].ShouldBe([Rgba(1)]); // index 1 drawn: no transparent index applies
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(5, 6)]
    public void LoopCountsArePlays(int stored, int plays)
    {
        var gif = new GifStream(1, 1, Palette(4)).Loop(stored)
            .Image(0, 0, 1, 1, [1], minCodeSize: 2).Image(0, 0, 1, 1, [2], minCodeSize: 2).Trailer();
        GifDecoder.ReadInfo(gif).LoopCount.ShouldBe(stored);
        SimdCodecs.TryOpenFrames(gif, out var reader).ShouldBeTrue();
        using (reader)
            reader.LoopCount.ShouldBe(plays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    public void AMinimumCodeSizeNoTableHoldsIsRefused(int minCodeSize)
    {
        var gif = new GifStream(1, 1, Palette(4)).Image(0, 0, 1, 1, [], minCodeSize: minCodeSize, codes: [0, 0]).Trailer();
        SimdCodecs.TryReadInfo(gif, out _).ShouldBeTrue(); // the structure is fine
        SimdCodecs.TryDecode(gif, out _).ShouldBeFalse();
        SimdCodecs.TryDecodeInto(gif, new byte[16], 0, ImagePixelFormat.Rgba32).ShouldBeFalse();
    }

    [Fact]
    public void CanvasesAndImagesPastTheLimitAreRefused()
    {
        SimdCodecs.TryReadInfo(new GifStream(8193, 8193, Palette(4)).Image(0, 0, 1, 1, [1], minCodeSize: 2).Trailer(), out _).ShouldBeFalse();
        SimdCodecs.TryReadInfo(new GifStream(4, 4, Palette(4)).Image(0, 0, 65535, 65535, [1], minCodeSize: 2).Trailer(), out _).ShouldBeFalse();
        SimdCodecs.TryReadInfo(new GifStream(8192, 8192, Palette(4)).Image(0, 0, 1, 1, [1], minCodeSize: 2).Trailer(), out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(17)]
    public void InterlacedRowsAreEveryRowOnce(int height)
    {
        Enumerable.Range(0, height).Select(r => GifCanvas.InterlacedRow(r, height)).Order().ShouldBe(Enumerable.Range(0, height));
        // And decode to the right places: row r of the stored image is canvas row InterlacedRow(r).
        var indices = new byte[height];
        for (var r = 0; r < height; r++) indices[r] = (byte)(r % 4);
        var gif = new GifStream(1, height, Palette(4)).Image(0, 0, 1, height, indices, minCodeSize: 2, interlaced: true).Trailer();
        var frame = Frames(gif).Single();
        for (var r = 0; r < height; r++)
            frame[GifCanvas.InterlacedRow(r, height)].ShouldBe(Rgba(indices[r]));
    }

    [Fact]
    public void MangledFilesNeverThrow()
    {
        var sources = new[] { GifDecoderTests.Animation("mixed"), GifDecoderTests.Animation("offsets") };
        var random = new Random(89);
        foreach (var source in sources)
        {
            for (var cut = 0; cut <= source.Length; cut += Math.Max(1, source.Length / 200))
                Exercise(source[..cut]);
            for (var trial = 0; trial < 400; trial++)
            {
                var mangled = (byte[])source.Clone();
                for (var flips = random.Next(1, 6); flips > 0; flips--)
                    mangled[random.Next(mangled.Length)] = (byte)random.Next(256);
                Exercise(mangled);
            }
        }

        static void Exercise(byte[] data)
        {
            SimdCodecs.TryReadInfo(data, out _);
            SimdCodecs.TryDecode(data, out _);
            if (!SimdCodecs.TryOpenFrames(data, out var reader)) return;
            using (reader)
            {
                reader.TryGetFrameInfo(0, out var info).ShouldBeTrue();
                var buffer = new byte[info.Info.Width * info.Info.Height * 4];
                for (var i = reader.FrameCount - 1; i >= 0; i--)
                    reader.TryDecodeFrameInto(i, buffer, 0, ImagePixelFormat.Rgba32);
            }
        }
    }
}
