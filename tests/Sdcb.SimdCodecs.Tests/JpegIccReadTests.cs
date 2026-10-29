using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Color.Icc;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="JpegDecoder.TryReadIccProfile"/> and the ICC profile the JPEG facade decode returns: profiles
/// libjpeg (Magick.NET) embedded, in one APP2 chunk and in many, and hand-built chunk sets that are out of
/// order, incomplete or inconsistent.
/// </summary>
public sealed class JpegIccReadTests
{
    private static byte[] MagickJpeg(ColorProfile profile, bool cmyk)
    {
        using var image = new MagickImage(MagickColors.CornflowerBlue, 24, 16);
        if (cmyk)
            image.ColorSpace = ColorSpace.CMYK;
        image.SetProfile(profile);
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    [Fact]
    public void Reads_a_single_chunk_profile_written_by_libjpeg()
    {
        var profile = ColorProfiles.AdobeRGB1998;
        var jpeg = MagickJpeg(profile, cmyk: false);

        Icc(jpeg).ShouldBe(profile.ToByteArray());
        SimdCodecs.TryDecode(jpeg, out var image).ShouldBeTrue();
        image.IccProfile.ToArray().ShouldBe(profile.ToByteArray());
        IccProfiles.Describe(image.IccProfile).ShouldBe("Adobe RGB (1998)");
    }

    [Fact]
    public void Joins_a_multi_chunk_profile_written_by_libjpeg()
    {
        // A CMYK press profile is several hundred KB: libjpeg splits it over many 64 KB APP2 segments.
        var profile = ColorProfiles.USWebCoatedSWOP;
        profile.ToByteArray().Length.ShouldBeGreaterThan(2 * 65519);
        var jpeg = MagickJpeg(profile, cmyk: true);

        Icc(jpeg).ShouldBe(profile.ToByteArray());
        SimdCodecs.TryDecode(jpeg, out var image).ShouldBeTrue();
        image.IccProfile.ToArray().ShouldBe(profile.ToByteArray());
    }

    [Fact]
    public void No_profile_is_null_and_an_empty_IccProfile()
    {
        var jpeg = JpegEncoder.Encode(new byte[8 * 8 * 3], 8, 8, 3);

        Icc(jpeg).ShouldBeNull();
        SimdCodecs.TryDecode(jpeg, out var image).ShouldBeTrue();
        image.IccProfile.IsEmpty.ShouldBeTrue();
        Icc("not a jpeg"u8).ShouldBeNull();
    }

    [Fact]
    public void Chunks_are_joined_by_sequence_number_not_file_order()
    {
        var jpeg = WithChunks((2, 3, "BB"), (3, 3, "C"), (1, 3, "AAA"));

        Encoding.ASCII.GetString(Icc(jpeg)!).ShouldBe("AAABBC");
    }

    [Fact]
    public void Inconsistent_chunks_are_null()
    {
        Icc(WithChunks((1, 2, "A"))).ShouldBeNull();                 // one missing
        Icc(WithChunks((1, 2, "A"), (1, 2, "B"))).ShouldBeNull();    // repeated
        Icc(WithChunks((1, 2, "A"), (2, 3, "B"))).ShouldBeNull();    // counts differ
        Icc(WithChunks((0, 1, "A"))).ShouldBeNull();                 // sequence 0
        Icc(WithChunks((1, 0, "A"))).ShouldBeNull();                 // count 0
        Icc(WithChunks((1, 1, ""))).ShouldBeNull();                  // empty profile
    }

    // A baseline JPEG with APP2 ICC_PROFILE segments (sequence, count, data) inserted after SOI.
    private static byte[] WithChunks(params (int Sequence, int Count, string Data)[] chunks)
    {
        var jpeg = JpegEncoder.Encode(new byte[8 * 8 * 3], 8, 8, 3);
        var output = new List<byte> { 0xFF, 0xD8 };
        foreach (var (sequence, count, data) in chunks)
        {
            byte[] payload = [.. "ICC_PROFILE\0"u8, (byte)sequence, (byte)count, .. Encoding.ASCII.GetBytes(data)];
            var length = payload.Length + 2;
            output.AddRange([0xFF, 0xE2, (byte)(length >> 8), (byte)length]);
            output.AddRange(payload);
        }

        output.AddRange(jpeg.Skip(2));
        return output.ToArray();
    }

    // TryReadIccProfile as a value: the profile, or null when it returns false (and then gives null).
    private static byte[]? Icc(ReadOnlySpan<byte> jpeg)
    {
        if (JpegDecoder.TryReadIccProfile(jpeg, out var profile))
            return profile;
        profile.ShouldBeNull();
        return null;
    }
}
