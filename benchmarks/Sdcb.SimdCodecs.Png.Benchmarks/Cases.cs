using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

namespace Sdcb.SimdCodecs.Png.Benchmarks;

/// <summary>Benchmark-wide settings, passed to BenchmarkDotNet's child processes through the environment.</summary>
internal static class Settings
{
    public const string SizeVariable = "PNG_BENCH_SIZE";
    public const string CasesVariable = "PNG_BENCH_CASES";

    /// <summary>Image size of every case, <c>WIDTHxHEIGHT</c> (default 1920x1080).</summary>
    public static (int Width, int Height) Size
    {
        get
        {
            string text = Environment.GetEnvironmentVariable(SizeVariable) is { Length: > 0 } s ? s : "1920x1080";
            Match m = Regex.Match(text, @"^(\d+)x(\d+)$");
            return m.Success
                ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))
                : throw new ArgumentException($"{SizeVariable} must look like 1920x1080, got '{text}'");
        }
    }

    public const string AblateVariable = "PNG_BENCH_ABLATE";

    /// <summary>Time the decode stage ablations (<c>--ablate</c>) next to <c>Current</c>.</summary>
    public static bool Ablate => Environment.GetEnvironmentVariable(AblateVariable) == "1";

    public static bool Selected(string caseName) =>
        Environment.GetEnvironmentVariable(CasesVariable) is not { Length: > 0 } filter || Regex.IsMatch(caseName, filter);
}

/// <summary>
/// One decode case: a PNG of one colour type / bit depth / interlace method, encoded from a
/// <see cref="SourceImage"/> by ImageSharp's encoder (a foreign encoder, so our decoder is not reading
/// its own writer's habits). Names look like <c>photo-rgba8</c>, <c>screen-p4-adam7</c>.
/// </summary>
internal sealed record DecodeCase(string Content, string Format, int ColorType, int BitDepth, bool Alpha, bool Adam7)
{
    public string Name => $"{Content}-{Format}{(Adam7 ? "-adam7" : "")}";

    /// <summary>Sample depth above 8 bits: the 16 → 8 narrowing differs between decoders by up to 1.</summary>
    public bool Wide => BitDepth == 16;

    private static readonly (string Format, int ColorType, int BitDepth, bool Alpha)[] Formats =
    [
        ("g1", 0, 1, false), ("g2", 0, 2, false), ("g4", 0, 4, false), ("g8", 0, 8, false), ("g16", 0, 16, false),
        ("rgb8", 2, 8, false), ("rgb16", 2, 16, false),
        ("p1", 3, 1, false), ("p2", 3, 2, false), ("p4", 3, 4, false), ("p8", 3, 8, false), ("p8a", 3, 8, true),
        ("ga8", 4, 8, true), ("ga16", 4, 16, true),
        ("rgba8", 6, 8, true), ("rgba16", 6, 16, true),
    ];

    public static IReadOnlyList<DecodeCase> All { get; } =
    [
        .. from content in SourceImage.Contents
           from adam7 in new[] { false, true }
           from f in Formats
           select new DecodeCase(content, f.Format, f.ColorType, f.BitDepth, f.Alpha, adam7),
    ];

    public static IEnumerable<DecodeCase> Selected => All.Where(c => Settings.Selected(c.Name));

    public static DecodeCase Find(string name) => All.Single(c => c.Name == name);

    public string PathFor((int Width, int Height) size) =>
        Path.Combine(BenchPaths.Cache, "inputs", $"{size.Width}x{size.Height}", Name + ".png");

    /// <summary>The case's PNG, generated into the cache on first use.</summary>
    public byte[] Read()
    {
        var size = Settings.Size;
        string path = PathFor(size);
        if (!File.Exists(path))
        {
            Generate(size, path);
        }

        return File.ReadAllBytes(path);
    }

    public void Generate((int Width, int Height) size, string path)
    {
        SourceImage source = SourceImage.Get(Content, size.Width, size.Height);
        PngEncoder encoder = ColorType == 3
            ? new PngEncoder
            {
                ColorType = PngColorType.Palette,
                BitDepth = (PngBitDepth)BitDepth,
                InterlaceMethod = Adam7 ? PngInterlaceMode.Adam7 : PngInterlaceMode.None,
                CompressionLevel = PngCompressionLevel.DefaultCompression,
                FilterMethod = PngFilterMethod.Adaptive,
                SkipMetadata = true,
                Quantizer = new WuQuantizer(new QuantizerOptions { MaxColors = 1 << BitDepth }),
            }
            : new PngEncoder
            {
                ColorType = (PngColorType)ColorType,
                BitDepth = (PngBitDepth)BitDepth,
                InterlaceMethod = Adam7 ? PngInterlaceMode.Adam7 : PngInterlaceMode.None,
                CompressionLevel = PngCompressionLevel.DefaultCompression,
                FilterMethod = PngFilterMethod.Adaptive,
                SkipMetadata = true,
            };

        using Image image = (ColorType, BitDepth, Alpha) switch
        {
            (0, 16, _) => Load<L16>(source.Gray16(), size),
            (0, _, _) => Load<L8>(source.Gray8(), size),
            (2, 16, _) => Load<Rgb48>(source.Rgb16(), size),
            (2, _, _) => Load<Rgb24>(source.Rgb8(), size),
            (3, _, true) => Load<Rgba32>(source.Rgba8(alpha: true), size),
            (3, _, false) => Load<Rgb24>(source.Rgb8(), size),
            (4, 16, _) => Load<La32>(source.GrayAlpha16(), size),
            (4, _, _) => Load<La16>(source.GrayAlpha8(), size),
            (6, 16, _) => Load<Rgba64>(source.Rgba16(alpha: true), size),
            _ => Load<Rgba32>(source.Rgba8(alpha: true), size),
        };

        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        byte[] png = stream.ToArray();

        // ImageSharp must have written exactly what was asked for: IHDR bit depth, colour type, interlace.
        byte depth = png[24], colorType = png[25], interlace = png[28];
        if (depth != BitDepth || colorType != ColorType || interlace != (Adam7 ? 1 : 0))
        {
            throw new InvalidOperationException(
                $"{Name}: ImageSharp wrote depth {depth} / colour type {colorType} / interlace {interlace}, " +
                $"expected {BitDepth} / {ColorType} / {(Adam7 ? 1 : 0)}");
        }

        if (ColorType == 3 && Alpha && !Contains(png, "tRNS"u8))
        {
            throw new InvalidOperationException($"{Name}: expected a tRNS chunk");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, png);
    }

    private static Image<TPixel> Load<TPixel>(ReadOnlySpan<byte> samples, (int Width, int Height) size)
        where TPixel : unmanaged, IPixel<TPixel> =>
        Image.LoadPixelData<TPixel>(samples, size.Width, size.Height);

    private static Image<TPixel> Load<TPixel>(ReadOnlySpan<ushort> samples, (int Width, int Height) size)
        where TPixel : unmanaged, IPixel<TPixel> =>
        Image.LoadPixelData<TPixel>(MemoryMarshal.AsBytes(samples), size.Width, size.Height);

    private static bool Contains(ReadOnlySpan<byte> png, ReadOnlySpan<byte> chunkType)
    {
        for (int offset = 8; offset + 8 <= png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            if (png.Slice(offset + 4, 4).SequenceEqual(chunkType))
            {
                return true;
            }

            offset += 12 + length;
        }

        return false;
    }
}

/// <summary>The sample layouts <see cref="PngWriter"/> encodes.</summary>
internal enum EncodeFormat { Gray8, Gray16, Rgb8, Rgb16, Rgba8, Rgba16 }

/// <summary>
/// One encode case: a <see cref="SourceImage"/> cut to one of <see cref="PngWriter"/>'s sample layouts,
/// at one <see cref="CompressionLevel"/>. Names look like <c>photo-rgba8-optimal</c>.
/// </summary>
internal sealed record EncodeCase(string Content, EncodeFormat Format, CompressionLevel Level)
{
    public string Name => $"{Content}-{Format.ToString().ToLowerInvariant()}-{LevelName(Level)}";

    public static readonly CompressionLevel[] Levels =
        [CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize];

    public static IReadOnlyList<EncodeCase> All { get; } =
    [
        .. from content in SourceImage.Contents
           from format in Enum.GetValues<EncodeFormat>()
           from level in Levels
           select new EncodeCase(content, format, level),
    ];

    public static IEnumerable<EncodeCase> Selected => All.Where(c => Settings.Selected(c.Name));

    public static EncodeCase Find(string name) => All.Single(c => c.Name == name);

    public static string LevelName(CompressionLevel level) => level switch
    {
        CompressionLevel.NoCompression => "store",
        CompressionLevel.Fastest => "fastest",
        CompressionLevel.Optimal => "optimal",
        _ => "smallest",
    };

    public int ColorType => Format switch
    {
        EncodeFormat.Gray8 or EncodeFormat.Gray16 => 0,
        EncodeFormat.Rgb8 or EncodeFormat.Rgb16 => 2,
        _ => 6,
    };

    public int BitDepth => Format is EncodeFormat.Gray16 or EncodeFormat.Rgb16 or EncodeFormat.Rgba16 ? 16 : 8;

    public int Channels => Format switch
    {
        EncodeFormat.Gray8 or EncodeFormat.Gray16 => 1,
        EncodeFormat.Rgb8 or EncodeFormat.Rgb16 => 3,
        _ => 4,
    };

    /// <summary>The raw samples: bytes for 8-bit layouts, host-order <c>ushort</c>s (as bytes) for 16-bit.</summary>
    public byte[] Samples()
    {
        var size = Settings.Size;
        SourceImage s = SourceImage.Get(Content, size.Width, size.Height);
        return Format switch
        {
            EncodeFormat.Gray8 => s.Gray8(),
            EncodeFormat.Rgb8 => s.Rgb8(),
            EncodeFormat.Rgba8 => s.Rgba8(alpha: true),
            EncodeFormat.Gray16 => MemoryMarshal.AsBytes<ushort>(s.Gray16()).ToArray(),
            EncodeFormat.Rgb16 => MemoryMarshal.AsBytes<ushort>(s.Rgb16()).ToArray(),
            _ => MemoryMarshal.AsBytes<ushort>(s.Rgba16(alpha: true)).ToArray(),
        };
    }
}
