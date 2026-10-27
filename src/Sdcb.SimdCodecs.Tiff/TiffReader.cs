using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// Pure-managed TIFF reader — the dual of <see cref="TiffWriter"/>. Reads
/// every IFD in chain order and decodes its strips or tiles into a single
/// contiguous byte buffer per page. <see cref="TiffPage.Pixels"/> is normalised to the
/// host's byte order: file-byte-order is detected from the "II" / "MM"
/// header and a final per-sample swap runs when (file order != host order)
/// so callers can re-interpret the bytes as <c>ushort</c> / <c>float</c>
/// with <c>MemoryMarshal.Cast</c> without further endian gymnastics.
///
/// Scope (v1):
/// <list type="bullet">
/// <item>Both byte orders: "II" (little-endian) and "MM" (big-endian). Most
///       astronomy TIFFs are II; some scanner / Photoshop output is MM.</item>
/// <item>Both layouts: strips, and tiles (<see cref="TiffPage.TileWidth"/> says which a
///       page used). A page carrying tags for both is refused -- TIFF 6.0 p.68 makes them
///       exclusive.</item>
/// <item>Bit depths 8, 16, 32 — uniform across all samples (per TIFF norm).</item>
/// <item>Compression: <see cref="TiffCompression.Uncompressed"/>, <see cref="TiffCompression.Lzw"/>,
///       <see cref="TiffCompression.Deflate"/>, <see cref="TiffCompression.ZlibPkzip"/>,
///       <see cref="TiffCompression.PackBits"/>.</item>
/// <item>Sample formats: <see cref="TiffSampleFormat.Uint"/>, <see cref="TiffSampleFormat.Int"/>,
///       <see cref="TiffSampleFormat.IeeeFloat"/>.</item>
/// <item>Predictors: <see cref="TiffPredictor.None"/> and
///       <see cref="TiffPredictor.HorizontalDifferencing"/> -- the default for
///       every writer that emits ZIP compression. <see cref="TiffPredictor.FloatingPoint"/>
///       throws rather than decoding past it.</item>
/// <item>Both planar configs, for strips and tiles: PlanarConfig=Separate planes are decoded one
///       sample per pixel and interleaved, so <see cref="TiffPage.Pixels"/> and the sink always see
///       chunky rows.</item>
/// <item>On-demand segment read: <see cref="ReadLayout(ReadOnlySpan{byte}, int)"/> describes how a page
///       is cut into strips or tiles, and <c>ReadSegment</c> decodes ONE of them into a caller's buffer
///       from a span, a seekable stream or a file path, reading only that segment's bytes (every
///       plane's, for a planar page). It runs the same decode, swap, predictor and interleave as
///       <see cref="ReadInto{TSink}"/>, so it equals that rectangle of the whole page.</item>
/// </list>
/// JPEG / BigTIFF are out of scope -- when they are needed, add them
/// here (the existing code loops over segments and dispatches on compression).
/// </summary>
public static partial class TiffReader
{
    /// <summary>Decode every page from a TIFF in-memory.</summary>
    public static TiffDocument Read(ReadOnlySpan<byte> tiff)
    {
        var fileIsLE = ReadHeader(tiff);

        var sink = new BufferingSink();
        return ReadCore(tiff, fileIsLE, ref sink, attachPixels: true);
    }

    /// <summary>
    /// Decode a TIFF, handing each page's pixels to <paramref name="sink"/> strip by strip instead of
    /// assembling a raster. Returns the pages' METADATA, with
    /// <see cref="TiffPage.Pixels"/> empty on every one.
    /// </summary>
    /// <remarks>
    /// <para>This is the same machinery <see cref="Read(ReadOnlySpan{byte})"/> runs -- that overload is
    /// simply this one with a sink that concatenates. One path, so the streaming form cannot drift from
    /// the buffering form in predictor handling, endianness or strip geometry, and the existing
    /// round-trip tests cover both.</para>
    ///
    /// <para>Pair it with a memory-mapped file for the case it exists for: an uncompressed page then
    /// materialises neither the file bytes nor a raster, because the reader hands the sink a slice of
    /// the mapping. See <see cref="ITiffStripSink"/> for when that applies and when a scratch buffer is
    /// used instead.</para>
    /// </remarks>
    public static TiffDocument ReadInto<TSink>(ReadOnlySpan<byte> tiff, ref TSink sink)
        where TSink : ITiffStripSink
    {
        var fileIsLE = ReadHeader(tiff);
        return ReadCore(tiff, fileIsLE, ref sink, attachPixels: false);
    }

    private static TiffDocument ReadCore<TSink>(ReadOnlySpan<byte> tiff, bool fileIsLE, ref TSink sink,
        bool attachPixels)
        where TSink : ITiffStripSink
    {
        var pages = new List<TiffPage>();
        var ifdOffset = (int)ReadUInt32(tiff.Slice(4, 4), fileIsLE);
        var pageIndex = 0;
        while (ifdOffset != 0)
        {
            var (page, nextOffset) = ReadPage(tiff, ifdOffset, fileIsLE, pageIndex, ref sink);
            // Only the buffering path re-attaches pixels, and it is the sink that holds them: a
            // streaming caller has already consumed them and gets metadata alone.
            pages.Add(attachPixels && sink is BufferingSink buffered
                ? page with { Pixels = buffered.TakePixels() }
                : page);
            ifdOffset = nextOffset;
            pageIndex++;
        }
        return new TiffDocument(pages);
    }

    /// <summary>
    /// Validates the 8-byte header and reports the file's byte order. Split out so
    /// <see cref="ReadInto{TSink}"/> and <see cref="Read(ReadOnlySpan{byte})"/> cannot come to disagree
    /// about what a valid TIFF is.
    /// </summary>
    private static bool ReadHeader(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) throw new InvalidDataException("TIFF too small for header");

        // Detect byte order from header bytes 0-1. "II" = little-endian,
        // "MM" = big-endian (TIFF 6.0 §2). All multi-byte values in the file
        // — including the magic, the IFD offsets, every IFD entry, every
        // pixel sample of width > 8 bits — follow this same order.
        bool fileIsLE;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I') fileIsLE = true;
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M') fileIsLE = false;
        else throw new InvalidDataException($"Unknown TIFF byte order tag {tiff[0]:X2}{tiff[1]:X2}");

        if (ReadUInt16(tiff.Slice(2, 2), fileIsLE) != 42)
            throw new InvalidDataException("TIFF magic mismatch");

        return fileIsLE;
    }

    /// <summary>
    /// The sink behind <see cref="Read(ReadOnlySpan{byte})"/>: concatenates a page's strips into the
    /// one contiguous buffer that overload has always returned.
    /// </summary>
    private sealed class BufferingSink : ITiffStripSink
    {
        private byte[] _pixels = [];
        private int _position;

        public bool BeginPage(int pageIndex, TiffPage description)
        {
            _pixels = new byte[checked(description.Width * description.Height
                * description.SamplesPerPixel * (description.BitsPerSample / 8))];
            _position = 0;
            return true;
        }

        public void Strip(int pageIndex, int firstRow, int rowCount, ReadOnlySpan<byte> samples)
        {
            // Clamped, not trusted: a truncated file can present more bytes than the page's declared
            // dimensions hold, and this overload's contract is a buffer of exactly that size.
            var copy = Math.Min(samples.Length, _pixels.Length - _position);
            if (copy <= 0) return;
            samples[..copy].CopyTo(_pixels.AsSpan(_position, copy));
            _position += copy;
        }

        /// <summary>Hands the assembled buffer over and forgets it, so the next page starts clean.</summary>
        public byte[] TakePixels()
        {
            var pixels = _pixels;
            _pixels = [];
            _position = 0;
            return pixels;
        }
    }

    /// <summary>Decode every page from a TIFF stream (slurped to a byte array).</summary>
    public static TiffDocument Read(Stream stream)
    {
        // TIFF readers need random access via the IFD chain — slurp the stream
        // and operate on the in-memory span. For very large TIFFs the caller
        // should map the file (MemoryMappedFile) and pass that span instead.
        // Pre-size when the length is known: MemoryStream grows by doubling, so slurping a
        // 100 MB TIFF into a default-capacity stream reallocates a dozen times and copies
        // roughly twice the file before the first byte is ever decoded.
        var capacity = stream.CanSeek ? (int)Math.Min(int.MaxValue, stream.Length - stream.Position) : 0;
        using var ms = new MemoryStream(capacity);
        stream.CopyTo(ms);
        return Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    private static (TiffPage Page, int NextIfdOffset) ReadPage<TSink>(ReadOnlySpan<byte> tiff, int ifdOffset,
        bool fileIsLE, int pageIndex, ref TSink sink)
        where TSink : ITiffStripSink
    {
        // ---- Parse and validate the IFD (shared with the on-demand segment read) ----
        var src = new SpanBytes(tiff);
        var ifd = TiffIfdParser.Parse(ref src, ifdOffset, fileIsLE);
        var width = ifd.Width;
        var height = ifd.Height;
        var samplesPerPixel = ifd.SamplesPerPixel;
        var bitsPerSample = ifd.BitsPerSample;
        var compression = ifd.Compression;
        var photometric = ifd.Photometric;
        var sampleFormat = ifd.SampleFormat;
        var predictor = ifd.Predictor;
        var rowsPerStrip = ifd.RowsPerStrip;
        var isTiled = ifd.IsTiled;
        var tileWidth = ifd.TileWidth;
        var tileHeight = ifd.TileHeight;
        var segmentOffsets = ifd.SegmentOffsets;
        var segmentByteCounts = ifd.SegmentByteCounts;
        var geometry = ifd.Geometry;
        var sMin = ifd.SMin;
        var sMax = ifd.SMax;
        var icc = ifd.Icc;
        var exifIfdOffset = ifd.ExifIfdOffset;
        var gpsIfdOffset = ifd.GpsInfoIfdOffset;
        var nextIfdOffset = (int)ifd.NextIfdOffset;
        var planar = geometry.IsPlanar;
        var planes = geometry.PlaneCount;
        var tilesAcross = geometry.SegmentsAcross;
        var tilesDown = geometry.SegmentsDown;
        // ---- Decode strips, normalise each, hand it to the sink -----------
        var bytesPerPixel = samplesPerPixel * (bitsPerSample / 8);
        var bytesPerRow = width * bytesPerPixel;

        // The page is fully described before a single pixel is decoded, which is the whole point of the
        // sink: a caller sizes its own destination from this and never needs a raster. Pixels stay empty
        // here; the buffering sink re-attaches its concatenated buffer in ReadCore.
        var page = new TiffPage(
            Width: width,
            Height: height,
            SamplesPerPixel: samplesPerPixel,
            BitsPerSample: bitsPerSample,
            Photometric: photometric,
            SampleFormat: sampleFormat,
            Compression: compression,
            RowsPerStrip: rowsPerStrip,
            Pixels: [],
            SMinSampleValue: sMin,
            SMaxSampleValue: sMax,
            IccProfile: icc,
            ExifIfdOffset: exifIfdOffset,
            GpsInfoIfdOffset: gpsIfdOffset,
            FileIsLittleEndian: fileIsLE,
            TileWidth: isTiled ? tileWidth : 0,
            TileHeight: isTiled ? tileHeight : 0);

        if (!sink.BeginPage(pageIndex, page))
        {
            // Declined: none of this page strips are touched. That is what makes reading page 0 of a
            // multi-page file cost only page 0.
            return (page, nextIfdOffset);
        }

        // Inside one segment a planar page holds a single sample per pixel; the planes are interleaved
        // into the chunky band below, so the sink still sees whole interleaved rows.
        var bytesPerSample = bitsPerSample / 8;
        var segBytesPerPixel = planar ? bytesPerSample : bytesPerPixel;
        var segSamplesPerPixel = geometry.SegmentSamplesPerPixel;
        var bandRows = geometry.SegmentHeight;

        // 8-bit samples have no byte order, and neither has a file that already matches the host --
        // which in practice is nearly every file, since writers emit II and hosts are little-endian.
        var needsSwap = fileIsLE != BitConverter.IsLittleEndian && bitsPerSample > 8;
        var needsPredictor = predictor == TiffPredictor.HorizontalDifferencing;

        // THE FAST CASE, and the reason ReadInto exists: nothing to decompress and nothing to rewrite,
        // so the sink gets a slice of the INPUT and no scratch is allocated at all. Reading a
        // memory-mapped uncompressed TIFF this way materialises neither the file bytes nor a raster.
        // Both guards are about mutation -- normalising would have to write, and a mapped view is
        // read-only. It is a STRIP-only promise: see the band buffer below for why a tile cannot be
        // handed over in place.
        var passFileBytesThrough = compression is TiffCompression.Uncompressed && !needsSwap && !needsPredictor;

        // One decoded segment: a strip's rows, or a tile's. A tile is always STORED full-size with
        // the overhang padded (TIFF 6.0 p.67), but the rows past the image's last are padding by
        // definition and the useful ones come first, so nothing below the image is ever decoded --
        // which also stops a page that declares a tile taller than itself from sizing the buffers off
        // that declaration rather than off the picture.
        var tileRowBytes = tileWidth * segBytesPerPixel;
        var segmentCapacity = isTiled
            ? checked(tileRowBytes * bandRows)
            : checked(bandRows * width * segBytesPerPixel);

        // ONE scratch buffer for every segment, plus ONE MemoryStream for the inflate path -- not one
        // of each per segment. A writer emitting ZIP almost always emits RowsPerStrip=1, so a real
        // file is thousands of strips: measured over a corpus of 16-bit RGB frames, 2,009 to 5,529 of
        // them. That was thousands of short-lived arrays plus thousands of streams per decode.
        byte[]? scratch = passFileBytesThrough ? null : ArrayPool<byte>.Shared.Rent(segmentCapacity);

        // A tile spans part of a row, and the sink's unit is whole rows, so tiles cannot be handed
        // over one at a time. One ROW of tiles is assembled here and passed on as a band. That keeps a
        // single sink contract for both layouts, and costs bandRows rows of the image rather than the
        // raster -- but it is a copy, which is why the zero-copy case above excludes tiles. It is
        // assembled unconditionally: a tile as wide as the image would already BE a band and could go
        // straight through, but that needs width to be a multiple of 16 (the spec's tile rule) and is
        // not worth a second path through this loop.
        byte[]? band = isTiled || planar ? ArrayPool<byte>.Shared.Rent(checked(bandRows * bytesPerRow)) : null;

        byte[]? inflateSource = null;
        MemoryStream? scratchStream = null;
        if (compression is TiffCompression.Deflate or TiffCompression.ZlibPkzip)
        {
            var maxSegment = 0;
            foreach (var byteCount in segmentByteCounts)
                maxSegment = Math.Max(maxSegment, (int)byteCount);
            inflateSource = ArrayPool<byte>.Shared.Rent(maxSegment);
            // writable + publiclyVisible so each segment is presented by moving Length/Position
            // rather than by constructing a new stream over a new array.
            scratchStream = new MemoryStream(inflateSource, 0, inflateSource.Length, writable: true,
                publiclyVisible: true);
        }

        try
        {
            if (isTiled)
            {
                for (var tileY = 0; tileY < tilesDown; tileY++)
                {
                    var firstRow = geometry.FirstRow(tileY);
                    var rowsInBand = geometry.RowCount(tileY);

                    for (var tileX = 0; tileX < tilesAcross; tileX++)
                    {
                        var firstCol = tileX * tileWidth;
                        var colPixels = Math.Min(tileWidth, width - firstCol);

                        // A planar page stores each sample's tile separately: decode every plane's tile
                        // at one sample per pixel and scatter it into its channel of the chunky band.
                        for (var plane = 0; plane < planes; plane++)
                        {
                            var index = geometry.SegmentIndex(plane, tileY, tileX);
                            var samples = DecodeSegment(tiff, segmentOffsets[index], segmentByteCounts[index],
                                index, compression, passFileBytesThrough, scratch, inflateSource, scratchStream,
                                segmentCapacity);

                            // Normalise the TILE, at the TILE's width. Horizontal differencing restarts at
                            // every row of every tile (TIFF 6.0 section 14) and a tile's row is tileWidth
                            // wide, not the image's -- run it at image width and the picture decodes
                            // without complaint, correct down its first tile column and drifting across
                            // the rest.
                            if (needsSwap)
                                SwapPixelsToHostOrder(scratch!.AsSpan(0, samples.Length), bitsPerSample);
                            if (needsPredictor)
                                UndoHorizontalDifferencing(scratch!.AsSpan(0, samples.Length), tileWidth, segSamplesPerPixel, bitsPerSample);

                            PlaceRows(samples, band!, rowsInBand, tileRowBytes, bytesPerRow, firstCol, colPixels,
                                segBytesPerPixel, bytesPerPixel, bytesPerSample, plane);
                        }
                    }

                    sink.Strip(pageIndex, firstRow, rowsInBand, band!.AsSpan(0, rowsInBand * bytesPerRow));
                }
            }
            else
            {
                for (var strip = 0; strip < geometry.SegmentsDown; strip++)
                {
                    var rowCursor = geometry.FirstRow(strip);
                    var rowCount = geometry.RowCount(strip);

                    if (!planar)
                    {
                        var index = geometry.SegmentIndex(0, strip);
                        // A chunky strip list may be short (truncated file): deliver the rows it covers.
                        if (index >= segmentOffsets.Length) break;
                        var samples = DecodeSegment(tiff, segmentOffsets[index], segmentByteCounts[index], index,
                            compression, passFileBytesThrough, scratch, inflateSource, scratchStream,
                            rowCount * bytesPerRow);

                        // Normalise this strip, not the assembled image. Both passes are row-local -- the
                        // swap is per sample, and differencing restarts at every row (TIFF 6.0 section 14)
                        // -- and strips hold whole rows, so a strip seam is never a special case and the
                        // result is identical to running them once over a whole raster.
                        if (needsSwap)
                            SwapPixelsToHostOrder(scratch!.AsSpan(0, samples.Length), bitsPerSample);
                        if (needsPredictor)
                            UndoHorizontalDifferencing(scratch!.AsSpan(0, samples.Length), width, samplesPerPixel, bitsPerSample);

                        sink.Strip(pageIndex, rowCursor, rowCount, samples);
                        continue;
                    }

                    for (var plane = 0; plane < planes; plane++)
                    {
                        var index = geometry.SegmentIndex(plane, strip);
                        var samples = DecodeSegment(tiff, segmentOffsets[index], segmentByteCounts[index], index,
                            compression, passFileBytesThrough, scratch, inflateSource, scratchStream,
                            rowCount * width * segBytesPerPixel);
                        if (needsSwap)
                            SwapPixelsToHostOrder(scratch!.AsSpan(0, samples.Length), bitsPerSample);
                        if (needsPredictor)
                            UndoHorizontalDifferencing(scratch!.AsSpan(0, samples.Length), width, segSamplesPerPixel, bitsPerSample);

                        PlaceRows(samples, band!, rowCount, width * segBytesPerPixel, bytesPerRow, 0, width,
                            segBytesPerPixel, bytesPerPixel, bytesPerSample, plane);
                    }

                    sink.Strip(pageIndex, rowCursor, rowCount, band!.AsSpan(0, rowCount * bytesPerRow));
                }
            }
        }
        finally
        {
            scratchStream?.Dispose();
            if (inflateSource is not null)
                ArrayPool<byte>.Shared.Return(inflateSource);
            if (scratch is not null)
                ArrayPool<byte>.Shared.Return(scratch);
            if (band is not null)
                ArrayPool<byte>.Shared.Return(band);
        }

        return (page, nextIfdOffset);
    }

    /// <summary>
    /// One SEGMENT -- a strip or a tile, the two being the same thing to every stage but geometry --
    /// from the file into decoded samples, returned as a slice of <paramref name="scratch"/> or, on
    /// the pass-through path, of the file itself. Shared by both layouts so that a tile can never
    /// come to decompress differently from a strip.
    /// </summary>
    private static ReadOnlySpan<byte> DecodeSegment(ReadOnlySpan<byte> tiff, uint offset, uint byteCount,
        int index, TiffCompression compression, bool passFileBytesThrough, byte[]? scratch,
        byte[]? inflateSource, MemoryStream? scratchStream, int capacity)
    {
        var start = (int)offset;
        var length = (int)byteCount;
        if (start < 0 || length < 0 || start + length > tiff.Length)
            throw new InvalidDataException($"Segment {index} extents out of bounds");
        var segment = tiff.Slice(start, length);

        switch (compression)
        {
            case TiffCompression.Uncompressed when passFileBytesThrough:
                return segment[..Math.Min(segment.Length, capacity)];
            case TiffCompression.Uncompressed:
            {
                var decoded = Math.Min(segment.Length, capacity);
                segment[..decoded].CopyTo(scratch!.AsSpan());
                return scratch.AsSpan(0, decoded);
            }
            case TiffCompression.Lzw:
            {
                // Decoded straight from the file span -- LZW needs no zlib stream, so it needs
                // neither the inflate source buffer nor the MemoryStream.
                var decoded = TiffLzw.Decode(segment, scratch!.AsSpan(0, capacity));
                return scratch.AsSpan(0, decoded);
            }
            case TiffCompression.Deflate:
            case TiffCompression.ZlibPkzip:
            {
                // SetLength BEFORE the copy, never after: MemoryStream ZERO-FILLS when it grows, so
                // setting the length second wipes the tail of any segment longer than the previous
                // one. That is silent and data-dependent -- segments of a smooth image all deflate to
                // near-identical sizes and never grow, so it only bites once the content compresses
                // unevenly.
                scratchStream!.SetLength(length);
                segment.CopyTo(inflateSource!.AsSpan());
                scratchStream.Position = 0;
                var decoded = InflateInto(scratchStream, scratch!.AsSpan(0, capacity));
                return scratch.AsSpan(0, decoded);
            }
            case TiffCompression.PackBits:
            {
                var decoded = TiffPackBits.Decode(segment, scratch!.AsSpan(0, capacity));
                return scratch.AsSpan(0, decoded);
            }
            default:
                throw new NotSupportedException($"Compression {compression} not supported in this reader");
        }
    }

    /// <summary>
    /// Copy <paramref name="rows"/> rows of a decoded segment into the chunky band, starting at pixel
    /// <paramref name="firstPixel"/> of each band row and covering <paramref name="colPixels"/> pixels,
    /// so an edge tile's padding is dropped. When the segment is chunky (<paramref name="srcPixelBytes"/>
    /// equals <paramref name="dstPixelBytes"/>) rows are copied whole; when it is one plane of a
    /// separate-planes page each sample is scattered into channel <paramref name="plane"/>. What a SHORT
    /// (truncated) segment cannot supply is zeroed rather than left behind: the band is reused, so "not
    /// written" would mean the previous band's pixels, which looks like real image content.
    /// </summary>
    private static void PlaceRows(ReadOnlySpan<byte> samples, Span<byte> band, int rows, int srcRowBytes,
        int dstRowBytes, int firstPixel, int colPixels, int srcPixelBytes, int dstPixelBytes,
        int bytesPerSample, int plane)
    {
        if (srcPixelBytes == dstPixelBytes)
        {
            var colBytes = colPixels * dstPixelBytes;
            for (var row = 0; row < rows; row++)
            {
                var dst = (row * dstRowBytes) + (firstPixel * dstPixelBytes);
                var src = row * srcRowBytes;
                var have = Math.Clamp(samples.Length - src, 0, colBytes);
                if (have > 0)
                    samples.Slice(src, have).CopyTo(band.Slice(dst, have));
                if (have < colBytes)
                    band.Slice(dst + have, colBytes - have).Clear();
            }
            return;
        }

        for (var row = 0; row < rows; row++)
        {
            var dst = (row * dstRowBytes) + (firstPixel * dstPixelBytes) + (plane * bytesPerSample);
            var src = row * srcRowBytes;
            for (var px = 0; px < colPixels; px++, dst += dstPixelBytes, src += srcPixelBytes)
            {
                var target = band.Slice(dst, bytesPerSample);
                if (src + bytesPerSample <= samples.Length)
                    samples.Slice(src, bytesPerSample).CopyTo(target);
                else
                    target.Clear();
            }
        }
    }

    /// <summary>
    /// Invert TIFF Predictor 2 (horizontal differencing, TIFF 6.0 section 14): each stored sample
    /// is the difference from the sample one pixel to its left in the same row and the same
    /// channel, so recovering the image is a running sum along each row, per channel.
    ///
    /// Differencing restarts at every row, which is what lets this run over the assembled buffer
    /// instead of per strip: strips hold whole rows, so the row boundaries are identical either
    /// way and a strip seam is never a special case.
    ///
    /// The addition WRAPS at the sample width on purpose -- the encoder computed the differences
    /// with the same wrap, so unchecked addition is exactly what reverses it. A trailing partial
    /// row (truncated file) is left alone rather than half-summed.
    /// </summary>
    private static void UndoHorizontalDifferencing(Span<byte> pixels, int width, int samplesPerPixel, int bitsPerSample)
    {
        var stride = width * samplesPerPixel;   // samples per row, every channel interleaved
        if (stride <= 0 || samplesPerPixel <= 0) return;

        switch (bitsPerSample)
        {
            case 8:
                AccumulateBytes(pixels, stride, samplesPerPixel);
                break;
            case 16:
                AccumulateUInt16(pixels, stride, samplesPerPixel);
                break;
            case 32:
                AccumulateUInt32(pixels, stride, samplesPerPixel);
                break;
        }
    }

    private static void AccumulateBytes(Span<byte> pixels, int stride, int samplesPerPixel)
    {
        var rows = pixels.Length / stride;
        for (var y = 0; y < rows; y++)
        {
            var row = pixels.Slice(y * stride, stride);
            for (var i = samplesPerPixel; i < row.Length; i++)
                row[i] = (byte)(row[i] + row[i - samplesPerPixel]);
        }
    }

    private static void AccumulateUInt16(Span<byte> pixels, int stride, int samplesPerPixel)
    {
        var samples = MemoryMarshal.Cast<byte, ushort>(pixels);
        var rows = samples.Length / stride;
        for (var y = 0; y < rows; y++)
        {
            var row = samples.Slice(y * stride, stride);
            for (var i = samplesPerPixel; i < row.Length; i++)
                row[i] = (ushort)(row[i] + row[i - samplesPerPixel]);
        }
    }

    /// <summary>
    /// 32-bit samples are summed as uint even when the page is IEEE float: Predictor 2 is defined
    /// on integer samples, and float data is what <see cref="TiffPredictor.FloatingPoint"/>
    /// exists for. An encoder that pairs float samples with Predictor 2 therefore differenced the
    /// raw bit patterns as integers, and this reverses exactly that.
    /// </summary>
    private static void AccumulateUInt32(Span<byte> pixels, int stride, int samplesPerPixel)
    {
        var samples = MemoryMarshal.Cast<byte, uint>(pixels);
        var rows = samples.Length / stride;
        for (var y = 0; y < rows; y++)
        {
            var row = samples.Slice(y * stride, stride);
            for (var i = samplesPerPixel; i < row.Length; i++)
                row[i] += row[i - samplesPerPixel];
        }
    }

    /// <summary>
    /// In-place per-sample byte-reverse. Float32 is treated as a 32-bit blob
    /// because reversing the 4 raw bytes of an IEEE-754 number gives back the
    /// other-endian IEEE-754 representation of the same value.
    /// </summary>
    private static void SwapPixelsToHostOrder(Span<byte> pixels, int bitsPerSample)
    {
        switch (bitsPerSample)
        {
            case 16:
                var asU16 = MemoryMarshal.Cast<byte, ushort>(pixels);
                for (var i = 0; i < asU16.Length; i++)
                    asU16[i] = BinaryPrimitives.ReverseEndianness(asU16[i]);
                break;
            case 32:
                var asU32 = MemoryMarshal.Cast<byte, uint>(pixels);
                for (var i = 0; i < asU32.Length; i++)
                    asU32[i] = BinaryPrimitives.ReverseEndianness(asU32[i]);
                break;
            // 8-bit: byte order is irrelevant.
        }
    }

    /// <summary>
    /// Inflate one zlib-wrapped strip from <paramref name="src"/> into the destination span,
    /// returning the number of bytes written. ZLibStream is forgiving of trailing zero
    /// padding the writer may have left after the deflate trailer (none for
    /// our writer, but some encoders pad strips up to the row boundary).
    /// </summary>
    private static int InflateInto(Stream src, Span<byte> dst)
    {
        // leaveOpen is required: the source is the caller's reused scratch stream. Each strip
        // IS an independent zlib stream and so needs its own ZLibStream -- that one remaining
        // per-strip allocation is inherent to the format, not to this code -- but disposing it
        // must not take the shared source with it.
        using var z = new ZLibStream(src, CompressionMode.Decompress, leaveOpen: true);
        var written = 0;
        while (written < dst.Length)
        {
            var n = z.Read(dst.Slice(written));
            if (n == 0) break;
            written += n;
        }
        return written;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool fileIsLE) => fileIsLE
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool fileIsLE) => fileIsLE
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static float ReadSingle(ReadOnlySpan<byte> bytes, bool fileIsLE) => fileIsLE
        ? BinaryPrimitives.ReadSingleLittleEndian(bytes)
        : BinaryPrimitives.ReadSingleBigEndian(bytes);

    private static uint ReadScalar(TiffFieldType type, ReadOnlySpan<byte> valueOrOffset, bool fileIsLE) => type switch
    {
        TiffFieldType.Byte  => valueOrOffset[0],
        TiffFieldType.Short => ReadUInt16(valueOrOffset.Slice(0, 2), fileIsLE),
        TiffFieldType.Long  => ReadUInt32(valueOrOffset.Slice(0, 4), fileIsLE),
        _ => throw new NotSupportedException($"Scalar tag has unexpected type {type}"),
    };
}

/// <summary>
/// Top-level result of a TIFF decode: every IFD in chain order, each as a
/// <see cref="TiffPage"/>. For single-page TIFFs (typical) this list has
/// length 1.
/// </summary>
public sealed record TiffDocument(IReadOnlyList<TiffPage> Pages);

/// <summary>
/// One decoded IFD plus its strip-concatenated pixel buffer.
/// <see cref="Pixels"/> is laid out in row-major contiguous order with each
/// sample in the *host's* byte order — the reader already swapped any
/// file-byte-order mismatch (e.g. MM file on an LE host) — so callers on
/// x64/arm64 can reinterpret-cast it as <c>ushort[]</c> / <c>float[]</c>
/// with <c>System.Runtime.InteropServices.MemoryMarshal.Cast</c> for
/// zero-copy access. Which layout the page was STORED in makes no difference to those bytes:
/// <see cref="TileWidth"/> and <see cref="TileHeight"/> are non-zero for a tiled page and zero
/// for a stripped one (where <see cref="RowsPerStrip"/> is the meaningful one instead), and
/// re-assembling the tiles is the reader's job, not the caller's.
/// </summary>
public sealed record TiffPage(
    int Width,
    int Height,
    int SamplesPerPixel,
    int BitsPerSample,
    TiffPhotometric Photometric,
    TiffSampleFormat SampleFormat,
    TiffCompression Compression,
    int RowsPerStrip,
    byte[] Pixels,
    float? SMinSampleValue,
    float? SMaxSampleValue,
    byte[]? IccProfile,
    int? ExifIfdOffset,
    int? GpsInfoIfdOffset,
    bool FileIsLittleEndian,
    int TileWidth = 0,
    int TileHeight = 0);
