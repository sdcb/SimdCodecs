using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Avif;

/// <summary>
/// Parsed ISO/IEC 21496-1 binary gain map metadata carried by an AVIF <c>tmap</c> derived item.
/// </summary>
internal sealed class AvifGainMapMetadata
{
    internal bool IsMultichannel { get; init; }
    internal bool UseBaseColourSpace { get; init; }
    internal double BaseHdrHeadroom { get; init; }
    internal double AlternateHdrHeadroom { get; init; }
    internal double[] GainMapMin { get; init; } = new double[3];
    internal double[] GainMapMax { get; init; } = new double[3];
    internal double[] Gamma { get; init; } = [1.0, 1.0, 1.0];
    internal double[] BaseOffset { get; init; } = new double[3];
    internal double[] AlternateOffset { get; init; } = new double[3];

    /// <summary>
    /// Parses ISO/IEC 21496-1 GainMapMetadata binary payload; returns null if unsupported or malformed.
    /// </summary>
    internal static AvifGainMapMetadata? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 62) return null;
        byte version = data[0];
        if (version != 0) return null;
        ushort minVersion = BinaryPrimitives.ReadUInt16BigEndian(data[1..]);
        if (minVersion != 0) return null;
        byte flags = data[5];
        bool isMc = (flags & 0x80) != 0;
        bool useBaseCs = (flags & 0x40) != 0;
        int channelCount = isMc ? 3 : 1;
        int needed = 22 + channelCount * 40;
        if (data.Length < needed) return null;

        if (!TryReadSignedRational(data[6..], out double baseHeadroom) ||
            !TryReadSignedRational(data[14..], out double altHeadroom))
            return null;

        var min = new double[3];
        var max = new double[3];
        var gamma = new double[3];
        var baseOff = new double[3];
        var altOff = new double[3];

        int pos = 22;
        for (int c = 0; c < channelCount; c++)
        {
            if (!TryReadSignedRational(data[pos..], out min[c]) ||
                !TryReadSignedRational(data[(pos + 8)..], out max[c]) ||
                !TryReadUnsignedRational(data[(pos + 16)..], out gamma[c]) ||
                !TryReadSignedRational(data[(pos + 24)..], out baseOff[c]) ||
                !TryReadSignedRational(data[(pos + 32)..], out altOff[c]) ||
                gamma[c] <= 0.0)
                return null;
            pos += 40;
        }
        if (!isMc)
        {
            min[1] = min[2] = min[0];
            max[1] = max[2] = max[0];
            gamma[1] = gamma[2] = gamma[0];
            baseOff[1] = baseOff[2] = baseOff[0];
            altOff[1] = altOff[2] = altOff[0];
        }

        return new AvifGainMapMetadata
        {
            IsMultichannel = isMc,
            UseBaseColourSpace = useBaseCs,
            BaseHdrHeadroom = baseHeadroom,
            AlternateHdrHeadroom = altHeadroom,
            GainMapMin = min,
            GainMapMax = max,
            Gamma = gamma,
            BaseOffset = baseOff,
            AlternateOffset = altOff,
        };
    }

    private static bool TryReadSignedRational(ReadOnlySpan<byte> s, out double value)
    {
        int num = BinaryPrimitives.ReadInt32BigEndian(s);
        uint den = BinaryPrimitives.ReadUInt32BigEndian(s[4..]);
        if (den == 0) { value = 0; return false; }
        value = (double)num / den;
        return double.IsFinite(value);
    }

    private static bool TryReadUnsignedRational(ReadOnlySpan<byte> s, out double value)
    {
        uint num = BinaryPrimitives.ReadUInt32BigEndian(s);
        uint den = BinaryPrimitives.ReadUInt32BigEndian(s[4..]);
        if (den == 0) { value = 0; return false; }
        value = (double)num / den;
        return double.IsFinite(value);
    }
}

/// <summary>
/// Dual-path <see cref="IDecodedImage"/> for an AVIF carrying an ISO/IEC 21496-1 <c>tmap</c> gain map:
/// <see cref="Pixels"/> and <see cref="ToFloats"/> lazily reconstruct linear HDR float32 RGB, while
/// <see cref="ToRgba8"/>, <see cref="ToRgba16"/> and <see cref="TryCopyTo"/> serve the SDR base rendition.
/// </summary>
internal sealed class AvifGainMapDecodedImage : IDecodedImage
{
    private static readonly double[] SrgbEotf8 = BuildSrgbEotf8();

    private readonly RasterImage _base;
    private readonly RasterImage _gainMap;
    private readonly AvifGainMapMetadata _meta;
    private RasterImage? _hdr;

    internal AvifGainMapDecodedImage(
        RasterImage baseImage,
        RasterImage gainMap,
        AvifGainMapMetadata metadata,
        ColorPrimaries primaries,
        ImageMetadata imageMetadata)
    {
        _base = baseImage;
        _gainMap = gainMap;
        _meta = metadata;
        ColorEncoding = new ColorEncoding
        {
            Primaries = primaries,
            Transfer = TransferFunction.Linear,
            Float = FloatSemantics.DisplayReferred,
        };
        Metadata = imageMetadata;
    }

    private RasterImage Hdr => _hdr ??= ReconstructHdr();

    public int Width => _base.Width;
    public int Height => _base.Height;
    public int Channels => 3;
    public SampleFormat SampleFormat => SampleFormat.Float32;
    public ReadOnlySpan<byte> Pixels => Hdr.Pixels;
    public ReadOnlySpan<byte> IccProfile => default;
    public ColorEncoding ColorEncoding { get; }
    public ImageMetadata Metadata { get; }

    public float[] ToFloats() => Hdr.ToFloats();
    public byte[] ToRgba8() => _base.ToRgba8();
    public ushort[] ToRgba16() => _base.ToRgba16();
    public bool TryCopyTo(Span<byte> destination, int stride, PixelFormat format) =>
        _base.TryCopyTo(destination, stride, format);

    public void Dispose()
    {
        _hdr?.Dispose();
        _gainMap.Dispose();
        _base.Dispose();
    }

    private RasterImage ReconstructHdr()
    {
        int w = _base.Width;
        int h = _base.Height;
        int baseCh = _base.Channels;
        bool base16 = _base.SampleFormat == SampleFormat.UInt16;
        ReadOnlySpan<byte> baseBytes = _base.Pixels;
        ReadOnlySpan<ushort> baseU16 = base16 ? MemoryMarshal.Cast<byte, ushort>(baseBytes) : default;

        int mapW = _gainMap.Width;
        int mapH = _gainMap.Height;
        int mapCh = _gainMap.Channels;
        bool map16 = _gainMap.SampleFormat == SampleFormat.UInt16;
        ReadOnlySpan<byte> mapBytes = _gainMap.Pixels;
        ReadOnlySpan<ushort> mapU16 = map16 ? MemoryMarshal.Cast<byte, ushort>(mapBytes) : default;
        int usableMapCh = mapCh >= 3 ? 3 : 1;

        var outBytes = new byte[checked((long)w * h * 3 * sizeof(float))];
        var output = MemoryMarshal.Cast<byte, float>(outBytes.AsSpan());

        // Full authored alternate headroom -> weight = 1.0 (ISO/IEC 21496-1 §6.4)
        double[] logMin = _meta.GainMapMin;
        double[] logMax = _meta.GainMapMax;
        double[] invGamma = [1.0 / _meta.Gamma[0], 1.0 / _meta.Gamma[1], 1.0 / _meta.Gamma[2]];
        double[] baseOff = _meta.BaseOffset;
        double[] altOff = _meta.AlternateOffset;
        double mapScale = map16 ? 1.0 / 65535.0 : 1.0 / 255.0;

        for (int y = 0; y < h; y++)
        {
            double v = (y + 0.5) * mapH / h - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(v), 0, mapH - 1);
            int y1 = Math.Min(y0 + 1, mapH - 1);
            double fy = Math.Clamp(v - y0, 0.0, 1.0);
            int row0 = y0 * mapW;
            int row1 = y1 * mapW;

            for (int x = 0; x < w; x++)
            {
                double u = (x + 0.5) * mapW / w - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(u), 0, mapW - 1);
                int x1 = Math.Min(x0 + 1, mapW - 1);
                double fx = Math.Clamp(u - x0, 0.0, 1.0);

                int baseIdx = (y * w + x) * baseCh;
                int outIdx = (y * w + x) * 3;
                for (int c = 0; c < 3; c++)
                {
                    int mc = c < usableMapCh ? c : 0;
                    double m00 = map16 ? mapU16[(row0 + x0) * mapCh + mc] : mapBytes[(row0 + x0) * mapCh + mc];
                    double m01 = map16 ? mapU16[(row0 + x1) * mapCh + mc] : mapBytes[(row0 + x1) * mapCh + mc];
                    double m10 = map16 ? mapU16[(row1 + x0) * mapCh + mc] : mapBytes[(row1 + x0) * mapCh + mc];
                    double m11 = map16 ? mapU16[(row1 + x1) * mapCh + mc] : mapBytes[(row1 + x1) * mapCh + mc];
                    double code = (m00 * (1.0 - fx) + m01 * fx) * (1.0 - fy) + (m10 * (1.0 - fx) + m11 * fx) * fy;

                    double recovery = Math.Clamp(code * mapScale, 0.0, 1.0);
                    if (invGamma[c] != 1.0)
                        recovery = Math.Pow(recovery, invGamma[c]);
                    double gain = Math.Pow(2.0, logMin[c] + (logMax[c] - logMin[c]) * recovery);

                    int bc = Math.Min(c, baseCh - 1);
                    double sdrLin = base16 ? SrgbEotf(baseU16[baseIdx + bc] / 65535.0) : SrgbEotf8[baseBytes[baseIdx + bc]];
                    output[outIdx + c] = (float)((sdrLin + baseOff[c]) * gain - altOff[c]);
                }
            }
        }

        return RasterImage.Wrap(w, h, 3, SampleFormat.Float32, outBytes, ColorEncoding);
    }

    private static double SrgbEotf(double v) =>
        v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static double[] BuildSrgbEotf8()
    {
        var lut = new double[256];
        for (int i = 0; i < 256; i++)
            lut[i] = SrgbEotf(i / 255.0);
        return lut;
    }
}
