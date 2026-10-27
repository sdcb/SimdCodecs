using System.Buffers.Binary;
using System.Text;
using Sdcb.SimdCodecs.Color.Icc;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Smoke tests for <see cref="IccProfiles"/>. Verifies the bundled binary
/// resource loads, has a valid ICC v2/v4 header (the 'acsp' signature at byte
/// offset 36 + a plausible header layout), and that subsequent reads return
/// the same byte sequence (the lazy cache is consistent).
/// </summary>
public sealed class IccProfilesTests
{
    [Fact]
    public void SRgbV4_LoadsKnownBytes()
    {
        var bytes = IccProfiles.SRgbV4.Span;

        // Profile size header (BE uint32 at offset 0) must equal total length.
        BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]).ShouldBe((uint)bytes.Length);

        // 'acsp' magic at offset 36 is the universal ICC profile identifier
        // (present in v2 and v4, ColourSync, Windows ICM, libtiff, lcms2, etc.).
        Encoding.ASCII.GetString(bytes.Slice(36, 4)).ShouldBe("acsp");

        // Device class 'mntr' (display profile) + 'RGB ' colour space + 'XYZ '
        // PCS — what every viewer expects from an sRGB display profile.
        Encoding.ASCII.GetString(bytes.Slice(12, 4)).ShouldBe("mntr");
        Encoding.ASCII.GetString(bytes.Slice(16, 4)).ShouldBe("RGB ");
        Encoding.ASCII.GetString(bytes.Slice(20, 4)).ShouldBe("XYZ ");
    }

    [Fact]
    public void SRgbV4_IsStable_AcrossCalls()
    {
        var a = IccProfiles.SRgbV4;
        var b = IccProfiles.SRgbV4;
        // Same backing array (Lazy caches), so the spans are identical references.
        a.Span.SequenceEqual(b.Span).ShouldBeTrue();
        a.Length.ShouldBe(b.Length);
    }
}
