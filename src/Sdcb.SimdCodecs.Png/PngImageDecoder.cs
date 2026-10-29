using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for PNG, bridging <see cref="PngReader"/>
/// into the <c>Sdcb.SimdCodecs</c> facade.
/// <para>
/// Fidelity mapping (<see cref="TryDecode"/>): greyscale / RGB / greyscale+alpha /
/// RGBA preserve their native channel count and bit depth (16-bit is converted from
/// PNG's big-endian to the host byte order required by <see cref="IDecodedImage"/>);
/// indexed images are palette-expanded to 8-bit RGBA (with tRNS alpha when present), and a
/// greyscale or RGB image with a tRNS transparent colour gains an alpha channel (greyscale + alpha,
/// RGBA) that is 0 on exactly the pixels of that colour.
/// Colour signalling survives the adapter: cICP (and gAMA 1.0 → linear) maps onto
/// <see cref="IDecodedImage.ColorEncoding"/>, so PNG-3 HDR tagging isn't dropped
/// at the facade boundary.
/// </para>
/// </summary>
public sealed class PngImageDecoder : IImageDecoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <inheritdoc />
    public static int SignatureLength => 8;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && header[..8].SequenceEqual(Signature);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        // 8 signature + 4 length + 4 "IHDR" + 13 IHDR payload = 33 bytes minimum.
        if (data.Length < 33 || !CanDecode(data)) return false;
        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;

        var width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
        int bitDepth = data[24];
        int colorType = data[25];
        if (width <= 0 || height <= 0) return false;
        var chunks = ScanChunks(data, colorType);
        if (!TryMapLayout(colorType, bitDepth, chunks.TransparentColor, out var channels, out var format)) return false;

        info = new ImageInfo(width, height, channels, format)
        {
            Format = ImageFormat.Png,
            // A palette's entries are 8-bit whatever the index depth.
            BitDepth = colorType == 3 ? 8 : bitDepth,
            Orientation = chunks.Orientation,
            FrameCount = chunks.FrameCount,
        };
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        image = null;
        if (!PngReader.TryDecode(data, out var png)) return false;
        try
        {
            image = ToDecoded(png);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>False for cICP PQ/HLG-tagged files: their integer code values are
    /// encoded for an HDR EOTF, so the code-value expansion would render them
    /// crushed/wrong-gamut - the same "no canonical 8-bit projection" refusal the
    /// float codecs make. Use <see cref="TryDecode"/> + a colour-managed
    /// conversion instead. Untagged, sRGB-tagged, and gAMA-linear files keep the
    /// display path (linear is monotone and the astro-master quick-look case;
    /// HLG's nominal SDR fallback was considered, but the facade can't know the
    /// display context, so HLG refuses alongside PQ).</remarks>
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format)
    {
        if (!TryReadDisplayReferred(data, out var header, out var idat)) return false;
        if (!PixelConverter.TryGetLayout(header.Width, header.Height, format, stride, destination.Length, out var rowStride)) return false;
        try
        {
            PngReader.DecodeInto(data, header, idat, destination, rowStride, format);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be
    /// at least <c>Width * Height * 4</c> bytes.
    /// </summary>
    /// <remarks>False for a corrupt stream, a destination that is too small, and cICP PQ/HLG-tagged
    /// files (see <see cref="TryDecodeInto"/>).</remarks>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, ImagePixelFormat.Rgba32);

    /// <summary>
    /// Decode a PNG to tightly-packed 8-bit RGBA in an array the caller owns, without knowing its
    /// size first: the one-call form of <see cref="TryReadInfo"/> + allocate + <see cref="TryDecodeIntoRgba8"/>.
    /// An 8-bit RGBA file's decoded samples already are that layout, so they are handed back as they
    /// are; every other colour type is expanded once, the same mapping <see cref="TryDecodeIntoRgba8"/>
    /// uses. Either way the result is a fresh array of exactly <c>width * height * 4</c> bytes that
    /// nothing else references, so a caller can adopt it (DIR.Lib's <c>RgbaImage(width, height, pixels)</c>)
    /// rather than copy it.
    /// </summary>
    /// <remarks>False for a stream this reader rejects (a corrupt
    /// file) and, like <see cref="TryDecodeIntoRgba8"/>, for cICP PQ/HLG-tagged files.</remarks>
    public static bool TryDecodeRgba8(ReadOnlySpan<byte> data, [NotNullWhen(true)] out byte[]? rgba, out int width, out int height)
    {
        rgba = null;
        width = height = 0;
        if (!TryReadDisplayReferred(data, out var header, out var idat)) return false;
        var length = (long)header.Width * header.Height * 4;
        if (length > Array.MaxLength) return false;
        var pixels = new byte[length];
        if (!TryDecodeRgba8Into(data, header, idat, pixels)) return false;
        rgba = pixels;
        width = header.Width;
        height = header.Height;
        return true;
    }

    // The shared first step of the RGBA8 projections: every chunk but the image data, then the refusal
    // of cICP PQ/HLG, whose code values are encoded for an HDR EOTF (see TryDecodeInto) - before a
    // pixel is inflated. A float projection that decodes those starts from PngReader.TryDecode instead
    // and skips the refusal.
    private static bool TryReadDisplayReferred(ReadOnlySpan<byte> data, [NotNullWhen(true)] out PngImage? header,
        [NotNullWhen(true)] out List<(int Offset, int Length)>? idat)
    {
        try
        {
            header = PngReader.ReadHeader(data, out idat);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            header = null;
            idat = null;
            return false;
        }

        if (header.Cicp is { } cicp &&
            cicp.TransferFunction is TransferFunction.Pq or TransferFunction.Hlg)
        {
            header = null;
            idat = null;
            return false;
        }
        return true;
    }

    // The image data straight into the caller's RGBA8 buffer, a row at a time (PngReader.DecodeRgba8).
    // False for corrupt image data and for an indexed pixel past the palette's end.
    private static bool TryDecodeRgba8Into(ReadOnlySpan<byte> data, PngImage header, List<(int Offset, int Length)> idat,
        Span<byte> destination)
    {
        try
        {
            PngReader.DecodeRgba8(data, header, idat, destination);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private readonly record struct ChunkFacts(bool TransparentColor, ImageOrientation Orientation, int FrameCount);

    // What TryReadInfo needs beyond IHDR, from chunk headers alone (lengths, no CRCs). Before the first
    // IDAT: whether a greyscale or RGB file carries a tRNS transparent colour, with the length test
    // PngReader.ReadHeader applies, and an APNG's acTL frame count (the APNG spec puts acTL there). Anywhere
    // up to IEND: the first eXIf, which the 2017 extension lets a writer place before or after IDAT.
    private static ChunkFacts ScanChunks(ReadOnlySpan<byte> data, int colorType)
    {
        var transparent = false;
        var frames = 1;
        var orientation = ImageOrientation.Normal;
        var exifSeen = false;
        var beforeIdat = true;
        var pos = 8;
        while (pos + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
            var type = data.Slice(pos + 4, 4);
            // A chunk running past the end stops the walk (PngReader refuses the file).
            if (length > data.Length - pos - 12 || type.SequenceEqual("IEND"u8)) break;
            var payload = data.Slice(pos + 8, (int)length);
            if (type.SequenceEqual("IDAT"u8))
                beforeIdat = false;
            else if (beforeIdat && type.SequenceEqual("tRNS"u8))
                transparent = colorType is 0 or 2 && length == (colorType == 0 ? 2u : 6u);
            else if (beforeIdat && type.SequenceEqual("acTL"u8) && payload.Length == 8)
                frames = (int)Math.Clamp(BinaryPrimitives.ReadUInt32BigEndian(payload), 1u, int.MaxValue);
            else if (!exifSeen && type.SequenceEqual("eXIf"u8))
            {
                exifSeen = true;
                orientation = ExifOrientation.Read(payload);
            }
            pos += 12 + (int)length;
        }
        return new ChunkFacts(transparent, orientation, frames);
    }

    // The layout TryDecode produces. Indexed always expands to RGBA8; the rest keep
    // their native channel count and bit depth, plus alpha for a tRNS transparent colour.
    // Mirrors the ToDecoded mapping below.
    private static bool TryMapLayout(int colorType, int bitDepth, bool transparentColor, out int channels, out SampleFormat format)
    {
        format = bitDepth == 16 ? SampleFormat.UInt16 : SampleFormat.UInt8;
        switch (colorType)
        {
            case 0: channels = transparentColor ? 2 : 1; break;             // greyscale (+ tRNS alpha)
            case 2: channels = transparentColor ? 4 : 3; break;             // RGB (+ tRNS alpha)
            case 3: channels = 4; format = SampleFormat.UInt8; break;       // indexed -> palette-expanded RGBA8
            case 4: channels = 2; break;                                    // greyscale + alpha
            case 6: channels = 4; break;                                    // RGBA
            default: channels = 0; return false;
        }
        return true;
    }

    private static RasterImage ToDecoded(PngImage png)
    {
        var icc = png.IccProfile;
        var color = MapColorEncoding(png);

        // Indexed: palette (+ optional tRNS) expansion to RGBA8 - PngImage.ToRgba8 already does this.
        if (png.ColorType == 3)
            return new RasterImage(png.Width, png.Height, 4, SampleFormat.UInt8, png.ToRgba8(), icc, color);

        var channels = PngReader.SamplesPerPixel(png.ColorType);
        var key = png.UnpackedTransparentColor();
        if (png.BitDepth == 16)
        {
            // PngImage.Pixels holds 16-bit samples big-endian; IDecodedImage requires
            // host byte order. AsUInt16Samples returns host-native ushort[].
            var samples = png.AsUInt16Samples();
            if (key is not null)
                return new RasterImage(png.Width, png.Height, channels + 1, SampleFormat.UInt16,
                    MemoryMarshal.AsBytes(WithKeyAlpha(samples, channels, key, ushort.MaxValue).AsSpan()).ToArray(), icc, color);
            var bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
            return new RasterImage(png.Width, png.Height, channels, SampleFormat.UInt16, bytes, icc, color);
        }

        // 8-bit: Pixels are already the interleaved host-order raster.
        if (key is not null)
        {
            return new RasterImage(png.Width, png.Height, channels + 1, SampleFormat.UInt8,
                WithKeyAlpha(png.Pixels, channels, key, byte.MaxValue), icc, color);
        }
        return new RasterImage(png.Width, png.Height, channels, SampleFormat.UInt8, png.Pixels, icc, color);
    }

    // Samples of `channels` per pixel with an alpha sample appended to each pixel: 0 where the pixel is
    // exactly the transparent colour, `opaque` elsewhere. Not a hot path (the fidelity tier copies anyway).
    private static T[] WithKeyAlpha<T>(T[] samples, int channels, ushort[] key, T opaque) where T : unmanaged, IBinaryInteger<T>
    {
        var pixels = samples.Length / channels;
        var result = new T[pixels * (channels + 1)];
        for (var p = 0; p < pixels; p++)
        {
            var match = true;
            for (var c = 0; c < channels; c++)
            {
                var v = samples[p * channels + c];
                result[p * (channels + 1) + c] = v;
                match &= ushort.CreateTruncating(v) == key[c];
            }
            result[p * (channels + 1) + channels] = match ? T.Zero : opaque;
        }
        return result;
    }

    // cICP is authoritative when present (PNG-3 gives it precedence over iCCP /
    // sRGB / gAMA); CicpChunk and ColorEncoding share the H.273 vocabulary, so
    // the mapping is a straight property copy. gAMA 1.0 declares linear-light
    // samples (e.g. astro masters) - H.273 codepoint 8 (Linear) says exactly
    // that. Anything else (sRGB chunk, gAMA ~1/2.2, untagged) lands on the sRGB
    // assumption; the raw gAMA / cHRM values stay available via PngReader for
    // consumers that need them.
    private static ColorEncoding MapColorEncoding(PngImage png)
    {
        if (png.Cicp is { } cicp)
            return new ColorEncoding
            {
                Primaries = cicp.ColorPrimaries,
                Transfer = cicp.TransferFunction,
                FullRange = cicp.VideoFullRangeFlag,
            };

        if (png.Gamma == 1.0)
            return ColorEncoding.AssumedSrgb with { Transfer = TransferFunction.Linear };

        return ColorEncoding.AssumedSrgb;
    }
}
