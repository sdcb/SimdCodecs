using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Avif;

/// <summary>
/// <see cref="IImageDecoder"/> and <see cref="IPlanarImageDecoder"/> adapter for AVIF,
/// bridging <see cref="AvifDecoder"/> into the <c>Sdcb.SimdCodecs</c> facade.
/// <para>
/// Decodes AV1 still images across Profiles 0, 1 and 2 (8, 10 and 12 bits; 4:2:0, 4:2:2, 4:4:4 and 4:0:0),
/// tiled <c>grid</c>, <c>iden</c>, <c>iovl</c> overlay, straight/premultiplied <c>auxl</c> alpha,
/// Superres, IntraBC, Film Grain, <c>clap</c>/<c>irot</c>/<c>imir</c> transforms, ICC/Exif/XMP/<c>thmb</c>
/// metadata, HDR PQ/HLG signalling, and ISO/IEC 21496-1 <c>tmap</c> gain maps.
/// </para>
/// </summary>
public sealed class AvifImageDecoder : IPlanarImageDecoder, IMultiFrameImageDecoder
{
    /// <summary>
    /// This codec's entry for <c>ImageCodecs</c> — its members bound by concrete method group for Mono AOT safety.
    /// </summary>
    public static ImageDecoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Avif,
        CanDecode = CanDecode,
        TryReadInfo = TryReadInfo,
        TryGetClosestSize = TryGetClosestSize,
        TryDecode = TryDecode,
        Decode = Decode,
        TryDecodeInto = TryDecodeInto,
        TryReadMetadata = TryReadMetadata,
        TryDecodeThumbnail = TryDecodeThumbnail,
        TryReadPlanarInfo = TryReadPlanarInfo,
        TryDecodePlanes = TryDecodePlanes,
        TryOpenFrames = TryOpenFrames,
    };

    /// <summary>The <c>ftyp</c> box with its compatible brands.</summary>
    public static int SignatureLength => 64;

    /// <summary>
    /// True for an ISO-BMFF file whose <c>ftyp</c> box lists an AVIF brand (<c>avif</c> or <c>avis</c>).
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16 || !header[4..8].SequenceEqual("ftyp"u8))
            return false;
        int size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(header), (uint)header.Length);
        for (int at = 8; at + 4 <= size; at += at == 8 ? 8 : 4)
        {
            var brand = header.Slice(at, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
                return true;
        }
        return false;
    }

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        int frameCount = 1;
        AvifSequence? seq = null;
        if (CanDecode(data) && AvifSequence.TryParse(data, out seq) && seq.FrameCount > 0)
            frameCount = seq.FrameCount;

        if (!TryParse(data, out var container))
        {
            if (seq is null) return false;
            try
            {
                var (sw, sh) = seq.DisplaySize();
                int sDepth = seq.BitDepth;
                int sCh = seq.HasAlpha ? 4 : 3;
                info = new ImageInfo(sw, sh, sCh, sDepth > 8 ? SampleFormat.UInt16 : SampleFormat.UInt8)
                {
                    Format = ImageFormat.Avif,
                    BitDepth = sDepth,
                    FrameCount = frameCount,
                    ColorEncoding = ColorEncodingOfSequence(seq),
                };
                return true;
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
            {
                return false;
            }
        }
        try
        {
            AvifDecoder.EnsureCanvasDimensions(data, container);
            var (width, height) = container.DisplaySize();
            var colorEncoding = ColorEncodingOf(data, container);
            bool hasThumb = AvifMetadata.FindThumbnail(container) is not null;

            if (HasSdrGainMap(container))
            {
                info = new ImageInfo(width, height, 3, SampleFormat.Float32)
                {
                    Format = ImageFormat.Avif,
                    FrameCount = frameCount,
                    ColorEncoding = new ColorEncoding
                    {
                        Primaries = colorEncoding.Primaries,
                        Transfer = TransferFunction.Linear,
                        Float = FloatSemantics.DisplayReferred,
                    },
                    HasThumbnail = hasThumb,
                };
                return true;
            }

            int depth = container.BitDepth;
            int channels = container.HasAlpha ? 4 : 3;
            info = new ImageInfo(width, height, channels, depth > 8 ? SampleFormat.UInt16 : SampleFormat.UInt8)
            {
                Format = ImageFormat.Avif,
                BitDepth = depth,
                FrameCount = frameCount,
                ColorEncoding = colorEncoding,
                HasThumbnail = hasThumb,
            };
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryOpenFrames(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out IImageFrameReader? reader)
    {
        if (AvifFrameReader.TryOpen(data, out var avifReader))
        {
            reader = avifReader;
            return true;
        }
        reader = null;
        return false;
    }

    private static ColorEncoding ColorEncodingOfSequence(AvifSequence seq)
    {
        if (seq.NclxColour is { } nclx)
        {
            return new ColorEncoding
            {
                Primaries = (ColorPrimaries)(nclx.ColourPrimaries is >= 0 and <= 255 ? nclx.ColourPrimaries : 2),
                Transfer = (TransferFunction)(nclx.TransferCharacteristics is >= 0 and <= 255 ? nclx.TransferCharacteristics : 2),
                FullRange = true,
            };
        }
        if (seq.IccProfile is not null)
            return IccPrimaries.Encoding(seq.IccProfile) ?? ColorEncoding.AssumedSrgb;
        return ColorEncoding.AssumedSrgb;
    }

    /// <inheritdoc />
    public static bool TryGetClosestSize(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight,
        out int width, out int height, out int scaleDenominator) =>
        ImageDecoderFallback.TryGetStoredSize(TryReadInfo, data, out width, out height, out scaleDenominator);

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default) =>
        ImageDecoderFallback.TryDecode(Decode, data, out image, options, cancellationToken);

    /// <inheritdoc />
    public static IDecodedImage Decode(ReadOnlySpan<byte> data, DecodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var container = Parse(data);
        (int Width, int Height) size;
        try
        {
            AvifDecoder.EnsureCanvasDimensions(data, container);
            size = container.DisplaySize();
        }
        catch (Exception e) when (e is ArgumentException or OverflowException)
        {
            throw Undecodable(e);
        }

        options?.Validate(size.Width, size.Height);
        cancellationToken.ThrowIfCancellationRequested();

        var colorEncoding = ColorEncodingOf(data, container);
        var metadata = AvifMetadata.Read(data, container);

        if (HasSdrGainMap(container))
        {
            try
            {
                var baseRaster = DecodeContainerToRaster(data, container, colorEncoding, metadata, cancellationToken);
                var gmContainer = AvifContainer.Parse(data, container.GainMapItemId);
                AvifDecoder.EnsureCanvasDimensions(data, gmContainer);
                var gmRaster = DecodeContainerToRaster(data, gmContainer, ColorEncoding.AssumedSrgb, ImageMetadata.Empty, cancellationToken);
                IDecodedImage hdr = new AvifGainMapDecodedImage(
                    baseRaster, gmRaster, container.GainMapMetadata!, colorEncoding.Primaries, metadata);
                return DecodeOptions.Apply(hdr, options);
            }
            catch (Exception e) when (e is ArgumentException or OverflowException)
            {
                throw Undecodable(e);
            }
        }

        try
        {
            var raster = DecodeContainerToRaster(data, container, colorEncoding, metadata, cancellationToken);
            return DecodeOptions.Apply(raster, options);
        }
        catch (Exception e) when (e is ArgumentException or OverflowException)
        {
            throw Undecodable(e);
        }
    }

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!TryParse(data, out var container)) return false;
        (int Width, int Height) size;
        ColorEncoding colorEncoding;
        try
        {
            AvifDecoder.EnsureCanvasDimensions(data, container);
            size = container.DisplaySize();
            colorEncoding = ColorEncodingOf(data, container);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }

        options?.Validate(size.Width, size.Height);
        if (colorEncoding.Transfer is TransferFunction.Pq or TransferFunction.Hlg)
            return false;

        var (outW, outH) = options?.GetOutputSize(size.Width, size.Height) ?? size;
        if (!PixelConverter.TryGetLayout(outW, outH, format, stride, destination.Length, out int rowStride))
            return false;

        try
        {
            using var full = DecodeContainerToRaster(data, container, colorEncoding, ImageMetadata.Empty, cancellationToken);
            using var final = DecodeOptions.Apply(full, options);
            return final.TryCopyTo(destination, rowStride, format);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata)
    {
        metadata = null;
        if (!TryParse(data, out var container)) return false;
        metadata = AvifMetadata.Read(data, container);
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail)
    {
        thumbnail = null;
        if (!TryParse(data, out var container) || AvifMetadata.FindThumbnail(container) is not { } thumbItem)
            return false;
        try
        {
            var thumb = AvifContainer.Parse(data, thumbItem.Id);
            AvifDecoder.EnsureCanvasDimensions(data, thumb);
            var enc = ColorEncodingOf(data, thumb);
            var meta = thumb.IccProfile is { Length: > 0 } icc ? new ImageMetadata { Icc = icc } : ImageMetadata.Empty;
            thumbnail = DecodeContainerToRaster(data, thumb, enc, meta, CancellationToken.None);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryReadPlanarInfo(ReadOnlySpan<byte> data, out PlanarInfo info)
    {
        info = default;
        if (!TryParse(data, out var container) || container.Transforms.Count != 0 || container.Overlay is not null)
            return false;
        if (container.Primary.TileConfigs[0].Monochrome)
            return false;
        try
        {
            AvifDecoder.EnsureCanvasDimensions(data, container);
            info = PlanarInfoOf(data, container);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryDecodePlanes(ReadOnlySpan<byte> data, Span<ushort> y, Span<ushort> cb, Span<ushort> cr,
        CancellationToken cancellationToken = default)
    {
        if (!TryParse(data, out var container) || container.Transforms.Count != 0 || container.Overlay is not null)
            return false;
        if (container.Primary.TileConfigs[0].Monochrome)
            return false;
        try
        {
            AvifDecoder.EnsureCanvasDimensions(data, container);
            var info = PlanarInfoOf(data, container);
            if (y.Length < info.LumaLength || cb.Length < info.ChromaLength || cr.Length < info.ChromaLength)
                return false;
            cancellationToken.ThrowIfCancellationRequested();
            var planes = AvifDecoder.DecodePlanes(data, container);
            planes.Y.AsSpan(0, info.LumaLength).CopyTo(y);
            planes.Cb!.AsSpan(0, info.ChromaLength).CopyTo(cb);
            planes.Cr!.AsSpan(0, info.ChromaLength).CopyTo(cr);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static RasterImage DecodeContainerToRaster(
        ReadOnlySpan<byte> data,
        AvifContainer container,
        ColorEncoding colorEncoding,
        ImageMetadata metadata,
        CancellationToken cancellationToken)
    {
        var (w, h) = container.DisplaySize();
        int depth = container.BitDepth;
        bool hasAlpha = container.HasAlpha;
        bool wide = depth > 8;
        int channels = hasAlpha ? 4 : 3;

        if (!wide)
        {
            var rgba = new byte[checked((long)w * h * 4)];
            AvifDecoder.DecodeContainerToRgba8(data, container, rgba, w, h, cancellationToken);
            if (hasAlpha)
                return RasterImage.Wrap(w, h, 4, SampleFormat.UInt8, rgba, colorEncoding, metadata: metadata);

            var rgb = new byte[checked((long)w * h * 3)];
            PixelConverter.Convert(rgba, w * 4, PixelFormat.Rgba32, rgb, w * 3, PixelFormat.Rgb24, w, h);
            return RasterImage.Wrap(w, h, 3, SampleFormat.UInt8, rgb, colorEncoding, metadata: metadata);
        }

        var rgba16Bytes = new byte[checked((long)w * h * 4 * sizeof(ushort))];
        var rgba16 = MemoryMarshal.Cast<byte, ushort>(rgba16Bytes.AsSpan());
        AvifDecoder.DecodeContainerToRgba16(data, container, rgba16, w, h, cancellationToken);
        if (hasAlpha)
            return RasterImage.Wrap(w, h, 4, SampleFormat.UInt16, rgba16Bytes, colorEncoding, bitDepth: depth < 16 ? depth : 0, metadata: metadata);

        var rgb16Bytes = new byte[checked((long)w * h * 3 * sizeof(ushort))];
        var rgb16 = MemoryMarshal.Cast<byte, ushort>(rgb16Bytes.AsSpan());
        for (int i = 0; i < w * h; i++)
        {
            rgb16[i * 3] = rgba16[i * 4];
            rgb16[i * 3 + 1] = rgba16[i * 4 + 1];
            rgb16[i * 3 + 2] = rgba16[i * 4 + 2];
        }
        return RasterImage.Wrap(w, h, 3, SampleFormat.UInt16, rgb16Bytes, colorEncoding, bitDepth: depth < 16 ? depth : 0, metadata: metadata);
    }

    private static bool HasSdrGainMap(AvifContainer container) =>
        container.GainMapMetadata is { } gm && gm.BaseHdrHeadroom <= gm.AlternateHdrHeadroom;

    private static PlanarInfo PlanarInfoOf(ReadOnlySpan<byte> data, AvifContainer container)
    {
        var cfg = container.Primary.TileConfigs[0];
        var seq = AvifDecoder.TryReadSequenceHeader(data, container);
        int matrix = container.NclxColour is { } nclx ? nclx.MatrixCoefficients : seq?.MatrixCoefficients ?? 2;
        bool fullRange = container.NclxColour is { } nclx2 ? nclx2.FullRange : seq?.ColorRangeFull ?? false;
        int csp = seq?.ChromaSamplePosition ?? cfg.ChromaSamplePosition;
        var chromaLoc = csp switch
        {
            1 => ChromaLocation.Left,
            2 => ChromaLocation.TopLeft,
            _ => ChromaLocation.Left,
        };
        return new PlanarInfo(
            container.Primary.CanvasWidth,
            container.Primary.CanvasHeight,
            1 << cfg.SubsamplingX,
            1 << cfg.SubsamplingY,
            cfg.BitDepth,
            (MatrixCoefficients)(matrix is >= 0 and <= 255 ? matrix : 2),
            fullRange,
            chromaLoc);
    }

    internal static ColorEncoding ColorEncodingOf(ReadOnlySpan<byte> data, AvifContainer container)
    {
        int primaries, transfer;
        if (container.NclxColour is { } nclx)
        {
            (primaries, transfer) = (nclx.ColourPrimaries, nclx.TransferCharacteristics);
        }
        else if (container.IccProfile is not null)
        {
            return IccPrimaries.Encoding(container.IccProfile) ?? ColorEncoding.AssumedSrgb;
        }
        else
        {
            var seq = AvifDecoder.TryReadSequenceHeader(data, container);
            if (seq is null || (seq.ColorPrimaries == 2 && seq.TransferCharacteristics == 2))
                return ColorEncoding.AssumedSrgb;
            (primaries, transfer) = (seq.ColorPrimaries, seq.TransferCharacteristics);
        }

        return new ColorEncoding
        {
            Primaries = (ColorPrimaries)(primaries is >= 0 and <= 255 ? primaries : 2),
            Transfer = (TransferFunction)(transfer is >= 0 and <= 255 ? transfer : 2),
            FullRange = true,
        };
    }

    private static bool TryParse(ReadOnlySpan<byte> data, [NotNullWhen(true)] out AvifContainer? container)
    {
        try
        {
            container = Parse(data);
            return true;
        }
        catch (InvalidDataException)
        {
            container = null;
            return false;
        }
    }

    private static AvifContainer Parse(ReadOnlySpan<byte> data)
    {
        if (!CanDecode(data))
            throw new InvalidDataException("The data does not start with an 'ftyp' box listing an AVIF brand.");
        try
        {
            return AvifContainer.Parse(data);
        }
        catch (Exception e) when (e is ArgumentException or OverflowException)
        {
            throw Undecodable(e);
        }
    }

    private static InvalidDataException Undecodable(Exception e) =>
        new($"The AVIF data is invalid: {e.Message}", e);
}
