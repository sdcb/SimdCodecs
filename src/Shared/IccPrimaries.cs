using System;
using System.Buffers.Binary;
using Sdcb.SimdCodecs.Abstractions;

// Compiled into each package that reads an ICC profile (see src/Shared/MqDecoder.cs for why it is shared as
// source), under that package's namespace so the copies are distinct types.
#if SIMDCODECS_JPEG
namespace Sdcb.SimdCodecs.Jpeg;
#elif SIMDCODECS_PNG
namespace Sdcb.SimdCodecs.Png;
#elif SIMDCODECS_WEBP
namespace Sdcb.SimdCodecs.Webp;
#elif SIMDCODECS_HEIF
namespace Sdcb.SimdCodecs.Heif;
#elif SIMDCODECS_TIFF
namespace Sdcb.SimdCodecs.Tiff;
#elif SIMDCODECS_JPEG2000
namespace Sdcb.SimdCodecs.Jpeg2000;
#elif SIMDCODECS_BMP
namespace Sdcb.SimdCodecs.Bmp;
#elif SIMDCODECS_COLOR_ICC
namespace Sdcb.SimdCodecs.Color.Icc;
#else
#error IccPrimaries.cs is shared source for the Jpeg, Png, Webp, Heif, Tiff, Jpeg2000, Bmp and Color.Icc packages only.
#endif

/// <summary>
/// The colour primaries an ICC profile declares (<c>IccProfiles.TryGetPrimaries</c> in Sdcb.SimdCodecs.Color.Icc
/// delegates here) and the <see cref="ColorEncoding"/> a decoder reports for an image whose only colour
/// signalling is that profile. Shared as source so decoders need not depend on the Color.Icc package.
/// </summary>
internal static class IccPrimaries
{
    /// <summary>
    /// The encoding for an image with no signalling of its own and this ICC profile: sRGB with the profile's
    /// primaries when they are BT.709 / Display P3 / BT.2020, null when the profile is empty or unrecognised
    /// (the caller keeps its default). Transfer and range are untouched: only the primaries are reported.
    /// </summary>
    public static ColorEncoding? Encoding(ReadOnlySpan<byte> profile)
    {
        if (profile.IsEmpty || !TryGetPrimaries(profile, out var primaries))
            return null;
        return primaries switch
        {
            ColorPrimaries.BT709 => ColorEncoding.AssumedSrgb,
            ColorPrimaries.DisplayP3 or ColorPrimaries.BT2020 => ColorEncoding.AssumedSrgb with { Primaries = primaries },
            _ => null,
        };
    }

    /// <summary>
    /// The H.273 colour primaries an RGB profile declares, when H.273 names them: a <c>cicp</c> tag (ICC v4.4)
    /// as it stands, otherwise the matrix colorants (<c>rXYZ</c>, <c>gXYZ</c>, <c>bXYZ</c>) compared with each
    /// standard's primaries and D65 white adapted to the D50 connection space by the Bradford transform (ICC.1
    /// Annex E), within 0.004 per component.
    /// </summary>
    public static bool TryGetPrimaries(ReadOnlySpan<byte> profile, out ColorPrimaries primaries)
    {
        primaries = ColorPrimaries.Unspecified;
        if (profile.Length < 132 || !profile.Slice(16, 4).SequenceEqual("RGB "u8))
            return false;

        // cicp: type, reserved, then primaries, transfer, matrix and range, one byte each.
        if (TryFindTag(profile, "cicp"u8, out var cicp) && cicp.Length >= 12 && cicp.StartsWith("cicp"u8))
        {
            primaries = (ColorPrimaries)cicp[8];
            return primaries != ColorPrimaries.Unspecified;
        }

        Span<double> colorants = stackalloc double[9];
        if (!TryReadXyz(profile, "rXYZ"u8, colorants[..3]) || !TryReadXyz(profile, "gXYZ"u8, colorants.Slice(3, 3))
            || !TryReadXyz(profile, "bXYZ"u8, colorants.Slice(6, 3)))
            return false;

        foreach (var (candidate, reference) in KnownColorants.Value)
        {
            var match = true;
            for (var i = 0; i < 9 && match; i++)
                match = Math.Abs(colorants[i] - reference[i]) <= 0.004;
            if (match)
            {
                primaries = candidate;
                return true;
            }
        }

        return false;
    }

    // The first tag table entry with this signature, bounds-checked against the profile: header
    // (128 bytes), the tag count, then 12-byte entries of signature, offset and size.
    internal static bool TryFindTag(ReadOnlySpan<byte> profile, ReadOnlySpan<byte> signature, out ReadOnlySpan<byte> tag)
    {
        tag = default;
        if (profile.Length < 132)
            return false;
        var count = BinaryPrimitives.ReadUInt32BigEndian(profile[128..]);
        for (long i = 0, entry = 132; i < count && entry + 12 <= profile.Length; i++, entry += 12)
        {
            var e = profile.Slice((int)entry, 12);
            if (!e[..4].SequenceEqual(signature))
                continue;
            var offset = BinaryPrimitives.ReadUInt32BigEndian(e[4..]);
            var size = BinaryPrimitives.ReadUInt32BigEndian(e[8..]);
            if (offset > (uint)profile.Length || size > (uint)profile.Length - offset)
                return false;
            tag = profile.Slice((int)offset, (int)size);
            return true;
        }

        return false;
    }

    // XYZType: 'XYZ ', reserved, then X, Y, Z as s15Fixed16Number.
    private static bool TryReadXyz(ReadOnlySpan<byte> profile, ReadOnlySpan<byte> signature, Span<double> xyz)
    {
        if (!TryFindTag(profile, signature, out var tag) || tag.Length < 20 || !tag.StartsWith("XYZ "u8))
            return false;
        for (var i = 0; i < 3; i++)
            xyz[i] = BinaryPrimitives.ReadInt32BigEndian(tag[(8 + 4 * i)..]) / 65536.0;
        return true;
    }

    // Each standard's colorants as an ICC profile stores them: the RGB-to-XYZ columns for its
    // primaries and D65 white, Bradford-adapted to the D50 PCS white (0.9642, 1, 0.8249).
    private static readonly Lazy<(ColorPrimaries, double[])[]> KnownColorants = new(() =>
    [
        (ColorPrimaries.BT709, Colorants(0.640, 0.330, 0.300, 0.600, 0.150, 0.060)),
        (ColorPrimaries.DisplayP3, Colorants(0.680, 0.320, 0.265, 0.690, 0.150, 0.060)),
        (ColorPrimaries.BT2020, Colorants(0.708, 0.292, 0.170, 0.797, 0.131, 0.046)),
    ]);

    private static double[] Colorants(double rx, double ry, double gx, double gy, double bx, double by)
    {
        // RGB-to-XYZ: the primaries' XYZ (Y = 1) scaled so that RGB (1, 1, 1) is the D65 white.
        double[] white = [0.3127 / 0.3290, 1, (1 - 0.3127 - 0.3290) / 0.3290];
        double[,] p =
        {
            { rx / ry, gx / gy, bx / by },
            { 1, 1, 1 },
            { (1 - rx - ry) / ry, (1 - gx - gy) / gy, (1 - bx - by) / by },
        };
        var s = Solve(p, white);
        var rgbToXyz = new double[3, 3];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                rgbToXyz[r, c] = p[r, c] * s[c];

        // Bradford: to cone space, scale D65 to D50, back.
        double[,] m = { { 0.8951, 0.2664, -0.1614 }, { -0.7502, 1.7135, 0.0367 }, { 0.0389, -0.0685, 1.0296 } };
        double[] d50 = [0.9642, 1, 0.8249];
        var src = Multiply(m, white);
        var dst = Multiply(m, d50);
        var adapt = new double[3, 3];
        var inverse = Invert(m);
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                for (var k = 0; k < 3; k++)
                    adapt[r, c] += inverse[r, k] * (dst[k] / src[k]) * m[k, c];

        var result = new double[9];
        for (var column = 0; column < 3; column++)
            for (var row = 0; row < 3; row++)
                for (var k = 0; k < 3; k++)
                    result[column * 3 + row] += adapt[row, k] * rgbToXyz[k, column];
        return result;
    }

    private static double[] Multiply(double[,] m, double[] v) =>
        [m[0, 0] * v[0] + m[0, 1] * v[1] + m[0, 2] * v[2],
         m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2],
         m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2]];

    private static double[] Solve(double[,] m, double[] v) => Multiply(Invert(m), v);

    private static double[,] Invert(double[,] m)
    {
        var det = m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
                - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
                + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        var r = new double[3, 3];
        r[0, 0] = (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) / det;
        r[0, 1] = (m[0, 2] * m[2, 1] - m[0, 1] * m[2, 2]) / det;
        r[0, 2] = (m[0, 1] * m[1, 2] - m[0, 2] * m[1, 1]) / det;
        r[1, 0] = (m[1, 2] * m[2, 0] - m[1, 0] * m[2, 2]) / det;
        r[1, 1] = (m[0, 0] * m[2, 2] - m[0, 2] * m[2, 0]) / det;
        r[1, 2] = (m[0, 2] * m[1, 0] - m[0, 0] * m[1, 2]) / det;
        r[2, 0] = (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]) / det;
        r[2, 1] = (m[0, 1] * m[2, 0] - m[0, 0] * m[2, 1]) / det;
        r[2, 2] = (m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0]) / det;
        return r;
    }
}
