using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Webp.Benchmarks;

/// <summary>
/// Black-box probe of how libwebp's <c>anim_dump</c> alpha-blends a frame over the canvas (RFC 9649
/// describes the blend in real numbers and leaves the integer rounding to the decoder). Each trial is a
/// two-frame lossless animation on a 256×256 canvas: frame 0 draws the canvas (pixel x, y has alpha y),
/// frame 1 is blended over it (alpha x), so every (source alpha, canvas alpha) pair occurs once, with
/// colours from a hash (0 under alpha 0, as the lossless encoder writes them). anim_dump's second canvas
/// is compared with <see cref="WebpAnimationDecoder"/>'s and every sample is written to
/// <c>.cache/work/blend-probe.bin</c> as (source alpha, canvas alpha, source value, canvas value, output
/// value, output alpha) bytes, three per pixel, for offline fitting.
/// </summary>
internal static class BlendProbe
{
    public static int Run(string? cwebp, int trials)
    {
        string bin = Path.GetDirectoryName(cwebp ?? Cwebp.Find(null) ?? throw new InvalidOperationException("cwebp not found"))!;
        string animDump = Path.Combine(bin, OperatingSystem.IsWindows() ? "anim_dump.exe" : "anim_dump");
        const int N = 256;
        string output = Path.Combine(BenchPaths.Work, "blend-probe.bin");
        Directory.CreateDirectory(BenchPaths.Work);
        using var file = File.Create(output);
        long samples = 0, mismatches = 0, alphaMismatches = 0;
        var examples = new List<string>();
        for (int t = 0; t < trials; t++)
        {
            var canvas = new byte[N * N * 4];
            var source = new byte[N * N * 4];
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    int i = 4 * (y * N + x);
                    for (int c = 0; c < 3; c++)
                    {
                        // Trial 0 and 1: the extremes; then hashed values.
                        canvas[i + c] = (byte)(y == 0 ? 0 : t switch { 0 => 255, 1 => 0, _ => Hash(x, y, c, t) });
                        source[i + c] = (byte)(x == 0 ? 0 : t switch { 0 => 0, 1 => 255, _ => Hash(x, y, c + 3, t) });
                    }

                    canvas[i + 3] = (byte)y;
                    source[i + 3] = (byte)x;
                }
            }

            var encoder = new WebpAnimationEncoder(N, N, new WebpAnimationEncodeOptions { Mode = WebpAnimationMode.Lossless, LosslessEffort = 1 });
            encoder.AddFrame(new WebpAnimationFrame { Pixels = canvas, Width = N, Height = N, Duration = TimeSpan.FromMilliseconds(10) });
            encoder.AddFrame(new WebpAnimationFrame { Pixels = source, Width = N, Height = N, Duration = TimeSpan.FromMilliseconds(10), Blend = true });
            byte[] webp = encoder.Encode();
            var sequence = new AnimSequence("probe", N, N, [canvas, source], 10, true);
            byte[] theirs = EncodeAnim.AnimDump(animDump, webp, sequence)[1];

            var decoder = new WebpAnimationDecoder(webp);
            var ours = new byte[N * N * 4];
            decoder.TryDecodeNextFrame(ours, out _);
            decoder.TryDecodeNextFrame(ours, out _);

            var record = new byte[6 * 3 * N * N];
            int at = 0;
            for (int p = 0; p < N * N; p++)
            {
                int i = 4 * p;
                if (theirs[i + 3] != ours[i + 3])
                {
                    alphaMismatches++;
                }

                for (int c = 0; c < 3; c++)
                {
                    record[at++] = source[i + 3];
                    record[at++] = canvas[i + 3];
                    record[at++] = source[i + c];
                    record[at++] = canvas[i + c];
                    record[at++] = theirs[i + c];
                    record[at++] = theirs[i + 3];
                    samples++;
                    if (theirs[i + 3] != 0 && theirs[i + c] != ours[i + c])
                    {
                        mismatches++;
                        if (examples.Count < 12)
                        {
                            examples.Add($"srcA {source[i + 3]} dstA {canvas[i + 3]} src {source[i + c]} dst {canvas[i + c]}: anim_dump {theirs[i + c]}, ours {ours[i + c]}");
                        }
                    }
                }
            }

            file.Write(record);
            Console.WriteLine($"[blend-probe] trial {t + 1}/{trials}");
        }

        Console.WriteLine($"{samples} colour samples, {mismatches} visible colour mismatches, {alphaMismatches} alpha mismatches");
        foreach (string e in examples)
        {
            Console.WriteLine("  " + e);
        }

        Console.WriteLine($"[blend-probe] {output}");
        return 0;

        static int Hash(int x, int y, int c, int t)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(c * 83492791) ^ (uint)(t * 2654435761u);
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return (int)(h & 0xFF);
        }
    }
}
