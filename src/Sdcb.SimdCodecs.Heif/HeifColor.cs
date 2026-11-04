using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Sdcb.SimdCodecs.Heif;

/// <summary>
/// Y′CbCr → RGBA8 conversion for decoded HEVC images. H.265 defines the decoded samples, not this step, so
/// it is defined here and frozen (like <c>JpegColor</c>): any change to the arithmetic is an output change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Matrix.</b> <c>matrix_coefficients</c> (H.273 / H.265 Table E.5) selects Kr and Kb: 1 → BT.709,
/// 4 → FCC, 5 / 6 → BT.601, 7 → SMPTE 240M, 9 / 10 → BT.2020 (10, constant luminance, is approximated
/// by the non-constant-luminance matrix), 0 → identity (G = Y, B = Cb, R = Cr); anything else, including
/// 2 (unspecified), → BT.601. Then R = Y + 2(1 − Kr)·Cr, B = Y + 2(1 − Kb)·Cb,
/// G = Y − 2Kb(1 − Kb)/Kg·Cb − 2Kr(1 − Kr)/Kg·Cr, with Kg = 1 − Kr − Kb.
/// </para>
/// <para>
/// <b>Range and bit depth.</b> Full range: Y and C span 0..2^b − 1, C centred at 2^(b−1), scaled by
/// 255 / (2^b − 1). Limited range: Y spans 16·2^(b−8) .. 235·2^(b−8) and C 16·2^(b−8) .. 240·2^(b−8)
/// (centred at 128·2^(b−8)), scaled by 255 / (219·2^(b−8)) and 255 / (224·2^(b−8)). That maps 10-bit
/// samples straight to 8-bit output, with no separate 10 → 8 bit step.
/// </para>
/// <para>
/// <b>Arithmetic.</b> Each output is (Σ coefficient·(sample − offset) + 2^15) &gt;&gt; 16, clamped to 0..255,
/// with every coefficient round(value · 2^16) computed once in double precision from the constants above.
/// Integer-only per pixel, so the result does not depend on the ISA.
/// </para>
/// <para>
/// <b>Chroma.</b> 4:2:0 chroma is replicated (nearest neighbour): chroma sample (x / 2, y / 2) serves
/// luma sample (x, y). It needs no samples outside a grid tile, so tiles convert independently.
/// </para>
/// <para>
/// <b>Grey (<see cref="ToGray"/>).</b> The 8-bit grey output is the stored luma, range-mapped like the RGB
/// path's Y term: gray = clamp((Y·(Y′ − YOffset) + 2^15) &gt;&gt; 16, 0, 255), with the same Y coefficient and
/// offset as above (limited → full range and b → 8 bits in one step). It is therefore exactly the R, G and B
/// a pixel with neutral chroma (Cb = Cr = 2^(b−1)) converts to. Chroma takes no part. For the identity matrix
/// (0) the Y plane holds G, not luma, so this step does not apply.
/// </para>
/// </remarks>
internal static class HeifColor
{
    internal readonly record struct Coefficients(
        int YOffset, int COffset, int Y, int CrR, int CbG, int CrG, int CbB, bool Identity);

    internal static Coefficients For(int matrixCoefficients, bool fullRange, int bitDepth)
    {
        var scale = 1 << (bitDepth - 8);
        double yMul, cMul;
        int yOffset;
        if (fullRange)
        {
            yMul = cMul = 255.0 / ((1 << bitDepth) - 1);
            yOffset = 0;
        }
        else
        {
            yMul = 255.0 / (219 * scale);
            cMul = 255.0 / (224 * scale);
            yOffset = 16 * scale;
        }
        var cOffset = 1 << (bitDepth - 1);

        if (matrixCoefficients == 0)
        {
            // Identity (GBR): every channel scaled like luma.
            var f = Fixed(yMul);
            return new Coefficients(yOffset, yOffset, f, 0, 0, 0, 0, true);
        }

        var (kr, kb) = matrixCoefficients switch
        {
            1 => (0.2126, 0.0722),
            4 => (0.30, 0.11),
            7 => (0.212, 0.087),
            9 or 10 => (0.2627, 0.0593),
            _ => (0.299, 0.114),
        };
        var kg = 1 - kr - kb;
        return new Coefficients(
            yOffset,
            cOffset,
            Fixed(yMul),
            Fixed(2 * (1 - kr) * cMul),
            Fixed(2 * kb * (1 - kb) / kg * cMul),
            Fixed(2 * kr * (1 - kr) / kg * cMul),
            Fixed(2 * (1 - kb) * cMul),
            false);
    }

    private static int Fixed(double value) => (int)Math.Round(value * 65536, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Converts a 4:2:0 block of <paramref name="width"/> × <paramref name="height"/> luma samples to RGBA8.
    /// <paramref name="rgba"/> starts at the block's first pixel and advances <paramref name="rgbaStride"/> bytes per row.
    /// </summary>
    internal static void ToRgba420(ReadOnlySpan<ushort> y, int yStride, ReadOnlySpan<ushort> cb, ReadOnlySpan<ushort> cr,
        int cStride, int width, int height, in Coefficients k, Span<byte> rgba, int rgbaStride)
    {
        for (var row = 0; row < height; row++)
        {
            var yRow = y.Slice(row * yStride, width);
            var cRowOffset = (row >> 1) * cStride;
            var cbRow = cb.Slice(cRowOffset);
            var crRow = cr.Slice(cRowOffset);
            var outRow = rgba.Slice(row * rgbaStride);
            var done = 0;
            if (!k.Identity)
            {
                if (Vector256.IsHardwareAccelerated && width >= 8)
                    done = ToRgba420RowV256(yRow, cbRow, crRow, outRow, width, in k);
                else if (Vector128.IsHardwareAccelerated && width >= 4)
                    done = ToRgba420RowV128(yRow, cbRow, crRow, outRow, width, in k);
            }
            ToRgba420RowScalar(yRow, cbRow, crRow, outRow, done, width, in k);
        }
    }

    /// <summary>
    /// Scalar definition of one luma row of the 4:2:0 → RGBA8 conversion, starting at <paramref name="start"/>.
    /// Called as a tail by the vector tiers and directly for the identity-matrix path.
    /// </summary>
    internal static void ToRgba420RowScalar(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cbRow, ReadOnlySpan<ushort> crRow,
        Span<byte> outRow, int start, int width, in Coefficients k)
    {
        for (var x = start; x < width; x++)
        {
            var luma = y[x] - k.YOffset;
            var u = cbRow[x >> 1] - k.COffset;
            var v = crRow[x >> 1] - k.COffset;
            int r, g, b;
            if (k.Identity)
            {
                r = (k.Y * v + 32768) >> 16;
                g = (k.Y * luma + 32768) >> 16;
                b = (k.Y * u + 32768) >> 16;
            }
            else
            {
                var yy = k.Y * luma + 32768;
                r = (yy + k.CrR * v) >> 16;
                g = (yy - k.CbG * u - k.CrG * v) >> 16;
                b = (yy + k.CbB * u) >> 16;
            }
            outRow[x * 4] = (byte)Math.Clamp(r, 0, 255);
            outRow[x * 4 + 1] = (byte)Math.Clamp(g, 0, 255);
            outRow[x * 4 + 2] = (byte)Math.Clamp(b, 0, 255);
            outRow[x * 4 + 3] = 255;
        }
    }

    /// <summary>
    /// Vector128 kernel for one luma row (non-identity path only). Processes 4 output pixels per step (each
    /// chroma sample serves a pair of luma samples, so 4 pixels need 2 chroma pairs = 4 chroma values).
    /// Returns the number of pixels completed (a multiple of 4). The proof: i + 4 ≤ width ≤ y.Length and cbRow/crRow
    /// are width/2 long (stride), so y[i..i+4] and cb/cr[(i/2)..(i/2)+2] are in range by the loop guard.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int ToRgba420RowV128(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cbRow, ReadOnlySpan<ushort> crRow,
        Span<byte> outRow, int width, in Coefficients k)
    {
        ref var py = ref MemoryMarshal.GetReference(y);
        ref var pcb = ref MemoryMarshal.GetReference(cbRow);
        ref var pcr = ref MemoryMarshal.GetReference(crRow);
        ref var po = ref MemoryMarshal.GetReference(outRow);
        var vYOffset = Vector128.Create(k.YOffset);
        var vY = Vector128.Create(k.Y);
        var vCrR = Vector128.Create(k.CrR);
        var vCbG = Vector128.Create(k.CbG);
        var vCrG = Vector128.Create(k.CrG);
        var vCbB = Vector128.Create(k.CbB);
        var vHalf = Vector128.Create(32768);
        var vMax = Vector128.Create(255);
        var vAlpha = Vector128.Create((uint)0xFF000000u);
        var i = 0;
        for (; i + 4 <= width; i += 4)
        {
            // 4 luma samples expanded to int32.
            var yv = Vector128.Create((int)Unsafe.Add(ref py, i), (int)Unsafe.Add(ref py, i + 1),
                                      (int)Unsafe.Add(ref py, i + 2), (int)Unsafe.Add(ref py, i + 3)) - vYOffset;
            // 2 chroma pairs, each replicated to serve 2 luma samples.
            var ci = i >> 1;
            var u0 = (int)Unsafe.Add(ref pcb, ci) - k.COffset;
            var u1 = (int)Unsafe.Add(ref pcb, ci + 1) - k.COffset;
            var v0 = (int)Unsafe.Add(ref pcr, ci) - k.COffset;
            var v1 = (int)Unsafe.Add(ref pcr, ci + 1) - k.COffset;
            var uv = Vector128.Create(u0, u0, u1, u1);
            var vv = Vector128.Create(v0, v0, v1, v1);
            var yy = yv * vY + vHalf;
            var rv = Vector128.Min(Vector128.Max((yy + vCrR * vv) >> 16, Vector128<int>.Zero), vMax).AsUInt32();
            var gv = Vector128.Min(Vector128.Max((yy - vCbG * uv - vCrG * vv) >> 16, Vector128<int>.Zero), vMax).AsUInt32();
            var bv = Vector128.Min(Vector128.Max((yy + vCbB * uv) >> 16, Vector128<int>.Zero), vMax).AsUInt32();
            // Pack 4 pixels: each pixel = R | (G << 8) | (B << 16) | 0xFF000000.
            var pixels = rv | (gv << 8) | (bv << 16) | vAlpha;
            pixels.StoreUnsafe(ref Unsafe.As<byte, uint>(ref po), (nuint)i);
        }
        return i;
    }

    /// <summary>
    /// Vector256 kernel for one luma row (non-identity path only). Processes 8 output pixels per step.
    /// Returns the number of pixels completed (a multiple of 8). The proof: i + 8 ≤ width ≤ y.Length and
    /// cbRow/crRow are width/2 long, so y[i..i+8] and cb/cr[(i/2)..(i/2)+4] are in range by the loop guard.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int ToRgba420RowV256(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cbRow, ReadOnlySpan<ushort> crRow,
        Span<byte> outRow, int width, in Coefficients k)
    {
        ref var py = ref MemoryMarshal.GetReference(y);
        ref var pcb = ref MemoryMarshal.GetReference(cbRow);
        ref var pcr = ref MemoryMarshal.GetReference(crRow);
        ref var po = ref MemoryMarshal.GetReference(outRow);
        var vYOffset = Vector256.Create(k.YOffset);
        var vY = Vector256.Create(k.Y);
        var vCrR = Vector256.Create(k.CrR);
        var vCbG = Vector256.Create(k.CbG);
        var vCrG = Vector256.Create(k.CrG);
        var vCbB = Vector256.Create(k.CbB);
        var vHalf = Vector256.Create(32768);
        var vMax = Vector256.Create(255);
        var vAlpha = Vector256.Create((uint)0xFF000000u);
        var i = 0;
        for (; i + 8 <= width; i += 8)
        {
            // 8 luma samples.
            var ylo = Vector128.Create((int)Unsafe.Add(ref py, i), (int)Unsafe.Add(ref py, i + 1),
                                       (int)Unsafe.Add(ref py, i + 2), (int)Unsafe.Add(ref py, i + 3));
            var yhi = Vector128.Create((int)Unsafe.Add(ref py, i + 4), (int)Unsafe.Add(ref py, i + 5),
                                       (int)Unsafe.Add(ref py, i + 6), (int)Unsafe.Add(ref py, i + 7));
            var yv = Vector256.Create(ylo, yhi) - vYOffset;
            // 4 chroma pairs, each replicated to serve 2 luma samples.
            var ci = i >> 1;
            var uv = Vector256.Create(
                (int)Unsafe.Add(ref pcb, ci) - k.COffset, (int)Unsafe.Add(ref pcb, ci) - k.COffset,
                (int)Unsafe.Add(ref pcb, ci + 1) - k.COffset, (int)Unsafe.Add(ref pcb, ci + 1) - k.COffset,
                (int)Unsafe.Add(ref pcb, ci + 2) - k.COffset, (int)Unsafe.Add(ref pcb, ci + 2) - k.COffset,
                (int)Unsafe.Add(ref pcb, ci + 3) - k.COffset, (int)Unsafe.Add(ref pcb, ci + 3) - k.COffset);
            var vv = Vector256.Create(
                (int)Unsafe.Add(ref pcr, ci) - k.COffset, (int)Unsafe.Add(ref pcr, ci) - k.COffset,
                (int)Unsafe.Add(ref pcr, ci + 1) - k.COffset, (int)Unsafe.Add(ref pcr, ci + 1) - k.COffset,
                (int)Unsafe.Add(ref pcr, ci + 2) - k.COffset, (int)Unsafe.Add(ref pcr, ci + 2) - k.COffset,
                (int)Unsafe.Add(ref pcr, ci + 3) - k.COffset, (int)Unsafe.Add(ref pcr, ci + 3) - k.COffset);
            var yy = yv * vY + vHalf;
            var rv = Vector256.Min(Vector256.Max((yy + vCrR * vv) >> 16, Vector256<int>.Zero), vMax).AsUInt32();
            var gv = Vector256.Min(Vector256.Max((yy - vCbG * uv - vCrG * vv) >> 16, Vector256<int>.Zero), vMax).AsUInt32();
            var bv = Vector256.Min(Vector256.Max((yy + vCbB * uv) >> 16, Vector256<int>.Zero), vMax).AsUInt32();
            // Pack 8 pixels: R | (G << 8) | (B << 16) | 0xFF000000.
            var pixels = rv | (gv << 8) | (bv << 16) | vAlpha;
            pixels.StoreUnsafe(ref Unsafe.As<byte, uint>(ref po), (nuint)i);
        }
        return i;
    }

    /// <summary>
    /// Converts a block of <paramref name="width"/> × <paramref name="height"/> luma samples to 8-bit grey
    /// (see the remarks on the class). Not for an identity-matrix image.
    /// <paramref name="gray"/> starts at the block's first pixel and advances <paramref name="grayStride"/> bytes per row.
    /// </summary>
    internal static void ToGray(ReadOnlySpan<ushort> y, int yStride, int width, int height, in Coefficients k, Span<byte> gray, int grayStride)
    {
        for (var row = 0; row < height; row++)
        {
            var yRow = y.Slice(row * yStride, width);
            var outRow = gray.Slice(row * grayStride, width);
            for (var x = 0; x < width; x++)
            {
                var value = (k.Y * (yRow[x] - k.YOffset) + 32768) >> 16;
                outRow[x] = (byte)Math.Clamp(value, 0, 255);
            }
        }
    }
}
