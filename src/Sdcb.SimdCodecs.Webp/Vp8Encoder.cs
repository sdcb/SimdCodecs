using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>Choices of the VP8 encoder that the effort level and quality set.</summary>
internal sealed record Vp8EncoderConfig
{
    /// <summary>Base quantizer index, 0 (finest) to 127.</summary>
    public int QuantIndex { get; init; } = 40;

    /// <summary>0..6: how many modes are tried and how many passes are made.</summary>
    public int Effort { get; init; } = 4;

    /// <summary>Loop filter level 0..63, or −1 to choose one.</summary>
    public int FilterLevel { get; init; } = -1;

    /// <summary>Loop filter sharpness 0..7.</summary>
    public int Sharpness { get; init; }

    /// <summary>Segments (1 = none, up to 4), each with its own quantizer and filter level.</summary>
    public int Segments { get; init; } = 4;

    /// <summary>Rate-distortion trade-off: λ = scale/256 · q², separately for the luma modes and chroma.</summary>
    public int LambdaI16 { get; init; } = 4;
    public int LambdaI4 { get; init; } = 4;
    public int LambdaUv { get; init; } = 4;

    /// <summary>Quantizer rounding (1/256 of a step) for DC and AC levels; lower is a larger dead zone.</summary>
    public int RoundingDc { get; init; } = 100;
    public int RoundingAc { get; init; } = 95;

    /// <summary>
    /// Base quantizer index of each quality, from the benchmark's <c>calibrate</c> command: the index at
    /// which this encoder (effort 4) writes the bytes libwebp 1.6.0 writes at that quality on the
    /// benchmark photographs (measured every 5, linear between).
    /// </summary>
    private static ReadOnlySpan<byte> QualityToIndex =>
    [
        105, 99, 93, 88, 82, 76, 74, 72, 71, 69,
        67, 66, 65, 63, 62, 61, 60, 59, 58, 57,
        56, 55, 54, 54, 53, 52, 51, 50, 50, 49,
        48, 47, 47, 46, 46, 45, 44, 43, 43, 42,
        41, 40, 39, 39, 38, 37, 36, 36, 35, 35,
        34, 34, 33, 33, 32, 32, 31, 31, 30, 30,
        29, 29, 28, 28, 27, 27, 27, 26, 26, 25,
        25, 25, 24, 24, 23, 23, 22, 21, 19, 18,
        17, 16, 15, 14, 13, 12, 11, 10, 9, 8,
        7, 6, 5, 4, 3, 2, 2, 1, 1, 0,
        0,
    ];

    /// <summary>
    /// Spread of the segment quantizers around the base: index steps from the flattest to the busiest
    /// segment at base index 40, in proportion to (index + 8) elsewhere. A fixed number of steps is a
    /// far larger change of step size at fine quantizers, where it cost PSNR for no gain in SSIM.
    /// </summary>
    public int SegmentSpread { get; init; } = 18;

    /// <summary>4×4 modes given a full rate-distortion trial per block, the best by SATD (10 = all).</summary>
    public int I4Candidates { get; init; } = 4;

    /// <summary>16×16 luma and chroma modes given a full trial, the best by SATD (4 = all).</summary>
    public int I16Candidates { get; init; } = 2;
    public int UvCandidates { get; init; } = 2;

    /// <summary>Lossless effort (0..9) of the alpha plane's VP8L stream.</summary>
    public int AlphaEffort { get; init; } = 4;

    /// <summary>Whether all four alpha filters are tried (otherwise none only); <see cref="AlphaFilter"/> ≥ 0 forces one.</summary>
    public bool AlphaTryFilters { get; init; }
    public int AlphaFilter { get; init; } = -1;

    /// <summary>Whether the loop filter level is searched by decoding (otherwise taken from the quantizer).</summary>
    public bool FilterSearch { get; init; }

    /// <summary>The configuration of a public quality (0..100) and effort (0..6).</summary>
    public static Vp8EncoderConfig For(int quality, int effort) => new()
    {
        QuantIndex = QualityToIndex[Math.Clamp(quality, 0, 100)],
        Effort = effort,
        I4Candidates = effort switch { <= 1 => 2, 2 => 3, 3 => 3, 4 => 4, 5 => 6, _ => 10 },
        I16Candidates = effort switch { <= 1 => 1, <= 4 => 2, _ => 4 },
        UvCandidates = effort switch { <= 3 => 1, 4 => 2, _ => 4 },
        FilterSearch = effort >= 6,
        AlphaEffort = WebpAlphaEncoder.LosslessEffort(effort),
        AlphaTryFilters = effort >= 6,
    };
}

/// <summary>
/// Pure-managed VP8 key-frame encoder (RFC 6386), built from the specification's description of the
/// format. The reconstruction it predicts from is the decoder's: it calls the decoder's own
/// predictors, inverse transforms and edge rules, so the frame it reconstructs is exactly the frame
/// any decoder will (before the loop filter).
/// <para>Every macroblock is coded as the cheapest of the 16×16 modes and of B_PRED with each 4×4 mode
/// chosen in turn, by rate-distortion cost D + λ·R: D the squared error of the reconstruction, R the
/// bits under the current token and mode probabilities. The tokens are kept, their statistics
/// give the coefficient probabilities the frame header updates, and only then are they written.</para>
/// </summary>
internal sealed partial class Vp8Encoder : IDisposable
{
    private const int RecLeft = 16; // bytes before column 0 in a reconstruction row (column −1 = left edge)

    private readonly int _width, _height, _mbW, _mbH;
    private readonly int _srcStride, _srcUvStride, _recStride, _recUvStride;
    private readonly byte[] _srcY, _srcU, _srcV;
    private readonly byte[] _recY, _recU, _recV;
    private readonly Vp8EncoderConfig _config;

    // Per macroblock: luma mode (0-3, 4 = B_PRED), chroma mode, segment, skip; 4×4 modes; levels.
    private readonly byte[] _yMode, _uvMode, _segment, _skip;
    private readonly byte[] _bModes;
    private readonly short[] _levels; // 25 blocks × 16 zigzag levels per macroblock (Y 0-15, U 16-19, V 20-23, Y2 24)

    private readonly SegmentQuant[] _quant = new SegmentQuant[4];
    private readonly byte[] _probabilities = new byte[Vp8EncoderCosts.ProbabilityCount];
    private Vp8TokenCosts _tokenCosts;
    private readonly int[] _yModeCost = new int[5], _uvModeCost = new int[4];
    private readonly int[] _bModeCost = new int[10 * 10 * 10];
    private int _segmentCount = 1;
    private readonly int[] _segmentFilter = new int[4];
    private readonly byte[] _segmentProbabilities = [255, 255, 255];

    private struct SegmentQuant
    {
        public int Index;
        public int YDc, YAc, Y2Dc, Y2Ac, UvDc, UvAc;
        public int IYDc, IYAc, IY2Dc, IY2Ac, IUvDc, IUvAc; // 65536 / step
        public long LambdaI16, LambdaI4, LambdaUv;
    }

    public Vp8Encoder(int width, int height, Vp8EncoderConfig config)
    {
        _width = width;
        _height = height;
        _config = config;
        _mbW = (width + 15) >> 4;
        _mbH = (height + 15) >> 4;
        _srcStride = _mbW * 16;
        _srcUvStride = _mbW * 8;
        _recStride = _srcStride + 2 * RecLeft;
        _recUvStride = _srcUvStride + 2 * RecLeft;
        int mbs = _mbW * _mbH;
        _srcY = ArrayPool<byte>.Shared.Rent(_srcStride * _mbH * 16);
        _srcU = ArrayPool<byte>.Shared.Rent(_srcUvStride * _mbH * 8);
        _srcV = ArrayPool<byte>.Shared.Rent(_srcUvStride * _mbH * 8);
        _recY = ArrayPool<byte>.Shared.Rent(_recStride * _mbH * 16);
        _recU = ArrayPool<byte>.Shared.Rent(_recUvStride * _mbH * 8);
        _recV = ArrayPool<byte>.Shared.Rent(_recUvStride * _mbH * 8);
        _yMode = ArrayPool<byte>.Shared.Rent(mbs);
        _uvMode = ArrayPool<byte>.Shared.Rent(mbs);
        _segment = ArrayPool<byte>.Shared.Rent(mbs);
        _skip = ArrayPool<byte>.Shared.Rent(mbs);
        _bModes = ArrayPool<byte>.Shared.Rent(mbs * 16);
        _levels = ArrayPool<short>.Shared.Rent(mbs * 25 * 16);
        Vp8Tables.CopyDefaultCoefficientProbabilities(_probabilities);
        _tokenCosts = new Vp8TokenCosts(_probabilities);
        for (int m = 0; m < 5; m++)
        {
            _yModeCost[m] = Vp8EncoderCosts.TreeCost(Vp8Tables.KeyFrameYModeTree, Vp8Tables.KeyFrameYModeProbabilities, m);
        }

        for (int m = 0; m < 4; m++)
        {
            _uvModeCost[m] = Vp8EncoderCosts.TreeCost(Vp8Tables.UvModeTree, Vp8Tables.KeyFrameUvModeProbabilities, m);
        }

        for (int above = 0; above < 10; above++)
        {
            for (int left = 0; left < 10; left++)
            {
                for (int m = 0; m < 10; m++)
                {
                    _bModeCost[(above * 10 + left) * 10 + m] =
                        Vp8EncoderCosts.TreeCost(Vp8Tables.BlockModeTree, Vp8Tables.KeyFrameBlockModeProbabilities[above][left], m);
                }
            }
        }
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_srcY);
        ArrayPool<byte>.Shared.Return(_srcU);
        ArrayPool<byte>.Shared.Return(_srcV);
        ArrayPool<byte>.Shared.Return(_recY);
        ArrayPool<byte>.Shared.Return(_recU);
        ArrayPool<byte>.Shared.Return(_recV);
        ArrayPool<byte>.Shared.Return(_yMode);
        ArrayPool<byte>.Shared.Return(_uvMode);
        ArrayPool<byte>.Shared.Return(_segment);
        ArrayPool<byte>.Shared.Return(_skip);
        ArrayPool<byte>.Shared.Return(_bModes);
        ArrayPool<short>.Shared.Return(_levels);
    }

    // ---------------------------------------------------------------- input

    /// <summary>
    /// Fills the source planes from ARGB words (BT.601 studio range, the matrix WebP decoders invert),
    /// chroma from the 2×2 RGB averages; the planes are padded to whole macroblocks by repeating the
    /// last column and row.
    /// </summary>
    public void SetArgb(ReadOnlySpan<uint> argb)
    {
        int w = _width, h = _height;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<uint> row = argb.Slice(y * w, w);
            Span<byte> dst = _srcY.AsSpan(y * _srcStride, _srcStride);
            for (int x = 0; x < w; x++)
            {
                uint c = row[x];
                int r = (int)(c >> 16) & 0xFF, g = (int)(c >> 8) & 0xFF, b = (int)c & 0xFF;
                dst[x] = (byte)((16839 * r + 33059 * g + 6420 * b + (16 << 16) + 32768) >> 16);
            }

            dst[w..].Fill(dst[w - 1]);
        }

        for (int y = h; y < _mbH * 16; y++)
        {
            _srcY.AsSpan((h - 1) * _srcStride, _srcStride).CopyTo(_srcY.AsSpan(y * _srcStride));
        }

        int cw = (w + 1) >> 1, ch = (h + 1) >> 1;
        for (int y = 0; y < ch; y++)
        {
            int y0 = 2 * y, y1 = Math.Min(2 * y + 1, h - 1);
            ReadOnlySpan<uint> r0 = argb.Slice(y0 * w, w), r1 = argb.Slice(y1 * w, w);
            Span<byte> du = _srcU.AsSpan(y * _srcUvStride, _srcUvStride), dv = _srcV.AsSpan(y * _srcUvStride, _srcUvStride);
            for (int x = 0; x < cw; x++)
            {
                int x0 = 2 * x, x1 = Math.Min(2 * x + 1, w - 1);
                uint a = r0[x0], b2 = r0[x1], c3 = r1[x0], d4 = r1[x1];
                int r = (int)(((a >> 16) & 0xFF) + ((b2 >> 16) & 0xFF) + ((c3 >> 16) & 0xFF) + ((d4 >> 16) & 0xFF));
                int g = (int)(((a >> 8) & 0xFF) + ((b2 >> 8) & 0xFF) + ((c3 >> 8) & 0xFF) + ((d4 >> 8) & 0xFF));
                int b = (int)((a & 0xFF) + (b2 & 0xFF) + (c3 & 0xFF) + (d4 & 0xFF));
                du[x] = Clamp8((-9719 * r - 19081 * g + 28800 * b + (128 << 18) + (1 << 17)) >> 18);
                dv[x] = Clamp8((28800 * r - 24116 * g - 4684 * b + (128 << 18) + (1 << 17)) >> 18);
            }

            du[cw..].Fill(du[cw - 1]);
            dv[cw..].Fill(dv[cw - 1]);
        }

        for (int y = ch; y < _mbH * 8; y++)
        {
            _srcU.AsSpan((ch - 1) * _srcUvStride, _srcUvStride).CopyTo(_srcU.AsSpan(y * _srcUvStride));
            _srcV.AsSpan((ch - 1) * _srcUvStride, _srcUvStride).CopyTo(_srcV.AsSpan(y * _srcUvStride));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clamp8(int v) => (byte)Math.Clamp(v, 0, 255);

    /// <summary>The source planes, as tight planes (for tests).</summary>
    internal void Source(out byte[] y, out byte[] u, out byte[] v)
    {
        int cw = (_width + 1) >> 1, ch = (_height + 1) >> 1;
        y = new byte[_width * _height];
        u = new byte[cw * ch];
        v = new byte[cw * ch];
        for (int r = 0; r < _height; r++)
        {
            _srcY.AsSpan(r * _srcStride, _width).CopyTo(y.AsSpan(r * _width));
        }

        for (int r = 0; r < ch; r++)
        {
            _srcU.AsSpan(r * _srcUvStride, cw).CopyTo(u.AsSpan(r * cw));
            _srcV.AsSpan(r * _srcUvStride, cw).CopyTo(v.AsSpan(r * cw));
        }
    }

    /// <summary>The reconstructed frame before the loop filter, as tight planes (for tests).</summary>
    internal void Reconstruction(out byte[] y, out byte[] u, out byte[] v)
    {
        int cw = (_width + 1) >> 1, ch = (_height + 1) >> 1;
        y = new byte[_width * _height];
        u = new byte[cw * ch];
        v = new byte[cw * ch];
        for (int r = 0; r < _height; r++)
        {
            _recY.AsSpan(r * _recStride + RecLeft, _width).CopyTo(y.AsSpan(r * _width));
        }

        for (int r = 0; r < ch; r++)
        {
            _recU.AsSpan(r * _recUvStride + RecLeft, cw).CopyTo(u.AsSpan(r * cw));
            _recV.AsSpan(r * _recUvStride + RecLeft, cw).CopyTo(v.AsSpan(r * cw));
        }
    }

    // ---------------------------------------------------------------- frame

    /// <summary>Encodes the frame: analysis, mode decision, probabilities, then the bitstream.</summary>
    public byte[] Encode()
    {
        SetupSegments();
        int passes = _config.Effort >= 5 ? 2 : 1;
        for (int pass = 0; pass < passes; pass++)
        {
            if (pass > 0)
            {
                // Decide again under the probabilities the first pass's tokens gave.
                ChooseProbabilities();
                _tokenCosts.Update(_probabilities);
            }

            EncodeMacroblocks();
        }

        ChooseProbabilities();
        int filterLevel = _config.FilterLevel >= 0 ? _config.FilterLevel : ChooseFilterLevel();
        return WriteFrame(filterLevel);
    }

    private void SetQuant(int segment, int index)
    {
        index = Math.Clamp(index, 0, 127);
        ref SegmentQuant q = ref _quant[segment];
        q.Index = index;
        q.YDc = Vp8Tables.DcQuantizers[index];
        q.YAc = Vp8Tables.AcQuantizers[index];
        q.Y2Dc = 2 * Vp8Tables.DcQuantizers[index];
        q.Y2Ac = Math.Max(8, 155 * Vp8Tables.AcQuantizers[index] / 100);
        q.UvDc = Math.Min(132, Vp8Tables.DcQuantizers[index]);
        q.UvAc = Vp8Tables.AcQuantizers[index];
        q.IYDc = 65536 / q.YDc;
        q.IYAc = 65536 / q.YAc;
        q.IY2Dc = 65536 / q.Y2Dc;
        q.IY2Ac = 65536 / q.Y2Ac;
        q.IUvDc = 65536 / q.UvDc;
        q.IUvAc = 65536 / q.UvAc;
        long q2 = (long)q.YAc * q.YAc;
        q.LambdaI16 = Math.Max(1, _config.LambdaI16 * q2);
        q.LambdaI4 = Math.Max(1, _config.LambdaI4 * q2);
        q.LambdaUv = Math.Max(1, _config.LambdaUv * q2);
    }

    // ---------------------------------------------------------------- macroblocks

    private void EncodeMacroblocks()
    {
        Array.Clear(_recY);
        Array.Clear(_recU);
        Array.Clear(_recV);
        uint[] aboveNz = new uint[_mbW];
        byte[] aboveB = new byte[_mbW * 4];
        Span<byte> leftB = stackalloc byte[4];
        Span<byte> aboveRows = stackalloc byte[Vp8Decoder.AboveLength];
        var scratch = new MacroblockScratch();
        for (int mbY = 0; mbY < _mbH; mbY++)
        {
            for (int row = 0; row < 16; row++)
            {
                _recY[(mbY * 16 + row) * _recStride + RecLeft - 1] = 129;
            }

            for (int row = 0; row < 8; row++)
            {
                _recU[(mbY * 8 + row) * _recUvStride + RecLeft - 1] = 129;
                _recV[(mbY * 8 + row) * _recUvStride + RecLeft - 1] = 129;
            }

            uint leftNz = 0;
            leftB.Clear();
            for (int mbX = 0; mbX < _mbW; mbX++)
            {
                ref byte aboveY = ref aboveRows[Vp8Decoder.AboveY];
                ref byte aboveU = ref aboveRows[Vp8Decoder.AboveU];
                ref byte aboveV = ref aboveRows[Vp8Decoder.AboveV];
                if (mbY == 0)
                {
                    // PrepareAbove fills the frame's top edge without reading the rows.
                    Vp8Decoder.PrepareAbove(ref aboveY, ref aboveU, ref aboveV, ref _recY[0], ref _recU[0], ref _recV[0], mbX, 0, _width);
                }
                else
                {
                    Vp8Decoder.PrepareAbove(ref aboveY, ref aboveU, ref aboveV,
                        ref _recY[(mbY * 16 - 1) * _recStride + RecLeft], ref _recU[(mbY * 8 - 1) * _recUvStride + RecLeft],
                        ref _recV[(mbY * 8 - 1) * _recUvStride + RecLeft], mbX, mbY, _width);
                }

                EncodeMacroblock(mbX, mbY, ref aboveNz[mbX], ref leftNz, aboveB.AsSpan(mbX * 4, 4), leftB, aboveRows, scratch);
            }
        }
    }

    /// <summary>Scratch buffers of one macroblock's decisions.</summary>
    private sealed class MacroblockScratch
    {
        public readonly int[] Residual = new int[16];
        public readonly int[] Coefficients = new int[16];
        public readonly int[] Dc = new int[16];
        public readonly int[] Y2 = new int[16];
        public readonly short[] Dequant = new short[16 * 16];
        public readonly short[] Y2Dequant = new short[16];
        public readonly short[] Residual16 = new short[16];
        public readonly byte[] Prediction = new byte[16 * 16];
        public readonly byte[] BestI16 = new byte[16 * 16];
        public readonly short[] LevelsI16 = new short[17 * 16]; // 16 Y blocks + Y2 at 16
        public readonly short[] BestLevelsI16 = new short[17 * 16];
        public readonly short[] LevelsI4 = new short[16 * 16];
        public readonly short[] LevelsUv = new short[8 * 16];
        public readonly short[] Trial = new short[16];
        public readonly short[] BestTrial = new short[16];
        public readonly byte[] Block = new byte[16];
        public readonly byte[] BestBlock = new byte[16];
        public readonly byte[] BestU = new byte[64], BestV = new byte[64];
        public readonly byte[] Modes = new byte[16];
        public readonly int[] Candidates = new int[10];
        public readonly short[] Deq4 = new short[16];
        public readonly byte[] Predictions = new byte[160];
    }

    /// <summary>
    /// The <see cref="Vp8EncoderConfig.I4Candidates"/> 4×4 modes most worth a full trial, by the sum of
    /// absolute Hadamard-transformed residuals plus √λ times the mode bits; all ten when that is the
    /// setting, from the ten predictions in <c>s.Predictions</c>. Returns the count, written to
    /// <c>s.Candidates</c> best first.
    /// </summary>
    private int PreselectI4(int srcOff, in SegmentQuant q, int modeRow, MacroblockScratch s)
    {
        int[] order = s.Candidates;
        int count = _config.I4Candidates;
        if (count >= 10)
        {
            for (int m = 0; m < 10; m++)
            {
                order[m] = m;
            }

            return 10;
        }

        Span<long> score = stackalloc long[10];
        double lambdaSad = Math.Sqrt(q.LambdaI4 / 256.0);
        for (int m = 0; m < 10; m++)
        {
            score[m] = (long)(Vp8EncoderKernels.Satd4(ref _srcY[srcOff], _srcStride, ref s.Predictions[m * 16], 4) * 256 +
                              lambdaSad * _bModeCost[modeRow + m]);
            order[m] = m;
        }

        // Partial selection sort: the first count entries, best first.
        for (int i = 0; i < count; i++)
        {
            int best = i;
            for (int j = i + 1; j < 10; j++)
            {
                if (score[order[j]] < score[order[best]])
                {
                    best = j;
                }
            }

            (order[i], order[best]) = (order[best], order[i]);
        }

        return count;
    }

    /// <summary>
    /// The 16×16 (or, with <paramref name="rec2"/>, both 8×8 chroma) modes most worth a full trial, by SATD
    /// plus √λ times the mode bits. Returns the count, written to <paramref name="order"/> best first.
    /// </summary>
    private int PreselectLarge(byte[] rec, int recBase, int recStride, byte[] src, int srcBase, int srcStride, int size,
        ref byte above, int mbX, int mbY, long lambda, int[] modeCost, int count, Span<int> order,
        byte[]? rec2, int recBase2, int srcBase2, ref byte above2)
    {
        for (int m = 0; m < 4; m++)
        {
            order[m] = m;
        }

        if (count >= 4)
        {
            return 4;
        }

        Span<long> score = stackalloc long[4];
        double lambdaSad = Math.Sqrt(lambda / 256.0);
        for (int m = 0; m < 4; m++)
        {
            Vp8Decoder.PredictLarge(ref rec[recBase], recStride, size, m, mbY > 0, mbX > 0, ref above);
            long satd = Vp8EncoderKernels.Satd(ref src[srcBase], srcStride, ref rec[recBase], recStride, size, size);
            if (rec2 is not null)
            {
                Vp8Decoder.PredictLarge(ref rec2[recBase2], recStride, size, m, mbY > 0, mbX > 0, ref above2);
                satd += Vp8EncoderKernels.Satd(ref _srcV[srcBase2], srcStride, ref rec2[recBase2], recStride, size, size);
            }

            score[m] = (long)(satd * 256 + lambdaSad * modeCost[m]);
        }

        for (int i = 0; i < count; i++)
        {
            int best = i;
            for (int j = i + 1; j < 4; j++)
            {
                if (score[order[j]] < score[order[best]])
                {
                    best = j;
                }
            }

            (order[i], order[best]) = (order[best], order[i]);
        }

        return count;
    }

    private void EncodeMacroblock(int mbX, int mbY, ref uint aboveNz, ref uint leftNz, Span<byte> aboveB, Span<byte> leftB,
        Span<byte> aboveRows, MacroblockScratch s)
    {
        int mb = mbY * _mbW + mbX;
        int segment = _segment[mb];
        ref SegmentQuant q = ref _quant[segment];
        Span<short> levels = _levels.AsSpan(mb * 25 * 16, 25 * 16);
        levels.Clear();
        int recBase = (mbY * 16) * _recStride + RecLeft + mbX * 16;
        int srcBase = (mbY * 16) * _srcStride + mbX * 16;
        ref byte aboveY = ref aboveRows[Vp8Decoder.AboveY];

        // ---- 16×16 luma
        long bestI16 = long.MaxValue;
        int bestMode16 = 0;
        uint nzI16Above = 0, nzI16Left = 0;
        Span<int> order = stackalloc int[4];
        int i16Count = PreselectLarge(_recY, recBase, _recStride, _srcY, srcBase, _srcStride, 16, ref aboveY, mbX, mbY,
            q.LambdaI16, _yModeCost, _config.I16Candidates, order, null, 0, 0, ref aboveY);
        for (int k = 0; k < i16Count; k++)
        {
            int mode = order[k];
            Vp8Decoder.PredictLarge(ref _recY[recBase], _recStride, 16, mode, mbY > 0, mbX > 0, ref aboveY);
            uint a = aboveNz, l = leftNz;
            long cost = TryI16(recBase, srcBase, q, s, ref a, ref l);
            cost += q.LambdaI16 * _yModeCost[mode] / 256;
            if (cost < bestI16)
            {
                bestI16 = cost;
                bestMode16 = mode;
                nzI16Above = a;
                nzI16Left = l;
                s.Prediction.AsSpan().CopyTo(s.BestI16);
                s.LevelsI16.AsSpan().CopyTo(s.BestLevelsI16);
            }
        }

        // ---- B_PRED: 4×4 modes chosen block by block, reconstructed in place.
        long costI4 = q.LambdaI4 * _yModeCost[4] / 256;
        uint i4Above = aboveNz, i4Left = leftNz;
        bool i4Possible = _config.Effort > 0;
        if (i4Possible)
        {
            for (int b = 0; b < 16 && costI4 < bestI16; b++)
            {
                int bx = b & 3, by = b >> 2;
                int aboveMode = by == 0 ? aboveB[bx] : s.Modes[b - 4];
                int leftMode = bx == 0 ? leftB[by] : s.Modes[b - 1];
                ref byte dst = ref _recY[recBase + by * 4 * _recStride + bx * 4];
                ref byte above4 = ref Vp8Decoder.SubblockAbove(ref dst, _recStride, bx, by, ref aboveY, aboveRows);
                int srcOff = srcBase + by * 4 * _srcStride + bx * 4;
                int ctx = (int)(((i4Above >> bx) & 1) + ((i4Left >> by) & 1));
                long bestBlock = long.MaxValue;
                int bestMode = 0, bestNz = 0;
                int modeRow = (aboveMode * 10 + leftMode) * 10;
                Vp8Decoder.PredictSubblockAll(ref dst, _recStride, ref above4, s.Predictions);
                int candidates = PreselectI4(srcOff, q, modeRow, s);
                for (int k = 0; k < candidates; k++)
                {
                    int mode = s.Candidates[k];
                    long c = TryBlock4(mode, srcOff, q, ctx, s, out int nz);
                    long j = c + q.LambdaI4 * _bModeCost[(aboveMode * 10 + leftMode) * 10 + mode] / 256;
                    if (j < bestBlock)
                    {
                        bestBlock = j;
                        bestMode = mode;
                        bestNz = nz;
                        s.Block.AsSpan().CopyTo(s.BestBlock);
                        s.Trial.AsSpan().CopyTo(s.BestTrial);
                    }
                }

                costI4 += bestBlock;
                s.Modes[b] = (byte)bestMode;
                s.BestTrial.AsSpan().CopyTo(s.LevelsI4.AsSpan(b * 16, 16));
                for (int r = 0; r < 4; r++)
                {
                    s.BestBlock.AsSpan(r * 4, 4).CopyTo(_recY.AsSpan(recBase + (by * 4 + r) * _recStride + bx * 4, 4));
                }

                i4Above = (i4Above & ~(1u << bx)) | ((uint)bestNz << bx);
                i4Left = (i4Left & ~(1u << by)) | ((uint)bestNz << by);
            }
        }

        bool useI4 = i4Possible && costI4 < bestI16;
        if (useI4)
        {
            _yMode[mb] = 4;
            s.Modes.AsSpan().CopyTo(_bModes.AsSpan(mb * 16, 16));
            for (int b = 0; b < 16; b++)
            {
                s.LevelsI4.AsSpan(b * 16, 16).CopyTo(levels.Slice(b * 16, 16));
            }

            levels.Slice(24 * 16, 16).Clear();
            aboveNz = (aboveNz & ~0xFu) | (i4Above & 0xF);
            leftNz = (leftNz & ~0xFu) | (i4Left & 0xF);
            for (int i = 0; i < 4; i++)
            {
                aboveB[i] = s.Modes[12 + i];
                leftB[i] = s.Modes[i * 4 + 3];
            }
        }
        else
        {
            _yMode[mb] = (byte)bestMode16;
            for (int r = 0; r < 16; r++)
            {
                s.BestI16.AsSpan(r * 16, 16).CopyTo(_recY.AsSpan(recBase + r * _recStride, 16));
            }

            s.BestLevelsI16.AsSpan(0, 16 * 16).CopyTo(levels);
            s.BestLevelsI16.AsSpan(16 * 16, 16).CopyTo(levels.Slice(24 * 16, 16));
            aboveNz = (aboveNz & ~(0xFu | NzY2)) | (nzI16Above & (0xFu | NzY2));
            leftNz = (leftNz & ~(0xFu | NzY2)) | (nzI16Left & (0xFu | NzY2));
            byte derived = bestMode16 switch { 0 => 0, 1 => 2, 2 => 3, _ => 1 };
            aboveB.Fill(derived);
            leftB.Fill(derived);
        }

        // ---- chroma
        int recUvBase = (mbY * 8) * _recUvStride + RecLeft + mbX * 8;
        int srcUvBase = (mbY * 8) * _srcUvStride + mbX * 8;
        long bestUv = long.MaxValue;
        int bestUvMode = 0;
        uint uvAbove = 0, uvLeft = 0;
        int uvCount = PreselectLarge(_recU, recUvBase, _recUvStride, _srcU, srcUvBase, _srcUvStride, 8, ref aboveRows[Vp8Decoder.AboveU],
            mbX, mbY, q.LambdaUv, _uvModeCost, _config.UvCandidates, order, _recV, recUvBase, srcUvBase, ref aboveRows[Vp8Decoder.AboveV]);
        for (int k = 0; k < uvCount; k++)
        {
            int mode = order[k];
            Vp8Decoder.PredictLarge(ref _recU[recUvBase], _recUvStride, 8, mode, mbY > 0, mbX > 0, ref aboveRows[Vp8Decoder.AboveU]);
            Vp8Decoder.PredictLarge(ref _recV[recUvBase], _recUvStride, 8, mode, mbY > 0, mbX > 0, ref aboveRows[Vp8Decoder.AboveV]);
            uint a = aboveNz, l = leftNz;
            long cost = TryChroma(recUvBase, srcUvBase, q, s, ref a, ref l) + q.LambdaUv * _uvModeCost[mode] / 256;
            if (cost < bestUv)
            {
                bestUv = cost;
                bestUvMode = mode;
                uvAbove = a;
                uvLeft = l;
                s.LevelsUv.AsSpan().CopyTo(levels.Slice(16 * 16, 8 * 16));
                CopyChroma(recUvBase, s, toBest: true);
            }
        }

        _uvMode[mb] = (byte)bestUvMode;
        CopyChroma(recUvBase, s, toBest: false);
        aboveNz = (aboveNz & ~0xF0u) | (uvAbove & 0xF0);
        leftNz = (leftNz & ~0xF0u) | (uvLeft & 0xF0);

        bool skip = levels.IndexOfAnyExcept((short)0) < 0;
        _skip[mb] = (byte)(skip ? 1 : 0);
    }

    private void CopyChroma(int recUvBase, MacroblockScratch s, bool toBest)
    {
        for (int r = 0; r < 8; r++)
        {
            Span<byte> u = _recU.AsSpan(recUvBase + r * _recUvStride, 8), v = _recV.AsSpan(recUvBase + r * _recUvStride, 8);
            if (toBest)
            {
                u.CopyTo(s.BestU.AsSpan(r * 8));
                v.CopyTo(s.BestV.AsSpan(r * 8));
            }
            else
            {
                s.BestU.AsSpan(r * 8, 8).CopyTo(u);
                s.BestV.AsSpan(r * 8, 8).CopyTo(v);
            }
        }
    }

    // Non-zero context bits: Y columns/rows 0-3, U 4-5, V 6-7, Y2 24 (as the decoder keeps them).
    private const uint NzY2 = 1u << 24;

    /// <summary>
    /// The I16 candidate predicted at <paramref name="recBase"/>: residual, transforms, quantisation,
    /// reconstruction (into <c>s.Prediction</c>, the frame keeps the prediction) and its cost without
    /// the mode bits. Updates the Y and Y2 contexts as the candidate would leave them.
    /// </summary>
    private long TryI16(int recBase, int srcBase, in SegmentQuant q, MacroblockScratch s, ref uint aboveNz, ref uint leftNz)
    {
        Span<short> dequant = s.Dequant;
        dequant.Clear();
        for (int r = 0; r < 16; r++)
        {
            _recY.AsSpan(recBase + r * _recStride, 16).CopyTo(s.Prediction.AsSpan(r * 16));
        }

        Span<short> levels = s.LevelsI16;
        Span<int> lastAc = stackalloc int[16];
        int rDc = _config.RoundingDc, rAc = _config.RoundingAc;
        for (int b = 0; b < 16; b++)
        {
            int bx = b & 3, by = b >> 2;
            Vp8EncoderKernels.ResidualDct(ref _srcY[srcBase + by * 4 * _srcStride + bx * 4], _srcStride,
                ref s.Prediction[by * 64 + bx * 4], 16, s.Coefficients);
            s.Dc[b] = s.Coefficients[0];
            levels[b * 16] = 0;
            lastAc[b] = Vp8EncoderKernels.Quantize(s.Coefficients, levels.Slice(b * 16, 16), dequant.Slice(b * 16, 16), 1,
                q.YDc, q.IYDc, rDc, q.YAc, q.IYAc, rAc);
        }

        Vp8Fdct.ForwardWalsh(s.Dc, s.Y2);
        Span<short> y2Levels = levels.Slice(16 * 16, 16);
        Span<short> y2Dequant = s.Y2Dequant;
        int lastY2 = Vp8EncoderKernels.Quantize(s.Y2, y2Levels, y2Dequant, 0, q.Y2Dc, q.IY2Dc, rDc, q.Y2Ac, q.IY2Ac, rAc);

        // Rate: Y2 first, then the 16 blocks from position 1, contexts as the decoder walks them.
        int ctx2 = (int)(((aboveNz >> 24) & 1) + ((leftNz >> 24) & 1));
        long rate = BlockCost(y2Levels, 0, lastY2, 1, ctx2);
        uint nz2 = lastY2 >= 0 ? NzY2 : 0;
        aboveNz = (aboveNz & ~NzY2) | nz2;
        leftNz = (leftNz & ~NzY2) | nz2;
        if (lastY2 >= 0)
        {
            Vp8Decoder.InverseWalshToBlocks(ref y2Dequant[0], ref dequant[0], lastY2 > 0);
        }

        for (int b = 0; b < 16; b++)
        {
            int bx = b & 3, by = b >> 2;
            int ctx = (int)(((aboveNz >> bx) & 1) + ((leftNz >> by) & 1));
            int last = lastAc[b];
            rate += BlockCost(levels.Slice(b * 16, 16), 1, last, 0, ctx);
            uint nz = last >= 0 ? 1u : 0u;
            aboveNz = (aboveNz & ~(1u << bx)) | (nz << bx);
            leftNz = (leftNz & ~(1u << by)) | (nz << by);
            if (last >= 0 || dequant[b * 16] != 0)
            {
                Vp8Decoder.InverseDctAdd(ref dequant[b * 16], ref s.Prediction[by * 64 + bx * 4], 16, s.Residual16, last > 0);
            }
        }

        long distortion = Vp8EncoderKernels.Sse(ref _srcY[srcBase], _srcStride, ref s.Prediction[0], 16, 16, 16);
        return (distortion << 8) + q.LambdaI16 * rate / 256;
    }

    /// <summary>
    /// One 4×4 block coded with <paramref name="mode"/>'s prediction from <c>s.Predictions</c>: its levels
    /// (into <c>s.Trial</c>), its reconstruction (into <c>s.Block</c>) and its cost.
    /// </summary>
    private long TryBlock4(int mode, int srcOff, in SegmentQuant q, int ctx, MacroblockScratch s, out int nonZero)
    {
        s.Predictions.AsSpan(mode * 16, 16).CopyTo(s.Block);

        Vp8EncoderKernels.ResidualDct(ref _srcY[srcOff], _srcStride, ref s.Block[0], 4, s.Coefficients);
        Span<short> levels = s.Trial;
        Span<short> deq = s.Deq4;
        int last = Vp8EncoderKernels.Quantize(s.Coefficients, levels, deq, 0, q.YDc, q.IYDc, _config.RoundingDc, q.YAc, q.IYAc,
            _config.RoundingAc);
        long rate = BlockCost(levels, 0, last, 3, ctx);
        nonZero = last >= 0 ? 1 : 0;
        if (last >= 0)
        {
            Vp8Decoder.InverseDctAdd(ref deq[0], ref s.Block[0], 4, s.Residual16, last > 0);
        }

        long distortion = Vp8EncoderKernels.Sse(ref _srcY[srcOff], _srcStride, ref s.Block[0], 4, 4, 4);
        return (distortion << 8) + q.LambdaI4 * rate / 256;
    }

    /// <summary>The chroma candidate predicted in the frame: both planes' 4 blocks, reconstructed in the frame.</summary>
    private long TryChroma(int recUvBase, int srcUvBase, in SegmentQuant q, MacroblockScratch s, ref uint aboveNz, ref uint leftNz)
    {
        Span<short> deq = s.Deq4;
        long rate = 0, distortion = 0;
        for (int plane = 0; plane < 2; plane++)
        {
            byte[] rec = plane == 0 ? _recU : _recV, src = plane == 0 ? _srcU : _srcV;
            for (int b = 0; b < 4; b++)
            {
                int bx = b & 1, by = b >> 1;
                int recOff = recUvBase + by * 4 * _recUvStride + bx * 4, srcOff = srcUvBase + by * 4 * _srcUvStride + bx * 4;
                Vp8EncoderKernels.ResidualDct(ref src[srcOff], _srcUvStride, ref rec[recOff], _recUvStride, s.Coefficients);
                Span<short> levels = s.LevelsUv.AsSpan((plane * 4 + b) * 16, 16);
                int last = Vp8EncoderKernels.Quantize(s.Coefficients, levels, deq, 0, q.UvDc, q.IUvDc, _config.RoundingDc, q.UvAc,
                    q.IUvAc, _config.RoundingAc);
                int abit = 4 + plane * 2 + bx, lbit = 4 + plane * 2 + by;
                int ctx = (int)(((aboveNz >> abit) & 1) + ((leftNz >> lbit) & 1));
                rate += BlockCost(levels, 0, last, 2, ctx);
                uint nz = last >= 0 ? 1u : 0u;
                aboveNz = (aboveNz & ~(1u << abit)) | (nz << abit);
                leftNz = (leftNz & ~(1u << lbit)) | (nz << lbit);
                if (last >= 0)
                {
                    Vp8Decoder.InverseDctAdd(ref deq[0], ref rec[recOff], _recUvStride, s.Residual16, last > 0);
                }
            }

            distortion += Vp8EncoderKernels.Sse(ref src[srcUvBase], _srcUvStride, ref rec[recUvBase], _recUvStride, 8, 8);
        }

        return (distortion << 8) + q.LambdaUv * rate / 256;
    }

    /// <summary>Zigzag index of the last non-zero level at or after <paramref name="start"/>, or −1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LastNonZero(ReadOnlySpan<short> levels, int start)
    {
        int last = levels.LastIndexOfAnyExcept((short)0);
        return last >= start ? last : -1;
    }

    /// <summary>The bits (1/256) of one block's tokens, given the zigzag index of its last non-zero level (−1 = none).</summary>
    private int BlockCost(ReadOnlySpan<short> levels, int start, int last, int type, int ctx)
    {
        int[] withEob = _tokenCosts.WithEob, noEob = _tokenCosts.NoEob;
        ReadOnlySpan<byte> bands = Vp8Tables.CoefficientBands;
        if (last < start)
        {
            return withEob[Vp8TokenCosts.Row(type, bands[start], ctx) + 11];
        }

        int cost = 0;
        bool afterZero = false;
        for (int i = start; i <= last; i++)
        {
            int level = Math.Abs((int)levels[i]);
            int row = Vp8TokenCosts.Row(type, bands[i], ctx);
            cost += (afterZero ? noEob : withEob)[row + Vp8EncoderCosts.ClassOf[level]] + Vp8EncoderCosts.ExtraAndSign[level];
            ctx = level == 0 ? 0 : level == 1 ? 1 : 2;
            afterZero = level == 0;
        }

        if (last < 15)
        {
            cost += withEob[Vp8TokenCosts.Row(type, bands[last + 1], ctx) + 11];
        }

        return cost;
    }
}
