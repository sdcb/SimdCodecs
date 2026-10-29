<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Gif</NuGetReference>
  <NuGetReference>Sdcb.SimdCodecs.Png</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Gif</Namespace>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
</Query>

// GIF has one global palette and only binary transparency. This sample creates a colourful RGBA
// animation, compares quantization with and without Floyd-Steinberg dithering, and decodes both outputs.
void Main()
{
	const int width = 320, height = 180, frameCount = 4;
	var frames = new GifAnimationFrame[frameCount];
	for (int frame = 0; frame < frameCount; frame++)
		frames[frame] = new GifAnimationFrame(MakeFrame(width, height, frame, frameCount), TimeSpan.FromMilliseconds(140));

	var outputs = new[]
	{
		("No dithering", new GifEncodeOptions { LoopCount = 0, Dither = false }),
		("Floyd-Steinberg dithering", new GifEncodeOptions { LoopCount = 0, Dither = true }),
	};

	foreach ((string name, GifEncodeOptions options) in outputs)
	{
		Stopwatch sw = Stopwatch.StartNew();
		byte[] gif = GifEncoder.EncodeAnimation(frames, width, height, channels: 4, options);
		double encodeMs = sw.Elapsed.TotalMilliseconds;
		GifInfo info = GifDecoder.ReadInfo(gif);
		GifImage image = GifDecoder.Decode(gif);

		new { Setting = name, Bytes = gif.Length, EncodeMs = Math.Round(encodeMs, 1), info.FrameCount, info.HasAlpha, info.LoopCount }
			.Dump(name);
		Util.Image(PngWriter.Encode(image.Rgba, image.Width, image.Height)).Dump($"{name}: first frame");
	}
}

static byte[] MakeFrame(int width, int height, int frame, int frameCount)
{
	byte[] rgba = new byte[width * height * 4];
	int cx = 48 + frame * (width - 96) / (frameCount - 1);
	int cy = height / 2 + (int)(Math.Sin(frame * 1.2) * 24);
	for (int y = 0; y < height; y++)
	{
		for (int x = 0; x < width; x++)
		{
			int i = (y * width + x) * 4;
			rgba[i] = (byte)(32 + x * 170 / (width - 1));
			rgba[i + 1] = (byte)(28 + y * 180 / (height - 1));
			rgba[i + 2] = (byte)(180 - x * 100 / (width - 1));
			rgba[i + 3] = 255;

			int dx = x - cx, dy = y - cy;
			if (dx * dx + dy * dy < 28 * 28)
				rgba[i + 3] = 0;
		}
	}
	return rgba;
}
