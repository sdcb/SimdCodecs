<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Exif</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Exif</Namespace>
  <Namespace>Sdcb.SimdCodecs.Tiff</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Camera metadata from a JPEG straight out of a Sony A7R III (downscaled, EXIF kept). FromBytes
// sniffs JPEG / TIFF / PNG; FromJpeg, FromTiff and FromPng skip the sniffing.
async Task Main()
{
	using HttpClient http = new();
	byte[] jpeg = await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/samples/v1/photo.jpg");

	ExifMetadata exif = ExifReader.FromBytes(jpeg) ?? throw new InvalidDataException("No EXIF");

	// The common fields, decoded.
	new
	{
		Camera = $"{exif.Make} {exif.Model}",
		exif.CaptureTime,
		Exposure = exif.ExposureTime is { } t ? $"{t.Numerator}/{t.Denominator} s" : null,
		Aperture = exif.FNumber is { } f ? $"f/{(double)f.Numerator / f.Denominator:0.#}" : null,
		exif.Iso,
		FocalLength = exif.FocalLength is { } fl ? $"{(double)fl.Numerator / fl.Denominator:0.#} mm" : null,
		exif.FocalLengthIn35mm,
		exif.Orientation,
		exif.Software,
		exif.Gps,
	}.Dump("ExifReader.FromBytes");

	// Everything else is in RawTags, keyed by tag number, with the TIFF type and raw bytes.
	exif.RawTags.Values.OrderBy(t => t.Tag).Select(t => new
	{
		Tag = $"0x{t.Tag:X4}",
		t.Type,
		t.Count,
		Value = t.Type == TiffFieldType.Ascii
			? Encoding.ASCII.GetString(t.Bytes).TrimEnd('\0')
			: Convert.ToHexString(t.Bytes.AsSpan(0, Math.Min(16, t.Bytes.Length))) + (t.Bytes.Length > 16 ? "…" : ""),
	}).Dump($"RawTags ({exif.RawTags.Count})");
}
