<Query Kind="Program">
  <AllowUnsafe>true</AllowUnsafe>
  <NuGetReference>Sdcb.SimdCodecs</NuGetReference>
  <NuGetReference Version="4.153.1">SkiaSharp</NuGetReference>
  <Namespace>Sdcb.SimdCodecs</Namespace>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>SkiaSharp</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Sdcb.SimdCodecs decodes, SkiaSharp draws: decode any supported format straight into an SKBitmap's
// pixel memory (no intermediate copy), then resize, annotate and re-encode with Skia.
async Task Main()
{
	using HttpClient http = new();
	byte[] data = await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/samples/v1/fuji.webp");

	using SKBitmap bitmap = Decode(data);
	new { bitmap.Width, bitmap.Height, bitmap.ColorType, bitmap.AlphaType }.Dump("SKBitmap");

	// Ordinary Skia from here on: a half-size copy with a caption.
	using SKBitmap small = bitmap.Resize(new SKImageInfo(bitmap.Width / 2, bitmap.Height / 2), new SKSamplingOptions(SKCubicResampler.Mitchell));
	using (SKCanvas canvas = new(small))
	using (SKFont font = new(SKTypeface.Default, 28))
	using (SKPaint paint = new() { Color = SKColors.White, IsAntialias = true })
	{
		canvas.DrawText("decoded by Sdcb.SimdCodecs, drawn by SkiaSharp", 16, small.Height - 20, SKTextAlign.Left, font, paint);
	}

	using SKData png = small.Encode(SKEncodedImageFormat.Png, 100);
	Util.Image(png.ToArray()).Dump();
}

/// <summary>Decodes any format Sdcb.SimdCodecs knows into an RGBA8888 (unpremultiplied) SKBitmap.</summary>
static unsafe SKBitmap Decode(byte[] data)
{
	if (!SimdCodecs.TryReadInfo(data, out ImageInfo info)) throw new InvalidDataException("Not a supported image");

	SKBitmap bitmap = new(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
	// RowBytes is Width * 4 for this colour type, which is exactly the layout TryDecodeIntoRgba8 writes.
	Span<byte> pixels = new((void*)bitmap.GetPixels(), bitmap.RowBytes * bitmap.Height);
	if (!SimdCodecs.TryDecodeIntoRgba8(data, pixels))
	{
		bitmap.Dispose();
		throw new InvalidDataException("Decode failed");
	}
	bitmap.NotifyPixelsChanged();
	return bitmap;
}
