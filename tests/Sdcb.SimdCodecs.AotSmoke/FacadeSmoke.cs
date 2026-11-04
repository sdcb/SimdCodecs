using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;
using Sdcb.SimdCodecs.Gif;
using Sdcb.SimdCodecs.Heif;
using Sdcb.SimdCodecs.Ico;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;
using Sdcb.SimdCodecs.Pnm;
using Sdcb.SimdCodecs.Qoi;
using Sdcb.SimdCodecs.Webp;
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
            Exercise(label, data);
            count++;
        }

        Check(!SimdCodecs.CanDecode("not an image"u8), "garbage", "CanDecode");
        return count + Heif() + V2();
    }

    /// <summary>
    /// The 2.0 entry points, each through its registry delegate: options, metadata, thumbnail, closest size, the
    /// session API, 16-bit and premultiplied output, and every built-in encoder through the encoder registry.
    /// </summary>
    private static int V2()
    {
        const int w = 37, h = 23;
        var rgb = Program.Pattern(w, h);
        var png = SimdCodecs.Encode(rgb, w, h, PixelFormat.Rgb24, ImageFormat.Png,
            metadata: new ImageMetadata { Icc = new byte[] { 1, 2, 3 } });
        var jpeg = SimdCodecs.Encode(rgb, w, h, PixelFormat.Rgb24, ImageFormat.Jpeg);

        Check(SimdCodecs.TryGetClosestSize(jpeg, 10, 6, out var cw, out var ch, out var scale), "v2", "TryGetClosestSize");
        Check(SimdCodecs.TryReadMetadata(png, out var metadata) && metadata.Icc.Length == 3, "v2", "TryReadMetadata");
        Check(!SimdCodecs.TryDecodeThumbnail(png, out _), "v2", "TryDecodeThumbnail");
        Check(!SimdCodecs.TryReadPlanarInfo(png, out _), "v2", "TryReadPlanarInfo");

        Check(SimdCodecs.TryDecode(jpeg, out var scaled, new DecodeOptions { ScaleDenominator = scale }), "v2", "TryDecode scaled");
        Check(SimdCodecs.TryDecode(png, out var region, new DecodeOptions { Region = new PixelRect(3, 4, 10, 7) }), "v2", "TryDecode region");
        var wide = new byte[w * h * 8];
        Check(SimdCodecs.TryDecodeInto(png, wide, 0, PixelFormat.Rgba64), "v2", "TryDecodeInto Rgba64");
        var pre = new byte[w * h * 4];
        Check(SimdCodecs.TryDecodeInto(png, pre, 0, PixelFormat.Bgra32Premultiplied), "v2", "TryDecodeInto Bgra32Premultiplied");

        using var session = ImageDecoder.Open(new MemoryStream(png));
        using var frame = session.GetFrame(0).Decode();
        var crop = new byte[5 * 5 * 3];
        session.GetFrame(0).CopyPixels(new PixelRect(1, 1, 5, 5), PixelFormat.Bgr24, crop, 0);

        var encoded = new List<string>();
        foreach (var format in new[] { ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Tiff, ImageFormat.Webp, ImageFormat.Gif,
                     ImageFormat.Bmp, ImageFormat.Ico, ImageFormat.Qoi, ImageFormat.Pnm })
        {
            Check(SimdCodecs.IsEncodable(format), "v2", $"IsEncodable {format}");
            var file = SimdCodecs.Encode(rgb, w, h, PixelFormat.Bgr24, format);
            Check(SimdCodecs.DetectFormat(file) == format, "v2", $"Encode {format}");
            encoded.Add($"{format}={Hash(file)}/{SimdCodecs.GetClosestInputFormat(format, PixelFormat.Rgba64)}");
        }

        Console.WriteLine($"facade v2 closest={cw}x{ch}/{scale} scaled={scaled.Width}x{scaled.Height} region={region.Width}x{region.Height}" +
            $"	{Hash(Pixels(scaled))} {Hash(Pixels(region))} {Hash(wide)} {Hash(pre)} {Hash(Pixels(frame))} {Hash(crop)}");
        Console.WriteLine($"facade v2-encode	{string.Join(' ', encoded)}");
        return 1;
    }

    private static void Exercise(string label, byte[] data)
    {
        Check(SimdCodecs.CanDecode(data), label, "CanDecode");
        Check(SimdCodecs.TryReadInfo(data, out var info), label, "TryReadInfo");
        Check(SimdCodecs.TryDecode(data, out var image), label, "TryDecode");
        var rgba = new byte[info.Width * info.Height * 4];
        // Float samples (PFM) have no 8-bit layout without a tone policy: TryDecodeInto says no.
        Check(SimdCodecs.TryDecodeIntoRgba8(data, rgba) == (info.SampleFormat != SampleFormat.Float32), label, "TryDecodeIntoRgba8");
        Check(SimdCodecs.TryOpenFrames(data, out var reader), label, "TryOpenFrames");
        using (reader)
        {
            var last = reader.FrameCount - 1;
            Check(reader.TryDecodeFrame(last, out var frame), label, "TryDecodeFrame");
            Console.WriteLine(
                $"facade {label} {SimdCodecs.DetectFormat(data)}/{info.Format} {info.Width}x{info.Height}x{info.Channels} {info.SampleFormat} frames={reader.FrameCount}" +
                $"\t{Hash(Pixels(image))} {Hash(rgba)} {Hash(Pixels(frame))}");
        }
    }

    /// <summary>
    /// HEIF is not built in (HEVC patents): the facade recognises it, decodes none of it until
    /// <c>SimdCodecs.Register(HeifImageDecoder.Registration)</c>, then serves every entry point. The
    /// registration's delegates are bound by method group in the HEIF package, which is what Mono AOT
    /// must compile correctly. The fixtures are downloaded by Oracle/heif/fetch.sh (run.sh calls it).
    /// </summary>
    private static int Heif()
    {
        var heic = Fixture("synth-screen-testsrc2-1280x720-main8.heic");
        var pq = Fixture("synth-screen-testsrc2-640x360-pq-vui.heic");

        Check(SimdCodecs.DetectFormat(heic) == ImageFormat.Heif, "heif", "DetectFormat (unregistered)");
        Check(!SimdCodecs.IsDecodable(ImageFormat.Heif), "heif", "IsDecodable (unregistered)");
        Check(!SimdCodecs.CanDecode(heic), "heif", "CanDecode (unregistered)");
        Check(!SimdCodecs.TryReadInfo(heic, out _), "heif", "TryReadInfo (unregistered)");
        Check(!SimdCodecs.TryDecode(heic, out _), "heif", "TryDecode (unregistered)");
        Check(!SimdCodecs.TryOpenFrames(heic, out _), "heif", "TryOpenFrames (unregistered)");

        SimdCodecs.Register(HeifImageDecoder.Registration);
        SimdCodecs.Register(HeifImageDecoder.Registration); // idempotent
        Check(SimdCodecs.IsDecodable(ImageFormat.Heif), "heif", "IsDecodable");
        Exercise("heif", heic);

        Check(SimdCodecs.TryReadInfo(heic, out var info), "heif", "TryReadInfo");
        var formats = new List<string>();
        foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Bgra32, PixelFormat.Rgb24, PixelFormat.Bgr24, PixelFormat.Gray8 })
        {
            var bpp = format.BytesPerPixel;
            var stride = info.Width * bpp + 3; // padded rows: the stride path
            var pixels = new byte[stride * info.Height];
            Check(SimdCodecs.TryDecodeInto(heic, pixels, stride, format), "heif", $"TryDecodeInto {format}");
            formats.Add($"{format}={Hash(pixels)}");
        }

        Console.WriteLine($"facade heif-layouts\t{string.Join(' ', formats)}");

        Check(SimdCodecs.TryDecode(pq, out var hdr), "heif-pq", "TryDecode");
        Check(hdr.ColorEncoding.Transfer == TransferFunction.Pq, "heif-pq", "ColorEncoding");
        Check(!SimdCodecs.TryDecodeIntoRgba8(pq, new byte[640 * 360 * 4]), "heif-pq", "TryDecodeIntoRgba8 refuses PQ");
        Console.WriteLine($"facade heif-pq {hdr.Width}x{hdr.Height} {hdr.ColorEncoding.Primaries}/{hdr.ColorEncoding.Transfer}\t{Hash(hdr.ToRgba8())}");
        return 2;
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
            ("webp-enc-lossless", WebpEncoder.EncodeLossless(rgba, w, h, PixelFormat.Rgba32)),
            ("webp-enc-lossy", WebpEncoder.EncodeLossy(rgb, w, h, PixelFormat.Rgb24, new WebpLossyEncodeOptions { Quality = 60, Effort = 6 })),
            ("webp-enc-lossy-alpha", WebpEncoder.EncodeLossy(rgba, w, h, PixelFormat.Rgba32, new WebpLossyEncodeOptions { Quality = 60, Effort = 6 })),
            ("webp-enc-anim", WebpEncoder.EncodeAnimation(
                [new WebpAnimationCanvasFrame(rgba, TimeSpan.FromMilliseconds(50)), new WebpAnimationCanvasFrame(rgba.Reverse().ToArray(), TimeSpan.FromMilliseconds(50))],
                w, h, PixelFormat.Rgba32, new WebpAnimationEncodeOptions { Mode = WebpAnimationMode.Mixed })),
            ("gif", GifEncoder.Encode(rgb, w, h, 3)),
            ("gif-anim", GifEncoder.EncodeAnimation(
                [new GifAnimationFrame(rgb, TimeSpan.FromMilliseconds(50)), new GifAnimationFrame(rgb.Reverse().ToArray(), TimeSpan.FromMilliseconds(50))],
                w, h, 3)),
            ("bmp", BmpEncoder.Encode(rgb, w, h, 3)),
            ("bmp-argb", BmpEncoder.Encode(rgba, w, h, 4)),
            ("bmp-rle8", BmpEncoder.Encode(rgb, w, h, 3, new BmpEncodeOptions { Format = BmpEncodeFormat.Indexed8, Rle = true })),
            ("bmp-565", BmpEncoder.Encode(rgb, w, h, 3, new BmpEncodeOptions { Format = BmpEncodeFormat.Rgb565 })),
            ("ico", IcoEncoder.Encode(
                [new IcoFrame(rgba, w, h), new IcoFrame(rgba, w, h, Encoding: IcoImageEncoding.Png), new IcoFrame(rgb, w, h, PixelFormat.Rgb24, IcoImageEncoding.Bmp4)])),
            ("cur", IcoEncoder.EncodeCursor([new IcoFrame(rgba, w, h, HotspotX: 3, HotspotY: 4)])),
            ("qoi", QoiEncoder.Encode(rgba, w, h, PixelFormat.Rgba32)),
            ("qoi-rgb", QoiEncoder.Encode(rgb, w, h, PixelFormat.Rgb24)),
            ("ppm", PnmEncoder.Encode(rgb, w, h, PixelFormat.Rgb24)),
            ("pgm-plain-16", PnmEncoder.Encode(rgb, w, h, PixelFormat.Rgb24, new PnmEncodeOptions { Format = PnmFormat.PlainGraymap, MaxValue = 1000 })),
            ("pbm", PnmEncoder.Encode(rgb, w, h, PixelFormat.Rgb24, new PnmEncodeOptions { Format = PnmFormat.Bitmap })),
            ("pam", PnmEncoder.Encode(rgba, w, h, PixelFormat.Rgba32)),
            ("pfm", PnmEncoder.EncodePfm([.. rgb.Select(v => v / 255f)], w, h, 3)),
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

    // Float rasters (PFM) have no 8-bit form: their samples are hashed as they are.
    private static byte[] Pixels(IDecodedImage image) =>
        image.SampleFormat == SampleFormat.Float32 ? [.. System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.ToFloats().AsSpan())] : image.ToRgba8();

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..16];

    private static void Check([DoesNotReturnIf(false)] bool ok, string label, string call)
    {
        if (!ok)
            throw new InvalidOperationException($"facade {label}: {call} failed");
    }
}
