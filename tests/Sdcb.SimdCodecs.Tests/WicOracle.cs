using System.Buffers.Binary;
using System.Diagnostics;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The Windows Imaging Component (WIC), Windows' own BMP and ICO decoder, as a black-box oracle. It is reached
/// through WPF's documented <c>System.Windows.Media.Imaging.BitmapDecoder</c> (which wraps WIC) in a Windows
/// PowerShell script, so no COM interface is declared by hand: every frame is converted to non-premultiplied
/// BGRA with colour management off and written out raw. One PowerShell process decodes a whole batch.
/// <para>
/// WIC exists only on Windows, so elsewhere the tests skip as not applicable — not as a missing oracle,
/// which <c>REQUIRE_ORACLES</c> would turn into a failure on Linux CI. On Windows, Windows PowerShell is part
/// of the system; its absence goes through <see cref="OracleGate"/>.
/// </para>
/// </summary>
internal static class WicOracle
{
    /// <summary>What WIC made of one file: its frames as RGBA, or the error it raised.</summary>
    internal sealed record Result(string? Error, IReadOnlyList<(int Width, int Height, byte[] Rgba)> Frames);

    private static string PowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private const string Script = """
        param([string]$dir)
        $ErrorActionPreference = 'Stop'
        Add-Type -AssemblyName PresentationCore
        $options = [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat -bor [Windows.Media.Imaging.BitmapCreateOptions]::IgnoreColorProfile
        foreach ($f in Get-ChildItem -LiteralPath $dir -Filter *.in) {
          $base = $f.FullName.Substring(0, $f.FullName.Length - 3)
          $fs = $null
          try {
            $fs = [IO.File]::OpenRead($f.FullName)
            $decoder = [Windows.Media.Imaging.BitmapDecoder]::Create($fs, $options, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
            $i = 0
            foreach ($frame in $decoder.Frames) {
              $bgra = New-Object Windows.Media.Imaging.FormatConvertedBitmap($frame, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
              $w = $bgra.PixelWidth; $h = $bgra.PixelHeight
              $out = New-Object byte[] (8 + $w * $h * 4)
              [BitConverter]::GetBytes([int]$w).CopyTo($out, 0)
              [BitConverter]::GetBytes([int]$h).CopyTo($out, 4)
              $pixels = New-Object byte[] ($w * $h * 4)
              $bgra.CopyPixels($pixels, $w * 4, 0)
              $pixels.CopyTo($out, 8)
              [IO.File]::WriteAllBytes("$base.$i.raw", $out)
              $i++
            }
          } catch {
            [IO.File]::WriteAllText("$base.err", $_.Exception.ToString())
          } finally {
            if ($fs -ne $null) { $fs.Close() }
          }
        }
        """;

    /// <summary>Skips off Windows; on Windows, requires Windows PowerShell (through <see cref="OracleGate"/>).</summary>
    public static void RequireOrSkip()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("WIC is a Windows component: there is nothing to compare against on this platform.");
        OracleGate.RequireOrSkip(File.Exists(PowerShell), "WIC (via Windows PowerShell and WPF)",
            $"Windows PowerShell was not found at {PowerShell}.");
    }

    /// <summary>Decodes every file with WIC in one PowerShell run.</summary>
    public static Dictionary<string, Result> Decode(IReadOnlyDictionary<string, byte[]> files)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "simdcodecs-wic-" + Guid.NewGuid().ToString("N")));
        try
        {
            var names = files.Keys.ToList();
            for (var i = 0; i < names.Count; i++) File.WriteAllBytes(Path.Combine(dir.FullName, $"{i:D5}.in"), files[names[i]]);
            var script = Path.Combine(dir.FullName, "decode.ps1");
            File.WriteAllText(script, Script);

            var start = new ProcessStartInfo(PowerShell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, dir.FullName })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                process.Kill();
                throw new TimeoutException("The WIC decode script did not finish in five minutes.");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"The WIC decode script failed ({process.ExitCode}): {stderr.Result}");

            var results = new Dictionary<string, Result>();
            for (var i = 0; i < names.Count; i++)
            {
                var error = Path.Combine(dir.FullName, $"{i:D5}.err");
                if (File.Exists(error))
                {
                    results[names[i]] = new Result(File.ReadAllText(error), []);
                    continue;
                }
                var frames = new List<(int, int, byte[])>();
                for (var f = 0; File.Exists(Path.Combine(dir.FullName, $"{i:D5}.{f}.raw")); f++)
                {
                    var raw = File.ReadAllBytes(Path.Combine(dir.FullName, $"{i:D5}.{f}.raw"));
                    int width = BinaryPrimitives.ReadInt32LittleEndian(raw), height = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));
                    var rgba = raw[8..];
                    for (var p = 0; p < rgba.Length; p += 4) (rgba[p], rgba[p + 2]) = (rgba[p + 2], rgba[p]);
                    frames.Add((width, height, rgba));
                }
                results[names[i]] = new Result(null, frames);
            }
            return results;
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
