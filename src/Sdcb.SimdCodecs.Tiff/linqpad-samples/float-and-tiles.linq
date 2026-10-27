<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Png</NuGetReference>
  <NuGetReference>Sdcb.SimdCodecs.Tiff</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
  <Namespace>Sdcb.SimdCodecs.Tiff</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Runtime.InteropServices</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Scientific-style TIFF: 32-bit float samples (Deflate + the floating-point predictor, as libtiff
// writes them), and a 16-bit tiled image read one tile at a time without decoding the rest.
async Task Main()
{
	using HttpClient http = new();
	const string baseUrl = "https://cv-public.sdcb.ai/simdcodecs/samples/v1/";

	// 1. Float. Pixels are host-order bytes, so MemoryMarshal.Cast gives the floats without a copy.
	byte[] floatTiff = await http.GetByteArrayAsync(baseUrl + "fuji-gray-float32.tif");
	TiffPageLayout floatLayout = TiffReader.ReadLayout(floatTiff);
	new { floatLayout.Width, floatLayout.Height, floatLayout.SampleFormat, floatLayout.BitsPerSample, floatLayout.Compression, floatLayout.Predictor }
		.Dump("fuji-gray-float32.tif");

	TiffPage floatPage = TiffReader.ReadPage(floatTiff);
	ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(floatPage.Pixels);
	float min = float.MaxValue, max = float.MinValue;
	foreach (float v in samples)
	{
		min = Math.Min(min, v);
		max = Math.Max(max, v);
	}
	new { Min = min, Max = max, floatPage.SMinSampleValue, floatPage.SMaxSampleValue }.Dump("Sample range");

	// Floats have no fixed 8-bit rendering; a min..max stretch is one choice among many.
	byte[] stretched = new byte[samples.Length];
	for (int i = 0; i < samples.Length; i++)
		stretched[i] = (byte)Math.Clamp((samples[i] - min) / (max - min) * 255f + 0.5f, 0, 255);
	Util.Image(PngWriter.EncodeGray8(stretched, floatPage.Width, floatPage.Height)).Dump("Stretched to 8-bit");

	// 2. Tiles. The layout says how the page is cut; ReadSegment decodes ONE tile into your buffer.
	//    With a Stream or a file path instead of a byte array, only that tile's bytes are read.
	byte[] tiledTiff = await http.GetByteArrayAsync(baseUrl + "fuji-16bit-tiled.tif");
	TiffPageLayout layout = TiffReader.ReadLayout(tiledTiff);
	new { layout.Width, layout.Height, layout.BitsPerSample, layout.SamplesPerPixel, Tile = $"{layout.TileWidth}x{layout.TileHeight}", Tiles = $"{layout.SegmentsAcross}x{layout.SegmentsDown}", layout.Compression, layout.Predictor }
		.Dump("fuji-16bit-tiled.tif");

	int tile = layout.SegmentAt(layout.Width / 2, layout.Height / 4);   // the tile holding the summit
	byte[] buffer = new byte[layout.SegmentBytes(tile)];
	Stopwatch sw = Stopwatch.StartNew();
	TiffSegmentRect rect = TiffReader.ReadSegment(tiledTiff, layout, tile, buffer);
	$"Tile {tile} at ({rect.X}, {rect.Y}), {rect.Width}x{rect.Height}, read in {sw.Elapsed.TotalMilliseconds:F2} ms".Dump();

	// 16-bit RGB -> 8-bit for display: keep the high byte.
	ReadOnlySpan<ushort> rgb16 = MemoryMarshal.Cast<byte, ushort>(buffer);
	byte[] rgb8 = new byte[rgb16.Length];
	for (int i = 0; i < rgb8.Length; i++) rgb8[i] = (byte)(rgb16[i] >> 8);
	Util.Image(PngWriter.EncodeRgb8(rgb8, rect.Width, rect.Height)).Dump("One tile");
}
