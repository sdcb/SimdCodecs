using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Sdcb.SimdCodecs.Webp.Benchmarks;

/// <summary>An animation input: full-canvas RGBA frames, each shown for <see cref="DurationMs"/>.</summary>
internal sealed record AnimSequence(string Name, int Width, int Height, List<byte[]> Frames, int DurationMs, bool HasAlpha)
{
    public long Pixels => (long)Width * Height * Frames.Count;
}

/// <summary>An animation encoder: frames in, file and seconds out.</summary>
internal sealed record AnimContestant(string Name, Func<AnimSequence, (byte[] Webp, double Seconds)> Encode);

/// <summary>
/// Animated WebP encode comparison. The reference is libwebp's own animation encoder as shipped in
/// the 1.6.0 release (<c>img2webp</c>, which drives WebPAnimEncoder; its time is the process wall
/// clock and so includes reading the PAM frames). Every output is decoded by <c>anim_dump</c> (libwebp's
/// WebPAnimDecoder, a black box) into composited canvases before it counts: lossless output must
/// reproduce every alpha value and every visible pixel of every frame; lossy output is scored by the
/// PSNR of the visible RGB over all frames, and swept over quality for a BD-rate.
/// <para>The inputs are synthetic and deterministic, each built for one thing an animation encoder
/// decides: <c>scroll</c> (everything moves: no sub-rectangle helps), <c>sprite</c> (a small object
/// moves over a still photo: the changed rectangle decides), <c>fade</c> (every pixel changes a little),
/// <c>alpha-sprite</c> (an anti-aliased object on transparency: blend and dispose decide) and
/// <c>slides</c> (held frames: identical frames must cost nearly nothing).</para>
/// </summary>
internal static class EncodeAnim
{
    public static bool Run(string? cwebp, int reps, string? filter, Action<string> line, List<AnimContestant>? ours = null)
    {
        string? bin = cwebp is null ? null : Path.GetDirectoryName(cwebp);
        string? img2webp = Tool(bin, "img2webp"), animDump = Tool(bin, "anim_dump");
        if (img2webp is null || animDump is null)
        {
            line("");
            line("### anim: skipped (img2webp / anim_dump not found next to cwebp; see README)");
            return true;
        }

        List<AnimSequence> sequences = Sequences(filter);
        line("");
        line($"### anim ({sequences.Count} sequences, {sequences.Sum(s => s.Pixels) / 1e6:F2} MP of frames; img2webp time is process wall clock)");
        line("");
        line("| sequence | canvas | frames | alpha |");
        line("|---|---|---:|---|");
        foreach (AnimSequence s in sequences)
        {
            line($"| {s.Name} | {s.Width}×{s.Height} | {s.Frames.Count} | {(s.HasAlpha ? "yes" : "no")} |");
        }

        bool ok = true;

        // Lossless (and mixed) at the default settings: size, time, exactness.
        var lossless = new List<AnimContestant>
        {
            new("img2webp -lossless", s => Img2Webp(img2webp, s, "-lossless")),
            new("img2webp -mixed", s => Img2Webp(img2webp, s, "-mixed")),
        };
        lossless.AddRange(ours?.Where(c => !c.Name.Contains("lossy", StringComparison.Ordinal)) ?? []);
        var bytes = new Dictionary<string, long>();
        line("");
        line("| encoder | MB | bpp | size vs img2webp -lossless | MP/s | accuracy |");
        line("|---|---:|---:|---:|---:|---|");
        foreach (AnimContestant c in lossless)
        {
            long total = 0;
            double seconds = 0;
            string accuracy = "exact";
            double minPsnr = double.MaxValue;
            foreach (AnimSequence s in sequences)
            {
                (byte[] webp, double first) = c.Encode(s);
                List<byte[]> decoded = AnimDump(animDump, webp, s);
                bool lossy = c.Name.Contains("mixed", StringComparison.Ordinal);
                if (!lossy && !VisibleEqual(s, decoded))
                {
                    accuracy = "WRONG";
                    Console.WriteLine($"[anim] {c.Name} on {s.Name}: decoded frames differ from the source");
                }
                else if (lossy)
                {
                    minPsnr = Math.Min(minPsnr, Psnr(s, decoded));
                    accuracy = string.Create(CultureInfo.InvariantCulture, $"lowest PSNR {minPsnr:F1} dB");
                }

                var times = new double[Math.Max(1, reps)];
                times[0] = first;
                for (int r = 1; r < times.Length; r++)
                {
                    times[r] = c.Encode(s).Seconds;
                }

                Array.Sort(times);
                seconds += times[times.Length / 2];
                total += webp.Length;
            }

            bytes[c.Name] = total;
            double pixels = sequences.Sum(s => (double)s.Pixels);
            line(string.Create(CultureInfo.InvariantCulture,
                $"| {c.Name} | {total / 1e6:F3} | {total * 8.0 / pixels:F3} | {(double)total / bytes[lossless[0].Name]:F3}x | {pixels / 1e6 / seconds:F2} | {accuracy} |"));
            ok &= accuracy != "WRONG";
            Console.WriteLine($"[anim] {c.Name} done");
        }

        if (bytes.TryGetValue("ours", out long mine))
        {
            bool sizeOk = mine <= bytes[lossless[0].Name];
            line("");
            line($"[gate] anim lossless ours {mine} bytes vs img2webp -lossless {bytes[lossless[0].Name]}: {(sizeOk ? "ok" : "LARGER")}");
            ok &= sizeOk;
        }

        // Lossy: a quality sweep, BD-rate on visible PSNR-RGB.
        var lossyCurves = new List<(string Name, Func<AnimSequence, int, byte[]> Encode)>
        {
            ("img2webp -lossy", (s, q) => Img2Webp(img2webp, s, "-lossy", "-q", q.ToString(CultureInfo.InvariantCulture)).Webp),
        };
        lossyCurves.AddRange(OursLossyCurves);
        line("");
        line("| encoder | BD-rate (visible PSNR-RGB) vs img2webp -lossy | per sequence |");
        line("|---|---:|---|");
        int[] qs = [30, 50, 70, 80, 90];
        var curves = new Dictionary<(string, string), List<(long, double)>>();
        foreach ((string name, Func<AnimSequence, int, byte[]> encode) in lossyCurves)
        {
            foreach (AnimSequence s in sequences)
            {
                var points = new List<(long, double)>();
                foreach (int q in qs)
                {
                    byte[] webp = encode(s, q);
                    points.Add((webp.Length, Psnr(s, AnimDump(animDump, webp, s))));
                }

                curves[(name, s.Name)] = points;
            }
        }

        foreach ((string name, _) in lossyCurves)
        {
            double[] per = sequences.Select(s => EncodeRd.BdRate(curves[(lossyCurves[0].Name, s.Name)], curves[(name, s.Name)])).ToArray();
            double mean = per.Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Average();
            line(string.Create(CultureInfo.InvariantCulture,
                $"| {name} | {mean * 100:+0.00;-0.00}% | {string.Join(", ", sequences.Select((s, i) => $"{s.Name} {per[i] * 100:+0.0;-0.0}%"))} |"));
            if (name == "ours lossy")
            {
                bool rdOk = mean <= 0;
                line("");
                line(string.Create(CultureInfo.InvariantCulture, $"[gate] anim lossy BD-rate vs img2webp: {mean * 100:+0.00;-0.00}%: {(rdOk ? "ok" : "LARGER")}"));
                ok &= rdOk;
            }
        }

        return ok;
    }

    /// <summary>Our lossy animation encoder as a quality curve, once it exists.</summary>
    internal static readonly List<(string Name, Func<AnimSequence, int, byte[]> Encode)> OursLossyCurves = [];

    private static string? Tool(string? dir, string name)
    {
        string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        if (dir is not null && File.Exists(Path.Combine(dir, exe)))
        {
            return Path.Combine(dir, exe);
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
    }

    // ---------------------------------------------------------------- libwebp tools

    private static readonly Dictionary<string, string[]> FrameFiles = [];

    /// <summary>Encodes with img2webp: every frame as a PAM, then <c>-d duration frame</c> for each.</summary>
    private static (byte[] Webp, double Seconds) Img2Webp(string img2webp, AnimSequence s, params string[] options)
    {
        string[] files = FramesFor(s);
        string output = Path.Combine(BenchPaths.Work, "anim-out.webp");
        File.Delete(output);
        var args = new List<string> { "-loop", "0" };
        bool fileLevel = options.Length > 0 && options[0] == "-mixed";
        if (fileLevel)
        {
            args.Add("-mixed");
        }

        foreach (string f in files)
        {
            args.Add("-d");
            args.Add(s.DurationMs.ToString(CultureInfo.InvariantCulture));
            if (!fileLevel)
            {
                args.AddRange(options);
            }

            args.Add(f);
        }

        args.Add("-o");
        args.Add(output);
        long start = Stopwatch.GetTimestamp();
        Exec(img2webp, args);
        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        if (!File.Exists(output))
        {
            throw new InvalidOperationException($"img2webp {string.Join(' ', options)} failed on {s.Name}");
        }

        return (File.ReadAllBytes(output), seconds);
    }

    private static string[] FramesFor(AnimSequence s)
    {
        lock (FrameFiles)
        {
            if (!FrameFiles.TryGetValue(s.Name, out string[]? files))
            {
                string dir = Path.Combine(BenchPaths.Work, "anim-" + s.Name);
                Directory.CreateDirectory(dir);
                files = new string[s.Frames.Count];
                for (int i = 0; i < files.Length; i++)
                {
                    files[i] = Path.Combine(dir, $"f{i:D3}.pam");
                    WritePam(files[i], s.Width, s.Height, s.Frames[i]);
                }

                FrameFiles[s.Name] = files;
            }

            return files;
        }
    }

    /// <summary>Decodes with anim_dump into composited canvases, one per frame.</summary>
    internal static List<byte[]> AnimDump(string animDump, byte[] webp, AnimSequence s)
    {
        string dir = Path.Combine(BenchPaths.Work, "anim-dump");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        string input = Path.Combine(dir, "in.webp");
        File.WriteAllBytes(input, webp);
        Exec(animDump, ["-folder", dir, "-prefix", "f", "-pam", input]);
        var frames = new List<byte[]>();
        foreach (string file in Directory.GetFiles(dir, "f*.pam").Order(StringComparer.Ordinal))
        {
            (int w, int h, byte[] rgba) = ReadPam(file);
            if (w != s.Width || h != s.Height)
            {
                throw new InvalidOperationException($"anim_dump: {s.Name} frame is {w}×{h}, expected {s.Width}×{s.Height}");
            }

            frames.Add(rgba);
        }

        // An encoder may merge identical frames into one longer frame, so the source frames are matched
        // to the decoded ones on the timeline: source frame f shows at f × duration.
        int[] durations = FrameDurations(webp);
        if (durations.Length != frames.Count)
        {
            throw new InvalidOperationException($"anim_dump: {s.Name} gave {frames.Count} frames for {durations.Length} ANMF chunks");
        }

        var timeline = new List<byte[]>();
        int index = 0;
        long end = durations.Length > 0 ? durations[0] : 0;
        for (int f = 0; f < s.Frames.Count; f++)
        {
            long t = (long)f * s.DurationMs;
            while (t >= end && index + 1 < durations.Length)
            {
                index++;
                end += durations[index];
            }

            timeline.Add(frames[index]);
        }

        if (end != (long)s.Frames.Count * s.DurationMs)
        {
            throw new InvalidOperationException($"anim_dump: {s.Name} lasts {end} ms, expected {s.Frames.Count * s.DurationMs}");
        }

        return timeline;
    }

    /// <summary>The duration of every ANMF chunk (RFC 9649 §2.7.1.1: a 24-bit field at payload offset 12).</summary>
    private static int[] FrameDurations(byte[] webp)
    {
        var list = new List<int>();
        int pos = 12;
        while (pos + 8 <= webp.Length)
        {
            int size = BitConverter.ToInt32(webp, pos + 4);
            if (webp.AsSpan(pos, 4).SequenceEqual("ANMF"u8))
            {
                list.Add(webp[pos + 20] | webp[pos + 21] << 8 | webp[pos + 22] << 16);
            }

            pos += 8 + size + (size & 1);
        }

        return list.ToArray();
    }

    private static void Exec(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({p.ExitCode}): {err.Result}");
        }
    }

    internal static void WritePam(string path, int width, int height, byte[] rgba)
    {
        using var fs = File.Create(path);
        fs.Write(Encoding.ASCII.GetBytes($"P7\nWIDTH {width}\nHEIGHT {height}\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n"));
        fs.Write(rgba);
    }

    internal static (int Width, int Height, byte[] Rgba) ReadPam(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int end = data.AsSpan().IndexOf("ENDHDR\n"u8);
        string header = Encoding.ASCII.GetString(data, 0, end);
        int Field(string name) => int.Parse(header.Split('\n').First(l => l.StartsWith(name + " ", StringComparison.Ordinal))[(name.Length + 1)..],
            CultureInfo.InvariantCulture);
        int w = Field("WIDTH"), h = Field("HEIGHT"), depth = Field("DEPTH");
        int offset = end + 7;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            for (int c = 0; c < 4; c++)
            {
                rgba[i * 4 + c] = c < depth ? data[offset + i * depth + c] : (byte)255;
            }
        }

        return (w, h, rgba);
    }

    // ---------------------------------------------------------------- scoring

    internal static bool VisibleEqual(AnimSequence s, List<byte[]> decoded)
    {
        for (int f = 0; f < s.Frames.Count; f++)
        {
            byte[] a = s.Frames[f], b = decoded[f];
            for (int i = 0; i < a.Length; i += 4)
            {
                if (a[i + 3] != b[i + 3] || (a[i + 3] != 0 && (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2])))
                {
                    return false;
                }
            }
        }

        return true;
    }

    internal static double Psnr(AnimSequence s, List<byte[]> decoded)
    {
        double se = 0;
        long n = 0;
        for (int f = 0; f < s.Frames.Count; f++)
        {
            byte[] a = s.Frames[f], b = decoded[f];
            for (int i = 0; i < a.Length; i += 4)
            {
                if (a[i + 3] == 0)
                {
                    continue;
                }

                for (int c = 0; c < 3; c++)
                {
                    int d = a[i + c] - b[i + c];
                    se += d * d;
                }

                n += 3;
            }
        }

        return n == 0 ? 99 : 10 * Math.Log10(255.0 * 255 / Math.Max(se / n, 1e-12));
    }

    // ---------------------------------------------------------------- inputs

    public static List<AnimSequence> Sequences(string? filter)
    {
        const int W = 480, H = 320, N = 24;
        byte[] photoA = Background(W, H, 0), photoB = Background(W, H, 1);
        var list = new List<AnimSequence>
        {
            Scroll(W, H, N),
            Sprite(W, H, N, photoA),
            Fade(W, H, N, photoA, photoB),
            AlphaSprite(W, H, N),
            Slides(W, H, N),
        };
        return list.Where(s => filter is null || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The first two photo masters (downscaled and cropped), or gradients without them.</summary>
    private static readonly Lazy<List<EncodeImage>> Photos = new(() => Directory.Exists(EncodeCorpus.Masters)
        ? EncodeCorpus.Load(960, null).Where(i => i.Kind == "photo").ToList()
        : []);

    private static byte[] Background(int w, int h, int index)
    {
        List<EncodeImage> photos = Photos.Value;
        var rgba = new byte[w * h * 4];
        if (photos.Count > index)
        {
            EncodeImage p = photos[index];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Min(p.Width - 1, x * p.Width / (w * 2) + p.Width / 4), sy = Math.Min(p.Height - 1, y * p.Height / (h * 2) + p.Height / 4);
                    p.Rgba.AsSpan(4 * (sy * p.Width + sx), 4).CopyTo(rgba.AsSpan(4 * (y * w + x)));
                }
            }

            return rgba;
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = 4 * (y * w + x);
                rgba[o] = (byte)(index == 0 ? x * 255 / w : 255 - y * 255 / h);
                rgba[o + 1] = (byte)((x + y) * 255 / (w + h));
                rgba[o + 2] = (byte)(index == 0 ? y * 255 / h : x * 255 / w);
                rgba[o + 3] = 255;
            }
        }

        return rgba;
    }

    private static uint Hash(int x, int y, uint seed)
    {
        uint h = (uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u ^ seed * 0xC2B2AE3Du;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 13;
        return h;
    }

    /// <summary>A tall page of text-like glyph rows and flat panels, scrolled 6 pixels a frame.</summary>
    private static AnimSequence Scroll(int w, int h, int n)
    {
        int pageH = h + n * 6;
        var page = new byte[w * pageH * 4];
        for (int y = 0; y < pageH; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = 4 * (y * w + x);
                bool panel = (y / 90) % 3 == 2 && x > 20 && x < w - 20;
                bool glyph = !panel && y % 18 < 12 && x > 16 && x < w - 16 &&
                             (Hash(x / 7, y / 18, 3) & 3) != 0 && (Hash(x, y, 9) & 7) < 3;
                (byte r, byte g, byte b) = panel ? ((byte)40, (byte)110, (byte)200) : glyph ? ((byte)30, (byte)30, (byte)35) : ((byte)250, (byte)250, (byte)248);
                (page[o], page[o + 1], page[o + 2], page[o + 3]) = (r, g, b, 255);
            }
        }

        var frames = new List<byte[]>();
        for (int f = 0; f < n; f++)
        {
            frames.Add(page.AsSpan(4 * w * f * 6, 4 * w * h).ToArray());
        }

        return new AnimSequence("scroll", w, h, frames, 50, HasAlpha: false);
    }

    private static AnimSequence Sprite(int w, int h, int n, byte[] background)
    {
        var frames = new List<byte[]>();
        for (int f = 0; f < n; f++)
        {
            byte[] frame = (byte[])background.Clone();
            int sx = 20 + f * (w - 104) / n, sy = h / 2 - 32 + (int)(40 * Math.Sin(f * 0.5));
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    int o = 4 * ((sy + y) * w + sx + x);
                    bool ring = Math.Abs(Math.Sqrt((x - 31.5) * (x - 31.5) + (y - 31.5) * (y - 31.5)) - 24) < 6;
                    if (ring)
                    {
                        (frame[o], frame[o + 1], frame[o + 2]) = ((byte)250, (byte)200, (byte)20);
                    }
                }
            }

            frames.Add(frame);
        }

        return new AnimSequence("sprite", w, h, frames, 80, HasAlpha: false);
    }

    private static AnimSequence Fade(int w, int h, int n, byte[] a, byte[] b)
    {
        var frames = new List<byte[]>();
        for (int f = 0; f < n; f++)
        {
            double t = (double)f / (n - 1);
            var frame = new byte[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                frame[i] = (i & 3) == 3 ? (byte)255 : (byte)Math.Round(a[i] * (1 - t) + b[i] * t);
            }

            frames.Add(frame);
        }

        return new AnimSequence("fade", w, h, frames, 60, HasAlpha: false);
    }

    private static AnimSequence AlphaSprite(int w, int h, int n)
    {
        var frames = new List<byte[]>();
        for (int f = 0; f < n; f++)
        {
            var frame = new byte[w * h * 4];
            double cx = 60 + f * (w - 120.0) / n, cy = h / 2.0 + 60 * Math.Sin(f * 0.4), r = 44;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    double a = Math.Clamp((r - d) / 1.5 + 0.5, 0, 1);
                    if (a > 0)
                    {
                        int o = 4 * (y * w + x);
                        (frame[o], frame[o + 1], frame[o + 2]) = ((byte)(120 + x * 135 / w), (byte)60, (byte)(200 - y * 150 / h));
                        frame[o + 3] = (byte)Math.Round(a * 255);
                    }
                }
            }

            // A fixed, semi-transparent caption bar, the same in every frame.
            for (int y = h - 40; y < h - 12; y++)
            {
                for (int x = 30; x < w - 30; x++)
                {
                    int o = 4 * (y * w + x);
                    if (frame[o + 3] == 0)
                    {
                        (frame[o], frame[o + 1], frame[o + 2], frame[o + 3]) = (20, 20, 20, 160);
                    }
                }
            }

            frames.Add(frame);
        }

        return new AnimSequence("alpha-sprite", w, h, frames, 70, HasAlpha: true);
    }

    private static AnimSequence Slides(int w, int h, int n)
    {
        byte[][] slides = [Background(w, h, 0), Background(w, h, 1), Background(w, h, 2), Background(w, h, 3)];
        var frames = new List<byte[]>();
        for (int f = 0; f < n; f++)
        {
            frames.Add((byte[])slides[f * slides.Length / n].Clone());
        }

        return new AnimSequence("slides", w, h, frames, 250, HasAlpha: false);
    }
}
