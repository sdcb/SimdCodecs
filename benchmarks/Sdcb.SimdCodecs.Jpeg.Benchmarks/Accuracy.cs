using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Sdcb.SimdCodecs.Jpeg;

namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

/// <summary>
/// Accuracy tables, laying the ground for "more accurate than libjpeg-turbo":
/// <list type="number">
/// <item><b>Coefficient layer</b> — libjpeg-turbo's quantized coefficients (via the
/// documented <c>tj3Transform</c> custom filter) vs our entropy decoder's, one by one.
/// They must be identical; entropy decoding has exactly one right answer.</item>
/// <item><b>Component planes</b> (after IDCT, before upsampling) — libjpeg-turbo
/// (<c>tj3DecompressToYUVPlanes8</c>), ours (<see cref="JpegDiagnostics"/>), each vs the
/// double-precision T.81 A.3.3 reference IDCT (<see cref="ReferenceIdct"/>), rounded.</item>
/// <item><b>RGB</b> — every contestant vs reference IDCT + bilinear chroma interpolation at
/// T.871's centred positions + double-precision JFIF (T.871) YCbCr→RGB. ImageSharp is only
/// measurable here.</item>
/// <item><b>IEEE 1180-style random-block test</b> — the 1180 procedure (random pixel
/// blocks → reference FDCT → rounded coefficients → IDCT under test vs reference) run
/// through real JPEG streams whose coefficients are written exactly via
/// <c>tj3Transform</c>, so all decoders see identical input.</item>
/// </list>
/// </summary>
internal static class Accuracy
{
    /// <summary>Signed integer error accumulator with per-position (within 8×8) statistics.</summary>
    internal sealed class ErrStats
    {
        public long N, Sum, SumSq, Exact;
        public int Peak;
        public readonly long[] PosN = new long[64], PosSum = new long[64], PosSumSq = new long[64];

        public void Add(int err, int pos)
        {
            N++;
            Sum += err;
            SumSq += (long)err * err;
            if (err == 0) Exact++;
            var a = Math.Abs(err);
            if (a > Peak) Peak = a;
            PosN[pos]++;
            PosSum[pos] += err;
            PosSumSq[pos] += (long)err * err;
        }

        public void Merge(ErrStats o)
        {
            lock (this)
            {
                N += o.N; Sum += o.Sum; SumSq += o.SumSq; Exact += o.Exact;
                Peak = Math.Max(Peak, o.Peak);
                for (var i = 0; i < 64; i++) { PosN[i] += o.PosN[i]; PosSum[i] += o.PosSum[i]; PosSumSq[i] += o.PosSumSq[i]; }
            }
        }

        public double Mse => N == 0 ? 0 : (double)SumSq / N;
        public double Mean => N == 0 ? 0 : (double)Sum / N;
        public double ExactPct => N == 0 ? 0 : 100.0 * Exact / N;
        public double Psnr => SumSq == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / Mse);
        public double MaxPosMse => Enumerable.Range(0, 64).Where(i => PosN[i] > 0).Select(i => (double)PosSumSq[i] / PosN[i]).DefaultIfEmpty().Max();
        public double MaxPosAbsMean => Enumerable.Range(0, 64).Where(i => PosN[i] > 0).Select(i => Math.Abs((double)PosSum[i] / PosN[i])).DefaultIfEmpty().Max();

        /// <summary>IEEE 1180-1990 limits: peak ≤ 1, per-position MSE ≤ 0.06 and |mean| ≤ 0.015, overall MSE ≤ 0.02 and |mean| ≤ 0.0015.</summary>
        public bool Meets1180 => Peak <= 1 && MaxPosMse <= 0.06 && MaxPosAbsMean <= 0.015 && Mse <= 0.02 && Math.Abs(Mean) <= 0.0015;
    }

    private sealed class FileResult
    {
        public required MatrixCase Case;
        public long CoeffCompared, CoeffMismatch, BlocksCompared, BlocksMismatch;
        public string? CoeffNote;
        public readonly ErrStats OursLuma = new(), OursChroma = new(), TurboLuma = new(), TurboChroma = new();
        public readonly Dictionary<string, ErrStats> Rgb = new();
    }

    public static string ResultFile => Path.Combine(BenchPaths.Work, "accuracy.md");

    /// <summary>Runs the accuracy tables; with <paramref name="gate"/>, returns 1 if the quality gate fails.</summary>
    public static int Run(IReadOnlyList<MatrixCase> all, bool gate = false)
    {
        TurboJpeg.SelfCheck();
        if (gate && BaselineDecoder.Description is null)
            throw new InvalidOperationException("--gate needs an A/B baseline: run `baseline <git-ref>` first.");
        // 3 MP tier (every sampling/quality/variant) plus the camera originals.
        var cases = all.Where(c => c.Tier is "3mp" or "camera").ToList();
        var results = new ConcurrentBag<FileResult>();
        var done = 0;
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, c =>
        {
            results.Add(Analyze(c));
            var n = Interlocked.Increment(ref done);
            if (n % 20 == 0) Console.WriteLine($"[accuracy] {n}/{cases.Count}");
        });

        var random = Ieee1180Style();
        var ordered = results.OrderBy(r => r.Case.Id, StringComparer.Ordinal).ToList();
        var md = Render(ordered, random);
        var failed = false;
        if (gate)
        {
            var (gateMd, ok) = Gate(ordered, random);
            md += gateMd;
            failed = !ok;
        }

        Directory.CreateDirectory(BenchPaths.Work);
        File.WriteAllText(ResultFile, md);
        Console.WriteLine(md);
        return failed ? 1 : 0;
    }

    // ------------------------------------------------------------------ quality gate

    /// <summary>Max RGB PSNR loss against the baseline build, per group.</summary>
    internal const double GateMaxPsnrDrop = 0.2;

    /// <summary>Required RGB PSNR lead over libjpeg-turbo for groups with chroma interpolation.</summary>
    internal const double GateMinLeadOverTurbo = 0.5;

    /// <summary>Max RGB peak error.</summary>
    internal const int GateMaxPeak = 2;

    /// <summary>
    /// The decode-quality gate a change to the decoder's output must pass, per group:
    /// RGB PSNR within <see cref="GateMaxPsnrDrop"/> dB of the baseline build; at least
    /// <see cref="GateMinLeadOverTurbo"/> dB above libjpeg-turbo where chroma is interpolated
    /// (4:4:4 — nothing interpolated, both decoders at the rounding floor — only not below
    /// it); RGB peak ≤ <see cref="GateMaxPeak"/>; every plane row and every IEEE 1180 random
    /// block row of ours within the 1180 limits.
    /// </summary>
    private static (string Markdown, bool Ok) Gate(List<FileResult> results, List<RandomTest> random)
    {
        var turboLabel = Contestants.Label(Contestant.LibjpegTurbo);
        var oursLabel = Contestants.Label(Contestant.Ours);
        var sb = new StringBuilder();
        var ok = true;
        string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

        sb.AppendLine("### Quality gate");
        sb.AppendLine();
        sb.AppendLine($"Per group: PSNR ≥ baseline − {GateMaxPsnrDrop} dB; PSNR ≥ turbo + {GateMinLeadOverTurbo} dB (4:4:4: ≥ turbo); peak ≤ {GateMaxPeak}; planes and IEEE 1180 blocks meet the 1180 limits.");
        sb.AppendLine();
        sb.AppendLine("| Group | Ours PSNR | Baseline PSNR | Turbo PSNR | Δ baseline | Lead over turbo | Peak | Result |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|:---:|");
        foreach (var g in results.Where(r => r.Rgb.Count > 0).GroupBy(r => r.Case.Group).OrderBy(g => GroupSort(g.Key)))
        {
            ErrStats Sum(string label) { var s = new ErrStats(); foreach (var r in g) s.Merge(r.Rgb[label]); return s; }
            var o = Sum(oursLabel);
            var b = Sum(BaselineLabel);
            var t = Sum(turboLabel);
            var interpolated = g.All(r => r.Case.Sampling != "444");
            var need = interpolated ? GateMinLeadOverTurbo : 0;
            var pass = o.Psnr >= b.Psnr - GateMaxPsnrDrop && o.Psnr >= t.Psnr + need && o.Peak <= GateMaxPeak;
            ok &= pass;
            sb.AppendLine($"| {g.Key} | {F2(o.Psnr)} | {F2(b.Psnr)} | {F2(t.Psnr)} | {F2(o.Psnr - b.Psnr)} | {F2(o.Psnr - t.Psnr)} (≥ {need}) | {o.Peak} | {(pass ? "pass" : "**FAIL**")} |");
        }

        sb.AppendLine();
        foreach (var g in results.GroupBy(r => r.Case.Group).OrderBy(g => GroupSort(g.Key)))
        {
            var l = new ErrStats(); var c = new ErrStats();
            foreach (var r in g) { l.Merge(r.OursLuma); c.Merge(r.OursChroma); }
            foreach (var (plane, st) in new[] { ("Y", l), ("Cb/Cr", c) })
            {
                if (st.N == 0 || st.Meets1180) continue;
                ok = false;
                sb.AppendLine($"- **FAIL** plane {g.Key} {plane}: outside the IEEE 1180 limits (peak {st.Peak}, MSE {st.Mse:F4}, mean {st.Mean:F4}).");
            }
        }

        foreach (var t in random.Where(t => !t.Ours.Meets1180))
        {
            ok = false;
            sb.AppendLine($"- **FAIL** IEEE 1180 block test {t.Name}: peak {t.Ours.Peak}, MSE {t.Ours.Mse:F4}, mean {t.Ours.Mean:F4}.");
        }

        sb.AppendLine();
        sb.AppendLine(ok ? "**Quality gate: PASS**" : "**Quality gate: FAIL**");
        sb.AppendLine();
        return (sb.ToString(), ok);
    }

    // ------------------------------------------------------------------ per-file analysis

    private static FileResult Analyze(MatrixCase c)
    {
        var jpeg = File.ReadAllBytes(c.Path);
        var r = new FileResult { Case = c };

        var ours = new ConcurrentDictionary<(int C, int X, int Y), short[]>();
        var planes = JpegDiagnostics.DecodePlanes(jpeg, (comp, bx, by, data) => ours[(comp, bx, by)] = data.ToArray());

        // 1. Coefficient layer.
        try
        {
            var turbo = TurboCoefficients.Read(jpeg);
            for (var ci = 0; ci < planes.Length; ci++)
            {
                if (!turbo.TryGetValue(ci, out var tp)) { r.CoeffNote = $"component {ci} missing from tj3Transform"; continue; }
                var q = planes[ci].Quant;
                var bw = (planes[ci].Width + 7) / 8;
                var bh = (planes[ci].Height + 7) / 8;
                for (var by = 0; by < Math.Min(bh, tp.BlocksH); by++)
                {
                    for (var bx = 0; bx < Math.Min(bw, tp.BlocksW); bx++)
                    {
                        r.BlocksCompared++;
                        var mine = ours[(ci, bx, by)];
                        var theirs = tp.Coeffs.AsSpan((by * tp.BlocksW + bx) * 64, 64);
                        var bad = false;
                        for (var k = 0; k < 64; k++)
                        {
                            r.CoeffCompared++;
                            var quantized = mine[k] % q[k] == 0 ? mine[k] / q[k] : int.MinValue;
                            if (quantized != theirs[k]) { r.CoeffMismatch++; bad = true; }
                        }

                        if (bad) r.BlocksMismatch++;
                    }
                }

                if (bw > tp.BlocksW || bh > tp.BlocksH)
                    r.CoeffNote = $"tj3Transform plane {tp.BlocksW}x{tp.BlocksH} blocks < ours {bw}x{bh}";
            }
        }
        catch (Exception e)
        {
            r.CoeffNote = "tj3Transform failed: " + e.Message;
        }

        // 2. Reference planes from the (verified) coefficients.
        var refPlanes = new double[planes.Length][];
        for (var ci = 0; ci < planes.Length; ci++)
        {
            var p = planes[ci];
            var refp = new double[p.Width * p.Height];
            var bw = (p.Width + 7) / 8;
            var bh = (p.Height + 7) / 8;
            var s = new double[64];
            for (var by = 0; by < bh; by++)
            {
                for (var bx = 0; bx < bw; bx++)
                {
                    ReferenceIdct.Inverse(ours[(ci, bx, by)], s);
                    for (var y = 0; y < 8 && by * 8 + y < p.Height; y++)
                        for (var x = 0; x < 8 && bx * 8 + x < p.Width; x++)
                            refp[(by * 8 + y) * p.Width + bx * 8 + x] = s[y * 8 + x] + 128;
                }
            }

            refPlanes[ci] = refp;
        }

        using (var td = new TurboDecoder(fast: false))
        {
            var tplanes = td.DecodeYuvPlanes(jpeg);
            for (var ci = 0; ci < planes.Length && ci < tplanes.Length; ci++)
            {
                var p = planes[ci];
                var (tp, tw, th) = tplanes[ci];
                var oursStats = ci == 0 ? r.OursLuma : r.OursChroma;
                var turboStats = ci == 0 ? r.TurboLuma : r.TurboChroma;
                for (var y = 0; y < Math.Min(p.Height, th); y++)
                {
                    for (var x = 0; x < Math.Min(p.Width, tw); x++)
                    {
                        var reference = Clamp255(refPlanes[ci][y * p.Width + x]);
                        var pos = (y & 7) * 8 + (x & 7);
                        oursStats.Add(p.Samples[y * p.Width + x] - reference, pos);
                        turboStats.Add(tp[y * tw + x] - reference, pos);
                    }
                }
            }
        }

        // 3. RGB vs reference IDCT + linear chroma interpolation at T.871's centred sample
        //    positions + JFIF conversion, all in double precision (no rounding until the end).
        if (planes.Length == 3 && planes[0].H == planes.Max(p => p.H) && planes[0].V == planes.Max(p => p.V))
        {
            var w = planes[0].Width;
            var h = planes[0].Height;
            var cbFull = UpsampleReference(planes, refPlanes, 1, w, h);
            var crFull = UpsampleReference(planes, refPlanes, 2, w, h);
            var refRgb = new byte[w * h * 3];
            for (var i = 0; i < w * h; i++)
            {
                var yy = Math.Clamp(refPlanes[0][i], 0, 255);
                var cb = cbFull[i] - 128;
                var cr = crFull[i] - 128;
                refRgb[i * 3] = (byte)Clamp255(yy + 1.402 * cr);
                refRgb[i * 3 + 1] = (byte)Clamp255(yy - (0.114 * 1.772 / 0.587) * cb - (0.299 * 1.402 / 0.587) * cr);
                refRgb[i * 3 + 2] = (byte)Clamp255(yy + 1.772 * cb);
            }

            foreach (var label in RgbLabels)
            {
                byte[] rgba;
                if (label == BaselineLabel)
                {
                    rgba = new byte[w * h * 4];
                    BaselineDecoder.DecodeTo(jpeg, rgba);
                }
                else
                {
                    rgba = Contestants.DecodeRgba(Enum.GetValues<Contestant>().Single(c => Contestants.Label(c) == label), jpeg, out _, out _);
                }

                var st = new ErrStats();
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var i = y * w + x;
                        var pos = (y & 7) * 8 + (x & 7);
                        for (var ch = 0; ch < 3; ch++)
                            st.Add(rgba[i * 4 + ch] - refRgb[i * 3 + ch], pos);
                    }

                r.Rgb[label] = st;
            }
        }

        return r;
    }

    /// <summary>Label of the A/B baseline build in the RGB table (see <see cref="BaselineDecoder"/>).</summary>
    private const string BaselineLabel = "Sdcb.SimdCodecs (baseline)";

    /// <summary>RGB-table rows: every contestant, plus the baseline build when one exists.</summary>
    private static readonly string[] RgbLabels = Enum.GetValues<Contestant>().Select(Contestants.Label)
        .Concat(BaselineDecoder.Description is null ? [] : [BaselineLabel]).ToArray();

    private static int Clamp255(double v) => (int)Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>
    /// Component <paramref name="ci"/>'s reference plane (clamped, unrounded) brought to
    /// full resolution by bilinear interpolation at JFIF's centred sample positions
    /// (T.871): output sample x sits at source coordinate (x + ½)/f − ½, edges replicate.
    /// For a 2× factor that is exactly ¾·near + ¼·far. Identity when not subsampled.
    /// </summary>
    private static double[] UpsampleReference(JpegComponentPlane[] planes, double[][] refPlanes, int ci, int w, int h)
    {
        var p = planes[ci];
        var src = refPlanes[ci];
        var fx = (double)planes[0].H / p.H;
        var fy = (double)planes[0].V / p.V;
        var dst = new double[w * h];
        for (var y = 0; y < h; y++)
        {
            var ys = (y + 0.5) / fy - 0.5;
            var y0 = (int)Math.Floor(ys);
            var ay = ys - y0;
            var ya = Math.Clamp(y0, 0, p.Height - 1);
            var yb = Math.Clamp(y0 + 1, 0, p.Height - 1);
            for (var x = 0; x < w; x++)
            {
                var xs = (x + 0.5) / fx - 0.5;
                var x0 = (int)Math.Floor(xs);
                var ax = xs - x0;
                var xa = Math.Clamp(x0, 0, p.Width - 1);
                var xb = Math.Clamp(x0 + 1, 0, p.Width - 1);
                double S(int yy, int xx) => Math.Clamp(src[yy * p.Width + xx], 0, 255);
                dst[y * w + x] = (1 - ay) * ((1 - ax) * S(ya, xa) + ax * S(ya, xb)) + ay * ((1 - ax) * S(yb, xa) + ax * S(yb, xb));
            }
        }

        return dst;
    }

    // ------------------------------------------------------------------ IEEE 1180-style test

    internal sealed record RandomTest(string Name, int L, int H, int Sign, long ClippedCoeffs, ErrStats Ours, ErrStats Turbo, ErrStats TurboFast);

    /// <summary>
    /// IEEE 1180-1990's procedure: 10 000 blocks of uniform random pixels in [-L, H]
    /// (and the same data negated), reference forward DCT, round, clip — those integer
    /// coefficients are the IDCT input; the IDCT under test is compared with the
    /// reference IDCT, both rounded. Two departures, stated in the report: the PRNG is
    /// our own (not the standard's), and the comparison happens in 8-bit sample space,
    /// so outputs are level-shifted and clamped to [0, 255] (identically for the
    /// reference) — which only bites in the wide-range tests.
    /// </summary>
    private static List<RandomTest> Ieee1180Style()
    {
        const int blocksW = 100, blocksH = 100; // 10 000 blocks
        const int w = blocksW * 8, h = blocksH * 8;

        // A grayscale q100 stream gives an all-ones quantization table: coefficients written
        // by tj3Transform are then exactly the IDCT input.
        var baseJpeg = TurboEncoder.Encode(new byte[w * h * 3], w, h, new EncodeSettings(TurboJpeg.SampGray, 100));
        var q = JpegDiagnostics.DecodePlanes(baseJpeg)[0].Quant;
        if (q.Any(v => v != 1)) throw new InvalidOperationException("q100 grayscale quant table is not all ones");

        var tests = new List<RandomTest>();
        foreach (var (lo, hi) in new[] { (256, 255), (5, 5), (300, 300) })
        {
            foreach (var sign in new[] { 1, -1 })
            {
                var rng = new XorShift(0x1180_0000u + (uint)(lo * 7 + hi * 13 + (sign + 1)));
                var blocks = new short[blocksW * blocksH][];
                long clipped = 0;
                var px = new double[64];
                var f = new double[64];
                for (var b = 0; b < blocks.Length; b++)
                {
                    for (var i = 0; i < 64; i++) px[i] = sign * (rng.Next(lo + hi + 1) - lo);
                    ReferenceIdct.Forward(px, f);
                    var block = new short[64];
                    for (var i = 0; i < 64; i++)
                    {
                        var v = (int)Math.Round(f[i], MidpointRounding.AwayFromZero);
                        // 1180 clips to [-2048, 2047]; baseline 8-bit Huffman coding further limits
                        // AC to ±1023 and keeps DC differences codable.
                        var limit = 1023;
                        var cv = Math.Clamp(v, -limit, limit);
                        if (cv != v) clipped++;
                        block[i] = (short)cv;
                    }

                    blocks[b] = block;
                }

                var jpeg = TurboCoefficients.Rewrite(baseJpeg, (comp, bx, by) =>
                    comp == 0 && bx < blocksW && by < blocksH ? blocks[by * blocksW + bx] : null);

                var reference = new int[w * h];
                var s = new double[64];
                for (var b = 0; b < blocks.Length; b++)
                {
                    ReferenceIdct.Inverse(blocks[b], s);
                    var bx = b % blocksW;
                    var by = b / blocksW;
                    for (var i = 0; i < 64; i++)
                        reference[(by * 8 + i / 8) * w + bx * 8 + i % 8] = Clamp255(s[i] + 128);
                }

                // Sanity: our entropy decode must see exactly the coefficients we wrote.
                var seen = 0L;
                var oursPlane = JpegDiagnostics.DecodePlanes(jpeg, (comp, bx, by, data) =>
                {
                    if (!data.SequenceEqual(blocks[by * blocksW + bx])) Interlocked.Increment(ref seen);
                })[0];
                if (seen != 0) throw new InvalidOperationException($"1180 stream: {seen} blocks did not round-trip through tj3Transform");

                ErrStats Measure(ReadOnlySpan<byte> plane, int stride)
                {
                    var st = new ErrStats();
                    for (var y = 0; y < h; y++)
                        for (var x = 0; x < w; x++)
                            st.Add(plane[y * stride + x] - reference[y * w + x], (y & 7) * 8 + (x & 7));
                    return st;
                }

                using var turbo = new TurboDecoder(fast: false);
                using var fast = new TurboDecoder(fast: true);
                var tp = turbo.DecodeYuvPlanes(jpeg)[0];
                var fp = fast.DecodeYuvPlanes(jpeg)[0];
                tests.Add(new RandomTest($"L={lo}, H={hi}, sign {(sign > 0 ? "+" : "−")}", lo, hi, sign, clipped,
                    Measure(oursPlane.Samples, oursPlane.Width), Measure(tp.Plane, tp.Width), Measure(fp.Plane, fp.Width)));
            }
        }

        return tests;
    }

    private struct XorShift(uint seed)
    {
        private uint _s = seed == 0 ? 1u : seed;

        public int Next(int n)
        {
            _s ^= _s << 13;
            _s ^= _s >> 17;
            _s ^= _s << 5;
            return (int)((ulong)_s * (uint)n >> 32);
        }
    }

    // ------------------------------------------------------------------ rendering

    private static string Render(List<FileResult> results, List<RandomTest> random)
    {
        var sb = new StringBuilder();
        string F(double v, string fmt = "F4") => double.IsPositiveInfinity(v) ? "∞" : v.ToString(fmt, CultureInfo.InvariantCulture);
        string Yes(bool b) => b ? "yes" : "**no**";
        string Signed(double v) { var r = Math.Round(v, 4); return r == 0 ? "0.0000" : r.ToString("+0.0000;-0.0000", CultureInfo.InvariantCulture); }

        sb.AppendLine("### Coefficient layer (libjpeg-turbo `tj3Transform` custom filter vs our entropy decoder)");
        sb.AppendLine();
        var totalCoeff = results.Sum(r => r.CoeffCompared);
        var totalMismatch = results.Sum(r => r.CoeffMismatch);
        sb.AppendLine($"{results.Count} files (every 3 MP matrix file + camera originals), {results.Sum(r => r.BlocksCompared):N0} blocks, " +
                      $"{totalCoeff:N0} quantized coefficients compared: **{totalMismatch:N0} mismatches** " +
                      $"in {results.Sum(r => r.BlocksMismatch):N0} blocks.");
        foreach (var r in results.Where(r => r.CoeffMismatch > 0 || r.CoeffNote is not null))
            sb.AppendLine($"- `{r.Case.Id}`: {r.CoeffMismatch:N0} mismatching coefficients{(r.CoeffNote is null ? "" : " — " + r.CoeffNote)}");
        sb.AppendLine();

        sb.AppendLine("### Component planes (after IDCT, before upsampling) vs double-precision reference IDCT");
        sb.AppendLine();
        sb.AppendLine("Error = decoder sample − round(reference IDCT + 128), clamped to [0, 255]. Mean is signed (bias).");
        sb.AppendLine("IEEE 1180 columns apply the 1180-1990 limits (peak ≤ 1, per-position MSE ≤ 0.06, per-position |mean| ≤ 0.015, " +
                      "overall MSE ≤ 0.02, overall |mean| ≤ 0.0015) to these real-image statistics.");
        sb.AppendLine();
        sb.AppendLine("| Group | Plane | Decoder | Peak | MSE | Mean | Max pos. MSE | Max pos. \\|mean\\| | Exact % | Meets 1180 limits |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|:---:|");
        var allOursL = new ErrStats(); var allOursC = new ErrStats(); var allTurboL = new ErrStats(); var allTurboC = new ErrStats();
        foreach (var g in results.GroupBy(r => r.Case.Group).OrderBy(g => GroupSort(g.Key)))
        {
            foreach (var (plane, pick) in new (string, Func<FileResult, (ErrStats Ours, ErrStats Turbo)>)[]
                     {
                         ("Y", r => (r.OursLuma, r.TurboLuma)),
                         ("Cb/Cr", r => (r.OursChroma, r.TurboChroma)),
                     })
            {
                var o = new ErrStats(); var t = new ErrStats();
                foreach (var r in g) { var (a, b) = pick(r); o.Merge(a); t.Merge(b); }
                if (o.N == 0) continue;
                (plane == "Y" ? allOursL : allOursC).Merge(o);
                (plane == "Y" ? allTurboL : allTurboC).Merge(t);
                foreach (var (name, st) in new[] { ("Sdcb.SimdCodecs", o), ("libjpeg-turbo", t) })
                    sb.AppendLine($"| {g.Key} | {plane} | {name} | {st.Peak} | {F(st.Mse)} | {Signed(st.Mean)} | {F(st.MaxPosMse)} | {F(st.MaxPosAbsMean)} | {F(st.ExactPct, "F2")} | {Yes(st.Meets1180)} |");
            }
        }

        foreach (var (plane, o, t) in new[] { ("Y", allOursL, allTurboL), ("Cb/Cr", allOursC, allTurboC) })
            foreach (var (name, st) in new[] { ("Sdcb.SimdCodecs", o), ("libjpeg-turbo", t) })
                sb.AppendLine($"| **all** | {plane} | {name} | {st.Peak} | {F(st.Mse)} | {Signed(st.Mean)} | {F(st.MaxPosMse)} | {F(st.MaxPosAbsMean)} | {F(st.ExactPct, "F2")} | {Yes(st.Meets1180)} |");
        sb.AppendLine();
        var oursPeak = Math.Max(allOursL.Peak, allOursC.Peak);
        var turboPeak = Math.Max(allTurboL.Peak, allTurboC.Peak);
        sb.AppendLine($"ITU-T T.83 / ISO/IEC 10918-2 decoder compliance allows each reconstructed sample to differ from the reference by at most 1 " +
                      $"(as commonly cited; the standard's text was not consulted for this table). Against the double-precision reference on this corpus: " +
                      $"Sdcb.SimdCodecs peak {oursPeak} → {(oursPeak <= 1 ? "**meets**" : "**fails**")}; libjpeg-turbo peak {turboPeak} → {(turboPeak <= 1 ? "**meets**" : "**fails**")}.");
        sb.AppendLine();

        sb.AppendLine("### IEEE 1180-style random-block test (10 000 blocks each, through real JPEG streams)");
        sb.AppendLine();
        sb.AppendLine("Departures from the standard: our own PRNG, coefficients limited to ±1023 so baseline Huffman can code them, " +
                      "and comparison in 8-bit sample space (level shift + clamp to [0, 255], applied to the reference too).");
        sb.AppendLine();
        sb.AppendLine("| Input | Clipped coeffs | Decoder | Peak | MSE | Mean | Max pos. MSE | Max pos. \\|mean\\| | Meets 1180 limits |");
        sb.AppendLine("|---|---:|---|---:|---:|---:|---:|---:|:---:|");
        foreach (var t in random)
            foreach (var (name, st) in new[] { ("Sdcb.SimdCodecs", t.Ours), ("libjpeg-turbo", t.Turbo), ("libjpeg-turbo (fast)", t.TurboFast) })
                sb.AppendLine($"| {t.Name} | {t.ClippedCoeffs:N0} | {name} | {st.Peak} | {F(st.Mse)} | {Signed(st.Mean)} | {F(st.MaxPosMse)} | {F(st.MaxPosAbsMean)} | {Yes(st.Meets1180)} |");
        sb.AppendLine();

        sb.AppendLine("### RGB output vs reference IDCT + linear chroma interpolation + double-precision JFIF (T.871) colour conversion");
        sb.AppendLine();
        sb.AppendLine("Reference: IDCT in double, +128, clamp to [0, 255] (unrounded); subsampled chroma brought to full resolution by bilinear " +
                      "interpolation at T.871's centred sample positions (¾·near + ¼·far for 2×, edges replicated); YCbCr→RGB in double; round, clamp. " +
                      "Every 3-component file whose luma is full resolution (all matrix files and camera originals).");
        if (BaselineDecoder.Description is { } baselineDescription)
            sb.AppendLine($"\"{BaselineLabel}\" is commit {baselineDescription}, loaded side by side (see `baseline`).");
        sb.AppendLine();
        sb.AppendLine("| Group | Decoder | Peak | MSE | Mean | PSNR (dB) | Exact % |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|");
        var bySampling = new SortedDictionary<string, Dictionary<string, ErrStats>>(StringComparer.Ordinal);
        foreach (var g in results.Where(r => r.Rgb.Count > 0).GroupBy(r => r.Case.Group).OrderBy(g => GroupSort(g.Key)))
        {
            foreach (var c in RgbLabels)
            {
                var st = new ErrStats();
                foreach (var r in g)
                {
                    st.Merge(r.Rgb[c]);
                    if (!bySampling.TryGetValue(r.Case.Sampling, out var totals))
                        bySampling[r.Case.Sampling] = totals = RgbLabels.ToDictionary(x => x, _ => new ErrStats());
                    totals[c].Merge(r.Rgb[c]);
                }

                sb.AppendLine($"| {g.Key} | {c} | {st.Peak} | {F(st.Mse)} | {Signed(st.Mean)} | {F(st.Psnr, "F2")} | {F(st.ExactPct, "F2")} |");
            }
        }

        foreach (var (sampling, totals) in bySampling)
        {
            foreach (var c in RgbLabels)
            {
                var st = totals[c];
                sb.AppendLine($"| **all {sampling}** | {c} | {st.Peak} | {F(st.Mse)} | {Signed(st.Mean)} | {F(st.Psnr, "F2")} | {F(st.ExactPct, "F2")} |");
            }
        }

        sb.AppendLine();
        return sb.ToString();
    }

    internal static string GroupSort(string group)
    {
        // Order: sampling, then quality numerically, then variant.
        var parts = group.Split('-');
        if (parts.Length < 2 || !parts[1].StartsWith('q')) return "~" + group;
        var q = int.Parse(parts[1][1..], CultureInfo.InvariantCulture);
        return $"{parts[0]}-{q:D3}-{string.Join('-', parts.Skip(2))}";
    }
}
