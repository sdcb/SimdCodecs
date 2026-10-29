using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Sdcb.SimdCodecs.Jpeg.JpegEncoderTables;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Pure-managed JPEG (ITU-T T.81) encoder: baseline sequential DCT with the Annex K Huffman
/// tables (default), optimised per-image Huffman tables (<see cref="JpegEncodeOptions.OptimizeHuffman"/>),
/// or progressive DCT with spectral selection and successive approximation
/// (<see cref="JpegEncodeOptions.Progressive"/>). Colour input is written as YCbCr 4:4:4 or 4:2:0,
/// grey input as a single-component JPEG; quality 1..100 scales the Annex K quantisation tables.
///
/// <para>
/// The pipeline runs one MCU row at a time: interleaved samples → level-shifted Y / Cb / Cr
/// float strips (T.871) → optional 2×2 chroma averaging → AAN forward DCT with the quantiser
/// folded into its scale factors → entropy coding of each block straight after its transform.
/// The kernels are Vector256 / Vector128 / scalar and produce identical output on every
/// instruction set (<see cref="JpegEncoderKernels"/>).
/// </para>
/// </summary>
public static class JpegEncoder
{
    /// <summary>
    /// Encodes an interleaved 8-bit raster to a JPEG byte stream.
    /// </summary>
    /// <param name="pixels">Interleaved, row-major, tightly-packed 8-bit samples,
    /// <c>width * height * channels</c> bytes.</param>
    /// <param name="width">Pixel width, 1..65535.</param>
    /// <param name="height">Pixel height, 1..65535.</param>
    /// <param name="channels">Samples per pixel: 1 = grayscale, 2 = gray+alpha
    /// (alpha ignored), 3 = RGB, 4 = RGBA (alpha ignored). Grey input (1 or 2) is written as a
    /// single-component JPEG; RGB(A) as three-component YCbCr.</param>
    /// <param name="options">Quality, subsampling and coding mode; null uses the defaults (quality 90, baseline).</param>
    /// <returns>A complete JPEG byte stream (SOI … EOI).</returns>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> is too small for the geometry.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension, channel count, or quality is out of range.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int channels, JpegEncodeOptions? options = null)
    {
        if (width is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be 1..65535.");
        if (height is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be 1..65535.");
        if (channels is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be 1..4.");
        options ??= new JpegEncodeOptions();
        if (options.Quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(options), options.Quality, "Quality must be 1..100.");

        var exif = ValidateExif(options.Exif);
        var xmp = options.Xmp is { } xmpOptions ? BuildXmpPacket(xmpOptions) : default;
        var icc = ValidateIcc(options.IccProfile);

        var required = (long)width * height * channels;
        if (pixels.Length < required)
            throw new ArgumentException($"Pixel buffer too small: {pixels.Length} < {required} (width*height*channels).", nameof(pixels));

        var gray = channels <= 2;
        var subsample = !gray && options.Subsampling switch
        {
            JpegSubsampling.Chroma444 => false,
            JpegSubsampling.Chroma420 => true,
            _ => options.Quality <= 90,
        };

        // libjpeg-style quality → quantiser scale percentage.
        var q = options.Quality;
        var scale = q < 50 ? 5000 / q : 200 - q * 2;
        var lumaQuant = ScaleQuant(LumaQuant, scale);
        var chromaQuant = ScaleQuant(ChromaQuant, scale);

        var frame = new Frame(width, height, gray, subsample);
        frame.Components[0].Recip = Reciprocals(lumaQuant);
        for (var c = 1; c < frame.Components.Length; c++) frame.Components[c].Recip = Reciprocals(chromaQuant);

        var stored = options.Progressive || options.OptimizeHuffman;
        var w = new JpegBitWriter(EstimateCapacity(width, height, channels));
        try
        {
            WriteHeaders(ref w, exif.Span, xmp, icc.Span, lumaQuant, gray ? null : chromaQuant);
            if (!stored)
            {
                WriteFrame(ref w, frame, progressive: false);
                var (dcL, acL, dcC, acC) = (StdDcLuma, StdAcLuma, StdDcChroma, StdAcChroma);
                WriteHuffmanTables(ref w, gray ? [(0, 0, dcL), (1, 0, acL)] : [(0, 0, dcL), (1, 0, acL), (0, 1, dcC), (1, 1, acC)]);
                WriteBaselineScanHeader(ref w, frame);
                Transform(pixels, channels, frame, ref w, [dcL, dcC], [acL, acC]);
            }
            else
            {
                foreach (var comp in frame.Components)
                    comp.Coefficients = ArrayPool<short>.Shared.Rent(checked(comp.BlocksW * comp.BlocksH * 64));
                Transform(pixels, channels, frame, ref w, null, null);

                if (options.Progressive)
                {
                    WriteFrame(ref w, frame, progressive: true);
                    JpegProgressiveEncoder.Encode(ref w, frame, gray ? JpegProgressiveEncoder.GrayScript : JpegProgressiveEncoder.ColorScript);
                }
                else
                {
                    WriteFrame(ref w, frame, progressive: false);
                    EncodeOptimizedBaseline(ref w, frame);
                }
            }

            w.FlushBits();
            w.WriteByte(0xFF);
            w.WriteByte(0xD9);
            return w.ToArrayAndReturn();
        }
        finally
        {
            if (w.Buffer.Length > 0) ArrayPool<byte>.Shared.Return(w.Buffer);
            foreach (var comp in frame.Components)
            {
                if (comp.Coefficients is { } coefficients) ArrayPool<short>.Shared.Return(coefficients);
            }
        }
    }

    // -------------------------------------------------------------- frame geometry

    internal sealed class Component(byte id, int h, int v, int tableId)
    {
        public readonly byte Id = id;
        public readonly int H = h, V = v;

        /// <summary>0 for luma, 1 for chroma: selects quantisation and Huffman table.</summary>
        public readonly int TableId = tableId;

        /// <summary>Blocks per row / column of the MCU-padded grid (what interleaved scans cover).</summary>
        public int BlocksW, BlocksH;

        /// <summary>Blocks a non-interleaved scan of this component covers (T.81 A.2.2).</summary>
        public int ScanBlocksW, ScanBlocksH;

        public float[] Recip = [];
        public short[]? Coefficients;

        public Span<short> Block(int row, int col) => Coefficients.AsSpan((row * BlocksW + col) * 64, 64);
    }

    internal sealed class Frame
    {
        public readonly int Width, Height, HMax, VMax, McusX, McusY;
        public readonly Component[] Components;

        public Frame(int width, int height, bool gray, bool subsample)
        {
            Width = width;
            Height = height;
            Components = gray
                ? [new Component(1, 1, 1, 0)]
                : [new Component(1, subsample ? 2 : 1, subsample ? 2 : 1, 0), new Component(2, 1, 1, 1), new Component(3, 1, 1, 1)];
            HMax = VMax = subsample ? 2 : 1;
            McusX = (width + 8 * HMax - 1) / (8 * HMax);
            McusY = (height + 8 * VMax - 1) / (8 * VMax);
            foreach (var c in Components)
            {
                c.BlocksW = McusX * c.H;
                c.BlocksH = McusY * c.V;
                c.ScanBlocksW = ((width * c.H + HMax - 1) / HMax + 7) / 8;
                c.ScanBlocksH = ((height * c.V + VMax - 1) / VMax + 7) / 8;
            }
        }
    }

    // -------------------------------------------------------------- tables

    // The Annex K example Huffman tables, built once (their AcCombined tables are 128 KiB each).
    private static readonly JpegHuffmanEncodeTable StdDcLuma = JpegHuffmanEncodeTable.FromSpec(DcLumaBits, DcLumaValues);
    private static readonly JpegHuffmanEncodeTable StdAcLuma = JpegHuffmanEncodeTable.FromSpec(AcLumaBits, AcLumaValues);
    private static readonly JpegHuffmanEncodeTable StdDcChroma = JpegHuffmanEncodeTable.FromSpec(DcChromaBits, DcChromaValues);
    private static readonly JpegHuffmanEncodeTable StdAcChroma = JpegHuffmanEncodeTable.FromSpec(AcChromaBits, AcChromaValues);

    private static byte[] ScaleQuant(ReadOnlySpan<byte> basis, int scale)
    {
        var t = new byte[64];
        for (var i = 0; i < 64; i++) t[i] = (byte)Math.Clamp((basis[i] * scale + 50) / 100, 1, 255);
        return t;
    }

    // AAN scale factors, s[k] = cos(kπ/16)·√2 for k > 0 and 1 for k = 0, times √8 (the DCT's 1/8 in 2-D).
    private static ReadOnlySpan<float> AanScale => [1.0f, 1.387039845f, 1.306562965f, 1.175875602f, 1.0f, 0.785694958f, 0.541196100f, 0.275899379f];

    /// <summary>1 / (quantiser × AAN scale) in the kernel's transposed layout <c>[u * 8 + v]</c>.</summary>
    private static float[] Reciprocals(byte[] quant)
    {
        var r = new float[64];
        for (var v = 0; v < 8; v++)
        {
            for (var u = 0; u < 8; u++)
                r[u * 8 + v] = 1f / (quant[v * 8 + u] * (AanScale[v] * 2.828427125f) * (AanScale[u] * 2.828427125f));
        }

        return r;
    }

    private static int EstimateCapacity(int width, int height, int channels) =>
        (int)Math.Min(64 * 1024 + (long)width * height * (channels <= 2 ? 1 : 3) / 4, int.MaxValue / 2);

    // -------------------------------------------------------------- transform pipeline

    /// <summary>MCU-row strips are processed in column chunks of this many pixels, so the float planes stay cache-resident.</summary>
    private const int ChunkWidth = 2048;

    /// <summary>
    /// Runs colour conversion and the forward DCT over the whole image, one MCU row at a time
    /// in chunks of <see cref="ChunkWidth"/> columns. With Huffman tables given, every block is
    /// entropy coded straight after its transform; otherwise the quantised coefficients are
    /// stored in each component's grid.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Transform(ReadOnlySpan<byte> pixels, int channels, Frame f, ref JpegBitWriter w,
        JpegHuffmanEncodeTable[]? dcTables, JpegHuffmanEncodeTable[]? acTables)
    {
        var comps = f.Components;
        var gray = comps.Length == 1;
        var subsample = f.HMax == 2;
        var rows = 8 * f.VMax;
        var mcuWidth = 8 * f.HMax;
        var chunkMcus = ChunkWidth / mcuWidth;
        const int stride = ChunkWidth;
        const int halfStride = ChunkWidth / 2;
        var rowBytes = f.Width * channels;

        var planeLength = rows * stride;
        var y = ArrayPool<float>.Shared.Rent(planeLength);
        var fullChroma = !gray && !subsample;
        float[] cb = fullChroma ? ArrayPool<float>.Shared.Rent(planeLength) : [];
        float[] cr = fullChroma ? ArrayPool<float>.Shared.Rent(planeLength) : [];
        float[] cb2 = subsample ? ArrayPool<float>.Shared.Rent(8 * halfStride) : [];
        float[] cr2 = subsample ? ArrayPool<float>.Shared.Rent(8 * halfStride) : [];
        Span<short> scratch = stackalloc short[64];
        Span<int> pred = stackalloc int[3];
        pred.Clear();
        var store = dcTables is null;
        var dcCodes = new uint[comps.Length][];
        var acCodes = new uint[comps.Length][];
        if (!store)
        {
            for (var c = 0; c < comps.Length; c++)
            {
                dcCodes[c] = dcTables![comps[c].TableId].Packed;
                acCodes[c] = acTables![comps[c].TableId].AcCombined;
            }
        }

        try
        {
            for (var my = 0; my < f.McusY; my++)
            {
                for (var cx = 0; cx < f.McusX; cx += chunkMcus)
                {
                    var mcus = Math.Min(chunkMcus, f.McusX - cx);
                    var x0 = cx * mcuWidth;
                    var width = mcus * mcuWidth;
                    var valid = Math.Min(width, f.Width - x0); // ≥ 1: McusX = ceil(Width / mcuWidth)

                    // Level-shifted planes for this chunk, rows past the bottom replicating the last
                    // image row and columns past the right edge the last column (the last chroma
                    // column for 4:2:0).
                    if (subsample)
                    {
                        var cValid = (valid + 1) / 2;
                        for (var r = 0; r < 8; r++)
                        {
                            var row0 = Math.Min(my * 16 + 2 * r, f.Height - 1);
                            var row1 = Math.Min(my * 16 + 2 * r + 1, f.Height - 1);
                            var ys0 = y.AsSpan(2 * r * stride, width);
                            var ys1 = y.AsSpan((2 * r + 1) * stride, width);
                            var cbs = cb2.AsSpan(r * halfStride, width / 2);
                            var crs = cr2.AsSpan(r * halfStride, width / 2);
                            JpegEncoderKernels.ConvertRowPair420(
                                pixels.Slice(row0 * rowBytes + x0 * channels, valid * channels),
                                pixels.Slice(row1 * rowBytes + x0 * channels, valid * channels),
                                channels, valid, ys0, ys1, cbs, crs);
                            if (valid < width)
                            {
                                ys0[valid..].Fill(ys0[valid - 1]);
                                ys1[valid..].Fill(ys1[valid - 1]);
                                cbs[cValid..].Fill(cbs[cValid - 1]);
                                crs[cValid..].Fill(crs[cValid - 1]);
                            }
                        }
                    }

                    var prevSrc = -1;
                    for (var r = 0; !subsample && r < rows; r++)
                    {
                        var srcRow = Math.Min(my * rows + r, f.Height - 1);
                        var o = r * stride;
                        if (srcRow == prevSrc)
                        {
                            y.AsSpan(o - stride, width).CopyTo(y.AsSpan(o, width));
                            if (!gray)
                            {
                                cb.AsSpan(o - stride, width).CopyTo(cb.AsSpan(o, width));
                                cr.AsSpan(o - stride, width).CopyTo(cr.AsSpan(o, width));
                            }

                            continue;
                        }

                        prevSrc = srcRow;
                        var src = pixels.Slice(srcRow * rowBytes + x0 * channels, valid * channels);
                        var ys = y.AsSpan(o, width);
                        Span<float> cbs = gray ? [] : cb.AsSpan(o, width);
                        Span<float> crs = gray ? [] : cr.AsSpan(o, width);
                        JpegEncoderKernels.ConvertRow(src, channels, valid, ys, cbs, crs);
                        if (valid < width)
                        {
                            ys[valid..].Fill(ys[valid - 1]);
                            if (!gray)
                            {
                                cbs[valid..].Fill(cbs[valid - 1]);
                                crs[valid..].Fill(crs[valid - 1]);
                            }
                        }
                    }


                    if (!store)
                    {
                        EncodeChunk(ref w, comps, mcus, y, subsample ? cb2 : cb, subsample ? cr2 : cr, stride, subsample ? halfStride : stride,
                            pred, dcCodes, acCodes, scratch);
                        continue;
                    }

                    for (var mx = 0; mx < mcus; mx++)
                    {
                        for (var c = 0; c < comps.Length; c++)
                        {
                            var comp = comps[c];
                            float[] plane;
                            int planeStride;
                            if (c == 0) { plane = y; planeStride = stride; }
                            else if (subsample) { plane = c == 1 ? cb2 : cr2; planeStride = halfStride; }
                            else { plane = c == 1 ? cb : cr; planeStride = stride; }

                            for (var v = 0; v < comp.V; v++)
                            {
                                for (var h = 0; h < comp.H; h++)
                                {
                                    var local = mx * comp.H + h;
                                    JpegEncoderKernels.ForwardDct(plane.AsSpan(v * 8 * planeStride + local * 8), planeStride, comp.Recip,
                                        comp.Block(my * comp.V + v, cx * comp.H + local));
                                }
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(y);
            if (fullChroma)
            {
                ArrayPool<float>.Shared.Return(cb);
                ArrayPool<float>.Shared.Return(cr);
            }

            if (subsample)
            {
                ArrayPool<float>.Shared.Return(cb2);
                ArrayPool<float>.Shared.Return(cr2);
            }
        }
    }
    // -------------------------------------------------------------- sequential entropy coding

    /// <summary>
    /// Huffman codes one block (T.81 F.1.2): DC difference, then AC run/size symbols found by
    /// walking the non-zero mask. Returns the block's DC value, the next predictor.
    /// </summary>
    /// <remarks>
    /// Unchecked accesses: <paramref name="zz"/> has 64 entries and k is a bit index of a 64-bit
    /// mask. DC values of 8-bit samples lie in -1024..1016, so a DC difference indexes
    /// <see cref="ValueBits"/> (±2048) in range and its size category (≤ 11) the 256-entry DC
    /// table. The AC index is <c>(run &lt;&lt; 11) | (v &amp; 2047)</c> with run ≤ 15, inside
    /// the 16 × 2048 <see cref="JpegHuffmanEncodeTable.AcCombined"/>; it names the right entry
    /// because |v| &lt; 1024 (the largest AC basis sum of a ±128 block is about 928, and
    /// quantisers are ≥ 1). The caller reserved <see cref="JpegBitWriter.BlockReserve"/> output
    /// bytes.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int EncodeBlock(ref JpegBitWriter writer, ReadOnlySpan<short> zz, int pred, uint[] dcPacked, uint[] acCombined)
    {
        var dc = EncodeBlockInline(ref writer, zz, pred, dcPacked, acCombined);
        writer.Stuff();
        return dc;
    }

    /// <summary>
    /// Transforms and Huffman codes the MCUs of one chunk: each block's forward DCT and its entropy
    /// coding run back to back in this one method, so there is no call per block and the
    /// out-of-order core can overlap one block's serial entropy coding with the next transform.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void EncodeChunk(ref JpegBitWriter writer, Component[] comps, int mcus, float[] y, float[] c1, float[] c2,
        int yStride, int cStride, Span<int> pred, uint[][] dcCodes, uint[][] acCodes, Span<short> scratch)
    {
        for (var mx = 0; mx < mcus; mx++)
        {
            writer.Ensure(JpegBitWriter.McuReserve);
            for (var c = 0; c < comps.Length; c++)
            {
                var comp = comps[c];
                var plane = c == 0 ? y : c == 1 ? c1 : c2;
                var stride = c == 0 ? yStride : cStride;
                for (var v = 0; v < comp.V; v++)
                {
                    for (var h = 0; h < comp.H; h++)
                    {
                        JpegEncoderKernels.ForwardDctInline(plane.AsSpan(v * 8 * stride + (mx * comp.H + h) * 8), stride, comp.Recip, scratch);
                        pred[c] = EncodeBlockInline(ref writer, scratch, pred[c], dcCodes[c], acCodes[c]);
                    }
                }
            }

            writer.Stuff();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EncodeBlockInline(ref JpegBitWriter writer, ReadOnlySpan<short> zz, int pred, uint[] dcPacked, uint[] acCombined)
    {
        _ = zz[63];
        _ = dcPacked[255];
        _ = acCombined[(16 << 11) - 1];
        ref var z = ref MemoryMarshal.GetReference(zz);
        ref var dc = ref MemoryMarshal.GetArrayDataReference(dcPacked);
        ref var ac = ref MemoryMarshal.GetArrayDataReference(acCombined);
        ref var vb = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(ValueBits), 2048);
        var s = new Emitter(ref MemoryMarshal.GetArrayDataReference(writer.Buffer), writer.Position, writer.Acc, (uint)writer.Bits);

        // Each symbol and its extra bits go out in one Put (see JpegHuffmanEncodeTable.Packed).
        int dcValue = z;
        var bits = Unsafe.Add(ref vb, dcValue - pred);
        var e = Unsafe.Add(ref dc, (nint)(bits >> 16));
        s.Put((e >> 5) | (bits & 0xFFFF), e & 31);

        var mask = JpegEncoderKernels.NonZeroMask(zz) & ~1ul;
        var last = 0;
        while (mask != 0)
        {
            var k = BitOperations.TrailingZeroCount(mask);
            var run = (uint)(k - last - 1);
            while (run >= 16)
            {
                var zrl = Unsafe.Add(ref ac, 15 << 11);
                s.Put(zrl >> 5, zrl & 31);
                run -= 16;
            }

            e = Unsafe.Add(ref ac, (nint)((run << 11) | ((uint)Unsafe.Add(ref z, (nint)(uint)k) & 2047)));
            s.Put(e >> 5, e & 31);
            last = k;
            mask &= mask - 1;
        }

        if (last != 63)
        {
            var eob = ac;
            s.Put(eob >> 5, eob & 31);
        }

        writer.Position = (int)s.Position;
        writer.Acc = s.Acc;
        writer.Bits = (int)s.Bits;
        return dcValue;
    }

    /// <summary>
    /// <see cref="JpegBitWriter.Put"/> on locals: the output base is fetched once per block (no
    /// null check per symbol) and the position is a native integer (no sign extension per store).
    /// </summary>
    private ref struct Emitter(ref byte output, nint position, ulong acc, uint bits)
    {
        private readonly ref byte _output = ref output;
        public nint Position = position;
        public ulong Acc = acc;
        public uint Bits = bits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Put(uint value, uint n)
        {
            // As JpegBitWriter.Put; Bits is at least 1 after adding n ≥ 1, so the shift by -Bits
            // (count taken mod 64) is 64 - Bits.
            Acc = (Acc << (int)n) | value;
            Bits += n;
            var aligned = Acc << -(int)Bits;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref _output, Position),
                BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(aligned) : aligned);
            Position += (nint)(Bits >> 3);
            Bits &= 7;
        }
    }
    /// <summary>
    /// For a coefficient v in -2048..2047 at index v + 2048: its size category in the high 16 bits
    /// and its extra bits (v, or v - 1 for negative v, masked to the category) in the low 16.
    /// </summary>
    private static readonly uint[] ValueBits = BuildValueBits();

    private static uint[] BuildValueBits()
    {
        var t = new uint[4096];
        for (var v = -2048; v < 2048; v++)
        {
            var nb = JpegBitWriter.Category((uint)Math.Abs(v));
            t[v + 2048] = ((uint)nb << 16) | ((uint)(v < 0 ? v - 1 : v) & ((1u << nb) - 1));
        }

        return t;
    }
    /// <summary>Counts the symbols <see cref="EncodeBlock"/> would emit, for Annex K.2 table optimisation.</summary>
    private static int CountBlock(ReadOnlySpan<short> zz, int pred, long[] dcFreq, long[] acFreq)
    {
        int dcValue = zz[0];
        var diff = dcValue - pred;
        dcFreq[JpegBitWriter.Category((uint)Math.Abs(diff))]++;

        var mask = JpegEncoderKernels.NonZeroMask(zz) & ~1ul;
        var last = 0;
        while (mask != 0)
        {
            var k = BitOperations.TrailingZeroCount(mask);
            var run = k - last - 1;
            for (; run >= 16; run -= 16) acFreq[0xF0]++;
            acFreq[(run << 4) | JpegBitWriter.Category((uint)Math.Abs((int)zz[k]))]++;
            last = k;
            mask &= mask - 1;
        }

        if (last != 63) acFreq[0]++;
        return dcValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void EncodeOptimizedBaseline(ref JpegBitWriter w, Frame f)
    {
        var comps = f.Components;
        var tables = comps.Length == 1 ? 1 : 2;
        var dcFreq = new long[tables][];
        var acFreq = new long[tables][];
        for (var t = 0; t < tables; t++) { dcFreq[t] = new long[256]; acFreq[t] = new long[256]; }

        Span<int> pred = stackalloc int[3];
        pred.Clear();
        ForEachMcuBlock(f, (int c, int row, int col, ref int p) => p = CountBlock(comps[c].Block(row, col), p, dcFreq[comps[c].TableId], acFreq[comps[c].TableId]), pred);

        var dc = new JpegHuffmanEncodeTable[tables];
        var ac = new JpegHuffmanEncodeTable[tables];
        var list = new List<(int, int, JpegHuffmanEncodeTable)>();
        for (var t = 0; t < tables; t++)
        {
            dc[t] = JpegHuffmanEncodeTable.FromFrequencies(dcFreq[t]);
            ac[t] = JpegHuffmanEncodeTable.FromFrequencies(acFreq[t]);
            list.Add((0, t, dc[t]));
            list.Add((1, t, ac[t]));
        }

        WriteHuffmanTables(ref w, [.. list]);
        WriteBaselineScanHeader(ref w, f);

        pred.Clear();
        for (var my = 0; my < f.McusY; my++)
        {
            for (var mx = 0; mx < f.McusX; mx++)
            {
                for (var c = 0; c < comps.Length; c++)
                {
                    var comp = comps[c];
                    for (var v = 0; v < comp.V; v++)
                    {
                        for (var h = 0; h < comp.H; h++)
                        {
                            w.Ensure(JpegBitWriter.BlockReserve);
                            pred[c] = EncodeBlock(ref w, comp.Block(my * comp.V + v, mx * comp.H + h), pred[c], dc[comp.TableId].Packed, ac[comp.TableId].AcCombined);
                        }
                    }
                }
            }
        }
    }

    internal delegate void BlockVisitor(int component, int row, int col, ref int pred);

    /// <summary>Visits every block of an interleaved scan over all components in MCU order.</summary>
    internal static void ForEachMcuBlock(Frame f, BlockVisitor visit, Span<int> pred)
    {
        var comps = f.Components;
        for (var my = 0; my < f.McusY; my++)
        {
            for (var mx = 0; mx < f.McusX; mx++)
            {
                for (var c = 0; c < comps.Length; c++)
                {
                    var comp = comps[c];
                    for (var v = 0; v < comp.V; v++)
                    {
                        for (var h = 0; h < comp.H; h++)
                            visit(c, my * comp.V + v, mx * comp.H + h, ref pred[c]);
                    }
                }
            }
        }
    }

    // -------------------------------------------------------------- marker segments

    /// <summary>Checks the EXIF blob before anything is written; returns it, or empty for "no segment".</summary>
    private static ReadOnlyMemory<byte> ValidateExif(ReadOnlyMemory<byte>? exif)
    {
        if (exif is not { } blob) return default;
        if (blob.Length > JpegEncodeOptions.MaxExifBytes)
            throw new ArgumentException($"EXIF blob is {blob.Length} bytes; one APP1 segment holds at most {JpegEncodeOptions.MaxExifBytes}.", "options");
        var s = blob.Span;
        var tiffHeader = s.Length >= 8
            && ((s[0] == (byte)'I' && s[1] == (byte)'I' && s[2] == 42 && s[3] == 0)
                || (s[0] == (byte)'M' && s[1] == (byte)'M' && s[2] == 0 && s[3] == 42));
        if (!tiffHeader)
            throw new ArgumentException("EXIF blob must be a TIFF structure (II*\\0 or MM\\0* header, at least 8 bytes), without the Exif\\0\\0 signature.", "options");
        return blob;
    }

    /// <summary>Builds the XMP packet before anything is written; a bad packet is an <see cref="ArgumentException"/> on <c>options</c>.</summary>
    private static byte[] BuildXmpPacket(JpegXmp xmp)
    {
        try
        {
            return xmp.BuildPacket();
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(ex.Message, "options", ex);
        }
    }

    private static ReadOnlyMemory<byte> ValidateIcc(ReadOnlyMemory<byte>? icc)
    {
        if (icc is not { } profile) return default;
        if (profile.IsEmpty)
            throw new ArgumentException("ICC profile is empty.", "options");
        if (profile.Length > JpegEncodeOptions.MaxIccProfileBytes)
            throw new ArgumentException($"ICC profile is {profile.Length} bytes; 255 APP2 segments hold at most {JpegEncodeOptions.MaxIccProfileBytes}.", "options");
        return profile;
    }

    private static void WriteHeaders(ref JpegBitWriter w, ReadOnlySpan<byte> exif, ReadOnlySpan<byte> xmp, ReadOnlySpan<byte> icc,
        byte[] lumaQuant, byte[]? chromaQuant)
    {
        // SOI + APP0 JFIF 1.01, no density, no thumbnail.
        w.WriteBytes([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        if (!exif.IsEmpty)
        {
            // APP1 "Exif\0\0" + the TIFF blob; the length field counts itself (already range-checked).
            w.WriteBytes([0xFF, 0xE1]);
            w.WriteUInt16(2 + 6 + exif.Length);
            w.WriteBytes("Exif\0\0"u8);
            w.WriteBytes(exif);
        }

        if (!xmp.IsEmpty)
        {
            // APP1 XMP identifier (NUL included) + the packet; the length was range-checked when it was built.
            ReadOnlySpan<byte> id = "http://ns.adobe.com/xap/1.0/\0"u8;
            w.WriteBytes([0xFF, 0xE1]);
            w.WriteUInt16(2 + id.Length + xmp.Length);
            w.WriteBytes(id);
            w.WriteBytes(xmp);
        }

        if (!icc.IsEmpty)
        {
            // APP2 "ICC_PROFILE\0", 1-based sequence number, chunk count, then up to MaxIccChunkBytes of the
            // profile; the size was range-checked, so the count fits its byte.
            var count = (icc.Length + JpegEncodeOptions.MaxIccChunkBytes - 1) / JpegEncodeOptions.MaxIccChunkBytes;
            for (var i = 0; i < count; i++)
            {
                var chunk = icc.Slice(i * JpegEncodeOptions.MaxIccChunkBytes,
                    Math.Min(JpegEncodeOptions.MaxIccChunkBytes, icc.Length - i * JpegEncodeOptions.MaxIccChunkBytes));
                w.WriteBytes([0xFF, 0xE2]);
                w.WriteUInt16(2 + 12 + 2 + chunk.Length);
                w.WriteBytes("ICC_PROFILE\0"u8);
                w.WriteBytes([(byte)(i + 1), (byte)count]);
                w.WriteBytes(chunk);
            }
        }

        // DQT: 8-bit tables in zig-zag order.
        var tables = chromaQuant is null ? 1 : 2;
        w.WriteBytes([0xFF, 0xDB]);
        w.WriteUInt16(2 + tables * 65);
        for (var t = 0; t < tables; t++)
        {
            var quant = t == 0 ? lumaQuant : chromaQuant!;
            w.WriteByte((byte)t);
            for (var i = 0; i < 64; i++) w.WriteByte(quant[ZigZagToNatural[i]]);
        }
    }

    private static void WriteFrame(ref JpegBitWriter w, Frame f, bool progressive)
    {
        w.WriteBytes([0xFF, progressive ? (byte)0xC2 : (byte)0xC0]);
        w.WriteUInt16(8 + 3 * f.Components.Length);
        w.WriteByte(8);
        w.WriteUInt16(f.Height);
        w.WriteUInt16(f.Width);
        w.WriteByte((byte)f.Components.Length);
        foreach (var c in f.Components)
        {
            w.WriteByte(c.Id);
            w.WriteByte((byte)((c.H << 4) | c.V));
            w.WriteByte((byte)c.TableId);
        }
    }

    /// <summary>One DHT segment for the given (class 0 = DC / 1 = AC, destination, table) triples.</summary>
    internal static void WriteHuffmanTables(ref JpegBitWriter w, ReadOnlySpan<(int Class, int Id, JpegHuffmanEncodeTable Table)> tables)
    {
        var length = 2;
        foreach (var t in tables) length += 17 + t.Table.Values.Length;
        w.WriteBytes([0xFF, 0xC4]);
        w.WriteUInt16(length);
        foreach (var (cls, id, table) in tables)
        {
            w.WriteByte((byte)((cls << 4) | id));
            w.WriteBytes(table.Bits);
            w.WriteBytes(table.Values);
        }
    }

    private static void WriteBaselineScanHeader(ref JpegBitWriter w, Frame f)
    {
        var comps = f.Components;
        WriteScanHeader(ref w, comps, comps.Select(c => (c.TableId, c.TableId)).ToArray(), 0, 63, 0, 0);
    }

    internal static void WriteScanHeader(ref JpegBitWriter w, ReadOnlySpan<Component> comps, ReadOnlySpan<(int Dc, int Ac)> tables, int ss, int se, int ah, int al)
    {
        w.WriteBytes([0xFF, 0xDA]);
        w.WriteUInt16(6 + 2 * comps.Length);
        w.WriteByte((byte)comps.Length);
        for (var i = 0; i < comps.Length; i++)
        {
            w.WriteByte(comps[i].Id);
            w.WriteByte((byte)((tables[i].Dc << 4) | tables[i].Ac));
        }

        w.WriteByte((byte)ss);
        w.WriteByte((byte)se);
        w.WriteByte((byte)((ah << 4) | al));
    }
}
