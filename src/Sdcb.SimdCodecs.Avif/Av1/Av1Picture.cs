namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// A decoded AV1 picture holding Y (and optionally Cb, Cr) planes as 16-bit samples in [0, (1 &lt;&lt; BitDepth) - 1].
/// </summary>
internal sealed class Av1Picture
{
    internal int Width { get; }
    internal int Height { get; }
    internal int BitDepth { get; }
    internal int SubsamplingX { get; }
    internal int SubsamplingY { get; }
    internal bool Monochrome { get; }
    internal int ColorPrimaries { get; }
    internal int TransferCharacteristics { get; }
    internal int MatrixCoefficients { get; }
    internal bool FullRange { get; }
    internal int ChromaSamplePosition { get; }

    /// <summary>Padded buffer widths (aligned to MI grid = multiple of 8 for luma, 4 for 4:2:0 chroma).</summary>
    internal int[] PlaneStrides { get; }
    internal int[] PlaneHeights { get; }
    internal ushort[][] Planes { get; }

    internal Av1Picture(Av1SequenceHeader seq, Av1FrameHeader fh)
    {
        Width = fh.UpscaledWidth;
        Height = fh.FrameHeight;
        BitDepth = seq.BitDepth;
        SubsamplingX = seq.SubsamplingX;
        SubsamplingY = seq.SubsamplingY;
        Monochrome = seq.Monochrome;
        ColorPrimaries = seq.ColorPrimaries;
        TransferCharacteristics = seq.TransferCharacteristics;
        MatrixCoefficients = seq.MatrixCoefficients;
        FullRange = seq.ColorRangeFull;
        ChromaSamplePosition = seq.ChromaSamplePosition;

        int numPlanes = seq.NumPlanes;
        PlaneStrides = new int[numPlanes];
        PlaneHeights = new int[numPlanes];
        Planes = new ushort[numPlanes][];

        int sbPx = seq.Use128x128Superblock ? 128 : 64;
        int miWidthPx = ((fh.MiCols * 4) + sbPx - 1) & ~(sbPx - 1);
        int miHeightPx = ((fh.MiRows * 4) + sbPx - 1) & ~(sbPx - 1);

        for (int p = 0; p < numPlanes; p++)
        {
            int sx = p == 0 ? 0 : seq.SubsamplingX;
            int sy = p == 0 ? 0 : seq.SubsamplingY;
            int stride = (miWidthPx + sx) >> sx;
            int height = (miHeightPx + sy) >> sy;
            PlaneStrides[p] = stride;
            PlaneHeights[p] = height;
            Planes[p] = new ushort[stride * height];
        }
    }

    internal int OutputPlaneWidth(int plane) => plane == 0 ? Width : (Width + SubsamplingX) >> SubsamplingX;
    internal int OutputPlaneHeight(int plane) => plane == 0 ? Height : (Height + SubsamplingY) >> SubsamplingY;
}
