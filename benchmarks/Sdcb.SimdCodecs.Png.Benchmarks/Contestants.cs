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
    public const string Baseline = "Baseline";
    public const string ImageSharp = "ImageSharp";
    public const string LibPng = "LibPng";
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

    /// <summary>Keeps results observable so no decode is dead code.</summary>
    public static object? Sink;

    public static DecodeSetup Create(DecodeCase c)
    {
        byte[] png = c.Read();
        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
        var setup = new DecodeSetup { Case = c, Png = png, Width = width, Height = height };
        int tolerance = c.Wide ? 1 : 0;

        byte[] reference = new byte[width * height * 4];
        DecodeImageSharp(png, reference);

        byte[] current = new byte[reference.Length];
        bool currentOk = PngImageDecoder.TryDecodeIntoRgba8(png, current);
        if (currentOk)
        {
            int diff = MaxDifference(current, reference);
            if (diff > tolerance)
            {
                throw new InvalidOperationException($"{c.Name}: Sdcb.SimdCodecs.Png differs from ImageSharp by up to {diff}; refusing to benchmark.");
            }

            byte[] buffer = new byte[reference.Length];
            setup.Contestants.Add(new(Names.Current, () => PngImageDecoder.TryDecodeIntoRgba8(png, buffer)));
            setup.Contestants.Add(new(Names.CurrentNative, () => Sink = PngReader.Decode(png)));
        }
        else
        {
            setup.Notes.Add("Sdcb.SimdCodecs.Png: unsupported" + (c.Adam7 ? " (Adam7)" : ""));
        }

        if (BaselinePng.IsBuilt)
        {
            byte[] buffer = new byte[reference.Length];
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

        {
            byte[] buffer = new byte[reference.Length];
            setup.Contestants.Add(new(Names.ImageSharp, () => DecodeImageSharp(png, buffer)));
        }

        if (LibPng.IsAvailable)
        {
            byte[] buffer = new byte[reference.Length];
            try
            {
                LibPng.DecodeRgba(png, buffer);
                int diff = MaxDifference(buffer, reference);
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

    /// <summary>Fragments for the opt-in parallel deflate (<see cref="PngWriteOptions.ParallelFragments"/>).</summary>
    public static int ParallelFragments => Environment.ProcessorCount;

    public static object? Sink;

    public static EncodeSetup Create(EncodeCase c)
    {
        var (width, height) = Settings.Size;
        byte[] samples = c.Samples();
        var setup = new EncodeSetup { Case = c, Width = width, Height = height };

        // timed: what is measured, when it is not simply encode() (ImageSharp writes into a reused stream).
        void Add(string name, Func<byte[]> encode, Action? timed = null)
        {
            byte[] output = encode();
            if (Verify(output, c, samples) is { } problem)
            {
                if (name is Names.Current or Names.CurrentParallel)
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
