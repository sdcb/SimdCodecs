using System;
using System.Threading;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>Where chroma samples sit relative to luma: the H.273 / H.265 <c>chroma_sample_loc_type</c> values.</summary>
public enum ChromaLocation : byte
{
    /// <summary>Co-sited horizontally with the left luma sample, centred vertically (H.265 default, type 0).</summary>
    Left = 0,

    /// <summary>Centred between the luma samples in both directions (JPEG / T.871, type 1).</summary>
    Center = 1,

    /// <summary>Co-sited with the top-left luma sample (type 2).</summary>
    TopLeft = 2,

    /// <summary>Centred horizontally, co-sited with the top row (type 3).</summary>
    Top = 3,

    /// <summary>Co-sited with the left sample of the bottom row (type 4).</summary>
    BottomLeft = 4,

    /// <summary>Centred horizontally, co-sited with the bottom row (type 5).</summary>
    Bottom = 5,
}

/// <summary>
/// The planes a YCbCr-coded image decodes to before colour conversion, and what is needed to interpret them.
/// </summary>
/// <param name="Width">Luma width in samples (the image width).</param>
/// <param name="Height">Luma height in samples.</param>
/// <param name="ChromaSubsamplingX">Horizontal chroma subsampling factor: 1 (4:4:4) or 2 (4:2:2, 4:2:0).</param>
/// <param name="ChromaSubsamplingY">Vertical chroma subsampling factor: 1 or 2 (4:2:0).</param>
/// <param name="BitDepth">Bits per sample, 8..16; samples are low-bit aligned in their 16-bit containers.</param>
/// <param name="Matrix">The YCbCr → RGB matrix.</param>
/// <param name="FullRange">True for full-range samples (0..2^N − 1), false for video range.</param>
/// <param name="ChromaLocation">Where chroma samples sit relative to luma.</param>
public readonly record struct PlanarInfo(int Width, int Height, int ChromaSubsamplingX, int ChromaSubsamplingY,
    int BitDepth, MatrixCoefficients Matrix, bool FullRange, ChromaLocation ChromaLocation)
{
    /// <summary>Chroma plane width: <c>⌈Width / ChromaSubsamplingX⌉</c>.</summary>
    public int ChromaWidth => (Width + ChromaSubsamplingX - 1) / ChromaSubsamplingX;

    /// <summary>Chroma plane height: <c>⌈Height / ChromaSubsamplingY⌉</c>.</summary>
    public int ChromaHeight => (Height + ChromaSubsamplingY - 1) / ChromaSubsamplingY;

    /// <summary>Samples in the luma plane.</summary>
    public int LumaLength => Width * Height;

    /// <summary>Samples in each chroma plane.</summary>
    public int ChromaLength => ChromaWidth * ChromaHeight;
}

/// <summary>
/// A decoder that can hand over its YCbCr planes as decoded, with no colour conversion: for transcoding without a
/// YCbCr → RGB → YCbCr round trip, and for video pipelines and GPU upload.
/// <para>
/// The planes are the codec's native output: its own chroma subsampling and bit depth, Y, Cb and Cr in that
/// order, tightly packed, samples low-bit aligned in 16-bit containers (a 10-bit plane holds 0..1023), not rescaled.
/// <see cref="PlanarInfo"/> carries everything needed to convert them. No conversion between planar layouts is
/// offered.
/// </para>
/// </summary>
public interface IPlanarImageDecoder : IImageDecoder
{
    /// <summary>
    /// Describes the planes <see cref="TryDecodePlanes"/> writes. False when the file is malformed or the image is not
    /// available as planes (an RGB-coded image, or one whose geometry the planes cannot carry, such as a HEIF crop
    /// with an odd origin under 4:2:0).
    /// </summary>
    static abstract bool TryReadPlanarInfo(ReadOnlySpan<byte> data, out PlanarInfo info);

    /// <summary>
    /// Decodes the planes into <paramref name="y"/>, <paramref name="cb"/> and <paramref name="cr"/>, sized from
    /// <see cref="TryReadPlanarInfo"/> (<see cref="PlanarInfo.LumaLength"/> and <see cref="PlanarInfo.ChromaLength"/>
    /// samples at least). False when the file is undecodable, a plane is too small, or the image is not available as
    /// planes.
    /// </summary>
    static abstract bool TryDecodePlanes(ReadOnlySpan<byte> data, Span<ushort> y, Span<ushort> cb, Span<ushort> cr,
        CancellationToken cancellationToken = default);
}
