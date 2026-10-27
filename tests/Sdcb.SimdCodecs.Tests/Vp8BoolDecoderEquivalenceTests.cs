using Sdcb.SimdCodecs.Webp;
using Shouldly;
using Xunit;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The production boolean decoder reads its input seven bytes at a time. These tests check it
/// against an independent boolean <i>encoder</i> and a bit-serial decoder, both written from the
/// RFC 6386 §7 description (16-bit window, one input bit per renormalisation step, zero bits past
/// the end), over random symbols, probabilities and stream lengths that end inside every refill
/// phase.
/// </summary>
public sealed class Vp8BoolDecoderEquivalenceTests
{
    [Fact]
    public void DecodesWhatAnIndependentEncoderWrote()
    {
        var random = new Random(6386);
        for (int trial = 0; trial < 400; trial++)
        {
            int count = trial < 64 ? trial : random.Next(0, 4000);
            int[] probabilities = new int[count];
            int[] bits = new int[count];
            for (int i = 0; i < count; i++)
            {
                probabilities[i] = (trial % 4) switch
                {
                    0 => random.Next(1, 4),
                    1 => random.Next(252, 256),
                    _ => random.Next(1, 256),
                };
                // Mostly the likely symbol, as real streams are, with some surprises.
                bool likelyZero = probabilities[i] >= 128;
                bool surprise = random.Next(8) == 0;
                bits[i] = likelyZero ^ surprise ? 0 : 1;
            }

            byte[] stream = Encode(probabilities, bits);
            Production(stream, probabilities).ShouldBe(bits, $"production, trial {trial}");
            Reference(stream, probabilities).Bits.ShouldBe(bits, $"reference, trial {trial}");
        }
    }

    [Fact]
    public void MatchesBitSerialReferenceOnArbitraryBytes()
    {
        // Arbitrary bytes are not always a valid stream (the window can start at or above the
        // range); the comparison runs up to the first read where that happens, past the end of the
        // data included.
        var random = new Random(9);
        for (int trial = 0; trial < 600; trial++)
        {
            int length = trial < 40 ? trial : random.Next(0, 300);
            byte[] data = new byte[length];
            random.NextBytes(data);
            if (trial % 7 == 0)
            {
                data.AsSpan().Fill(0);
            }

            int reads = length * 10 + 64;
            int[] probabilities = new int[reads];
            for (int i = 0; i < reads; i++)
            {
                probabilities[i] = random.Next(1, 256);
            }

            (int[] expected, int valid) = Reference(data, probabilities);
            int[] actual = Production(data, probabilities);
            actual.AsSpan(0, valid).ToArray().ShouldBe(expected.AsSpan(0, valid).ToArray(), $"trial {trial}, length {length}");
        }
    }

    [Fact]
    public void LiteralsMatchBitSerialReference()
    {
        var random = new Random(7);
        int[] probabilities = Enumerable.Repeat(128, 2000).ToArray();
        int[] bits = Enumerable.Range(0, 2000).Select(_ => random.Next(2)).ToArray();
        byte[] stream = Encode(probabilities, bits);
        var production = new Vp8BoolDecoder(stream);
        int position = 0;
        for (int i = 0; position + 16 <= bits.Length; i++)
        {
            int count = 1 + i % 16;
            int expected = 0;
            for (int b = 0; b < count; b++)
            {
                expected = (expected << 1) | bits[position++];
            }

            production.ReadLiteral(count).ShouldBe(expected, $"literal {i}");
        }
    }

    private static int[] Production(byte[] data, int[] probabilities)
    {
        var decoder = new Vp8BoolDecoder(data);
        int[] bits = new int[probabilities.Length];
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = decoder.ReadBit(probabilities[i]);
        }

        return bits;
    }

    private static (int[] Bits, int Valid) Reference(byte[] data, int[] probabilities)
    {
        int position = 2;
        int bitIndex = 0;
        uint value = (uint)((data.Length > 0 ? data[0] : 0) << 8 | (data.Length > 1 ? data[1] : 0));
        uint range = 255;
        int[] bits = new int[probabilities.Length];
        int valid = probabilities.Length;
        for (int i = 0; i < probabilities.Length; i++)
        {
            if (value >= range << 8 && valid == probabilities.Length)
            {
                valid = i;
            }

            uint split = 1 + (((range - 1) * (uint)probabilities[i]) >> 8);
            if (value >= split << 8)
            {
                bits[i] = 1;
                range -= split;
                value -= split << 8;
            }
            else
            {
                range = split;
            }

            while (range < 128)
            {
                range <<= 1;
                int input = position < data.Length ? (data[position] >> (7 - bitIndex)) & 1 : 0;
                if (++bitIndex == 8)
                {
                    bitIndex = 0;
                    position++;
                }

                value = ((value << 1) | (uint)input) & 0xffff;
            }
        }

        return (bits, valid);
    }

    /// <summary>
    /// Arithmetic encoder: <c>low</c> is the bottom of the current interval; a byte is emitted
    /// every 8 renormalisation shifts (the first after 24), and a carry out of it is propagated
    /// into the bytes already written. Flushed by coding 32 even-odds zeros, which pins every
    /// pending bit.
    /// </summary>
    private static byte[] Encode(int[] probabilities, int[] bits)
    {
        var output = new List<byte>();
        ulong low = 0;
        uint range = 255;
        int pending = 24;

        void Put(int probability, int bit)
        {
            uint split = 1 + (((range - 1) * (uint)probability) >> 8);
            if (bit != 0)
            {
                low += split;
                range -= split;
            }
            else
            {
                range = split;
            }

            while (range < 128)
            {
                range <<= 1;
                low <<= 1;
                if (--pending == 0)
                {
                    // Bits 24..31 are the finished byte; bit 32 is a carry into the bytes
                    // already written.
                    if ((low >> 32) != 0)
                    {
                        int index = output.Count - 1;
                        while (output[index] == 0xff)
                        {
                            output[index--] = 0;
                        }

                        output[index]++;
                    }

                    output.Add((byte)(low >> 24));
                    low &= 0x00ff_ffff;
                    pending = 8;
                }
            }
        }

        for (int i = 0; i < bits.Length; i++)
        {
            Put(probabilities[i], bits[i]);
        }

        for (int i = 0; i < 32; i++)
        {
            Put(128, 0);
        }

        return output.ToArray();
    }
}
