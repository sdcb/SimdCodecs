<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs</NuGetReference>
  <Namespace>Sdcb.SimdCodecs</Namespace>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
  <Namespace>System.IO.Compression</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Quick start: hand the facade any image file's bytes. It sniffs the format from the magic
// bytes (JPEG, PNG, TIFF, WebP, JPEG 2000, JBIG2) and decodes to RGBA -- no format switch needed.
async Task Main()
{
	using HttpClient http = new();
	byte[] data = await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/samples/v1/photo.jpg");

	// Header only: size, channels and sample format, without decoding a pixel.
	if (!SimdCodecs.TryReadInfo(data, out ImageInfo info)) throw new InvalidDataException("Not a supported image");
	info.Dump("SimdCodecs.TryReadInfo");

	// Decode straight into a buffer you own: 4 bytes per pixel, R G B A.
	byte[] rgba = new byte[info.Width * info.Height * 4];
	Stopwatch sw = Stopwatch.StartNew();
	if (!SimdCodecs.TryDecodeIntoRgba8(data, rgba)) throw new InvalidDataException("Decode failed");
	$"Decoded {info.Width}x{info.Height} in {sw.Elapsed.TotalMilliseconds:F1} ms".Dump();

	Show(rgba, info.Width, info.Height).Dump("photo.jpg, decoded by Sdcb.SimdCodecs");

	// Or keep the decoder's native layout (channels, 8/16-bit or float samples, ICC profile).
	if (SimdCodecs.TryDecode(data, out IDecodedImage? image))
	{
		new { image.Width, image.Height, image.Channels, image.SampleFormat, IccBytes = image.IccProfile.Length, image.ColorEncoding }
			.Dump("SimdCodecs.TryDecode");
	}
}

/// <summary>Displays RGBA pixels in LINQPad by encoding them as PNG.</summary>
static object Show(byte[] rgba, int width, int height) =>
	Util.Image(PngWriter.Encode(rgba, width, height, new PngWriteOptions { CompressionLevel = CompressionLevel.Fastest }));
