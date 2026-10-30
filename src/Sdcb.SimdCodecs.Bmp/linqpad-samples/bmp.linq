<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Bmp</NuGetReference>
  <NuGetReference>Sdcb.SimdCodecs.Png</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>Sdcb.SimdCodecs.Bmp</Namespace>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
</Query>

// Every BMP layout the encoder writes, from one RGBA image: size, how long it took, what the headers say, and
// whether it decodes back to the same pixels (palettes and 16-bit are lossy, the rest exact).
void Main()
{
	const int width = 640, height = 360;
	byte[] rgba = MakeImage(width, height);
	Util.Image(PngWriter.Encode(rgba, width, height)).Dump("Source");

	var layouts = new (string Name, BmpEncodeOptions Options)[]
	{
		("24-bit", new() { Format = BmpEncodeFormat.Rgb24 }),
		("32-bit with alpha (V4)", new() { Format = BmpEncodeFormat.Argb32 }),
		("16-bit 5-6-5", new() { Format = BmpEncodeFormat.Rgb565 }),
		("8-bit palette, dithered", new() { Format = BmpEncodeFormat.Indexed8, Dither = true }),
		("8-bit palette, RLE8", new() { Format = BmpEncodeFormat.Indexed8, Rle = true }),
		("4-bit palette, RLE4", new() { Format = BmpEncodeFormat.Indexed4, Rle = true }),
		("1-bit, dithered", new() { Format = BmpEncodeFormat.Indexed1, Dither = true }),
		("24-bit, OS/2 core header", new() { Format = BmpEncodeFormat.Rgb24, Header = BmpHeaderVersion.Core }),
	};

	var rows = new List<object>();
	foreach (var (name, options) in layouts)
	{
		var sw = Stopwatch.StartNew();
		byte[] bmp = BmpEncoder.Encode(rgba, width, height, ImagePixelFormat.Rgba32, options);
		double encodeMs = sw.Elapsed.TotalMilliseconds;

		BmpInfo info = BmpDecoder.ReadInfo(bmp);
		var decoded = new byte[width * height * 4];
		sw.Restart();
		BmpDecoder.DecodeTo(bmp, decoded, ImagePixelFormat.Rgba32);
		double decodeMs = sw.Elapsed.TotalMilliseconds;

		rows.Add(new
		{
			Layout = name,
			KB = bmp.Length / 1024,
			EncodeMs = Math.Round(encodeMs, 2),
			DecodeMs = Math.Round(decodeMs, 2),
			info.BitsPerPixel,
			info.Compression,
			info.HeaderKind,
			Exact = decoded.AsSpan().SequenceEqual(options.Format == BmpEncodeFormat.Argb32 ? rgba : Opaque(rgba)),
			Image = Util.Image(PngWriter.Encode(decoded, width, height)),
		});
	}
	rows.Dump("Encoded, read back");
}

static byte[] Opaque(byte[] rgba)
{
	byte[] copy = (byte[])rgba.Clone();
	for (int i = 3; i < copy.Length; i += 4) copy[i] = 255;
	return copy;
}

static byte[] MakeImage(int width, int height)
{
	byte[] rgba = new byte[width * height * 4];
	for (int y = 0; y < height; y++)
	{
		for (int x = 0; x < width; x++)
		{
			int i = (y * width + x) * 4;
			double t = Math.Sin(x * 0.05) * Math.Cos(y * 0.04);
			rgba[i] = (byte)(128 + 127 * t);
			rgba[i + 1] = (byte)(x * 255 / (width - 1));
			rgba[i + 2] = (byte)(y * 255 / (height - 1));
			int dx = x - width / 2, dy = y - height / 2;
			rgba[i + 3] = (byte)Math.Clamp(255 - Math.Sqrt(dx * dx + dy * dy) * 255 / (height / 2), 0, 255);
		}
	}
	return rgba;
}
