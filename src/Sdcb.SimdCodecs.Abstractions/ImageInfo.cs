namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// Dimensions + layout read from an image's header alone, without decoding
/// pixels. Lets a caller size a destination buffer before calling
/// <see cref="IImageDecoder.TryDecodeInto"/> - the info-then-decode split
/// every real decoder already supports (PNG's IHDR / JPEG's SOF precede the
/// pixel data).
/// </summary>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Channels">Samples per pixel: 1 (gray), 2 (gray+alpha), 3 (RGB), 4 (RGBA).</param>
/// <param name="SampleFormat">The per-channel sample type the full decode will produce.</param>
public readonly record struct ImageInfo(int Width, int Height, int Channels, SampleFormat SampleFormat);
