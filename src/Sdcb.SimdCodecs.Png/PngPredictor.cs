using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// PNG row unfilter for FlateDecode with Predictor 10-15.
/// The dual of <see cref="PngWriter"/>'s filter encoders:
/// same Sub / Up / Average / Paeth formulas with the signs flipped.
/// Shared between TIFF (PNG → TIFF Deflate) and PDF (FlateDecode streams).
/// </summary>
/// <remarks>
/// <para>Up has no dependency along the row and is added sixteen bytes at a time. Sub, Average and
/// Paeth each need the pixel to the left already unfiltered, a chain no vector can cut across, so
/// they are vectorised the other way: one pixel per step, all its bytes (up to eight) in one vector.
/// For RGB and RGBA that turns three or four serial byte chains, with Paeth's data-dependent branches
/// (which mispredict about half the time on photographs), into one short arithmetic chain per pixel.
/// Average's floor((a + b) / 2) is UHADD on AdvSimd, and pavgb's ceiling minus the lost low bit on x86;
/// Paeth is computed in 16-bit lanes and chosen with compare masks, no branches. A one-byte pixel, and
/// a CPU without 128-bit vectors, take the scalar formulas, which are also the tail of every row.</para>
/// </remarks>
public static class PngPredictor
{
    /// <summary>
    /// Unfilter PNG-predicted data. Each row is prefixed with a 1-byte filter type.
    /// </summary>
    /// <param name="input">Raw inflated data (with filter bytes).</param>
    /// <param name="columns">Number of data bytes per row (excluding filter byte).</param>
    /// <param name="bytesPerComponent">Bytes per component (typically 1).</param>
    /// <returns>Unfiltered data (without filter bytes).</returns>
    public static byte[] Unfilter(ReadOnlySpan<byte> input, int columns, int bytesPerComponent = 1)
    {
        var rowLength = columns;
        var rowWithFilter = rowLength + 1;
        var numRows = input.Length / rowWithFilter;
        var output = new byte[numRows * rowLength];
        var firstRowAbove = new byte[rowLength];

        for (var row = 0; row < numRows; row++)
        {
            var inputRow = input.Slice(row * rowWithFilter, rowWithFilter);
            var outputRow = output.AsSpan(row * rowLength, rowLength);
            inputRow[1..].CopyTo(outputRow);
            // An unknown filter type leaves the bytes as they are, as None does.
            var above = row == 0 ? firstRowAbove : output.AsSpan((row - 1) * rowLength, rowLength);
            UnfilterRow(inputRow[0], outputRow, above, bytesPerComponent);
        }

        return output;
    }

    /// <summary>
    /// Unfilters one row in place: <paramref name="row"/> holds the filtered bytes (without the filter
    /// byte) and is left holding the samples. Every filter reads only bytes left of the one it writes, and
    /// the previous row, so the row can be its own input. <paramref name="prevRow"/> is the previous row's
    /// samples, all zero for the first row.
    /// </summary>
    internal static void UnfilterRow(byte filterType, Span<byte> row, ReadOnlySpan<byte> prevRow, int bytesPerComponent)
    {
        if (filterType is 0 or > 4)
            return; // None, and an unknown type, leave the bytes as they are, as Unfilter copies them.
        if (prevRow.Length < row.Length)
            throw new ArgumentException($"Previous row too short: {prevRow.Length} < {row.Length}", nameof(prevRow));
        if (bytesPerComponent < 1)
            throw new ArgumentOutOfRangeException(nameof(bytesPerComponent));

        switch (filterType)
        {
            case 1: // Sub
                UnfilterSub(row, bytesPerComponent);
                break;
            case 2: // Up
                UnfilterUp(row, prevRow);
                break;
            case 3: // Average
                UnfilterAverage(row, prevRow, bytesPerComponent);
                break;
            default: // Paeth
                UnfilterPaeth(row, prevRow, bytesPerComponent);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PaethPredictor(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = p >= a ? p - a : a - p;
        var pb = p >= b ? p - b : b - p;
        var pc = p >= c ? p - c : c - p;
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    // ---- per-pixel vectors -------------------------------------------------------------------
    // A pixel of bpp bytes is loaded as 4 (bpp <= 4) or 8 bytes into the low lanes of a vector, and
    // exactly its bpp bytes are stored back (StorePixel). Storing the whole 4 or 8 bytes would be
    // simpler but slower: the next pixel's load starts inside that store, and a load that only
    // partly overlaps an earlier store cannot be forwarded from it, so every pixel of RGB (or grey +
    // alpha) waited for the store to retire - slower than the scalar code. Loops run while
    // i + width <= the row's length, which with prevRow.Length >= row.Length (checked in UnfilterRow)
    // bounds every unchecked access; the scalar tail finishes the row.

    private static bool VectorPixels(int bpp) => bpp is >= 2 and <= 8 && Vector128.IsHardwareAccelerated;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> LoadPixel(ref byte source, int offset, bool wide) => wide
        ? Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, offset))).AsByte()
        : Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, offset))).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StorePixel(ref byte destination, int offset, Vector128<byte> value, int bpp)
    {
        ref var d = ref Unsafe.Add(ref destination, offset);
        switch (bpp)
        {
            case 2:
                Unsafe.WriteUnaligned(ref d, value.AsUInt16().ToScalar());
                break;
            case 3:
                Unsafe.WriteUnaligned(ref d, value.AsUInt16().ToScalar());
                Unsafe.Add(ref d, 2) = value.GetElement(2);
                break;
            case 4:
                Unsafe.WriteUnaligned(ref d, value.AsUInt32().ToScalar());
                break;
            case 5:
                Unsafe.WriteUnaligned(ref d, value.AsUInt32().ToScalar());
                Unsafe.Add(ref d, 4) = value.GetElement(4);
                break;
            case 6:
                Unsafe.WriteUnaligned(ref d, value.AsUInt32().ToScalar());
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 4), value.AsUInt16().GetElement(2));
                break;
            case 7:
                Unsafe.WriteUnaligned(ref d, value.AsUInt32().ToScalar());
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 4), value.AsUInt16().GetElement(2));
                Unsafe.Add(ref d, 6) = value.GetElement(6);
                break;
            default:
                Unsafe.WriteUnaligned(ref d, value.AsUInt64().ToScalar());
                break;
        }
    }

    // floor((a + b) / 2) per byte.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> FloorAverage(Vector128<byte> a, Vector128<byte> b)
    {
        if (AdvSimd.IsSupported)
            return AdvSimd.FusedAddHalving(a, b);
        if (Sse2.IsSupported)
            return Sse2.Average(a, b) - ((a ^ b) & Vector128.Create((byte)1)); // pavgb rounds up
        return (a & b) + Vector128.ShiftRightLogical(a ^ b, 1);
    }

    private static void UnfilterUp(Span<byte> row, ReadOnlySpan<byte> prevRow)
    {
        var n = row.Length;
        var i = 0;
        ref var r = ref MemoryMarshal.GetReference(row);
        ref var p = ref MemoryMarshal.GetReference(prevRow);
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 32 <= n; i += 32)
                (Vector256.LoadUnsafe(ref r, (nuint)i) + Vector256.LoadUnsafe(ref p, (nuint)i)).StoreUnsafe(ref r, (nuint)i);
        }
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 16 <= n; i += 16)
                (Vector128.LoadUnsafe(ref r, (nuint)i) + Vector128.LoadUnsafe(ref p, (nuint)i)).StoreUnsafe(ref r, (nuint)i);
        }
        for (; i < n; i++)
            row[i] = (byte)(row[i] + prevRow[i]);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void UnfilterSub(Span<byte> row, int bpp)
    {
        var n = row.Length;
        var i = bpp; // the first pixel has nothing to its left
        var wide = bpp > 4;
        var width = wide ? 8 : 4;
        if (VectorPixels(bpp) && width <= n)
        {
            ref var r = ref MemoryMarshal.GetReference(row);
            var left = LoadPixel(ref r, 0, wide);
            for (; i + width <= n; i += bpp)
            {
                var filtered = LoadPixel(ref r, i, wide);
                left += filtered;
                StorePixel(ref r, i, left, bpp);
            }
        }

        for (; i < n; i++)
            row[i] = (byte)(row[i] + row[i - bpp]);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void UnfilterAverage(Span<byte> row, ReadOnlySpan<byte> prevRow, int bpp)
    {
        var n = row.Length;
        var i = 0;
        var wide = bpp > 4;
        var width = wide ? 8 : 4;
        if (VectorPixels(bpp))
        {
            ref var r = ref MemoryMarshal.GetReference(row);
            ref var p = ref MemoryMarshal.GetReference(prevRow);
            var left = Vector128<byte>.Zero; // left of the first pixel
            for (; i + width <= n; i += bpp)
            {
                var filtered = LoadPixel(ref r, i, wide);
                left = filtered + FloorAverage(left, LoadPixel(ref p, i, wide));
                StorePixel(ref r, i, left, bpp);
            }
        }

        for (; i < n; i++)
        {
            var left = i >= bpp ? row[i - bpp] : 0;
            row[i] = (byte)(row[i] + ((left + prevRow[i]) >> 1));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void UnfilterPaeth(Span<byte> row, ReadOnlySpan<byte> prevRow, int bpp)
    {
        var n = row.Length;
        var i = 0;
        var wide = bpp > 4;
        var width = wide ? 8 : 4;
        if (VectorPixels(bpp))
        {
            ref var r = ref MemoryMarshal.GetReference(row);
            ref var p = ref MemoryMarshal.GetReference(prevRow);
            var low = Vector128.Create((short)0xFF);
            // Left (a), above (b) and upper-left (c) in 16-bit lanes; a and c are 0 for the first pixel.
            Vector128<short> a = Vector128<short>.Zero, c = Vector128<short>.Zero;
            for (; i + width <= n; i += bpp)
            {
                var filtered = LoadPixel(ref r, i, wide);
                var b = Vector128.WidenLower(LoadPixel(ref p, i, wide)).AsInt16();
                // p = a + b - c; pa = |p - a| = |b - c|, pb = |a - c|, pc = |(a - c) + (b - c)|.
                var bc = b - c;
                var ac = a - c;
                var pa = Vector128.Abs(bc);
                var pb = Vector128.Abs(ac);
                var pc = Vector128.Abs(ac + bc);
                var takeA = Vector128.LessThanOrEqual(pa, pb) & Vector128.LessThanOrEqual(pa, pc);
                var takeB = Vector128.LessThanOrEqual(pb, pc);
                var predicted = Vector128.ConditionalSelect(takeA, a, Vector128.ConditionalSelect(takeB, b, c));
                a = (predicted + Vector128.WidenLower(filtered).AsInt16()) & low;
                c = b;
                StorePixel(ref r, i, Vector128.Narrow(a.AsUInt16(), a.AsUInt16()), bpp);
            }
        }

        for (; i < n; i++)
        {
            var left = i >= bpp ? row[i - bpp] : 0;
            var above = (int)prevRow[i];
            var upperLeft = i >= bpp ? (int)prevRow[i - bpp] : 0;
            row[i] = (byte)(row[i] + PaethPredictor(left, above, upperLeft));
        }
    }
}
