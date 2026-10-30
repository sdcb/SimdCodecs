<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Ico</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>Sdcb.SimdCodecs.Ico</Namespace>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
</Query>

// A multi-size icon (16, 32 and 48 as BMP, 256 as PNG) and a cursor with a hotspot: written, then every image
// read back with its size, depth and encoding.
void Main()
{
	int[] sizes = [16, 32, 48, 256];
	IcoFrame[] frames = [.. sizes.Select(s => new IcoFrame(MakeIcon(s), s, s))];
	byte[] ico = IcoEncoder.Encode(frames);
	File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "simdcodecs-sample.ico"), ico);

	IcoInfo info = IcoDecoder.ReadInfo(ico);
	new { info.Kind, Images = info.Entries.Count, Bytes = ico.Length, BestIndex = info.Best }.Dump("Icon");
	info.Entries.Select((e, i) =>
	{
		IcoImage image = IcoDecoder.Decode(ico, i);
		return new { e.Width, e.Height, e.BitsPerPixel, e.IsPng, Preview = Util.Image(PngWriter.Encode(image.Rgba, image.Width, image.Height)) };
	}).Dump("Images");

	byte[] cur = IcoEncoder.EncodeCursor([new IcoFrame(MakeIcon(32), 32, 32, HotspotX: 3, HotspotY: 3)]);
	IcoDecoder.ReadInfo(cur).Entries.Dump("Cursor");
}

// A round badge: a gradient disc with a soft edge on a transparent background.
static byte[] MakeIcon(int size)
{
	byte[] rgba = new byte[size * size * 4];
	double r = size / 2.0;
	for (int y = 0; y < size; y++)
	{
		for (int x = 0; x < size; x++)
		{
			int i = (y * size + x) * 4;
			double d = Math.Sqrt((x + 0.5 - r) * (x + 0.5 - r) + (y + 0.5 - r) * (y + 0.5 - r));
			rgba[i] = (byte)(40 + 200 * x / size);
			rgba[i + 1] = (byte)(90 + 120 * y / size);
			rgba[i + 2] = 220;
			rgba[i + 3] = (byte)Math.Clamp((r - d) * 255, 0, 255);
		}
	}
	return rgba;
}
