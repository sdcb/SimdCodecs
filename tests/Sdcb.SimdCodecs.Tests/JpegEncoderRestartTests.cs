using ImageMagick;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="JpegEncodeOptions.RestartInterval"/> (T.81 B.2.4.4, F.1.2.3, G.1.2): restart markers change the stream,
/// never the coefficients, so every decoder must give exactly the pixels it gives for the same encode without them.
/// Checked with our decoder, libjpeg-turbo (<see cref="TurboJpegOracle"/>) and Magick.NET, plus a parse of the stream
/// itself: the DRI value, and per scan ceil(MCUs / N) - 1 markers numbered RST0..RST7 in turn, with the scan's MCU
/// count derived from the frame header (interleaved: MCUs; one component: that component's blocks).
/// </summary>
public sealed class JpegEncoderRestartTests
{
    public enum Sampling { S444, S420, Gray }

    public enum Mode { Baseline, Optimized, Progressive }

    // Neither size is a multiple of the 8 or 16 pixel MCU, in either axis.
    private static readonly (int W, int H)[] Sizes = [(67, 45), (21, 130)];

    /// <summary>N tags: 1, 7, one MCU row of the frame's interleaved MCUs, and more than the whole image.</summary>
    public static IEnumerable<object[]> Cases()
    {
        foreach (var (w, h) in Sizes)
            foreach (var sampling in Enum.GetValues<Sampling>())
                foreach (var mode in Enum.GetValues<Mode>())
                    foreach (var n in new[] { "1", "7", "row", "beyond" })
                        yield return [w, h, sampling, mode, n];
    }

    private static int McuSize(Sampling s) => s == Sampling.S420 ? 16 : 8;

    private static int ResolveInterval(string tag, int w, int h, Sampling s)
    {
        var mcu = McuSize(s);
        var perRow = (w + mcu - 1) / mcu;
        var total = perRow * ((h + mcu - 1) / mcu);
        return tag switch { "1" => 1, "7" => 7, "row" => perRow, _ => total * 64 + 1 }; // beyond even the block count
    }

    private static (byte[] Pixels, int Channels, JpegEncodeOptions Options) Setup(int w, int h, Sampling sampling, Mode mode)
    {
        var channels = sampling == Sampling.Gray ? 1 : 3;
        var options = new JpegEncodeOptions
        {
            Quality = 85,
            Subsampling = sampling == Sampling.S420 ? JpegSubsampling.Chroma420 : JpegSubsampling.Chroma444,
            OptimizeHuffman = mode == Mode.Optimized,
            Progressive = mode == Mode.Progressive,
        };
        var rgb = SyntheticPhoto.Rgb(w, h);
        return (channels == 1 ? Luma(rgb) : rgb, channels, options);
    }

    private static byte[] Luma(byte[] rgb)
    {
        var g = new byte[rgb.Length / 3];
        for (var i = 0; i < g.Length; i++) g[i] = (byte)((rgb[3 * i] * 77 + rgb[3 * i + 1] * 150 + rgb[3 * i + 2] * 29 + 128) >> 8);
        return g;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RestartMarkersLeaveOurPixelsUnchangedAndAreWellFormed(int w, int h, Sampling sampling, Mode mode, string n)
    {
        var interval = ResolveInterval(n, w, h, sampling);
        var (pixels, channels, options) = Setup(w, h, sampling, mode);
        var plain = JpegEncoder.Encode(pixels, w, h, channels, options);
        var restarted = JpegEncoder.Encode(pixels, w, h, channels, options with { RestartInterval = interval });
        var label = $"{w}x{h} {sampling} {mode} N={interval}";

        JpegDecoder.Decode(restarted).Pixels.ShouldBe(JpegDecoder.Decode(plain).Pixels, label);

        var stream = Parse(restarted);
        stream.RestartInterval.ShouldBe(interval, label);
        stream.Progressive.ShouldBe(mode == Mode.Progressive, label);
        stream.Scans.Count.ShouldBe(Parse(plain).Scans.Count, label);
        foreach (var scan in stream.Scans)
        {
            var expected = (scan.Mcus + interval - 1) / interval - 1;
            scan.Restarts.Count.ShouldBe(expected, $"{label}: scan of {scan.Mcus} MCUs");
            for (var i = 0; i < scan.Restarts.Count; i++) scan.Restarts[i].ShouldBe(i % 8, $"{label}: marker {i}");
        }

        // A one-MCU interval in a multi-MCU scan must actually produce markers, so this test cannot pass vacuously.
        if (n == "1") stream.Scans.Sum(s => s.Restarts.Count).ShouldBeGreaterThan(0, label);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void LibjpegTurboDecodesTheSamePixelsWithAndWithoutRestarts(int w, int h, Sampling sampling, Mode mode, string n)
    {
        TurboJpegOracle.RequireOrSkip();
        var interval = ResolveInterval(n, w, h, sampling);
        var (pixels, channels, options) = Setup(w, h, sampling, mode);
        var plain = TurboJpegOracle.Decode(JpegEncoder.Encode(pixels, w, h, channels, options), channels == 1);
        var restarted = TurboJpegOracle.Decode(JpegEncoder.Encode(pixels, w, h, channels, options with { RestartInterval = interval }), channels == 1);
        (restarted.Width, restarted.Height).ShouldBe((w, h));
        restarted.Pixels.ShouldBe(plain.Pixels, $"{w}x{h} {sampling} {mode} N={interval}");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MagickDecodesTheSamePixelsWithAndWithoutRestarts(int w, int h, Sampling sampling, Mode mode, string n)
    {
        var interval = ResolveInterval(n, w, h, sampling);
        var (pixels, channels, options) = Setup(w, h, sampling, mode);
        var plain = MagickRgb(JpegEncoder.Encode(pixels, w, h, channels, options));
        var restarted = MagickRgb(JpegEncoder.Encode(pixels, w, h, channels, options with { RestartInterval = interval }));
        restarted.ShouldBe(plain, $"{w}x{h} {sampling} {mode} N={interval}");
    }

    [Theory]
    [InlineData(Mode.Baseline)]
    [InlineData(Mode.Optimized)]
    [InlineData(Mode.Progressive)]
    public void ZeroIntervalWritesTheSameBytesAsNoOption(Mode mode)
    {
        foreach (var sampling in Enum.GetValues<Sampling>())
        {
            var (pixels, channels, options) = Setup(67, 45, sampling, mode);
            var without = JpegEncoder.Encode(pixels, 67, 45, channels, options);
            JpegEncoder.Encode(pixels, 67, 45, channels, options with { RestartInterval = 0 }).ShouldBe(without, sampling.ToString());
            Parse(without).RestartInterval.ShouldBeNull(sampling.ToString());
        }
    }

    [Fact]
    public void MaximumIntervalIsWritten()
    {
        var (pixels, channels, options) = Setup(21, 13, Sampling.S444, Mode.Baseline);
        Parse(JpegEncoder.Encode(pixels, 21, 13, channels, options with { RestartInterval = 65535 })).RestartInterval.ShouldBe(65535);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MinValue)]
    public void IntervalOutOfRangeThrows(int interval)
    {
        var px = new byte[8 * 8 * 3];
        Should.Throw<ArgumentOutOfRangeException>(() => JpegEncoder.Encode(px, 8, 8, 3, new JpegEncodeOptions { RestartInterval = interval }));
    }

    // ------------------------------------------------------------ helpers

    private static byte[] MagickRgb(byte[] jpeg)
    {
        using var m = new MagickImage(jpeg);
        return m.ToByteArray(MagickFormat.Rgb);
    }

    private sealed record ScanInfo(int Mcus, List<int> Restarts);

    private sealed record StreamInfo(int? RestartInterval, bool Progressive, List<ScanInfo> Scans);

    /// <summary>
    /// Walks the marker segments (T.81 B.1.1) and each scan's entropy-coded data: 0xFF 0x00 is a stuffed byte,
    /// 0xFF 0xD0..0xD7 a restart marker (its index recorded), any other marker ends the scan.
    /// </summary>
    private static StreamInfo Parse(byte[] s)
    {
        (s[0], s[1]).ShouldBe(((byte)0xFF, (byte)0xD8));
        int? dri = null;
        var progressive = false;
        int width = 0, height = 0, hMax = 1, vMax = 1;
        var comps = new Dictionary<int, (int H, int V)>();
        var scans = new List<ScanInfo>();
        var p = 2;
        while (true)
        {
            s[p].ShouldBe((byte)0xFF, $"marker expected at {p}");
            var marker = s[p + 1];
            p += 2;
            if (marker == 0xD9) break;
            var length = (s[p] << 8) | s[p + 1];
            var body = s.AsSpan(p + 2, length - 2);
            switch (marker)
            {
                case 0xDD:
                    length.ShouldBe(4);
                    dri = (body[0] << 8) | body[1];
                    break;
                case 0xC0 or 0xC2:
                    progressive = marker == 0xC2;
                    height = (body[1] << 8) | body[2];
                    width = (body[3] << 8) | body[4];
                    for (var i = 0; i < body[5]; i++)
                    {
                        var (hf, vf) = (body[7 + 3 * i] >> 4, body[7 + 3 * i] & 15);
                        comps[body[6 + 3 * i]] = (hf, vf);
                        hMax = Math.Max(hMax, hf);
                        vMax = Math.Max(vMax, vf);
                    }

                    break;
            }

            p += length;
            if (marker != 0xDA) continue;

            // T.81 A.2: an interleaved scan counts MCUs of the frame; a one-component scan counts that component's blocks.
            int mcus;
            if (body[0] > 1)
            {
                mcus = ((width + 8 * hMax - 1) / (8 * hMax)) * ((height + 8 * vMax - 1) / (8 * vMax));
            }
            else
            {
                var (hf, vf) = comps[body[1]];
                var cw = (width * hf + hMax - 1) / hMax;
                var ch = (height * vf + vMax - 1) / vMax;
                mcus = ((cw + 7) / 8) * ((ch + 7) / 8);
            }

            var restarts = new List<int>();
            while (true)
            {
                if (s[p] != 0xFF) { p++; continue; }
                var next = s[p + 1];
                if (next == 0x00) { p += 2; continue; }
                if (next is >= 0xD0 and <= 0xD7) { restarts.Add(next - 0xD0); p += 2; continue; }
                break; // the next marker segment (fill bytes are not written by our encoder)
            }

            scans.Add(new ScanInfo(mcus, restarts));
        }

        p.ShouldBe(s.Length, "EOI is the last thing in the stream");
        return new StreamInfo(dri, progressive, scans);
    }
}
