namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// Image source from raw pixel data. Supports both strip and tiled layouts.
/// Returns raw (uncompressed) segment bytes; <see cref="TiffWriter"/> is responsible for
/// any compression based on <see cref="TiffPageOptions.Compression"/> — having
/// <see cref="PixelSource"/> also compress here would double-compress the data.
/// </summary>
public sealed class PixelSource : IImageSource
{
    private readonly ReadOnlyMemory<byte> _pixels;
    private readonly int _bytesPerRow;
    private readonly int _rowsPerSegment;
    private readonly int _segmentWidth;
    private readonly int _segmentHeight;
    private readonly TiffLayout _layout;

    public PixelSource(ReadOnlyMemory<byte> pixels, int width, int height, TiffPageOptions options)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        Compression = options.Compression;

        var bytesPerPixel = options.SamplesPerPixel * (options.BitsPerSample / 8);
        _bytesPerRow = width * bytesPerPixel;
        _layout = options.Layout;

        if (_layout == TiffLayout.Tiled)
        {
            _segmentWidth = options.TileWidth;
            _segmentHeight = options.TileHeight;
            var tilesX = (width + _segmentWidth - 1) / _segmentWidth;
            var tilesY = (height + _segmentHeight - 1) / _segmentHeight;
            SegmentCount = tilesX * tilesY;
            _rowsPerSegment = _segmentHeight;
        }
        else
        {
            _segmentWidth = width;
            _segmentHeight = options.RowsPerStrip > 0 ? options.RowsPerStrip : height;
            _rowsPerSegment = _segmentHeight;
            SegmentCount = (height + _rowsPerSegment - 1) / _rowsPerSegment;
        }

        Photometric = options.Photometric;
    }

    public int Width { get; }
    public int Height { get; }
    public int SegmentCount { get; }
    public TiffCompression Compression { get; }
    public TiffPhotometric Photometric { get; }
    public bool IsPreCompressed => false;

    public ValueTask<ReadOnlyMemory<byte>> GetSegmentAsync(int index, CancellationToken ct = default)
    {
        if ((uint)index >= (uint)SegmentCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (_layout == TiffLayout.Tiled)
        {
            var tilesX = (Width + _segmentWidth - 1) / _segmentWidth;
            var tileX = index % tilesX;
            var tileY = index / tilesX;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(ExtractTile(tileX, tileY));
        }

        var startRow = index * _rowsPerSegment;
        var rows = Math.Min(_rowsPerSegment, Height - startRow);
        var offset = startRow * _bytesPerRow;
        var length = rows * _bytesPerRow;
        return ValueTask.FromResult<ReadOnlyMemory<byte>>(_pixels.Slice(offset, length));
    }

    private byte[] ExtractTile(int tileX, int tileY)
    {
        var bytesPerPixel = _bytesPerRow / Width;
        var tileRowBytes = _segmentWidth * bytesPerPixel;
        var tileBytes = new byte[tileRowBytes * _segmentHeight];

        var srcStartRow = tileY * _segmentHeight;
        var srcStartCol = tileX * _segmentWidth;
        var actualRows = Math.Min(_segmentHeight, Height - srcStartRow);
        var actualCols = Math.Min(_segmentWidth, Width - srcStartCol);
        var actualColBytes = actualCols * bytesPerPixel;

        var src = _pixels.Span;
        for (var row = 0; row < actualRows; row++)
        {
            var srcOffset = (srcStartRow + row) * _bytesPerRow + srcStartCol * bytesPerPixel;
            var dstOffset = row * tileRowBytes;
            src.Slice(srcOffset, actualColBytes).CopyTo(tileBytes.AsSpan(dstOffset));
            // Remaining bytes in the row are already zero (edge padding)
        }

        return tileBytes;
    }
}
