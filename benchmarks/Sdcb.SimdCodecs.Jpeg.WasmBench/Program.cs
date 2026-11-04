using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Jpeg.WasmBench;

// browser-wasm: no arguments (Node runs main.mjs). Desktop: `prepare <photo.jpg> <dir>` writes the
// 4:4:4 and progressive variants run.sh embeds; with no arguments it times the embedded images.
if (args.Length == 3 && args[0] == "prepare")
{
    var source = File.ReadAllBytes(args[1]);
    var img = JpegDecoder.Decode(source);
    var (w, h) = (img.Width, img.Height);
    var rgb = new byte[w * h * 3];
    JpegDecoder.DecodeTo(source, rgb, w * 3, JpegPixelFormat.Rgb24);
    File.WriteAllBytes(Path.Combine(args[2], "photo-444.jpg"),
        JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = 90, Subsampling = JpegSubsampling.Chroma444 }));
    File.WriteAllBytes(Path.Combine(args[2], "photo-progressive.jpg"),
        JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = 90, Subsampling = JpegSubsampling.Chroma420, Progressive = true }));
    return 0;
}

#if WASM_PROBE
GC.KeepAlive(typeof(Probe).GetMethods()); // keep the probe methods from the trimmer
#endif

return Stages.Run(OperatingSystem.IsBrowser() ? "browser-wasm (Mono AOT)" : $"desktop ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription})");
