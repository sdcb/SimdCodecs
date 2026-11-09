using System.Diagnostics;
using System.Reflection;
using System.Runtime.Intrinsics;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jpeg.WasmBench;

/// <summary>
/// Whole-image JPEG decode time and its per-stage split, by ablation, on whatever runtime runs
/// this (browser-wasm Mono AOT or the desktop JIT). Shared verbatim by both projects so the two
/// columns of a comparison measure the same code.
/// </summary>
internal static class Stages
{
    private const int Warmup = 3, Rounds = 10;

    /// <summary>The images embedded by the project (<c>.cache/images/*.jpg</c>), by file name.</summary>
    public static IEnumerable<(string Name, byte[] Data)> EmbeddedImages()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".jpg", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            var data = new byte[s.Length];
            s.ReadExactly(data);
            yield return (name, data);
        }
    }

    public static int Run(string runtime)
    {
        Console.WriteLine($"# {runtime}: V128={Vector128.IsHardwareAccelerated} V256={Vector256.IsHardwareAccelerated} " +
            $"cores={Environment.ProcessorCount}; median (min) ms of {Rounds} rounds after {Warmup} warm-ups");
        if (!Verify.Run())
            return 1;
        Console.WriteLine("image\tfacade\trgba\tentropy\tidct\tup+color\tno-idct\tno-up+color\tchecksum");
        var any = false;
        foreach (var (name, data) in EmbeddedImages())
        {
            any = true;
            RunOne(name, data);
        }

        if (!any)
            Console.WriteLine("no images embedded: run run.sh (it fetches photo.jpg and prepares the variants)");
        Kernels();
        return any ? 0 : 1;
    }

    private static void RunOne(string name, byte[] data)
    {
        var info = JpegDecoder.ReadInfo(data);
        var rgba = new byte[info.Width * info.Height * 4];
        ReadOnlyMemory<byte> memory = data;

        // Output check first: a fast number from a wrong decode is worthless. The checksum is
        // printed so the wasm and desktop runs can be compared byte for byte.
        JpegDiagnostics.DecodeTo(data, rgba, JpegAblation.None);
        var checksum = Fnv(rgba);

        Action[] modes =
        [
            () =>
            {
                if (!ImageCodecs.TryOpenFrames(memory, out var frames))
                    throw new InvalidOperationException("not openable");
                using (frames)
                {
                    if (!frames.TryDecodeFrame(0, out var image))
                        throw new InvalidOperationException("not decodable");
                    image.Dispose();
                }
            },
            () => JpegDiagnostics.DecodeTo(data, rgba, JpegAblation.None),
            () => JpegDiagnostics.DecodeTo(data, rgba, JpegAblation.SkipBlockSink),
            () => JpegDiagnostics.DecodeTo(data, rgba, JpegAblation.SkipIdct),
            () => JpegDiagnostics.DecodeTo(data, rgba, JpegAblation.SkipUpsample | JpegAblation.SkipColor),
        ];

        var times = new double[modes.Length][];
        for (var m = 0; m < modes.Length; m++)
            times[m] = new double[Rounds];

        // Interleaved per round, so drift hits every mode alike.
        for (var r = -Warmup; r < Rounds; r++)
        {
            for (var m = 0; m < modes.Length; m++)
            {
                var t = Stopwatch.GetTimestamp();
                modes[m]();
                var ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                if (r >= 0)
                    times[m][r] = ms;
            }
        }

        var med = times.Select(Median).ToArray();
        var min = times.Select(t => t.Min()).ToArray();
        string F(int m) => $"{med[m]:F2} ({min[m]:F2})";
        Console.WriteLine($"{name} {info.Width}x{info.Height}\t{F(0)}\t{F(1)}\t{F(2)}\t{med[1] - med[3]:F2}\t{med[1] - med[4]:F2}\t{F(3)}\t{F(4)}\t{checksum:x16}");
    }

    /// <summary>
    /// The row and block kernels alone, sized like photo.jpg (1600×1067, 4:2:0): one call per
    /// output row (or per 8×8 block), so a stage's cost can be split between its kernels.
    /// </summary>
    private static void Kernels()
    {
        const int W = 1600, H = 1067, Blocks = 200 * 134 * 6 / 4; // Y + Cb + Cr blocks of a 4:2:0 frame
        var rng = new Random(1);
        var plane = new byte[W * 2];
        rng.NextBytes(plane);
        var y = new byte[W];
        rng.NextBytes(y);
        var cb16 = new ushort[W];
        var cr16 = new ushort[W];
        for (var i = 0; i < W; i++)
            (cb16[i], cr16[i]) = ((ushort)rng.Next(4081), (ushort)rng.Next(4081));
        var rgba = new byte[W * 4];
        var line8 = new byte[W];
        var block = new short[64];
        for (var i = 0; i < 64; i++)
            block[i] = (short)(i < 20 ? rng.Next(-300, 300) : 0);
        var outPlane = new byte[8 * 64];

        (string Name, Action Run)[] kernels =
        [
            ("YccRow16 x H", () => { for (var j = 0; j < H; j++) JpegColor.YccRow(rgba, y, cb16, cr16, W); }),
            ("Row16 Hv2 x 2H", () => { for (var j = 0; j < 2 * H; j++) JpegResample.Row16(JpegResample.Hv2, cb16, plane, 0, W / 2, W / 2, 2); }),
            ("Row Hv2 x 2H", () => { for (var j = 0; j < 2 * H; j++) JpegResample.Row(JpegResample.Hv2, line8, cb16, plane, 0, W / 2, W / 2, 2); }),
            ("Idct8x8 x blocks", () => { for (var j = 0; j < Blocks; j++) JpegIdct.Idct8x8(outPlane, 0, 64, block); }),
            ("Idct8x8(last=19)", () => { for (var j = 0; j < Blocks; j++) JpegIdct.Idct8x8(outPlane, 0, 64, block, 19); }),
            ("Idct8x8Scalar", () => { for (var j = 0; j < Blocks; j++) JpegIdct.Idct8x8Scalar(outPlane, 0, 64, block); }),
        ];
        foreach (var (name, run) in kernels)
        {
            var t = new double[Rounds];
            for (var r = -Warmup; r < Rounds; r++)
            {
                var start = Stopwatch.GetTimestamp();
                run();
                if (r >= 0)
                    t[r] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            Console.WriteLine($"kernel {name}	{Median(t):F2} ({t.Min():F2})");
        }
    }

    private static double Median(double[] t)
    {
        var s = t.Order().ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    private static ulong Fnv(ReadOnlySpan<byte> data)
    {
        var h = 14695981039346656037UL;
        foreach (var b in data)
            h = (h ^ b) * 1099511628211UL;
        return h;
    }
}
