using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace Sdcb.SimdCodecs.Abstractions.CodecSupport;

/// <summary>One frame a <see cref="BufferedEncoderSession"/> has collected: its pixels, tightly packed, in the format
/// its rows arrived in.</summary>
public sealed class BufferedFrame
{
    internal BufferedFrame(int width, int height, PixelFormat format, byte[] pixels, ColorEncoding? color,
        ImageMetadata? metadata, TimeSpan? duration)
    {
        Width = width;
        Height = height;
        Format = format;
        PixelArray = pixels;
        Color = color;
        Metadata = metadata;
        Duration = duration;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The layout of <see cref="Pixels"/>.</summary>
    public PixelFormat Format { get; }

    /// <summary>The pixels, tightly packed (<c>Width × Format.BytesPerPixel</c> bytes a row).</summary>
    public ReadOnlySpan<byte> Pixels => PixelArray.AsSpan(0, Width * Height * Format.BytesPerPixel);

    internal byte[] PixelArray { get; }

    /// <summary>How the samples encode colour; null for sRGB.</summary>
    public ColorEncoding? Color { get; }

    /// <summary>Metadata blocks to write; null for none.</summary>
    public ImageMetadata? Metadata { get; }

    /// <summary>How long an animation shows the frame; null when not given.</summary>
    public TimeSpan? Duration { get; }

    /// <summary>The pixels converted to <paramref name="format"/>, tightly packed: what a codec without a native path for
    /// <see cref="Format"/> encodes (the frame's own array when it already is in that format).</summary>
    public ReadOnlySpan<byte> PixelsAs(PixelFormat format) =>
        format == Format ? Pixels : PixelConverter.ToPacked(Pixels, 0, Format, format, Width, Height);
}

/// <summary>
/// The encoder session for a codec whose encoder takes a whole image (or a whole animation): it collects each frame's
/// rows into pooled memory, then encodes every frame at <see cref="IImageEncoderSession.Commit"/>. The session contract
/// (<see cref="IImageEncoderSession"/>) holds as for a streaming encoder; only the memory saving is absent. A codec
/// derives from it and implements <see cref="Encode"/>.
/// </summary>
public abstract class BufferedEncoderSession : IImageEncoderSession
{
    private readonly List<BufferedFrame> _frames = [];
    private FrameWriter? _current;
    private bool _committed;
    private bool _disposed;

    /// <summary>Starts a session that writes to <paramref name="output"/>.</summary>
    protected BufferedEncoderSession(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        Output = output;
    }

    /// <summary>Where the file is written.</summary>
    protected IBufferWriter<byte> Output { get; }

    /// <inheritdoc />
    public abstract int MaxFrames { get; }

    /// <summary>The largest width and height the format stores (checked by <see cref="AddFrame"/>).</summary>
    protected virtual int MaxDimension => int.MaxValue;

    /// <inheritdoc />
    public IImageFrameEncoder AddFrame(int width, int height, PixelFormat format, ColorEncoding? color = null,
        ImageMetadata? metadata = null, TimeSpan? duration = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
            throw new InvalidOperationException("The session is committed.");
        if (_current is { Complete: false })
            throw new InvalidOperationException($"Frame {_frames.Count} is incomplete: {_current.RowsWritten} of {_current.Height} rows written.");
        if (_frames.Count + (_current is null ? 0 : 1) >= MaxFrames)
            throw new InvalidOperationException($"The format holds at most {MaxFrames} frame(s).");
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            throw new ArgumentException($"A frame is 1..{MaxDimension} pixels in each direction, not {width}x{height}.");
        if (_current is not null)
            _frames.Add(_current.Finish());
        var bytes = checked((long)width * height * format.BytesPerPixel);
        if (bytes > Array.MaxLength)
            throw new ArgumentException($"A {width}x{height} {format} frame does not fit in memory.");
        _current = new FrameWriter(width, height, format, color, metadata, duration);
        return _current;
    }

    /// <inheritdoc />
    public void Commit(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
            throw new InvalidOperationException("The session is already committed.");
        if (_current is null)
            throw new InvalidOperationException("No frame was added.");
        if (!_current.Complete)
            throw new InvalidOperationException($"Frame {_frames.Count} is incomplete: {_current.RowsWritten} of {_current.Height} rows written.");
        _frames.Add(_current.Finish());
        _current = null;
        _committed = true;
        Encode(_frames, cancellationToken);
    }

    /// <summary>Encodes the collected frames (at least one, at most <see cref="MaxFrames"/>) to <see cref="Output"/>.</summary>
    protected abstract void Encode(IReadOnlyList<BufferedFrame> frames, CancellationToken cancellationToken);

    /// <summary>Writes <paramref name="bytes"/> to <see cref="Output"/>: how a codec whose encoder returns a byte array
    /// hands it over.</summary>
    protected void WriteOutput(ReadOnlySpan<byte> bytes)
    {
        var span = Output.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        Output.Advance(bytes.Length);
    }

    /// <summary>Returns the frames' pooled memory.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var frame in _frames)
            ArrayPool<byte>.Shared.Return(frame.PixelArray);
        _frames.Clear();
        _current?.Release();
        _current = null;
        GC.SuppressFinalize(this);
    }

    private sealed class FrameWriter(int width, int height, PixelFormat format, ColorEncoding? color,
        ImageMetadata? metadata, TimeSpan? duration) : IImageFrameEncoder
    {
        private byte[]? _pixels = ArrayPool<byte>.Shared.Rent(width * height * format.BytesPerPixel);

        public int Height => height;

        public int RowsWritten { get; private set; }

        public bool Complete => RowsWritten == height;

        public void WriteRows(ReadOnlySpan<byte> rows, int rowCount, int stride = 0, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_pixels is null, this);
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
            ArgumentOutOfRangeException.ThrowIfNegative(stride);
            if (rowCount > height - RowsWritten)
                throw new ArgumentException($"{rowCount} rows run past the frame: {RowsWritten} of {height} are written.", nameof(rowCount));
            if (rowCount == 0)
                return;
            var rowBytes = width * format.BytesPerPixel;
            var step = stride == 0 ? rowBytes : stride;
            if (step < rowBytes)
                throw new ArgumentException($"A stride of {stride} is shorter than a {rowBytes}-byte row.", nameof(stride));
            if (rows.Length < (long)step * (rowCount - 1) + rowBytes)
                throw new ArgumentException($"{rowCount} rows at stride {step} need more than {rows.Length} bytes.", nameof(rows));
            for (var y = 0; y < rowCount; y++)
                rows.Slice(y * step, rowBytes).CopyTo(_pixels.AsSpan((RowsWritten + y) * rowBytes, rowBytes));
            RowsWritten += rowCount;
        }

        public BufferedFrame Finish()
        {
            var pixels = _pixels!;
            _pixels = null;
            return new BufferedFrame(width, height, format, pixels, color, metadata, duration);
        }

        public void Release()
        {
            if (_pixels is not null)
                ArrayPool<byte>.Shared.Return(_pixels);
            _pixels = null;
        }
    }
}

/// <summary>Helpers for implementing <see cref="IImageEncoder.GetClosestInputFormat"/>.</summary>
public static class EncoderFormats
{
    /// <summary>
    /// For an encoder that stores 8-bit samples: the requested format with 8-bit samples (alpha mode and channels kept).
    /// </summary>
    public static PixelFormat Closest8Bit(PixelFormat requested) => requested.To8Bit();

    /// <summary>For an encoder that stores 8- and 16-bit samples: the requested format itself.</summary>
    public static PixelFormat Same(PixelFormat requested) => requested;
}
