<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs</NuGetReference>
  <Namespace>Sdcb.SimdCodecs</Namespace>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
  <Namespace>System.IO.Compression</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// One code path for every format the facade knows: the same three calls decode a JPEG, a 16-bit
// PNG, a palette PNG, lossy / lossless / animated WebP, tiled and float TIFF, JPEG 2000 and JBIG2.
async Task Main()
{
	string[] files =
	[
		"photo.jpg",
		"fuji.png",
		"fuji-16bit.png",
		"fuji-indexed.png",
		"fuji.webp",
		"cutout-lossless.webp",
		"mandelbrot-zoom.webp",
		"fuji-deflate.tif",
		"fuji-16bit-tiled.tif",
		"fuji-gray-float32.tif",
		"multipage.tif",
		"fuji.jp2",
		"text-page.jb2",
	];

	using HttpClient http = new();
	var rows = new List<object>();
	foreach (string name in files)
	{
		byte[] data = await http.GetByteArrayAsync($"https://cv-public.sdcb.ai/simdcodecs/samples/v1/{name}");
		if (!SimdCodecs.TryReadInfo(data, out ImageInfo info))
		{
			rows.Add(new { File = name, Error = "not recognised" });
			continue;
		}

		Stopwatch sw = Stopwatch.StartNew();
		byte[] rgba = new byte[info.Width * info.Height * 4];
		bool ok = info.SampleFormat == SampleFormat.Float32
			// Float samples have no single 8-bit rendering, so the facade will not pick one for you:
			// take the floats and choose your own stretch (here: min..max of the image).
			? DecodeFloatStretched(data, rgba)
			: SimdCodecs.TryDecodeIntoRgba8(data, rgba);
		double ms = sw.Elapsed.TotalMilliseconds;

		rows.Add(new
		{
			File = name,
			KB = data.Length / 1024,
			Size = $"{info.Width}x{info.Height}",
			info.Channels,
			info.SampleFormat,
			DecodeMs = Math.Round(ms, 1),
			Preview = ok ? Show(Thumbnail(rgba, info.Width, info.Height, 320, out int tw, out int th), tw, th) : "decode failed",
		});
	}
	rows.Dump("Decoded by Sdcb.SimdCodecs");
}

static bool DecodeFloatStretched(byte[] data, byte[] rgba)
{
	if (!SimdCodecs.TryDecode(data, out IDecodedImage? image)) return false;
	float[] f = image.ToFloats();   // RGBA floats, alpha = 1
	float min = float.MaxValue, max = float.MinValue;
	for (int i = 0; i < f.Length; i++)
	{
		if ((i & 3) == 3) continue;
		min = Math.Min(min, f[i]);
		max = Math.Max(max, f[i]);
	}
	for (int i = 0; i < f.Length; i++)
		rgba[i] = (i & 3) == 3 ? (byte)255 : (byte)Math.Clamp((f[i] - min) / (max - min) * 255f + 0.5f, 0, 255);
	return true;
}

/// <summary>Nearest-neighbour downscale to at most <paramref name="maxSide"/> pixels, for previews.</summary>
static byte[] Thumbnail(byte[] rgba, int width, int height, int maxSide, out int tw, out int th)
{
	double scale = Math.Min(1.0, (double)maxSide / Math.Max(width, height));
	tw = Math.Max(1, (int)(width * scale));
	th = Math.Max(1, (int)(height * scale));
	byte[] result = new byte[tw * th * 4];
	for (int y = 0; y < th; y++)
	{
		int sy = (int)((long)y * height / th);
		for (int x = 0; x < tw; x++)
		{
			int sx = (int)((long)x * width / tw);
			rgba.AsSpan((sy * width + sx) * 4, 4).CopyTo(result.AsSpan((y * tw + x) * 4, 4));
		}
	}
	return result;
}

/// <summary>Displays RGBA pixels in LINQPad by encoding them as PNG.</summary>
static object Show(byte[] rgba, int width, int height) =>
	Util.Image(PngWriter.Encode(rgba, width, height, new PngWriteOptions { CompressionLevel = CompressionLevel.Fastest }));
