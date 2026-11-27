namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// AV1 Inter prediction (§7.11.3): 8-tap / 4-tap subpel interpolation, scaled references,
/// global &amp; local warped motion (§7.11.3.5–8), OBMC (§7.11.3.9–10), compound average,
/// distance-weighted (JNT), wedge mask, difference-weighted mask, and InterIntra blending.
/// </summary>
internal sealed class Av1InterPredict
{
    private readonly Av1SequenceHeader _seq;
    private readonly Av1FrameHeader _fh;
    private readonly Av1Picture _pic;
    private readonly Av1FrameState _st;
    private readonly Av1DecoderSession _session;

    // Reusable per-tile scratch buffers (max block = 128x128)
    private readonly int[] _pred0 = new int[128 * 128];
    private readonly int[] _pred1 = new int[128 * 128];
    private readonly int[] _obmcPred = new int[128 * 128];
    // Intermediate height for scaled refs up to 2x scale: ((127 * 2048 + 1023) >> 10) + 8 = 263 -> 288
    private readonly int[] _interBuf = new int[288 * 128];
    private readonly int[] _warpInter = new int[15 * 8];
    private readonly byte[] _mask = new byte[128 * 128];
    private int _maskW;

    // Local warp state shared across planes of a block
    private readonly int[] _localWarpParams = new int[6];
    private bool _localValid;

    internal Av1InterPredict(
        Av1SequenceHeader seq,
        Av1FrameHeader fh,
        Av1Picture pic,
        Av1FrameState st,
        Av1DecoderSession session)
    {
        _seq = seq;
        _fh = fh;
        _pic = pic;
        _st = st;
        _session = session;
    }

    internal bool IsScaled(int refFrame)
    {
        int refIdx = _fh.RefFrameIdx[refFrame - Av1Constants.LastFrame];
        var slot = _session.RefFrames[refIdx];
        if (!slot.Valid) return false;
        int xScale = ((slot.UpscaledWidth << Av1Constants.RefScaleShift) + (_fh.FrameWidth / 2)) / _fh.FrameWidth;
        int yScale = ((slot.FrameHeight << Av1Constants.RefScaleShift) + (_fh.FrameHeight / 2)) / _fh.FrameHeight;
        const int noScale = 1 << Av1Constants.RefScaleShift;
        return xScale != noScale || yScale != noScale;
    }

    internal void PredictInter(
        int plane,
        int x,
        int y,
        int w,
        int h,
        int candRow,
        int candCol,
        int miRow,
        int miCol,
        int miSize,
        int yMode,
        int motionMode,
        int compoundType,
        bool isInterIntra,
        int interIntraMode,
        bool wedgeInterIntra,
        int wedgeIndex,
        int wedgeSign,
        int maskType,
        bool useIntrabc,
        bool availU,
        bool availL,
        int numWarpSamples,
        ReadOnlySpan<int> warpCandList)
    {
        int candIdx = candRow * _st.MiCols + candCol;
        bool isCompound = _st.RefFrames[candIdx * 2 + 1] > Av1Constants.IntraFrame;

        // §7.11.3.2 Rounding variables
        int bd = _seq.BitDepth;
        int interRound0 = 3 + (bd == 12 ? 2 : 0);
        int interRound1 = (isCompound ? 7 : 11) - ((bd == 12 && !isCompound) ? 2 : 0);
        int interPostRound = 2 * Av1Constants.FilterBits - (interRound0 + interRound1);

        if (plane == 0 && motionMode == Av1Constants.MotionLocalWarp)
        {
            _localValid = EstimateLocalWarp(miRow, miCol, miSize, candIdx, numWarpSamples, warpCandList);
            if (_localValid)
                _localValid = SetupShear(_localWarpParams, out _, out _, out _, out _);
        }

        int numLists = isCompound ? 2 : 1;
        Span<int> gmSpan = stackalloc int[6];
        for (int refList = 0; refList < numLists; refList++)
        {
            int[] predDst = refList == 0 ? _pred0 : _pred1;
            int refFrame = _st.RefFrames[candIdx * 2 + refList];
            bool globalValid = false;
            if ((yMode == Av1Constants.GlobalMv || yMode == Av1Constants.GlobalGlobalMv)
                && refFrame > Av1Constants.IntraFrame
                && _fh.GmType[refFrame] > Av1Constants.GmTranslation)
            {
                for (int k = 0; k < 6; k++)
                    gmSpan[k] = _fh.GmParams[refFrame, k];
                globalValid = SetupShear(gmSpan, out _, out _, out _, out _);
            }

            int useWarp;
            if (w < 8 || h < 8 || _fh.ForceIntegerMv)
                useWarp = 0;
            else if (motionMode == Av1Constants.MotionLocalWarp && _localValid)
                useWarp = 1;
            else if ((yMode == Av1Constants.GlobalMv || yMode == Av1Constants.GlobalGlobalMv)
                && refFrame > Av1Constants.IntraFrame
                && _fh.GmType[refFrame] > Av1Constants.GmTranslation
                && !IsScaled(refFrame)
                && globalValid)
                useWarp = 2;
            else
                useWarp = 0;

            int mvY = _st.Mvs[candIdx * 4 + refList * 2];
            int mvX = _st.Mvs[candIdx * 4 + refList * 2 + 1];
            int refIdx = useIntrabc ? -1 : _fh.RefFrameIdx[refFrame - Av1Constants.LastFrame];

            if (useWarp != 0)
            {
                ReadOnlySpan<int> wp = useWarp == 1 ? _localWarpParams : gmSpan;
                for (int i8 = 0; i8 <= ((h - 1) >> 3); i8++)
                {
                    for (int j8 = 0; j8 <= ((w - 1) >> 3); j8++)
                    {
                        BlockWarp(plane, refIdx, wp, x, y, i8, j8, w, h, interRound0, interRound1, predDst);
                    }
                }
            }
            else
            {
                ScaleMv(plane, refIdx, x, y, mvY, mvX, out int startX, out int startY, out int stepX, out int stepY);
                BlockInterPrediction(plane, refIdx, startX, startY, stepX, stepY, w, h, candIdx, interRound0, interRound1, predDst);
            }
        }

        // Prepare Mask (§7.11.3.1)
        if (compoundType == Av1Constants.CompoundWedge && plane == 0)
        {
            FillWedgeMask(miSize, wedgeSign, wedgeIndex, w, h);
        }
        else if (compoundType == Av1Constants.CompoundIntra)
        {
            FillIntraVariantMask(interIntraMode, w, h);
        }
        else if (compoundType == Av1Constants.CompoundDiffWtd && plane == 0)
        {
            FillDiffWtdMask(w, h, bd, interPostRound, maskType);
        }

        ushort[] dstPlane = _pic.Planes[plane];
        int dstStride = _pic.PlaneStrides[plane];
        int maxSample = (1 << bd) - 1;

        if (!isCompound && !isInterIntra)
        {
            for (int r = 0; r < h; r++)
            {
                int dstOff = (y + r) * dstStride + x;
                int srcOff = r * w;
                for (int c = 0; c < w; c++)
                    dstPlane[dstOff + c] = (ushort)Math.Clamp(_pred0[srcOff + c], 0, maxSample);
            }
        }
        else if (compoundType == Av1Constants.CompoundAverage)
        {
            int shift = 1 + interPostRound;
            for (int r = 0; r < h; r++)
            {
                int dstOff = (y + r) * dstStride + x;
                int srcOff = r * w;
                for (int c = 0; c < w; c++)
                    dstPlane[dstOff + c] = (ushort)Math.Clamp(Av1Constants.Round2(_pred0[srcOff + c] + _pred1[srcOff + c], shift), 0, maxSample);
            }
        }
        else if (compoundType == Av1Constants.CompoundDistance)
        {
            ComputeDistanceWeights(candIdx, out int fwdWeight, out int bckWeight);
            int shift = 4 + interPostRound;
            for (int r = 0; r < h; r++)
            {
                int dstOff = (y + r) * dstStride + x;
                int srcOff = r * w;
                for (int c = 0; c < w; c++)
                {
                    int val = Av1Constants.Round2(fwdWeight * _pred0[srcOff + c] + bckWeight * _pred1[srcOff + c], shift);
                    dstPlane[dstOff + c] = (ushort)Math.Clamp(val, 0, maxSample);
                }
            }
        }
        else
        {
            MaskBlend(plane, x, y, w, h, bd, interPostRound, isInterIntra, wedgeInterIntra);
        }

        if (motionMode == Av1Constants.MotionObmc)
        {
            ApplyObmc(plane, w, h, miRow, miCol, miSize, availU, availL, bd, interRound0, interRound1);
        }
    }

    // §7.11.3.3 Motion vector scaling process
    private void ScaleMv(
        int plane, int refIdx, int x, int y, int mvY, int mvX,
        out int startX, out int startY, out int stepX, out int stepY)
    {
        int refUpW, refH;
        if (refIdx < 0)
        {
            refUpW = _fh.UpscaledWidth;
            refH = _fh.FrameHeight;
        }
        else
        {
            var slot = _session.RefFrames[refIdx];
            if (!slot.Valid || slot.Picture is null)
                throw new InvalidDataException($"AV1 inter prediction references invalid frame slot {refIdx}.");
            refUpW = slot.UpscaledWidth;
            refH = slot.FrameHeight;
            if (2 * _fh.FrameWidth < refUpW || 2 * _fh.FrameHeight < refH
                || _fh.FrameWidth > 16 * refUpW || _fh.FrameHeight > 16 * refH)
            {
                throw new InvalidDataException("AV1 reference frame scaling ratio out of specification bounds.");
            }
        }

        int xScale = ((refUpW << Av1Constants.RefScaleShift) + (_fh.FrameWidth / 2)) / _fh.FrameWidth;
        int yScale = ((refH << Av1Constants.RefScaleShift) + (_fh.FrameHeight / 2)) / _fh.FrameHeight;
        int subX = plane > 0 ? _seq.SubsamplingX : 0;
        int subY = plane > 0 ? _seq.SubsamplingY : 0;
        const int halfSample = 1 << (Av1Constants.SubpelBits - 1);

        long origX = (x << Av1Constants.SubpelBits) + ((2 * mvX) >> subX) + halfSample;
        long origY = (y << Av1Constants.SubpelBits) + ((2 * mvY) >> subY) + halfSample;
        long baseX = origX * xScale - ((long)halfSample << Av1Constants.RefScaleShift);
        long baseY = origY * yScale - ((long)halfSample << Av1Constants.RefScaleShift);
        const int off = (1 << (Av1Constants.ScaleSubpelBits - Av1Constants.SubpelBits)) / 2;

        startX = (int)Av1Constants.Round2Signed(baseX, Av1Constants.RefScaleShift + Av1Constants.SubpelBits - Av1Constants.ScaleSubpelBits) + off;
        startY = (int)Av1Constants.Round2Signed(baseY, Av1Constants.RefScaleShift + Av1Constants.SubpelBits - Av1Constants.ScaleSubpelBits) + off;
        stepX = Av1Constants.Round2Signed(xScale, Av1Constants.RefScaleShift - Av1Constants.ScaleSubpelBits);
        stepY = Av1Constants.Round2Signed(yScale, Av1Constants.RefScaleShift - Av1Constants.ScaleSubpelBits);
    }

    // §7.11.3.4 Block inter prediction process
    private void BlockInterPrediction(
        int plane, int refIdx, int x, int y, int xStep, int yStep,
        int w, int h, int candIdx, int interRound0, int interRound1, int[] pred)
    {
        Av1Picture refPic;
        int refUpW, refH;
        if (refIdx < 0)
        {
            refPic = _pic;
            refUpW = _fh.MiCols * 4;
            refH = _fh.MiRows * 4;
        }
        else
        {
            var slot = _session.RefFrames[refIdx];
            if (!slot.Valid || slot.Picture is null)
                throw new InvalidDataException($"AV1 inter prediction references invalid frame slot {refIdx}.");
            refPic = slot.Picture;
            refUpW = slot.UpscaledWidth;
            refH = slot.FrameHeight;
        }

        int subX = plane > 0 ? _seq.SubsamplingX : 0;
        int subY = plane > 0 ? _seq.SubsamplingY : 0;
        int lastX = ((refUpW + subX) >> subX) - 1;
        int lastY = ((refH + subY) >> subY) - 1;
        ushort[] refBuf = refPic.Planes[plane];
        int refStride = refPic.PlaneStrides[plane];

        int intermediateHeight = (((h - 1) * yStep + (1 << Av1Constants.ScaleSubpelBits) - 1) >> Av1Constants.ScaleSubpelBits) + 8;

        int hFilter = _st.InterpFilters[candIdx * 2 + 1];
        if (w <= 4)
        {
            if (hFilter is Av1Constants.EightTap or Av1Constants.EightTapSharp)
                hFilter = 4;
            else if (hFilter == Av1Constants.EightTapSmooth)
                hFilter = 5;
        }

        int yBase = y >> 10;
        int hFilterBase = hFilter * 128;
        for (int r = 0; r < intermediateHeight; r++)
        {
            int srcRow = Av1Constants.Clip3(0, lastY, yBase + r - 3) * refStride;
            int interRow = r * w;
            for (int c = 0; c < w; c++)
            {
                int p = x + xStep * c;
                int phaseOff = hFilterBase + (((p >> 6) & Av1Constants.SubpelMask) << 3);
                int pInt = (p >> 10) - 3;
                int s = 0;
                for (int t = 0; t < 8; t++)
                    s += SubpelFilters[phaseOff + t] * refBuf[srcRow + Av1Constants.Clip3(0, lastX, pInt + t)];
                _interBuf[interRow + c] = Av1Constants.Round2(s, interRound0);
            }
        }

        int vFilter = _st.InterpFilters[candIdx * 2];
        if (h <= 4)
        {
            if (vFilter is Av1Constants.EightTap or Av1Constants.EightTapSharp)
                vFilter = 4;
            else if (vFilter == Av1Constants.EightTapSmooth)
                vFilter = 5;
        }

        int yFrac = y & 1023;
        int vFilterBase = vFilter * 128;
        for (int r = 0; r < h; r++)
        {
            int p = yFrac + yStep * r;
            int phaseOff = vFilterBase + (((p >> 6) & Av1Constants.SubpelMask) << 3);
            int pInt = p >> 10;
            int dstRow = r * w;
            for (int c = 0; c < w; c++)
            {
                int s = 0;
                for (int t = 0; t < 8; t++)
                    s += SubpelFilters[phaseOff + t] * _interBuf[(pInt + t) * w + c];
                pred[dstRow + c] = Av1Constants.Round2(s, interRound1);
            }
        }
    }

    // §7.11.3.5 Block warp process
    private void BlockWarp(
        int plane, int refIdx, ReadOnlySpan<int> warpParams,
        int x, int y, int i8, int j8, int w, int h,
        int interRound0, int interRound1, int[] pred)
    {
        var slot = _session.RefFrames[refIdx];
        if (!slot.Valid || slot.Picture is null)
            throw new InvalidDataException($"AV1 warped prediction references invalid frame slot {refIdx}.");

        int subX = plane > 0 ? _seq.SubsamplingX : 0;
        int subY = plane > 0 ? _seq.SubsamplingY : 0;
        int lastX = ((slot.UpscaledWidth + subX) >> subX) - 1;
        int lastY = ((slot.FrameHeight + subY) >> subY) - 1;
        ushort[] refBuf = slot.Picture.Planes[plane];
        int refStride = slot.Picture.PlaneStrides[plane];

        int srcX = (x + j8 * 8 + 4) << subX;
        int srcY = (y + i8 * 8 + 4) << subY;
        long dstX = (long)warpParams[2] * srcX + (long)warpParams[3] * srcY + warpParams[0];
        long dstY = (long)warpParams[4] * srcX + (long)warpParams[5] * srcY + warpParams[1];

        SetupShear(warpParams, out int alpha, out int beta, out int gamma, out int delta);

        long x4 = dstX >> subX;
        long y4 = dstY >> subY;
        int ix4 = (int)(x4 >> Av1Constants.WarpedModelPrecBits);
        int sx4 = (int)(x4 & ((1 << Av1Constants.WarpedModelPrecBits) - 1));
        int iy4 = (int)(y4 >> Av1Constants.WarpedModelPrecBits);
        int sy4 = (int)(y4 & ((1 << Av1Constants.WarpedModelPrecBits) - 1));

        for (int i1 = -7; i1 < 8; i1++)
        {
            int srcRow = Av1Constants.Clip3(0, lastY, iy4 + i1) * refStride;
            int interRow = (i1 + 7) * 8;
            for (int i2 = -4; i2 < 4; i2++)
            {
                int sx = sx4 + alpha * i2 + beta * i1;
                int offs = Av1Constants.Clip3(0, 192, Av1Constants.Round2(sx, Av1Constants.WarpedDiffPrecBits) + Av1Constants.WarpedPixelPrecShifts);
                int fOff = offs << 3;
                int xBase = ix4 + i2 - 3;
                int s = 0;
                for (int i3 = 0; i3 < 8; i3++)
                    s += WarpedFilters[fOff + i3] * refBuf[srcRow + Av1Constants.Clip3(0, lastX, xBase + i3)];
                _warpInter[interRow + (i2 + 4)] = Av1Constants.Round2(s, interRound0);
            }
        }

        int rLimit = Math.Min(4, h - i8 * 8 - 4);
        int cLimit = Math.Min(4, w - j8 * 8 - 4);
        for (int i1 = -4; i1 < rLimit; i1++)
        {
            int dstRow = (i8 * 8 + i1 + 4) * w + j8 * 8 + 4;
            for (int i2 = -4; i2 < cLimit; i2++)
            {
                int sy = sy4 + gamma * i2 + delta * i1;
                int offs = Av1Constants.Clip3(0, 192, Av1Constants.Round2(sy, Av1Constants.WarpedDiffPrecBits) + Av1Constants.WarpedPixelPrecShifts);
                int fOff = offs << 3;
                int s = 0;
                for (int i3 = 0; i3 < 8; i3++)
                    s += WarpedFilters[fOff + i3] * _warpInter[(i1 + i3 + 4) * 8 + (i2 + 4)];
                pred[dstRow + i2] = Av1Constants.Round2(s, interRound1);
            }
        }
    }

    // §7.11.3.6 Setup shear process
    internal static bool SetupShear(ReadOnlySpan<int> warpParams, out int alpha, out int beta, out int gamma, out int delta)
    {
        if (warpParams[2] == 0)
        {
            alpha = beta = gamma = delta = 0;
            return false;
        }
        int alpha0 = Av1Constants.Clip3(-32768, 32767, warpParams[2] - (1 << Av1Constants.WarpedModelPrecBits));
        int beta0 = Av1Constants.Clip3(-32768, 32767, warpParams[3]);
        ResolveDivisor(warpParams[2], out int divShift, out int divFactor);

        long v = (long)warpParams[4] << Av1Constants.WarpedModelPrecBits;
        int gamma0 = (int)Av1Constants.Clip3(-32768L, 32767L, Av1Constants.Round2Signed(v * divFactor, divShift));
        long w = (long)warpParams[3] * warpParams[4];
        int delta0 = (int)Av1Constants.Clip3(-32768L, 32767L,
            warpParams[5] - Av1Constants.Round2Signed(w * divFactor, divShift) - (1 << Av1Constants.WarpedModelPrecBits));

        alpha = Av1Constants.Round2Signed(alpha0, Av1Constants.WarpParamReduceBits) << Av1Constants.WarpParamReduceBits;
        beta = Av1Constants.Round2Signed(beta0, Av1Constants.WarpParamReduceBits) << Av1Constants.WarpParamReduceBits;
        gamma = Av1Constants.Round2Signed(gamma0, Av1Constants.WarpParamReduceBits) << Av1Constants.WarpParamReduceBits;
        delta = Av1Constants.Round2Signed(delta0, Av1Constants.WarpParamReduceBits) << Av1Constants.WarpParamReduceBits;

        if (4 * Math.Abs(alpha) + 7 * Math.Abs(beta) >= (1 << Av1Constants.WarpedModelPrecBits))
            return false;
        if (4 * Math.Abs(gamma) + 4 * Math.Abs(delta) >= (1 << Av1Constants.WarpedModelPrecBits))
            return false;
        return true;
    }

    // §7.11.3.7 Resolve divisor process
    private static void ResolveDivisor(long d, out int divShift, out int divFactor)
    {
        long absD = Math.Abs(d);
        int n = Av1Constants.FloorLog2(absD);
        long e = absD - (1L << n);
        int f = n > Av1Constants.DivLutBits
            ? (int)Av1Constants.Round2(e, n - Av1Constants.DivLutBits)
            : (int)(e << (Av1Constants.DivLutBits - n));
        divShift = n + Av1Constants.DivLutPrecBits;
        divFactor = d < 0 ? -DivLut[f] : DivLut[f];
    }

    // §7.11.3.8 Warp estimation process
    private bool EstimateLocalWarp(int miRow, int miCol, int miSize, int candIdx, int numSamples, ReadOnlySpan<int> candList)
    {
        long a00 = 0, a01 = 0, a11 = 0;
        long bx0 = 0, bx1 = 0, by0 = 0, by1 = 0;

        int w4 = Av1Constants.Num4x4BlocksWide[miSize];
        int h4 = Av1Constants.Num4x4BlocksHigh[miSize];
        int midY = miRow * 4 + h4 * 2 - 1;
        int midX = miCol * 4 + w4 * 2 - 1;
        int suy = midY * 8;
        int sux = midX * 8;
        int mvY = _st.Mvs[candIdx * 4];
        int mvX = _st.Mvs[candIdx * 4 + 1];
        int duy = suy + mvY;
        int dux = sux + mvX;

        for (int i = 0; i < numSamples; i++)
        {
            long sy = candList[i * 4] - suy;
            long sx = candList[i * 4 + 1] - sux;
            long dy = candList[i * 4 + 2] - duy;
            long dx = candList[i * 4 + 3] - dux;
            if (Math.Abs(sx - dx) < Av1Constants.LsMvMax && Math.Abs(sy - dy) < Av1Constants.LsMvMax)
            {
                a00 += LsProduct(sx, sx) + 8;
                a01 += LsProduct(sx, sy) + 4;
                a11 += LsProduct(sy, sy) + 8;
                bx0 += LsProduct(sx, dx) + 8;
                bx1 += LsProduct(sy, dx) + 4;
                by0 += LsProduct(sx, dy) + 4;
                by1 += LsProduct(sy, dy) + 8;
            }
        }

        long det = a00 * a11 - a01 * a01;
        if (det == 0) return false;

        ResolveDivisor(det, out int divShift, out int divFactor);
        long df = divFactor;
        divShift -= Av1Constants.WarpedModelPrecBits;
        if (divShift < 0)
        {
            df <<= -divShift;
            divShift = 0;
        }

        int diagLo = (1 << Av1Constants.WarpedModelPrecBits) - Av1Constants.WarpedModelNonDiagAffineClamp + 1;
        int diagHi = (1 << Av1Constants.WarpedModelPrecBits) + Av1Constants.WarpedModelNonDiagAffineClamp - 1;
        int nonDiagLo = -Av1Constants.WarpedModelNonDiagAffineClamp + 1;
        int nonDiagHi = Av1Constants.WarpedModelNonDiagAffineClamp - 1;

        _localWarpParams[2] = (int)Av1Constants.Clip3(diagLo, diagHi, Av1Constants.Round2Signed((a11 * bx0 - a01 * bx1) * df, divShift));
        _localWarpParams[3] = (int)Av1Constants.Clip3(nonDiagLo, nonDiagHi, Av1Constants.Round2Signed((-a01 * bx0 + a00 * bx1) * df, divShift));
        _localWarpParams[4] = (int)Av1Constants.Clip3(nonDiagLo, nonDiagHi, Av1Constants.Round2Signed((a11 * by0 - a01 * by1) * df, divShift));
        _localWarpParams[5] = (int)Av1Constants.Clip3(diagLo, diagHi, Av1Constants.Round2Signed((-a01 * by0 + a00 * by1) * df, divShift));

        long vx = ((long)mvX << (Av1Constants.WarpedModelPrecBits - 3))
            - ((long)midX * (_localWarpParams[2] - (1 << Av1Constants.WarpedModelPrecBits)) + (long)midY * _localWarpParams[3]);
        long vy = ((long)mvY << (Av1Constants.WarpedModelPrecBits - 3))
            - ((long)midX * _localWarpParams[4] + (long)midY * (_localWarpParams[5] - (1 << Av1Constants.WarpedModelPrecBits)));

        _localWarpParams[0] = (int)Av1Constants.Clip3(-Av1Constants.WarpedModelTransClamp, Av1Constants.WarpedModelTransClamp - 1, vx);
        _localWarpParams[1] = (int)Av1Constants.Clip3(-Av1Constants.WarpedModelTransClamp, Av1Constants.WarpedModelTransClamp - 1, vy);
        return true;
    }

    private static long LsProduct(long a, long b) => ((a * b) >> 2) + (a + b);

    // §7.11.3.9–10 Overlapped motion compensation
    private void ApplyObmc(
        int plane, int w, int h, int miRow, int miCol, int miSize,
        bool availU, bool availL, int bd, int interRound0, int interRound1)
    {
        int subX = plane > 0 ? _seq.SubsamplingX : 0;
        int subY = plane > 0 ? _seq.SubsamplingY : 0;
        int maxSample = (1 << bd) - 1;
        ushort[] dstPlane = _pic.Planes[plane];
        int dstStride = _pic.PlaneStrides[plane];

        if (availU && Av1Constants.SubsampledSize(miSize, subX, subY) >= Av1Constants.Block8x8)
        {
            int w4 = Av1Constants.Num4x4BlocksWide[miSize];
            int x4 = miCol;
            int y4 = miRow;
            int nCount = 0;
            int nLimit = Math.Min(4, Av1Constants.MiWidthLog2[miSize]);
            while (nCount < nLimit && x4 < Math.Min(_fh.MiCols, miCol + w4))
            {
                int candRow = miRow - 1;
                int candCol = x4 | 1;
                int candIdx = candRow * _st.MiCols + candCol;
                int candSz = _st.MiSizes[candIdx];
                int step4 = Av1Constants.Clip3(2, 16, Av1Constants.Num4x4BlocksWide[candSz]);
                if (_st.RefFrames[candIdx * 2] > Av1Constants.IntraFrame)
                {
                    nCount++;
                    int predW = Math.Min(w, (step4 * 4) >> subX);
                    int predH = Math.Min(h >> 1, 32 >> subY);
                    byte[] mask = GetObmcMask(predH);
                    PredictAndBlendOverlap(plane, candIdx, (x4 * 4) >> subX, (y4 * 4) >> subY,
                        predW, predH, pass: 0, mask, maxSample, interRound0, interRound1, dstPlane, dstStride);
                }
                x4 += step4;
            }
        }

        if (availL)
        {
            int h4 = Av1Constants.Num4x4BlocksHigh[miSize];
            int x4 = miCol;
            int y4 = miRow;
            int nCount = 0;
            int nLimit = Math.Min(4, Av1Constants.MiHeightLog2[miSize]);
            while (nCount < nLimit && y4 < Math.Min(_fh.MiRows, miRow + h4))
            {
                int candCol = miCol - 1;
                int candRow = y4 | 1;
                int candIdx = candRow * _st.MiCols + candCol;
                int candSz = _st.MiSizes[candIdx];
                int step4 = Av1Constants.Clip3(2, 16, Av1Constants.Num4x4BlocksHigh[candSz]);
                if (_st.RefFrames[candIdx * 2] > Av1Constants.IntraFrame)
                {
                    nCount++;
                    int predW = Math.Min(w >> 1, 32 >> subX);
                    int predH = Math.Min(h, (step4 * 4) >> subY);
                    byte[] mask = GetObmcMask(predW);
                    PredictAndBlendOverlap(plane, candIdx, (x4 * 4) >> subX, (y4 * 4) >> subY,
                        predW, predH, pass: 1, mask, maxSample, interRound0, interRound1, dstPlane, dstStride);
                }
                y4 += step4;
            }
        }
    }

    private void PredictAndBlendOverlap(
        int plane, int candIdx, int predX, int predY, int predW, int predH,
        int pass, byte[] mask, int maxSample, int interRound0, int interRound1,
        ushort[] dstPlane, int dstStride)
    {
        int mvY = _st.Mvs[candIdx * 4];
        int mvX = _st.Mvs[candIdx * 4 + 1];
        int refIdx = _fh.RefFrameIdx[_st.RefFrames[candIdx * 2] - Av1Constants.LastFrame];
        ScaleMv(plane, refIdx, predX, predY, mvY, mvX, out int startX, out int startY, out int stepX, out int stepY);
        BlockInterPrediction(plane, refIdx, startX, startY, stepX, stepY, predW, predH, candIdx, interRound0, interRound1, _obmcPred);

        for (int i = 0; i < predH; i++)
        {
            int dstOff = (predY + i) * dstStride + predX;
            int srcOff = i * predW;
            for (int j = 0; j < predW; j++)
            {
                int m = pass == 0 ? mask[i] : mask[j];
                int op = Math.Clamp(_obmcPred[srcOff + j], 0, maxSample);
                dstPlane[dstOff + j] = (ushort)Av1Constants.Round2(m * dstPlane[dstOff + j] + (64 - m) * op, 6);
            }
        }
    }

    private static byte[] GetObmcMask(int length) => length switch
    {
        2 => ObmcMask2,
        4 => ObmcMask4,
        8 => ObmcMask8,
        16 => ObmcMask16,
        _ => ObmcMask32,
    };

    // §7.11.3.11 Wedge mask
    private void FillWedgeMask(int miSize, int wedgeSign, int wedgeIndex, int w, int h)
    {
        var table = WedgeMasks[miSize];
        if (table is null)
        {
            Array.Fill(_mask, (byte)32, 0, w * h);
            _maskW = w;
            return;
        }
        int offset = ((wedgeSign * Av1Constants.WedgeTypes) + wedgeIndex) * w * h;
        table.AsSpan(offset, w * h).CopyTo(_mask);
        _maskW = w;
    }

    // §7.11.3.12 Difference weight mask
    private void FillDiffWtdMask(int w, int h, int bd, int interPostRound, int maskType)
    {
        int shift = (bd - 8) + interPostRound;
        for (int i = 0; i < h; i++)
        {
            int row = i * w;
            for (int j = 0; j < w; j++)
            {
                int diff = Math.Abs(_pred0[row + j] - _pred1[row + j]);
                diff = Av1Constants.Round2(diff, shift);
                int m = Av1Constants.Clip3(0, 64, 38 + diff / 16);
                _mask[row + j] = (byte)(maskType != 0 ? 64 - m : m);
            }
        }
        _maskW = w;
    }

    // §7.11.3.13 Intra mode variant mask
    private void FillIntraVariantMask(int interIntraMode, int w, int h)
    {
        int sizeScale = 128 / Math.Max(h, w);
        for (int i = 0; i < h; i++)
        {
            int row = i * w;
            for (int j = 0; j < w; j++)
            {
                _mask[row + j] = interIntraMode switch
                {
                    Av1Constants.IiVPred => IiWeights1d[i * sizeScale],
                    Av1Constants.IiHPred => IiWeights1d[j * sizeScale],
                    Av1Constants.IiSmoothPred => IiWeights1d[Math.Min(i, j) * sizeScale],
                    _ => 32,
                };
            }
        }
        _maskW = w;
    }

    // §7.11.3.14 Mask blend process
    private void MaskBlend(int plane, int dstX, int dstY, int w, int h, int bd, int interPostRound, bool isInterIntra, bool wedgeInterIntra)
    {
        int subX = plane > 0 ? _seq.SubsamplingX : 0;
        int subY = plane > 0 ? _seq.SubsamplingY : 0;
        bool directMask = (subX == 0 && subY == 0) || (isInterIntra && !wedgeInterIntra);
        ushort[] dstPlane = _pic.Planes[plane];
        int dstStride = _pic.PlaneStrides[plane];
        int maxSample = (1 << bd) - 1;
        int compShift = 6 + interPostRound;

        for (int r = 0; r < h; r++)
        {
            int dstOff = (dstY + r) * dstStride + dstX;
            int srcOff = r * w;
            for (int c = 0; c < w; c++)
            {
                int m;
                if (directMask)
                    m = _mask[r * _maskW + c];
                else if (subX != 0 && subY == 0)
                    m = Av1Constants.Round2(_mask[r * _maskW + 2 * c] + _mask[r * _maskW + 2 * c + 1], 1);
                else if (subX == 0 && subY != 0)
                    m = Av1Constants.Round2(_mask[(2 * r) * _maskW + c] + _mask[(2 * r + 1) * _maskW + c], 1);
                else
                    m = Av1Constants.Round2(
                        _mask[(2 * r) * _maskW + 2 * c] + _mask[(2 * r) * _maskW + 2 * c + 1] +
                        _mask[(2 * r + 1) * _maskW + 2 * c] + _mask[(2 * r + 1) * _maskW + 2 * c + 1], 2);

                if (isInterIntra)
                {
                    int pred0 = Math.Clamp(Av1Constants.Round2(_pred0[srcOff + c], interPostRound), 0, maxSample);
                    int pred1 = dstPlane[dstOff + c];
                    dstPlane[dstOff + c] = (ushort)Av1Constants.Round2(m * pred1 + (64 - m) * pred0, 6);
                }
                else
                {
                    int val = Av1Constants.Round2(m * _pred0[srcOff + c] + (64 - m) * _pred1[srcOff + c], compShift);
                    dstPlane[dstOff + c] = (ushort)Math.Clamp(val, 0, maxSample);
                }
            }
        }
    }

    // §7.11.3.15 Distance weights
    private void ComputeDistanceWeights(int candIdx, out int fwdWeight, out int bckWeight)
    {
        int h0 = _fh.OrderHints[_st.RefFrames[candIdx * 2]];
        int h1 = _fh.OrderHints[_st.RefFrames[candIdx * 2 + 1]];
        int d1 = Av1Constants.Clip3(0, Av1Constants.MaxFrameDistance, Math.Abs(_seq.GetRelativeDist(h0, _fh.OrderHint)));
        int d0 = Av1Constants.Clip3(0, Av1Constants.MaxFrameDistance, Math.Abs(_seq.GetRelativeDist(h1, _fh.OrderHint)));
        int order = d0 <= d1 ? 1 : 0;
        if (d0 == 0 || d1 == 0)
        {
            fwdWeight = QuantDistLookup[3 * 2 + order];
            bckWeight = QuantDistLookup[3 * 2 + (1 - order)];
            return;
        }
        int i;
        for (i = 0; i < 3; i++)
        {
            int c0 = QuantDistWeight[i * 2 + order];
            int c1 = QuantDistWeight[i * 2 + (1 - order)];
            if (order != 0 ? (d0 * c0 > d1 * c1) : (d0 * c0 < d1 * c1))
                break;
        }
        fwdWeight = QuantDistLookup[i * 2 + order];
        bckWeight = QuantDistLookup[i * 2 + (1 - order)];
    }

    // Static tables (§7.11.3)
    private static readonly byte[] ObmcMask2 = [45, 64];
    private static readonly byte[] ObmcMask4 = [39, 50, 59, 64];
    private static readonly byte[] ObmcMask8 = [36, 42, 48, 53, 57, 61, 64, 64];
    private static readonly byte[] ObmcMask16 = [34, 37, 40, 43, 46, 49, 52, 54, 56, 58, 60, 61, 64, 64, 64, 64];
    private static readonly byte[] ObmcMask32 =
    [
        33, 35, 36, 38, 40, 41, 43, 44, 45, 47, 48, 50, 51, 52, 53, 55,
        56, 57, 58, 59, 60, 60, 61, 62, 64, 64, 64, 64, 64, 64, 64, 64,
    ];

    private static readonly byte[] IiWeights1d =
    [
        60, 58, 56, 54, 52, 50, 48, 47, 45, 44, 42, 41, 39, 38, 37, 35, 34, 33, 32,
        31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 22, 21, 20, 19, 19, 18, 18, 17, 16,
        16, 15, 15, 14, 14, 13, 13, 12, 12, 12, 11, 11, 10, 10, 10,  9,  9,  9,  8,
         8,  8,  8,  7,  7,  7,  7,  6,  6,  6,  6,  6,  5,  5,  5,  5,  5,  4,  4,
         4,  4,  4,  4,  4,  4,  3,  3,  3,  3,  3,  3,  3,  3,  3,  2,  2,  2,  2,
         2,  2,  2,  2,  2,  2,  2,  2,  2,  2,  2,  1,  1,  1,  1,  1,  1,  1,  1,
         1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
    ];

    private static readonly int[] QuantDistWeight = [2, 3, 2, 5, 2, 7, 1, Av1Constants.MaxFrameDistance];
    private static readonly int[] QuantDistLookup = [9, 7, 11, 5, 12, 4, 13, 3];

    private static readonly byte[] WedgeMasterObliqueOdd =
    [
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0,0,0,0,0,1,2,6,18,
        37,53,60,63,64,64,64,64,64,64,64,64,64,64,64,64,
        64,64,64,64,64,64,64,64,64,64,64,64,64,64,64,64,
    ];
    private static readonly byte[] WedgeMasterObliqueEven =
    [
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0,0,0,0,0,1,4,11,27,
        46,58,62,63,64,64,64,64,64,64,64,64,64,64,64,64,
        64,64,64,64,64,64,64,64,64,64,64,64,64,64,64,64,
    ];
    private static readonly byte[] WedgeMasterVertical =
    [
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0,0,0,0,0,0,2,7,21,
        43,57,62,64,64,64,64,64,64,64,64,64,64,64,64,64,
        64,64,64,64,64,64,64,64,64,64,64,64,64,64,64,64,
    ];

    private static readonly byte[,,] WedgeCodebook =
    {
        {
            { Av1Constants.WedgeOblique27, 4, 4 }, { Av1Constants.WedgeOblique63, 4, 4 },
            { Av1Constants.WedgeOblique117, 4, 4 }, { Av1Constants.WedgeOblique153, 4, 4 },
            { Av1Constants.WedgeHorizontal, 4, 2 }, { Av1Constants.WedgeHorizontal, 4, 4 },
            { Av1Constants.WedgeHorizontal, 4, 6 }, { Av1Constants.WedgeVertical, 4, 4 },
            { Av1Constants.WedgeOblique27, 4, 2 }, { Av1Constants.WedgeOblique27, 4, 6 },
            { Av1Constants.WedgeOblique153, 4, 2 }, { Av1Constants.WedgeOblique153, 4, 6 },
            { Av1Constants.WedgeOblique63, 2, 4 }, { Av1Constants.WedgeOblique63, 6, 4 },
            { Av1Constants.WedgeOblique117, 2, 4 }, { Av1Constants.WedgeOblique117, 6, 4 },
        },
        {
            { Av1Constants.WedgeOblique27, 4, 4 }, { Av1Constants.WedgeOblique63, 4, 4 },
            { Av1Constants.WedgeOblique117, 4, 4 }, { Av1Constants.WedgeOblique153, 4, 4 },
            { Av1Constants.WedgeVertical, 2, 4 }, { Av1Constants.WedgeVertical, 4, 4 },
            { Av1Constants.WedgeVertical, 6, 4 }, { Av1Constants.WedgeHorizontal, 4, 4 },
            { Av1Constants.WedgeOblique27, 4, 2 }, { Av1Constants.WedgeOblique27, 4, 6 },
            { Av1Constants.WedgeOblique153, 4, 2 }, { Av1Constants.WedgeOblique153, 4, 6 },
            { Av1Constants.WedgeOblique63, 2, 4 }, { Av1Constants.WedgeOblique63, 6, 4 },
            { Av1Constants.WedgeOblique117, 2, 4 }, { Av1Constants.WedgeOblique117, 6, 4 },
        },
        {
            { Av1Constants.WedgeOblique27, 4, 4 }, { Av1Constants.WedgeOblique63, 4, 4 },
            { Av1Constants.WedgeOblique117, 4, 4 }, { Av1Constants.WedgeOblique153, 4, 4 },
            { Av1Constants.WedgeHorizontal, 4, 2 }, { Av1Constants.WedgeHorizontal, 4, 6 },
            { Av1Constants.WedgeVertical, 2, 4 }, { Av1Constants.WedgeVertical, 6, 4 },
            { Av1Constants.WedgeOblique27, 4, 2 }, { Av1Constants.WedgeOblique27, 4, 6 },
            { Av1Constants.WedgeOblique153, 4, 2 }, { Av1Constants.WedgeOblique153, 4, 6 },
            { Av1Constants.WedgeOblique63, 2, 4 }, { Av1Constants.WedgeOblique63, 6, 4 },
            { Av1Constants.WedgeOblique117, 2, 4 }, { Av1Constants.WedgeOblique117, 6, 4 },
        },
    };

    private static readonly byte[]?[] WedgeMasks = BuildWedgeMaskTable();

    private static byte[]?[] BuildWedgeMaskTable()
    {
        const int sz = Av1Constants.MaskMasterSize;
        var master = new byte[6, sz, sz];
        for (int j = 0; j < sz; j++)
        {
            int shift = sz / 4;
            for (int i = 0; i < sz; i += 2)
            {
                master[Av1Constants.WedgeOblique63, i, j] = WedgeMasterObliqueEven[Av1Constants.Clip3(0, sz - 1, j - shift)];
                shift--;
                master[Av1Constants.WedgeOblique63, i + 1, j] = WedgeMasterObliqueOdd[Av1Constants.Clip3(0, sz - 1, j - shift)];
                master[Av1Constants.WedgeVertical, i, j] = WedgeMasterVertical[j];
                master[Av1Constants.WedgeVertical, i + 1, j] = WedgeMasterVertical[j];
            }
        }
        for (int i = 0; i < sz; i++)
        {
            for (int j = 0; j < sz; j++)
            {
                byte msk = master[Av1Constants.WedgeOblique63, i, j];
                master[Av1Constants.WedgeOblique27, j, i] = msk;
                master[Av1Constants.WedgeOblique117, i, sz - 1 - j] = (byte)(64 - msk);
                master[Av1Constants.WedgeOblique153, sz - 1 - j, i] = (byte)(64 - msk);
                master[Av1Constants.WedgeHorizontal, j, i] = master[Av1Constants.WedgeVertical, i, j];
            }
        }

        var result = new byte[]?[Av1Constants.BlockSizes];
        for (int bsize = Av1Constants.Block8x8; bsize < Av1Constants.BlockSizes; bsize++)
        {
            if (Av1Constants.WedgeBits[bsize] <= 0) continue;
            int w = Av1Constants.BlockWidth[bsize];
            int h = Av1Constants.BlockHeight[bsize];
            int shape = h > w ? 0 : h < w ? 1 : 2;
            var table = new byte[2 * Av1Constants.WedgeTypes * w * h];
            for (int wedge = 0; wedge < Av1Constants.WedgeTypes; wedge++)
            {
                int dir = WedgeCodebook[shape, wedge, 0];
                int xoff = sz / 2 - ((WedgeCodebook[shape, wedge, 1] * w) >> 3);
                int yoff = sz / 2 - ((WedgeCodebook[shape, wedge, 2] * h) >> 3);
                int sum = 0;
                for (int i = 0; i < w; i++) sum += master[dir, yoff, xoff + i];
                for (int i = 1; i < h; i++) sum += master[dir, yoff + i, xoff];
                int avg = (sum + (w + h - 1) / 2) / (w + h - 1);
                int flipSign = avg < 32 ? 1 : 0;
                int offA = ((flipSign * Av1Constants.WedgeTypes) + wedge) * w * h;
                int offB = (((1 - flipSign) * Av1Constants.WedgeTypes) + wedge) * w * h;
                for (int i = 0; i < h; i++)
                {
                    for (int j = 0; j < w; j++)
                    {
                        byte m = master[dir, yoff + i, xoff + j];
                        table[offA + i * w + j] = m;
                        table[offB + i * w + j] = (byte)(64 - m);
                    }
                }
            }
            result[bsize] = table;
        }
        return result;
    }

    internal static readonly short[] SubpelFilters =
    [
           0,    0,    0,  128,    0,    0,    0,    0,
           0,    2,   -6,  126,    8,   -2,    0,    0,
           0,    2,  -10,  122,   18,   -4,    0,    0,
           0,    2,  -12,  116,   28,   -8,    2,    0,
           0,    2,  -14,  110,   38,  -10,    2,    0,
           0,    2,  -14,  102,   48,  -12,    2,    0,
           0,    2,  -16,   94,   58,  -12,    2,    0,
           0,    2,  -14,   84,   66,  -12,    2,    0,
           0,    2,  -14,   76,   76,  -14,    2,    0,
           0,    2,  -12,   66,   84,  -14,    2,    0,
           0,    2,  -12,   58,   94,  -16,    2,    0,
           0,    2,  -12,   48,  102,  -14,    2,    0,
           0,    2,  -10,   38,  110,  -14,    2,    0,
           0,    2,   -8,   28,  116,  -12,    2,    0,
           0,    0,   -4,   18,  122,  -10,    2,    0,
           0,    0,   -2,    8,  126,   -6,    2,    0,
           0,    0,    0,  128,    0,    0,    0,    0,
           0,    2,   28,   62,   34,    2,    0,    0,
           0,    0,   26,   62,   36,    4,    0,    0,
           0,    0,   22,   62,   40,    4,    0,    0,
           0,    0,   20,   60,   42,    6,    0,    0,
           0,    0,   18,   58,   44,    8,    0,    0,
           0,    0,   16,   56,   46,   10,    0,    0,
           0,   -2,   16,   54,   48,   12,    0,    0,
           0,   -2,   14,   52,   52,   14,   -2,    0,
           0,    0,   12,   48,   54,   16,   -2,    0,
           0,    0,   10,   46,   56,   16,    0,    0,
           0,    0,    8,   44,   58,   18,    0,    0,
           0,    0,    6,   42,   60,   20,    0,    0,
           0,    0,    4,   40,   62,   22,    0,    0,
           0,    0,    4,   36,   62,   26,    0,    0,
           0,    0,    2,   34,   62,   28,    2,    0,
           0,    0,    0,  128,    0,    0,    0,    0,
          -2,    2,   -6,  126,    8,   -2,    2,    0,
          -2,    6,  -12,  124,   16,   -6,    4,   -2,
          -2,    8,  -18,  120,   26,  -10,    6,   -2,
          -4,   10,  -22,  116,   38,  -14,    6,   -2,
          -4,   10,  -22,  108,   48,  -18,    8,   -2,
          -4,   10,  -24,  100,   60,  -20,    8,   -2,
          -4,   10,  -24,   90,   70,  -22,   10,   -2,
          -4,   12,  -24,   80,   80,  -24,   12,   -4,
          -2,   10,  -22,   70,   90,  -24,   10,   -4,
          -2,    8,  -20,   60,  100,  -24,   10,   -4,
          -2,    8,  -18,   48,  108,  -22,   10,   -4,
          -2,    6,  -14,   38,  116,  -22,   10,   -4,
          -2,    6,  -10,   26,  120,  -18,    8,   -2,
          -2,    4,   -6,   16,  124,  -12,    6,   -2,
           0,    2,   -2,    8,  126,   -6,    2,   -2,
           0,    0,    0,  128,    0,    0,    0,    0,
           0,    0,    0,  120,    8,    0,    0,    0,
           0,    0,    0,  112,   16,    0,    0,    0,
           0,    0,    0,  104,   24,    0,    0,    0,
           0,    0,    0,   96,   32,    0,    0,    0,
           0,    0,    0,   88,   40,    0,    0,    0,
           0,    0,    0,   80,   48,    0,    0,    0,
           0,    0,    0,   72,   56,    0,    0,    0,
           0,    0,    0,   64,   64,    0,    0,    0,
           0,    0,    0,   56,   72,    0,    0,    0,
           0,    0,    0,   48,   80,    0,    0,    0,
           0,    0,    0,   40,   88,    0,    0,    0,
           0,    0,    0,   32,   96,    0,    0,    0,
           0,    0,    0,   24,  104,    0,    0,    0,
           0,    0,    0,   16,  112,    0,    0,    0,
           0,    0,    0,    8,  120,    0,    0,    0,
           0,    0,    0,  128,    0,    0,    0,    0,
           0,    0,   -4,  126,    8,   -2,    0,    0,
           0,    0,   -8,  122,   18,   -4,    0,    0,
           0,    0,  -10,  116,   28,   -6,    0,    0,
           0,    0,  -12,  110,   38,   -8,    0,    0,
           0,    0,  -12,  102,   48,  -10,    0,    0,
           0,    0,  -14,   94,   58,  -10,    0,    0,
           0,    0,  -12,   84,   66,  -10,    0,    0,
           0,    0,  -12,   76,   76,  -12,    0,    0,
           0,    0,  -10,   66,   84,  -12,    0,    0,
           0,    0,  -10,   58,   94,  -14,    0,    0,
           0,    0,  -10,   48,  102,  -12,    0,    0,
           0,    0,   -8,   38,  110,  -12,    0,    0,
           0,    0,   -6,   28,  116,  -10,    0,    0,
           0,    0,   -4,   18,  122,   -8,    0,    0,
           0,    0,   -2,    8,  126,   -4,    0,    0,
           0,    0,    0,  128,    0,    0,    0,    0,
           0,    0,   30,   62,   34,    2,    0,    0,
           0,    0,   26,   62,   36,    4,    0,    0,
           0,    0,   22,   62,   40,    4,    0,    0,
           0,    0,   20,   60,   42,    6,    0,    0,
           0,    0,   18,   58,   44,    8,    0,    0,
           0,    0,   16,   56,   46,   10,    0,    0,
           0,    0,   14,   54,   48,   12,    0,    0,
           0,    0,   12,   52,   52,   12,    0,    0,
           0,    0,   12,   48,   54,   14,    0,    0,
           0,    0,   10,   46,   56,   16,    0,    0,
           0,    0,    8,   44,   58,   18,    0,    0,
           0,    0,    6,   42,   60,   20,    0,    0,
           0,    0,    4,   40,   62,   22,    0,    0,
           0,    0,    4,   36,   62,   26,    0,    0,
           0,    0,    2,   34,   62,   30,    0,    0,
    ];

    internal static readonly short[] WarpedFilters =
    [
           0,    0,  127,    1,    0,    0,    0,    0,
           0,   -1,  127,    2,    0,    0,    0,    0,
           1,   -3,  127,    4,   -1,    0,    0,    0,
           1,   -4,  126,    6,   -2,    1,    0,    0,
           1,   -5,  126,    8,   -3,    1,    0,    0,
           1,   -6,  125,   11,   -4,    1,    0,    0,
           1,   -7,  124,   13,   -4,    1,    0,    0,
           2,   -8,  123,   15,   -5,    1,    0,    0,
           2,   -9,  122,   18,   -6,    1,    0,    0,
           2,  -10,  121,   20,   -6,    1,    0,    0,
           2,  -11,  120,   22,   -7,    2,    0,    0,
           2,  -12,  119,   25,   -8,    2,    0,    0,
           3,  -13,  117,   27,   -8,    2,    0,    0,
           3,  -13,  116,   29,   -9,    2,    0,    0,
           3,  -14,  114,   32,  -10,    3,    0,    0,
           3,  -15,  113,   35,  -10,    2,    0,    0,
           3,  -15,  111,   37,  -11,    3,    0,    0,
           3,  -16,  109,   40,  -11,    3,    0,    0,
           3,  -16,  108,   42,  -12,    3,    0,    0,
           4,  -17,  106,   45,  -13,    3,    0,    0,
           4,  -17,  104,   47,  -13,    3,    0,    0,
           4,  -17,  102,   50,  -14,    3,    0,    0,
           4,  -17,  100,   52,  -14,    3,    0,    0,
           4,  -18,   98,   55,  -15,    4,    0,    0,
           4,  -18,   96,   58,  -15,    3,    0,    0,
           4,  -18,   94,   60,  -16,    4,    0,    0,
           4,  -18,   91,   63,  -16,    4,    0,    0,
           4,  -18,   89,   65,  -16,    4,    0,    0,
           4,  -18,   87,   68,  -17,    4,    0,    0,
           4,  -18,   85,   70,  -17,    4,    0,    0,
           4,  -18,   82,   73,  -17,    4,    0,    0,
           4,  -18,   80,   75,  -17,    4,    0,    0,
           4,  -18,   78,   78,  -18,    4,    0,    0,
           4,  -17,   75,   80,  -18,    4,    0,    0,
           4,  -17,   73,   82,  -18,    4,    0,    0,
           4,  -17,   70,   85,  -18,    4,    0,    0,
           4,  -17,   68,   87,  -18,    4,    0,    0,
           4,  -16,   65,   89,  -18,    4,    0,    0,
           4,  -16,   63,   91,  -18,    4,    0,    0,
           4,  -16,   60,   94,  -18,    4,    0,    0,
           3,  -15,   58,   96,  -18,    4,    0,    0,
           4,  -15,   55,   98,  -18,    4,    0,    0,
           3,  -14,   52,  100,  -17,    4,    0,    0,
           3,  -14,   50,  102,  -17,    4,    0,    0,
           3,  -13,   47,  104,  -17,    4,    0,    0,
           3,  -13,   45,  106,  -17,    4,    0,    0,
           3,  -12,   42,  108,  -16,    3,    0,    0,
           3,  -11,   40,  109,  -16,    3,    0,    0,
           3,  -11,   37,  111,  -15,    3,    0,    0,
           2,  -10,   35,  113,  -15,    3,    0,    0,
           3,  -10,   32,  114,  -14,    3,    0,    0,
           2,   -9,   29,  116,  -13,    3,    0,    0,
           2,   -8,   27,  117,  -13,    3,    0,    0,
           2,   -8,   25,  119,  -12,    2,    0,    0,
           2,   -7,   22,  120,  -11,    2,    0,    0,
           1,   -6,   20,  121,  -10,    2,    0,    0,
           1,   -6,   18,  122,   -9,    2,    0,    0,
           1,   -5,   15,  123,   -8,    2,    0,    0,
           1,   -4,   13,  124,   -7,    1,    0,    0,
           1,   -4,   11,  125,   -6,    1,    0,    0,
           1,   -3,    8,  126,   -5,    1,    0,    0,
           1,   -2,    6,  126,   -4,    1,    0,    0,
           0,   -1,    4,  127,   -3,    1,    0,    0,
           0,    0,    2,  127,   -1,    0,    0,    0,
           0,    0,    0,  127,    1,    0,    0,    0,
           0,    0,   -1,  127,    2,    0,    0,    0,
           0,    1,   -3,  127,    4,   -2,    1,    0,
           0,    1,   -5,  127,    6,   -2,    1,    0,
           0,    2,   -6,  126,    8,   -3,    1,    0,
          -1,    2,   -7,  126,   11,   -4,    2,   -1,
          -1,    3,   -8,  125,   13,   -5,    2,   -1,
          -1,    3,  -10,  124,   16,   -6,    3,   -1,
          -1,    4,  -11,  123,   18,   -7,    3,   -1,
          -1,    4,  -12,  122,   20,   -7,    3,   -1,
          -1,    4,  -13,  121,   23,   -8,    3,   -1,
          -2,    5,  -14,  120,   25,   -9,    4,   -1,
          -1,    5,  -15,  119,   27,  -10,    4,   -1,
          -1,    5,  -16,  118,   30,  -11,    4,   -1,
          -2,    6,  -17,  116,   33,  -12,    5,   -1,
          -2,    6,  -17,  114,   35,  -12,    5,   -1,
          -2,    6,  -18,  113,   38,  -13,    5,   -1,
          -2,    7,  -19,  111,   41,  -14,    6,   -2,
          -2,    7,  -19,  110,   43,  -15,    6,   -2,
          -2,    7,  -20,  108,   46,  -15,    6,   -2,
          -2,    7,  -20,  106,   49,  -16,    6,   -2,
          -2,    7,  -21,  104,   51,  -16,    7,   -2,
          -2,    7,  -21,  102,   54,  -17,    7,   -2,
          -2,    8,  -21,  100,   56,  -18,    7,   -2,
          -2,    8,  -22,   98,   59,  -18,    7,   -2,
          -2,    8,  -22,   96,   62,  -19,    7,   -2,
          -2,    8,  -22,   94,   64,  -19,    7,   -2,
          -2,    8,  -22,   91,   67,  -20,    8,   -2,
          -2,    8,  -22,   89,   69,  -20,    8,   -2,
          -2,    8,  -22,   87,   72,  -21,    8,   -2,
          -2,    8,  -21,   84,   74,  -21,    8,   -2,
          -2,    8,  -22,   82,   77,  -21,    8,   -2,
          -2,    8,  -21,   79,   79,  -21,    8,   -2,
          -2,    8,  -21,   77,   82,  -22,    8,   -2,
          -2,    8,  -21,   74,   84,  -21,    8,   -2,
          -2,    8,  -21,   72,   87,  -22,    8,   -2,
          -2,    8,  -20,   69,   89,  -22,    8,   -2,
          -2,    8,  -20,   67,   91,  -22,    8,   -2,
          -2,    7,  -19,   64,   94,  -22,    8,   -2,
          -2,    7,  -19,   62,   96,  -22,    8,   -2,
          -2,    7,  -18,   59,   98,  -22,    8,   -2,
          -2,    7,  -18,   56,  100,  -21,    8,   -2,
          -2,    7,  -17,   54,  102,  -21,    7,   -2,
          -2,    7,  -16,   51,  104,  -21,    7,   -2,
          -2,    6,  -16,   49,  106,  -20,    7,   -2,
          -2,    6,  -15,   46,  108,  -20,    7,   -2,
          -2,    6,  -15,   43,  110,  -19,    7,   -2,
          -2,    6,  -14,   41,  111,  -19,    7,   -2,
          -1,    5,  -13,   38,  113,  -18,    6,   -2,
          -1,    5,  -12,   35,  114,  -17,    6,   -2,
          -1,    5,  -12,   33,  116,  -17,    6,   -2,
          -1,    4,  -11,   30,  118,  -16,    5,   -1,
          -1,    4,  -10,   27,  119,  -15,    5,   -1,
          -1,    4,   -9,   25,  120,  -14,    5,   -2,
          -1,    3,   -8,   23,  121,  -13,    4,   -1,
          -1,    3,   -7,   20,  122,  -12,    4,   -1,
          -1,    3,   -7,   18,  123,  -11,    4,   -1,
          -1,    3,   -6,   16,  124,  -10,    3,   -1,
          -1,    2,   -5,   13,  125,   -8,    3,   -1,
          -1,    2,   -4,   11,  126,   -7,    2,   -1,
           0,    1,   -3,    8,  126,   -6,    2,    0,
           0,    1,   -2,    6,  127,   -5,    1,    0,
           0,    1,   -2,    4,  127,   -3,    1,    0,
           0,    0,    0,    2,  127,   -1,    0,    0,
           0,    0,    0,    1,  127,    0,    0,    0,
           0,    0,    0,   -1,  127,    2,    0,    0,
           0,    0,    1,   -3,  127,    4,   -1,    0,
           0,    0,    1,   -4,  126,    6,   -2,    1,
           0,    0,    1,   -5,  126,    8,   -3,    1,
           0,    0,    1,   -6,  125,   11,   -4,    1,
           0,    0,    1,   -7,  124,   13,   -4,    1,
           0,    0,    2,   -8,  123,   15,   -5,    1,
           0,    0,    2,   -9,  122,   18,   -6,    1,
           0,    0,    2,  -10,  121,   20,   -6,    1,
           0,    0,    2,  -11,  120,   22,   -7,    2,
           0,    0,    2,  -12,  119,   25,   -8,    2,
           0,    0,    3,  -13,  117,   27,   -8,    2,
           0,    0,    3,  -13,  116,   29,   -9,    2,
           0,    0,    3,  -14,  114,   32,  -10,    3,
           0,    0,    3,  -15,  113,   35,  -10,    2,
           0,    0,    3,  -15,  111,   37,  -11,    3,
           0,    0,    3,  -16,  109,   40,  -11,    3,
           0,    0,    3,  -16,  108,   42,  -12,    3,
           0,    0,    4,  -17,  106,   45,  -13,    3,
           0,    0,    4,  -17,  104,   47,  -13,    3,
           0,    0,    4,  -17,  102,   50,  -14,    3,
           0,    0,    4,  -17,  100,   52,  -14,    3,
           0,    0,    4,  -18,   98,   55,  -15,    4,
           0,    0,    4,  -18,   96,   58,  -15,    3,
           0,    0,    4,  -18,   94,   60,  -16,    4,
           0,    0,    4,  -18,   91,   63,  -16,    4,
           0,    0,    4,  -18,   89,   65,  -16,    4,
           0,    0,    4,  -18,   87,   68,  -17,    4,
           0,    0,    4,  -18,   85,   70,  -17,    4,
           0,    0,    4,  -18,   82,   73,  -17,    4,
           0,    0,    4,  -18,   80,   75,  -17,    4,
           0,    0,    4,  -18,   78,   78,  -18,    4,
           0,    0,    4,  -17,   75,   80,  -18,    4,
           0,    0,    4,  -17,   73,   82,  -18,    4,
           0,    0,    4,  -17,   70,   85,  -18,    4,
           0,    0,    4,  -17,   68,   87,  -18,    4,
           0,    0,    4,  -16,   65,   89,  -18,    4,
           0,    0,    4,  -16,   63,   91,  -18,    4,
           0,    0,    4,  -16,   60,   94,  -18,    4,
           0,    0,    3,  -15,   58,   96,  -18,    4,
           0,    0,    4,  -15,   55,   98,  -18,    4,
           0,    0,    3,  -14,   52,  100,  -17,    4,
           0,    0,    3,  -14,   50,  102,  -17,    4,
           0,    0,    3,  -13,   47,  104,  -17,    4,
           0,    0,    3,  -13,   45,  106,  -17,    4,
           0,    0,    3,  -12,   42,  108,  -16,    3,
           0,    0,    3,  -11,   40,  109,  -16,    3,
           0,    0,    3,  -11,   37,  111,  -15,    3,
           0,    0,    2,  -10,   35,  113,  -15,    3,
           0,    0,    3,  -10,   32,  114,  -14,    3,
           0,    0,    2,   -9,   29,  116,  -13,    3,
           0,    0,    2,   -8,   27,  117,  -13,    3,
           0,    0,    2,   -8,   25,  119,  -12,    2,
           0,    0,    2,   -7,   22,  120,  -11,    2,
           0,    0,    1,   -6,   20,  121,  -10,    2,
           0,    0,    1,   -6,   18,  122,   -9,    2,
           0,    0,    1,   -5,   15,  123,   -8,    2,
           0,    0,    1,   -4,   13,  124,   -7,    1,
           0,    0,    1,   -4,   11,  125,   -6,    1,
           0,    0,    1,   -3,    8,  126,   -5,    1,
           0,    0,    1,   -2,    6,  126,   -4,    1,
           0,    0,    0,   -1,    4,  127,   -3,    1,
           0,    0,    0,    0,    2,  127,   -1,    0,
           0,    0,    0,    0,    2,  127,   -1,    0,
    ];

    internal static readonly short[] DivLut =
    [
        16384, 16320, 16257, 16194, 16132, 16070, 16009, 15948, 15888, 15828, 15768, 15709, 15650, 15592, 15534, 15477,
        15420, 15364, 15308, 15252, 15197, 15142, 15087, 15033, 14980, 14926, 14873, 14821, 14769, 14717, 14665, 14614,
        14564, 14513, 14463, 14413, 14364, 14315, 14266, 14218, 14170, 14122, 14075, 14028, 13981, 13935, 13888, 13843,
        13797, 13752, 13707, 13662, 13618, 13574, 13530, 13487, 13443, 13400, 13358, 13315, 13273, 13231, 13190, 13148,
        13107, 13066, 13026, 12985, 12945, 12906, 12866, 12827, 12788, 12749, 12710, 12672, 12633, 12596, 12558, 12520,
        12483, 12446, 12409, 12373, 12336, 12300, 12264, 12228, 12193, 12157, 12122, 12087, 12053, 12018, 11984, 11950,
        11916, 11882, 11848, 11815, 11782, 11749, 11716, 11683, 11651, 11619, 11586, 11555, 11523, 11491, 11460, 11429,
        11398, 11367, 11336, 11305, 11275, 11245, 11215, 11185, 11155, 11125, 11096, 11067, 11038, 11009, 10980, 10951,
        10923, 10894, 10866, 10838, 10810, 10782, 10755, 10727, 10700, 10673, 10645, 10618, 10592, 10565, 10538, 10512,
        10486, 10460, 10434, 10408, 10382, 10356, 10331, 10305, 10280, 10255, 10230, 10205, 10180, 10156, 10131, 10107,
        10082, 10058, 10034, 10010, 9986, 9963, 9939, 9916, 9892, 9869, 9846, 9823, 9800, 9777, 9754, 9732,
        9709, 9687, 9664, 9642, 9620, 9598, 9576, 9554, 9533, 9511, 9489, 9468, 9447, 9425, 9404, 9383,
        9362, 9341, 9321, 9300, 9279, 9259, 9239, 9218, 9198, 9178, 9158, 9138, 9118, 9098, 9079, 9059,
        9039, 9020, 9001, 8981, 8962, 8943, 8924, 8905, 8886, 8867, 8849, 8830, 8812, 8793, 8775, 8756,
        8738, 8720, 8702, 8684, 8666, 8648, 8630, 8613, 8595, 8577, 8560, 8542, 8525, 8508, 8490, 8473,
        8456, 8439, 8422, 8405, 8389, 8372, 8355, 8339, 8322, 8306, 8289, 8273, 8257, 8240, 8224, 8208,
        8192,
    ];
}
