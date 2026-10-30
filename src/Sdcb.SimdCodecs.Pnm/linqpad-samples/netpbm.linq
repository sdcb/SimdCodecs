<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Pnm</NuGetReference>
  <NuGetReference>Sdcb.SimdCodecs.Png</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>Sdcb.SimdCodecs.Png</Namespace>
  <Namespace>Sdcb.SimdCodecs.Pnm</Namespace>
  <Namespace>System.Runtime.InteropServices</Namespace>
</Query>

// The whole Netpbm family from one RGBA image: P1 to P7, plain (ASCII) and raw, then a hand-written PPM, a 16-bit
// PGM and a floating-point PFM, each read back.
void Main()
{
	const int width = 320, height = 200;
	byte[] rgba = MakeImage(width, height);
	byte[] opaque = Opaque(rgba);

	// Colour formats must read back exactly (a PPM drops alpha); grey and 1-bit ones are lossy by nature.
	var formats = new (PnmFormat Format, byte[]? Expected)[]
	{
		(PnmFormat.PlainBitmap, null),
		(PnmFormat.PlainGraymap, null),
		(PnmFormat.PlainPixmap, opaque),
		(PnmFormat.Bitmap, null),
		(PnmFormat.Graymap, null),
		(PnmFormat.Pixmap, opaque),
		(PnmFormat.Arbitrary, rgba),
	};

	var rows = new List<object>();
	byte[]? plainPixmap = null;
	foreach (var (format, expected) in formats)
	{
		byte[] file = PnmEncoder.Encode(rgba, width, height, ImagePixelFormat.Rgba32, new PnmEncodeOptions { Format = format });
		if (format == PnmFormat.PlainPixmap) plainPixmap = file;
		PnmInfo info = PnmDecoder.ReadInfo(file);
		var decoded = new byte[width * height * 4];
		PnmDecoder.DecodeTo(file, decoded, ImagePixelFormat.Rgba32);
		rows.Add(new
		{
			Format = format,
			Magic = Encoding.ASCII.GetString(file, 0, 2),
			KB = Math.Round(file.Length / 1024.0, 1),
			info.Channels,
			info.MaxValue,
			info.TupleType,
			ReadBack = expected is null ? "lossy (grey / 1-bit)" : decoded.AsSpan().SequenceEqual(expected) ? "exact" : "DIFFERENT",
			Image = Util.Image(PngWriter.Encode(decoded, width, height)),
		});
	}
	rows.Dump("One image, every format");

	// The plain formats are text: a header, then decimal samples, no line longer than 70 characters.
	string text = Encoding.ASCII.GetString(plainPixmap!, 0, 400);
	string.Join("\n", text.Split('\n').Take(5)).Dump("The start of the P3 file");

	// Written by hand: comments, ragged whitespace, and a maximum of 15 instead of 255.
	string handWritten = """
		P3
		# A 3 x 2 image, written by hand
		3 2
		# samples run from 0 to 15
		15
		15  0  0     0 15  0     0  0 15
		15 15  0    15 15 15     0  0  0
		""";
	PnmImage small = PnmDecoder.Decode(Encoding.ASCII.GetBytes(handWritten));
	Enumerable.Range(0, small.Width * small.Height)
		.Select(i => new { X = i % small.Width, Y = i / small.Width, R = small.Pixels[i * 3], G = small.Pixels[i * 3 + 1], B = small.Pixels[i * 3 + 2] })
		.Dump($"Hand-written P3, scaled from 0..{small.Info.MaxValue} to 0..255");
	Util.Image(PngWriter.Encode(Enlarge(small, 40), small.Width * 40, small.Height * 40)).Dump("Enlarged");

	// 16 bits: a ramp over all 65536 levels, which an 8-bit file cannot hold. Samples come back in host order.
	const int rampWidth = 1024, rampHeight = 64;
	var ramp = new ushort[rampWidth * rampHeight];
	for (int i = 0; i < ramp.Length; i++) ramp[i] = (ushort)(i % rampWidth * 65535 / (rampWidth - 1));
	byte[] pgm16 = PnmEncoder.Encode(ramp, rampWidth, rampHeight, channels: 1);
	PnmImage ramp16 = PnmDecoder.Decode(pgm16);
	new
	{
		ramp16.Info.Format,
		ramp16.Info.MaxValue,
		ramp16.SampleFormat,
		Exact = MemoryMarshal.Cast<byte, ushort>(ramp16.Pixels).SequenceEqual(ramp),
	}.Dump("16-bit PGM");

	// PFM: 32-bit floats, linear light, here a glow that peaks at 4 (well above the 1.0 of display white).
	var hdr = new float[width * height * 3];
	for (int y = 0; y < height; y++)
	{
		for (int x = 0; x < width; x++)
		{
			double dx = (x - width / 2.0) / width, dy = (y - height / 2.0) / height;
			float glow = (float)(4 * Math.Exp(-(dx * dx + dy * dy) * 20));
			int i = (y * width + x) * 3;
			hdr[i] = glow;
			hdr[i + 1] = glow * 0.6f;
			hdr[i + 2] = 0.05f + glow * 0.2f;
		}
	}
	byte[] pfm = PnmEncoder.EncodePfm(hdr, width, height, channels: 3);
	PnmImage floats = PnmDecoder.Decode(pfm);
	new
	{
		floats.Info.Format,
		floats.Info.Scale,
		Max = MemoryMarshal.Cast<byte, float>(floats.Pixels).ToArray().Max(),
		Exact = MemoryMarshal.Cast<byte, float>(floats.Pixels).SequenceEqual(hdr),
	}.Dump("PFM (a negative scale means little-endian samples)");

	// Floats need a tone choice before they can be shown: stretch the range to 8 bits and apply the sRGB curve.
	PfmImageDecoder.TryDecode(pfm, out IDecodedImage? image);
	Util.Image(PngWriter.Encode(PixelConverter.StretchToRgba8(image!, 0, 4, srgbEncode: true), width, height)).Dump("PFM, stretched for display");
}

static byte[] Opaque(byte[] rgba)
{
	byte[] copy = (byte[])rgba.Clone();
	for (int i = 3; i < copy.Length; i += 4) copy[i] = 255;
	return copy;
}

// Each pixel of an 8-bit RGB image as a scale × scale block of RGBA.
static byte[] Enlarge(PnmImage image, int scale)
{
	int width = image.Width * scale, height = image.Height * scale;
	byte[] rgba = new byte[width * height * 4];
	for (int y = 0; y < height; y++)
	{
		for (int x = 0; x < width; x++)
		{
			int s = (y / scale * image.Width + x / scale) * 3, d = (y * width + x) * 4;
			rgba[d] = image.Pixels[s];
			rgba[d + 1] = image.Pixels[s + 1];
			rgba[d + 2] = image.Pixels[s + 2];
			rgba[d + 3] = 255;
		}
	}
	return rgba;
}

static byte[] MakeImage(int width, int height)
{
	byte[] rgba = new byte[width * height * 4];
	for (int y = 0; y < height; y++)
	{
		for (int x = 0; x < width; x++)
		{
			int i = (y * width + x) * 4;
			double t = Math.Sin(x * 0.05) * Math.Cos(y * 0.04);
			rgba[i] = (byte)(128 + 127 * t);
			rgba[i + 1] = (byte)(x * 255 / (width - 1));
			rgba[i + 2] = (byte)(y * 255 / (height - 1));
			int dx = x - width / 2, dy = y - height / 2;
			rgba[i + 3] = (byte)Math.Clamp(255 - Math.Sqrt(dx * dx + dy * dy) * 255 / (height / 2), 0, 255);
		}
	}
	return rgba;
}
