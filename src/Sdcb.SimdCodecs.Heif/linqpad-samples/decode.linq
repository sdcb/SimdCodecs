<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Heif</NuGetReference>
  <NuGetReference>Sdcb.SimdCodecs.Png</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Heif</Namespace>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
  <Namespace>System.IO.Compression</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// HEIF / HEIC phone photos with the codec's own API. HEVC is patent-encumbered and this package conveys no
// patent licence: evaluate your obligations before deploying it.
//
// ReadInfo parses the container only (no HEVC decoding); Decode returns display-oriented RGBA8 (clap / irot /
// imir applied) and passes the ICC profile through; DecodeTo writes into a buffer you own. A phone photo is a
// grid of 512x512 HEVC tiles, decoded in parallel and converted straight into the output.
async Task Main()
{
	using HttpClient http = new();
	const string baseUrl = "https://cv-public.sdcb.ai/simdcodecs/test-images/heif/";

	foreach (string name in new[]
	{
		"hevc-main-3072x4096-grid.heic",            // 12.6 MP, 8-bit, 48 tiles
		"hevc-main10-3072x4096-grid.heic",          // 12.6 MP, 10-bit (Main 10), converted to 8-bit RGBA
		"synth-photo-mangrove-1599x1199-main8.heic", // one item, cropped by a 'clap' clean aperture
	})
	{
		byte[] heic = await http.GetByteArrayAsync(baseUrl + name);
		HeifInfo info = HeifDecoder.ReadInfo(heic);
		new { info.Width, info.Height, info.BitDepth, info.TileCount, IccBytes = info.IccProfile?.Length, info.Orientation }.Dump(name);

		// The first calls also JIT-compile the decoder (and tier it up), so report the fastest of a few.
		double best = double.MaxValue;
		for (int i = 0; i < 5; i++)
		{
			Stopwatch sw = Stopwatch.StartNew();
			HeifDecoder.Decode(heic).Dispose();
			best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
		}
		using HeifImage image = HeifDecoder.Decode(heic);
		$"Decoded {image.Width}x{image.Height} ({image.Width * (double)image.Height / 1e6:F1} MP) in {best:F1} ms (fastest of 5)".Dump();

		// A 12 MP PNG is slow to show; a box-filtered thumbnail is enough to look at.
		Show(Thumbnail(image.Rgba, image.Width, image.Height, 800, out int tw, out int th), tw, th).Dump();
	}

	// Decoding into a reused buffer: size it from ReadInfo, then decode as many images as you like into it.
	byte[] photo = await http.GetByteArrayAsync(baseUrl + "hevc-main-3072x4096-grid.heic");
	HeifInfo photoInfo = HeifDecoder.ReadInfo(photo);
	byte[] rgba = new byte[photoInfo.Width * photoInfo.Height * 4];
	var (width, height) = HeifDecoder.DecodeTo(photo, rgba);
	$"DecodeTo filled {rgba.Length:N0} bytes ({width}x{height})".Dump();

	// Malformed or unsupported data (image sequences, range-extension profiles, ...) throws InvalidDataException.
	try { HeifDecoder.Decode(photo.AsSpan(0, photo.Length / 2)); }
	catch (InvalidDataException e) { $"Truncated file: {e.Message}".Dump(); }
}

/// <summary>Displays RGBA pixels in LINQPad by encoding them as PNG.</summary>
static object Show(byte[] rgba, int width, int height) =>
	Util.Image(PngWriter.Encode(rgba, width, height, new PngWriteOptions { CompressionLevel = CompressionLevel.Fastest }));

/// <summary>Averages square blocks of pixels so the result is at most <paramref name="maxSide"/> pixels on its longer side.</summary>
static byte[] Thumbnail(ReadOnlySpan<byte> rgba, int width, int height, int maxSide, out int tw, out int th)
{
	int step = Math.Max(1, (Math.Max(width, height) + maxSide - 1) / maxSide);
	tw = width / step;
	th = height / step;
	var result = new byte[tw * th * 4];
	for (int y = 0; y < th; y++)
	for (int x = 0; x < tw; x++)
	for (int c = 0; c < 4; c++)
	{
		int sum = 0;
		for (int dy = 0; dy < step; dy++)
		for (int dx = 0; dx < step; dx++)
			sum += rgba[((y * step + dy) * width + x * step + dx) * 4 + c];
		result[(y * tw + x) * 4 + c] = (byte)(sum / (step * step));
	}
	return result;
}
