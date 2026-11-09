<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Png</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
  <Namespace>System.IO.Compression</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Lossy PNG, the pngquant use case: PngWriteOptions.Quantize reduces an image to a palette of at most 256 colours
// (alpha included) and writes it indexed, typically a third to a quarter of the truecolour file. Any PNG decoder
// reads the result; nothing about the format changes.
async Task Main()
{
	using HttpClient http = new();
	PngImage photo = PngReader.Decode(await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/samples/v1/fuji.png"));
	int w = photo.Width, h = photo.Height;
	new { photo.Width, photo.Height, photo.ColorType, photo.BitDepth }.Dump("fuji.png");

	// 1. Lossless against quantized. Quality is 0-100 (about what pngquant's --quality of the same number reaches):
	//    MaxQuality picks the fewest colours that reach it, MaxColors caps the palette, Dither trades banding in
	//    smooth areas for fine noise. Indexed pixels are a byte or less, so SmallestSize deflate costs little time.
	PngQuantizeOptions quantize = new();
	var outputs = new (string Name, PngWriteOptions Options)[]
	{
		("lossless", new()),
		("256 colours, dithered (the defaults)", new() { Quantize = quantize }),
		("256 colours, no dither", new() { Quantize = quantize with { Dither = false } }),
		("fewest colours for quality 80", new() { Quantize = quantize with { MaxQuality = 80 } }),
		("32 colours, dithered", new() { Quantize = quantize with { MaxColors = 32 } }),
		("256 colours + SmallestSize", new() { Quantize = quantize, CompressionLevel = CompressionLevel.SmallestSize }),
		// MinQuality is the floor: 16 colours cannot reach quality 90 on a photo, so this one is written losslessly.
		("16 colours, but at least quality 90", new() { Quantize = quantize with { MaxColors = 16, MinQuality = 90 } }),
	};

	long losslessBytes = 0;
	var results = new List<(string Name, byte[] Png)>();
	outputs.Select(o =>
	{
		Stopwatch sw = Stopwatch.StartNew();
		byte[] png = PngWriter.EncodeRgb8(photo.Pixels, w, h, o.Options);
		double ms = sw.Elapsed.TotalMilliseconds;
		if (losslessBytes == 0) losslessBytes = png.Length;
		results.Add((o.Name, png));
		PngImage back = PngReader.Decode(png);
		return new
		{
			Output = o.Name,
			KB = png.Length / 1024,
			OfLossless = $"{100.0 * png.Length / losslessBytes:F0} %",
			EncodeMs = Math.Round(ms, 1),
			back.ColorType,
			back.BitDepth,
			PaletteEntries = back.Palette?.Length / 3,
		};
	}).Dump("PngWriter.EncodeRgb8");

	// 2. Where the difference shows: the smooth sky. The same 320x200 crop of each file, shown at 2x.
	const int CropX = 0, CropY = 0, CropW = 320, CropH = 200;
	Util.HorizontalRun(true, results
		.Where(r => r.Name is "lossless" or "256 colours, no dither" or "256 colours, dithered (the defaults)" or "32 colours, dithered")
		.Select(r => Util.VerticalRun(r.Name, Util.Image(Crop(PngReader.Decode(r.Png), CropX, CropY, CropW, CropH), Util.ScaleMode.ResizeTo(CropW * 2)))))
		.Dump("Sky, top-left corner");

	Util.Image(results[1].Png).Dump($"256 colours, dithered: {results[1].Png.Length / 1024} KB");
}

/// <summary>A crop of a decoded PNG as a new PNG, for display.</summary>
static byte[] Crop(PngImage png, int x, int y, int width, int height)
{
	byte[] rgba = png.ToRgba8();
	byte[] crop = new byte[width * height * 4];
	for (int row = 0; row < height; row++)
		rgba.AsSpan(((y + row) * png.Width + x) * 4, width * 4).CopyTo(crop.AsSpan(row * width * 4));
	return PngWriter.Encode(crop, width, height);
}
