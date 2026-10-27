using System.Buffers.Binary;
using System.IO.Compression;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Regression tests for TIFF Predictor (tag 317) on the read path.
///
/// The bug these pin: <see cref="TiffReader"/> inflated Deflate strips but never inverted the
/// predictor, so any TIFF written with <see cref="TiffPredictor.HorizontalDifferencing"/> decoded to
/// the horizontal DERIVATIVE of the image. That is the silent kind of wrong -- correct dimensions,
/// correct channel count, a full-size buffer, no exception -- and it renders as an embossed relief
/// map, or as pure noise once the source has any high-frequency content. Predictor 2 is what
/// essentially every writer turns on alongside ZIP compression, so this hit real files from
/// Photoshop, PixInsight and GraXpert rather than an exotic corner.
///
/// The fixtures are hand-built rather than produced by <see cref="TiffWriter"/> on purpose: the
/// writer does not emit predictors, so a round-trip through it could not reach this code at all.
/// </summary>
public sealed class TiffPredictorTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void HorizontalDifferencing_IsInverted_SoPixelsSurviveTheRoundTrip(int bitsPerSample)
    {
        const int width = 7;
        const int height = 5;
        const int spp = 3;

        var original = BuildRamp(width, height, spp, bitsPerSample);
        var tiff = BuildDeflateTiff(original, width, height, spp, bitsPerSample, TiffPredictor.HorizontalDifferencing);

        var page = TiffReader.Read(tiff).Pages.ShouldHaveSingleItem();

        page.Width.ShouldBe(width);
        page.Height.ShouldBe(height);
        page.SamplesPerPixel.ShouldBe(spp);
        page.BitsPerSample.ShouldBe(bitsPerSample);
        // The whole point: the decoded samples are the ORIGINAL image, not the differences that
        // were actually stored in the file.
        page.Pixels.ShouldBe(original);
    }

    [Fact]
    public void APredictorTheReaderCannotInvert_Throws_RatherThanDecodingPastIt()
    {
        // Predictor 3 is a genuinely different transform (byte-plane split, then byte-wise
        // differencing), so treating it as predictor 2 -- or ignoring it -- would produce wrong
        // pixels with no signal to the caller. Refusing is the only honest option until it is
        // implemented, and this test exists so that "not implemented" cannot silently become
        // "silently wrong" again.
        var original = BuildRamp(4, 3, 1, 32);
        var tiff = BuildDeflateTiff(original, 4, 3, 1, 32, TiffPredictor.FloatingPoint);

        var ex = Should.Throw<NotSupportedException>(() => TiffReader.Read(tiff));
        ex.Message.ShouldContain("Predictor");
    }

    [Fact]
    public void AnExplicitPredictorOfNone_LeavesSamplesAlone()
    {
        // Guards the other direction: the inversion must not run when the tag says None, which is
        // also the spec default for the far more common file that omits the tag entirely.
        var original = BuildRamp(7, 5, 3, 16);
        var tiff = BuildDeflateTiff(original, 7, 5, 3, 16, TiffPredictor.None);

        var page = TiffReader.Read(tiff).Pages.ShouldHaveSingleItem();
        page.Pixels.ShouldBe(original);
    }

    /// <summary>
    /// A deterministic ramp with a deliberate mid-row jump, so the stored differences are large
    /// enough to wrap the sample width. Wrapping is the case worth covering: the encoder computed
    /// the differences modulo the sample width, so only unchecked addition reverses them, and a
    /// widened accumulator would quietly produce different pixels.
    /// </summary>
    private static byte[] BuildRamp(int width, int height, int spp, int bitsPerSample)
    {
        var bytesPerSample = bitsPerSample / 8;
        var pixels = new byte[width * height * spp * bytesPerSample];
        for (var i = 0; i < width * height * spp; i++)
        {
            // Big steps plus a channel offset; every third pixel drops hard to force a wrap.
            var v = (uint)(i * 9157 + (i % spp) * 21_713);
            if (i % 3 == 0) v ^= 0xF0F0u;
            switch (bitsPerSample)
            {
                case 8:
                    pixels[i] = (byte)v;
                    break;
                case 16:
                    BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i * 2, 2), (ushort)v);
                    break;
                default:
                    BinaryPrimitives.WriteUInt32LittleEndian(pixels.AsSpan(i * 4, 4), v);
                    break;
            }
        }
        return pixels;
    }

    /// <summary>Apply horizontal differencing, reading from the original so each difference is
    /// against an undifferenced neighbour.</summary>
    private static byte[] Difference(byte[] pixels, int width, int spp, int bitsPerSample)
    {
        var stride = width * spp;
        var outp = (byte[])pixels.Clone();
        var rows = pixels.Length / (stride * (bitsPerSample / 8));
        for (var y = 0; y < rows; y++)
        {
            for (var i = stride - 1; i >= spp; i--)
            {
                var cur = y * stride + i;
                var left = cur - spp;
                switch (bitsPerSample)
                {
                    case 8:
                        outp[cur] = (byte)(pixels[cur] - pixels[left]);
                        break;
                    case 16:
                        BinaryPrimitives.WriteUInt16LittleEndian(outp.AsSpan(cur * 2, 2),
                            (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(cur * 2, 2))
                                   - BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(left * 2, 2))));
                        break;
                    default:
                        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(cur * 4, 4),
                            BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan(cur * 4, 4))
                          - BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan(left * 4, 4)));
                        break;
                }
            }
        }
        return outp;
    }

    /// <summary>
    /// Hand-build a little-endian, Deflate-compressed, one-row-per-strip TIFF. RowsPerStrip=1
    /// mirrors what real writers emit alongside ZIP compression -- and it is what made the strip
    /// loop's per-strip allocations expensive, so it is the shape worth testing.
    /// </summary>
    private static byte[] BuildDeflateTiff(byte[] pixels, int width, int height, int spp,
        int bitsPerSample, TiffPredictor predictor)
    {
        var stored = predictor == TiffPredictor.HorizontalDifferencing
            ? Difference(pixels, width, spp, bitsPerSample)
            : pixels;

        var rowBytes = width * spp * (bitsPerSample / 8);
        var strips = new byte[height][];
        for (var y = 0; y < height; y++)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionMode.Compress, leaveOpen: true))
                z.Write(stored.AsSpan(y * rowBytes, rowBytes));
            strips[y] = ms.ToArray();
        }

        // Assert the FIXTURE's own premise before drawing any conclusion about the reader: strip 0
        // must inflate back to the bytes we meant to store. Without this, a builder bug presents
        // as a reader bug, which is exactly what happened while these tests were being written.
        using (var check = new MemoryStream(strips[0]))
        using (var unz = new ZLibStream(check, CompressionMode.Decompress))
        {
            var back = new byte[rowBytes];
            var got = unz.ReadAtLeast(back, rowBytes, throwOnEndOfStream: false);
            got.ShouldBe(rowBytes, "fixture: strip 0 did not inflate to a full row");
            back.ShouldBe(stored[..rowBytes], "fixture: strip 0 inflated to the wrong bytes");
        }

        const int entryCount = 11;
        const int ifdOffset = 8;
        var ifdSize = 2 + entryCount * 12 + 4;
        // A TIFF entry whose payload fits in the four-byte value field MUST store it there; an
        // offset is only legal once it does not fit. So BitsPerSample is inline at 1-2 samples and
        // external at 3+. Getting this wrong produced a fixture whose bit depth read back as the
        // offset value (146), which looked like a reader bug.
        var bpsInline = spp * 2 <= 4;
        var bpsOffset = ifdOffset + ifdSize;
        var stripOffsetsOffset = bpsOffset + (bpsInline ? 0 : spp * 2);
        var stripCountsOffset = stripOffsetsOffset + height * 4;
        var dataOffset = stripCountsOffset + height * 4;

        var total = dataOffset + strips.Sum(s => s.Length);
        var tiff = new byte[total];

        tiff[0] = (byte)'I';
        tiff[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4, 4), ifdOffset);

        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(ifdOffset, 2), entryCount);
        var at = ifdOffset + 2;
        // Tags must ascend -- the reader does not care, but every other reader does, and a fixture
        // that only our reader accepts is a weaker fixture.
        WriteShort(tiff, ref at, TiffTag.ImageWidth, (ushort)width);
        WriteShort(tiff, ref at, TiffTag.ImageLength, (ushort)height);
        if (bpsInline)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at, 2), TiffTag.BitsPerSample);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 2, 2), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 4, 4), (uint)spp);
            for (var c = 0; c < spp; c++)
                BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 8 + c * 2, 2), (ushort)bitsPerSample);
            at += 12;
        }
        else
        {
            WriteEntry(tiff, ref at, TiffTag.BitsPerSample, type: 3, count: (uint)spp, (uint)bpsOffset);
        }
        WriteShort(tiff, ref at, TiffTag.Compression, (ushort)TiffCompression.Deflate);
        WriteShort(tiff, ref at, TiffTag.PhotometricInterp,
            (ushort)(spp >= 3 ? TiffPhotometric.Rgb : TiffPhotometric.MinIsBlack));
        WriteEntry(tiff, ref at, TiffTag.StripOffsets, type: 4, count: (uint)height, (uint)stripOffsetsOffset);
        WriteShort(tiff, ref at, TiffTag.SamplesPerPixel, (ushort)spp);
        WriteShort(tiff, ref at, TiffTag.RowsPerStrip, 1);
        WriteEntry(tiff, ref at, TiffTag.StripByteCounts, type: 4, count: (uint)height, (uint)stripCountsOffset);
        WriteShort(tiff, ref at, TiffTag.PlanarConfig, 1);
        WriteShort(tiff, ref at, TiffTag.Predictor, (ushort)predictor);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at, 4), 0);   // no next IFD

        if (!bpsInline)
        {
            for (var c = 0; c < spp; c++)
                BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(bpsOffset + c * 2, 2), (ushort)bitsPerSample);
        }

        var cursor = dataOffset;
        for (var y = 0; y < height; y++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(stripOffsetsOffset + y * 4, 4), (uint)cursor);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(stripCountsOffset + y * 4, 4), (uint)strips[y].Length);
            strips[y].CopyTo(tiff.AsSpan(cursor));
            cursor += strips[y].Length;
        }

        return tiff;
    }

    private static void WriteShort(byte[] tiff, ref int at, ushort tag, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at, 2), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 2, 2), 3);  // SHORT
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 4, 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 8, 2), value);
        at += 12;
    }

    private static void WriteEntry(byte[] tiff, ref int at, ushort tag, ushort type, uint count, uint valueOrOffset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at, 2), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 2, 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 4, 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 8, 4), valueOrOffset);
        at += 12;
    }
}
