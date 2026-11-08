using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Png;

public static partial class PngReader
{
    /// <summary>
    /// The layout <see cref="PngImageDecoder.TryDecode"/> gives a non-interlaced image (its fidelity layout): indexed
    /// as 8-bit RGBA, everything else in its own channels and depth (16-bit in host byte order), plus an alpha channel
    /// for a tRNS transparent colour.
    /// </summary>
    internal static PixelFormat FidelityFormat(PngImage header)
    {
        if (header.ColorType == 3)
            return PixelFormat.ForChannels(4, SampleFormat.UInt8);
        var channels = SamplesPerPixel(header.ColorType) + (header.TransparentColor is null ? 0 : 1);
        return PixelFormat.ForChannels(channels, header.SourceBitDepth == 16 ? SampleFormat.UInt16 : SampleFormat.UInt8);
    }

    /// <summary>
    /// Decodes a window of a non-interlaced image, optionally box-downscaled, straight into
    /// <paramref name="destination"/> in <paramref name="format"/>, rows <paramref name="dstStride"/> bytes apart:
    /// exactly what the full decode in the fidelity layout (<see cref="FidelityFormat"/>), then
    /// <see cref="BoxDownscaler"/> by <paramref name="factor"/> over <paramref name="sourceRect"/>, then
    /// <see cref="PixelConverter"/> to <paramref name="format"/> gives, without a buffer the size of the image.
    /// <para>Rows are inflated and unfiltered only down to the last row of <paramref name="sourceRect"/>, and only as far
    /// right as its last column (a pixel's unfiltering needs only the pixels left of and above it); the decode then
    /// stops, so image data below the window is never inflated and never checked. When the window reaches the bottom
    /// row, the end of the stream is checked as the full decode checks it. Rows above the window are unfiltered but
    /// not expanded, and only the window's columns are expanded.</para>
    /// </summary>
    /// <param name="sourceRect">The source pixels to decode: for <paramref name="factor"/> &gt; 1 a window aligned to
    /// box boundaries (<see cref="BoxDownscaler.GetSourceRect"/>).</param>
    /// <exception cref="InvalidDataException">The image data is corrupt within the rows decoded, or an indexed pixel in
    /// the window lies past the palette's end.</exception>
    /// <exception cref="ArgumentException">The image is interlaced, the window does not lie inside the image, or the
    /// destination does not fit.</exception>
    internal static unsafe void DecodeWindow(ReadOnlySpan<byte> data, PngImage header, List<(int Offset, int Length)> idat,
        PixelRect sourceRect, int factor, Span<byte> destination, int dstStride, PixelFormat format,
        CancellationToken cancellationToken = default)
    {
        if (header.Interlaced)
            throw new ArgumentException("DecodeWindow decodes non-interlaced images only.", nameof(header));
        int width = header.Width, height = header.Height, bitDepth = header.SourceBitDepth, colorType = header.ColorType;
        if (sourceRect.Width <= 0 || sourceRect.Height <= 0 || !sourceRect.FitsIn(width, height))
            throw new ArgumentException($"The window {sourceRect} does not lie inside the {width}x{height} image.", nameof(sourceRect));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);

        int x0 = sourceRect.X, w = sourceRect.Width, x1 = x0 + w, y0 = sourceRect.Y, y1 = y0 + sourceRect.Height;
        var outWidth = (int)(((long)w + factor - 1) / factor);
        var outHeight = (int)(((long)sourceRect.Height + factor - 1) / factor);
        if (!PixelConverter.TryGetLayout(outWidth, outHeight, format, dstStride, destination.Length, out var stride))
            throw new ArgumentException($"The destination does not fit a {outWidth}x{outHeight} {format} image at stride {dstStride}.", nameof(destination));
        var outRowBytes = outWidth * format.BytesPerPixel;

        var fidelity = FidelityFormat(header);
        var spp = SamplesPerPixel(colorType);
        var key = header.UnpackedTransparentColor();
        var filterBpp = bitDepth < 8 ? 1 : spp * (bitDepth / 8);
        var rowBytes = checked((int)(((long)width * spp * bitDepth + 7) / 8));
        // The bytes of a row that hold columns 0 .. x1 − 1: all its unfiltering needs.
        var prefix = (int)(((long)x1 * spp * bitDepth + 7) / 8);
        // A row in the fidelity layout is built in scratch unless it is the samples as they are (8-bit, no key).
        var needsBuild = colorType == 3 || key is not null || bitDepth == 16;
        var fidelityRowBytes = needsBuild ? w * fidelity.BytesPerPixel : 0;
        var box = factor > 1 ? new BoxDownscaler(w, sourceRect.Height, fidelity.Channels, fidelity.Sample, factor) : null;
        var boxRowBytes = box is not null && format != fidelity ? box.OutputRowBytes : 0;
        var unpackedBytes = bitDepth < 8 ? x1 : 0;
        // Index expansion: the 8-bit indices of the row (unpacked first when sub-byte) through the palette, as
        // PngImage.ToRgba8 maps them.
        var palette = colorType == 3 ? new PngRgba8Expander(3, 8, header.Palette, header.PaletteAlpha) : null;

        var scratch = ArrayPool<byte>.Shared.Rent(checked(2 * rowBytes + unpackedBytes + fidelityRowBytes + boxRowBytes));
        try
        {
            var current = scratch.AsSpan(0, rowBytes);
            var above = scratch.AsSpan(rowBytes, rowBytes);
            var unpacked = scratch.AsSpan(2 * rowBytes, unpackedBytes);
            var built = scratch.AsSpan(2 * rowBytes + unpackedBytes, fidelityRowBytes);
            var boxRow = scratch.AsSpan(2 * rowBytes + unpackedBytes + fidelityRowBytes, boxRowBytes);
            above[..prefix].Clear();

            fixed (byte* pinned = data)
            {
                using var rows = new RowInflater(pinned, idat, ((long)rowBytes + 1) * height, width, height, filterBpp);
                for (var y = 0; y < y1; y++)
                {
                    if ((y & 63) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    var filterType = rows.Read(current);
                    PngPredictor.UnfilterRow(filterType, current[..prefix], above[..prefix], filterBpp);
                    if (y >= y0)
                    {
                        var row = FidelityRow(current, x0, w, bitDepth, colorType, spp, key, palette, unpacked, built);
                        if (box is null)
                        {
                            PixelConverter.ConvertRow(row, fidelity, destination.Slice((y - y0) * stride, outRowBytes), format, w);
                        }
                        else if (box.PushRow(row))
                        {
                            var target = destination.Slice(box.RowsTaken * stride, outRowBytes);
                            if (boxRowBytes == 0)
                            {
                                box.TakeRow(target);
                            }
                            else
                            {
                                box.TakeRow(boxRow);
                                PixelConverter.ConvertRow(boxRow, fidelity, target, format, outWidth);
                            }
                        }
                    }
                    var swap = above;
                    above = current;
                    current = swap;
                }
                // Stopping early leaves the rest of the stream unread and unchecked; a window down to the last row
                // checks the stream's end as the full decode does.
                if (y1 == height)
                    rows.Finish();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// Columns <paramref name="x0"/> .. <paramref name="x0"/> + <paramref name="w"/> − 1 of an unfiltered row in the
    /// fidelity layout, the samples <c>PngImageDecoder.ToDecoded</c> gives them: the row itself for 8-bit samples without
    /// a transparent colour, otherwise built in <paramref name="built"/>.
    /// </summary>
    private static ReadOnlySpan<byte> FidelityRow(ReadOnlySpan<byte> row, int x0, int w, int bitDepth, int colorType,
        int spp, ushort[]? key, PngRgba8Expander? palette, Span<byte> unpacked, Span<byte> built)
    {
        if (bitDepth < 8)
        {
            // Greyscale or indexed, one sample per pixel: unpacked to a byte each up to the window's right edge.
            PngRgba8Expander.UnpackSubByte(row, unpacked, unpacked.Length, bitDepth, scaleGray: colorType == 0);
            row = unpacked;
            bitDepth = 8;
        }

        if (palette is not null)
        {
            palette.Expand(row[x0..], built, w);
            return built;
        }

        if (bitDepth == 8)
        {
            var samples = row.Slice(x0 * spp, w * spp);
            if (key is null)
                return samples;
            // tRNS: alpha 0 on exactly the pixels of the key colour, 255 elsewhere (the key is masked to the depth).
            var o = 0;
            for (var i = 0; i < samples.Length; i += spp)
            {
                var match = true;
                for (var c = 0; c < spp; c++)
                {
                    var v = samples[i + c];
                    built[o++] = v;
                    match &= v == key[c];
                }
                built[o++] = match ? (byte)0 : byte.MaxValue;
            }
            return built;
        }

        // 16-bit: big-endian in the file, host order in the fidelity layout; a key compares at full precision.
        var source = row.Slice(x0 * spp * 2, w * spp * 2);
        var output = MemoryMarshal.Cast<byte, ushort>(built);
        if (key is null)
        {
            var input = MemoryMarshal.Cast<byte, ushort>(source);
            if (BitConverter.IsLittleEndian)
                BinaryPrimitives.ReverseEndianness(input, output);
            else
                input.CopyTo(output);
            return built;
        }
        var k = 0;
        for (var i = 0; i < source.Length; i += 2 * spp)
        {
            var match = true;
            for (var c = 0; c < spp; c++)
            {
                var v = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(i + 2 * c, 2));
                output[k++] = v;
                match &= v == key[c];
            }
            output[k++] = match ? (ushort)0 : ushort.MaxValue;
        }
        return built;
    }
}
