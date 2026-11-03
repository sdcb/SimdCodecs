using System.Globalization;
using System.Text.RegularExpressions;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Resolves the HEIF oracle: <c>ffmpeg</c>, whose HEVC decoder decodes a HEIC file's tiles to raw YUV.
/// <para>
/// <b>Binary only.</b> ffmpeg's and libde265's sources are on the AGENTS.md clean-room list, so they are never
/// read; running the program and comparing its output samples is fine — the same line drawn around libwebp's
/// tools. The pinned version (<see cref="PinnedVersion"/>) is checked before any sample is compared, because a
/// different build may legitimately decode differently at the edges of the specification.
/// </para>
/// <para>
/// Locally set <c>FFMPEG</c> to an executable, put <c>ffmpeg</c> on <see cref="System.Environment"/>'s PATH, or
/// use the build in <c>C:\_\3rd\ffmpeg-bin</c>. With <c>REQUIRE_ORACLES=1</c> (CI) a missing or mismatched
/// oracle fails instead of skipping.
/// </para>
/// </summary>
internal static class HeifOracle
{
    /// <summary>The ffmpeg release the byte-exact expectations were recorded against.</summary>
    public const string PinnedVersion = "8.1.2";

    private const string HowToGet =
        "Install ffmpeg " + PinnedVersion + " and put it on PATH, or set FFMPEG to its executable " +
        "(this machine: C:\\_\\3rd\\ffmpeg-bin\\ffmpeg.exe).";

    private static readonly Lazy<(string? Path, string Reason)> FfmpegTool = new(Resolve);

    /// <summary>The resolved <c>ffmpeg</c> at the pinned version, or null with the reason it was not usable.</summary>
    public static string? Ffmpeg => FfmpegTool.Value.Path;

    /// <summary>Why the oracle was unusable, for a skip message.</summary>
    public static string FfmpegReason => FfmpegTool.Value.Reason;

    /// <summary>Returns <c>ffmpeg</c>; skips, or fails under <c>REQUIRE_ORACLES</c>, when it is missing or not the pinned version.</summary>
    public static string RequireFfmpeg()
    {
        OracleGate.RequireOrSkip(Ffmpeg is not null, $"ffmpeg {PinnedVersion}", FfmpegTool.Value.Reason);
        return Ffmpeg!;
    }

    /// <summary>
    /// Decodes an image file with ffmpeg to raw YUV at the stream's native pixel format (no colour conversion),
    /// writing to a temporary file; returns the bytes. Throws <see cref="InvalidOperationException"/> when ffmpeg fails.
    /// </summary>
    public static byte[] DecodeToYuv(string ffmpegPath, string inputPath)
    {
        string output = Path.Combine(Path.GetTempPath(), "sdcb-heif-" + Guid.NewGuid().ToString("N") + ".yuv");
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (string argument in new[] { "-v", "error", "-y", "-i", inputPath, "-f", "rawvideo", output })
            {
                startInfo.ArgumentList.Add(argument);
            }
            using (var process = System.Diagnostics.Process.Start(startInfo)!)
            {
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"ffmpeg failed on {Path.GetFileName(inputPath)}: {error.Trim()}");
                }
            }
            return File.ReadAllBytes(output);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static (string? Path, string Reason) Resolve()
    {
        string executable = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        string? configured = System.Environment.GetEnvironmentVariable("FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string path = Path.GetFullPath(configured);
            return File.Exists(path) ? CheckVersion(path) : (null, $"FFMPEG is set to '{configured}', which does not exist. {HowToGet}");
        }

        foreach (string entry in (System.Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(entry, executable);
            if (File.Exists(candidate)) return CheckVersion(candidate);
        }

        foreach (string directory in new[] { @"C:\_\3rd\ffmpeg-bin", "/usr/bin", "/usr/local/bin" })
        {
            string candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return CheckVersion(candidate);
        }

        return (null, $"ffmpeg was not found via FFMPEG, PATH or the known install directories. {HowToGet}");
    }

    private static (string? Path, string Reason) CheckVersion(string path)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(path)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-version");
            using var process = System.Diagnostics.Process.Start(startInfo)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            var match = Regex.Match(output, @"ffmpeg version (\S+)");
            if (!match.Success) return (null, $"{path} did not report an ffmpeg version. {HowToGet}");
            string version = match.Groups[1].Value;
            // "8.1.2-full_build-..." — the pin is the numeric prefix.
            var numeric = Regex.Match(version, @"^\d+(\.\d+)*").Value;
            return numeric == PinnedVersion
                ? (path, "")
                : (null, $"ffmpeg at {path} is version {version}, but the byte-exact expectations are pinned to {PinnedVersion}. {HowToGet}");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            return (null, $"{path} could not be started: {e.Message}. {HowToGet}");
        }
    }
}
