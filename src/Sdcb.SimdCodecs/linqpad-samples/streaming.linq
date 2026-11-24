<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs</NuGetReference>
  <Namespace>Sdcb.SimdCodecs</Namespace>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>Sdcb.SimdCodecs.Jpeg</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Streaming decode and encode: read an image from a stream that cannot seek (an HTTP response, an upload, a pipe) a band
// of rows at a time with ImageCodecs.OpenRowReaderAsync, and write the rows into another format as they arrive with
// ImageCodecs.CreateEncoder(format, stream). Neither side ever holds the whole image, so memory stays at a band of rows
// and a small input buffer whatever the image size. IsStreaming says whether a codec really works as the data flows;
// when it cannot (a progressive JPEG, an interlaced PNG, palette quantisation, ...) the same API reads or writes the
// image whole behind the scenes.
const string Url = "https://cv-public.sdcb.ai/simdcodecs/samples/v1/photo.jpg";
const int Band = 16;

async Task Main()
{
	using HttpClient http = new();
	string pngPath = Path.Combine(Path.GetTempPath(), "simdcodecs-streaming.png");

	// 1. HTTP response -> JPEG row reader -> PNG encoder -> file. The response body is forward-only; nothing here asks
	//    for its length or seeks it. The rows go from the decoder straight into the encoder, Band rows at a time.
	long t = Stopwatch.GetTimestamp();
	await using (Stream body = await http.GetStreamAsync(Url))
	await using (IImageRowReader reader = await ImageCodecs.OpenRowReaderAsync(body, PixelFormat.Rgb24))
	await using (FileStream file = File.Create(pngPath))
	await using (StreamEncoderSession session = ImageCodecs.CreateEncoder(ImageFormat.Png, file, leaveOpen: true))
	{
		StreamFrameEncoder frame = session.AddFrame(reader.Width, reader.Height, reader.Format);
		byte[] band = new byte[reader.Width * reader.Format.BytesPerPixel * Band];
		int rows;
		while ((rows = await reader.ReadRowsAsync(band, Band)) > 0)
			await frame.WriteRowsAsync(band.AsMemory(), rows);
		await session.CommitAsync();

		new
		{
			Size = $"{reader.Width}x{reader.Height}",
			DecoderStreams = reader.IsStreaming,
			EncoderStreams = session.IsStreaming,
			BandBytes = band.Length,
			WholeImageBytes = reader.Width * reader.Format.BytesPerPixel * reader.Height,
			PngBytes = file.Length,
			MsIncludingDownload = Math.Round(Stopwatch.GetElapsedTime(t).TotalMilliseconds, 1),
		}.Dump("JPEG over HTTP -> PNG file, a band of rows at a time");
	}

	// 2. The streamed rows are exactly what a whole-image decode gives: PNG is lossless, so decoding the file must give
	//    back the JPEG decoder's pixels byte for byte.
	byte[] jpeg = await http.GetByteArrayAsync(Url);
	using PixelBuffer expected = ImageCodecs.Decode(jpeg, PixelFormat.Rgb24);
	using PixelBuffer actual = ImageCodecs.Decode(File.ReadAllBytes(pngPath), PixelFormat.Rgb24);
	bool same = true;
	int rowBytes = expected.Width * 3;
	for (int y = 0; y < expected.Height && same; y++)
		same = expected.Pixels.Slice(y * expected.Stride, rowBytes).SequenceEqual(actual.Pixels.Slice(y * actual.Stride, rowBytes));
	same.Dump("Streamed PNG == ImageCodecs.Decode(jpeg), every pixel");

	// 3. Not every input or output can stream, and the code does not have to care: the same loop works, IsStreaming
	//    reports false, and the codec reads the input whole (or writes the file at Commit) instead.
	using PixelBuffer source = ImageCodecs.Decode(jpeg, PixelFormat.Rgb24);
	var cases = new List<object>();
	foreach (var (name, options) in new (string, JpegEncodeOptions)[]
	{
		("baseline JPEG", new JpegEncodeOptions { Quality = 85 }),
		("progressive JPEG", new JpegEncodeOptions { Quality = 85, Progressive = true }),
	})
	{
		byte[] encoded = ImageCodecs.Encode(source.Pixels, source.Width, source.Height, source.Format, ImageFormat.Jpeg, stride: source.Stride, options: options);
		using Stream input = new ForwardOnlyStream(new MemoryStream(encoded));
		using IImageRowReader reader = ImageCodecs.OpenRowReader(input, PixelFormat.Rgb24);
		byte[] band = new byte[reader.Width * 3 * Band];
		while (reader.ReadRows(band, Band) > 0) { }
		cases.Add(new { Input = name, reader.IsStreaming, reader.RowsRead });
	}
	foreach (ImageFormat format in new[] { ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Tiff, ImageFormat.Bmp, ImageFormat.Webp, ImageFormat.Gif })
	{
		using MemoryStream output = new();
		using StreamEncoderSession session = ImageCodecs.CreateEncoder(format, output);
		StreamFrameEncoder frame = session.AddFrame(source.Width, source.Height, source.Format);
		long beforeCommit = 0;
		for (int y = 0; y < source.Height; y += Band)
		{
			int n = Math.Min(Band, source.Height - y);
			frame.WriteRows(source.Pixels.Slice(y * source.Stride), n, source.Stride);
			if (beforeCommit == 0 && y + n >= source.Height / 2) beforeCommit = output.Length;   // bytes already out at the halfway row
		}
		session.Commit();
		cases.Add(new { Output = format.ToString(), session.IsStreaming, BytesWrittenHalfway = beforeCommit, TotalBytes = output.Length });
	}
	cases.Dump("IsStreaming: which variants and formats really stream");
}

/// <summary>A stream that can only be read forward, like a network response or a pipe.</summary>
sealed class ForwardOnlyStream(Stream inner) : Stream
{
	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
	public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
	public override int Read(Span<byte> buffer) => inner.Read(buffer);
	public override void Flush() { }
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
