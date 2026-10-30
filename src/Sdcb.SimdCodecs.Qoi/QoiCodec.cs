using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Qoi;

/// <summary>
/// The QOI stream, from the specification at qoiformat.org: a 14-byte header (<c>qoif</c>, big-endian width and
/// height, channels, colour space), then one chunk per pixel or run, then seven 0x00 bytes and a 0x01.
/// <para>
/// A pixel is held as a <see cref="uint"/> whose memory bytes are R, G, B, A — <c>R | G &lt;&lt; 8 | B &lt;&lt; 16 |
/// A &lt;&lt; 24</c> on the little-endian hosts .NET runs on — so a row of them is an RGBA row.
/// </para>
/// </summary>
internal static class QoiCodec
{
    public const int HeaderSize = 14;
    public const int PaddingSize = 8;

    /// <summary>The largest image read or written: 2^28 pixels, 1 GiB as RGBA.</summary>
    public const long MaximumPixels = 1L << 28;

    public const int OpIndex = 0x00, OpDiff = 0x40, OpLuma = 0x80, OpRun = 0xC0, OpRgb = 0xFE, OpRgba = 0xFF;

    /// <summary>A pixel's slot in the 64-entry index: <c>(r·3 + g·5 + b·7 + a·11) mod 64</c>.</summary>
    /// <remarks>
    /// The four bytes are spread into 16-bit lanes (R at bit 0, B at 16, G at 32, A at 48) and multiplied by a
    /// constant whose lanes hold the weights in reverse, so lane 48 of the product collects
    /// <c>r·3 + b·7 + g·5 + a·11</c>. No lane sum exceeds 255·26 &lt; 2^16, so no carry crosses a lane.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Hash(uint px)
    {
        ulong v = px;
        var lanes = (v & 0x00FF00FF) | ((v & 0xFF00FF00) << 24);
        return (int)((lanes * 0x0003_0007_0005_000BUL) >> 48) & 63;
    }

    public static QoiInfo ReadHeader(ReadOnlySpan<byte> data)
    {
        if (!HasSignature(data) || data.Length < HeaderSize)
            throw new InvalidDataException("Not a QOI image.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        int channels = data[12], colorSpace = data[13];
        if (width == 0 || height == 0)
            throw new InvalidDataException("QOI image has no pixels.");
        if ((ulong)width * height > MaximumPixels)
            throw new InvalidDataException($"QOI size {width}x{height} is above the limit of {MaximumPixels:N0} pixels.");
        if (channels is not (3 or 4))
            throw new InvalidDataException($"QOI channel count {channels} is not 3 or 4.");
        if (colorSpace > 1)
            throw new InvalidDataException($"QOI colour space {colorSpace} is not 0 or 1.");
        return new QoiInfo((int)width, (int)height, channels, (QoiColorSpace)colorSpace);
    }

    public static bool HasSignature(ReadOnlySpan<byte> data) => data.StartsWith("qoif"u8);

    [InlineArray(64)]
    internal struct Index64
    {
        private uint _element;
    }

    /// <summary>
    /// The decoder state between calls: a stream decodes into any number of consecutive pixel spans (rows,
    /// or chunks of a row), a run carrying over from one to the next.
    /// </summary>
    internal struct Reader
    {
        private int _position;
        private uint _px;
        private int _run;
        private readonly uint _alpha;
        private Index64 _index;

        /// <param name="opaque">A 3-channel image: every pixel's alpha is 255 whatever the chunks say.</param>
        public Reader(bool opaque)
        {
            _position = HeaderSize;
            _alpha = opaque ? 0xFF000000u : 0;
            _px = 0xFF000000u;
        }

        /// <summary>Decodes the next <c>pixels.Length</c> pixels.</summary>
        /// <exception cref="InvalidDataException">The chunks end first.</exception>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Read(ReadOnlySpan<byte> data, Span<uint> pixels)
        {
            var n = pixels.Length;
            var i = 0;
            var run = _run;
            var px = _px;
            if (run > 0)
            {
                i = Math.Min(run, n);
                pixels[..i].Fill(px);
                run -= i;
            }
            if (i == n)
            {
                _run = run;
                return;
            }

            // Every chunk is at most 5 bytes. While p <= limit = length − 5 a chunk read from p stays inside data.
            // Within the last 5 bytes the rest is copied into a zeroed 16-byte buffer and p restarts at 0 with
            // limit = remaining − 1, so a chunk starting at p <= 3 reads at most up to byte 8 of it; one that ran
            // past the real bytes leaves p > remaining, caught below.
            Span<byte> tail = stackalloc byte[16];
            scoped ref var src = ref MemoryMarshal.GetReference(data);
            var p = _position;
            var limit = data.Length - 5;
            var tailBase = -1;
            var alpha = _alpha;
            ref var dst = ref MemoryMarshal.GetReference(pixels);
            ref var index = ref Unsafe.As<Index64, uint>(ref _index);

            while (i < n)
            {
                if (p > limit)
                {
                    var remaining = tailBase < 0 ? data.Length - p : 0;
                    if (remaining <= 0)
                        throw new InvalidDataException("QOI data ends before the last pixel.");
                    tail.Clear();
                    data[p..].CopyTo(tail);
                    tailBase = p;
                    p = 0;
                    limit = remaining - 1;
                    src = ref MemoryMarshal.GetReference(tail);
                }

                uint b1 = Unsafe.Add(ref src, p);
                if (b1 < OpLuma)
                {
                    if (b1 < OpDiff)
                    {
                        // Index slots fill only with pixels already decoded, all opaque in a 3-channel image but
                        // for the zeroed initial slots a malformed stream may name.
                        px = Unsafe.Add(ref index, (int)b1) | alpha;
                        p++;
                    }
                    else
                    {
                        // r, g, b each change by −2..1, stored with a bias of 2.
                        var delta = ((b1 >> 4) & 3) | (((b1 >> 2) & 3) << 8) | ((b1 & 3) << 16);
                        px = AddBytes(px, delta, 0x00020202);
                        p++;
                    }
                }
                else if (b1 < OpRun)
                {
                    // Green changes by −32..31; red and blue by green's change plus −8..7.
                    uint b2 = Unsafe.Add(ref src, p + 1);
                    var dg = (b1 & 63) - 32;
                    var dr = dg - 8 + (b2 >> 4);
                    var db = dg - 8 + (b2 & 15);
                    px = AddBytes(px, (dr & 0xFF) | ((dg & 0xFF) << 8) | ((db & 0xFF) << 16), 0);
                    p += 2;
                }
                else if (b1 < OpRgb)
                {
                    // The previous pixel 1..62 times; this chunk does not change it.
                    run = (int)(b1 & 63) + 1;
                    p++;
                    var k = Math.Min(run, n - i);
                    pixels.Slice(i, k).Fill(px);
                    i += k;
                    run -= k;
                    continue;
                }
                else if (b1 == OpRgb)
                {
                    px = (px & 0xFF000000u) | (ReadUInt32(ref Unsafe.Add(ref src, p + 1)) & 0x00FFFFFFu);
                    p += 4;
                }
                else
                {
                    px = ReadUInt32(ref Unsafe.Add(ref src, p + 1)) | alpha;
                    p += 5;
                }

                Unsafe.Add(ref index, Hash(px)) = px;
                // i < n = pixels.Length.
                Unsafe.Add(ref dst, i++) = px;
            }

            if (tailBase >= 0)
            {
                if (p > limit + 1)
                    throw new InvalidDataException("QOI data ends inside the last chunk.");
                p += tailBase;
            }
            _position = p;
            _run = run;
            _px = px;
        }

        /// <summary>
        /// Byte-wise <paramref name="px"/> + <paramref name="delta"/> − <paramref name="bias"/>, each byte modulo 256.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint AddBytes(uint px, uint delta, uint bias)
        {
            // Subtracting the bias first keeps every byte of delta within 0..255 as an 8-bit two's complement
            // value; then the low 7 bits of each byte add without crossing into the next, and the top bits xor in.
            delta = SubBytes(delta, bias);
            var low = (px & 0x7F7F7F7Fu) + (delta & 0x7F7F7F7Fu);
            return low ^ ((px ^ delta) & 0x80808080u);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint SubBytes(uint a, uint b)
        {
            if (b == 0) return a;
            var low = (a | 0x80808080u) - (b & 0x7F7F7F7Fu);
            return low ^ ((a ^ ~b) & 0x80808080u);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadUInt32(ref byte source)
    {
        var v = Unsafe.ReadUnaligned<uint>(ref source);
        return BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
    }
}
