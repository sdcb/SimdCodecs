using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>Receives finished (reconstructed and loop-filtered) rows from <see cref="Vp8Decoder"/>.</summary>
internal interface IVp8RowSink
{
    /// <summary>
    /// Luma rows [<paramref name="first"/>, <paramref name="end"/>) are final, and so is every chroma
    /// row that fancy upsampling of them reads: rows ((first - 1) >> 1) to (end >> 1), clamped to
    /// the image. Row r of a plane is at cache row r - row0 (row0 may be negative: the cache keeps
    /// rows above the current macroblock row).
    /// </summary>
    void Emit(ref byte y, int yStride, int yRow0, ref byte u, ref byte v, int uvStride, int uvRow0, int first, int end);
}

internal static class Vp8Decoder
{
    // Rows kept above the macroblock row being reconstructed: the loop filter reads 4 rows above a
    // horizontal macroblock edge, and output lags by half a macroblock row so that fancy chroma
    // upsampling has its row below (luma: 8 rows of output lag; chroma: 5 rows lag + 1).
    private const int LumaCacheExtra = 8;
    private const int ChromaCacheExtra = 6;

    // Bytes before column 0 of each row-cache row (column -1 holds the left frame edge).
    private const int CacheLeft = 16;

    // Layout of the per-macroblock "above" scratch: each run is preceded by its top-left pixel.
    private const int AboveY = 4;          // 16 + 4 above-right
    private const int AboveSubblock = 28;  // 4 + 4, assembled for right-hand 4×4 blocks
    private const int AboveU = 40;
    private const int AboveV = 52;
    private const int AboveLength = 64;

    public static Vp8Yuv Decode(ReadOnlySpan<byte> frame, int width, int height, bool applyLoopFilter = true)
        => DecodeToPlanes(frame, width, height,
            applyLoopFilter ? WebpAblation.None : WebpAblation.SkipLoopFilter, pooledOutput: false);

    internal static Vp8Yuv DecodePooled(ReadOnlySpan<byte> frame, int width, int height,
        WebpAblation ablation = WebpAblation.None)
        => DecodeToPlanes(frame, width, height, ablation, pooledOutput: true);

    private static Vp8Yuv DecodeToPlanes(ReadOnlySpan<byte> frame, int width, int height,
        WebpAblation ablation, bool pooledOutput)
    {
        int chromaWidth = (width + 1) >> 1;
        int chromaHeight = (height + 1) >> 1;
        int yPlaneLength = checked(width * height);
        int chromaPlaneLength = checked(chromaWidth * chromaHeight);
        byte[]? yPlane = null;
        byte[]? uPlane = null;
        byte[]? vPlane = null;
        try
        {
            yPlane = pooledOutput ? WebpBufferPool<byte>.Rent(yPlaneLength) : new byte[yPlaneLength];
            uPlane = pooledOutput ? WebpBufferPool<byte>.Rent(chromaPlaneLength) : new byte[chromaPlaneLength];
            vPlane = pooledOutput ? WebpBufferPool<byte>.Rent(chromaPlaneLength) : new byte[chromaPlaneLength];
            var sink = new PlaneSink(yPlane, uPlane, vPlane, width, height);
            DecodeFrame(frame, width, height, ablation, ref sink);
            var result = new Vp8Yuv(width, height, yPlane, uPlane, vPlane, pooledOutput);
            yPlane = null;
            uPlane = null;
            vPlane = null;
            return result;
        }
        finally
        {
            if (pooledOutput)
            {
                if (yPlane is not null) WebpBufferPool<byte>.Return(yPlane);
                if (uPlane is not null) WebpBufferPool<byte>.Return(uPlane);
                if (vPlane is not null) WebpBufferPool<byte>.Return(vPlane);
            }
        }
    }

    /// <summary>Copies finished rows into tight Y / U / V planes.</summary>
    private readonly struct PlaneSink(byte[] y, byte[] u, byte[] v, int width, int height) : IVp8RowSink
    {
        public void Emit(ref byte yCache, int yStride, int yRow0, ref byte uCache, ref byte vCache, int uvStride, int uvRow0,
            int first, int end)
        {
            for (int row = first; row < end; row++)
            {
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref yCache, (row - yRow0) * yStride), width)
                    .CopyTo(y.AsSpan(row * width, width));
            }

            int chromaWidth = (width + 1) >> 1;
            int chromaEnd = end == height ? (height + 1) >> 1 : end >> 1;
            for (int row = first >> 1; row < chromaEnd; row++)
            {
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref uCache, (row - uvRow0) * uvStride), chromaWidth)
                    .CopyTo(u.AsSpan(row * chromaWidth, chromaWidth));
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref vCache, (row - uvRow0) * uvStride), chromaWidth)
                    .CopyTo(v.AsSpan(row * chromaWidth, chromaWidth));
            }
        }
    }

    /// <summary>
    /// Decodes a VP8 key frame macroblock row by macroblock row: each row is reconstructed into a
    /// small row cache, the previous rows' loop filtering completes as this row is filtered, and
    /// finished rows go to <paramref name="sink"/> while they are still in cache. Working memory is
    /// a few macroblock rows, not a frame.
    /// </summary>
    internal static void DecodeFrame<TSink>(
        ReadOnlySpan<byte> frame,
        int width,
        int height,
        WebpAblation ablation,
        ref TSink sink)
        where TSink : IVp8RowSink, allows ref struct
    {
        bool applyLoopFilter = (ablation & WebpAblation.SkipLoopFilter) == 0;
        bool skipResidual = (ablation & WebpAblation.SkipResidual) != 0;
        bool skipPrediction = (ablation & WebpAblation.SkipPrediction) != 0;
        bool skipCopy = (ablation & WebpAblation.SkipMacroblockCopy) != 0;
        if (frame.Length < 10)
        {
            throw new InvalidDataException("The VP8 key-frame header is truncated.");
        }

        uint frameTag = (uint)(frame[0] | frame[1] << 8 | frame[2] << 16);
        if ((frameTag & 1) != 0)
        {
            throw new NotSupportedException("VP8 inter-frame decoding lands in a later rung.");
        }

        int partitionLength = (int)(frameTag >> 5);
        if (partitionLength > frame.Length - 10 ||
            frame[3] != 0x9d || frame[4] != 0x01 || frame[5] != 0x2a)
        {
            throw new InvalidDataException("The VP8 key-frame header or first partition is invalid.");
        }

        int codedWidth = BinaryPrimitives.ReadUInt16LittleEndian(frame[6..8]) & 0x3fff;
        int codedHeight = BinaryPrimitives.ReadUInt16LittleEndian(frame[8..10]) & 0x3fff;
        if (codedWidth != width || codedHeight != height)
        {
            throw new InvalidDataException("The WebP and VP8 dimensions do not match.");
        }

        ReadOnlySpan<byte> headerPartition = frame.Slice(10, partitionLength);
        var header = new Vp8BoolDecoder(headerPartition);
        if (header.ReadBit(128) != 0)
        {
            throw new NotSupportedException("Reserved VP8 key-frame color-space flags are unsupported.");
        }

        _ = header.ReadBit(128);

        SegmentFeatures segments = ReadSegments(ref header);
        FilterSettings filter;
        try
        {
            filter = ReadFilter(ref header);
        }
        catch
        {
            ReturnSegmentBuffers(segments);
            throw;
        }

        byte[] coefficientProbabilities = Array.Empty<byte>();
        bool coefficientProbabilitiesRented = false;
        int partitionCount;
        Quantizers quantizers;
        bool skipFlagPresent;
        byte skipProbability;
        PartitionSlices partitions;
        try
        {
            partitionCount = 1 << header.ReadLiteral(2);
            quantizers = ReadQuantizers(ref header);
            _ = header.ReadBit(128);
            coefficientProbabilities = WebpBufferPool<byte>.Rent(4 * 8 * 3 * 11);
            coefficientProbabilitiesRented = true;
            Vp8Tables.CopyDefaultCoefficientProbabilities(coefficientProbabilities);
            ReadCoefficientProbabilityUpdates(ref header, coefficientProbabilities);
            skipFlagPresent = header.ReadBit(128) != 0;
            skipProbability = skipFlagPresent ? (byte)header.ReadLiteral(8) : (byte)0;

            ReadOnlySpan<byte> tokenFrame = frame[(10 + partitionLength)..];
            partitions = ReadPartitions(tokenFrame, partitionCount);
        }
        catch
        {
            if (coefficientProbabilitiesRented)
            {
                WebpBufferPool<byte>.Return(coefficientProbabilities);
            }

            ReturnSegmentBuffers(segments);
            ReturnFilterBuffers(filter);
            throw;
        }

        var tokens = new Vp8TokenPartitions(
            partitions.P0, partitions.P1, partitions.P2, partitions.P3,
            partitions.P4, partitions.P5, partitions.P6, partitions.P7);

        int mbWidth = (width + 15) >> 4;
        int mbHeight = (height + 15) >> 4;
        int paddedWidth = mbWidth * 16;
        // Cache rows carry CacheLeft bytes before column 0: column -1 is the 129 left frame edge
        // intra prediction reads; the rest keeps rows aligned.
        int yStride = paddedWidth + 2 * CacheLeft;
        int uvStride = paddedWidth / 2 + 2 * CacheLeft;
        byte[] yCache = WebpBufferPool<byte>.Rent(checked(yStride * (LumaCacheExtra + 16)));
        byte[] uCache = WebpBufferPool<byte>.Rent(checked(uvStride * (ChromaCacheExtra + 8)));
        byte[] vCache = WebpBufferPool<byte>.Rent(checked(uvStride * (ChromaCacheExtra + 8)));
        // Unfiltered bottom rows of the previous macroblock row: intra prediction reads these, while
        // the cache above the current row holds them loop-filtered.
        byte[] topRows = WebpBufferPool<byte>.Rent(checked(paddedWidth * 2));
        byte[] aboveB = WebpBufferPool<byte>.Rent(mbWidth * 4);
        uint[] aboveNz = WebpBufferPool<uint>.Rent(mbWidth);
        byte[] rowFilter = WebpBufferPool<byte>.Rent(mbWidth * 2);
        // The row above the macroblock being predicted, with its top-left pixel at index -1:
        // luma 16 + 4 above-right (at AboveY), U and V 8 (at AboveU / AboveV).
        Span<byte> aboveRows = stackalloc byte[AboveLength];
        Span<byte> blockModes = stackalloc byte[16];
        Span<byte> leftB = stackalloc byte[4];
        uint leftNz = 0;
        Span<short> residual = stackalloc short[16];
        // 25 blocks of 16 coefficients: 16 luma, 4 U, 4 V, Y2. Kept zero between blocks: each block
        // is cleared right after it has been consumed.
        Span<short> mbCoefficients = stackalloc short[25 * 16];
        mbCoefficients.Clear();
        SegmentParameterSet segmentParameters = default;
        ComputeSegmentParameters(segments, quantizers, filter, segmentParameters);
        ref byte probabilityBase = ref MemoryMarshal.GetArrayDataReference(coefficientProbabilities);

        try
        {
            Array.Clear(aboveB);
            Array.Clear(aboveNz);
            ref byte aboveY = ref Unsafe.Add(ref MemoryMarshal.GetReference(aboveRows), AboveY);
            ref byte aboveU = ref Unsafe.Add(ref MemoryMarshal.GetReference(aboveRows), AboveU);
            ref byte aboveV = ref Unsafe.Add(ref MemoryMarshal.GetReference(aboveRows), AboveV);
            ref byte topY = ref MemoryMarshal.GetArrayDataReference(topRows);
            ref byte topU = ref Unsafe.Add(ref topY, paddedWidth);
            ref byte topV = ref Unsafe.Add(ref topU, paddedWidth / 2);
            ref byte yRow0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(yCache), LumaCacheExtra * yStride + CacheLeft);
            ref byte uRow0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(uCache), ChromaCacheExtra * uvStride + CacheLeft);
            ref byte vRow0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(vCache), ChromaCacheExtra * uvStride + CacheLeft);
            int emitted = 0;
            for (int mbY = 0; mbY < mbHeight; mbY++)
            {
                for (int row = 0; row < 16; row++)
                {
                    Unsafe.Add(ref yRow0, row * yStride - 1) = 129;
                }

                for (int row = 0; row < 8; row++)
                {
                    Unsafe.Add(ref uRow0, row * uvStride - 1) = 129;
                    Unsafe.Add(ref vRow0, row * uvStride - 1) = 129;
                }

                leftB.Clear();
                leftNz = 0;
                int tokenPartition = mbY & (partitionCount - 1);
                ref Vp8BoolDecoder tokenDecoder = ref tokens.PartitionRef(tokenPartition);
                for (int mbX = 0; mbX < mbWidth; mbX++)
                {
                    int modes = ReadMacroblockModes(ref header, segments.Enabled && segments.UpdateMap ? segments.MapProbabilities : null,
                        skipFlagPresent ? skipProbability : -1, aboveB.AsSpan(mbX * 4, 4), leftB, blockModes);
                    int segment = modes & 3;
                    bool skip = (modes & 4) != 0;
                    byte yMode = (byte)((modes >> 3) & 7);
                    byte uvMode = (byte)(modes >> 6);
                    ref SegmentParameters sp = ref segmentParameters[segment];
                    ref short coefficientBase = ref MemoryMarshal.GetReference(mbCoefficients);
                    bool i16 = yMode != 4;
                    ulong masks = 0;
                    if (skip)
                    {
                        // A skipped macroblock has no coefficients; only a Y2 context survives, and
                        // only when the macroblock has no Y2 block itself (RFC 6386 §13.3).
                        uint keep = i16 ? 0u : NzY2;
                        aboveNz[mbX] &= keep;
                        leftNz &= keep;
                    }
                    else
                    {
                        masks = ParseResiduals(ref tokenDecoder, ref probabilityBase, ref coefficientBase, ref sp, i16,
                            ref aboveNz[mbX], ref leftNz);
                    }

                    uint nonzeroMask = (uint)masks;
                    uint acMask = (uint)(masks >> 32);
                    rowFilter[mbX * 2] = i16 ? sp.FilterLevelI16 : sp.FilterLevelI4;
                    // Inner edges are filtered unless the macroblock is i16 with no coefficients.
                    rowFilter[mbX * 2 + 1] = (byte)(nonzeroMask == 0 && i16 ? 0 : 1);

                    if (!skipCopy)
                    {
                        PrepareAbove(ref aboveY, ref aboveU, ref aboveV, ref topY, ref topU, ref topV, mbX, mbY, width);
                    }

                    ref byte dy = ref Unsafe.Add(ref yRow0, mbX * 16);
                    ref byte du = ref Unsafe.Add(ref uRow0, mbX * 8);
                    ref byte dv = ref Unsafe.Add(ref vRow0, mbX * 8);
                    if (!skipPrediction)
                    {
                        if (i16)
                        {
                            PredictLarge(ref dy, yStride, 16, yMode, mbY > 0, mbX > 0, ref aboveY);
                        }

                        PredictLarge(ref du, uvStride, 8, uvMode, mbY > 0, mbX > 0, ref aboveU);
                        PredictLarge(ref dv, uvStride, 8, uvMode, mbY > 0, mbX > 0, ref aboveV);
                    }

                    if (i16 && (nonzeroMask & NzY2) != 0)
                    {
                        ref short y2 = ref Unsafe.Add(ref coefficientBase, 24 * 16);
                        InverseWalshToBlocks(ref y2, ref coefficientBase, (acMask & NzY2) != 0);
                        Unsafe.InitBlockUnaligned(ref Unsafe.As<short, byte>(ref y2), 0, 32);
                    }

                    for (int block = 0; block < 16; block++)
                    {
                        int blockX = block & 3;
                        int blockY = block >> 2;
                        ref byte destination = ref Unsafe.Add(ref dy, blockY * 4 * yStride + blockX * 4);
                        if (!i16 && !skipPrediction)
                        {
                            PredictSubblock(ref destination, yStride, blockModes[block],
                                ref SubblockAbove(ref destination, yStride, blockX, blockY, ref aboveY, aboveRows));
                        }

                        // An i16 block can be all zero except for the DC its Y2 block supplied.
                        ref short coefficients = ref Unsafe.Add(ref coefficientBase, block * 16);
                        if (((nonzeroMask >> block) & 1) != 0 || coefficients != 0)
                        {
                            if (!skipResidual)
                            {
                                InverseDctAdd(ref coefficients, ref destination, yStride, residual, ((acMask >> block) & 1) != 0);
                            }

                            Unsafe.InitBlockUnaligned(ref Unsafe.As<short, byte>(ref coefficients), 0, 32);
                        }
                    }

                    if ((nonzeroMask & 0xff0000) != 0)
                    {
                        for (int block = 16; block < 24; block++)
                        {
                            if (((nonzeroMask >> block) & 1) == 0)
                            {
                                continue;
                            }

                            ref short coefficients = ref Unsafe.Add(ref coefficientBase, block * 16);
                            if (!skipResidual)
                            {
                                ref byte plane = ref block < 20 ? ref du : ref dv;
                                InverseDctAdd(ref coefficients, ref Unsafe.Add(ref plane, ((block >> 1) & 1) * 4 * uvStride + (block & 1) * 4),
                                    uvStride, residual, ((acMask >> block) & 1) != 0);
                            }

                            Unsafe.InitBlockUnaligned(ref Unsafe.As<short, byte>(ref coefficients), 0, 32);
                        }
                    }
                }

                if (!skipCopy)
                {
                    // The next row predicts from this one's bottom row before the loop filter.
                    MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref yRow0, 15 * yStride), paddedWidth)
                        .CopyTo(MemoryMarshal.CreateSpan(ref topY, paddedWidth));
                    MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref uRow0, 7 * uvStride), paddedWidth / 2)
                        .CopyTo(MemoryMarshal.CreateSpan(ref topU, paddedWidth / 2));
                    MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref vRow0, 7 * uvStride), paddedWidth / 2)
                        .CopyTo(MemoryMarshal.CreateSpan(ref topV, paddedWidth / 2));
                }

                if (applyLoopFilter)
                {
                    FilterMacroblockRow(yCache, yStride, uCache, vCache, uvStride, mbWidth, mbY, filter, rowFilter);
                }

                int end = mbY == mbHeight - 1 ? height : mbY * 16 + 8;
                if ((ablation & WebpAblation.SkipOutput) == 0 && end > emitted)
                {
                    sink.Emit(
                        ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(yCache), CacheLeft), yStride, mbY * 16 - LumaCacheExtra,
                        ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(uCache), CacheLeft),
                        ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(vCache), CacheLeft),
                        uvStride, mbY * 8 - ChromaCacheExtra, emitted, end);
                }

                emitted = end;

                // Slide the cache down by one macroblock row, keeping the rows the next row's loop
                // filter and output still need.
                if (!skipCopy)
                {
                    yCache.AsSpan(16 * yStride, LumaCacheExtra * yStride).CopyTo(yCache);
                    uCache.AsSpan(8 * uvStride, ChromaCacheExtra * uvStride).CopyTo(uCache);
                    vCache.AsSpan(8 * uvStride, ChromaCacheExtra * uvStride).CopyTo(vCache);
                }
            }
        }
        finally
        {
            WebpBufferPool<byte>.Return(yCache);
            WebpBufferPool<byte>.Return(uCache);
            WebpBufferPool<byte>.Return(vCache);
            WebpBufferPool<byte>.Return(topRows);
            WebpBufferPool<byte>.Return(aboveB);
            WebpBufferPool<uint>.Return(aboveNz);
            WebpBufferPool<byte>.Return(rowFilter);
            WebpBufferPool<byte>.Return(coefficientProbabilities);
            ReturnSegmentBuffers(segments);
            ReturnFilterBuffers(filter);
        }
    }

    /// <summary>
    /// The unfiltered row above macroblock (mbX, mbY) and its top-left pixel: luma with the four
    /// above-right pixels (taken from the last image column where they fall outside the image),
    /// chroma without. Frame edges read 127 above and 129 to the left (RFC 6386 §12.2).
    /// </summary>
    private static void PrepareAbove(ref byte aboveY, ref byte aboveU, ref byte aboveV, ref byte topY, ref byte topU,
        ref byte topV, int mbX, int mbY, int width)
    {
        if (mbY == 0)
        {
            Unsafe.InitBlockUnaligned(ref Unsafe.Subtract(ref aboveY, 1), 127, 21);
            Unsafe.InitBlockUnaligned(ref Unsafe.Subtract(ref aboveU, 1), 127, 9);
            Unsafe.InitBlockUnaligned(ref Unsafe.Subtract(ref aboveV, 1), 127, 9);
            return;
        }

        Unsafe.Subtract(ref aboveY, 1) = mbX == 0 ? (byte)129 : Unsafe.Add(ref topY, mbX * 16 - 1);
        Unsafe.Subtract(ref aboveU, 1) = mbX == 0 ? (byte)129 : Unsafe.Add(ref topU, mbX * 8 - 1);
        Unsafe.Subtract(ref aboveV, 1) = mbX == 0 ? (byte)129 : Unsafe.Add(ref topV, mbX * 8 - 1);
        Unsafe.CopyBlockUnaligned(ref aboveY, ref Unsafe.Add(ref topY, mbX * 16), 16);
        Unsafe.CopyBlockUnaligned(ref aboveU, ref Unsafe.Add(ref topU, mbX * 8), 8);
        Unsafe.CopyBlockUnaligned(ref aboveV, ref Unsafe.Add(ref topV, mbX * 8), 8);
        int right = mbX * 16 + 16;
        if (right + 4 <= width)
        {
            Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref aboveY, 16), ref Unsafe.Add(ref topY, right), 4);
        }
        else
        {
            for (int i = 0; i < 4; i++)
            {
                Unsafe.Add(ref aboveY, 16 + i) = Unsafe.Add(ref topY, Math.Min(right + i, width - 1));
            }
        }
    }

    /// <summary>
    /// What a 4×4 block predicts from above (index -1 is its top-left pixel, 0-3 above, 4-7 above
    /// right): the macroblock's top row for the first block row, the reconstructed rows of the
    /// macroblock otherwise, except that the right-hand column always takes its above-right pixels
    /// from the row above the macroblock (RFC 6386 §12.3); that case is assembled in
    /// <paramref name="aboveRows"/> at <see cref="AboveSubblock"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte SubblockAbove(ref byte destination, int stride, int blockX, int blockY, ref byte aboveY,
        Span<byte> aboveRows)
    {
        if (blockY == 0)
        {
            return ref Unsafe.Add(ref aboveY, blockX * 4);
        }

        ref byte rowAbove = ref Unsafe.Subtract(ref destination, stride);
        if (blockX < 3)
        {
            return ref rowAbove;
        }

        ref byte assembled = ref Unsafe.Add(ref MemoryMarshal.GetReference(aboveRows), AboveSubblock);
        Unsafe.Subtract(ref assembled, 1) = Unsafe.Subtract(ref rowAbove, 1);
        Unsafe.WriteUnaligned(ref assembled, Unsafe.ReadUnaligned<uint>(ref rowAbove));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref assembled, 4), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref aboveY, 16)));
        return ref assembled;
    }

    /// <summary>Per-segment dequantisation factors and loop-filter levels, computed once per frame.</summary>
    private struct SegmentParameters
    {
        public int YDc;
        public int YAc;
        public int Y2Dc;
        public int Y2Ac;
        public int UvDc;
        public int UvAc;
        public byte FilterLevelI16;
        public byte FilterLevelI4;
    }

    [InlineArray(4)]
    private struct SegmentParameterSet
    {
        private SegmentParameters _element;
    }

    private static void ComputeSegmentParameters(
        in SegmentFeatures segments, in Quantizers quantizers, in FilterSettings filter, Span<SegmentParameters> result)
    {
        for (int segment = 0; segment < 4; segment++)
        {
            int segmentQuant = segments.Enabled ? segments.Quant[segment] : 0;
            int qIndex = Math.Clamp(segments.Absolute ? segmentQuant : quantizers.Base + segmentQuant, 0, 127);
            int segmentFilter = segments.Enabled ? segments.Filter[segment] : 0;
            int filterLevel = segments.Enabled && segments.Absolute ? segmentFilter : filter.Level + segmentFilter;
            int levelI16 = filterLevel;
            int levelI4 = filterLevel;
            if (filter.DeltaEnabled)
            {
                levelI16 += filter.RefDeltas[0];
                levelI4 += filter.RefDeltas[0] + filter.ModeDeltas[0];
            }

            ref SegmentParameters p = ref result[segment];
            p.FilterLevelI16 = (byte)Math.Clamp(levelI16, 0, 63);
            p.FilterLevelI4 = (byte)Math.Clamp(levelI4, 0, 63);
            p.YDc = Vp8Tables.DcQuantizers[Math.Clamp(qIndex + quantizers.YDcDelta, 0, 127)];
            p.YAc = Vp8Tables.AcQuantizers[qIndex];
            p.Y2Dc = 2 * Vp8Tables.DcQuantizers[Math.Clamp(qIndex + quantizers.Y2DcDelta, 0, 127)];
            p.Y2Ac = Math.Max(8, 155 * Vp8Tables.AcQuantizers[Math.Clamp(qIndex + quantizers.Y2AcDelta, 0, 127)] / 100);
            p.UvDc = Math.Min(132, Vp8Tables.DcQuantizers[Math.Clamp(qIndex + quantizers.UvDcDelta, 0, 127)]);
            p.UvAc = Vp8Tables.AcQuantizers[Math.Clamp(qIndex + quantizers.UvAcDelta, 0, 127)];
        }
    }

    // Offset of the (band, context 0) probability row for each coefficient position:
    // band(position) × 3 contexts × 11 node probabilities (RFC 6386 §13.3).
    private static ReadOnlySpan<byte> BandRowOffsets =>
        [0, 33, 66, 99, 198, 132, 165, 198, 198, 198, 198, 198, 198, 198, 198, 231];

    // Non-zero contexts (RFC 6386 §13.3) of a macroblock edge, one bit per 4×4 block column (above)
    // or row (left): Y in bits 0-3, U in 4-5, V in 6-7, Y2 in bit 24.
    private const uint NzY2 = 1u << 24;

    /// <summary>
    /// Parses all coefficient tokens of one macroblock with the arithmetic state in registers
    /// throughout, updating the above/left non-zero contexts. Returns two block masks: bits 0-15
    /// luma, 16-19 U, 20-23 V, 24 Y2; the low half marks blocks with a non-zero token, the high
    /// half blocks with one beyond the DC position.
    /// </summary>
    private static ulong ParseResiduals(ref Vp8BoolDecoder decoder, ref byte probabilities, ref short coefficients,
        ref SegmentParameters sp, bool i16, ref uint aboveNz, ref uint leftNz)
    {
        decoder.Load(out ulong v, out int b, out uint r);
        uint above = aboveNz;
        uint left = leftNz;
        ulong masks = 0;
        ref byte aboveBits = ref MemoryMarshal.GetReference(BlockAboveContextBit);
        ref byte leftBits = ref MemoryMarshal.GetReference(BlockLeftContextBit);

        // One call site for every block keeps a single inlined copy of the token walk, so the
        // arithmetic state stays in registers. Block 24 (Y2) goes first when present.
        int block = i16 ? 24 : 0;
        int lumaType = i16 ? 0 : 3;
        int lumaStart = i16 ? 1 : 0;
        int lumaDc = i16 ? 1 : sp.YDc;
        while (true)
        {
            int type, start, dc, ac;
            if (block < 16)
            {
                type = lumaType;
                start = lumaStart;
                dc = lumaDc;
                ac = sp.YAc;
            }
            else if (block < 24)
            {
                type = 2;
                start = 0;
                dc = sp.UvDc;
                ac = sp.UvAc;
            }
            else
            {
                type = 1;
                start = 0;
                dc = sp.Y2Dc;
                ac = sp.Y2Ac;
            }

            int aboveBit = Unsafe.Add(ref aboveBits, block);
            int leftBit = Unsafe.Add(ref leftBits, block);
            int context = (int)(((above >> aboveBit) & 1) + ((left >> leftBit) & 1));
            int last = Tokens(ref decoder, ref v, ref b, ref r, ref Unsafe.Add(ref probabilities, type * 8 * 33), context,
                start, ref Unsafe.Add(ref coefficients, block * 16), dc, ac);
            uint nonzero = last > 0 ? 1u : 0u;
            above = (above & ~(1u << aboveBit)) | (nonzero << aboveBit);
            left = (left & ~(1u << leftBit)) | (nonzero << leftBit);
            masks |= ((ulong)nonzero << block) | ((last > 1 ? 1UL : 0UL) << (block + 32));
            if (block == 24)
            {
                block = 0;
            }
            else if (++block == 24)
            {
                break;
            }
        }

        aboveNz = above;
        leftNz = left;
        decoder.Store(v, b, r);
        return masks;
    }

    // Context bit of each block (0-15 luma, 16-19 U, 20-23 V, 24 Y2) in the above / left masks.
    private static ReadOnlySpan<byte> BlockAboveContextBit =>
        [0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 4, 5, 4, 5, 6, 7, 6, 7, 24];

    private static ReadOnlySpan<byte> BlockLeftContextBit =>
        [0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 24];

    /// <summary>
    /// Parses one block's tokens (RFC 6386 §13.2) and stores the dequantised coefficients, wrapped
    /// to 16 bits, at their raster positions in <paramref name="output"/> (which must be zero).
    /// Returns one past the zigzag index of the last non-zero token, or 0 if there is none.
    /// The coefficient token tree is walked with its node probabilities in fixed order:
    /// p0 EOB?, p1 ZERO?, p2 ONE?, p3 (TWO..FOUR | larger), p4 TWO?, p5 THREE/FOUR,
    /// p6 (cat1/2 | cat3..6), p7 cat1/cat2, p8 (cat3/4 | cat5/6), p9 cat3/cat4, p10 cat5/cat6.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Tokens(ref Vp8BoolDecoder decoder, ref ulong v, ref int b, ref uint r, ref byte typeRows,
        int context, int position, ref short output, int dcFactor, int acFactor)
    {
        ref byte bandRows = ref MemoryMarshal.GetReference(BandRowOffsets);
        ref byte zigzag = ref MemoryMarshal.GetArrayDataReference(Vp8Tables.Zigzag);
        ref byte p = ref Unsafe.Add(ref typeRows, Unsafe.Add(ref bandRows, position) + context * 11);
        int last = 0;
        if (Vp8BoolDecoder.ReadBit(ref decoder, ref v, ref b, ref r, p) != 0)
        {
            while (true)
            {
                if (Vp8BoolDecoder.ReadBit(ref decoder, ref v, ref b, ref r, Unsafe.Add(ref p, 1)) == 0)
                {
                    // ZERO: the next token cannot be EOB, so its tree walk starts at p1.
                    if (++position == 16)
                    {
                        break;
                    }

                    p = ref Unsafe.Add(ref typeRows, Unsafe.Add(ref bandRows, position));
                    continue;
                }

                int value;
                int nextContext;
                if (Vp8BoolDecoder.ReadBit(ref decoder, ref v, ref b, ref r, Unsafe.Add(ref p, 2)) == 0)
                {
                    value = 1;
                    nextContext = 11;
                }
                else
                {
                    nextContext = 22;
                    if (Vp8BoolDecoder.ReadBit(ref decoder, ref v, ref b, ref r, Unsafe.Add(ref p, 3)) == 0)
                    {
                        value = Vp8BoolDecoder.ReadBit(ref decoder, ref v, ref b, ref r, Unsafe.Add(ref p, 4)) == 0
                            ? 2
                            : 3 + Vp8BoolDecoder.ReadBit(ref decoder, ref v, ref b, ref r, Unsafe.Add(ref p, 5));
                    }
                    else if (Vp8BoolDecoder.ReadBit(ref decoder, ref v, ref b, ref r, Unsafe.Add(ref p, 6)) == 0)
                    {
                        if (Vp8BoolDecoder.ReadBit(ref decoder, ref v, ref b, ref r, Unsafe.Add(ref p, 7)) == 0)
                        {
                            value = 5 + Vp8BoolDecoder.ReadBit(ref decoder, ref v, ref b, ref r, 159);
                        }
                        else
                        {
                            int high = Vp8BoolDecoder.ReadBit(ref decoder, ref v, ref b, ref r, 165) << 1;
                            value = 7 + (high | Vp8BoolDecoder.ReadBit(ref decoder, ref v, ref b, ref r, 145));
                        }
                    }
                    else
                    {
                        // Rare: hand the state back through memory so that the locals never have
                        // their address taken by a call that is not inlined.
                        decoder.Store(v, b, r);
                        value = ReadCategory3To6(ref decoder, ref p);
                        decoder.Load(out v, out b, out r);
                    }
                }

                int factor = position == 0 ? dcFactor : acFactor;
                int signed = Vp8BoolDecoder.ReadSigned(ref decoder, ref v, ref b, ref r, value);
                Unsafe.Add(ref output, Unsafe.Add(ref zigzag, position)) = unchecked((short)(signed * factor));
                last = ++position;
                if (position == 16)
                {
                    break;
                }

                p = ref Unsafe.Add(ref typeRows, Unsafe.Add(ref bandRows, position) + nextContext);
                if (Vp8BoolDecoder.ReadBit(ref decoder, ref v, ref b, ref r, p) == 0)
                {
                    break;
                }
            }
        }

        return last;
    }

    // DCT_CAT3..DCT_CAT6 (the subtree at p8) with their extra bits, RFC 6386 §13.2.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadCategory3To6(ref Vp8BoolDecoder d, ref byte p)
    {
        ReadOnlySpan<byte> extra;
        int baseValue;
        if (d.ReadBit(Unsafe.Add(ref p, 8)) == 0)
        {
            if (d.ReadBit(Unsafe.Add(ref p, 9)) == 0)
            {
                extra = Vp8Tables.Pcat3;
                baseValue = 11;
            }
            else
            {
                extra = Vp8Tables.Pcat4;
                baseValue = 19;
            }
        }
        else if (d.ReadBit(Unsafe.Add(ref p, 10)) == 0)
        {
            extra = Vp8Tables.Pcat5;
            baseValue = 35;
        }
        else
        {
            extra = Vp8Tables.Pcat6;
            baseValue = 67;
        }

        int value = 0;
        for (int i = 0; extra[i] != 0; i++)
        {
            value = (value << 1) | d.ReadBit(extra[i]);
        }

        return baseValue + value;
    }

    /// <summary>
    /// Inverse Walsh–Hadamard transform of the dequantised Y2 block (RFC 6386 §14.3): each output
    /// becomes the DC coefficient (index 0) of the corresponding luma block.
    /// </summary>
    private static void InverseWalshToBlocks(ref short input, ref short blocks, bool hasAc)
    {
        if (!hasAc)
        {
            short dc = (short)((input + 3) >> 3);
            for (int i = 0; i < 16; i++)
            {
                Unsafe.Add(ref blocks, i * 16) = dc;
            }

            return;
        }

        Span<short> horizontal = stackalloc short[16];
        for (int column = 0; column < 4; column++)
        {
            int a = Unsafe.Add(ref input, column) + Unsafe.Add(ref input, 12 + column);
            int b = Unsafe.Add(ref input, 4 + column) + Unsafe.Add(ref input, 8 + column);
            int c = Unsafe.Add(ref input, 4 + column) - Unsafe.Add(ref input, 8 + column);
            int d = Unsafe.Add(ref input, column) - Unsafe.Add(ref input, 12 + column);
            horizontal[column] = unchecked((short)(a + b));
            horizontal[4 + column] = unchecked((short)(c + d));
            horizontal[8 + column] = unchecked((short)(a - b));
            horizontal[12 + column] = unchecked((short)(d - c));
        }

        for (int row = 0; row < 4; row++)
        {
            int i = row * 4;
            int a = horizontal[i] + horizontal[i + 3];
            int b = horizontal[i + 1] + horizontal[i + 2];
            int c = horizontal[i + 1] - horizontal[i + 2];
            int d = horizontal[i] - horizontal[i + 3];
            Unsafe.Add(ref blocks, i * 16) = (short)((a + b + 3) >> 3);
            Unsafe.Add(ref blocks, (i + 1) * 16) = (short)((c + d + 3) >> 3);
            Unsafe.Add(ref blocks, (i + 2) * 16) = (short)((a - b + 3) >> 3);
            Unsafe.Add(ref blocks, (i + 3) * 16) = (short)((d - c + 3) >> 3);
        }
    }

    /// <summary>
    /// RFC 6386 §14.4 inverse DCT of 16-bit dequantised coefficients into a residual block: a
    /// vertical pass whose results wrap to 16 bits, then a horizontal pass rounded by (x + 4) >> 3.
    /// Without AC coefficients every residual is the rounded DC.
    /// </summary>
    internal static void InverseDctShort(ref short input, Span<short> output, bool hasAcCoefficients)
    {
        if (!hasAcCoefficients)
        {
            output.Fill((short)((input + 4) >> 3));
            return;
        }

        Span<short> horizontal = stackalloc short[16];
        for (int column = 0; column < 4; column++)
        {
            Transform1D(Unsafe.Add(ref input, column), Unsafe.Add(ref input, 8 + column),
                Unsafe.Add(ref input, 4 + column), Unsafe.Add(ref input, 12 + column),
                out int a, out int b, out int c, out int d);
            horizontal[column] = unchecked((short)a);
            horizontal[4 + column] = unchecked((short)b);
            horizontal[8 + column] = unchecked((short)c);
            horizontal[12 + column] = unchecked((short)d);
        }

        for (int row = 0; row < 4; row++)
        {
            int i = row * 4;
            Transform1D(horizontal[i], horizontal[i + 2], horizontal[i + 1], horizontal[i + 3],
                out int a, out int b, out int c, out int d);
            output[i] = (short)((a + 4) >> 3);
            output[i + 1] = (short)((b + 4) >> 3);
            output[i + 2] = (short)((c + 4) >> 3);
            output[i + 3] = (short)((d + 4) >> 3);
        }
    }

    private static void ReturnSegmentBuffers(SegmentFeatures segments)
    {
        if (segments.Quant is not null)
        {
            WebpBufferPool<int>.Return(segments.Quant);
        }

        if (segments.Filter is not null)
        {
            WebpBufferPool<int>.Return(segments.Filter);
        }

        if (segments.MapProbabilities is not null)
        {
            WebpBufferPool<byte>.Return(segments.MapProbabilities);
        }
    }

    private static void ReturnFilterBuffers(FilterSettings filter)
    {
        if (filter.RefDeltas is not null)
        {
            WebpBufferPool<sbyte>.Return(filter.RefDeltas);
        }

        if (filter.ModeDeltas is not null)
        {
            WebpBufferPool<sbyte>.Return(filter.ModeDeltas);
        }
    }

    private static SegmentFeatures ReadSegments(ref Vp8BoolDecoder decoder)
    {
        bool enabled = decoder.ReadBit(128) != 0;
        if (!enabled)
        {
            return default;
        }

        bool updateMap = decoder.ReadBit(128) != 0;
        bool updateData = decoder.ReadBit(128) != 0;
        bool absolute = false;
        int[]? quant = null;
        int[]? filter = null;
        byte[]? mapProbabilities = null;
        try
        {
            quant = WebpBufferPool<int>.Rent(4);
            filter = WebpBufferPool<int>.Rent(4);
            Array.Clear(quant, 0, 4);
            Array.Clear(filter, 0, 4);
            if (updateData)
            {
                absolute = decoder.ReadBit(128) != 0;
                for (int i = 0; i < 4; i++)
                {
                    if (decoder.ReadBit(128) != 0)
                    {
                        quant[i] = decoder.ReadSignedLiteral(7);
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    if (decoder.ReadBit(128) != 0)
                    {
                        filter[i] = decoder.ReadSignedLiteral(6);
                    }
                }
            }

            mapProbabilities = WebpBufferPool<byte>.Rent(3);
            mapProbabilities.AsSpan(0, 3).Fill(255);
            if (updateMap)
            {
                for (int i = 0; i < 3; i++)
                {
                    if (decoder.ReadBit(128) != 0)
                    {
                        mapProbabilities[i] = (byte)decoder.ReadLiteral(8);
                    }
                }
            }

            return new SegmentFeatures(enabled, updateMap, absolute, quant, filter, mapProbabilities);
        }
        catch
        {
            if (quant is not null) WebpBufferPool<int>.Return(quant);
            if (filter is not null) WebpBufferPool<int>.Return(filter);
            if (mapProbabilities is not null) WebpBufferPool<byte>.Return(mapProbabilities);
            throw;
        }
    }

    private static FilterSettings ReadFilter(ref Vp8BoolDecoder decoder)
    {
        bool simple = decoder.ReadBit(128) != 0;
        int level = decoder.ReadLiteral(6);
        int sharpness = decoder.ReadLiteral(3);
        sbyte[]? refs = null;
        sbyte[]? modes = null;
        try
        {
            refs = WebpBufferPool<sbyte>.Rent(4);
            modes = WebpBufferPool<sbyte>.Rent(4);
            Array.Clear(refs, 0, 4);
            Array.Clear(modes, 0, 4);
            bool deltaEnabled = decoder.ReadBit(128) != 0;
            if (deltaEnabled && decoder.ReadBit(128) != 0)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (decoder.ReadBit(128) != 0)
                    {
                        refs[i] = (sbyte)decoder.ReadSignedLiteral(6);
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    if (decoder.ReadBit(128) != 0)
                    {
                        modes[i] = (sbyte)decoder.ReadSignedLiteral(6);
                    }
                }
            }

            return new FilterSettings(simple, level, sharpness, deltaEnabled, refs, modes);
        }
        catch
        {
            if (refs is not null) WebpBufferPool<sbyte>.Return(refs);
            if (modes is not null) WebpBufferPool<sbyte>.Return(modes);
            throw;
        }
    }

    private static Quantizers ReadQuantizers(ref Vp8BoolDecoder decoder)
    {
        int baseIndex = decoder.ReadLiteral(7);
        return new Quantizers(
            baseIndex,
            ReadDelta(ref decoder),
            ReadDelta(ref decoder),
            ReadDelta(ref decoder),
            ReadDelta(ref decoder),
            ReadDelta(ref decoder));
    }

    private static int ReadDelta(ref Vp8BoolDecoder decoder) =>
        decoder.ReadBit(128) == 0 ? 0 : decoder.ReadSignedLiteral(4);

    private static void ReadCoefficientProbabilityUpdates(ref Vp8BoolDecoder decoder, byte[] probabilities)
    {
        int updateIndex = 0;
        for (int type = 0; type < 4; type++)
        {
            for (int band = 0; band < 8; band++)
            {
                for (int context = 0; context < 3; context++)
                {
                    for (int node = 0; node < 11; node++)
                    {
                        if (decoder.ReadBit(Vp8Tables.CoefficientUpdateProbabilities[type][band][context][node]) != 0)
                        {
                            probabilities[updateIndex] = (byte)decoder.ReadLiteral(8);
                        }

                        updateIndex++;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The per-macroblock header fields of a key frame (RFC 6386 §19.3): segment id, skip flag, luma
    /// mode, the sixteen 4×4 modes of a B_PRED macroblock (contexts from the above / left modes,
    /// updated here) and the chroma mode, with the bool decoder's state in locals throughout.
    /// Returns segment | skip &lt;&lt; 2 | yMode &lt;&lt; 3 | uvMode &lt;&lt; 6.
    /// </summary>
    private static int ReadMacroblockModes(ref Vp8BoolDecoder header, byte[]? segmentProbabilities, int skipProbability,
        scoped Span<byte> aboveModes, scoped Span<byte> leftModes, scoped Span<byte> blockModes)
    {
        header.Load(out ulong v, out int b, out uint r);
        int segment = segmentProbabilities is null ? 0
            : ReadTree(ref header, ref v, ref b, ref r, Vp8Tables.SegmentIdTree, segmentProbabilities);
        int skip = skipProbability < 0 ? 0 : Vp8BoolDecoder.ReadBit(ref header, ref v, ref b, ref r, skipProbability);
        int yMode = ReadTree(ref header, ref v, ref b, ref r, Vp8Tables.KeyFrameYModeTree, Vp8Tables.KeyFrameYModeProbabilities);
        if (yMode == 4)
        {
            ref byte above = ref MemoryMarshal.GetReference(aboveModes[..4]);
            ref byte left = ref MemoryMarshal.GetReference(leftModes[..4]);
            ref byte modes = ref MemoryMarshal.GetReference(blockModes[..16]);
            ref byte probabilities = ref MemoryMarshal.GetArrayDataReference(BlockModeProbabilities);
            ref sbyte tree = ref MemoryMarshal.GetArrayDataReference(Vp8Tables.BlockModeTree);
            for (int i = 0; i < 16; i++)
            {
                // Modes are 0-9 (tree leaves, or the 0-3 derived from a luma mode), so the
                // context row index is below 100.
                int aboveMode = i < 4 ? Unsafe.Add(ref above, i) : Unsafe.Add(ref modes, i - 4);
                int leftMode = (i & 3) == 0 ? Unsafe.Add(ref left, i >> 2) : Unsafe.Add(ref modes, i - 1);
                ref byte p = ref Unsafe.Add(ref probabilities, (aboveMode * 10 + leftMode) * 9);
                int node = 0;
                int child;
                while ((child = Unsafe.Add(ref tree, node + Vp8BoolDecoder.ReadBit(ref header, ref v, ref b, ref r,
                    Unsafe.Add(ref p, node >> 1)))) > 0)
                {
                    node = child;
                }

                Unsafe.Add(ref modes, i) = (byte)-child;
            }

            for (int i = 0; i < 4; i++)
            {
                Unsafe.Add(ref above, i) = Unsafe.Add(ref modes, 12 + i);
                Unsafe.Add(ref left, i) = Unsafe.Add(ref modes, i * 4 + 3);
            }
        }
        else
        {
            byte derived = yMode switch { 0 => 0, 1 => 2, 2 => 3, _ => 1 };
            aboveModes[..4].Fill(derived);
            leftModes[..4].Fill(derived);
        }

        int uvMode = ReadTree(ref header, ref v, ref b, ref r, Vp8Tables.UvModeTree, Vp8Tables.KeyFrameUvModeProbabilities);
        header.Store(v, b, r);
        return segment | (skip << 2) | (yMode << 3) | (uvMode << 6);
    }

    // RFC 6386 §11.5 key-frame 4×4 mode probabilities, [above][left][node] flattened.
    private static readonly byte[] BlockModeProbabilities = FlattenBlockModeProbabilities();

    private static byte[] FlattenBlockModeProbabilities()
    {
        byte[] flat = new byte[10 * 10 * 9];
        for (int above = 0; above < 10; above++)
        {
            for (int left = 0; left < 10; left++)
            {
                Vp8Tables.KeyFrameBlockModeProbabilities[above][left].CopyTo(flat, (above * 10 + left) * 9);
            }
        }

        return flat;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadTree(ref Vp8BoolDecoder decoder, ref ulong v, ref int b, ref uint r, ReadOnlySpan<sbyte> tree,
        ReadOnlySpan<byte> probabilities)
    {
        int node = 0;
        while (true)
        {
            int child = tree[node + Vp8BoolDecoder.ReadBit(ref decoder, ref v, ref b, ref r, probabilities[node >> 1])];
            if (child <= 0)
            {
                return -child;
            }

            node = child;
        }
    }

    private static int ReadSegmentId(ref Vp8BoolDecoder decoder, byte[] probabilities) =>
        ReadTree(ref decoder, Vp8Tables.SegmentIdTree, probabilities);

    private static int ReadYMode(ref Vp8BoolDecoder decoder) =>
        ReadTree(ref decoder, Vp8Tables.KeyFrameYModeTree, Vp8Tables.KeyFrameYModeProbabilities);

    private static int ReadUvMode(ref Vp8BoolDecoder decoder) =>
        ReadTree(ref decoder, Vp8Tables.UvModeTree, Vp8Tables.KeyFrameUvModeProbabilities);

    private static int ReadBMode(ref Vp8BoolDecoder decoder, int above, int left) =>
        ReadTree(ref decoder, Vp8Tables.BlockModeTree, Vp8Tables.KeyFrameBlockModeProbabilities[above][left]);

    private static int ReadTree(ref Vp8BoolDecoder decoder, ReadOnlySpan<sbyte> tree,
        ReadOnlySpan<byte> probabilities, int firstNode = 0)
    {
        int node = firstNode;
        while (true)
        {
            int child = tree[node + decoder.ReadBit(probabilities[node >> 1])];
            if (child <= 0)
            {
                return -child;
            }

            node = child;
        }
    }

    private static PartitionSlices ReadPartitions(ReadOnlySpan<byte> data, int count)
    {
        ReadOnlySpan<byte> p0 = default, p1 = default, p2 = default, p3 = default;
        ReadOnlySpan<byte> p4 = default, p5 = default, p6 = default, p7 = default;
        int sizeCount = count - 1;
        int tableLength = sizeCount * 3;
        if (data.Length < tableLength)
        {
            throw new InvalidDataException("The VP8 token partition size table is truncated.");
        }

        Span<int> sizes = stackalloc int[7];
        for (int i = 0; i < sizeCount; i++)
        {
            int sizeOffset = i * 3;
            sizes[i] = data[sizeOffset] | data[sizeOffset + 1] << 8 | data[sizeOffset + 2] << 16;
        }

        int offset = tableLength;
        for (int i = 0; i < count; i++)
        {
            int size = i == sizeCount ? data.Length - offset : sizes[i];
            if (size > data.Length - offset)
            {
                throw new InvalidDataException("A VP8 token partition exceeds the frame.");
            }

            ReadOnlySpan<byte> part = data.Slice(offset, size);
            switch (i)
            {
                case 0: p0 = part; break;
                case 1: p1 = part; break;
                case 2: p2 = part; break;
                case 3: p3 = part; break;
                case 4: p4 = part; break;
                case 5: p5 = part; break;
                case 6: p6 = part; break;
                case 7: p7 = part; break;
            }

            offset += size;
        }

        return new PartitionSlices(p0, p1, p2, p3, p4, p5, p6, p7);
    }

    private static void Transform1D(int p0, int p2, int p1, int p3,
        out int a, out int b, out int c, out int d)
    {
        int a0 = p0 + p2;
        int b0 = p0 - p2;
        int c0 = ((p1 * 35468) >> 16) - (p3 + ((p3 * 20091) >> 16));
        int d0 = p1 + ((p1 * 20091) >> 16) + ((p3 * 35468) >> 16);
        a = a0 + d0;
        b = b0 + c0;
        c = b0 - c0;
        d = a0 - d0;
    }

    /// <summary>Inverse-transforms a dequantised block and adds it to the 4×4 pixels at <paramref name="destination"/>.</summary>
    private static void InverseDctAdd(ref short coefficients, ref byte destination, int stride, Span<short> residual, bool hasAc)
    {
        if (hasAc && Vector128.IsHardwareAccelerated && (Sse2.IsSupported || AdvSimd.Arm64.IsSupported))
        {
            InverseDctAddVector(ref coefficients, ref destination, stride);
            return;
        }

        InverseDctShort(ref coefficients, residual, hasAc);
        AddResidual(ref destination, stride, residual);
    }

    /// <summary>
    /// <see cref="InverseDctShort"/> + <see cref="AddResidualScalar"/> in 32-bit lanes, one row of
    /// the block per vector: the vertical pass across the four columns at once, results wrapped to
    /// 16 bits as the scalar pass stores them; a 4×4 transpose; the horizontal pass across the four
    /// rows, rounded by (x + 4) >> 3 and wrapped; a transpose back; then the add and clamp.
    /// Every product (x × 20091 / 35468 with |x| ≤ 32768) fits 32 bits, as in the scalar code.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void InverseDctAddVector(ref short coefficients, ref byte destination, int stride)
    {
        Vector128<int> r0 = LoadRow(ref coefficients, 0);
        Vector128<int> r1 = LoadRow(ref coefficients, 4);
        Vector128<int> r2 = LoadRow(ref coefficients, 8);
        Vector128<int> r3 = LoadRow(ref coefficients, 12);
        Transform1DVector(r0, r1, r2, r3, out Vector128<int> h0, out Vector128<int> h1, out Vector128<int> h2, out Vector128<int> h3);
        Transpose4x4(Wrap16(h0), Wrap16(h1), Wrap16(h2), Wrap16(h3),
            out Vector128<int> t0, out Vector128<int> t1, out Vector128<int> t2, out Vector128<int> t3);
        Transform1DVector(t0, t1, t2, t3, out Vector128<int> o0, out Vector128<int> o1, out Vector128<int> o2, out Vector128<int> o3);
        Vector128<int> four = Vector128.Create(4);
        Transpose4x4(Wrap16((o0 + four) >> 3), Wrap16((o1 + four) >> 3), Wrap16((o2 + four) >> 3), Wrap16((o3 + four) >> 3),
            out Vector128<int> row0, out Vector128<int> row1, out Vector128<int> row2, out Vector128<int> row3);
        AddRow(ref destination, row0);
        AddRow(ref Unsafe.Add(ref destination, stride), row1);
        AddRow(ref Unsafe.Add(ref destination, 2 * stride), row2);
        AddRow(ref Unsafe.Add(ref destination, 3 * stride), row3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> LoadRow(ref short coefficients, int offset) =>
        Vector128.WidenLower(Vector128.CreateScalar(
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref coefficients, offset)))).AsInt16());

    // RFC 6386 §14.4's 1-D transform on (p0, p1, p2, p3) = rows / columns 0..3.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transform1DVector(Vector128<int> p0, Vector128<int> p1, Vector128<int> p2, Vector128<int> p3,
        out Vector128<int> a, out Vector128<int> b, out Vector128<int> c, out Vector128<int> d)
    {
        Vector128<int> a0 = p0 + p2;
        Vector128<int> b0 = p0 - p2;
        Vector128<int> c0 = ((p1 * 35468) >> 16) - (p3 + ((p3 * 20091) >> 16));
        Vector128<int> d0 = p1 + ((p1 * 20091) >> 16) + ((p3 * 35468) >> 16);
        a = a0 + d0;
        b = b0 + c0;
        c = b0 - c0;
        d = a0 - d0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Wrap16(Vector128<int> value) => (value << 16) >> 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose4x4(Vector128<int> r0, Vector128<int> r1, Vector128<int> r2, Vector128<int> r3,
        out Vector128<int> c0, out Vector128<int> c1, out Vector128<int> c2, out Vector128<int> c3)
    {
        Vector128<int> low01, high01, low23, high23;
        if (Sse2.IsSupported)
        {
            low01 = Sse2.UnpackLow(r0, r1);
            high01 = Sse2.UnpackHigh(r0, r1);
            low23 = Sse2.UnpackLow(r2, r3);
            high23 = Sse2.UnpackHigh(r2, r3);
            c0 = Sse2.UnpackLow(low01.AsInt64(), low23.AsInt64()).AsInt32();
            c1 = Sse2.UnpackHigh(low01.AsInt64(), low23.AsInt64()).AsInt32();
            c2 = Sse2.UnpackLow(high01.AsInt64(), high23.AsInt64()).AsInt32();
            c3 = Sse2.UnpackHigh(high01.AsInt64(), high23.AsInt64()).AsInt32();
        }
        else
        {
            low01 = AdvSimd.Arm64.ZipLow(r0, r1);
            high01 = AdvSimd.Arm64.ZipHigh(r0, r1);
            low23 = AdvSimd.Arm64.ZipLow(r2, r3);
            high23 = AdvSimd.Arm64.ZipHigh(r2, r3);
            c0 = AdvSimd.Arm64.ZipLow(low01.AsInt64(), low23.AsInt64()).AsInt32();
            c1 = AdvSimd.Arm64.ZipHigh(low01.AsInt64(), low23.AsInt64()).AsInt32();
            c2 = AdvSimd.Arm64.ZipLow(high01.AsInt64(), high23.AsInt64()).AsInt32();
            c3 = AdvSimd.Arm64.ZipHigh(high01.AsInt64(), high23.AsInt64()).AsInt32();
        }
    }

    // pixel + residual clamped to [0, 255] for four pixels (the residual already fits 16 bits).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddRow(ref byte destination, Vector128<int> residual)
    {
        Vector128<int> pixels = Vector128.WidenLower(Vector128.WidenLower(
            Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref destination)).AsByte())).AsInt32();
        Vector128<int> sum = Vector128.Min(Vector128.Max(pixels + residual, Vector128<int>.Zero), Vector128.Create(255));
        Vector128<ushort> words = Vector128.Narrow(sum.AsUInt32(), sum.AsUInt32());
        Unsafe.WriteUnaligned(ref destination, Vector128.Narrow(words, words).AsUInt32().ToScalar());
    }

    private static void AddResidual(ref byte destination, int stride, ReadOnlySpan<short> residual)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            AddResidualVector(ref destination, stride, residual);
            return;
        }

        AddResidualScalar(ref destination, stride, residual);
    }

    internal static void AddResidualVector(ref byte destination, int stride, ReadOnlySpan<short> residual)
    {
        Vector128<short> zero = Vector128<short>.Zero;
        Vector128<short> max = Vector128.Create((short)255);
        ref byte dst = ref destination;
        ref short res = ref MemoryMarshal.GetReference(residual);
        for (int row = 0; row < 4; row++)
        {
            Vector128<short> p = Vector128.WidenLower(
                Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref dst)).AsByte()).AsInt16();
            Vector128<short> r = Vector128.CreateScalar(
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<short, byte>(ref res))).AsInt16();
            Vector128<short> s = Vector128.Min(Vector128.Max(Vector128.Add(p, r), zero), max);
            Unsafe.WriteUnaligned(ref dst,
                Vector128.Narrow(s.AsUInt16(), s.AsUInt16()).AsUInt32().ToScalar());
            dst = ref Unsafe.Add(ref dst, stride);
            res = ref Unsafe.Add(ref res, 4);
        }
    }

    internal static void AddResidualScalar(ref byte destination, int stride, ReadOnlySpan<short> residual)
    {
        for (int row = 0; row < 4; row++)
        {
            ref byte line = ref Unsafe.Add(ref destination, row * stride);
            for (int column = 0; column < 4; column++)
            {
                ref byte pixel = ref Unsafe.Add(ref line, column);
                pixel = (byte)Math.Clamp(pixel + residual[row * 4 + column], 0, 255);
            }
        }
    }

    /// <summary>
    /// 16×16 luma or 8×8 chroma prediction (RFC 6386 §12.2): 0 DC, 1 vertical, 2 horizontal,
    /// 3 TrueMotion. <paramref name="destination"/> is the block's top-left pixel; the row above
    /// and the column to the left (frame edges already filled with 127 / 129) are read from around
    /// it. DC averages only the edges inside the frame.
    /// </summary>
    private static void PredictLarge(ref byte destination, int stride, int size, int mode, bool hasTop, bool hasLeft,
        ref byte above)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            switch (mode)
            {
                case 0:
                    PredictDcVector(ref destination, stride, size, hasTop, hasLeft, ref above);
                    return;
                case 1:
                    PredictVerticalVector(ref destination, stride, size, ref above);
                    return;
                case 3:
                    PredictTrueMotionVector(ref destination, stride, size, ref above);
                    return;
            }
        }

        PredictLargeScalar(ref destination, stride, size, mode, hasTop, hasLeft, ref above);
    }

    // Whole-row vertical copy: the predicted block is just the row above, stored size times.
    internal static void PredictVerticalVector(ref byte destination, int stride, int size, ref byte above)
    {
        ref byte row = ref destination;
        if (size == 16)
        {
            Vector128<byte> aboveRow = Vector128.LoadUnsafe(ref above);
            for (int i = 0; i < 16; i++)
            {
                aboveRow.StoreUnsafe(ref row);
                row = ref Unsafe.Add(ref row, stride);
            }
        }
        else
        {
            ulong aboveRow = Unsafe.ReadUnaligned<ulong>(ref above);
            for (int i = 0; i < 8; i++)
            {
                Unsafe.WriteUnaligned(ref row, aboveRow);
                row = ref Unsafe.Add(ref row, stride);
            }
        }
    }

    // TrueMotion: dst[col] = clamp(left[row] + above[col] - topLeft). Widening to short lanes keeps
    // the sum exact and the clamp before Narrow makes the truncating narrow exact.
    internal static void PredictTrueMotionVector(ref byte destination, int stride, int size, ref byte aboveRow)
    {
        int topLeft = Unsafe.Subtract(ref aboveRow, 1);
        Vector128<short> zero = Vector128<short>.Zero;
        Vector128<short> max = Vector128.Create((short)255);
        if (size == 16)
        {
            Vector128<byte> above = Vector128.LoadUnsafe(ref aboveRow);
            Vector128<short> aboveLo = Vector128.WidenLower(above).AsInt16();
            Vector128<short> aboveHi = Vector128.WidenUpper(above).AsInt16();
            for (int row = 0; row < 16; row++)
            {
                ref byte line = ref Unsafe.Add(ref destination, row * stride);
                Vector128<short> delta = Vector128.Create((short)(Unsafe.Subtract(ref line, 1) - topLeft));
                Vector128<short> lo = Vector128.Min(Vector128.Max(Vector128.Add(aboveLo, delta), zero), max);
                Vector128<short> hi = Vector128.Min(Vector128.Max(Vector128.Add(aboveHi, delta), zero), max);
                Vector128.Narrow(lo.AsUInt16(), hi.AsUInt16()).StoreUnsafe(ref line);
            }
        }
        else
        {
            Vector128<short> above = Vector128.WidenLower(
                Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref aboveRow)).AsByte()).AsInt16();
            for (int row = 0; row < 8; row++)
            {
                ref byte line = ref Unsafe.Add(ref destination, row * stride);
                Vector128<short> delta = Vector128.Create((short)(Unsafe.Subtract(ref line, 1) - topLeft));
                Vector128<short> value = Vector128.Min(Vector128.Max(Vector128.Add(above, delta), zero), max);
                Unsafe.WriteUnaligned(ref line, Vector128.Narrow(value.AsUInt16(), value.AsUInt16()).AsUInt64().ToScalar());
            }
        }
    }

    internal static void PredictDcVector(ref byte destination, int stride, int size, bool hasTop, bool hasLeft,
        ref byte aboveRow)
    {
        int sum = 0;
        if (hasTop)
        {
            if (size == 16)
            {
                Vector128<byte> above = Vector128.LoadUnsafe(ref aboveRow);
                sum += (int)Vector128.Sum(Vector128.WidenLower(above))
                     + (int)Vector128.Sum(Vector128.WidenUpper(above));
            }
            else
            {
                Vector128<byte> above = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref aboveRow)).AsByte();
                sum += (int)Vector128.Sum(Vector128.WidenLower(above));
            }
        }

        if (hasLeft)
        {
            for (int i = 0; i < size; i++)
            {
                sum += Unsafe.Add(ref destination, i * stride - 1);
            }
        }

        int value = hasTop && hasLeft ? (sum + size) / (size * 2) :
            hasTop || hasLeft ? (sum + size / 2) / size : 128;
        Vector128<byte> fill = Vector128.Create((byte)value);
        ref byte row = ref destination;
        if (size == 16)
        {
            for (int i = 0; i < 16; i++)
            {
                fill.StoreUnsafe(ref row);
                row = ref Unsafe.Add(ref row, stride);
            }
        }
        else
        {
            ulong fill64 = fill.AsUInt64().ToScalar();
            for (int i = 0; i < 8; i++)
            {
                Unsafe.WriteUnaligned(ref row, fill64);
                row = ref Unsafe.Add(ref row, stride);
            }
        }
    }

    internal static void PredictLargeScalar(ref byte destination, int stride, int size, int mode, bool hasTop, bool hasLeft,
        ref byte aboveRow)
    {
        if (mode == 1)
        {
            for (int row = 0; row < size; row++)
            {
                for (int column = 0; column < size; column++)
                {
                    Unsafe.Add(ref destination, row * stride + column) = Unsafe.Add(ref aboveRow, column);
                }
            }

            return;
        }

        if (mode == 2)
        {
            for (int row = 0; row < size; row++)
            {
                ref byte line = ref Unsafe.Add(ref destination, row * stride);
                MemoryMarshal.CreateSpan(ref line, size).Fill(Unsafe.Subtract(ref line, 1));
            }

            return;
        }

        if (mode == 3)
        {
            int topLeft = Unsafe.Subtract(ref aboveRow, 1);
            for (int row = 0; row < size; row++)
            {
                ref byte line = ref Unsafe.Add(ref destination, row * stride);
                int left = Unsafe.Subtract(ref line, 1);
                for (int column = 0; column < size; column++)
                {
                    Unsafe.Add(ref line, column) = (byte)Math.Clamp(left + Unsafe.Add(ref aboveRow, column) - topLeft, 0, 255);
                }
            }

            return;
        }

        int sum = 0;
        for (int i = 0; i < size; i++)
        {
            if (hasTop) sum += Unsafe.Add(ref aboveRow, i);
            if (hasLeft) sum += Unsafe.Add(ref destination, i * stride - 1);
        }

        int value = hasTop && hasLeft ? (sum + size) / (size * 2) :
            hasTop || hasLeft ? (sum + size / 2) / size : 128;
        for (int row = 0; row < size; row++)
        {
            MemoryMarshal.CreateSpan(ref Unsafe.Add(ref destination, row * stride), size).Fill((byte)value);
        }
    }

    /// <summary>
    /// 4×4 luma prediction (RFC 6386 §12.3). Reads the 8 pixels above (the 4 above-right ones
    /// included), the 4 to the left and the top-left corner from around <paramref name="destination"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PredictSubblock(ref byte destination, int stride, int mode, ref byte aboveRow)
    {
        if (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported)
        {
            PredictSubblockVector(ref destination, stride, mode, ref aboveRow);
        }
        else
        {
            PredictSubblockScalar(ref destination, stride, mode, ref aboveRow);
        }
    }

    /// <summary>
    /// All ten 4×4 modes from one 16-byte edge vector E = [L3 L3 L2 L1 L0 P A0..A7 A7 A7] (left
    /// column bottom-up, top-left, above and above-right, the ends repeated as RFC 6386 §12.3's
    /// HU and LD modes do). Every directional output pixel is Avg3 centred on some E[k + 1]
    /// (vector a3[k]) or Avg2 of E[k], E[k + 1] (a2[k]); a per-mode byte shuffle of each picks
    /// the 16 outputs, with 0xFF marking a lane taken from the other vector.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void PredictSubblockVector(ref byte destination, int stride, int mode, ref byte aboveRow)
    {
        ulong left3 = Unsafe.Add(ref destination, 3 * stride - 1);
        ulong left2 = Unsafe.Add(ref destination, 2 * stride - 1);
        ulong left1 = Unsafe.Add(ref destination, stride - 1);
        ulong left0 = Unsafe.Subtract(ref destination, 1);
        // aboveRow[-1..6] = P A0..A6; aboveRow[7] = A7.
        ulong above8 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Subtract(ref aboveRow, 1));
        ulong a7 = Unsafe.Add(ref aboveRow, 7);
        if (mode == 0)
        {
            uint sum = 4 + (uint)(left0 + left1 + left2 + left3);
            uint top = (uint)(above8 >> 8);
            sum += (top & 0xFF) + ((top >> 8) & 0xFF) + ((top >> 16) & 0xFF) + (top >> 24);
            uint fill = (sum >> 3) * 0x01010101u;
            Unsafe.WriteUnaligned(ref destination, fill);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, stride), fill);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 2 * stride), fill);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 3 * stride), fill);
            return;
        }

        ulong lo = left3 | (left3 << 8) | (left2 << 16) | (left1 << 24) | (left0 << 32) | ((above8 & 0xFFFFFF) << 40);
        ulong hi = (above8 >> 24) | (a7 << 40) | (a7 << 48) | (a7 << 56);
        Vector128<byte> edge = Vector128.Create(lo, hi).AsByte();
        Vector128<byte> result;
        if (mode == 1)
        {
            // TrueMotion: clamp(L[row] + A[column] - P) in 16-bit lanes, two rows per vector.
            Vector128<byte> aboveRep = TableLookup(edge, Vector128.Create((byte)6, 7, 8, 9, 6, 7, 8, 9, 6, 7, 8, 9, 6, 7, 8, 9));
            Vector128<byte> leftRep = TableLookup(edge, Vector128.Create((byte)4, 4, 4, 4, 3, 3, 3, 3, 2, 2, 2, 2, 1, 1, 1, 1));
            Vector128<short> topLeft = Vector128.Create((short)(byte)(above8 & 0xFF));
            Vector128<short> zero = Vector128<short>.Zero;
            Vector128<short> max = Vector128.Create((short)255);
            Vector128<short> sumLo = Vector128.WidenLower(aboveRep).AsInt16() + Vector128.WidenLower(leftRep).AsInt16() - topLeft;
            Vector128<short> sumHi = Vector128.WidenUpper(aboveRep).AsInt16() + Vector128.WidenUpper(leftRep).AsInt16() - topLeft;
            sumLo = Vector128.Min(Vector128.Max(sumLo, zero), max);
            sumHi = Vector128.Min(Vector128.Max(sumHi, zero), max);
            result = Vector128.Narrow(sumLo.AsUInt16(), sumHi.AsUInt16());
        }
        else
        {
            Vector128<byte> next = TableLookup(edge, Vector128.Create((byte)1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 15));
            Vector128<byte> next2 = TableLookup(edge, Vector128.Create((byte)2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 15, 15));
            Vector128<byte> avg2 = RoundingAverage(edge, next);
            // (a + 2b + c + 2) >> 2 == avg(b, floor((a + c) / 2)): when a + c is odd, 2b + a + c + 1
            // is even and adding 1 more cannot reach a multiple of 4.
            Vector128<byte> outer = RoundingAverage(edge, next2) - ((edge ^ next2) & Vector128.Create((byte)1));
            Vector128<byte> avg3 = RoundingAverage(next, outer);
            ref byte table = ref Unsafe.Add(ref MemoryMarshal.GetReference(SubblockShuffles), (mode - 2) * 32);
            result = TableLookup(avg3, Vector128.LoadUnsafe(ref table))
                   | TableLookup(avg2, Vector128.LoadUnsafe(ref Unsafe.Add(ref table, 16)));
        }

        Vector128<uint> rows = result.AsUInt32();
        Unsafe.WriteUnaligned(ref destination, rows.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, stride), rows.GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 2 * stride), rows.GetElement(2));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 3 * stride), rows.GetElement(3));
    }

    // Per mode 2-9: 16 indices into a3 then 16 into a2 (see PredictSubblockVector); 0xFF = not this one.
    private static ReadOnlySpan<byte> SubblockShuffles =>
    [
        // 2 VE
        5, 6, 7, 8, 5, 6, 7, 8, 5, 6, 7, 8, 5, 6, 7, 8,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        // 3 HE
        3, 3, 3, 3, 2, 2, 2, 2, 1, 1, 1, 1, 0, 0, 0, 0,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        // 4 LD
        6, 7, 8, 9, 7, 8, 9, 10, 8, 9, 10, 11, 9, 10, 11, 12,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        // 5 RD
        4, 5, 6, 7, 3, 4, 5, 6, 2, 3, 4, 5, 1, 2, 3, 4,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        // 6 VR
        255, 255, 255, 255, 4, 5, 6, 7, 3, 255, 255, 255, 2, 4, 5, 6,
        5, 6, 7, 8, 255, 255, 255, 255, 255, 5, 6, 7, 255, 255, 255, 255,
        // 7 VL
        255, 255, 255, 255, 6, 7, 8, 9, 255, 255, 255, 10, 7, 8, 9, 11,
        6, 7, 8, 9, 255, 255, 255, 255, 7, 8, 9, 255, 255, 255, 255, 255,
        // 8 HD
        255, 4, 5, 6, 255, 3, 255, 4, 255, 2, 255, 3, 255, 1, 255, 2,
        4, 255, 255, 255, 3, 255, 4, 255, 2, 255, 3, 255, 1, 255, 2, 255,
        // 9 HU
        255, 2, 255, 1, 255, 1, 255, 0, 255, 0, 255, 255, 255, 255, 255, 255,
        3, 255, 2, 255, 2, 255, 1, 255, 1, 255, 0, 0, 0, 0, 0, 0,
    ];

    // Byte table lookup where an index of 0xFF (>= 16) yields 0: pshufb and tbl both do this.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> TableLookup(Vector128<byte> table, Vector128<byte> indices)
    {
        if (Ssse3.IsSupported)
        {
            return Ssse3.Shuffle(table, indices);
        }

        return AdvSimd.Arm64.VectorTableLookup(table, indices);
    }

    // (a + b + 1) >> 1 per byte.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> RoundingAverage(Vector128<byte> a, Vector128<byte> b)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.Average(a, b);
        }

        return AdvSimd.FusedAddRoundedHalving(a, b);
    }

    internal static void PredictSubblockScalar(ref byte destination, int stride, int mode, ref byte aboveRow)
    {
        Span<byte> above = stackalloc byte[8];
        Span<byte> left = stackalloc byte[4];
        Span<byte> edge = stackalloc byte[9];
        for (int i = 0; i < 8; i++)
        {
            above[i] = Unsafe.Add(ref aboveRow, i);
        }

        for (int i = 0; i < 4; i++)
        {
            left[i] = Unsafe.Add(ref destination, i * stride - 1);
        }

        byte p = Unsafe.Subtract(ref aboveRow, 1);
        edge[0] = left[3];
        edge[1] = left[2];
        edge[2] = left[1];
        edge[3] = left[0];
        edge[4] = p;
        for (int i = 0; i < 4; i++)
        {
            edge[5 + i] = above[i];
        }

        // The mode is constant per 4x4 block: dispatch once instead of inside the per-pixel loop.
        switch (mode)
        {
            case 0:
            {
                byte value = (byte)SubblockDc(above, left);
                for (int row = 0; row < 4; row++)
                {
                    MemoryMarshal.CreateSpan(ref Unsafe.Add(ref destination, row * stride), 4).Fill(value);
                }

                break;
            }
            case 1:
            {
                for (int row = 0; row < 4; row++)
                {
                    int leftValue = left[row];
                    for (int column = 0; column < 4; column++)
                    {
                        Unsafe.Add(ref destination, row * stride + column) = (byte)Clamp(leftValue + above[column] - p);
                    }
                }

                break;
            }
            case 2:
            {
                for (int row = 0; row < 4; row++)
                {
                    for (int column = 0; column < 4; column++)
                    {
                        Unsafe.Add(ref destination, row * stride + column) = (byte)Avg3(
                            column == 0 ? p : above[column - 1], above[column], above[column + 1]);
                    }
                }

                break;
            }
            case 3:
            {
                for (int row = 0; row < 4; row++)
                {
                    int previous = row == 0 ? p : left[row - 1];
                    int next = left[Math.Min(row + 1, 3)];
                    int current = left[row];
                    byte value = (byte)Avg3(previous, current, next);
                    MemoryMarshal.CreateSpan(ref Unsafe.Add(ref destination, row * stride), 4).Fill(value);
                }

                break;
            }
            case 4:
            {
                for (int row = 0; row < 4; row++)
                {
                    for (int column = 0; column < 4; column++)
                    {
                        Unsafe.Add(ref destination, row * stride + column) = (byte)LeftDown(above, row + column + 1);
                    }
                }

                break;
            }
            case 5:
            {
                for (int row = 0; row < 4; row++)
                {
                    for (int column = 0; column < 4; column++)
                    {
                        Unsafe.Add(ref destination, row * stride + column) = (byte)Avg3(
                            edge[4 + column - row - 1], edge[4 + column - row], edge[4 + column - row + 1]);
                    }
                }

                break;
            }
            case 6:
            {
                for (int row = 0; row < 4; row++)
                {
                    for (int column = 0; column < 4; column++)
                    {
                        Unsafe.Add(ref destination, row * stride + column) = (byte)VerticalRight(edge, row, column);
                    }
                }

                break;
            }
            case 7:
            {
                for (int row = 0; row < 4; row++)
                {
                    for (int column = 0; column < 4; column++)
                    {
                        Unsafe.Add(ref destination, row * stride + column) = (byte)VerticalLeft(above, row, column);
                    }
                }

                break;
            }
            case 8:
            {
                for (int row = 0; row < 4; row++)
                {
                    for (int column = 0; column < 4; column++)
                    {
                        Unsafe.Add(ref destination, row * stride + column) = (byte)HorizontalDown(edge, row, column);
                    }
                }

                break;
            }
            default:
            {
                for (int row = 0; row < 4; row++)
                {
                    for (int column = 0; column < 4; column++)
                    {
                        Unsafe.Add(ref destination, row * stride + column) = (byte)HorizontalUp(left, row, column);
                    }
                }

                break;
            }
        }
    }

    private static int SubblockDc(ReadOnlySpan<byte> above, ReadOnlySpan<byte> left)
    {
        int sum = 4;
        for (int i = 0; i < 4; i++) sum += above[i] + left[i];
        return sum >> 3;
    }

    private static int LeftDown(ReadOnlySpan<byte> above, int index) =>
        index < 7 ? Avg3(above[index - 1], above[index], above[index + 1]) : Avg3(above[6], above[7], above[7]);

    private static int VerticalRight(ReadOnlySpan<byte> edge, int row, int column)
    {
        if (row == 0) return Avg2(edge[4 + column], edge[5 + column]);
        if (row == 1) return Avg3(edge[3 + column], edge[4 + column], edge[5 + column]);
        if (row == 2) return column == 0 ? Avg3(edge[2], edge[3], edge[4]) : Avg2(edge[3 + column], edge[4 + column]);
        return Avg3(edge[column == 0 ? 1 : 2 + column], edge[column == 0 ? 2 : 3 + column], edge[column == 0 ? 3 : 4 + column]);
    }

    private static int VerticalLeft(ReadOnlySpan<byte> above, int row, int column) =>
        row switch
        {
            0 => Avg2(above[column], above[column + 1]),
            1 => Avg3(above[column], above[column + 1], above[column + 2]),
            2 when column < 3 => Avg2(above[column + 1], above[column + 2]),
            2 => Avg3(above[4], above[5], above[6]),
            3 => column == 3
                ? Avg3(above[5], above[6], above[7])
                : Avg3(above[column + 1], above[column + 2], above[column + 3]),
            _ => 0,
        };

    private static int HorizontalDown(ReadOnlySpan<byte> edge, int row, int column)
    {
        int index = row switch
        {
            0 => column switch { 0 => 3, 1 => 4, 2 => 5, _ => 6 },
            1 => column switch { 0 => 2, 1 => 3, 2 => 3, _ => 4 },
            2 => column switch { 0 => 1, 1 => 2, 2 => 2, _ => 3 },
            _ => column switch { 0 => 0, 1 => 1, 2 => 1, _ => 2 },
        };
        bool half = (row == 0 && column == 0) ||
                    (row == 1 && (column == 0 || column == 2)) ||
                    (row == 2 && (column == 0 || column == 2)) ||
                    (row == 3 && (column == 0 || column == 2));
        return half ? Avg2(edge[index], edge[index + 1]) : Avg3(edge[index - 1], edge[index], edge[index + 1]);
    }

    private static int HorizontalUp(ReadOnlySpan<byte> left, int row, int column)
    {
        if (row == 3 || (row == 2 && column >= 2)) return left[3];
        if (row == 0)
        {
            return column switch { 0 => Avg2(left[0], left[1]), 1 => Avg3(left[0], left[1], left[2]), 2 => Avg2(left[1], left[2]), _ => Avg3(left[1], left[2], left[3]) };
        }

        if (row == 1)
        {
            return column switch { 0 => Avg2(left[1], left[2]), 1 => Avg3(left[1], left[2], left[3]), 2 => Avg2(left[2], left[3]), _ => Avg3(left[2], left[3], left[3]) };
        }

        return column == 0 ? Avg2(left[2], left[3]) : Avg3(left[2], left[3], left[3]);
    }

    private static int Avg2(int a, int b) => (a + b + 1) >> 1;
    private static int Avg3(int a, int b, int c) => (a + 2 * b + c + 2) >> 2;
    private static int Clamp(int value) => Math.Clamp(value, 0, 255);

    /// <summary>
    /// Loop-filters one macroblock row in the row cache (RFC 6386 §15), macroblock by macroblock in
    /// the order the whole-frame filter uses: left edge, inner vertical edges, top edge, inner
    /// horizontal edges. Top edges reach into the previous row, still in the cache above.
    /// <paramref name="rowFilter"/> holds (level, filter-inner-edges) per macroblock.
    /// </summary>
    private static void FilterMacroblockRow(byte[] y, int yStride, byte[] u, byte[] v, int uvStride,
        int mbWidth, int mbY, FilterSettings filter, byte[] rowFilter)
    {
        int yRow = LumaCacheExtra;
        int uvY = ChromaCacheExtra;
        for (int mbX = 0; mbX < mbWidth; mbX++)
        {
            int level = rowFilter[mbX * 2];
            if (level == 0) continue;
            int interior = level;
            if (filter.Sharpness != 0)
            {
                interior >>= filter.Sharpness > 4 ? 2 : 1;
                interior = Math.Min(interior, 9 - filter.Sharpness);
            }

            interior = Math.Max(interior, 1);
            int edgeLimit = 2 * (level + 2) + interior;
            int subLimit = 2 * level + interior;
            int hev = level >= 40 ? 2 : level >= 15 ? 1 : 0;
            bool skipInner = rowFilter[mbX * 2 + 1] == 0;
            int x = CacheLeft + mbX * 16;
            int uvX = CacheLeft + mbX * 8;
            if (Vp8LoopFilter.IsSupported)
            {
                FilterMacroblock(ref y[yRow * yStride + x], yStride, ref u[uvY * uvStride + uvX], ref v[uvY * uvStride + uvX], uvStride,
                    mbX > 0, mbY > 0, !skipInner, filter.Simple, interior, edgeLimit, subLimit, hev);
                continue;
            }

            if (mbX > 0)
            {
                FilterEdge(y, yStride, x, yRow, 16, true, true, filter.Simple, interior, edgeLimit, hev);
                if (!filter.Simple)
                {
                    FilterEdge(u, uvStride, uvX, uvY, 8, true, true, false, interior, edgeLimit, hev);
                    FilterEdge(v, uvStride, uvX, uvY, 8, true, true, false, interior, edgeLimit, hev);
                }
            }

            if (!skipInner)
            {
                for (int edge = 4; edge < 16; edge += 4)
                {
                    FilterEdge(y, yStride, x + edge, yRow, 16, true, false, filter.Simple, interior, subLimit, hev);
                }

                if (!filter.Simple)
                {
                    FilterEdge(u, uvStride, uvX + 4, uvY, 8, true, false, false, interior, subLimit, hev);
                    FilterEdge(v, uvStride, uvX + 4, uvY, 8, true, false, false, interior, subLimit, hev);
                }
            }

            if (mbY > 0)
            {
                FilterEdge(y, yStride, x, yRow, 16, false, true, filter.Simple, interior, edgeLimit, hev);
                if (!filter.Simple)
                {
                    FilterEdge(u, uvStride, uvX, uvY, 8, false, true, false, interior, edgeLimit, hev);
                    FilterEdge(v, uvStride, uvX, uvY, 8, false, true, false, interior, edgeLimit, hev);
                }
            }

            if (!skipInner)
            {
                for (int edge = 4; edge < 16; edge += 4)
                {
                    FilterEdge(y, yStride, x, yRow + edge, 16, false, false, filter.Simple, interior, subLimit, hev);
                }

                if (!filter.Simple)
                {
                    FilterEdge(u, uvStride, uvX, uvY + 4, 8, false, false, false, interior, subLimit, hev);
                    FilterEdge(v, uvStride, uvX, uvY + 4, 8, false, false, false, interior, subLimit, hev);
                }
            }
        }
    }

    /// <summary>One macroblock's edges through the 16-lane kernels, in the frame filter's order.</summary>
    private static void FilterMacroblock(ref byte y, int yStride, ref byte u, ref byte v, int uvStride,
        bool left, bool top, bool inner, bool simple, int interior, int edgeLimit, int subLimit, int hev)
    {
        Vp8LoopFilter.Kind edgeKind = simple ? Vp8LoopFilter.Kind.Simple : Vp8LoopFilter.Kind.Macroblock;
        Vp8LoopFilter.Kind innerKind = simple ? Vp8LoopFilter.Kind.Simple : Vp8LoopFilter.Kind.Inner;
        if (left)
        {
            Vp8LoopFilter.LumaVertical(ref y, yStride, edgeKind, edgeLimit, interior, hev);
            if (!simple)
            {
                Vp8LoopFilter.ChromaVertical(ref u, ref v, uvStride, edgeKind, edgeLimit, interior, hev);
            }
        }

        if (inner)
        {
            Vp8LoopFilter.LumaVertical(ref Unsafe.Add(ref y, 4), yStride, innerKind, subLimit, interior, hev);
            Vp8LoopFilter.LumaVertical(ref Unsafe.Add(ref y, 8), yStride, innerKind, subLimit, interior, hev);
            Vp8LoopFilter.LumaVertical(ref Unsafe.Add(ref y, 12), yStride, innerKind, subLimit, interior, hev);
            if (!simple)
            {
                Vp8LoopFilter.ChromaVertical(ref Unsafe.Add(ref u, 4), ref Unsafe.Add(ref v, 4), uvStride, innerKind, subLimit, interior, hev);
            }
        }

        if (top)
        {
            Vp8LoopFilter.LumaHorizontal(ref y, yStride, edgeKind, edgeLimit, interior, hev);
            if (!simple)
            {
                Vp8LoopFilter.ChromaHorizontal(ref u, ref v, uvStride, edgeKind, edgeLimit, interior, hev);
            }
        }

        if (inner)
        {
            Vp8LoopFilter.LumaHorizontal(ref Unsafe.Add(ref y, 4 * yStride), yStride, innerKind, subLimit, interior, hev);
            Vp8LoopFilter.LumaHorizontal(ref Unsafe.Add(ref y, 8 * yStride), yStride, innerKind, subLimit, interior, hev);
            Vp8LoopFilter.LumaHorizontal(ref Unsafe.Add(ref y, 12 * yStride), yStride, innerKind, subLimit, interior, hev);
            if (!simple)
            {
                Vp8LoopFilter.ChromaHorizontal(ref Unsafe.Add(ref u, 4 * uvStride), ref Unsafe.Add(ref v, 4 * uvStride), uvStride,
                    innerKind, subLimit, interior, hev);
            }
        }
    }

    private static void FilterEdge(byte[] plane, int stride, int x, int y, int length,
        bool vertical, bool macroblockEdge, bool simple, int interior, int edge, int hev)
    {
        ref byte center = ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(plane), y * stride + x);
        // Scalar reference path (no 16-lane kernels on this machine).
        int sampleStep = vertical ? 1 : stride;
        int pixelAdvance = vertical ? stride : 1;
        if (simple)
        {
            FilterSimpleScalar(ref center, sampleStep, pixelAdvance, length, edge);
        }
        else if (macroblockEdge)
        {
            FilterNormalMacroblockScalar(ref center, sampleStep, pixelAdvance, length, interior, edge, hev);
        }
        else
        {
            FilterNormalInnerScalar(ref center, sampleStep, pixelAdvance, length, interior, edge, hev);
        }
    }

    internal static void FilterVerticalScalar(
        byte[] plane, int stride, int x, int y, int length, bool simple,
        bool macroblockEdge, int interior, int edge, int hev)
    {
        ref byte center = ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(plane), y * stride + x);
        FilterVerticalScalar(ref center, stride, length, simple, macroblockEdge, interior, edge, hev);
    }

    private static void FilterVerticalScalar(
        ref byte center, int stride, int length, bool simple,
        bool macroblockEdge, int interior, int edge, int hev)
    {
        if (simple)
        {
            FilterSimpleScalar(ref center, 1, stride, length, edge);
        }
        else if (macroblockEdge)
        {
            FilterNormalMacroblockScalar(ref center, 1, stride, length, interior, edge, hev);
        }
        else
        {
            FilterNormalInnerScalar(ref center, 1, stride, length, interior, edge, hev);
        }
    }

    internal static void FilterHorizontalScalar(
        byte[] plane, int stride, int x, int y, int length, bool simple,
        bool macroblockEdge, int interior, int edge, int hev)
    {
        ref byte center = ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(plane), y * stride + x);
        if (simple)
        {
            FilterSimpleScalar(ref center, stride, 1, length, edge);
        }
        else if (macroblockEdge)
        {
            FilterNormalMacroblockScalar(ref center, stride, 1, length, interior, edge, hev);
        }
        else
        {
            FilterNormalInnerScalar(ref center, stride, 1, length, interior, edge, hev);
        }
    }

    private static void FilterSimpleScalar(
        ref byte center, int sampleStep, int pixelAdvance, int length, int edge)
    {
        for (int i = 0; i < length; i++)
        {
            ref byte sample = ref Unsafe.Add(ref center, i * pixelAdvance);
            int p1 = Unsafe.Add(ref sample, -2 * sampleStep);
            int p0 = Unsafe.Add(ref sample, -sampleStep);
            int q0 = sample;
            int q1 = Unsafe.Add(ref sample, sampleStep);
            if (Math.Abs(p0 - q0) * 2 + Math.Abs(p1 - q1) / 2 > edge)
            {
                continue;
            }

            int adjustment = ClampSigned(ClampSigned(p1 - q1) + 3 * (q0 - p0));
            int qAdjustment = FilterTap(adjustment, 4);
            int pAdjustment = FilterTap(adjustment, 3);
            Unsafe.Add(ref sample, -sampleStep) = (byte)Math.Clamp(p0 + pAdjustment, 0, 255);
            sample = (byte)Math.Clamp(q0 - qAdjustment, 0, 255);
        }
    }

    private static void FilterNormalMacroblockScalar(
        ref byte center, int sampleStep, int pixelAdvance, int length,
        int interior, int edge, int hev)
    {
        for (int i = 0; i < length; i++)
        {
            ref byte sample = ref Unsafe.Add(ref center, i * pixelAdvance);
            int p3 = Unsafe.Add(ref sample, -4 * sampleStep);
            int p2 = Unsafe.Add(ref sample, -3 * sampleStep);
            int p1 = Unsafe.Add(ref sample, -2 * sampleStep);
            int p0 = Unsafe.Add(ref sample, -sampleStep);
            int q0 = sample;
            int q1 = Unsafe.Add(ref sample, sampleStep);
            int q2 = Unsafe.Add(ref sample, 2 * sampleStep);
            int q3 = Unsafe.Add(ref sample, 3 * sampleStep);
            if (Math.Abs(p0 - q0) * 2 + Math.Abs(p1 - q1) / 2 > edge ||
                Math.Abs(p3 - p2) > interior || Math.Abs(p2 - p1) > interior ||
                Math.Abs(p1 - p0) > interior || Math.Abs(q1 - q0) > interior ||
                Math.Abs(q2 - q1) > interior || Math.Abs(q3 - q2) > interior)
            {
                continue;
            }

            bool highVariance = Math.Abs(p1 - p0) > hev || Math.Abs(q1 - q0) > hev;
            int adjustment = ClampSigned(ClampSigned(p1 - q1) + 3 * (q0 - p0));
            if (highVariance)
            {
                int qAdjustment = FilterTap(adjustment, 4);
                int pAdjustment = FilterTap(adjustment, 3);
                Unsafe.Add(ref sample, -sampleStep) = (byte)Math.Clamp(p0 + pAdjustment, 0, 255);
                sample = (byte)Math.Clamp(q0 - qAdjustment, 0, 255);
                continue;
            }

            int outer = (27 * adjustment + 63) >> 7;
            int middle = (18 * adjustment + 63) >> 7;
            int inner = (9 * adjustment + 63) >> 7;
            Unsafe.Add(ref sample, -sampleStep) = (byte)Math.Clamp(p0 + outer, 0, 255);
            sample = (byte)Math.Clamp(q0 - outer, 0, 255);
            Unsafe.Add(ref sample, -2 * sampleStep) = (byte)Math.Clamp(p1 + middle, 0, 255);
            Unsafe.Add(ref sample, sampleStep) = (byte)Math.Clamp(q1 - middle, 0, 255);
            Unsafe.Add(ref sample, -3 * sampleStep) = (byte)Math.Clamp(p2 + inner, 0, 255);
            Unsafe.Add(ref sample, 2 * sampleStep) = (byte)Math.Clamp(q2 - inner, 0, 255);
        }
    }

    private static void FilterNormalInnerScalar(
        ref byte center, int sampleStep, int pixelAdvance, int length,
        int interior, int edge, int hev)
    {
        for (int i = 0; i < length; i++)
        {
            ref byte sample = ref Unsafe.Add(ref center, i * pixelAdvance);
            int p3 = Unsafe.Add(ref sample, -4 * sampleStep);
            int p2 = Unsafe.Add(ref sample, -3 * sampleStep);
            int p1 = Unsafe.Add(ref sample, -2 * sampleStep);
            int p0 = Unsafe.Add(ref sample, -sampleStep);
            int q0 = sample;
            int q1 = Unsafe.Add(ref sample, sampleStep);
            int q2 = Unsafe.Add(ref sample, 2 * sampleStep);
            int q3 = Unsafe.Add(ref sample, 3 * sampleStep);
            if (Math.Abs(p0 - q0) * 2 + Math.Abs(p1 - q1) / 2 > edge ||
                Math.Abs(p3 - p2) > interior || Math.Abs(p2 - p1) > interior ||
                Math.Abs(p1 - p0) > interior || Math.Abs(q1 - q0) > interior ||
                Math.Abs(q2 - q1) > interior || Math.Abs(q3 - q2) > interior)
            {
                continue;
            }

            bool highVariance = Math.Abs(p1 - p0) > hev || Math.Abs(q1 - q0) > hev;
            int adjustment = highVariance
                ? ClampSigned(ClampSigned(p1 - q1) + 3 * (q0 - p0))
                : ClampSigned(3 * (q0 - p0));
            int qAdjustment = FilterTap(adjustment, 4);
            int pAdjustment = FilterTap(adjustment, 3);
            Unsafe.Add(ref sample, -sampleStep) = (byte)Math.Clamp(p0 + pAdjustment, 0, 255);
            sample = (byte)Math.Clamp(q0 - qAdjustment, 0, 255);
            if (highVariance)
            {
                continue;
            }

            int inner = (qAdjustment + 1) >> 1;
            Unsafe.Add(ref sample, -2 * sampleStep) = (byte)Math.Clamp(p1 + inner, 0, 255);
            Unsafe.Add(ref sample, sampleStep) = (byte)Math.Clamp(q1 - inner, 0, 255);
        }
    }

    private static int ClampSigned(int value) => Math.Clamp(value, -128, 127);

    // RFC 6386 15.2: the biased adjustment is clamped to int8 BEFORE the shift. Without the clamp,
    // adjustment >= 124 (+4) or >= 125 (+3) shifts to 16 instead of 15.
    private static int FilterTap(int adjustment, int bias) => ClampSigned(adjustment + bias) >> 3;

    // adjustment is already in [-128, 127], so adding a positive bias can only overflow upwards.

    private readonly record struct SegmentFeatures(bool Enabled, bool UpdateMap, bool Absolute,
        int[] Quant, int[] Filter, byte[] MapProbabilities);
    private readonly record struct FilterSettings(bool Simple, int Level, int Sharpness,
        bool DeltaEnabled, sbyte[] RefDeltas, sbyte[] ModeDeltas);
    private readonly record struct Quantizers(int Base, int YDcDelta, int Y2DcDelta,
        int Y2AcDelta, int UvDcDelta, int UvAcDelta);

    private readonly ref struct PartitionSlices
    {
        public PartitionSlices(ReadOnlySpan<byte> p0, ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2,
            ReadOnlySpan<byte> p3, ReadOnlySpan<byte> p4, ReadOnlySpan<byte> p5,
            ReadOnlySpan<byte> p6, ReadOnlySpan<byte> p7)
        {
            P0 = p0;
            P1 = p1;
            P2 = p2;
            P3 = p3;
            P4 = p4;
            P5 = p5;
            P6 = p6;
            P7 = p7;
        }

        public ReadOnlySpan<byte> P0 { get; }
        public ReadOnlySpan<byte> P1 { get; }
        public ReadOnlySpan<byte> P2 { get; }
        public ReadOnlySpan<byte> P3 { get; }
        public ReadOnlySpan<byte> P4 { get; }
        public ReadOnlySpan<byte> P5 { get; }
        public ReadOnlySpan<byte> P6 { get; }
        public ReadOnlySpan<byte> P7 { get; }
    }
}
