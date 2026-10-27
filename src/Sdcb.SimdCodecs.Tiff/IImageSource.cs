namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// Provides pre-compressed or raw image data for a single TIFF page.
/// Segments are strips or tiles depending on the page layout.
/// </summary>
public interface IImageSource
{
    int Width { get; }
    int Height { get; }
    int SegmentCount { get; }
    TiffCompression Compression { get; }
    TiffPhotometric Photometric { get; }

    /// <summary>True if segment bytes are already compressed and should be written verbatim.</summary>
    bool IsPreCompressed { get; }

    ValueTask<ReadOnlyMemory<byte>> GetSegmentAsync(int index, CancellationToken ct = default);
}
