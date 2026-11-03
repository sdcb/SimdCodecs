// Compiled into each package that uses it (see src/Shared/MqDecoder.cs for why it is shared as source),
// under that package's namespace so the copies are distinct types.
#if SIMDCODECS_HEIF
namespace Sdcb.SimdCodecs.Heif.Isobmff;
#else
#error ItemContainer.cs is shared source for the ISOBMFF-based packages (Sdcb.SimdCodecs.Heif, Sdcb.SimdCodecs.Avif).
#endif

/// <summary>
/// The generic ISOBMFF meta-box item model (ISO/IEC 14496-12 §8.11): <c>pitm</c>, <c>iinf</c>,
/// <c>iloc</c>, <c>iprp</c>, <c>iref</c> and <c>idat</c>. HEIF (23008-12) and AVIF both put a still
/// image in this same item model, so nothing here knows about a codec: properties are handed out as
/// raw spans and each package's own parser interprets the ones it recognises.
/// </summary>
/// <remarks>
/// Untrusted input: every offset is checked against the file length before use, the item, extent,
/// property and child-box counts are capped, and nesting is capped, so a malformed file throws
/// <see cref="InvalidDataException"/> rather than looping, recursing without bound or allocating
/// without bound. The container never copies item payloads; <see cref="Resolve"/> slices the
/// caller's own buffer.
/// </remarks>
internal sealed class ItemContainer
{
    /// <summary>Hard cap on items in one container (the <c>iinf</c> count is a 16-bit field).</summary>
    internal const int MaxItems = 65_535;

    /// <summary>Hard cap on extents in one item.</summary>
    internal const int MaxExtents = 65_535;

    /// <summary>Hard cap on shared properties in one <c>ipco</c>.</summary>
    internal const int MaxProperties = 4_096;

    /// <summary>Hard cap on box nesting, so a crafted container cannot exhaust the stack.</summary>
    internal const int MaxDepth = 16;

    internal readonly record struct Extent(long Offset, long Length);

    internal sealed class Item
    {
        internal uint Id;
        internal uint Type;
        internal int ConstructionMethod;

        /// <summary>Item data location, in order; a zero-length extent is an offset-only entry.</summary>
        internal readonly List<Extent> Extents = [];
    }

    internal sealed class Property
    {
        internal uint Type;
        internal int Offset;
        internal int Length;
    }

    internal readonly record struct Reference(uint Type, uint From, uint[] To);

    private readonly Dictionary<uint, Item> _itemsById = [];

    /// <summary>All items in <c>iinf</c> order.</summary>
    internal List<Item> Items { get; } = [];

    /// <summary>The <c>pitm</c> primary item ID, or 0 when the container has none.</summary>
    internal uint PrimaryItemId { get; private set; }

    /// <summary>Shared properties in <c>ipco</c> order; <c>ipma</c> associations are 1-based into this.</summary>
    internal List<Property> Properties { get; } = [];

    /// <summary>Item → associated property indices (1-based, as written in <c>ipma</c>).</summary>
    internal Dictionary<uint, int[]> Associations { get; } = [];

    /// <summary><c>iref</c> references, in file order.</summary>
    internal List<Reference> References { get; } = [];

    /// <summary><c>idat</c> payload offset, or -1 when absent.</summary>
    internal int IdatOffset { get; private set; } = -1;

    internal int IdatLength { get; private set; }

    /// <summary>The major brand in <c>ftyp</c>, or 0 when absent.</summary>
    internal uint MajorBrand { get; private set; }

    private ItemContainer() { }

    /// <summary>
    /// Parses the container's metadata. The returned object refers to <paramref name="data"/> by
    /// offset only, so the caller must keep the same buffer for as long as it calls
    /// <see cref="Resolve"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a well-formed ISOBMFF still-image container.</exception>
    internal static ItemContainer Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) throw new InvalidDataException("The file is too short to be an ISOBMFF container.");

        var container = new ItemContainer();
        var metaSeen = false;
        var reader = new BoxReader(data);
        while (reader.HasBox)
        {
            var position = reader.Position;
            if (!reader.TryReadBox(out var type, out var payload, out var boxEnd))
            {
                throw new InvalidDataException($"Malformed box header at offset {position}.");
            }

            switch (type)
            {
                case BoxReader.BoxFtyp:
                    if (boxEnd - payload >= 8)
                    {
                        container.MajorBrand = reader.ReadU32BE(payload);
                    }
                    break;

                case BoxReader.BoxMeta:
                    container.ParseMeta(data, payload, boxEnd);
                    metaSeen = true;
                    break;
            }
        }

        if (!metaSeen) throw new InvalidDataException("The container has no 'meta' box; it is not a HEIF/AVIF still image.");
        return container;
    }

    /// <summary>The item with <paramref name="id"/>, or <see langword="null"/> when absent.</summary>
    internal Item? Find(uint id) => _itemsById.GetValueOrDefault(id);

    /// <summary>
    /// Copies an item's data (all its extents concatenated) into <paramref name="destination"/>,
    /// which must be at least <see cref="GetItemLength"/> bytes. Returns the number of bytes written.
    /// </summary>
    /// <exception cref="InvalidDataException">An extent lies outside the file.</exception>
    internal int Resolve(ReadOnlySpan<byte> data, Item item, Span<byte> destination)
    {
        var written = 0;
        foreach (var extent in item.Extents)
        {
            var source = ResolveExtent(data, item, extent);
            if (written + source.Length > destination.Length)
            {
                throw new InvalidDataException("Item data does not fit the supplied buffer.");
            }
            source.CopyTo(destination[written..]);
            written += source.Length;
        }
        return written;
    }

    /// <summary>Total byte length of an item's extents.</summary>
    internal static long GetItemLength(Item item)
    {
        long length = 0;
        foreach (var extent in item.Extents) length += extent.Length;
        return length;
    }

    /// <summary>
    /// Resolves one extent to a slice of <paramref name="data"/>, applying the construction method:
    /// 0 = absolute file offset, 1 = offset into <c>idat</c>. Construction method 2 (item offset) is
    /// not used by still-image files and is rejected.
    /// </summary>
    /// <exception cref="InvalidDataException">The extent lies outside the file.</exception>
    internal ReadOnlySpan<byte> ResolveExtent(ReadOnlySpan<byte> data, Item item, Extent extent)
    {
        long baseOffset = item.ConstructionMethod switch
        {
            0 => 0,
            1 when IdatOffset >= 0 => IdatOffset,
            1 => throw new InvalidDataException("Item construction method 1 requires an 'idat' box."),
            var method => throw new InvalidDataException($"Unsupported item construction method {method}."),
        };

        // Checked in long arithmetic: offset and length are attacker-controlled 32/64-bit fields.
        var start = baseOffset + extent.Offset;
        var length = extent.Length;
        if (start < 0 || length < 0 || start > data.Length || length > data.Length - start)
        {
            throw new InvalidDataException("Item extent lies outside the file.");
        }

        var limit = item.ConstructionMethod == 1 ? IdatLength : data.Length;
        if (start + length > baseOffset + limit)
        {
            throw new InvalidDataException("Item extent lies outside the box it is based on.");
        }

        return data.Slice((int)start, (int)length);
    }

    /// <summary>The payload of the first property associated with <paramref name="itemId"/> of type <paramref name="type"/>.</summary>
    internal ReadOnlySpan<byte> FindProperty(ReadOnlySpan<byte> data, uint itemId, uint type)
    {
        if (!Associations.TryGetValue(itemId, out var indices)) return default;
        foreach (var index in indices)
        {
            // ipma indices are 1-based; 0 means "no property".
            if (index <= 0 || index > Properties.Count) continue;
            var property = Properties[index - 1];
            if (property.Type == type) return data.Slice(property.Offset, property.Length);
        }
        return default;
    }

    /// <summary>All properties associated with <paramref name="itemId"/> of type <paramref name="type"/>, in association order.</summary>
    internal IEnumerable<ReadOnlyMemory<byte>> FindProperties(byte[] data, uint itemId, uint type)
    {
        if (!Associations.TryGetValue(itemId, out var indices)) yield break;
        foreach (var index in indices)
        {
            if (index <= 0 || index > Properties.Count) continue;
            var property = Properties[index - 1];
            if (property.Type == type) yield return data.AsMemory(property.Offset, property.Length);
        }
    }

    private void ParseMeta(ReadOnlySpan<byte> data, int payload, int boxEnd)
    {
        if (payload + 4 > boxEnd) throw new InvalidDataException("Truncated 'meta' box.");

        // Two passes over the children: 'iloc' names items that 'iinf' declares, and a file may put either box
        // first, so the item table is built from 'iinf' before any 'iloc' extent is attached.
        var iloc = (-1, -1);
        var iprp = (-1, -1);
        var iref = (-1, -1);

        // 'meta' is a FullBox (version + flags), then children.
        var reader = new BoxReader(data, payload + 4, boxEnd);
        while (reader.HasBox)
        {
            var position = reader.Position;
            if (!reader.TryReadBox(out var type, out var childPayload, out var childEnd))
            {
                throw new InvalidDataException($"Malformed box header inside 'meta' at offset {position}.");
            }

            switch (type)
            {
                case BoxReader.BoxHdlr:
                    // The handler must be 'pict'; other handlers mean this is not a still image.
                    if (childEnd - childPayload < 12) throw new InvalidDataException("Truncated 'hdlr' box.");
                    if (reader.ReadU32BE(childPayload + 8) != BoxReader.BoxPict)
                    {
                        throw new InvalidDataException("The 'meta' handler is not 'pict'; this is not a still image.");
                    }
                    break;

                case BoxReader.BoxPitm:
                    if (childEnd - childPayload < 6) throw new InvalidDataException("Truncated 'pitm' box.");
                    PrimaryItemId = reader.ReadU8(childPayload) == 0
                        ? reader.ReadU16BE(childPayload + 4)
                        : reader.ReadU32BE(childPayload + 4);
                    break;

                case BoxReader.BoxIinf:
                    ParseIinf(data, childPayload, childEnd);
                    break;

                case BoxReader.BoxIdat:
                    IdatOffset = childPayload;
                    IdatLength = childEnd - childPayload;
                    break;

                case BoxReader.BoxIloc:
                    iloc = (childPayload, childEnd);
                    break;

                case BoxReader.BoxIprp:
                    iprp = (childPayload, childEnd);
                    break;

                case BoxReader.BoxIref:
                    iref = (childPayload, childEnd);
                    break;
            }
        }

        if (iloc.Item1 < 0) throw new InvalidDataException("The 'meta' box has no 'iloc'.");
        ParseIloc(data, iloc.Item1, iloc.Item2);
        if (iprp.Item1 >= 0) ParseIprp(data, iprp.Item1, iprp.Item2, 1);
        if (iref.Item1 >= 0) ParseIref(data, iref.Item1, iref.Item2);
    }

    private void ParseIinf(ReadOnlySpan<byte> data, int payload, int boxEnd)
    {
        if (payload + 4 > boxEnd) throw new InvalidDataException("Truncated 'iinf' box.");
        var version = data[payload];
        // version 0 has a 16-bit entry count, 1 and later a 32-bit one.
        var countSize = version == 0 ? 2 : 4;
        if (payload + 4 + countSize > boxEnd) throw new InvalidDataException("Truncated 'iinf' entry count.");

        var reader = new BoxReader(data, payload + 4 + countSize, boxEnd);
        while (reader.HasBox)
        {
            var position = reader.Position;
            if (!reader.TryReadBox(out var type, out var entryPayload, out var entryEnd) || type != BoxReader.BoxInfe)
            {
                throw new InvalidDataException($"Expected an 'infe' box inside 'iinf' at offset {position}.");
            }
            if (Items.Count >= MaxItems) throw new InvalidDataException("Too many items.");

            var infe = new BoxReader(data, entryPayload, entryEnd);
            if (entryEnd - entryPayload < 8) throw new InvalidDataException("Truncated 'infe' box.");
            var infeVersion = infe.ReadU8(entryPayload);
            // infe v2: 16-bit item_ID, 16-bit protection index; v3: 32-bit item_ID. v0/v1 carry no item type.
            var idSize = infeVersion >= 3 ? 4 : 2;
            if (entryPayload + 4 + idSize + 2 > entryEnd) throw new InvalidDataException("Truncated 'infe' box.");
            var item = new Item
            {
                Id = idSize == 4 ? infe.ReadU32BE(entryPayload + 4) : infe.ReadU16BE(entryPayload + 4),
            };
            var cursor = entryPayload + 4 + idSize + 2;
            if (infeVersion >= 2)
            {
                if (cursor + 4 > entryEnd) throw new InvalidDataException("Truncated 'infe' item type.");
                item.Type = infe.ReadU32BE(cursor);
                cursor += 4;
            }

            // item_name is a null-terminated UTF-8 string; its length is not needed, only skipped.
            if (cursor > entryEnd) throw new InvalidDataException("Truncated 'infe' box.");
            _ = SkipCString(data, ref cursor, entryEnd);

            if (item.Id != 0 && !_itemsById.TryAdd(item.Id, item)) item.Id = 0;
            if (item.Id != 0) Items.Add(item);
        }
    }

    private void ParseIloc(ReadOnlySpan<byte> data, int payload, int boxEnd)
    {
        if (payload + 4 > boxEnd) throw new InvalidDataException("Truncated 'iloc' box.");
        var version = data[payload];
        if (version > 2) throw new InvalidDataException($"Unsupported 'iloc' version {version}.");
        var reader = new BoxReader(data, payload + 4, boxEnd);

        var cursor = payload + 4;
        if (cursor + 2 > boxEnd) throw new InvalidDataException("Truncated 'iloc' box.");
        var offsetSize = data[cursor] >> 4;
        var lengthSize = data[cursor] & 0x0F;
        // Second byte: base_offset_size (high nibble), then index_size (v1/v2) or reserved (v0).
        var baseOffsetSize = data[cursor + 1] >> 4;
        var indexSize = version is 1 or 2 ? data[cursor + 1] & 0x0F : 0;
        cursor += 2;

        if (offsetSize is not (0 or 4 or 8) || lengthSize is not (0 or 4 or 8) ||
            indexSize is not (0 or 4 or 8) || baseOffsetSize is not (0 or 4 or 8))
        {
            throw new InvalidDataException("Invalid 'iloc' field widths.");
        }

        long itemCount;
        if (version < 2)
        {
            if (cursor + 2 > boxEnd) throw new InvalidDataException("Truncated 'iloc' item count.");
            itemCount = reader.ReadU16BE(cursor);
            cursor += 2;
        }
        else
        {
            if (cursor + 4 > boxEnd) throw new InvalidDataException("Truncated 'iloc' item count.");
            itemCount = reader.ReadU32BE(cursor);
            cursor += 4;
        }
        if (itemCount > MaxItems) throw new InvalidDataException("Too many 'iloc' items.");

        for (long i = 0; i < itemCount; i++)
        {
            uint itemId;
            if (version < 2)
            {
                Require(cursor, 2);
                itemId = reader.ReadU16BE(cursor);
                cursor += 2;
            }
            else
            {
                Require(cursor, 4);
                itemId = reader.ReadU32BE(cursor);
                cursor += 4;
            }

            var constructionMethod = 0;
            if (version is 1 or 2)
            {
                Require(cursor, 2);
                constructionMethod = reader.ReadU16BE(cursor) & 0x0F;
                cursor += 2;
            }

            Require(cursor, 2);
            var dataReferenceIndex = reader.ReadU16BE(cursor);
            cursor += 2;
            if (dataReferenceIndex != 0)
            {
                throw new InvalidDataException("Item data stored outside the file ('dref') is not supported.");
            }

            var baseOffset = ReadVariable(reader, ref cursor, baseOffsetSize);
            Require(cursor, 2);
            var extentCount = reader.ReadU16BE(cursor);
            cursor += 2;
            if (extentCount > MaxExtents) throw new InvalidDataException("Too many extents in one item.");

            var item = Find(itemId);
            if (item is null)
            {
                // An iloc entry for an item that infe does not declare: skip its extents.
                item = new Item { Id = 0 };
            }
            else
            {
                item.ConstructionMethod = constructionMethod;
            }

            for (var e = 0; e < extentCount; e++)
            {
                if ((version is 1 or 2) && indexSize != 0)
                {
                    _ = ReadVariable(reader, ref cursor, indexSize);
                }
                var extentOffset = ReadVariable(reader, ref cursor, offsetSize);
                var extentLength = ReadVariable(reader, ref cursor, lengthSize);
                if (item.Id != 0)
                {
                    long offset = baseOffset + extentOffset;
                    if (offset < 0 || extentLength < 0) throw new InvalidDataException("Negative item extent.");
                    item.Extents.Add(new Extent(offset, extentLength));
                }
            }
        }

        void Require(int at, int count)
        {
            if (at < 0 || count < 0 || at > boxEnd - count)
            {
                throw new InvalidDataException("Truncated 'iloc' box.");
            }
        }
    }

    private static long ReadVariable(BoxReader reader, ref int cursor, int size)
    {
        long value = size switch
        {
            0 => 0,
            4 => reader.ReadU32BE(cursor),
            8 => unchecked((long)reader.ReadU64BE(cursor)),
            _ => throw new InvalidDataException("Invalid 'iloc' field width."),
        };
        cursor += size;
        return value;
    }

    private void ParseIprp(ReadOnlySpan<byte> data, int start, int end, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("Box nesting is too deep.");

        var reader = new BoxReader(data, start, end);
        while (reader.HasBox)
        {
            var position = reader.Position;
            if (!reader.TryReadBox(out var type, out var payload, out var boxEnd))
            {
                throw new InvalidDataException($"Malformed box inside 'iprp' at offset {position}.");
            }

            switch (type)
            {
                case BoxReader.BoxIpco:
                    var ipco = new BoxReader(data, payload, boxEnd);
                    while (ipco.HasBox)
                    {
                        var propertyStart = ipco.Position;
                        if (!ipco.TryReadBox(out var propertyType, out var propertyPayload, out var propertyEnd))
                        {
                            throw new InvalidDataException($"Malformed property box at offset {propertyStart}.");
                        }
                        if (Properties.Count >= MaxProperties) throw new InvalidDataException("Too many properties.");
                        Properties.Add(new Property
                        {
                            Type = propertyType,
                            Offset = propertyPayload,
                            Length = propertyEnd - propertyPayload,
                        });
                    }
                    break;

                case BoxReader.BoxIpma:
                    ParseIpma(data, payload, boxEnd);
                    break;
            }
        }
    }

    private void ParseIpma(ReadOnlySpan<byte> data, int payload, int boxEnd)
    {
        if (payload + 8 > boxEnd) throw new InvalidDataException("Truncated 'ipma' box.");
        var version = data[payload];
        var flags = (uint)((data[payload + 1] << 16) | (data[payload + 2] << 8) | data[payload + 3]);
        var reader = new BoxReader(data, payload, boxEnd);
        var cursor = payload + 4;
        var entryCount = reader.ReadU32BE(cursor);
        cursor += 4;
        if (entryCount > MaxItems) throw new InvalidDataException("Too many 'ipma' entries.");

        var wideIndex = (flags & 1) != 0;
        for (uint i = 0; i < entryCount; i++)
        {
            uint itemId;
            if (cursor + 2 > boxEnd) throw new InvalidDataException("Truncated 'ipma' box.");
            if (version < 1)
            {
                itemId = reader.ReadU16BE(cursor);
                cursor += 2;
            }
            else
            {
                if (cursor + 4 > boxEnd) throw new InvalidDataException("Truncated 'ipma' box.");
                itemId = reader.ReadU32BE(cursor);
                cursor += 4;
            }

            if (cursor + 1 > boxEnd) throw new InvalidDataException("Truncated 'ipma' box.");
            var associationCount = data[cursor];
            cursor++;
            var indices = new int[associationCount];
            for (var a = 0; a < associationCount; a++)
            {
                int index;
                if (wideIndex)
                {
                    if (cursor + 2 > boxEnd) throw new InvalidDataException("Truncated 'ipma' box.");
                    index = reader.ReadU16BE(cursor) & 0x7FFF;
                    cursor += 2;
                }
                else
                {
                    if (cursor + 1 > boxEnd) throw new InvalidDataException("Truncated 'ipma' box.");
                    index = data[cursor] & 0x7F;
                    cursor++;
                }
                indices[a] = index;
            }
            Associations[itemId] = indices;
        }
    }

    private void ParseIref(ReadOnlySpan<byte> data, int payload, int boxEnd)
    {
        if (payload + 4 > boxEnd) throw new InvalidDataException("Truncated 'iref' box.");
        var version = data[payload];
        var reader = new BoxReader(data, payload + 4, boxEnd);
        while (reader.HasBox)
        {
            var position = reader.Position;
            if (!reader.TryReadBox(out var type, out var referencePayload, out var referenceEnd))
            {
                throw new InvalidDataException($"Malformed reference box at offset {position}.");
            }

            var idSize = version == 0 ? 2 : 4;
            if (referenceEnd - referencePayload < idSize + 2) throw new InvalidDataException("Truncated 'iref' entry.");
            var from = idSize == 2 ? reader.ReadU16BE(referencePayload) : reader.ReadU32BE(referencePayload);
            var count = reader.ReadU16BE(referencePayload + idSize);
            var cursor = referencePayload + idSize + 2;
            if (count > MaxItems || (long)cursor + (long)count * idSize > referenceEnd)
            {
                throw new InvalidDataException("Invalid 'iref' reference count.");
            }

            var to = new uint[count];
            for (var i = 0; i < count; i++)
            {
                to[i] = idSize == 2 ? reader.ReadU16BE(cursor) : reader.ReadU32BE(cursor);
                cursor += idSize;
            }
            References.Add(new Reference(type, from, to));
        }
    }

    private static int SkipCString(ReadOnlySpan<byte> data, ref int cursor, int end)
    {
        var start = cursor;
        while (cursor < end && data[cursor] != 0) cursor++;
        if (cursor >= end) throw new InvalidDataException("Truncated null-terminated string.");
        cursor++;
        return cursor - start;
    }
}
