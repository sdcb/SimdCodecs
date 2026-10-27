namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// Single-strip passthrough source for raw JPEG bytes.
/// No re-encoding — bytes go straight into the TIFF strip.
/// </summary>
public sealed class JpegPassthroughSource : IImageSource
{
    private readonly ReadOnlyMemory<byte> _jpegBytes;

    public JpegPassthroughSource(ReadOnlyMemory<byte> jpegBytes, int width, int height)
    {
        _jpegBytes = jpegBytes;
        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }
    public int SegmentCount => 1;
    public TiffCompression Compression => TiffCompression.Jpeg;
    public TiffPhotometric Photometric => TiffPhotometric.YCbCr;
    public bool IsPreCompressed => true;

    public ValueTask<ReadOnlyMemory<byte>> GetSegmentAsync(int index, CancellationToken ct = default)
    {
        if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
        return ValueTask.FromResult(_jpegBytes);
    }
}
