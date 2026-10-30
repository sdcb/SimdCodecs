using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Png.Benchmarks;
using Sdcb.SimdCodecs.Pnm;
using Sdcb.SimdCodecs.Qoi;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Pbm;
using SixLabors.ImageSharp.PixelFormats;
using static Sdcb.SimdCodecs.QoiPnm.Benchmarks.Timing;
using IsQoiEncoder = SixLabors.ImageSharp.Formats.Qoi.QoiEncoder;
using IsQoiChannels = SixLabors.ImageSharp.Formats.Qoi.QoiChannels;

namespace Sdcb.SimdCodecs.QoiPnm.Benchmarks;

internal static class ImageSharpSetup
{
    /// <summary>One thread, like ours.</summary>
    public static readonly Configuration SingleThread = CreateSingleThread();

    public static readonly DecoderOptions Decoder = new() { Configuration = SingleThread };

    private static Configuration CreateSingleThread()
    {
        var configuration = Configuration.Default.Clone();
        configuration.MaxDegreeOfParallelism = 1;
        return configuration;
    }
}

/// <summary>
/// One case: a format of a <see cref="SourceImage"/>, decoded or encoded. Names look like <c>photo-qoi-rgba</c>,
/// <c>screen-ppm16</c>. A decode case's file is written by ImageSharp where it writes the format (QOI, 8-bit P1–P6),
/// by ours otherwise (PAM, PFM, and 16-bit PPM: ImageSharp 3.1.12 writes and reads 16-bit samples least significant
/// byte first, where the Netpbm pages and ImageMagick put the most significant first).
/// </summary>
internal sealed record Case(string Content, string Format)
{
    public string Name => $"{Content}-{Format}";

    public static readonly string[] Formats = ["qoi-rgba", "qoi-rgb", "ppm", "pgm", "pbm", "ppm16", "ppm-plain", "pam-rgba", "pfm"];

    public static IEnumerable<Case> Selected => from content in SourceImage.Contents
                                                from format in Formats
                                                let c = new Case(content, format)
                                                where Settings.Selected(c.Name)
                                                select c;

    public bool Qoi => Format.StartsWith("qoi", StringComparison.Ordinal);

    public bool ImageSharpWrites => Format is not ("pam-rgba" or "pfm");

    public SourceImage Image => SourceImage.Get(Content, Settings.Size.Width, Settings.Size.Height);

    /// <summary>The input an encoder gets, and its layout: RGBA, RGB, grey, 16-bit RGB or float RGB.</summary>
    public (byte[] Pixels, ImagePixelFormat Format) Input() => Format switch
    {
        "qoi-rgba" or "pam-rgba" => (Image.Rgba8(alpha: true), ImagePixelFormat.Rgba32),
        "pgm" or "pbm" => (Image.Gray8(), ImagePixelFormat.Gray8),
        "ppm16" => (MemoryMarshal.AsBytes(Image.Rgb16().AsSpan()).ToArray(), ImagePixelFormat.Rgb24),
        "pfm" => (MemoryMarshal.AsBytes(Image.Rgb8().Select(v => v / 255f).ToArray().AsSpan()).ToArray(), ImagePixelFormat.Rgb24),
        _ => (Image.Rgb8(), ImagePixelFormat.Rgb24),
    };

    public PnmEncodeOptions PnmOptions => new()
    {
        Format = Format switch
        {
            "pgm" => PnmFormat.Graymap,
            "pbm" => PnmFormat.Bitmap,
            "ppm-plain" => PnmFormat.PlainPixmap,
            "pam-rgba" => PnmFormat.Arbitrary,
            _ => PnmFormat.Pixmap,
        },
    };

    /// <summary>Our encoder on the case's input.</summary>
    public byte[] EncodeOurs() => EncodeOurs(Input());

    public byte[] EncodeOurs((byte[] Pixels, ImagePixelFormat Format) input)
    {
        var (pixels, format) = input;
        var (w, h) = Settings.Size;
        return Format switch
        {
            "pfm" => PnmEncoder.EncodePfm(MemoryMarshal.Cast<byte, float>(pixels), w, h, 3),
            "ppm16" => PnmEncoder.Encode(MemoryMarshal.Cast<byte, ushort>(pixels), w, h, 3),
            _ when Qoi => QoiEncoder.Encode(pixels, w, h, format),
            _ => PnmEncoder.Encode(pixels, w, h, format, PnmOptions),
        };
    }

    /// <summary>ImageSharp's encoder for this case and an image of the input built beforehand, or null.</summary>
    public (Image Image, IImageEncoder Encoder)? ImageSharpEncoder()
    {
        if (!ImageSharpWrites) return null;
        var (pixels, _) = Input();
        var (w, h) = Settings.Size;
        var config = ImageSharpSetup.SingleThread;
        Image image = Format switch
        {
            "qoi-rgba" => SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(config, pixels, w, h),
            "pgm" or "pbm" => SixLabors.ImageSharp.Image.LoadPixelData<L8>(config, pixels, w, h),
            "ppm16" => SixLabors.ImageSharp.Image.LoadPixelData<Rgb48>(config, pixels, w, h),
            _ => SixLabors.ImageSharp.Image.LoadPixelData<Rgb24>(config, pixels, w, h),
        };
        IImageEncoder encoder = Format switch
        {
            "qoi-rgba" => new IsQoiEncoder { Channels = IsQoiChannels.Rgba },
            "qoi-rgb" => new IsQoiEncoder { Channels = IsQoiChannels.Rgb },
            "pgm" => new PbmEncoder { ColorType = PbmColorType.Grayscale, Encoding = PbmEncoding.Binary, ComponentType = PbmComponentType.Byte },
            "pbm" => new PbmEncoder { ColorType = PbmColorType.BlackAndWhite, Encoding = PbmEncoding.Binary, ComponentType = PbmComponentType.Bit },
            "ppm16" => new PbmEncoder { ColorType = PbmColorType.Rgb, Encoding = PbmEncoding.Binary, ComponentType = PbmComponentType.Short },
            "ppm-plain" => new PbmEncoder { ColorType = PbmColorType.Rgb, Encoding = PbmEncoding.Plain, ComponentType = PbmComponentType.Byte },
            _ => new PbmEncoder { ColorType = PbmColorType.Rgb, Encoding = PbmEncoding.Binary, ComponentType = PbmComponentType.Byte },
        };
        return (image, encoder);
    }

    /// <summary>The decode case's file: ImageSharp's where it writes the format correctly, ours otherwise.</summary>
    public byte[] File()
    {
        if (Format == "ppm16" || ImageSharpEncoder() is not { } e) return EncodeOurs();
        using var stream = new MemoryStream();
        e.Image.Save(stream, e.Encoder);
        e.Image.Dispose();
        return stream.ToArray();
    }
}

internal sealed class Setup(Case c)
{
    public Case Case { get; } = c;
    public List<Contestant> Contestants { get; } = [];
    public List<string> Notes { get; } = [];
    public Dictionary<string, long> Allocated { get; } = [];
    public Dictionary<string, int> Sizes { get; } = [];
    public int Bytes { get; set; }

    /// <summary>
    /// Decoding: ours (into a reused RGBA buffer; PFM to its float samples), the baseline, and ImageSharp
    /// (<c>Image.Load&lt;Rgba32&gt;</c> + <c>CopyPixelDataTo</c>) where it reads the format. Ours must give the source
    /// back where the file is lossless 8-bit, and every other contestant ours.
    /// </summary>
    public static Setup Decode(Case c)
    {
        var setup = new Setup(c);
        var data = c.File();
        setup.Bytes = data.Length;
        var (w, h) = Settings.Size;
        if (c.Format == "pfm")
        {
            setup.Contestants.Add(new(Names.Current, () => PnmDecoder.Decode(data)));
            setup.Notes.Add("to native float samples; ImageSharp reads no PFM");
            return setup;
        }

        var rgba = new byte[w * h * 4];
        bool Ours(byte[] o) => c.Qoi ? QoiImageDecoder.TryDecodeInto(data, o, 0, ImagePixelFormat.Rgba32) : PnmImageDecoder.TryDecodeInto(data, o, 0, ImagePixelFormat.Rgba32);
        if (!Ours(rgba)) throw new InvalidOperationException($"{c.Name}: current does not decode the file");
        if (c.Format is "qoi-rgba" or "qoi-rgb" or "ppm" or "pam-rgba")
        {
            var expected = c.Image.Rgba8(alpha: c.Format.EndsWith("rgba", StringComparison.Ordinal));
            if (!rgba.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException($"{c.Name}: current does not give the source back");
        }
        setup.Contestants.Add(new(Names.Current, () => Ours(rgba)));

        if (Baseline.CanDecode(c.Qoi))
        {
            var other = new byte[rgba.Length];
            Baseline.TryDecodeInto(c.Qoi, data, other);
            if (other.AsSpan().SequenceEqual(rgba)) setup.Contestants.Add(new(Names.Baseline, () => Baseline.TryDecodeInto(c.Qoi, data, other)));
            else setup.Notes.Add("baseline differs from current, not timed");
        }

        if (c.ImageSharpWrites)
        {
            var other = new byte[rgba.Length];
            DecodeImageSharp(data, other);
            var worst = 0;
            for (var i = 0; i < rgba.Length; i++) worst = Math.Max(worst, Math.Abs(other[i] - rgba[i]));
            if (worst == 0)
                setup.Contestants.Add(new(Names.ImageSharp, () => DecodeImageSharp(data, other)));
            else
                setup.Notes.Add($"ImageSharp differs from current by up to {worst}, not timed" +
                                (c.Format == "ppm16" ? " (it reads 16-bit samples least significant byte first)" : ""));
        }
        return setup;
    }

    private static void DecodeImageSharp(byte[] data, byte[] rgba)
    {
        using var image = Image.Load<Rgba32>(ImageSharpSetup.Decoder, data);
        image.CopyPixelDataTo(rgba);
    }

    /// <summary>
    /// Encoding: ours to a new array and into a reused stream, the baseline, and ImageSharp (from an image built
    /// beforehand, into a reused stream). Every output must decode, with our decoder, to the input.
    /// </summary>
    public static Setup Encode(Case c)
    {
        var setup = new Setup(c);
        var (w, h) = Settings.Size;
        var (pixels, format) = c.Input();
        var input = (pixels, format);
        var ours = c.EncodeOurs(input);
        var reference = DecodeNative(ours);
        void Check(string name, byte[] output)
        {
            if (!DecodeNative(output).AsSpan().SequenceEqual(reference))
                throw new InvalidOperationException($"{c.Name}: {name}'s output does not decode to what ours does");
            setup.Sizes[name] = output.Length;
        }
        Check(Names.Current, ours);
        setup.Contestants.Add(new(Names.Current, () => c.EncodeOurs(input)));

        var stream = new MemoryStream();
        Action toStream = c.Format switch
        {
            "pfm" => () => PnmEncoder.EncodePfm(MemoryMarshal.Cast<byte, float>(pixels), w, h, 3, stream),
            "ppm16" => () => PnmEncoder.Encode(MemoryMarshal.Cast<byte, ushort>(pixels), w, h, 3, stream),
            _ when c.Qoi => () => QoiEncoder.Encode(pixels, w, h, format, stream),
            _ => () => PnmEncoder.Encode(pixels, w, h, format, stream, c.PnmOptions),
        };
        toStream();
        Check(Names.CurrentStream, stream.ToArray());
        setup.Contestants.Add(new(Names.CurrentStream, () =>
        {
            stream.Position = 0;
            toStream();
        }));

        if (c.Format is not ("pfm" or "ppm16") && Baseline.CanEncode(c.Qoi))
        {
            Func<byte[]> baseline;
            if (c.Qoi)
            {
                baseline = () => Baseline.EncodeQoi(pixels, w, h, format);
            }
            else
            {
                var encode = Baseline.CreatePnmEncoder(c.PnmOptions);
                baseline = () => encode(pixels, w, h, format);
            }
            Check(Names.Baseline, baseline());
            setup.Contestants.Add(new(Names.Baseline, () => baseline()));
        }

        if (c.ImageSharpEncoder() is { } e)
        {
            var isStream = new MemoryStream();
            e.Image.Save(isStream, e.Encoder);
            if (DecodeNative(isStream.ToArray()).AsSpan().SequenceEqual(reference))
            {
                setup.Sizes[Names.ImageSharp] = (int)isStream.Length;
                setup.Contestants.Add(new(Names.ImageSharp, () =>
                {
                    isStream.Position = 0;
                    e.Image.Save(isStream, e.Encoder);
                }));
            }
            else
            {
                setup.Notes.Add("ImageSharp's output decodes to other pixels, not timed" +
                                (c.Format == "ppm16" ? " (it writes 16-bit samples least significant byte first)" : ""));
            }
        }
        else
        {
            setup.Notes.Add("ImageSharp writes no " + (c.Format == "pfm" ? "PFM" : "PAM"));
        }
        return setup;
    }

    private static byte[] DecodeNative(byte[] file) =>
        QoiDecoder.TryReadInfo(file, out _) ? QoiDecoder.Decode(file).Pixels : PnmDecoder.Decode(file).Pixels;
}

/// <summary>The decode and encode matrices: every selected case, every contestant, timed and tabulated.</summary>
internal static class Matrix
{
    public static string Run(bool decode, int rounds)
    {
        var rows = new List<(Setup Setup, Dictionary<string, double> Ms)>();
        foreach (var c in Case.Selected)
        {
            var setup = decode ? Setup.Decode(c) : Setup.Encode(c);
            var ms = Time(setup.Contestants, rounds, setup.Allocated);
            rows.Add((setup, ms));
            Console.WriteLine($"[{(decode ? "decode" : "encode")}] {c.Name,-20} " + string.Join("  ", ms.Select(kv => $"{kv.Key} {F(kv.Value)}")) +
                              (setup.Notes.Count > 0 ? "  (" + string.Join("; ", setup.Notes) + ")" : ""));
        }

        var names = decode
            ? new[] { Names.Current, Names.Baseline, Names.ImageSharp }
            : [Names.Current, Names.CurrentStream, Names.Baseline, Names.ImageSharp];
        var compare = decode ? Names.Current : Names.CurrentStream;
        var sb = new StringBuilder();
        sb.AppendLine($"### {(decode ? "Decode" : "Encode")} (in-process medians of {rounds} interleaved rounds, ms)");
        sb.AppendLine();
        sb.AppendLine(decode
            ? "`current` = `QoiImageDecoder` / `PnmImageDecoder.TryDecodeInto` to RGBA in a caller buffer (PFM: `PnmDecoder.Decode` to " +
              "floats); ImageSharp = `Image.Load<Rgba32>` + `CopyPixelDataTo`, one thread. Every timed output was checked against ours first."
            : "`current` = `QoiEncoder` / `PnmEncoder.Encode` to a new array; `current → stream` = the `Stream` overload into a reused " +
              "stream, as ImageSharp writes; ImageSharp = its encoder from an image built beforehand into a reused stream, one thread. " +
              "Every output decodes to the input.");
        sb.AppendLine();
        sb.AppendLine("| Case | KB | " + string.Join(" | ", names.Select(Names.Label)) + $" | {Names.Label(compare)} ÷ ImageSharp | ÷ baseline |");
        sb.AppendLine("|---|---:|" + string.Concat(names.Select(_ => "---:|")) + "---:|---:|");
        var notes = new List<string>();
        foreach (var (s, ms) in rows)
        {
            var kb = decode ? s.Bytes : s.Sizes.GetValueOrDefault(Names.Current);
            sb.AppendLine($"| `{s.Case.Name}` | {kb / 1024.0:F0} | " + string.Join(" | ", names.Select(n => Ms(ms, n))) +
                          $" | {Ratio(ms, compare, Names.ImageSharp)} | {Ratio(ms, compare, Names.Baseline)} |");
            notes.AddRange(s.Notes.Select(n => $"`{s.Case.Name}`: {n}"));
        }
        sb.AppendLine();
        foreach (var (a, b) in new[] { (compare, Names.ImageSharp), (compare, Names.Baseline) })
        {
            foreach (var codec in new[] { "qoi", "pnm" })
            {
                var subset = rows.Where(r => r.Setup.Case.Qoi == (codec == "qoi")).Select(r => r.Ms).ToList();
                if (GeoMean(subset, a, b) is { } g)
                    sb.AppendLine($"- {codec.ToUpperInvariant()}: {Names.Label(a)} ÷ {Names.Label(b)} geometric mean {g.ToString("F2", CultureInfo.InvariantCulture)}× " +
                                  $"({subset.Count(m => m.ContainsKey(a) && m.ContainsKey(b))} cases)");
            }
        }
        AppendAllocations(sb, rows.Select(r => (r.Setup.Case.Name, r.Setup.Allocated)).ToList(), names);
        AppendNotes(sb, notes);
        return sb.ToString();
    }
}
