using System.Buffers.Binary;

namespace Sdcb.SimdCodecs.Webp;

internal readonly ref struct WebpContainer
{
    private WebpContainer(
        WebpInfo info,
        ReadOnlySpan<byte> vp8Bitstream,
        ReadOnlySpan<byte> iccProfile,
        bool hasIccProfile,
        ReadOnlySpan<byte> exif,
        ReadOnlySpan<byte> xmp,
        bool hasExif,
        bool hasXmp,
        ReadOnlySpan<byte> alphaData,
        bool hasAlphaChunk,
        WebpFrameData[]? animationFrames,
        WebpFrameData stillFrame,
        uint animationBackgroundColor,
        int loopCount)
    {
        Info = info;
        Vp8Bitstream = vp8Bitstream;
        IccProfile = iccProfile;
        HasIccProfile = hasIccProfile;
        Exif = exif;
        Xmp = xmp;
        HasExif = hasExif;
        HasXmp = hasXmp;
        AlphaData = alphaData;
        HasAlphaChunk = hasAlphaChunk;
        _animationFrames = animationFrames;
        _stillFrame = stillFrame;
        AnimationBackgroundColor = animationBackgroundColor;
        LoopCount = loopCount;
    }

    public WebpInfo Info { get; }
    public ReadOnlySpan<byte> Vp8Bitstream { get; }
    public ReadOnlySpan<byte> IccProfile { get; }
    public bool HasIccProfile { get; }
    public ReadOnlySpan<byte> Exif { get; }
    public ReadOnlySpan<byte> Xmp { get; }
    public bool HasExif { get; }
    public bool HasXmp { get; }
    public ReadOnlySpan<byte> AlphaData { get; }
    public bool HasAlphaChunk { get; }
    private readonly WebpFrameData[]? _animationFrames;
    private readonly WebpFrameData _stillFrame;

    // A still image's single frame is only put in an array when asked for (the animation API),
    // so that reading a still image's container allocates nothing.
    public WebpFrameData[] Frames => _animationFrames ?? [_stillFrame];
    public uint AnimationBackgroundColor { get; }
    public int LoopCount { get; }

    public static WebpContainer Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WEBP"u8))
        {
            throw new InvalidDataException("The data does not have a WebP RIFF header.");
        }

        uint riffLength = BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]);
        long riffEndLong = 8L + riffLength;
        if (riffLength < 4 || riffEndLong > data.Length)
        {
            throw new InvalidDataException("The WebP RIFF size exceeds the supplied data.");
        }

        // dwebp accepts bytes after the RIFF-declared end, but rejects a RIFF size that
        // truncates a file's chunks or extends beyond the supplied data.
        int riffEnd = (int)riffEndLong;
        bool extended = false;
        bool animationFlag = false;
        bool alphaFlag = false;
        bool hasAlphaChunk = false;
        bool hasAnimChunk = false;
        int canvasWidth = 0;
        int canvasHeight = 0;
        int imageWidth = 0;
        int imageHeight = 0;
        int primaryOffset = -1;
        int primaryLength = 0;
        bool primaryLossless = false;
        int iccOffset = -1;
        int iccLength = 0;
        int exifOffset = -1;
        int exifLength = 0;
        int xmpOffset = -1;
        int xmpLength = 0;
        int alphaOffset = -1;
        int alphaLength = 0;
        bool primaryHasAlpha = false;
        uint animationBackgroundColor = 0;
        int loopCount = 0;
        List<WebpFrameData>? frames = null;
        int offset = 12;

        while (offset < riffEnd)
        {
            if (riffEnd - offset < 8)
            {
                throw new InvalidDataException("The WebP RIFF chunk header is truncated.");
            }

            ReadOnlySpan<byte> type = data.Slice(offset, 4);
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            long payloadEndLong = (long)offset + 8 + chunkLength;
            long nextChunkLong = payloadEndLong + (chunkLength & 1);
            if (payloadEndLong > riffEnd || nextChunkLong > riffEnd)
            {
                throw new InvalidDataException("A WebP RIFF chunk exceeds the declared container size.");
            }

            int payloadOffset = offset + 8;
            int payloadLength = checked((int)chunkLength);
            ReadOnlySpan<byte> payload = data.Slice(payloadOffset, payloadLength);

            if (type.SequenceEqual("VP8X"u8))
            {
                if (extended || payloadLength < 10)
                {
                    throw new InvalidDataException("The WebP VP8X chunk is invalid.");
                }

                extended = true;
                byte flags = payload[0];
                animationFlag = (flags & 0x02) != 0;
                alphaFlag = (flags & 0x10) != 0;
                canvasWidth = 1 + ReadUInt24(payload[4..7]);
                canvasHeight = 1 + ReadUInt24(payload[7..10]);
            }
            else if (type.SequenceEqual("VP8 "u8))
            {
                if (primaryOffset >= 0)
                {
                    throw new InvalidDataException("The WebP contains more than one image bitstream.");
                }

                (imageWidth, imageHeight) = ReadVp8Dimensions(payload);
                if (!extended)
                {
                    canvasWidth = imageWidth;
                    canvasHeight = imageHeight;
                }
                primaryOffset = payloadOffset;
                primaryLength = payloadLength;
            }
            else if (type.SequenceEqual("VP8L"u8))
            {
                if (primaryOffset >= 0)
                {
                    throw new InvalidDataException("The WebP contains more than one image bitstream.");
                }

                (imageWidth, imageHeight, bool losslessHasAlpha) = ReadVp8LDimensions(payload);
                primaryHasAlpha = losslessHasAlpha;
                if (!extended)
                {
                    canvasWidth = imageWidth;
                    canvasHeight = imageHeight;
                }
                alphaFlag |= losslessHasAlpha;
                primaryOffset = payloadOffset;
                primaryLength = payloadLength;
                primaryLossless = true;
            }
            else if (type.SequenceEqual("ALPH"u8))
            {
                if (hasAlphaChunk)
                {
                    throw new InvalidDataException("The WebP contains more than one ALPH chunk.");
                }

                hasAlphaChunk = true;
                alphaOffset = payloadOffset;
                alphaLength = payloadLength;
            }
            else if (type.SequenceEqual("ICCP"u8))
            {
                if (iccOffset >= 0)
                {
                    throw new InvalidDataException("The WebP contains more than one ICC profile.");
                }

                iccOffset = payloadOffset;
                iccLength = payloadLength;
            }
            else if (type.SequenceEqual("EXIF"u8))
            {
                if (exifOffset >= 0)
                {
                    throw new InvalidDataException("The WebP contains more than one EXIF chunk.");
                }

                exifOffset = payloadOffset;
                exifLength = payloadLength;
            }
            else if (type.SequenceEqual("XMP "u8))
            {
                if (xmpOffset >= 0)
                {
                    throw new InvalidDataException("The WebP contains more than one XMP chunk.");
                }

                xmpOffset = payloadOffset;
                xmpLength = payloadLength;
            }
            else if (type.SequenceEqual("ANIM"u8))
            {
                if (hasAnimChunk || payloadLength != 6)
                {
                    throw new InvalidDataException("The WebP ANIM chunk is invalid.");
                }

                hasAnimChunk = true;
                animationBackgroundColor = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
                loopCount = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..6]);
            }
            else if (type.SequenceEqual("ANMF"u8))
            {
                if (!extended)
                {
                    throw new InvalidDataException("The WebP ANMF chunk requires a VP8X chunk.");
                }

                (frames ??= []).Add(ReadAnimationFrame(payload, payloadOffset, canvasWidth, canvasHeight));
            }

            offset = checked((int)nextChunkLong);
        }

        if (offset != riffEnd || canvasWidth <= 0 || canvasHeight <= 0)
        {
            throw new InvalidDataException("The WebP container has no valid image bitstream.");
        }

        if ((long)canvasWidth * canvasHeight > WebpLimits.MaximumPixels)
        {
            throw new InvalidDataException("The WebP dimensions exceed the supported pixel limit.");
        }

        bool animated = animationFlag;
        if (hasAnimChunk && !animated)
        {
            throw new InvalidDataException("The WebP ANIM chunk requires the VP8X animation flag.");
        }

        if (animated && (!hasAnimChunk || frames is null))
        {
            throw new InvalidDataException("The WebP animation chunks are incomplete.");
        }

        if (!animated && frames is not null)
        {
            throw new InvalidDataException("The WebP contains animation frames without the animation flag.");
        }

        if (!animated && primaryOffset < 0)
        {
            throw new InvalidDataException("The WebP container has no valid image bitstream.");
        }

        // A still VP8X canvas must be the bitstream's own size; libwebp refuses a mismatch as a
        // bitstream error. Accepting it would pad a 1x1 bitstream out to whatever canvas the header
        // claims -- plausible-looking wrong output, and up to 256 MiB from a ~56-byte file.
        if (!animated && extended && (imageWidth != canvasWidth || imageHeight != canvasHeight))
        {
            throw new InvalidDataException(
                $"The WebP VP8X canvas ({canvasWidth}x{canvasHeight}) does not match its image bitstream " +
                $"({imageWidth}x{imageHeight}).");
        }

        WebpFrameData stillFrame = default;
        if (!animated)
        {
            stillFrame = new WebpFrameData(
                0,
                0,
                canvasWidth,
                canvasHeight,
                0,
                false,
                false,
                primaryLossless ? WebpFormat.Lossless : WebpFormat.Lossy,
                primaryOffset,
                primaryLength,
                alphaOffset,
                alphaLength,
                primaryHasAlpha || hasAlphaChunk);
        }

        bool hasAlpha = alphaFlag || hasAlphaChunk || (animated
            ? frames!.Exists(static frame => frame.HasAlpha) ||
              frames[0].X != 0 || frames[0].Y != 0 ||
              frames[0].Width != canvasWidth || frames[0].Height != canvasHeight ||
              frames.Take(frames.Count - 1).Any(static frame => frame.DisposeToBackground)
            : stillFrame.HasAlpha);
        var info = new WebpInfo(
            canvasWidth,
            canvasHeight,
            hasAlpha,
            animated ? WebpFormat.Animated : primaryLossless ? WebpFormat.Lossless : WebpFormat.Lossy,
            animated ? frames!.Count : 1);
        return new WebpContainer(
            info,
            primaryOffset < 0 ? ReadOnlySpan<byte>.Empty : data.Slice(primaryOffset, primaryLength),
            iccOffset < 0 ? ReadOnlySpan<byte>.Empty : data.Slice(iccOffset, iccLength),
            iccOffset >= 0,
            exifOffset < 0 ? ReadOnlySpan<byte>.Empty : data.Slice(exifOffset, exifLength),
            xmpOffset < 0 ? ReadOnlySpan<byte>.Empty : data.Slice(xmpOffset, xmpLength),
            exifOffset >= 0,
            xmpOffset >= 0,
            alphaOffset < 0 ? ReadOnlySpan<byte>.Empty : data.Slice(alphaOffset, alphaLength),
            hasAlphaChunk,
            frames?.ToArray(),
            stillFrame,
            animationBackgroundColor,
            animated ? loopCount : 0);
    }

    private static WebpFrameData ReadAnimationFrame(
        ReadOnlySpan<byte> payload,
        int payloadOffset,
        int canvasWidth,
        int canvasHeight)
    {
        if (payload.Length < 16)
        {
            throw new InvalidDataException("The WebP ANMF frame header is truncated.");
        }

        int x = checked(ReadUInt24(payload[..3]) * 2);
        int y = checked(ReadUInt24(payload[3..6]) * 2);
        int width = ReadUInt24(payload[6..9]) + 1;
        int height = ReadUInt24(payload[9..12]) + 1;
        int duration = ReadUInt24(payload[12..15]);
        byte flags = payload[15];
        if ((flags & 0xfc) != 0)
        {
            throw new InvalidDataException("The WebP ANMF reserved flags are nonzero.");
        }

        if ((long)x + width > canvasWidth || (long)y + height > canvasHeight)
        {
            throw new InvalidDataException("The WebP animation frame exceeds the canvas.");
        }

        int bitstreamOffset = -1;
        int bitstreamLength = 0;
        int alphaOffset = -1;
        int alphaLength = 0;
        WebpFormat format = WebpFormat.Lossy;
        bool hasAlpha = false;
        ReadOnlySpan<byte> chunks = payload[16..];
        int offset = 0;
        while (offset < chunks.Length)
        {
            if (chunks.Length - offset < 8)
            {
                throw new InvalidDataException("The WebP ANMF subchunk header is truncated.");
            }

            ReadOnlySpan<byte> type = chunks.Slice(offset, 4);
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunks.Slice(offset + 4, 4));
            long payloadEndLong = (long)offset + 8 + chunkLength;
            long nextChunkLong = payloadEndLong + (chunkLength & 1);
            if (payloadEndLong > chunks.Length || nextChunkLong > chunks.Length)
            {
                throw new InvalidDataException("A WebP ANMF subchunk exceeds the frame data.");
            }

            int chunkPayloadOffset = checked(payloadOffset + 16 + offset + 8);
            int chunkPayloadLength = checked((int)chunkLength);
            ReadOnlySpan<byte> chunkPayload = chunks.Slice(offset + 8, chunkPayloadLength);
            if (type.SequenceEqual("ALPH"u8))
            {
                if (alphaOffset >= 0 || bitstreamOffset >= 0)
                {
                    throw new InvalidDataException("The WebP ANMF alpha subchunk is misplaced or duplicated.");
                }

                alphaOffset = chunkPayloadOffset;
                alphaLength = chunkPayloadLength;
                hasAlpha = true;
            }
            else if (type.SequenceEqual("VP8 "u8) || type.SequenceEqual("VP8L"u8))
            {
                if (bitstreamOffset >= 0)
                {
                    throw new InvalidDataException("The WebP ANMF contains multiple image bitstreams.");
                }

                int imageWidth;
                int imageHeight;
                if (type.SequenceEqual("VP8 "u8))
                {
                    (imageWidth, imageHeight) = ReadVp8Dimensions(chunkPayload);
                    format = WebpFormat.Lossy;
                }
                else
                {
                    (imageWidth, imageHeight, bool losslessHasAlpha) = ReadVp8LDimensions(chunkPayload);
                    format = WebpFormat.Lossless;
                    hasAlpha |= losslessHasAlpha;
                }

                if (imageWidth != width || imageHeight != height)
                {
                    throw new InvalidDataException("The WebP ANMF bitstream dimensions do not match its frame.");
                }

                bitstreamOffset = chunkPayloadOffset;
                bitstreamLength = chunkPayloadLength;
            }

            offset = checked((int)nextChunkLong);
        }

        if (offset != chunks.Length || bitstreamOffset < 0)
        {
            throw new InvalidDataException("The WebP ANMF has no valid image bitstream.");
        }

        if (format == WebpFormat.Lossless && alphaOffset >= 0)
        {
            throw new InvalidDataException("A VP8L animation frame cannot have a separate ALPH chunk.");
        }

        bool blend = (flags & 0x02) == 0;
        bool disposeToBackground = (flags & 0x01) != 0;
        return new WebpFrameData(
            x,
            y,
            width,
            height,
            duration,
            blend,
            disposeToBackground,
            format,
            bitstreamOffset,
            bitstreamLength,
            alphaOffset,
            alphaLength,
            hasAlpha);
    }

    private static (int Width, int Height) ReadVp8Dimensions(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10 || (payload[0] & 1) != 0 ||
            payload[3] != 0x9d || payload[4] != 0x01 || payload[5] != 0x2a)
        {
            throw new InvalidDataException("The VP8 key-frame header is invalid or truncated.");
        }

        int width = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2)) & 0x3fff;
        int height = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2)) & 0x3fff;
        if (width == 0 || height == 0)
        {
            throw new InvalidDataException("The VP8 dimensions must be nonzero.");
        }

        uint firstPartitionLength = (uint)(payload[0] | payload[1] << 8 | payload[2] << 16) >> 5;
        if (firstPartitionLength > payload.Length - 10)
        {
            throw new InvalidDataException("The VP8 first partition exceeds the frame.");
        }

        return (width, height);
    }

    private static (int Width, int Height, bool HasAlpha) ReadVp8LDimensions(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5 || payload[0] != 0x2f)
        {
            throw new InvalidDataException("The VP8L header is invalid or truncated.");
        }

        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..5]);
        int width = 1 + (int)(bits & 0x3fff);
        int height = 1 + (int)((bits >> 14) & 0x3fff);
        bool hasAlpha = ((bits >> 28) & 1) != 0;
        if ((bits >> 29) != 0)
        {
            throw new InvalidDataException("The VP8L version is unsupported.");
        }

        return (width, height, hasAlpha);
    }

    private static int ReadUInt24(ReadOnlySpan<byte> data) =>
        data[0] | (data[1] << 8) | (data[2] << 16);
}

internal readonly record struct WebpFrameData(
    int X,
    int Y,
    int Width,
    int Height,
    int DurationMs,
    bool Blend,
    bool DisposeToBackground,
    WebpFormat Format,
    int BitstreamOffset,
    int BitstreamLength,
    int AlphaOffset,
    int AlphaLength,
    bool HasAlpha);
