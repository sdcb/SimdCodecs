using System.Diagnostics;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// ExifTool as a black-box writer of metadata: it splits an XMP packet too large for one APP1 segment into a standard
/// packet and Extended XMP (XMP Specification Part 3, §1.1.3.1), which gives the JPEG XMP reader foreign bytes to join.
/// <para>
/// <b>Binary only.</b> ExifTool is Perl, licensed like Perl (Artistic / GPL); its source is never read. CI installs the
/// apt package <c>libimage-exiftool-perl</c>; locally put <c>exiftool</c> (Windows: <c>exiftool.exe</c>) on
/// <c>PATH</c> or point <c>EXIFTOOL</c> at it. Gated by <see cref="OracleGate"/>.
/// </para>
/// </summary>
internal static class ExifToolOracle
{
    private const string HowToGet =
        "Install ExifTool (apt-get install libimage-exiftool-perl; Windows: exiftool.exe from exiftool.org) on PATH, or set EXIFTOOL.";

    private static readonly Lazy<(string? Path, string Reason)> Tool = new(Resolve);

    /// <summary>Returns the resolved <c>exiftool</c>; skips, or fails under <c>REQUIRE_ORACLES</c>, when it is missing.</summary>
    public static string Require()
    {
        OracleGate.RequireOrSkip(Tool.Value.Path is not null, "exiftool", Tool.Value.Reason);
        return Tool.Value.Path!;
    }

    /// <summary>Runs ExifTool with <paramref name="arguments"/> and fails the test unless it exits with 0.</summary>
    public static void Run(string exe, params string[] arguments)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(true); } catch { /* best effort */ }
            Assert.Fail("exiftool did not finish in 60 s.");
        }

        Assert.True(process.ExitCode == 0, $"exiftool exited with {process.ExitCode}: {stderr}{stdout.Result}");
    }

    private static (string? Path, string Reason) Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("EXIFTOOL");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.GetFullPath(configured);
            return File.Exists(path) ? (path, "") : (null, $"EXIFTOOL is set to '{configured}', which does not exist. {HowToGet}");
        }

        var executable = OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool";
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(entry, executable);
            if (File.Exists(candidate))
                return (candidate, "");
        }

        return (null, $"exiftool was not found via EXIFTOOL or PATH. {HowToGet}");
    }
}
