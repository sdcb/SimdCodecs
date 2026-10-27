using System.Buffers.Binary;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Structural tests for the SampleFormat (tag 339) emission added to <see cref="TiffWriter"/>.
/// Hand-parses the first IFD from the writer output and asserts the tag is present with the
/// expected per-sample values for both float32 and uint16/uint8 pages.
/// </summary>
public sealed class TiffSampleFormatTests
{
    [Fact]
    public async Task Float32Rgb_EmitsIeeeFloatSampleFormat()
    {
        var bytes = await WriteSinglePageAsync(width: 4, height: 4, options: new TiffPageOptions
        {
            SamplesPerPixel = 3,
            BitsPerSample = 32,
            Photometric = TiffPhotometric.Rgb,
            SampleFormat = TiffSampleFormat.IeeeFloat,
            Compression = TiffCompression.Uncompressed,
        });

        var sampleFormat = ReadShortArrayTag(bytes, TiffTag.SampleFormat);
        sampleFormat.ShouldNotBeNull();
        sampleFormat!.ShouldBe([(ushort)TiffSampleFormat.IeeeFloat,
                                (ushort)TiffSampleFormat.IeeeFloat,
                                (ushort)TiffSampleFormat.IeeeFloat]);
    }

    [Fact]
    public async Task Float32Grayscale_EmitsIeeeFloatSampleFormat()
    {
        var bytes = await WriteSinglePageAsync(width: 2, height: 2, options: new TiffPageOptions
        {
            SamplesPerPixel = 1,
            BitsPerSample = 32,
            Photometric = TiffPhotometric.MinIsBlack,
            SampleFormat = TiffSampleFormat.IeeeFloat,
            Compression = TiffCompression.Uncompressed,
        });

        var sampleFormat = ReadShortArrayTag(bytes, TiffTag.SampleFormat);
        sampleFormat.ShouldNotBeNull();
        sampleFormat!.ShouldBe([(ushort)TiffSampleFormat.IeeeFloat]);
    }

    [Fact]
    public async Task Uint16Grayscale_EmitsUintSampleFormat()
    {
        var bytes = await WriteSinglePageAsync(width: 2, height: 2, options: new TiffPageOptions
        {
            SamplesPerPixel = 1,
            BitsPerSample = 16,
            Photometric = TiffPhotometric.MinIsBlack,
            SampleFormat = TiffSampleFormat.Uint,
            Compression = TiffCompression.Uncompressed,
        });

        var sampleFormat = ReadShortArrayTag(bytes, TiffTag.SampleFormat);
        sampleFormat.ShouldNotBeNull();
        sampleFormat!.ShouldBe([(ushort)TiffSampleFormat.Uint]);
    }

    [Fact]
    public async Task Uint8Rgba_DefaultUintEmittedForAllFourSamples()
    {
        // Default SampleFormat is Uint — verify it's emitted explicitly (not skipped) so
        // readers don't have to fall back to the spec default. Counts must match SamplesPerPixel.
        var bytes = await WriteSinglePageAsync(width: 2, height: 2, options: new TiffPageOptions
        {
            SamplesPerPixel = 4,
            BitsPerSample = 8,
            Photometric = TiffPhotometric.Rgb,
            ExtraSamples = TiffExtraSamples.UnassociatedAlpha,
            Compression = TiffCompression.Uncompressed,
        });

        var sampleFormat = ReadShortArrayTag(bytes, TiffTag.SampleFormat);
        sampleFormat.ShouldNotBeNull();
        sampleFormat!.Length.ShouldBe(4);
        sampleFormat.ShouldAllBe(v => v == (ushort)TiffSampleFormat.Uint);
    }

    private static async Task<byte[]> WriteSinglePageAsync(int width, int height, TiffPageOptions options)
    {
        var bytesPerPixel = options.SamplesPerPixel * (options.BitsPerSample / 8);
        var pixels = new byte[width * height * bytesPerPixel];
        // Content doesn't matter — we only inspect IFD structure.
        using var ms = new MemoryStream();
        await using (var writer = TiffWriter.Create(ms))
        {
            await writer.AddPageAsync(pixels, width, height, options);
            await writer.FlushAsync();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Reads tag <paramref name="tag"/> from the first IFD as a ushort array, or null if absent.
    /// Handles both inline (count*2 &lt;= 4 bytes) and overflow encodings.
    /// Verifies the field type is <see cref="TiffFieldType.Short"/> (3).
    /// </summary>
    private static ushort[]? ReadShortArrayTag(ReadOnlySpan<byte> tiff, ushort tag)
    {
        // Header: II, 42, IFD offset
        tiff[0].ShouldBe((byte)'I');
        tiff[1].ShouldBe((byte)'I');
        BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(2, 2)).ShouldBe((ushort)42);

        var ifdOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.Slice(4, 4));
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(ifdOffset, 2));

        const int entrySize = 12;
        for (var i = 0; i < entryCount; i++)
        {
            var entry = tiff.Slice(ifdOffset + 2 + i * entrySize, entrySize);
            var entryTag = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            if (entryTag != tag) continue;

            var fieldType = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(2, 2));
            // SHORT = 3 per TIFF spec
            fieldType.ShouldBe((ushort)3, $"tag {tag} expected to be SHORT type");

            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4, 4));
            var totalBytes = count * 2;

            ReadOnlySpan<byte> valueBytes;
            if (totalBytes <= 4)
            {
                valueBytes = entry.Slice(8, totalBytes);
            }
            else
            {
                var dataOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8, 4));
                valueBytes = tiff.Slice(dataOffset, totalBytes);
            }

            var result = new ushort[count];
            for (var k = 0; k < count; k++)
                result[k] = BinaryPrimitives.ReadUInt16LittleEndian(valueBytes.Slice(k * 2, 2));
            return result;
        }
        return null;
    }
}
