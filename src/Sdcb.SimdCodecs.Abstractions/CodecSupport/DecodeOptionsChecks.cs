using System;
using System.Numerics;

namespace Sdcb.SimdCodecs.Abstractions.CodecSupport;

/// <summary>
/// What a codec does with the <see cref="DecodeOptions"/> it is given: check them against the image, pick the scale it
/// decodes at natively, and apply the rest of the scale and a region it cannot decode natively. Extension members, so a
/// codec writes <c>options?.Validate(…)</c> and <c>DecodeOptions.Apply(…)</c>; they stay out of a consumer's view unless
/// this namespace is imported.
/// </summary>
public static class DecodeOptionsChecks
{
    extension(DecodeOptions options)
    {
        /// <summary>
        /// Checks the options against a stored <paramref name="width"/> × <paramref name="height"/> image: the
        /// <see cref="DecodeOptions.Region"/> must lie inside the image at <see cref="DecodeOptions.ScaleDenominator"/>.
        /// Every scale is accepted (what a codec does not produce natively, <c>DecodeOptions.Apply(image, options, nativeScale)</c>
        /// box-filters).
        /// </summary>
        /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the scaled image.</exception>
        public void Validate(int width, int height)
        {
            if (options.Region is { } r)
            {
                var (w, h) = options.GetScaledSize(width, height);
                if (!r.FitsIn(w, h))
                    throw new ArgumentException($"The region {r} does not lie inside the {w}x{h} image.", nameof(options.Region));
            }
        }

        /// <summary>
        /// Checks the options against the image <paramref name="info"/> describes: as <c>Validate(width, height)</c>,
        /// except that with <see cref="DecodeOptions.ApplyOrientation"/> set the <see cref="DecodeOptions.Region"/> must
        /// lie inside the displayed image (width and height exchanged for <see cref="ImageInfo.SwapsDimensions"/>).
        /// </summary>
        /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the image.</exception>
        public void Validate(in ImageInfo info)
        {
            if (options.Region is { } r)
            {
                var (w, h) = options.GetScaledSize(info.Width, info.Height);
                if (options.ApplyOrientation && info.SwapsDimensions)
                    (w, h) = (h, w);
                if (!r.FitsIn(w, h))
                    throw new ArgumentException($"The region {r} does not lie inside the {w}x{h} image.", nameof(options.Region));
            }
        }

        /// <summary>
        /// The scale to decode at natively, for a decoder that scales natively by every power of two up to
        /// <paramref name="maxNativeScaleDenominator"/> (1: it does not scale): the largest power of two that is at most
        /// both that and <see cref="DecodeOptions.ScaleDenominator"/>. Pass the decode at it to
        /// <c>DecodeOptions.Apply(image, options, nativeScale)</c>, which box-filters the rest.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxNativeScaleDenominator"/> is not positive.</exception>
        public int GetNativeScale(int maxNativeScaleDenominator)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxNativeScaleDenominator);
            return Math.Min(options.ScaleDenominator, 1 << BitOperations.Log2((uint)maxNativeScaleDenominator));
        }
    }

    extension(DecodeOptions)
    {
        /// <summary>
        /// The generic path for a decoder without native scaling or region decoding: <c>DecodeOptions.Apply(image, options, nativeScale)</c>
        /// for <paramref name="image"/>, the whole image at full size.
        /// </summary>
        /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the scaled image.</exception>
        public static IDecodedImage Apply(IDecodedImage image, DecodeOptions? options) => Apply(image, options, 1);

        /// <summary>
        /// The generic scale and region path: <paramref name="image"/> is the whole image decoded at
        /// 1/<paramref name="nativeScale"/> (see <c>GetNativeScale</c>). Box-filters it by the rest of
        /// <see cref="DecodeOptions.ScaleDenominator"/> (<see cref="BoxDownscaler"/>), then crops it to
        /// <see cref="DecodeOptions.Region"/> (in the scaled image's coordinates), disposing it. Without either it returns
        /// <paramref name="image"/> itself.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="nativeScale"/> is not a power of two that divides
        /// <see cref="DecodeOptions.ScaleDenominator"/>.</exception>
        /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the scaled image.</exception>
        public static IDecodedImage Apply(IDecodedImage image, DecodeOptions? options, int nativeScale)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (options is null)
                return image;
            if (nativeScale <= 0 || (nativeScale & (nativeScale - 1)) != 0 || nativeScale > options.ScaleDenominator)
                throw new ArgumentOutOfRangeException(nameof(nativeScale), nativeScale, "The native scale is a power of two no larger than ScaleDenominator.");
            var factor = options.ScaleDenominator / nativeScale;
            var w = (int)(((long)image.Width + factor - 1) / factor);
            var h = (int)(((long)image.Height + factor - 1) / factor);
            if (options.Region is { } r && !r.FitsIn(w, h))
                throw new ArgumentException($"The region {r} does not lie inside the {w}x{h} image.", nameof(options.Region));
            if (factor == 1)
            {
                if (options.Region is not { } region)
                    return image;
                using (image)
                    return RasterImage.Crop(image, region, options.Pool);
            }
            using (image)
                return BoxDownscaler.Downscale(image, factor, options.Region, options.Pool);
        }
    }
}
