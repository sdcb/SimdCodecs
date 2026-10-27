using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs.Tests;

public sealed class WebpMetadataTests
{
    [Fact]
    public void StillImageAndAdapterPreserveMetadataBytes()
    {
        string directory = FixtureDirectory();
        byte[] icc = File.ReadAllBytes(Path.Combine(directory, "metadata.icc"));
        byte[] exif = File.ReadAllBytes(Path.Combine(directory, "metadata.exif"));
        byte[] xmp = File.ReadAllBytes(Path.Combine(directory, "metadata.xmp"));
        byte[] source = File.ReadAllBytes(Path.Combine(directory, "webp-metadata.webp"));

        WebpImage image = WebpDecoder.Decode(source);

        Assert.Equal(icc, image.IccProfile);
        Assert.Equal(exif, image.Exif);
        Assert.Equal(xmp, image.Xmp);
        Assert.True(WebpImageDecoder.TryDecode(source, out IDecodedImage? decoded));
        Assert.NotNull(decoded);
        Assert.Equal(icc, decoded.IccProfile.ToArray());
        Assert.Equal(ColorEncoding.AssumedSrgb, decoded.ColorEncoding);
    }

    [Fact]
    public void AnimationPreservesMetadataBytesAndMissingMetadataIsNull()
    {
        string directory = FixtureDirectory();
        byte[] icc = File.ReadAllBytes(Path.Combine(directory, "metadata.icc"));
        byte[] exif = File.ReadAllBytes(Path.Combine(directory, "metadata.exif"));
        byte[] xmp = File.ReadAllBytes(Path.Combine(directory, "metadata.xmp"));
        byte[] source = File.ReadAllBytes(Path.Combine(directory, "anim-webpmux-metadata.webp"));

        WebpAnimation animation = WebpDecoder.DecodeAnimation(source);

        Assert.Equal(icc, animation.IccProfile);
        Assert.Equal(exif, animation.Exif);
        Assert.Equal(xmp, animation.Xmp);

        byte[] plain = File.ReadAllBytes(Path.Combine(directory, "flat-gray-16x16.webp"));
        WebpImage imageWithoutMetadata = WebpDecoder.Decode(plain);
        WebpAnimation animationWithoutMetadata = WebpDecoder.DecodeAnimation(plain);
        Assert.Null(imageWithoutMetadata.IccProfile);
        Assert.Null(imageWithoutMetadata.Exif);
        Assert.Null(imageWithoutMetadata.Xmp);
        Assert.Null(animationWithoutMetadata.IccProfile);
        Assert.Null(animationWithoutMetadata.Exif);
        Assert.Null(animationWithoutMetadata.Xmp);
    }

    private static string FixtureDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp");
}
