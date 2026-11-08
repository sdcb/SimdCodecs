using System;
using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// The metadata a JPEG carries in its APPn segments, as raw blocks for <see cref="IImageDecoder.TryReadMetadata"/>,
/// plus the two facts <see cref="ImageInfo"/> reports: the declared pixel density (JFIF APP0, else Exif IFD0) and
/// whether Exif IFD1 holds a JPEG thumbnail. Malformed segments read as absent.
/// </summary>
internal static class JpegMetadata
{
    private const byte App0 = 0xE0;
    private const byte App1 = 0xE1;

    private static ReadOnlySpan<byte> ExifId => "Exif\0\0"u8;
    private static ReadOnlySpan<byte> JfifId => "JFIF\0"u8;
    private static ReadOnlySpan<byte> XmpId => "http://ns.adobe.com/xap/1.0/\0"u8;

    /// <summary>The Exif block from its TIFF header (without <c>Exif\0\0</c>); empty when absent.</summary>
    public static ReadOnlyMemory<byte> ReadExif(ReadOnlySpan<byte> jpeg) =>
        JpegSegmentScanner.TryFindAppPayload(jpeg, App1, ExifId, out var range) ? jpeg[range].ToArray() : default;

    /// <summary>
    /// The XMP packet: the standard packet (the first APP1 with the XMP namespace URI), with the Extended XMP it names
    /// (<c>xmpNote:HasExtendedXMP</c>, APP1 <c>http://ns.adobe.com/xmp/extension/</c> chunks) joined in by
    /// <see cref="JpegExtendedXmp"/>; empty when there is no standard packet. When the extended chunks are missing,
    /// inconsistent or fail their MD5, the standard packet comes back unchanged.
    /// </summary>
    public static ReadOnlyMemory<byte> ReadXmp(ReadOnlySpan<byte> jpeg)
    {
        List<JpegSegment> segments;
        try
        {
            segments = JpegSegmentScanner.Scan(jpeg);
        }
        catch (InvalidDataException)
        {
            return default;
        }

        JpegSegment? standard = null;
        var hasExtension = false;
        foreach (var segment in segments)
        {
            if (segment.Marker != App1)
                continue;
            if (standard is null && segment.Payload(jpeg).StartsWith(XmpId))
                standard = segment;
            else if (JpegExtendedXmp.IsExtensionSegment(jpeg, segment))
                hasExtension = true;
        }

        if (standard is not { } s)
            return default;
        var packet = s.Payload(jpeg)[XmpId.Length..];
        return hasExtension && JpegExtendedXmp.TryJoin(jpeg, segments, packet, out var joined) ? joined : packet.ToArray();
    }

    /// <summary>
    /// The density a JFIF APP0 declares (units 1 inch, 2 cm, 0 aspect ratio), or failing that Exif IFD0's
    /// XResolution / YResolution; null when neither is present. A JFIF density of 0 or 1:1 aspect-only is no
    /// information and reads as absent so that an Exif one can take over.
    /// </summary>
    public static Resolution? ReadResolution(ReadOnlySpan<byte> jpeg)
    {
        if (JpegSegmentScanner.TryFindAppPayload(jpeg, App0, JfifId, out var range))
        {
            // JFIF 1.02 APP0 after the identifier: version (2), units (1), Xdensity (2), Ydensity (2), …
            var p = jpeg[range];
            if (p.Length >= 7)
            {
                var units = p[2];
                int x = p[3] << 8 | p[4], y = p[5] << 8 | p[6];
                if (x > 0 && y > 0 && !(units == 0 && x == 1 && y == 1))
                {
                    var unit = units switch
                    {
                        1 => ResolutionUnit.PerInch,
                        2 => ResolutionUnit.PerCentimeter,
                        _ => ResolutionUnit.AspectRatio,
                    };
                    return new Resolution(x, y, unit);
                }
            }
        }

        return JpegSegmentScanner.TryFindAppPayload(jpeg, App1, ExifId, out var exif)
            ? ExifBlock.ReadResolution(jpeg[exif])
            : null;
    }

    /// <summary>True when Exif IFD1 locates a JPEG thumbnail inside the APP1 block.</summary>
    public static bool HasExifThumbnail(ReadOnlySpan<byte> jpeg) =>
        JpegSegmentScanner.TryFindAppPayload(jpeg, App1, ExifId, out var exif)
        && ExifBlock.TryGetThumbnail(jpeg[exif], out _);

    /// <summary>Decodes the Exif IFD1 thumbnail, a JPEG stream of its own.</summary>
    public static bool TryDecodeExifThumbnail(ReadOnlySpan<byte> jpeg, [NotNullWhen(true)] out IDecodedImage? thumbnail)
    {
        thumbnail = null;
        if (!JpegSegmentScanner.TryFindAppPayload(jpeg, App1, ExifId, out var exif))
            return false;
        var block = jpeg[exif];
        if (!ExifBlock.TryGetThumbnail(block, out var range))
            return false;
        var stream = block[range.Start.Value..range.End.Value];
        // The thumbnail is a complete JPEG; it cannot itself carry a thumbnail worth recursing into.
        return JpegImageDecoder.TryDecode(stream, out thumbnail);
    }
}
