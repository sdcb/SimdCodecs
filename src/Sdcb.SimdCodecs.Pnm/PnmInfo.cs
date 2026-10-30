using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Pnm;

/// <summary>The Netpbm formats, by their magic number.</summary>
public enum PnmFormat
{
    /// <summary>Chosen from the input when encoding: P5 for grey, P6 for RGB, P7 when there is alpha.</summary>
    Auto = 0,

    /// <summary>P1: plain (ASCII) PBM, 1 bit per pixel, 1 is black.</summary>
    PlainBitmap = 1,

    /// <summary>P2: plain (ASCII) PGM, grey.</summary>
    PlainGraymap = 2,

    /// <summary>P3: plain (ASCII) PPM, RGB.</summary>
    PlainPixmap = 3,

    /// <summary>P4: raw PBM, 1 bit per pixel packed into bytes, 1 is black.</summary>
    Bitmap = 4,

    /// <summary>P5: raw PGM, grey, 1 or 2 bytes per sample.</summary>
    Graymap = 5,

    /// <summary>P6: raw PPM, RGB, 1 or 2 bytes per sample.</summary>
    Pixmap = 6,

    /// <summary>P7: PAM, 1 to 4 channels named by a tuple type, 1 or 2 bytes per sample.</summary>
    Arbitrary = 7,

    /// <summary><c>Pf</c>: PFM grey, 32-bit float samples.</summary>
    FloatGraymap = 8,

    /// <summary><c>PF</c>: PFM RGB, 32-bit float samples.</summary>
    FloatPixmap = 9,
}

/// <summary>What a Netpbm header says about an image.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Format">The magic number.</param>
/// <param name="Channels">1 (grey), 2 (grey and alpha), 3 (RGB) or 4 (RGBA).</param>
/// <param name="MaxValue">The largest sample value: 1 for a bitmap, 1 to 65535 otherwise, 0 for PFM.</param>
/// <param name="TupleType">A PAM header's tuple type, or null.</param>
/// <param name="Scale">A PFM header's scale: negative for little-endian samples. Its magnitude is reported, not
/// applied. 0 for the other formats.</param>
public readonly record struct PnmInfo(int Width, int Height, PnmFormat Format, int Channels, int MaxValue, string? TupleType, float Scale)
{
    /// <summary>The samples <see cref="PnmDecoder.Decode"/> returns: 8-bit up to a maximum of 255, 16-bit above,
    /// float for PFM.</summary>
    public SampleFormat SampleFormat =>
        Format is PnmFormat.FloatGraymap or PnmFormat.FloatPixmap ? SampleFormat.Float32 : MaxValue > 255 ? SampleFormat.UInt16 : SampleFormat.UInt8;

    /// <summary>True for the plain (ASCII) formats P1 to P3.</summary>
    public bool IsPlain => Format is PnmFormat.PlainBitmap or PnmFormat.PlainGraymap or PnmFormat.PlainPixmap;
}

/// <summary>
/// A decoded Netpbm image. Integer samples are scaled from the file's maximum to the full range of
/// <see cref="SampleFormat"/>; 16-bit and float samples are in host byte order.
/// </summary>
public sealed class PnmImage
{
    internal PnmImage(PnmInfo info, byte[] pixels)
    {
        Info = info;
        Pixels = pixels;
    }

    /// <summary>The header.</summary>
    public PnmInfo Info { get; }

    /// <summary>Width in pixels.</summary>
    public int Width => Info.Width;

    /// <summary>Height in pixels.</summary>
    public int Height => Info.Height;

    /// <summary>1 (grey), 2 (grey and alpha), 3 (RGB) or 4 (RGBA).</summary>
    public int Channels => Info.Channels;

    /// <summary>8-bit, 16-bit or 32-bit float samples.</summary>
    public SampleFormat SampleFormat => Info.SampleFormat;

    /// <summary>The samples, top row first, tightly packed.</summary>
    public byte[] Pixels { get; }
}
