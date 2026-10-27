namespace Sdcb.SimdCodecs.Webp;

/// <summary>Options for <see cref="WebpDecoder.DecodeTo(ReadOnlySpan{byte}, Span{byte}, int, WebpDecodeOptions?)"/>.</summary>
public sealed record WebpDecodeOptions
{
    /// <summary>
    /// Decode independent parts of an image on a second (thread-pool) thread. Off by default: the
    /// default decode is single-threaded, like libwebp's simple decoding API.
    /// </summary>
    /// <remarks>
    /// What runs concurrently today: the transparency (ALPH) plane of a lossy image is decoded while
    /// the calling thread decodes the VP8 frame. Lossy images without transparency, lossless and
    /// animated images decode exactly as without this option. The output is identical either way.
    /// It allocates a little per call (a task and a copy of the ALPH chunk).
    /// </remarks>
    public bool Multithreaded { get; init; }
}
