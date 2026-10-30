<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Qoi</NuGetReference>
  <NuGetReference>Sdcb.SimdCodecs.Png</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
  <Namespace>Sdcb.SimdCodecs.Qoi</Namespace>
</Query>

// QOI against PNG on two kinds of image, a noisy photo-like one and a screenshot-like one of flat colours and thin
// lines. QOI's case is speed: it encodes an order of magnitude faster than PNG. PNG's is size: deflate finds the
// repeats QOI's one-pixel lookback cannot, and on flat synthetic images that is a large factor. Then decoding
// straight into a padded BGRA buffer, and what the header carries.
void Main()
{
	const int width = 1280, height = 720;
	var images = new (string Name, byte[] Rgba)[]
	{
		("Photo-like", MakePhoto(width, height)),
		("Screenshot-like", MakeScreen(width, height)),
	};

	var rows = new List<object>();
	foreach (var (name, rgba) in images)
	{
		byte[] qoi = QoiEncoder.Encode(rgba, width, height, ImagePixelFormat.Rgba32);
		byte[] png = PngWriter.Encode(rgba, width, height);
		var decoded = new byte[rgba.Length];

		double qoiEncode = Best(() => QoiEncoder.Encode(rgba, width, height, ImagePixelFormat.Rgba32));
		double pngEncode = Best(() => PngWriter.Encode(rgba, width, height));
		double qoiDecode = Best(() => QoiDecoder.DecodeTo(qoi, decoded, ImagePixelFormat.Rgba32));
		bool qoiExact = decoded.AsSpan().SequenceEqual(rgba);
		double pngDecode = Best(() => PngImageDecoder.TryDecodeIntoRgba8(png, decoded));
		bool pngExact = decoded.AsSpan().SequenceEqual(rgba);

		rows.Add(new
		{
			Image = name,
			QoiKB = qoi.Length / 1024,
			PngKB = png.Length / 1024,
			QoiEncodeMs = qoiEncode,
			PngEncodeMs = pngEncode,
			QoiDecodeMs = qoiDecode,
			PngDecodeMs = pngDecode,
			BothExact = qoiExact && pngExact,
			Preview = Util.Image(png),
		});
	}
	rows.Dump("QOI and PNG, best of 5 runs (both lossless)");

	// Decode into a BGRA buffer whose rows are padded to a multiple of 64 bytes: no intermediate copy, no allocation.
	byte[] screen = images[1].Rgba;
	byte[] file = QoiEncoder.Encode(screen, width, height, ImagePixelFormat.Rgba32);
	int stride = (width * 4 + 63) / 64 * 64;
	var bgra = new byte[stride * height];
	QoiDecoder.DecodeTo(file, bgra, ImagePixelFormat.Bgra32, stride);
	int px = 100 * stride + 200 * 4, src = (100 * width + 200) * 4;
	new
	{
		Stride = stride,
		Rgba = $"{screen[src]}, {screen[src + 1]}, {screen[src + 2]}, {screen[src + 3]}",
		Bgra = $"{bgra[px]}, {bgra[px + 1]}, {bgra[px + 2]}, {bgra[px + 3]}",
	}.Dump("Pixel (200, 100) in the source and in the padded BGRA buffer");

	// The header: 3 or 4 channels, and a colour space byte that is reported but does not change the pixels.
	byte[] rgb = new byte[width * height * 3];
	for (int i = 0, j = 0; j < rgb.Length; i += 4, j += 3)
	{
		rgb[j] = screen[i];
		rgb[j + 1] = screen[i + 1];
		rgb[j + 2] = screen[i + 2];
	}
	var headers = new[]
	{
		QoiDecoder.ReadInfo(file),
		QoiDecoder.ReadInfo(QoiEncoder.Encode(rgb, width, height, ImagePixelFormat.Rgb24)),
		QoiDecoder.ReadInfo(QoiEncoder.Encode(rgb, width, height, ImagePixelFormat.Rgb24, QoiColorSpace.Linear)),
	};
	headers.Dump("Headers: RGBA input, RGB input, RGB input marked linear");
}

static double Best(Action action)
{
	action(); // warm up
	double best = double.MaxValue;
	for (int i = 0; i < 5; i++)
	{
		var sw = Stopwatch.StartNew();
		action();
		best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
	}
	return Math.Round(best, 2);
}

// Smooth colour with a little noise, the way a camera's sensor leaves it.
static byte[] MakePhoto(int width, int height)
{
	var random = new Random(1);
	byte[] rgba = new byte[width * height * 4];
	for (int y = 0; y < height; y++)
	{
		for (int x = 0; x < width; x++)
		{
			int i = (y * width + x) * 4;
			double t = Math.Sin(x * 0.011) * Math.Cos(y * 0.017);
			rgba[i] = Noisy(110 + 90 * t, random);
			rgba[i + 1] = Noisy(60 + 150.0 * y / height, random);
			rgba[i + 2] = Noisy(200 - 150.0 * x / width, random);
			rgba[i + 3] = 255;
		}
	}
	return rgba;
}

static byte Noisy(double value, Random random) => (byte)Math.Clamp(value + random.Next(-6, 7), 0, 255);

// A window-like layout: flat panels, a title bar, and rows of thin dark strokes standing in for text.
static byte[] MakeScreen(int width, int height)
{
	byte[] rgba = new byte[width * height * 4];
	for (int y = 0; y < height; y++)
	{
		for (int x = 0; x < width; x++)
		{
			int i = (y * width + x) * 4;
			(byte r, byte g, byte b) = y < 40 ? ((byte)45, (byte)90, (byte)160)
				: x < 240 ? ((byte)235, (byte)238, (byte)242)
				: ((byte)255, (byte)255, (byte)255);
			bool text = y > 60 && y % 24 < 12 && x > 260 && x < width - 40 && (x * 7 + y / 24 * 13) % 23 < 9 && (x / 37 + y / 24) % 5 != 0;
			bool menu = x < 240 && y > 60 && y % 32 < 2;
			if (text) (r, g, b) = (30, 30, 30);
			if (menu) (r, g, b) = (200, 205, 212);
			rgba[i] = r;
			rgba[i + 1] = g;
			rgba[i + 2] = b;
			rgba[i + 3] = 255;
		}
	}
	return rgba;
}
