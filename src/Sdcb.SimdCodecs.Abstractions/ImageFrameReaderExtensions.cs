using System;
using System.Collections.Generic;
using System.IO;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>A frame <see cref="ImageFrameReaderExtensions.EnumerateFramesInto"/> has just written.</summary>
/// <param name="Index">The frame's index in the reader.</param>
/// <param name="Duration">How long the frame is shown, for an animation; null for a page.</param>
public readonly record struct FrameTiming(int Index, TimeSpan? Duration);

/// <summary>Conveniences over <see cref="IImageFrameReader"/>, for every reader.</summary>
public static class ImageFrameReaderExtensions
{
    /// <summary>
    /// Plays the frames once, in order, through one caller-owned buffer — the playback loop. Each step of
    /// the enumeration decodes the next frame into <paramref name="destination"/> (as
    /// <see cref="IImageFrameReader.TryDecodeFrameInto"/>) and then yields its index and duration, so the
    /// buffer holds that frame until the next step overwrites it. Frames go in order, which is each
    /// reader's cheap path: an animation composites every frame once.
    /// <para>
    /// One pass, frame 0 to the last; to loop, enumerate again (<see cref="IImageFrameReader.LoopCount"/>
    /// says how often). The buffer must fit every frame: an animation's frames all have the canvas's
    /// size, a document's pages may not (with <paramref name="stride"/> 0 each page is packed to its own
    /// width). A frame that cannot be decoded ends the enumeration with an exception; to skip such a
    /// frame instead, call the reader's <c>Try</c> methods by index.
    /// </para>
    /// </summary>
    /// <param name="reader">The frames.</param>
    /// <param name="destination">The pixels of the current frame; rows <paramref name="stride"/> bytes apart.</param>
    /// <param name="stride">Bytes from one row to the next; 0 means tightly packed.</param>
    /// <param name="format">The pixel layout to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stride"/> is negative or <paramref name="format"/> unknown.</exception>
    /// <exception cref="ArgumentException">While enumerating: a frame does not fit <paramref name="destination"/> at <paramref name="stride"/>.</exception>
    /// <exception cref="NotSupportedException">While enumerating: a frame has float samples, which have no 8-bit projection.</exception>
    /// <exception cref="InvalidDataException">While enumerating: a frame is malformed or undecodable.</exception>
    public static IEnumerable<FrameTiming> EnumerateFramesInto(this IImageFrameReader reader,
        Memory<byte> destination, int stride, ImagePixelFormat format)
    {
        // Checked here, not on the first MoveNext: an iterator's body runs only when enumerated.
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegative(stride);
        _ = PixelConverter.BytesPerPixel(format); // throws for an unknown format
        return Frames(reader, destination, stride, format);
    }

    private static IEnumerable<FrameTiming> Frames(IImageFrameReader reader, Memory<byte> destination, int stride,
        ImagePixelFormat format)
    {
        for (var index = 0; index < reader.FrameCount; index++)
        {
            if (!reader.TryGetFrameInfo(index, out var frame))
                throw new InvalidDataException($"Frame {index} cannot be read: it is malformed, or in a layout its decoder does not present.");
            var info = frame.Info;
            if (info.SampleFormat == SampleFormat.Float32)
                throw new NotSupportedException($"Frame {index} has float samples, which have no 8-bit {format} projection.");
            if (!PixelConverter.TryGetLayout(info.Width, info.Height, format, stride, destination.Length, out _))
            {
                throw new ArgumentException(
                    $"Frame {index} ({info.Width}x{info.Height} {format}, stride {stride}) does not fit a destination of {destination.Length} bytes.",
                    nameof(destination));
            }
            if (!reader.TryDecodeFrameInto(index, destination.Span, stride, format))
                throw new InvalidDataException($"Frame {index} could not be decoded.");
            yield return new FrameTiming(index, frame.Duration);
        }
    }
}
