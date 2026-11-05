namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// How the decoder interprets a frame's components, for a container that says so itself (TIFF's
/// PhotometricInterpretation) rather than through the JFIF / Adobe markers and component IDs a JPEG file uses.
/// </summary>
public enum JpegColorTransform
{
    /// <summary>
    /// The stream decides, as <see cref="JpegDecoder.Decode(ReadOnlySpan{byte}, JpegScale)"/> always has: three
    /// components are RGB when their IDs are 'R', 'G', 'B' or an Adobe marker says transform 0 (without JFIF), else
    /// YCbCr; four are Adobe CMYK (transform 0), YCCK (transform 2) or YCbCr with K ignored.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// No colour transform: the components are the output. Three components are written as R, G, B; four as R, G, B,
    /// A with the fourth component in A (a CMYK frame thus arrives as C, M, Y, K, unconverted); one as grey.
    /// </summary>
    None = 1,

    /// <summary>
    /// The first three components are Y, Cb, Cr (T.871), converted to RGB whatever the markers say; a fourth is K
    /// of a YCCK frame.
    /// </summary>
    YCbCr = 2,
}

/// <summary>
/// What a JPEG stream embedded in another container needs from it: the tables it was coded with, when they are stored
/// apart from it, and the meaning of its components. Pass to
/// <see cref="JpegDecoder.DecodeTo(ReadOnlySpan{byte}, Span{byte}, int, JpegPixelFormat, JpegDecodeOptions?, JpegScale, CancellationToken)"/>,
/// <see cref="JpegDecoder.Decode(ReadOnlySpan{byte}, JpegDecodeOptions?, JpegScale)"/> or
/// <see cref="JpegDecoder.ReadInfo(ReadOnlySpan{byte}, JpegDecodeOptions?)"/>.
/// </summary>
/// <remarks>
/// Only which conversion runs is selected here; the conversions themselves are the decoder's frozen arithmetic, so a
/// stream decoded with tables supplied equals the same stream with those tables spliced in.
/// </remarks>
public sealed class JpegDecodeOptions
{
    /// <summary>
    /// An abbreviated table-specification stream (ITU-T T.81 B.5): SOI, then DQT / DHT (and DRI, APPn, COM) segments,
    /// then EOI, as TIFF's JPEGTables tag (347, TIFF Technical Note 2) holds them. Its tables are loaded before the
    /// image stream is read, and the image stream's own tables, if any, replace them as usual. Empty for none.
    /// </summary>
    public ReadOnlyMemory<byte> Tables { get; init; }

    /// <summary>How the components are interpreted; <see cref="JpegColorTransform.Auto"/> by default.</summary>
    public JpegColorTransform ColorTransform { get; init; }
}
