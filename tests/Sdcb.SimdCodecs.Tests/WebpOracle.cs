namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Resolves libwebp's reference tools — <c>dwebp</c> for still images and <c>anim_dump</c> for
/// animations — as black-box oracles for <c>Sdcb.SimdCodecs.Webp</c>.
/// <para>
/// <b>Binary only.</b> libwebp is BSD-3 (notice-retaining), so its source is never read; running
/// the program and comparing its pixels is fine, the same line drawn around jbig2dec and OpenJPEG.
/// </para>
/// <para>
/// The committed digests (<c>Fixtures/webp/*-golden.tsv</c>) run without either tool. The live
/// comparisons go through <see cref="OracleGate"/>, so with <c>REQUIRE_ORACLES=1</c> (CI) a missing
/// tool is a failure rather than a quiet fallback to the digests. CI gets both from the apt
/// <c>webp</c> package; locally, install the same package, put the libwebp release binaries on
/// <c>PATH</c>, build <c>anim_dump</c> with <c>Oracle/webp/build.sh</c>, or point <c>DWEBP</c> /
/// <c>ANIM_DUMP</c> at an executable.
/// </para>
/// </summary>
internal static class WebpOracle
{
    private const string HowToGet =
        "Install the `webp` package (apt-get install webp), put the libwebp release binaries on PATH, " +
        "or set DWEBP / ANIM_DUMP; anim_dump can also be built with " +
        "`bash tests/Sdcb.SimdCodecs.Tests/Oracle/webp/build.sh`.";

    private static readonly Lazy<(string? Path, string Reason)> DwebpTool = new(() => Resolve("DWEBP", "dwebp"));
    private static readonly Lazy<(string? Path, string Reason)> AnimDumpTool = new(() => Resolve("ANIM_DUMP", "anim_dump"));

    /// <summary>The resolved <c>dwebp</c>, or null.</summary>
    public static string? Dwebp => DwebpTool.Value.Path;

    /// <summary>The resolved <c>anim_dump</c>, or null.</summary>
    public static string? AnimDump => AnimDumpTool.Value.Path;

    /// <summary>Returns <c>dwebp</c>; skips, or fails under <c>REQUIRE_ORACLES</c>, when it is missing.</summary>
    public static string RequireDwebp()
    {
        OracleGate.RequireOrSkip(Dwebp is not null, "dwebp", DwebpTool.Value.Reason);
        return Dwebp!;
    }

    /// <summary>Returns <c>anim_dump</c>; skips, or fails under <c>REQUIRE_ORACLES</c>, when it is missing.</summary>
    public static string RequireAnimDump()
    {
        OracleGate.RequireOrSkip(AnimDump is not null, "anim_dump", AnimDumpTool.Value.Reason);
        return AnimDump!;
    }

    private static (string? Path, string Reason) Resolve(string variable, string tool)
    {
        string executable = OperatingSystem.IsWindows() ? tool + ".exe" : tool;

        // An explicit setting wins, and a bogus one exercises the REQUIRE_ORACLES failure path.
        string? configured = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string path = Path.GetFullPath(configured);
            return File.Exists(path)
                ? (path, "")
                : (null, $"{variable} is set to '{configured}', which does not exist. {HowToGet}");
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
        {
            foreach (string relative in new[]
                     {
                         Path.Combine("Oracle", "webp", "bin"),
                         Path.Combine("tests", "Sdcb.SimdCodecs.Tests", "Oracle", "webp", "bin"),
                     })
            {
                string candidate = Path.Combine(directory.FullName, relative, executable);
                if (File.Exists(candidate))
                {
                    return (candidate, "");
                }
            }
        }

        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(entry, executable);
            if (File.Exists(candidate))
            {
                return (candidate, "");
            }
        }

        return (null, $"{tool} was not found via {variable}, Oracle/webp/bin or PATH. {HowToGet}");
    }
}
