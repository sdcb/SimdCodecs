using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Sdcb.SimdCodecs.Png.Benchmarks;

/// <summary>One timed operation. <see cref="Name"/> is also the BenchmarkDotNet method name.</summary>
internal sealed record Contestant(string Name, Action Run);

internal static class Names
{
    public const string Current = "Current";
    public const string CurrentNative = "CurrentNative";
    public const string CurrentParallel = "CurrentParallel";
    public const string CurrentReduced = "CurrentReduced";
    public const string Baseline = "Baseline";
    public const string ImageSharp = "ImageSharp";
    public const string LibPng = "LibPng";

    // Decode ablations of Current (PngDiagnostics): a stage's cost is what disappears without it.
    public const string NoCrc = "NoCrc";
    public const string NoUnfilter = "NoUnfilter";
    public const string NoExpand = "NoExpand";
    public const string InflateOnly = "InflateOnly";
}

internal static class ImageSharpSetup
{
    /// <summary>One thread, like every other contestant: the comparison is single-threaded throughput.</summary>
    public static readonly Configuration SingleThread = CreateSingleThread();

    public static readonly DecoderOptions Decoder = new() { Configuration = SingleThread };

    private static Configuration CreateSingleThread()
    {
        Configuration configuration = Configuration.Default.Clone();
        configuration.MaxDegreeOfParallelism = 1;
        return configuration;
    }
}

/// <summary>
/// Everything one decode case needs: the input, and the contestants that decode it to 8-bit RGBA —
/// each checked against ImageSharp's output before it may be timed (exact, or ±1 where 16-bit
/// samples are narrowed, since rounding and truncation are both legitimate). A wrong
/// <c>Current</c> is fatal; a disagreeing competitor is dropped with a note.
/// </summary>
internal sealed class DecodeSetup
{
    public required DecodeCase Case { get; init; }
    public required byte[] Png { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public List<Contestant> Contestants { get; } = [];
    public List<string> Notes { get; } = [];

    /// <summary>Managed bytes one operation allocates, per contestant (filled by the timing run).</summary>
    public Dictionary<string, long> Allocated { get; } = [];

    /// <summary>Keeps results observable so no decode is dead code.</summary>
    public static object? Sink;

    /// <summary>Stage ablations timed alongside <c>Current</c> when <see cref="Settings.Ablate"/> is on.</summary>
    public static readonly (string Name, PngAblation Flags)[] Ablations =
    [
        (Names.NoCrc, PngAblation.SkipCrc),
        (Names.NoUnfilter, PngAblation.SkipUnfilter),
        (Names.NoExpand, PngAblation.SkipExpand),
        (Names.InflateOnly, PngAblation.InflateOnly),
    ];

    /// <param name="c">The case.</param>
    /// <param name="only">
    /// Set up just this contestant, without the cross-checks (which would run every other one): what
    /// <c>peakmem</c>'s child processes need. Null for the checked, complete setup.
    /// </param>
    public static DecodeSetup Create(DecodeCase c, string? only = null)
    {
        byte[] png = c.Read();
        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
        var setup = new DecodeSetup { Case = c, Png = png, Width = width, Height = height };
        int tolerance = c.Wide ? 1 : 0;
        bool verify = only is null;
        bool Want(string name) => only is null || only == name;
        int length = width * height * 4;
        byte[] Destination()
        {
            var buffer = new byte[length];
            if (only is not null)
            {
                buffer.AsSpan().Fill(0xFF); // commit the pages before peakmem measures
            }

            return buffer;
        }

        byte[] reference = new byte[verify ? length : 0];
        if (verify)
        {
            DecodeImageSharp(png, reference);
        }

        byte[] current = new byte[length];
        bool tryCurrent = verify || Want(Names.Current) || Want(Names.CurrentNative);
        bool currentOk = tryCurrent && PngImageDecoder.TryDecodeIntoRgba8(png, current);
        if (currentOk)
        {
            int diff = verify ? MaxDifference(current, reference) : 0;
            if (diff > tolerance)
            {
                throw new InvalidOperationException($"{c.Name}: Sdcb.SimdCodecs.Png differs from ImageSharp by up to {diff}; refusing to benchmark.");
            }

            byte[] buffer = Destination();
            if (Want(Names.Current))
            {
                setup.Contestants.Add(new(Names.Current, () => PngImageDecoder.TryDecodeIntoRgba8(png, buffer)));
            }

            if (Want(Names.CurrentNative))
            {
                setup.Contestants.Add(new(Names.CurrentNative, () => Sink = PngReader.Decode(png)));
            }

            if (Settings.Ablate && only is null)
            {
                foreach ((string name, PngAblation flags) in Ablations)
                {
                    setup.Contestants.Add(new(name, () => PngDiagnostics.DecodeRgba8(png, buffer, flags)));
                }
            }
        }
        else if (tryCurrent)
        {
            setup.Notes.Add("Sdcb.SimdCodecs.Png: unsupported" + (c.Adam7 ? " (Adam7)" : ""));
        }

        if (BaselinePng.IsBuilt && Want(Names.Baseline))
        {
            byte[] buffer = Destination();
            if (!BaselinePng.TryDecodeIntoRgba8(png, buffer))
            {
                setup.Notes.Add("baseline: unsupported");
            }
            else
            {
                if (currentOk && !buffer.AsSpan().SequenceEqual(current))
                {
                    setup.Notes.Add("baseline: OUTPUT DIFFERS from current");
                }

                setup.Contestants.Add(new(Names.Baseline, () => BaselinePng.TryDecodeIntoRgba8(png, buffer)));
            }
        }

        if (Want(Names.ImageSharp))
        {
            byte[] buffer = Destination();
            setup.Contestants.Add(new(Names.ImageSharp, () => DecodeImageSharp(png, buffer)));
        }

        if (LibPng.IsAvailable && Want(Names.LibPng))
        {
            byte[] buffer = Destination();
            try
            {
                LibPng.DecodeRgba(png, buffer);
                int diff = verify ? MaxDifference(buffer, reference) : 0;
                if (diff > tolerance)
                {
                    setup.Notes.Add($"libpng: output differs from ImageSharp by up to {diff}, not timed");
                }
                else
                {
                    setup.Contestants.Add(new(Names.LibPng, () => LibPng.DecodeRgba(png, buffer)));
                }
            }
            catch (InvalidDataException e)
            {
                setup.Notes.Add("libpng: " + e.Message);
            }
        }

        return setup;
    }

    private static void DecodeImageSharp(byte[] png, byte[] rgba)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(ImageSharpSetup.Decoder, png);
        image.CopyPixelDataTo(rgba);
    }

    private static int MaxDifference(byte[] a, byte[] b)
    {
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            max = Math.Max(max, Math.Abs(a[i] - b[i]));
        }

        return max;
    }
}

/// <summary>
/// Everything one encode case needs: the raw samples and the contestants that encode them. Every
/// contestant's output is decoded once (by <see cref="PngReader"/>, which the test suite checks against
/// libpng) and must reproduce the samples exactly in the requested colour type and depth: a lossless
/// encoder that is not lossless is not timed. <see cref="Sizes"/> holds each one's output length.
/// </summary>
internal sealed class EncodeSetup
{
    public required EncodeCase Case { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public List<Contestant> Contestants { get; } = [];
    public Dictionary<string, int> Sizes { get; } = [];
    public List<string> Notes { get; } = [];

    /// <summary>The layout <see cref="PngWriteOptions.LosslessReduction"/> chose, e.g. <c>indexed 4-bit</c>.</summary>
    public string? ReducedLayout { get; set; }

    /// <summary>Managed bytes one operation allocates, per contestant (filled by the timing run).</summary>
    public Dictionary<string, long> Allocated { get; } = [];

    /// <summary>Fragments for the opt-in parallel deflate (<see cref="PngWriteOptions.ParallelFragments"/>).</summary>
    public static int ParallelFragments => Environment.ProcessorCount;

    public static object? Sink;

    /// <param name="c">The case.</param>
    /// <param name="only">Set up just this contestant, unverified (see <see cref="DecodeSetup.Create"/>).</param>
    public static EncodeSetup Create(EncodeCase c, string? only = null)
    {
        var (width, height) = Settings.Size;
        byte[] samples = c.Samples();
        var setup = new EncodeSetup { Case = c, Width = width, Height = height };

        // timed: what is measured, when it is not simply encode() (ImageSharp writes into a reused stream).
        // A reduced encode is checked for the same pixels in whatever layout it chose.
        void Add(string name, Func<byte[]> encode, Action? timed = null)
        {
            var lossless = name == Names.CurrentReduced;
            if (only is not null)
            {
                if (name == only)
                {
                    setup.Contestants.Add(new(name, timed ?? (() => Sink = encode())));
                }

                return;
            }

            byte[] output = encode();
            if ((lossless ? VerifyLossless(output, c, samples) : Verify(output, c, samples)) is { } problem)
            {
                if (name is Names.Current or Names.CurrentParallel or Names.CurrentReduced)
                {
                    throw new InvalidOperationException($"{c.Name}: {name} output is wrong ({problem}); refusing to benchmark.");
                }

                setup.Notes.Add($"{name}: output does not round-trip ({problem}), not timed");
                return;
            }

            setup.Sizes[name] = output.Length;
            setup.Contestants.Add(new(name, timed ?? (() => Sink = encode())));
        }

        var options = new PngWriteOptions { CompressionLevel = c.Level };
        Add(Names.Current, () => EncodeCurrent(c.Format, samples, width, height, options));
        var parallel = options with { ParallelFragments = ParallelFragments };
        Add(Names.CurrentParallel, () => EncodeCurrent(c.Format, samples, width, height, parallel));
        var reduced = options with { LosslessReduction = true };
        Add(Names.CurrentReduced, () => EncodeCurrent(c.Format, samples, width, height, reduced));
        if (only is null)
        {
            var header = PngReader.Decode(EncodeCurrent(c.Format, samples, width, height, reduced));
            setup.ReducedLayout = LayoutName(header.ColorType, header.SourceBitDepth, header.PaletteAlpha is not null || header.TransparentColor is not null);
        }

        if (BaselinePng.IsBuilt)
        {
            if (BaselinePng.CanEncode(c.Format))
            {
                object baselineOptions = BaselinePng.CreateOptions(c.Level);
                Add(Names.Baseline, () => BaselinePng.Encode(c.Format, samples, width, height, baselineOptions));
            }
            else
            {
                setup.Notes.Add("baseline: no encoder for " + c.Format);
            }
        }

        AddImageSharp(c, samples, width, height, Add);

        // The simplified API has no compression-level setting: libpng's default level sits on the
        // "optimal" rows (zlib level 6, the same level .NET's Optimal and ImageSharp's default use).
        if (LibPng.IsAvailable && c.Level == CompressionLevel.Optimal)
        {
            uint format = c.Format switch
            {
                EncodeFormat.Gray8 => LibPng.FormatGray,
                EncodeFormat.Rgb8 => LibPng.FormatRgb,
                EncodeFormat.Rgba8 => LibPng.FormatRgba,
                EncodeFormat.Gray16 => LibPng.FormatGray | LibPng.FormatLinear,
                EncodeFormat.Rgb16 => LibPng.FormatRgb | LibPng.FormatLinear,
                _ => LibPng.FormatRgba | LibPng.FormatLinear,
            };
            byte[] scratch = new byte[samples.Length + samples.Length / 8 + 65536];
            scratch.AsSpan().Fill(0); // libpng writes into it: committed up front, like a caller's buffer
            Add(Names.LibPng, () =>
            {
                int length = LibPng.Encode(samples, width, height, format, ref scratch);
                return scratch.AsSpan(0, length).ToArray();
            });
        }

        return setup;
    }

    private static byte[] EncodeCurrent(EncodeFormat format, byte[] samples, int width, int height, PngWriteOptions options) => format switch
    {
        EncodeFormat.Gray8 => PngWriter.EncodeGray8(samples, width, height, options),
        EncodeFormat.Rgb8 => PngWriter.EncodeRgb8(samples, width, height, options),
        EncodeFormat.Rgba8 => PngWriter.Encode(samples, width, height, options),
        EncodeFormat.Gray16 => PngWriter.EncodeGray16(MemoryMarshal.Cast<byte, ushort>(samples), width, height, options),
        EncodeFormat.Rgb16 => PngWriter.EncodeRgb16(MemoryMarshal.Cast<byte, ushort>(samples), width, height, options),
        _ => PngWriter.EncodeRgba16(MemoryMarshal.Cast<byte, ushort>(samples), width, height, options),
    };

    private static void AddImageSharp(EncodeCase c, byte[] samples, int width, int height, Action<string, Func<byte[]>, Action?> add)
    {
        Image image = c.Format switch
        {
            EncodeFormat.Gray8 => Image.LoadPixelData<L8>(ImageSharpSetup.SingleThread, samples, width, height),
            EncodeFormat.Gray16 => Image.LoadPixelData<L16>(ImageSharpSetup.SingleThread, samples, width, height),
            EncodeFormat.Rgb8 => Image.LoadPixelData<Rgb24>(ImageSharpSetup.SingleThread, samples, width, height),
            EncodeFormat.Rgb16 => Image.LoadPixelData<Rgb48>(ImageSharpSetup.SingleThread, samples, width, height),
            EncodeFormat.Rgba8 => Image.LoadPixelData<Rgba32>(ImageSharpSetup.SingleThread, samples, width, height),
            _ => Image.LoadPixelData<Rgba64>(ImageSharpSetup.SingleThread, samples, width, height),
        };
        var encoder = new PngEncoder
        {
            ColorType = (PngColorType)c.ColorType,
            BitDepth = c.BitDepth == 16 ? PngBitDepth.Bit16 : PngBitDepth.Bit8,
            // Same zlib levels as .NET's CompressionLevel: 0, 1, 6, 9.
            CompressionLevel = c.Level switch
            {
                CompressionLevel.NoCompression => PngCompressionLevel.NoCompression,
                CompressionLevel.Fastest => PngCompressionLevel.BestSpeed,
                CompressionLevel.Optimal => PngCompressionLevel.DefaultCompression,
                _ => PngCompressionLevel.BestCompression,
            },
            FilterMethod = PngFilterMethod.Adaptive,
            SkipMetadata = true,
        };
        var stream = new MemoryStream();
        void Encode()
        {
            stream.SetLength(0);
            image.Save(stream, encoder);
        }

        // The ToArray copy is for the verification only; the encode into the reused stream is what is timed.
        add(Names.ImageSharp, () =>
        {
            Encode();
            return stream.ToArray();
        }, Encode);
    }

    private static string LayoutName(int colorType, int depth, bool trns) =>
        (colorType switch { 0 => "grey", 2 => "RGB", 3 => "indexed", 4 => "grey+alpha", _ => "RGBA" }) + $" {depth}-bit" + (trns ? " + tRNS" : "");

    /// <summary>
    /// Null when <paramref name="png"/>, in any layout, reads back as exactly the pixels of
    /// <paramref name="samples"/>: both compared as 16-bit RGBA, widened the way PNG widens samples.
    /// </summary>
    private static string? VerifyLossless(byte[] png, EncodeCase c, byte[] samples)
    {
        PngImage image;
        try
        {
            image = PngReader.Decode(png);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            return "unreadable: " + e.Message;
        }

        var source = new PngImage
        {
            Width = image.Width,
            Height = image.Height,
            BitDepth = c.BitDepth,
            ColorType = c.ColorType,
            Pixels = c.BitDepth == 16 ? BigEndian(samples) : samples,
        };
        return Rgba16(image).AsSpan().SequenceEqual(Rgba16(source)) ? null : "pixels differ";
    }

    private static byte[] BigEndian(byte[] hostOrder16)
    {
        var bytes = new byte[hostOrder16.Length];
        for (var i = 0; i < bytes.Length; i += 2)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i), MemoryMarshal.Read<ushort>(hostOrder16.AsSpan(i)));
        return bytes;
    }

    // Every pixel as 16-bit R, G, B, A: 8-bit samples (unpacked sub-byte grey included) as v·257, grey
    // to all three, palette looked up, tRNS applied, no alpha as 65535.
    private static ushort[] Rgba16(PngImage image)
    {
        var count = image.Width * image.Height;
        var channels = image.SamplesPerPixel;
        var wide = image.BitDepth == 16;
        var key = image.UnpackedTransparentColor();
        int Sample(int i) => wide
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(image.Pixels.AsSpan(2 * i))
            : image.Pixels[i] * 257;
        var rgba = new ushort[4 * count];
        for (var p = 0; p < count; p++)
        {
            int r, g, b, a = 65535;
            if (image.ColorType == 3)
            {
                var index = image.Pixels[p];
                (r, g, b) = (image.Palette![3 * index] * 257, image.Palette[3 * index + 1] * 257, image.Palette[3 * index + 2] * 257);
                if (image.PaletteAlpha is { } alpha && index < alpha.Length)
                    a = alpha[index] * 257;
            }
            else
            {
                r = Sample(p * channels);
                (g, b) = channels >= 3 ? (Sample(p * channels + 1), Sample(p * channels + 2)) : (r, r);
                if (channels is 2 or 4)
                    a = Sample(p * channels + channels - 1);
                var k = wide ? 1 : 257;
                if (key is not null && r == key[0] * k && (channels == 1 || (g == key[1] * k && b == key[2] * k)))
                    a = 0;
            }
            (rgba[4 * p], rgba[4 * p + 1], rgba[4 * p + 2], rgba[4 * p + 3]) = ((ushort)r, (ushort)g, (ushort)b, (ushort)a);
        }
        return rgba;
    }

    /// <summary>Null when <paramref name="png"/> holds exactly <paramref name="samples"/> as <paramref name="c"/> asks.</summary>
    private static string? Verify(byte[] png, EncodeCase c, byte[] samples)
    {
        PngImage image;
        try
        {
            image = PngReader.Decode(png);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            return "unreadable: " + e.Message;
        }

        if (image.ColorType != c.ColorType || image.BitDepth != c.BitDepth)
        {
            return $"colour type {image.ColorType} depth {image.BitDepth}, expected {c.ColorType} / {c.BitDepth}";
        }

        bool same = c.BitDepth == 16
            ? image.AsUInt16Samples().AsSpan().SequenceEqual(MemoryMarshal.Cast<byte, ushort>(samples))
            : image.Pixels.AsSpan().SequenceEqual(samples);
        return same ? null : "samples differ";
    }
}
