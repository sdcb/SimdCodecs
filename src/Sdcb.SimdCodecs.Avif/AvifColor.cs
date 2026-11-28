using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Sdcb.SimdCodecs.Avif;

/// <summary>
/// YCbCr / monochrome + optional alpha → RGBA8 conversion for decoded AVIF images.
/// Uses exact fixed-point integer arithmetic (matching the repo's frozen conversion model).
/// </summary>
internal static class AvifColor
{
    internal readonly record struct Coefficients(
        int YOffset, int COffset, int Y, int CrR, int CbG, int CrG, int CbB, bool Identity);

    internal static Coefficients For(int matrixCoefficients, bool fullRange, int bitDepth)
    {
        int scale = 1 << (bitDepth - 8);
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
        int cOffset = 1 << (bitDepth - 1);

        if (matrixCoefficients == 0)
            return new Coefficients(yOffset, yOffset, Fixed(yMul), 0, 0, 0, 0, true);

        var (kr, kb) = matrixCoefficients switch
        {
            1 => (0.2126, 0.0722),
            4 => (0.30, 0.11),
            7 => (0.212, 0.087),
            9 or 10 => (0.2627, 0.0593),
            _ => (0.299, 0.114),
        };
        double kg = 1.0 - kr - kb;
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

    internal readonly record struct Coefficients16(
        int YOffset, int COffset, int Y, int CrR, int CbG, int CrG, int CbB, bool Identity);

    internal static Coefficients16 For16(int matrixCoefficients, bool fullRange, int bitDepth)
    {
        int scale = 1 << (bitDepth - 8);
        double yMul, cMul;
        int yOffset;
        if (fullRange)
        {
            yMul = cMul = 65535.0 / ((1 << bitDepth) - 1);
            yOffset = 0;
        }
        else
        {
            yMul = 65535.0 / (219 * scale);
            cMul = 65535.0 / (224 * scale);
            yOffset = 16 * scale;
        }
        int cOffset = 1 << (bitDepth - 1);

        if (matrixCoefficients == 0)
            return new Coefficients16(yOffset, yOffset, Fixed16(yMul), 0, 0, 0, 0, true);

        var (kr, kb) = matrixCoefficients switch
        {
            1 => (0.2126, 0.0722),
            4 => (0.30, 0.11),
            7 => (0.212, 0.087),
            9 or 10 => (0.2627, 0.0593),
            _ => (0.299, 0.114),
        };
        double kg = 1.0 - kr - kb;
        return new Coefficients16(
            yOffset,
            cOffset,
            Fixed16(yMul),
            Fixed16(2 * (1 - kr) * cMul),
            Fixed16(2 * kb * (1 - kb) / kg * cMul),
            Fixed16(2 * kr * (1 - kr) / kg * cMul),
            Fixed16(2 * (1 - kb) * cMul),
            false);
    }

    private static int Fixed(double value) => (int)Math.Round(value * 65536.0, MidpointRounding.AwayFromZero);
    private static int Fixed16(double value) => (int)Math.Round(value * 16384.0, MidpointRounding.AwayFromZero);

    internal static ushort SampleAlphaTo16(ushort sample, int alphaBitDepth, bool alphaFullRange)
    {
        if (alphaFullRange)
        {
            int max = (1 << alphaBitDepth) - 1;
            return (ushort)Math.Clamp(((long)sample * 65535 + (max >> 1)) / max, 0, 65535);
        }
        int scale = 1 << (alphaBitDepth - 8);
        int a = (int)Math.Round((sample - 16 * scale) * 65535.0 / (219 * scale));
        return (ushort)Math.Clamp(a, 0, 65535);
    }

    internal static void PixelToRgba16(
        ushort ySample, ushort cbSample, ushort crSample, bool monochrome,
        in Coefficients16 k, ushort alpha, bool premultiplied,
        out ushort rOut, out ushort gOut, out ushort bOut, out ushort aOut)
    {
        int r, g, b;
        int luma = ySample - k.YOffset;
        if (monochrome)
        {
            r = g = b = (int)Math.Clamp(((long)k.Y * luma + 8192) >> 14, 0, 65535);
        }
        else if (k.Identity)
        {
            int u = cbSample - k.COffset;
            int v = crSample - k.COffset;
            r = (int)Math.Clamp(((long)k.Y * v + 8192) >> 14, 0, 65535);
            g = (int)Math.Clamp(((long)k.Y * luma + 8192) >> 14, 0, 65535);
            b = (int)Math.Clamp(((long)k.Y * u + 8192) >> 14, 0, 65535);
        }
        else
        {
            int u = cbSample - k.COffset;
            int v = crSample - k.COffset;
            long yy = (long)k.Y * luma + 8192;
            r = (int)Math.Clamp((yy + (long)k.CrR * v) >> 14, 0, 65535);
            g = (int)Math.Clamp((yy - (long)k.CbG * u - (long)k.CrG * v) >> 14, 0, 65535);
            b = (int)Math.Clamp((yy + (long)k.CbB * u) >> 14, 0, 65535);
        }

        if (premultiplied && alpha > 0 && alpha < 65535)
        {
            r = (int)Math.Min(65535, ((long)r * 65535 + (alpha >> 1)) / alpha);
            g = (int)Math.Min(65535, ((long)g * 65535 + (alpha >> 1)) / alpha);
            b = (int)Math.Min(65535, ((long)b * 65535 + (alpha >> 1)) / alpha);
        }

        rOut = (ushort)r;
        gOut = (ushort)g;
        bOut = (ushort)b;
        aOut = alpha;
    }

    internal static byte SampleAlphaTo8(ushort sample, int alphaBitDepth, bool alphaFullRange)
    {
        if (alphaFullRange)
        {
            if (alphaBitDepth == 8) return (byte)Math.Min(sample, (ushort)255);
            int max = (1 << alphaBitDepth) - 1;
            return (byte)Math.Clamp((sample * 255 + (max >> 1)) / max, 0, 255);
        }
        int scale = 1 << (alphaBitDepth - 8);
        int a = (int)Math.Round((sample - 16 * scale) * 255.0 / (219 * scale));
        return (byte)Math.Clamp(a, 0, 255);
    }

    internal static void PixelToRgba(
        ushort ySample, ushort cbSample, ushort crSample, bool monochrome,
        in Coefficients k, byte alpha, bool premultiplied,
        out byte rOut, out byte gOut, out byte bOut, out byte aOut)
    {
        int r, g, b;
        int luma = ySample - k.YOffset;
        if (monochrome)
        {
            r = g = b = Math.Clamp((k.Y * luma + 32768) >> 16, 0, 255);
        }
        else if (k.Identity)
        {
            int u = cbSample - k.COffset;
            int v = crSample - k.COffset;
            r = Math.Clamp((k.Y * v + 32768) >> 16, 0, 255);
            g = Math.Clamp((k.Y * luma + 32768) >> 16, 0, 255);
            b = Math.Clamp((k.Y * u + 32768) >> 16, 0, 255);
        }
        else
        {
            int u = cbSample - k.COffset;
            int v = crSample - k.COffset;
            int yy = k.Y * luma + 32768;
            r = Math.Clamp((yy + k.CrR * v) >> 16, 0, 255);
            g = Math.Clamp((yy - k.CbG * u - k.CrG * v) >> 16, 0, 255);
            b = Math.Clamp((yy + k.CbB * u) >> 16, 0, 255);
        }

        if (premultiplied && alpha > 0 && alpha < 255)
        {
            r = Math.Min(255, (r * 255 + (alpha >> 1)) / alpha);
            g = Math.Min(255, (g * 255 + (alpha >> 1)) / alpha);
            b = Math.Min(255, (b * 255 + (alpha >> 1)) / alpha);
        }

        rOut = (byte)r;
        gOut = (byte)g;
        bOut = (byte)b;
        aOut = alpha;
    }

    /// <summary>
    /// One row of opaque RGBA8 (alpha 255, straight) from Y and nearest-neighbour Cb / Cr: what
    /// <see cref="PixelToRgba"/> gives pixel by pixel, for every pixel of <paramref name="y"/>. Not for
    /// <see cref="Coefficients.Identity"/>. <paramref name="cb"/> and <paramref name="cr"/> hold at least
    /// <c>(width + subX) &gt;&gt; subX</c> samples (ignored when <paramref name="monochrome"/>); <paramref name="dst"/>
    /// holds <c>width × 4</c> bytes.
    /// </summary>
    internal static void RowToRgba8(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cb, ReadOnlySpan<ushort> cr,
        bool monochrome, int subX, in Coefficients k, Span<byte> dst)
    {
        int done = Vector128.IsHardwareAccelerated ? RowToRgba8Vector(y, cb, cr, monochrome, subX, in k, dst) : 0;
        RowToRgba8Scalar(y, cb, cr, monochrome, subX, in k, dst, done);
    }

    /// <summary>The scalar twin of <see cref="RowToRgba8Vector"/>, from pixel <paramref name="start"/>.</summary>
    internal static void RowToRgba8Scalar(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cb, ReadOnlySpan<ushort> cr,
        bool monochrome, int subX, in Coefficients k, Span<byte> dst, int start)
    {
        for (int x = start; x < y.Length; x++)
        {
            ushort cbs = monochrome ? (ushort)0 : cb[x >> subX];
            ushort crs = monochrome ? (ushort)0 : cr[x >> subX];
            PixelToRgba(y[x], cbs, crs, monochrome, in k, 255, false,
                out dst[x * 4], out dst[x * 4 + 1], out dst[x * 4 + 2], out dst[x * 4 + 3]);
        }
    }

    /// <summary>
    /// <see cref="RowToRgba8"/> eight pixels at a time; returns how many pixels it wrote (a multiple of 8). The int32
    /// lanes cannot overflow: AV1 samples have at most 12 bits, and every coefficient of <see cref="For"/> is below
    /// 2^18 (the largest, 8-bit studio-range Cb→B, is about 1.1 × 2^17), so each product stays below 2^30 and the
    /// three-term sums below 2^31, as in the scalar int arithmetic. A monochrome row runs the same formula with zero
    /// chroma coefficients, which is the scalar monochrome branch.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int RowToRgba8Vector(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cb, ReadOnlySpan<ushort> cr,
        bool monochrome, int subX, in Coefficients k, Span<byte> dst)
    {
        var yOff = Vector128.Create(k.YOffset);
        var cOff = Vector128.Create(k.COffset);
        var kY = Vector128.Create(k.Y);
        var kCrR = Vector128.Create(monochrome ? 0 : k.CrR);
        var kCbG = Vector128.Create(monochrome ? 0 : k.CbG);
        var kCrG = Vector128.Create(monochrome ? 0 : k.CrG);
        var kCbB = Vector128.Create(monochrome ? 0 : k.CbB);
        var round = Vector128.Create(32768);
        var max = Vector128.Create(255);
        var opaque = Vector128.Create(unchecked((int)0xFF000000));
        var dup = Vector128.Create((ushort)0, 0, 1, 1, 2, 2, 3, 3);

        int width = y.Length;
        int x = 0;
        for (; x + 8 <= width; x += 8)
        {
            var yv = Vector128.Create(y.Slice(x, 8));
            Vector128<ushort> cbv, crv;
            if (monochrome)
            {
                cbv = crv = yv; // multiplied by zero coefficients
            }
            else if (subX == 0)
            {
                cbv = Vector128.Create(cb.Slice(x, 8));
                crv = Vector128.Create(cr.Slice(x, 8));
            }
            else
            {
                int cx = x >> 1;
                if (cx + 8 > cb.Length || cx + 8 > cr.Length)
                    break; // the scalar tail finishes the row
                cbv = Vector128.Shuffle(Vector128.Create(cb.Slice(cx, 8)), dup);
                crv = Vector128.Shuffle(Vector128.Create(cr.Slice(cx, 8)), dup);
            }
            Pixels8(Vector128.WidenLower(yv), Vector128.WidenLower(cbv), Vector128.WidenLower(crv)).CopyTo(dst.Slice(x * 4, 16));
            Pixels8(Vector128.WidenUpper(yv), Vector128.WidenUpper(cbv), Vector128.WidenUpper(crv)).CopyTo(dst.Slice(x * 4 + 16, 16));
        }
        return x;

        Vector128<byte> Pixels8(Vector128<uint> ys, Vector128<uint> cbs, Vector128<uint> crs)
        {
            var luma = ys.AsInt32() - yOff;
            var u = cbs.AsInt32() - cOff;
            var v = crs.AsInt32() - cOff;
            var yy = kY * luma + round;
            var r = Clamp(Vector128.ShiftRightArithmetic(yy + kCrR * v, 16), max);
            var g = Clamp(Vector128.ShiftRightArithmetic(yy - kCbG * u - kCrG * v, 16), max);
            var b = Clamp(Vector128.ShiftRightArithmetic(yy + kCbB * u, 16), max);
            return (r | Vector128.ShiftLeft(g, 8) | Vector128.ShiftLeft(b, 16) | opaque).AsByte();
        }
    }

    /// <summary>
    /// One row of opaque RGBA16 (alpha 65535, straight): what <see cref="PixelToRgba16"/> gives pixel by pixel. The
    /// same contract as <see cref="RowToRgba8"/>, with <paramref name="dst"/> holding <c>width × 4</c> samples.
    /// </summary>
    internal static void RowToRgba16(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cb, ReadOnlySpan<ushort> cr,
        bool monochrome, int subX, in Coefficients16 k, Span<ushort> dst)
    {
        int done = Vector128.IsHardwareAccelerated ? RowToRgba16Vector(y, cb, cr, monochrome, subX, in k, dst) : 0;
        RowToRgba16Scalar(y, cb, cr, monochrome, subX, in k, dst, done);
    }

    /// <summary>The scalar twin of <see cref="RowToRgba16Vector"/>, from pixel <paramref name="start"/>.</summary>
    internal static void RowToRgba16Scalar(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cb, ReadOnlySpan<ushort> cr,
        bool monochrome, int subX, in Coefficients16 k, Span<ushort> dst, int start)
    {
        for (int x = start; x < y.Length; x++)
        {
            ushort cbs = monochrome ? (ushort)0 : cb[x >> subX];
            ushort crs = monochrome ? (ushort)0 : cr[x >> subX];
            PixelToRgba16(y[x], cbs, crs, monochrome, in k, 65535, false,
                out dst[x * 4], out dst[x * 4 + 1], out dst[x * 4 + 2], out dst[x * 4 + 3]);
        }
    }

    /// <summary>
    /// <see cref="RowToRgba16"/> eight pixels at a time; returns how many pixels it wrote. The scalar sums
    /// (<c>Y·luma + 8192 + C·chroma</c>, 14 fraction bits) need 64 bits: a coefficient reaches about 2^21. Each
    /// coefficient is split as <c>hi·2^14 + lo</c> (all of them are positive), so the sum is
    /// <c>hiSum·2^14 + loSum</c> and <c>sum &gt;&gt; 14 = hiSum + (loSum &gt;&gt; 14)</c> exactly (an arithmetic
    /// shift is a floor). With samples of at most 12 bits each lo product is below 2^26 in magnitude, so loSum stays
    /// below 2^28; hi is below 2^9 (8-bit studio range has the largest, about 300), so hiSum stays below 2^22. Both fit int32.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int RowToRgba16Vector(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cb, ReadOnlySpan<ushort> cr,
        bool monochrome, int subX, in Coefficients16 k, Span<ushort> dst)
    {
        var yOff = Vector128.Create(k.YOffset);
        var cOff = Vector128.Create(k.COffset);
        var yHi = Vector128.Create(k.Y >> 14);
        var yLo = Vector128.Create(k.Y & 16383);
        int crR = monochrome ? 0 : k.CrR, cbG = monochrome ? 0 : k.CbG, crG = monochrome ? 0 : k.CrG, cbB = monochrome ? 0 : k.CbB;
        var crRHi = Vector128.Create(crR >> 14);
        var crRLo = Vector128.Create(crR & 16383);
        var cbGHi = Vector128.Create(cbG >> 14);
        var cbGLo = Vector128.Create(cbG & 16383);
        var crGHi = Vector128.Create(crG >> 14);
        var crGLo = Vector128.Create(crG & 16383);
        var cbBHi = Vector128.Create(cbB >> 14);
        var cbBLo = Vector128.Create(cbB & 16383);
        var round = Vector128.Create(8192);
        var max = Vector128.Create(65535);
        var opaque = Vector128.Create(unchecked((int)0xFFFF0000));
        var dup = Vector128.Create((ushort)0, 0, 1, 1, 2, 2, 3, 3);

        int width = y.Length;
        int x = 0;
        for (; x + 8 <= width; x += 8)
        {
            var yv = Vector128.Create(y.Slice(x, 8));
            Vector128<ushort> cbv, crv;
            if (monochrome)
            {
                cbv = crv = yv; // multiplied by zero coefficients
            }
            else if (subX == 0)
            {
                cbv = Vector128.Create(cb.Slice(x, 8));
                crv = Vector128.Create(cr.Slice(x, 8));
            }
            else
            {
                int cx = x >> 1;
                if (cx + 8 > cb.Length || cx + 8 > cr.Length)
                    break; // the scalar tail finishes the row
                cbv = Vector128.Shuffle(Vector128.Create(cb.Slice(cx, 8)), dup);
                crv = Vector128.Shuffle(Vector128.Create(cr.Slice(cx, 8)), dup);
            }
            Pixels16(Vector128.WidenLower(yv), Vector128.WidenLower(cbv), Vector128.WidenLower(crv), dst.Slice(x * 4, 16));
            Pixels16(Vector128.WidenUpper(yv), Vector128.WidenUpper(cbv), Vector128.WidenUpper(crv), dst.Slice(x * 4 + 16, 16));
        }
        return x;

        void Pixels16(Vector128<uint> ys, Vector128<uint> cbs, Vector128<uint> crs, Span<ushort> d)
        {
            var luma = ys.AsInt32() - yOff;
            var u = cbs.AsInt32() - cOff;
            var v = crs.AsInt32() - cOff;
            var hiY = yHi * luma;
            var loY = yLo * luma + round;
            var r = Clamp(hiY + crRHi * v + Vector128.ShiftRightArithmetic(loY + crRLo * v, 14), max);
            var g = Clamp(hiY - cbGHi * u - crGHi * v + Vector128.ShiftRightArithmetic(loY - cbGLo * u - crGLo * v, 14), max);
            var b = Clamp(hiY + cbBHi * u + Vector128.ShiftRightArithmetic(loY + cbBLo * u, 14), max);
            var rg = (r | Vector128.ShiftLeft(g, 16)).AsUInt32();
            var ba = (b | opaque).AsUInt32();
            var p01 = Vector128.WidenLower(rg) | Vector128.ShiftLeft(Vector128.WidenLower(ba), 32);
            var p23 = Vector128.WidenUpper(rg) | Vector128.ShiftLeft(Vector128.WidenUpper(ba), 32);
            p01.AsUInt16().CopyTo(d);
            p23.AsUInt16().CopyTo(d[8..]);
        }
    }

    /// <summary>
    /// <see cref="RowToRgba16"/> followed by keeping each sample's high byte (what converting RGBA16 to RGBA8 does),
    /// in one pass: <c>clamp(s, 0, 65535) &gt;&gt; 8</c> of the same sums, written as opaque RGBA8.
    /// </summary>
    internal static void RowToRgba8Wide(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cb, ReadOnlySpan<ushort> cr,
        bool monochrome, int subX, in Coefficients16 k, Span<byte> dst)
    {
        int done = Vector128.IsHardwareAccelerated ? RowToRgba8WideVector(y, cb, cr, monochrome, subX, in k, dst) : 0;
        RowToRgba8WideScalar(y, cb, cr, monochrome, subX, in k, dst, done);
    }

    /// <summary>The scalar twin of <see cref="RowToRgba8WideVector"/>, from pixel <paramref name="start"/>.</summary>
    internal static void RowToRgba8WideScalar(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cb, ReadOnlySpan<ushort> cr,
        bool monochrome, int subX, in Coefficients16 k, Span<byte> dst, int start)
    {
        for (int x = start; x < y.Length; x++)
        {
            ushort cbs = monochrome ? (ushort)0 : cb[x >> subX];
            ushort crs = monochrome ? (ushort)0 : cr[x >> subX];
            PixelToRgba16(y[x], cbs, crs, monochrome, in k, 65535, false, out ushort r, out ushort g, out ushort b, out _);
            dst[x * 4] = (byte)(r >> 8);
            dst[x * 4 + 1] = (byte)(g >> 8);
            dst[x * 4 + 2] = (byte)(b >> 8);
            dst[x * 4 + 3] = 255;
        }
    }

    /// <summary><see cref="RowToRgba8Wide"/> eight pixels at a time, with the sums of <see cref="RowToRgba16Vector"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int RowToRgba8WideVector(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> cb, ReadOnlySpan<ushort> cr,
        bool monochrome, int subX, in Coefficients16 k, Span<byte> dst)
    {
        var yOff = Vector128.Create(k.YOffset);
        var cOff = Vector128.Create(k.COffset);
        var yHi = Vector128.Create(k.Y >> 14);
        var yLo = Vector128.Create(k.Y & 16383);
        int crR = monochrome ? 0 : k.CrR, cbG = monochrome ? 0 : k.CbG, crG = monochrome ? 0 : k.CrG, cbB = monochrome ? 0 : k.CbB;
        var crRHi = Vector128.Create(crR >> 14);
        var crRLo = Vector128.Create(crR & 16383);
        var cbGHi = Vector128.Create(cbG >> 14);
        var cbGLo = Vector128.Create(cbG & 16383);
        var crGHi = Vector128.Create(crG >> 14);
        var crGLo = Vector128.Create(crG & 16383);
        var cbBHi = Vector128.Create(cbB >> 14);
        var cbBLo = Vector128.Create(cbB & 16383);
        var round = Vector128.Create(8192);
        var max = Vector128.Create(65535);
        var opaque = Vector128.Create(unchecked((int)0xFF000000));
        var dup = Vector128.Create((ushort)0, 0, 1, 1, 2, 2, 3, 3);

        int width = y.Length;
        int x = 0;
        for (; x + 8 <= width; x += 8)
        {
            var yv = Vector128.Create(y.Slice(x, 8));
            Vector128<ushort> cbv, crv;
            if (monochrome)
            {
                cbv = crv = yv; // multiplied by zero coefficients
            }
            else if (subX == 0)
            {
                cbv = Vector128.Create(cb.Slice(x, 8));
                crv = Vector128.Create(cr.Slice(x, 8));
            }
            else
            {
                int cx = x >> 1;
                if (cx + 8 > cb.Length || cx + 8 > cr.Length)
                    break; // the scalar tail finishes the row
                cbv = Vector128.Shuffle(Vector128.Create(cb.Slice(cx, 8)), dup);
                crv = Vector128.Shuffle(Vector128.Create(cr.Slice(cx, 8)), dup);
            }
            Pixels8(Vector128.WidenLower(yv), Vector128.WidenLower(cbv), Vector128.WidenLower(crv), dst.Slice(x * 4, 16));
            Pixels8(Vector128.WidenUpper(yv), Vector128.WidenUpper(cbv), Vector128.WidenUpper(crv), dst.Slice(x * 4 + 16, 16));
        }
        return x;

        void Pixels8(Vector128<uint> ys, Vector128<uint> cbs, Vector128<uint> crs, Span<byte> d)
        {
            var luma = ys.AsInt32() - yOff;
            var u = cbs.AsInt32() - cOff;
            var v = crs.AsInt32() - cOff;
            var hiY = yHi * luma;
            var loY = yLo * luma + round;
            var r = Clamp(hiY + crRHi * v + Vector128.ShiftRightArithmetic(loY + crRLo * v, 14), max);
            var g = Clamp(hiY - cbGHi * u - crGHi * v + Vector128.ShiftRightArithmetic(loY - cbGLo * u - crGLo * v, 14), max);
            var b = Clamp(hiY + cbBHi * u + Vector128.ShiftRightArithmetic(loY + cbBLo * u, 14), max);
            // r, g, b are in [0, 65535]: their high bytes land in bytes 0, 1, 2 of each pixel.
            var px = Vector128.ShiftRightLogical(r, 8) | (g & Vector128.Create(0xFF00)) | Vector128.ShiftLeft(Vector128.ShiftRightLogical(b, 8), 16) | opaque;
            px.AsByte().CopyTo(d);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Clamp(Vector128<int> value, Vector128<int> max) =>
        Vector128.Min(Vector128.Max(value, Vector128<int>.Zero), max);
}
