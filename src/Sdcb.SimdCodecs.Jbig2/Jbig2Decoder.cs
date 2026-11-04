using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>
/// JBIG2 (ITU-T T.88) bilevel decoder, written clean-room from the
/// specification.
/// <para>
/// The primary entry point is <see cref="Decode(ReadOnlySpan{byte}, ReadOnlySpan{byte}, int, int, CancellationToken)"/>,
/// shaped for PDF's <c>/JBIG2Decode</c> filter rather than for the facade. A
/// PDF-embedded JBIG2 stream is not a file: T.88 §D.3's "embedded stream"
/// organization has no file header (so there is nothing for
/// <c>IImageDecoder.CanDecode</c> to sniff), its shared segment dictionaries sit
/// in a separate stream referenced by <c>/DecodeParms /JBIG2Globals</c>, and its
/// width and height come from the image dictionary. All three of those are
/// arguments here because none of them can be recovered from the bytes.
/// </para>
/// <para>
/// Standalone <c>.jb2</c> files — which do have a header, and do carry their own
/// dimensions — go through <see cref="DecodeFile(ReadOnlySpan{byte}, int, CancellationToken)"/>, and register with the
/// <c>Sdcb.SimdCodecs</c> facade via <see cref="Jbig2ImageDecoder"/>.
/// </para>
/// <para>
/// <b>Implemented:</b> every region type T.88 defines — generic regions (§6.2.5
/// templates 0-3 with TPGDON and arbitrary AT pixels, MMR / ITU-T T.6 §6.2.6, and
/// the unknown data length of §7.2.7), refinement regions with or without TPGRON
/// (§6.3), symbol dictionaries and text regions both arithmetically and
/// Huffman-coded (§6.4/§6.5, with the standard tables of Annex B, custom table
/// segments, per-instance refinement and refine/aggregate symbols), pattern
/// dictionaries and halftone regions with MMR, HENABLESKIP and any GRAYMAX
/// (§6.6/§6.7), page information, region composition, and every page of a
/// multi-page file.
/// <b>Not implemented:</b> EXTTEMPLATE generic regions, symbol dictionaries that
/// import or retain arithmetic contexts, and T.6's 2D extension codes. Each throws
/// <see cref="NotSupportedException"/> naming the feature — a stream this decoder
/// cannot fully reconstruct fails loudly rather than returning a
/// plausible-looking partial page.
/// </para>
/// <para>
/// <b>After changing this decoder,</b> run <c>Issue1Jbig2CorpusTests</c> against the
/// files attached to issue #1 (https://github.com/sdcb/SimdCodecs/issues/1): download
/// and extract them, set <c>SIMDCODECS_ISSUE1_DIR</c>, and every page is compared with
/// jbig2dec. They are not in the repository because their licence is unknown.
/// </para>
/// </summary>
public static class Jbig2Decoder
{
    /// <summary>The 8-byte file header of a standalone <c>.jb2</c> file (T.88 §D.4.1).</summary>
    public static ReadOnlySpan<byte> FileSignature => [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Decodes a PDF-embedded JBIG2 image stream.
    /// </summary>
    /// <param name="embedded">The image XObject's stream data, after any outer filters.</param>
    /// <param name="globals">
    /// The <c>/DecodeParms /JBIG2Globals</c> stream, or empty when the image has none. Its
    /// segments are processed first, exactly as if they preceded
    /// <paramref name="embedded"/>.
    /// </param>
    /// <param name="width">Page width, from the image dictionary's <c>/Width</c>.</param>
    /// <param name="height">Page height, from the image dictionary's <c>/Height</c>.</param>
    /// <param name="cancellationToken">A cancellation token to monitor for cancellation requests.</param>
    /// <exception cref="InvalidDataException">The stream is malformed or truncated.</exception>
    /// <exception cref="NotSupportedException">The stream needs a JBIG2 feature this decoder does not implement yet.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The requested page is not positive, or is larger than
    /// <see cref="Jbig2Limits.MaxBitmapPixels"/> pixels. These dimensions come from
    /// a PDF image dictionary, so they are no more trustworthy than the codestream
    /// and are checked rather than multiplied out.
    /// </exception>
    public static Jbig2Image Decode(ReadOnlySpan<byte> embedded, ReadOnlySpan<byte> globals, int width, int height,
        CancellationToken cancellationToken = default)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        if ((long)width * height > Jbig2Limits.MaxBitmapPixels)
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"A {width}x{height} page is {(long)width * height:N0} pixels, past the " +
                $"{Jbig2Limits.MaxBitmapPixels:N0} this decoder will allocate for one bitmap.");

        cancellationToken.ThrowIfCancellationRequested();

        var state = new DecodingState(new Jbig2Bitmap(width, height));

        // Globals carry segment definitions shared by several images in one PDF;
        // they are processed as a prefix of the image's own segment stream, and
        // the results they leave behind — symbol dictionaries, above all — stay
        // in scope for the image's own segments. Any page association is honoured
        // as-is, since a globals stream conventionally uses page 0 ("applies to
        // every page").
        if (!globals.IsEmpty)
            ProcessStream(globals, ReadSequential(globals), state, targetPage: 0, cancellationToken);

        ProcessStream(embedded, ReadSequential(embedded), state, targetPage: 0, cancellationToken);

        return new Jbig2Image(width, height, state.Page.Data);
    }

    /// <summary>
    /// Decodes a PDF-embedded JBIG2 image stream that has no
    /// <c>/JBIG2Globals</c>.
    /// </summary>
    /// <param name="embedded">The image XObject's stream data, after any outer filters.</param>
    /// <param name="width">Page width, from the image dictionary's <c>/Width</c>.</param>
    /// <param name="height">Page height, from the image dictionary's <c>/Height</c>.</param>
    /// <param name="cancellationToken">A cancellation token to monitor for cancellation requests.</param>
    public static Jbig2Image Decode(ReadOnlySpan<byte> embedded, int width, int height,
        CancellationToken cancellationToken = default) =>
        Decode(embedded, default, width, height, cancellationToken);

    /// <summary>
    /// Decodes page 1 of a standalone <c>.jb2</c> file (T.88 Annex D), in either
    /// the sequential or the random-access organization.
    /// </summary>
    /// <param name="file">The standalone JBIG2 file bytes.</param>
    /// <param name="cancellationToken">A cancellation token to monitor for cancellation requests.</param>
    /// <exception cref="InvalidDataException">The file header or a segment is malformed.</exception>
    /// <exception cref="NotSupportedException">The file needs a JBIG2 feature this decoder does not implement yet.</exception>
    public static Jbig2Image DecodeFile(ReadOnlySpan<byte> file, CancellationToken cancellationToken = default) =>
        DecodeFile(file, pageNumber: 1, cancellationToken);

    /// <summary>
    /// Decodes one page of a standalone <c>.jb2</c> file (T.88 Annex D). Segments
    /// associated with page 0 (shared dictionaries) apply to every page; the rest
    /// are filtered to <paramref name="pageNumber"/>.
    /// </summary>
    /// <param name="file">The standalone JBIG2 file bytes.</param>
    /// <param name="pageNumber">
    /// The page's association number (T.88 §7.2.6): 1 for the first page. A file's pages
    /// are numbered 1 to the page count that
    /// <see cref="TryReadFileInfo(ReadOnlySpan{byte}, out int, out int, out int)"/> reports.
    /// </param>
    /// <param name="cancellationToken">A cancellation token to monitor for cancellation requests.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageNumber"/> is not positive.</exception>
    /// <exception cref="InvalidDataException">The file header or a segment is malformed, or the file has no such page.</exception>
    /// <exception cref="NotSupportedException">The file needs a JBIG2 feature this decoder does not implement yet.</exception>
    public static Jbig2Image DecodeFile(ReadOnlySpan<byte> file, int pageNumber, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageNumber);

        var segments = ReadFile(file, out var body);
        if (!TryFindPageSize(body, segments, (uint)pageNumber, out var width, out var height))
            throw new InvalidDataException(
                $"JBIG2: file has no page information segment for page {pageNumber}, or its height is indeterminate.");

        cancellationToken.ThrowIfCancellationRequested();

        var state = new DecodingState(new Jbig2Bitmap(width, height));
        ProcessStream(body, segments, state, targetPage: (uint)pageNumber, cancellationToken);

        return new Jbig2Image(width, height, state.Page.Data);
    }

    /// <summary>
    /// Reads page 1's dimensions from a standalone <c>.jb2</c> file without
    /// decoding pixels. False when the header is malformed, the page information
    /// segment is missing, or the page is striped with no end-of-stripe segment
    /// to bound it.
    /// </summary>
    public static bool TryReadFileInfo(ReadOnlySpan<byte> file, out int width, out int height) =>
        TryReadFileInfo(file, out width, out height, out _);

    /// <summary>
    /// <see cref="TryReadFileInfo(ReadOnlySpan{byte}, out int, out int)"/>, plus the number of pages:
    /// page information segments, one per page (T.88 §7.4.8). The width and height are page 1's;
    /// decode any page with <see cref="DecodeFile(ReadOnlySpan{byte}, int, CancellationToken)"/>.
    /// </summary>
    public static bool TryReadFileInfo(ReadOnlySpan<byte> file, out int width, out int height, out int pages)
    {
        width = height = 0;
        pages = 0;
        try
        {
            var segments = ReadFile(file, out var body);
            foreach (var segment in segments)
            {
                if (segment.Type == SegmentType.PageInformation) pages++;
            }
            return TryFindPageSize(body, segments, 1, out width, out height);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    // ---- file / stream structure -------------------------------------------------

    private static List<SegmentHeader> ReadFile(ReadOnlySpan<byte> file, out ReadOnlySpan<byte> body)
    {
        if (file.Length < 9 || !file[..8].SequenceEqual(FileSignature))
            throw new InvalidDataException("JBIG2: missing file header signature.");

        var flags = file[8];
        var sequential = (flags & 0x01) != 0;
        var pageCountKnown = (flags & 0x02) == 0;

        var offset = 9;
        if (pageCountKnown)
        {
            if (file.Length < 13) throw new InvalidDataException("JBIG2: truncated file header.");
            offset += 4;
        }

        body = file[offset..];
        return sequential ? ReadSequential(body) : ReadRandomAccess(body);
    }

    /// <summary>
    /// Sequential / embedded organization: each segment's data part immediately
    /// follows its header, so <see cref="SegmentHeader.DataStart"/> as parsed is
    /// already correct.
    /// </summary>
    private static List<SegmentHeader> ReadSequential(ReadOnlySpan<byte> stream)
    {
        var segments = new List<SegmentHeader>();
        var position = 0;
        while (position < stream.Length)
        {
            var header = Jbig2Segment.ReadHeader(stream, ref position);
            if (header.LengthUnknown) header = Jbig2Segment.ResolveUnknownLength(stream, header);
            segments.Add(header);
            position = header.DataStart + header.DataLength;
        }

        return segments;
    }

    /// <summary>
    /// Random-access organization (T.88 §D.2): every segment header first, then
    /// every data part in the same order. The header block ends exactly where the
    /// bytes still unread equal the data lengths declared so far — which is a
    /// precise terminator, not a heuristic, because this decoder rejects
    /// unknown-length segments up front.
    /// </summary>
    private static List<SegmentHeader> ReadRandomAccess(ReadOnlySpan<byte> stream)
    {
        var headers = new List<SegmentHeader>();
        var position = 0;
        long declared = 0;

        while (position + declared < stream.Length)
        {
            var header = Jbig2Segment.ReadHeader(stream, ref position);
            if (header.LengthUnknown)
                throw new InvalidDataException(
                    "JBIG2: a random-access file has a segment of unknown data length, which T.88 §7.2.7 " +
                    "allows only where each data part follows its header.");
            headers.Add(header);
            declared += header.DataLength;
        }

        if (position + declared != stream.Length)
            throw new InvalidDataException("JBIG2: random-access segment headers do not account for the file's data parts.");

        var segments = new List<SegmentHeader>(headers.Count);
        var dataStart = position;
        foreach (var header in headers)
        {
            segments.Add(header with { DataStart = dataStart });
            dataStart += header.DataLength;
        }

        return segments;
    }

    // ---- page geometry -----------------------------------------------------------

    private static bool TryFindPageSize(
        ReadOnlySpan<byte> stream, List<SegmentHeader> segments, uint page, out int width, out int height)
    {
        width = height = 0;

        foreach (var segment in segments)
        {
            // Page 1 also accepts a page information segment on page 0, which some
            // single-page encoders write.
            if (segment.Type != SegmentType.PageInformation
                || !(segment.Page == page || (page == 1 && segment.Page == 0))) continue;
            if (segment.DataLength < 19) return false;

            var info = stream.Slice(segment.DataStart, segment.DataLength);
            var w = BinaryPrimitives.ReadUInt32BigEndian(info);
            var h = BinaryPrimitives.ReadUInt32BigEndian(info[4..]);

            // §7.4.8.2: an unknown height means the page is striped, and its real
            // extent is only known from the end-of-stripe segments that follow.
            if (h == uint.MaxValue && !TryFindStripedHeight(stream, segments, segment.Page, out h))
                return false;

            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue
                || (long)w * h > Jbig2Limits.MaxBitmapPixels)
                return false;

            width = (int)w;
            height = (int)h;
            return true;
        }

        return false;
    }

    private static bool TryFindStripedHeight(
        ReadOnlySpan<byte> stream, List<SegmentHeader> segments, uint page, out uint height)
    {
        // §7.4.10: an end-of-stripe segment carries the row number of the stripe's
        // last row, so the tallest one plus one is the page height.
        long last = -1;
        foreach (var segment in segments)
        {
            if (segment.Type != SegmentType.EndOfStripe || segment.DataLength < 4 || segment.Page != page) continue;
            last = Math.Max(last, BinaryPrimitives.ReadUInt32BigEndian(stream.Slice(segment.DataStart, 4)));
        }

        height = (uint)(last + 1);
        return last >= 0;
    }

    // ---- segment dispatch --------------------------------------------------------

    /// <summary>
    /// Everything a segment might need from the segments before it. T.88 makes
    /// most of the interesting segment types <em>referential</em> — a text region
    /// names the symbol dictionaries it draws from, a halftone region names its
    /// pattern dictionary, a refinement region names the intermediate region it
    /// corrects — so the results have to outlive the segment that produced them,
    /// keyed by segment number.
    /// </summary>
    private sealed class DecodingState(Jbig2Bitmap page)
    {
        // Three lookups keyed by segment number, all written once per producing
        // segment and read by the segments that name it. They are fields rather
        // than properties because nothing outside this class touches them, and
        // handing out a live Dictionary would advertise more than the two
        // operations below.
        private readonly Dictionary<uint, Jbig2Bitmap> _intermediateRegions = [];
        private readonly Dictionary<uint, Jbig2Bitmap[]> _symbolDictionaries = [];
        private readonly Dictionary<uint, Jbig2Bitmap[]> _patternDictionaries = [];
        private readonly Dictionary<uint, HuffmanTable> _tables = [];

        /// <summary>The page being composited onto.</summary>
        public Jbig2Bitmap Page { get; } = page;

        /// <summary>
        /// The pixel allowance for this whole decode, sized from the page — the one
        /// dimension in the transaction the stream does not choose. Shared across
        /// every segment, so a stream cannot get around it by splitting its work
        /// into many individually-plausible regions.
        /// </summary>
        public Jbig2PixelBudget Budget { get; } = new(Jbig2Limits.BudgetFor(page.Width, page.Height));

        /// <summary>Whether any region has been composited yet — see <see cref="ApplyPageInformation"/>.</summary>
        public bool Composited { get; set; }

        /// <summary>Records an intermediate region (§7.4.1.1), for a later refinement region to name.</summary>
        public void StoreIntermediateRegion(uint segment, Jbig2Bitmap bitmap) =>
            _intermediateRegions[segment] = bitmap;

        /// <summary>Records a symbol dictionary's exported symbols.</summary>
        public void StoreSymbols(uint segment, Jbig2Bitmap[] symbols) =>
            _symbolDictionaries[segment] = symbols;

        /// <summary>Records a custom Huffman table (§7.4.13).</summary>
        public void StoreTable(uint segment, HuffmanTable table) => _tables[segment] = table;

        /// <summary>
        /// The custom Huffman tables a segment refers to, in referred-to order: the
        /// order in which its table selection flags consume them (§7.4.2.1.6,
        /// §7.4.3.1.6).
        /// </summary>
        public Queue<HuffmanTable> GatherTables(SegmentHeader segment)
        {
            var tables = new Queue<HuffmanTable>();
            foreach (var referred in segment.ReferredTo)
                if (_tables.TryGetValue(referred, out var table))
                    tables.Enqueue(table);

            return tables;
        }

        /// <summary>Records a pattern dictionary's dither patterns.</summary>
        public void StorePatterns(uint segment, Jbig2Bitmap[] patterns) =>
            _patternDictionaries[segment] = patterns;

        /// <summary>
        /// The symbols a segment inherits, gathered from every dictionary it
        /// refers to, in referred-to order — which is the order §6.5.8.2.3 numbers
        /// them in, so it is load-bearing rather than incidental.
        /// </summary>
        public Jbig2Bitmap[] GatherSymbols(SegmentHeader segment)
        {
            var symbols = new List<Jbig2Bitmap>();
            foreach (var referred in segment.ReferredTo)
                if (_symbolDictionaries.TryGetValue(referred, out var exported))
                    symbols.AddRange(exported);

            return [.. symbols];
        }

        /// <summary>The patterns a halftone region draws from, or null when it names no dictionary.</summary>
        public Jbig2Bitmap[]? FindPatterns(SegmentHeader segment) => FindFirst(_patternDictionaries, segment);

        /// <summary>
        /// The intermediate region a refinement segment corrects, or null when it
        /// refines the page instead.
        /// </summary>
        public Jbig2Bitmap? FindIntermediateRegion(SegmentHeader segment) =>
            FindFirst(_intermediateRegions, segment);

        /// <summary>
        /// Unlike symbols, which accumulate across every dictionary a segment
        /// names, a halftone region has one pattern dictionary and a refinement
        /// region has one reference — so the first match wins.
        /// </summary>
        private static T? FindFirst<T>(Dictionary<uint, T> store, SegmentHeader segment) where T : class
        {
            foreach (var referred in segment.ReferredTo)
                if (store.TryGetValue(referred, out var value))
                    return value;

            return null;
        }
    }

    private static void ProcessStream(
        ReadOnlySpan<byte> stream,
        List<SegmentHeader> segments,
        DecodingState state,
        uint targetPage,
        CancellationToken cancellationToken = default)
    {
        foreach (var segment in segments)
        {
            // Page 0 means "not page-specific"; otherwise a file's segments are
            // filtered to the page being decoded. Embedded streams pass
            // targetPage 0 and take everything, since a PDF image is one page by
            // construction and encoders are inconsistent about the field.
            if (targetPage != 0 && segment.Page != 0 && segment.Page != targetPage) continue;

            if (segment.Type == SegmentType.PageInformation || IsRegionSegment(segment.Type))
                cancellationToken.ThrowIfCancellationRequested();

            ProcessSegment(segment, stream.Slice(segment.DataStart, segment.DataLength), state);
        }
    }

    private static bool IsRegionSegment(SegmentType type) => type switch
    {
        SegmentType.IntermediateTextRegion or
        SegmentType.ImmediateTextRegion or
        SegmentType.ImmediateLosslessTextRegion or
        SegmentType.IntermediateHalftoneRegion or
        SegmentType.ImmediateHalftoneRegion or
        SegmentType.ImmediateLosslessHalftoneRegion or
        SegmentType.IntermediateGenericRegion or
        SegmentType.ImmediateGenericRegion or
        SegmentType.ImmediateLosslessGenericRegion or
        SegmentType.IntermediateRefinementRegion or
        SegmentType.ImmediateRefinementRegion or
        SegmentType.ImmediateLosslessRefinementRegion => true,
        _ => false,
    };

    private static void ProcessSegment(SegmentHeader segment, ReadOnlySpan<byte> data, DecodingState state)
    {
        switch (segment.Type)
        {
            case SegmentType.PageInformation:
                ApplyPageInformation(data, state.Page, state.Composited);
                break;

            case SegmentType.ImmediateGenericRegion:
            case SegmentType.ImmediateLosslessGenericRegion:
                Compose(state, DecodeGenericRegion(data, state.Budget, out var genericInfo, segment.RowCount), genericInfo);
                break;

            case SegmentType.IntermediateGenericRegion:
                // Not composited onto the page: an intermediate region is an
                // auxiliary buffer that some later refinement region names as its
                // reference (§7.4.1.1).
                state.StoreIntermediateRegion(segment.Number, DecodeGenericRegion(data, state.Budget, out _));
                break;

            case SegmentType.ImmediateRefinementRegion:
            case SegmentType.ImmediateLosslessRefinementRegion:
                Compose(state, DecodeRefinementRegion(data, segment, state, out var refineInfo), refineInfo);
                break;

            case SegmentType.IntermediateRefinementRegion:
                state.StoreIntermediateRegion(segment.Number, DecodeRefinementRegion(data, segment, state, out _));
                break;

            case SegmentType.EndOfPage:
            case SegmentType.EndOfStripe:
            case SegmentType.EndOfFile:
            case SegmentType.Profiles:
            case SegmentType.Extension:
                break;

            case SegmentType.SymbolDictionary:
                state.StoreSymbols(segment.Number, DecodeSymbolDictionary(data, segment, state));
                break;

            case SegmentType.ImmediateTextRegion:
            case SegmentType.ImmediateLosslessTextRegion:
                Compose(state, DecodeTextRegion(data, segment, state, out var textInfo), textInfo);
                break;

            case SegmentType.IntermediateTextRegion:
                state.StoreIntermediateRegion(segment.Number, DecodeTextRegion(data, segment, state, out _));
                break;

            case SegmentType.Tables:
                state.StoreTable(segment.Number, HuffmanTable.ReadSegment(data));
                break;

            case SegmentType.PatternDictionary:
                state.StorePatterns(segment.Number, DecodePatternDictionary(data, state.Budget));
                break;

            case SegmentType.ImmediateHalftoneRegion:
            case SegmentType.ImmediateLosslessHalftoneRegion:
                Compose(state, DecodeHalftoneRegion(data, segment, state, out var halftoneInfo), halftoneInfo);
                break;

            case SegmentType.IntermediateHalftoneRegion:
                state.StoreIntermediateRegion(segment.Number, DecodeHalftoneRegion(data, segment, state, out _));
                break;

            default:
                // §7.2.3: a decoder skips segment types it does not recognise. The
                // data length in the header is what makes that safe.
                break;
        }
    }

    /// <summary>Merges a decoded region onto the page at the placement its region info gives.</summary>
    private static void Compose(DecodingState state, Jbig2Bitmap region, RegionInfo info)
    {
        state.Page.Combine(region, info.X, info.Y, info.Operator);
        state.Composited = true;
    }

    /// <summary>
    /// Page information segment (T.88 §7.4.8). Only the default pixel value has
    /// any effect here: the page's own width and height are overridden by the
    /// caller-supplied dimensions (embedded) or already consumed to size the
    /// bitmap (file).
    /// </summary>
    private static void ApplyPageInformation(ReadOnlySpan<byte> data, Jbig2Bitmap page, bool composited)
    {
        if (data.Length < 19)
            throw new InvalidDataException("JBIG2: truncated page information segment.");

        var flags = data[16];
        var defaultPixel = (flags >> 2) & 1;

        // A conforming stream puts page information before any region, so a late
        // one would be re-painting over decoded content; ignore it rather than
        // erase what has already been composited.
        if (defaultPixel != 0 && !composited)
            page.Data.AsSpan().Fill(1);
    }

    /// <summary>Generic region segment (T.88 §7.4.6): region info, flags, AT pixels, then MQ-coded data.</summary>
    private static Jbig2Bitmap DecodeGenericRegion(
        ReadOnlySpan<byte> data, Jbig2PixelBudget budget, out RegionInfo info, uint? rowCount = null)
    {
        var position = 0;
        var region = Jbig2Segment.ReadRegionInfo(data, ref position, rowCount);
        info = region;

        if (position >= data.Length)
            throw new InvalidDataException("JBIG2: truncated generic region segment flags.");

        var flags = data[position++];
        var mmr = (flags & 0x01) != 0;
        var template = (flags >> 1) & 0x03;
        var typicalPrediction = (flags & 0x08) != 0;
        var extTemplate = (flags & 0x10) != 0;

        Jbig2Bitmap bitmap;
        if (mmr)
        {
            // §7.4.6.3: the AT pixel list is present only when MMR is 0, so the
            // coded data starts immediately after the flags byte. GBTEMPLATE and
            // TPGDON are required to be 0 here and mean nothing to T.6 coding —
            // a stream that sets them anyway still decodes to the right pixels,
            // so they are ignored rather than made fatal.
            bitmap = MmrDecoder.Decode(data[position..], region.Width, region.Height, budget);
        }
        else
        {
            if (extTemplate)
                throw new NotSupportedException("JBIG2: EXTTEMPLATE generic regions are not implemented yet.");

            Span<sbyte> at = stackalloc sbyte[8];
            var atPixels = template == 0 ? 4 : 1;
            if (position + atPixels * 2 > data.Length)
                throw new InvalidDataException("JBIG2: truncated generic region AT pixel list.");

            for (var i = 0; i < atPixels; i++)
            {
                at[i * 2] = (sbyte)data[position++];
                at[i * 2 + 1] = (sbyte)data[position++];
            }

            var contexts = new byte[1 << GenericRegionDecoder.ContextBits(template)];
            var mq = new MqDecoder(data[position..]);
            bitmap = GenericRegionDecoder.Decode(
                ref mq, contexts, region.Width, region.Height, template, typicalPrediction, at, budget);
        }

        return bitmap;
    }

    /// <summary>
    /// Generic refinement region segment (T.88 §7.4.7): region info, flags, AT
    /// pixels, then MQ-coded corrections to a reference bitmap.
    /// </summary>
    /// <remarks>
    /// §7.4.7.2 picks the reference in one of two ways. If the segment refers to
    /// an intermediate region, that region's buffer is the reference; otherwise
    /// it refines <em>the page itself</em>, the rectangle the region info points
    /// at.
    /// <para>
    /// The result is then composited with the region's own external combination
    /// operator, like any other region — it is <b>not</b> forced to REPLACE. That
    /// is worth stating because forcing it looks right and is not: under OR a
    /// page refinement can only ever add black, so an encoder that wants to clear
    /// pixels has to say REPLACE in the region info, and one that says OR means
    /// OR. jbig2dec settles the question — it disagreed with a forced REPLACE on
    /// every case that clears a pixel and agrees once the declared operator is
    /// honoured.
    /// </para>
    /// </remarks>
    private static Jbig2Bitmap DecodeRefinementRegion(
        ReadOnlySpan<byte> data, SegmentHeader segment, DecodingState state, out RegionInfo info)
    {
        var position = 0;
        var region = Jbig2Segment.ReadRegionInfo(data, ref position);

        if (position >= data.Length)
            throw new InvalidDataException("JBIG2: truncated refinement region segment flags.");

        var flags = data[position++];
        var template = flags & 0x01;
        var typicalPrediction = (flags & 0x02) != 0;

        Span<sbyte> at = stackalloc sbyte[4];
        RefinementRegionDecoder.NominalAt.CopyTo(at);
        if (template == 0)
        {
            if (position + 4 > data.Length)
                throw new InvalidDataException("JBIG2: truncated refinement region AT pixel list.");

            for (var i = 0; i < 4; i++) at[i] = (sbyte)data[position++];
        }

        // Lifting the page rectangle out is not just convenience: the decoder
        // reads the reference while writing its output, so they cannot be the
        // same storage.
        var reference = state.FindIntermediateRegion(segment)
            ?? state.Page.Crop(region.X, region.Y, region.Width, region.Height);

        info = region;

        var contexts = new byte[1 << RefinementRegionDecoder.ContextBits(template)];
        var mq = new MqDecoder(data[position..]);

        // §7.4.7.2: a region segment always refines the co-located reference, so
        // the offsets are zero here. They are only non-zero inside a symbol
        // dictionary or text region, which position each refinement themselves.
        return RefinementRegionDecoder.Decode(
            ref mq, contexts, region.Width, region.Height, template,
            reference, dx: 0, dy: 0, typicalPrediction, at, state.Budget);
    }

    /// <summary>
    /// Symbol dictionary segment (T.88 §7.4.3): flags, AT pixels, the two symbol
    /// counts, then arithmetically-coded symbol bitmaps.
    /// </summary>
    private static Jbig2Bitmap[] DecodeSymbolDictionary(
        ReadOnlySpan<byte> data, SegmentHeader segment, DecodingState state)
    {
        if (data.Length < 2)
            throw new InvalidDataException("JBIG2: truncated symbol dictionary segment flags.");

        var position = 0;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(data);
        position += 2;

        var huffman = (flags & 0x0001) != 0;
        var refinementAggregate = (flags & 0x0002) != 0;
        var template = (flags >> 10) & 0x03;
        var refinementTemplate = (flags >> 12) & 0x01;
        var contextUsed = (flags & 0x0100) != 0;
        var contextRetained = (flags & 0x0200) != 0;

        // §7.4.3.1.1 bits 8-9: carrying adaptive state across segments. Refusing
        // is not laziness — a dictionary decoded with the wrong initial contexts
        // produces plausible but wrong glyphs, which is precisely the failure this
        // codec is built to avoid.
        if (contextUsed || contextRetained)
            throw new NotSupportedException(
                "JBIG2: symbol dictionaries that import or retain arithmetic contexts are not implemented.");

        // §7.4.2.1.2: the generic AT pixels are present only for arithmetic coding.
        Span<sbyte> at = stackalloc sbyte[8];
        if (!huffman)
        {
            var atPixels = template == 0 ? 4 : 1;
            if (position + atPixels * 2 > data.Length)
                throw new InvalidDataException("JBIG2: truncated symbol dictionary AT pixel list.");

            for (var i = 0; i < atPixels * 2; i++) at[i] = (sbyte)data[position++];
        }

        Span<sbyte> refinementAt = stackalloc sbyte[4];
        RefinementRegionDecoder.NominalAt.CopyTo(refinementAt);
        if (refinementAggregate && refinementTemplate == 0)
        {
            if (position + 4 > data.Length)
                throw new InvalidDataException("JBIG2: truncated symbol dictionary refinement AT pixel list.");

            for (var i = 0; i < 4; i++) refinementAt[i] = (sbyte)data[position++];
        }

        if (position + 8 > data.Length)
            throw new InvalidDataException("JBIG2: truncated symbol dictionary symbol counts.");

        var exported = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
        var created = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]);
        position += 8;

        // A dictionary is at most a few thousand glyphs in practice; the cap keeps
        // a corrupt count from asking for gigabytes before anything is decoded.
        const uint symbolLimit = 1 << 20;
        if (created > symbolLimit || exported > symbolLimit)
            throw new InvalidDataException($"JBIG2: implausible symbol dictionary counts {created} new / {exported} exported.");

        var inputSymbols = state.GatherSymbols(segment);
        if (exported > inputSymbols.Length + created)
            throw new InvalidDataException(
                $"JBIG2: symbol dictionary exports {exported} of only {inputSymbols.Length + created} available symbols.");

        var parameters = new SymbolDictionaryParameters(
            (int)created, (int)exported, template, refinementAggregate, refinementTemplate);

        if (huffman)
        {
            // §7.4.2.1.1 bits 2-7: which table each number is read with, standard or
            // the next custom one this segment refers to.
            var custom = state.GatherTables(segment);
            var tables = new SymbolDictionaryDecoder.HuffmanTables(
                SelectTable((flags >> 2) & 0x03, [4, 5, 0], custom, "SDHUFFDH"),
                SelectTable((flags >> 4) & 0x03, [2, 3, 0], custom, "SDHUFFDW"),
                SelectTable((flags >> 6) & 0x01, [1], custom, "SDHUFFBMSIZE"),
                SelectTable((flags >> 7) & 0x01, [1], custom, "SDHUFFAGGINST"));

            return SymbolDictionaryDecoder.DecodeHuffman(
                data[position..], parameters, inputSymbols, tables, refinementAt, state.Budget);
        }

        var mq = new MqDecoder(data[position..]);
        return SymbolDictionaryDecoder.Decode(
            ref mq, parameters, inputSymbols, at, refinementAt, state.Budget);
    }

    /// <summary>
    /// Text region segment (T.88 §7.4.4): region info, flags, then the coded
    /// layout that stamps dictionary symbols onto a fresh region.
    /// </summary>
    private static Jbig2Bitmap DecodeTextRegion(
        ReadOnlySpan<byte> data, SegmentHeader segment, DecodingState state, out RegionInfo info)
    {
        var position = 0;
        var region = Jbig2Segment.ReadRegionInfo(data, ref position);
        info = region;

        if (position + 2 > data.Length)
            throw new InvalidDataException("JBIG2: truncated text region segment flags.");

        var flags = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
        position += 2;

        var huffman = (flags & 0x0001) != 0;
        var refine = (flags & 0x0002) != 0;
        var logStrips = (flags >> 2) & 0x03;
        var corner = (ReferenceCorner)((flags >> 4) & 0x03);
        var transposed = (flags & 0x0040) != 0;
        var combination = (CombinationOperator)((flags >> 7) & 0x03);
        var defaultPixel = (byte)((flags >> 9) & 0x01);
        var refinementTemplate = (flags >> 15) & 0x01;

        // §7.4.4.1.1 bits 10-14: a signed five-bit field, so the top bit is a sign.
        var dsOffset = (flags >> 10) & 0x1F;
        if (dsOffset > 15) dsOffset -= 32;

        // §7.4.3.1.2: the Huffman table selections come next, when SBHUFF is set.
        var huffmanFlags = 0;
        if (huffman)
        {
            if (position + 2 > data.Length)
                throw new InvalidDataException("JBIG2: truncated text region Huffman flags.");
            huffmanFlags = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            position += 2;
        }

        Span<sbyte> refinementAt = stackalloc sbyte[4];
        RefinementRegionDecoder.NominalAt.CopyTo(refinementAt);
        if (refine && refinementTemplate == 0)
        {
            if (position + 4 > data.Length)
                throw new InvalidDataException("JBIG2: truncated text region refinement AT pixel list.");

            for (var i = 0; i < 4; i++) refinementAt[i] = (sbyte)data[position++];
        }

        if (position + 4 > data.Length)
            throw new InvalidDataException("JBIG2: truncated text region instance count.");

        var instances = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
        position += 4;

        var symbols = state.GatherSymbols(segment);
        if (symbols.Length == 0)
            throw new InvalidDataException("JBIG2: text region refers to no symbol dictionary.");

        // Every instance costs at least one coded decision, so a count far past
        // the data available can only be corruption.
        if (instances > (uint)(data.Length - position) * 8 + 64)
            throw new InvalidDataException($"JBIG2: text region declares {instances} instances, more than its data can hold.");

        var parameters = new TextRegionParameters(
            region.Width, region.Height, (int)instances, 1 << logStrips,
            defaultPixel, combination, corner, transposed, dsOffset, refine, refinementTemplate);

        var refinementContexts = refine
            ? new byte[1 << RefinementRegionDecoder.ContextBits(refinementTemplate)]
            : [];

        if (huffman)
        {
            var custom = state.GatherTables(segment);
            var tables = new TextRegionDecoder.HuffmanTables(
                SelectTable(huffmanFlags & 0x03, [6, 7, 0], custom, "SBHUFFFS"),
                SelectTable((huffmanFlags >> 2) & 0x03, [8, 9, 10], custom, "SBHUFFDS"),
                SelectTable((huffmanFlags >> 4) & 0x03, [11, 12, 13], custom, "SBHUFFDT"),
                SelectTable((huffmanFlags >> 6) & 0x03, [14, 15, 0], custom, "SBHUFFRDW"),
                SelectTable((huffmanFlags >> 8) & 0x03, [14, 15, 0], custom, "SBHUFFRDH"),
                SelectTable((huffmanFlags >> 10) & 0x03, [14, 15, 0], custom, "SBHUFFRDX"),
                SelectTable((huffmanFlags >> 12) & 0x03, [14, 15, 0], custom, "SBHUFFRDY"),
                SelectTable((huffmanFlags >> 14) & 0x01, [1], custom, "SBHUFFRSIZE"));

            var reader = new Jbig2BitReader(data[position..]);
            var symbolCodes = TextRegionDecoder.ReadSymbolCodeTable(ref reader, symbols.Length);
            return TextRegionDecoder.DecodeHuffman(
                ref reader, parameters, symbols, tables, symbolCodes, codeLength: 0,
                refinementContexts, refinementAt, state.Budget);
        }

        var codeLength = TextRegionDecoder.SymbolCodeLength(symbols.Length);
        var idContexts = new byte[ArithIntDecoder.IdContextSize(codeLength)];

        var mq = new MqDecoder(data[position..]);
        return TextRegionDecoder.Decode(
            ref mq, parameters, symbols, new TextRegionDecoder.Fields(),
            idContexts, refinementContexts, refinementAt, state.Budget);
    }

    /// <summary>
    /// Resolves one Huffman table selection field. <paramref name="standard"/> maps
    /// each value to a standard table number, 0 marking a reserved value; the value
    /// one past the list (3 for a two-bit field, 1 for a one-bit field) means "the
    /// next custom table".
    /// </summary>
    private static HuffmanTable SelectTable(int selection, int[] standard, Queue<HuffmanTable> custom, string field)
    {
        var customValue = standard.Length == 1 ? 1 : 3;
        if (selection == customValue)
            return custom.Count > 0
                ? custom.Dequeue()
                : throw new InvalidDataException($"JBIG2: {field} selects a custom Huffman table the segment does not refer to.");

        var number = selection < standard.Length ? standard[selection] : 0;
        return number != 0
            ? HuffmanTable.Get(number)
            : throw new InvalidDataException($"JBIG2: {field} selection {selection} is reserved.");
    }

    /// <summary>
    /// Pattern dictionary segment (T.88 §7.4.4): flags, the pattern size, the
    /// highest grey level, then one wide collective bitmap holding every pattern.
    /// </summary>
    private static Jbig2Bitmap[] DecodePatternDictionary(ReadOnlySpan<byte> data, Jbig2PixelBudget budget)
    {
        if (data.Length < 7)
            throw new InvalidDataException("JBIG2: truncated pattern dictionary segment.");

        var flags = data[0];
        var mmr = (flags & 0x01) != 0;
        var template = (flags >> 1) & 0x03;

        int patternWidth = data[1];
        int patternHeight = data[2];
        var maxIndex = BinaryPrimitives.ReadUInt32BigEndian(data[3..]);

        if (patternWidth == 0 || patternHeight == 0)
            throw new InvalidDataException($"JBIG2: pattern dictionary has a {patternWidth}x{patternHeight} pattern size.");

        // GRAYMAX is a four-byte field (§7.4.5.1.4), and real dictionaries go past
        // 255 (a 20x20 cell has 401 grey levels). The collective bitmap is
        // (GRAYMAX + 1) patterns wide, so bounding that one bitmap bounds the
        // allocation; the pixel budget is charged by the region decoder as usual.
        var collectiveWidth = (maxIndex + 1L) * patternWidth;
        if (collectiveWidth > int.MaxValue || collectiveWidth * patternHeight > Jbig2Limits.MaxBitmapPixels)
            throw new InvalidDataException(
                $"JBIG2: pattern dictionary GRAYMAX {maxIndex} with {patternWidth}x{patternHeight} patterns " +
                $"needs a {collectiveWidth}x{patternHeight} collective bitmap, past the " +
                $"{Jbig2Limits.MaxBitmapPixels:N0} pixel ceiling.");

        if (mmr)
            return HalftoneDecoder.DecodePatternDictionaryMmr(
                data[7..], patternWidth, patternHeight, (int)maxIndex, budget);

        var mq = new MqDecoder(data[7..]);
        return HalftoneDecoder.DecodePatternDictionary(
            ref mq, patternWidth, patternHeight, (int)maxIndex, template, budget);
    }

    /// <summary>
    /// Halftone region segment (T.88 §7.4.5): region info, flags, the grid
    /// geometry, then the grey-code bitplanes.
    /// </summary>
    private static Jbig2Bitmap DecodeHalftoneRegion(
        ReadOnlySpan<byte> data, SegmentHeader segment, DecodingState state, out RegionInfo info)
    {
        var position = 0;
        var region = Jbig2Segment.ReadRegionInfo(data, ref position);
        info = region;

        // Flags, HGW, HGH, HGX, HGY, HRX, HRY — 1 + 4 + 4 + 4 + 4 + 2 + 2.
        if (position + 21 > data.Length)
            throw new InvalidDataException("JBIG2: truncated halftone region segment.");

        var flags = data[position++];
        var mmr = (flags & 0x01) != 0;
        var template = (flags >> 1) & 0x03;
        var enableSkip = (flags & 0x08) != 0;
        var combination = (CombinationOperator)((flags >> 4) & 0x07);
        var defaultPixel = (byte)((flags >> 7) & 0x01);

        // HENABLESKIP changes what gets coded, not just how fast: skipped cells are
        // absent from the stream entirely, which is why HalftoneDecoder rebuilds
        // the same SKIP bitmap the encoder used before reading any plane.
        if (combination > CombinationOperator.Replace)
            throw new InvalidDataException($"JBIG2: reserved halftone combination operator {(int)combination}.");

        var gridWidth = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
        var gridHeight = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]);
        var gridX = BinaryPrimitives.ReadInt32BigEndian(data[(position + 8)..]);
        var gridY = BinaryPrimitives.ReadInt32BigEndian(data[(position + 12)..]);
        var vectorX = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 16)..]);
        var vectorY = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 18)..]);
        position += 20;

        if (gridWidth == 0 || gridHeight == 0
            || (long)gridWidth * gridHeight > Jbig2Limits.MaxHalftoneGridCells)
            throw new InvalidDataException(
                $"JBIG2: halftone grid {gridWidth}x{gridHeight} is {(long)gridWidth * gridHeight:N0} cells, " +
                $"past the {Jbig2Limits.MaxHalftoneGridCells:N0} cell ceiling.");

        var patterns = state.FindPatterns(segment)
            ?? throw new InvalidDataException("JBIG2: halftone region refers to no pattern dictionary.");

        var parameters = new HalftoneDecoder.HalftoneParameters(
            region.Width, region.Height, (int)gridWidth, (int)gridHeight,
            gridX, gridY, vectorX, vectorY, template, defaultPixel, combination, mmr, enableSkip);

        return HalftoneDecoder.DecodeHalftoneRegion(data[position..], parameters, patterns, state.Budget);
    }

}
