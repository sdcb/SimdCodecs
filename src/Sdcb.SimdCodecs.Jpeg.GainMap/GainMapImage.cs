using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// A decoded gain-map JPEG: the authored SDR base rendition, the (typically
/// quarter-scale) gain map, and the scalar reconstruction parameters.
/// <see cref="ReconstructHdr"/> turns the pair into HDR linear floats for any
/// display headroom — the display-adaptive dial that makes the format useful.
/// </summary>
public sealed class GainMapImage
{
    /// <summary>The SDR base rendition — ordinary display-referred sRGB, 8-bit.</summary>
    public RasterImage Base { get; }

    /// <summary>The gain map raster, 8-bit; single-channel maps decoded through a JPEG
    /// codec surface as RGB with identical channels, which the math handles uniformly.</summary>
    public RasterImage GainMap { get; }

    /// <summary>The scalar hdrgm parameters describing how to undo the tone mapping.</summary>
    public GainMapMetadata Metadata { get; }

    public GainMapImage(RasterImage baseImage, RasterImage gainMap, GainMapMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(baseImage);
        ArgumentNullException.ThrowIfNull(gainMap);
        ArgumentNullException.ThrowIfNull(metadata);
        if (baseImage.SampleFormat != SampleFormat.UInt8 || gainMap.SampleFormat != SampleFormat.UInt8)
            throw new ArgumentException("Gain-map reconstruction is defined on 8-bit base and map rasters.");
        metadata.Validate();
        Base = baseImage;
        GainMap = gainMap;
        Metadata = metadata;
    }

    /// <summary>
    /// Reconstructs HDR linear RGB (<see cref="SampleFormat.Float32"/>, 3 channels,
    /// <see cref="ColorEncoding"/> = Linear + DisplayReferred, 1.0 = SDR white) for a
    /// display with <paramref name="displayHeadroom"/> × SDR-white peak luminance.
    /// Headroom 1.0 (or anything ≤ <see cref="GainMapMetadata.HdrCapacityMin"/>)
    /// reproduces the linearized base exactly; ≥ <see cref="GainMapMetadata.HdrCapacityMax"/>
    /// applies the full authored gain; in between interpolates in log space.
    /// The gain map is bilinearly upsampled to the base's resolution.
    /// </summary>
    public RasterImage ReconstructHdr(double displayHeadroom)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(displayHeadroom);
        if (Metadata.BaseRenditionIsHdr)
            throw new NotSupportedException("BaseRenditionIsHDR=true (HDR-base) files are not supported; only the SDR-base form is implemented.");

        var width = Base.Width;
        var height = Base.Height;
        var weight = Weight(displayHeadroom);
        var lut = SrgbTransfer.EotfLut8;
        var basePixels = Base.Pixels;
        var baseChannels = Base.Channels;
        var baseG = Math.Min(1, baseChannels - 1);
        var baseB = Math.Min(2, baseChannels - 1);

        var outBytes = new byte[width * height * 3 * sizeof(float)];
        var output = MemoryMarshal.Cast<byte, float>(outBytes.AsSpan());
        var encoding = new ColorEncoding { Transfer = TransferFunction.Linear, Float = FloatSemantics.DisplayReferred };

        if (weight == 0)
        {
            // Fast path, and the format's exactness promise: at SDR headroom the
            // result IS the base, just linearized — no +offset−offset float noise.
            for (int i = 0, o = 0; o < output.Length; i += baseChannels, o += 3)
            {
                output[o] = (float)lut[basePixels[i]];
                output[o + 1] = (float)lut[basePixels[i + baseG]];
                output[o + 2] = (float)lut[basePixels[i + baseB]];
            }
            return new RasterImage(width, height, 3, SampleFormat.Float32, outBytes, null, encoding);
        }

        var logMin = Math.Log2(Metadata.GainMapMin);
        var logMax = Math.Log2(Metadata.GainMapMax);
        var invGamma = 1.0 / Metadata.Gamma;
        var offsetSdr = Metadata.OffsetSdr;
        var offsetHdr = Metadata.OffsetHdr;

        var map = GainMap.Pixels;
        var mapChannels = GainMap.Channels;
        var mapWidth = GainMap.Width;
        var mapHeight = GainMap.Height;
        // Per-channel gains when the map carries RGB; a gray map (or gray-decoded-
        // to-RGB) collapses to the same value for all three.
        var usableMapChannels = mapChannels >= 3 ? 3 : 1;

        for (var y = 0; y < height; y++)
        {
            // Center-aligned bilinear map coordinates (the resampling convention
            // Skia/libultrahdr use when scaling the quarter-size map back up).
            var v = (y + 0.5) * mapHeight / height - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(v), 0, mapHeight - 1);
            var y1 = Math.Min(y0 + 1, mapHeight - 1);
            var fy = Math.Clamp(v - y0, 0, 1);

            for (var x = 0; x < width; x++)
            {
                var u = (x + 0.5) * mapWidth / width - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(u), 0, mapWidth - 1);
                var x1 = Math.Min(x0 + 1, mapWidth - 1);
                var fx = Math.Clamp(u - x0, 0, 1);

                var row0 = y0 * mapWidth;
                var row1 = y1 * mapWidth;
                var baseIndex = (y * width + x) * baseChannels;
                var outIndex = (y * width + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var mc = c < usableMapChannels ? c : 0;
                    var code = (map[(row0 + x0) * mapChannels + mc] * (1 - fx) + map[(row0 + x1) * mapChannels + mc] * fx) * (1 - fy)
                             + (map[(row1 + x0) * mapChannels + mc] * (1 - fx) + map[(row1 + x1) * mapChannels + mc] * fx) * fy;

                    var recovery = code / 255.0;
                    if (invGamma != 1.0)
                        recovery = Math.Pow(recovery, invGamma);
                    var gain = Math.Pow(2, (logMin + (logMax - logMin) * recovery) * weight);

                    var sdrLinear = lut[basePixels[baseIndex + Math.Min(c, baseChannels - 1)]];
                    output[outIndex + c] = (float)((sdrLinear + offsetSdr) * gain - offsetHdr);
                }
            }
        }

        return new RasterImage(width, height, 3, SampleFormat.Float32, outBytes, null, encoding);
    }

    /// <summary>The gain-application weight W ∈ [0,1] for a display headroom — the
    /// log-space position between <see cref="GainMapMetadata.HdrCapacityMin"/> and
    /// <see cref="GainMapMetadata.HdrCapacityMax"/>.</summary>
    public double Weight(double displayHeadroom)
    {
        var logMin = Math.Log2(Metadata.HdrCapacityMin);
        var logMax = Math.Log2(Metadata.HdrCapacityMax);
        return Math.Clamp((Math.Log2(displayHeadroom) - logMin) / (logMax - logMin), 0, 1);
    }
}
