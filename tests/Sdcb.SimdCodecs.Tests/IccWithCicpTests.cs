using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Color.Icc;
using Shouldly;
using Xunit;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Verifies <see cref="IccProfiles.WithCicp"/> emits an ICC profile that
/// is structurally valid per ICC v4 §7.2: the profile-size header field,
/// the tag-table count, the appended <c>cicp</c> tag entry, the cicp tag
/// body, and the Profile ID MD5 all round-trip cleanly.
/// </summary>
public class IccWithCicpTests
{
    [Fact]
    public void Hdr10_Preset_HasExpectedLayout()
    {
        var src = IccProfiles.SRgbV4.Span;
        var srcSize = src.Length;
        var srcTagCount = BinaryPrimitives.ReadUInt32BigEndian(src.Slice(128, 4));

        var dst = IccProfiles.WithCicp(
            src,
            ColorPrimaries.BT2020,
            TransferFunction.Pq);

        // 1. Profile grew by 24 bytes (12 for the new tag-table entry,
        //    12 for the cicp tag body).
        dst.Length.ShouldBe(srcSize + 24);

        // 2. Header's profile-size field matches.
        BinaryPrimitives.ReadUInt32BigEndian(dst).ShouldBe((uint)dst.Length);

        // 3. Tag count incremented by one.
        var newTagCount = BinaryPrimitives.ReadUInt32BigEndian(dst.AsSpan(128, 4));
        newTagCount.ShouldBe(srcTagCount + 1);

        // 4. Walk the new tag table: every existing entry's offset was
        //    shifted by +12; the last (newly added) entry has signature
        //    'cicp' and points to the end of the file.
        var seenCicp = false;
        for (var i = 0; i < newTagCount; i++)
        {
            var entry = dst.AsSpan(132 + i * 12, 12);
            var sig = entry[..4].ToArray();
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(4, 4));
            var size = BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(8, 4));
            if (sig is [(byte)'c', (byte)'i', (byte)'c', (byte)'p'])
            {
                seenCicp = true;
                size.ShouldBe(12u, "cicp tag body is 4-byte type sig + 4 reserved + 4 CICP bytes");
                offset.ShouldBe((uint)(dst.Length - 12), "cicp body appended at end");
                // Verify body contents
                var body = dst.AsSpan((int)offset, (int)size);
                body[..4].ToArray().ShouldBe(new byte[] { (byte)'c', (byte)'i', (byte)'c', (byte)'p' });
                body[4..8].ToArray().ShouldBe(new byte[] { 0, 0, 0, 0 }, "4 reserved zeros");
                body[8].ShouldBe((byte)ColorPrimaries.BT2020);
                body[9].ShouldBe((byte)TransferFunction.Pq);
                body[10].ShouldBe((byte)MatrixCoefficients.Identity);
                body[11].ShouldBe((byte)1, "videoFullRange=true");
            }
        }
        seenCicp.ShouldBeTrue("a 'cicp' tag-table entry must be present");

        // 5. Profile ID (header bytes 84..99) is a recomputed MD5 that
        //    matches what we'd get by zeroing flags + intent + ID then
        //    hashing. NOT the all-zero sentinel that SRgbV4 ships with
        //    (proof that we actually recomputed it).
        var profileId = dst.AsSpan(84, 16).ToArray();
        profileId.ShouldNotBe(new byte[16], "Profile ID was recomputed, not left at zero");

        var hashBuf = (byte[])dst.Clone();
        hashBuf.AsSpan(44, 4).Clear();   // flags
        hashBuf.AsSpan(64, 4).Clear();   // intent
        hashBuf.AsSpan(84, 16).Clear();  // ID itself
        Span<byte> expectedHash = stackalloc byte[16];
        MD5.HashData(hashBuf, expectedHash);
        profileId.ShouldBe(expectedHash.ToArray());
    }

    [Fact]
    public void Srgb_Preset_WritesCorrectCodepoints()
    {
        var dst = IccProfiles.WithCicp(
            IccProfiles.SRgbV4.Span,
            ColorPrimaries.BT709,
            TransferFunction.Srgb);

        // Find the cicp tag body.
        var n = BinaryPrimitives.ReadUInt32BigEndian(dst.AsSpan(128, 4));
        for (var i = 0; i < n; i++)
        {
            var entry = dst.AsSpan(132 + i * 12, 12);
            if (entry[..4].SequenceEqual("cicp"u8))
            {
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(4, 4));
                dst[offset + 8].ShouldBe((byte)1);   // BT.709 primaries
                dst[offset + 9].ShouldBe((byte)13);  // sRGB transfer
                dst[offset + 10].ShouldBe((byte)0);  // Identity matrix
                dst[offset + 11].ShouldBe((byte)1);  // full range
                return;
            }
        }
        Assert.Fail("cicp tag not found in output");
    }

    [Fact]
    public void Hlg_Preset_WritesCorrectCodepoints()
    {
        var dst = IccProfiles.WithCicp(
            IccProfiles.SRgbV4.Span,
            ColorPrimaries.BT2020,
            TransferFunction.Hlg,
            videoFullRange: false);

        var n = BinaryPrimitives.ReadUInt32BigEndian(dst.AsSpan(128, 4));
        for (var i = 0; i < n; i++)
        {
            var entry = dst.AsSpan(132 + i * 12, 12);
            if (entry[..4].SequenceEqual("cicp"u8))
            {
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(4, 4));
                dst[offset + 8].ShouldBe((byte)9);   // BT.2020 primaries
                dst[offset + 9].ShouldBe((byte)18);  // HLG transfer
                dst[offset + 11].ShouldBe((byte)0);  // narrow range
                return;
            }
        }
        Assert.Fail("cicp tag not found in output");
    }

    [Fact]
    public void Rejects_TruncatedProfile()
    {
        var tiny = new byte[64];
        Should.Throw<ArgumentException>(() =>
            IccProfiles.WithCicp(tiny, ColorPrimaries.BT2020, TransferFunction.Pq));
    }

    [Fact]
    public void Rejects_SizeMismatch()
    {
        // Header claims a size that doesn't match the buffer length.
        var bytes = IccProfiles.SRgbV4.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)(bytes.Length + 100));
        Should.Throw<ArgumentException>(() =>
            IccProfiles.WithCicp(bytes, ColorPrimaries.BT2020, TransferFunction.Pq));
    }
}
