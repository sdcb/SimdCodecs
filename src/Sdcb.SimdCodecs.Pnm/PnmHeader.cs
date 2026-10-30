using System.Globalization;
using System.Text;

namespace Sdcb.SimdCodecs.Pnm;

/// <summary>
/// Netpbm headers, from the Netpbm format pages (pbm, pgm, ppm, pam) and the PFM description: a magic number,
/// then whitespace-separated decimal fields where <c>#</c> starts a comment that runs to the end of the line,
/// then — for the raw formats — exactly one whitespace byte before the raster. PAM has keyword lines ending in
/// <c>ENDHDR</c>.
/// </summary>
internal static class PnmHeader
{
    /// <summary>The largest image read or written: 2^28 pixels.</summary>
    public const long MaximumPixels = 1L << 28;

    public static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r';

    /// <summary>P1 to P7 followed by whitespace.</summary>
    public static bool HasPnmSignature(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == 'P' && data[1] is >= (byte)'1' and <= (byte)'7' && IsSpace(data[2]);

    /// <summary><c>Pf</c> or <c>PF</c> followed by whitespace.</summary>
    public static bool HasPfmSignature(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == 'P' && data[1] is (byte)'f' or (byte)'F' && IsSpace(data[2]);

    /// <summary>Reads the header.</summary>
    /// <param name="data">The file.</param>
    /// <param name="rasterOffset">Where the raster starts.</param>
    /// <param name="tupleType">Whether to make a PAM tuple type's string; decoding into a buffer skips it, to
    /// allocate nothing.</param>
    /// <exception cref="InvalidDataException">The header is malformed or the image too large.</exception>
    public static PnmInfo Read(ReadOnlySpan<byte> data, out int rasterOffset, bool tupleType = true)
    {
        if (HasPfmSignature(data)) return ReadPfm(data, out rasterOffset);
        if (!HasPnmSignature(data)) throw new InvalidDataException("Not a Netpbm image.");
        var format = (PnmFormat)(data[1] - '0');
        if (format == PnmFormat.Arbitrary) return ReadPam(data, out rasterOffset, tupleType);

        var p = 2;
        var width = Number(data, ref p, "width");
        var height = Number(data, ref p, "height");
        var bitmap = format is PnmFormat.PlainBitmap or PnmFormat.Bitmap;
        var maxValue = bitmap ? 1 : Number(data, ref p, "maximum value");
        if (maxValue is < 1 or > 65535)
            throw new InvalidDataException($"Netpbm maximum value {maxValue} is not 1..65535.");
        Check(width, height);

        // One whitespace byte ends the header; the plain formats skip any amount before each sample anyway.
        if (p >= data.Length || !IsSpace(data[p]))
            throw new InvalidDataException("Netpbm header does not end in whitespace.");
        rasterOffset = p + 1;
        var channels = format is PnmFormat.PlainPixmap or PnmFormat.Pixmap ? 3 : 1;
        return new PnmInfo(width, height, format, channels, maxValue, null, 0);
    }

    private static PnmInfo ReadPam(ReadOnlySpan<byte> data, out int rasterOffset, bool keepTupleType)
    {
        int width = -1, height = -1, depth = -1, maxValue = -1;
        StringBuilder? tupleType = null;
        var p = 2;
        while (true)
        {
            SkipSpace(data, ref p);
            var start = p;
            while (p < data.Length && !IsSpace(data[p])) p++;
            var keyword = data[start..p];
            if (keyword.SequenceEqual("ENDHDR"u8))
            {
                // The raster starts after the newline that ends this line.
                while (p < data.Length && data[p] != '\n')
                {
                    if (!IsSpace(data[p])) throw new InvalidDataException("PAM ENDHDR line has trailing text.");
                    p++;
                }
                if (p >= data.Length) throw new InvalidDataException("PAM header ends without a newline after ENDHDR.");
                rasterOffset = p + 1;
                break;
            }
            if (keyword.SequenceEqual("TUPLTYPE"u8))
            {
                while (p < data.Length && data[p] is (byte)' ' or (byte)'\t') p++;
                var valueStart = p;
                while (p < data.Length && data[p] is not ((byte)'\n' or (byte)'\r')) p++;
                if (p - valueStart > 256) throw new InvalidDataException("PAM tuple type is too long.");
                if (keepTupleType)
                {
                    var value = Encoding.ASCII.GetString(data[valueStart..p]).TrimEnd();
                    tupleType = tupleType is null ? new StringBuilder(value) : tupleType.Append(' ').Append(value);
                    if (tupleType.Length > 256) throw new InvalidDataException("PAM tuple type is too long.");
                }
                continue;
            }

            ref var field = ref keyword.SequenceEqual("WIDTH"u8) ? ref width
                : ref keyword.SequenceEqual("HEIGHT"u8) ? ref height
                : ref keyword.SequenceEqual("DEPTH"u8) ? ref depth
                : ref keyword.SequenceEqual("MAXVAL"u8) ? ref maxValue
                : ref Unknown(keyword);
            if (field >= 0) throw new InvalidDataException($"PAM header repeats {Encoding.ASCII.GetString(keyword)}.");
            field = Number(data, ref p, "PAM header field");
        }

        if (width < 0 || height < 0 || depth < 0 || maxValue < 0)
            throw new InvalidDataException("PAM header lacks WIDTH, HEIGHT, DEPTH or MAXVAL.");
        if (depth is < 1 or > 4)
            throw new InvalidDataException($"PAM depth {depth} is not 1..4.");
        if (maxValue is < 1 or > 65535)
            throw new InvalidDataException($"PAM maximum value {maxValue} is not 1..65535.");
        Check(width, height);
        return new PnmInfo(width, height, PnmFormat.Arbitrary, depth, maxValue, tupleType?.ToString(), 0);
    }

    private static ref int Unknown(ReadOnlySpan<byte> keyword) =>
        throw new InvalidDataException(keyword.IsEmpty ? "PAM header ends before ENDHDR." : $"PAM header has an unknown keyword '{Encoding.ASCII.GetString(keyword)}'.");

    private static PnmInfo ReadPfm(ReadOnlySpan<byte> data, out int rasterOffset)
    {
        var format = data[1] == 'F' ? PnmFormat.FloatPixmap : PnmFormat.FloatGraymap;
        var p = 2;
        var width = Number(data, ref p, "width");
        var height = Number(data, ref p, "height");
        SkipSpace(data, ref p);
        var start = p;
        while (p < data.Length && !IsSpace(data[p])) p++;
        if (p - start > 64
            || !float.TryParse(data[start..p], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
            || scale == 0 || !float.IsFinite(scale))
            throw new InvalidDataException("PFM scale is not a non-zero number.");
        Check(width, height);
        if (p >= data.Length) throw new InvalidDataException("PFM header does not end in whitespace.");
        rasterOffset = p + 1;
        return new PnmInfo(width, height, format, format == PnmFormat.FloatPixmap ? 3 : 1, 0, null, scale);
    }

    private static void Check(int width, int height)
    {
        if (width == 0 || height == 0)
            throw new InvalidDataException("Netpbm image has no pixels.");
        if ((long)width * height > MaximumPixels)
            throw new InvalidDataException($"Netpbm size {width}x{height} is above the limit of {MaximumPixels:N0} pixels.");
    }

    /// <summary>Skips whitespace and comments.</summary>
    public static void SkipSpace(ReadOnlySpan<byte> data, ref int p)
    {
        while (p < data.Length)
        {
            var b = data[p];
            if (IsSpace(b))
            {
                p++;
            }
            else if (b == '#')
            {
                while (p < data.Length && data[p] is not ((byte)'\n' or (byte)'\r')) p++;
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>A non-negative decimal header field after optional whitespace and comments, at most
    /// <see cref="int.MaxValue"/>.</summary>
    private static int Number(ReadOnlySpan<byte> data, ref int p, string what)
    {
        SkipSpace(data, ref p);
        var start = p;
        long value = 0;
        while (p < data.Length && data[p] is >= (byte)'0' and <= (byte)'9')
        {
            value = value * 10 + (data[p] - '0');
            if (value > int.MaxValue) throw new InvalidDataException($"Netpbm {what} is too large.");
            p++;
        }
        if (p == start) throw new InvalidDataException($"Netpbm {what} is missing.");
        if (p < data.Length && !IsSpace(data[p]) && data[p] != '#')
            throw new InvalidDataException($"Netpbm {what} is not a number.");
        return (int)value;
    }
}
