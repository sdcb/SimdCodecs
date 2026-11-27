using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Avif.Isobmff;

namespace Sdcb.SimdCodecs.Avif;

/// <summary>
/// Metadata items attached to the primary AVIF image (ISO/IEC 23008-12 §6.4.9 / Annex A):
/// <c>Exif</c> and XMP (<c>mime</c>) items linked by <c>cdsc</c>, plus <c>thmb</c> thumbnail items.
/// </summary>
internal static class AvifMetadata
{
    private const uint RefThmb = 0x74686D62; // 'thmb'
    private const uint ItemMime = 0x6D696D65; // 'mime'

    /// <summary>Reads the Exif, XMP and ICC metadata associated with the container's primary item.</summary>
    internal static ImageMetadata Read(ReadOnlySpan<byte> data, AvifContainer container)
    {
        ReadOnlyMemory<byte> exif = default, xmp = default;
        var items = container.Items;
        uint targetId = container.RootItemId;
        foreach (var reference in items.References)
        {
            if (reference.Type != ExifItem.RefCdsc || Array.IndexOf(reference.To, targetId) < 0)
                continue;
            var item = items.Find(reference.From);
            if (item is null)
                continue;
            if (item.Type == ExifItem.ItemExif && exif.IsEmpty)
                exif = ReadExif(data, items, item);
            else if (item.Type == ItemMime && xmp.IsEmpty && IsXmp(item.ContentType))
                xmp = ExifItem.ReadItem(data, items, item) ?? default;
        }
        return new ImageMetadata { Exif = exif, Xmp = xmp, Icc = container.IccProfile };
    }

    /// <summary>Finds the first thumbnail image item linked to the container's primary item via <c>thmb</c>.</summary>
    internal static ItemContainer.Item? FindThumbnail(AvifContainer container)
    {
        var items = container.Items;
        uint targetId = container.RootItemId;
        foreach (var reference in items.References)
        {
            if (reference.Type != RefThmb || Array.IndexOf(reference.To, targetId) < 0)
                continue;
            var item = items.Find(reference.From);
            if (item is not null && item.Type is AvifContainer.ItemAv01 or AvifContainer.ItemGrid or AvifContainer.ItemIden or AvifContainer.ItemIovl)
                return item;
        }
        return null;
    }

    private static bool IsXmp(string? contentType) =>
        contentType is "application/rdf+xml" or "application/xmp+xml" or "text/xml";

    // Transformative properties (irot/imir) are already applied to decoded pixels, so reset Exif Orientation to 1.
    private static ReadOnlyMemory<byte> ReadExif(ReadOnlySpan<byte> data, ItemContainer items, ItemContainer.Item item)
    {
        var exif = ExifItem.Read(data, items, item);
        if (!exif.IsEmpty)
            ExifOrientation.TryReset(MemoryMarshal.AsMemory(exif).Span);
        return exif;
    }
}
