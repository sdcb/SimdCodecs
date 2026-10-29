using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Color.Icc;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="IccProfiles.Describe"/>: the profiles Magick.NET ships (v2 <c>desc</c>, compared with
/// Magick's own <c>Description</c>), the bundled v4 profile, hand-built <c>mluc</c> tags for the
/// language choice, and malformed input that must yield null rather than throw.
/// </summary>
public sealed class IccDescribeTests
{
    public static TheoryData<string> MagickProfiles => new() { "SRGB", "AdobeRGB1998", "AppleRGB", "CoatedFOGRA39", "ColorMatchRGB", "USWebCoatedSWOP" };

    private static ColorProfile MagickProfile(string name) =>
        (ColorProfile)typeof(ColorProfiles).GetProperty(name)!.GetValue(null)!;

    [Theory]
    [MemberData(nameof(MagickProfiles))]
    public void Matches_Magick_on_its_bundled_profiles(string name)
    {
        var profile = MagickProfile(name);
        var bytes = profile.ToByteArray();

        var description = IccProfiles.Describe(bytes);

        description.ShouldNotBeNull();
        description.ShouldBe(profile.Description);
    }

    [Fact]
    public void Reads_the_bundled_v4_profile_and_its_cicp_variant()
    {
        IccProfiles.Describe(IccProfiles.SRgbV4.Span).ShouldBe("sRGB built-in");
        IccProfiles.Describe(IccProfiles.WithCicp(IccProfiles.SRgbV4.Span, ColorPrimaries.BT709, TransferFunction.Srgb))
            .ShouldBe("sRGB built-in");
    }

    [Fact]
    public void Mluc_prefers_en_US_then_English_then_the_first_record()
    {
        IccProfiles.Describe(Profile(Mluc(("de", "DE", "Deutsch"), ("en", "GB", "British"), ("en", "US", "American"))))
            .ShouldBe("American");
        IccProfiles.Describe(Profile(Mluc(("de", "DE", "Deutsch"), ("en", "GB", "British")))).ShouldBe("British");
        IccProfiles.Describe(Profile(Mluc(("fr", "FR", "Français"), ("de", "DE", "Deutsch")))).ShouldBe("Français");
    }

    [Fact]
    public void Text_description_is_trimmed_at_the_NUL()
    {
        IccProfiles.Describe(Profile(TextDescription("Display P3\0junk"))).ShouldBe("Display P3");
    }

    [Fact]
    public void Malformed_or_missing_description_is_null()
    {
        IccProfiles.Describe([]).ShouldBeNull();
        IccProfiles.Describe(new byte[200]).ShouldBeNull();                       // no tags
        IccProfiles.Describe(Profile(("cprt", TextDescription("x")))).ShouldBeNull(); // no desc tag
        IccProfiles.Describe(Profile(("desc", "XYZ \0\0\0\0"u8.ToArray()))).ShouldBeNull(); // another type
        IccProfiles.Describe(Profile(TextDescription(""))).ShouldBeNull();

        var good = Profile(Mluc(("en", "US", "Name")));
        for (var cut = 0; cut < good.Length; cut++)
            IccProfiles.Describe(good.AsSpan(0, cut)); // must not throw

        // Huge counts, offsets and sizes in every 32-bit field of the tag table and the tag.
        for (var at = 128; at + 4 <= good.Length; at++)
        {
            var bad = (byte[])good.Clone();
            BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(at), 0xFFFFFFF0u);
            IccProfiles.Describe(bad);
        }
    }

    [Fact]
    public void Random_mutations_never_throw()
    {
        var random = new Random(1234);
        var seeds = new[] { IccProfiles.SRgbV4.ToArray(), Profile(TextDescription("Adobe RGB (1998)")) };
        for (var i = 0; i < 20000; i++)
        {
            var bytes = (byte[])seeds[i % 2].Clone();
            for (var m = random.Next(1, 8); m > 0; m--)
                bytes[random.Next(128, bytes.Length)] = (byte)random.Next(256);
            IccProfiles.Describe(bytes);
        }
    }

    private static byte[] Profile(byte[] desc) => Profile(("desc", desc));

    // A 128-byte header (contents irrelevant here), the tag table, then each tag's data.
    private static byte[] Profile(params (string Sig, byte[] Data)[] tags)
    {
        var bytes = new List<byte>(new byte[128]);
        bytes.AddRange(U32(tags.Length));
        var offset = 132 + 12 * tags.Length;
        foreach (var (sig, data) in tags)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(sig));
            bytes.AddRange(U32(offset));
            bytes.AddRange(U32(data.Length));
            offset += data.Length;
        }

        foreach (var (_, data) in tags)
            bytes.AddRange(data);
        var result = bytes.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        return result;
    }

    private static byte[] TextDescription(string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text + "\0");
        return [.. "desc"u8, 0, 0, 0, 0, .. U32(ascii.Length), .. ascii];
    }

    private static byte[] Mluc(params (string Lang, string Country, string Text)[] records)
    {
        var head = new List<byte>([.. "mluc"u8, 0, 0, 0, 0, .. U32(records.Length), .. U32(12)]);
        var strings = new List<byte>();
        var stringsStart = 16 + 12 * records.Length;
        foreach (var (lang, country, text) in records)
        {
            var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
            head.AddRange(Encoding.ASCII.GetBytes(lang + country));
            head.AddRange(U32(utf16.Length));
            head.AddRange(U32(stringsStart + strings.Count));
            strings.AddRange(utf16);
        }

        return [.. head, .. strings];
    }

    private static byte[] U32(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)value);
        return b;
    }
}
