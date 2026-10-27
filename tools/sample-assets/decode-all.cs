#:project ../../src/Sdcb.SimdCodecs/Sdcb.SimdCodecs.csproj
#:property PublishAot=false
// Decodes every file in <in-dir> with the Sdcb.SimdCodecs facade and writes <out-dir>/<file>.<W>x<H>.rgba
// (8-bit RGBA; float images stretched from their min..max). make-showcase.py builds the README image
// from these dumps, so every pixel in it went through this library.
//   dotnet run decode-all.cs -- out decoded
using Sdcb.SimdCodecs.Abstractions;

string inDir = args[0], outDir = args[1];
Directory.CreateDirectory(outDir);
foreach (string path in Directory.GetFiles(inDir).Where(p => !p.EndsWith("SHA256SUMS")).Order())
{
    byte[] data = File.ReadAllBytes(path);
    string name = Path.GetFileName(path);
    if (!Sdcb.SimdCodecs.SimdCodecs.TryReadInfo(data, out ImageInfo info))
    {
        Console.WriteLine($"{name,-24} not recognised");
        continue;
    }

    byte[] rgba = new byte[checked(info.Width * info.Height * 4)];
    bool ok;
    if (info.SampleFormat == SampleFormat.Float32)
    {
        ok = Sdcb.SimdCodecs.SimdCodecs.TryDecode(data, out IDecodedImage? image);
        if (ok)
        {
            float[] f = image!.ToFloats();
            float min = f.Min(), max = f.Max();
            for (int i = 0; i < rgba.Length; i++)
                rgba[i] = (i & 3) == 3 ? (byte)255 : (byte)Math.Clamp((f[i] - min) / (max - min) * 255f + 0.5f, 0, 255);
        }
    }
    else ok = Sdcb.SimdCodecs.SimdCodecs.TryDecodeIntoRgba8(data, rgba);

    Console.WriteLine($"{name,-24} {info.Width}x{info.Height} {info.SampleFormat} {(ok ? "ok" : "FAILED")}");
    if (ok) File.WriteAllBytes(Path.Combine(outDir, $"{name}.{info.Width}x{info.Height}.rgba"), rgba);
}
