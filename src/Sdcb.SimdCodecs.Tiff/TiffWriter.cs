using System.IO.Compression;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// Writes multi-page TIFF files with strip or tiled layout.
/// Supports JPEG passthrough, Deflate compression, and raw pixel input.
/// </summary>
public sealed class TiffWriter : IAsyncDisposable
{
    private readonly TiffFileTarget _target;
    private int _pageCount;
    private long _prevNextIfdPatchOffset = -1;
    private long _firstIfdOffset = -1;
    private bool _headerWritten;
    private bool _flushed;

    private TiffWriter(TiffFileTarget target)
    {
        _target = target;
    }

    /// <summary>Create a TiffWriter that writes to a file path.</summary>
    public static TiffWriter Create(string filePath) =>
        new(TiffFileTarget.FromFile(filePath));

    /// <summary>Create a TiffWriter over any writable+seekable stream.</summary>
    public static TiffWriter Create(Stream stream) =>
        new(TiffFileTarget.FromStream(stream));

    /// <summary>Add a page from a custom image source.</summary>
    public async Task AddPageAsync(IImageSource source, TiffPageOptions? options = null,
        CancellationToken ct = default)
    {
        await EnsureHeaderAsync(ct).ConfigureAwait(false);
        options ??= TiffPageOptions.Default;

        // Write segment data
        var offsets = new uint[source.SegmentCount];
        var byteCounts = new uint[source.SegmentCount];

        for (var i = 0; i < source.SegmentCount; i++)
        {
            await _target.AlignAsync(ct).ConfigureAwait(false);
            offsets[i] = (uint)_target.Position;

            var data = await source.GetSegmentAsync(i, ct).ConfigureAwait(false);

            if (source.IsPreCompressed)
            {
                await _target.WriteAsync(data, ct).ConfigureAwait(false);
            }
            else
            {
                // Refuse a compression this writer cannot APPLY, rather than falling through to the
                // raw bytes. The IFD is stamped with options.Compression regardless (see BuildIfd), so
                // the fall-through produced a file whose data and label disagreed -- uncompressed bytes
                // announced as LZW. Nothing reports that: the writer succeeds, the file is the right
                // size, and the reader dutifully LZW-decodes raw bytes into garbage that still has the
                // correct dimensions. It cost two test suites their meaning before it was noticed,
                // because a reader and a streaming reader decode the same garbage identically, so an
                // equivalence test over it passes while asserting nothing.
                //
                // Uncompressed is the only compression whose "encoder" is legitimately the identity.
                var compressed = options.Compression switch
                {
                    TiffCompression.Deflate or TiffCompression.ZlibPkzip =>
                        DeflateZlib(data.Span),
                    TiffCompression.Uncompressed => data.ToArray(),
                    var unsupported => throw new NotSupportedException(
                        $"TiffWriter cannot encode {unsupported}; writing the bytes uncompressed while "
                        + "labelling them otherwise would produce a file no reader can decode. "
                        + "Supported: Uncompressed, Deflate, ZlibPkzip."),
                };
                await _target.WriteAsync(compressed, ct).ConfigureAwait(false);
            }

            byteCounts[i] = (uint)(_target.Position - offsets[i]);
        }

        // Build IFD
        var ifd = BuildIfd(source, options, offsets, byteCounts);

        // Capture IFD start position
        await _target.AlignAsync(ct).ConfigureAwait(false);
        var ifdOffset = _target.Position;

        var nextIfdPatchOffset = await ifd.WriteAsync(_target, ct).ConfigureAwait(false);

        // Chain: patch previous page's NextIFD to point here
        if (_prevNextIfdPatchOffset >= 0)
            await _target.PatchUInt32Async(_prevNextIfdPatchOffset, (uint)ifdOffset, ct).ConfigureAwait(false);

        if (_firstIfdOffset < 0)
            _firstIfdOffset = ifdOffset;

        _prevNextIfdPatchOffset = nextIfdPatchOffset;
        _pageCount++;
    }

    private static IfdBuilder BuildIfd(IImageSource source, TiffPageOptions options,
        uint[] offsets, uint[] byteCounts)
    {
        var ifd = new IfdBuilder();

        ifd.SetLong(TiffTag.ImageWidth, (uint)source.Width);
        ifd.SetLong(TiffTag.ImageLength, (uint)source.Height);
        ifd.SetShort(TiffTag.Compression, (ushort)(source.IsPreCompressed ? source.Compression : options.Compression));
        ifd.SetShort(TiffTag.PhotometricInterp, (ushort)source.Photometric);
        ifd.SetShort(TiffTag.SamplesPerPixel, (ushort)options.SamplesPerPixel);
        ifd.SetShort(TiffTag.PlanarConfig, (ushort)TiffPlanarConfig.Contig);

        var bps = new ushort[options.SamplesPerPixel];
        Array.Fill(bps, (ushort)options.BitsPerSample);
        ifd.SetShortArray(TiffTag.BitsPerSample, bps);

        // SampleFormat (tag 339): one value per sample. Always emitted so float32 pixels
        // aren't misread as uint by readers that honour the tag (Magick.NET, libtiff, PIL).
        var sampleFormats = new ushort[options.SamplesPerPixel];
        Array.Fill(sampleFormats, (ushort)options.SampleFormat);
        ifd.SetShortArray(TiffTag.SampleFormat, sampleFormats);

        ifd.SetRational(TiffTag.XResolution, (uint)Math.Round(options.DpiX), 1);
        ifd.SetRational(TiffTag.YResolution, (uint)Math.Round(options.DpiY), 1);
        ifd.SetShort(TiffTag.ResolutionUnit, (ushort)TiffResolutionUnit.Inch);

        if (options.Layout == TiffLayout.Tiled)
        {
            ifd.SetLong(TiffTag.TileWidth, (uint)options.TileWidth);
            ifd.SetLong(TiffTag.TileLength, (uint)options.TileHeight);
            ifd.SetLongArray(TiffTag.TileOffsets, offsets);
            ifd.SetLongArray(TiffTag.TileByteCounts, byteCounts);
        }
        else
        {
            var rowsPerStrip = options.RowsPerStrip > 0 ? options.RowsPerStrip : source.Height;
            ifd.SetLong(TiffTag.RowsPerStrip, (uint)rowsPerStrip);
            ifd.SetLongArray(TiffTag.StripOffsets, offsets);
            ifd.SetLongArray(TiffTag.StripByteCounts, byteCounts);
        }

        if (options.SMinSampleValue is { } smin)
        {
            var values = new float[options.SamplesPerPixel];
            Array.Fill(values, smin);
            ifd.SetFloatArray(TiffTag.SMinSampleValue, values);
        }
        if (options.SMaxSampleValue is { } smax)
        {
            var values = new float[options.SamplesPerPixel];
            Array.Fill(values, smax);
            ifd.SetFloatArray(TiffTag.SMaxSampleValue, values);
        }

        if (options.ExtraSamples.HasValue)
            ifd.SetShortArray(TiffTag.ExtraSamples, [(ushort)options.ExtraSamples.Value]);
        if (!string.IsNullOrEmpty(options.Artist))
            ifd.SetAscii(TiffTag.Artist, options.Artist);
        if (!string.IsNullOrEmpty(options.Software))
            ifd.SetAscii(TiffTag.Software, options.Software);
        if (!options.IccProfile.IsEmpty)
            ifd.SetUndefined(TiffTag.IccProfile, options.IccProfile.ToArray());

        return ifd;
    }

    /// <summary>Add a page from raw pixel data.</summary>
    public Task AddPageAsync(ReadOnlyMemory<byte> pixels, int width, int height,
        TiffPageOptions? options = null, CancellationToken ct = default)
    {
        options ??= TiffPageOptions.Default;
        var source = new PixelSource(pixels, width, height, options);
        return AddPageAsync(source, options, ct);
    }

    /// <summary>Add a JPEG page (passthrough, no re-encoding).</summary>
    public Task AddJpegPageAsync(ReadOnlyMemory<byte> jpegBytes, int width, int height,
        double dpi = 96, CancellationToken ct = default)
    {
        var source = new JpegPassthroughSource(jpegBytes, width, height);
        var options = new TiffPageOptions
        {
            SamplesPerPixel = 3,
            BitsPerSample = 8,
            Photometric = TiffPhotometric.YCbCr,
            Compression = TiffCompression.Jpeg,
            DpiX = dpi, DpiY = dpi,
        };
        return AddPageAsync(source, options, ct);
    }

    /// <summary>Add a PNG page — strips framing, unfilters rows, re-deflates as ZLIB.</summary>
    public Task AddPngPageAsync(ReadOnlyMemory<byte> pngBytes,
        double dpi = 96, CancellationToken ct = default)
    {
        var source = PngDeflateSource.Create(pngBytes);
        var samplesPerPixel = source.Photometric == TiffPhotometric.Rgb ? 3 : 1;
        var hasAlpha = source.Width > 0 && pngBytes.Span[25] == 6; // color type 6 = RGBA
        if (hasAlpha) samplesPerPixel = 4;

        var options = new TiffPageOptions
        {
            SamplesPerPixel = samplesPerPixel,
            BitsPerSample = 8,
            Photometric = source.Photometric,
            Compression = TiffCompression.Deflate,
            ExtraSamples = hasAlpha ? TiffExtraSamples.UnassociatedAlpha : null,
            DpiX = dpi, DpiY = dpi,
        };
        return AddPageAsync(source, options, ct);
    }

    /// <summary>Flush the TIFF header and finalize the IFD chain.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_flushed) return;
        _flushed = true;

        if (!_headerWritten || _firstIfdOffset < 0) return;

        // Patch TIFF header: bytes 4-7 = offset to first IFD
        await _target.PatchUInt32Async(4, (uint)_firstIfdOffset, ct).ConfigureAwait(false);

        // Last page's NextIFD is already 0 (written by IfdBuilder)
        await _target.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_flushed)
            await FlushAsync().ConfigureAwait(false);
        await _target.DisposeAsync().ConfigureAwait(false);
    }

    private async Task EnsureHeaderAsync(CancellationToken ct)
    {
        if (_headerWritten) return;
        _headerWritten = true;

        // TIFF header: byte order tag (II=LE / MM=BE) + magic 42 + offset to
        // first IFD (patched later). We always declare the file's byte order
        // to match the host so multi-byte tag values can be written verbatim
        // from native memory — no swap step on the write path, just a verbatim
        // copy. TiffReader will honour whichever order it sees in the header.
        var orderByte = (byte)(BitConverter.IsLittleEndian ? 'I' : 'M');
        var magicBytes = BitConverter.GetBytes((ushort)42); // host byte order
        byte[] header = [orderByte, orderByte, magicBytes[0], magicBytes[1], 0x00, 0x00, 0x00, 0x00];
        await _target.WriteAsync(header, ct).ConfigureAwait(false);
    }

    /// <summary>Deflate data with ZLIB framing, using BCL ZLibStream.</summary>
    internal static byte[] DeflateZlib(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }
}
