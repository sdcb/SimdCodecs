<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs</NuGetReference>
  <Namespace>Sdcb.SimdCodecs</Namespace>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Quick start: hand the facade any image file's bytes. It sniffs the format from the magic bytes
// (JPEG, PNG, TIFF, WebP, GIF, BMP, ICO / CUR, QOI, Netpbm, JPEG 2000, JBIG2) and decodes to the
// pixel format you ask for -- no format switch needed.
async Task Main()
{
	using HttpClient http = new();
	byte[] data = await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/samples/v1/photo.jpg");

	// Header only: size, layout, colour encoding and EXIF orientation, without decoding a pixel.
	if (!ImageCodecs.TryReadInfo(data, out ImageInfo info)) throw new InvalidDataException("Not a supported image");
	info.Dump("ImageCodecs.TryReadInfo");

	// One call: 8-bit RGBA, turned upright. Pixels come out as stored unless you ask: a phone photo taken in
	// portrait is stored sideways with an EXIF orientation tag, and ApplyOrientation applies it.
	DecodeOptions upright = new() { ApplyOrientation = true };
	Stopwatch sw = Stopwatch.StartNew();
	using (PixelBuffer rgba = ImageCodecs.Decode(data, PixelFormat.Rgba32, upright))
	{
		$"Decoded {rgba.Width}x{rgba.Height} in {sw.Elapsed.TotalMilliseconds:F1} ms".Dump();
		Show(rgba).Dump("photo.jpg, decoded by Sdcb.SimdCodecs");
	}

	// Into a buffer you own (a bitmap, a Mat, an upload buffer), in any PixelFormat: GetOutputSize gives the size
	// the options produce (width and height swap for a photo stored a quarter turn off).
	(int width, int height) = upright.GetOutputSize(info);
	byte[] bgr = new byte[width * height * PixelFormat.Bgr24.BytesPerPixel];
	if (!ImageCodecs.TryDecodeInto(data, bgr, 0, PixelFormat.Bgr24, upright)) throw new InvalidDataException("Decode failed");
	$"TryDecodeInto: {bgr.Length:N0} bytes of BGR, {width}x{height}".Dump();

	// Or keep the decoder's own layout: channels, 8/16-bit or float samples, and the file's Exif / XMP / ICC.
	// A decoded image is IDisposable: it returns pooled memory (DecodeOptions.Pool) when disposed.
	using (IDecodedImage image = ImageCodecs.Decode(data))
		new { image.Width, image.Height, image.Channels, image.SampleFormat, IccBytes = image.Metadata.Icc.Length, image.ColorEncoding }
			.Dump("ImageCodecs.Decode");

	// Decode throws with the reason; the Try* methods return false instead.
	Reason(() => ImageCodecs.Decode(data.AsSpan(0, 1000))).Dump("A JPEG cut after 1000 bytes");
	Reason(() => ImageCodecs.Decode("not an image"u8)).Dump("Not an image");
}

static string Reason(Action decode)
{
	try { decode(); return "decoded"; }
	catch (Exception e) when (e is InvalidDataException or NotSupportedException) { return $"{e.GetType().Name}: {e.Message}"; }
}

/// <summary>Displays decoded pixels in LINQPad by encoding them as PNG.</summary>
static object Show(PixelBuffer pixels) =>
	Util.Image(ImageCodecs.Encode(pixels.Pixels, pixels.Width, pixels.Height, pixels.Format, ImageFormat.Png, pixels.Stride));
