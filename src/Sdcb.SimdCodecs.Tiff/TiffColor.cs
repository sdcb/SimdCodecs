using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// How the facade turns a page's samples, as <see cref="TiffReader"/> delivers them (chunky, host order, sub-byte
/// samples expanded, YCbCr data units expanded to one Y, Cb, Cr per pixel), into what <see cref="IDecodedImage"/>
/// holds: grey or RGB, plus straight alpha. Built once per page from its directory; converts row by row, so a whole
/// page and any rectangle of it go through exactly the same arithmetic.
/// </summary>
/// <remarks>
/// <para>The arithmetic below is this repository's definition (TIFF 6.0 leaves the rounding open) and is frozen:
/// changing any formula changes decoded pixels.</para>
/// <list type="bullet">
/// <item><b>Palette</b> (ColorMap, tag 320, TIFF 6.0 p.29): 16-bit entries, all reds then greens then blues. Indices
/// of 1/2/4/8 bits give 8-bit RGB with each entry reduced by <c>v &gt;&gt; 8</c> (exact for the usual <c>v8 * 257</c>
/// and <c>v8 * 256</c> encodings of an 8-bit colour); 16-bit indices give the 16-bit entries unchanged.</item>
/// <item><b>CMYK</b> (InkSet 1, TIFF 6.0 section 16; 0 = no ink): <c>R = (M - C)(M - K) / M</c> and likewise G from
/// M(agenta) and B from Y(ellow), with <c>M</c> = 255 or 65535, rounded to nearest: for 8 bits
/// <c>t = x*y + 128; (t + (t &gt;&gt; 8)) &gt;&gt; 8</c>, for 16 bits <c>(x*y + 32767) / 65535</c>, where
/// <c>x = M - C</c>, <c>y = M - K</c>. Both equal <c>round(x*y/M)</c> exactly.</item>
/// <item><b>YCbCr</b> (non-JPEG pages; TIFF 6.0 sections 20 and 21), 8-bit: each code is expanded by
/// ReferenceBlackWhite, <c>Y' = (Y - B0) * 255 / (W0 - B0)</c>, <c>Cb' = (Cb - B1) * 127 / (W1 - B1)</c>,
/// <c>Cr' = (Cr - B2) * 127 / (W2 - B2)</c> (the spec's CodingRange is 255 for Y and 127 for chroma), then
/// <c>R = Cr' * (2 - 2*LumaRed) + Y'</c>, <c>B = Cb' * (2 - 2*LumaBlue) + Y'</c>,
/// <c>G = (Y' - LumaBlue*B - LumaRed*R) / LumaGreen</c>, evaluated in double precision in exactly that order (no fused
/// multiply-add), and each result is <c>floor(x + 0.5)</c> clamped to 0..255. YCbCrCoefficients default to
/// 299/587/114 (CCIR 601-1). The spec's ReferenceBlackWhite default (0, 255 for every component) is, as it says itself,
/// inappropriate for YCbCr; an absent tag means its "no headroom/footroom" values 0, 255, 128, 255, 128, 255. Chroma is
/// replicated over its data unit by the reader (no interpolation; YCbCrPositioning is not used).</item>
/// <item><b>Associated alpha</b> (ExtraSamples 1, TIFF 6.0 section 21's matting text: colour stored premultiplied, and
/// "if A is zero, then the color components should be interpreted as zero"): un-premultiplied to straight alpha,
/// <c>c = min(M, (c*M + a/2) / a)</c> in integers (<c>c = 0</c> when <c>a = 0</c>), <c>c / a</c> for float samples.
/// For grey and RGB only; a palette page may carry unassociated alpha beside its index.</item>
/// <item><b>ExtraSamples</b> (338): the first extra sample marked associated (1) or unassociated (2) alpha becomes the
/// alpha channel; samples marked 0 ("unspecified data", not opacity) and every further extra sample are dropped. A
/// page with extra samples but no ExtraSamples tag (not conforming) keeps its first extra sample as straight alpha,
/// which is what this adapter has always done for RGBA and grey + alpha files written without the tag.</item>
/// <item><b>MinIsWhite</b> grey is inverted (<c>~v</c>) to MinIsBlack.</item>
/// </list>
/// </remarks>
internal sealed class TiffColor
{
    private enum Kind { Gray, Rgb, Palette, Cmyk, YCbCr }

    private readonly Kind _kind;
    private readonly int _inChannels;
    private readonly int _inBytes;
    private readonly bool _inHalf;
    private readonly bool _invert;
    private readonly int _alpha = -1;
    private readonly bool _premultiplied;
    private readonly ushort[]? _map;
    private readonly int _mapEntries;
    private readonly double[]? _yTable, _cbTable, _crTable;
    private readonly double _kr, _kb, _lr, _lg, _lb;

    private TiffColor(Kind kind, int inChannels, int inBytes, bool inHalf, int outChannels, SampleFormat outFormat)
    {
        _kind = kind;
        _inChannels = inChannels;
        _inBytes = inBytes;
        _inHalf = inHalf;
        OutChannels = outChannels;
        OutFormat = outFormat;
    }

    private TiffColor(TiffColor basis, int alpha, bool premultiplied, bool invert, ushort[]? map, int mapEntries,
        double[]? y, double[]? cb, double[]? cr, double[]? luma)
        : this(basis._kind, basis._inChannels, basis._inBytes, basis._inHalf, basis.OutChannels + (alpha >= 0 ? 1 : 0), basis.OutFormat)
    {
        _alpha = alpha;
        _premultiplied = premultiplied;
        _invert = invert;
        _map = map;
        _mapEntries = mapEntries;
        (_yTable, _cbTable, _crTable) = (y, cb, cr);
        if (luma is not null)
        {
            (_lr, _lg, _lb) = (luma[0], luma[1], luma[2]);
            _kr = 2 - (2 * _lr);
            _kb = 2 - (2 * _lb);
        }
    }

    /// <summary>Channels of the output: 1 grey, 2 grey + alpha, 3 RGB, 4 RGBA.</summary>
    public int OutChannels { get; }

    public SampleFormat OutFormat { get; }

    /// <summary>The output is the input, byte for byte: the page's pixels can be used as they are.</summary>
    public bool IsIdentity => _kind is Kind.Gray or Kind.Rgb && !_invert && !_premultiplied && !_inHalf
        && OutChannels == _inChannels && (_alpha < 0 || _alpha == _inChannels - 1);

    public int InBytesPerPixel => _inChannels * _inBytes;

    public int OutBytesPerPixel => OutChannels * OutFormat switch { SampleFormat.UInt8 => 1, SampleFormat.UInt16 => 2, _ => 4 };

    /// <summary>The plan for a page, or false for one the facade refuses (CIELab, signed or 32-bit integer samples, ...).</summary>
    public static bool TryCreate(TiffPageLayout layout, out TiffColor plan) =>
        TryCreate(layout.Photometric, layout.SamplesPerPixel, layout.BitsPerSample, layout.StoredBitsPerSample,
            layout.SampleFormat, layout.Ifd.Color, out plan);

    internal static bool TryCreate(TiffPhotometric photometric, int samplesPerPixel, int bitsPerSample, int storedBits,
        TiffSampleFormat sampleFormat, TiffColorTags tags, out TiffColor plan)
    {
        plan = null!;
        var (kind, baseChannels) = photometric switch
        {
            TiffPhotometric.MinIsBlack or TiffPhotometric.MinIsWhite => (Kind.Gray, 1),
            TiffPhotometric.Rgb => (Kind.Rgb, 3),
            TiffPhotometric.Palette => (Kind.Palette, 1),
            TiffPhotometric.Cmyk => (Kind.Cmyk, 4),
            TiffPhotometric.YCbCr => (Kind.YCbCr, 3),
            _ => ((Kind)(-1), 0),
        };
        if (baseChannels == 0 || samplesPerPixel < baseChannels) return false;

        SampleFormat outFormat;
        var half = false;
        switch (bitsPerSample, sampleFormat)
        {
            case (8, TiffSampleFormat.Uint): outFormat = SampleFormat.UInt8; break;
            case (16, TiffSampleFormat.Uint): outFormat = SampleFormat.UInt16; break;
            case (16, TiffSampleFormat.IeeeFloat): outFormat = SampleFormat.Float32; half = true; break;
            case (32, TiffSampleFormat.IeeeFloat): outFormat = SampleFormat.Float32; break;
            default: return false; // signed integers, 32-bit unsigned
        }
        var isFloat = sampleFormat == TiffSampleFormat.IeeeFloat;
        // Colour transforms and inversion are defined here on unsigned integer samples only.
        if (isFloat && kind is not (Kind.Gray or Kind.Rgb)) return false;
        if (photometric == TiffPhotometric.MinIsWhite && isFloat) return false;
        if (kind == Kind.Cmyk && tags.InkSet != 1) return false;
        if (kind == Kind.YCbCr && bitsPerSample != 8) return false;

        // Alpha: the first extra sample marked associated or unassociated (or, without the tag, the first extra).
        var alpha = -1;
        var premultiplied = false;
        var extras = samplesPerPixel - baseChannels;
        if (extras > 0)
        {
            if (tags.ExtraSamples is null)
            {
                alpha = baseChannels;
            }
            else
            {
                for (var k = 0; k < extras && k < tags.ExtraSamples.Length; k++)
                {
                    if (tags.ExtraSamples[k] is 1 or 2)
                    {
                        alpha = baseChannels + k;
                        premultiplied = tags.ExtraSamples[k] == 1;
                        break;
                    }
                }
            }
        }
        // Premultiplied colour is defined (and un-premultiplied here) for grey and RGB samples only; a palette's would be
        // premultiplied colour-map entries (TIFF 6.0 p.78), CMYK's and YCbCr's have no defined meaning.
        // Premultiplied colour is defined (and un-premultiplied here) for grey and RGB samples only: a palette's would be
        // premultiplied colour-map entries (TIFF 6.0 p.78), and CMYK's and YCbCr's have no defined meaning.
        if (premultiplied && kind is not (Kind.Gray or Kind.Rgb)) return false;

        ushort[]? map = null;
        var mapEntries = 0;
        if (kind == Kind.Palette)
        {
            mapEntries = 1 << storedBits;
            if (tags.ColorMap is null || tags.ColorMap.Length != 3 * mapEntries) return false;
            map = tags.ColorMap;
            if (storedBits < 16) outFormat = SampleFormat.UInt8;
        }

        double[]? yt = null, cbt = null, crt = null, luma = null;
        if (kind == Kind.YCbCr)
        {
            var rbw = tags.ReferenceBlackWhite ?? [0, 255, 128, 255, 128, 255];
            if (rbw[1] == rbw[0] || rbw[3] == rbw[2] || rbw[5] == rbw[4]) return false;
            yt = new double[256];
            cbt = new double[256];
            crt = new double[256];
            for (var code = 0; code < 256; code++)
            {
                yt[code] = (code - rbw[0]) * 255 / (rbw[1] - rbw[0]);
                cbt[code] = (code - rbw[2]) * 127 / (rbw[3] - rbw[2]);
                crt[code] = (code - rbw[4]) * 127 / (rbw[5] - rbw[4]);
            }
            luma = tags.Coefficients;
        }

        var outChannels = kind == Kind.Gray ? 1 : 3;
        var basis = new TiffColor(kind, samplesPerPixel, bitsPerSample / 8, half, outChannels, outFormat);
        plan = new TiffColor(basis, alpha, premultiplied, photometric == TiffPhotometric.MinIsWhite, map, mapEntries,
            yt, cbt, crt, luma);
        return true;
    }

    /// <summary>Converts <paramref name="pixels"/> pixels from <paramref name="source"/> into <paramref name="destination"/>.</summary>
    public void ConvertRow(ReadOnlySpan<byte> source, Span<byte> destination, int pixels)
    {
        switch (OutFormat)
        {
            // TryCreate pairs 8-bit output with 8-bit samples (palette indices included) and 16-bit with 16-bit.
            case SampleFormat.UInt8:
                Row8(source, destination, pixels);
                break;
            case SampleFormat.UInt16:
                Row16(MemoryMarshal.Cast<byte, ushort>(source), MemoryMarshal.Cast<byte, ushort>(destination), pixels);
                break;
            default:
                RowFloat(source, MemoryMarshal.Cast<byte, float>(destination), pixels);
                break;
        }
    }

    private void Row8(ReadOnlySpan<byte> src, Span<byte> dst, int pixels)
    {
        var n = _inChannels;
        var o = OutChannels;
        for (int p = 0, s = 0, d = 0; p < pixels; p++, s += n, d += o)
        {
            switch (_kind)
            {
                case Kind.Gray:
                    dst[d] = _invert ? (byte)~src[s] : src[s];
                    break;
                case Kind.Rgb:
                    dst[d] = src[s];
                    dst[d + 1] = src[s + 1];
                    dst[d + 2] = src[s + 2];
                    break;
                case Kind.Palette:
                {
                    var i = src[s];
                    dst[d] = (byte)(_map![i] >> 8);
                    dst[d + 1] = (byte)(_map[_mapEntries + i] >> 8);
                    dst[d + 2] = (byte)(_map[(2 * _mapEntries) + i] >> 8);
                    break;
                }
                case Kind.Cmyk:
                {
                    var k = 255 - src[s + 3];
                    dst[d] = Mul8(255 - src[s], k);
                    dst[d + 1] = Mul8(255 - src[s + 1], k);
                    dst[d + 2] = Mul8(255 - src[s + 2], k);
                    break;
                }
                default:
                    YccPixel(src[s], src[s + 1], src[s + 2], dst.Slice(d, 3));
                    break;
            }
            if (_alpha >= 0)
            {
                var a = src[s + _alpha];
                dst[d + o - 1] = a;
                if (_premultiplied)
                {
                    for (var c = 0; c < o - 1; c++)
                        dst[d + c] = a == 0 ? (byte)0 : (byte)Math.Min(255, ((dst[d + c] * 255) + (a / 2)) / a);
                }
            }
        }
    }

    private void Row16(ReadOnlySpan<ushort> src, Span<ushort> dst, int pixels)
    {
        var n = _inChannels;
        var o = OutChannels;
        for (int p = 0, s = 0, d = 0; p < pixels; p++, s += n, d += o)
        {
            switch (_kind)
            {
                case Kind.Gray:
                    dst[d] = _invert ? (ushort)~src[s] : src[s];
                    break;
                case Kind.Rgb:
                    dst[d] = src[s];
                    dst[d + 1] = src[s + 1];
                    dst[d + 2] = src[s + 2];
                    break;
                case Kind.Palette:
                {
                    var i = src[s];
                    dst[d] = _map![i];
                    dst[d + 1] = _map[_mapEntries + i];
                    dst[d + 2] = _map[(2 * _mapEntries) + i];
                    break;
                }
                default: // CMYK
                {
                    var k = (uint)(65535 - src[s + 3]);
                    dst[d] = Mul16(65535u - src[s], k);
                    dst[d + 1] = Mul16(65535u - src[s + 1], k);
                    dst[d + 2] = Mul16(65535u - src[s + 2], k);
                    break;
                }
            }
            if (_alpha >= 0)
            {
                var a = src[s + _alpha];
                dst[d + o - 1] = a;
                if (_premultiplied)
                {
                    for (var c = 0; c < o - 1; c++)
                        dst[d + c] = a == 0 ? (ushort)0 : (ushort)Math.Min(65535u, ((dst[d + c] * 65535u) + (a / 2u)) / a);
                }
            }
        }
    }

    private void RowFloat(ReadOnlySpan<byte> source, Span<float> dst, int pixels)
    {
        var n = _inChannels;
        var o = OutChannels;
        var colour = o - (_alpha >= 0 ? 1 : 0);
        var halves = MemoryMarshal.Cast<byte, Half>(source);
        var singles = MemoryMarshal.Cast<byte, float>(source);
        for (int p = 0, s = 0, d = 0; p < pixels; p++, s += n, d += o)
        {
            for (var c = 0; c < colour; c++)
                dst[d + c] = _inHalf ? (float)halves[s + c] : singles[s + c];
            if (_alpha < 0) continue;
            var a = _inHalf ? (float)halves[s + _alpha] : singles[s + _alpha];
            dst[d + o - 1] = a;
            if (_premultiplied)
            {
                for (var c = 0; c < colour; c++)
                    dst[d + c] = a > 0 ? dst[d + c] / a : 0;
            }
        }
    }

    /// <summary>The frozen YCbCr to RGB conversion (see the type remarks).</summary>
    private void YccPixel(byte y, byte cb, byte cr, Span<byte> rgb)
    {
        var yv = _yTable![y];
        var r = (_crTable![cr] * _kr) + yv;
        var b = (_cbTable![cb] * _kb) + yv;
        var g = (yv - (_lb * b) - (_lr * r)) / _lg;
        rgb[0] = Clamp8(r);
        rgb[1] = Clamp8(g);
        rgb[2] = Clamp8(b);
    }

    private static byte Clamp8(double v)
    {
        var rounded = Math.Floor(v + 0.5);
        return rounded <= 0 ? (byte)0 : rounded >= 255 ? (byte)255 : (byte)rounded;
    }

    /// <summary>round(x * y / 255) for x, y in 0..255.</summary>
    internal static byte Mul8(int x, int y)
    {
        var t = (x * y) + 128;
        return (byte)((t + (t >> 8)) >> 8);
    }

    /// <summary>round(x * y / 65535) for x, y in 0..65535; the sum stays below 2^32.</summary>
    internal static ushort Mul16(uint x, uint y) => (ushort)(((x * y) + 32767u) / 65535u);
}
