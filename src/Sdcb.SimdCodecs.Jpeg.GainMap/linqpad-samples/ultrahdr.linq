<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs</NuGetReference>
  <Namespace>Sdcb.SimdCodecs</Namespace>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>Sdcb.SimdCodecs.Jpeg</Namespace>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
  <Namespace>System.IO.Compression</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Ultra HDR ("gain map") JPEG, both directions. A gain-map JPEG is an ordinary SDR JPEG that every
// viewer can show, plus a small second JPEG saying how much brighter each area may get on an HDR
// display. Here an HDR rendition is made from a 16-bit PNG (highlights pushed up to 4x SDR white),
// written as Ultra HDR, and read back.
async Task Main()
{
	using HttpClient http = new();
	byte[] png = await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/samples/v1/fuji-16bit.png");
	if (!SimdCodecs.TryDecode(png, out IDecodedImage? source)) throw new InvalidDataException("Decode failed");
	int w = source.Width, h = source.Height;

	// 1. An HDR rendition: linear light, 1.0 = SDR white. Snow and sky get up to 4x headroom, so the
	//    HDR differs from the SDR where it matters for a gain map: in the highlights.
	float[] encoded = source.ToFloats();   // sRGB-encoded 0..1, RGBA
	float[] hdr = new float[w * h * 3];
	for (int p = 0; p < w * h; p++)
	{
		float r = SrgbToLinear(encoded[p * 4]), g = SrgbToLinear(encoded[p * 4 + 1]), b = SrgbToLinear(encoded[p * 4 + 2]);
		float y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
		float t = Math.Clamp((y - 0.35f) / 0.6f, 0, 1);
		float boost = 1 + 3 * t * t * (3 - 2 * t);   // smoothstep from 1x to 4x
		hdr[p * 3] = r * boost;
		hdr[p * 3 + 1] = g * boost;
		hdr[p * 3 + 2] = b * boost;
	}
	byte[] sdr = source.ToRgba8();   // the SDR rendition is the image as it is

	// 2. Write: fit the gain map, encode both JPEGs, assemble the container (MPF + XMP metadata).
	GainMapComputeResult gainMap = JpegGainMap.Compute(hdr, sdr, w, h);
	byte[] sdrJpeg = JpegEncoder.Encode(sdr, w, h, 4, new JpegEncodeOptions { Quality = 92 });
	byte[] gainMapJpeg = JpegEncoder.Encode(gainMap.GainMapGray8, gainMap.Width, gainMap.Height, 1, new JpegEncodeOptions { Quality = 85 });
	byte[] ultraHdr = JpegGainMap.Assemble(sdrJpeg, gainMapJpeg, gainMap.Metadata);

	string path = Path.Combine(Path.GetTempPath(), "simdcodecs-ultrahdr.jpg");
	File.WriteAllBytes(path, ultraHdr);
	new { Path = path, KB = ultraHdr.Length / 1024, SdrKB = sdrJpeg.Length / 1024, GainMapKB = gainMapJpeg.Length / 1024, GainMapSize = $"{gainMap.Width}x{gainMap.Height}" }
		.Dump("Ultra HDR written -- open it in Chrome or Photos on an HDR display");
	gainMap.Metadata.Dump("Gain map metadata");
	Util.Image(gainMapJpeg).Dump("The gain map (bright = more HDR boost)");

	// 3. Read back. The facade treats it as a normal JPEG for 8-bit output (the SDR base, which is
	//    what an HDR-unaware viewer shows) and returns the reconstructed HDR as floats.
	SimdCodecs.TryDecode(ultraHdr, out IDecodedImage? decoded);
	decoded!.ColorEncoding.Dump("Facade: ColorEncoding of the float path");
	float[] hdrBack = decoded.ToFloats();
	new { MaxLinear = hdrBack.Where((_, i) => (i & 3) != 3).Max() }.Dump("Brightest reconstructed sample (1.0 = SDR white)");

	// 4. Display adaptation: a screen with less headroom gets a partial boost. Each preview divides by
	//    the headroom, so it shows what the display would, scaled into SDR range.
	if (!JpegGainMap.TryRead(ultraHdr, out GainMapImage? image)) throw new InvalidDataException("No gain map");
	Util.HorizontalRun(true, new[] { 1.0, 2.0, 4.0 }.Select(headroom =>
	{
		RasterImage r = image.ReconstructHdr(headroom);
		return (object)new { Headroom = headroom, Weight = Math.Round(image.Weight(headroom), 2), Preview = ShowScaled(r, headroom) };
	})).Dump("ReconstructHdr at different display headrooms");
}

static float SrgbToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

static float LinearToSrgb(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;

/// <summary>Shows linear HDR floats divided by <paramref name="headroom"/>, re-encoded to sRGB, at half size.</summary>
static object ShowScaled(RasterImage hdr, double headroom)
{
	float[] f = hdr.ToFloats();
	int w = hdr.Width / 2, h = hdr.Height / 2;
	byte[] rgba = new byte[w * h * 4];
	for (int y = 0; y < h; y++)
	for (int x = 0; x < w; x++)
	for (int c = 0; c < 4; c++)
	{
		float v = f[((y * 2) * hdr.Width + x * 2) * 4 + c];
		rgba[(y * w + x) * 4 + c] = c == 3 ? (byte)255 : (byte)Math.Clamp(LinearToSrgb(v / (float)headroom) * 255 + 0.5f, 0, 255);
	}
	return Util.Image(PngWriter.Encode(rgba, w, h, new PngWriteOptions { CompressionLevel = CompressionLevel.Fastest }));
}
