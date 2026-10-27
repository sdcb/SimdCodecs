using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Color.Icc;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Round-trip tests for <see cref="TiffReader"/>. Every test writes a TIFF
/// via <see cref="TiffWriter"/> with a representative shape (uint8 / uint16 /
/// float32 × compressed/uncompressed × single/multi-strip × single/multi-page)
/// and decodes it back through <see cref="TiffReader"/>. The reader's
/// strip-concat output is asserted byte-equal to what the caller fed the
/// writer — that's the strongest correctness check we can run without a
/// third-party decoder dependency.
///
/// SampleFormat / SMin / SMax / IccProfile are checked explicitly because
/// they're easy to drop on the floor in a tag-by-tag decoder.
/// </summary>
public sealed class TiffReaderRoundTripTests
{
    [Theory]
    [InlineData(TiffCompression.Uncompressed)]
    [InlineData(TiffCompression.Deflate)]
    [InlineData(TiffCompression.ZlibPkzip)]
    public async Task Uint16Grayscale_SingleStrip_RoundTripsThroughReader(TiffCompression compression)
    {
        const int width = 8;
        const int height = 6;
        var pixels = new byte[width * height * 2];
        for (var i = 0; i < width * height; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i * 2, 2), (ushort)(i * 1000 + 7));

        var tiff = await WriteSinglePageAsync(pixels, width, height, new TiffPageOptions
        {
            SamplesPerPixel = 1,
            BitsPerSample = 16,
            Photometric = TiffPhotometric.MinIsBlack,
            SampleFormat = TiffSampleFormat.Uint,
            Compression = compression,
        });

        var doc = TiffReader.Read(tiff);
        doc.Pages.Count.ShouldBe(1);
        var page = doc.Pages[0];
        page.Width.ShouldBe(width);
        page.Height.ShouldBe(height);
        page.SamplesPerPixel.ShouldBe(1);
        page.BitsPerSample.ShouldBe(16);
        page.Compression.ShouldBe(compression);
        page.SampleFormat.ShouldBe(TiffSampleFormat.Uint);
        page.Pixels.ShouldBe(pixels);
    }

    [Theory]
    [InlineData(TiffCompression.Deflate, 0)]   // single strip
    [InlineData(TiffCompression.Deflate, 2)]   // multi-strip — exercises per-strip Inflate
    [InlineData(TiffCompression.Deflate, 1)]   // every-row strip (extreme case)
    public async Task Uint16Rgb_MultiStrip_RoundTripsThroughReader(TiffCompression compression, int rowsPerStrip)
    {
        const int width = 6;
        const int height = 6;
        var pixels = new byte[width * height * 3 * 2];
        for (var i = 0; i < pixels.Length / 2; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i * 2, 2), (ushort)((i * 37 + 5) & 0xFFFF));

        var tiff = await WriteSinglePageAsync(pixels, width, height, new TiffPageOptions
        {
            SamplesPerPixel = 3,
            BitsPerSample = 16,
            Photometric = TiffPhotometric.Rgb,
            SampleFormat = TiffSampleFormat.Uint,
            Compression = compression,
            RowsPerStrip = rowsPerStrip,
        });

        var page = TiffReader.Read(tiff).Pages[0];
        page.Width.ShouldBe(width);
        page.Height.ShouldBe(height);
        page.SamplesPerPixel.ShouldBe(3);
        page.Pixels.ShouldBe(pixels);
    }

    [Fact]
    public async Task Float32Rgb_PreservesSampleFormatAndRangeTags()
    {
        const int width = 4;
        const int height = 4;
        var pixels = new byte[width * height * 3 * 4];
        for (var i = 0; i < pixels.Length / 4; i++)
            BinaryPrimitives.WriteSingleLittleEndian(pixels.AsSpan(i * 4, 4), i / 47f);

        var tiff = await WriteSinglePageAsync(pixels, width, height, new TiffPageOptions
        {
            SamplesPerPixel = 3,
            BitsPerSample = 32,
            Photometric = TiffPhotometric.Rgb,
            SampleFormat = TiffSampleFormat.IeeeFloat,
            SMinSampleValue = 0f,
            SMaxSampleValue = 65535f,
            Compression = TiffCompression.Deflate,
        });

        var page = TiffReader.Read(tiff).Pages[0];
        page.BitsPerSample.ShouldBe(32);
        page.SampleFormat.ShouldBe(TiffSampleFormat.IeeeFloat);
        page.SMinSampleValue.ShouldBe(0f);
        page.SMaxSampleValue.ShouldBe(65535f);
        page.Pixels.ShouldBe(pixels);

        // Zero-copy float reinterpretation (LE host).
        var floats = MemoryMarshal.Cast<byte, float>(page.Pixels.AsSpan());
        floats.Length.ShouldBe(width * height * 3);
        floats[0].ShouldBe(0f);
        floats[1].ShouldBe(1 / 47f);
    }

    [Fact]
    public async Task IccProfile_RoundTripsThroughReader()
    {
        const int width = 2;
        const int height = 2;
        var pixels = new byte[width * height * 3]; // uint8 RGB
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 17);

        var profile = IccProfiles.SRgbV4;
        var tiff = await WriteSinglePageAsync(pixels, width, height, new TiffPageOptions
        {
            SamplesPerPixel = 3,
            BitsPerSample = 8,
            Photometric = TiffPhotometric.Rgb,
            SampleFormat = TiffSampleFormat.Uint,
            Compression = TiffCompression.Uncompressed,
            IccProfile = profile,
        });

        var page = TiffReader.Read(tiff).Pages[0];
        page.IccProfile.ShouldNotBeNull();
        profile.Span.SequenceEqual(page.IccProfile).ShouldBeTrue();
        page.Pixels.ShouldBe(pixels);
    }

    [Fact]
    public async Task MultiPage_TiffChain_AllPagesDecoded()
    {
        var ct = TestContext.Current.CancellationToken;
        const int width = 4;
        const int height = 4;
        var page0 = new byte[width * height];
        var page1 = new byte[width * height];
        for (var i = 0; i < page0.Length; i++) page0[i] = (byte)(i * 7);
        for (var i = 0; i < page1.Length; i++) page1[i] = (byte)(0xFF - i * 5);

        using var ms = new MemoryStream();
        await using (var writer = TiffWriter.Create(ms))
        {
            await writer.AddPageAsync(page0, width, height, new TiffPageOptions
            {
                SamplesPerPixel = 1, BitsPerSample = 8,
                Photometric = TiffPhotometric.MinIsBlack, SampleFormat = TiffSampleFormat.Uint,
                Compression = TiffCompression.Uncompressed,
            }, ct);
            await writer.AddPageAsync(page1, width, height, new TiffPageOptions
            {
                SamplesPerPixel = 1, BitsPerSample = 8,
                Photometric = TiffPhotometric.MinIsBlack, SampleFormat = TiffSampleFormat.Uint,
                Compression = TiffCompression.Deflate,
            }, ct);
            await writer.FlushAsync(ct);
        }

        var doc = TiffReader.Read(ms.ToArray());
        doc.Pages.Count.ShouldBe(2);
        doc.Pages[0].Pixels.ShouldBe(page0);
        doc.Pages[0].Compression.ShouldBe(TiffCompression.Uncompressed);
        doc.Pages[1].Pixels.ShouldBe(page1);
        doc.Pages[1].Compression.ShouldBe(TiffCompression.Deflate);
    }

    [Fact]
    public void Read_UnknownByteOrderTag_Throws()
    {
        // Header bytes that aren't "II" or "MM" — the only two TIFF byte orders.
        var bytes = new byte[] { (byte)'X', (byte)'Y', 0, 0, 0, 0, 0, 0 };
        Should.Throw<InvalidDataException>(() => TiffReader.Read(bytes));
    }

    [Fact]
    public void Read_BadMagic_Throws()
    {
        // "II" header but the magic in bytes 2-3 isn't 42.
        var bytes = new byte[] { (byte)'I', (byte)'I', 99, 0, 8, 0, 0, 0 };
        Should.Throw<InvalidDataException>(() => TiffReader.Read(bytes));
    }

    [Fact]
    public void Read_MmTiffWith16BitPixels_SwapsToHostOrder()
    {
        // Hand-crafted MM (big-endian) TIFF: 2×2 uint16 grayscale, single strip,
        // uncompressed. TiffWriter only emits II, so this is the only way to
        // exercise the file-byte-order != host-byte-order swap path.
        ushort[] expectedPixels = [0x1234, 0x5678, 0x9ABC, 0xDEF0];
        var tiff = BuildMmGrayUint16Tiff(width: 2, height: 2, expectedPixels);

        var page = TiffReader.Read(tiff).Pages[0];
        page.BitsPerSample.ShouldBe(16);

        // After the reader's swap, pixels are in host order — on LE x64 that
        // means MemoryMarshal.Cast<byte, ushort> returns the expected values.
        var asUshort = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(page.Pixels.AsSpan());
        asUshort.ToArray().ShouldBe(expectedPixels);
    }

    /// <summary>
    /// Encode a minimal MM TIFF with one strip of 16-bit grayscale samples.
    /// Layout: 8-byte header + 5-entry IFD + nextIFD pointer + pixel data.
    /// Used only to exercise the reader's BE→host swap path.
    /// </summary>
    private static byte[] BuildMmGrayUint16Tiff(int width, int height, ushort[] pixels)
    {
        const int ifdOffset = 8;
        const int entryCount = 5;
        const int entrySize = 12;
        var dirEnd = ifdOffset + 2 + entryCount * entrySize;
        var nextIfdAt = dirEnd;
        var stripOffset = nextIfdAt + 4;
        var stripByteCount = pixels.Length * 2;

        var tiff = new byte[stripOffset + stripByteCount];

        // Header: "MM" + magic 42 (BE) + first IFD offset 8 (BE).
        tiff[0] = (byte)'M'; tiff[1] = (byte)'M';
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4, 4), (uint)ifdOffset);

        // IFD entry count (BE).
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(ifdOffset, 2), entryCount);

        // Helper: write one IFD entry. SHORT values are packed in the high
        // 2 bytes of the value/offset field per BE order; LONG values fill
        // all 4 bytes.
        var entryBase = ifdOffset + 2;
        WriteEntryShort(tiff, entryBase + 0 * entrySize, tag: 256 /* ImageWidth */,  value: (ushort)width);
        WriteEntryShort(tiff, entryBase + 1 * entrySize, tag: 257 /* ImageLength */, value: (ushort)height);
        WriteEntryShort(tiff, entryBase + 2 * entrySize, tag: 258 /* BitsPerSample */, value: 16);
        WriteEntryLong (tiff, entryBase + 3 * entrySize, tag: 273 /* StripOffsets */, value: (uint)stripOffset);
        WriteEntryLong (tiff, entryBase + 4 * entrySize, tag: 279 /* StripByteCounts */, value: (uint)stripByteCount);

        // NextIFD pointer = 0 (end of chain).
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(nextIfdAt, 4), 0);

        // Pixel samples in BE order.
        for (var i = 0; i < pixels.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(stripOffset + i * 2, 2), pixels[i]);

        return tiff;
    }

    private static void WriteEntryShort(byte[] tiff, int at, ushort tag, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at, 2), tag);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at + 2, 2), 3); // SHORT
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(at + 4, 4), 1); // count
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at + 8, 2), value);
        tiff[at + 10] = 0; tiff[at + 11] = 0; // padding
    }

    private static void WriteEntryLong(byte[] tiff, int at, ushort tag, uint value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at, 2), tag);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at + 2, 2), 4); // LONG
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(at + 4, 4), 1); // count
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(at + 8, 4), value);
    }

    private static async Task<byte[]> WriteSinglePageAsync(byte[] pixels, int width, int height, TiffPageOptions options)
    {
        using var ms = new MemoryStream();
        await using (var writer = TiffWriter.Create(ms))
        {
            await writer.AddPageAsync(pixels, width, height, options);
            await writer.FlushAsync();
        }
        return ms.ToArray();
    }
}
