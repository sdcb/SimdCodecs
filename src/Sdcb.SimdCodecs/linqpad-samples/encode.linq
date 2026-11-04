<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs</NuGetReference>
  <Namespace>Sdcb.SimdCodecs</Namespace>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>Sdcb.SimdCodecs.Gif</Namespace>
  <Namespace>Sdcb.SimdCodecs.Jpeg</Namespace>
  <Namespace>Sdcb.SimdCodecs.Webp</Namespace>
  <Namespace>System.Buffers</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Every encoder through one API: SimdCodecs.Encode for a decoded image or raw pixels in any PixelFormat, and an
// encoder session (CreateEncoder) that takes rows band by band and several frames for an animation.
async Task Main()
{
	using HttpClient http = new();
	byte[] jpeg = await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/samples/v1/photo.jpg");
	if (!SimdCodecs.TryDecode(jpeg, out IDecodedImage? decoded)) throw new InvalidDataException("Decode failed");
	using IDecodedImage photo = decoded;

	// 1. Transcode a decoded image to every built-in encoder. Its Exif, XMP and ICC go along wherever the format
	//    and encoder store them (PNG, JPEG, WebP, TIFF here), so the capture time, GPS position and colour profile survive.
	//    The options type picks a codec's settings (none: its defaults); GetClosestInputFormat says what the encoder
	//    stores a 16-bit RGBA image as, before any precision is lost.
	(ImageFormat Format, EncodeOptions? Options)[] targets =
	[
		(ImageFormat.Png, null),
		(ImageFormat.Jpeg, new JpegEncodeOptions { Quality = 85 }),
		(ImageFormat.Webp, new WebpLossyEncodeOptions { Quality = 80 }),
		(ImageFormat.Webp, null),   // lossless
		(ImageFormat.Tiff, null),
		(ImageFormat.Gif, new GifEncodeOptions { Dither = true }),
		(ImageFormat.Bmp, null),
		(ImageFormat.Qoi, null),
		(ImageFormat.Pnm, null),
	];
	var rows = new List<object>();
	foreach (var (format, options) in targets)
	{
		using MemoryStream file = new();
		long t = Stopwatch.GetTimestamp();
		SimdCodecs.Encode(photo, format, file, options);
		double ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
		byte[] bytes = file.ToArray();
		SimdCodecs.TryReadMetadata(bytes, out ImageMetadata? kept);
		rows.Add(new
		{
			Format = format.ToString(),
			Options = options?.GetType().Name ?? "(defaults)",
			KB = bytes.Length / 1024,
			EncodeMs = Math.Round(ms, 1),
			Rgba64StoredAs = SimdCodecs.GetClosestInputFormat(format, PixelFormat.Rgba64).ToString(),
			ExifKept = kept?.Exif.Length > 0,
			ReadsBack = SimdCodecs.TryReadInfo(bytes, out ImageInfo back) && back.Width == photo.Width,
		});
	}
	rows.Dump($"SimdCodecs.Encode(image, format, stream): {photo.Width}x{photo.Height} photo, Exif {photo.Metadata.Exif.Length} bytes");

	// 2. Raw pixels in any PixelFormat, rows any stride apart: here straight-alpha BGRA with an alpha ramp,
	//    as a UI surface holds it. The encoder converts; there is no need for an RGBA copy first.
	const int w = 512, h = 256;
	byte[] bgra = new byte[w * h * 4];
	for (int y = 0; y < h; y++)
	for (int x = 0; x < w; x++)
	{
		int i = (y * w + x) * 4;
		bgra[i] = (byte)(x / 2);          // B
		bgra[i + 1] = (byte)y;            // G
		bgra[i + 2] = (byte)(255 - x / 2); // R
		bgra[i + 3] = (byte)(255 * x / (w - 1));
	}
	byte[] png = SimdCodecs.Encode(bgra, w, h, PixelFormat.Bgra32, ImageFormat.Png);
	Util.Image(png).Dump($"SimdCodecs.Encode(Bgra32 pixels, ImageFormat.Png): {png.Length / 1024} KB");

	// 3. A session takes the image in bands, so a producer (a renderer, a scanner, a tiled source) never holds
	//    the whole frame: AddFrame declares it, WriteRows streams it, Commit finishes the file.
	ArrayBufferWriter<byte> output = new();
	using (IImageEncoderSession session = SimdCodecs.CreateEncoder(ImageFormat.Jpeg, output, new JpegEncodeOptions { Quality = 90 }))
	{
		const int bw = 1024, bh = 768, band = 64;
		IImageFrameEncoder frame = session.AddFrame(bw, bh, PixelFormat.Rgb24);
		byte[] rows64 = new byte[bw * band * 3];
		for (int y0 = 0; y0 < bh; y0 += band)
		{
			int n = Math.Min(band, bh - y0);
			for (int y = 0; y < n; y++)
			for (int x = 0; x < bw; x++)
			{
				int i = (y * bw + x) * 3;
				bool check = ((x >> 6) + ((y0 + y) >> 6)) % 2 == 0;
				rows64[i] = (byte)(check ? 230 : 40);
				rows64[i + 1] = (byte)(x * 255 / bw);
				rows64[i + 2] = (byte)((y0 + y) * 255 / bh);
			}
			frame.WriteRows(rows64, n);
		}
		session.Commit();
		$"Rows written: {frame.RowsWritten}, in bands of {band}".Dump("CreateEncoder: a JPEG streamed band by band");
	}
	Util.Image(output.WrittenSpan.ToArray()).Dump($"{output.WrittenCount / 1024} KB");

	// 4. Several frames make an animation (GIF, WebP): each AddFrame carries its duration.
	foreach (ImageFormat format in new[] { ImageFormat.Gif, ImageFormat.Webp })
	{
		ArrayBufferWriter<byte> anim = new();
		int maxFrames;
		using (IImageEncoderSession session = SimdCodecs.CreateEncoder(format, anim))
		{
			maxFrames = session.MaxFrames;   // 1 for a still-only format
			const int size = 160, frames = 12;
			for (int f = 0; f < frames; f++)
				session.AddFrame(size, size, PixelFormat.Rgba32, duration: TimeSpan.FromMilliseconds(80))
					.WriteRows(Spinner(size, f, frames), size);
			session.Commit();
		}
		byte[] bytes = anim.WrittenSpan.ToArray();
		using ImageDecoder back = ImageDecoder.Open(bytes);
		new { format, MaxFrames = maxFrames, KB = Math.Round(bytes.Length / 1024.0, 1), back.FrameCount, back.LoopCount, Preview = Util.Image(bytes) }
			.Dump($"CreateEncoder({format}): 12 frames, read back with ImageDecoder");
	}
}

/// <summary>One frame of a rotating dot on a transparent background.</summary>
static byte[] Spinner(int size, int frame, int frames)
{
	byte[] rgba = new byte[size * size * 4];
	double angle = 2 * Math.PI * frame / frames, cx = size / 2 + size * 0.3 * Math.Cos(angle), cy = size / 2 + size * 0.3 * Math.Sin(angle);
	for (int y = 0; y < size; y++)
	for (int x = 0; x < size; x++)
	{
		double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
		if (d > size * 0.12) continue;
		int i = (y * size + x) * 4;
		rgba[i] = (byte)(127 + 127 * Math.Cos(angle));
		rgba[i + 1] = 90;
		rgba[i + 2] = (byte)(127 + 127 * Math.Sin(angle));
		rgba[i + 3] = 255;
	}
	return rgba;
}
