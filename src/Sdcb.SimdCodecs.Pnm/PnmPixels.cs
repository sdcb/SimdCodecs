using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Pnm;

/// <summary>
/// Netpbm rasters to samples. Integer samples scale from the file's maximum <c>m</c> to the output range:
/// <c>(v · 255 + m / 2) / m</c> for 8 bits, <c>(v · 65535 + m / 2) / m</c> for 16, a value above <c>m</c>
/// counting as <c>m</c>. An 8-bit layout from a 16-bit file takes the 16-bit value's high byte, as
/// <see cref="ImagePixelFormat"/> defines. A plain raster's samples are decimal numbers (single <c>0</c> /
/// <c>1</c> characters for P1) separated by whitespace and comments; a raw one is binary, big-endian for 16
/// bits, a PBM row padded to a whole byte, a PFM row bottom-up.
/// </summary>
internal static class PnmPixels
{
    private const int Chunk = 1024; // pixels per step: a multiple of 8, so a PBM chunk starts on a byte

    /// <summary>Bytes per raw raster row, or 0 for a plain one.</summary>
    public static long RowBytes(in PnmInfo info) => info.Format switch
    {
        PnmFormat.Bitmap => (info.Width + 7L) / 8,
        PnmFormat.FloatGraymap or PnmFormat.FloatPixmap => 4L * info.Width * info.Channels,
        _ when info.IsPlain => 0,
        _ => (long)info.Width * info.Channels * (info.MaxValue > 255 ? 2 : 1),
    };

    private static ReadOnlySpan<byte> Raster(ReadOnlySpan<byte> data, in PnmInfo info, int offset)
    {
        var size = RowBytes(info) * info.Height;
        if (offset + size > data.Length)
            throw new InvalidDataException("Netpbm raster is truncated.");
        return data.Slice(offset, (int)size);
    }

    /// <summary>Decodes into an 8-bit layout. PFM has none: it is refused by the callers.</summary>
    public static void DecodeTo(ReadOnlySpan<byte> data, in PnmInfo info, int offset, Span<byte> destination, int rowStride, ImagePixelFormat format)
    {
        int width = info.Width, channels = info.Channels, bpp = PixelConverter.BytesPerPixel(format);
        scoped var source = new Source8(data, info, offset);
        Span<byte> samples = stackalloc byte[Chunk * 4];
        for (var y = 0; y < info.Height; y++)
        {
            var row = destination.Slice(y * rowStride, width * bpp);
            for (var x = 0; x < width; x += Chunk)
            {
                var n = Math.Min(Chunk, width - x);
                var chunk = source.Read(y, x, n, samples);
                PixelConverter.ConvertRow(chunk, channels, SampleFormat.UInt8, row[(x * bpp)..], format, n);
            }
        }
    }

    /// <summary>Decodes to the native samples: <see cref="PnmInfo.SampleFormat"/>, tightly packed.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, in PnmInfo info, int offset)
    {
        int width = info.Width, height = info.Height, channels = info.Channels;
        var sampleBytes = RasterImage.BytesPerSample(info.SampleFormat);
        var size = (long)width * channels * sampleBytes * height;
        if (size > Array.MaxLength)
            throw new InvalidDataException($"Netpbm image {width}x{height} does not fit in one array.");
        var rowBytes = (int)(size / height);
        var pixels = GC.AllocateUninitializedArray<byte>((int)size);
        switch (info.SampleFormat)
        {
            case SampleFormat.UInt8:
            {
                scoped var source = new Source8(data, info, offset);
                for (var y = 0; y < height; y++)
                {
                    var row = pixels.AsSpan(y * rowBytes, rowBytes);
                    for (var x = 0; x < width; x += Chunk)
                    {
                        var n = Math.Min(Chunk, width - x);
                        var target = row.Slice(x * channels, n * channels);
                        var chunk = source.Read(y, x, n, target);
                        if (chunk != target) chunk.CopyTo(target);
                    }
                }
                break;
            }
            case SampleFormat.UInt16:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(pixels.AsSpan());
                var max = (uint)info.MaxValue;
                if (info.IsPlain)
                {
                    var p = offset;
                    for (var i = 0; i < samples.Length; i++)
                        samples[i] = Scale16(PlainNumber(data, ref p), max);
                }
                else
                {
                    var raster = MemoryMarshal.Cast<byte, ushort>(Raster(data, info, offset));
                    if (BitConverter.IsLittleEndian) BinaryPrimitives.ReverseEndianness(raster, samples);
                    else raster.CopyTo(samples);
                    if (max != 65535)
                        foreach (ref var s in samples)
                            s = Scale16(s, max);
                }
                break;
            }
            default:
            {
                // PFM: rows bottom-up, little-endian when the scale is negative.
                var raster = Raster(data, info, offset);
                var swap = info.Scale < 0 != BitConverter.IsLittleEndian;
                for (var y = 0; y < height; y++)
                {
                    var from = MemoryMarshal.Cast<byte, uint>(raster.Slice((height - 1 - y) * rowBytes, rowBytes));
                    var to = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan(y * rowBytes, rowBytes));
                    if (swap) BinaryPrimitives.ReverseEndianness(from, to);
                    else from.CopyTo(to);
                }
                break;
            }
        }
        return pixels;
    }

    public static byte Scale8(uint v, uint max) => (byte)((Math.Min(v, max) * 255 + max / 2) / max);

    public static ushort Scale16(uint v, uint max) => (ushort)((Math.Min(v, max) * 65535 + max / 2) / max);

    /// <summary>The next decimal sample of a plain raster, after whitespace and comments.</summary>
    public static uint PlainNumber(ReadOnlySpan<byte> data, ref int p)
    {
        PnmHeader.SkipSpace(data, ref p);
        var start = p;
        uint value = 0;
        while (p < data.Length && data[p] is >= (byte)'0' and <= (byte)'9')
        {
            // Anything above 65535 is above every maximum and clamps alike.
            value = Math.Min(value * 10 + (uint)(data[p] - '0'), 65536);
            p++;
        }
        if (p == start)
            throw new InvalidDataException(p >= data.Length ? "Netpbm raster is truncated." : "Netpbm plain raster has a non-numeric sample.");
        return value;
    }

    /// <summary>
    /// 8-bit samples, chunk by chunk: a raw 8-bit raster at maximum 255 is returned in place, everything else is
    /// converted into the caller's buffer. A plain raster is read in order, so chunks must be asked for in order.
    /// </summary>
    private ref struct Source8
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly ReadOnlySpan<byte> _raster;
        private readonly PnmInfo _info;
        private readonly long _rowBytes;
        private int _plain;
        private readonly int _tier;

        public Source8(ReadOnlySpan<byte> data, in PnmInfo info, int offset)
        {
            _data = data;
            _info = info;
            _rowBytes = RowBytes(info);
            _tier = PnmKernels.BestTier;
            if (info.IsPlain) _plain = offset;
            else _raster = Raster(data, info, offset);
        }

        public ReadOnlySpan<byte> Read(int y, int x, int n, Span<byte> buffer)
        {
            var count = n * _info.Channels;
            var output = buffer[..count];
            var max = (uint)_info.MaxValue;
            switch (_info.Format)
            {
                case PnmFormat.Bitmap:
                    PnmKernels.ExpandBits(_raster.Slice((int)(y * _rowBytes) + (x >> 3)), output, n, _tier);
                    return output;
                case PnmFormat.PlainBitmap:
                    for (var i = 0; i < count; i++)
                    {
                        PnmHeader.SkipSpace(_data, ref _plain);
                        if (_plain >= _data.Length) throw new InvalidDataException("Netpbm raster is truncated.");
                        var c = _data[_plain++];
                        output[i] = c switch
                        {
                            (byte)'0' => 255,
                            (byte)'1' => 0,
                            _ => throw new InvalidDataException("PBM plain raster has a character other than 0 or 1."),
                        };
                    }
                    return output;
                case PnmFormat.PlainGraymap or PnmFormat.PlainPixmap:
                    if (max > 255)
                        for (var i = 0; i < count; i++)
                            output[i] = (byte)(Scale16(PlainNumber(_data, ref _plain), max) >> 8);
                    else if (max == 255)
                        for (var i = 0; i < count; i++)
                            output[i] = (byte)Math.Min(PlainNumber(_data, ref _plain), 255);
                    else
                        for (var i = 0; i < count; i++)
                            output[i] = Scale8(PlainNumber(_data, ref _plain), max);
                    return output;
            }

            if (max > 255)
            {
                var raw = _raster.Slice((int)(y * _rowBytes) + 2 * x * _info.Channels, 2 * count);
                if (max == 65535)
                {
                    PnmKernels.HighBytes(raw, output, count, _tier);
                }
                else
                {
                    for (var i = 0; i < count; i++)
                        output[i] = (byte)(Scale16(BinaryPrimitives.ReadUInt16BigEndian(raw[(2 * i)..]), max) >> 8);
                }
                return output;
            }

            var bytes = _raster.Slice((int)(y * _rowBytes) + x * _info.Channels, count);
            if (max == 255) return bytes;
            Span<byte> table = stackalloc byte[256];
            for (var v = 0; v < 256; v++) table[v] = Scale8((uint)v, max);
            for (var i = 0; i < count; i++) output[i] = table[bytes[i]];
            return output;
        }
    }
}
