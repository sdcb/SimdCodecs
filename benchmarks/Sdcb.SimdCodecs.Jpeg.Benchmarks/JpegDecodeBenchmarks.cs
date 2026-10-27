using BenchmarkDotNet.Attributes;
using Sdcb.SimdCodecs.Jpeg;

namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

/// <summary>
/// Full-frame JPEG decode through the public API. Deliberately measures the whole
/// pipeline (Huffman → dequantize → IDCT → upsample → colour convert) rather than
/// individual internal kernels: a kernel that gets faster in isolation but does not
/// move this number has not actually made decoding faster, which is a mistake this
/// repo has already made once.
///
/// <para>The subsampling axis matters because it changes the block count per output
/// pixel: 4:2:0 codes 1.5 blocks per 8×8 of output, 4:4:4 codes 3. So the pair
/// brackets how much of decode is per-block work (IDCT, entropy) versus per-pixel
/// work (upsampling, colour convert).</para>
/// </summary>
[MemoryDiagnoser]
public class JpegDecodeBenchmarks
{
    private byte[] _jpeg420 = [];
    private byte[] _jpeg444 = [];
    private byte[] _rgba = [];

    /// <summary>
    /// 2048×1536. Content is smooth gradients plus low-amplitude noise: a flat
    /// synthetic image would leave nearly every block DC-only and let the IDCT's
    /// all-AC-zero shortcut hide the real cost of the transform.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        const int w = 2048;
        const int h = 1536;
        var rgb = new byte[w * h * 3];
        var rnd = new Random(7);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                rgb[i + 0] = (byte)Math.Clamp(x * 255 / (w - 1) + rnd.Next(-8, 9), 0, 255);
                rgb[i + 1] = (byte)Math.Clamp(y * 255 / (h - 1) + rnd.Next(-8, 9), 0, 255);
                rgb[i + 2] = (byte)Math.Clamp(128 + 90 * Math.Sin(x * 0.01) * Math.Cos(y * 0.013) + rnd.Next(-8, 9), 0, 255);
            }
        }

        _jpeg420 = JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = 90, Subsampling = JpegSubsampling.Chroma420 });
        _jpeg444 = JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = 95, Subsampling = JpegSubsampling.Chroma444 });
        _rgba = new byte[w * h * 4];
    }

    [Benchmark(Baseline = true, Description = "4:2:0 full scale")]
    public (int, int) Decode420() => JpegDecoder.DecodeTo(_jpeg420, _rgba);

    [Benchmark(Description = "4:4:4 full scale")]
    public (int, int) Decode444() => JpegDecoder.DecodeTo(_jpeg444, _rgba);

    /// <summary>
    /// Scaled decode runs the reduced IDCT instead of the full 8×8 one and produces
    /// 1/64th the pixels, so it isolates how much of full-scale decode is the
    /// transform plus the per-pixel output path.
    /// </summary>
    [Benchmark(Description = "4:2:0 eighth scale")]
    public (int, int) Decode420Eighth() => JpegDecoder.DecodeTo(_jpeg420, _rgba, JpegScale.Eighth);
}
