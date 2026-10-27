using Sdcb.SimdCodecs.Jpeg;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;

namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

internal enum Contestant
{
    Ours,
    LibjpegTurbo,
    LibjpegTurboFast,
    ImageSharp,
}

/// <summary>Single-threaded decode-to-RGBA for every contestant (used for correctness checks and tooling).</summary>
internal static class Contestants
{
    /// <summary>ImageSharp pinned to one thread, as the benchmark rules require.</summary>
    public static readonly DecoderOptions ImageSharpOptions = CreateImageSharpOptions();

    private static DecoderOptions CreateImageSharpOptions()
    {
        var configuration = Configuration.Default.Clone();
        configuration.MaxDegreeOfParallelism = 1;
        return new DecoderOptions { Configuration = configuration };
    }

    public static string Label(Contestant c) => c switch
    {
        Contestant.Ours => "Sdcb.SimdCodecs",
        Contestant.LibjpegTurbo => "libjpeg-turbo",
        Contestant.LibjpegTurboFast => "libjpeg-turbo (fast)",
        Contestant.ImageSharp => "ImageSharp",
        _ => c.ToString(),
    };

    public static byte[] DecodeRgba(Contestant c, byte[] jpeg, out int width, out int height)
    {
        switch (c)
        {
            case Contestant.Ours:
            {
                var info = JpegDecoder.ReadInfo(jpeg);
                var rgba = new byte[info.Width * info.Height * 4];
                (width, height) = JpegDecoder.DecodeTo(jpeg, rgba);
                return rgba;
            }
            case Contestant.LibjpegTurbo:
            case Contestant.LibjpegTurboFast:
            {
                using var d = new TurboDecoder(c == Contestant.LibjpegTurboFast);
                var (w, h) = d.ReadSize(jpeg);
                var rgba = new byte[w * h * 4];
                (width, height) = d.DecodeRgba(jpeg, rgba);
                return rgba;
            }
            case Contestant.ImageSharp:
            {
                using var image = Image.Load<Rgba32>(ImageSharpOptions, jpeg);
                width = image.Width;
                height = image.Height;
                var rgba = new byte[width * height * 4];
                image.CopyPixelDataTo(rgba);
                return rgba;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(c));
        }
    }

    /// <summary>PSNR over the RGB channels of two RGBA buffers (alpha ignored).</summary>
    public static double PsnrRgb(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return double.NaN;
        long sq = 0;
        long n = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            for (var ch = 0; ch < 3; ch++)
            {
                var d = a[i + ch] - b[i + ch];
                sq += d * d;
            }

            n += 3;
        }

        if (sq == 0) return double.PositiveInfinity;
        var mse = (double)sq / n;
        return 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
