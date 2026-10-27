using System;
using System.Numerics;

namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// The inverse discrete wavelet transform of T.800 Annex F: the reversible 5/3
/// filter in integers, and the irreversible 9/7 filter in single-precision floats.
/// <para>
/// Reconstruction climbs the resolutions: level 0's LL band is the starting
/// image, and each level up interleaves that image with the level's HL, LH and
/// HH bands and filters the result, first along rows and then along columns.
/// </para>
/// <para>
/// Everything is done in absolute coordinates. F.3.3's interleave places a
/// sample by the <em>parity</em> of its index on the resolution grid, so an
/// image whose origin is not at zero interleaves differently from one that is,
/// and a version of this that worked in widths and heights would decode a
/// correct-looking but shifted picture for any codestream with a non-zero
/// <c>XOsiz</c>.
/// </para>
/// <para>
/// <b>The columns are filtered a whole row at a time.</b> A lifting step updates
/// every sample of one parity from its two neighbours of the other, so down a
/// column it is "this row, less a multiple of the rows above and below", which is
/// the same arithmetic for every column at once: a vector operation over
/// contiguous rows rather than a strided walk down each column. The symmetric
/// extension needs no materialising there, because both filters are symmetric:
/// lifting a whole-sample-symmetric signal leaves it symmetric, so the sample
/// beyond an edge is always the current value of its mirror inside, and reading
/// the mirror row gives exactly what extending first would have. Walking each
/// column through a copied line was most of the wavelet's time, and the wavelet
/// was half of a lossy decode.
/// </para>
/// </summary>
internal static class InverseWavelet
{
    /// <summary>
    /// Reconstructs the tile-component's samples from its decoded subband
    /// coefficients, at the first <paramref name="resolutions"/> resolution
    /// levels: all of them for the full image, fewer for a reduced one.
    /// </summary>
    /// <returns>The samples, row-major over that resolution level's bounds.</returns>
    public static int[] Reconstruct(TileComponent tile, int resolutions)
    {
        // Resolution 0 is the LL band verbatim — for a codestream with no
        // decomposition at all, this is the whole answer.
        var lowPass = tile.Resolutions[0].Bands[0];
        if (resolutions == 1) return (int[])lowPass.Coefficients.Clone();

        var bounds = tile.Resolutions[resolutions - 1].Bounds;
        var (result, other) = Buffers<int>(tile, resolutions);
        var current = FirstInput(result, other, resolutions);
        lowPass.Coefficients.CopyTo(current, 0);
        var currentBounds = lowPass.Bounds;

        var line = Math.Max(bounds.Width, bounds.Height) + 8;
        var extended = new int[line];
        var lifted = new int[line];

        for (var r = 1; r < resolutions; r++)
        {
            var resolution = tile.Resolutions[r];
            var next = current == result ? other : result;
            Lift(current, currentBounds, resolution, next, extended, lifted);
            current = next;
            currentBounds = resolution.Bounds;
        }

        return result;
    }

    /// <summary>
    /// <see cref="Reconstruct"/> for a 9/7 tile-component: the same climb over the
    /// same interleave, from the dequantized coefficients, in floats.
    /// <para>
    /// Single precision, deliberately. The 9/7 coefficients are irrational, so no
    /// width makes this exact, and the reference decoders compute in floats too:
    /// matching their arithmetic is what keeps the difference from them to the
    /// rounding of the last step rather than a drift built up over five levels.
    /// </para>
    /// </summary>
    /// <returns>The samples, row-major over that resolution level's bounds, before rounding.</returns>
    public static float[] ReconstructIrreversible(TileComponent tile, int resolutions)
    {
        var lowPass = tile.Resolutions[0].Bands[0];
        if (resolutions == 1) return (float[])lowPass.Values.Clone();

        var bounds = tile.Resolutions[resolutions - 1].Bounds;
        var (result, other) = Buffers<float>(tile, resolutions);
        var current = FirstInput(result, other, resolutions);
        lowPass.Values.CopyTo(current, 0);
        var currentBounds = lowPass.Bounds;

        var extended = new float[Math.Max(bounds.Width, bounds.Height) + 2 * IrreversibleMargin];

        for (var r = 1; r < resolutions; r++)
        {
            var resolution = tile.Resolutions[r];
            var next = current == result ? other : result;
            LiftIrreversible(current, currentBounds, resolution, next, extended);
            current = next;
            currentBounds = resolution.Bounds;
        }

        return result;
    }

    /// <summary>
    /// The two buffers a reconstruction alternates between: each level reads the one below it from
    /// one and writes itself to the other. Only the last level is full size, so only one buffer is;
    /// the other holds at most the level below it, a quarter as much. Two full-size buffers cost a
    /// lossy decode a quarter more memory than it had needed when every level got a fresh array.
    /// </summary>
    private static (T[] Result, T[] Other) Buffers<T>(TileComponent tile, int resolutions) =>
        (new T[tile.Resolutions[resolutions - 1].Bounds.Area],
         new T[Math.Max(tile.Resolutions[resolutions - 2].Bounds.Area, tile.Resolutions[0].Bands[0].Bounds.Area)]);

    /// <summary>
    /// Where the LL band starts, chosen so that the levels, alternating, end in the full-size buffer:
    /// with an odd number of levels above it the first writes the full-size buffer, so the band starts
    /// in the other, and with an even number the other way round.
    /// </summary>
    private static T[] FirstInput<T>(T[] result, T[] other, int resolutions) =>
        (resolutions - 1) % 2 == 1 ? other : result;

    /// <summary>
    /// T.800 F.3.2 (2D_SR) for one resolution level: interleave, filter the rows,
    /// then filter the columns.
    /// </summary>
    private static void Lift(
        int[] lowPass, Rect lowPassBounds, Resolution resolution, int[] destination, int[] extended, int[] lifted)
    {
        var bounds = resolution.Bounds;
        var width = bounds.Width;
        var height = bounds.Height;
        if (width == 0 || height == 0) return;

        var samples = destination.AsSpan(0, width * height);
        Interleave(samples, bounds, lowPass, lowPassBounds, resolution, band => band.Coefficients);

        // F.3.4 HOR_SR then F.3.5 VER_SR. The order is fixed by the spec and is
        // not a free choice: the 5/3 lifting steps do not commute across
        // dimensions.
        for (var y = 0; y < height; y++)
            Filter(samples.Slice(y * width, width), bounds.X0, bounds.X1, extended, lifted);

        FilterColumns(samples, width, bounds.Y0, bounds.Y1);
    }

    /// <summary>
    /// <see cref="Lift"/> for the 9/7 filter: the same interleave and the same row-then-column
    /// order, over floats.
    /// </summary>
    private static void LiftIrreversible(
        float[] lowPass, Rect lowPassBounds, Resolution resolution, float[] destination, float[] extended)
    {
        var bounds = resolution.Bounds;
        var width = bounds.Width;
        var height = bounds.Height;
        if (width == 0 || height == 0) return;

        var samples = destination.AsSpan(0, width * height);
        Interleave(samples, bounds, lowPass, lowPassBounds, resolution, band => band.Values);

        for (var y = 0; y < height; y++)
            Filter97(samples.Slice(y * width, width), bounds.X0, bounds.X1, extended);

        FilterColumns97(samples, width, bounds.Y0, bounds.Y1);
    }

    /// <summary>
    /// F.3.3 (2D_INTERLEAVE). The four bands land on the four parities of the
    /// resolution grid: LL on even/even, HL on odd/even, LH on even/odd, HH on
    /// odd/odd — in ABSOLUTE coordinates, so which band a given cell takes
    /// depends on where the image sits, not merely on its size.
    /// <para>
    /// The destination is a reused buffer, so it is cleared first: the four bands
    /// cover every cell of the level between them, but a cell no band reached must
    /// read zero, as it did when every level had a fresh array.
    /// </para>
    /// </summary>
    private static void Interleave<T>(
        Span<T> samples, Rect bounds, T[] lowPass, Rect lowPassBounds, Resolution resolution,
        Func<Subband, T[]> coefficients)
    {
        samples.Clear();
        Scatter(samples, bounds, lowPass, lowPassBounds, xOdd: false, yOdd: false);
        foreach (var band in resolution.Bands)
        {
            switch (band.Kind)
            {
                case BandKind.Hl:
                    Scatter(samples, bounds, coefficients(band), band.Bounds, xOdd: true, yOdd: false);
                    break;
                case BandKind.Lh:
                    Scatter(samples, bounds, coefficients(band), band.Bounds, xOdd: false, yOdd: true);
                    break;
                case BandKind.Hh:
                    Scatter(samples, bounds, coefficients(band), band.Bounds, xOdd: true, yOdd: true);
                    break;
            }
        }
    }

    /// <summary>
    /// Places one band's coefficients on the interleaved grid at the parity its
    /// orientation dictates.
    /// </summary>
    private static void Scatter<T>(
        Span<T> destination, Rect destinationBounds, T[] source, Rect sourceBounds, bool xOdd, bool yOdd)
    {
        if (sourceBounds.IsEmpty) return;

        var width = destinationBounds.Width;
        var sourceWidth = sourceBounds.Width;

        for (var v = sourceBounds.Y0; v < sourceBounds.Y1; v++)
        {
            // A band sample at index v sits at 2v (+1 for a high-pass band) on
            // the resolution grid.
            var y = 2 * v + (yOdd ? 1 : 0) - destinationBounds.Y0;
            if ((uint)y >= (uint)destinationBounds.Height) continue;

            var sourceRow = (v - sourceBounds.Y0) * sourceWidth;
            var destinationRow = y * width;

            for (var u = sourceBounds.X0; u < sourceBounds.X1; u++)
            {
                var x = 2 * u + (xOdd ? 1 : 0) - destinationBounds.X0;
                if ((uint)x >= (uint)width) continue;

                destination[destinationRow + x] = source[sourceRow + (u - sourceBounds.X0)];
            }
        }
    }

    /// <summary>
    /// T.800 F.3.7 (1D_SR) with the reversible filter of F.3.8.2, over the
    /// half-open index range <c>[i0, i1)</c>.
    /// </summary>
    /// <param name="extended">Scratch for the extended signal, at least <c>i1 - i0 + 4</c> long.</param>
    /// <param name="lifted">Scratch for the lifted signal, as long.</param>
    private static void Filter(Span<int> signal, int i0, int i1, int[] extended, int[] lifted)
    {
        var length = i1 - i0;
        if (length <= 0) return;

        if (length == 1)
        {
            RefuseALoneHighPassSample(i0);
            return;
        }

        // Work over a margin-padded copy so the symmetric extension of F.3.6 can
        // be materialised once rather than tested for on every access.
        const int margin = 2;
        var count = length + 2 * margin;
        var y = extended.AsSpan(0, count);
        for (var j = 0; j < count; j++)
        {
            y[j] = signal[Mirror(i0 - margin + j, i0, i1) - i0];
        }

        var low = FloorHalf(i0);
        var high = FloorHalf(i1);
        var x = lifted.AsSpan(0, count);
        y.CopyTo(x);

        var origin = i0 - margin;

        // F.3.8.2, step 1: every even-indexed sample, undoing the update.
        for (var n = low; n <= high; n++)
        {
            var index = 2 * n - origin;
            if ((uint)index >= (uint)x.Length) continue;

            // >> is an arithmetic shift, which IS the floor division the spec
            // writes; `/ 4` would round toward zero and be wrong for negatives.
            x[index] = y[index] - ((y[index - 1] + y[index + 1] + 2) >> 2);
        }

        // F.3.8.2, step 2: every odd-indexed sample, from the even ones just
        // recovered.
        for (var n = low; n < high; n++)
        {
            var index = 2 * n + 1 - origin;
            if ((uint)index >= (uint)x.Length) continue;

            x[index] = y[index] + ((x[index - 1] + x[index + 1]) >> 1);
        }

        x.Slice(margin, length).CopyTo(signal);
    }

    /// <summary>
    /// <see cref="Filter"/> down every column at once: F.3.8.2's two steps, each a whole row
    /// of one parity updated from the rows either side of it, mirrored at the edges.
    /// </summary>
    private static void FilterColumns(Span<int> samples, int width, int y0, int y1)
    {
        var height = y1 - y0;
        if (height <= 1)
        {
            if (height == 1) RefuseALoneHighPassSample(y0);
            return;
        }

        var firstEven = (y0 & 1) == 0 ? 0 : 1;
        var firstOdd = 1 - firstEven;
        var two = new Vector<int>(2);

        // Step 1: every even row, less a quarter of the two odd rows beside it, rounded down.
        for (var j = firstEven; j < height; j += 2)
        {
            var row = samples.Slice(j * width, width);
            var above = samples.Slice(MirrorRow(j - 1, height) * width, width);
            var below = samples.Slice(MirrorRow(j + 1, height) * width, width);

            var x = 0;
            for (; x <= width - Vector<int>.Count; x += Vector<int>.Count)
            {
                var sum = new Vector<int>(above[x..]) + new Vector<int>(below[x..]) + two;
                (new Vector<int>(row[x..]) - Vector.ShiftRightArithmetic(sum, 2)).CopyTo(row[x..]);
            }

            for (; x < width; x++) row[x] -= (above[x] + below[x] + 2) >> 2;
        }

        // Step 2: every odd row, plus half of the two even rows beside it, now recovered.
        for (var j = firstOdd; j < height; j += 2)
        {
            var row = samples.Slice(j * width, width);
            var above = samples.Slice(MirrorRow(j - 1, height) * width, width);
            var below = samples.Slice(MirrorRow(j + 1, height) * width, width);

            var x = 0;
            for (; x <= width - Vector<int>.Count; x += Vector<int>.Count)
            {
                var sum = new Vector<int>(above[x..]) + new Vector<int>(below[x..]);
                (new Vector<int>(row[x..]) + Vector.ShiftRightArithmetic(sum, 1)).CopyTo(row[x..]);
            }

            for (; x < width; x++) row[x] += (above[x] + below[x]) >> 1;
        }
    }

    // T.800 Table F.4: the 9/7 lifting parameters and scaling factor.
    private const float Alpha = -1.586134342059924f;
    private const float Beta = -0.052980118572961f;
    private const float Gamma = 0.882911075530934f;
    private const float Delta = 0.443506852043971f;
    private const float K = 1.230174104914001f;
    private const float InverseK = 1f / K;

    /// <summary>
    /// How far the signal is extended past each end before filtering. Each of the four lifting
    /// steps reads one neighbour each way, so a value computed next to the end of the extended
    /// buffer, where one neighbour is missing, is wrong, and the wrongness moves one sample
    /// inward per step: four steps, four samples. With this margin every wrong value is in the
    /// extension and none is in the signal.
    /// </summary>
    private const int IrreversibleMargin = 4;

    /// <summary>
    /// T.800 F.3.7 (1D_SR) with the irreversible filter of F.3.8.2, over the half-open index range
    /// <c>[i0, i1)</c>: undo the scaling (the low-pass samples by K, the high-pass by 1/K), then
    /// the four lifting steps in reverse, each subtracting what the forward transform added.
    /// </summary>
    private static void Filter97(Span<float> signal, int i0, int i1, float[] scratch)
    {
        var length = i1 - i0;
        if (length <= 0) return;

        if (length == 1)
        {
            RefuseALoneHighPassSample(i0);
            return;
        }

        var origin = i0 - IrreversibleMargin;
        var count = length + 2 * IrreversibleMargin;
        var x = scratch.AsSpan(0, count);
        for (var j = 0; j < count; j++) x[j] = signal[Mirror(origin + j, i0, i1) - i0];

        // x[j] holds the sample at absolute index origin + j, and which samples are low-pass is
        // decided by the parity of that ABSOLUTE index, not of j.
        var firstEven = (origin & 1) == 0 ? 0 : 1;
        var firstOdd = 1 - firstEven;

        // Steps 1 and 2.
        for (var j = firstEven; j < count; j += 2) x[j] *= K;
        for (var j = firstOdd; j < count; j += 2) x[j] *= InverseK;

        // Steps 3 to 6, each over every sample of its parity that has both neighbours.
        Lift97(x, firstEven, Delta);
        Lift97(x, firstOdd, Gamma);
        Lift97(x, firstEven, Beta);
        Lift97(x, firstOdd, Alpha);

        x.Slice(IrreversibleMargin, length).CopyTo(signal);
    }

    /// <summary>One lifting step: every sample of one parity less <paramref name="c"/> times the sum of its neighbours.</summary>
    private static void Lift97(Span<float> x, int first, float c)
    {
        var start = first == 0 ? 2 : first;
        for (var j = start; j + 1 < x.Length; j += 2) x[j] -= c * (x[j - 1] + x[j + 1]);
    }

    /// <summary>
    /// <see cref="Filter97"/> down every column at once, as <see cref="FilterColumns"/> is for the
    /// 5/3: the scaling, then each lifting step a whole row of one parity at a time. The
    /// arithmetic per sample is the same, in the same order, as the line filter's, so the two give
    /// the same floats.
    /// </summary>
    private static void FilterColumns97(Span<float> samples, int width, int y0, int y1)
    {
        var height = y1 - y0;
        if (height <= 1)
        {
            if (height == 1) RefuseALoneHighPassSample(y0);
            return;
        }

        var firstEven = (y0 & 1) == 0 ? 0 : 1;
        var firstOdd = 1 - firstEven;

        for (var j = 0; j < height; j++)
        {
            var row = samples.Slice(j * width, width);
            var scale = ((j - firstEven) & 1) == 0 ? K : InverseK;
            var x = 0;
            var factor = new Vector<float>(scale);
            for (; x <= width - Vector<float>.Count; x += Vector<float>.Count)
                (new Vector<float>(row[x..]) * factor).CopyTo(row[x..]);
            for (; x < width; x++) row[x] *= scale;
        }

        LiftRows97(samples, width, height, firstEven, Delta);
        LiftRows97(samples, width, height, firstOdd, Gamma);
        LiftRows97(samples, width, height, firstEven, Beta);
        LiftRows97(samples, width, height, firstOdd, Alpha);
    }

    /// <summary>One lifting step over whole rows: every row of one parity less <paramref name="c"/> times the sum of the rows beside it.</summary>
    private static void LiftRows97(Span<float> samples, int width, int height, int first, float c)
    {
        var factor = new Vector<float>(c);
        for (var j = first; j < height; j += 2)
        {
            var row = samples.Slice(j * width, width);
            var above = samples.Slice(MirrorRow(j - 1, height) * width, width);
            var below = samples.Slice(MirrorRow(j + 1, height) * width, width);

            var x = 0;
            for (; x <= width - Vector<float>.Count; x += Vector<float>.Count)
            {
                var sum = new Vector<float>(above[x..]) + new Vector<float>(below[x..]);
                (new Vector<float>(row[x..]) - factor * sum).CopyTo(row[x..]);
            }

            for (; x < width; x++) row[x] -= c * (above[x] + below[x]);
        }
    }

    /// <summary>
    /// F.3.7's degenerate case. An even index is a lone low-pass sample and passes
    /// through untouched; that happens for any image one sample wide, so it is
    /// ordinary and reachable. An odd index is a lone HIGH-pass sample. The spec
    /// halves it, but reaching this needs an odd image origin, which nothing
    /// available here can encode — so there is no way to check it against the
    /// reference. Refusing beats emitting a number no oracle has ever confirmed;
    /// the whole family's discipline.
    /// </summary>
    private static void RefuseALoneHighPassSample(int i0)
    {
        if ((i0 & 1) == 0) return;

        throw new NotSupportedException(
            "JPEG 2000: a subband one sample wide starting at an odd coordinate is not implemented. " +
            "It needs an odd image or tile origin, which opj_compress cannot emit, so this path has " +
            "no reference to be validated against.");
    }

    /// <summary>
    /// Whole-point symmetric extension (T.800 F.3.6): reflect about <c>i0</c> and
    /// <c>i1 - 1</c> without repeating the edge sample.
    /// </summary>
    private static int Mirror(int index, int i0, int i1)
    {
        var period = 2 * (i1 - i0 - 1);
        if (period <= 0) return i0;

        var k = (index - i0) % period;
        if (k < 0) k += period;

        return i0 + (k >= i1 - i0 ? period - k : k);
    }

    /// <summary>
    /// <see cref="Mirror"/> one row beyond either edge of a column of <paramref name="height"/>
    /// rows (at least two), in row numbers from 0, which is as far as a lifting step reaches.
    /// </summary>
    private static int MirrorRow(int row, int height) =>
        row < 0 ? -row : row >= height ? 2 * (height - 1) - row : row;

    /// <summary>Floor of half, correct for negative values (<c>>></c>, not <c>/</c>).</summary>
    private static int FloorHalf(int value) => value >> 1;

    /// <summary>
    /// Runs one 1D synthesis over an interleaved signal, for tests that need to
    /// check the lifting arithmetic without building a codestream around it.
    /// </summary>
    internal static int[] FilterForTests(int[] interleaved, int i0, int i1)
    {
        var signal = (int[])interleaved.Clone();
        Filter(signal.AsSpan(), i0, i1, new int[signal.Length + 8], new int[signal.Length + 8]);
        return signal;
    }

    /// <summary>The 9/7 counterpart of <see cref="FilterForTests"/>.</summary>
    internal static float[] Filter97ForTests(float[] interleaved, int i0, int i1)
    {
        var signal = (float[])interleaved.Clone();
        Filter97(signal.AsSpan(), i0, i1, new float[signal.Length + 2 * IrreversibleMargin]);
        return signal;
    }
}
