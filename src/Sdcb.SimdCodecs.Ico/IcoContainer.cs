using System.Buffers.Binary;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;
using Sdcb.SimdCodecs.Png;

namespace Sdcb.SimdCodecs.Ico;

/// <summary>An icon file's kind: its directory's type field.</summary>
public enum IcoKind
{
    /// <summary>An icon (<c>.ico</c>, type 1).</summary>
    Icon = 1,

    /// <summary>A cursor (<c>.cur</c>, type 2): each image has a hotspot.</summary>
    Cursor = 2,
}

/// <summary>One image of an icon or cursor, as its directory entry and its own header describe it.</summary>
/// <param name="Width">Width in pixels, from the image itself (a directory entry's byte says 0 for 256, and can disagree).</param>
/// <param name="Height">Height in pixels, from the image itself.</param>
/// <param name="BitsPerPixel">The image's depth: a DIB's bits per pixel, or a PNG's bits per sample times its samples per pixel.</param>
/// <param name="IsPng">True when the image is a PNG stream, false for a DIB with an AND mask.</param>
/// <param name="HotspotX">A cursor's hotspot, from the left edge; 0 for an icon.</param>
/// <param name="HotspotY">A cursor's hotspot, from the top edge; 0 for an icon.</param>
public readonly record struct IcoEntry(int Width, int Height, int BitsPerPixel, bool IsPng, int HotspotX, int HotspotY);

/// <summary>An icon or cursor's directory, read without decoding a pixel.</summary>
/// <param name="Kind">Icon or cursor.</param>
/// <param name="Entries">The images, in directory order.</param>
/// <param name="Best">The index of the image <see cref="IcoDecoder.Decode(ReadOnlySpan{byte})"/> returns: the largest,
/// then the deepest, then the first.</param>
public sealed record IcoInfo(IcoKind Kind, IReadOnlyList<IcoEntry> Entries, int Best);

/// <summary>One decoded image of an icon or cursor: tightly packed, non-premultiplied 8-bit RGBA.</summary>
public sealed class IcoImage
{
    internal IcoImage(IcoEntry entry, byte[] rgba)
    {
        Entry = entry;
        Rgba = rgba;
    }

    /// <summary>Which image this is: size, depth, encoding, hotspot.</summary>
    public IcoEntry Entry { get; }

    /// <summary>Width in pixels.</summary>
    public int Width => Entry.Width;

    /// <summary>Height in pixels.</summary>
    public int Height => Entry.Height;

    /// <summary>RGBA pixels, top row first, exactly <c>Width * Height * 4</c> bytes.</summary>
    public byte[] Rgba { get; }
}

/// <summary>
/// The directory of an icon or cursor (<c>ICONDIR</c> and its <c>ICONDIRENTRY</c> records, as Microsoft documents
/// them for icon files), with every image's location checked and its own header read.
/// </summary>
internal sealed class IcoContainer
{
    private const int DirectorySize = 6, EntrySize = 16;

    /// <summary>The largest image decoded, in pixels (a DIB's limit is the BMP decoder's, the same).</summary>
    public const long MaximumPixels = 1L << 28;

    private IcoContainer(IcoKind kind, IcoEntry[] entries, (int Offset, int Length)[] payloads)
    {
        Kind = kind;
        Entries = entries;
        Payloads = payloads;
        Best = 0;
        for (var i = 1; i < entries.Length; i++)
        {
            long area = (long)entries[i].Width * entries[i].Height, best = (long)entries[Best].Width * entries[Best].Height;
            if (area > best || (area == best && entries[i].BitsPerPixel > entries[Best].BitsPerPixel)) Best = i;
        }
    }

    public IcoKind Kind { get; }

    public IcoEntry[] Entries { get; }

    public (int Offset, int Length)[] Payloads { get; }

    public int Best { get; }

    public IcoInfo ToInfo() => new(Kind, Entries, Best);

    public ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> data, int index) => data.Slice(Payloads[index].Offset, Payloads[index].Length);

    /// <summary>True for a directory of <paramref name="kind"/>: reserved 0, the type, a non-zero count, and a first
    /// image that starts after the directory. Needs 22 bytes.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> header, IcoKind kind)
    {
        if (header.Length < DirectorySize + EntrySize) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(header) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(header[2..]) != (int)kind)
            return false;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(header[(DirectorySize + 12)..]);
        return count > 0 && offset >= DirectorySize + EntrySize * (long)count;
    }

    /// <exception cref="InvalidDataException">The data is not an icon or cursor, or an image is unreadable.</exception>
    public static IcoContainer Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < DirectorySize || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0)
            throw new InvalidDataException("Not an icon or cursor: no ICONDIR.");
        var type = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        if (type is not (1 or 2)) throw new InvalidDataException($"Icon directory type {type} is neither an icon (1) nor a cursor (2).");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (count == 0) throw new InvalidDataException("The icon directory holds no images.");
        var end = DirectorySize + EntrySize * count;
        if (data.Length < end) throw new InvalidDataException("The icon directory is truncated.");

        var entries = new IcoEntry[count];
        var payloads = new (int, int)[count];
        for (var i = 0; i < count; i++)
        {
            var e = data.Slice(DirectorySize + EntrySize * i, EntrySize);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            if (offset < end || offset >= data.Length)
                throw new InvalidDataException($"Icon image {i} starts at {offset}, outside the file.");
            // A size that runs past the end is cut to the file: writers get it wrong, the image says its own length.
            var length = (int)Math.Min(size, (uint)(data.Length - offset));
            payloads[i] = ((int)offset, length);
            var payload = data.Slice((int)offset, length);
            int hotX = 0, hotY = 0;
            if (type == 2)
            {
                hotX = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]);
                hotY = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
            }
            entries[i] = Describe(payload, i) with { HotspotX = hotX, HotspotY = hotY };
        }
        return new IcoContainer((IcoKind)type, entries, payloads);
    }

    private static IcoEntry Describe(ReadOnlySpan<byte> payload, int index)
    {
        if (PngImageDecoder.CanDecode(payload))
        {
            if (!PngImageDecoder.TryReadInfo(payload, out var png))
                throw new InvalidDataException($"Icon image {index} is a PNG stream that does not read.");
            if ((long)png.Width * png.Height > MaximumPixels)
                throw new InvalidDataException($"Icon image {index} is {png.Width}x{png.Height}, above the limit of {MaximumPixels:N0} pixels.");
            return new IcoEntry(png.Width, png.Height, png.BitDepth * png.Channels, true, 0, 0);
        }
        var dib = BmpDecoder.ReadDibInfo(payload, iconImage: true);
        return new IcoEntry(dib.Width, dib.Height, dib.BitsPerPixel, false, 0, 0);
    }

    /// <summary>Decodes image <paramref name="index"/> into <paramref name="destination"/> (laid out by the caller).</summary>
    /// <exception cref="InvalidDataException">The image does not decode.</exception>
    public void DecodeInto(ReadOnlySpan<byte> data, int index, Span<byte> destination, int rowStride, ImagePixelFormat format)
    {
        var payload = Payload(data, index);
        if (Entries[index].IsPng)
        {
            if (!PngImageDecoder.TryDecodeInto(payload, destination, rowStride, format))
                throw new InvalidDataException($"Icon image {index}'s PNG stream does not decode.");
        }
        else
        {
            BmpDecoder.DecodeDibTo(payload, destination, format, rowStride, iconImage: true);
        }
    }

    public IcoImage Decode(ReadOnlySpan<byte> data, int index)
    {
        var entry = Entries[index];
        var rgba = new byte[(long)entry.Width * entry.Height * 4];
        DecodeInto(data, index, rgba, entry.Width * 4, ImagePixelFormat.Rgba32);
        return new IcoImage(entry, rgba);
    }
}
