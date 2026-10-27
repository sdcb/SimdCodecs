using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// One tile-part's coded data: where its bitstream begins and ends inside the
/// codestream (T.800 A.4.2).
/// </summary>
/// <param name="TileIndex">Isot — which tile this part belongs to.</param>
/// <param name="PartIndex">TPsot — this part's position in that tile's sequence.</param>
/// <param name="Start">Offset of the first byte after SOD.</param>
/// <param name="Length">Bytes of packet data in this part.</param>
internal readonly record struct TilePart(int TileIndex, int PartIndex, int Start, int Length);

/// <summary>
/// Everything the main header said, plus where each tile-part's data lives.
/// </summary>
/// <param name="Cod">The main header's COD: the coding style of any component no COC overrides.</param>
/// <param name="Qcd">The main header's QCD: the quantization of any component no QCC overrides.</param>
/// <param name="ComponentCoding">
/// Each component's coding style with its COC applied (T.800 A.6.2). This, not <see cref="Cod"/>, is
/// what the pipeline reads: a COC overrides COD for its component whichever comes first in the header.
/// </param>
/// <param name="ComponentQuantization">Each component's quantization with its QCC applied (A.6.5).</param>
internal sealed record CodestreamHeader(
    SizMarker Siz,
    CodingStyle Cod,
    Quantization Qcd,
    CodingStyle[] ComponentCoding,
    Quantization[] ComponentQuantization,
    int Layers,
    ProgressionOrder Progression,
    bool MultipleComponentTransform,
    bool UseSopMarkers,
    bool UseEphMarkers,
    List<TilePart> TileParts);

/// <summary>
/// Walks a raw JPEG 2000 codestream (T.800 Annex A), parsing the main header
/// markers and locating each tile-part's coded data.
/// <para>
/// This layer does no entropy decoding. It exists so that a codestream outside
/// the implemented envelope is refused <b>here</b>, by name, before any of the
/// pipeline runs — the discipline the rest of this repo already holds to, and
/// the reason <c>Sdcb.SimdCodecs.Jbig2</c> throws <see cref="NotSupportedException"/>
/// naming SDHUFF rather than returning a plausible-looking raster.
/// </para>
/// <para>
/// The distinction kept throughout: <see cref="InvalidDataException"/> means the
/// bytes are malformed or hostile, <see cref="NotSupportedException"/> means
/// they are a perfectly legal codestream using a feature this rung does not
/// implement yet. A caller can reasonably retry the second with another decoder
/// and cannot do anything with the first.
/// </para>
/// </summary>
internal static class CodestreamReader
{
    /// <summary>The raw-J2K magic: SOC immediately followed by SIZ.</summary>
    public static bool LooksLikeCodestream(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0xFF && data[1] == 0x4F && data[2] == 0xFF && data[3] == 0x51;

    /// <summary>Parses the main header and indexes the tile-parts.</summary>
    /// <exception cref="InvalidDataException">The codestream is malformed.</exception>
    /// <exception cref="NotSupportedException">It uses a feature this decoder does not implement.</exception>
    public static CodestreamHeader Read(ReadOnlySpan<byte> data)
    {
        if (!LooksLikeCodestream(data))
            throw new InvalidDataException(
                "JPEG 2000: not a raw codestream — it does not begin with SOC (FF4F) followed by SIZ (FF51).");

        var offset = 2; // past SOC
        SizMarker? siz = null;
        CodingStyle? cod = null;
        Quantization? qcd = null;
        var layers = 0;
        var progression = ProgressionOrder.Lrcp;
        var mct = false;
        var sop = false;
        var eph = false;
        var tileParts = new List<TilePart>();

        // COC and QCC may come before or after the COD and QCD they override, so they are
        // collected here and applied once the whole main header has been read.
        var codingOverrides = new Dictionary<int, CodingStyle>();
        var quantizationOverrides = new Dictionary<int, Quantization>();

        while (offset < data.Length)
        {
            var marker = ReadUInt16(data, ref offset, "marker");
            if (marker == Markers.Eoc) break;

            if (marker == Markers.Sod)
                throw new InvalidDataException("JPEG 2000: SOD outside a tile-part.");

            if (marker == Markers.Sot)
            {
                ReadTilePart(data, ref offset, tileParts);
                continue;
            }

            // Every other marker in the main header carries a 16-bit length that
            // COUNTS ITSELF, so the segment body is length - 2 bytes.
            var segmentStart = offset;
            var length = ReadUInt16(data, ref offset, "marker segment length");
            if (length < 2 || segmentStart + length > data.Length)
                throw new InvalidDataException(
                    $"JPEG 2000: marker {marker:X4} at offset {segmentStart - 2} declares a {length}-byte " +
                    "segment that runs past the end of the codestream.");

            var body = data.Slice(offset, length - 2);
            offset = segmentStart + length;

            switch (marker)
            {
                case Markers.Siz:
                    siz = ParseSiz(body);
                    break;

                case Markers.Cod:
                    (cod, layers, progression, mct, sop, eph) = ParseCod(body);
                    break;

                case Markers.Qcd:
                    qcd = ParseQcd(body);
                    break;

                // Deliberately skipped: pure indexes and metadata that change
                // nothing about how the coded data is read.
                case Markers.Com:
                case Markers.Tlm:
                case Markers.Plm:
                case Markers.Plt:
                case Markers.Crg:
                    break;

                case Markers.Coc:
                {
                    var (component, style) = ParseCoc(body, RequireSiz(siz, "COC"));
                    codingOverrides[component] = style;
                    break;
                }

                case Markers.Qcc:
                {
                    var (component, quantization) = ParseQcc(body, RequireSiz(siz, "QCC"));
                    quantizationOverrides[component] = quantization;
                    break;
                }

                case Markers.Poc:
                    throw new NotSupportedException(
                        "JPEG 2000: progression order changes (POC) are not implemented.");

                case Markers.Ppm:
                    throw new NotSupportedException(
                        "JPEG 2000: packed packet headers (PPM) are not implemented. The packet headers live " +
                        "in a separate marker segment rather than ahead of their packets, so ignoring this " +
                        "would desynchronise tier-2 rather than merely lose a feature.");

                case Markers.Rgn:
                    throw new NotSupportedException(
                        "JPEG 2000: regions of interest (RGN / MAXSHIFT) are refused. PDF does not require " +
                        "them, and a decoder that ignored the marker would silently reconstruct the " +
                        "region-of-interest coefficients at the wrong magnitude.");

                default:
                    throw new NotSupportedException(
                        $"JPEG 2000: unrecognised marker {marker:X4} in the main header. Refusing rather " +
                        "than skipping it: a marker this decoder does not know may change how the bytes " +
                        "after it are to be read.");
            }
        }

        if (siz is null) throw new InvalidDataException("JPEG 2000: no SIZ marker.");
        if (cod is null) throw new InvalidDataException("JPEG 2000: no COD marker.");
        if (qcd is null) throw new InvalidDataException("JPEG 2000: no QCD marker.");
        if (tileParts.Count == 0) throw new InvalidDataException("JPEG 2000: no tile-part data (no SOT).");

        var componentCoding = new CodingStyle[siz.Components.Length];
        var componentQuantization = new Quantization[siz.Components.Length];
        for (var c = 0; c < componentCoding.Length; c++)
        {
            componentCoding[c] = codingOverrides.GetValueOrDefault(c, cod);
            componentQuantization[c] = quantizationOverrides.GetValueOrDefault(c, qcd);
        }

        var header = new CodestreamHeader(
            siz, cod, qcd, componentCoding, componentQuantization,
            layers, progression, mct, sop, eph, tileParts);
        Validate(header);
        return header;
    }

    /// <summary>
    /// A COC or QCC names its component in one byte, or in two when SIZ declares more than 256
    /// components (A.6.2), so it cannot be read before SIZ. SIZ must follow SOC directly anyway.
    /// </summary>
    private static SizMarker RequireSiz(SizMarker? siz, string marker) =>
        siz ?? throw new InvalidDataException($"JPEG 2000: {marker} before SIZ.");

    /// <summary>Reads a component index, one byte or two by how many components SIZ declared.</summary>
    private static int ReadComponentIndex(ReadOnlySpan<byte> body, SizMarker siz, ref int offset, string marker)
    {
        var wide = siz.Components.Length > 256;
        if (body.Length < offset + (wide ? 2 : 1))
            throw new InvalidDataException($"JPEG 2000: {marker} segment is too short.");

        int component = wide ? BinaryPrimitives.ReadUInt16BigEndian(body[offset..]) : body[offset];
        offset += wide ? 2 : 1;

        if (component >= siz.Components.Length)
            throw new InvalidDataException(
                $"JPEG 2000: {marker} names component {component}, but SIZ declares only " +
                $"{siz.Components.Length}.");

        return component;
    }

    /// <summary>
    /// Reads one SOT segment and the tile-part data that follows its SOD.
    /// </summary>
    private static void ReadTilePart(ReadOnlySpan<byte> data, ref int offset, List<TilePart> tileParts)
    {
        var sotStart = offset - 2;
        var length = ReadUInt16(data, ref offset, "SOT length");
        if (length != 10)
            throw new InvalidDataException($"JPEG 2000: SOT declares Lsot={length}, which must be 10.");

        var tileIndex = ReadUInt16(data, ref offset, "Isot");
        var psot = (int)ReadUInt32(data, ref offset, "Psot");
        var partIndex = data[offset++];
        offset++; // TNsot — how many parts this tile has; unused while multi-part is refused.

        // Psot counts from the first byte of the SOT MARKER, not from here. Zero
        // is legal and means "to the end of the codestream, or to the next SOT",
        // which only the last tile-part may use (A.4.2).
        var tilePartEnd = psot == 0 ? data.Length : sotStart + psot;
        if (tilePartEnd > data.Length || tilePartEnd <= offset)
            throw new InvalidDataException(
                $"JPEG 2000: tile-part at offset {sotStart} declares Psot={psot}, which does not fit the " +
                "codestream.");

        // Between SOT and SOD sit the tile-part header markers. Rung 1 decodes a
        // single-tile-part codestream whose coding style is entirely in the main
        // header, so anything here is out of envelope and is named as such.
        while (true)
        {
            var marker = ReadUInt16(data, ref offset, "tile-part header marker");
            if (marker == Markers.Sod) break;

            var segmentStart = offset;
            var segmentLength = ReadUInt16(data, ref offset, "tile-part marker segment length");
            if (segmentLength < 2 || segmentStart + segmentLength > data.Length)
                throw new InvalidDataException(
                    $"JPEG 2000: marker {marker:X4} in a tile-part header declares a {segmentLength}-byte " +
                    "segment that runs past the end of the codestream.");
            offset = segmentStart + segmentLength;

            switch (marker)
            {
                case Markers.Com:
                case Markers.Plt:
                    break;

                case Markers.Ppt:
                    throw new NotSupportedException(
                        "JPEG 2000: packed packet headers (PPT) are not implemented.");

                case Markers.Cod:
                case Markers.Coc:
                case Markers.Qcd:
                case Markers.Qcc:
                case Markers.Poc:
                case Markers.Rgn:
                    throw new NotSupportedException(
                        $"JPEG 2000: marker {marker:X4} in a tile-part header overrides the main header for " +
                        "one tile, which is not implemented.");

                default:
                    throw new NotSupportedException(
                        $"JPEG 2000: unrecognised marker {marker:X4} in a tile-part header.");
            }
        }

        tileParts.Add(new TilePart(tileIndex, partIndex, offset, tilePartEnd - offset));
        offset = tilePartEnd;
    }

    private static SizMarker ParseSiz(ReadOnlySpan<byte> body)
    {
        // Rsiz(2) X1(4) Y1(4) X0(4) Y0(4) XT(4) YT(4) XT0(4) YT0(4) C(2) = 36
        if (body.Length < 36)
            throw new InvalidDataException("JPEG 2000: SIZ segment is too short.");

        var offset = 2; // Rsiz — capabilities; nothing here depends on it.
        var x1 = (int)ReadUInt32(body, ref offset, "Xsiz");
        var y1 = (int)ReadUInt32(body, ref offset, "Ysiz");
        var x0 = (int)ReadUInt32(body, ref offset, "XOsiz");
        var y0 = (int)ReadUInt32(body, ref offset, "YOsiz");
        var tileWidth = (int)ReadUInt32(body, ref offset, "XTsiz");
        var tileHeight = (int)ReadUInt32(body, ref offset, "YTsiz");
        var tileX0 = (int)ReadUInt32(body, ref offset, "XTOsiz");
        var tileY0 = (int)ReadUInt32(body, ref offset, "YTOsiz");
        var componentCount = ReadUInt16(body, ref offset, "Csiz");

        // These are 32-bit fields read into int, so the sign is the first thing
        // to check: a codestream declaring Xsiz = 0x80000000 would otherwise
        // arrive as a negative width and sail through every `>` bound below.
        if (x0 < 0 || y0 < 0 || x1 <= x0 || y1 <= y0)
            throw new InvalidDataException(
                $"JPEG 2000: SIZ declares an empty or out-of-range image region ({x0},{y0})-({x1},{y1}).");
        if (tileWidth <= 0 || tileHeight <= 0 || tileX0 < 0 || tileY0 < 0 || tileX0 > x0 || tileY0 > y0)
            throw new InvalidDataException(
                $"JPEG 2000: SIZ declares an invalid tile grid: origin ({tileX0},{tileY0}), " +
                $"size {tileWidth}x{tileHeight}.");

        if (componentCount is 0 or > Jpeg2000Limits.MaxComponents)
            throw new InvalidDataException(
                $"JPEG 2000: SIZ declares {componentCount} components; the limit here is " +
                $"{Jpeg2000Limits.MaxComponents}.");

        if (body.Length < offset + componentCount * 3)
            throw new InvalidDataException(
                $"JPEG 2000: SIZ declares {componentCount} components but is too short to describe them.");

        var components = new ComponentInfo[componentCount];
        for (var c = 0; c < componentCount; c++)
        {
            var ssiz = body[offset++];
            var horizontal = body[offset++];
            var vertical = body[offset++];

            if (horizontal == 0 || vertical == 0)
                throw new InvalidDataException(
                    $"JPEG 2000: component {c} declares a zero subsampling factor.");

            components[c] = new ComponentInfo(
                BitDepth: (ssiz & 0x7F) + 1,
                IsSigned: (ssiz & 0x80) != 0,
                HorizontalSeparation: horizontal,
                VerticalSeparation: vertical);
        }

        return new SizMarker(x0, y0, x1, y1, tileX0, tileY0, tileWidth, tileHeight, components);
    }

    private static (CodingStyle Cod, int Layers, ProgressionOrder Progression, bool Mct, bool Sop, bool Eph)
        ParseCod(ReadOnlySpan<byte> body)
    {
        // Scod(1) ProgOrder(1) Layers(2) MCT(1), then SPcod: Levels(1) xcb(1) ycb(1) style(1) transform(1) = 10
        if (body.Length < 10)
            throw new InvalidDataException("JPEG 2000: COD segment is too short.");

        var scod = body[0];
        var progression = (ProgressionOrder)body[1];
        var layers = BinaryPrimitives.ReadUInt16BigEndian(body[2..]);
        var mct = body[4] != 0;

        if (!Enum.IsDefined(progression))
            throw new InvalidDataException($"JPEG 2000: COD declares progression order {body[1]}, which T.800 does not define.");
        if (layers == 0)
            throw new InvalidDataException("JPEG 2000: COD declares zero quality layers.");

        var style = ParseCodingStyle(body[5..], (scod & 0x01) != 0, "COD");
        return (style, layers, progression, mct, (scod & 0x02) != 0, (scod & 0x04) != 0);
    }

    /// <summary>
    /// COC (A.6.2): one component's coding style. Its SPcoc is laid out exactly as COD's SPcod, and
    /// its Scoc carries only the precinct bit, since progression, layers and the component transform
    /// are properties of the whole codestream and stay COD's.
    /// </summary>
    private static (int Component, CodingStyle Style) ParseCoc(ReadOnlySpan<byte> body, SizMarker siz)
    {
        var offset = 0;
        var component = ReadComponentIndex(body, siz, ref offset, "COC");
        if (body.Length < offset + 1)
            throw new InvalidDataException("JPEG 2000: COC segment is too short.");

        var scoc = body[offset++];
        return (component, ParseCodingStyle(body[offset..], (scoc & 0x01) != 0, "COC"));
    }

    /// <summary>
    /// SPcod / SPcoc (Table A.15): decomposition levels, code-block size and style, the wavelet,
    /// and the precinct sizes when the style byte before it signalled custom ones.
    /// </summary>
    private static CodingStyle ParseCodingStyle(ReadOnlySpan<byte> spcod, bool customPrecincts, string marker)
    {
        if (spcod.Length < 5)
            throw new InvalidDataException($"JPEG 2000: {marker} segment is too short.");

        var levels = spcod[0];

        // xcb and ycb are stored biased by 2: the value 4 means 2^6 = 64.
        var codeBlockWidthExponent = (spcod[1] & 0x0F) + 2;
        var codeBlockHeightExponent = (spcod[2] & 0x0F) + 2;
        var codeBlockStyle = spcod[3];
        var transform = (WaveletTransform)spcod[4];

        if (!Enum.IsDefined(transform))
            throw new InvalidDataException($"JPEG 2000: {marker} declares wavelet transform {spcod[4]}, which T.800 does not define.");
        if (levels > Jpeg2000Limits.MaxDecompositionLevels)
            throw new InvalidDataException(
                $"JPEG 2000: {marker} declares {levels} decomposition levels; the limit here is " +
                $"{Jpeg2000Limits.MaxDecompositionLevels}.");

        // T.800 Table A.18: no code-block dimension below 4 or above 1024, and
        // no more than 4096 coefficients in one block.
        if (codeBlockWidthExponent is < 2 or > 10 || codeBlockHeightExponent is < 2 or > 10 ||
            codeBlockWidthExponent + codeBlockHeightExponent > 12)
            throw new InvalidDataException(
                $"JPEG 2000: {marker} declares a code-block of 2^{codeBlockWidthExponent} x " +
                $"2^{codeBlockHeightExponent}, outside what T.800 Table A.18 permits.");

        var precinctSizes = Array.Empty<byte>();
        if (customPrecincts)
        {
            if (spcod.Length < 5 + levels + 1)
                throw new InvalidDataException(
                    $"JPEG 2000: {marker} signals custom precincts but does not carry one size per resolution.");
            precinctSizes = spcod.Slice(5, levels + 1).ToArray();
        }

        return new CodingStyle(
            levels,
            codeBlockWidthExponent,
            codeBlockHeightExponent,
            codeBlockStyle,
            transform,
            precinctSizes);
    }

    /// <summary>
    /// QCC (A.6.5): one component's quantization. After the component index it is laid out exactly
    /// as QCD.
    /// </summary>
    private static (int Component, Quantization Quantization) ParseQcc(ReadOnlySpan<byte> body, SizMarker siz)
    {
        var offset = 0;
        var component = ReadComponentIndex(body, siz, ref offset, "QCC");
        return (component, ParseQcd(body[offset..]));
    }

    private static Quantization ParseQcd(ReadOnlySpan<byte> body)
    {
        if (body.Length < 1)
            throw new InvalidDataException("JPEG 2000: QCD segment is too short.");

        var sqcd = body[0];
        var style = (QuantizationStyle)(sqcd & 0x1F);
        var guardBits = sqcd >> 5;
        var values = body[1..];

        switch (style)
        {
            case QuantizationStyle.None:
            {
                // One byte per subband; the exponent is the top five bits.
                var exponents = new int[values.Length];
                for (var i = 0; i < values.Length; i++) exponents[i] = values[i] >> 3;
                return new Quantization(style, guardBits, exponents, new int[values.Length]);
            }

            case QuantizationStyle.ScalarDerived:
            case QuantizationStyle.ScalarExpounded:
            {
                if (values.Length % 2 != 0)
                    throw new InvalidDataException("JPEG 2000: QCD step sizes are not a whole number of 16-bit values.");

                var count = values.Length / 2;
                var exponents = new int[count];
                var mantissas = new int[count];
                for (var i = 0; i < count; i++)
                {
                    var packed = BinaryPrimitives.ReadUInt16BigEndian(values[(i * 2)..]);
                    exponents[i] = packed >> 11;
                    mantissas[i] = packed & 0x07FF;
                }

                return new Quantization(style, guardBits, exponents, mantissas);
            }

            default:
                throw new InvalidDataException(
                    $"JPEG 2000: QCD declares quantization style {sqcd & 0x1F}, which T.800 Table A.28 does not define.");
        }
    }

    /// <summary>
    /// Refuses, by name, everything outside the envelope this decoder has actually
    /// been validated against. Each message says which rung owns the feature, so
    /// the refusal reads as a roadmap position rather than a dead end.
    /// </summary>
    private static void Validate(CodestreamHeader header)
    {
        var siz = header.Siz;

        // Every component must fit the one sample format the output carries: unsigned, at most 16
        // bits, at full resolution, and all at the same precision. Each of those is a real feature
        // (signed data, deep data, chroma subsampling, mixed depths) that nothing here has a fixture
        // for yet.
        var first = siz.Components[0];
        for (var c = 0; c < siz.Components.Length; c++)
        {
            var component = siz.Components[c];
            if (component.IsSigned)
                throw new NotSupportedException(
                    "JPEG 2000: signed components are not implemented.");
            if (component.BitDepth is < 1 or > 16)
                throw new NotSupportedException(
                    $"JPEG 2000: {component.BitDepth}-bit components are not implemented; the limit here is 16.");
            if (component.HorizontalSeparation != 1 || component.VerticalSeparation != 1)
                throw new NotSupportedException(
                    "JPEG 2000: component subsampling is not implemented.");
            if (component.BitDepth != first.BitDepth)
                throw new NotSupportedException(
                    $"JPEG 2000: component {c} is {component.BitDepth}-bit where component 0 is " +
                    $"{first.BitDepth}-bit. Components of different precisions are not implemented.");
        }

        if (siz.TileCount != 1)
            throw new NotSupportedException(
                $"JPEG 2000: this codestream has {siz.TileCount} tiles; only single-tile images are " +
                "implemented. Multiple tiles are rung 3.");
        if (header.TileParts.Count != 1)
            throw new NotSupportedException(
                $"JPEG 2000: this tile is split into {header.TileParts.Count} tile-parts, which is rung 3.");

        if (header.Progression != ProgressionOrder.Lrcp)
            throw new NotSupportedException(
                $"JPEG 2000: progression order {header.Progression} is not implemented; only LRCP is. " +
                "With one tile and maximal precincts the five orders differ only in how they nest layer, " +
                "resolution and component, and only LRCP's nesting has a fixture behind it.");

        if (header.UseSopMarkers || header.UseEphMarkers)
            throw new NotSupportedException(
                "JPEG 2000: SOP and EPH markers are not implemented; they are rung 3.");

        for (var c = 0; c < siz.Components.Length; c++)
        {
            var coding = header.ComponentCoding[c];
            var quantization = header.ComponentQuantization[c];

            // The 5/3 wavelet carries integers that need no step size, the 9/7 one quantization
            // indices that do. Encoders pair them that way, and the other two pairings would each
            // need a reading of the step this decoder has no fixture for: refused, not guessed.
            var reversible = coding.Transform == WaveletTransform.Reversible53;
            if (reversible && quantization.Style != QuantizationStyle.None)
                throw new NotSupportedException(
                    $"JPEG 2000: component {c} uses the reversible 5/3 wavelet with {quantization.Style} " +
                    "quantization. Only unquantized coefficients are implemented for the 5/3 wavelet.");
            if (!reversible && quantization.Style == QuantizationStyle.None)
                throw new NotSupportedException(
                    $"JPEG 2000: component {c} uses the irreversible 9/7 wavelet with no quantization step " +
                    "sizes. Only scalar quantization, derived or expounded, is implemented for the 9/7 " +
                    "wavelet.");

            if (coding.CodeBlockStyle != 0)
                throw new NotSupportedException(
                    $"JPEG 2000: code-block style flags 0x{coding.CodeBlockStyle:X2} are not implemented " +
                    "(T.800 Table A.19: selective arithmetic bypass, context reset, termination per pass, " +
                    "vertically causal context, predictable termination, segmentation symbols). Each changes " +
                    "how the coded bytes are read, so ignoring one desynchronises tier-1 rather than degrading " +
                    "quality.");

            if (coding.PrecinctSizes.Length != 0)
                throw new NotSupportedException(
                    "JPEG 2000: custom precinct sizes are not implemented; only the maximal default, which " +
                    "makes each resolution a single precinct. Precincts are rung 3.");
        }

        if (header.MultipleComponentTransform)
        {
            // A.6.1: the transform applies to components 0, 1 and 2, as RCT when they use the 5/3
            // wavelet and as ICT when they use the 9/7. A codestream that sets it with fewer than
            // three components, or with the three on different wavelets, names no transform at all.
            if (siz.Components.Length < 3)
                throw new InvalidDataException(
                    $"JPEG 2000: COD sets the multiple component transform on a {siz.Components.Length}-" +
                    "component image; it needs three.");

            var transform = header.ComponentCoding[0].Transform;
            if (header.ComponentCoding[1].Transform != transform || header.ComponentCoding[2].Transform != transform)
                throw new InvalidDataException(
                    "JPEG 2000: COD sets the multiple component transform, but components 0 to 2 do not " +
                    "share one wavelet, so neither RCT nor ICT applies.");
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, ref int offset, string what)
    {
        if (offset + 2 > data.Length)
            throw new InvalidDataException($"JPEG 2000: codestream ends inside {what}.");

        var value = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        return value;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int offset, string what)
    {
        if (offset + 4 > data.Length)
            throw new InvalidDataException($"JPEG 2000: codestream ends inside {what}.");

        var value = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        offset += 4;
        return value;
    }
}
