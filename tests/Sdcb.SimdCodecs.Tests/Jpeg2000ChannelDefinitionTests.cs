using System.Buffers.Binary;
using System.Text;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg2000;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The JP2 channel definition box (<c>cdef</c>, T.800 I.5.3.6): which channel is colour (and which
/// colour), which is opacity, and what the decoder and the facade make of that.
/// <para>
/// The real files are OpenJPEG's: <c>opj_compress</c> writes a <c>cdef</c> for an RGBA or grey+alpha PNG,
/// and its lossless codestream decodes to the source exactly (committed as a PAM beside it by
/// <c>make-fixtures.sh</c>), which <c>opj_decompress</c> also writes, so the oracle agrees. Everything
/// <c>opj_compress</c> does not write (a reordered box, premultiplied opacity, a damaged or odd box) is
/// built here around a committed codestream, as in <see cref="Jpeg2000Jp2Tests"/>.
/// </para>
/// </summary>
public class Jpeg2000ChannelDefinitionTests
{
    private static string AlphaDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000-alpha");

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000");

    private static byte[] Alpha(string name) => File.ReadAllBytes(Path.Combine(AlphaDirectory, name + ".jp2"));

    private static Pnm.Image Expected(string name) => Pnm.Read(Path.Combine(AlphaDirectory, name + ".pam"));

    [Theory]
    [InlineData("rgba-struct37x29", 4)]
    [InlineData("graya-struct37x29", 2)]
    public void OpenJpegsAlphaFiles_DecodeExactlyAndAreDescribed(string name, int channels)
    {
        var expected = Expected(name);
        expected.Components.ShouldBe(channels);

        var decoded = Jpeg2000Decoder.Decode(Alpha(name), cancellationToken: TestContext.Current.CancellationToken);

        decoded.Components.ShouldBe(channels);
        decoded.Samples.ShouldBe(expected.Samples);
        decoded.Channels.ShouldNotBeNull();
        decoded.Channels.Select(c => c.Type).ShouldBe(
            [.. Enumerable.Repeat(Jp2ChannelType.Colour, channels - 1), Jp2ChannelType.Opacity]);
        decoded.Channels.Select(c => c.Association).ShouldBe([.. Enumerable.Range(1, channels - 1), 0]);

        var info = Jpeg2000Decoder.ReadInfo(Alpha(name));
        info.Components.ShouldBe(channels);
        info.Channels.ShouldBe(decoded.Channels);
    }

    /// <summary>Through the facade: 4 channels RGBA and 2 grey+alpha, the source samples, straight.</summary>
    [Theory]
    [InlineData("rgba-struct37x29", 4)]
    [InlineData("graya-struct37x29", 2)]
    public void TheFacade_PresentsAlpha(string name, int channels)
    {
        var expected = Expected(name);

        ImageCodecs.TryReadInfo(Alpha(name), out var info).ShouldBeTrue();
        (info.Width, info.Height, info.Channels, info.SampleFormat).ShouldBe((37, 29, channels, SampleFormat.UInt8));

        ImageCodecs.TryDecode(Alpha(name), out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image.Channels.ShouldBe(channels);
        image.Pixels.ToArray().ShouldBe(expected.Samples.Select(s => (byte)s).ToArray());

        // Into RGBA: alpha is the last channel and colour is straight.
        var rgba = new byte[37 * 29 * 4];
        ImageCodecs.TryDecodeInto(Alpha(name), rgba, 0, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        for (var i = 0; i < 37 * 29; i++)
        {
            rgba[i * 4 + 3].ShouldBe((byte)expected.Samples[i * channels + channels - 1]);
            rgba[i * 4].ShouldBe((byte)expected.Samples[i * channels]);
        }
    }

    /// <summary>The live oracle agrees on the same files (PAM out of <c>opj_decompress</c>).</summary>
    [Theory]
    [InlineData("rgba-struct37x29")]
    [InlineData("graya-struct37x29")]
    public void OpenJpegDecodesThemTheSameWay(string name)
    {
        OpenJpegOracle.RequireOrSkip();
        var oracle = OpenJpegOracle.DecodeFile(Alpha(name), ".jp2", ".pnm");

        ImageCodecs.TryDecode(Alpha(name), out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();

        oracle.Components.ShouldBe(image.Channels);
        image.Pixels.ToArray().ShouldBe(oracle.Samples.Select(s => (byte)s).ToArray());
    }

    /// <summary>
    /// A box that lists the colours as blue, green, red (Asoc 3, 2, 1 on channels 0, 1, 2) reorders them:
    /// the decoder hands back red, green, blue, which is also what OpenJPEG writes.
    /// </summary>
    [Fact]
    public void ReorderedColourChannels_ComeBackInTheColourSpacesOrder()
    {
        var wrapped = Wrap("rgb-struct64", Cdef((0, 0, 3), (1, 0, 2), (2, 0, 1)));
        var source = Pnm.Read(Path.Combine(FixtureDirectory, "rgb-struct64.ppm"));

        var decoded = Jpeg2000Decoder.Decode(wrapped, cancellationToken: TestContext.Current.CancellationToken);

        decoded.Components.ShouldBe(3);
        decoded.Samples.ShouldBe(Reverse(source.Samples, 3));
        decoded.Channels!.Select(c => c.Association).ShouldBe([1, 2, 3]);

        ImageCodecs.TryDecode(wrapped, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image.Pixels.ToArray().ShouldBe(Reverse(source.Samples, 3).Select(s => (byte)s).ToArray());

        OpenJpegOracle.RequireOrSkip();
        var oracle = OpenJpegOracle.DecodeFile(wrapped, ".jp2", ".ppm");
        oracle.Samples.ShouldBe(decoded.Samples);
    }

    /// <summary>The channel numbers in the box are those after the palette and component mapping, not codestream components.</summary>
    [Fact]
    public void ChannelNumbers_CountChannelsAfterThePalette()
    {
        // 256 entries x 3 columns: entry i is (i, 255 - i, i / 2).
        var palette = new List<byte> { 1, 0, 3, 7, 7, 7 };
        for (var i = 0; i < 256; i++) palette.AddRange([(byte)i, (byte)(255 - i), (byte)(i / 2)]);
        var codestream = File.ReadAllBytes(Path.Combine(FixtureDirectory, "dwt5-struct64.j2k"));
        var grey = Pnm.Read(Path.Combine(FixtureDirectory, "dwt5-struct64.pgm"));
        byte[] map = [0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 2];

        // Channels 0, 1, 2 are the palette's columns; the box makes channel 2 the first colour.
        var wrapped = Jp2(
            Header(ImageHeader(), EnumeratedColour(16), Box("pclr", [.. palette]), Box("cmap", map),
                Cdef((0, 0, 3), (1, 0, 2), (2, 0, 1))),
            Box("jp2c", codestream));

        var decoded = Jpeg2000Decoder.Decode(wrapped, cancellationToken: TestContext.Current.CancellationToken);

        decoded.Components.ShouldBe(3);
        for (var p = 0; p < grey.Samples.Length; p++)
        {
            int i = grey.Samples[p];
            decoded.Samples[p * 3].ShouldBe((ushort)(i / 2));
            decoded.Samples[p * 3 + 1].ShouldBe((ushort)(255 - i));
            decoded.Samples[p * 3 + 2].ShouldBe((ushort)i);
        }

        Jpeg2000Decoder.ReadInfo(wrapped).Channels!.Select(c => c.Association).ShouldBe([1, 2, 3]);

        OpenJpegOracle.RequireOrSkip();
        OpenJpegOracle.DecodeFile(wrapped, ".jp2", ".ppm").Samples.ShouldBe(decoded.Samples);
    }

    /// <summary>
    /// Premultiplied opacity (Typ 2) is divided out by the facade, in integers:
    /// <c>min(255, (c * 255 + a / 2) / a)</c>, and 0 where a is 0. The decoder reports it as it is coded.
    /// </summary>
    [Fact]
    public void PremultipliedOpacity_IsDividedOutByTheFacadeOnly()
    {
        var jp2 = ReplaceCdef(Alpha("rgba-struct37x29"), Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 2, 0)));
        var source = Expected("rgba-struct37x29");

        var decoded = Jpeg2000Decoder.Decode(jp2, cancellationToken: TestContext.Current.CancellationToken);
        decoded.Samples.ShouldBe(source.Samples);
        decoded.Channels![3].Type.ShouldBe(Jp2ChannelType.PremultipliedOpacity);

        ImageCodecs.TryDecode(jp2, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        var pixels = image.Pixels.ToArray();
        var sawZero = false;
        var sawClamp = false;
        for (var p = 0; p < 37 * 29; p++)
        {
            int a = source.Samples[p * 4 + 3];
            pixels[p * 4 + 3].ShouldBe((byte)a);
            for (var c = 0; c < 3; c++)
            {
                int premultiplied = source.Samples[p * 4 + c];
                var expected = a == 0 ? 0 : Math.Min(255, (premultiplied * 255 + a / 2) / a);
                pixels[p * 4 + c].ShouldBe((byte)expected, $"pixel {p} channel {c}");
                sawZero |= a == 0;
                sawClamp |= a != 0 && premultiplied * 255 + a / 2 >= 255 * a && expected == 255;
            }
        }

        sawZero.ShouldBeTrue("no pixel had alpha 0");
        sawClamp.ShouldBeTrue("no pixel exercised the clamp");
        // Decoding the same file again must not see the first decode's rewritten samples.
        ImageCodecs.TryDecode(jp2, out var again, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        again.Pixels.ToArray().ShouldBe(pixels);
    }

    /// <summary>
    /// What the facade will not present, it refuses with false; the decoder still reads every one.
    /// Opacity of a single colour, a second opacity, an unspecified channel, a fourth colour (CMYK-like) and
    /// sYCC are all pictures that would come out in the wrong colours.
    /// </summary>
    [Theory]
    [InlineData("per-colour opacity")]
    [InlineData("two opacities")]
    [InlineData("unspecified channel")]
    [InlineData("unassociated opacity")]
    public void WhatTheFacadeCannotPresent_IsRefusedAndStillDecoded(string what)
    {
        var entries = what switch
        {
            "per-colour opacity" => new[] { (0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 1, 1) },
            "two opacities" => [(0, 0, 1), (1, 0, 2), (2, 1, 0), (3, 1, 0)],
            "unspecified channel" => [(0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 0xFFFF, 0xFFFF)],
            _ => [(0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 1, 0xFFFF)],
        };
        var jp2 = ReplaceCdef(Alpha("rgba-struct37x29"), Cdef(entries));

        ImageCodecs.TryDecode(jp2, out _, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
        ImageCodecs.TryReadInfo(jp2, out _).ShouldBeFalse();
        Jpeg2000Decoder.Decode(jp2, cancellationToken: TestContext.Current.CancellationToken).Components.ShouldBe(4);
    }

    [Fact]
    public void AnUnspecifiedChannel_IsReportedLast()
    {
        var jp2 = ReplaceCdef(Alpha("rgba-struct37x29"), Cdef((3, 0xFFFF, 0xFFFF), (1, 0, 2), (0, 0, 1), (2, 0, 3)));
        var source = Expected("rgba-struct37x29");

        var decoded = Jpeg2000Decoder.Decode(jp2, cancellationToken: TestContext.Current.CancellationToken);

        decoded.Channels!.Select(c => c.Type).ShouldBe(
            [Jp2ChannelType.Colour, Jp2ChannelType.Colour, Jp2ChannelType.Colour, Jp2ChannelType.Unspecified]);
        decoded.Samples.ShouldBe(source.Samples);
    }

    /// <summary>Four components with no box are still refused (could be CMYK), and so are four colours with one.</summary>
    [Fact]
    public void FourComponents_StayRefusedWithoutAnAlphaDefinition()
    {
        var cmyk = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000-raw", "cmyk16.j2k"));
        ImageCodecs.TryDecode(cmyk, out _, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();

        var fourColours = Jp2(
            Header(ImageHeader(), EnumeratedColour(16), Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 0, 4))),
            Box("jp2c", cmyk));
        ImageCodecs.TryDecode(fourColours, out _, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
        Jpeg2000Decoder.Decode(fourColours, cancellationToken: TestContext.Current.CancellationToken).Components.ShouldBe(4);
    }

    /// <summary>A colour space that does not match the colour channels is refused rather than shown in the wrong colours.</summary>
    [Fact]
    public void AGreyColourSpaceWithThreeColourChannels_IsRefused()
    {
        var wrapped = Jp2(
            Header(ImageHeader(), EnumeratedColour(17), Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3))),
            Box("jp2c", File.ReadAllBytes(Path.Combine(FixtureDirectory, "rgb-struct64.j2k"))));

        ImageCodecs.TryDecode(wrapped, out _, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
    }

    /// <summary>A box that contradicts itself or the image is invalid data, never a wrong picture.</summary>
    [Theory]
    [InlineData("channel twice")]
    [InlineData("channel out of range")]
    [InlineData("colour twice")]
    [InlineData("colour gap")]
    [InlineData("colour zero")]
    [InlineData("unknown type")]
    [InlineData("opacity of missing colour")]
    [InlineData("no entries")]
    [InlineData("short box")]
    [InlineData("truncated entries")]
    public void ABadChannelDefinition_IsInvalid(string what)
    {
        byte[] box = what switch
        {
            "channel twice" => Cdef((0, 0, 1), (0, 0, 2), (2, 0, 3)),
            "channel out of range" => Cdef((0, 0, 1), (1, 0, 2), (3, 0, 3)),
            "colour twice" => Cdef((0, 0, 1), (1, 0, 1), (2, 0, 2)),
            "colour gap" => Cdef((0, 0, 1), (1, 0, 2), (2, 0, 4)),
            "colour zero" => Cdef((0, 0, 0), (1, 0, 1), (2, 0, 2)),
            "unknown type" => Cdef((0, 0, 1), (1, 0, 2), (2, 7, 3)),
            "opacity of missing colour" => Cdef((0, 0, 1), (1, 0, 2), (2, 1, 5)),
            "no entries" => Box("cdef", [0, 0]),
            "short box" => Box("cdef", [0]),
            _ => Box("cdef", [0, 3, 0, 0, 0, 0, 0, 1]),
        };
        var wrapped = Jp2(Header(ImageHeader(), EnumeratedColour(16), box), Box("jp2c", File.ReadAllBytes(Path.Combine(FixtureDirectory, "rgb-struct64.j2k"))));

        Should.Throw<InvalidDataException>(() => Jpeg2000Decoder.Decode(wrapped));
        Should.Throw<InvalidDataException>(() => Jpeg2000Decoder.ReadInfo(wrapped));
        ImageCodecs.TryDecode(wrapped, out _, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
    }

    /// <summary>The facade's alpha survives a scaled decode (reduce), sample for sample against the decoder at the same reduction.</summary>
    [Fact]
    public void AlphaSurvivesAScaledDecode()
    {
        var bytes = Alpha("rgba-struct37x29");
        var reduced = Jpeg2000Decoder.Decode(bytes, 1, TestContext.Current.CancellationToken);

        ImageCodecs.TryDecode(bytes, out var image, new DecodeOptions { ScaleDenominator = 2 }, TestContext.Current.CancellationToken)
            .ShouldBeTrue();

        (image.Width, image.Height, image.Channels).ShouldBe((reduced.Width, reduced.Height, 4));
        image.Pixels.ToArray().ShouldBe(reduced.Samples.Select(s => (byte)s).ToArray());
    }

    private static ushort[] Reverse(ushort[] samples, int channels)
    {
        var result = new ushort[samples.Length];
        for (var i = 0; i < samples.Length; i += channels)
            for (var c = 0; c < channels; c++)
                result[i + c] = samples[i + channels - 1 - c];
        return result;
    }

    // --- box builders -----------------------------------------------------------------------

    /// <summary>A JP2 file for the named lossless fixture codestream with the given channel definition.</summary>
    private static byte[] Wrap(string fixture, byte[] cdef) =>
        Jp2(Header(ImageHeader(), EnumeratedColour(16), cdef),
            Box("jp2c", File.ReadAllBytes(Path.Combine(FixtureDirectory, fixture + ".j2k"))));

    /// <summary>A <c>cdef</c> box of (channel, type, association) entries.</summary>
    private static byte[] Cdef(params (int Channel, int Type, int Association)[] entries)
    {
        var body = new List<byte> { (byte)(entries.Length >> 8), (byte)entries.Length };
        foreach (var (channel, type, association) in entries)
        {
            body.AddRange([(byte)(channel >> 8), (byte)channel, (byte)(type >> 8), (byte)type, (byte)(association >> 8), (byte)association]);
        }

        return Box("cdef", [.. body]);
    }

    /// <summary>The same file with its <c>cdef</c> box replaced by <paramref name="cdef"/> (a whole box), fixing the header's length.</summary>
    private static byte[] ReplaceCdef(byte[] jp2, byte[] cdef)
    {
        var start = jp2.AsSpan().IndexOf("cdef"u8) - 4;
        var oldLength = BinaryPrimitives.ReadInt32BigEndian(jp2.AsSpan(start));
        var header = jp2.AsSpan().IndexOf("jp2h"u8) - 4;
        var headerLength = BinaryPrimitives.ReadInt32BigEndian(jp2.AsSpan(header));

        byte[] result = [.. jp2[..start], .. cdef, .. jp2[(start + oldLength)..]];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(header), headerLength - oldLength + cdef.Length);
        return result;
    }

    private static byte[] Jp2(params byte[][] boxes)
    {
        byte[] signature = [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];
        var fileType = Box("ftyp", [.. "jp2 "u8, 0, 0, 0, 0, .. "jp2 "u8]);
        return [.. signature, .. fileType, .. boxes.SelectMany(b => b)];
    }

    private static byte[] Header(params byte[][] boxes) => Box("jp2h", [.. boxes.SelectMany(b => b)]);

    private static byte[] ImageHeader() =>
        Box("ihdr", [.. BigEndian32(64), .. BigEndian32(64), 0, 3, 7, 7, 0, 0]);

    private static byte[] EnumeratedColour(int colourSpace) =>
        Box("colr", [1, 0, 0, .. BigEndian32(colourSpace)]);

    private static byte[] Box(string type, byte[] body) =>
        [.. BigEndian32(8 + body.Length), .. Encoding.ASCII.GetBytes(type), .. body];

    private static byte[] BigEndian32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}
