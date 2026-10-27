<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Png</NuGetReference>
  <NuGetReference>Sdcb.SimdCodecs.Webp</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
  <Namespace>Sdcb.SimdCodecs.Webp</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// WebP stills: lossy (VP8) and lossless with an alpha channel (VP8L). ReadInfo looks at the container
// only; Decode returns RGBA plus any ICC / EXIF / XMP chunks; DecodeTo writes into your own buffer.
async Task Main()
{
	using HttpClient http = new();
	const string baseUrl = "https://cv-public.sdcb.ai/simdcodecs/samples/v1/";

	foreach (string name in new[] { "fuji.webp", "cutout-lossless.webp" })
	{
		byte[] webp = await http.GetByteArrayAsync(baseUrl + name);
		WebpInfo info = WebpDecoder.ReadInfo(webp);
		info.Dump(name);

		Stopwatch sw = Stopwatch.StartNew();
		WebpImage image = WebpDecoder.Decode(webp);
		double ms = sw.Elapsed.TotalMilliseconds;
		new { DecodeMs = Math.Round(ms, 1), IccBytes = image.IccProfile?.Length, ExifBytes = image.Exif?.Length, XmpBytes = image.Xmp?.Length }.Dump();

		// PNG keeps the alpha channel, so the lossless cut-out shows its soft rounded edge.
		Util.Image(PngWriter.Encode(image.Rgba, image.Width, image.Height)).Dump();
	}

	// Decoding into a reused buffer (stride 0 = tightly packed).
	byte[] lossy = await http.GetByteArrayAsync(baseUrl + "fuji.webp");
	WebpInfo lossyInfo = WebpDecoder.ReadInfo(lossy);
	byte[] rgba = new byte[lossyInfo.Width * lossyInfo.Height * 4];
	WebpDecoder.DecodeTo(lossy, rgba);
	$"DecodeTo filled {rgba.Length:N0} bytes".Dump();
}
