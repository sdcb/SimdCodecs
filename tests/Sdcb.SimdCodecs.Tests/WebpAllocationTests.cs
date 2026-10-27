using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="WebpDecoder.DecodeTo(ReadOnlySpan{byte}, Span{byte}, int)"/> of a still image into a
/// caller buffer allocates nothing on the managed heap once warm: working buffers come from
/// <see cref="System.Buffers.ArrayPool{T}"/>, everything else lives on the stack.
/// </summary>
public sealed class WebpAllocationTests
{
    [Theory]
    [InlineData("testsrc2-320x240-q75.webp")]
    [InlineData("lossy-alpha-q50.webp")]
    [InlineData("lossy-alpha-m1-best.webp")]
    [InlineData("lossless-z6.webp")]
    [InlineData("lossless-palette-4.webp")]
    [InlineData("lossless-exact-alpha.webp")]
    public void DecodeTo_AllocatesNothing(string fixture)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp", fixture));
        WebpInfo info = WebpDecoder.ReadInfo(file);
        int stride = info.Width * 4 + 16;
        byte[] rgba = new byte[(info.Height - 1) * stride + info.Width * 4];
        for (int i = 0; i < 3; i++)
        {
            WebpDecoder.DecodeTo(file, rgba);
            WebpDecoder.DecodeTo(file, rgba, stride);
        }

        // ArrayPool.Shared keeps one array per size in a thread-local slot and the rest in
        // per-core stacks other threads may drain under parallel test load; a rent that misses
        // allocates. The decoder itself allocates nothing, so the best of a few attempts must be
        // exactly zero.
        long best = long.MaxValue;
        for (int attempt = 0; attempt < 8 && best != 0; attempt++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            WebpDecoder.DecodeTo(file, rgba);
            WebpDecoder.DecodeTo(file, rgba, stride);
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, best);
    }
}
