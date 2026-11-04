using System;
using System.Buffers;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>A rectangle of pixels: columns <c>X .. X + Width − 1</c> of rows <c>Y .. Y + Height − 1</c>.</summary>
/// <param name="X">The first column.</param>
/// <param name="Y">The first row.</param>
/// <param name="Width">Columns, at least 1.</param>
/// <param name="Height">Rows, at least 1.</param>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>True when the rectangle is non-empty and lies inside a <paramref name="width"/> × <paramref name="height"/> image.</summary>
    public bool FitsIn(int width, int height) =>
        X >= 0 && Y >= 0 && Width > 0 && Height > 0 && (long)X + Width <= width && (long)Y + Height <= height;
}

/// <summary>
/// What a decode should produce besides the default — the whole image at full size, in freshly allocated memory.
/// <para>
/// Size negotiation works as in WIC: ask the decoder what it can do natively
/// (<see cref="IImageDecoder.TryGetClosestSize"/>), then request exactly that. A decoder never falls back silently:
/// a <see cref="ScaleDenominator"/> it cannot produce is refused with <see cref="NotSupportedException"/>, so the
/// caller knows to scale the result itself. A <see cref="Region"/> works with every decoder; one without native region
/// decoding decodes the whole image and crops it, with the same result and no speed-up.
/// </para>
/// </summary>
public sealed record DecodeOptions
{
    private readonly int _scale = 1;

    /// <summary>The default: whole image, full size, new memory.</summary>
    public static DecodeOptions Default { get; } = new();

    /// <summary>
    /// Decode at 1 / <c>ScaleDenominator</c> of the stored size in each direction: 1, 2, 4 or 8. The scaled size is
    /// <c>⌈width / d⌉ × ⌈height / d⌉</c>. Only a codec that scales natively (JPEG, in the DCT domain) accepts a value
    /// other than 1; see <see cref="IImageDecoder.TryGetClosestSize"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to a value other than 1, 2, 4 or 8.</exception>
    public int ScaleDenominator
    {
        get => _scale;
        init
        {
            if (value is not (1 or 2 or 4 or 8))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The scale denominator is 1, 2, 4 or 8.");
            _scale = value;
        }
    }

    /// <summary>
    /// Decode only these pixels, in stored coordinates (before <see cref="ImageInfo.Orientation"/> is applied) of the
    /// image at the requested scale. Null decodes the whole image. It must lie inside that image, or the decode
    /// throws <see cref="ArgumentException"/>: size it from <see cref="IImageDecoder.TryReadInfo"/> and
    /// <see cref="GetOutputSize"/>.
    /// </summary>
    public PixelRect? Region { get; init; }

    /// <summary>
    /// The pool to rent the decoded image's pixel buffer from (<see cref="RasterImage.Rent"/>); dispose the image to
    /// return it. Null allocates, and disposing is then optional. A codec that decodes straight into the result buffer
    /// rents it here; one that builds the image from an internal buffer of its own may still allocate. Ignored by
    /// <c>TryDecodeInto</c>, which writes into the caller's buffer.
    /// </summary>
    public ArrayPool<byte>? Pool { get; init; }

    /// <summary>True when the options ask for nothing but the whole image at full size.</summary>
    public bool IsFullImage => _scale == 1 && Region is null;

    /// <summary>The size of the image at <see cref="ScaleDenominator"/>: <c>⌈width / d⌉ × ⌈height / d⌉</c>.</summary>
    public (int Width, int Height) GetScaledSize(int width, int height) =>
        ((int)(((long)width + _scale - 1) / _scale), (int)(((long)height + _scale - 1) / _scale));

    /// <summary>The size of what a decode with these options produces from a stored <paramref name="width"/> ×
    /// <paramref name="height"/> image: the region's size, or the scaled size.</summary>
    public (int Width, int Height) GetOutputSize(int width, int height) =>
        Region is { } r ? (r.Width, r.Height) : GetScaledSize(width, height);
}
