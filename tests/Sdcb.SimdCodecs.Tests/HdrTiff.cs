using System.Buffers.Binary;
using Sdcb.SimdCodecs.Tiff;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Test helper that writes strict <b>uncompressed</b> 16-bit / float / half TIFFs, plus sample
/// packers, for the high-bit-depth facade tests.
/// Built on <see cref="Sdcb.SimdCodecs.Tiff.TiffWriter"/> (which supports uncompressed 16/32-bit).
/// </summary>
internal static class HdrTiff
{
    public static byte[] Uint16Gray(int w, int h, int[] y)
    {
        var px = new byte[w * h * 2];
        for (var i = 0; i < w * h; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(px.AsSpan(i * 2, 2), (ushort)y[i]);
        return Write(px, w, h, 1, 16, TiffSampleFormat.Uint, TiffPhotometric.MinIsBlack);
    }

    public static byte[] Uint16Rgb(int w, int h, int[] r, int[] g, int[] b)
    {
        var px = new byte[w * h * 3 * 2];
        for (var i = 0; i < w * h; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(px.AsSpan((i * 3 + 0) * 2, 2), (ushort)r[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(px.AsSpan((i * 3 + 1) * 2, 2), (ushort)g[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(px.AsSpan((i * 3 + 2) * 2, 2), (ushort)b[i]);
        }
        return Write(px, w, h, 3, 16, TiffSampleFormat.Uint, TiffPhotometric.Rgb);
    }

    public static byte[] Float32Gray(int w, int h, float[] y)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
            BinaryPrimitives.WriteSingleLittleEndian(px.AsSpan(i * 4, 4), y[i]);
        return Write(px, w, h, 1, 32, TiffSampleFormat.IeeeFloat, TiffPhotometric.MinIsBlack);
    }

    public static byte[] HalfGray(int w, int h, Half[] y)
    {
        var px = new byte[w * h * 2];
        for (var i = 0; i < w * h; i++)
            BinaryPrimitives.WriteInt16LittleEndian(px.AsSpan(i * 2, 2), BitConverter.HalfToInt16Bits(y[i]));
        return Write(px, w, h, 1, 16, TiffSampleFormat.IeeeFloat, TiffPhotometric.MinIsBlack);
    }

    public static byte[] HalfRgb(int w, int h, Half[] rgb)
    {
        var px = new byte[w * h * 3 * 2];
        for (var i = 0; i < w * h * 3; i++)
            BinaryPrimitives.WriteInt16LittleEndian(px.AsSpan(i * 2, 2), BitConverter.HalfToInt16Bits(rgb[i]));
        return Write(px, w, h, 3, 16, TiffSampleFormat.IeeeFloat, TiffPhotometric.Rgb);
    }

    private static byte[] Write(byte[] pixels, int w, int h, int spp, int bps, TiffSampleFormat fmt, TiffPhotometric photo)
    {
        using var ms = new MemoryStream();
        var options = new TiffPageOptions
        {
            Compression = TiffCompression.Uncompressed,
            SamplesPerPixel = spp,
            BitsPerSample = bps,
            SampleFormat = fmt,
            Photometric = photo,
            RowsPerStrip = 0, // one strip
        };
        // TiffWriter is async; drive it synchronously for the test harness.
        var t = WriteAsync(ms, pixels, w, h, options);
        t.GetAwaiter().GetResult();
        return ms.ToArray();
    }

    private static async Task WriteAsync(Stream ms, byte[] pixels, int w, int h, TiffPageOptions options)
    {
        await using var writer = TiffWriter.Create(ms);
        await writer.AddPageAsync(pixels, w, h, options);
        await writer.FlushAsync();
    }
}
