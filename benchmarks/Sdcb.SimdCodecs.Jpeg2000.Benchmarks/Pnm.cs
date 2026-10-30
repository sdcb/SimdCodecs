using System;
using System.IO;

namespace Sdcb.SimdCodecs.Jpeg2000.Benchmarks;

internal static class Pnm
{
    internal sealed record Image(int Width, int Height, int Components, int MaxValue, ushort[] Samples);

    public static Image Read(string path) => Parse(File.ReadAllBytes(path));

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
            _ => throw new InvalidDataException($"Only binary PNM (P5/P6) is supported; got '{magic}'."),
        };

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
