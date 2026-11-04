using System;
using System.Buffers;
using System.IO;
using System.Threading;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;
using Sdcb.SimdCodecs.Tiff;
using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs;

public static partial class SimdCodecs
{
    // The built-in encoders, bound through each codec's Registration property (method groups bound in the codec,
    // never in a generic method: see the decoder registry). RegisterEncoder replaces copy-on-write.
    private static ImageEncoderRegistration[] s_encoders =
    [
        PngImageEncoder.Registration,
        JpegImageEncoder.Registration,
        TiffImageEncoder.Registration,
        WebpImageEncoder.Registration,
    ];
    private static readonly Lock s_encoderLock = new();

    /// <summary>
    /// Enables encoding to a format the facade does not encode out of the box, or replaces the encoder for one
    /// (<c>SimdCodecs.RegisterEncoder(MyEncoder.Registration)</c>). PNG, JPEG, TIFF, WebP, GIF, BMP, ICO, QOI and Netpbm
    /// are built in. Registering the same encoder again does nothing; a later registration for a format replaces
    /// the earlier one.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="registration"/> is null.</exception>
    public static void RegisterEncoder(ImageEncoderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (s_encoderLock)
        {
            var current = s_encoders;
            var list = new System.Collections.Generic.List<ImageEncoderRegistration>(current.Length + 1);
            foreach (var entry in current)
            {
                if (entry.Format != registration.Format)
                    list.Add(entry);
            }
            list.Add(registration);
            Volatile.Write(ref s_encoders, list.ToArray());
        }
    }

    /// <summary>True when a registered encoder writes <paramref name="format"/>.</summary>
    public static bool IsEncodable(ImageFormat format) => FindEncoder(format) is not null;

    /// <summary>
    /// The pixel format <paramref name="format"/>'s encoder stores <paramref name="requested"/> as without converting it
    /// (see <see cref="IImageEncoder.GetClosestInputFormat"/>), so a caller sees a precision loss before making it.
    /// </summary>
    /// <exception cref="NotSupportedException">No registered encoder writes <paramref name="format"/>.</exception>
    public static PixelFormat GetClosestInputFormat(ImageFormat format, PixelFormat requested) =>
        RequireEncoder(format).GetClosestInputFormat(requested);

    /// <summary>Opens an encoding session for <paramref name="format"/> writing to <paramref name="output"/>.</summary>
    /// <exception cref="NotSupportedException">No registered encoder writes <paramref name="format"/>.</exception>
    public static IImageEncoderSession CreateEncoder(ImageFormat format, IBufferWriter<byte> output, EncodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        return RequireEncoder(format).Create(output, options);
    }

    /// <summary>
    /// Encodes <paramref name="image"/> as <paramref name="format"/> into <paramref name="destination"/>: its pixels (an
    /// 8-bit-only format receives them narrowed, <c>v &gt;&gt; 8</c>), its <see cref="IDecodedImage.ColorEncoding"/> and
    /// its <see cref="IDecodedImage.Metadata"/> (Exif, XMP, ICC), so a transcode keeps the capture time, GPS position and
    /// colour profile. Float images cannot be encoded this way (no integer projection).
    /// </summary>
    /// <exception cref="NotSupportedException">No registered encoder writes <paramref name="format"/>, or the image has
    /// float samples.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static void Encode(IDecodedImage image, ImageFormat format, Stream destination, EncodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var buffer = new ArrayBufferWriter<byte>();
        Encode(image, format, buffer, options, cancellationToken);
        destination.Write(buffer.WrittenSpan);
    }

    /// <summary><see cref="Encode(IDecodedImage, ImageFormat, Stream, EncodeOptions?, CancellationToken)"/> into a buffer
    /// writer (for a <c>Stream</c> with no copy: <c>PipeWriter.Create(stream)</c>).</summary>
    public static void Encode(IDecodedImage image, ImageFormat format, IBufferWriter<byte> destination, EncodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(destination);
        if (image.SampleFormat is not (SampleFormat.UInt8 or SampleFormat.UInt16))
            throw new NotSupportedException($"{image.SampleFormat} images have no integer projection to encode; convert them first.");
        var source = PixelFormat.ForChannels(image.Channels, image.SampleFormat);
        var metadata = image.Metadata;
        if (metadata.Icc.IsEmpty && !image.IccProfile.IsEmpty)
            metadata = metadata.WithIcc(image.IccProfile.ToArray());
        using var session = CreateEncoder(format, destination, options);
        var frame = session.AddFrame(image.Width, image.Height, source, image.ColorEncoding, metadata.IsEmpty ? null : metadata);
        frame.WriteRows(image.Pixels, image.Height, 0, cancellationToken);
        session.Commit(cancellationToken);
    }

    /// <summary>
    /// Encodes <paramref name="width"/> × <paramref name="height"/> pixels in <paramref name="pixelFormat"/> as
    /// <paramref name="format"/> and returns the file: the one-call form of a session with one frame.
    /// </summary>
    /// <exception cref="NotSupportedException">No registered encoder writes <paramref name="format"/>.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat pixelFormat, ImageFormat format,
        int stride = 0, EncodeOptions? options = null, ImageMetadata? metadata = null, CancellationToken cancellationToken = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var session = CreateEncoder(format, buffer, options))
        {
            session.AddFrame(width, height, pixelFormat, null, metadata).WriteRows(pixels, height, stride, cancellationToken);
            session.Commit(cancellationToken);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static ImageEncoderRegistration? FindEncoder(ImageFormat format)
    {
        foreach (var entry in Volatile.Read(ref s_encoders))
        {
            if (entry.Format == format)
                return entry;
        }
        return null;
    }

    private static ImageEncoderRegistration RequireEncoder(ImageFormat format) =>
        FindEncoder(format) ?? throw new NotSupportedException($"No registered encoder writes {format}.");
}
