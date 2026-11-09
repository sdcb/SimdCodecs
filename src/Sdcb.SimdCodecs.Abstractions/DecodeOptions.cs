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
/// (<see cref="IImageDecoder.TryGetClosestSize"/>), then request exactly that for the cheapest decode. Every decoder
/// accepts every <see cref="ScaleDenominator"/>, though: one beyond its native scales decodes at the largest native
/// scale and applies the frozen box filter described there for the rest. A <see cref="Region"/> works with every
/// decoder; one without native region decoding decodes the whole image and crops it, with the same result and no
/// speed-up.
/// </para>
/// </summary>
public sealed record DecodeOptions
{
    private readonly int _scale = 1;

    /// <summary>The default: whole image, full size, new memory.</summary>
    public static DecodeOptions Default { get; } = new();

    /// <summary>
    /// Decode at 1 / <c>ScaleDenominator</c> of the stored size in each direction: a power of two, 1 to 2^30. The
    /// scaled size is <c>⌈width / d⌉ × ⌈height / d⌉</c>. Every codec accepts every denominator. Those it produces
    /// natively are cheap (JPEG: up to 8, in the DCT domain; JPEG 2000: up to 2 to its decomposition levels; other
    /// codecs: 1 only), and <see cref="IImageDecoder.TryGetClosestSize"/> reports them. A larger one decodes at the
    /// largest native scale <c>n</c>, then reduces that image by the box filter of
    /// <see cref="CodecSupport.BoxDownscaler"/> with box side <c>d / n</c>.
    /// <para>
    /// The box filter is frozen. Output pixel (bx, by) averages the input pixels x ∈ [bx·k, min(bx·k + k, w)) of the
    /// rows y ∈ [by·k, min(by·k + k, h)), k the box side; boxes at the right and bottom edges are partial, n is the
    /// number of pixels in the clipped box. It works on the stored code values (gamma space, no linearisation). Without
    /// alpha each channel is <c>(Σc + n/2) / n</c> in integers. With alpha, A = Σa: alpha is <c>(A + n/2) / n</c> and
    /// each colour channel <c>(Σ(c·a) + A/2) / A</c>, or 0 where A is 0 (weighted by alpha, without rounding per
    /// pixel). 8- and 16-bit sums are exact, so every SIMD tier gives the same bytes. Float32 samples sum in double,
    /// row by row and left to right in each box: <c>Σc / n</c>, with alpha <c>A / n</c> and <c>Σ(c·a) / A</c> (0 where A
    /// is 0). The bit depth, colour encoding and metadata are kept. A <see cref="Region"/> applies after the scale.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to a value that is not a power of two from 1 to 2^30.</exception>
    public int ScaleDenominator
    {
        get => _scale;
        init
        {
            if (value <= 0 || value > 1 << 30 || (value & (value - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The scale denominator is a power of two from 1 to 2^30.");
            _scale = value;
        }
    }

    /// <summary>
    /// Decode only these pixels of the image at the requested scale: in stored coordinates (before
    /// <see cref="ImageInfo.Orientation"/> is applied), or in displayed coordinates when <see cref="ApplyOrientation"/>
    /// is set. Null decodes the whole image. It must lie inside that image, or the decode throws
    /// <see cref="ArgumentException"/>: size it from <see cref="IImageDecoder.TryReadInfo"/> and
    /// <see cref="GetOutputSize(in ImageInfo)"/>.
    /// </summary>
    public PixelRect? Region { get; init; }

    /// <summary>
    /// Return the pixels as the picture is shown: the decode applies <see cref="ImageInfo.Orientation"/> (the EXIF /
    /// TIFF Orientation tag) with <see cref="PixelTransform"/>, so orientations 5..8 exchange width and height
    /// (<see cref="ImageInfo.SwapsDimensions"/>), and the image's <see cref="ImageMetadata.Exif"/> is a copy with its
    /// Orientation set to 1 (the rest of the block byte for byte). False, the default, returns the stored pixels as
    /// every decoder always has.
    /// <para>
    /// The decode scales first, in stored orientation, then turns the result: the partial boxes of the box filter
    /// (<see cref="ScaleDenominator"/>) lie at the stored right and bottom edges, wherever those end up.
    /// <see cref="Region"/> is in the displayed coordinates of the scaled image; the decoder maps it back to the stored
    /// rectangle, decodes that, and turns it. <see cref="ImageInfo"/> from <see cref="IImageDecoder.TryReadInfo"/> stays
    /// the stored size, since it describes the file: size a <c>TryDecodeInto</c> destination with
    /// <see cref="GetOutputSize(in ImageInfo)"/>. An image whose orientation is <see cref="ImageOrientation.Normal"/>
    /// (and every format without the tag) decodes exactly as without the option; HEIF already applies its
    /// <c>irot</c> / <c>imir</c> and reports Normal, so the option changes nothing there. Every decoder that reports an
    /// orientation honours it (JPEG, gain-map JPEG, PNG, WebP, TIFF), through its still and frame paths alike.
    /// </para>
    /// </summary>
    public bool ApplyOrientation { get; init; }

    /// <summary>
    /// The pool to rent the decoded image's pixel buffer from (<see cref="RasterImage.Rent"/>); dispose the image to
    /// return it. Null allocates, and disposing is then optional. A codec that decodes straight into the result buffer
    /// rents it here; one that builds the image from an internal buffer of its own may still allocate. Ignored by
    /// <c>TryDecodeInto</c>, which writes into the caller's buffer.
    /// </summary>
    public ArrayPool<byte>? Pool { get; init; }

    /// <summary>True when the options ask for nothing but the whole image at full size (whatever
    /// <see cref="ApplyOrientation"/> says, which a decoder honours apart).</summary>
    public bool IsFullImage => _scale == 1 && Region is null;

    /// <summary>The size of the image at <see cref="ScaleDenominator"/>: <c>⌈width / d⌉ × ⌈height / d⌉</c>.</summary>
    public (int Width, int Height) GetScaledSize(int width, int height) =>
        ((int)(((long)width + _scale - 1) / _scale), (int)(((long)height + _scale - 1) / _scale));

    /// <summary>The size of what a decode with these options produces from a stored <paramref name="width"/> ×
    /// <paramref name="height"/> image: the region's size, or the scaled size. It ignores orientation; with
    /// <see cref="ApplyOrientation"/> set, use <see cref="GetOutputSize(in ImageInfo)"/>.</summary>
    public (int Width, int Height) GetOutputSize(int width, int height) =>
        Region is { } r ? (r.Width, r.Height) : GetScaledSize(width, height);

    /// <summary>The size of what a decode with these options produces from the image <paramref name="info"/> describes
    /// (<see cref="IImageDecoder.TryReadInfo"/>): the region's size, or the scaled size, with width and height
    /// exchanged when <see cref="ApplyOrientation"/> is set and the orientation turns the image a quarter
    /// (<see cref="ImageInfo.SwapsDimensions"/>). What a <c>TryDecodeInto</c> destination holds.</summary>
    public (int Width, int Height) GetOutputSize(in ImageInfo info)
    {
        if (Region is { } r)
            return (r.Width, r.Height);
        var (w, h) = GetScaledSize(info.Width, info.Height);
        return ApplyOrientation && info.SwapsDimensions ? (h, w) : (w, h);
    }
}
