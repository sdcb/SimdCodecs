using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for TIFF, bridging <see cref="TiffReader"/>
/// into the <c>Sdcb.SimdCodecs</c> facade.
/// <para>
/// Fidelity mapping (<see cref="TryDecode"/>, first page of multi-page files):
/// 8/16-bit unsigned-integer and 32-bit IEEE-float samples map directly —
/// <see cref="TiffPage.Pixels"/> is already interleaved, row-major, host byte
/// order — and 16-bit IEEE-float (half) widens losslessly to
/// <see cref="SampleFormat.Float32"/>. Layouts <see cref="IDecodedImage"/> cannot
/// represent (palette / CMYK / YCbCr / MinIsWhite photometrics, signed or
/// 32-bit-uint samples, sub-byte depths) return false rather than decode wrongly.
/// </para>
/// <para>
/// Colour meaning: float pages surface <see cref="TransferFunction.Linear"/> +
/// <see cref="FloatSemantics.SceneReferred"/> on
/// <see cref="IDecodedImage.ColorEncoding"/> (the float-TIFF scene-linear
/// convention — see <see cref="TiffPageOptions"/>); integer pages keep the sRGB
/// assumption. SMin/SMax stay available via <see cref="TiffReader"/> for
/// consumers that stretch.
/// </para>
/// </summary>
public sealed class TiffImageDecoder : IImageDecoder
{
    /// <inheritdoc />
    public static int SignatureLength => 4;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 4 &&
        (header[..4].SequenceEqual("II*\0"u8) || header[..4].SequenceEqual("MM\0*"u8));

    /// <inheritdoc />
    /// <remarks>TIFF geometry lives in the IFD chain, not at a fixed header offset,
    /// so this decodes the first page rather than peeking a fixed-size prefix.</remarks>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        if (!TryDecodeCore(data, out var image)) return false;
        info = new ImageInfo(image.Width, image.Height, image.Channels, image.SampleFormat);
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        var ok = TryDecodeCore(data, out var raster);
        image = raster;
        return ok;
    }

    /// <inheritdoc />
    /// <remarks>False for float pages: scene-linear float has no canonical 8-bit
    /// projection (see <see cref="RasterImage.ExpandToRgba8"/>) — use
    /// <see cref="TryDecode"/> + <see cref="IDecodedImage.ToFloats"/> instead.</remarks>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination)
    {
        if (!TryDecodeCore(data, out var image)) return false;
        if (image.SampleFormat == SampleFormat.Float32) return false;
        if (rgbaDestination.Length < (long)image.Width * image.Height * 4) return false;
        image.ExpandToRgba8(rgbaDestination);
        return true;
    }

    private static bool TryDecodeCore(ReadOnlySpan<byte> data, [NotNullWhen(true)] out RasterImage? image)
    {
        image = null;
        TiffDocument doc;
        try
        {
            doc = TiffReader.Read(data);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        if (doc.Pages.Count == 0) return false;
        var page = doc.Pages[0];

        // Only photometrics whose samples IDecodedImage represents directly: gray
        // (MinIsBlack) and RGB. MinIsWhite would need inversion, palette an
        // expansion, CMYK/YCbCr/CieLab a colour transform - all meaning changes
        // this adapter refuses to make silently.
        if (page.Photometric is not (TiffPhotometric.MinIsBlack or TiffPhotometric.Rgb)) return false;
        var channels = page.SamplesPerPixel;
        if (channels is < 1 or > 4) return false;

        var color = page.SampleFormat == TiffSampleFormat.IeeeFloat
            ? new ColorEncoding { Transfer = TransferFunction.Linear, Float = FloatSemantics.SceneReferred }
            : ColorEncoding.AssumedSrgb;

        switch (page.BitsPerSample, page.SampleFormat)
        {
            case (8, TiffSampleFormat.Uint):
                image = new RasterImage(page.Width, page.Height, channels, SampleFormat.UInt8, page.Pixels, page.IccProfile, color);
                return true;
            case (16, TiffSampleFormat.Uint):
                image = new RasterImage(page.Width, page.Height, channels, SampleFormat.UInt16, page.Pixels, page.IccProfile, color);
                return true;
            case (32, TiffSampleFormat.IeeeFloat):
                image = new RasterImage(page.Width, page.Height, channels, SampleFormat.Float32, page.Pixels, page.IccProfile, color);
                return true;
            case (16, TiffSampleFormat.IeeeFloat):
            {
                // 16-bit IEEE float (half): widen losslessly to Float32.
                var halves = MemoryMarshal.Cast<byte, Half>(page.Pixels);
                var widened = new float[halves.Length];
                for (var i = 0; i < halves.Length; i++) widened[i] = (float)halves[i];
                image = new RasterImage(page.Width, page.Height, channels, SampleFormat.Float32,
                    MemoryMarshal.AsBytes(widened.AsSpan()).ToArray(), page.IccProfile, color);
                return true;
            }
            default:
                return false; // signed ints, 32-bit uint, sub-byte depths
        }
    }
}
