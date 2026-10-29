using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Gain-map ("Ultra HDR") JPEG entry points. The write path
/// (<see cref="Compute"/> + <see cref="Assemble"/>) emits the Android
/// Ultra HDR v1 form — GContainer XMP on the primary <em>and</em> an MPF APP2,
/// the superset every Adobe-dialect reader (Chromium/Skia, Android, ACR)
/// accepts. The read path (<see cref="TryRead"/> / <see cref="TrySplit"/>)
/// locates the gain map via MPF first, GContainer second.
/// <para>
/// Like <c>JpegIccInjector</c>, assembly splices metadata around
/// already-encoded baseline JPEGs — bring any encoder. Decoding goes through
/// <see cref="JpegDecoder"/>.
/// </para>
/// </summary>
public static class JpegGainMap
{
    /// <summary>
    /// Detects a gain-map JPEG, decodes both renditions, and parses the hdrgm
    /// parameters. False when the stream is not a JPEG, carries no locatable
    /// gain map, lacks hdrgm metadata, or either rendition fails to decode.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> jpeg, [NotNullWhen(true)] out GainMapImage? image)
    {
        image = null;
        if (!TrySplit(jpeg, out var baseRange, out var gainMapRange, out var metadata))
            return false;

        JpegImage baseImage, gainMapImage;
        try
        {
            baseImage = JpegDecoder.Decode(jpeg[baseRange]);
            gainMapImage = JpegDecoder.Decode(jpeg[gainMapRange]);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        image = new GainMapImage(
            new RasterImage(baseImage.Width, baseImage.Height, 4, SampleFormat.UInt8, baseImage.Pixels),
            new RasterImage(gainMapImage.Width, gainMapImage.Height, 4, SampleFormat.UInt8, gainMapImage.Pixels),
            metadata);
        return true;
    }

    /// <summary>
    /// Locates the two embedded JPEGs and the metadata without decoding any pixels
    /// — for consumers that re-mux or hand the renditions to their own decoder.
    /// The base range keeps its spliced XMP/MPF segments (it remains a complete,
    /// standalone SDR JPEG, which is exactly how legacy viewers see the file).
    /// </summary>
    public static bool TrySplit(ReadOnlySpan<byte> jpeg, out Range baseJpeg, out Range gainMapJpeg, [NotNullWhen(true)] out GainMapMetadata? metadata)
    {
        baseJpeg = default;
        gainMapJpeg = default;
        metadata = null;
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return false;

        var gainMapStart = -1;
        var gainMapLength = 0;

        // Preferred locator: the MPF index — explicit offsets, no guessing.
        if (JpegSegmentScanner.TryFindAppPayload(jpeg, 0xE2, MpfSegment.Identifier, out var mpfPayload))
        {
            var (payloadOffset, payloadLength) = mpfPayload.GetOffsetAndLength(jpeg.Length);
            if (MpfSegment.TryParse(jpeg.Slice(payloadOffset, payloadLength), payloadOffset, out var entries))
            {
                foreach (var entry in entries)
                {
                    if (entry.ImageOffset <= 0 || entry.ImageOffset + 2 > jpeg.Length)
                        continue;
                    var start = (int)entry.ImageOffset;
                    if (jpeg[start] != 0xFF || jpeg[start + 1] != 0xD8)
                        continue;
                    gainMapStart = start;
                    // Tolerate writers that leave the length 0 or oversized: the
                    // gain map then runs to end-of-file.
                    gainMapLength = entry.ImageLength > 0 && start + entry.ImageLength <= jpeg.Length
                        ? (int)entry.ImageLength
                        : jpeg.Length - start;
                    break;
                }
            }
        }

        // Fallback locator: the GContainer directory — the gain map is the trailing
        // Item:Length bytes of the file (the Skia convention for MPF-less files).
        if (gainMapStart < 0
            && JpegSegmentScanner.TryFindAppPayload(jpeg, 0xE1, GainMapXmp.App1Identifier, out var xmpPayload)
            && GainMapXmp.TryParseContainerGainMapLength(jpeg[xmpPayload], out var declaredLength)
            && declaredLength < jpeg.Length)
        {
            var start = jpeg.Length - declaredLength;
            if (jpeg[start] == 0xFF && jpeg[start + 1] == 0xD8)
            {
                gainMapStart = start;
                gainMapLength = declaredLength;
            }
        }

        if (gainMapStart < 0)
            return false;

        var gainMap = jpeg.Slice(gainMapStart, gainMapLength);
        if (!JpegSegmentScanner.TryFindAppPayload(gainMap, 0xE1, GainMapXmp.App1Identifier, out var gainMapXmp)
            || !GainMapXmp.TryParseGainMapMetadata(gainMap[gainMapXmp], out metadata))
            return false;

        baseJpeg = ..gainMapStart;
        gainMapJpeg = gainMapStart..(gainMapStart + gainMapLength);
        return true;
    }

    /// <summary>
    /// Splices a gain map into an SDR base JPEG, producing an Ultra HDR v1 file:
    /// the base gains a GContainer XMP APP1 + MPF APP2 (after its existing APPn
    /// segments), the gain-map JPEG gains an hdrgm XMP APP1, and the two are
    /// concatenated. Both inputs are complete baseline JPEGs from any encoder;
    /// neither is re-encoded. Legacy viewers see only the base.
    /// </summary>
    /// <exception cref="ArgumentException">Either input is not a JPEG starting with SOI,
    /// has a malformed header, or already carries gain-map metadata (an MPF index, or
    /// hdrgm / GContainer XMP) — such as the base range <see cref="TrySplit"/> returns,
    /// which keeps the original file's.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="metadata"/> is unusable
    /// (see <see cref="GainMapMetadata.Validate"/>).</exception>
    public static byte[] Assemble(ReadOnlySpan<byte> sdrBaseJpeg, ReadOnlySpan<byte> gainMapJpeg, GainMapMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        metadata.Validate();

        var hdrgmSegment = GainMapXmp.WriteGainMapApp1(metadata);
        var gainMapInsert = FindInsertionPoint(gainMapJpeg, nameof(gainMapJpeg));
        var gainMapTotal = gainMapJpeg.Length + hdrgmSegment.Length;

        var containerSegment = GainMapXmp.WritePrimaryApp1(gainMapTotal);
        var baseInsert = FindInsertionPoint(sdrBaseJpeg, nameof(sdrBaseJpeg));
        var primaryTotal = sdrBaseJpeg.Length + containerSegment.Length + MpfSegment.TotalLength;
        var mpfSegment = MpfSegment.Write(baseInsert + containerSegment.Length, primaryTotal, gainMapTotal);

        var output = new byte[primaryTotal + gainMapTotal];
        var pos = 0;
        sdrBaseJpeg[..baseInsert].CopyTo(output.AsSpan(pos)); pos += baseInsert;
        containerSegment.CopyTo(output.AsSpan(pos)); pos += containerSegment.Length;
        mpfSegment.CopyTo(output.AsSpan(pos)); pos += mpfSegment.Length;
        sdrBaseJpeg[baseInsert..].CopyTo(output.AsSpan(pos)); pos += sdrBaseJpeg.Length - baseInsert;
        gainMapJpeg[..gainMapInsert].CopyTo(output.AsSpan(pos)); pos += gainMapInsert;
        hdrgmSegment.CopyTo(output.AsSpan(pos)); pos += hdrgmSegment.Length;
        gainMapJpeg[gainMapInsert..].CopyTo(output.AsSpan(pos));

        return output;
    }

    /// <summary>
    /// Generates a gain map from aligned HDR and SDR renditions of the same scene:
    /// per-pixel log2 luminance boost, bounds fitted to the content, box-downsampled
    /// by <paramref name="downscale"/> and quantized to 8-bit gray. The HDR input is
    /// display-referred linear RGB with 1.0 = SDR white (the scRGB convention);
    /// the SDR input is ordinary sRGB-encoded 8-bit RGB. Both accept 3- or
    /// 4-channel interleaved layouts (alpha ignored) — <c>ToFloats()</c> /
    /// <c>ToRgba8()</c> output plugs in directly.
    /// <para>
    /// The tone mapping that produced the SDR rendition is the caller's policy
    /// (for astro masters: your stretch); this method only records its inverse.
    /// </para>
    /// </summary>
    public static GainMapComputeResult Compute(ReadOnlySpan<float> hdrLinearRgb, ReadOnlySpan<byte> sdrRgb8, int width, int height, int downscale = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(downscale);
        var pixelCount = width * height;
        var hdrChannels = ChannelsOf(hdrLinearRgb.Length, pixelCount, nameof(hdrLinearRgb));
        var sdrChannels = ChannelsOf(sdrRgb8.Length, pixelCount, nameof(sdrRgb8));

        const double offset = 1.0 / 64;
        var lut = SrgbTransfer.EotfLut8;

        // Pass 1: per-pixel log2 luminance boost (Rec.709 luma on linear light,
        // offsets keeping black finite), tracking the content's bounds.
        var logGain = new float[pixelCount];
        double logMin = double.MaxValue, logMax = double.MinValue;
        for (int p = 0, h = 0, s = 0; p < pixelCount; p++, h += hdrChannels, s += sdrChannels)
        {
            var sdrY = 0.2126 * lut[sdrRgb8[s]] + 0.7152 * lut[sdrRgb8[s + 1]] + 0.0722 * lut[sdrRgb8[s + 2]];
            var hdrY = 0.2126 * Math.Max(0, hdrLinearRgb[h])
                     + 0.7152 * Math.Max(0, hdrLinearRgb[h + 1])
                     + 0.0722 * Math.Max(0, hdrLinearRgb[h + 2]);
            var g = Math.Log2((hdrY + offset) / (sdrY + offset));
            logGain[p] = (float)g;
            if (g < logMin) logMin = g;
            if (g > logMax) logMax = g;
        }

        // Degenerate content (flat gain everywhere) still needs a non-empty code
        // range and a capacity ramp; a hair of slack costs nothing visually.
        if (logMax - logMin < 1e-9)
            logMax = logMin + 1e-9;
        var metadata = new GainMapMetadata
        {
            GainMapMin = Math.Pow(2, logMin),
            GainMapMax = Math.Pow(2, logMax),
            OffsetSdr = offset,
            OffsetHdr = offset,
            HdrCapacityMin = 1.0,
            // The headroom at which the full map applies = the content's max boost,
            // floored above capacity-min so the W ramp stays well-defined.
            HdrCapacityMax = Math.Max(Math.Pow(2, logMax), 1.0625),
        };

        // Pass 2: box-average each downscale×downscale block in log space
        // (geometric mean of gains), normalize to the fitted bounds, quantize.
        var mapWidth = (width + downscale - 1) / downscale;
        var mapHeight = (height + downscale - 1) / downscale;
        var map = new byte[mapWidth * mapHeight];
        var range = logMax - logMin;
        for (var my = 0; my < mapHeight; my++)
        {
            var yEnd = Math.Min((my + 1) * downscale, height);
            for (var mx = 0; mx < mapWidth; mx++)
            {
                var xEnd = Math.Min((mx + 1) * downscale, width);
                double sum = 0;
                var count = 0;
                for (var y = my * downscale; y < yEnd; y++)
                {
                    for (var x = mx * downscale; x < xEnd; x++)
                    {
                        sum += logGain[y * width + x];
                        count++;
                    }
                }
                var normalized = Math.Clamp((sum / count - logMin) / range, 0, 1);
                map[my * mapWidth + mx] = (byte)Math.Round(normalized * 255);
            }
        }

        return new GainMapComputeResult(map, mapWidth, mapHeight, metadata);
    }

    private static int ChannelsOf(int length, int pixelCount, string paramName)
    {
        if (length == pixelCount * 3) return 3;
        if (length == pixelCount * 4) return 4;
        throw new ArgumentException($"Expected width*height*3 or *4 interleaved samples, got {length} for {pixelCount} pixels.", paramName);
    }

    /// <summary>Where new APP segments go: after the APPn run that follows SOI —
    /// the same placement rule as <c>JpegIccInjector</c>.</summary>
    private static int FindInsertionPoint(ReadOnlySpan<byte> jpeg, string paramName)
    {
        List<JpegSegment> segments;
        try
        {
            segments = JpegSegmentScanner.Scan(jpeg);
        }
        catch (InvalidDataException e)
        {
            throw new ArgumentException($"Not a structurally valid JPEG: {e.Message}", paramName);
        }

        // An input that already carries gain-map structure - the base half of TrySplit, say, which
        // keeps its MPF index and GContainer XMP - would come out with two of each, and readers then
        // treat the file as a plain JPEG. Ordinary XMP (a camera's) is fine.
        foreach (var segment in segments)
        {
            var payload = segment.Payload(jpeg);
            if ((segment.Marker == 0xE2 && payload.StartsWith(MpfSegment.Identifier))
                || (segment.Marker == 0xE1 && payload.StartsWith(GainMapXmp.App1Identifier)
                    && (payload.IndexOf("http://ns.adobe.com/hdr-gain-map/1.0/"u8) >= 0
                        || payload.IndexOf("http://ns.google.com/photos/1.0/container/"u8) >= 0)))
            {
                throw new ArgumentException(
                    "The JPEG already carries gain-map metadata (an MPF index or hdrgm / GContainer XMP); " +
                    "pass a plain JPEG, e.g. one re-encoded from the decoded pixels.", paramName);
            }
        }

        for (var i = 1; i < segments.Count; i++)
        {
            if (segments[i].Marker is < 0xE0 or > 0xEF)
                return segments[i].SegmentOffset;
        }

        // Unreachable: Scan always terminates the list with SOS or EOI, neither APPn.
        throw new ArgumentException("No insertion point found.", paramName);
    }
}

/// <summary>The output of <see cref="JpegGainMap.Compute"/>: single-channel 8-bit
/// gain-map pixels (encode with any JPEG encoder, then <see cref="JpegGainMap.Assemble"/>)
/// plus the fitted reconstruction parameters.</summary>
public sealed record GainMapComputeResult(byte[] GainMapGray8, int Width, int Height, GainMapMetadata Metadata);
