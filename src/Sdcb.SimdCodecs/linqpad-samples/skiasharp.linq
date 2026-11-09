<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs</NuGetReference>
  <NuGetReference Version="4.153.1">SkiaSharp</NuGetReference>
  <Namespace>Sdcb.SimdCodecs</Namespace>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>SkiaSharp</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Sdcb.SimdCodecs decodes, SkiaSharp draws: decode any supported format straight into an SKBitmap's
// pixel memory, in Skia's own native layout (premultiplied, BGRA or RGBA by platform) so drawing needs no
// conversion, upright by the file's EXIF orientation; then resize, annotate and re-encode with Skia.
async Task Main()
{
	using HttpClient http = new();
	byte[] data = await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/samples/v1/cutout-lossless.webp");

	using SKBitmap bitmap = Decode(data);
	new { bitmap.Width, bitmap.Height, bitmap.ColorType, bitmap.AlphaType }.Dump("SKBitmap");

	// Ordinary Skia from here on: a half-size copy over a checkerboard (the cutout has transparency), with a caption.
	using SKBitmap small = bitmap.Resize(new SKImageInfo(bitmap.Width / 2, bitmap.Height / 2, bitmap.ColorType, bitmap.AlphaType),
		new SKSamplingOptions(SKCubicResampler.Mitchell));
	using SKBitmap canvasBitmap = new(small.Width, small.Height);
	using (SKCanvas canvas = new(canvasBitmap))
	using (SKFont font = new(SKTypeface.Default, 16))
	using (SKPaint paint = new() { Color = SKColors.Black, IsAntialias = true })
	{
		DrawCheckerboard(canvas, small.Width, small.Height);
		canvas.DrawBitmap(small, 0, 0);
		canvas.DrawText("decoded by Sdcb.SimdCodecs, drawn by SkiaSharp", 12, small.Height - 12, SKTextAlign.Left, font, paint);
	}

	using SKData png = canvasBitmap.Encode(SKEncodedImageFormat.Png, 100);
	Util.Image(png.ToArray()).Dump();
}

/// <summary>Decodes any format Sdcb.SimdCodecs knows into a premultiplied SKBitmap in the platform's colour type.</summary>
static unsafe SKBitmap Decode(byte[] data)
{
	DecodeOptions upright = new() { ApplyOrientation = true };
	if (!ImageCodecs.TryReadInfo(data, out ImageInfo info)) throw new InvalidDataException("Not a supported image");
	(int width, int height) = upright.GetOutputSize(info);

	// Skia's native colour type is BGRA on Windows and most other platforms, RGBA on some; both have a PixelFormat.
	SKColorType colorType = SKImageInfo.PlatformColorType;
	PixelFormat format = colorType == SKColorType.Bgra8888 ? PixelFormat.Bgra32Premultiplied : PixelFormat.Rgba32Premultiplied;
	SKBitmap bitmap = new(new SKImageInfo(width, height, colorType == SKColorType.Bgra8888 ? colorType : SKColorType.Rgba8888, SKAlphaType.Premul));

	Span<byte> pixels = new((void*)bitmap.GetPixels(), bitmap.RowBytes * bitmap.Height);
	if (!ImageCodecs.TryDecodeInto(data, pixels, bitmap.RowBytes, format, upright))
	{
		bitmap.Dispose();
		throw new InvalidDataException("Decode failed");
	}
	bitmap.NotifyPixelsChanged();
	return bitmap;
}

static void DrawCheckerboard(SKCanvas canvas, int width, int height)
{
	const int Cell = 16;
	using SKPaint light = new() { Color = new SKColor(0xEE, 0xEE, 0xEE) };
	using SKPaint dark = new() { Color = new SKColor(0xCC, 0xCC, 0xCC) };
	for (int y = 0; y < height; y += Cell)
		for (int x = 0; x < width; x += Cell)
			canvas.DrawRect(x, y, Cell, Cell, ((x + y) / Cell) % 2 == 0 ? light : dark);
}
