using Sdcb.SimdCodecs.Jpeg;
using Shouldly;
using Xunit;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="JpegSegmentScanner"/> — the byte-level APPn access point added for
/// gain-map support (and the long-flagged ICC/EXIF surfacing gap). The scanner
/// only follows the marker-and-length rhythm, so handmade minimal streams are
/// valid inputs.
/// </summary>
public sealed class JpegSegmentScannerTests
{
    /// <summary>SOI, one APP1 ("AB"), one DQT-ish segment, then SOS.</summary>
    private static byte[] MinimalJpeg() =>
    [
        0xFF, 0xD8,                         // SOI
        0xFF, 0xE1, 0x00, 0x04, 0x41, 0x42, // APP1, payload "AB"
        0xFF, 0xDB, 0x00, 0x03, 0x99,       // DQT, payload 0x99
        0xFF, 0xDA, 0x00, 0x02,             // SOS, empty payload
    ];

    [Fact]
    public void Scan_walks_soi_to_sos()
    {
        var segments = JpegSegmentScanner.Scan(MinimalJpeg());

        segments.Count.ShouldBe(4);
        segments[0].Marker.ShouldBe((byte)0xD8);
        segments[1].Marker.ShouldBe((byte)0xE1);
        segments[1].SegmentOffset.ShouldBe(2);
        segments[1].PayloadOffset.ShouldBe(6);
        segments[1].PayloadLength.ShouldBe(2);
        segments[1].TotalLength.ShouldBe(6);
        segments[2].Marker.ShouldBe((byte)0xDB);
        segments[3].Marker.ShouldBe(JpegSegmentScanner.Sos);
    }

    [Fact]
    public void Scan_payload_slices_the_right_bytes()
    {
        var jpeg = MinimalJpeg();
        var segments = JpegSegmentScanner.Scan(jpeg);

        segments[1].Payload(jpeg).ToArray().ShouldBe("AB"u8.ToArray());
    }

    [Fact]
    public void Scan_rejects_non_jpeg_and_truncated_streams()
    {
        Should.Throw<InvalidDataException>(() => JpegSegmentScanner.Scan([0x89, 0x50, 0x4E, 0x47]));
        // SOI then a segment whose declared length runs past EOF.
        Should.Throw<InvalidDataException>(() => JpegSegmentScanner.Scan([0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xFF, 0x00]));
        // SOI then garbage where a marker byte should be.
        Should.Throw<InvalidDataException>(() => JpegSegmentScanner.Scan([0xFF, 0xD8, 0x00, 0x11, 0x22, 0x33]));
    }

    [Fact]
    public void Scan_handles_fill_bytes_before_markers()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xFF, 0xFF, 0xE1, 0x00, 0x04, 0x41, 0x42, // two fill bytes, then APP1
            0xFF, 0xDA, 0x00, 0x02,
        ];

        var segments = JpegSegmentScanner.Scan(jpeg);

        segments.Count.ShouldBe(3);
        segments[1].Marker.ShouldBe((byte)0xE1);
        segments[1].Payload(jpeg).ToArray().ShouldBe("AB"u8.ToArray());
    }

    [Fact]
    public void TryFindAppPayload_matches_identifier_and_returns_trailing_payload()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xE2, 0x00, 0x08, (byte)'M', (byte)'P', (byte)'F', 0x00, 0xCA, 0xFE,
            0xFF, 0xDA, 0x00, 0x02,
        ];

        JpegSegmentScanner.TryFindAppPayload(jpeg, 0xE2, "MPF\0"u8, out var payload).ShouldBeTrue();
        jpeg.AsSpan()[payload].ToArray().ShouldBe([0xCA, 0xFE]);

        JpegSegmentScanner.TryFindAppPayload(jpeg, 0xE1, "MPF\0"u8, out _).ShouldBeFalse("wrong marker");
        JpegSegmentScanner.TryFindAppPayload(jpeg, 0xE2, "Exif\0\0"u8, out _).ShouldBeFalse("wrong identifier");
        JpegSegmentScanner.TryFindAppPayload([1, 2, 3], 0xE2, "MPF\0"u8, out _).ShouldBeFalse("not a JPEG");
    }

    [Fact]
    public void TryFindAppPayload_finds_an_injected_icc_profile()
    {
        // Cross-check against the family's other splicer: JpegIccInjector output
        // must be discoverable through the scanner.
        byte[] profile = [0xDE, 0xAD, 0xBE, 0xEF];
        var jpeg = JpegIccInjector.EmbedIccProfile(MinimalJpeg(), profile);

        JpegSegmentScanner.TryFindAppPayload(jpeg, 0xE2, "ICC_PROFILE\0"u8, out var payload).ShouldBeTrue();
        // Payload after the identifier: sequence 1 of 1, then the profile bytes.
        jpeg.AsSpan()[payload].ToArray().ShouldBe([0x01, 0x01, 0xDE, 0xAD, 0xBE, 0xEF]);
    }
}
