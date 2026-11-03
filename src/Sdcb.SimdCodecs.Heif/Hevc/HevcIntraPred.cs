namespace Sdcb.SimdCodecs.Heif.Hevc;

/// <summary>
/// Intra sample prediction of H.265 §8.4.4.2.
/// </summary>
/// <remarks>
/// The 4N + 1 reference samples p[x][y] are held in one array <c>refs</c>, ordered the way the substitution
/// process of §8.4.4.2.2 scans them: index 0 is p[-1][2N-1] (bottom of the left column), index 2N - 1 - y is
/// p[-1][y], index 2N is the corner p[-1][-1], and index 2N + 1 + x is p[x][-1]. In that order both the
/// substitution and the [1 2 1] smoothing filter of §8.4.4.2.3 become one pass over consecutive entries.
/// </remarks>
internal static class HevcIntraPred
{
    private static readonly sbyte[] IntraPredAngle =
    [
        0, 0, 32, 26, 21, 17, 13, 9, 5, 2, 0, -2, -5, -9, -13, -17, -21, -26,
        -32, -26, -21, -17, -13, -9, -5, -2, 0, 2, 5, 9, 13, 17, 21, 26, 32,
    ];

    private static readonly short[] InvAngle =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        -4096, -1638, -910, -630, -482, -390, -315, -256, -315, -390, -482, -630, -910, -1638, -4096,
    ];

    /// <summary>
    /// Substitutes unavailable reference samples (§8.4.4.2.2). <paramref name="available"/> marks, per entry of
    /// <paramref name="refs"/>, whether the sample was available; <paramref name="anyAvailable"/> is false when none was.
    /// </summary>
    internal static void Substitute(Span<int> refs, ReadOnlySpan<bool> available, bool anyAvailable, int bitDepth)
    {
        if (!anyAvailable)
        {
            refs.Fill(1 << (bitDepth - 1));
            return;
        }
        if (!available[0])
        {
            var k = 1;
            while (!available[k]) k++;
            refs[0] = refs[k];
        }
        for (var i = 1; i < refs.Length; i++)
        {
            if (!available[i]) refs[i] = refs[i - 1];
        }
    }

    /// <summary>
    /// Predicts one N×N block into <paramref name="dst"/> (row stride <paramref name="stride"/>), applying the
    /// neighbouring-sample filter of §8.4.4.2.3 when <paramref name="filterNeighbours"/> is set (luma for 4:2:0).
    /// <paramref name="refs"/> is modified in place by the filter.
    /// </summary>
    internal static void Predict(Span<int> refs, int log2Size, int mode, bool isLuma, bool filterNeighbours,
        bool strongSmoothing, int bitDepth, Span<ushort> dst, int stride)
    {
        var n = 1 << log2Size;
        var corner = 2 * n;

        if (filterNeighbours && mode != 1 && n != 4)
        {
            var minDistVerHor = Math.Min(Math.Abs(mode - 26), Math.Abs(mode - 10));
            var threshold = n switch { 8 => 7, 16 => 1, _ => 0 };
            if (minDistVerHor > threshold) Filter(refs, n, strongSmoothing, bitDepth);
        }

        var max = (1 << bitDepth) - 1;
        if (mode == 0)
        {
            // INTRA_PLANAR (8-46).
            var topRight = refs[corner + 1 + n];
            var bottomLeft = refs[corner - 1 - n];
            for (var y = 0; y < n; y++)
            {
                var left = refs[corner - 1 - y];
                var row = dst.Slice(y * stride, n);
                for (var x = 0; x < n; x++)
                {
                    row[x] = (ushort)(((n - 1 - x) * left + (x + 1) * topRight +
                                       (n - 1 - y) * refs[corner + 1 + x] + (y + 1) * bottomLeft + n) >> (log2Size + 1));
                }
            }
            return;
        }

        if (mode == 1)
        {
            // INTRA_DC (8-47 .. 8-52).
            var sum = n;
            for (var i = 0; i < n; i++) sum += refs[corner + 1 + i] + refs[corner - 1 - i];
            var dcVal = sum >> (log2Size + 1);
            for (var y = 0; y < n; y++) dst.Slice(y * stride, n).Fill((ushort)dcVal);
            if (isLuma && n < 32)
            {
                dst[0] = (ushort)((refs[corner - 1] + 2 * dcVal + refs[corner + 1] + 2) >> 2);
                for (var x = 1; x < n; x++) dst[x] = (ushort)((refs[corner + 1 + x] + 3 * dcVal + 2) >> 2);
                for (var y = 1; y < n; y++) dst[y * stride] = (ushort)((refs[corner - 1 - y] + 3 * dcVal + 2) >> 2);
            }
            return;
        }

        // INTRA_ANGULAR2..34 (8-53 .. 8-68). ref[] is indexed from -n; refBuf[i + n] holds ref[i].
        var angle = (int)IntraPredAngle[mode];
        Span<int> refBuf = stackalloc int[3 * n + 1];
        var vertical = mode >= 18;

        // main(i) = p[-1 + i][-1] (vertical) or p[-1][-1 + i] (horizontal); side(i) is the other edge.
        for (var x = 0; x <= n; x++) refBuf[n + x] = vertical ? refs[corner + x] : refs[corner - x];
        if (angle < 0)
        {
            var last = (n * angle) >> 5;
            if (last < -1)
            {
                var inv = (int)InvAngle[mode];
                for (var x = last; x <= -1; x++)
                {
                    var s = -1 + ((x * inv + 128) >> 8);
                    // side(s): vertical → p[-1][s] (left column), horizontal → p[s][-1] (top row).
                    refBuf[n + x] = vertical ? refs[corner - 1 - s] : refs[corner + 1 + s];
                }
            }
        }
        else
        {
            for (var x = n + 1; x <= 2 * n; x++) refBuf[n + x] = vertical ? refs[corner + x] : refs[corner - x];
        }

        for (var j = 0; j < n; j++)
        {
            var iIdx = ((j + 1) * angle) >> 5;
            var iFact = ((j + 1) * angle) & 31;
            for (var i = 0; i < n; i++)
            {
                var a = refBuf[n + i + iIdx + 1];
                var value = iFact != 0 ? ((32 - iFact) * a + iFact * refBuf[n + i + iIdx + 2] + 16) >> 5 : a;
                // Vertical: j is the row y and i the column x; horizontal: the reverse.
                if (vertical) dst[j * stride + i] = (ushort)value;
                else dst[i * stride + j] = (ushort)value;
            }
        }

        if (isLuma && n < 32)
        {
            if (mode == 26)
            {
                for (var y = 0; y < n; y++)
                {
                    dst[y * stride] = (ushort)Math.Clamp(refs[corner + 1] + ((refs[corner - 1 - y] - refs[corner]) >> 1), 0, max);
                }
            }
            else if (mode == 10)
            {
                for (var x = 0; x < n; x++)
                {
                    dst[x] = (ushort)Math.Clamp(refs[corner - 1] + ((refs[corner + 1 + x] - refs[corner]) >> 1), 0, max);
                }
            }
        }
    }

    private static void Filter(Span<int> refs, int n, bool strongSmoothing, int bitDepth)
    {
        var corner = 2 * n;
        var last = 4 * n;
        if (strongSmoothing && n == 32)
        {
            var threshold = 1 << (bitDepth - 5);
            var c = refs[corner];
            var topEnd = refs[last];   // p[63][-1]
            var leftEnd = refs[0];     // p[-1][63]
            if (Math.Abs(c + topEnd - 2 * refs[corner + n]) < threshold &&
                Math.Abs(c + leftEnd - 2 * refs[corner - n]) < threshold)
            {
                // (8-36 .. 8-40): bilinear between the corner and the far ends.
                for (var i = 0; i < 63; i++)
                {
                    refs[corner + 1 + i] = ((63 - i) * c + (i + 1) * topEnd + 32) >> 6;
                    refs[corner - 1 - i] = ((63 - i) * c + (i + 1) * leftEnd + 32) >> 6;
                }
                return;
            }
        }

        // (8-41 .. 8-45): [1 2 1] over consecutive entries, the two ends unchanged.
        var previous = refs[0];
        for (var i = 1; i < last; i++)
        {
            var current = refs[i];
            refs[i] = (previous + 2 * current + refs[i + 1] + 2) >> 2;
            previous = current;
        }
    }
}
