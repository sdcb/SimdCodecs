using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Pnm;

/// <summary>How <see cref="PnmEncoder"/> writes an image.</summary>
public sealed record PnmEncodeOptions
{
    /// <summary>The magic number. <see cref="PnmFormat.Auto"/> picks P5 for 1 channel, P6 for 3, P7 for 2 or 4.
    /// A PBM or PGM format takes the input's luma (a PBM: black below 128), a PPM drops alpha. The float formats
    /// are written by <see cref="PnmEncoder.EncodePfm(ReadOnlySpan{float}, int, int, int)"/> only.</summary>
    public PnmFormat Format { get; init; }

    /// <summary>The maximum sample value, 1 to 65535; above 255 samples take 2 bytes. 0 keeps the input's range
    /// (255 for 8-bit input, 65535 for 16-bit). Ignored for PBM.</summary>
    public int MaxValue { get; init; }

    /// <summary>A PAM tuple type; null names it from the channel count (GRAYSCALE, GRAYSCALE_ALPHA, RGB,
    /// RGB_ALPHA).</summary>
    public string? TupleType { get; init; }
}

/// <summary>
/// Pure-managed Netpbm encoder, written from the Netpbm format pages: every PBM, PGM and PPM format, plain and
/// raw, PAM, and PFM. Plain rasters have at most 70 characters per line and start each image row on a new line.
/// </summary>
public static class PnmEncoder
{
    private const int Chunk = 1024; // pixels per step: a multiple of 8, so a PBM chunk ends on a byte

    /// <summary>Encodes an 8-bit image.</summary>
    /// <param name="pixels">The image, top row first.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">The input layout.</param>
    /// <param name="options">The output format; null for <see cref="PnmFormat.Auto"/>.</param>
    /// <param name="stride">Bytes from one input row to the next; 0 means tightly packed.</param>
    /// <exception cref="ArgumentException">The size, buffer or options do not fit.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format,
        PnmEncodeOptions? options = null, int stride = 0)
    {
        var input = Input.Of(pixels, width, height, format, stride);
        return ToArray(input, Plan(input, options));
    }

    /// <summary>Encodes an 8-bit image into <paramref name="output"/> through one pooled buffer.</summary>
    /// <param name="pixels">The image, top row first.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">The input layout.</param>
    /// <param name="output">Where the file is written, from its current position.</param>
    /// <param name="options">The output format; null for <see cref="PnmFormat.Auto"/>.</param>
    /// <param name="stride">Bytes from one input row to the next; 0 means tightly packed.</param>
    /// <exception cref="ArgumentException">The size, buffer or options do not fit.</exception>
    public static void Encode(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format, Stream output,
        PnmEncodeOptions? options = null, int stride = 0)
    {
        ArgumentNullException.ThrowIfNull(output);
        var input = Input.Of(pixels, width, height, format, stride);
        ToStream(input, Plan(input, options), output);
    }

    /// <summary>Encodes tightly packed 8-bit samples: 1 (grey), 2 (grey and alpha), 3 (RGB) or 4 (RGBA) channels.</summary>
    /// <exception cref="ArgumentException">The size, channel count, buffer or options do not fit.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> samples, int width, int height, int channels, PnmEncodeOptions? options = null)
    {
        var input = Input.Of(samples, width, height, channels, sixteen: false);
        return ToArray(input, Plan(input, options));
    }

    /// <summary>
    /// Encodes tightly packed 16-bit samples in host byte order: 1 (grey), 2 (grey and alpha), 3 (RGB) or 4 (RGBA)
    /// channels. The format must keep the channel count: P2 / P5 for 1, P3 / P6 for 3, P7 for any.
    /// </summary>
    /// <exception cref="ArgumentException">The size, channel count, buffer or options do not fit.</exception>
    public static byte[] Encode(ReadOnlySpan<ushort> samples, int width, int height, int channels, PnmEncodeOptions? options = null)
    {
        var input = Input.Of(MemoryMarshal.AsBytes(samples), width, height, channels, sixteen: true);
        return ToArray(input, Plan(input, options));
    }

    /// <summary><see cref="Encode(ReadOnlySpan{ushort}, int, int, int, PnmEncodeOptions?)"/> into a stream.</summary>
    /// <exception cref="ArgumentException">The size, channel count, buffer or options do not fit.</exception>
    public static void Encode(ReadOnlySpan<ushort> samples, int width, int height, int channels, Stream output, PnmEncodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        var input = Input.Of(MemoryMarshal.AsBytes(samples), width, height, channels, sixteen: true);
        ToStream(input, Plan(input, options), output);
    }

    /// <summary>
    /// Encodes a PFM: 1 (<c>Pf</c>) or 3 (<c>PF</c>) channels of float samples, tightly packed, top row first. The
    /// file is little-endian (scale −1) with rows bottom-up, as PFM stores them.
    /// </summary>
    /// <exception cref="ArgumentException">The size, channel count or buffer does not fit.</exception>
    public static byte[] EncodePfm(ReadOnlySpan<float> samples, int width, int height, int channels)
    {
        var header = PfmHeader(samples, width, height, channels);
        var rowBytes = width * channels * 4;
        var file = GC.AllocateUninitializedArray<byte>(header.Length + rowBytes * height);
        header.CopyTo(file, 0);
        WritePfmRows(samples, width, height, channels, file.AsSpan(header.Length));
        return file;
    }

    /// <summary><see cref="EncodePfm(ReadOnlySpan{float}, int, int, int)"/> into a stream.</summary>
    /// <exception cref="ArgumentException">The size, channel count or buffer does not fit.</exception>
    public static void EncodePfm(ReadOnlySpan<float> samples, int width, int height, int channels, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var header = PfmHeader(samples, width, height, channels);
        output.Write(header);
        var rowBytes = width * channels * 4;
        var buffer = ArrayPool<byte>.Shared.Rent(rowBytes);
        try
        {
            for (var y = height - 1; y >= 0; y--)
            {
                WritePfmRows(samples.Slice(y * width * channels, width * channels), width, 1, channels, buffer);
                output.Write(buffer, 0, rowBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte[] PfmHeader(ReadOnlySpan<float> samples, int width, int height, int channels)
    {
        CheckSize(width, height);
        if (channels is not (1 or 3))
            throw new ArgumentException("PFM has 1 or 3 channels.", nameof(channels));
        if ((long)width * height * channels > samples.Length)
            throw new ArgumentException($"The samples do not hold a {width}x{height} image of {channels} channels.", nameof(samples));
        if ((long)width * height * channels * 4 > Array.MaxLength - 64)
            throw new ArgumentException($"A {width}x{height} PFM does not fit in one array.");
        return Encoding.ASCII.GetBytes($"{(channels == 3 ? "PF" : "Pf")}\n{width} {height}\n-1.0\n");
    }

    // Rows bottom-up, little-endian.
    private static void WritePfmRows(ReadOnlySpan<float> samples, int width, int height, int channels, Span<byte> output)
    {
        var row = width * channels;
        for (var y = 0; y < height; y++)
        {
            var from = MemoryMarshal.Cast<float, uint>(samples.Slice((height - 1 - y) * row, row));
            var to = MemoryMarshal.Cast<byte, uint>(output.Slice(y * row * 4, row * 4));
            if (BitConverter.IsLittleEndian) from.CopyTo(to);
            else BinaryPrimitives.ReverseEndianness(from, to);
        }
    }

    private static void CheckSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > PnmHeader.MaximumPixels)
            throw new ArgumentException($"Netpbm size {width}x{height} must be positive and at most {PnmHeader.MaximumPixels:N0} pixels.");
    }

    /// <summary>The input: rows of 8-bit pixels in a layout, or of 8- or 16-bit samples by channel count.</summary>
    private readonly ref struct Input
    {
        public readonly ReadOnlySpan<byte> Pixels;
        public readonly int Width, Height, Stride, Channels;
        public readonly bool Sixteen, Bgr;

        private Input(ReadOnlySpan<byte> pixels, int width, int height, int stride, int channels, bool sixteen, bool bgr)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
            Stride = stride;
            Channels = channels;
            Sixteen = sixteen;
            Bgr = bgr;
        }

        public static Input Of(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format, int stride)
        {
            CheckSize(width, height);
            if (!PixelConverter.TryGetLayout(width, height, format, stride, pixels.Length, out var rowStride))
                throw new ArgumentException($"The pixels do not hold a {width}x{height} {format} image with stride {stride}.", nameof(pixels));
            var channels = PixelConverter.BytesPerPixel(format);
            return new Input(pixels, width, height, rowStride, channels, false, format is ImagePixelFormat.Bgra32 or ImagePixelFormat.Bgr24);
        }

        public static Input Of(ReadOnlySpan<byte> samples, int width, int height, int channels, bool sixteen)
        {
            CheckSize(width, height);
            if (channels is < 1 or > 4)
                throw new ArgumentException("Channels must be 1 to 4.", nameof(channels));
            var rowStride = (long)width * channels * (sixteen ? 2 : 1);
            if (rowStride * height > samples.Length)
                throw new ArgumentException($"The samples do not hold a {width}x{height} image of {channels} channels.", nameof(samples));
            return new Input(samples, width, height, (int)rowStride, channels, sixteen, false);
        }

        public ReadOnlySpan<byte> Row(int y) => Pixels.Slice(y * Stride, Width * Channels * (Sixteen ? 2 : 1));
    }

    /// <summary>What to write: the format, its channels and maximum, and the header.</summary>
    private readonly record struct Target(PnmFormat Format, int Channels, int MaxValue, byte[] Header)
    {
        public bool Plain => Format is PnmFormat.PlainBitmap or PnmFormat.PlainGraymap or PnmFormat.PlainPixmap;
        public bool Bitmap => Format is PnmFormat.PlainBitmap or PnmFormat.Bitmap;

        /// <summary>The exact size of a raw file, or an estimate for a plain one.</summary>
        public long Size(int width, int height) => Header.Length + height * Format switch
        {
            PnmFormat.Bitmap => (width + 7L) / 8,
            PnmFormat.PlainBitmap => 2L * width + 1,
            _ when Plain => (long)width * Channels * (MaxValue > 255 ? 6 : 4) + 1,
            _ => (long)width * Channels * (MaxValue > 255 ? 2 : 1),
        };
    }

    private static Target Plan(in Input input, PnmEncodeOptions? options)
    {
        options ??= new PnmEncodeOptions();
        var format = options.Format switch
        {
            PnmFormat.Auto => input.Channels switch { 1 => PnmFormat.Graymap, 3 => PnmFormat.Pixmap, _ => PnmFormat.Arbitrary },
            >= PnmFormat.PlainBitmap and <= PnmFormat.Arbitrary => options.Format,
            _ => throw new ArgumentException($"{options.Format} is not a PBM, PGM, PPM or PAM format; PFM is written by EncodePfm.", nameof(options)),
        };
        var channels = format switch
        {
            PnmFormat.PlainBitmap or PnmFormat.Bitmap or PnmFormat.PlainGraymap or PnmFormat.Graymap => 1,
            PnmFormat.PlainPixmap or PnmFormat.Pixmap => 3,
            _ => input.Channels,
        };
        if (input.Sixteen && (channels != input.Channels || format is PnmFormat.PlainBitmap or PnmFormat.Bitmap))
            throw new ArgumentException($"16-bit input of {input.Channels} channels cannot be written as {format}.", nameof(options));
        var maxValue = format is PnmFormat.PlainBitmap or PnmFormat.Bitmap ? 1
            : options.MaxValue == 0 ? (input.Sixteen ? 65535 : 255)
            : options.MaxValue;
        if (maxValue is < 1 or > 65535)
            throw new ArgumentException($"Maximum value {maxValue} is not 1..65535.", nameof(options));

        var magic = (int)format;
        string header;
        if (format == PnmFormat.Arbitrary)
        {
            var tupleType = options.TupleType ?? channels switch { 1 => "GRAYSCALE", 2 => "GRAYSCALE_ALPHA", 3 => "RGB", _ => "RGB_ALPHA" };
            if (tupleType.Length == 0 || tupleType.Any(c => c is < ' ' or > '~'))
                throw new ArgumentException("The tuple type must be printable ASCII on one line.", nameof(options));
            header = $"P7\nWIDTH {input.Width}\nHEIGHT {input.Height}\nDEPTH {channels}\nMAXVAL {maxValue}\nTUPLTYPE {tupleType}\nENDHDR\n";
        }
        else
        {
            header = maxValue == 1 && format is PnmFormat.PlainBitmap or PnmFormat.Bitmap
                ? $"P{magic}\n{input.Width} {input.Height}\n"
                : $"P{magic}\n{input.Width} {input.Height}\n{maxValue}\n";
        }
        return new Target(format, channels, maxValue, Encoding.ASCII.GetBytes(header));
    }

    private static byte[] ToArray(in Input input, Target target)
    {
        var size = target.Size(input.Width, input.Height);
        if (!target.Plain && size > Array.MaxLength)
            throw new ArgumentException($"A {input.Width}x{input.Height} {target.Format} does not fit in one array.");
        var writer = new ArrayBufferWriter<byte>((int)Math.Min(size, Array.MaxLength));
        Write(input, target, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static void ToStream(in Input input, Target target, Stream output)
    {
        using var writer = new StreamBufferWriter(output);
        Write(input, target, writer);
        writer.Flush();
    }

    private static void Write(in Input input, Target target, IBufferWriter<byte> output)
    {
        output.Write(target.Header);
        int width = input.Width, channels = target.Channels, max = target.MaxValue;
        var tier = PnmKernels.BestTier;
        Span<byte> rgb = stackalloc byte[Chunk * 4];
        Span<byte> converted = stackalloc byte[Chunk * 4];
        Span<char> digits = stackalloc char[8];
        for (var y = 0; y < input.Height; y++)
        {
            var row = input.Row(y);
            var column = 0;
            for (var x = 0; x < width; x += Chunk)
            {
                var n = Math.Min(Chunk, width - x);
                var count = n * channels;

                // The chunk's samples in the target's channels: 8-bit bytes, or 16-bit host-order values.
                scoped ReadOnlySpan<byte> samples;
                if (input.Sixteen)
                {
                    samples = row.Slice(x * channels * 2, count * 2);
                }
                else
                {
                    samples = row.Slice(x * input.Channels, n * input.Channels);
                    if (input.Bgr)
                    {
                        // B, G, R(, A) to R, G, B(, A): the swap that makes BGRA from RGBA works both ways.
                        PixelConverter.ConvertRow(samples, input.Channels, SampleFormat.UInt8, rgb,
                            input.Channels == 4 ? ImagePixelFormat.Bgra32 : ImagePixelFormat.Bgr24, n);
                        samples = rgb[..(n * input.Channels)];
                    }
                    if (channels != input.Channels)
                    {
                        PixelConverter.ConvertRow(samples, input.Channels, SampleFormat.UInt8, converted,
                            channels == 1 ? ImagePixelFormat.Gray8 : ImagePixelFormat.Rgb24, n);
                        samples = converted[..count];
                    }
                }

                switch (target.Format)
                {
                    case PnmFormat.Bitmap:
                    {
                        var bytes = (n + 7) / 8;
                        PnmKernels.PackBits(samples, output.GetSpan(bytes), n, tier);
                        output.Advance(bytes);
                        break;
                    }
                    case PnmFormat.PlainBitmap:
                        for (var i = 0; i < n; i++)
                            column = Plain(output, samples[i] < 128 ? 1u : 0u, column, digits);
                        break;
                    case PnmFormat.PlainGraymap or PnmFormat.PlainPixmap:
                        for (var i = 0; i < count; i++)
                            column = Plain(output, Sample(samples, i, input.Sixteen, max), column, digits);
                        break;
                    default:
                        WriteRaw(samples, count, input.Sixteen, max, output);
                        break;
                }
            }
            if (target.Plain)
            {
                output.GetSpan(1)[0] = (byte)'\n';
                output.Advance(1);
            }
        }
    }

    /// <summary>Sample <paramref name="i"/> scaled to <paramref name="max"/>, rounding to nearest.</summary>
    private static uint Sample(ReadOnlySpan<byte> samples, int i, bool sixteen, int max)
    {
        if (!sixteen)
        {
            uint v = samples[i];
            return max == 255 ? v : (v * (uint)max + 127) / 255;
        }
        uint w = MemoryMarshal.Cast<byte, ushort>(samples)[i];
        return max == 65535 ? w : (uint)(((ulong)w * (uint)max + 32767) / 65535);
    }

    private static void WriteRaw(ReadOnlySpan<byte> samples, int count, bool sixteen, int max, IBufferWriter<byte> output)
    {
        if (max <= 255)
        {
            var span = output.GetSpan(count);
            if (!sixteen && max == 255)
                samples[..count].CopyTo(span);
            else
                for (var i = 0; i < count; i++)
                    span[i] = (byte)Sample(samples, i, sixteen, max);
            output.Advance(count);
            return;
        }

        var wide = MemoryMarshal.Cast<byte, ushort>(output.GetSpan(2 * count)[..(2 * count)]);
        if (sixteen && max == 65535)
        {
            var from = MemoryMarshal.Cast<byte, ushort>(samples)[..count];
            if (BitConverter.IsLittleEndian) BinaryPrimitives.ReverseEndianness(from, wide);
            else from.CopyTo(wide);
        }
        else
        {
            for (var i = 0; i < count; i++)
            {
                var v = (ushort)Sample(samples, i, sixteen, max);
                wide[i] = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(v) : v;
            }
        }
        output.Advance(2 * count);
    }

    /// <summary>Writes a decimal sample, breaking lines before 70 characters.</summary>
    private static int Plain(IBufferWriter<byte> output, uint value, int column, Span<char> digits)
    {
        value.TryFormat(digits, out var length);
        var span = output.GetSpan(length + 1);
        var o = 0;
        if (column > 0)
        {
            if (column + 1 + length > 70)
            {
                span[o++] = (byte)'\n';
                column = 0;
            }
            else
            {
                span[o++] = (byte)' ';
                column++;
            }
        }
        for (var i = 0; i < length; i++)
            span[o++] = (byte)digits[i];
        output.Advance(o);
        return column + length;
    }
}
