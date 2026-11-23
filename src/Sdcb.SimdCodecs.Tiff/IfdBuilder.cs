using System.Runtime.InteropServices;
using System.Text;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// Accumulates IFD entries in tag-sorted order and writes a complete IFD.
/// Returns the file offset of the NextIFD field for chaining.
/// </summary>
internal sealed class IfdBuilder
{
    private readonly SortedDictionary<ushort, (TiffFieldType Type, uint Count, byte[] ValueBytes)> _entries = new();

    public void SetShort(ushort tag, ushort value)
    {
        // Host byte order: TiffWriter emits an "II" header on LE hosts and
        // "MM" on BE hosts so multi-byte tag values can be written verbatim
        // without a swap. MemoryMarshal.Write stores the raw memory bytes
        // in native order.
        var bytes = new byte[2];
        MemoryMarshal.Write(bytes.AsSpan(), in value);
        _entries[tag] = (TiffFieldType.Short, 1, bytes);
    }

    public void SetLong(ushort tag, uint value)
    {
        var bytes = new byte[4];
        MemoryMarshal.Write(bytes.AsSpan(), in value);
        _entries[tag] = (TiffFieldType.Long, 1, bytes);
    }

    public void SetRational(ushort tag, uint numerator, uint denominator)
    {
        var bytes = new byte[8];
        MemoryMarshal.Write(bytes.AsSpan(0, 4), in numerator);
        MemoryMarshal.Write(bytes.AsSpan(4, 4), in denominator);
        _entries[tag] = (TiffFieldType.Rational, 1, bytes);
    }

    public void SetAscii(ushort tag, string value)
    {
        var bytes = new byte[Encoding.ASCII.GetByteCount(value) + 1]; // null-terminated
        Encoding.ASCII.GetBytes(value, bytes);
        bytes[^1] = 0;
        _entries[tag] = (TiffFieldType.Ascii, (uint)bytes.Length, bytes);
    }

    public void SetShortArray(ushort tag, ushort[] values)
    {
        // Cast the typed array straight to bytes — its in-memory layout is
        // already the host byte order, which is what the file expects.
        var src = MemoryMarshal.AsBytes(values.AsSpan());
        var bytes = new byte[src.Length];
        src.CopyTo(bytes);
        _entries[tag] = (TiffFieldType.Short, (uint)values.Length, bytes);
    }

    public void SetLongArray(ushort tag, uint[] values)
    {
        var src = MemoryMarshal.AsBytes(values.AsSpan());
        var bytes = new byte[src.Length];
        src.CopyTo(bytes);
        _entries[tag] = (TiffFieldType.Long, (uint)values.Length, bytes);
    }

    public void SetFloatArray(ushort tag, float[] values)
    {
        var src = MemoryMarshal.AsBytes(values.AsSpan());
        var bytes = new byte[src.Length];
        src.CopyTo(bytes);
        _entries[tag] = (TiffFieldType.Float, (uint)values.Length, bytes);
    }

    public void SetUndefined(ushort tag, byte[] data)
    {
        _entries[tag] = (TiffFieldType.Undefined, (uint)data.Length, data);
    }

    public void SetBytes(ushort tag, byte[] data)
    {
        _entries[tag] = (TiffFieldType.Byte, (uint)data.Length, data);
    }

    /// <summary>An entry copied from another TIFF structure; its value bytes are already in host byte order.</summary>
    public void Set(TiffExifEntry entry)
    {
        _entries[entry.Tag] = (entry.Type, entry.Count, entry.Value);
    }

    /// <summary>
    /// Writes the IFD to the target. Returns the file offset of the NextIFD pointer field.
    /// </summary>
    public async Task<long> WriteAsync(TiffFileTarget target, CancellationToken ct = default)
    {
        await target.AlignAsync(ct).ConfigureAwait(false);
        var ifdStart = target.Position;
        var bytes = Serialize(ifdStart, out var nextIfdField);
        await target.WriteAsync(bytes, ct).ConfigureAwait(false);
        return ifdStart + nextIfdField;
    }

    /// <summary>
    /// The IFD as it is stored at file offset <paramref name="ifdStart"/> (a word boundary): the entry count, the
    /// entries in tag order, the NextIFD pointer (0; the caller patches it) and then every value longer than four bytes,
    /// in entry order. <paramref name="nextIfdField"/> is the NextIFD pointer's offset within the returned bytes.
    /// </summary>
    public byte[] Serialize(long ifdStart, out int nextIfdField)
    {
        const int entrySize = 12; // tag(2) + type(2) + count(4) + value/offset(4)
        var entryCount = (ushort)_entries.Count;
        nextIfdField = 2 + entryCount * entrySize;
        var length = (long)nextIfdField + 4;
        foreach (var (_, (_, _, valueBytes)) in _entries)
            if (valueBytes.Length > 4)
                length += valueBytes.Length;
        if (ifdStart + length > uint.MaxValue)
            throw new IOException("The TIFF file would pass 4 GiB, the most a classic TIFF offset addresses.");

        var bytes = new byte[length];
        var span = bytes.AsSpan();
        MemoryMarshal.Write(span, in entryCount);

        // Overflow data starts after all entries + NextIFD pointer.
        var overflowOffset = (uint)(ifdStart + nextIfdField + 4);
        var at = 2;
        var overflowAt = nextIfdField + 4;
        foreach (var (tag, (type, count, valueBytes)) in _entries)
        {
            var typeShort = (ushort)type;
            MemoryMarshal.Write(span.Slice(at, 2), in tag);
            MemoryMarshal.Write(span.Slice(at + 2, 2), in typeShort);
            MemoryMarshal.Write(span.Slice(at + 4, 4), in count);
            if (valueBytes.Length <= 4)
            {
                // Inline: padded to 4 bytes (the array is zeroed).
                valueBytes.CopyTo(span.Slice(at + 8));
            }
            else
            {
                MemoryMarshal.Write(span.Slice(at + 8, 4), in overflowOffset);
                overflowOffset += (uint)valueBytes.Length;
                valueBytes.CopyTo(span.Slice(overflowAt));
                overflowAt += valueBytes.Length;
            }
            at += entrySize;
        }
        // NextIFD pointer stays 0 (no next page) until the caller patches it.
        return bytes;
    }
}
