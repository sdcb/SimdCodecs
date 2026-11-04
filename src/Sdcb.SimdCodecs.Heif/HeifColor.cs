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
/// <b>Grey (<see cref="ToGrayRow"/>).</b> The 8-bit grey output is the stored luma, range-mapped like the RGB
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
    /// Converts one row of <paramref name="width"/> pixels of a 4:2:0 image: luma <paramref name="y"/>[x] takes chroma
    /// <paramref name="cb"/>[x / 2] and <paramref name="cr"/>[x / 2]. Writes 4 bytes per pixel, R G B A, or B G R A when
    /// <paramref name="bgr"/> is set (a byte permutation of the same values).
    /// </summary>
    /// <remarks>
    /// The kernels are split into the arithmetic (the frozen R, G, B integers, identical on every tier) and the
    /// store, which only places those values: another output layout is another store. A 16-bit output (2.0) is a
    /// different coefficient set (65535 / (2^b − 1) scaling) and a 16-bit clamp and store over the same structure.
    /// </remarks>
    internal static void ToRgba420Row(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cb, ReadOnlySpan<ushort> cr,
        Span<byte> outRow, int width, in Coefficients k, bool bgr = false)
    {
        CheckRow(y, cb, cr, outRow, width);
        var done = 0;
        if (!k.Identity)
        {
            if (Vector256.IsHardwareAccelerated && width >= 8)
                done = ToRgba420RowV256(y, cb, cr, outRow, width, in k, bgr);
            else if (Vector128.IsHardwareAccelerated && width >= 4)
                done = ToRgba420RowV128(y, cb, cr, outRow, width, in k, bgr);
        }
        ToRgba420RowScalar(y, cb, cr, outRow, done, width, in k, bgr);
    }

    /// <summary>The bounds every row kernel relies on: the proof for the vector tiers' unchecked accesses.</summary>
    private static void CheckRow(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cb, ReadOnlySpan<ushort> cr, Span<byte> outRow, int width)
    {
        var chroma = (width + 1) >> 1;
        if (width < 0 || y.Length < width || cb.Length < chroma || cr.Length < chroma || outRow.Length < (long)width * 4)
        {
            throw new ArgumentException("The colour row does not fit its buffers.");
        }
    }

    /// <summary>
    /// The scalar definition of <see cref="ToRgba420Row"/> from pixel <paramref name="start"/> on: the vector tiers'
    /// tail and the identity-matrix path.
    /// </summary>
    internal static void ToRgba420RowScalar(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cbRow, ReadOnlySpan<ushort> crRow,
        Span<byte> outRow, int start, int width, in Coefficients k, bool bgr = false)
    {
        var rAt = bgr ? 2 : 0;
        var bAt = bgr ? 0 : 2;
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
            outRow[x * 4 + rAt] = (byte)Math.Clamp(r, 0, 255);
            outRow[x * 4 + 1] = (byte)Math.Clamp(g, 0, 255);
            outRow[x * 4 + bAt] = (byte)Math.Clamp(b, 0, 255);
            outRow[x * 4 + 3] = 255;
        }
    }

    /// <summary>
    /// Vector128 tier of <see cref="ToRgba420Row"/> (not for the identity matrix): 4 pixels per step, which take 2
    /// chroma samples each of Cb and Cr. Returns the pixels done (a multiple of 4).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int ToRgba420RowV128(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cbRow, ReadOnlySpan<ushort> crRow,
        Span<byte> outRow, int width, in Coefficients k, bool bgr = false)
    {
        CheckRow(y, cbRow, crRow, outRow, width);
        // i + 4 <= width: y[i .. i + 3], cb / cr[i / 2 .. i / 2 + 1] (< (width + 1) / 2) and out[4i .. 4i + 15] are in
        // range by CheckRow.
        ref var py = ref MemoryMarshal.GetReference(y);
        ref var pcb = ref MemoryMarshal.GetReference(cbRow);
        ref var pcr = ref MemoryMarshal.GetReference(crRow);
        ref var po = ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(outRow));
        var vYOffset = Vector128.Create(k.YOffset);
        var vY = Vector128.Create(k.Y);
        var vCrR = Vector128.Create(k.CrR);
        var vCbG = Vector128.Create(k.CbG);
        var vCrG = Vector128.Create(k.CrG);
        var vCbB = Vector128.Create(k.CbB);
        var vHalf = Vector128.Create(32768);
        var vMax = Vector128.Create(255);
        var vAlpha = Vector128.Create(0xFF000000u);
        var rShift = bgr ? 16 : 0;
        var bShift = bgr ? 0 : 16;
        var i = 0;
        for (; i + 4 <= width; i += 4)
        {
            var yv = Vector128.Create((int)Unsafe.Add(ref py, i), Unsafe.Add(ref py, i + 1), Unsafe.Add(ref py, i + 2), Unsafe.Add(ref py, i + 3)) - vYOffset;
            var ci = i >> 1;
            var u0 = Unsafe.Add(ref pcb, ci) - k.COffset;
            var u1 = Unsafe.Add(ref pcb, ci + 1) - k.COffset;
            var v0 = Unsafe.Add(ref pcr, ci) - k.COffset;
            var v1 = Unsafe.Add(ref pcr, ci + 1) - k.COffset;
            var uv = Vector128.Create(u0, u0, u1, u1);
            var vv = Vector128.Create(v0, v0, v1, v1);
            var yy = yv * vY + vHalf;
            var rv = Vector128.Min(Vector128.Max((yy + vCrR * vv) >> 16, Vector128<int>.Zero), vMax).AsUInt32();
            var gv = Vector128.Min(Vector128.Max((yy - vCbG * uv - vCrG * vv) >> 16, Vector128<int>.Zero), vMax).AsUInt32();
            var bv = Vector128.Min(Vector128.Max((yy + vCbB * uv) >> 16, Vector128<int>.Zero), vMax).AsUInt32();
            ((rv << rShift) | (gv << 8) | (bv << bShift) | vAlpha).StoreUnsafe(ref po, (nuint)i);
        }
        return i;
    }

    /// <summary>Vector256 tier of <see cref="ToRgba420Row"/> (not for the identity matrix): 8 pixels per step.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int ToRgba420RowV256(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cbRow, ReadOnlySpan<ushort> crRow,
        Span<byte> outRow, int width, in Coefficients k, bool bgr = false)
    {
        CheckRow(y, cbRow, crRow, outRow, width);
        // i + 8 <= width: y[i .. i + 7], cb / cr[i / 2 .. i / 2 + 3] (< (width + 1) / 2) and out[4i .. 4i + 31] are in
        // range by CheckRow.
        ref var py = ref MemoryMarshal.GetReference(y);
        ref var pcb = ref MemoryMarshal.GetReference(cbRow);
        ref var pcr = ref MemoryMarshal.GetReference(crRow);
        ref var po = ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(outRow));
        var vYOffset = Vector256.Create(k.YOffset);
        var vCOffset = Vector128.Create((short)k.COffset);
        var vY = Vector256.Create(k.Y);
        var vCrR = Vector256.Create(k.CrR);
        var vCbG = Vector256.Create(k.CbG);
        var vCrG = Vector256.Create(k.CrG);
        var vCbB = Vector256.Create(k.CbB);
        var vHalf = Vector256.Create(32768);
        var vMax = Vector256.Create(255);
        var vAlpha = Vector256.Create(0xFF000000u);
        var rShift = bgr ? 16 : 0;
        var bShift = bgr ? 0 : 16;
        var i = 0;
        for (; i + 8 <= width; i += 8)
        {
            var yv = Vector256.Create(Vector128.WidenLower(Vector128.LoadUnsafe(ref py, (nuint)i)).AsInt32(),
                                  Vector128.WidenUpper(Vector128.LoadUnsafe(ref py, (nuint)i)).AsInt32()) - vYOffset;
            var ci = i >> 1;
            // Four chroma samples, each duplicated for its two luma samples: c0 c0 c1 c1 c2 c2 c3 c3.
            var cb4 = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref pcb, ci)))).AsUInt16();
            var cr4 = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref pcr, ci)))).AsUInt16();
            var dup = Vector128.Create((ushort)0, 0, 1, 1, 2, 2, 3, 3);
            var uv = Vector256.Create(Vector128.WidenLower(Vector128.Shuffle(cb4, dup).AsInt16() - vCOffset).AsInt32(),
                                      Vector128.WidenUpper(Vector128.Shuffle(cb4, dup).AsInt16() - vCOffset).AsInt32());
            var vv = Vector256.Create(Vector128.WidenLower(Vector128.Shuffle(cr4, dup).AsInt16() - vCOffset).AsInt32(),
                                      Vector128.WidenUpper(Vector128.Shuffle(cr4, dup).AsInt16() - vCOffset).AsInt32());
            var yy = yv * vY + vHalf;
            var rv = Vector256.Min(Vector256.Max((yy + vCrR * vv) >> 16, Vector256<int>.Zero), vMax).AsUInt32();
            var gv = Vector256.Min(Vector256.Max((yy - vCbG * uv - vCrG * vv) >> 16, Vector256<int>.Zero), vMax).AsUInt32();
            var bv = Vector256.Min(Vector256.Max((yy + vCbB * uv) >> 16, Vector256<int>.Zero), vMax).AsUInt32();
            ((rv << rShift) | (gv << 8) | (bv << bShift) | vAlpha).StoreUnsafe(ref po, (nuint)i);
        }
        return i;
    }

    /// <summary>
    /// Converts one row of <paramref name="width"/> luma samples to 8-bit grey (see the remarks on the class).
    /// Not for an identity-matrix image.
    /// </summary>
    internal static void ToGrayRow(ReadOnlySpan<ushort> y, Span<byte> outRow, int width, in Coefficients k)
    {
        var yRow = y[..width];
        var gray = outRow[..width];
        for (var x = 0; x < yRow.Length; x++)
        {
            var value = (k.Y * (yRow[x] - k.YOffset) + 32768) >> 16;
            gray[x] = (byte)Math.Clamp(value, 0, 255);
        }
    }
}
