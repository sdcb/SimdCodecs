using System.Runtime.CompilerServices;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// Forward transforms for the VP8 encoder, the counterparts of RFC 6386 Section 14's inverses.
/// <para>The inverse DCT's 1-D pass (constants 20091/65536 = √2·cos(π/8) − 1 and 35468/65536 =
/// √2·sin(π/8)) is twice the orthonormal 4-point inverse DCT, and the 2-D result is shifted right by
/// 3: a residual block is half the orthonormal inverse of its coefficients. The forward transform is
/// therefore twice the orthonormal DCT, computed here in fixed point (√2·cos(π/8) ≈ 5352/4096,
/// √2·sin(π/8) ≈ 2217/4096) with three guard bits between the passes and rounding at the end.</para>
/// <para>The inverse Walsh–Hadamard transform is H·X·H / 8 with the symmetric ±1 matrix H
/// (H·H = 4I); its exact inverse is H·Y·H / 2.</para>
/// </summary>
internal static class Vp8Fdct
{
    /// <summary>Forward DCT of a 4×4 residual block (raster order) into raster-order coefficients.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Forward(ReadOnlySpan<int> residual, Span<int> coefficients)
    {
        Span<int> t = stackalloc int[16];
        for (int r = 0; r < 4; r++)
        {
            int x0 = residual[r * 4], x1 = residual[r * 4 + 1], x2 = residual[r * 4 + 2], x3 = residual[r * 4 + 3];
            int a = x0 + x3, b = x1 + x2, c = x1 - x2, d = x0 - x3;
            t[r * 4] = (a + b) * 8;
            t[r * 4 + 2] = (a - b) * 8;
            t[r * 4 + 1] = (d * 5352 + c * 2217 + 256) >> 9;
            t[r * 4 + 3] = (d * 2217 - c * 5352 + 256) >> 9;
        }

        for (int k = 0; k < 4; k++)
        {
            int y0 = t[k], y1 = t[4 + k], y2 = t[8 + k], y3 = t[12 + k];
            int a = y0 + y3, b = y1 + y2, c = y1 - y2, d = y0 - y3;
            coefficients[k] = (a + b + 8) >> 4;
            coefficients[8 + k] = (a - b + 8) >> 4;
            coefficients[4 + k] = (d * 5352 + c * 2217 + 32768) >> 16;
            coefficients[12 + k] = (d * 2217 - c * 5352 + 32768) >> 16;
        }
    }

    /// <summary>Forward Walsh–Hadamard transform of the 16 luma DC values (block raster order).</summary>
    public static void ForwardWalsh(ReadOnlySpan<int> dc, Span<int> coefficients)
    {
        Span<int> t = stackalloc int[16];
        for (int r = 0; r < 4; r++)
        {
            int x0 = dc[r * 4], x1 = dc[r * 4 + 1], x2 = dc[r * 4 + 2], x3 = dc[r * 4 + 3];
            t[r * 4] = x0 + x1 + x2 + x3;
            t[r * 4 + 1] = x0 + x1 - x2 - x3;
            t[r * 4 + 2] = x0 - x1 - x2 + x3;
            t[r * 4 + 3] = x0 - x1 + x2 - x3;
        }

        for (int k = 0; k < 4; k++)
        {
            int y0 = t[k], y1 = t[4 + k], y2 = t[8 + k], y3 = t[12 + k];
            coefficients[k] = Half(y0 + y1 + y2 + y3);
            coefficients[4 + k] = Half(y0 + y1 - y2 - y3);
            coefficients[8 + k] = Half(y0 - y1 - y2 + y3);
            coefficients[12 + k] = Half(y0 - y1 + y2 - y3);
        }
    }

    // Halves, rounding half away from zero.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Half(int v) => v >= 0 ? (v + 1) >> 1 : -((1 - v) >> 1);
}
