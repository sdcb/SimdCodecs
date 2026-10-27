namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Decoded result of an ITU-T T.81 Annex H ("lossless JPEG", SOF3) bitstream.
/// Samples are stored in row-major order with components interleaved per the
/// scan order: for an interleaved 2-component scan the layout is
/// <c>c0[r,c], c1[r,c], c0[r,c+1], c1[r,c+1], …</c>. All sample values are
/// in <c>[0, 2^Precision)</c>.
///
/// This is a *codec* result — no demosaic, no Bayer interpretation, no
/// Canon-CR2 slice stitching. Callers that need a raw camera image must
/// stitch slices and apply white balance / black levels on top.
/// </summary>
public sealed record LosslessJpegResult(
    int Width,
    int Height,
    int Precision,
    int Components,
    ushort[] Samples);

/// <summary>
/// Decoder for lossless JPEG (ITU-T T.81 Annex H, SOF3 marker). This is a
/// completely separate code path from <see cref="JpegDecoder"/>: the DCT-based
/// decoder handles only SOF0/1/2 (baseline + progressive). Lossless JPEG is a
/// structurally different codec (Huffman-coded sample-difference predictors, no
/// quantisation, up to 16-bit precision) and needs its own implementation.
///
/// Used in practice for raw-camera files: Canon CR2's IFD3 raw strip carries
/// 14-bit Bayer data as a 2-component interleaved lossless JPEG sub-frame.
/// </summary>
public static class LosslessJpeg
{
    /// <summary>
    /// Decode a lossless JPEG bitstream supplied as a span.
    /// </summary>
    public static LosslessJpegResult FromMemory(ReadOnlySpan<byte> bytes)
        => LosslessJpegDecoder.Decode(bytes);

    /// <summary>
    /// Decode a lossless JPEG bitstream supplied as a byte array.
    /// </summary>
    public static LosslessJpegResult FromMemory(byte[] bytes)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        return LosslessJpegDecoder.Decode(bytes);
    }

    /// <summary>
    /// Decode a lossless JPEG bitstream from a stream. The stream is read
    /// to end into memory first — lossless JPEG decoding needs random-ish
    /// access within the entropy-coded data and isn't well suited to a
    /// pure streaming implementation.
    /// </summary>
    public static LosslessJpegResult FromStream(Stream stream)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return LosslessJpegDecoder.Decode(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }
}
