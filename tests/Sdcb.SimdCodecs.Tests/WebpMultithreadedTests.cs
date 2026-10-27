using System.Buffers.Binary;
using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The opt-in multithreaded decode (ALPH plane on a second thread) against the single-threaded one,
/// and the pass that writes alpha into rows emitted before the plane was ready.
/// </summary>
public sealed class WebpMultithreadedTests
{
    private static readonly WebpDecodeOptions Multithreaded = new() { Multithreaded = true };

    [Theory]
    [InlineData("lossy-alpha-q50.webp")]
    [InlineData("lossy-alpha-m0-best.webp")]
    [InlineData("lossy-alpha-m1-fast.webp")]
    [InlineData("lossy-alpha-m1-none.webp")]
    [InlineData("testsrc2-320x240-q75.webp")]
    [InlineData("lossless-z6.webp")]
    public void MultithreadedDecodeMatchesSingleThreaded(string fixture)
    {
        byte[] file = ReadFixture(fixture);
        WebpInfo info = WebpDecoder.ReadInfo(file);
        byte[] expected = new byte[info.Width * info.Height * 4];
        WebpDecoder.DecodeTo(file, expected);

        // Repeated: whether the plane arrives before, during or after the frame varies by run.
        for (int run = 0; run < 8; run++)
        {
            byte[] actual = new byte[expected.Length];
            WebpDecoder.DecodeTo(file, actual, 0, Multithreaded);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"{fixture}, run {run}");
        }

        int stride = info.Width * 4 + 8;
        byte[] strided = new byte[(info.Height - 1) * stride + info.Width * 4];
        Array.Fill(strided, (byte)0x5A);
        WebpDecoder.DecodeTo(file, strided, stride, Multithreaded);
        for (int row = 0; row < info.Height; row++)
        {
            Assert.True(strided.AsSpan(row * stride, info.Width * 4)
                .SequenceEqual(expected.AsSpan(row * info.Width * 4, info.Width * 4)), $"{fixture}, strided row {row}");
        }
    }

    [Fact]
    public void MalformedAlphaFailsWithInvalidDataExceptionInBothModes()
    {
        byte[] file = ReadFixture("lossy-alpha-q50.webp");
        int alph = FindChunk(file, "ALPH");
        // Compression method 3 is reserved (RFC 9649 §2.7.1.6 ALPH: 0 raw, 1 lossless).
        file[alph + 8] = (byte)((file[alph + 8] & ~3) | 3);
        WebpInfo info = WebpDecoder.ReadInfo(file);
        byte[] rgba = new byte[info.Width * info.Height * 4];

        Assert.Throws<InvalidDataException>(() => WebpDecoder.DecodeTo(file, rgba));
        Assert.Throws<InvalidDataException>(() => WebpDecoder.DecodeTo(file, rgba, 0, Multithreaded));
        Assert.False(WebpDecoder.TryDecodeTo(file, rgba, 0, Multithreaded));
    }

    [Fact]
    public void StoreAlphaRowsWritesOnlyTheAlphaBytesOfTheFirstRows()
    {
        var random = new Random(0xA7);
        foreach (int width in new[] { 1, 3, 4, 5, 17, 33 })
        {
            const int height = 5;
            const int rows = 3;
            int stride = width * 4 + 4;
            byte[] output = new byte[(height - 1) * stride + width * 4];
            random.NextBytes(output);
            byte[] alpha = new byte[width * height];
            random.NextBytes(alpha);
            byte[] expected = (byte[])output.Clone();
            for (int row = 0; row < rows; row++)
            {
                for (int x = 0; x < width; x++)
                {
                    expected[row * stride + x * 4 + 3] = alpha[row * width + x];
                }
            }

            WebpDecoder.StoreAlphaRows(output, stride, width, alpha, rows);
            Assert.Equal(expected, output);
        }
    }

    private static int FindChunk(byte[] file, string fourCc)
    {
        int offset = 12;
        while (offset + 8 <= file.Length)
        {
            if (System.Text.Encoding.ASCII.GetString(file, offset, 4) == fourCc)
            {
                return offset;
            }

            int size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset + 4));
            offset += 8 + size + (size & 1);
        }

        throw new InvalidOperationException($"No {fourCc} chunk.");
    }

    private static byte[] ReadFixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp", name));
}
