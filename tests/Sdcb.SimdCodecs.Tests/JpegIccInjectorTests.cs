using System.Buffers.Binary;
using Sdcb.SimdCodecs.Color.Icc;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Verifies APP2 ICC profile injection by <see cref="JpegIccInjector"/>.
/// Tests synthesise minimal JPEG-like byte streams — the injector only walks
/// markers, it never decodes pixels, so we can keep inputs to a handful of
/// markers and still exercise the full parse path.
/// </summary>
public sealed class JpegIccInjectorTests
{
    [Fact]
    public void EmbedIccProfile_NoExistingApps_InsertsAfterSoi()
    {
        // SOI + SOF0(2-byte length, no payload — invalid but tolerable to the marker walker)
        // + EOI. Marker walker stops at the non-APP SOF0, which is where the APP2 should land.
        var jpeg = new byte[]
        {
            0xFF, 0xD8,                   // SOI
            0xFF, 0xC0, 0x00, 0x02,       // SOF0, length=2 (no payload)
            0xFF, 0xD9,                   // EOI
        };

        var profile = IccProfiles.SRgbV4;
        var output = JpegIccInjector.EmbedIccProfile(jpeg, profile);

        // Expected: SOI + APP2(ICC) + SOF0 + EOI. APP2 sits immediately after SOI.
        output[0].ShouldBe((byte)0xFF);
        output[1].ShouldBe((byte)0xD8);
        output[2].ShouldBe((byte)0xFF);
        output[3].ShouldBe((byte)0xE2);

        var extracted = ExtractIccProfile(output);
        profile.Span.SequenceEqual(extracted).ShouldBeTrue();
    }

    [Fact]
    public void EmbedIccProfile_WithExistingAppMarkers_InsertsAfterLastAppMarker()
    {
        // SOI + APP0(JFIF) + APP1(EXIF stub) + SOF0 + EOI. Injector should land
        // the new APP2 between APP1 and SOF0, not between SOI and APP0.
        var jpeg = new byte[]
        {
            0xFF, 0xD8,                                     // SOI
            0xFF, 0xE0, 0x00, 0x05, 0x4A, 0x46, 0x49,       // APP0 length=5, payload "JFI"
            0xFF, 0xE1, 0x00, 0x04, 0x45, 0x58,             // APP1 length=4, payload "EX"
            0xFF, 0xC0, 0x00, 0x02,                         // SOF0, length=2 (no payload)
            0xFF, 0xD9,                                     // EOI
        };

        var profile = IccProfiles.SRgbV4;
        var output = JpegIccInjector.EmbedIccProfile(jpeg, profile);

        // The two original APP markers should still be in order at the start, then APP2,
        // then SOF0. Walk the prefix to verify.
        output[0..2].ShouldBe(new byte[] { 0xFF, 0xD8 });
        output[2..4].ShouldBe(new byte[] { 0xFF, 0xE0 });   // APP0 preserved
        output[9..11].ShouldBe(new byte[] { 0xFF, 0xE1 });  // APP1 preserved

        // After APP1 (which is 6 bytes total: FF E1 + 4 length-inclusive), the APP2 starts.
        // APP0 = 2 + 5 = 7 bytes; APP1 = 2 + 4 = 6 bytes. SOI=2. Total prefix = 15 bytes.
        output[15].ShouldBe((byte)0xFF);
        output[16].ShouldBe((byte)0xE2);

        var extracted = ExtractIccProfile(output);
        profile.Span.SequenceEqual(extracted).ShouldBeTrue();
    }

    [Fact]
    public void EmbedIccProfile_SegmentLengthMatchesSpec()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x02, 0xFF, 0xD9 };
        var profile = IccProfiles.SRgbV4;
        var output = JpegIccInjector.EmbedIccProfile(jpeg, profile);

        // APP2 sits at offset 2. Bytes 2-3 are FF E2; bytes 4-5 are the big-endian length.
        // Length is segment-inclusive: 2 (length field) + 12 ("ICC_PROFILE\0") + 1 (seq)
        // + 1 (total) + profile.Length.
        var declaredLength = BinaryPrimitives.ReadUInt16BigEndian(output.AsSpan(4, 2));
        declaredLength.ShouldBe((ushort)(16 + profile.Length));
    }

    [Fact]
    public void EmbedIccProfile_EmitsCorrectIccMarkerAndSequence()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x02, 0xFF, 0xD9 };
        var profile = IccProfiles.SRgbV4;
        var output = JpegIccInjector.EmbedIccProfile(jpeg, profile);

        // After FF E2 [len_hi] [len_lo] (4 bytes), the next 12 bytes must be "ICC_PROFILE\0".
        var marker = output.AsSpan(6, 12);
        marker.SequenceEqual("ICC_PROFILE\0"u8).ShouldBeTrue();

        // Then 1 byte sequence (1) + 1 byte total (1) for our single-segment write.
        output[18].ShouldBe((byte)0x01);
        output[19].ShouldBe((byte)0x01);
    }

    [Fact]
    public void EmbedIccProfile_NoSoi_Throws()
    {
        var notJpeg = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG signature, deliberately wrong
        var profile = IccProfiles.SRgbV4;
        Should.Throw<ArgumentException>(() => JpegIccInjector.EmbedIccProfile(notJpeg, profile));
    }

    [Fact]
    public void EmbedIccProfile_EmptyProfile_Throws()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x02, 0xFF, 0xD9 };
        Should.Throw<ArgumentOutOfRangeException>(() => JpegIccInjector.EmbedIccProfile(jpeg, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void EmbedIccProfile_OversizeProfile_Throws()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x02, 0xFF, 0xD9 };
        // 65520 = single-segment limit + 1.
        var oversize = new byte[65520];
        Should.Throw<ArgumentOutOfRangeException>(() => JpegIccInjector.EmbedIccProfile(jpeg, oversize));
    }

    [Fact]
    public void EmbedIccProfile_BoundaryProfileSize_Accepted()
    {
        // The largest profile that fits in a single APP2 segment: 65535 - 16 = 65519 bytes.
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x02, 0xFF, 0xD9 };
        var maxProfile = new byte[65519];
        for (var i = 0; i < maxProfile.Length; i++) maxProfile[i] = (byte)(i & 0xFF);

        var output = JpegIccInjector.EmbedIccProfile(jpeg, maxProfile);
        var extracted = ExtractIccProfile(output);
        new ReadOnlySpan<byte>(maxProfile).SequenceEqual(extracted).ShouldBeTrue();
    }

    [Fact]
    public void EmbedIccProfile_MalformedAppLength_Throws()
    {
        // SOI + APP0 with length=1 (invalid — length must be >=2 to cover the length field itself).
        var jpeg = new byte[]
        {
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x01,
            0xFF, 0xD9,
        };
        Should.Throw<ArgumentException>(() => JpegIccInjector.EmbedIccProfile(jpeg, IccProfiles.SRgbV4));
    }

    /// <summary>
    /// Walk the post-injection JPEG, find the APP2 ICC segment, and return its
    /// payload. Re-implements the reader half of the ICC.1:2004 annex B carriage
    /// so the test never trusts the writer's bookkeeping.
    /// </summary>
    private static byte[] ExtractIccProfile(byte[] jpeg)
    {
        var pos = 2; // skip SOI
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF)
                throw new InvalidOperationException($"Expected marker at offset {pos}");
            pos++;
            var marker = jpeg[pos++];
            if (marker == 0xD9 || marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01)
                continue;

            var segLen = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos, 2));
            if (marker == 0xE2 &&
                jpeg.AsSpan(pos + 2, 12).SequenceEqual("ICC_PROFILE\0"u8))
            {
                // Single-segment carriage: bytes after seq(1)+total(1) are the profile.
                var profileStart = pos + 2 + 12 + 2;
                var profileLen = segLen - 16; // overhead = 2(len) + 12(marker) + 1(seq) + 1(total)
                return jpeg.AsSpan(profileStart, profileLen).ToArray();
            }
            pos += segLen;
        }
        throw new InvalidOperationException("No ICC profile APP2 segment found.");
    }
}
