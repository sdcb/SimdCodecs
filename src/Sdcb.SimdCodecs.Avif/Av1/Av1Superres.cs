namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// AV1 horizontal Superres upscaling process (§7.16), applied after CDEF and before Loop Restoration.
/// </summary>
internal static class Av1Superres
{
    private const int SuperresScaleBits = 14;
    private const int SuperresScaleMask = (1 << SuperresScaleBits) - 1;
    private const int SuperresExtraBits = 8;
    private const int SuperresFilterTaps = 8;
    private const int SuperresFilterOffset = 3;
    private const int FilterBits = 7;

    // Upscale_Filter[64][8] (§7.16)
    private static readonly short[] UpscaleFilter =
    [
           0,    0,    0,  128,    0,    0,    0,    0,
           0,    0,   -1,  128,    2,   -1,    0,    0,
           0,    1,   -3,  127,    4,   -2,    1,    0,
           0,    1,   -4,  127,    6,   -3,    1,    0,
           0,    2,   -6,  126,    8,   -3,    1,    0,
           0,    2,   -7,  125,   11,   -4,    1,    0,
          -1,    2,   -8,  125,   13,   -5,    2,    0,
          -1,    3,   -9,  124,   15,   -6,    2,    0,
          -1,    3,  -10,  123,   18,   -6,    2,   -1,
          -1,    3,  -11,  122,   20,   -7,    3,   -1,
          -1,    4,  -12,  121,   22,   -8,    3,   -1,
          -1,    4,  -13,  120,   25,   -9,    3,   -1,
          -1,    4,  -14,  118,   28,   -9,    3,   -1,
          -1,    4,  -15,  117,   30,  -10,    4,   -1,
          -1,    5,  -16,  116,   32,  -11,    4,   -1,
          -1,    5,  -16,  114,   35,  -12,    4,   -1,
          -1,    5,  -17,  112,   38,  -12,    4,   -1,
          -1,    5,  -18,  111,   40,  -13,    5,   -1,
          -1,    5,  -18,  109,   43,  -14,    5,   -1,
          -1,    6,  -19,  107,   45,  -14,    5,   -1,
          -1,    6,  -19,  105,   48,  -15,    5,   -1,
          -1,    6,  -19,  103,   51,  -16,    5,   -1,
          -1,    6,  -20,  101,   53,  -16,    6,   -1,
          -1,    6,  -20,   99,   56,  -17,    6,   -1,
          -1,    6,  -20,   97,   58,  -17,    6,   -1,
          -1,    6,  -20,   95,   61,  -18,    6,   -1,
          -2,    7,  -20,   93,   64,  -18,    6,   -2,
          -2,    7,  -20,   91,   66,  -19,    6,   -1,
          -2,    7,  -20,   88,   69,  -19,    6,   -1,
          -2,    7,  -20,   86,   71,  -19,    6,   -1,
          -2,    7,  -20,   84,   74,  -20,    7,   -2,
          -2,    7,  -20,   81,   76,  -20,    7,   -1,
          -2,    7,  -20,   79,   79,  -20,    7,   -2,
          -1,    7,  -20,   76,   81,  -20,    7,   -2,
          -2,    7,  -20,   74,   84,  -20,    7,   -2,
          -1,    6,  -19,   71,   86,  -20,    7,   -2,
          -1,    6,  -19,   69,   88,  -20,    7,   -2,
          -1,    6,  -19,   66,   91,  -20,    7,   -2,
          -2,    6,  -18,   64,   93,  -20,    7,   -2,
          -1,    6,  -18,   61,   95,  -20,    6,   -1,
          -1,    6,  -17,   58,   97,  -20,    6,   -1,
          -1,    6,  -17,   56,   99,  -20,    6,   -1,
          -1,    6,  -16,   53,  101,  -20,    6,   -1,
          -1,    5,  -16,   51,  103,  -19,    6,   -1,
          -1,    5,  -15,   48,  105,  -19,    6,   -1,
          -1,    5,  -14,   45,  107,  -19,    6,   -1,
          -1,    5,  -14,   43,  109,  -18,    5,   -1,
          -1,    5,  -13,   40,  111,  -18,    5,   -1,
          -1,    4,  -12,   38,  112,  -17,    5,   -1,
          -1,    4,  -12,   35,  114,  -16,    5,   -1,
          -1,    4,  -11,   32,  116,  -16,    5,   -1,
          -1,    4,  -10,   30,  117,  -15,    4,   -1,
          -1,    3,   -9,   28,  118,  -14,    4,   -1,
          -1,    3,   -9,   25,  120,  -13,    4,   -1,
          -1,    3,   -8,   22,  121,  -12,    4,   -1,
          -1,    3,   -7,   20,  122,  -11,    3,   -1,
          -1,    2,   -6,   18,  123,  -10,    3,   -1,
           0,    2,   -6,   15,  124,   -9,    3,   -1,
           0,    2,   -5,   13,  125,   -8,    2,   -1,
           0,    1,   -4,   11,  125,   -7,    2,    0,
           0,    1,   -3,    8,  126,   -6,    2,    0,
           0,    1,   -3,    6,  127,   -4,    1,    0,
           0,    1,   -2,    4,  127,   -3,    1,    0,
           0,    0,   -1,    2,  128,   -1,    0,    0,
    ];

    /// <summary>
    /// Horizontally upscales every plane in <paramref name="srcPlanes"/> from <c>FrameWidth</c>
    /// to <c>UpscaledWidth</c> into <paramref name="dstPlanes"/> (sharing <see cref="Av1Picture.PlaneStrides"/>).
    /// </summary>
    internal static void Apply(
        Av1SequenceHeader seq,
        Av1FrameHeader fh,
        Av1Picture pic,
        ushort[][] srcPlanes,
        ushort[][] dstPlanes)
    {
        int bitDepth = seq.BitDepth;
        for (int plane = 0; plane < seq.NumPlanes; plane++)
        {
            int subX = plane > 0 ? seq.SubsamplingX : 0;
            int subY = plane > 0 ? seq.SubsamplingY : 0;

            int downscaledPlaneW = Av1Constants.Round2(fh.FrameWidth, subX);
            int upscaledPlaneW = Av1Constants.Round2(fh.UpscaledWidth, subX);
            int planeH = Av1Constants.Round2(fh.FrameHeight, subY);

            long stepX = (((long)downscaledPlaneW << SuperresScaleBits) + (upscaledPlaneW / 2)) / upscaledPlaneW;
            long err = (upscaledPlaneW * stepX) - ((long)downscaledPlaneW << SuperresScaleBits);
            long initialSubpelX = (-(((long)upscaledPlaneW - downscaledPlaneW) << (SuperresScaleBits - 1)) + upscaledPlaneW / 2) / upscaledPlaneW
                                + (1 << (SuperresExtraBits - 1)) - err / 2;
            initialSubpelX &= SuperresScaleMask;

            int miW = fh.MiCols >> subX;
            int maxX = miW * 4 - 1;
            int stride = pic.PlaneStrides[plane];
            ushort[] src = srcPlanes[plane];
            ushort[] dst = dstPlanes[plane];

            if (planeH >= 64)
            {
                Parallel.For(0, planeH, AvifDiagnostics.ParallelOptions, y => UpscaleRow(src, dst, stride, y, upscaledPlaneW, initialSubpelX, stepX, maxX, bitDepth));
            }
            else
            {
                for (int y = 0; y < planeH; y++)
                    UpscaleRow(src, dst, stride, y, upscaledPlaneW, initialSubpelX, stepX, maxX, bitDepth);
            }
        }
    }

    private static void UpscaleRow(
        ushort[] src, ushort[] dst, int stride, int y,
        int upscaledPlaneW, long initialSubpelX, long stepX, int maxX, int bitDepth)
    {
        int rowOff = y * stride;
        for (int x = 0; x < upscaledPlaneW; x++)
        {
            long srcX = -(1L << SuperresScaleBits) + initialSubpelX + x * stepX;
            int srcXPx = (int)(srcX >> SuperresScaleBits);
            int srcXSubpel = (int)((srcX & SuperresScaleMask) >> SuperresExtraBits);
            int fOff = srcXSubpel * SuperresFilterTaps;
            int sum = 0;
            for (int k = 0; k < SuperresFilterTaps; k++)
            {
                int sampleX = Av1Constants.Clip3(0, maxX, srcXPx + (k - SuperresFilterOffset));
                sum += src[rowOff + sampleX] * UpscaleFilter[fOff + k];
            }
            dst[rowOff + x] = (ushort)Av1Constants.Clip1(Av1Constants.Round2(sum, FilterBits), bitDepth);
        }
    }
}
