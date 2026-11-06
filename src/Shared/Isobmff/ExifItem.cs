using System.Buffers.Binary;

// Compiled into each package that reads an ISOBMFF item model, under that package's namespace.
#if SIMDCODECS_HEIF
namespace Sdcb.SimdCodecs.Heif.Isobmff;
#elif SIMDCODECS_EXIF
namespace Sdcb.SimdCodecs.Exif.Isobmff;
#else
#error ExifItem.cs is shared source for the ISOBMFF-based packages (Sdcb.SimdCodecs.Heif, Sdcb.SimdCodecs.Exif).
#endif

/// <summary>
/// The <c>Exif</c> item of a HEIF / AVIF still image (23008-12 §6.4.9 / Annex A.2.1) and the generic item-data read
/// both packages use, so the Exif block they hand out is the same bytes. Item data is resolved through <c>iloc</c>,
/// bounded by the file; a malformed item reads as absent.
/// </summary>
internal static class ExifItem
{
    internal const uint RefCdsc = 0x63647363; // 'cdsc': "content describes"
    internal const uint ItemExif = 0x45786966; // 'Exif'
    private const long MaxItemBytes = 16 << 20;

    /// <summary>
    /// The Exif block that describes the primary item (a <c>cdsc</c> reference from an <c>Exif</c> item to it), from
    /// its TIFF header on; empty when there is none.
    /// </summary>
    public static ReadOnlyMemory<byte> FindPrimary(ReadOnlySpan<byte> data, ItemContainer items)
    {
        foreach (var reference in items.References)
        {
            if (reference.Type != RefCdsc || Array.IndexOf(reference.To, items.PrimaryItemId) < 0)
                continue;
            if (items.Find(reference.From) is { Type: ItemExif } item && Read(data, items, item) is { IsEmpty: false } exif)
                return exif;
        }
        return default;
    }

    /// <summary>
    /// The Exif item's payload (23008-12 A.2.1): a 32-bit offset to the TIFF header, then the Exif data; returned from
    /// the TIFF header on.
    /// </summary>
    public static ReadOnlyMemory<byte> Read(ReadOnlySpan<byte> data, ItemContainer items, ItemContainer.Item item)
    {
        if (ReadItem(data, items, item) is not { Length: >= 4 } payload)
            return default;
        var offset = BinaryPrimitives.ReadUInt32BigEndian(payload);
        if (offset > (uint)payload.Length - 4)
            return default;
        var tiff = payload.AsMemory(4 + (int)offset);
        // Some writers put "Exif\0\0" before the header and count it in the offset, some do not.
        if (tiff.Span.StartsWith("Exif\0\0"u8))
            tiff = tiff[6..];
        return tiff.Length >= 8 ? tiff : default;
    }

    /// <summary>The bytes of <paramref name="item"/>, or <see langword="null"/> when it is empty, too large or unreadable.</summary>
    public static byte[]? ReadItem(ReadOnlySpan<byte> data, ItemContainer items, ItemContainer.Item item)
    {
        try
        {
            var length = ItemContainer.GetItemLength(item);
            if (length <= 0 || length > data.Length || length > MaxItemBytes)
                return null;
            var buffer = new byte[length];
            items.Resolve(data, item, buffer);
            return buffer;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return null;
        }
    }
}
