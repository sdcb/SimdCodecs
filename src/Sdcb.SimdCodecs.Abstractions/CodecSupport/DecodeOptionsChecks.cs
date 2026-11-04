using System;

namespace Sdcb.SimdCodecs.Abstractions.CodecSupport;

/// <summary>
/// What a codec does with the <see cref="DecodeOptions"/> it is given: check them against the image, and apply a
/// region it cannot decode natively. Extension members, so a codec writes <c>options?.Validate(…)</c> and
/// <c>DecodeOptions.Apply(…)</c>; they stay out of a consumer's view unless this namespace is imported.
/// </summary>
public static class DecodeOptionsChecks
{
    extension(DecodeOptions options)
    {
        /// <summary>
        /// Checks the options against a stored <paramref name="width"/> × <paramref name="height"/> image, for a
        /// decoder that scales natively by <paramref name="supportsScale"/> (or only at 1).
        /// </summary>
        /// <exception cref="NotSupportedException">The decoder cannot produce <see cref="DecodeOptions.ScaleDenominator"/>.</exception>
        /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the scaled image.</exception>
        public void Validate(int width, int height, bool supportsScale)
        {
            var scale = options.ScaleDenominator;
            if (scale != 1 && !supportsScale)
                throw new NotSupportedException(
                    $"This decoder does not scale natively; it cannot decode at 1/{scale}. Ask TryGetClosestSize, then scale the result.");
            if (options.Region is { } r)
            {
                var (w, h) = options.GetScaledSize(width, height);
                if (!r.FitsIn(w, h))
                    throw new ArgumentException($"The region {r} does not lie inside the {w}x{h} image.", nameof(options.Region));
            }
        }
    }

    extension(DecodeOptions)
    {
        /// <summary>
        /// The generic region path for a decoder without native region decoding: checks the options (at scale 1
        /// only), then crops <paramref name="image"/> — the whole decoded image — to <see cref="DecodeOptions.Region"/>,
        /// disposing it. Without a region it returns <paramref name="image"/> itself.
        /// </summary>
        /// <exception cref="NotSupportedException"><see cref="DecodeOptions.ScaleDenominator"/> is not 1.</exception>
        /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the image.</exception>
        public static IDecodedImage Apply(IDecodedImage image, DecodeOptions? options)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (options is null)
                return image;
            options.Validate(image.Width, image.Height, supportsScale: false);
            if (options.Region is not { } r)
                return image;
            using (image)
                return RasterImage.Crop(image, r, options.Pool);
        }
    }
}
