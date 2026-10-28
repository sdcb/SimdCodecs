using System;
using System.Diagnostics.CodeAnalysis;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// One frame of a multi-frame file, read from its headers: what it decodes to, and how long an
/// animation shows it.
/// </summary>
/// <param name="Info">The frame's layout, as <see cref="IImageDecoder.TryReadInfo"/> describes a still:
/// what <see cref="IImageFrameReader.TryDecodeFrame"/> returns for it. Its
/// <see cref="ImageInfo.FrameCount"/> is 1.</param>
/// <param name="Duration">How long the frame is shown, for an animation; null for a page.</param>
public readonly record struct ImageFrameInfo(ImageInfo Info, TimeSpan? Duration);

/// <summary>
/// The frames of an image file, decoded one at a time by index: an animation's frames (animated
/// WebP) or a document's pages (multi-page TIFF). A still image is a reader of one frame.
/// <para>
/// An animation's frame is what a viewer shows at that point: the canvas with every earlier frame
/// composited in, so every frame has the canvas's size. A page is an image of its own, with its own
/// size, layout and orientation. Decoding frame <c>i + 1</c> after frame <c>i</c> is the cheap path; any
/// other order may replay earlier frames (an animation restarts from the nearest frame that does not
/// depend on the ones before it), but gives the same pixels.
/// </para>
/// <para>
/// Each frame follows the still-image contract: <see cref="TryDecodeFrame"/> is the fidelity tier,
/// <see cref="TryDecodeFrameInto"/> writes its <see cref="IDecodedImage.ToRgba8"/> converted to the
/// requested layout, as <see cref="IImageDecoder.TryDecodeInto"/> does. A reader keeps decoding state
/// between calls (an animation's canvas), so it is not safe to share between threads; dispose it to
/// release that state. It holds on to the encoded data, which the caller must not change meanwhile.
/// </para>
/// </summary>
public interface IImageFrameReader : IDisposable
{
    /// <summary>
    /// The frames this reader decodes. It can be fewer than <see cref="ImageInfo.FrameCount"/> reports for
    /// the file, when the codec decodes only some of them (it then presents the still image the format
    /// defines as the fallback).
    /// </summary>
    int FrameCount { get; }

    /// <summary>
    /// For an animation, how many times it plays: 0 is forever. Null for a document of pages and for a
    /// still image.
    /// </summary>
    int? LoopCount { get; }

    /// <summary>Reads frame <paramref name="index"/>'s layout and duration without decoding it.
    /// False when its header is malformed.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not below <see cref="FrameCount"/>.</exception>
    bool TryGetFrameInfo(int index, out ImageFrameInfo frame);

    /// <summary>Decodes frame <paramref name="index"/> at its full fidelity (see
    /// <see cref="IImageDecoder.TryDecode"/>). False when the frame is undecodable.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not below <see cref="FrameCount"/>.</exception>
    bool TryDecodeFrame(int index, [NotNullWhen(true)] out IDecodedImage? image);

    /// <summary>
    /// Decodes frame <paramref name="index"/> into a caller-provided 8-bit destination in
    /// <paramref name="format"/>, rows <paramref name="stride"/> bytes apart (0: tightly packed), with
    /// the semantics of <see cref="IImageDecoder.TryDecodeInto"/>. Size it from
    /// <see cref="TryGetFrameInfo"/>. False when the destination does not fit, the frame is
    /// undecodable, or it has no 8-bit projection.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not below <see cref="FrameCount"/>.</exception>
    bool TryDecodeFrameInto(int index, Span<byte> destination, int stride, ImagePixelFormat format);
}

/// <summary>
/// An <see cref="IImageDecoder"/> for a format that holds several images (animation frames or pages):
/// it opens an <see cref="IImageFrameReader"/> over them. Frame 0 is the image
/// <see cref="IImageDecoder.TryDecode"/> returns, unless the format's still image lies outside its
/// animation (an APNG whose default image is not a frame).
/// </summary>
public interface IMultiFrameImageDecoder : IImageDecoder
{
    /// <summary>
    /// Opens a reader over every frame of <paramref name="data"/>, reading only what is needed to list
    /// them. False when the file is malformed. The reader keeps <paramref name="data"/>.
    /// </summary>
    static abstract bool TryOpenFrames(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out IImageFrameReader? reader);
}
