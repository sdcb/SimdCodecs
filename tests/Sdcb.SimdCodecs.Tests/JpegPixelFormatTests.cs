using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="JpegDecoder.DecodeTo(ReadOnlySpan{byte}, Span{byte}, int, JpegPixelFormat, JpegScale)"/>:
/// every format is an exact channel permutation / projection of the golden-pinned RGBA
/// decode, rows land at the requested stride, and row padding is never touched.
/// </summary>
public sealed class JpegPixelFormatTests
{
    public static TheoryData<string, JpegPixelFormat, int> Cases()
    {
        var d = new TheoryData<string, JpegPixelFormat, int>();
        foreach (var sampling in new[] { "4:2:0", "4:4:4", "gray", "cmyk" })
            foreach (var format in new[] { JpegPixelFormat.Rgba32, JpegPixelFormat.Bgra32, JpegPixelFormat.Rgb24 })
                foreach (var scale in new[] { 1, 2 })
                    d.Add(sampling, format, scale);
        return d;
    }

    private static byte[] Encode(string sampling)
    {
        const int w = 61, h = 27;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < rgb.Length; ++i)
            rgb[i] = (byte)(i * 37 % 251);
        return sampling switch
        {
            "gray" => JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 85, gray: true),
            "cmyk" => JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 85, cmyk: true),
            _ => JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 85, sampling),
        };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Format_IsPermutationOfRgba(string sampling, JpegPixelFormat format, int scale)
    {
        var jpeg = Encode(sampling);
        var reference = JpegDecoder.Decode(jpeg, (JpegScale)scale);
        var bpp = format == JpegPixelFormat.Rgb24 ? 3 : 4;
        var stride = reference.Width * bpp + 13;
        var dest = new byte[stride * reference.Height + 5];
        Array.Fill(dest, (byte)0x5A);

        var (w, h) = JpegDecoder.DecodeTo(jpeg, dest, stride, format, (JpegScale)scale);
        (w, h).ShouldBe((reference.Width, reference.Height));

        for (var y = 0; y < h; ++y)
        {
            for (var x = 0; x < w; ++x)
            {
                var s = (y * w + x) * 4;
                var o = y * stride + x * bpp;
                byte r = reference.Pixels[s], g = reference.Pixels[s + 1], b = reference.Pixels[s + 2];
                var expected = format switch
                {
                    JpegPixelFormat.Rgba32 => new[] { r, g, b, (byte)255 },
                    JpegPixelFormat.Bgra32 => new[] { b, g, r, (byte)255 },
                    _ => new[] { r, g, b },
                };
                dest.AsSpan(o, bpp).ToArray().ShouldBe(expected, $"{sampling} {format} ({x},{y})");
            }

            dest.AsSpan(y * stride + w * bpp, y == h - 1 ? dest.Length - (y * stride + w * bpp) : stride - w * bpp)
                .ToArray().ShouldAllBe(v => v == 0x5A, $"{sampling} {format} row {y} padding");
        }
    }

    [Fact]
    public void Rejects_ShortStrideAndBuffer()
    {
        var jpeg = Encode("4:2:0");
        Should.Throw<ArgumentOutOfRangeException>(() => JpegDecoder.DecodeTo(jpeg, new byte[100000], 61 * 3 - 1, JpegPixelFormat.Rgb24));
        Should.Throw<ArgumentException>(() => JpegDecoder.DecodeTo(jpeg, new byte[61 * 4 * 27 - 1], 61 * 4, JpegPixelFormat.Bgra32));
        Should.Throw<ArgumentOutOfRangeException>(() => JpegDecoder.DecodeTo(jpeg, new byte[100000], 61 * 4, (JpegPixelFormat)42));
        // Exactly stride × (h − 1) + row is enough.
        Should.NotThrow(() => JpegDecoder.DecodeTo(jpeg, new byte[200 * 26 + 61 * 3], 200, JpegPixelFormat.Rgb24));
    }
}
