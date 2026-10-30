using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;
using Sdcb.SimdCodecs.Gif;
using Sdcb.SimdCodecs.Ico;
using Sdcb.SimdCodecs.Pnm;
using Sdcb.SimdCodecs.Qoi;
using Sdcb.SimdCodecs.Jbig2;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Jpeg2000;
using Sdcb.SimdCodecs.Png;
using Sdcb.SimdCodecs.Tiff;
using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs;

/// <summary>
/// The image-codec facade: sniff a byte stream's format by its leading magic-byte
/// signature and dispatch to the matching Sdcb.SimdCodecs codec. One reference to this
/// package pulls the whole codec family in lockstep; consumers call these entry
/// points instead of hard-wiring per-format decoders.
/// <para>
/// Registers PNG, JPEG, TIFF, JBIG2, JPEG 2000, WebP, GIF, BMP, ICO, CUR, QOI, Netpbm (PBM, PGM, PPM, PAM) and PFM. To add a
/// format, implement <see cref="IImageDecoder"/> in its codec package and add a
/// line binding its members to <see cref="_registry"/> - the order is the sniff order.
/// </para>
/// <para>
/// JBIG2 is registered for standalone <c>.jb2</c> files only. Its real use -
/// PDF's <c>/JBIG2Decode</c> filter - cannot come through here at all: an
/// embedded JBIG2 stream has no file header to sniff, keeps its segment
/// dictionaries in a separate <c>/JBIG2Globals</c> stream, and takes its
/// dimensions from the image dictionary. Those callers use
/// <c>Jbig2Decoder.Decode(embedded, globals, width, height)</c> directly.
/// </para>
/// <para>
/// JPEG 2000 is registered for the images whose samples are grey or RGB once
/// decoded: one or three components, not sYCC. Its JP2 and raw-J2K signatures
/// collide with nothing above. What the facade cannot present (alpha, CMYK,
/// sYCC) is refused rather than guessed, and <c>Jpeg2000Decoder.Decode</c>
/// still hands back every component, with the JP2 colour box reported.
/// </para>
/// <para>
/// WebP is decoded by <see cref="WebpImageDecoder"/>: lossy (VP8), lossless (VP8L)
/// and, for an animation, its first composited frame. It returns RGB8 or RGBA8
/// according to the container's alpha declaration. Every frame is available through
/// <see cref="TryOpenFrames"/>, which also reads a multi-page TIFF's pages.
/// </para>
/// <para>
/// Float-sample content (e.g. float TIFF) decodes through
/// <see cref="TryDecode"/> with <see cref="IDecodedImage.ColorEncoding"/>
/// declaring its meaning; <see cref="TryDecodeInto"/> serves the
/// integer-sample display path only - projecting HDR float to 8 bits is a
/// consumer tone/stretch decision, not a codec one. For viewing,
/// <see cref="PixelConverter.StretchToRgba8(IDecodedImage, bool)"/> stretches the colour range linearly onto 0..255.
/// </para>
/// <para>
/// A gain-map ("Ultra HDR") JPEG is recognised too: <see cref="TryDecode"/> +
/// <see cref="IDecodedImage.ToFloats"/> reconstruct the authored HDR (linear,
/// display-referred), while <see cref="TryDecodeInto"/> and
/// <see cref="IDecodedImage.ToRgba8"/> return the SDR base rendition - so the
/// float path is HDR and the 8-bit path is the graceful SDR fallback. Plain JPEGs
/// are unaffected.
/// </para>
/// </summary>
public static class SimdCodecs
{
    // ReadOnlySpan<byte> is a ref struct, so it cannot be a Func<> type argument -
    // hence the bespoke delegate types matching IImageDecoder's static members.
    private delegate bool CanDecodeFn(ReadOnlySpan<byte> header);
    private delegate bool TryReadInfoFn(ReadOnlySpan<byte> data, out ImageInfo info);
    private delegate bool TryDecodeFn(ReadOnlySpan<byte> data, out IDecodedImage? image);
    private delegate bool TryDecodeIntoFn(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format);
    private delegate bool TryOpenFramesFn(ReadOnlyMemory<byte> data, out IImageFrameReader? reader);

    // TryOpenFrames is null for a still-only codec: the facade presents its image as one frame.
    private readonly record struct Entry(
        ImageFormat Format,
        CanDecodeFn CanDecode,
        TryReadInfoFn TryReadInfo,
        TryDecodeFn TryDecode,
        TryDecodeIntoFn TryDecodeInto,
        TryOpenFramesFn? TryOpenFrames = null);

    // Explicit registry: IImageDecoder's static-abstract members are not enumerable
    // and reflection-scanning for implementors is not AOT-safe, so each codec is
    // registered by hand. Each delegate is bound to the concrete type's method group,
    // never inside a generic method (Register<T>() => new(T.CanDecode, ...)): Mono AOT
    // (browser-wasm, iOS, Android) compiles a delegate over a static abstract member
    // of a type parameter into a call that traps with "function signature mismatch".
    // tests/Sdcb.SimdCodecs.WasmSmoke runs every entry point under Mono AOT.
    private static readonly Entry[] _registry =
    [
        new(ImageFormat.Png, PngImageDecoder.CanDecode, PngImageDecoder.TryReadInfo, PngImageDecoder.TryDecode, PngImageDecoder.TryDecodeInto),
        // GainMap must precede the plain JPEG decoder: both answer the FF D8 FF
        // signature, but GainMapImageDecoder.CanDecode only claims a JPEG that
        // actually carries a locatable gain map, so ordinary JPEGs fall through.
        // It makes ToFloats() reconstruct the authored HDR while the 8-bit path
        // keeps returning the SDR base.
        new(ImageFormat.Jpeg, GainMapImageDecoder.CanDecode, GainMapImageDecoder.TryReadInfo, GainMapImageDecoder.TryDecode, GainMapImageDecoder.TryDecodeInto),
        new(ImageFormat.Jpeg, JpegImageDecoder.CanDecode, JpegImageDecoder.TryReadInfo, JpegImageDecoder.TryDecode, JpegImageDecoder.TryDecodeInto),
        new(ImageFormat.Tiff, TiffImageDecoder.CanDecode, TiffImageDecoder.TryReadInfo, TiffImageDecoder.TryDecode, TiffImageDecoder.TryDecodeInto, TiffImageDecoder.TryOpenFrames),
        new(ImageFormat.Jbig2, Jbig2ImageDecoder.CanDecode, Jbig2ImageDecoder.TryReadInfo, Jbig2ImageDecoder.TryDecode, Jbig2ImageDecoder.TryDecodeInto),
        new(ImageFormat.Jpeg2000, Jpeg2000ImageDecoder.CanDecode, Jpeg2000ImageDecoder.TryReadInfo, Jpeg2000ImageDecoder.TryDecode, Jpeg2000ImageDecoder.TryDecodeInto),
        new(ImageFormat.Webp, WebpImageDecoder.CanDecode, WebpImageDecoder.TryReadInfo, WebpImageDecoder.TryDecode, WebpImageDecoder.TryDecodeInto, WebpImageDecoder.TryOpenFrames),
        new(ImageFormat.Gif, GifImageDecoder.CanDecode, GifImageDecoder.TryReadInfo, GifImageDecoder.TryDecode, GifImageDecoder.TryDecodeInto, GifImageDecoder.TryOpenFrames),
        new(ImageFormat.Bmp, BmpImageDecoder.CanDecode, BmpImageDecoder.TryReadInfo, BmpImageDecoder.TryDecode, BmpImageDecoder.TryDecodeInto),
        new(ImageFormat.Ico, IcoImageDecoder.CanDecode, IcoImageDecoder.TryReadInfo, IcoImageDecoder.TryDecode, IcoImageDecoder.TryDecodeInto, IcoImageDecoder.TryOpenFrames),
        new(ImageFormat.Cur, CurImageDecoder.CanDecode, CurImageDecoder.TryReadInfo, CurImageDecoder.TryDecode, CurImageDecoder.TryDecodeInto, CurImageDecoder.TryOpenFrames),
        new(ImageFormat.Qoi, QoiImageDecoder.CanDecode, QoiImageDecoder.TryReadInfo, QoiImageDecoder.TryDecode, QoiImageDecoder.TryDecodeInto),
        // Netpbm signatures are two ASCII bytes and a space: sniffed last, after every binary magic.
        new(ImageFormat.Pnm, PnmImageDecoder.CanDecode, PnmImageDecoder.TryReadInfo, PnmImageDecoder.TryDecode, PnmImageDecoder.TryDecodeInto),
        new(ImageFormat.Pfm, PfmImageDecoder.CanDecode, PfmImageDecoder.TryReadInfo, PfmImageDecoder.TryDecode, PfmImageDecoder.TryDecodeInto),
    ];

    /// <summary>
    /// Opens a reader over every frame of <paramref name="data"/> — an animated WebP's frames, a multi-page
    /// TIFF's pages — to decode them one at a time by index (see <see cref="IImageFrameReader"/>). Any
    /// other image opens as a reader of one frame, its still image, so a caller handles every format the
    /// same way. The reader keeps <paramref name="data"/>; dispose it when done. Returns false when no
    /// registered codec recognises the stream or the file is malformed.
    /// </summary>
    public static bool TryOpenFrames(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out IImageFrameReader? reader)
    {
        foreach (var entry in _registry)
        {
            if (!entry.CanDecode(data.Span))
                continue;
            if (entry.TryOpenFrames is { } open)
                return open(data, out reader);
            if (!entry.TryReadInfo(data.Span, out var info))
                break;
            reader = new StillFrameReader(data, info with { FrameCount = 1 }, entry);
            return true;
        }

        reader = null;
        return false;
    }

    /// <summary>A still image as a reader of one frame, decoded through its codec's entry.</summary>
    private sealed class StillFrameReader(ReadOnlyMemory<byte> data, ImageInfo info, Entry entry) : IImageFrameReader
    {
        private bool _disposed;

        public int FrameCount => 1;

        public int? LoopCount => null;

        public bool TryGetFrameInfo(int index, out ImageFrameInfo frame)
        {
            Check(index);
            frame = new ImageFrameInfo(info, null);
            return true;
        }

        public bool TryDecodeFrame(int index, [NotNullWhen(true)] out IDecodedImage? image)
        {
            Check(index);
            return entry.TryDecode(data.Span, out image);
        }

        public bool TryDecodeFrameInto(int index, Span<byte> destination, int stride, ImagePixelFormat format)
        {
            Check(index);
            return entry.TryDecodeInto(data.Span, destination, stride, format);
        }

        public void Dispose() => _disposed = true;

        private void Check(int index)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, 1);
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    /// <summary>
    /// Decodes <paramref name="data"/> into a codec-neutral <see cref="IDecodedImage"/>
    /// (fidelity tier - preserves source bit depth and channel count). Returns false
    /// when no registered codec recognises the stream or the payload is undecodable.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        foreach (var entry in _registry)
        {
            if (entry.CanDecode(data))
                return entry.TryDecode(data, out image);
        }

        image = null;
        return false;
    }

    /// <summary>
    /// Reads dimensions + layout from the header without a full decode, so a caller
    /// can size a destination for <see cref="TryDecodeInto"/>. Returns false when
    /// no registered codec recognises the stream or the header is malformed.
    /// </summary>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        foreach (var entry in _registry)
        {
            if (entry.CanDecode(data))
                return entry.TryReadInfo(data, out info);
        }

        info = default;
        return false;
    }

    /// <summary>
    /// Decodes directly into a caller-provided 8-bit destination in <paramref name="format"/>, rows
    /// <paramref name="stride"/> bytes apart (0: tightly packed) — the display hot path, straight into a
    /// UI surface (<see cref="ImagePixelFormat.Bgra32"/>), an OpenCV <c>Mat</c>
    /// (<see cref="ImagePixelFormat.Bgr24"/>) or an OCR input (<see cref="ImagePixelFormat.Gray8"/>).
    /// Size the destination via <see cref="TryReadInfo"/> and <see cref="PixelConverter.TryGetLayout"/>.
    /// See <see cref="IImageDecoder.TryDecodeInto"/> for what the pixels are. Returns false when no codec
    /// recognises the stream, the destination does not fit, or the payload is undecodable.
    /// </summary>
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format)
    {
        foreach (var entry in _registry)
        {
            if (entry.CanDecode(data))
                return entry.TryDecodeInto(data, destination, stride, format);
        }

        return false;
    }

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA (row-major, 4 bytes/pixel, R,G,B,A byte
    /// order). <paramref name="rgbaDestination"/> must be at least <c>Width * Height * 4</c> bytes (size it
    /// via <see cref="TryReadInfo"/>).
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, ImagePixelFormat.Rgba32);

    /// <summary>
    /// The format of <paramref name="data"/> by its leading signature: one the facade decodes (the
    /// codec that would be dispatched to), or one it recognises but does not decode — HEIF, AVIF,
    /// JPEG XL, JPEG XR, OpenEXR, PSD — so a caller can name what it cannot open.
    /// <see cref="ImageFormat.Unknown"/> otherwise. Like <see cref="CanDecode"/>, it reads the
    /// signature only; pass at least the first 64 bytes (an ISO-BMFF <c>ftyp</c> box lists HEIF and
    /// AVIF brands past its start).
    /// </summary>
    public static ImageFormat DetectFormat(ReadOnlySpan<byte> data)
    {
        foreach (var entry in _registry)
        {
            if (entry.CanDecode(data))
                return entry.Format;
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0x0A])
            || data.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x00, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A]))
            return ImageFormat.JpegXl;
        if (data.StartsWith((ReadOnlySpan<byte>)[(byte)'I', (byte)'I', 0xBC, 0x01])) return ImageFormat.JpegXr;
        if (data.StartsWith((ReadOnlySpan<byte>)[0x76, 0x2F, 0x31, 0x01])) return ImageFormat.OpenExr;
        if (data.StartsWith("8BPS"u8)) return ImageFormat.Psd;
        return DetectIsoBmff(data);
    }

    /// <summary>
    /// True when the facade decodes <paramref name="format"/> — a format some registered codec reports,
    /// as opposed to one <see cref="DetectFormat"/> only recognises. False for
    /// <see cref="ImageFormat.Unknown"/>.
    /// </summary>
    public static bool IsDecodable(ImageFormat format)
    {
        foreach (var entry in _registry)
        {
            if (entry.Format == format)
                return true;
        }

        return false;
    }

    // HEIF and AVIF are ISO-BMFF files that open with an 'ftyp' box: size, 'ftyp', a major brand,
    // a minor version, then compatible brands. An AVIF brand anywhere wins over the HEIF ones,
    // since AVIF files commonly also list the generic 'mif1'.
    private static ImageFormat DetectIsoBmff(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || !data[4..8].SequenceEqual("ftyp"u8))
            return ImageFormat.Unknown;
        var size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data), (uint)data.Length);
        var heif = false;
        for (var at = 8; at + 4 <= size; at += at == 8 ? 8 : 4) // skip the minor version after the major brand
        {
            var brand = data.Slice(at, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
                return ImageFormat.Avif;
            heif |= brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("heim"u8)
                || brand.SequenceEqual("heis"u8) || brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8)
                || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8);
        }

        return heif ? ImageFormat.Heif : ImageFormat.Unknown;
    }

    /// <summary>
    /// True if any registered codec recognises the stream's leading bytes. Does not
    /// validate the full payload - a positive result only means the signature matched.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        foreach (var entry in _registry)
        {
            if (entry.CanDecode(data))
                return true;
        }

        return false;
    }
}
