using System.Diagnostics;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// ffmpeg as a black-box APNG oracle (see <see cref="HeifOracle"/> for how it is found and pinned): it writes APNG files — foreign
/// bytes, with the dispose and blend operations and sub-rectangles its own encoder picks — and decodes them to composited frames.
/// <para>
/// CI has no ffmpeg, so the live comparison skips there even under <c>REQUIRE_ORACLES</c> (which would otherwise fail every job
/// that lacks a tool); <c>Fixtures/png/apng/*.apng</c> and their <c>apng-golden.tsv</c> carry the same comparison to CI. Set
/// <c>APNG_REQUIRE_FFMPEG=1</c> to make a missing ffmpeg a failure, as on a machine that is meant to have it.
/// </para>
/// </summary>
internal static class ApngOracle
{
    /// <summary>Returns the pinned ffmpeg; skips (or fails under <c>APNG_REQUIRE_FFMPEG</c>) when it is missing.</summary>
    public static string Require()
    {
        if (HeifOracle.Ffmpeg is null && Environment.GetEnvironmentVariable("APNG_REQUIRE_FFMPEG") is not ("1" or "true"))
            Assert.Skip($"ffmpeg {HeifOracle.PinnedVersion} unavailable — skipping the live APNG comparison (the committed goldens still run). {HeifOracle.FfmpegReason}");
        OracleGate.RequireOrSkip(HeifOracle.Ffmpeg is not null, $"ffmpeg {HeifOracle.PinnedVersion}", HeifOracle.FfmpegReason);
        return HeifOracle.Ffmpeg!;
    }

    /// <summary>
    /// ffmpeg's frames of an APNG, each the composited canvas in 8-bit RGBA (<paramref name="wide"/> false) or 16-bit RGBA, little-endian
    /// (true), concatenated. Every frame is kept (no frame-rate conversion).
    /// </summary>
    public static byte[] DecodeFrames(string ffmpeg, byte[] apng, bool wide)
    {
        var input = TempPath(".apng");
        var output = TempPath(".raw");
        try
        {
            File.WriteAllBytes(input, apng);
            Run(ffmpeg, "-v", "error", "-y", "-i", input, "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", wide ? "rgba64le" : "rgba", output);
            return File.ReadAllBytes(output);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    /// <summary>
    /// ffmpeg's APNG encoding of raw frames (<paramref name="inputPixelFormat"/>, tightly packed, <paramref name="width"/> ×
    /// <paramref name="height"/>, <paramref name="rate"/> frames a second) as <paramref name="outputPixelFormat"/>.
    /// </summary>
    public static byte[] Encode(string ffmpeg, byte[] raw, int width, int height, int rate, int plays, string inputPixelFormat, string outputPixelFormat)
    {
        var input = TempPath(".raw");
        var output = TempPath(".apng");
        try
        {
            File.WriteAllBytes(input, raw);
            Run(ffmpeg, "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", inputPixelFormat, "-s", $"{width}x{height}", "-framerate", rate.ToString(),
                "-i", input, "-plays", plays.ToString(), "-pix_fmt", outputPixelFormat, "-f", "apng", output);
            return File.ReadAllBytes(output);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    private static string TempPath(string extension) => Path.Combine(Path.GetTempPath(), "sdcb-apng-" + Guid.NewGuid().ToString("N") + extension);

    private static void Run(string ffmpeg, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed: {error.Trim()}");
    }
}
