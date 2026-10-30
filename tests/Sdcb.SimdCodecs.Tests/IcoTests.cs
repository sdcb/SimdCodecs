using System.Buffers.Binary;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Ico;
using Shouldly;
using W = Sdcb.SimdCodecs.Tests.TestBmpWriter;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Icons and cursors: hand-assembled directories (<see cref="Directory"/>) of DIB and PNG images decode to the
/// test's model of them; every encoder layout round-trips; ImageMagick's icons decode to its own reading; the
/// directory's edge cases, the facade and frame reader, truncation and mutation.
/// </summary>
public sealed class IcoTests
{
    /// <summary>An <c>ICONDIR</c> with one <c>ICONDIRENTRY</c> per payload, as Microsoft documents the icon file.</summary>
    internal static byte[] Directory(int type, params (byte[] Payload, int Width, int Height, int A, int B)[] images)
    {
        var header = new byte[6 + 16 * images.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), (ushort)type);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)images.Length);
        var offset = header.Length;
        for (var i = 0; i < images.Length; i++)
        {
            var e = header.AsSpan(6 + 16 * i);
            e[0] = (byte)images[i].Width;
            e[1] = (byte)images[i].Height;
            BinaryPrimitives.WriteUInt16LittleEndian(e[4..], (ushort)images[i].A); // planes, or hotspot x
            BinaryPrimitives.WriteUInt16LittleEndian(e[6..], (ushort)images[i].B); // bit count, or hotspot y
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)images[i].Payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)offset);
            offset += images[i].Payload.Length;
        }
        return W.Concat([header, .. images.Select(i => i.Payload)]);
    }

    private static byte[] RandomRgba(int width, int height, int seed, bool binaryAlpha = false)
    {
        var rgba = new byte[width * height * 4];
        new Random(seed).NextBytes(rgba);
        if (binaryAlpha)
        {
            for (var i = 3; i < rgba.Length; i += 4) rgba[i] = rgba[i] < 128 ? (byte)0 : (byte)255;
        }
        return rgba;
    }

    // ---- Hand-built directories --------------------------------------------------------------------------------

    [Fact]
    public void Dib_and_png_images_decode_and_the_largest_deepest_is_the_default()
    {
        var small = RandomRgba(16, 16, 1);
        var dib8 = W.Concat(W.InfoHeader(40, 32, 64, 8, 0, colorsUsed: 2), W.Palette([0x102030, 0x405060]),
            W.Rows(32, 32, 8, (x, y) => (uint)((x + y) & 1)), W.Rows(32, 32, 1, (x, _) => x < 4 ? 1u : 0u));
        var dib32 = W.Concat(W.InfoHeader(40, 32, 64, 32, 0), W.Rows(32, 32, 32, (x, y) => (uint)(x * 8 << 24 | y)), new byte[4 * 32]);
        var png = Png.PngWriter.Encode(small, 16, 16);
        var ico = Directory(1, (dib8, 32, 32, 1, 8), (png, 16, 16, 1, 32), (dib32, 32, 32, 1, 32));

        var info = IcoDecoder.ReadInfo(ico);
        info.Kind.ShouldBe(IcoKind.Icon);
        info.Entries.Select(e => (e.Width, e.Height, e.BitsPerPixel, e.IsPng)).ShouldBe([(32, 32, 8, false), (16, 16, 32, true), (32, 32, 32, false)]);
        info.Best.ShouldBe(2); // 32x32 twice; 32 bits beats 8

        IcoDecoder.Decode(ico, 1).Rgba.ShouldBe(small);
        BmpDecoderTests.ShouldMatch(IcoDecoder.Decode(ico, 0).Rgba, BmpDecoderTests.Image(32, 32, (x, y) =>
        {
            var c = ((x + y) & 1) == 0 ? 0x102030 : 0x405060;
            return (c >> 16, c >> 8 & 255, c & 255, x < 4 ? 0 : 255);
        }), "8-bit DIB with its AND mask");
        BmpDecoderTests.ShouldMatch(IcoDecoder.Decode(ico).Rgba, BmpDecoderTests.Image(32, 32, (x, y) => (0, 0, y, x * 8)), "32-bit DIB, alpha from the pixels");
    }

    [Fact]
    public void A_cursor_carries_its_hotspots_and_reports_its_own_format()
    {
        var dib = W.Concat(W.InfoHeader(40, 16, 32, 1, 0), W.Palette([0, 0xFFFFFF]),
            W.Rows(16, 16, 1, (x, y) => x == y ? 1u : 0u), W.Rows(16, 16, 1, (x, y) => x == y || x == y + 1 ? 0u : 1u));
        var cur = Directory(2, (dib, 16, 16, 5, 7));
        var info = IcoDecoder.ReadInfo(cur);
        info.Kind.ShouldBe(IcoKind.Cursor);
        (info.Entries[0].HotspotX, info.Entries[0].HotspotY).ShouldBe((5, 7));

        SimdCodecs.DetectFormat(cur).ShouldBe(ImageFormat.Cur);
        SimdCodecs.TryReadInfo(cur, out var image).ShouldBeTrue();
        (image.Format, image.Width, image.Channels).ShouldBe((ImageFormat.Cur, 16, 4));
        IcoImageDecoder.CanDecode(cur).ShouldBeFalse();
        CurImageDecoder.TryReadInfo(Directory(1, (dib, 16, 16, 1, 1)), out _).ShouldBeFalse();
    }

    [Fact]
    public void Each_image_says_its_own_size_whatever_the_directory_byte_says()
    {
        var rgba = RandomRgba(256, 256, 3);
        var png = Png.PngWriter.Encode(rgba, 256, 256);
        var dib = W.Concat(W.InfoHeader(40, 20, 40, 24, 0), W.Rows(20, 20, 24, (x, y) => (uint)(x * 1000 + y)), new byte[4 * 20]);
        // 0 means 256; the second entry claims 48x48 for a 20x20 image.
        var ico = Directory(1, (png, 0, 0, 1, 32), (dib, 48, 48, 1, 24));
        var info = IcoDecoder.ReadInfo(ico);
        (info.Entries[0].Width, info.Entries[0].Height).ShouldBe((256, 256));
        (info.Entries[1].Width, info.Entries[1].Height).ShouldBe((20, 20));
        IcoDecoder.Decode(ico).Rgba.ShouldBe(rgba);
    }

    [Fact]
    public void An_image_size_past_the_end_of_the_file_is_cut_to_it()
    {
        var dib = W.Concat(W.InfoHeader(40, 8, 16, 32, 0), W.Rows(8, 8, 32, (x, y) => 0xFF000000u | (uint)(x << 8 | y)), new byte[4 * 8]);
        var ico = Directory(1, (dib, 8, 8, 1, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(6 + 8), 100_000);
        IcoDecoder.Decode(ico).Width.ShouldBe(8);
    }

    public static TheoryData<string, byte[]> CorruptDirectories()
    {
        var dib = W.Concat(W.InfoHeader(40, 4, 8, 32, 0), new byte[4 * 4 * 4 + 4 * 4]);
        var good = Directory(1, (dib, 4, 4, 1, 32));
        byte[] With(int at, params byte[] bytes)
        {
            var copy = good.ToArray();
            bytes.CopyTo(copy, at);
            return copy;
        }
        return new()
        {
            { "reserved not 0", With(0, 1) },
            { "type 3", With(2, 3) },
            { "no images", With(4, 0) },
            { "count past the directory", With(4, 9) },
            { "image inside the directory", With(18, 6, 0, 0, 0) },
            { "image past the end", With(18, 0, 1, 0, 0) },
            { "compressed DIB", Directory(1, (W.Concat(W.InfoHeader(40, 4, 8, 8, 1), new byte[1024 + 8]), 4, 4, 1, 8)) },
            { "PNG that does not read", Directory(1, ([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0], 4, 4, 1, 32)) },
            { "truncated", good[..10] },
        };
    }

    [Theory]
    [MemberData(nameof(CorruptDirectories))]
    public void Corrupt_directories_are_refused(string name, byte[] ico)
    {
        Should.Throw<InvalidDataException>(() => IcoDecoder.Decode(ico), name);
        IcoImageDecoder.TryDecode(ico, out _).ShouldBeFalse(name);
        IcoImageDecoder.TryOpenFrames(ico, out _).ShouldBeFalse(name);
    }

    // ---- Encoder -----------------------------------------------------------------------------------------------

    public static TheoryData<IcoImageEncoding, int> Encodings()
    {
        var data = new TheoryData<IcoImageEncoding, int>();
        foreach (var encoding in Enum.GetValues<IcoImageEncoding>())
        foreach (var size in new[] { 1, 16, 17, 33, 48, 256 })
            data.Add(encoding, size);
        return data;
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void Every_encoding_round_trips(IcoImageEncoding encoding, int size)
    {
        // Few enough colours for the palette encodings to be exact; alpha all or nothing for the masked ones.
        var colours = encoding switch { IcoImageEncoding.Bmp1 => 2, IcoImageEncoding.Bmp4 => 15, _ => 200 };
        var masked = encoding is IcoImageEncoding.Bmp24 or IcoImageEncoding.Bmp8 or IcoImageEncoding.Bmp4 or IcoImageEncoding.Bmp1;
        var random = new Random(size + (int)encoding * 1000);
        var palette = Enumerable.Range(0, colours).Select(_ => random.Next(1 << 24)).ToArray();
        // Masked images write transparent pixels black, so black is one of the colours when any are transparent.
        palette[0] = masked ? 0 : palette[0];
        var rgba = new byte[size * size * 4];
        for (var p = 0; p < size * size; p++)
        {
            var transparent = random.Next(4) == 0;
            var c = transparent && masked ? 0 : palette[random.Next(colours)];
            var a = masked ? (transparent ? 0 : 255) : random.Next(256);
            (rgba[4 * p], rgba[4 * p + 1], rgba[4 * p + 2], rgba[4 * p + 3]) = ((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)a);
        }
        var ico = IcoEncoder.Encode([new IcoFrame(rgba, size, size, Encoding: encoding)]);
        var entry = IcoDecoder.ReadInfo(ico).Entries[0];
        var png = encoding == IcoImageEncoding.Png || (encoding == IcoImageEncoding.Auto && size == 256);
        entry.IsPng.ShouldBe(png);
        BmpDecoderTests.ShouldMatch(IcoDecoder.Decode(ico).Rgba, rgba, $"{encoding}, {size}x{size}");
        BmpDecoderTests.ShouldMatch(Magick(ico)[0], rgba, $"{encoding}, {size}x{size}, read by ImageMagick");
    }

    [Fact]
    public void A_multi_size_icon_and_cursor_keep_their_order_sizes_and_hotspots()
    {
        IcoFrame[] frames =
        [
            new(RandomRgba(16, 16, 1), 16, 16, HotspotX: 1, HotspotY: 2),
            new(RandomRgba(48, 32, 2, true), 48, 32, Encoding: IcoImageEncoding.Bmp8, HotspotX: 47, HotspotY: 31),
            new(RandomRgba(256, 256, 3), 256, 256, HotspotX: 128, HotspotY: 0),
        ];
        foreach (var cursor in new[] { false, true })
        {
            var data = cursor ? IcoEncoder.EncodeCursor(frames) : IcoEncoder.Encode(frames);
            var info = IcoDecoder.ReadInfo(data);
            info.Entries.Select(e => (e.Width, e.Height)).ShouldBe([(16, 16), (48, 32), (256, 256)]);
            info.Best.ShouldBe(2);
            if (cursor) info.Entries.Select(e => (e.HotspotX, e.HotspotY)).ShouldBe([(1, 2), (47, 31), (128, 0)]);

            SimdCodecs.TryOpenFrames(data, out var reader).ShouldBeTrue();
            using (reader)
            {
                reader.FrameCount.ShouldBe(3);
                reader.LoopCount.ShouldBeNull();
                for (var i = 0; i < 3; i++)
                {
                    reader.TryGetFrameInfo(i, out var frame).ShouldBeTrue();
                    (frame.Info.Width, frame.Info.Height, frame.Info.Format, frame.Duration).ShouldBe(
                        (frames[i].Width, frames[i].Height, cursor ? ImageFormat.Cur : ImageFormat.Ico, (TimeSpan?)null));
                    var bgra = new byte[frames[i].Width * frames[i].Height * 4 + 7];
                    reader.TryDecodeFrameInto(i, bgra, 0, ImagePixelFormat.Bgra32).ShouldBeTrue();
                    reader.TryDecodeFrame(i, out var decoded).ShouldBeTrue();
                    var expected = new byte[bgra.Length];
                    PixelConverter.TryConvert(decoded!, expected, 0, ImagePixelFormat.Bgra32).ShouldBeTrue();
                    bgra.ShouldBe(expected, $"frame {i}");
                }
            }
        }
    }

    [Fact]
    public void Encoder_refuses_what_an_icon_cannot_hold()
    {
        var rgba = new byte[300 * 300 * 4];
        Should.Throw<ArgumentException>(() => IcoEncoder.Encode([]));
        Should.Throw<ArgumentOutOfRangeException>(() => IcoEncoder.Encode(rgba, 300, 300));
        Should.Throw<ArgumentOutOfRangeException>(() => IcoEncoder.EncodeCursor([new IcoFrame(rgba, 16, 16, HotspotX: 16)]));
        Should.Throw<ArgumentException>(() => IcoEncoder.Encode(rgba.AsSpan(0, 10), 16, 16));
    }

    // ---- ImageMagick -------------------------------------------------------------------------------------------

    internal static List<byte[]> Magick(byte[] ico)
    {
        using var frames = new MagickImageCollection(ico, MagickFormat.Ico);
        return [.. frames.Select(f =>
        {
            using var pixels = f.GetPixels();
            return pixels.ToShortArray(PixelMapping.RGBA)!.Select(v => (byte)(v >> 8)).ToArray();
        })];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Icons_imagemagick_writes_decode_to_its_own_reading(bool png)
    {
        using var image = BmpMagickTests.Source(256, 256, 1000, alpha: true, seed: png ? 1 : 2);
        image.Settings.SetDefine(MagickFormat.Icon, "auto-resize", "256,48,32,16");
        if (!png) image.Settings.SetDefine(MagickFormat.Icon, "format", "bmp");
        var ico = image.ToByteArray(MagickFormat.Ico);
        var info = IcoDecoder.ReadInfo(ico);
        info.Entries.Count.ShouldBe(4);
        var theirs = Magick(ico);
        for (var i = 0; i < info.Entries.Count; i++)
        {
            var because = $"{(png ? "PNG" : "BMP")} icon, image {i}: {info.Entries[i]}";
            var ours = IcoDecoder.Decode(ico, i).Rgba;
            // ImageMagick leaves the colour of fully transparent pixels to whatever it wrote; compare alpha alone there.
            for (var p = 0; p < ours.Length; p += 4)
            {
                if (ours[p + 3] == 0 && theirs[i][p + 3] == 0) continue;
                if (!ours.AsSpan(p, 4).SequenceEqual(theirs[i].AsSpan(p, 4)))
                    throw new ShouldAssertException($"{because}: pixel {p / 4} is {Convert.ToHexString(ours, p, 4)}, ImageMagick has {Convert.ToHexString(theirs[i], p, 4)}");
            }
        }
    }

    // ---- Robustness --------------------------------------------------------------------------------------------

    [Fact]
    public void Truncations_and_mutations_decode_or_throw_invalid_data()
    {
        var seeds = new[]
        {
            IcoEncoder.Encode([new IcoFrame(RandomRgba(16, 16, 1), 16, 16), new IcoFrame(RandomRgba(20, 20, 2), 20, 20, Encoding: IcoImageEncoding.Png),
                new IcoFrame(RandomRgba(9, 9, 3), 9, 9, Encoding: IcoImageEncoding.Bmp4)]),
            IcoEncoder.EncodeCursor([new IcoFrame(RandomRgba(32, 32, 4, true), 32, 32, Encoding: IcoImageEncoding.Bmp1, HotspotX: 3)]),
        };
        var random = new Random(0x1c0);
        foreach (var seed in seeds)
        {
            for (var length = 0; length < seed.Length; length++) DecodeOne(seed[..length], $"cut to {length}");
            for (var i = 0; i < 3000; i++)
            {
                var data = seed.ToArray();
                var at = random.Next(random.Next(2) == 0 ? Math.Min(128, data.Length) : data.Length);
                data[at] = random.Next(3) switch { 0 => (byte)random.Next(256), 1 => 0xFF, _ => (byte)(data[at] ^ (1 << random.Next(8))) };
                DecodeOne(data, $"byte {at} = {data[at]}");
            }
        }
    }

    private static void DecodeOne(byte[] data, string context)
    {
        try
        {
            if (!IcoDecoder.TryReadInfo(data, out var info)) return;
            for (var i = 0; i < info.Entries.Count; i++)
            {
                if ((long)info.Entries[i].Width * info.Entries[i].Height > 1 << 22) continue;
                IcoDecoder.Decode(data, i);
            }
            SimdCodecs.TryDecode(data, out _);
            if (SimdCodecs.TryOpenFrames(data, out var reader))
            {
                using (reader)
                {
                    for (var i = 0; i < reader.FrameCount; i++)
                    {
                        reader.TryGetFrameInfo(i, out var frame);
                        if ((long)frame.Info.Width * frame.Info.Height <= 1 << 22) reader.TryDecodeFrame(i, out _);
                    }
                }
            }
        }
        catch (InvalidDataException)
        {
        }
        catch (Exception e)
        {
            throw new ShouldAssertException($"{context}: {e.GetType().Name}: {e.Message}", e);
        }
    }
}
