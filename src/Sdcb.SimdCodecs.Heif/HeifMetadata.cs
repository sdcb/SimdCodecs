using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Heif.Isobmff;

namespace Sdcb.SimdCodecs.Heif;

/// <summary>
/// The metadata items a HEIF file attaches to its primary image (23008-12 §6.4.9 / Annex A): an <c>Exif</c> item and
/// an XMP <c>mime</c> item, each linked to the primary by a <c>cdsc</c> ("content describes") reference, and a
/// <c>thmb</c> thumbnail, an image item linked the same way. Item data is resolved through <c>iloc</c>, bounded by the
/// file; a malformed item reads as absent.
/// </summary>
internal static class HeifMetadata
{
    private const uint RefThmb = 0x74686D62; // 'thmb'
    private const uint ItemMime = 0x6D696D65; // 'mime'
    private const uint ItemHvc1 = 0x68766331; // 'hvc1'

    /// <summary>The Exif and XMP blocks described as metadata of the primary item, and its ICC profile.</summary>
    public static ImageMetadata Read(ReadOnlySpan<byte> data, HeifContainer container)
    {
        ReadOnlyMemory<byte> exif = default, xmp = default;
        var items = container.Items;
        foreach (var reference in items.References)
        {
            if (reference.Type != ExifItem.RefCdsc || Array.IndexOf(reference.To, items.PrimaryItemId) < 0)
                continue;
            var item = items.Find(reference.From);
            if (item is null)
                continue;
            if (item.Type == ExifItem.ItemExif && exif.IsEmpty)
                exif = ReadExif(data, items, item);
            else if (item.Type == ItemMime && xmp.IsEmpty && IsXmp(item.ContentType))
                xmp = ReadItem(data, items, item) ?? default;
        }
        return new ImageMetadata { Exif = exif, Xmp = xmp, Icc = container.IccProfile };
    }

    /// <summary>The first <c>hvc1</c> thumbnail item of the primary image, if any.</summary>
    public static ItemContainer.Item? FindThumbnail(HeifContainer container)
    {
        var items = container.Items;
        foreach (var reference in items.References)
        {
            if (reference.Type != RefThmb || Array.IndexOf(reference.To, items.PrimaryItemId) < 0)
                continue;
            if (items.Find(reference.From) is { Type: ItemHvc1 } item)
                return item;
        }
        return null;
    }

    private static bool IsXmp(string? contentType) =>
        contentType is "application/rdf+xml" or "application/xmp+xml" or "text/xml";

    // The decoder outputs display-oriented pixels (irot / imir applied), so an Exif Orientation other than 1 would
    // make a transcoder rotate twice: reset it in the private copy ReadItem made.
    private static ReadOnlyMemory<byte> ReadExif(ReadOnlySpan<byte> data, ItemContainer items, ItemContainer.Item item)
    {
        var exif = ExifItem.Read(data, items, item);
        if (!exif.IsEmpty)
            ExifOrientation.TryReset(MemoryMarshal.AsMemory(exif).Span);
        return exif;
    }

    private static byte[]? ReadItem(ReadOnlySpan<byte> data, ItemContainer items, ItemContainer.Item item) =>
        ExifItem.ReadItem(data, items, item);
}
