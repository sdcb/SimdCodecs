using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Ico;
using Sdcb.SimdCodecs.Png.Benchmarks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using IsBmpEncoder = SixLabors.ImageSharp.Formats.Bmp.BmpEncoder;
using IsBits = SixLabors.ImageSharp.Formats.Bmp.BmpBitsPerPixel;

namespace Sdcb.SimdCodecs.Bmp.Benchmarks;

internal static class ImageSharpSetup
{
    /// <summary>One thread, as every other contestant runs.</summary>
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
/// The contestants of one decode case, each checked before it may be timed. <c>current</c> must give the source
/// back exactly when the file holds it losslessly, and otherwise agree exactly with a competitor; if not, the run
/// stops. A competitor that disagrees with <c>current</c> is dropped from the case with a note (WIC is compared on
/// colour alone where it ignores alpha by design: a 32-bit <c>BI_RGB</c> pixel's fourth byte).
/// </summary>
internal sealed class DecodeSetup
{
    public required DecodeCase Case { get; init; }
    public required byte[] Data { get; init; }
    public int Width => Case.Size.Width;
    public int Height => Case.Size.Height;
    public List<Contestant> Contestants { get; } = [];
    public List<string> Notes { get; } = [];
    public Dictionary<string, long> Allocated { get; } = [];

    public static DecodeSetup Create(DecodeCase c, string? only = null)
    {
        var data = c.File();
        var setup = new DecodeSetup { Case = c, Data = data };
        bool Want(string name) => only is null || only == name;
        var (w, h) = c.Size;
        var rgba = new byte[w * h * 4];
        var bgra = new byte[w * h * 4];
        var icon = c.Icon;
        var best = icon ? IcoDecoder.ReadInfo(data).Best : 0;

        bool Current(byte[] into, ImagePixelFormat format) => icon
            ? IcoImageDecoder.TryDecodeInto(data, into, 0, format)
            : BmpImageDecoder.TryDecodeInto(data, into, 0, format);

        if (only is null)
        {
            if (!Current(rgba, ImagePixelFormat.Rgba32)) throw new InvalidOperationException($"{c.Name}: current does not decode it");
            Current(bgra, ImagePixelFormat.Bgra32);
            if (c.Lossless && !rgba.AsSpan().SequenceEqual(c.Source()))
                throw new InvalidOperationException($"{c.Name}: current does not give the source back; refusing to benchmark.");
        }

        var agreed = c.Lossless;
        if (Want(Names.Current)) setup.Contestants.Add(new(Names.Current, () => Current(rgba, ImagePixelFormat.Rgba32)));
        if (Want(Names.CurrentBgra)) setup.Contestants.Add(new(Names.CurrentBgra, () => Current(bgra, ImagePixelFormat.Bgra32)));

        if (Baseline.IsBuilt && Want(Names.Baseline) && Baseline.CanDecode(icon))
        {
            var buffer = new byte[w * h * 4];
            if (only is null && (!Baseline.TryDecodeInto(icon, data, buffer) || !buffer.AsSpan().SequenceEqual(rgba)))
                setup.Notes.Add("baseline: output differs from current, not timed");
            else
                setup.Contestants.Add(new(Names.Baseline, () => Baseline.TryDecodeInto(icon, data, buffer)));
        }

        if (!icon && Want(Names.ImageSharp))
        {
            var buffer = new byte[w * h * 4];
            if (only is null)
            {
                DecodeImageSharp(data, buffer);
                if (buffer.AsSpan().SequenceEqual(rgba)) agreed = true;
                else setup.Notes.Add($"ImageSharp: differs from current at pixel {FirstDifference(buffer, rgba)}, not timed");
            }
            if (only is not null || buffer.AsSpan().SequenceEqual(rgba))
                setup.Contestants.Add(new(Names.ImageSharp, () => DecodeImageSharp(data, buffer)));
        }

        if (Wic.IsAvailable && Want(Names.Wic))
        {
            var buffer = new byte[w * h * 4];
            var ok = true;
            if (only is null)
            {
                Wic.Decode(data, best, buffer);
                var colourOnly = c.Format == "bgra32" && !c.Icon && IsBiRgb32(data);
                ok = colourOnly ? SameColour(buffer, bgra) : buffer.AsSpan().SequenceEqual(bgra);
                if (ok && !colourOnly) agreed = true;
                if (!ok) setup.Notes.Add($"WIC: differs from current at pixel {FirstDifference(buffer, bgra)}, not timed");
            }
            if (ok) setup.Contestants.Add(new(Names.Wic, () => Wic.Decode(data, best, buffer)));
        }

        if (only is null && !agreed)
            throw new InvalidOperationException($"{c.Name}: no competitor agrees with current and the file is lossy; refusing to benchmark.");
        return setup;
    }

    private static void DecodeImageSharp(byte[] data, byte[] rgba)
    {
        using var image = Image.Load<Rgba32>(ImageSharpSetup.Decoder, data);
        image.CopyPixelDataTo(rgba);
    }

    private static bool IsBiRgb32(byte[] bmp) => BmpDecoder.ReadInfo(bmp) is { BitsPerPixel: 32, Compression: BmpCompression.Rgb };

    private static bool SameColour(byte[] a, byte[] b)
    {
        for (var i = 0; i < a.Length; i += 4)
        {
            if (!a.AsSpan(i, 3).SequenceEqual(b.AsSpan(i, 3))) return false;
        }
        return true;
    }

    internal static int FirstDifference(byte[] a, byte[] b) => a.AsSpan().CommonPrefixLength(b) / 4;
}

/// <summary>
/// The contestants of one encode case. Every output is decoded (by our decoder) and, for the lossless layouts, must
/// give the source back exactly; the lossy ones (5-6-5, palettes) report their size. Inputs are each library's
/// natural one: RGBA for ours and ImageSharp (an <c>Image&lt;Rgba32&gt;</c> built beforehand), B, G, R(, A) for WIC,
/// which has no RGBA layout — and <c>current BGR(A)</c> is ours from that same input.
/// </summary>
internal sealed class EncodeSetup
{
    public required EncodeCase Case { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public List<Contestant> Contestants { get; } = [];
    public List<string> Notes { get; } = [];
    public Dictionary<string, long> Allocated { get; } = [];
    public Dictionary<string, int> Sizes { get; } = [];

    public static EncodeSetup Create(EncodeCase c, string? only = null)
    {
        if (c.Format == "ico-set") return CreateIcon(c, only);
        var (w, h) = Settings.Size;
        var setup = new EncodeSetup { Case = c, Width = w, Height = h };
        bool Want(string name) => only is null || only == name;
        var rgba = SourceImage.Get(c.Content, w, h).Rgba8(c.Alpha);
        var options = c.Options;

        void Check(string name, byte[] bmp)
        {
            setup.Sizes[name] = bmp.Length;
            if (only is not null) return;
            var back = new byte[w * h * 4];
            if (!BmpImageDecoder.TryDecodeInto(bmp, back, 0, ImagePixelFormat.Rgba32))
                throw new InvalidOperationException($"{c.Name}: {name}'s output does not decode");
            if (c.Lossless && !back.AsSpan().SequenceEqual(rgba))
                throw new InvalidOperationException($"{c.Name}: {name}'s output does not give the source back (pixel {DecodeSetup.FirstDifference(back, rgba)})");
        }

        if (Want(Names.Current))
        {
            Check(Names.Current, BmpEncoder.Encode(rgba, w, h, ImagePixelFormat.Rgba32, options));
            setup.Contestants.Add(new(Names.Current, () => BmpEncoder.Encode(rgba, w, h, ImagePixelFormat.Rgba32, options)));
        }

        // B, G, R(, A) input, for the layouts WIC writes.
        byte[]? native = c.Format switch
        {
            "rgb24" => Swizzle(rgba, 3),
            "argb32" => Swizzle(rgba, 4),
            _ => null,
        };
        var nativeFormat = c.Format == "rgb24" ? ImagePixelFormat.Bgr24 : ImagePixelFormat.Bgra32;
        if (native is not null && Want(Names.CurrentBgra))
        {
            Check(Names.CurrentBgra, BmpEncoder.Encode(native, w, h, nativeFormat, options));
            setup.Contestants.Add(new(Names.CurrentBgra, () => BmpEncoder.Encode(native, w, h, nativeFormat, options)));
        }

        if (Baseline.IsBuilt && Want(Names.Baseline) && Baseline.CanEncode)
        {
            var encode = Baseline.CreateEncoder(options);
            Check(Names.Baseline, encode(rgba, w, h));
            setup.Contestants.Add(new(Names.Baseline, () => encode(rgba, w, h)));
        }

        IsBits? bits = c.Format switch
        {
            "rgb24" => IsBits.Pixel24,
            "argb32" => IsBits.Pixel32,
            "rgb565" => IsBits.Pixel16,
            "p8" => IsBits.Pixel8,
            _ => null, // ImageSharp writes no RLE
        };
        if (bits is { } b && Want(Names.ImageSharp))
        {
            var image = Image.LoadPixelData<Rgba32>(ImageSharpSetup.SingleThread, rgba, w, h);
            var encoder = new IsBmpEncoder { BitsPerPixel = b, SupportTransparency = c.Alpha };
            var stream = new MemoryStream();
            image.SaveAsBmp(stream, encoder);
            Check(Names.ImageSharp, stream.ToArray());
            if (c.Format == "rgb565") setup.Notes.Add("ImageSharp's 16-bit layout is its own (it may be 5-5-5); sizes are equal either way");
            setup.Contestants.Add(new(Names.ImageSharp, () =>
            {
                stream.SetLength(0);
                image.SaveAsBmp(stream, encoder);
            }));
        }

        if (native is not null && Wic.IsAvailable && Want(Names.Wic))
        {
            var stream = new MemoryStream();
            var length = Wic.Encode(native, w, h, c.Alpha, stream);
            Check(Names.Wic, stream.GetBuffer()[..length]);
            setup.Contestants.Add(new(Names.Wic, () => Wic.Encode(native, w, h, c.Alpha, stream)));
        }
        return setup;
    }

    private static EncodeSetup CreateIcon(EncodeCase c, string? only)
    {
        var setup = new EncodeSetup { Case = c, Width = 256, Height = 256 };
        IcoFrame[] frames = [.. new[] { 16, 32, 48, 256 }.Select(s => new IcoFrame(SourceImage.Get(c.Content, s, s).Rgba8(true), s, s))];
        var ico = IcoEncoder.Encode(frames);
        setup.Sizes[Names.Current] = ico.Length;
        if (only is null)
        {
            for (var i = 0; i < frames.Length; i++)
            {
                if (!IcoDecoder.Decode(ico, i).Rgba.AsSpan().SequenceEqual(frames[i].Pixels.Span))
                    throw new InvalidOperationException($"{c.Name}: image {i} does not round-trip");
            }
        }
        if (only is null || only == Names.Current) setup.Contestants.Add(new(Names.Current, () => IcoEncoder.Encode(frames)));
        setup.Notes.Add("16, 32 and 48 as 32-bit DIBs, 256 as PNG; neither ImageSharp 3.1 nor WIC writes icons");
        return setup;
    }

    // RGBA to B, G, R (3) or B, G, R, A (4).
    private static byte[] Swizzle(byte[] rgba, int bytes)
    {
        var count = rgba.Length / 4;
        var output = new byte[count * bytes];
        for (var i = 0; i < count; i++)
        {
            output[bytes * i] = rgba[4 * i + 2];
            output[bytes * i + 1] = rgba[4 * i + 1];
            output[bytes * i + 2] = rgba[4 * i];
            if (bytes == 4) output[bytes * i + 3] = rgba[4 * i + 3];
        }
        return output;
    }
}
