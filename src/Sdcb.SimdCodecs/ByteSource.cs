using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace Sdcb.SimdCodecs;

/// <summary>
/// Random-access bytes an image is read from: memory, a seekable stream or a memory-mapped file. The session API
/// (<see cref="ImageDecoder"/>) reads through it, so a codec can later fetch only the parts it needs (a HEIF item,
/// a TIFF strip) instead of the whole file. In 2.0 every codec still decodes from the whole file in memory:
/// <see cref="ImageDecoder"/> materialises it once (<see cref="GetMemory"/>), without a copy for memory and mapped
/// files.
/// </summary>
public interface IByteSource : IDisposable
{
    /// <summary>The length in bytes; −1 when unknown.</summary>
    long Length { get; }

    /// <summary>True when <see cref="Read"/> can read at any offset.</summary>
    bool CanSeek { get; }

    /// <summary>Reads up to <c>destination.Length</c> bytes from <paramref name="offset"/>; returns the bytes read
    /// (fewer only at the end of the source).</summary>
    int Read(long offset, Span<byte> destination);

    /// <summary>The whole source as one block of memory (reading and buffering it if needed).</summary>
    /// <exception cref="InvalidDataException">The source is longer than an array can hold.</exception>
    ReadOnlyMemory<byte> GetMemory();
}

/// <summary>The <see cref="IByteSource"/> implementations the session API opens.</summary>
public static class ByteSource
{
    /// <summary>A source over memory the caller keeps unchanged while it is used.</summary>
    public static IByteSource FromMemory(ReadOnlyMemory<byte> data) => new MemorySource(data);

    /// <summary>
    /// A source over <paramref name="stream"/>. A seekable stream is read at the offsets asked for; any other stream
    /// is buffered whole into memory first (a network or request-body stream has no other way to be revisited).
    /// </summary>
    /// <param name="stream">The stream, positioned at the start of the image.</param>
    /// <param name="leaveOpen">Keep <paramref name="stream"/> open when the source is disposed.</param>
    /// <exception cref="InvalidDataException">The stream is longer than an array can hold.</exception>
    public static IByteSource FromStream(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek)
            return new StreamSource(stream, leaveOpen);
        var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        if (!leaveOpen)
            stream.Dispose();
        return new MemorySource(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
    }

    /// <summary>A source over a file, memory-mapped read-only.</summary>
    /// <exception cref="InvalidDataException">The file is empty or longer than an array can hold.</exception>
    public static IByteSource FromFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new MappedFileSource(path);
    }

    private sealed class MemorySource(ReadOnlyMemory<byte> data) : IByteSource
    {
        public long Length => data.Length;
        public bool CanSeek => true;

        public int Read(long offset, Span<byte> destination)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (offset >= data.Length) return 0;
            var n = (int)Math.Min(destination.Length, data.Length - offset);
            data.Span.Slice((int)offset, n).CopyTo(destination);
            return n;
        }

        public ReadOnlyMemory<byte> GetMemory() => data;
        public void Dispose() { }
    }

    private sealed class StreamSource(Stream stream, bool leaveOpen) : IByteSource
    {
        private readonly long _origin = stream.Position;
        private byte[]? _all;

        public long Length => stream.Length - _origin;
        public bool CanSeek => true;

        public int Read(long offset, Span<byte> destination)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            stream.Position = _origin + offset;
            var total = 0;
            while (total < destination.Length)
            {
                var n = stream.Read(destination[total..]);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        public ReadOnlyMemory<byte> GetMemory()
        {
            if (_all is not null) return _all;
            if (Length > Array.MaxLength)
                throw new InvalidDataException($"The image is {Length} bytes, more than an array can hold.");
            var all = new byte[Length];
            if (Read(0, all) != all.Length)
                throw new InvalidDataException("The stream ended before its declared length.");
            return _all = all;
        }

        public void Dispose()
        {
            if (!leaveOpen) stream.Dispose();
        }
    }

    private sealed unsafe class MappedFileSource : IByteSource
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;
        private readonly byte* _pointer;
        private readonly long _length;
        private UnmanagedMemory? _memory;

        public MappedFileSource(string path)
        {
            _length = new FileInfo(path).Length;
            if (_length == 0 || _length > Array.MaxLength)
                throw new InvalidDataException($"The file is {_length} bytes; an image file is 1..{Array.MaxLength} bytes.");
            _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _view = _file.CreateViewAccessor(0, _length, MemoryMappedFileAccess.Read);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _pointer = p + _view.PointerOffset;
        }

        public long Length => _length;
        public bool CanSeek => true;

        public int Read(long offset, Span<byte> destination)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (offset >= _length) return 0;
            var n = (int)Math.Min(destination.Length, _length - offset);
            new ReadOnlySpan<byte>(_pointer + offset, n).CopyTo(destination);
            return n;
        }

        public ReadOnlyMemory<byte> GetMemory() => (_memory ??= new UnmanagedMemory(_pointer, (int)_length)).Memory;

        public void Dispose()
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _file.Dispose();
        }
    }

    // A Memory<byte> over the mapped view, valid until the source is disposed.
    private sealed unsafe class UnmanagedMemory(byte* pointer, int length) : System.Buffers.MemoryManager<byte>
    {
        public override Span<byte> GetSpan() => new(pointer, length);
        public override System.Buffers.MemoryHandle Pin(int elementIndex = 0) => new(pointer + elementIndex);
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
