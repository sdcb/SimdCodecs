<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Exif</NuGetReference>
  <NuGetReference>Sdcb.SimdCodecs.Jpeg</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Exif</Namespace>
  <Namespace>Sdcb.SimdCodecs.Jpeg</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// JPEG encoding: quality, chroma subsampling, optimised Huffman tables, progressive mode, and
// EXIF / XMP metadata. Each encode is decoded again to measure what it cost in quality (PSNR).
async Task Main()
{
	using HttpClient http = new();
	byte[] source = await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/samples/v1/photo.jpg");
	// Decoded at half size: re-encoding a JPEG at its own quality and full size lands on nearly the same
	// coefficients again, which would make every PSNR below look better than it is.
	JpegImage image = JpegDecoder.Decode(source, JpegScale.Half);   // RGBA, 4 bytes per pixel
	int w = image.Width, h = image.Height;

	// 1. The knobs, side by side. Optimised and progressive streams decode to exactly the same pixels
	//    as baseline at the same quality -- they only change the file size.
	(string Name, JpegEncodeOptions Options)[] settings =
	[
		("q50", new JpegEncodeOptions { Quality = 50 }),
		("q75", new JpegEncodeOptions { Quality = 75 }),
		("q90 (default)", new JpegEncodeOptions()),
		("q90 4:4:4", new JpegEncodeOptions { Subsampling = JpegSubsampling.Chroma444 }),
		("q90 optimised Huffman", new JpegEncodeOptions { OptimizeHuffman = true }),
		("q90 progressive", new JpegEncodeOptions { Progressive = true }),
		("q95", new JpegEncodeOptions { Quality = 95 }),
	];

	settings.Select(s =>
	{
		Stopwatch sw = Stopwatch.StartNew();
		byte[] jpeg = JpegEncoder.Encode(image.Pixels, w, h, channels: 4, s.Options);
		double ms = sw.Elapsed.TotalMilliseconds;
		return new
		{
			Setting = s.Name,
			KB = Math.Round(jpeg.Length / 1024.0, 1),
			EncodeMs = Math.Round(ms, 1),
			PsnrDb = Math.Round(Psnr(image.Pixels, JpegDecoder.Decode(jpeg).Pixels), 2),
		};
	}).Dump($"Re-encoding a {w}x{h} photo");

	// 2. What quality looks like: the same 400x300 crop at q20 and q90.
	byte[] crop = Crop(image.Pixels, w, 300, 150, 400, 300);
	Util.HorizontalRun(true,
		Util.Image(JpegEncoder.Encode(crop, 400, 300, 4, new JpegEncodeOptions { Quality = 20 })),
		Util.Image(JpegEncoder.Encode(crop, 400, 300, 4, new JpegEncodeOptions { Quality = 90 }))).Dump("q20 vs q90");

	// 3. Metadata: an EXIF blob (built by Sdcb.SimdCodecs.Exif) and an XMP packet travel in APP1 segments.
	byte[] tagged = JpegEncoder.Encode(image.Pixels, w, h, 4, new JpegEncodeOptions
	{
		Exif = ExifWriter.Build(new ExifWriteData(
			Software: "Sdcb.SimdCodecs",
			Artist: "LINQPad sample",
			DateTime: DateTime.Now,
			UserComment: "UserComment may hold Unicode: 你好")),
		Xmp = new JpegXmp { CreatorTool = "Sdcb.SimdCodecs", Description = "Re-encoded in a LINQPad sample" },
	});
	ExifReader.FromJpeg(tagged).Dump("EXIF read back from the new file");
	JpegSegmentScanner.Scan(tagged).Where(s => s.Marker is >= 0xE0 and <= 0xEF)
		.Select(s => new { Marker = $"APP{s.Marker - 0xE0}", s.PayloadLength, Signature = Encoding.ASCII.GetString(tagged, s.PayloadOffset, Math.Min(29, s.PayloadLength)).Split('\0')[0] })
		.Dump("APP segments");

	// Grey input is written as a single component.
	byte[] gray = new byte[w * h];
	for (int i = 0; i < gray.Length; i++)
		gray[i] = (byte)((image.Pixels[i * 4] * 77 + image.Pixels[i * 4 + 1] * 150 + image.Pixels[i * 4 + 2] * 29) >> 8);
	byte[] grayJpeg = JpegEncoder.Encode(gray, w, h, channels: 1);
	new { KB = grayJpeg.Length / 1024, Components = JpegDecoder.ReadInfo(grayJpeg).Components }.Dump("Greyscale JPEG");
}

static double Psnr(byte[] a, byte[] b)
{
	double sum = 0;
	long n = 0;
	for (int i = 0; i < a.Length; i++)
	{
		if ((i & 3) == 3) continue;   // alpha
		double d = a[i] - b[i];
		sum += d * d;
		n++;
	}
	return 10 * Math.Log10(255.0 * 255.0 / (sum / n));
}

static byte[] Crop(byte[] rgba, int width, int x, int y, int cw, int ch)
{
	byte[] result = new byte[cw * ch * 4];
	for (int row = 0; row < ch; row++)
		rgba.AsSpan(((y + row) * width + x) * 4, cw * 4).CopyTo(result.AsSpan(row * cw * 4));
	return result;
}
