using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Ico;
using Sdcb.SimdCodecs.Png.Benchmarks;
using SixLabors.ImageSharp;
using IsBmpEncoder = SixLabors.ImageSharp.Formats.Bmp.BmpEncoder;
using IsBits = SixLabors.ImageSharp.Formats.Bmp.BmpBitsPerPixel;
using SixLabors.ImageSharp.PixelFormats;

namespace Sdcb.SimdCodecs.Bmp.Benchmarks;

/// <summary>Who wrote a decode case's file.</summary>
internal enum Writer { ImageSharp, Ours }

/// <summary>
/// One decode case: a bitmap or icon cut from a <see cref="SourceImage"/>. Bitmaps ImageSharp can write
/// (24- and 32-bit, its 16-bit, 8/4/1-bit palettes) are written by ImageSharp, a foreign encoder; the layouts it
/// cannot write (RLE, 5-6-5, a V4 alpha mask, top-down) and every icon by ours. Names look like
/// <c>photo-rgb24</c>, <c>screen-rle8</c>, <c>photo-ico256-png</c>.
/// </summary>
internal sealed record DecodeCase(string Content, string Format, Writer Writer, bool Icon = false)
{
    public string Name => $"{Content}-{Format}";

    public static IReadOnlyList<DecodeCase> All { get; } =
    [
        .. from content in SourceImage.Contents
           from c in new (string Format, Writer Writer, bool Icon)[]
           {
               ("rgb24", Writer.ImageSharp, false),
               ("rgb24-topdown", Writer.Ours, false),
               ("bgra32", Writer.ImageSharp, false),
               ("argb32-v4", Writer.Ours, false),
               ("rgb16", Writer.ImageSharp, false),
               ("rgb565", Writer.Ours, false),
               ("p8", Writer.ImageSharp, false),
               ("p4", Writer.ImageSharp, false),
               ("p1", Writer.ImageSharp, false),
               ("rle8", Writer.Ours, false),
               ("rle4", Writer.Ours, false),
               ("ico256-bmp32", Writer.Ours, true),
               ("ico256-png", Writer.Ours, true),
               ("ico48-bmp8", Writer.Ours, true),
           }
           select new DecodeCase(content, c.Format, c.Writer, c.Icon),
    ];

    public static IEnumerable<DecodeCase> Selected => All.Where(c => Settings.Selected(c.Name));

    public static DecodeCase Find(string name) => All.Single(c => c.Name == name);

    /// <summary>The image size: the bitmap size, or the icon's.</summary>
    public (int Width, int Height) Size => !Icon ? Settings.Size : Format.StartsWith("ico48", StringComparison.Ordinal) ? (48, 48) : (256, 256);

    /// <summary>The source pixels, RGBA (translucent for the alpha layouts).</summary>
    public byte[] Source()
    {
        var (w, h) = Size;
        var alpha = Format is "bgra32" or "argb32-v4" || Icon;
        return SourceImage.Get(Content, w, h).Rgba8(alpha);
    }

    /// <summary>True when the file holds the source exactly, so a decode must give it back.</summary>
    public bool Lossless => Format is "rgb24" or "rgb24-topdown" or "bgra32" or "argb32-v4" or "ico256-bmp32" or "ico256-png";

    private ((int, int) Size, byte[] Bytes)? _file;

    /// <summary>The case's file at the current size, written on first use.</summary>
    public byte[] File()
    {
        if (_file is not { } f || f.Size != Size) _file = f = (Size, Write());
        return f.Bytes;
    }

    private byte[] Write()
    {
        var (w, h) = Size;
        var rgba = Source();
        if (Writer == Writer.ImageSharp)
        {
            using var image = Image.LoadPixelData<Rgba32>(rgba, w, h);
            var encoder = Format switch
            {
                "rgb24" => new IsBmpEncoder { BitsPerPixel = IsBits.Pixel24 },
                "bgra32" => new IsBmpEncoder { BitsPerPixel = IsBits.Pixel32, SupportTransparency = true },
                "rgb16" => new IsBmpEncoder { BitsPerPixel = IsBits.Pixel16 },
                "p8" => new IsBmpEncoder { BitsPerPixel = IsBits.Pixel8 },
                "p4" => new IsBmpEncoder { BitsPerPixel = IsBits.Pixel4 },
                _ => new IsBmpEncoder { BitsPerPixel = IsBits.Pixel1 },
            };
            using var stream = new MemoryStream();
            image.SaveAsBmp(stream, encoder);
            return stream.ToArray();
        }
        return Format switch
        {
            "rgb24-topdown" => BmpEncoder.Encode(rgba, w, h, ImagePixelFormat.Rgba32, new BmpEncodeOptions { Format = BmpEncodeFormat.Rgb24, TopDown = true }),
            "argb32-v4" => BmpEncoder.Encode(rgba, w, h, ImagePixelFormat.Rgba32, new BmpEncodeOptions { Format = BmpEncodeFormat.Argb32, Header = BmpHeaderVersion.V4 }),
            "rgb565" => BmpEncoder.Encode(rgba, w, h, ImagePixelFormat.Rgba32, new BmpEncodeOptions { Format = BmpEncodeFormat.Rgb565 }),
            "rle8" => BmpEncoder.Encode(rgba, w, h, ImagePixelFormat.Rgba32, new BmpEncodeOptions { Format = BmpEncodeFormat.Indexed8, Rle = true }),
            "rle4" => BmpEncoder.Encode(rgba, w, h, ImagePixelFormat.Rgba32, new BmpEncodeOptions { Format = BmpEncodeFormat.Indexed4, Rle = true }),
            "ico256-bmp32" => IcoEncoder.Encode(rgba, w, h, ImagePixelFormat.Rgba32, IcoImageEncoding.Bmp32),
            "ico256-png" => IcoEncoder.Encode(rgba, w, h, ImagePixelFormat.Rgba32, IcoImageEncoding.Png),
            "ico48-bmp8" => IcoEncoder.Encode(rgba, w, h, ImagePixelFormat.Rgba32, IcoImageEncoding.Bmp8),
            _ => throw new InvalidOperationException(Format),
        };
    }
}

/// <summary>
/// One encode case: a layout of <see cref="BmpEncoder"/>, from RGBA source pixels. Names look like
/// <c>photo-rgb24</c>, <c>screen-p8</c>.
/// </summary>
internal sealed record EncodeCase(string Content, string Format)
{
    public string Name => $"{Content}-{Format}";

    public static IReadOnlyList<EncodeCase> All { get; } =
    [
        .. from content in SourceImage.Contents
           from format in new[] { "rgb24", "argb32", "rgb565", "p8", "rle8", "ico-set" }
           select new EncodeCase(content, format),
    ];

    public static IEnumerable<EncodeCase> Selected => All.Where(c => Settings.Selected(c.Name));

    public static EncodeCase Find(string name) => All.Single(c => c.Name == name);

    public bool Alpha => Format is "argb32" or "ico-set";

    /// <summary>Whether a decode of the output must give the source back exactly.</summary>
    public bool Lossless => Format is "rgb24" or "argb32" or "ico-set";

    public BmpEncodeOptions Options => Format switch
    {
        "rgb24" => new BmpEncodeOptions { Format = BmpEncodeFormat.Rgb24 },
        "argb32" => new BmpEncodeOptions { Format = BmpEncodeFormat.Argb32 },
        "rgb565" => new BmpEncodeOptions { Format = BmpEncodeFormat.Rgb565 },
        "p8" => new BmpEncodeOptions { Format = BmpEncodeFormat.Indexed8 },
        _ => new BmpEncodeOptions { Format = BmpEncodeFormat.Indexed8, Rle = true },
    };
}
