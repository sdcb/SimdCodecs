using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// The enumerated colour spaces of a JP2 <c>colr</c> box (T.800 Table I.10). Part 1 defines these
/// three; any other number is kept as it was read, so a caller can see what it was and refuse it.
/// </summary>
public enum Jp2EnumeratedColourSpace
{
    /// <summary>sRGB (IEC 61966-2-1).</summary>
    Srgb = 16,

    /// <summary>Greyscale, with sRGB's transfer curve.</summary>
    Greyscale = 17,

    /// <summary>sYCC: YCbCr derived from sRGB, which still has to be converted to RGB to be shown.</summary>
    Sycc = 18,
}

/// <summary>
/// What a JP2 file's first <c>colr</c> box says the decoded components mean (T.800 I.5.3.3).
/// <para>
/// Reported, never applied. The codestream's own component transform is undone by the decoder,
/// because that is part of how the samples were coded; this is a statement about what the samples
/// then ARE, and the right response to it depends on the caller. A PDF reader in particular has its
/// own rule (ISO 32000-2 8.9.5, the image dictionary's <c>/ColorSpace</c>): when the dictionary
/// names a colour space, this specification is ignored, and only when it names none is this one
/// used. Only the caller holds the dictionary, so only the caller can apply that.
/// </para>
/// </summary>
/// <param name="Method">METH: 1 for an enumerated colour space, 2 for a restricted ICC profile.</param>
/// <param name="EnumeratedColourSpace">The EnumCS value when <paramref name="Method"/> is 1.</param>
/// <param name="IccProfile">The profile bytes when <paramref name="Method"/> is 2.</param>
public sealed record Jp2Colour(int Method, Jp2EnumeratedColourSpace? EnumeratedColourSpace, byte[]? IccProfile);

/// <summary>What a channel of a decoded image is (T.800 Table I.16, the channel definition box's Typ).</summary>
public enum Jp2ChannelType
{
    /// <summary>A colour channel of the colour space; <see cref="Jp2Channel.Association"/> says which one (1 is the first).</summary>
    Colour = 0,

    /// <summary>Opacity (alpha): 0 is transparent, the maximum sample is opaque. Straight, not multiplied into the colour.</summary>
    Opacity = 1,

    /// <summary>Opacity whose value has already been multiplied into the colour channels it applies to.</summary>
    PremultipliedOpacity = 2,

    /// <summary>A channel whose use is not specified (Typ 65535, or a channel the box does not describe).</summary>
    Unspecified = 65535,
}

/// <summary>
/// One channel of an image whose JP2 file has a channel definition box (T.800 I.5.3.6): what it is,
/// and what it belongs to.
/// </summary>
/// <param name="Type">What the channel is.</param>
/// <param name="Association">
/// For a colour channel, which colour of the colour space it is (1 to the number of colour
/// channels; for sRGB 1 is red, 2 green, 3 blue). For an opacity channel, 0 when it applies to the
/// whole image or 1 and up for the one colour it applies to. -1 for an unspecified channel.
/// </param>
public sealed record Jp2Channel(Jp2ChannelType Type, int Association);

/// <summary>Where a JP2 file's codestream is, and what its header said about colour.</summary>
internal readonly record struct Jp2Layout(int CodestreamStart, int CodestreamLength, Jp2Colour? Colour)
{
    /// <summary>The channel definition box, or null when the header has none.</summary>
    public Jp2ChannelDefinition? Definition { get; init; }

    /// <summary>
    /// The palette and component mapping boxes, or null when the header has neither and the
    /// codestream's components are the image's channels as they stand.
    /// </summary>
    public Jp2ChannelMapping? Channels { get; init; }
}

/// <summary>
/// A JP2 palette (<c>pclr</c>, T.800 I.5.3.4) together with the component mapping (<c>cmap</c>,
/// I.5.3.5) that says which channel of the image comes from which component, directly or through
/// which palette column.
/// </summary>
/// <param name="Entries">NE, the number of palette entries; 0 when there is no palette.</param>
/// <param name="Columns">NPC, the number of palette columns.</param>
/// <param name="ColumnDepths">The bit depth of each palette column.</param>
/// <param name="Values">The palette, <c>Values[entry * Columns + column]</c>.</param>
/// <param name="Map">Each output channel: the codestream component, and the palette column or -1 for direct use.</param>
internal sealed record Jp2ChannelMapping(
    int Entries, int Columns, int[] ColumnDepths, ushort[] Values, (int Component, int Column)[] Map)
{
    /// <summary>
    /// The bit depth of every output channel, which must agree, since the decoded image carries one
    /// precision. <paramref name="componentDepths"/> supplies it for the directly mapped channels.
    /// </summary>
    /// <exception cref="InvalidDataException">The mapping names a component that does not exist.</exception>
    /// <exception cref="NotSupportedException">The channels come out at different precisions.</exception>
    public int OutputDepth(IReadOnlyList<int> componentDepths)
    {
        var depth = 0;
        foreach (var (component, column) in Map)
        {
            if (component >= componentDepths.Count)
                throw new InvalidDataException(
                    $"JPEG 2000: the component mapping box names component {component}, but the codestream has " +
                    $"{componentDepths.Count}.");

            var channelDepth = column < 0 ? componentDepths[component] : ColumnDepths[column];
            if (depth != 0 && channelDepth != depth)
                throw new NotSupportedException(
                    "JPEG 2000: the JP2 channels come out at different bit depths, and the decoded image has one " +
                    "precision for all of them.");
            depth = channelDepth;
        }

        return depth;
    }
}

/// <summary>
/// A JP2 channel definition (<c>cdef</c>, T.800 I.5.3.6) as read: one entry per described channel,
/// indexed by channel number, where a channel is what is left after the palette and component
/// mapping, or a codestream component when there are none.
/// </summary>
/// <param name="Entries">Each entry's channel number (Cn), type (Typ) and association (Asoc).</param>
internal sealed record Jp2ChannelDefinition((int Channel, int Type, int Association)[] Entries)
{
    /// <summary>
    /// Settles the definition against an image of <paramref name="channelCount"/> channels. Returns
    /// the order the decoded image presents them in, and what each is there: the colour channels
    /// first, in the order of the colour space (Asoc 1, 2, ...), then the opacity channels in channel
    /// order, then channels with no stated use (including any the box does not mention) in channel
    /// order.
    /// </summary>
    /// <returns><c>Order[i]</c> is the source channel of output channel i.</returns>
    /// <exception cref="InvalidDataException">A channel is out of range, a colour is not associated exactly once, or an opacity names a colour that is not there.</exception>
    public (int[] Order, Jp2Channel[] Channels) Resolve(int channelCount)
    {
        var colour = new List<(int Channel, int Association)>();
        var opacity = new List<(int Channel, Jp2Channel Description)>();
        var placed = new bool[channelCount];

        foreach (var (channel, type, association) in Entries)
        {
            if (channel >= channelCount)
                throw new InvalidDataException(
                    $"JPEG 2000: the channel definition box describes channel {channel}, but the image has {channelCount}.");

            switch (type)
            {
                case (int)Jp2ChannelType.Colour:
                    colour.Add((channel, association));
                    break;
                case (int)Jp2ChannelType.Opacity or (int)Jp2ChannelType.PremultipliedOpacity:
                    opacity.Add((channel, new Jp2Channel((Jp2ChannelType)type, association == 0xFFFF ? -1 : association)));
                    break;
            }
        }

        // The colour channels are exactly the colours 1..k of the colour space, each once.
        colour.Sort(static (a, b) => a.Association.CompareTo(b.Association));
        for (var i = 0; i < colour.Count; i++)
        {
            if (colour[i].Association != i + 1)
                throw new InvalidDataException(
                    $"JPEG 2000: the channel definition box's {colour.Count} colour channels are not associated with " +
                    "colours 1 to " + colour.Count + " once each.");
        }

        var order = new List<int>(channelCount);
        var channels = new List<Jp2Channel>(channelCount);
        foreach (var (channel, association) in colour)
        {
            order.Add(channel);
            placed[channel] = true;
            channels.Add(new Jp2Channel(Jp2ChannelType.Colour, association));
        }

        opacity.Sort(static (a, b) => a.Channel.CompareTo(b.Channel));
        foreach (var (channel, description) in opacity)
        {
            if (description.Association > colour.Count)
                throw new InvalidDataException(
                    $"JPEG 2000: the channel definition box's opacity channel {channel} applies to colour " +
                    $"{description.Association}, but there are {colour.Count} colour channels.");

            order.Add(channel);
            placed[channel] = true;
            channels.Add(description);
        }

        for (var channel = 0; channel < channelCount; channel++)
        {
            if (placed[channel]) continue;
            order.Add(channel);
            channels.Add(new Jp2Channel(Jp2ChannelType.Unspecified, -1));
        }

        return ([.. order], [.. channels]);
    }
}

/// <summary>
/// Reads the JP2 file format of T.800 Annex I far enough to find the codestream and its colour
/// specification: the signature and file type boxes, the header superbox with its image header and
/// colour boxes, and the contiguous codestream box.
/// <para>
/// The palette (<c>pclr</c>) and component mapping (<c>cmap</c>) boxes are read here and applied by
/// the decoder: a palette turns one component of indices into channels of colour. The channel
/// definition box (<c>cdef</c>) is read here too and applied after them: it can say a channel is
/// opacity or reorder the colour channels, and the decoder puts the colour channels in the colour
/// space's order with opacity after them.
/// </para>
/// <para>
/// The image header's own width, height and component count are not checked against the
/// codestream's. Where they disagree the codestream is what decodes, and refusing a file over the
/// header's mistake would refuse images other readers show.
/// </para>
/// </summary>
internal static class Jp2Reader
{
    /// <summary>The signature box, which must be the file's first twelve bytes (I.5.1).</summary>
    private static ReadOnlySpan<byte> Signature =>
        [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    private const uint FileType = 0x66747970;        // 'ftyp'
    private const uint Header = 0x6A703268;          // 'jp2h'
    private const uint ImageHeader = 0x69686472;     // 'ihdr'
    private const uint ColourSpecification = 0x636F6C72; // 'colr'
    private const uint Palette = 0x70636C72;         // 'pclr'
    private const uint ComponentMapping = 0x636D6170; // 'cmap'
    private const uint ChannelDefinition = 0x63646566; // 'cdef'
    private const uint Codestream = 0x6A703263;      // 'jp2c'

    /// <summary>True when <paramref name="data"/> begins with the JP2 signature box.</summary>
    public static bool LooksLikeJp2(ReadOnlySpan<byte> data) =>
        data.Length >= Signature.Length && data[..Signature.Length].SequenceEqual(Signature);

    /// <summary>Finds the codestream and the colour specification.</summary>
    /// <exception cref="InvalidDataException">The box structure is malformed or has no codestream.</exception>
    /// <exception cref="NotSupportedException">A box changes the meaning of the components in a way not implemented.</exception>
    public static Jp2Layout Read(ReadOnlySpan<byte> data)
    {
        if (!LooksLikeJp2(data))
            throw new InvalidDataException("JPEG 2000: not a JP2 file — it does not begin with the signature box.");

        var offset = Signature.Length;
        var sawFileType = false;
        var sawHeader = false;
        Jp2Colour? colour = null;
        Jp2ChannelMapping? channels = null;
        Jp2ChannelDefinition? definition = null;

        while (offset < data.Length)
        {
            var (type, bodyStart, bodyLength) = ReadBox(data, offset);
            var body = data.Slice(bodyStart, bodyLength);

            switch (type)
            {
                case FileType:
                    // I.5.2: the brand list must name 'jp2 '. A file that does not claim Part 1
                    // compatibility is Part 2 (JPX) or something else, and is not this decoder's.
                    if (!ListsJp2Brand(body))
                        throw new NotSupportedException(
                            "JPEG 2000: the file type box does not list the 'jp2 ' brand, so this is not a " +
                            "Part 1 JP2 file (JPX, Part 2, is refused).");
                    sawFileType = true;
                    break;

                case Header:
                    if (!sawFileType)
                        throw new InvalidDataException("JPEG 2000: the JP2 header box comes before the file type box.");
                    (colour, channels, definition) = ReadHeader(body);
                    sawHeader = true;
                    break;

                case Codestream:
                    if (!sawHeader)
                        throw new InvalidDataException("JPEG 2000: the codestream box comes before the JP2 header box.");

                    // The first codestream is the image (I.5.4); anything after it is not read.
                    return new Jp2Layout(bodyStart, bodyLength, colour) { Channels = channels, Definition = definition };
            }

            // Every other top-level box (resolution, XML, UUID, IPR) is metadata about the image,
            // not part of decoding it, and is skipped.
            offset = bodyStart + bodyLength;
        }

        throw new InvalidDataException("JPEG 2000: the JP2 file has no contiguous codestream box.");
    }

    /// <summary>
    /// The header superbox: the image header first (I.5.3.1), then at least one colour
    /// specification, of which only the first counts (I.5.3.3).
    /// </summary>
    private static (Jp2Colour? Colour, Jp2ChannelMapping? Channels, Jp2ChannelDefinition? Definition) ReadHeader(ReadOnlySpan<byte> header)
    {
        Jp2Colour? colour = null;
        Jp2ChannelMapping? palette = null;
        (int Component, int Column)[]? map = null;
        Jp2ChannelDefinition? definition = null;
        var offset = 0;
        var first = true;

        while (offset < header.Length)
        {
            var (type, bodyStart, bodyLength) = ReadBox(header, offset);
            var body = header.Slice(bodyStart, bodyLength);

            if (first && type != ImageHeader)
                throw new InvalidDataException("JPEG 2000: the JP2 header box does not begin with an image header box.");
            first = false;

            switch (type)
            {
                case ImageHeader:
                    // HEIGHT(4) WIDTH(4) NC(2) BPC(1) C(1) UnkC(1) IPR(1). C is the compression type,
                    // and 7 is the only value Part 1 defines: JPEG 2000 itself.
                    if (body.Length < 14)
                        throw new InvalidDataException("JPEG 2000: the image header box is too short.");
                    if (body[11] != 7)
                        throw new NotSupportedException(
                            $"JPEG 2000: the image header names compression type {body[11]}; only 7, JPEG 2000 " +
                            "itself, is defined by Part 1.");
                    break;

                case ColourSpecification:
                    colour ??= ReadColour(body);
                    break;

                case Palette:
                    palette ??= ReadPalette(body);
                    break;

                case ComponentMapping:
                    map ??= ReadComponentMapping(body);
                    break;

                case ChannelDefinition:
                    definition ??= ReadChannelDefinition(body);
                    break;
            }

            offset = bodyStart + bodyLength;
        }

        if (first) throw new InvalidDataException("JPEG 2000: the JP2 header box is empty.");
        return (colour, Combine(palette, map), definition);
    }

    /// <summary>
    /// I.5.3.4 and I.5.3.5 together. A palette is meaningless without the mapping that says which
    /// component indexes it, so one without the other is malformed; a mapping without a palette can
    /// only select components directly.
    /// </summary>
    private static Jp2ChannelMapping? Combine(Jp2ChannelMapping? palette, (int Component, int Column)[]? map)
    {
        if (palette is null && map is null) return null;
        if (map is null)
            throw new InvalidDataException("JPEG 2000: the JP2 file has a palette box but no component mapping box.");

        palette ??= new Jp2ChannelMapping(0, 0, [], [], []);
        foreach (var (_, column) in map)
        {
            if (column >= palette.Columns)
                throw new InvalidDataException(
                    $"JPEG 2000: the component mapping box names palette column {column}, but the palette has " +
                    $"{palette.Columns}.");
        }

        return palette with { Map = map };
    }

    /// <summary>
    /// The channel definition box (I.5.3.6): N(2), then N entries of Cn(2) Typ(2) Asoc(2). A channel
    /// is described at most once, and Typ is one of the four values Part 1 defines.
    /// </summary>
    private static Jp2ChannelDefinition ReadChannelDefinition(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2)
            throw new InvalidDataException("JPEG 2000: the channel definition box is too short.");

        int count = BinaryPrimitives.ReadUInt16BigEndian(body);
        if (count == 0 || body.Length - 2 < count * 6)
            throw new InvalidDataException(
                $"JPEG 2000: the channel definition box declares {count} channels, which its {body.Length} bytes do not hold.");

        var entries = new (int Channel, int Type, int Association)[count];
        var seen = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var entry = body.Slice(2 + i * 6, 6);
            var channel = BinaryPrimitives.ReadUInt16BigEndian(entry);
            var type = BinaryPrimitives.ReadUInt16BigEndian(entry[2..]);
            var association = BinaryPrimitives.ReadUInt16BigEndian(entry[4..]);

            if (!seen.Add(channel))
                throw new InvalidDataException($"JPEG 2000: the channel definition box describes channel {channel} twice.");
            if (type is not (0 or 1 or 2 or 0xFFFF))
                throw new InvalidDataException(
                    $"JPEG 2000: the channel definition box gives channel {channel} type {type}, which T.800 does not define.");

            entries[i] = (channel, type, association);
        }

        return new Jp2ChannelDefinition(entries);
    }

    /// <summary>
    /// The palette box (I.5.3.4): NE(2) entries and NPC(1) columns, a depth byte per column (the low
    /// seven bits are the depth less one, the top bit the sign), then the entries one after another,
    /// each value in as many whole bytes as its column's depth needs.
    /// </summary>
    private static Jp2ChannelMapping ReadPalette(ReadOnlySpan<byte> body)
    {
        if (body.Length < 3)
            throw new InvalidDataException("JPEG 2000: the palette box is too short.");

        var entries = BinaryPrimitives.ReadUInt16BigEndian(body);
        int columns = body[2];

        // I.5.3.4 bounds NE at 1024 and NPC at 255, and neither below 1. The box is checked against
        // its own length before anything is sized from it, so the table it allocates is never more
        // than the bytes that describe it.
        if (entries is 0 or > 1024 || columns == 0)
            throw new InvalidDataException(
                $"JPEG 2000: the palette box declares {entries} entries of {columns} columns, outside what T.800 allows.");
        if (body.Length < 3 + columns)
            throw new InvalidDataException("JPEG 2000: the palette box is too short for its column depths.");

        var depths = new int[columns];
        var bytes = new int[columns];
        var rowBytes = 0;
        for (var c = 0; c < columns; c++)
        {
            var b = body[3 + c];
            if ((b & 0x80) != 0)
                throw new NotSupportedException("JPEG 2000: signed palette columns are not implemented.");

            depths[c] = (b & 0x7F) + 1;
            if (depths[c] > 16)
                throw new NotSupportedException(
                    $"JPEG 2000: a {depths[c]}-bit palette column is not implemented; the limit here is 16.");
            bytes[c] = (depths[c] + 7) / 8;
            rowBytes += bytes[c];
        }

        var offset = 3 + columns;
        if (body.Length - offset < (long)entries * rowBytes)
            throw new InvalidDataException("JPEG 2000: the palette box is too short for its entries.");

        var values = new ushort[entries * columns];
        for (var e = 0; e < entries; e++)
        {
            for (var c = 0; c < columns; c++)
            {
                int value = bytes[c] == 1 ? body[offset] : BinaryPrimitives.ReadUInt16BigEndian(body[offset..]);
                offset += bytes[c];

                // Bits above the column's declared depth are not part of the value.
                values[e * columns + c] = (ushort)(value & ((1 << depths[c]) - 1));
            }
        }

        return new Jp2ChannelMapping(entries, columns, depths, values, []);
    }

    /// <summary>
    /// The component mapping box (I.5.3.5): four bytes per output channel, CMP(2) MTYP(1) PCOL(1).
    /// MTYP 0 takes component CMP as it is, MTYP 1 looks it up in palette column PCOL.
    /// </summary>
    private static (int Component, int Column)[] ReadComponentMapping(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0 || body.Length % 4 != 0)
            throw new InvalidDataException("JPEG 2000: the component mapping box is not a whole number of entries.");

        var map = new (int Component, int Column)[body.Length / 4];
        for (var i = 0; i < map.Length; i++)
        {
            var entry = body.Slice(i * 4, 4);
            int component = BinaryPrimitives.ReadUInt16BigEndian(entry);
            map[i] = entry[2] switch
            {
                0 => (component, -1),
                1 => (component, entry[3]),
                _ => throw new InvalidDataException(
                    $"JPEG 2000: the component mapping box has mapping type {entry[2]}, which T.800 does not define."),
            };
        }

        return map;
    }

    /// <summary>
    /// A colour specification box (I.5.3.3): METH(1) PREC(1) APPROX(1), then EnumCS(4) for an
    /// enumerated colour space, or the ICC profile for a restricted one.
    /// </summary>
    private static Jp2Colour ReadColour(ReadOnlySpan<byte> body)
    {
        if (body.Length < 3)
            throw new InvalidDataException("JPEG 2000: the colour specification box is too short.");

        var method = body[0];
        switch (method)
        {
            case 1:
                if (body.Length < 7)
                    throw new InvalidDataException("JPEG 2000: the colour specification box is too short for EnumCS.");
                return new Jp2Colour(method, (Jp2EnumeratedColourSpace)BinaryPrimitives.ReadUInt32BigEndian(body[3..]), null);

            case 2:
                return new Jp2Colour(method, null, body[3..].ToArray());

            default:
                // Part 2 adds methods for any ICC profile and vendor colour; Part 1 defines two.
                throw new NotSupportedException(
                    $"JPEG 2000: colour specification method {method} is not defined by Part 1 JP2.");
        }
    }

    private static bool ListsJp2Brand(ReadOnlySpan<byte> body)
    {
        // BR(4) MinV(4), then the compatibility list CL, four bytes an entry.
        for (var i = 8; i + 4 <= body.Length; i += 4)
        {
            if (body[i] == (byte)'j' && body[i + 1] == (byte)'p' && body[i + 2] == (byte)'2' && body[i + 3] == (byte)' ')
                return true;
        }

        return false;
    }

    /// <summary>
    /// One box header (I.4): LBox(4) and TBox(4), then XLBox(8) when LBox is 1. An LBox of 0 means
    /// the box runs to the end of what contains it; any other value below 8 cannot hold even its
    /// own header.
    /// </summary>
    private static (uint Type, int BodyStart, int BodyLength) ReadBox(ReadOnlySpan<byte> data, int offset)
    {
        if (data.Length - offset < 8)
            throw new InvalidDataException("JPEG 2000: a JP2 box header runs past the end of the file.");

        long length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        var type = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
        var headerLength = 8;

        if (length == 1)
        {
            if (data.Length - offset < 16)
                throw new InvalidDataException("JPEG 2000: a JP2 box's extended length runs past the end of the file.");
            length = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(offset + 8)..]);
            headerLength = 16;
        }
        else if (length == 0)
        {
            length = data.Length - offset;
        }

        // A negative length here is a 64-bit XLBox above long.MaxValue, which is as malformed as one
        // too short for its own header or longer than the file.
        if (length < headerLength || length > data.Length - offset)
            throw new InvalidDataException(
                $"JPEG 2000: a JP2 box at offset {offset} declares {length} bytes, which does not fit.");

        return (type, offset + headerLength, (int)length - headerLength);
    }
}
