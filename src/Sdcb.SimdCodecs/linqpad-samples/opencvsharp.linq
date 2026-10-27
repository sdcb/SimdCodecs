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

// Sdcb.SimdCodecs decodes, OpenCV processes: the decoded RGBA buffer becomes a Mat, here run through
// Canny edge detection. Handy for formats OpenCV's own imdecode does not read, such as JBIG2.
async Task Main()
{
	using HttpClient http = new();
	byte[] data = await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/samples/v1/photo.jpg");

	using Mat bgr = Decode(data);
	new { bgr.Width, bgr.Height, Type = bgr.Type().ToString() }.Dump("Mat");

	using Mat gray = new();
	using Mat edges = new();
	Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
	Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);
	Cv2.Canny(gray, edges, 50, 150);

	Cv2.ImEncode(".png", edges, out byte[] png);
	Util.Image(png).Dump("Canny edges");
}

/// <summary>Decodes any format Sdcb.SimdCodecs knows into a BGR (CV_8UC3) Mat, OpenCV's usual layout.</summary>
static Mat Decode(byte[] data)
{
	if (!SimdCodecs.TryReadInfo(data, out ImageInfo info)) throw new InvalidDataException("Not a supported image");

	using Mat rgba = new(info.Height, info.Width, MatType.CV_8UC4);
	// A freshly allocated Mat is continuous, so its memory is one Width * Height * 4 span.
	if (!SimdCodecs.TryDecodeIntoRgba8(data, rgba.AsSpan<byte>())) throw new InvalidDataException("Decode failed");

	Mat bgr = new();
	Cv2.CvtColor(rgba, bgr, ColorConversionCodes.RGBA2BGR);
	return bgr;
}
