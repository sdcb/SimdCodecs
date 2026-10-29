<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Gif</NuGetReference>
  <NuGetReference>Sdcb.SimdCodecs.Png</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Gif</Namespace>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// GIF decoding: metadata and the first frame are cheap, while an animation can be composited one
// frame at a time into a caller-owned buffer. The sample asset has transparency, disposal-to-background
// and a NETSCAPE loop count.
async Task Main()
{
	using HttpClient http = new();
	byte[] gif = await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/samples/v1/animated.gif");

	GifInfo info = GifDecoder.ReadInfo(gif);
	info.Dump("GifDecoder.ReadInfo");

	GifImage first = GifDecoder.Decode(gif);
	Util.Image(ToPng(first.Rgba, first.Width, first.Height)).Dump("First frame");

	if (!GifImageDecoder.TryOpenFrames(gif, out IImageFrameReader? reader) || reader is null)
		throw new InvalidDataException("The sample GIF could not be opened as frames.");

	using (reader)
	{
		byte[] canvas = new byte[info.Width * info.Height * 4];
		var rows = new List<object>();
		DumpContainer player = new();
		player.Dump("Playing frames");

		foreach (FrameTiming frame in reader.EnumerateFramesInto(canvas, 0, ImagePixelFormat.Rgba32))
		{
			rows.Add(new { frame.Index, DurationMs = frame.Duration?.TotalMilliseconds });
			player.Content = Util.Image(ToPng(canvas, info.Width, info.Height));
			if (frame.Duration is { } duration)
				await Task.Delay((int)Math.Min(duration.TotalMilliseconds, 250));
		}

		rows.Dump("Decoded frames (one reused RGBA buffer)");
	}
}

static byte[] ToPng(byte[] rgba, int width, int height) =>
	PngWriter.Encode(rgba, width, height, new PngWriteOptions { CompressionLevel = System.IO.Compression.CompressionLevel.Fastest });
