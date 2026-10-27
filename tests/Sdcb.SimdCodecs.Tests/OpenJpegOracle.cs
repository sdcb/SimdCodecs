using System.Diagnostics;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Runs <b>OpenJPEG</b>'s reference tools (<c>opj_decompress</c> /
/// <c>opj_compress</c>) as an external oracle for <c>Sdcb.SimdCodecs.Jpeg2000</c>.
/// <para>
/// <b>Binary only.</b> OpenJPEG is BSD-2 — permissive, but third-party, and the
/// clean-room rules keep every file here first-party. So its C is never a
/// port source (the decoder is clean-room from ITU-T T.800), while running the
/// program and reading its pixels is fine. The same line already drawn around
/// jbig2dec, jbig2enc and libjpeg's <c>jidctred.c</c>.
/// </para>
/// <para>
/// <b>Most JPEG 2000 tests do not need this class.</b> A reversible (5/3)
/// codestream decodes to its source raster exactly, so the committed
/// <c>Fixtures/jpeg2000/*.pgm</c> is its own expected output and those tests
/// assert byte equality with no subprocess and no tolerance. What the oracle is
/// for is the cases committed bytes cannot answer: the lossy 9/7 path, and
/// spot-checking that a fixture still means what it meant when it was made.
/// </para>
/// <para>
/// Get the binaries with
/// <c>bash tests/Sdcb.SimdCodecs.Tests/Oracle/jpeg2000/fetch.sh</c>. They are
/// git-ignored, pinned by version and SHA-256, and downloaded rather than built
/// — see that directory's README for why.
/// </para>
/// </summary>
internal static class OpenJpegOracle
{
    /// <summary>The pinned release. Asserted at run time, not just at download time.</summary>
    private const string PinnedVersion = "2.5.4";

    private const string HowToGet =
        "Fetch it with `bash tests/Sdcb.SimdCodecs.Tests/Oracle/jpeg2000/fetch.sh` (needs curl and unzip/tar).";

    private static readonly Lazy<Tools> Resolved = new(Resolve);

    private sealed record Tools(string? Decompress, string? Compress, string? LibraryPath, string Reason);

    /// <summary>True when both reference tools can actually be invoked.</summary>
    public static bool IsAvailable => Resolved.Value.Decompress is not null && Resolved.Value.Compress is not null;

    /// <summary>Why the oracle is unavailable, for the skip message.</summary>
    public static string UnavailableReason => Resolved.Value.Reason;

    /// <summary>
    /// Gate for every test in this harness: skips when OpenJPEG is missing, or
    /// fails when <c>REQUIRE_ORACLES=1</c> says it should have been there.
    /// </summary>
    public static void RequireOrSkip() => OracleGate.RequireOrSkip(IsAvailable, "OpenJPEG", UnavailableReason);

    /// <summary>
    /// Decodes a JPEG 2000 codestream with <c>opj_decompress</c> and returns the
    /// PNM it wrote. <paramref name="extension"/> selects the writer —
    /// <c>.pgm</c> for one component, <c>.ppm</c> for three.
    /// </summary>
    public static Pnm.Image Decode(byte[] codestream, string extension = ".pgm", params string[] extraArgs)
    {
        var tools = Resolved.Value;
        if (tools.Decompress is null) throw new InvalidOperationException("OpenJPEG oracle is not available.");

        // Scratch lives under the test binaries rather than %TEMP%, for the
        // reason Jbig2Oracle records: the temp path can carry an 8.3 short name
        // (SEBAST~1) and the output directory is always a full long path.
        var scratch = Path.Combine(AppContext.BaseDirectory, "jpeg2000-oracle-tmp");
        Directory.CreateDirectory(scratch);
        var stem = Path.Combine(scratch, Guid.NewGuid().ToString("n"));
        var input = stem + ".j2k";
        var output = stem + extension;

        try
        {
            File.WriteAllBytes(input, codestream);

            var args = new List<string> { "-i", input, "-o", output };
            args.AddRange(extraArgs);
            var (exitCode, log) = Run(tools.Decompress, args, tools.LibraryPath);

            if (!File.Exists(output))
                throw new InvalidOperationException(
                    $"opj_decompress produced no output (exit {exitCode}).{Environment.NewLine}{log}");

            return Pnm.Read(output);
        }
        finally
        {
            TryDelete(input);
            TryDelete(output);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort — scratch lives under the test output directory */ }
    }

    private static Tools Resolve()
    {
        // An explicit prefix wins — for a local build, and for exercising the
        // REQUIRE_ORACLES failure path by pointing it somewhere bogus.
        var configured = Environment.GetEnvironmentVariable("OPJ_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var found = FromPrefix(configured);
            return found ?? new Tools(null, null, null,
                $"OPJ_HOME is set to '{configured}', which has no runnable bin/opj_decompress. {HowToGet}");
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            foreach (var relative in new[]
                     {
                         Path.Combine("Oracle", "jpeg2000", "dist"),
                         Path.Combine("tests", "Sdcb.SimdCodecs.Tests", "Oracle", "jpeg2000", "dist"),
                     })
            {
                var found = FromPrefix(Path.Combine(dir.FullName, relative));
                if (found is not null) return found;
            }
        }

        return new Tools(null, null, null, $"OpenJPEG {PinnedVersion} not found. {HowToGet}");
    }

    private static Tools? FromPrefix(string prefix)
    {
        var bin = Path.Combine(prefix, "bin");
        var decompress = Executable(bin, "opj_decompress");
        var compress = Executable(bin, "opj_compress");
        if (decompress is null || compress is null) return null;

        // The shipped Linux binaries carry no RUNPATH, so a SYSTEM libopenjp2
        // wins the search — on Ubuntu jammy that is 2.4.0 and the tool dies with
        // "undefined symbol: opj_decoder_set_strict_mode". Pointing
        // LD_LIBRARY_PATH at the sibling lib/ is what makes the pinned build
        // actually be the one that runs. fetch.sh sets the same variable; if you
        // change one, change both.
        var lib = Path.Combine(prefix, "lib");
        var libraryPath = Directory.Exists(lib) ? lib : null;

        return VerifyVersion(decompress, libraryPath)
            ? new Tools(decompress, compress, libraryPath, "")
            : null;
    }

    private static string? Executable(string dir, string name)
    {
        foreach (var candidate in new[] { Path.Combine(dir, name), Path.Combine(dir, name + ".exe") })
            if (File.Exists(candidate))
                return candidate;

        return null;
    }

    /// <summary>
    /// Confirms the tool runs <em>and</em> that it loaded the pinned library.
    /// <para>
    /// Two traps here, both met in practice. First, <c>-h</c> exits
    /// <b>non-zero</b> — it is the usage path — so gating on the exit code alone
    /// reports a working oracle as missing. Second, the banner names the library
    /// the process actually <em>loaded</em>, not the one it shipped beside, which
    /// is exactly what catches a system <c>libopenjp2</c> hijacking the pinned
    /// binary. Checking the banner covers both.
    /// </para>
    /// <para>
    /// And the converse trap, learned from <see cref="Jbig2Oracle"/>: never
    /// probe by searching the combined output for the tool's own name. A shell
    /// that cannot find the program answers by naming it — "command not found" —
    /// so a substring test matches the very message that proves it is absent.
    /// </para>
    /// </summary>
    private static bool VerifyVersion(string exe, string? libraryPath)
    {
        try
        {
            var (_, log) = Run(exe, ["-h"], libraryPath);
            return log.Contains($"openjp2 library v{PinnedVersion}", StringComparison.Ordinal);
        }
        catch
        {
            // Missing executable, wrong architecture, blocked by policy — all
            // just mean "no oracle here", never a test failure.
            return false;
        }
    }

    private static (int ExitCode, string Log) Run(string exe, IEnumerable<string> args, string? libraryPath)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        if (libraryPath is not null) info.Environment["LD_LIBRARY_PATH"] = libraryPath;

        using var process = Process.Start(info)
                            ?? throw new InvalidOperationException($"could not start {exe}");

        // Read both pipes before waiting: opj_decompress is chatty on stdout and
        // a full pipe buffer would deadlock the wait.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(true); } catch { /* best effort */ }
            throw new InvalidOperationException($"{Path.GetFileName(exe)} timed out.");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
