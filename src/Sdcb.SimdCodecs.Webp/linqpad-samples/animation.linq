<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Png</NuGetReference>
  <NuGetReference>Sdcb.SimdCodecs.Webp</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
  <Namespace>Sdcb.SimdCodecs.Webp</Namespace>
  <Namespace>System.IO.Compression</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Animated WebP: a 121-frame Mandelbrot deep zoom. WebpAnimationDecoder composites frames one at a
// time into a single canvas buffer (memory stays at one frame however long the animation is), and can
// seek; WebpDecoder.DecodeAnimation decodes every frame up front instead.
async Task Main()
{
	using HttpClient http = new();
	byte[] webp = await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/samples/v1/mandelbrot-zoom.webp");

	WebpAnimationDecoder decoder = new(webp);
	new { decoder.CanvasWidth, decoder.CanvasHeight, decoder.FrameCount, decoder.LoopCount, DurationMs = decoder.Frames.Sum(f => f.DurationMs) }
		.Dump("WebpAnimationDecoder");
	decoder.Frames.Take(5).Dump("First frames: rectangle, timing, blending");

	// 1. Play it: decode the next frame into the canvas, show it, wait for its duration.
	byte[] canvas = new byte[decoder.CanvasWidth * decoder.CanvasHeight * 4];
	DumpContainer player = new();
	player.Dump("Playing");
	Stopwatch decodeTime = new();
	while (true)
	{
		decodeTime.Start();
		bool more = decoder.TryDecodeNextFrame(canvas, out WebpFrameInfo frame);
		decodeTime.Stop();
		if (!more) break;

		player.Content = Util.Image(ToPng(canvas, decoder.CanvasWidth, decoder.CanvasHeight));
		await Task.Delay(frame.DurationMs);
	}
	$"Decoded {decoder.FrameCount} frames in {decodeTime.Elapsed.TotalMilliseconds:F0} ms of decode time".Dump();

	// 2. Seek by time: jump to whatever is on screen at 10 s. Decoding restarts at the nearest
	//    independent frame before it, not at frame 0.
	int index = decoder.FrameIndexAt(10_000);
	decoder.Seek(index);
	decoder.TryDecodeNextFrame(canvas, out WebpFrameInfo at10s);
	Util.Image(ToPng(canvas, decoder.CanvasWidth, decoder.CanvasHeight)).Dump($"Frame {at10s.Index}, shown from {at10s.TimestampMs} ms");
}

static byte[] ToPng(byte[] rgba, int width, int height) =>
	PngWriter.Encode(rgba, width, height, new PngWriteOptions { CompressionLevel = CompressionLevel.Fastest });
