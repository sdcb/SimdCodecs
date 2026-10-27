using System.Buffers.Binary;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// TIFF LZW decode. The two things an implementation gets subtly wrong are both covered here, because
/// both decode PLAUSIBLY at first and only diverge later: codes are packed MSB-first (GIF's LZW packs
/// them LSB-first), and the code width grows on the <i>early change</i> schedule -- one code before
/// each power of two. Get either wrong and a strip decodes correctly for a few hundred codes and then
/// turns to noise, so a fixture must be long enough to cross a width boundary or it proves nothing.
///
/// The encoder lives in the test because <see cref="TiffWriter"/> does not emit LZW, so there is no
/// round-trip through the library that could reach this code. It mirrors the decoder's width schedule
/// deliberately -- that makes this a round-trip test, which cannot by itself prove the schedule matches
/// libtiff. What pins that is the real-file check: a Photoshop-written LZW TIFF decodes to a clean
/// image (verified against an independent implementation), and it would not if the schedule were off.
/// </summary>
public sealed class TiffLzwTests
{
    [Theory]
    [InlineData(64)]      // stays inside 9-bit codes
    [InlineData(4096)]    // crosses 511 into 10-bit, and 1023 into 11-bit
    [InlineData(20000)]   // crosses 2047 into 12-bit
    public void Decode_RoundTripsData_AcrossEveryCodeWidthBoundary(int length)
    {
        var original = Pseudorandom(length);
        var encoded = LzwEncode(original);

        var decoded = new byte[original.Length];
        var written = TiffLzw.Decode(encoded, decoded);

        written.ShouldBe(original.Length);
        decoded.ShouldBe(original);
    }

    [Fact]
    public void Decode_StopsAtTheDestinationLength_WithoutOverrunning()
    {
        // A strip that decodes to more than its share is clamped, matching the uncompressed path,
        // rather than throwing: a trailing partial row is a truncated file, not a corrupt one.
        var original = Pseudorandom(2000);
        var encoded = LzwEncode(original);

        var half = new byte[original.Length / 2];
        var written = TiffLzw.Decode(encoded, half);

        written.ShouldBe(half.Length);
        half.ShouldBe(original[..half.Length]);
    }

    [Fact]
    public void Decode_HandlesTheKwKwKCase()
    {
        // A run of one repeated byte is what produces a code the encoder defined with the very code it
        // is emitting -- the decoder has not built that entry yet and must synthesise it. Easy to get
        // wrong, and it only shows up on repeated data.
        var original = new byte[512];
        original.AsSpan().Fill(0xAB);
        var encoded = LzwEncode(original);

        var decoded = new byte[original.Length];
        TiffLzw.Decode(encoded, decoded).ShouldBe(original.Length);
        decoded.ShouldBe(original);
    }

    [Fact]
    public void Decode_EmptyInput_WritesNothing()
    {
        TiffLzw.Decode([], new byte[16]).ShouldBe(0);
    }

    /// <summary>Deterministic, and varied enough to force steady dictionary growth.</summary>
    private static byte[] Pseudorandom(int length)
    {
        var data = new byte[length];
        var state = 0x12345678u;
        for (var i = 0; i < length; i++)
        {
            state = state * 1664525u + 1013904223u;
            // Deliberately not uniform: a little repetition keeps it compressible, which is what makes
            // the dictionary grow instead of the encoder emitting mostly literals.
            data[i] = (byte)((state >> 16) % 40);
        }
        return data;
    }

    /// <summary>TIFF LZW encoder: MSB-first, early-change width growth, Clear first and EOI last.</summary>
    private static byte[] LzwEncode(ReadOnlySpan<byte> data)
    {
        const int clearCode = 256;
        const int eoiCode = 257;

        var table = new Dictionary<(int Prefix, byte Suffix), int>();
        var nextCode = 258;
        var width = 9;
        var bits = new List<byte>();
        var acc = 0;
        var accBits = 0;

        void Emit(int code)
        {
            acc = (acc << width) | code;
            accBits += width;
            while (accBits >= 8)
            {
                bits.Add((byte)((acc >> (accBits - 8)) & 0xFF));
                accBits -= 8;
            }
        }

        static int WidthFor(int next) => next >= 2047 ? 12 : next >= 1023 ? 11 : next >= 511 ? 10 : 9;

        Emit(clearCode);
        if (data.Length > 0)
        {
            var omega = (int)data[0];
            for (var i = 1; i < data.Length; i++)
            {
                var k = data[i];
                if (table.TryGetValue((omega, k), out var combined))
                {
                    omega = combined;
                    continue;
                }

                Emit(omega);
                // The width for the NEXT code is derived from the entry count the DECODER will have
                // when it reads it -- i.e. the pre-increment value. The decoder is always one entry
                // behind, because it cannot add an entry until it has seen the following code, so an
                // encoder that widens off its own post-increment count runs one code ahead and the two
                // desynchronise at the first boundary. Verified against a real Photoshop-written LZW
                // TIFF: the decoder's schedule reads it whole, and shifting by one collapses it after
                // 620 of 2,160,000 bytes.
                var widthBasis = nextCode;
                if (nextCode < 4096)
                {
                    table[(omega, k)] = nextCode++;
                    width = WidthFor(widthBasis);
                }
                else
                {
                    // Table full: the stream must be reset, exactly as the decoder expects.
                    Emit(clearCode);
                    table.Clear();
                    nextCode = 258;
                    width = 9;
                }
                omega = k;
            }
            Emit(omega);
        }
        Emit(eoiCode);

        if (accBits > 0)
        {
            bits.Add((byte)((acc << (8 - accBits)) & 0xFF));
        }
        return [.. bits];
    }
}
