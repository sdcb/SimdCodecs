using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Qoi;

/// <summary>
/// Pure-managed QOI encoder, written from the specification at qoiformat.org. Each pixel takes the first chunk
/// that fits, in the order run, index, diff, luma, RGB, RGBA; the output decodes with any QOI decoder.
/// RGBA and BGRA input is written as 4 channels, RGB, BGR and grey as 3.
/// </summary>
public static class QoiEncoder
{
    private const int Chunk = 512;

    // A chunk of pixels writes at most 5 bytes each, plus a run left over from before it.
    private const int ChunkOutput = Chunk * 5 + 1;

    /// <summary>Encodes a QOI file.</summary>
    /// <param name="pixels">The image, top row first.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">The input layout: any <see cref="PixelFormat"/>. QOI stores 8-bit RGB or RGBA, so 16-bit
    /// input keeps its high byte, premultiplied input is unpremultiplied, grey is stored as RGB and an alpha channel
    /// makes a 4-channel file (all by <see cref="PixelConverter"/>).</param>
    /// <param name="colorSpace">The colour space byte to write; it does not change the pixels.</param>
    /// <param name="stride">Bytes from one input row to the next; 0 means tightly packed.</param>
    /// <exception cref="ArgumentException">The size or buffer does not fit.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format,
        QoiColorSpace colorSpace = QoiColorSpace.Srgb, int stride = 0)
    {
        var rowStride = Validate(pixels, width, height, format, colorSpace, stride);
        // The worst case, 5 bytes a pixel, in a pooled buffer; the file is a copy of what it took.
        var worst = QoiCodec.HeaderSize + 5L * width * height + ChunkOutput + QoiCodec.PaddingSize + 1;
        if (worst > Array.MaxLength)
        {
            var growing = new ArrayBufferWriter<byte>(1 << 20);
            Write(pixels, width, height, format, colorSpace, rowStride, growing);
            return growing.WrittenSpan.ToArray();
        }
        var buffer = ArrayPool<byte>.Shared.Rent((int)worst);
        try
        {
            var writer = new ArrayWriter(buffer);
            Write(pixels, width, height, format, colorSpace, rowStride, writer);
            return buffer.AsSpan(0, writer.Written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Encodes a QOI file into <paramref name="output"/> through one pooled buffer: no buffer of the whole file is
    /// allocated.
    /// </summary>
    /// <param name="pixels">The image, top row first.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">The input layout: any <see cref="PixelFormat"/> (see the other overload).</param>
    /// <param name="output">Where the file is written, from its current position.</param>
    /// <param name="colorSpace">The colour space byte to write; it does not change the pixels.</param>
    /// <param name="stride">Bytes from one input row to the next; 0 means tightly packed.</param>
    /// <exception cref="ArgumentException">The size or buffer does not fit.</exception>
    public static void Encode(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format, Stream output,
        QoiColorSpace colorSpace = QoiColorSpace.Srgb, int stride = 0)
    {
        ArgumentNullException.ThrowIfNull(output);
        var rowStride = Validate(pixels, width, height, format, colorSpace, stride);
        using var writer = new StreamBufferWriter(output);
        Write(pixels, width, height, format, colorSpace, rowStride, writer);
        writer.Flush();
    }

    /// <summary>
    /// Encodes a QOI file into <paramref name="output"/>, checking <paramref name="cancellationToken"/> every 64 rows:
    /// the writer behind the encoder abstraction's QOI session.
    /// </summary>
    internal static void Encode(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format, IBufferWriter<byte> output,
        QoiColorSpace colorSpace, int stride, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        var rowStride = Validate(pixels, width, height, format, colorSpace, stride);
        Write(pixels, width, height, format, colorSpace, rowStride, output, cancellationToken);
    }

    private static int Validate(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format, QoiColorSpace colorSpace, int stride)
    {
        if (width <= 0 || height <= 0 || (long)width * height > QoiCodec.MaximumPixels)
            throw new ArgumentException($"QOI size {width}x{height} must be positive and at most {QoiCodec.MaximumPixels:N0} pixels.");
        if (colorSpace is not (QoiColorSpace.Srgb or QoiColorSpace.Linear))
            throw new ArgumentOutOfRangeException(nameof(colorSpace));
        if (!PixelConverter.TryGetLayout(width, height, format, stride, pixels.Length, out var rowStride))
            throw new ArgumentException($"The pixels do not hold a {width}x{height} {format} image with stride {stride}.", nameof(pixels));
        return rowStride;
    }

    private static void Write(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format, QoiColorSpace colorSpace,
        int rowStride, IBufferWriter<byte> output, CancellationToken cancellationToken = default)
    {
        var header = output.GetSpan(QoiCodec.HeaderSize);
        "qoif"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)height);
        header[12] = format.HasAlpha ? (byte)4 : (byte)3;
        header[13] = (byte)colorSpace;
        output.Advance(QoiCodec.HeaderSize);

        // Every pixel goes to the encoder as straight 8-bit RGBA; RGBA32 input already is.
        var bpp = format.BytesPerPixel;
        Span<uint> rgba = stackalloc uint[Chunk];
        var state = new Writer();
        for (var y = 0; y < height; y++)
        {
            if ((y & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var row = pixels.Slice(y * rowStride, width * bpp);
            for (var x = 0; x < width; x += Chunk)
            {
                var n = Math.Min(Chunk, width - x);
                scoped ReadOnlySpan<uint> chunk;
                if (format == PixelFormat.Rgba32)
                {
                    chunk = MemoryMarshal.Cast<byte, uint>(row.Slice(x * 4, n * 4));
                }
                else
                {
                    PixelConverter.ConvertRow(row.Slice(x * bpp, n * bpp), format, MemoryMarshal.AsBytes(rgba), PixelFormat.Rgba32, n);
                    chunk = rgba[..n];
                }
                var span = output.GetSpan(ChunkOutput);
                output.Advance(state.Write(chunk, span));
            }
        }

        var end = output.GetSpan(1 + QoiCodec.PaddingSize);
        var written = state.Flush(end);
        end.Slice(written, QoiCodec.PaddingSize).Clear();
        end[written + QoiCodec.PaddingSize - 1] = 1;
        output.Advance(written + QoiCodec.PaddingSize);
    }

    /// <summary>The encoder state between chunks of pixels: the previous pixel, the open run, the index.</summary>
    private struct Writer
    {
        private uint _prev;
        private int _run;
        private QoiCodec.Index64 _index;

        public Writer() => _prev = 0xFF000000u;

        /// <summary>Encodes <paramref name="pixels"/>; <paramref name="output"/> holds at least 5 bytes per pixel + 1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public int Write(ReadOnlySpan<uint> pixels, Span<byte> output)
        {
            if (output.Length < pixels.Length * 5 + 1)
                throw new ArgumentException("Output chunk too small.", nameof(output));
            // Every pixel writes at most 5 bytes, after at most one pending run byte, and the check above sizes
            // output for that: o stays below output.Length.
            ref var dst = ref MemoryMarshal.GetReference(output);
            ref var index = ref Unsafe.As<QoiCodec.Index64, uint>(ref _index);
            var o = 0;
            var prev = _prev;
            var run = _run;
            foreach (var px in pixels)
            {
                if (px == prev)
                {
                    if (++run == 62)
                    {
                        Unsafe.Add(ref dst, o++) = QoiCodec.OpRun | 61;
                        run = 0;
                    }
                    continue;
                }
                if (run > 0)
                {
                    Unsafe.Add(ref dst, o++) = (byte)(QoiCodec.OpRun | (run - 1));
                    run = 0;
                }

                var h = QoiCodec.Hash(px);
                ref var slot = ref Unsafe.Add(ref index, h);
                if (slot == px)
                {
                    Unsafe.Add(ref dst, o++) = (byte)h;
                }
                else
                {
                    slot = px;
                    if (((px ^ prev) & 0xFF000000u) == 0)
                    {
                        var dr = (sbyte)(byte)(px - prev);
                        var dg = (sbyte)(byte)((px >> 8) - (prev >> 8));
                        var db = (sbyte)(byte)((px >> 16) - (prev >> 16));
                        if ((uint)(dr + 2) < 4 && (uint)(dg + 2) < 4 && (uint)(db + 2) < 4)
                        {
                            Unsafe.Add(ref dst, o++) = (byte)(QoiCodec.OpDiff | (dr + 2) << 4 | (dg + 2) << 2 | (db + 2));
                        }
                        else
                        {
                            int dgr = dr - dg, dgb = db - dg;
                            if ((uint)(dg + 32) < 64 && (uint)(dgr + 8) < 16 && (uint)(dgb + 8) < 16)
                            {
                                Unsafe.Add(ref dst, o) = (byte)(QoiCodec.OpLuma | (dg + 32));
                                Unsafe.Add(ref dst, o + 1) = (byte)((dgr + 8) << 4 | (dgb + 8));
                                o += 2;
                            }
                            else
                            {
                                // The opcode, then R, G, B: the pixel's low three bytes.
                                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o), QoiCodec.OpRgb | (BitConverter.IsLittleEndian ? px << 8 : BinaryPrimitives.ReverseEndianness(px << 8)));
                                o += 4;
                            }
                        }
                    }
                    else
                    {
                        Unsafe.Add(ref dst, o) = QoiCodec.OpRgba;
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o + 1), BitConverter.IsLittleEndian ? px : BinaryPrimitives.ReverseEndianness(px));
                        o += 5;
                    }
                }
                prev = px;
            }
            _prev = prev;
            _run = run;
            return o;
        }

        /// <summary>Writes the open run, if any.</summary>
        public int Flush(Span<byte> output)
        {
            if (_run == 0) return 0;
            output[0] = (byte)(QoiCodec.OpRun | (_run - 1));
            _run = 0;
            return 1;
        }
    }
}
