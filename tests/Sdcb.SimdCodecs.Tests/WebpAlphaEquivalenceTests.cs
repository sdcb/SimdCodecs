using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The ALPH fast paths against the general ones: colour-indexing straight to alpha bytes against
/// the ARGB colour-indexing transform followed by green extraction, and the vector green
/// extraction against the scalar loop.
/// </summary>
public sealed class WebpAlphaEquivalenceTests
{
    [Fact]
    public void ColorIndexingToAlphaMatchesArgbTransformThenGreen()
    {
        var random = new Random(0xA1F);
        // Table sizes on both sides of every packing threshold (2 / 4 / 16 entries).
        foreach (int tableSize in new[] { 1, 2, 3, 4, 5, 16, 17, 200, 256 })
        {
            int bits = tableSize <= 2 ? 3 : tableSize <= 4 ? 2 : tableSize <= 16 ? 1 : 0;
            int packed = 1 << bits;
            foreach (int width in new[] { 1, 7, 8, 9, 31, 64, 65 })
            {
                const int height = 5;
                int encodedWidth = (width + packed - 1) / packed;
                int[] table = new int[256];
                // Entries past the palette are filled too, so reading one instead of producing 0 shows.
                for (int i = 0; i < table.Length; i++)
                {
                    table[i] = random.Next() | 0x100;
                }

                int[] pixels = new int[encodedWidth * height];
                for (int i = 0; i < pixels.Length; i++)
                {
                    // Index bytes may point past the palette (those decode to 0).
                    pixels[i] = random.Next();
                }

                var transform = new Vp8LDecoder.Transform(3, width, height, bits, null, table, tableSize);
                // In place: the packed image at the start of a full-size buffer.
                int[] argb = new int[width * height];
                pixels.CopyTo(argb, 0);
                Vp8LDecoder.ApplyColorIndexing(argb, transform);
                int bitsPerIndex = 8 / packed;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int index = (((pixels[y * encodedWidth + x / packed] >> 8) & 0xff) >> (x % packed * bitsPerIndex)) &
                            ((1 << bitsPerIndex) - 1);
                        Assert.Equal(index < tableSize ? table[index] : 0, argb[y * width + x]);
                    }
                }

                byte[] expected = new byte[width * height];
                for (int i = 0; i < expected.Length; i++)
                {
                    expected[i] = (byte)(argb[i] >> 8);
                }

                byte[] actual = new byte[width * height];
                Vp8LDecoder.ExpandColorIndexingToAlpha(pixels, transform, actual);
                Assert.True(expected.AsSpan().SequenceEqual(actual), $"table {tableSize}, width {width}");
            }
        }
    }

    [Fact]
    public void VectorGreenExtractionMatchesScalar()
    {
        var random = new Random(0xA20);
        foreach (int count in new[] { 0, 1, 31, 32, 33, 64, 100, 1000 })
        {
            int[] argb = new int[count];
            for (int i = 0; i < count; i++)
            {
                argb[i] = random.Next() ^ (random.Next() << 16);
            }

            byte[] actual = new byte[count + 3];
            Vp8LDecoder.CopyAlpha(argb, actual, count);
            for (int i = 0; i < count; i++)
            {
                Assert.Equal((byte)(argb[i] >> 8), actual[i]);
            }

            Assert.All(actual.AsSpan(count).ToArray(), static b => Assert.Equal(0, b));
        }
    }
}
