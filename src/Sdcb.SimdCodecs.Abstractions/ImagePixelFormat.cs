namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// The 8-bit pixel layouts a decoder can write straight into a caller's buffer
/// (<see cref="IImageDecoder.TryDecodeInto"/>). Every layout is display-referred
/// code values: 16-bit samples keep their high byte (<c>v &gt;&gt; 8</c>), as
/// <see cref="IDecodedImage.ToRgba8"/> does. Conversions follow
/// <see cref="PixelConverter"/>.
/// </summary>
public enum ImagePixelFormat
{
    /// <summary>4 bytes per pixel: R, G, B, A. A missing alpha is 255.</summary>
    Rgba32,

    /// <summary>4 bytes per pixel: B, G, R, A — the layout of most Windows, WPF and Skia surfaces.
    /// A missing alpha is 255.</summary>
    Bgra32,

    /// <summary>3 bytes per pixel: R, G, B. Alpha is dropped, not composited.</summary>
    Rgb24,

    /// <summary>3 bytes per pixel: B, G, R — OpenCV's default. Alpha is dropped, not composited.</summary>
    Bgr24,

    /// <summary>1 byte per pixel: the image's luma. Grey sources keep their samples. A codec that
    /// stores luma returns it as stored — a YCbCr JPEG its Y plane, skipping chroma entirely, as
    /// libjpeg-turbo and OpenCV do — which can differ by a code value or two from the luma of the
    /// decoded RGB. Other colour sources take the BT.601 luma of their code values,
    /// <c>(77·R + 150·G + 29·B + 128) &gt;&gt; 8</c>. Alpha is dropped, not composited.</summary>
    Gray8,
}
