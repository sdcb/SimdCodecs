using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// Strips PNG framing, unfilters rows, and re-deflates as ZLIB for TIFF Deflate strips.
/// Uses a MemoryMappedFile scratch buffer to avoid GC pressure from large allocations.
/// </summary>
public sealed class PngDeflateSource : IImageSource, IDisposable
{
    private readonly ReadOnlyMemory<byte> _pngBytes;
    private readonly int _bytesPerPixel;
    private readonly int _rowStride;
    private byte[]? _cachedResult;

    private PngDeflateSource(ReadOnlyMemory<byte> pngBytes, int width, int height,
        int bytesPerPixel, TiffPhotometric photometric)
    {
        _pngBytes = pngBytes;
        Width = width;
        Height = height;
        _bytesPerPixel = bytesPerPixel;
        _rowStride = width * bytesPerPixel;
        Photometric = photometric;
    }

    /// <summary>
    /// Parse the PNG header and create the source. Does NOT decompress yet — deferred to GetSegmentAsync.
    /// </summary>
    public static PngDeflateSource Create(ReadOnlyMemory<byte> pngBytes)
    {
        var span = pngBytes.Span;

        // Validate PNG signature: 137 80 78 71 13 10 26 10
        if (span.Length < 33 || span[0] != 137 || span[1] != 80 || span[2] != 78 || span[3] != 71)
            throw new InvalidDataException("Not a valid PNG file");

        // IHDR chunk starts at byte 8 (4 length + 4 type at byte 8)
        // IHDR data starts at byte 16
        var ihdrLength = BinaryPrimitives.ReadInt32BigEndian(span[8..]);
        if (ihdrLength < 13)
            throw new InvalidDataException("IHDR chunk too short");

        var width = BinaryPrimitives.ReadInt32BigEndian(span[16..]);
        var height = BinaryPrimitives.ReadInt32BigEndian(span[20..]);
        var bitDepth = span[24];
        var colorType = span[25];

        if (bitDepth != 8)
            throw new NotSupportedException($"Only 8-bit PNG is supported, got {bitDepth}-bit");

        var (bytesPerPixel, photometric) = colorType switch
        {
            2 => (3, TiffPhotometric.Rgb),      // TrueColor
            6 => (4, TiffPhotometric.Rgb),      // TrueColorWithAlpha
            0 => (1, TiffPhotometric.MinIsBlack), // Grayscale
            _ => throw new NotSupportedException($"PNG color type {colorType} not supported"),
        };

        return new PngDeflateSource(pngBytes, width, height, bytesPerPixel, photometric);
    }

    public int Width { get; }
    public int Height { get; }
    public int SegmentCount => 1;
    public TiffCompression Compression => TiffCompression.Deflate;
    public TiffPhotometric Photometric { get; }
    public bool IsPreCompressed => true; // we produce ZLIB-framed output

    public ValueTask<ReadOnlyMemory<byte>> GetSegmentAsync(int index, CancellationToken ct = default)
    {
        if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));

        if (_cachedResult is not null)
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(_cachedResult);

        var result = ProcessPng();
        _cachedResult = result;
        return ValueTask.FromResult<ReadOnlyMemory<byte>>(result);
    }

    private byte[] ProcessPng()
    {
        // Step 1: Extract all IDAT data, stripping PNG chunk framing
        var idatData = ExtractIdatData(_pngBytes.Span);

        // Step 2: Decompress ZLIB (IDAT) into MMF scratch buffer
        var unfilteredSize = Height * _rowStride;
        var filteredSize = Height * (_rowStride + 1); // +1 per row for filter byte

        using var scratchMmf = MemoryMappedFile.CreateNew(null, filteredSize);
        using var scratchAccessor = scratchMmf.CreateViewAccessor(0, filteredSize, MemoryMappedFileAccess.ReadWrite);

        int inflatedBytes;
        using (var scratchStream = scratchMmf.CreateViewStream(0, filteredSize, MemoryMappedFileAccess.Write))
        {
            // IDAT data is ZLIB-wrapped — use ZLibStream to decompress
            using var idatStream = new MemoryStream(idatData, writable: false);
            using var inflate = new ZLibStream(idatStream, CompressionMode.Decompress);
            inflate.CopyTo(scratchStream);
            inflatedBytes = (int)scratchStream.Position;
        }

        // Read inflated data from scratch MMF
        var filteredData = new byte[inflatedBytes];
        scratchAccessor.ReadArray(0, filteredData, 0, inflatedBytes);

        // Step 3: Unfilter using PngPredictor
        var unfiltered = Sdcb.SimdCodecs.Png.PngPredictor.Unfilter(filteredData, _rowStride, _bytesPerPixel);

        // Step 4: Re-deflate with ZLIB framing
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(unfiltered);
        return ms.ToArray();
    }

    /// <summary>
    /// Walk PNG chunks, collect all IDAT payload bytes (including ZLIB header).
    /// </summary>
    private static byte[] ExtractIdatData(ReadOnlySpan<byte> png)
    {
        using var ms = new MemoryStream();
        var offset = 8; // skip PNG signature

        while (offset + 8 <= png.Length)
        {
            var chunkLength = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            var chunkType = png.Slice(offset + 4, 4);
            var dataStart = offset + 8;

            if (chunkLength < 0 || dataStart + chunkLength > png.Length)
                break;

            // Check if chunk type is "IDAT"
            if (chunkType[0] == 'I' && chunkType[1] == 'D' && chunkType[2] == 'A' && chunkType[3] == 'T')
            {
                ms.Write(png.Slice(dataStart, chunkLength));
            }

            // Check if chunk type is "IEND"
            if (chunkType[0] == 'I' && chunkType[1] == 'E' && chunkType[2] == 'N' && chunkType[3] == 'D')
                break;

            offset = dataStart + chunkLength + 4; // +4 for CRC
        }

        return ms.ToArray();
    }

    public void Dispose()
    {
        // MMF scratch is created and disposed within ProcessPng — nothing to hold here
    }
}
