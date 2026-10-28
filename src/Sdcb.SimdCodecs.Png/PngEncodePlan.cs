using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// How the caller's pixels become the file's rows: the IHDR colour type and depth, the PLTE and tRNS
/// to write, and the per-row conversion <see cref="Fill"/> that <see cref="PngWriter"/>'s row loop
/// calls on each source row before filtering it.
/// <para>A <b>direct</b> plan writes the caller's layout as it is (optionally without its alpha), the
/// writer's behaviour before <see cref="PngWriteOptions.LosslessReduction"/> existed, byte for byte. A
/// <b>reduced</b> plan comes from one scan of the image (<see cref="Analyze"/>) and picks the smallest
/// layout that still holds every sample exactly:</para>
/// <list type="bullet">
///   <item>alpha that is everywhere opaque is dropped; alpha that is only ever 0 or opaque, with every
///   transparent pixel one colour that no opaque pixel has, becomes a tRNS transparent colour;</item>
///   <item>RGB whose channels are equal everywhere becomes greyscale;</item>
///   <item>16-bit samples whose two bytes are equal everywhere (an 8-bit value v stored as v·257, which
///   is how PNG itself widens 8 bits to 16) become 8-bit;</item>
///   <item>8-bit greyscale whose levels all lie on a coarser depth's scale (multiples of 17, 85 or
///   255) becomes 4-, 2- or 1-bit;</item>
///   <item>at most 256 distinct colours become indexed colour, at the smallest index depth that holds
///   them, with tRNS alpha for the translucent entries (which are put first, so tRNS stays short).</item>
/// </list>
/// <para>Among the layouts that apply, the one with the fewest bits per pixel wins, greyscale before
/// indexed before truecolour on a tie. Indexed and sub-byte rows are written with filter None, as the
/// PNG specification recommends for both: their samples are indices or packed levels, which the
/// arithmetic filters do not predict.</para>
/// </summary>
internal sealed class PngEncodePlan
{
    // Source: Channels logical samples per pixel (1 grey, 2 grey + alpha, 3 RGB, 4 RGBA) among Stride
    // stored ones (4 for RGBA input written without its alpha), each SampleBytes (1, or 2 host-order).
    private readonly int _channels;
    private readonly int _stride;
    private readonly int _sampleBytes;
    private readonly IOps? _ops; // null for a direct plan
    private readonly PaletteMap? _map;

    private PngEncodePlan(int channels, int stride, int sampleBytes, byte colorType, byte bitDepth, IOps? ops, PaletteMap? map)
    {
        _channels = channels;
        _stride = stride;
        _sampleBytes = sampleBytes;
        ColorType = colorType;
        BitDepth = bitDepth;
        _ops = ops;
        _map = map;
    }

    /// <summary>IHDR colour type: 0, 2, 3, 4 or 6.</summary>
    public byte ColorType { get; }

    /// <summary>IHDR bit depth: 1, 2, 4, 8 or 16.</summary>
    public byte BitDepth { get; }

    /// <summary>PLTE (RGB triples) for colour type 3, else null.</summary>
    public byte[]? Palette { get; private init; }

    /// <summary>tRNS payload as written (palette alpha, or the transparent colour's two-byte samples), else null.</summary>
    public byte[]? Transparency { get; private init; }

    /// <summary>False for indexed and sub-byte rows, which are written with filter None.</summary>
    public bool Filtered => ColorType != 3 && BitDepth >= 8;

    private int OutChannels => PngReader.SamplesPerPixel(ColorType);

    /// <summary>Bytes of one file row, without its filter byte.</summary>
    public int RowBytes(int width) => checked((int)(((long)width * OutChannels * BitDepth + 7) / 8));

    /// <summary>The filters' left-neighbour distance: bytes per pixel, at least one.</summary>
    public int FilterBpp => Math.Max(1, OutChannels * BitDepth / 8);

    /// <summary>Bytes of one source row.</summary>
    public int SourceRowBytes(int width) => checked(width * _stride * _sampleBytes);

    /// <summary>
    /// The plan for writing the source as it is: colour type from its channels (alpha dropped when
    /// <paramref name="channels"/> is below <paramref name="stride"/>), depth from its sample size, and
    /// tRNS from <see cref="PngWriteOptions.TransparentColor"/> when given.
    /// </summary>
    public static PngEncodePlan Direct(int channels, int stride, int sampleBytes, ushort[]? transparentColor)
    {
        var colorType = channels switch { 1 => (byte)0, 2 => (byte)4, 3 => (byte)2, _ => (byte)6 };
        var bitDepth = (byte)(8 * sampleBytes);
        byte[]? trns = null;
        if (transparentColor is not null)
        {
            if (colorType is not (0 or 2))
                throw new ArgumentException(
                    "A transparent colour (tRNS) is only for greyscale and RGB output; this image has an alpha channel",
                    nameof(PngWriteOptions.TransparentColor));
            if (transparentColor.Length != channels)
                throw new ArgumentException(
                    $"The transparent colour needs {channels} sample(s) for this layout, got {transparentColor.Length}",
                    nameof(PngWriteOptions.TransparentColor));
            if (bitDepth == 8 && transparentColor.Any(v => v > 255))
                throw new ArgumentException("An 8-bit image's transparent colour samples are 0-255", nameof(PngWriteOptions.TransparentColor));
            trns = KeyBytes(transparentColor);
        }
        return new PngEncodePlan(channels, stride, sampleBytes, colorType, bitDepth, ops: null, map: null) { Transparency = trns };
    }

    /// <summary>One file row from one source row (<see cref="SourceRowBytes"/> → <see cref="RowBytes"/>).</summary>
    public void Fill(ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        if (_ops is null)
        {
            PngWriter.FillRow(src, dst, _stride, _channels, _sampleBytes);
            return;
        }

        switch (ColorType)
        {
            case 3: _ops.FillIndexed(src, dst, width, _map!, BitDepth); break;
            case 0 when BitDepth < 8: _ops.FillPackedGrey(src, dst, width, BitDepth); break;
            default: _ops.FillSamples(src, dst, width, ColorType, BitDepth); break;
        }
    }

    // ---------------- analysis ---------------------------------------------------------------------

    /// <summary>
    /// The smallest exact layout for the image (see the class remarks).
    /// <paramref name="keepColourModel"/> (an ICC profile is attached, and a profile describes either
    /// greyscale or colour) keeps greyscale greyscale and colour colour.
    /// </summary>
    public static PngEncodePlan Analyze(ReadOnlySpan<byte> samples, int width, int height,
        int channels, int stride, int sampleBytes, bool keepColourModel)
    {
        var ops = OpsFor(channels, stride, sampleBytes);
        var pixels = width * height;
        var sourceIsGrey = channels <= 2;
        var s = ops.Scan(samples, pixels, trackPalette: !(keepColourModel && sourceIsGrey));
        var wide = sampleBytes == 2 && !s.Fits8;
        var hasAlpha = channels is 2 or 4;
        var needAlpha = hasAlpha && !s.Opaque;
        // A key is useless if an opaque pixel has its colour: the palette, when collected, knows that;
        // otherwise a second pass looks.
        var shift = sampleBytes == 2 ? 8 : 0;
        var keyable = needAlpha && s.BinaryAlpha && s.KeyCandidate
            && !(s.Map is { } seen
                ? seen.IndexOf((uint)((s.KeyR >> shift) | ((s.KeyG >> shift) << 8) | ((s.KeyB >> shift) << 16) | (0xFFu << 24))) >= 0
                : ops.OpaqueHasColour(samples, pixels, s.KeyR, s.KeyG, s.KeyB));

        // Candidates in tie-break order: greyscale, indexed, truecolour.
        (byte ColorType, byte Depth, bool Key) best = default;
        var bestBits = int.MaxValue;
        void Consider(byte colorType, byte depth, bool key)
        {
            var bits = PngReader.SamplesPerPixel(colorType) * depth;
            if (bits < bestBits)
            {
                bestBits = bits;
                best = (colorType, depth, key);
            }
        }

        var depth = wide ? (byte)16 : (byte)8;
        if (s.Grey && !(keepColourModel && !sourceIsGrey))
        {
            var greyDepth = wide ? (byte)16 : (byte)s.GreyDepth;
            if (!needAlpha) Consider(0, greyDepth, false);
            else if (keyable) Consider(0, greyDepth, true);
            else Consider(4, depth, false);
        }
        if (s.Map is not null && !wide && !(keepColourModel && sourceIsGrey))
            Consider(3, s.Map.Count switch { <= 2 => 1, <= 4 => 2, <= 16 => 4, _ => 8 }, false);
        if (!sourceIsGrey)
        {
            if (!needAlpha) Consider(2, depth, false);
            else if (keyable) Consider(2, depth, true);
            else Consider(6, depth, false);
        }

        var (colorType, bitDepth, useKey) = best;
        // The source's own layout (or it minus an opaque alpha) needs no conversion: write it directly.
        var sameDepth = bitDepth == 8 * sampleBytes;
        if (sameDepth && !useKey && colorType == (channels switch { 1 => 0, 2 => 4, 3 => 2, _ => 6 }))
            return Direct(channels, stride, sampleBytes, null);
        if (sameDepth && !useKey && channels == 4 && colorType == 2)
            return Direct(3, stride, sampleBytes, null);
        if (sameDepth && !useKey && channels == 2 && colorType == 0)
            return Direct(1, stride, sampleBytes, null);

        if (colorType == 3)
        {
            var (palette, alpha) = s.Map!.OrderTranslucentFirst();
            return new PngEncodePlan(channels, stride, sampleBytes, 3, bitDepth, ops, s.Map)
            {
                Palette = palette,
                Transparency = alpha,
            };
        }

        byte[]? trns = null;
        if (useKey)
        {
            ushort[] key = colorType == 0 ? [s.KeyR] : [s.KeyR, s.KeyG, s.KeyB];
            for (var i = 0; i < key.Length; i++)
                key[i] = ScaleToDepth(key[i], sampleBytes, bitDepth);
            trns = KeyBytes(key);
        }
        return new PngEncodePlan(channels, stride, sampleBytes, colorType, bitDepth, ops, map: null) { Transparency = trns };
    }

    private struct Stats
    {
        public bool Opaque, BinaryAlpha, Grey, Fits8, KeyCandidate, SawTransparent;
        public int GreyDepth;
        public ushort KeyR, KeyG, KeyB;
        public PaletteMap? Map;
    }

    // The smallest grey depth on whose scale an 8-bit level lies: 1 for 0 and 255, 2 for multiples of
    // 85, 4 for multiples of 17 (a level l of depth d is l·255/(2^d − 1), PNG's left-bit-replication).
    private static readonly byte[] GreyDepthOf = [.. Enumerable.Range(0, 256)
        .Select(v => v is 0 or 255 ? 1 : v % 85 == 0 ? 2 : v % 17 == 0 ? 4 : 8).Select(d => (byte)d)];

    // A source sample at the file's depth: a 16-bit value that fits 8 bits keeps its high byte, an 8-bit
    // grey level on a sub-byte scale becomes its level (v >> (8 − depth) undoes l·255/(2^depth − 1)).
    private static ushort ScaleToDepth(int v, int sampleBytes, int depth)
    {
        if (depth == 16) return (ushort)v;
        var v8 = sampleBytes == 2 ? v >> 8 : v;
        return (ushort)(depth == 8 ? v8 : v8 >> (8 - depth));
    }

    private static byte[] KeyBytes(ushort[] key)
    {
        var bytes = new byte[2 * key.Length];
        for (var i = 0; i < key.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2 * i), key[i]);
        return bytes;
    }

    // ---------------- per-layout kernels -----------------------------------------------------------

    /// <summary>
    /// One source layout, read through a struct type parameter so the JIT compiles every loop below once
    /// per layout with the read inlined: no per-pixel switch on channels or sample size.
    /// </summary>
    private interface IPixels
    {
        /// <summary>Logical samples per pixel: 1 grey, 2 grey + alpha, 3 RGB, 4 RGBA.</summary>
        static abstract int Channels { get; }

        /// <summary>Stored samples per pixel (more than <see cref="Channels"/> when an alpha is ignored).</summary>
        static abstract int Stride { get; }

        /// <summary>16-bit host-order samples rather than bytes.</summary>
        static abstract bool Wide { get; }

        /// <summary>Pixel <paramref name="pixel"/> as R, G, B, A (grey in all three; no alpha as the maximum).</summary>
        static abstract void Read(ref byte samples, nint pixel, out int r, out int g, out int b, out int a);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int At8(ref byte s, nint i) => Unsafe.Add(ref s, i);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int At16(ref byte s, nint i) => Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref s, 2 * i));

    private readonly struct Grey8 : IPixels
    {
        public static int Channels => 1;
        public static int Stride => 1;
        public static bool Wide => false;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(ref byte s, nint p, out int r, out int g, out int b, out int a) { r = g = b = At8(ref s, p); a = 0xFF; }
    }

    private readonly struct GreyAlpha8 : IPixels
    {
        public static int Channels => 2;
        public static int Stride => 2;
        public static bool Wide => false;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(ref byte s, nint p, out int r, out int g, out int b, out int a) { r = g = b = At8(ref s, 2 * p); a = At8(ref s, 2 * p + 1); }
    }

    private readonly struct GreyOfGreyAlpha8 : IPixels
    {
        public static int Channels => 1;
        public static int Stride => 2;
        public static bool Wide => false;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(ref byte s, nint p, out int r, out int g, out int b, out int a) { r = g = b = At8(ref s, 2 * p); a = 0xFF; }
    }

    private readonly struct Rgb8 : IPixels
    {
        public static int Channels => 3;
        public static int Stride => 3;
        public static bool Wide => false;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(ref byte s, nint p, out int r, out int g, out int b, out int a) { r = At8(ref s, 3 * p); g = At8(ref s, 3 * p + 1); b = At8(ref s, 3 * p + 2); a = 0xFF; }
    }

    private readonly struct Rgba8 : IPixels
    {
        public static int Channels => 4;
        public static int Stride => 4;
        public static bool Wide => false;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(ref byte s, nint p, out int r, out int g, out int b, out int a) { r = At8(ref s, 4 * p); g = At8(ref s, 4 * p + 1); b = At8(ref s, 4 * p + 2); a = At8(ref s, 4 * p + 3); }
    }

    private readonly struct RgbOfRgba8 : IPixels
    {
        public static int Channels => 3;
        public static int Stride => 4;
        public static bool Wide => false;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(ref byte s, nint p, out int r, out int g, out int b, out int a) { r = At8(ref s, 4 * p); g = At8(ref s, 4 * p + 1); b = At8(ref s, 4 * p + 2); a = 0xFF; }
    }

    private readonly struct Grey16 : IPixels
    {
        public static int Channels => 1;
        public static int Stride => 1;
        public static bool Wide => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(ref byte s, nint p, out int r, out int g, out int b, out int a) { r = g = b = At16(ref s, p); a = 0xFFFF; }
    }

    private readonly struct GreyAlpha16 : IPixels
    {
        public static int Channels => 2;
        public static int Stride => 2;
        public static bool Wide => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(ref byte s, nint p, out int r, out int g, out int b, out int a) { r = g = b = At16(ref s, 2 * p); a = At16(ref s, 2 * p + 1); }
    }

    private readonly struct GreyOfGreyAlpha16 : IPixels
    {
        public static int Channels => 1;
        public static int Stride => 2;
        public static bool Wide => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(ref byte s, nint p, out int r, out int g, out int b, out int a) { r = g = b = At16(ref s, 2 * p); a = 0xFFFF; }
    }

    private readonly struct Rgb16 : IPixels
    {
        public static int Channels => 3;
        public static int Stride => 3;
        public static bool Wide => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(ref byte s, nint p, out int r, out int g, out int b, out int a) { r = At16(ref s, 3 * p); g = At16(ref s, 3 * p + 1); b = At16(ref s, 3 * p + 2); a = 0xFFFF; }
    }

    private readonly struct Rgba16 : IPixels
    {
        public static int Channels => 4;
        public static int Stride => 4;
        public static bool Wide => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(ref byte s, nint p, out int r, out int g, out int b, out int a) { r = At16(ref s, 4 * p); g = At16(ref s, 4 * p + 1); b = At16(ref s, 4 * p + 2); a = At16(ref s, 4 * p + 3); }
    }

    private readonly struct RgbOfRgba16 : IPixels
    {
        public static int Channels => 3;
        public static int Stride => 4;
        public static bool Wide => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(ref byte s, nint p, out int r, out int g, out int b, out int a) { r = At16(ref s, 4 * p); g = At16(ref s, 4 * p + 1); b = At16(ref s, 4 * p + 2); a = 0xFFFF; }
    }

    private static IOps OpsFor(int channels, int stride, int sampleBytes) => (channels, stride, sampleBytes) switch
    {
        (1, 1, 1) => new Ops<Grey8>(),
        (2, 2, 1) => new Ops<GreyAlpha8>(),
        (1, 2, 1) => new Ops<GreyOfGreyAlpha8>(),
        (3, 3, 1) => new Ops<Rgb8>(),
        (4, 4, 1) => new Ops<Rgba8>(),
        (3, 4, 1) => new Ops<RgbOfRgba8>(),
        (1, 1, 2) => new Ops<Grey16>(),
        (2, 2, 2) => new Ops<GreyAlpha16>(),
        (1, 2, 2) => new Ops<GreyOfGreyAlpha16>(),
        (3, 3, 2) => new Ops<Rgb16>(),
        (4, 4, 2) => new Ops<Rgba16>(),
        (3, 4, 2) => new Ops<RgbOfRgba16>(),
        _ => throw new ArgumentException($"No PNG source layout of {channels} of {stride} samples of {sampleBytes} byte(s)"),
    };

    private interface IOps
    {
        Stats Scan(ReadOnlySpan<byte> samples, int pixels, bool trackPalette);
        bool OpaqueHasColour(ReadOnlySpan<byte> samples, int pixels, int keyR, int keyG, int keyB);
        void FillSamples(ReadOnlySpan<byte> src, Span<byte> dst, int width, int colorType, int depth);
        void FillPackedGrey(ReadOnlySpan<byte> src, Span<byte> dst, int width, int depth);
        void FillIndexed(ReadOnlySpan<byte> src, Span<byte> dst, int width, PaletteMap map, int depth);
    }

    private sealed class Ops<T> : IOps where T : struct, IPixels
    {
        private static int PixelBytes => T.Stride * (T.Wide ? 2 : 1);
        private static int Max => T.Wide ? 0xFFFF : 0xFF;

        // The one check every unchecked read below relies on: the span holds all `pixels` pixels.
        private static void Check(ReadOnlySpan<byte> samples, int pixels)
        {
            if (samples.Length < (long)pixels * PixelBytes)
                throw new ArgumentException($"{samples.Length} bytes cannot hold {pixels} pixels of {PixelBytes} bytes");
        }

        /// <summary>
        /// One pass, in blocks of 4096 pixels, that stops at the first block boundary where nothing is left
        /// to learn: a photograph has lost its palette, its greyness and its depth reductions within a few
        /// hundred pixels, and unless an alpha channel is still opaque the scan ends there. While only "is
        /// the alpha opaque" is open, whole blocks are checked sixty-four bytes at a time.
        /// </summary>
        public Stats Scan(ReadOnlySpan<byte> samples, int pixels, bool trackPalette)
        {
            Check(samples, pixels);
            ref var s0 = ref MemoryMarshal.GetReference(samples);
            var greySource = T.Channels <= 2;
            var hasAlpha = T.Channels is 2 or 4;
            var shift = T.Wide ? 8 : 0;
            var st = new Stats { Opaque = true, BinaryAlpha = true, Grey = true, Fits8 = true, KeyCandidate = true, GreyDepth = 1 };
            var map = trackPalette ? new PaletteMap() : null;
            var depthAlive = true;
            var last = 0u;
            var haveLast = false;
            for (var i = 0; i < pixels;)
            {
                var end = Math.Min(pixels, i + 4096);
                for (; i < end; i++)
                {
                    T.Read(ref s0, i, out var r, out var g, out var b, out var a);

                    // 16-bit samples are 8-bit values while both their bytes agree.
                    if (T.Wide && st.Fits8 && (((r ^ (r >> 8)) | (g ^ (g >> 8)) | (b ^ (b >> 8)) | (a ^ (a >> 8))) & 0xFF) != 0)
                    {
                        st.Fits8 = false;
                        map = null;        // a palette holds 8-bit samples only
                        depthAlive = false; // and so do the sub-byte depths
                    }

                    if (!greySource && st.Grey && (r != g || r != b))
                    {
                        st.Grey = false;
                        depthAlive = false;
                    }

                    if (depthAlive && GreyDepthOf[r >> shift] is var d && d > st.GreyDepth)
                    {
                        st.GreyDepth = d;
                        depthAlive = d < 8;
                    }

                    if (hasAlpha && a != Max && st.BinaryAlpha)
                    {
                        st.Opaque = false;
                        if (a != 0)
                        {
                            st.BinaryAlpha = false;
                        }
                        else if (!st.SawTransparent)
                        {
                            st.SawTransparent = true;
                            (st.KeyR, st.KeyG, st.KeyB) = ((ushort)r, (ushort)g, (ushort)b);
                        }
                        else if (r != st.KeyR || g != st.KeyG || b != st.KeyB)
                        {
                            st.KeyCandidate = false;
                        }
                    }

                    if (map is not null)
                    {
                        var colour = (uint)((r >> shift) | ((g >> shift) << 8) | ((b >> shift) << 16) | ((a >> shift) << 24));
                        if (!haveLast || colour != last)
                        {
                            if (map.Add(colour) < 0)
                                map = null;
                            last = colour;
                            haveLast = true;
                        }
                    }
                }

                // Plain greyscale at full 8-bit depth: a palette of more than 16 levels is 8 bits a pixel,
                // which greyscale already is (and wins the tie), so the palette has nothing left to offer.
                if (map is not null && T.Channels == 1 && !depthAlive && map.Count > 16)
                    map = null;

                if (map is null && !(T.Wide && st.Fits8) && !(!greySource && st.Grey) && !depthAlive)
                {
                    if (!(hasAlpha && st.BinaryAlpha && (st.Opaque || st.KeyCandidate)))
                        break;
                    if (st.Opaque)
                        i = FirstNonOpaqueBlock(samples, i, pixels);
                }
            }

            st.Map = map;
            return st;
        }

        // From pixel `from`, the first 64-byte block (16 / 32 pixels) holding an alpha below the maximum,
        // or the start of the tail too short for a block; the scalar scan resumes there.
        private static int FirstNonOpaqueBlock(ReadOnlySpan<byte> samples, int from, int pixels)
        {
            if (!Vector128.IsHardwareAccelerated)
                return from;

            // PixelBytes is 2, 4 or 8 for a layout with alpha, so a block is whole pixels and the pattern
            // of alpha bytes repeats in every vector.
            var pixelBytes = PixelBytes;
            var alphaBytes = T.Wide ? 2 : 1;
            Span<byte> pattern = stackalloc byte[16];
            for (var k = 0; k < 16; k++)
                pattern[k] = k % pixelBytes >= pixelBytes - alphaBytes ? (byte)0 : (byte)0xFF;
            var notAlpha = Vector128.Create((ReadOnlySpan<byte>)pattern);

            ref var s = ref MemoryMarshal.GetReference(samples);
            var total = (nint)pixels * pixelBytes;
            var at = (nint)from * pixelBytes;
            // Each block's loads end at `at + 64 <= total <= samples.Length` (Check).
            for (; at + 64 <= total; at += 64)
            {
                var all = Vector128.LoadUnsafe(ref s, (nuint)at) & Vector128.LoadUnsafe(ref s, (nuint)(at + 16))
                        & Vector128.LoadUnsafe(ref s, (nuint)(at + 32)) & Vector128.LoadUnsafe(ref s, (nuint)(at + 48));
                if ((all | notAlpha) != Vector128<byte>.AllBitsSet)
                    break;
            }
            return (int)(at / pixelBytes);
        }

        public bool OpaqueHasColour(ReadOnlySpan<byte> samples, int pixels, int keyR, int keyG, int keyB)
        {
            Check(samples, pixels);
            ref var s0 = ref MemoryMarshal.GetReference(samples);
            for (var i = 0; i < pixels; i++)
            {
                T.Read(ref s0, i, out var r, out var g, out var b, out var a);
                if (a == Max && r == keyR && g == keyG && b == keyB)
                    return true;
            }
            return false;
        }

        // Greyscale, grey + alpha, RGB or RGBA at 8 or 16 bits. Greyscale writes R (= G = B), grey + alpha
        // R and A. Narrowing 16-bit samples to their high bytes with every channel kept is one vector
        // narrow per sixteen samples.
        public void FillSamples(ReadOnlySpan<byte> src, Span<byte> dst, int width, int colorType, int depth)
        {
            Check(src, width);
            var outChannels = PngReader.SamplesPerPixel(colorType);
            if (dst.Length < (long)width * outChannels * (depth / 8))
                throw new ArgumentException("Row too short for the samples", nameof(dst));
            ref var s0 = ref MemoryMarshal.GetReference(src);
            ref var d0 = ref MemoryMarshal.GetReference(dst);

            if (T.Wide && depth == 8 && outChannels == T.Channels && T.Channels == T.Stride)
            {
                ref var u0 = ref Unsafe.As<byte, ushort>(ref s0);
                nint total = width * T.Stride, k = 0;
                if (Vector128.IsHardwareAccelerated)
                {
                    // Loads [k, k+16) of `total` samples, stores [k, k+16) of `total` bytes.
                    for (; k + 16 <= total; k += 16)
                    {
                        var lo = Vector128.ShiftRightLogical(Vector128.LoadUnsafe(ref u0, (nuint)k), 8);
                        var hi = Vector128.ShiftRightLogical(Vector128.LoadUnsafe(ref u0, (nuint)(k + 8)), 8);
                        Vector128.Narrow(lo, hi).StoreUnsafe(ref d0, (nuint)k);
                    }
                }
                for (; k < total; k++)
                    Unsafe.Add(ref d0, k) = (byte)(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref s0, 2 * k)) >> 8);
                return;
            }

            var shift = T.Wide && depth == 8 ? 8 : 0;
            for (nint x = 0; x < width; x++)
            {
                T.Read(ref s0, x, out var r, out var g, out var b, out var a);
                var o = x * outChannels;
                if (depth == 16)
                {
                    Put16(ref d0, o, r);
                    if (outChannels == 2) Put16(ref d0, o + 1, a);
                    if (outChannels >= 3) { Put16(ref d0, o + 1, g); Put16(ref d0, o + 2, b); }
                    if (outChannels == 4) Put16(ref d0, o + 3, a);
                }
                else
                {
                    Unsafe.Add(ref d0, o) = (byte)(r >> shift);
                    if (outChannels == 2) Unsafe.Add(ref d0, o + 1) = (byte)(a >> shift);
                    if (outChannels >= 3) { Unsafe.Add(ref d0, o + 1) = (byte)(g >> shift); Unsafe.Add(ref d0, o + 2) = (byte)(b >> shift); }
                    if (outChannels == 4) Unsafe.Add(ref d0, o + 3) = (byte)(a >> shift);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Put16(ref byte d, nint sample, int v)
        {
            Unsafe.Add(ref d, 2 * sample) = (byte)(v >> 8);
            Unsafe.Add(ref d, 2 * sample + 1) = (byte)v;
        }

        public void FillPackedGrey(ReadOnlySpan<byte> src, Span<byte> dst, int width, int depth)
        {
            Check(src, width);
            ref var s0 = ref MemoryMarshal.GetReference(src);
            var shift = (T.Wide ? 8 : 0) + 8 - depth;
            var packer = new BitPacker(dst, depth);
            for (nint x = 0; x < width; x++)
            {
                T.Read(ref s0, x, out var r, out _, out _, out _);
                packer.Put(r >> shift);
            }
            packer.Flush();
        }

        public void FillIndexed(ReadOnlySpan<byte> src, Span<byte> dst, int width, PaletteMap map, int depth)
        {
            Check(src, width);
            ref var s0 = ref MemoryMarshal.GetReference(src);
            var shift = T.Wide ? 8 : 0;
            var last = 0u;
            var lastIndex = -1;
            var packer = new BitPacker(dst, depth);
            for (nint x = 0; x < width; x++)
            {
                T.Read(ref s0, x, out var r, out var g, out var b, out var a);
                var colour = (uint)((r >> shift) | ((g >> shift) << 8) | ((b >> shift) << 16) | ((a >> shift) << 24));
                if (lastIndex < 0 || colour != last)
                {
                    lastIndex = map.IndexOf(colour);
                    last = colour;
                }
                packer.Put(lastIndex);
            }
            packer.Flush();
        }
    }

    // Samples of `depth` bits into a row, most significant first, the last byte's unused bits zero.
    private ref struct BitPacker(Span<byte> row, int depth)
    {
        private readonly Span<byte> _row = row;
        private int _pos, _acc, _bits;

        public void Put(int sample)
        {
            if (depth == 8)
            {
                _row[_pos++] = (byte)sample;
                return;
            }
            _acc = (_acc << depth) | sample;
            _bits += depth;
            if (_bits == 8)
            {
                _row[_pos++] = (byte)_acc;
                _acc = _bits = 0;
            }
        }

        public void Flush()
        {
            if (_bits > 0)
                _row[_pos] = (byte)(_acc << (8 - _bits));
        }
    }

    /// <summary>
    /// At most 256 RGBA8 colours (packed R | G·2^8 | B·2^16 | A·2^24) and their palette indices, in an
    /// open-addressed table of 512 slots, so a lookup is a multiply, a shift and usually one compare.
    /// </summary>
    private sealed class PaletteMap
    {
        private const int Slots = 512;
        private readonly uint[] _keys = new uint[Slots];
        private readonly short[] _index = Enumerable.Repeat((short)-1, Slots).ToArray();
        private readonly List<uint> _colours = [];

        public int Count => _colours.Count;

        private static int Home(uint colour) => (int)((colour * 0x9E3779B1u) >> 23);

        /// <summary>The colour's index, adding it if new; −1 once a 257th colour would be needed.</summary>
        public int Add(uint colour)
        {
            for (var slot = Home(colour); ; slot = (slot + 1) & (Slots - 1))
            {
                if (_index[slot] < 0)
                {
                    if (_colours.Count == 256)
                        return -1;
                    _keys[slot] = colour;
                    _index[slot] = (short)_colours.Count;
                    _colours.Add(colour);
                    return _index[slot];
                }
                if (_keys[slot] == colour)
                    return _index[slot];
            }
        }

        /// <summary>The colour's index, or −1. At most 256 of 512 slots are used, so a probe ends.</summary>
        public int IndexOf(uint colour)
        {
            for (var slot = Home(colour); ; slot = (slot + 1) & (Slots - 1))
            {
                if (_index[slot] < 0) return -1;
                if (_keys[slot] == colour) return _index[slot];
            }
        }

        /// <summary>
        /// Renumbers the colours translucent first (in order of first appearance, then the opaque ones
        /// likewise), and returns PLTE and tRNS for that order; tRNS is null when every colour is opaque.
        /// </summary>
        public (byte[] Palette, byte[]? Alpha) OrderTranslucentFirst()
        {
            var order = _colours.Where(c => c >> 24 != 0xFF).Concat(_colours.Where(c => c >> 24 == 0xFF)).ToArray();
            var translucent = _colours.Count(c => c >> 24 != 0xFF);
            var palette = new byte[3 * order.Length];
            var alpha = translucent > 0 ? new byte[translucent] : null;
            for (var i = 0; i < order.Length; i++)
            {
                var c = order[i];
                palette[3 * i] = (byte)c;
                palette[3 * i + 1] = (byte)(c >> 8);
                palette[3 * i + 2] = (byte)(c >> 16);
                if (i < translucent)
                    alpha![i] = (byte)(c >> 24);
                for (var slot = Home(c); ; slot = (slot + 1) & (Slots - 1))
                {
                    if (_keys[slot] == c && _index[slot] >= 0)
                    {
                        _index[slot] = (short)i;
                        break;
                    }
                }
            }
            return (palette, alpha);
        }
    }
}
