using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Gif;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;
using Sdcb.SimdCodecs.Tiff;

namespace Sdcb.SimdCodecs.AotSmoke;

/// <summary>
/// Every entry point of the <c>SimdCodecs</c> facade on one image of each registered format. The
/// facade binds each codec's static members into delegates, which is exactly what an ahead-of-time
/// compiler can get wrong (Mono AOT on browser-wasm once trapped with "function signature mismatch"
/// on the first call), so this runs under NativeAOT and Mono AOT and must print what the JIT prints.
/// </summary>
internal static class FacadeSmoke
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        foreach (var (label, data) in await SamplesAsync())
        {
            Check(SimdCodecs.CanDecode(data), label, "CanDecode");
            Check(SimdCodecs.TryReadInfo(data, out var info), label, "TryReadInfo");
            Check(SimdCodecs.TryDecode(data, out var image), label, "TryDecode");
            var rgba = new byte[info.Width * info.Height * 4];
            Check(SimdCodecs.TryDecodeIntoRgba8(data, rgba), label, "TryDecodeIntoRgba8");
            Check(SimdCodecs.TryOpenFrames(data, out var reader), label, "TryOpenFrames");
            using (reader)
            {
                var last = reader.FrameCount - 1;
                Check(reader.TryDecodeFrame(last, out var frame), label, "TryDecodeFrame");
                Console.WriteLine(
                    $"facade {label} {SimdCodecs.DetectFormat(data)}/{info.Format} {info.Width}x{info.Height}x{info.Channels} {info.SampleFormat} frames={reader.FrameCount}" +
                    $"\t{Hash(image.ToRgba8())} {Hash(rgba)} {Hash(frame.ToRgba8())}");
            }

            count++;
        }

        Check(!SimdCodecs.CanDecode("not an image"u8), "garbage", "CanDecode");
        return count;
    }

    private static async Task<List<(string, byte[])>> SamplesAsync()
    {
        const int w = 37, h = 23;
        var rgb = Program.Pattern(w, h);
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            rgb.AsSpan(i * 3, 3).CopyTo(rgba.AsSpan(i * 4));
            rgba[i * 4 + 3] = (byte)(i * 7);
        }

        using var tiff = new MemoryStream();
        await using (var writer = TiffWriter.Create(tiff))
        {
            await writer.AddPageAsync(rgb, w, h);
            await writer.AddPageAsync(rgb.Reverse().ToArray(), w, h);
            await writer.FlushAsync();
        }

        return
        [
            ("png", PngWriter.Encode(rgba, w, h)),
            ("jpeg", JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = 80 })),
            ("tiff", tiff.ToArray()),
            ("jbig2", Fixture("sym.jb2")),
            ("j2k", Fixture("rgb-struct64.j2k")),
            ("jp2", Fixture("jp2-rgb-struct64.jp2")),
            ("webp-lossy", Fixture("astronaut-192-q20-normal.webp")),
            ("webp-lossless", Fixture("lossless-palette-16.webp")),
            ("webp-anim", Fixture("anim-img2webp-mixed.webp")),
            ("gif", GifEncoder.Encode(rgb, w, h, 3)),
            ("gif-anim", GifEncoder.EncodeAnimation(
                [new GifAnimationFrame(rgb, TimeSpan.FromMilliseconds(50)), new GifAnimationFrame(rgb.Reverse().ToArray(), TimeSpan.FromMilliseconds(50))],
                w, h, 3)),
        ];
    }

    // The test project's fixtures, embedded by file name (see the .csproj): browser-wasm has no file system.
    private static byte[] Fixture(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"fixture {name} is not embedded");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..16];

    private static void Check([DoesNotReturnIf(false)] bool ok, string label, string call)
    {
        if (!ok)
            throw new InvalidOperationException($"facade {label}: {call} failed");
    }
}
