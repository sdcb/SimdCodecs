<Query Kind="Program">
  <NuGetReference>OpenCvSharp5</NuGetReference>
  <NuGetReference>OpenCvSharp5.runtime.osx.arm64</NuGetReference>
  <NuGetReference>OpenCvSharp5.runtime.osx.x64</NuGetReference>
  <NuGetReference>OpenCvSharp5.runtime.win</NuGetReference>
  <NuGetReference>Sdcb.SimdCodecs</NuGetReference>
  <Namespace>OpenCvSharp</Namespace>
  <Namespace>Sdcb.SimdCodecs</Namespace>
  <Namespace>Sdcb.SimdCodecs.Abstractions</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Sdcb.SimdCodecs decodes, OpenCV processes: the decoder writes straight into a Mat's memory in the layout
// OpenCV wants (BGR, or grey for an algorithm that needs only luma), upright by the file's EXIF orientation.
// Handy for formats OpenCV's own imdecode does not read, such as JBIG2, and for phone photos it would show sideways.
async Task Main()
{
	using HttpClient http = new();
	byte[] data = await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/samples/v1/photo.jpg");

	using Mat bgr = Decode(data, PixelFormat.Bgr24, MatType.CV_8UC3);
	new { bgr.Width, bgr.Height, Type = bgr.Type().ToString() }.Dump("Mat");

	// Canny needs grey: decode to Gray8 instead of converting the BGR Mat.
	using Mat gray = Decode(data, PixelFormat.Gray8, MatType.CV_8UC1);
	using Mat edges = new();
	Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);
	Cv2.Canny(gray, edges, 50, 150);

	// Paint the edges red over the photo.
	bgr.SetTo(new Scalar(0, 0, 255), edges);
	Cv2.ImEncode(".png", bgr, out byte[] png);
	Util.Image(png).Dump("Canny edges over the photo");
}

/// <summary>
/// Decodes any format Sdcb.SimdCodecs knows into a new Mat: <paramref name="format"/> must have the Mat type's
/// layout (Bgr24 for CV_8UC3, Bgra32 for CV_8UC4, Gray8 for CV_8UC1).
/// </summary>
static Mat Decode(byte[] data, PixelFormat format, MatType type)
{
	DecodeOptions upright = new() { ApplyOrientation = true };
	if (!ImageCodecs.TryReadInfo(data, out ImageInfo info)) throw new InvalidDataException("Not a supported image");
	(int width, int height) = upright.GetOutputSize(info);

	Mat mat = new(height, width, type);
	// A Mat's rows are Step() bytes apart; for a freshly allocated one that is Width * channels, one contiguous span.
	if (!ImageCodecs.TryDecodeInto(data, mat.AsSpan<byte>(), (int)mat.Step(), format, upright))
	{
		mat.Dispose();
		throw new InvalidDataException("Decode failed");
	}
	return mat;
}
