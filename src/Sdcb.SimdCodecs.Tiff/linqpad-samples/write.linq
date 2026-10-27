<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Tiff</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Tiff</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Writing TIFF: pages from raw pixels (strips or tiles, Deflate or uncompressed, 8/16-bit), and a PNG
// carried over without decoding its pixels. The result is read back and compared.
async Task Main()
{
	using HttpClient http = new();
	const string baseUrl = "https://cv-public.sdcb.ai/simdcodecs/samples/v1/";
	TiffPage rgb8 = TiffReader.ReadPage(await http.GetByteArrayAsync(baseUrl + "fuji-deflate.tif"));
	TiffPage rgb16 = TiffReader.ReadPage(await http.GetByteArrayAsync(baseUrl + "fuji-16bit-tiled.tif"));
	byte[] png = await http.GetByteArrayAsync(baseUrl + "fuji.png");

	string path = Path.Combine(Path.GetTempPath(), "simdcodecs-written.tif");
	await using (TiffWriter writer = TiffWriter.Create(path))
	{
		// Page 0: 8-bit RGB in Deflate strips (the defaults), with some tags.
		await writer.AddPageAsync(rgb8.Pixels, rgb8.Width, rgb8.Height, new TiffPageOptions
		{
			Software = "Sdcb.SimdCodecs LINQPad sample",
			DpiX = 300,
			DpiY = 300,
		});

		// Page 1: 16-bit RGB in 256x256 Deflate tiles. Samples are host-order ushorts, as the reader returns them.
		await writer.AddPageAsync(rgb16.Pixels, rgb16.Width, rgb16.Height, new TiffPageOptions
		{
			BitsPerSample = 16,
			Layout = TiffLayout.Tiled,
		});

		// Page 2: a PNG's deflate stream re-wrapped as a TIFF page.
		await writer.AddPngPageAsync(png);
	}

	byte[] written = File.ReadAllBytes(path);
	new { Path = path, KB = written.Length / 1024 }.Dump("Written");

	TiffDocument doc = TiffReader.Read(written);
	doc.Pages.Select((p, i) => new
	{
		Page = i,
		Size = $"{p.Width}x{p.Height}",
		p.BitsPerSample,
		p.Compression,
		Tiles = p.TileWidth > 0 ? $"{p.TileWidth}x{p.TileHeight}" : "strips",
		SameAsSource = i switch
		{
			0 => p.Pixels.AsSpan().SequenceEqual(rgb8.Pixels),
			1 => p.Pixels.AsSpan().SequenceEqual(rgb16.Pixels),
			_ => p.Pixels.AsSpan().SequenceEqual(rgb8.Pixels),   // fuji.png and fuji-deflate.tif hold the same image
		},
	}).Dump("Read back");
}
