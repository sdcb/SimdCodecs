namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// Lossy reduction to a palette of at most 256 colours (alpha included, written as PLTE + tRNS), for
/// <see cref="PngWriteOptions.Quantize"/>: the kind of file pngquant makes, typically a third to a quarter of a
/// truecolour PNG's size.
/// </summary>
/// <remarks>
/// <para>Colours are compared in OKLab, premultiplied by alpha, with alpha as a fourth coordinate. The palette is a
/// variance-minimising box split of the image's colours refined by k-means; pixels map to the nearest entry, with
/// optional edge-aware Floyd–Steinberg dithering.</para>
/// <para><b>Quality</b> is a 0–100 scale defined in this library: each value stands for a target colour error (an RMS
/// distance in premultiplied OKLab). The scale was set so that a given quality reaches about the error pngquant
/// 2.17's <c>--quality</c> of the same number reaches, measured on photographs and UI images; the palette sizes and
/// files that come out are this library's own, not pngquant's. <see cref="MaxQuality"/> chooses the fewest colours
/// that reach it (100: as many as <see cref="MaxColors"/> allows); when even <see cref="MaxColors"/> colours stay
/// below <see cref="MinQuality"/>, the image is written losslessly instead, as if <see cref="PngWriteOptions.Quantize"/>
/// were not set.</para>
/// <para>At <see cref="MaxQuality"/> 100 an image with no more distinct colours than <see cref="MaxColors"/> is written
/// exactly; below 100 it too gets the fewest colours that reach the quality. 16-bit input is rounded to 8 bits
/// first.</para>
/// <para>For the smallest file pair it with <see cref="PngWriteOptions.CompressionLevel"/> =
/// <see cref="System.IO.Compression.CompressionLevel.SmallestSize"/>: an indexed image is one byte a pixel or less, so
/// the strongest deflate costs little time, and on flat images it saves up to a third.</para>
/// </remarks>
public sealed record PngQuantizeOptions
{
    /// <summary>The most palette entries to use, 2–256. Default 256.</summary>
    public int MaxColors { get; init; } = 256;

    /// <summary>The lowest quality (0–100) worth writing; below it the image is written losslessly. Default 0: always quantize.</summary>
    public int MinQuality { get; init; }

    /// <summary>The quality (0–100) to aim for with as few colours as possible. Default 100: use up to <see cref="MaxColors"/>.</summary>
    public int MaxQuality { get; init; } = 100;

    /// <summary>
    /// Floyd–Steinberg error diffusion, strongest on smooth areas (where it turns banding into fine noise) and fading
    /// out across edges and texture (where the error is masked and noise only costs bytes). Default on.
    /// </summary>
    public bool Dither { get; init; } = true;
}
