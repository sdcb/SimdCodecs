using System.Runtime.CompilerServices;

namespace Sdcb.SimdCodecs.Heif.Isobmff;

/// <summary>
/// Reads ISOBMFF boxes from a contiguous span of bytes.
/// Shared source: compiled into each codec package under its own namespace
/// (see src/Shared/Isobmff/BoxReader.cs and each package's .csproj Compile item).
/// </summary>
internal ref struct BoxReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private readonly int _end;

    /// <summary>Initialises a reader over <paramref name="data"/>[<paramref name="start"/>..<paramref name="end"/>].</summary>
    internal BoxReader(ReadOnlySpan<byte> data, int start, int end)
    {
        _data = data;
        _pos = start;
        _end = end;
    }

    /// <summary>Initialises a reader over the full span.</summary>
    internal BoxReader(ReadOnlySpan<byte> data) : this(data, 0, data.Length) { }

    /// <summary>Current read position.</summary>
    internal readonly int Position => _pos;

    /// <summary><see langword="true"/> when at least 8 bytes remain for a box header.</summary>
    internal readonly bool HasBox => _pos + 8 <= _end;

    /// <summary>
    /// Reads the next box header (size + type).
    /// Returns <see langword="false"/> when the stream is exhausted or the header is invalid.
    /// On success, <paramref name="type"/> receives the 4-byte FourCC,
    /// <paramref name="payloadStart"/> the offset of the payload,
    /// <paramref name="boxEnd"/> the exclusive end of the whole box.
    /// </summary>
    internal bool TryReadBox(out uint type, out int payloadStart, out int boxEnd)
    {
        if (_pos + 8 > _end)
        {
            type = 0; payloadStart = 0; boxEnd = 0;
            return false;
        }

        long size = ReadU32BE(_pos);
        type = ReadU32BE(_pos + 4);

        if (size == 1)
        {
            // Extended-size: next 8 bytes hold the actual size.
            if (_pos + 16 > _end) { payloadStart = 0; boxEnd = 0; return false; }
            size = (long)ReadU64BE(_pos + 8);
            payloadStart = _pos + 16;
        }
        else if (size == 0)
        {
            // Box extends to the end of the container.
            size = _end - _pos;
            payloadStart = _pos + 8;
        }
        else
        {
            payloadStart = _pos + 8;
        }

        if (size < 8 || _pos + size > _end)
        {
            payloadStart = 0; boxEnd = 0;
            return false;
        }

        boxEnd = _pos + (int)size;
        _pos = boxEnd;
        return true;
    }

    /// <summary>
    /// Skips the FullBox version + flags (4 bytes) and returns the version byte.
    /// Must be called immediately after <see cref="TryReadBox"/> when the box is a FullBox.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadFullBoxHeader(int payloadStart) => _data[payloadStart];

    // ── Raw readers (big-endian, unchecked — callers validate bounds via box size) ─

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly uint ReadU32BE(int offset) =>
        ((uint)_data[offset] << 24) | ((uint)_data[offset + 1] << 16) |
        ((uint)_data[offset + 2] << 8) | _data[offset + 3];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly ulong ReadU64BE(int offset) =>
        ((ulong)ReadU32BE(offset) << 32) | ReadU32BE(offset + 4);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly ushort ReadU16BE(int offset) =>
        (ushort)((_data[offset] << 8) | _data[offset + 1]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly byte ReadU8(int offset) => _data[offset];

    /// <summary>Returns the 4-byte FourCC at <paramref name="offset"/> as a <see cref="uint"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly uint FourCC(int offset) => ReadU32BE(offset);

    /// <summary>
    /// Returns a slice of the underlying data, bounds-checked against the current box.
    /// </summary>
    internal readonly ReadOnlySpan<byte> Slice(int offset, int length) =>
        _data.Slice(offset, length);

    /// <summary>
    /// Returns the remaining bytes within the current box payload as a span.
    /// </summary>
    internal readonly ReadOnlySpan<byte> SlicePayload(int payloadStart, int boxEnd) =>
        _data.Slice(payloadStart, boxEnd - payloadStart);

    // ── FourCC helpers ────────────────────────────────────────────────────────────

    internal static uint MakeFourCC(byte a, byte b, byte c, byte d) =>
        ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;

    internal static uint MakeFourCC(ReadOnlySpan<byte> s) =>
        MakeFourCC(s[0], s[1], s[2], s[3]);
}
