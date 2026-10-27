using System.Buffers.Binary;
using System.IO.Compression;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// End-to-end smoke tests for <see cref="TiffWriter"/> that exercise the strip-decode path
/// readers will take. Catches regressions like the <see cref="PixelSource"/> double-compress
/// bug — previously the writer's IsPreCompressed=false + PixelSource self-compress combo
/// would double-deflate strip data, making the output unreadable by libtiff/Magick.NET.
/// The in-test mini-reader is intentionally minimal: it only handles the subset of TIFF
/// our writer emits (little-endian, strip layout, Uncompressed or Deflate, contig planar).
/// </summary>
public sealed class TiffWriterRoundTripTests
{
    [Theory]
    [InlineData(TiffCompression.Uncompressed)]
    [InlineData(TiffCompression.Deflate)]
    [InlineData(TiffCompression.ZlibPkzip)]
    public async Task Uint16Grayscale_SingleStrip_RoundTrips(TiffCompression compression)
    {
        const int width = 8;
        const int height = 6;
        var pixels = new byte[width * height * 2];
        for (var i = 0; i < width * height; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i * 2, 2), (ushort)(i * 1000 + 7));

        var bytes = await WriteSinglePageAsync(pixels, width, height, new TiffPageOptions
        {
            SamplesPerPixel = 1,
            BitsPerSample = 16,
            Photometric = TiffPhotometric.MinIsBlack,
            SampleFormat = TiffSampleFormat.Uint,
            Compression = compression,
        });

        var decoded = DecodeFirstPage(bytes);
        decoded.Width.ShouldBe(width);
        decoded.Height.ShouldBe(height);
        decoded.Pixels.ShouldBe(pixels);
    }

    [Theory]
    [InlineData(TiffCompression.Uncompressed, 0)]   // single strip
    [InlineData(TiffCompression.Deflate, 0)]        // single strip
    [InlineData(TiffCompression.Deflate, 2)]        // multi-strip — exercises per-strip Deflate
    [InlineData(TiffCompression.Deflate, 1)]        // every-row strip (extreme case)
    public async Task Uint16Rgb_RoundTrips(TiffCompression compression, int rowsPerStrip)
    {
        const int width = 6;
        const int height = 6;
        var pixels = new byte[width * height * 3 * 2];
        for (var i = 0; i < pixels.Length / 2; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i * 2, 2), (ushort)((i * 37 + 5) & 0xFFFF));

        var bytes = await WriteSinglePageAsync(pixels, width, height, new TiffPageOptions
        {
            SamplesPerPixel = 3,
            BitsPerSample = 16,
            Photometric = TiffPhotometric.Rgb,
            SampleFormat = TiffSampleFormat.Uint,
            Compression = compression,
            RowsPerStrip = rowsPerStrip,
        });

        var decoded = DecodeFirstPage(bytes);
        decoded.Width.ShouldBe(width);
        decoded.Height.ShouldBe(height);
        decoded.Pixels.ShouldBe(pixels);
    }

    [Fact]
    public async Task Float32Rgb_Deflate_RoundTripsWithSampleFormatAndRangeTags()
    {
        const int width = 4;
        const int height = 4;
        var pixels = new byte[width * height * 3 * 4];
        for (var i = 0; i < pixels.Length / 4; i++)
            BinaryPrimitives.WriteSingleLittleEndian(pixels.AsSpan(i * 4, 4), i / 47f);

        var bytes = await WriteSinglePageAsync(pixels, width, height, new TiffPageOptions
        {
            SamplesPerPixel = 3,
            BitsPerSample = 32,
            Photometric = TiffPhotometric.Rgb,
            SampleFormat = TiffSampleFormat.IeeeFloat,
            SMinSampleValue = 0f,
            SMaxSampleValue = 65535f,
            Compression = TiffCompression.Deflate,
        });

        var decoded = DecodeFirstPage(bytes);
        decoded.Pixels.ShouldBe(pixels);
        // Verify the new range tags are emitted with one value per sample.
        ReadFloatArrayTag(bytes, TiffTag.SMinSampleValue).ShouldBe([0f, 0f, 0f]);
        ReadFloatArrayTag(bytes, TiffTag.SMaxSampleValue).ShouldBe([65535f, 65535f, 65535f]);
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

    private record DecodedPage(int Width, int Height, byte[] Pixels);

    /// <summary>
    /// Decodes the first IFD of a little-endian TIFF emitted by <see cref="TiffWriter"/>.
    /// Concatenates all strips (in order) into a single contig pixel buffer. Handles
    /// Uncompressed and Deflate/ZlibPkzip; throws on anything else (we don't write LZW).
    /// </summary>
    private static DecodedPage DecodeFirstPage(ReadOnlySpan<byte> tiff)
    {
        tiff[0].ShouldBe((byte)'I');
        tiff[1].ShouldBe((byte)'I');
        BinaryPrimitives.ReadUInt16LittleEndian(tiff[2..]).ShouldBe((ushort)42);

        var ifdOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff[4..]);
        var width = (int)ReadScalarLongOrShort(tiff, ifdOffset, TiffTag.ImageWidth);
        var height = (int)ReadScalarLongOrShort(tiff, ifdOffset, TiffTag.ImageLength);
        var compression = (TiffCompression)ReadScalarLongOrShort(tiff, ifdOffset, TiffTag.Compression);
        var stripOffsets = ReadLongOrShortArrayTag(tiff, ifdOffset, TiffTag.StripOffsets)
            ?? throw new InvalidDataException("missing StripOffsets");
        var stripByteCounts = ReadLongOrShortArrayTag(tiff, ifdOffset, TiffTag.StripByteCounts)
            ?? throw new InvalidDataException("missing StripByteCounts");
        stripOffsets.Length.ShouldBe(stripByteCounts.Length);

        using var pixelStream = new MemoryStream();
        for (var i = 0; i < stripOffsets.Length; i++)
        {
            var stripBytes = tiff.Slice((int)stripOffsets[i], (int)stripByteCounts[i]).ToArray();
            switch (compression)
            {
                case TiffCompression.Uncompressed:
                    pixelStream.Write(stripBytes);
                    break;
                case TiffCompression.Deflate:
                case TiffCompression.ZlibPkzip:
                    using (var src = new MemoryStream(stripBytes))
                    using (var z = new ZLibStream(src, CompressionMode.Decompress))
                        z.CopyTo(pixelStream);
                    break;
                default:
                    throw new NotSupportedException($"compression {compression} not handled");
            }
        }
        return new DecodedPage(width, height, pixelStream.ToArray());
    }

    private static uint ReadScalarLongOrShort(ReadOnlySpan<byte> tiff, int ifdOffset, ushort tag)
    {
        var entryStart = FindEntry(tiff, ifdOffset, tag)
            ?? throw new InvalidDataException($"tag {tag} missing");
        var fieldType = BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(entryStart + 2, 2));
        return fieldType switch
        {
            3 => BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(entryStart + 8, 2)),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(tiff.Slice(entryStart + 8, 4)),
            _ => throw new NotSupportedException($"scalar tag {tag} has unexpected type {fieldType}"),
        };
    }

    private static uint[]? ReadLongOrShortArrayTag(ReadOnlySpan<byte> tiff, int ifdOffset, ushort tag)
    {
        var entryStart = FindEntry(tiff, ifdOffset, tag);
        if (entryStart is null) return null;
        var fieldType = BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(entryStart.Value + 2, 2));
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.Slice(entryStart.Value + 4, 4));
        var elemSize = fieldType switch { 3 => 2, 4 => 4, _ => throw new NotSupportedException($"tag {tag} type {fieldType}") };
        var totalBytes = count * elemSize;

        ReadOnlySpan<byte> data;
        if (totalBytes <= 4) data = tiff.Slice(entryStart.Value + 8, totalBytes);
        else
        {
            var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.Slice(entryStart.Value + 8, 4));
            data = tiff.Slice(off, totalBytes);
        }
        var result = new uint[count];
        for (var i = 0; i < count; i++)
            result[i] = elemSize == 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i * 2, 2))
                : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i * 4, 4));
        return result;
    }

    private static float[]? ReadFloatArrayTag(ReadOnlySpan<byte> tiff, ushort tag)
    {
        var ifdOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff[4..]);
        var entryStart = FindEntry(tiff, ifdOffset, tag);
        if (entryStart is null) return null;
        var fieldType = BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(entryStart.Value + 2, 2));
        fieldType.ShouldBe((ushort)TiffFieldType.Float, $"tag {tag} expected FLOAT");
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.Slice(entryStart.Value + 4, 4));
        var totalBytes = count * 4;
        ReadOnlySpan<byte> data;
        if (totalBytes <= 4) data = tiff.Slice(entryStart.Value + 8, totalBytes);
        else
        {
            var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.Slice(entryStart.Value + 8, 4));
            data = tiff.Slice(off, totalBytes);
        }
        var result = new float[count];
        for (var i = 0; i < count; i++)
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(i * 4, 4));
        return result;
    }

    /// <summary>Returns the file offset of the 12-byte IFD entry for <paramref name="tag"/>, or null if absent.</summary>
    private static int? FindEntry(ReadOnlySpan<byte> tiff, int ifdOffset, ushort tag)
    {
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(ifdOffset, 2));
        const int entrySize = 12;
        for (var i = 0; i < entryCount; i++)
        {
            var entryStart = ifdOffset + 2 + i * entrySize;
            var entryTag = BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(entryStart, 2));
            if (entryTag == tag) return entryStart;
        }
        return null;
    }
}
