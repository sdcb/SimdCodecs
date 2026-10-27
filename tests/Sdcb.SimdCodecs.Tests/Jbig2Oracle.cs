using System.Diagnostics;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Runs <b>jbig2dec</b> (Artifex's reference JBIG2 decoder) as an external
/// oracle: hand it a stream we produced and compare its raster against ours.
/// <para>
/// <b>Binary only.</b> jbig2dec is AGPL, so its source can never be a port
/// source for this MIT repo — but running a program is not linking to it,
/// and its output is just pixels. That distinction is the one the roadmap's
/// licence matrix is built around, and it is the reason this file shells out
/// instead of vendoring anything.
/// </para>
/// <para>
/// Resolution order: <c>JBIG2DEC</c> if set, <c>jbig2dec</c> on PATH (Linux CI),
/// a native build fetched into <c>Oracle/jbig2/dist/bin</c> by
/// <c>fetch-windows.sh</c> (upstream ships no Windows binary, so this is MSYS2's),
/// then through WSL (<c>wsl.exe -- jbig2dec</c>), then unavailable. Callers gate on
/// <see cref="IsAvailable"/> and skip; the tests use <c>Assert.SkipUnless</c> so
/// an absent oracle shows up as a reported skip rather than a silent pass.
/// </para>
/// </summary>
internal static class Jbig2Oracle
{
    private static readonly Lazy<(string? Exe, bool ViaWsl, string Reason)> Resolved = new(Resolve);

    /// <summary>True when jbig2dec can actually be invoked.</summary>
    public static bool IsAvailable => Resolved.Value.Exe is not null;

    /// <summary>Why the oracle is unavailable, for the skip message.</summary>
    public static string UnavailableReason => Resolved.Value.Reason;

    /// <summary>
    /// Gate for every test in this harness: skips when jbig2dec is missing, or
    /// fails when <c>REQUIRE_ORACLES=1</c> says it should have been there.
    /// </summary>
    public static void RequireOrSkip() => OracleGate.RequireOrSkip(IsAvailable, "jbig2dec", UnavailableReason);

    /// <summary>A decoded bilevel raster, 1 = black — the same polarity PBM and T.88 both use.</summary>
    public sealed record Raster(int Width, int Height, byte[] Bits);

    private static (string? Exe, bool ViaWsl, string Reason) Resolve()
    {
        // An explicit path wins — for a custom build, and for exercising the
        // REQUIRE_ORACLES failure path by pointing it somewhere bogus.
        var configured = Environment.GetEnvironmentVariable("JBIG2DEC");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return TryVersion(configured, [], out _)
                ? (configured, false, "")
                : (null, false, $"JBIG2DEC is set to '{configured}', which does not run as jbig2dec.");
        }

        if (TryVersion("jbig2dec", [], out _))
            return ("jbig2dec", false, "");

        // A fetched native build (Oracle/jbig2/fetch-windows.sh), found by walking up
        // from the test output directory — the same lookup OpenJpegOracle uses.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            foreach (var relative in new[]
                     {
                         Path.Combine("Oracle", "jbig2", "dist", "bin"),
                         Path.Combine("tests", "Sdcb.SimdCodecs.Tests", "Oracle", "jbig2", "dist", "bin"),
                     })
            {
                foreach (var name in new[] { "jbig2dec.exe", "jbig2dec" })
                {
                    var candidate = Path.Combine(dir.FullName, relative, name);
                    if (File.Exists(candidate) && TryVersion(candidate, [], out _))
                        return (candidate, false, "");
                }
            }
        }

        if (OperatingSystem.IsWindows() && TryVersion("wsl.exe", ["--", "jbig2dec"], out _))
            return ("wsl.exe", true, "");

        return (null, false,
            "jbig2dec not found. Install it natively (apt-get install jbig2dec) or, on Windows, " +
            "fetch a native build with `bash tests/Sdcb.SimdCodecs.Tests/Oracle/jbig2/fetch-windows.sh` " +
            "(or install it into the default WSL distro: wsl -- sudo apt-get install -y jbig2dec).");
    }

    private static bool TryVersion(string exe, string[] prefix, out string output)
    {
        output = "";
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in prefix) info.ArgumentList.Add(a);
            info.ArgumentList.Add("--version");

            using var process = Process.Start(info);
            if (process is null) return false;

            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15_000)) { try { process.Kill(true); } catch { /* best effort */ } return false; }

            // The exit code is checked FIRST, and it is load-bearing rather than
            // belt-and-braces. A shell that cannot find the program says so by
            // NAME — `/bin/bash: line 1: jbig2dec: command not found`, exit 127 —
            // so a substring test on the combined output matches the very
            // message that proves the tool is absent. That made `wsl.exe --
            // jbig2dec` resolve as available on a box with no jbig2dec in WSL,
            // turning 75 tests from honest skips into red failures. Loud, so it
            // was never wrong output, but it is exactly backwards from the
            // OracleGate contract.
            return process.ExitCode == 0
                && output.Contains("jbig2dec", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Missing executable, WSL not installed, no default distro — all just
            // mean "no oracle here", never a test failure.
            return false;
        }
    }

    /// <summary>
    /// Decodes <paramref name="stream"/> with jbig2dec and returns its raster.
    /// <paramref name="embedded"/> selects <c>-e</c> (a PDF-style stream with no
    /// file header) over a standalone <c>.jb2</c> file.
    /// </summary>
    public static Raster Decode(byte[] stream, bool embedded = false)
    {
        var (exe, viaWsl, _) = Resolved.Value;
        if (exe is null) throw new InvalidOperationException("jbig2dec oracle is not available.");

        // Temp files live under the test binaries rather than %TEMP%: the temp
        // path can carry an 8.3 short name (SEBAST~1), which /mnt/c/... cannot
        // resolve, and the output directory is always a full long path.
        var scratch = Path.Combine(AppContext.BaseDirectory, "jbig2-oracle-tmp");
        Directory.CreateDirectory(scratch);

        var stem = Path.Combine(scratch, $"o{Guid.NewGuid():N}");
        var input = stem + ".jb2";
        var output = stem + ".pbm";

        try
        {
            File.WriteAllBytes(input, stream);

            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            if (viaWsl)
            {
                info.ArgumentList.Add("--");
                info.ArgumentList.Add("jbig2dec");
            }

            info.ArgumentList.Add("-q");
            if (embedded) info.ArgumentList.Add("-e");
            info.ArgumentList.Add("-t");
            info.ArgumentList.Add("pbm");
            info.ArgumentList.Add("-o");
            info.ArgumentList.Add(viaWsl ? ToWslPath(output) : output);
            info.ArgumentList.Add(viaWsl ? ToWslPath(input) : input);

            using var process = Process.Start(info)
                ?? throw new InvalidOperationException("Could not start jbig2dec.");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(true); } catch { /* best effort */ }
                throw new InvalidOperationException("jbig2dec timed out.");
            }

            if (!File.Exists(output))
                throw new InvalidOperationException(
                    $"jbig2dec produced no output (exit {process.ExitCode}).\n{stdout}\n{stderr}");

            return ParsePbm(File.ReadAllBytes(output));
        }
        finally
        {
            TryDelete(input);
            TryDelete(output);
        }
    }

    /// <summary><c>C:\dir\file</c> → <c>/mnt/c/dir/file</c>. Hand-rolled because
    /// <c>wslpath</c> through interop mangles backslashes.</summary>
    private static string ToWslPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length < 3 || full[1] != ':')
            throw new ArgumentException($"Expected a drive-letter path, got '{full}'.", nameof(path));

        return $"/mnt/{char.ToLowerInvariant(full[0])}{full[2..].Replace('\\', '/')}";
    }

    /// <summary>
    /// Parses a binary PBM (P4). Bits are packed MSB-first, rows padded to a byte,
    /// and a set bit means black — so the unpacked result is already in T.88
    /// polarity and needs no inversion.
    /// </summary>
    private static Raster ParsePbm(byte[] pbm)
    {
        var position = 0;

        if (ReadToken(pbm, ref position) != "P4")
            throw new InvalidDataException("jbig2dec did not produce a binary PBM (P4).");

        var width = int.Parse(ReadToken(pbm, ref position));
        var height = int.Parse(ReadToken(pbm, ref position));

        // Exactly one whitespace byte separates the header from the raster.
        position++;

        var stride = (width + 7) / 8;
        if (pbm.Length - position < stride * height)
            throw new InvalidDataException("Truncated PBM raster from jbig2dec.");

        var bits = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = position + y * stride;
            for (var x = 0; x < width; x++)
                bits[y * width + x] = (byte)((pbm[row + (x >> 3)] >> (7 - (x & 7))) & 1);
        }

        return new Raster(width, height, bits);
    }

    private static string ReadToken(byte[] data, ref int position)
    {
        while (position < data.Length)
        {
            if (data[position] == '#')
            {
                while (position < data.Length && data[position] != '\n') position++;
            }
            else if (char.IsWhiteSpace((char)data[position]))
            {
                position++;
            }
            else
            {
                break;
            }
        }

        var start = position;
        while (position < data.Length && !char.IsWhiteSpace((char)data[position]) && data[position] != '#')
            position++;

        return System.Text.Encoding.ASCII.GetString(data, start, position - start);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* the scratch directory is disposable */ }
    }
}
