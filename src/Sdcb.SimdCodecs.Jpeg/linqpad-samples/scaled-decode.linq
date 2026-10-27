<Query Kind="Program">
  <NuGetReference>Sdcb.SimdCodecs.Jpeg</NuGetReference>
  <Namespace>Sdcb.SimdCodecs.Jpeg</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Thumbnails without decoding the full image: JpegScale.Half / Quarter / Eighth run a reduced inverse
// DCT, so the full-resolution planes never exist. Entropy decoding still reads every coefficient, so
// the saving shows most at Quarter and Eighth. Plus decoding into a buffer you own, in RGBA, BGRA or
// RGB, without allocating on the managed heap.
async Task Main()
{
	using HttpClient http = new();
	byte[] jpeg = await http.GetByteArrayAsync("https://cv-public.sdcb.ai/simdcodecs/samples/v1/photo.jpg");

	JpegInfo info = JpegDecoder.ReadInfo(jpeg);   // header only
	info.Dump("JpegDecoder.ReadInfo");

	// 1. Every scale, timed (best of 5, after a warm-up).
	JpegScale[] scales = [JpegScale.Full, JpegScale.Half, JpegScale.Quarter, JpegScale.Eighth];
	scales.Select(scale =>
	{
		JpegImage image = JpegDecoder.Decode(jpeg, scale);
		double best = Enumerable.Range(0, 5).Min(_ =>
		{
			Stopwatch sw = Stopwatch.StartNew();
			JpegDecoder.Decode(jpeg, scale);
			return sw.Elapsed.TotalMilliseconds;
		});
		return new { Scale = scale, Size = $"{image.Width}x{image.Height}", BestMs = Math.Round(best, 2) };
	}).Dump("Scaled decode");

	// 2. A 1/8 decode is a ready-made thumbnail. Shown via a JPEG re-encode, since LINQPad displays files.
	JpegImage thumb = JpegDecoder.Decode(jpeg, JpegScale.Eighth);
	Util.Image(JpegEncoder.Encode(thumb.Pixels, thumb.Width, thumb.Height, 4)).Dump("JpegScale.Eighth");

	// 3. Decode into a caller-owned buffer. Size it from ScaledSize; reuse it for every image of that
	//    size. After the first call warms the decoder's per-thread state, this allocates nothing.
	var (w, h) = info.ScaledSize(JpegScale.Quarter);
	byte[] bgra = new byte[w * h * 4];
	JpegDecoder.DecodeTo(jpeg, bgra, stride: w * 4, JpegPixelFormat.Bgra32, JpegScale.Quarter);   // warm-up

	long before = GC.GetAllocatedBytesForCurrentThread();
	for (int i = 0; i < 10; i++)
		JpegDecoder.DecodeTo(jpeg, bgra, stride: w * 4, JpegPixelFormat.Bgra32, JpegScale.Quarter);
	long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
	new { Format = "Bgra32", Size = $"{w}x{h}", Decodes = 10, ManagedBytesAllocated = allocated }.Dump("DecodeTo into a reused buffer");

	// RGB24 with a padded stride (e.g. a bitmap whose rows are 4-byte aligned).
	int stride = (w * 3 + 3) & ~3;
	byte[] rgb = new byte[stride * h];
	JpegDecoder.DecodeTo(jpeg, rgb, stride, JpegPixelFormat.Rgb24, JpegScale.Quarter);
	new { Format = "Rgb24", Stride = stride, FirstPixel = $"#{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2}" }.Dump();
}
