namespace Sdcb.SimdCodecs.Heif.Hevc;

/// <summary>
/// A decoded HEVC picture: three sample planes at the coded size (all bit depths stored as 16-bit), plus the
/// conformance cropping window that defines the output picture (§7.4.3.2.1).
/// </summary>
internal sealed class HevcPicture
{
    internal HevcPicture(int width, int height, int chromaFormatIdc, int bitDepthY, int bitDepthC)
    {
        Width = width;
        Height = height;
        ChromaFormatIdc = chromaFormatIdc;
        BitDepthY = bitDepthY;
        BitDepthC = bitDepthC;
        SubWidthC = chromaFormatIdc is 1 or 2 ? 2 : 1;
        SubHeightC = chromaFormatIdc == 1 ? 2 : 1;
        ChromaWidth = chromaFormatIdc == 0 ? 0 : width / SubWidthC;
        ChromaHeight = chromaFormatIdc == 0 ? 0 : height / SubHeightC;
        Planes =
        [
            new ushort[(long)width * height],
            new ushort[(long)ChromaWidth * ChromaHeight],
            new ushort[(long)ChromaWidth * ChromaHeight],
        ];
    }

    /// <summary>Coded width in luma samples (pic_width_in_luma_samples).</summary>
    internal int Width { get; }

    /// <summary>Coded height in luma samples.</summary>
    internal int Height { get; }

    internal int ChromaFormatIdc { get; }
    internal int BitDepthY { get; }
    internal int BitDepthC { get; }
    internal int SubWidthC { get; }
    internal int SubHeightC { get; }
    internal int ChromaWidth { get; }
    internal int ChromaHeight { get; }

    /// <summary>Y, Cb, Cr, row-major, stride = the plane's width.</summary>
    internal ushort[][] Planes { get; }

    /// <summary>Conformance window in luma samples.</summary>
    internal int CropLeft, CropRight, CropTop, CropBottom;

    internal int OutputWidth => Width - CropLeft - CropRight;
    internal int OutputHeight => Height - CropTop - CropBottom;

    internal int PlaneWidth(int cIdx) => cIdx == 0 ? Width : ChromaWidth;
    internal int PlaneHeight(int cIdx) => cIdx == 0 ? Height : ChromaHeight;
}
