using System.Runtime.CompilerServices;

namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// PNG row unfilter for FlateDecode with Predictor 10-15.
/// The dual of <see cref="PngWriter"/>'s filter encoders:
/// same Sub / Up / Average / Paeth formulas with the signs flipped.
/// Shared between TIFF (PNG → TIFF Deflate) and PDF (FlateDecode streams).
/// </summary>
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
        var prevRow = new byte[rowLength];

        for (var row = 0; row < numRows; row++)
        {
            var inputRow = input.Slice(row * rowWithFilter, rowWithFilter);
            var filterType = inputRow[0];
            var filteredData = inputRow[1..];
            var outputRow = output.AsSpan(row * rowLength, rowLength);

            switch (filterType)
            {
                case 0: // None
                    filteredData.CopyTo(outputRow);
                    break;
                case 1: // Sub
                    UnfilterSub(filteredData, outputRow, bytesPerComponent);
                    break;
                case 2: // Up
                    UnfilterUp(filteredData, outputRow, prevRow);
                    break;
                case 3: // Average
                    UnfilterAverage(filteredData, outputRow, prevRow, bytesPerComponent);
                    break;
                case 4: // Paeth
                    UnfilterPaeth(filteredData, outputRow, prevRow, bytesPerComponent);
                    break;
                default:
                    filteredData.CopyTo(outputRow);
                    break;
            }

            outputRow.CopyTo(prevRow);
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
        switch (filterType)
        {
            case 1: // Sub
                UnfilterSub(row, row, bytesPerComponent);
                break;
            case 2: // Up
                UnfilterUp(row, row, prevRow);
                break;
            case 3: // Average
                UnfilterAverage(row, row, prevRow, bytesPerComponent);
                break;
            case 4: // Paeth
                UnfilterPaeth(row, row, prevRow, bytesPerComponent);
                break;
            // None, and an unknown type, leave the bytes as they are, as Unfilter copies them.
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

    private static void UnfilterSub(ReadOnlySpan<byte> filtered, Span<byte> output, int bpp)
    {
        for (var i = 0; i < bpp && i < filtered.Length; i++)
            output[i] = filtered[i];
        for (var i = bpp; i < filtered.Length; i++)
            output[i] = (byte)(filtered[i] + output[i - bpp]);
    }

    private static void UnfilterUp(ReadOnlySpan<byte> filtered, Span<byte> output, ReadOnlySpan<byte> prevRow)
    {
        for (var i = 0; i < filtered.Length; i++)
            output[i] = (byte)(filtered[i] + prevRow[i]);
    }

    private static void UnfilterAverage(ReadOnlySpan<byte> filtered, Span<byte> output,
        ReadOnlySpan<byte> prevRow, int bpp)
    {
        for (var i = 0; i < filtered.Length; i++)
        {
            var left = i >= bpp ? output[i - bpp] : (byte)0;
            output[i] = (byte)(filtered[i] + (left + prevRow[i]) / 2);
        }
    }

    private static void UnfilterPaeth(ReadOnlySpan<byte> filtered, Span<byte> output,
        ReadOnlySpan<byte> prevRow, int bpp)
    {
        for (var i = 0; i < filtered.Length; i++)
        {
            var left = i >= bpp ? output[i - bpp] : 0;
            var above = (int)prevRow[i];
            var upperLeft = i >= bpp ? (int)prevRow[i - bpp] : 0;
            output[i] = (byte)(filtered[i] + PaethPredictor(left, above, upperLeft));
        }
    }
}
