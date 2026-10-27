<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Png</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
  <Namespace>System.IO.Compression</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// PNG in and out: what PngReader reports for 8-bit, 16-bit and palette files, writing RGB / grey /
// 16-bit with text chunks, and trading speed for size with the compression level and parallel deflate.
async Task Main()
{
	using HttpClient http = new();
	const string baseUrl = "https://cv-public.sdcb.ai/simdcodecs/samples/v1/";

	// 1. Reading keeps the file's own layout: bit depth, colour type, palette.
	string[] names = ["fuji.png", "fuji-16bit.png", "fuji-indexed.png"];
	var files = new Dictionary<string, PngImage>();
	foreach (string name in names)
	{
		PngImage png = PngReader.Decode(await http.GetByteArrayAsync(baseUrl + name));
		files[name] = png;
		new { File = name, png.Width, png.Height, png.BitDepth, png.ColorType, png.SamplesPerPixel, PaletteEntries = png.Palette?.Length / 3, png.Gamma, IccBytes = png.IccProfile?.Length }
			.Dump(name);
	}
	Util.Image(PngWriter.Encode(files["fuji-indexed.png"].ToRgba8(), files["fuji-indexed.png"].Width, files["fuji-indexed.png"].Height))
		.Dump("fuji-indexed.png (64 colours), expanded with ToRgba8()");

	// 2. Writing. Pixels go in tightly packed; 16-bit samples as ushort in host order.
	PngImage rgb8 = files["fuji.png"];
	int w = rgb8.Width, h = rgb8.Height;
	byte[] gray = new byte[w * h];
	for (int i = 0; i < gray.Length; i++)
		gray[i] = (byte)((rgb8.Pixels[i * 3] * 77 + rgb8.Pixels[i * 3 + 1] * 150 + rgb8.Pixels[i * 3 + 2] * 29) >> 8);

	PngWriteOptions withText = new()
	{
		Text = [new PngText("Title", "Mount Fuji"), new PngText("Software", "Sdcb.SimdCodecs.Png")],
	};
	PngImage rgb16 = files["fuji-16bit.png"];

	var outputs = new (string Name, Func<byte[]> Encode)[]
	{
		("RGB8, default (Optimal)", () => PngWriter.EncodeRgb8(rgb8.Pixels, w, h, withText)),
		("RGB8, Fastest", () => PngWriter.EncodeRgb8(rgb8.Pixels, w, h, withText with { CompressionLevel = CompressionLevel.Fastest })),
		("RGB8, SmallestSize", () => PngWriter.EncodeRgb8(rgb8.Pixels, w, h, withText with { CompressionLevel = CompressionLevel.SmallestSize })),
		("RGB8, 8 parallel fragments", () => PngWriter.EncodeRgb8(rgb8.Pixels, w, h, withText with { ParallelFragments = 8 })),
		("Gray8", () => PngWriter.EncodeGray8(gray, w, h, withText)),
		("RGB16", () => PngWriter.EncodeRgb16(rgb16.AsUInt16Samples(), rgb16.Width, rgb16.Height)),
	};
	outputs.Select(o =>
	{
		Stopwatch sw = Stopwatch.StartNew();
		byte[] bytes = o.Encode();
		double ms = sw.Elapsed.TotalMilliseconds;
		PngImage back = PngReader.Decode(bytes);
		return new { Output = o.Name, KB = bytes.Length / 1024, EncodeMs = Math.Round(ms, 1), back.BitDepth, back.ColorType, Text = string.Join("; ", back.Text.Select(t => $"{t.Keyword}={t.Text}")) };
	}).Dump("PngWriter");

	Util.Image(PngWriter.EncodeGray8(gray, w, h)).Dump("Gray8");
}
