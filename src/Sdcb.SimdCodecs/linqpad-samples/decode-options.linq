<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs</NuGetReference>
  <Namespace>Sdcb.SimdCodecs</Namespace>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
  <Namespace>System.Buffers</Namespace>
  <Namespace>System.IO.Compression</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Runtime.InteropServices</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Beyond "decode the whole image": metadata and the embedded preview without decoding pixels, scaled and
// region decodes, pooled memory, 16-bit and premultiplied output, and a decoding session over a stream or a
// file that walks an animation's frames. Every call here works the same for every format the facade knows.
const string Base = "https://cv-public.sdcb.ai/simdcodecs/samples/v1/";

async Task Main()
{
	using HttpClient http = new();
	byte[] jpeg = await http.GetByteArrayAsync(Base + "photo.jpg");

	// 1. The header: besides size and layout, the colour encoding, the declared resolution and whether the file
	//    embeds a preview.
	if (!SimdCodecs.TryReadInfo(jpeg, out ImageInfo info)) throw new InvalidDataException("Not a supported image");
	new { info.Width, info.Height, info.Orientation, info.ColorEncoding, info.Resolution, info.HasThumbnail }.Dump("TryReadInfo");

	// 2. The metadata blocks, raw (parse the Exif with Sdcb.SimdCodecs.Exif), without decoding a pixel.
	if (SimdCodecs.TryReadMetadata(jpeg, out ImageMetadata? metadata))
		new { ExifBytes = metadata.Exif.Length, XmpBytes = metadata.Xmp.Length, IccBytes = metadata.Icc.Length }.Dump("TryReadMetadata");

	// 3. The preview a camera embeds (Exif IFD1): a gallery thumbnail for the cost of a tiny JPEG. This photo was
	//    downscaled and lost its preview, so HasThumbnail is false; then fall back to a scaled decode (step 4).
	if (info.HasThumbnail && SimdCodecs.TryDecodeThumbnail(jpeg, out IDecodedImage? thumbnail))
		using (thumbnail)
			Show(thumbnail).Dump($"TryDecodeThumbnail: {thumbnail.Width}x{thumbnail.Height}");
	else
		"No embedded preview".Dump("TryDecodeThumbnail");

	// 4. Size negotiation, as in WIC: ask what the codec decodes to natively near the size you want, then ask for
	//    exactly that (the closest size not smaller than the request). JPEG scales in the DCT domain (1/2 .. 1/8),
	//    so a small preview costs a fraction of a full decode.
	SimdCodecs.TryGetClosestSize(jpeg, 400, 260, out int w, out int h, out int denominator);
	DecodeOptions scaled = new() { ScaleDenominator = denominator };
	new
	{
		Requested = "400x260",
		Native = $"{w}x{h} (1/{denominator})",
		FullMs = Fastest(() => SimdCodecs.TryDecode(jpeg, out IDecodedImage? i) && Dispose(i)),
		ScaledMs = Fastest(() => SimdCodecs.TryDecode(jpeg, out IDecodedImage? i, scaled) && Dispose(i)),
	}.Dump("TryGetClosestSize + DecodeOptions.ScaleDenominator");

	// 5. A region, in stored coordinates of the image at the requested scale: here a 400x300 crop at full size.
	DecodeOptions crop = new() { Region = new PixelRect(info.Width / 2 - 200, info.Height / 2 - 150, 400, 300) };
	if (SimdCodecs.TryDecode(jpeg, out IDecodedImage? cropped, crop))
		using (cropped)
			Show(cropped).Dump("DecodeOptions.Region: the centre at full resolution");

	// 6. Pooled memory: the result's pixels are rented from the pool and go back when the image is disposed,
	//    which takes the large allocation out of a decode loop.
	DecodeOptions pooled = new() { Pool = ArrayPool<byte>.Shared };
	new
	{
		AllocatedPerDecodeKB = AllocatedKB(() => SimdCodecs.TryDecode(jpeg, out IDecodedImage? i) && Dispose(i)),
		PooledPerDecodeKB = AllocatedKB(() => SimdCodecs.TryDecode(jpeg, out IDecodedImage? i, pooled) && Dispose(i)),
	}.Dump("DecodeOptions.Pool (dispose the image to return its buffer)");

	// 7. Any PixelFormat into your own buffer: 16 bits per sample from a 16-bit PNG, premultiplied BGRA (what WPF,
	//    Direct2D and most compositors want) from a WebP with a soft alpha edge.
	byte[] png16 = await http.GetByteArrayAsync(Base + "fuji-16bit.png");
	SimdCodecs.TryReadInfo(png16, out ImageInfo pngInfo);
	byte[] rgba64 = new byte[pngInfo.Width * pngInfo.Height * PixelFormat.Rgba64.BytesPerPixel];
	SimdCodecs.TryDecodeInto(png16, rgba64, 0, PixelFormat.Rgba64);
	ushort[] first = MemoryMarshal.Cast<byte, ushort>(rgba64.AsSpan(0, 8)).ToArray();
	new { pngInfo.BitDepth, FirstPixelRgba64 = string.Join(", ", first) }.Dump("PixelFormat.Rgba64: all 16 bits kept");

	byte[] webp = await http.GetByteArrayAsync(Base + "cutout-lossless.webp");
	SimdCodecs.TryReadInfo(webp, out ImageInfo webpInfo);
	int stride = webpInfo.Width * 4;
	byte[] straight = new byte[stride * webpInfo.Height], premultiplied = new byte[stride * webpInfo.Height];
	SimdCodecs.TryDecodeInto(webp, straight, stride, PixelFormat.Bgra32);
	SimdCodecs.TryDecodeInto(webp, premultiplied, stride, PixelFormat.Bgra32Premultiplied);
	int edge = FirstHalfTransparent(straight);   // a pixel on the soft edge of the mask
	new
	{
		Straight = string.Join(", ", straight.Skip(edge).Take(4)),
		Premultiplied = string.Join(", ", premultiplied.Skip(edge).Take(4)),
	}.Dump("PixelFormat.Bgra32 vs Bgra32Premultiplied, one edge pixel (B, G, R, A)");

	// 8. A decoding session (ImageDecoder): open once, from a stream (OpenAsync reads a network stream without
	//    blocking), a file (memory-mapped) or memory, then decode its frames one by one. An animation's frames are
	//    its composited canvases, with their durations.
	await using (Stream body = await http.GetStreamAsync(Base + "animated.gif"))
	using (ImageDecoder gif = await ImageDecoder.OpenAsync(body))
	{
		var frames = new List<object>();
		for (int i = 0; i < gif.FrameCount; i++)
		{
			ImageFrame frame = gif.GetFrame(i);
			using IDecodedImage image = frame.Decode();
			frames.Add(new { frame.Index, frame.Duration, Canvas = Show(image) });
		}
		new { gif.Format, gif.FrameCount, gif.LoopCount, Frames = frames }.Dump("ImageDecoder.OpenAsync over an HTTP stream");
	}

	string path = Path.Combine(Path.GetTempPath(), "simdcodecs-photo.jpg");
	File.WriteAllBytes(path, jpeg);
	using (ImageDecoder file = ImageDecoder.Open(path))
	{
		// CopyPixels: a region, at a scale, in any format, straight into a buffer -- here a quarter-size BGR crop.
		ImageFrame frame = file.GetFrame(0);
		DecodeOptions quarter = new() { ScaleDenominator = 4 };
		(int qw, int qh) = quarter.GetScaledSize(frame.Info.Width, frame.Info.Height);
		PixelRect top = new(0, 0, qw, qh / 2);
		byte[] bgr = new byte[top.Width * top.Height * 3];
		frame.CopyPixels(top, PixelFormat.Bgr24, bgr, 0, quarter);
		new { file.Format, file.FrameCount, ExifBytes = file.Metadata.Exif.Length, Region = top }
			.Dump("ImageDecoder.Open(path): memory-mapped; GetFrame(0).CopyPixels(region, Bgr24, ..., 1/4 scale)");
		Show(bgr, PixelFormat.Bgr24, top.Width, top.Height).Dump();
	}
}

static bool Dispose(IDecodedImage? image) { image?.Dispose(); return image is not null; }

/// <summary>The fastest of five runs, in ms: the first calls also JIT-compile the decoder.</summary>
static double Fastest(Func<bool> decode)
{
	double best = double.MaxValue;
	for (int i = 0; i < 5; i++)
	{
		long t = Stopwatch.GetTimestamp();
		if (!decode()) throw new InvalidDataException("Decode failed");
		best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalMilliseconds);
	}
	return Math.Round(best, 1);
}

/// <summary>Managed bytes allocated by one call, after a warm-up call (in KB).</summary>
static long AllocatedKB(Func<bool> decode)
{
	decode();
	long before = GC.GetAllocatedBytesForCurrentThread();
	if (!decode()) throw new InvalidDataException("Decode failed");
	return (GC.GetAllocatedBytesForCurrentThread() - before) / 1024;
}

/// <summary>The byte offset of the first pixel whose alpha is near one half.</summary>
static int FirstHalfTransparent(byte[] bgra)
{
	for (int i = 0; i < bgra.Length; i += 4)
		if (bgra[i + 3] is > 96 and < 160) return i;
	return 0;
}

/// <summary>Displays a decoded image in LINQPad (any layout or depth, converted to RGBA 8-bit).</summary>
static object Show(IDecodedImage image) => Show(image.ToRgba8(), PixelFormat.Rgba32, image.Width, image.Height);

static object Show(byte[] pixels, PixelFormat format, int width, int height) =>
	Util.Image(SimdCodecs.Encode(pixels, width, height, format, ImageFormat.Png,
		options: new PngWriteOptions { CompressionLevel = CompressionLevel.Fastest }));
