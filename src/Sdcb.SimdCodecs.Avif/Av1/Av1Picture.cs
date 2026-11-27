using System.Buffers;

namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// A decoded AV1 picture holding Y (and optionally Cb, Cr) planes as 16-bit samples in [0, (1 &lt;&lt; BitDepth) - 1].
/// </summary>
internal sealed class Av1Picture : IDisposable
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
    internal int[] PlaneLengths { get; }
    internal ushort[][] Planes { get; private set; }
    internal bool PlanesPooled { get; private set; }

    internal Av1Picture(Av1SequenceHeader seq, Av1FrameHeader fh, bool pooled = true)
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
        PlaneLengths = new int[numPlanes];
        Planes = new ushort[numPlanes][];
        PlanesPooled = pooled;

        int sbPx = seq.Use128x128Superblock ? 128 : 64;
        int miWidthPx = Math.Max(((fh.MiCols * 4) + sbPx - 1) & ~(sbPx - 1), fh.UpscaledWidth);
        int miHeightPx = ((fh.MiRows * 4) + sbPx - 1) & ~(sbPx - 1);

        for (int p = 0; p < numPlanes; p++)
        {
            int sx = p == 0 ? 0 : seq.SubsamplingX;
            int sy = p == 0 ? 0 : seq.SubsamplingY;
            int stride = (miWidthPx + sx) >> sx;
            int height = (miHeightPx + sy) >> sy;
            int len = stride * height;
            PlaneStrides[p] = stride;
            PlaneHeights[p] = height;
            PlaneLengths[p] = len;
            if (pooled)
            {
                var buf = ArrayPool<ushort>.Shared.Rent(len);
                // Clear only what is needed? No, tile decoding writes every coded pixel; clear padding to keep deterministic edges.
                buf.AsSpan(0, len).Clear();
                Planes[p] = buf;
            }
            else
            {
                Planes[p] = new ushort[len];
            }
        }
    }

    internal void TakePlanes(ushort[][] planes, bool pooled)
    {
        Planes = planes;
        PlanesPooled = pooled;
    }

    internal int OutputPlaneWidth(int plane) => plane == 0 ? Width : (Width + SubsamplingX) >> SubsamplingX;
    internal int OutputPlaneHeight(int plane) => plane == 0 ? Height : (Height + SubsamplingY) >> SubsamplingY;

    internal Av1Picture Clone()
    {
        var copy = new Av1Picture(this);
        for (int p = 0; p < Planes.Length; p++)
            Planes[p].AsSpan(0, PlaneLengths[p]).CopyTo(copy.Planes[p]);
        return copy;
    }

    public void Dispose()
    {
        if (PlanesPooled)
        {
            PlanesPooled = false;
            Av1FrameDecoder.ReturnPlanes(Planes);
        }
    }

    private Av1Picture(Av1Picture src)
    {
        Width = src.Width;
        Height = src.Height;
        BitDepth = src.BitDepth;
        SubsamplingX = src.SubsamplingX;
        SubsamplingY = src.SubsamplingY;
        Monochrome = src.Monochrome;
        ColorPrimaries = src.ColorPrimaries;
        TransferCharacteristics = src.TransferCharacteristics;
        MatrixCoefficients = src.MatrixCoefficients;
        FullRange = src.FullRange;
        ChromaSamplePosition = src.ChromaSamplePosition;
        PlaneStrides = (int[])src.PlaneStrides.Clone();
        PlaneHeights = (int[])src.PlaneHeights.Clone();
        PlaneLengths = (int[])src.PlaneLengths.Clone();
        Planes = new ushort[src.Planes.Length][];
        PlanesPooled = false;
        for (int p = 0; p < Planes.Length; p++)
            Planes[p] = new ushort[src.PlaneLengths[p]];
    }
}
