using System.IO.Compression;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="PngWriteOptions.ParallelFragments"/>: splitting the IDAT into independently deflated
/// pieces that concatenate into one ordinary deflate stream.
/// </summary>
/// <remarks>
/// <para><b>This cannot be pinned by comparing bytes to the serial encoder</b>, which is exactly what
/// makes it worth testing carefully. A fragment can only back-reference its own data, so the file
/// legitimately differs from a single-stream one and differs again at every fragment count. The
/// contract is therefore about MEANING, not bytes: whatever the count, the pixels that come back out
/// are the same ones, and the container is a valid zlib stream rather than one our own reader happens
/// to tolerate.</para>
/// <para>The three ways this construction fails are each covered below, because each produces a file
/// that looks entirely plausible until something tries to read it: a fragment that was disposed
/// rather than flushed carries a FINAL block and truncates the image at the first join; a missing
/// terminating block leaves the stream unfinished; and a mis-combined Adler-32 passes our own reader
/// (which does not check it) while a stricter decoder rejects the file.</para>
/// </remarks>
public sealed class PngWriterParallelDeflateTests
{
    // Big enough that the fragment floor does not collapse the split, and awkward enough that a
    // band-boundary error cannot land on a round number.
    private const int Width = 419;
    private const int Height = 733;

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    public void EveryFragmentCountDecodesToTheSamePixelsAsOneStream_8Bit(int fragments)
    {
        var rgb = Pixels8();

        var serial = PngReader.Decode(PngWriter.EncodeRgb8(rgb, Width, Height, new PngWriteOptions()));
        var split = PngReader.Decode(PngWriter.EncodeRgb8(rgb, Width, Height,
            new PngWriteOptions { ParallelFragments = fragments }));

        split.Width.ShouldBe(serial.Width);
        split.Height.ShouldBe(serial.Height);
        split.ColorType.ShouldBe(serial.ColorType);
        split.Pixels.ShouldBe(serial.Pixels);
        split.Pixels.ShouldBe(rgb);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void EveryFragmentCountDecodesToTheSamePixelsAsOneStream_16Bit(int fragments)
    {
        var rgba = Pixels16();

        var serial = PngReader.Decode(PngWriter.EncodeRgba16(rgba, Width, Height, new PngWriteOptions()));
        var split = PngReader.Decode(PngWriter.EncodeRgba16(rgba, Width, Height,
            new PngWriteOptions { ParallelFragments = fragments }));

        split.Pixels.ShouldBe(serial.Pixels);
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.Optimal)]
    [InlineData(CompressionLevel.SmallestSize)]
    public void TheSplitSurvivesEveryCompressionLevel(CompressionLevel level)
    {
        // NoCompression is the interesting one: its blocks are STORED, and a stored-block stream
        // still has to end its fragments on a byte boundary for the concatenation to hold.
        var rgb = Pixels8();

        var serial = PngWriter.EncodeRgb8(rgb, Width, Height, new PngWriteOptions { CompressionLevel = level });
        var split = PngWriter.EncodeRgb8(rgb, Width, Height,
            new PngWriteOptions { CompressionLevel = level, ParallelFragments = 4 });

        PngReader.Decode(split).Pixels.ShouldBe(PngReader.Decode(serial).Pixels);
    }

    /// <summary>
    /// The IDAT must be a well-formed zlib stream by the BCL's reckoning, not merely by ours.
    /// </summary>
    /// <remarks>
    /// <see cref="PngReader"/> inflates through <see cref="ZLibStream"/> already, so this is not a
    /// wholly independent check of the deflate structure -- but it IS the check that the trailing
    /// Adler-32 is right, because inflating to the end validates it and a decoder that stopped early
    /// would never reach it. A wrongly combined checksum is otherwise completely silent.
    /// </remarks>
    [Fact]
    public void TheFragmentedStreamIsAValidZlibStreamRightToItsChecksum()
    {
        var rgb = Pixels8();
        var png = PngWriter.EncodeRgb8(rgb, Width, Height, new PngWriteOptions { ParallelFragments = 4 });

        var idat = ExtractIdat(png);
        using var src = new MemoryStream(idat);
        using var inflate = new ZLibStream(src, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);

        // Filter byte plus a filtered scanline, per row. Anything short means a fragment terminated
        // the stream early; anything long means a stray block got in.
        raw.Length.ShouldBe((long)Height * ((Width * 3) + 1));
    }

    [Fact]
    public void TheSameFragmentCountAlwaysProducesTheSameBytes()
    {
        // Determinism is what makes the option safe to pin a golden file against. Thread scheduling
        // must not reach the output: the fragments are assembled in index order, never in the order
        // they finish.
        var rgb = Pixels8();
        var options = new PngWriteOptions { ParallelFragments = 4 };

        var first = PngWriter.EncodeRgb8(rgb, Width, Height, options);
        for (var repeat = 0; repeat < 8; repeat++)
        {
            PngWriter.EncodeRgb8(rgb, Width, Height, options).ShouldBe(first);
        }
    }

    [Fact]
    public void AskingForMoreFragmentsThanTheImageCanCarryIsReducedRatherThanRefused()
    {
        // Two rows of a tiny image cannot be eight fragments. The encoder must quietly write fewer
        // rather than emit empty ones or divide by zero.
        ReadOnlySpan<byte> rgb = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

        var png = PngWriter.EncodeRgb8(rgb, 2, 2, new PngWriteOptions { ParallelFragments = 8 });

        PngReader.Decode(png).Pixels.ShouldBe(rgb.ToArray());
    }

    [Fact]
    public void OneFragmentIsByteForByteTheSingleStreamEncoder()
    {
        // The default must remain the old path exactly, not a one-fragment trip through the new one:
        // the two produce different (both valid) files, and every existing golden expects the former.
        var rgb = Pixels8();

        PngWriter.EncodeRgb8(rgb, Width, Height, new PngWriteOptions { ParallelFragments = 1 })
            .ShouldBe(PngWriter.EncodeRgb8(rgb, Width, Height, new PngWriteOptions()));
    }

    [Fact]
    public void SplittingCostsAlmostNothingInSize()
    {
        // A fragment cannot reference the one before it, so some redundancy goes unexploited at each
        // join. The point of this assertion is the ORDER of that cost: fractions of a percent, not
        // the kind of regression that would make the option a bad trade.
        var rgb = Pixels8();

        var serial = PngWriter.EncodeRgb8(rgb, Width, Height, new PngWriteOptions()).Length;
        var split = PngWriter.EncodeRgb8(rgb, Width, Height, new PngWriteOptions { ParallelFragments = 8 }).Length;

        ((split - serial) / (double)serial).ShouldBeLessThan(0.02,
            $"{serial} bytes in one stream, {split} in eight fragments");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void StoredRowsAreUnfiltered(int fragments)
    {
        // Stored blocks compress nothing, so the filters buy nothing and the encoder skips choosing:
        // the inflated IDAT is every row's filter byte 0 followed by its samples as they are.
        var rgb = Pixels8();
        var png = PngWriter.EncodeRgb8(rgb, Width, Height,
            new PngWriteOptions { CompressionLevel = CompressionLevel.NoCompression, ParallelFragments = fragments });

        using var inflate = new ZLibStream(new MemoryStream(ExtractIdat(png)), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);

        var expected = new byte[Height * (Width * 3 + 1)];
        for (var y = 0; y < Height; y++)
            rgb.AsSpan(y * Width * 3, Width * 3).CopyTo(expected.AsSpan(y * (Width * 3 + 1) + 1));
        raw.ToArray().ShouldBe(expected);
        PngReader.Decode(png).Pixels.ShouldBe(rgb);
    }

    /// <summary>The IDAT chunk's payload, which for our writer is the whole zlib stream.</summary>
    private static byte[] ExtractIdat(byte[] png)
    {
        var at = 8;   // past the signature
        while (at + 8 <= png.Length)
        {
            var length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            if (type == "IDAT")
            {
                return png[(at + 8)..(at + 8 + length)];
            }

            at += 12 + length;   // length + type + data + crc
        }

        throw new InvalidOperationException("no IDAT chunk");
    }

    /// <summary>Structured enough that several filters get chosen, noisy enough that none is perfect.</summary>
    private static byte[] Pixels8()
    {
        var rgb = new byte[Width * Height * 3];
        for (int p = 0, s = 0; p < Width * Height; p++, s += 3)
        {
            var x = p % Width;
            var y = p / Width;
            rgb[s] = (byte)((x * 7) + (y * 3));
            rgb[s + 1] = (byte)((x * 3) ^ (y * 11));
            rgb[s + 2] = (byte)(200 - x + (y * 5));
        }

        return rgb;
    }

    private static ushort[] Pixels16()
    {
        var rgba = new ushort[Width * Height * 4];
        for (int p = 0, s = 0; p < Width * Height; p++, s += 4)
        {
            var x = p % Width;
            var y = p / Width;
            rgba[s] = (ushort)((x * 613) + (y * 271));
            rgba[s + 1] = (ushort)((x * 1031) ^ (y * 4099));
            rgba[s + 2] = (ushort)(50021 - (x * 13) + (y * 97));
            rgba[s + 3] = ushort.MaxValue;
        }

        return rgba;
    }
}
