using System.Buffers;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="ImageCodecs.Decode(ReadOnlySpan{byte}, PixelFormat, DecodeOptions?, CancellationToken)"/>, the one-call
/// pixel form, is byte for byte the three-step path it replaces (<see cref="ImageCodecs.TryReadInfo"/>, a buffer of
/// <see cref="DecodeOptions.GetOutputSize"/>, <see cref="ImageCodecs.TryDecodeInto"/>), whole, scaled and cropped, and
/// carries the file's metadata (<see cref="ImageCodecs.TryReadMetadata"/>) and colour encoding. Files are Magick.NET's,
/// with an ICC profile and, for JPEG, an Exif block.
/// </summary>
public sealed class DecodeToFormatTests
{
    private const int W = 61, H = 43;

    private static readonly Lazy<Dictionary<string, byte[]>> Files = new(() =>
    {
        var pixels = new byte[W * H * 4];
        new Random(7).NextBytes(pixels);
        for (var p = 0; p < W * H; p++)
            pixels[p * 4 + 3] = (byte)(p % 5 == 0 ? 0 : 255);
        byte[] Write(MagickFormat format, bool alpha)
        {
            using var image = new MagickImage();
            image.ReadPixels(pixels, new PixelReadSettings(W, H, StorageType.Char, "RGBA"));
            if (!alpha)
                image.Alpha(AlphaOption.Off);
            image.SetProfile(ColorProfiles.AdobeRGB1998);
            if (format == MagickFormat.Jpeg)
            {
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.Artist, "DecodeToFormatTests");
                image.SetProfile(exif);
            }
            return image.ToByteArray(format);
        }
        return new()
        {
            ["jpeg"] = Write(MagickFormat.Jpeg, alpha: false),
            ["png"] = Write(MagickFormat.Png, alpha: true),
            ["webp"] = Write(MagickFormat.WebP, alpha: true),
            ["gif"] = Write(MagickFormat.Gif, alpha: true),
        };
    });

    public static TheoryData<string, string, int, bool> Cases()
    {
        var data = new TheoryData<string, string, int, bool>();
        foreach (var file in new[] { "jpeg", "png", "webp", "gif" })
        foreach (var format in new[] { "Rgba32", "Bgr24", "Gray8" })
        foreach (var (scale, region) in new[] { (1, false), (2, false), (1, true), (2, true) })
            data.Add(file, format, scale, region);
        return data;
    }

    private static PixelFormat FormatOf(string name) => name switch
    {
        "Rgba32" => PixelFormat.Rgba32,
        "Bgr24" => PixelFormat.Bgr24,
        _ => PixelFormat.Gray8,
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void OneCall_IsTheThreeStepPath(string file, string formatName, int scale, bool region)
    {
        var data = Files.Value[file];
        var format = FormatOf(formatName);
        var ct = TestContext.Current.CancellationToken;
        DecodeOptions? options = (scale, region) switch
        {
            (1, false) => null,
            (_, false) => new DecodeOptions { ScaleDenominator = scale },
            _ => new DecodeOptions { ScaleDenominator = scale, Region = new PixelRect(3, 2, 11 / scale + 4, 9 / scale + 3) },
        };

        ImageCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
        var (w, h) = (options ?? DecodeOptions.Default).GetOutputSize(info.Width, info.Height);
        var expected = new byte[w * h * format.BytesPerPixel];
        ImageCodecs.TryDecodeInto(data, expected, 0, format, options, ct).ShouldBeTrue();
        ImageCodecs.TryReadMetadata(data, out var metadata).ShouldBeTrue();

        using var buffer = ImageCodecs.Decode(data, format, options, ct);
        (buffer.Width, buffer.Height, buffer.Format, buffer.Stride).ShouldBe((w, h, format, w * format.BytesPerPixel));
        buffer.Pixels.ToArray().ShouldBe(expected);
        buffer.ColorEncoding.ShouldBe(info.ColorEncoding);
        buffer.Metadata.Icc.ToArray().ShouldBe(metadata.Icc.ToArray());
        buffer.Metadata.Exif.ToArray().ShouldBe(metadata.Exif.ToArray());
        buffer.Metadata.Xmp.ToArray().ShouldBe(metadata.Xmp.ToArray());
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    public void Metadata_TheFileCarriesComesThrough(string file)
    {
        // GIF has no metadata blocks; the other three keep Magick's ICC profile, and JPEG its Exif block.
        using var buffer = ImageCodecs.Decode(Files.Value[file], PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken);
        buffer.Metadata.Icc.ToArray().ShouldBe(ColorProfiles.AdobeRGB1998.ToByteArray());
        if (file == "jpeg")
        {
            // The block from its TIFF header on (no "Exif\0\0"), holding the Artist tag Magick wrote.
            var exif = buffer.Metadata.Exif.Span;
            exif[..4].ToArray().ShouldBe("II*\0"u8.ToArray());
            System.Text.Encoding.ASCII.GetString(exif).ShouldContain("DecodeToFormatTests\0");
        }
    }

    [Fact]
    public void Pool_RentsTheBufferAndGivesTheSamePixels()
    {
        var data = Files.Value["jpeg"];
        var ct = TestContext.Current.CancellationToken;
        using var plain = ImageCodecs.Decode(data, PixelFormat.Bgra32, cancellationToken: ct);
        var pool = new CountingPool();
        var buffer = ImageCodecs.Decode(data, PixelFormat.Bgra32, new DecodeOptions { Pool = pool }, ct);
        buffer.Pixels.SequenceEqual(plain.Pixels).ShouldBeTrue();
        pool.Rented.ShouldBe(1);
        buffer.Dispose();
        pool.Returned.ShouldBe(1);
        Should.Throw<ObjectDisposedException>(() => buffer.Pixels.Length);
    }

    [Fact]
    public void GainMapJpeg_IsItsSdrBase()
    {
        var data = GainMapFacadeTests.BuildUltraHdr(out _);
        var ct = TestContext.Current.CancellationToken;
        ImageCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
        info.SampleFormat.ShouldBe(SampleFormat.Float32); // the fidelity tier is the HDR
        var expected = new byte[info.Width * info.Height * 4];
        ImageCodecs.TryDecodeInto(data, expected, 0, PixelFormat.Rgba32, cancellationToken: ct).ShouldBeTrue();

        using var buffer = ImageCodecs.Decode(data, PixelFormat.Rgba32, cancellationToken: ct);
        buffer.Pixels.ToArray().ShouldBe(expected);
        // The SDR base is display pixels; an untagged base is sRGB.
        buffer.ColorEncoding.ShouldBe(ColorEncoding.AssumedSrgb);
    }

    private sealed class CountingPool : ArrayPool<byte>
    {
        public int Rented { get; private set; }

        public int Returned { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            Rented++;
            return new byte[minimumLength + 13];
        }

        public override void Return(byte[] array, bool clearArray = false) => Returned++;
    }
}
