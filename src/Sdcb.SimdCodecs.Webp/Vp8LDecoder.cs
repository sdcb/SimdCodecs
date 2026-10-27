using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// RFC 9649 VP8L entropy and transform decoder. The implementation follows
/// Sections 3.5-3.8; no implementation source from an existing WebP codec is
/// used.
/// </summary>
internal static class Vp8LDecoder
{
    private static readonly int[] CodeLengthCodeOrder =
        { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

    private static readonly (int X, int Y)[] DistanceMap =
    {
        (0, 1), (1, 0), (1, 1), (-1, 1), (0, 2), (2, 0), (1, 2), (-1, 2),
        (2, 1), (-2, 1), (2, 2), (-2, 2), (0, 3), (3, 0), (1, 3), (-1, 3),
        (3, 1), (-3, 1), (2, 3), (-2, 3), (3, 2), (-3, 2), (0, 4), (4, 0),
        (1, 4), (-1, 4), (4, 1), (-4, 1), (3, 3), (-3, 3), (2, 4), (-2, 4),
        (4, 2), (-4, 2), (0, 5), (3, 4), (-3, 4), (4, 3), (-4, 3), (5, 0),
        (1, 5), (-1, 5), (5, 1), (-5, 1), (2, 5), (-2, 5), (5, 2), (-5, 2),
        (4, 4), (-4, 4), (3, 5), (-3, 5), (5, 3), (-5, 3), (0, 6), (6, 0),
        (1, 6), (-1, 6), (6, 1), (-6, 1), (2, 6), (-2, 6), (6, 2), (-6, 2),
        (4, 5), (-4, 5), (5, 4), (-5, 4), (3, 6), (-3, 6), (6, 3), (-6, 3),
        (0, 7), (7, 0), (1, 7), (-1, 7), (5, 5), (-5, 5), (7, 1), (-7, 1),
        (4, 6), (-4, 6), (6, 4), (-6, 4), (2, 7), (-2, 7), (7, 2), (-7, 2),
        (3, 7), (-3, 7), (7, 3), (-7, 3), (5, 6), (-5, 6), (6, 5), (-6, 5),
        (8, 0), (4, 7), (-4, 7), (7, 4), (-7, 4), (8, 1), (8, 2), (6, 6),
        (-6, 6), (8, 3), (5, 7), (-5, 7), (7, 5), (-7, 5), (8, 4), (6, 7),
        (-6, 7), (7, 6), (-7, 6), (8, 5), (7, 7), (-7, 7), (8, 6), (8, 7)
    };

    public static byte[] DecodeRgba(ReadOnlySpan<byte> stream)
    {
        ReadHeader(stream, out int width, out int height);
        byte[] result = new byte[checked(width * height * 4)];
        DecodeRgbaInPlace(stream, result, width, height, WebpAblation.None);
        return result;
    }

    public static void DecodeRgbaTo(ReadOnlySpan<byte> stream, Span<byte> output, int stride,
        WebpAblation ablation = WebpAblation.None)
    {
        ReadHeader(stream, out int width, out int height);
        int rowLength = checked(width * 4);
        if (stride < rowLength || output.Length < checked((height - 1) * stride + rowLength))
        {
            throw new ArgumentException("The destination is too small for the decoded VP8L image.", nameof(output));
        }

        if (stride == rowLength)
        {
            DecodeRgbaInPlace(stream, output, width, height, ablation);
            return;
        }

        int[] argb = DecodeImage(new BitReader(stream[5..]), width, height, allowTransforms: true, ablation,
            Span<byte>.Empty, out _, Span<int>.Empty)!;
        try
        {
            if ((ablation & WebpAblation.SkipOutput) == 0)
            {
                ToRgba(argb.AsSpan(0, width * height), output, width, height, stride);
            }
        }
        finally
        {
            WebpBufferPool<int>.Return(argb);
        }
    }

    /// <summary>
    /// A tightly packed RGBA destination has exactly the size of the ARGB image (4 bytes a pixel):
    /// the image is decoded and transformed in it and then swizzled in place, so no frame-sized
    /// buffer is held besides the caller's.
    /// </summary>
    private static void DecodeRgbaInPlace(ReadOnlySpan<byte> stream, Span<byte> output, int width, int height,
        WebpAblation ablation)
    {
        int pixelCount = width * height;
        Span<int> argb = MemoryMarshal.Cast<byte, int>(output[..(pixelCount * 4)]);
        DecodeImage(new BitReader(stream[5..]), width, height, allowTransforms: true, ablation,
            Span<byte>.Empty, out _, argb);
        if ((ablation & WebpAblation.SkipOutput) == 0)
        {
            ToRgba(argb, output, pixelCount, 1, pixelCount * 4);
        }
    }

    public static byte[] DecodeAlpha(ReadOnlySpan<byte> stream, int width, int height)
    {
        if ((long)width * height > WebpLimits.MaximumPixels)
        {
            throw new InvalidDataException("The VP8L alpha dimensions exceed the pixel limit.");
        }

        int[] argb = DecodeImage(new BitReader(stream), width, height, allowTransforms: true, WebpAblation.None,
            Span<byte>.Empty, out _, Span<int>.Empty)!;
        try
        {
            int pixelCount = checked(width * height);
            byte[] alpha = new byte[pixelCount];
            CopyAlpha(argb, alpha, pixelCount);
            return alpha;
        }
        finally
        {
            WebpBufferPool<int>.Return(argb);
        }
    }

    public static void DecodeAlphaTo(ReadOnlySpan<byte> stream, int width, int height, Span<byte> alpha)
    {
        if ((long)width * height > WebpLimits.MaximumPixels)
        {
            throw new InvalidDataException("The VP8L alpha dimensions exceed the pixel limit.");
        }

        int pixelCount = checked(width * height);
        if (alpha.Length < pixelCount)
        {
            throw new ArgumentException("The alpha destination is too small.", nameof(alpha));
        }

        int[] argb = DecodeImage(new BitReader(stream), width, height, allowTransforms: true,
            WebpAblation.None, alpha[..pixelCount], out bool alphaWritten, Span<int>.Empty)!;
        try
        {
            if (!alphaWritten)
            {
                CopyAlpha(argb, alpha, pixelCount);
            }
        }
        finally
        {
            WebpBufferPool<int>.Return(argb);
        }
    }

    // The alpha plane is the green channel (RFC 9649 §2 / the ALPH chunk definition).
    internal static void CopyAlpha(ReadOnlySpan<int> argb, Span<byte> alpha, int pixelCount)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref int source = ref MemoryMarshal.GetReference(argb);
            ref byte destination = ref MemoryMarshal.GetReference(alpha);
            Vector256<int> mask = Vector256.Create(0xff);
            for (; i <= pixelCount - 32; i += 32)
            {
                Vector256<int> a = (Vector256.LoadUnsafe(ref source, (nuint)i) >> 8) & mask;
                Vector256<int> b = (Vector256.LoadUnsafe(ref source, (nuint)(i + 8)) >> 8) & mask;
                Vector256<int> c = (Vector256.LoadUnsafe(ref source, (nuint)(i + 16)) >> 8) & mask;
                Vector256<int> d = (Vector256.LoadUnsafe(ref source, (nuint)(i + 24)) >> 8) & mask;
                Vector256.Narrow(Vector256.Narrow(a, b), Vector256.Narrow(c, d)).AsByte()
                    .StoreUnsafe(ref destination, (nuint)i);
            }
        }

        for (; i < pixelCount; i++)
        {
            alpha[i] = (byte)((argb[i] >> 8) & 0xff);
        }
    }

    /// <summary>
    /// Colour-indexing inverse transform (RFC 9649 §4.4) that keeps only the green channel of each
    /// palette entry, which is all an ALPH plane needs: every packed pixel's index byte expands to
    /// its 1, 2, 4 or 8 alpha bytes through a 256-entry table, instead of building ARGB pixels and
    /// extracting green afterwards. Indices past the palette give 0, as in the ARGB transform.
    /// </summary>
    internal static void ExpandColorIndexingToAlpha(Span<int> pixels, Transform transform, Span<byte> alpha)
    {
        int packed = 1 << transform.Bits;
        int bitsPerIndex = 8 / packed;
        int mask = (1 << bitsPerIndex) - 1;
        int width = transform.Width;
        int encodedWidth = (width + packed - 1) / packed;
        int[] table = transform.Table!;
        int tableLength = transform.TableLength;
        Span<byte> lut = stackalloc byte[256 * 8];
        for (int value = 0; value < 256; value++)
        {
            for (int k = 0; k < packed; k++)
            {
                int index = (value >> (k * bitsPerIndex)) & mask;
                lut[value * packed + k] = index < tableLength ? (byte)(table[index] >> 8) : (byte)0;
            }
        }

        ref byte lutBase = ref MemoryMarshal.GetReference(lut);
        int fullGroups = width / packed;
        for (int y = 0; y < transform.Height; y++)
        {
            ReadOnlySpan<int> source = pixels.Slice(y * encodedWidth, encodedWidth);
            Span<byte> row = alpha.Slice(y * width, width);
            ref byte destination = ref MemoryMarshal.GetReference(row);
            int group = 0;
            switch (packed)
            {
                case 8:
                    for (; group < fullGroups; group++)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, group * 8),
                            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref lutBase, ((source[group] >> 8) & 0xff) * 8)));
                    }

                    break;
                case 4:
                    for (; group < fullGroups; group++)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, group * 4),
                            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref lutBase, ((source[group] >> 8) & 0xff) * 4)));
                    }

                    break;
                case 2:
                    for (; group < fullGroups; group++)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, group * 2),
                            Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref lutBase, ((source[group] >> 8) & 0xff) * 2)));
                    }

                    break;
                default:
                    for (; group < fullGroups; group++)
                    {
                        row[group] = lut[(source[group] >> 8) & 0xff];
                    }

                    break;
            }

            // A last, partial group.
            for (int x = fullGroups * packed; x < width; x++)
            {
                int value = (source[x / packed] >> 8) & 0xff;
                row[x] = lut[value * packed + x % packed];
            }
        }
    }

    [InlineArray(4)]
    private struct TransformList
    {
        private Transform _element;
    }

    /// <summary>
    /// Decodes a VP8L image (transforms and the spatially coded pixels) into
    /// <paramref name="destination"/>, which needs width × height pixels, or, when it is empty,
    /// into a pooled array that is returned (the caller returns it to the pool).
    /// </summary>
    /// <param name="alphaDestination">
    /// When not empty and the last inverse transform is colour indexing, that transform writes the
    /// alpha (green) plane here directly and <paramref name="alphaWritten"/> is true; the pixels
    /// are then the packed indices.
    /// </param>
    private static int[]? DecodeImage(BitReader reader, int width, int height, bool allowTransforms,
        WebpAblation ablation, Span<byte> alphaDestination, out bool alphaWritten, Span<int> destination)
    {
        alphaWritten = false;
        if (width <= 0 || height <= 0 || (long)width * height > WebpLimits.MaximumPixels)
        {
            throw new InvalidDataException("The VP8L image dimensions are invalid.");
        }

        int pixelCount = width * height;
        TransformList transforms = default;
        int transformCount = 0;
        int encodedWidth = width;
        int encodedHeight = height;
        int[]? owned = null;
        try
        {
            if (allowTransforms)
            {
                Span<bool> seen = stackalloc bool[4];
                seen.Clear();
                while (reader.ReadBit() != 0)
                {
                    int type = reader.ReadBits(2);
                    if (seen[type])
                    {
                        throw new InvalidDataException("The VP8L transform sequence is invalid.");
                    }

                    seen[type] = true;
                    Transform transform = ReadTransform(ref reader, type, encodedWidth, encodedHeight);
                    transforms[transformCount++] = transform;
                    if (type == 3)
                    {
                        encodedWidth = (encodedWidth + (1 << transform.Bits) - 1) >> transform.Bits;
                    }
                }
            }

            Span<int> pixels;
            if (destination.IsEmpty)
            {
                owned = WebpBufferPool<int>.Rent(pixelCount);
                pixels = owned.AsSpan(0, pixelCount);
            }
            else
            {
                pixels = destination[..pixelCount];
            }

            DecodeImageStream(ref reader, encodedWidth, encodedHeight, hasMetaPrefix: true, pixels);
            for (int i = transformCount - 1; i >= 0; i--)
            {
                if (((ablation & WebpAblation.SkipTransforms) != 0 && transforms[i].Type != 3) ||
                    ((ablation & WebpAblation.SkipPredictorTransform) != 0 && transforms[i].Type == 0))
                {
                    continue;
                }

                if (i == 0 && transforms[0].Type == 3 && !alphaDestination.IsEmpty)
                {
                    ExpandColorIndexingToAlpha(pixels, transforms[0], alphaDestination);
                    alphaWritten = true;
                    break;
                }

                ApplyTransform(pixels, transforms[i]);
            }

            int[]? result = owned;
            owned = null;
            return result;
        }
        finally
        {
            if (owned is not null)
            {
                WebpBufferPool<int>.Return(owned);
            }

            for (int i = 0; i < transformCount; i++)
            {
                if (transforms[i].Data is { } data)
                {
                    WebpBufferPool<int>.Return(data);
                }

                if (transforms[i].Table is { } table)
                {
                    WebpBufferPool<int>.Return(table);
                }
            }
        }
    }

    private static Transform ReadTransform(ref BitReader reader, int type, int width, int height)
    {
        if (type == 0 || type == 1)
        {
            int bits = reader.ReadBits(3) + 2;
            int blockWidth = (width + (1 << bits) - 1) >> bits;
            int blockHeight = (height + (1 << bits) - 1) >> bits;
            int[] data = DecodeImageStream(ref reader, blockWidth, blockHeight, hasMetaPrefix: false);
            return new Transform(type, width, height, bits, data, null, 0);
        }

        if (type == 2)
        {
            return new Transform(type, width, height, 0, null, null, 0);
        }

        int tableSize = reader.ReadBits(8) + 1;
        int bitsPerPixel = tableSize <= 2 ? 3 : tableSize <= 4 ? 2 : tableSize <= 16 ? 1 : 0;
        int tableWidth = tableSize;
        int[] tableData = DecodeImageStream(ref reader, tableWidth, 1, hasMetaPrefix: false);
        int[] table = WebpBufferPool<int>.Rent(tableSize);
        try
        {
            int previous = 0;
            for (int i = 0; i < tableWidth; i++)
            {
                int packed = tableData[i];
                int alpha = ((packed >> 24) & 0xff) + ((previous >> 24) & 0xff);
                int red = ((packed >> 16) & 0xff) + ((previous >> 16) & 0xff);
                int green = ((packed >> 8) & 0xff) + ((previous >> 8) & 0xff);
                int blue = (packed & 0xff) + (previous & 0xff);
                previous = ((alpha & 0xff) << 24) | ((red & 0xff) << 16) |
                    ((green & 0xff) << 8) | (blue & 0xff);
                table[i] = previous;
            }

            return new Transform(type, width, height, bitsPerPixel, null, table, tableSize);
        }
        catch
        {
            WebpBufferPool<int>.Return(table);
            throw;
        }
        finally
        {
            WebpBufferPool<int>.Return(tableData);
        }
    }

    // A subimage (entropy image, transform data, palette) into a pooled array.
    private static int[] DecodeImageStream(ref BitReader reader, int width, int height, bool hasMetaPrefix)
    {
        if (width <= 0 || height <= 0 || (long)width * height > WebpLimits.MaximumPixels)
        {
            throw new InvalidDataException("The VP8L subimage dimensions are invalid.");
        }

        int[] result = WebpBufferPool<int>.Rent(width * height);
        try
        {
            DecodeImageStream(ref reader, width, height, hasMetaPrefix, result.AsSpan(0, width * height));
            return result;
        }
        catch
        {
            WebpBufferPool<int>.Return(result);
            throw;
        }
    }

    /// <summary>Decodes an entropy-coded image of width × height pixels into <paramref name="result"/>.</summary>
    private static void DecodeImageStream(ref BitReader reader, int width, int height, bool hasMetaPrefix,
        Span<int> result)
    {
        if (width <= 0 || height <= 0 || (long)width * height > WebpLimits.MaximumPixels)
        {
            throw new InvalidDataException("The VP8L subimage dimensions are invalid.");
        }

        int[]? entropy = null;
        HuffmanGroup[]? groups = null;
        uint[]? tables = null;
        int[]? cache = null;
        int groupCount = 1;
        int entropyWidth = 1;
        // With no meta prefix image every pixel is in group 0: a one-entry image of group 0 and a
        // block width no column reaches stand in for it.
        int prefixBits = 31;
        int cacheBits = 0;
        int pixelCount = checked(width * height);
        try
        {
            if (reader.ReadBit() != 0)
            {
                cacheBits = reader.ReadBits(4);
                if (cacheBits is < 1 or > 11)
                {
                    throw new InvalidDataException("The VP8L color cache size is invalid.");
                }
            }

            if (hasMetaPrefix && reader.ReadBit() != 0)
            {
                prefixBits = reader.ReadBits(3) + 2;
                entropyWidth = (width + (1 << prefixBits) - 1) >> prefixBits;
                int entropyHeight = (height + (1 << prefixBits) - 1) >> prefixBits;
                int entropyCount = checked(entropyWidth * entropyHeight);
                entropy = DecodeImageStream(ref reader, entropyWidth, entropyHeight, hasMetaPrefix: false);
                int max = 0;
                for (int i = 0; i < entropyCount; i++)
                {
                    max = Math.Max(max, (entropy[i] >> 8) & 0xffff);
                }

                groupCount = max + 1;
            }

            groups = WebpBufferPool<HuffmanGroup>.Rent(groupCount);
            int greenAlphabet = 256 + 24 + (cacheBits == 0 ? 0 : 1 << cacheBits);
            tables = WebpBufferPool<uint>.Rent(2048);
            int tablesUsed = 0;
            for (int i = 0; i < groupCount; i++)
            {
                groups[i] = ReadHuffmanGroup(ref reader, greenAlphabet, ref tables, ref tablesUsed);
            }

            int cacheLength = cacheBits == 0 ? 0 : 1 << cacheBits;
            if (cacheLength != 0)
            {
                cache = WebpBufferPool<int>.Rent(cacheLength);
                Array.Clear(cache, 0, cacheLength);
            }

            DecodePixels(ref reader, result, pixelCount, width, entropy ?? SingleGroupImage, entropy is not null, entropyWidth, prefixBits,
                groups, groupCount, tables, cache, cacheBits);
            reader.ThrowIfTruncated();
        }
        finally
        {
            if (cache is not null)
            {
                WebpBufferPool<int>.Return(cache);
            }

            if (entropy is not null)
            {
                WebpBufferPool<int>.Return(entropy);
            }

            if (groups is not null)
            {
                WebpBufferPool<HuffmanGroup>.Return(groups);
            }

            if (tables is not null)
            {
                WebpBufferPool<uint>.Return(tables);
            }
        }
    }

    private static readonly int[] SingleGroupImage = [0];

    /// <summary>
    /// The spatially coded pixels of one image (RFC 9649 §5.2): literals, backward references and
    /// colour cache codes. The bit buffer lives in locals throughout. The colour cache is filled
    /// lazily: its content only matters when a cache code is read, so the pixels produced since the
    /// last such read are inserted (in order, which gives the same cache) right before it.
    /// </summary>
    private static void DecodePixels(ref BitReader reader, Span<int> result, int pixelCount, int width, int[] entropy,
        bool spatialGroups, int entropyWidth, int prefixBits, HuffmanGroup[] groups, int groupCount, uint[] tables, int[]? cache,
        int cacheBits)
    {
        reader.Load(out ulong buffer, out int bits);
        ref int output = ref MemoryMarshal.GetReference(result);
        ref uint tableBase = ref MemoryMarshal.GetArrayDataReference(tables);
        int blockMask = (1 << prefixBits) - 1;
        int position = 0;
        int cached = 0;
        int x = 0;
        int y = 0;
        ref HuffmanGroup group = ref SelectGroup(groups, groupCount, entropy, entropyWidth, prefixBits, 0, 0);
        while (position < pixelCount)
        {
            Refill(ref reader, ref buffer, ref bits);
            int symbol = ReadSymbol(ref Unsafe.Add(ref tableBase, group.Green), group.GreenMask, ref buffer, ref bits);
            if (symbol < 256)
            {
                int color;
                if (group.TrivialLiteral)
                {
                    color = group.Literal | (symbol << 8);
                }
                else
                {
                    // Green and red take at most 30 bits; refill before blue and alpha.
                    int red = ReadSymbol(ref Unsafe.Add(ref tableBase, group.Red), group.RedMask, ref buffer, ref bits);
                    Refill(ref reader, ref buffer, ref bits);
                    int blue = ReadSymbol(ref Unsafe.Add(ref tableBase, group.Blue), group.BlueMask, ref buffer, ref bits);
                    int alpha = ReadSymbol(ref Unsafe.Add(ref tableBase, group.Alpha), group.AlphaMask, ref buffer, ref bits);
                    color = (alpha << 24) | (red << 16) | (symbol << 8) | blue;
                }

                // position < pixelCount <= result.Length (loop condition; callers pass result of pixelCount).
                Unsafe.Add(ref output, position++) = color;
                if (++x == width)
                {
                    x = 0;
                    y++;
                    // Past the last row there is no group (nor an entry in the meta prefix image).
                    if (position == pixelCount)
                    {
                        break;
                    }

                    group = ref SelectGroup(groups, groupCount, entropy, entropyWidth, prefixBits, x, y);
                }
                else if ((x & blockMask) == 0)
                {
                    group = ref SelectGroup(groups, groupCount, entropy, entropyWidth, prefixBits, x, y);
                }

                continue;
            }

            if (symbol < 280)
            {
                // At least 17 bits are left after green; length extra bits are at most 10, distance
                // codes 15 and distance extra bits 18, with a refill (to at least 32) between.
                int length = PrefixValue(symbol - 256, ref buffer, ref bits);
                Refill(ref reader, ref buffer, ref bits);
                // The distance alphabet has 40 symbols, so the code is always valid.
                int distanceCode = ReadSymbol(ref Unsafe.Add(ref tableBase, group.Distance), group.DistanceMask,
                    ref buffer, ref bits);
                Refill(ref reader, ref buffer, ref bits);
                int distance = Distance(PrefixValue(distanceCode, ref buffer, ref bits), width);
                if (distance > position)
                {
                    throw new InvalidDataException("The VP8L backward distance is invalid.");
                }

                if (length > pixelCount - position)
                {
                    throw new InvalidDataException("The VP8L backward copy exceeds the image.");
                }

                CopyBackward(result, position, distance, length);
                position += length;
                if (spatialGroups && position < pixelCount)
                {
                    (y, x) = Math.DivRem(position, width);
                    group = ref SelectGroup(groups, groupCount, entropy, entropyWidth, prefixBits, x, y);
                }

                continue;
            }

            // The green alphabet is 280 + the cache size, so a cache is present and the index in range.
            int[] colors = cache!;
            ref int cacheBase = ref MemoryMarshal.GetArrayDataReference(colors);
            int shift = 32 - cacheBits;
            for (; cached < position; cached++)
            {
                int value = Unsafe.Add(ref output, cached);
                Unsafe.Add(ref cacheBase, (int)((0x1e35a7bdu * (uint)value) >> shift)) = value;
            }

            Unsafe.Add(ref output, position++) = Unsafe.Add(ref cacheBase, symbol - 280);
            if (++x == width)
            {
                x = 0;
                y++;
                if (position == pixelCount)
                {
                    break;
                }

                group = ref SelectGroup(groups, groupCount, entropy, entropyWidth, prefixBits, x, y);
            }
            else if ((x & blockMask) == 0)
            {
                group = ref SelectGroup(groups, groupCount, entropy, entropyWidth, prefixBits, x, y);
            }
        }

        reader.Store(buffer, bits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref HuffmanGroup SelectGroup(HuffmanGroup[] groups, int groupCount, int[] entropy, int entropyWidth,
        int prefixBits, int x, int y)
    {
        int index = (entropy[(y >> prefixBits) * entropyWidth + (x >> prefixBits)] >> 8) & 0xffff;
        if ((uint)index >= (uint)groupCount)
        {
            throw new InvalidDataException("The VP8L meta prefix code is out of range.");
        }

        return ref groups[index];
    }

    /// <summary>
    /// LZ77 copy within <paramref name="pixels"/>. An overlapping copy (distance &lt; length) repeats
    /// the last <paramref name="distance"/> pixels: it is done in chunks that double, each read from
    /// the already periodic run so that source and destination never overlap.
    /// </summary>
    private static void CopyBackward(Span<int> pixels, int position, int distance, int length)
    {
        int source = position - distance;
        if (length <= 16)
        {
            // A forward element-by-element copy also repeats the period of an overlapping one.
            // Callers checked 0 <= source and position + length <= pixels.Length.
            ref int from = ref Unsafe.Add(ref MemoryMarshal.GetReference(pixels), source);
            ref int to = ref Unsafe.Add(ref MemoryMarshal.GetReference(pixels), position);
            for (int i = 0; i < length; i++)
            {
                Unsafe.Add(ref to, i) = Unsafe.Add(ref from, i);
            }

            return;
        }

        if (distance >= length)
        {
            pixels.Slice(source, length).CopyTo(pixels.Slice(position, length));
            return;
        }

        if (distance == 1)
        {
            pixels.Slice(position, length).Fill(pixels[source]);
            return;
        }

        int copied = 0;
        while (copied < length)
        {
            int chunk = Math.Min(length - copied, distance + copied);
            pixels.Slice(source, chunk).CopyTo(pixels.Slice(position + copied, chunk));
            copied += chunk;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Refill(ref BitReader reader, ref ulong buffer, ref int bits)
    {
        if (bits < 32)
        {
            reader.Refill(ref buffer, ref bits);
        }
    }

    /// <summary>
    /// Decodes one symbol with at least 15 bits in the buffer. A root entry is either a symbol (code
    /// length in the low byte) or, with <see cref="SubtableFlag"/>, the offset and index width of
    /// the second-level table that the next bits select from. Entries are built so that every
    /// index is inside the code's table (see <see cref="BuildHuffmanTable"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadSymbol(ref uint table, uint rootMask, ref ulong buffer, ref int bits)
    {
        uint entry = Unsafe.Add(ref table, (nint)((uint)buffer & rootMask));
        if ((entry & SubtableFlag) != 0)
        {
            uint index = (entry >> 16) + (((uint)buffer >> RootBits) & ((1u << (int)(entry & 0xff)) - 1));
            entry = Unsafe.Add(ref table, (nint)index);
        }

        int length = (int)(entry & 0xff);
        buffer >>= length;
        bits -= length;
        return (int)(entry >> 16);
    }

    // RFC 9649 §5.2.2 prefix coding of LZ77 lengths and distances; at most 18 extra bits.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PrefixValue(int code, ref ulong buffer, ref int bits)
    {
        if (code < 4)
        {
            return code + 1;
        }

        int extraBits = (code - 2) >> 1;
        int offset = (2 + (code & 1)) << extraBits;
        int extra = (int)((uint)buffer & ((1u << extraBits) - 1));
        buffer >>= extraBits;
        bits -= extraBits;
        return offset + extra + 1;
    }

    private static HuffmanGroup ReadHuffmanGroup(ref BitReader reader, int greenAlphabet, ref uint[] tables,
        ref int tablesUsed)
    {
        HuffmanGroup group = default;
        group.Green = ReadHuffmanCode(ref reader, greenAlphabet, ref tables, ref tablesUsed, out group.GreenMask, out _);
        group.Red = ReadHuffmanCode(ref reader, 256, ref tables, ref tablesUsed, out group.RedMask, out int red);
        group.Blue = ReadHuffmanCode(ref reader, 256, ref tables, ref tablesUsed, out group.BlueMask, out int blue);
        group.Alpha = ReadHuffmanCode(ref reader, 256, ref tables, ref tablesUsed, out group.AlphaMask, out int alpha);
        group.Distance = ReadHuffmanCode(ref reader, 40, ref tables, ref tablesUsed, out group.DistanceMask, out _);
        // Red, blue and alpha each with a single symbol cost no bits: the literal is then
        // known up to its green value.
        if (red >= 0 && blue >= 0 && alpha >= 0)
        {
            group.TrivialLiteral = true;
            group.Literal = (alpha << 24) | (red << 16) | blue;
        }

        return group;
    }

    /// <summary>Reads one prefix code (RFC 9649 §3.7.2.1) and appends its decoding table.</summary>
    /// <param name="singleSymbol">The symbol when the code has only one (it then takes no bits), else -1.</param>
    /// <returns>The offset of the code's table in <paramref name="tables"/>.</returns>
    private static int ReadHuffmanCode(ref BitReader reader, int alphabetSize, ref uint[] tables, ref int tablesUsed,
        out uint rootMask, out int singleSymbol)
    {
        int startBit = reader.BitPosition;
        int[] lengths = WebpBufferPool<int>.Rent(alphabetSize);
        try
        {
            Span<int> lengthsSpan = lengths.AsSpan(0, alphabetSize);
            lengthsSpan.Clear();
            if (reader.ReadBit() != 0)
            {
                int count = reader.ReadBit() + 1;
                int firstWide = reader.ReadBit();
                int first = reader.ReadBits(1 + 7 * firstWide);
                if (first >= alphabetSize)
                {
                    throw new InvalidDataException("The VP8L simple prefix symbol is out of range.");
                }

                lengthsSpan[first] = 1;
                if (count == 2)
                {
                    int second = reader.ReadBits(8);
                    if (second >= alphabetSize)
                    {
                        throw new InvalidDataException("The VP8L simple prefix symbol is out of range.");
                    }

                    lengthsSpan[second] = 1;
                }

                return BuildHuffmanTable(lengthsSpan, ref tables, ref tablesUsed, out rootMask, out singleSymbol);
            }

            Span<int> codeLengthLengths = stackalloc int[19];
            codeLengthLengths.Clear();
            int numberOfCodeLengths = 4 + reader.ReadBits(4);
            for (int i = 0; i < numberOfCodeLengths; i++)
            {
                codeLengthLengths[CodeLengthCodeOrder[i]] = reader.ReadBits(3);
            }

            // Code lengths are at most 7 bits, so this table has no second level and at most 128 entries.
            Span<uint> codeLengthTable = stackalloc uint[1 << 7];
            uint codeLengthMask;
            try
            {
                codeLengthMask = FillHuffmanTable(codeLengthLengths, codeLengthTable, out _);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    $"The VP8L code-length tree for alphabet {alphabetSize}, starting at bit {startBit}, " +
                    $"is invalid at bit {reader.BitPosition} ({numberOfCodeLengths} lengths): " +
                    $"{string.Join(',', codeLengthLengths.ToArray())}.",
                    exception);
            }

            int lengthNBits = reader.ReadBit() == 0 ? 0 : 2 + 2 * reader.ReadBits(3);
            int maxSymbol = lengthNBits == 0 ? alphabetSize : 2 + reader.ReadBits(lengthNBits);
            if (maxSymbol > alphabetSize)
            {
                throw new InvalidDataException("The VP8L prefix alphabet is too large.");
            }

            int index = 0;
            int symbolsRead = 0;
            int previous = 8;
            while (symbolsRead < maxSymbol && index < alphabetSize)
            {
                int value = reader.ReadSymbol(codeLengthTable, codeLengthMask);
                symbolsRead++;
                if (value < 16)
                {
                    lengthsSpan[index++] = value;
                    if (value != 0) previous = value;
                }
                else if (value == 16)
                {
                    int repeat = 3 + reader.ReadBits(2);
                    if (repeat > alphabetSize - index)
                    {
                        throw new InvalidDataException(
                            $"The VP8L prefix alphabet {alphabetSize} starting at bit {startBit} " +
                            $"exceeds its output size at symbol {symbolsRead} (index {index}, " +
                            $"repeat {repeat}, max_symbol {maxSymbol}).");
                    }

                    lengthsSpan.Slice(index, repeat).Fill(previous);
                    index += repeat;
                }
                else
                {
                    // The code-length alphabet has 19 symbols: this is 17 or 18.
                    int repeat = value == 17 ? 3 + reader.ReadBits(3) : 11 + reader.ReadBits(7);
                    if (repeat > alphabetSize - index)
                    {
                        throw new InvalidDataException(
                            $"The VP8L prefix alphabet {alphabetSize} starting at bit {startBit} " +
                            $"exceeds its output size at symbol {symbolsRead} (index {index}, " +
                            $"repeat {repeat}, max_symbol {maxSymbol}).");
                    }

                    lengthsSpan.Slice(index, repeat).Clear();
                    index += repeat;
                }
            }

            return BuildHuffmanTable(lengthsSpan, ref tables, ref tablesUsed, out rootMask, out singleSymbol);
        }
        finally
        {
            WebpBufferPool<int>.Return(lengths);
        }
    }

    // Root table index width; codes up to 15 bits long put the rest in second-level tables of at
    // most 2^7 entries.
    private const int RootBits = 8;
    private const uint SubtableFlag = 0x100;

    /// <summary>Appends the decoding table of a code with the given lengths to <paramref name="tables"/>.</summary>
    private static int BuildHuffmanTable(ReadOnlySpan<int> lengths, ref uint[] tables, ref int tablesUsed,
        out uint rootMask, out int singleSymbol)
    {
        int size = HuffmanTableSize(lengths);
        if (tables.Length - tablesUsed < size)
        {
            uint[] larger = WebpBufferPool<uint>.Rent(Math.Max(checked(tablesUsed + size), checked(tables.Length * 2)));
            tables.AsSpan(0, tablesUsed).CopyTo(larger);
            WebpBufferPool<uint>.Return(tables);
            tables = larger;
        }

        int offset = tablesUsed;
        rootMask = FillHuffmanTable(lengths, tables.AsSpan(offset, size), out singleSymbol);
        tablesUsed += size;
        return offset;
    }

    /// <summary>
    /// Validates code lengths (each 0-15; the code complete, i.e. Kraft sum exactly 1, unless it
    /// has at most one symbol) and returns the size of their decoding table.
    /// </summary>
    private static int HuffmanTableSize(ReadOnlySpan<int> lengths)
    {
        Span<int> counts = stackalloc int[16];
        counts.Clear();
        int maxLength = 0;
        int symbolCount = 0;
        foreach (int length in lengths)
        {
            if ((uint)length > 15)
            {
                throw new InvalidDataException("The VP8L code length is invalid.");
            }

            if (length != 0)
            {
                counts[length]++;
                maxLength = Math.Max(maxLength, length);
                symbolCount++;
            }
        }

        if (symbolCount <= 1)
        {
            return 1;
        }

        int code = 0;
        for (int length = 1; length <= maxLength; length++)
        {
            code = (code + counts[length - 1]) << 1;
        }

        if (code + counts[maxLength] != 1 << maxLength)
        {
            throw new InvalidDataException("The VP8L prefix code is incomplete.");
        }

        int root = Math.Min(RootBits, maxLength);
        if (maxLength <= RootBits)
        {
            return 1 << root;
        }

        Span<byte> subtableBits = stackalloc byte[1 << RootBits];
        CollectSubtableBits(lengths, counts, subtableBits);
        int size = 1 << RootBits;
        foreach (byte subBits in subtableBits)
        {
            if (subBits != 0)
            {
                size += 1 << subBits;
            }
        }

        return size;
    }

    /// <summary>
    /// Fills a decoding table for (already validated) code lengths and returns its root index mask.
    /// Canonical codes (RFC 9649 §3.7.2.1, as in RFC 1951 §3.2.2) are read first bit first, so each
    /// symbol's entries sit at its bit-reversed code: every 2^length-th root slot for a short code;
    /// for a longer one, the slot of its first 8 bits points to a second-level table indexed by the
    /// remaining bits, sized for the longest code under that slot. A complete code fills every entry.
    /// </summary>
    private static uint FillHuffmanTable(ReadOnlySpan<int> lengths, Span<uint> table, out int singleSymbol)
    {
        Span<int> counts = stackalloc int[16];
        counts.Clear();
        int maxLength = 0;
        int symbolCount = 0;
        int onlySymbol = 0;
        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            int length = lengths[symbol];
            if ((uint)length > 15)
            {
                throw new InvalidDataException("The VP8L code length is invalid.");
            }

            if (length != 0)
            {
                counts[length]++;
                maxLength = Math.Max(maxLength, length);
                symbolCount++;
                onlySymbol = symbol;
            }
        }

        if (symbolCount <= 1)
        {
            // No symbol at all decodes as symbol 0; either way the code takes no bits.
            table[0] = (uint)onlySymbol << 16;
            singleSymbol = onlySymbol;
            return 0;
        }

        singleSymbol = -1;
        Span<int> next = stackalloc int[16];
        int code = 0;
        for (int length = 1; length <= maxLength; length++)
        {
            code = (code + counts[length - 1]) << 1;
            next[length] = code;
        }

        if (code + counts[maxLength] != 1 << maxLength)
        {
            throw new InvalidDataException("The VP8L prefix code is incomplete.");
        }

        int root = Math.Min(RootBits, maxLength);
        int rootSize = 1 << root;
        Span<byte> subtableBits = stackalloc byte[1 << RootBits];
        Span<ushort> subtableStart = stackalloc ushort[1 << RootBits];
        if (maxLength > RootBits)
        {
            CollectSubtableBits(lengths, counts, subtableBits);
            int start = rootSize;
            for (int slot = 0; slot < rootSize; slot++)
            {
                int subBits = subtableBits[slot];
                if (subBits != 0)
                {
                    subtableStart[slot] = (ushort)start;
                    table[slot] = ((uint)start << 16) | SubtableFlag | (uint)subBits;
                    start += 1 << subBits;
                }
            }
        }

        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            int length = lengths[symbol];
            if (length == 0)
            {
                continue;
            }

            int reversed = ReverseBits(next[length]++, length);
            uint entry = ((uint)symbol << 16) | (uint)length;
            if (length <= root)
            {
                for (int slot = reversed; slot < rootSize; slot += 1 << length)
                {
                    table[slot] = entry;
                }
            }
            else
            {
                int slot = reversed & ((1 << RootBits) - 1);
                Span<uint> subtable = table.Slice(subtableStart[slot], 1 << subtableBits[slot]);
                for (int index = reversed >> RootBits; index < subtable.Length; index += 1 << (length - RootBits))
                {
                    subtable[index] = entry;
                }
            }
        }

        return (uint)(rootSize - 1);
    }

    // For each root slot, the index width of its second-level table: the longest code under it, minus 8.
    private static void CollectSubtableBits(ReadOnlySpan<int> lengths, ReadOnlySpan<int> counts, Span<byte> subtableBits)
    {
        subtableBits.Clear();
        Span<int> next = stackalloc int[16];
        int code = 0;
        for (int length = 1; length < 16; length++)
        {
            code = (code + counts[length - 1]) << 1;
            next[length] = code;
        }

        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            int length = lengths[symbol];
            if (length == 0)
            {
                continue;
            }

            int canonical = next[length]++;
            if (length > RootBits)
            {
                int slot = ReverseBits(canonical, length) & ((1 << RootBits) - 1);
                subtableBits[slot] = (byte)Math.Max(subtableBits[slot], length - RootBits);
            }
        }
    }

    private static int ReverseBits(int value, int count)
    {
        int reversed = 0;
        for (int i = 0; i < count; i++)
        {
            reversed = (reversed << 1) | ((value >> i) & 1);
        }

        return reversed;
    }

    private static int Distance(int distanceCode, int width)
    {
        if (distanceCode > 120)
        {
            return distanceCode - 120;
        }

        if (distanceCode < 1 || distanceCode > DistanceMap.Length)
        {
            throw new InvalidDataException("The VP8L distance code is invalid.");
        }

        (int x, int y) = DistanceMap[distanceCode - 1];
        return Math.Max(1, x + y * width);
    }

    private static void ApplyTransform(Span<int> pixels, Transform transform)
    {
        switch (transform.Type)
        {
            case 0:
                ApplyPredictor(pixels, transform);
                break;
            case 1:
                ApplyColorTransform(pixels, transform);
                break;
            case 2:
                ApplySubtractGreen(pixels, transform);
                break;
            case 3:
                ApplyColorIndexing(pixels, transform);
                break;
            default:
                throw new InvalidDataException("Unknown VP8L transform.");
        }
    }

    private static void ApplyPredictor(Span<int> pixels, Transform transform)
    {
        if (Vector.IsHardwareAccelerated)
        {
            ApplyPredictorVector(pixels, transform);
        }
        else
        {
            ApplyPredictorScalar(pixels, transform);
        }
    }

    internal static void ApplyPredictorScalar(Span<int> pixels, Transform transform)
    {
        int width = transform.Width;
        int height = transform.Height;
        int blockWidth = (width + (1 << transform.Bits) - 1) >> transform.Bits;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                int left = x == 0 ? 0 : pixels[index - 1];
                int top = y == 0 ? 0 : pixels[index - width];
                int topLeft = x == 0 || y == 0 ? 0 : pixels[index - width - 1];
                int topRight = y == 0 ? 0 : x == width - 1
                    ? pixels[index - x]
                    : pixels[index - width + 1];
                int mode = (transform.Data![(y >> transform.Bits) * blockWidth + (x >> transform.Bits)] >> 8) & 0xff;
                int prediction = Predict(mode, x, y, width, left, top, topLeft, topRight);
                pixels[index] = AddArgb(pixels[index], prediction);
            }
        }
    }

    // Per-block-segment dispatch: the mode is constant across a (1 << bits)-wide
    // run of pixels, so the per-pixel mode fetch + switch of the scalar path is
    // paid once per segment instead. Modes that only read the row above are
    // bytewise-vectorizable (AddArgb/Average are per-byte-lane operations);
    // modes that read the just-written left pixel stay scalar per segment.
    internal static void ApplyPredictorVector(Span<int> pixels, Transform transform)
    {
        int width = transform.Width;
        int height = transform.Height;
        int bits = transform.Bits;
        int blockWidth = (width + (1 << bits) - 1) >> bits;
        int[] data = transform.Data!;
        int index = 0;
        for (int y = 0; y < height; y++)
        {
            int blockRow = (y >> bits) * blockWidth;
            if (y == 0)
            {
                // First pixel uses the black predictor; the rest of the top row
                // is left-predicted no matter what the block modes say.
                pixels[index] = AddArgb(pixels[index], unchecked((int)0xff000000));
                index++;
                for (int column = 1; column < width; column++, index++)
                {
                    pixels[index] = AddArgb(pixels[index], pixels[index - 1]);
                }

                continue;
            }

            // x == 0 always predicts top, whatever the segment mode is.
            pixels[index] = AddArgb(pixels[index], pixels[index - width]);
            index++;
            int x = 1;
            while (x < width)
            {
                int mode = (data[blockRow + (x >> bits)] >> 8) & 0xff;
                int remaining = Math.Min(((x >> bits) + 1) << bits, width) - x;
                if (mode is 5 or 6 or 7 or 10 or 11 or 12 or 13 && (Sse2.IsSupported || AdvSimd.IsSupported))
                {
                    PredictFromLeftVector(pixels, index, remaining, width, mode);
                    x += remaining;
                    index += remaining;
                    continue;
                }

                switch (mode)
                {
                    case 0:
                        AddConstant(pixels, index, remaining, unchecked((int)0xff000000));
                        break;
                    case 1:
                        LeftSegment(pixels, index, remaining);
                        break;
                    case 2:
                        AddAbove(pixels, index, remaining, -width);
                        break;
                    case 3:
                        AddAbove(pixels, index, remaining, 1 - width);
                        break;
                    case 4:
                        AddAbove(pixels, index, remaining, -width - 1);
                        break;
                    case 5:
                        for (int i = 0; i < remaining; i++)
                        {
                            int j = index + i;
                            pixels[j] = AddArgb(pixels[j],
                                Average(Average(pixels[j - 1], pixels[j - width + 1]), pixels[j - width]));
                        }

                        break;
                    case 6:
                        for (int i = 0; i < remaining; i++)
                        {
                            int j = index + i;
                            pixels[j] = AddArgb(pixels[j], Average(pixels[j - 1], pixels[j - width - 1]));
                        }

                        break;
                    case 7:
                        for (int i = 0; i < remaining; i++)
                        {
                            int j = index + i;
                            pixels[j] = AddArgb(pixels[j], Average(pixels[j - 1], pixels[j - width]));
                        }

                        break;
                    case 8:
                        AddAverageAbove(pixels, index, remaining, -width - 1, -width);
                        break;
                    case 9:
                        AddAverageAbove(pixels, index, remaining, -width, 1 - width);
                        break;
                    case 10:
                        for (int i = 0; i < remaining; i++)
                        {
                            int j = index + i;
                            pixels[j] = AddArgb(pixels[j],
                                Average(Average(pixels[j - 1], pixels[j - width - 1]),
                                    Average(pixels[j - width], pixels[j - width + 1])));
                        }

                        break;
                    case 11:
                        SelectSegment(pixels, index, remaining, width);
                        break;
                    case 12:
                        for (int i = 0; i < remaining; i++)
                        {
                            int j = index + i;
                            pixels[j] = AddArgb(pixels[j],
                                ClampAdd(pixels[j - 1], pixels[j - width], pixels[j - width - 1]));
                        }

                        break;
                    case 13:
                        for (int i = 0; i < remaining; i++)
                        {
                            int j = index + i;
                            pixels[j] = AddArgb(pixels[j],
                                ClampHalf(Average(pixels[j - 1], pixels[j - width]), pixels[j - width - 1]));
                        }

                        break;
                    default:
                        throw new InvalidDataException("The VP8L predictor mode is invalid.");
                }

                x += remaining;
                index += remaining;
            }
        }
    }

    /// <summary>
    /// The predictor modes that read the pixel just reconstructed to the left (RFC 9649 §4.1): a
    /// serial chain, one pixel per step, so each pixel is one vector (bytes 0-3) and the left
    /// pixel never leaves a register. Average2 is the floor average, Select a compare and blend
    /// (no branch on the data), the clamps go through 16-bit lanes.
    /// <paramref name="index"/> is at x >= 1 of a row y >= 1, so every neighbour read is at or
    /// after index - width - 1 >= 0 and before index + count &lt;= pixels.Length.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void PredictFromLeftVector(Span<int> pixels, int index, int count, int width, int mode)
    {
        ref int row = ref Unsafe.Add(ref MemoryMarshal.GetReference(pixels), index);
        ref int above = ref Unsafe.Subtract(ref row, width);
        Vector128<byte> left = Pixel(ref Unsafe.Subtract(ref row, 1));
        switch (mode)
        {
            case 5:
                for (int i = 0; i < count; i++)
                {
                    Vector128<byte> pred = AverageFloor(AverageFloor(left, Pixel(ref Unsafe.Add(ref above, i + 1))),
                        Pixel(ref Unsafe.Add(ref above, i)));
                    left = Pixel(ref Unsafe.Add(ref row, i)) + pred;
                    Unsafe.Add(ref row, i) = left.AsInt32().ToScalar();
                }

                break;
            case 6:
                for (int i = 0; i < count; i++)
                {
                    Vector128<byte> pred = AverageFloor(left, Pixel(ref Unsafe.Add(ref above, i - 1)));
                    left = Pixel(ref Unsafe.Add(ref row, i)) + pred;
                    Unsafe.Add(ref row, i) = left.AsInt32().ToScalar();
                }

                break;
            case 7:
                for (int i = 0; i < count; i++)
                {
                    Vector128<byte> pred = AverageFloor(left, Pixel(ref Unsafe.Add(ref above, i)));
                    left = Pixel(ref Unsafe.Add(ref row, i)) + pred;
                    Unsafe.Add(ref row, i) = left.AsInt32().ToScalar();
                }

                break;
            case 10:
                for (int i = 0; i < count; i++)
                {
                    Vector128<byte> topPart = AverageFloor(Pixel(ref Unsafe.Add(ref above, i)),
                        Pixel(ref Unsafe.Add(ref above, i + 1)));
                    Vector128<byte> pred = AverageFloor(AverageFloor(left, Pixel(ref Unsafe.Add(ref above, i - 1))), topPart);
                    left = Pixel(ref Unsafe.Add(ref row, i)) + pred;
                    Unsafe.Add(ref row, i) = left.AsInt32().ToScalar();
                }

                break;
            case 11:
                for (int i = 0; i < count; i++)
                {
                    Vector128<byte> top = Pixel(ref Unsafe.Add(ref above, i));
                    Vector128<byte> topLeft = Pixel(ref Unsafe.Add(ref above, i - 1));
                    // Lane 0 of each holds the 4-channel L1 distance (zero bytes elsewhere cancel).
                    Vector128<int> distanceToLeft = Sad4Vector(top, topLeft);
                    Vector128<int> distanceToTop = Sad4Vector(left, topLeft);
                    Vector128<byte> useLeft = Vector128.LessThan(distanceToLeft, distanceToTop).AsByte();
                    left = Pixel(ref Unsafe.Add(ref row, i)) + Vector128.ConditionalSelect(useLeft, left, top);
                    Unsafe.Add(ref row, i) = left.AsInt32().ToScalar();
                }

                break;
            case 12:
                for (int i = 0; i < count; i++)
                {
                    Vector128<short> delta = Vector128.WidenLower(Pixel(ref Unsafe.Add(ref above, i))).AsInt16()
                        - Vector128.WidenLower(Pixel(ref Unsafe.Add(ref above, i - 1))).AsInt16();
                    Vector128<byte> pred = PackClamped(Vector128.WidenLower(left).AsInt16() + delta);
                    left = Pixel(ref Unsafe.Add(ref row, i)) + pred;
                    Unsafe.Add(ref row, i) = left.AsInt32().ToScalar();
                }

                break;
            default:
                for (int i = 0; i < count; i++)
                {
                    Vector128<short> average = Vector128.WidenLower(AverageFloor(left, Pixel(ref Unsafe.Add(ref above, i)))).AsInt16();
                    Vector128<short> difference = average - Vector128.WidenLower(Pixel(ref Unsafe.Add(ref above, i - 1))).AsInt16();
                    // (a - b) / 2 truncating toward zero, as RFC 9649's C division does.
                    Vector128<short> half = Vector128.ShiftRightArithmetic(difference - Vector128.ShiftRightArithmetic(difference, 15), 1);
                    left = Pixel(ref Unsafe.Add(ref row, i)) + PackClamped(average + half);
                    Unsafe.Add(ref row, i) = left.AsInt32().ToScalar();
                }

                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Pixel(ref int pixel) => Vector128.CreateScalar(pixel).AsByte();

    // floor((a + b) / 2) per byte.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> AverageFloor(Vector128<byte> a, Vector128<byte> b)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.Average(a, b) - ((a ^ b) & Vector128<byte>.One);
        }

        return AdvSimd.FusedAddHalving(a, b);
    }

    // Lanes 0-3 clamped to 0..255 into bytes 0-3.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> PackClamped(Vector128<short> value)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.PackUnsignedSaturate(value, value);
        }

        return AdvSimd.ExtractNarrowingSaturateUnsignedLower(value).ToVector128Unsafe();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Sad4Vector(Vector128<byte> a, Vector128<byte> b)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.SumAbsoluteDifferences(a, b).AsInt32();
        }

        Vector128<byte> difference = AdvSimd.AbsoluteDifference(a, b);
        return Vector128.CreateScalarUnsafe((int)AdvSimd.Arm64.AddAcross(difference.GetLower()).ToScalar());
    }

    private static void AddAbove(Span<int> pixels, int index, int count, int offset)
    {
        int i = 0;
        int byteCount = count * 4;
        if (Vector.IsHardwareAccelerated && byteCount >= Vector<byte>.Count)
        {
            ref byte baseRef = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<int, byte>(pixels));
            ref byte destination = ref Unsafe.Add(ref baseRef, index * 4);
            ref byte source = ref Unsafe.Add(ref baseRef, (index + offset) * 4);
            for (; i + Vector<byte>.Count <= byteCount; i += Vector<byte>.Count)
            {
                Vector<byte> current = Vector.LoadUnsafe(ref destination, (nuint)i);
                Vector<byte> above = Vector.LoadUnsafe(ref source, (nuint)i);
                (current + above).StoreUnsafe(ref destination, (nuint)i);
            }
        }

        for (int pixel = i / 4; pixel < count; pixel++)
        {
            pixels[index + pixel] = AddArgb(pixels[index + pixel], pixels[index + pixel + offset]);
        }
    }

    private static void AddAverageAbove(Span<int> pixels, int index, int count, int offsetA, int offsetB)
    {
        int i = 0;
        int byteCount = count * 4;
        if (Vector.IsHardwareAccelerated && byteCount >= Vector<byte>.Count)
        {
            ref byte baseRef = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<int, byte>(pixels));
            ref byte destination = ref Unsafe.Add(ref baseRef, index * 4);
            ref byte sourceA = ref Unsafe.Add(ref baseRef, (index + offsetA) * 4);
            ref byte sourceB = ref Unsafe.Add(ref baseRef, (index + offsetB) * 4);
            for (; i + Vector<byte>.Count <= byteCount; i += Vector<byte>.Count)
            {
                Vector<byte> a = Vector.LoadUnsafe(ref sourceA, (nuint)i);
                Vector<byte> b = Vector.LoadUnsafe(ref sourceB, (nuint)i);
                // (a + b) >> 1 per byte lane, no carry between lanes.
                Vector<byte> average = (a & b) + Vector.ShiftRightLogical(a ^ b, 1);
                Vector<byte> current = Vector.LoadUnsafe(ref destination, (nuint)i);
                (current + average).StoreUnsafe(ref destination, (nuint)i);
            }
        }

        for (int pixel = i / 4; pixel < count; pixel++)
        {
            pixels[index + pixel] = AddArgb(pixels[index + pixel],
                Average(pixels[index + pixel + offsetA], pixels[index + pixel + offsetB]));
        }
    }

    private static void AddConstant(Span<int> pixels, int index, int count, int value)
    {
        int i = 0;
        int byteCount = count * 4;
        if (Vector.IsHardwareAccelerated && byteCount >= Vector<byte>.Count)
        {
            Vector<int> pattern = new(value);
            Vector<byte> constant = Unsafe.As<Vector<int>, Vector<byte>>(ref pattern);
            ref byte baseRef = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<int, byte>(pixels));
            ref byte destination = ref Unsafe.Add(ref baseRef, index * 4);
            for (; i + Vector<byte>.Count <= byteCount; i += Vector<byte>.Count)
            {
                Vector<byte> current = Vector.LoadUnsafe(ref destination, (nuint)i);
                (current + constant).StoreUnsafe(ref destination, (nuint)i);
            }
        }

        for (int pixel = i / 4; pixel < count; pixel++)
        {
            pixels[index + pixel] = AddArgb(pixels[index + pixel], value);
        }
    }

    // Byte-position shuffles for the left-predictor prefix sum. Shift masks
    // move each int's bytes one/two/four pixels forward (zero-fill at the
    // start); indices are absolute across the whole 256-bit vector, which is
    // what lets the 16-byte step carry into the upper lane.
    private static readonly Vector256<byte> PrefixShift4 = Vector256.Create(
        0x80, 0x80, 0x80, 0x80, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
        12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27);
    private static readonly Vector256<byte> PrefixShift8 = Vector256.Create(
        0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23);
    private static readonly Vector256<byte> PrefixShift16 = Vector256.Create(
        0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
        0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
    private static readonly Vector256<byte> Broadcast4x8 = Vector256.Create(
        (byte)0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3,
        0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3);
    private static readonly Vector256<byte> BroadcastLast4x8 = Vector256.Create(
        (byte)28, 29, 30, 31, 28, 29, 30, 31, 28, 29, 30, 31, 28, 29, 30, 31,
        28, 29, 30, 31, 28, 29, 30, 31, 28, 29, 30, 31, 28, 29, 30, 31);
    private static readonly Vector128<byte> PrefixShift4x4 = Vector128.Create(
        0x80, 0x80, 0x80, 0x80, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
    private static readonly Vector128<byte> PrefixShift8x4 = Vector128.Create(
        0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
        0, 1, 2, 3, 4, 5, 6, 7);
    private static readonly Vector128<byte> Broadcast4x4 = Vector128.Create(
        (byte)0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3);
    private static readonly Vector128<byte> BroadcastLast4x4 = Vector128.Create(
        (byte)12, 13, 14, 15, 12, 13, 14, 15, 12, 13, 14, 15, 12, 13, 14, 15);

    // Mode 1 (left): out[i] = out[i-1] + res[i] per byte lane — a prefix sum
    // with stride 4. Byte adds wrap mod 256 exactly like AddArgb, so the
    // log-step shuffle+adds compute all four channels' sums at once; the
    // previous pixel broadcasts in as the base.
    private static void LeftSegment(Span<int> pixels, int index, int count)
    {
        ref int baseRef = ref MemoryMarshal.GetReference(pixels);
        ref byte bytes = ref Unsafe.As<int, byte>(ref baseRef);
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<byte> carry = Vector256.Shuffle(
                Vector256.CreateScalar(pixels[index - 1]).AsByte(), Broadcast4x8);
            for (; i + 8 <= count; i += 8)
            {
                Vector256<byte> v = Vector256.LoadUnsafe(ref bytes, (nuint)((index + i) * 4));
                v += Vector256.Shuffle(v, PrefixShift4);
                v += Vector256.Shuffle(v, PrefixShift8);
                v += Vector256.Shuffle(v, PrefixShift16);
                v += carry;
                v.StoreUnsafe(ref bytes, (nuint)((index + i) * 4));
                carry = Vector256.Shuffle(v, BroadcastLast4x8);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> carry = Vector128.Shuffle(
                Vector128.CreateScalar(pixels[index - 1]).AsByte(), Broadcast4x4);
            for (; i + 4 <= count; i += 4)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref bytes, (nuint)((index + i) * 4));
                v += Vector128.Shuffle(v, PrefixShift4x4);
                v += Vector128.Shuffle(v, PrefixShift8x4);
                v += carry;
                v.StoreUnsafe(ref bytes, (nuint)((index + i) * 4));
                carry = Vector128.Shuffle(v, BroadcastLast4x4);
            }
        }

        int left = pixels[index + i - 1];
        for (; i < count; i++)
        {
            left = AddArgb(pixels[index + i], left);
            pixels[index + i] = left;
        }
    }

    // Mode 11 (select): same recurrence as the scalar loop, but kept in vector
    // registers — each 4-channel L1 distance is one SAD op and the residual
    // add is a wrapping byte add. The left value stays in a register.
    private static void SelectSegment(Span<int> pixels, int index, int count, int width)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> left = Vector128.CreateScalar(pixels[index - 1]).AsByte();
            for (int i = 0; i < count; i++)
            {
                int j = index + i;
                Vector128<byte> top = Vector128.CreateScalar(pixels[j - width]).AsByte();
                Vector128<byte> topLeft = Vector128.CreateScalar(pixels[j - width - 1]).AsByte();
                int dl = Sad4(top, topLeft);
                int dt = Sad4(left, topLeft);
                Vector128<byte> pred = dl < dt ? left : top;
                Vector128<byte> o = Vector128.CreateScalar(pixels[j]).AsByte() + pred;
                pixels[j] = o.AsInt32().ToScalar();
                left = o;
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            int j = index + i;
            pixels[j] = AddArgb(pixels[j], Select(pixels[j - 1], pixels[j - width], pixels[j - width - 1]));
        }
    }

    // a and b have only bytes 0-3 set (CreateScalar zero-pads), so summing the
    // low absolute byte differences gives the four-channel L1 distance.
    private static int Sad4(Vector128<byte> a, Vector128<byte> b)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.SumAbsoluteDifferences(a, b).ToScalar();
        }

        Vector128<byte> diff = Vector128.Max(a, b) - Vector128.Min(a, b);
        Vector128<ushort> wide = Vector128.WidenLower(diff);
        return wide[0] + wide[1] + wide[2] + wide[3];
    }

    private static int Predict(int mode, int x, int y, int width, int left, int top, int topLeft, int topRight)
    {
        if (x == 0 && y == 0) return unchecked((int)0xff000000);
        if (y == 0) return left;
        if (x == 0) return top;
        return mode switch
        {
            0 => unchecked((int)0xff000000),
            1 => left,
            2 => top,
            3 => topRight,
            4 => topLeft,
            5 => Average(Average(left, topRight), top),
            6 => Average(left, topLeft),
            7 => Average(left, top),
            8 => Average(topLeft, top),
            9 => Average(top, topRight),
            10 => Average(Average(left, topLeft), Average(top, topRight)),
            11 => Select(left, top, topLeft),
            12 => ClampAdd(left, top, topLeft),
            13 => ClampHalf(Average(left, top), topLeft),
            _ => throw new InvalidDataException("The VP8L predictor mode is invalid."),
        };
    }

    private static void ApplyColorTransform(Span<int> pixels, Transform transform)
    {
        if (Vector.IsHardwareAccelerated)
        {
            ApplyColorTransformVector(pixels, transform);
        }
        else
        {
            ApplyColorTransformScalar(pixels, transform);
        }
    }

    internal static void ApplyColorTransformScalar(Span<int> pixels, Transform transform)
    {
        int blockWidth = (transform.Width + (1 << transform.Bits) - 1) >> transform.Bits;
        for (int y = 0; y < transform.Height; y++)
        {
            for (int x = 0; x < transform.Width; x++)
            {
                int index = y * transform.Width + x;
                int cte = transform.Data![(y >> transform.Bits) * blockWidth + (x >> transform.Bits)];
                int green = (pixels[index] >> 8) & 0xff;
                int red = (pixels[index] >> 16) & 0xff;
                int blue = pixels[index] & 0xff;
                int redDelta = ColorDelta((cte >> 0) & 0xff, green);
                int blueDelta = ColorDelta((cte >> 8) & 0xff, green);
                int redForBlue = (red + redDelta) & 0xff;
                blue = (blue + blueDelta + ColorDelta((cte >> 16) & 0xff, redForBlue)) & 0xff;
                red = redForBlue;
                pixels[index] = (pixels[index] & unchecked((int)0xff00ff00)) | (red << 16) | blue;
            }
        }
    }

    // Per-block-segment dispatch again: the three multiplier bytes are constant
    // across a (1 << bits)-wide run, so they broadcast into int lanes. All the
    // arithmetic is exact int math (products fit, shifts match the scalar >>
    // semantics), so vector lanes are bit-identical to the scalar path.
    internal static void ApplyColorTransformVector(Span<int> pixels, Transform transform)
    {
        int width = transform.Width;
        int height = transform.Height;
        int bits = transform.Bits;
        int blockWidth = (width + (1 << bits) - 1) >> bits;
        int[] data = transform.Data!;
        int index = 0;
        for (int y = 0; y < height; y++)
        {
            int blockRow = (y >> bits) * blockWidth;
            for (int bx = 0; bx < blockWidth; bx++)
            {
                int x = bx << bits;
                if (x >= width)
                {
                    break;
                }

                int cte = data[blockRow + bx];
                int count = Math.Min((bx + 1) << bits, width) - x;
                ColorTransformSegment(pixels, index, count, cte);
                index += count;
            }
        }
    }

    private static void ColorTransformSegment(Span<int> pixels, int index, int count, int cte)
    {
        int c0 = (sbyte)cte;
        int c1 = (sbyte)(cte >> 8);
        int c2 = (sbyte)(cte >> 16);
        int i = 0;
        if (Vector.IsHardwareAccelerated && count >= Vector<int>.Count)
        {
            Vector<int> c0v = new(c0);
            Vector<int> c1v = new(c1);
            Vector<int> c2v = new(c2);
            Vector<int> ff = new(0xff);
            Vector<int> keep = new(unchecked((int)0xff00ff00));
            ref int baseRef = ref MemoryMarshal.GetReference(pixels);
            ref int at = ref Unsafe.Add(ref baseRef, index);
            for (; i + Vector<int>.Count <= count; i += Vector<int>.Count)
            {
                Vector<int> v = Vector.LoadUnsafe(ref at, (nuint)i);
                Vector<int> green = (v >> 8) & ff;
                Vector<int> red = (v >> 16) & ff;
                Vector<int> blue = v & ff;
                Vector<int> signedGreen = (green << 24) >> 24;
                Vector<int> newRed = red + ((signedGreen * c0v) >> 5);
                Vector<int> red8 = newRed & ff;
                Vector<int> signedRed = (red8 << 24) >> 24;
                Vector<int> newBlue = (blue + ((signedGreen * c1v) >> 5) +
                    ((signedRed * c2v) >> 5)) & ff;
                ((v & keep) | (red8 << 16) | newBlue).StoreUnsafe(ref at, (nuint)i);
            }
        }

        for (; i < count; i++)
        {
            int p = pixels[index + i];
            int green = (p >> 8) & 0xff;
            int red = (p >> 16) & 0xff;
            int blue = p & 0xff;
            int redDelta = c0 * (sbyte)green >> 5;
            int blueDelta = c1 * (sbyte)green >> 5;
            int redForBlue = (red + redDelta) & 0xff;
            blue = (blue + blueDelta + (c2 * (sbyte)redForBlue >> 5)) & 0xff;
            pixels[index + i] = (p & unchecked((int)0xff00ff00)) | (redForBlue << 16) | blue;
        }
    }

    // Byte positions 1 of each little-endian int, spread into positions 0 and 2:
    // the shuffle produces the addend {green, 0, green, 0} per pixel so one
    // vector add implements (blue += green; red += green) per byte lane.
    private static readonly Vector128<byte> SubtractGreenSpread = Vector128.Create(
        (byte)1, 0x80, 1, 0x80, 5, 0x80, 5, 0x80, 9, 0x80, 9, 0x80, 13, 0x80, 13, 0x80);
    // Vector256.Shuffle takes indices absolute to the whole vector, not per lane.
    private static readonly Vector256<byte> SubtractGreenSpread256 = Vector256.Create(
        (byte)1, 0x80, 1, 0x80, 5, 0x80, 5, 0x80, 9, 0x80, 9, 0x80, 13, 0x80, 13, 0x80,
        17, 0x80, 17, 0x80, 21, 0x80, 21, 0x80, 25, 0x80, 25, 0x80, 29, 0x80, 29, 0x80);

    private static void ApplySubtractGreen(Span<int> pixels, Transform transform)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            ApplySubtractGreenVector(pixels, transform);
        }
        else
        {
            ApplySubtractGreenScalar(pixels, transform);
        }
    }

    internal static void ApplySubtractGreenVector(Span<int> pixels, Transform transform)
    {
        int pixelCount = checked(transform.Width * transform.Height);
        ref int baseRef = ref MemoryMarshal.GetReference(pixels);
        ref byte bytes = ref Unsafe.As<int, byte>(ref baseRef);
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 8 <= pixelCount; i += 8)
            {
                Vector256<byte> v = Vector256.LoadUnsafe(ref bytes, (nuint)(i * 4));
                v += Vector256.Shuffle(v, SubtractGreenSpread256);
                v.StoreUnsafe(ref bytes, (nuint)(i * 4));
            }
        }
        else
        {
            for (; i + 4 <= pixelCount; i += 4)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref bytes, (nuint)(i * 4));
                v += Vector128.Shuffle(v, SubtractGreenSpread);
                v.StoreUnsafe(ref bytes, (nuint)(i * 4));
            }
        }

        ApplySubtractGreenTail(pixels, transform, i);
    }

    internal static void ApplySubtractGreenScalar(Span<int> pixels, Transform transform) =>
        ApplySubtractGreenTail(pixels, transform, 0);

    private static void ApplySubtractGreenTail(Span<int> pixels, Transform transform, int i)
    {
        int pixelCount = checked(transform.Width * transform.Height);
        for (; i < pixelCount; i++)
        {
            int green = (pixels[i] >> 8) & 0xff;
            int red = (((pixels[i] >> 16) & 0xff) + green) & 0xff;
            int blue = ((pixels[i] & 0xff) + green) & 0xff;
            pixels[i] = (pixels[i] & unchecked((int)0xff00ff00)) | (red << 16) | blue;
        }
    }

    /// <summary>
    /// Colour-indexing inverse transform (RFC 9649 §4.4), in place: <paramref name="pixels"/> holds
    /// the packed image (rows of ceil(width / packed) pixels) at its start and must have room for
    /// width × height pixels. Rows go last to first and right to left: a pixel's packed source
    /// is never after its destination, and every source still to be read is strictly before it.
    /// Indices past the palette give 0.
    /// </summary>
    internal static void ApplyColorIndexing(Span<int> pixels, Transform transform)
    {
        int bits = transform.Bits;
        int packed = 1 << bits;
        int bitsPerIndex = 8 / packed;
        int mask = (1 << bitsPerIndex) - 1;
        int width = transform.Width;
        int height = transform.Height;
        int encodedWidth = (width + packed - 1) >> bits;
        _ = pixels[checked(width * height) - 1];
        Span<int> palette = stackalloc int[256];
        palette.Clear();
        transform.Table.AsSpan(0, transform.TableLength).CopyTo(palette);
        if (bits == 0)
        {
            for (int i = width * height - 1; i >= 0; i--)
            {
                pixels[i] = palette[(pixels[i] >> 8) & 0xff];
            }

            return;
        }

        for (int y = height - 1; y >= 0; y--)
        {
            int inRow = y * encodedWidth;
            int outRow = y * width;
            for (int x = width - 1; x >= 0; x--)
            {
                int indices = (pixels[inRow + (x >> bits)] >> 8) & 0xff;
                pixels[outRow + x] = palette[(indices >> ((x & (packed - 1)) * bitsPerIndex)) & mask];
            }
        }
    }

    private static int AddArgb(int a, int b)
    {
        return ((((a >> 24) & 0xff) + ((b >> 24) & 0xff)) & 0xff) << 24 |
            ((((a >> 16) & 0xff) + ((b >> 16) & 0xff)) & 0xff) << 16 |
            ((((a >> 8) & 0xff) + ((b >> 8) & 0xff)) & 0xff) << 8 |
            (((a & 0xff) + (b & 0xff)) & 0xff);
    }

    private static int ColorDelta(int transform, int color)
    {
        int signedTransform = (sbyte)transform;
        int signedColor = (sbyte)color;
        return signedTransform * signedColor >> 5;
    }

    private static int Average(int a, int b) =>
        (((a >> 24 & 0xff) + (b >> 24 & 0xff)) >> 1) << 24 |
        (((a >> 16 & 0xff) + (b >> 16 & 0xff)) >> 1) << 16 |
        (((a >> 8 & 0xff) + (b >> 8 & 0xff)) >> 1) << 8 |
        ((a & 0xff) + (b & 0xff)) >> 1;

    private static int ClampAdd(int a, int b, int c) =>
        PackClamp((a >> 24 & 0xff) + (b >> 24 & 0xff) - (c >> 24 & 0xff),
            (a >> 16 & 0xff) + (b >> 16 & 0xff) - (c >> 16 & 0xff),
            (a >> 8 & 0xff) + (b >> 8 & 0xff) - (c >> 8 & 0xff),
            (a & 0xff) + (b & 0xff) - (c & 0xff));

    private static int ClampHalf(int a, int b) =>
        PackClamp((a >> 24 & 0xff) + ((a >> 24 & 0xff) - (b >> 24 & 0xff)) / 2,
            (a >> 16 & 0xff) + ((a >> 16 & 0xff) - (b >> 16 & 0xff)) / 2,
            (a >> 8 & 0xff) + ((a >> 8 & 0xff) - (b >> 8 & 0xff)) / 2,
            (a & 0xff) + ((a & 0xff) - (b & 0xff)) / 2);

    private static int Select(int left, int top, int topLeft)
    {
        int leftAlpha = left >> 24 & 0xff;
        int leftRed = left >> 16 & 0xff;
        int leftGreen = left >> 8 & 0xff;
        int leftBlue = left & 0xff;
        int topAlpha = top >> 24 & 0xff;
        int topRed = top >> 16 & 0xff;
        int topGreen = top >> 8 & 0xff;
        int topBlue = top & 0xff;
        int topLeftAlpha = topLeft >> 24 & 0xff;
        int topLeftRed = topLeft >> 16 & 0xff;
        int topLeftGreen = topLeft >> 8 & 0xff;
        int topLeftBlue = topLeft & 0xff;
        int dl = Math.Abs(topAlpha - topLeftAlpha) + Math.Abs(topRed - topLeftRed) +
            Math.Abs(topGreen - topLeftGreen) + Math.Abs(topBlue - topLeftBlue);
        int dt = Math.Abs(leftAlpha - topLeftAlpha) + Math.Abs(leftRed - topLeftRed) +
            Math.Abs(leftGreen - topLeftGreen) + Math.Abs(leftBlue - topLeftBlue);

        return dl < dt ? left : top;
    }

    private static int PackClamp(int a, int r, int g, int b) =>
        (Math.Clamp(a, 0, 255) << 24) | (Math.Clamp(r, 0, 255) << 16) |
        (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);

    private static void ReadHeader(ReadOnlySpan<byte> stream, out int width, out int height)
    {
        if (stream.Length < 5 || stream[0] != 0x2f)
        {
            throw new InvalidDataException("The VP8L stream header is truncated.");
        }

        uint header = (uint)stream[1] | ((uint)stream[2] << 8) |
            ((uint)stream[3] << 16) | ((uint)stream[4] << 24);
        width = 1 + (int)(header & 0x3fff);
        height = 1 + (int)((header >> 14) & 0x3fff);
        if ((header >> 29) != 0 || (long)width * height > WebpLimits.MaximumPixels)
        {
            throw new InvalidDataException("The VP8L dimensions or version are invalid.");
        }
    }

    // Little-endian ARGB ints are [B,G,R,A] in memory; RGBA wants bytes 0 and 2
    // swapped, which is a single byte shuffle per vector.
    private static readonly Vector128<byte> ToRgbaSwap = Vector128.Create(
        (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);

    private static readonly Vector256<byte> ToRgbaSwap256 = Vector256.Create(
        (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15,
        18, 17, 16, 19, 22, 21, 20, 23, 26, 25, 24, 27, 30, 29, 28, 31);

    private static void ToRgba(ReadOnlySpan<int> argb, Span<byte> output, int width, int height, int stride)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            ToRgbaVector(argb, output, width, height, stride);
            return;
        }

        ToRgbaScalar(argb, output, width, height, stride);
    }

    internal static void ToRgbaScalar(ReadOnlySpan<int> argb, Span<byte> output, int width, int height, int stride)
    {
        for (int row = 0; row < height; row++)
        {
            int sourceIndex = row * width;
            int outputIndex = row * stride;
            for (int column = 0; column < width; column++, sourceIndex++, outputIndex += 4)
            {
                // Read before writing: the output may be the same memory (in-place swizzle).
                int p = argb[sourceIndex];
                output[outputIndex] = (byte)(p >> 16);
                output[outputIndex + 1] = (byte)(p >> 8);
                output[outputIndex + 2] = (byte)p;
                output[outputIndex + 3] = (byte)(p >> 24);
            }
        }

    }

    internal static void ToRgbaVector(ReadOnlySpan<int> argb, Span<byte> output, int width, int height, int stride)
    {
        ref int sourceBase = ref MemoryMarshal.GetReference(argb);
        ref byte sourceBytes = ref Unsafe.As<int, byte>(ref sourceBase);
        ref byte outputBase = ref MemoryMarshal.GetReference(output);
        for (int row = 0; row < height; row++)
        {
            ref byte source = ref Unsafe.Add(ref sourceBytes, row * width * 4);
            ref byte target = ref Unsafe.Add(ref outputBase, row * stride);
            int x = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                for (; x + 8 <= width; x += 8)
                {
                    Vector256<byte> v = Vector256.LoadUnsafe(ref source, (nuint)(x * 4));
                    Vector256.Shuffle(v, ToRgbaSwap256).StoreUnsafe(ref target, (nuint)(x * 4));
                }
            }
            else
            {
                for (; x + 4 <= width; x += 4)
                {
                    Vector128<byte> v = Vector128.LoadUnsafe(ref source, (nuint)(x * 4));
                    Vector128.Shuffle(v, ToRgbaSwap).StoreUnsafe(ref target, (nuint)(x * 4));
                }
            }

            for (; x < width; x++)
            {
                int p = argb[row * width + x];
                int outputIndex = row * stride + x * 4;
                output[outputIndex] = (byte)(p >> 16);
                output[outputIndex + 1] = (byte)(p >> 8);
                output[outputIndex + 2] = (byte)p;
                output[outputIndex + 3] = (byte)(p >> 24);
            }
        }
    }

    internal readonly record struct Transform(
        int Type,
        int Width,
        int Height,
        int Bits,
        int[]? Data,
        int[]? Table,
        int TableLength);

    /// <summary>The five prefix codes of one group: table offsets and root index masks.</summary>
    private struct HuffmanGroup
    {
        public int Green;
        public int Red;
        public int Blue;
        public int Alpha;
        public int Distance;
        public uint GreenMask;
        public uint RedMask;
        public uint BlueMask;
        public uint AlphaMask;
        public uint DistanceMask;
        public bool TrivialLiteral;
        public int Literal;
    }

    /// <summary>
    /// LSB-first bit reader over a 64-bit buffer, refilled with one unaligned 8-byte load. Past the
    /// end it reads zero bytes; once the bits consumed exceed the data the stream is truncated, which
    /// <see cref="ThrowIfTruncated"/> reports (and the refill itself as soon as it is certain).
    /// </summary>
    private ref struct BitReader
    {
        private ref byte _next;
        private int _remaining;
        private readonly int _length;
        private int _padding;
        // The low _bits bits are unread input. Bits above them may hold the start of the next
        // input byte (a refill takes whole bytes but loads 8): the next refill ORs in the same bits.
        private ulong _buffer;
        private int _bits;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _next = ref MemoryMarshal.GetReference(data);
            _remaining = data.Length;
            _length = data.Length;
            _padding = 0;
            _buffer = 0;
            _bits = 0;
        }

        public readonly int BitPosition => (int)Math.Min(int.MaxValue, ((long)_length - _remaining + _padding) * 8 - _bits);

        public readonly void ThrowIfTruncated()
        {
            if (_padding != 0 && ((long)_length - _remaining + _padding) * 8 - _bits > (long)_length * 8)
            {
                throw new InvalidDataException("The VP8L bitstream is truncated.");
            }
        }

        public readonly void Load(out ulong buffer, out int bits)
        {
            buffer = _buffer;
            bits = _bits;
        }

        public void Store(ulong buffer, int bits)
        {
            _buffer = buffer;
            _bits = bits;
        }

        public int ReadBit() => ReadBits(1);

        public int ReadBits(int count)
        {
            if ((uint)count > 24)
            {
                throw new InvalidDataException("The VP8L bit count is invalid.");
            }

            if (_bits < count)
            {
                Refill(ref _buffer, ref _bits);
            }

            int result = (int)((uint)_buffer & ((1u << count) - 1));
            _buffer >>= count;
            _bits -= count;
            return result;
        }

        public int ReadSymbol(scoped ReadOnlySpan<uint> table, uint rootMask)
        {
            if (_bits < 15)
            {
                Refill(ref _buffer, ref _bits);
            }

            return Vp8LDecoder.ReadSymbol(ref MemoryMarshal.GetReference(table), rootMask, ref _buffer, ref _bits);
        }

        /// <summary>Brings <paramref name="bits"/> (at most 63 on entry) to at least 56.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Refill(ref ulong buffer, ref int bits)
        {
            if (_remaining >= 8)
            {
                // _remaining >= 8 proves the load in range; (63 - bits) / 8 whole bytes are taken,
                // which brings bits to bits | 56.
                buffer |= Unsafe.ReadUnaligned<ulong>(ref _next) << bits;
                int take = (63 - bits) >> 3;
                _next = ref Unsafe.Add(ref _next, take);
                _remaining -= take;
                bits |= 56;
            }
            else
            {
                _buffer = buffer;
                _bits = bits;
                RefillTail();
                buffer = _buffer;
                bits = _bits;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RefillTail()
        {
            while (_bits < 56)
            {
                ulong next = 0;
                if (_remaining > 0)
                {
                    next = _next;
                    _next = ref Unsafe.Add(ref _next, 1);
                    _remaining--;
                }
                else
                {
                    // With 8 zero bytes already appended and fewer than 56 bits unread, more than
                    // the whole input has been consumed.
                    if (_padding >= 8)
                    {
                        throw new InvalidDataException("The VP8L bitstream is truncated.");
                    }

                    _padding++;
                }

                _buffer |= next << _bits;
                _bits += 8;
            }
        }
    }
}
