using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using Sdcb.SimdCodecs.AotSmoke;
using Sdcb.SimdCodecs.Jpeg;

// One "label<TAB>sha256" line per decode on stdout (diffed by run.sh); the vector
// tier this process dispatches to goes to stderr, for the log.
Console.Error.WriteLine(
    $"aot-smoke: V128={Vector128.IsHardwareAccelerated} " +
    $"V256={Vector256.IsHardwareAccelerated} V512={Vector512.IsHardwareAccelerated}");

var count = 0;
foreach (var (w, h) in new[] { (67, 45), (16, 16), (1, 1), (130, 7), (333, 21) })
{
    foreach (var quality in new[] { 30, 75, 97 })
    {
        foreach (var subsampling in new[] { JpegSubsampling.Chroma420, JpegSubsampling.Chroma444 })
        {
            var jpeg = JpegEncoder.Encode(Pattern(w, h), w, h, 3, new JpegEncodeOptions { Quality = quality, Subsampling = subsampling });
            var info = JpegDecoder.ReadInfo(jpeg);
            foreach (var scale in new[] { JpegScale.Full, JpegScale.Half, JpegScale.Quarter, JpegScale.Eighth })
            {
                var (sw, sh) = info.ScaledSize(scale);
                foreach (var format in new[] { JpegPixelFormat.Rgba32, JpegPixelFormat.Bgra32, JpegPixelFormat.Rgb24 })
                {
                    var bpp = format == JpegPixelFormat.Rgb24 ? 3 : 4;
                    var dst = new byte[sw * sh * bpp];
                    JpegDecoder.DecodeTo(jpeg, dst, sw * bpp, format, scale);
                    Console.WriteLine($"{w}x{h} q{quality} {subsampling} {scale} {format}\t{Convert.ToHexString(SHA256.HashData(dst))}");
                    count++;
                }
            }
        }
    }
}

var facade = await FacadeSmoke.RunAsync();
Console.Error.WriteLine($"aot-smoke: {count} decodes, {facade} facade images");
return 0;

// The partial class lets FacadeSmoke share the pattern.
internal static partial class Program
{
    internal static byte[] Pattern(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        var seed = 0x2545F491u;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                seed = seed * 1664525u + 1013904223u;
                var i = (y * w + x) * 3;
                rgb[i] = (byte)(x * 255 / Math.Max(1, w - 1) ^ (seed >> 29));
                rgb[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1) + (((x >> 2) ^ (y >> 2)) & 1) * 40);
                rgb[i + 2] = (byte)(128 + 100 * Math.Sin(x * 0.3 + y * 0.1));
            }
        }

        return rgb;
    }
}
