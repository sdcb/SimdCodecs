<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Png</NuGetReference>
  <NuGetReference>Sdcb.SimdCodecs.Tiff</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
  <Namespace>Sdcb.SimdCodecs.Tiff</Namespace>
  <Namespace>System.IO.Compression</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// A multi-page TIFF whose pages differ in everything: RGB with Deflate, greyscale with LZW, and a
// 1-bit scanned page in CCITT Group 4. ReadLayout describes a page without decoding it; ReadPage
// decodes one page; Read decodes them all.
async Task Main()
{
	using HttpClient http = new();
	byte[] tiff = await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/samples/v1/multipage.tif");

	// 1. Walk the pages cheaply: only the directories are read.
	var layouts = new List<TiffPageLayout>();
	for (int page = 0; ; page++)
	{
		try { layouts.Add(TiffReader.ReadLayout(tiff, page)); }
		catch (ArgumentOutOfRangeException) { break; }
	}
	layouts.Select(l => new
	{
		Page = l.PageIndex,
		Size = $"{l.Width}x{l.Height}",
		l.SamplesPerPixel,
		StoredBits = l.StoredBitsPerSample,
		l.BitsPerSample,
		l.Compression,
		l.Predictor,
		Strips = l.SegmentCount,
	}).Dump("TiffReader.ReadLayout");

	// 2. Decode each page. A 1-bit page arrives expanded to 8 bits (0 / 255); Photometric says
	//    whether 0 is black (MinIsBlack) or white (MinIsWhite, usual for fax).
	foreach (TiffPageLayout layout in layouts)
	{
		Stopwatch sw = Stopwatch.StartNew();
		TiffPage page = TiffReader.ReadPage(tiff, layout.PageIndex);
		double ms = sw.Elapsed.TotalMilliseconds;

		byte[] png = page.SamplesPerPixel == 3
			? PngWriter.EncodeRgb8(page.Pixels, page.Width, page.Height, Fast)
			: PngWriter.EncodeGray8(page.Photometric == TiffPhotometric.MinIsWhite ? Invert(page.Pixels) : page.Pixels, page.Width, page.Height, Fast);
		Util.Image(png).Dump($"Page {layout.PageIndex}: {page.Compression}, {page.Photometric}, decoded in {ms:F1} ms");
	}

	// 3. Or everything at once.
	TiffDocument doc = TiffReader.Read(tiff);
	doc.Pages.Select(p => new { p.Width, p.Height, p.BitsPerSample, p.Photometric, p.Compression, PixelBytes = p.Pixels.Length })
		.Dump("TiffReader.Read");
}

static readonly PngWriteOptions Fast = new() { CompressionLevel = CompressionLevel.Fastest };

static byte[] Invert(byte[] pixels) => pixels.Select(b => (byte)~b).ToArray();
