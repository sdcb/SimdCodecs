using System.Buffers.Binary;
using ImageMagick;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Produces genuine ITU-T T.6 (CCITT Group 4) coded bytes with ImageMagick, and
/// unwraps them from the TIFF that carries them.
/// <para>
/// This is the MMR oracle, and it is the cheapest one in the repo: Magick.NET is
/// already referenced by the test project, so third-party MMR bytes
/// cost no new dependency, no build step, and nothing to install — the tests run
/// everywhere, CI included. What makes it a real oracle rather than a round-trip
/// is that <b>nothing here shares code with the decoder under test</b>: libtiff
/// writes the runs, and <c>Sdcb.SimdCodecs.Jbig2</c> reads them back.
/// </para>
/// <para>
/// T.6 is exactly the coding T.88 §6.2.6 selects for a generic region with
/// <c>MMR = 1</c>, and exactly what PDF's <c>/CCITTFaxDecode</c> carries when
/// <c>K &lt; 0</c>. Both of those are a single coded block for the whole image,
/// and so is what comes out of here: ImageMagick forces a fax-compressed TIFF to
/// one strip regardless of any rows-per-strip request, which for once is exactly
/// the shape wanted.
/// </para>
/// </summary>
internal static class Group4Tiff
{
    /// <summary>
    /// A Group 4 TIFF taken apart: the coded bytes plus what is needed to
    /// interpret them.
    /// </summary>
    /// <param name="MinIsWhite">
    /// The TIFF <c>PhotometricInterpretation</c> ImageMagick chose: true for 0
    /// (WhiteIsZero). Informational only — <see cref="Encode"/> has already
    /// compensated, so <c>Coded</c> is always in T.88 polarity.
    /// </param>
    internal sealed record Encoded(int Width, int Height, bool MinIsWhite, byte[] Coded);

    /// <summary>
    /// Whether ImageMagick's TIFF writer tags bilevel output WhiteIsZero.
    /// <para>
    /// It does not, as it turns out — it writes <c>PhotometricInterpretation = 1</c>
    /// (BlackIsZero), so a black pixel is stored as bit 0. libtiff's fax coder
    /// does not consult that tag: it codes bit 0 as a <em>white</em> run either
    /// way, and a reader is expected to apply the photometric afterwards. So
    /// under BlackIsZero the coded runs come out inverted with respect to T.88,
    /// and <see cref="Encode"/> flips its input to cancel it.
    /// </para>
    /// <para>
    /// Probed once rather than assumed, so the day ImageMagick changes its mind
    /// this harness follows it instead of quietly producing negatives.
    /// </para>
    /// </summary>
    private static readonly Lazy<bool> WritesMinIsWhite = new(() =>
    {
        // A pattern that is not symmetric under inversion, so the probe can only
        // be read one way.
        var probe = new byte[8 * 8];
        probe[0] = 1;
        return Write(probe, 8, 8, invert: false).MinIsWhite;
    });

    /// <summary>
    /// Encodes a bilevel raster as Group 4 and returns the coded bytes.
    /// <paramref name="pixels"/> is one byte per pixel with <b>1 = black</b>, and
    /// the result codes the same thing: a decoded 1 bit is a black pixel, exactly
    /// as T.88 §6.2.6 expects.
    /// </summary>
    public static Encoded Encode(byte[] pixels, int width, int height) =>
        Write(pixels, width, height, invert: !WritesMinIsWhite.Value);

    private static Encoded Write(byte[] pixels, int width, int height, bool invert)
    {
        // ImageMagick reads 8-bit grey, so black becomes 0 and white 255; the
        // bilevel conversion below then has nothing to threshold.
        var grey = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++) grey[i] = (pixels[i] != 0) != invert ? (byte)0 : (byte)255;

        var settings = new MagickReadSettings
        {
            Format = MagickFormat.Gray,
            Width = (uint)width,
            Height = (uint)height,
            Depth = 8,
        };

        using var image = new MagickImage(grey, settings);
        image.ColorType = ColorType.Bilevel;
        image.Depth = 1;
        image.Format = MagickFormat.Tiff;
        image.Settings.Compression = CompressionMethod.Group4;

        return Parse(image.ToByteArray(), width, height);
    }

    /// <summary>
    /// Minimal TIFF reader — just enough to find the coded strips and prove they
    /// really are Group 4. <c>Sdcb.SimdCodecs.Tiff</c>'s reader is not usable here: it
    /// hands back pixels when what this harness wants is the untouched codestream,
    /// and it decodes Group 4 itself, so it would not be an independent party.
    /// </summary>
    private static Encoded Parse(byte[] tiff, int expectedWidth, int expectedHeight)
    {
        var little = tiff[0] == 'I';
        if (!little && tiff[0] != 'M') throw new InvalidDataException("Not a TIFF.");

        var ifd = (int)ReadUInt32(tiff, 4, little);
        var count = ReadUInt16(tiff, ifd, little);

        var fields = new Dictionary<ushort, (ushort Type, uint Count, int ValueOffset)>();
        for (var i = 0; i < count; i++)
        {
            var entry = ifd + 2 + i * 12;
            fields[ReadUInt16(tiff, entry, little)] =
                (ReadUInt16(tiff, entry + 2, little), ReadUInt32(tiff, entry + 4, little), entry + 8);
        }

        var width = (int)Scalar(256);
        var height = (int)Scalar(257);
        var compression = Scalar(259);
        var photometric = Scalar(262);
        var fillOrder = fields.ContainsKey(266) ? Scalar(266) : 1;
        var rowsPerStrip = fields.ContainsKey(278) ? (int)Scalar(278) : height;
        var t6Options = fields.ContainsKey(293) ? Scalar(293) : 0;

        // Every one of these would silently change what the strips mean, so they
        // are assertions rather than handled cases. If ImageMagick ever stops
        // honouring the Group 4 request, this is where it shows up — rather than
        // as a mystifying decode failure.
        if (compression != 4) throw new InvalidDataException($"ImageMagick wrote TIFF compression {compression}, not Group 4.");
        if (fillOrder != 1) throw new InvalidDataException($"TIFF FillOrder {fillOrder} is not MSB-first.");
        if (t6Options != 0) throw new InvalidDataException($"TIFF T6Options {t6Options} enables uncompressed mode.");
        if (width != expectedWidth || height != expectedHeight)
            throw new InvalidDataException($"ImageMagick wrote {width}x{height}, expected {expectedWidth}x{expectedHeight}.");

        // A TIFF strip is an independently coded block — each one restarts from
        // its own imaginary white reference line — so more than one would be
        // several MMR streams rather than the single block JBIG2 and PDF both
        // use. ImageMagick never emits more for fax compression, and this
        // harness would be testing something else if it did.
        if (rowsPerStrip < height)
            throw new InvalidDataException($"ImageMagick split the Group 4 image into strips of {rowsPerStrip} rows.");

        var offsets = Array(273);
        var counts = Array(279);
        if (offsets.Length != 1)
            throw new InvalidDataException($"Expected one Group 4 strip, got {offsets.Length}.");

        return new Encoded(width, height, MinIsWhite: photometric == 0,
            tiff[(int)offsets[0]..(int)(offsets[0] + counts[0])]);

        uint Scalar(ushort tag) => Array(tag)[0];

        uint[] Array(ushort tag)
        {
            var (type, n, valueOffset) = fields[tag];
            var size = type == 3 ? 2 : 4;   // SHORT or LONG; nothing here uses another type

            // A value of four bytes or fewer is stored inline in the entry.
            var at = n * size <= 4 ? valueOffset : (int)ReadUInt32(tiff, valueOffset, little);

            var values = new uint[n];
            for (var i = 0; i < n; i++)
                values[i] = size == 2 ? ReadUInt16(tiff, at + i * 2, little) : ReadUInt32(tiff, at + i * 4, little);

            return values;
        }
    }

    private static ushort ReadUInt16(byte[] data, int offset, bool little) => little
        ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset))
        : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));

    private static uint ReadUInt32(byte[] data, int offset, bool little) => little
        ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset))
        : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
}
