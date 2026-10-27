namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// The arithmetic of where a page's segments (strips or tiles, and for PlanarConfig=Separate one set per
/// plane) sit in the offset arrays and in the picture. One place, so the whole-page read and any
/// on-demand read of a single segment cannot come to disagree about which index covers which rows.
/// </summary>
/// <remarks>
/// TIFF 6.0 p.22 and p.68: with PlanarConfig=2 the offset arrays hold SamplesPerPixel planes one after
/// another, each plane carrying its own full set of strips (or tiles); plane <c>p</c>'s segment
/// <c>k</c> is entry <c>p * SegmentsPerPlane + k</c>. A page with one sample per pixel has the same
/// layout either way, so it is treated as chunky.
/// </remarks>
internal readonly struct TiffSegmentGeometry
{
    public TiffSegmentGeometry(int width, int height, int samplesPerPixel, bool separatePlanes,
        bool isTiled, int rowsPerStrip, int tileWidth, int tileHeight)
    {
        Width = width;
        Height = height;
        SamplesPerPixel = samplesPerPixel;
        IsTiled = isTiled;
        IsPlanar = separatePlanes && samplesPerPixel > 1;
        TileWidth = isTiled ? tileWidth : 0;

        // RowsPerStrip defaults to the whole image in one strip (2^32-1 per TIFF 6.0), and a file may
        // omit it, so a missing or absurd value becomes the image height rather than a zero step.
        // A tile taller than the image is clamped the same way: rows past the image are padding.
        SegmentHeight = isTiled
            ? Math.Min(tileHeight, height)
            : rowsPerStrip > 0 && rowsPerStrip <= height ? rowsPerStrip : height;

        SegmentsAcross = isTiled ? (width + tileWidth - 1) / tileWidth : 1;
        SegmentsDown = (height + SegmentHeight - 1) / SegmentHeight;
    }

    public int Width { get; }
    public int Height { get; }
    public int SamplesPerPixel { get; }
    public bool IsTiled { get; }

    /// <summary>True when the offset arrays hold one run of segments per sample (PlanarConfig=Separate).</summary>
    public bool IsPlanar { get; }

    /// <summary>Tile width, or 0 for a strip.</summary>
    public int TileWidth { get; }

    /// <summary>Rows in a full strip, or in a tile band (clamped to the image height).</summary>
    public int SegmentHeight { get; }

    /// <summary>Tiles per row of tiles; 1 for strips.</summary>
    public int SegmentsAcross { get; }

    /// <summary>Strips, or rows of tiles, per plane.</summary>
    public int SegmentsDown { get; }

    public int PlaneCount => IsPlanar ? SamplesPerPixel : 1;

    /// <summary>Samples per pixel inside one segment: 1 when planar, else the page's.</summary>
    public int SegmentSamplesPerPixel => IsPlanar ? 1 : SamplesPerPixel;

    public int SegmentsPerPlane => SegmentsAcross * SegmentsDown;

    public int TotalSegments => PlaneCount * SegmentsPerPlane;

    /// <summary>Index into the offset / byte-count arrays of one segment of one plane.</summary>
    public int SegmentIndex(int plane, int down, int across = 0) =>
        (plane * SegmentsPerPlane) + (down * SegmentsAcross) + across;

    /// <summary>Which strip (or row of tiles) covers an image row.</summary>
    public int DownIndexForRow(int row) => row / SegmentHeight;

    /// <summary>Which tile column covers an image column; 0 for strips.</summary>
    public int AcrossIndexForColumn(int column) => IsTiled ? column / TileWidth : 0;

    public int FirstRow(int down) => down * SegmentHeight;

    /// <summary>Image rows a strip / tile band really covers; the last one is the remainder.</summary>
    public int RowCount(int down) => Math.Min(SegmentHeight, Height - (down * SegmentHeight));
}
