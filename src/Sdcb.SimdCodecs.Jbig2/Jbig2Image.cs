using System;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>
/// A decoded JBIG2 page: a bilevel raster held one byte per pixel.
/// <para>
/// <b>Polarity is T.88's, not PDF's.</b> In <see cref="Bits"/> a <c>1</c> means
/// <em>black</em> (T.88 §3.1 defines the foreground that way, and every coding
/// procedure in the spec is written around it). PDF then layers its own reading
/// on top through <c>/Decode</c> and <c>ImageMask</c>, which can invert it
/// conditionally — but that is a property of the image dictionary, not of the
/// codestream, so this decoder emits one documented polarity and leaves the
/// inversion to the PDF layer. Same line the facade draws when it refuses to
/// tone-map HDR float.
/// </para>
/// <para>
/// There is no 1-bit <see cref="SampleFormat"/>, and adding one would change the
/// stride contract for every codec in the family, so the grey projections here
/// expand to 8 bits: <see cref="ToGray8"/> and <see cref="ToRaster"/> map black
/// to 0 and white to 255, which is the conventional visual reading and what a
/// display consumer wants. A consumer implementing <c>ImageMask</c> should read
/// <see cref="Bits"/> instead and skip the expansion entirely.
/// </para>
/// </summary>
public sealed class Jbig2Image
{
    private readonly byte[] _bits;

    internal Jbig2Image(int width, int height, byte[] bits)
    {
        Width = width;
        Height = height;
        _bits = bits;
    }

    /// <summary>Pixel width.</summary>
    public int Width { get; }

    /// <summary>Pixel height.</summary>
    public int Height { get; }

    /// <summary>
    /// Row-major pixels, one byte per pixel, each 0 (white) or 1 (black). The
    /// row stride is <see cref="Width"/> with no padding.
    /// </summary>
    public ReadOnlySpan<byte> Bits => _bits;

    /// <summary>
    /// Projects to a freshly-allocated 8-bit greyscale raster, one byte per
    /// pixel: black becomes 0, white becomes 255.
    /// </summary>
    public byte[] ToGray8()
    {
        var gray = new byte[_bits.Length];
        ExpandToGray8(gray);
        return gray;
    }

    /// <summary>
    /// Expands into a caller-provided 8-bit greyscale destination (row-major, one
    /// byte per pixel, black 0 / white 255) — the allocation-free form of
    /// <see cref="ToGray8"/>. <paramref name="destination"/> must hold at least
    /// <c>Width * Height</c> bytes.
    /// </summary>
    public void ExpandToGray8(Span<byte> destination)
    {
        if (destination.Length < _bits.Length)
            throw new ArgumentException("Destination too small for Width*Height grey bytes.", nameof(destination));

        for (var i = 0; i < _bits.Length; i++)
            destination[i] = _bits[i] != 0 ? (byte)0 : (byte)255;
    }

    /// <summary>
    /// Wraps the grey projection as a codec-neutral <see cref="RasterImage"/>
    /// (1 channel, <see cref="SampleFormat.UInt8"/>) for the
    /// <c>Sdcb.SimdCodecs</c> facade's fidelity tier.
    /// </summary>
    public RasterImage ToRaster() =>
        new(Width, Height, 1, SampleFormat.UInt8, ToGray8());
}
