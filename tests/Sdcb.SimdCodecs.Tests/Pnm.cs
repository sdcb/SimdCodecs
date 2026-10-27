namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// A minimal reader for binary PNM (<c>P5</c> greyscale, <c>P6</c> RGB) — the
/// interchange format the JPEG 2000 fixtures and the OpenJPEG oracle both speak.
/// <para>
/// It exists because <c>opj_decompress</c> stamps a comment line into the header
/// it writes (<c>P5\n#OpenJPEG-2.5.4\n…</c>), so a decoded <c>.pgm</c> is never
/// byte-identical to the source <c>.pgm</c> even when every pixel matches. The
/// comparison has to be on parsed payloads, and skipping <c>#</c> comments is
/// the whole reason this is not four lines of <c>Split</c>.
/// </para>
/// </summary>
internal static class Pnm
{
    /// <summary>A decoded PNM: interleaved samples, <see cref="Components"/> per pixel.</summary>
    /// <param name="Width">Pixels across.</param>
    /// <param name="Height">Pixels down.</param>
    /// <param name="Components">1 for <c>P5</c>, 3 for <c>P6</c>.</param>
    /// <param name="MaxValue">Header maxval; above 255 the samples are 16-bit.</param>
    /// <param name="Samples">Interleaved samples, row-major, already widened from big-endian when 16-bit.</param>
    internal sealed record Image(int Width, int Height, int Components, int MaxValue, ushort[] Samples);

    /// <summary>Reads a binary PNM file.</summary>
    public static Image Read(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Reads a binary PNM from memory.</summary>
    public static Image Parse(ReadOnlySpan<byte> bytes)
    {
        var offset = 0;
        var magic = NextToken(bytes, ref offset);
        var width = int.Parse(NextToken(bytes, ref offset));
        var height = int.Parse(NextToken(bytes, ref offset));
        var maxValue = int.Parse(NextToken(bytes, ref offset));

        var components = magic switch
        {
            "P5" => 1,
            "P6" => 3,
            _ => throw new InvalidDataException(
                $"Only binary PNM (P5/P6) is supported here; got '{magic}'."),
        };

        // Exactly ONE whitespace byte separates the maxval from the raster, and
        // it is part of the format rather than something to skip greedily: a
        // raster whose first sample happens to be 0x20 would lose a pixel.
        offset++;

        var count = checked(width * height * components);
        var samples = new ushort[count];
        if (maxValue > 255)
        {
            if (bytes.Length - offset < count * 2)
                throw new InvalidDataException("PNM raster is short.");
            for (var i = 0; i < count; i++)
                samples[i] = (ushort)((bytes[offset + i * 2] << 8) | bytes[offset + i * 2 + 1]);
        }
        else
        {
            if (bytes.Length - offset < count)
                throw new InvalidDataException("PNM raster is short.");
            for (var i = 0; i < count; i++) samples[i] = bytes[offset + i];
        }

        return new Image(width, height, components, maxValue, samples);
    }

    private static string NextToken(ReadOnlySpan<byte> bytes, ref int offset)
    {
        while (true)
        {
            while (offset < bytes.Length && char.IsWhiteSpace((char)bytes[offset])) offset++;
            if (offset < bytes.Length && bytes[offset] == (byte)'#')
            {
                while (offset < bytes.Length && bytes[offset] != (byte)'\n') offset++;
                continue;
            }

            break;
        }

        var start = offset;
        while (offset < bytes.Length && !char.IsWhiteSpace((char)bytes[offset])) offset++;
        if (offset == start) throw new InvalidDataException("Truncated PNM header.");

        return System.Text.Encoding.ASCII.GetString(bytes[start..offset]);
    }
}
