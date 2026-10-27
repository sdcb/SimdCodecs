using System.Buffers;
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
            WriteHeaders(ref w, exif.Span, xmp, lumaQuant, gray ? null : chromaQuant);
            if (!stored)
            {
                WriteFrame(ref w, frame, progressive: false);
                var dcL = JpegHuffmanEncodeTable.FromSpec(DcLumaBits, DcLumaValues);
                var acL = JpegHuffmanEncodeTable.FromSpec(AcLumaBits, AcLumaValues);
                var dcC = JpegHuffmanEncodeTable.FromSpec(DcChromaBits, DcChromaValues);
                var acC = JpegHuffmanEncodeTable.FromSpec(AcChromaBits, AcChromaValues);
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

    /// <summary>
    /// Runs colour conversion and the forward DCT over the whole image, one MCU row at a time.
    /// With Huffman tables given, every block is entropy coded straight after its transform;
    /// otherwise the quantised coefficients are stored in each component's grid.
    /// </summary>
    private static void Transform(ReadOnlySpan<byte> pixels, int channels, Frame f, ref JpegBitWriter w,
        JpegHuffmanEncodeTable[]? dcTables, JpegHuffmanEncodeTable[]? acTables)
    {
        var comps = f.Components;
        var gray = comps.Length == 1;
        var subsample = f.HMax == 2;
        var rows = 8 * f.VMax;
        var stride = f.McusX * 8 * f.HMax;
        var halfStride = stride / 2;
        var rowBytes = f.Width * channels;

        var planeLength = rows * stride;
        var y = ArrayPool<float>.Shared.Rent(planeLength);
        float[] cb = gray ? [] : ArrayPool<float>.Shared.Rent(planeLength);
        float[] cr = gray ? [] : ArrayPool<float>.Shared.Rent(planeLength);
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
                dcCodes[c] = dcTables![comps[c].TableId].Codes;
                acCodes[c] = acTables![comps[c].TableId].Codes;
            }
        }

        try
        {
            for (var my = 0; my < f.McusY; my++)
            {
                // Level-shifted planes for this MCU row, rows past the bottom replicating the last
                // image row and columns past the right edge the last column.
                var prevSrc = -1;
                for (var r = 0; r < rows; r++)
                {
                    var srcRow = Math.Min(my * rows + r, f.Height - 1);
                    var o = r * stride;
                    if (srcRow == prevSrc)
                    {
                        y.AsSpan(o - stride, stride).CopyTo(y.AsSpan(o, stride));
                        if (!gray)
                        {
                            cb.AsSpan(o - stride, stride).CopyTo(cb.AsSpan(o, stride));
                            cr.AsSpan(o - stride, stride).CopyTo(cr.AsSpan(o, stride));
                        }

                        continue;
                    }

                    prevSrc = srcRow;
                    var src = pixels.Slice(srcRow * rowBytes, rowBytes);
                    var ys = y.AsSpan(o, stride);
                    Span<float> cbs = gray ? [] : cb.AsSpan(o, stride);
                    Span<float> crs = gray ? [] : cr.AsSpan(o, stride);
                    JpegEncoderKernels.ConvertRow(src, channels, f.Width, ys, cbs, crs);
                    if (f.Width < stride)
                    {
                        ys[f.Width..].Fill(ys[f.Width - 1]);
                        if (!gray)
                        {
                            cbs[f.Width..].Fill(cbs[f.Width - 1]);
                            crs[f.Width..].Fill(crs[f.Width - 1]);
                        }
                    }
                }

                if (subsample)
                {
                    for (var r = 0; r < 8; r++)
                    {
                        JpegEncoderKernels.Downsample2x2(cb.AsSpan(2 * r * stride, stride), cb.AsSpan((2 * r + 1) * stride, stride), cb2.AsSpan(r * halfStride, halfStride), halfStride);
                        JpegEncoderKernels.Downsample2x2(cr.AsSpan(2 * r * stride, stride), cr.AsSpan((2 * r + 1) * stride, stride), cr2.AsSpan(r * halfStride, halfStride), halfStride);
                    }
                }

                for (var mx = 0; mx < f.McusX; mx++)
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
                                var col = mx * comp.H + h;
                                var block = plane.AsSpan(v * 8 * planeStride + col * 8);
                                var zz = store ? comp.Block(my * comp.V + v, col) : scratch;
                                JpegEncoderKernels.ForwardDct(block, planeStride, comp.Recip, zz);
                                if (!store)
                                {
                                    w.Ensure(JpegBitWriter.BlockReserve);
                                    pred[c] = EncodeBlock(ref w, zz, pred[c], dcCodes[c], acCodes[c]);
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
            if (!gray)
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
    /// mask; the code tables have 256 entries and every index is a byte — a DC size category
    /// (≤ 16) or <c>(run &lt;&lt; 4) | size</c> with run ≤ 15 and size ≤ 15. The caller reserved
    /// <see cref="JpegBitWriter.BlockReserve"/> output bytes.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int EncodeBlock(ref JpegBitWriter writer, ReadOnlySpan<short> zz, int pred, uint[] dcCodes, uint[] acCodes)
    {
        var w = writer;
        _ = zz[63];
        _ = dcCodes[255];
        _ = acCodes[255];
        ref var z = ref MemoryMarshal.GetReference(zz);
        ref var dc = ref MemoryMarshal.GetArrayDataReference(dcCodes);
        ref var ac = ref MemoryMarshal.GetArrayDataReference(acCodes);

        int dcValue = z;
        var diff = dcValue - pred;
        var s = diff >> 31;
        var nb = JpegBitWriter.Category((uint)((diff ^ s) - s));
        var code = Unsafe.Add(ref dc, nb);
        w.Put(((code >> 8) << nb) | ((uint)(diff + s) & ((1u << nb) - 1)), (int)(code & 0xFF) + nb);

        var mask = JpegEncoderKernels.NonZeroMask(zz) & ~1ul;
        var last = 0;
        while (mask != 0)
        {
            var k = BitOperations.TrailingZeroCount(mask);
            var run = k - last - 1;
            while (run >= 16)
            {
                var zrl = Unsafe.Add(ref ac, 0xF0);
                w.Put(zrl >> 8, (int)(zrl & 0xFF));
                run -= 16;
            }

            int v = Unsafe.Add(ref z, k);
            s = v >> 31;
            nb = JpegBitWriter.Category((uint)((v ^ s) - s));
            code = Unsafe.Add(ref ac, (run << 4) | nb);
            w.Put(((code >> 8) << nb) | ((uint)(v + s) & ((1u << nb) - 1)), (int)(code & 0xFF) + nb);
            last = k;
            mask &= mask - 1;
        }

        if (last != 63)
        {
            var eob = ac;
            w.Put(eob >> 8, (int)(eob & 0xFF));
        }

        writer = w;
        return dcValue;
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
                            pred[c] = EncodeBlock(ref w, comp.Block(my * comp.V + v, mx * comp.H + h), pred[c], dc[comp.TableId].Codes, ac[comp.TableId].Codes);
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

    private static void WriteHeaders(ref JpegBitWriter w, ReadOnlySpan<byte> exif, ReadOnlySpan<byte> xmp, byte[] lumaQuant, byte[]? chromaQuant)
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
