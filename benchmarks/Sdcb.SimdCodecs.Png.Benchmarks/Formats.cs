using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Png.Benchmarks;

/// <summary>
/// The display layouts of <see cref="PngImageDecoder.TryDecodeInto"/>: for each case and each
/// <see cref="ImagePixelFormat"/> other than RGBA, the native path (rows converted as they are
/// unfiltered) against the two-step path a caller of an RGBA-only API takes (decode to RGBA, then
/// <see cref="PixelConverter"/> over the frame), with RGBA itself and libpng's simplified API in the
/// same layout for scale. The two of ours must produce identical bytes, checked before timing.
/// </summary>
internal static class Formats
{
    private static readonly ImagePixelFormat[] Targets =
        [ImagePixelFormat.Bgra32, ImagePixelFormat.Rgb24, ImagePixelFormat.Bgr24, ImagePixelFormat.Gray8];

    /// <summary>Without <c>--cases</c>: the common colour types, plain and Adam7.</summary>
    public const string DefaultCases = "^(photo|screen)-(g8|rgb8|p8|rgba8|rgb16)(-adam7)?$";

    public static string Run(int rounds)
    {
        var rows = new List<(string Case, ImagePixelFormat Format, double Rgba, double Native, double TwoStep, double? LibPng)>();
        foreach (DecodeCase c in DecodeCase.Selected)
        {
            byte[] png = c.Read();
            int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
            int pixels = width * height;
            var rgba = new byte[pixels * 4];
            foreach (ImagePixelFormat format in Targets)
            {
                int bpp = PixelConverter.BytesPerPixel(format);
                var native = new byte[pixels * bpp];
                var twoStep = new byte[pixels * bpp];
                var contestants = new List<Contestant>
                {
                    new("rgba", () => Check(PngImageDecoder.TryDecodeIntoRgba8(png, rgba))),
                    new("native", () => Check(PngImageDecoder.TryDecodeInto(png, native, 0, format))),
                    new("two-step", () =>
                    {
                        Check(PngImageDecoder.TryDecodeIntoRgba8(png, rgba));
                        PixelConverter.ConvertRow(rgba, 4, SampleFormat.UInt8, twoStep, format, pixels);
                    }),
                };
                if (LibPng.IsAvailable && LibPngFormat(format) is { } libFormat)
                {
                    var lib = new byte[pixels * bpp];
                    contestants.Add(new("libpng", () => LibPng.Decode(png, lib, libFormat)));
                }

                // Correctness before timing.
                foreach (Contestant contestant in contestants)
                    contestant.Run();
                if (!native.AsSpan().SequenceEqual(twoStep))
                    throw new InvalidOperationException($"{c.Name} {format}: the native path differs from decode + convert");

                Dictionary<string, double> ms = Matrix.Time(contestants, rounds);
                rows.Add((c.Name, format, ms["rgba"], ms["native"], ms["two-step"], ms.TryGetValue("libpng", out double l) ? l : null));
                Console.WriteLine($"[formats] {c.Name,-22} {format,-6} " + string.Join("  ", ms.Select(kv => $"{kv.Key} {kv.Value:F3}")));
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"### Decode into each layout (in-process medians of {rounds} interleaved rounds, ms)");
        sb.AppendLine();
        sb.AppendLine("`native` = `PngImageDecoder.TryDecodeInto(format)`; `two-step` = `TryDecodeIntoRgba8` + `PixelConverter.ConvertRow` " +
                      "over the frame (what an RGBA-only API leaves a caller to do); `rgba` = `TryDecodeIntoRgba8` alone; libpng = simplified " +
                      "API `png_image_finish_read` in the same layout (its grey is its own luma, and it composites alpha it drops onto a " +
                      "background, so its bytes differ). Ratios below 1 mean native is faster.");
        sb.AppendLine();
        sb.AppendLine("| Case | Layout | rgba | native | two-step | libpng | native ÷ two-step | native ÷ rgba | native ÷ libpng |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in rows)
        {
            sb.AppendLine($"| `{r.Case}` | {r.Format} | {F(r.Rgba)} | {F(r.Native)} | {F(r.TwoStep)} | {(r.LibPng is { } l ? F(l) : "—")} | " +
                          $"{r.Native / r.TwoStep:F2} | {r.Native / r.Rgba:F2} | {(r.LibPng is { } lp ? (r.Native / lp).ToString("F2", CultureInfo.InvariantCulture) : "—")} |");
        }

        return sb.ToString();
    }

    // libpng's simplified-API format flags (alpha 1, colour 2, BGR 0x10), as its manual documents them.
    private static uint? LibPngFormat(ImagePixelFormat format) => format switch
    {
        ImagePixelFormat.Bgra32 => LibPng.FormatRgba | LibPng.FlagBgr,
        ImagePixelFormat.Rgb24 => LibPng.FormatRgb,
        ImagePixelFormat.Bgr24 => LibPng.FormatRgb | LibPng.FlagBgr,
        ImagePixelFormat.Gray8 => LibPng.FormatGray,
        _ => null,
    };

    private static void Check(bool ok)
    {
        if (!ok)
            throw new InvalidOperationException("decode failed");
    }

    private static string F(double ms) => ms.ToString(ms < 10 ? "F3" : "F2", CultureInfo.InvariantCulture);
}
