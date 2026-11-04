<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs</NuGetReference>
  <NuGetReference>Sdcb.SimdCodecs.Heif</NuGetReference>
  <Namespace>Sdcb.SimdCodecs</Namespace>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>Sdcb.SimdCodecs.Heif</Namespace>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
  <Namespace>System.IO.Compression</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// HEIF / HEIC through the Sdcb.SimdCodecs facade. HEVC is patent-encumbered, so the facade package does not
// reference Sdcb.SimdCodecs.Heif: install it yourself and register it once at start-up. This package conveys no
// patent licence; evaluate your obligations before deploying it.
async Task Main()
{
	using HttpClient http = new();
	byte[] heic = await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/test-images/heif/hevc-main-3072x4096-grid.heic");

	// Before registration the facade recognises HEIF (so you can tell the user what it cannot open) but does not decode it.
	new
	{
		Detected = SimdCodecs.DetectFormat(heic),
		IsDecodable = SimdCodecs.IsDecodable(ImageFormat.Heif),
		TryReadInfo = SimdCodecs.TryReadInfo(heic, out _),
	}.Dump("Before SimdCodecs.Register");

	SimdCodecs.Register(HeifImageDecoder.Registration); // idempotent and thread-safe; cannot be undone

	// From here on every facade entry point handles HEIF like a built-in format.
	if (!SimdCodecs.TryReadInfo(heic, out ImageInfo info)) throw new InvalidDataException("Not a decodable HEIF file");
	info.Dump("After SimdCodecs.Register: TryReadInfo (display size; 3 channels, 8-bit)");

	// Any 8-bit layout, any stride, straight into your buffer: each tile is converted into it as soon as it is
	// decoded, so no full-size intermediate frame exists. Here BGRA (Windows / WPF / Skia surfaces) with 64 bytes
	// of padding after every row, as a sub-rectangle of a wider surface would have.
	int stride = info.Width * 4 + 64;
	byte[] bgra = new byte[stride * info.Height];
	if (!SimdCodecs.TryDecodeInto(heic, bgra, stride, ImagePixelFormat.Bgra32)) throw new InvalidDataException("Decode failed");
	$"Bgra32, stride {stride}: {Fastest(() => SimdCodecs.TryDecodeInto(heic, bgra, stride, ImagePixelFormat.Bgra32)):F1} ms".Dump();

	// Gray8 is the stored luma (Y), range-mapped to 8 bits: chroma is never converted.
	byte[] gray = new byte[info.Width * info.Height];
	SimdCodecs.TryDecodeInto(heic, gray, 0, ImagePixelFormat.Gray8);
	$"Gray8: {Fastest(() => SimdCodecs.TryDecodeInto(heic, gray, 0, ImagePixelFormat.Gray8)):F1} ms".Dump();

	ShowBgra(bgra, stride, info.Width, info.Height).Dump("Bgra32 (thumbnail)");
	ShowGray(gray, info.Width, info.Height).Dump("Gray8 (thumbnail)");

	// The fidelity view: 8-bit RGB with the ICC profile passed through and the colour encoding from the file.
	if (SimdCodecs.TryDecode(heic, out IDecodedImage? image))
	{
		new { image.Width, image.Height, image.Channels, image.SampleFormat, IccBytes = image.IccProfile.Length, image.ColorEncoding }
			.Dump("SimdCodecs.TryDecode");
	}
}

/// <summary>The fastest of five runs, in ms: the first calls also JIT-compile (and tier up) the decoder.</summary>
static double Fastest(Action decode)
{
	double best = double.MaxValue;
	for (int i = 0; i < 5; i++)
	{
		Stopwatch sw = Stopwatch.StartNew();
		decode();
		best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
	}
	return best;
}

const int Thumb = 6; // show every 6th pixel: 512x682 for a 3072x4096 photo

static object ShowBgra(byte[] bgra, int stride, int width, int height)
{
	int tw = width / Thumb, th = height / Thumb;
	var rgba = new byte[tw * th * 4];
	for (int y = 0; y < th; y++)
	for (int x = 0; x < tw; x++)
	{
		int s = y * Thumb * stride + x * Thumb * 4, d = (y * tw + x) * 4;
		rgba[d] = bgra[s + 2]; rgba[d + 1] = bgra[s + 1]; rgba[d + 2] = bgra[s]; rgba[d + 3] = bgra[s + 3];
	}
	return Util.Image(PngWriter.Encode(rgba, tw, th, new PngWriteOptions { CompressionLevel = CompressionLevel.Fastest }));
}

static object ShowGray(byte[] gray, int width, int height)
{
	int tw = width / Thumb, th = height / Thumb;
	var small = new byte[tw * th];
	for (int y = 0; y < th; y++)
	for (int x = 0; x < tw; x++)
		small[y * tw + x] = gray[y * Thumb * width + x * Thumb];
	return Util.Image(PngWriter.EncodeGray8(small, tw, th, new PngWriteOptions { CompressionLevel = CompressionLevel.Fastest }));
}
