using System.Buffers.Binary;
using Sdcb.SimdCodecs.Jbig2;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Builds synthetic JBIG2 segment streams and files for the decoder tests — the
/// write side of T.88 §7, plus an arithmetic generic-region encoder built on
/// <see cref="Jbig2MqEncoder"/>.
/// <para>
/// Nothing here is shipped: JBIG2 encoding is a stated non-goal. It exists so
/// the tests can drive cases no third-party fixture reaches — every template,
/// every AT placement, TPGDON either way — and so they run in CI with no
/// external dependency. Real third-party bytes are covered separately by the
/// committed jbig2enc fixtures in <c>Jbig2EncoderFixtureTests</c>.
/// </para>
/// <para>
/// <b>What a round-trip through this does and does not prove.</b> The region
/// encoder deliberately calls the shipped
/// <see cref="GenericRegionDecoder.Context"/> to form its contexts, so an
/// encode/decode round-trip validates the MQ integration, the scan order, and
/// the TPGDON logic — but it cannot validate the context templates themselves,
/// because both sides share any mistake made there. That is what
/// <c>Jbig2OracleTests</c> is for: the same streams pushed through jbig2dec,
/// which has its own templates. The one-hot tests in
/// <c>Jbig2GenericRegionTests</c> pin the literal bit numbering on top of that.
/// </para>
/// </summary>
internal static class Jbig2StreamBuilder
{
    /// <summary>Arithmetically encodes a bitmap as generic-region coded data (T.88 §6.2).</summary>
    public static byte[] EncodeGenericRegion(Jbig2Bitmap bitmap, int template, bool typicalPrediction, sbyte[] at)
    {
        var encoder = new Jbig2MqEncoder();
        var contexts = new byte[1 << GenericRegionDecoder.ContextBits(template)];
        var sltpContext = GenericRegionDecoder.TypicalPredictionContext(template);
        var ltp = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            if (typicalPrediction)
            {
                // A row is "typical" when it repeats the one above; row 0 counts
                // only if it is blank, because the decoder has nothing above it
                // to copy and leaves the row white.
                var typical = y == 0 ? RowIsBlank(bitmap, 0) : RowsMatch(bitmap, y, y - 1);
                var toggle = typical != (ltp == 1);

                encoder.Encode(contexts, sltpContext, toggle ? 1 : 0);
                if (toggle) ltp ^= 1;
                if (ltp == 1) continue;
            }

            for (var x = 0; x < bitmap.Width; x++)
            {
                var context = GenericRegionDecoder.Context(bitmap, x, y, template, at);
                encoder.Encode(contexts, context, bitmap.Data[y * bitmap.Width + x]);
            }
        }

        return encoder.Flush();
    }

    /// <summary>A complete immediate-generic-region segment data part (T.88 §7.4.6).</summary>
    public static byte[] GenericRegionSegment(
        Jbig2Bitmap bitmap,
        int x = 0,
        int y = 0,
        CombinationOperator op = CombinationOperator.Or,
        int template = 0,
        bool typicalPrediction = false,
        sbyte[]? at = null)
    {
        at ??= [.. GenericRegionDecoder.NominalAt(template)];

        var body = new List<byte>();
        WriteUInt32(body, (uint)bitmap.Width);
        WriteUInt32(body, (uint)bitmap.Height);
        WriteUInt32(body, (uint)x);
        WriteUInt32(body, (uint)y);
        body.Add((byte)op);

        // Generic region flags: MMR=0, GBTEMPLATE in bits 1-2, TPGDON in bit 3.
        body.Add((byte)((template << 1) | (typicalPrediction ? 0x08 : 0)));

        var atPixels = template == 0 ? 4 : 1;
        for (var i = 0; i < atPixels; i++)
        {
            body.Add((byte)at[i * 2]);
            body.Add((byte)at[i * 2 + 1]);
        }

        body.AddRange(EncodeGenericRegion(bitmap, template, typicalPrediction, at));
        return [.. body];
    }

    /// <summary>
    /// An immediate-generic-region segment data part carrying already-MMR-coded
    /// data (T.88 §7.4.6 with <c>MMR = 1</c>).
    /// <para>
    /// There is no MMR encoder here and there will not be one — the coded bytes
    /// come from <see cref="Group4Tiff"/>, i.e. from libtiff. This method only
    /// wraps them in the segment envelope, which is what lets the very same
    /// third-party bytes be pushed through the whole segment layer and compared
    /// against jbig2dec.
    /// </para>
    /// </summary>
    public static byte[] MmrGenericRegionSegment(
        int width,
        int height,
        byte[] coded,
        int x = 0,
        int y = 0,
        CombinationOperator op = CombinationOperator.Or)
    {
        var body = new List<byte>();
        WriteUInt32(body, (uint)width);
        WriteUInt32(body, (uint)height);
        WriteUInt32(body, (uint)x);
        WriteUInt32(body, (uint)y);
        body.Add((byte)op);

        // Generic region flags: MMR=1. §7.4.6.3 makes the AT pixel list
        // conditional on MMR being 0, so none follows.
        body.Add(0x01);

        body.AddRange(coded);
        return [.. body];
    }

    /// <summary>
    /// Arithmetically encodes a bitmap as refinement-region coded data against
    /// <paramref name="reference"/> (T.88 §6.3).
    /// </summary>
    /// <remarks>
    /// The TPGRON logic here has to mirror the decoder's decision for decision,
    /// including which pixels get skipped — a row may only be marked "typical" if
    /// every pixel the decoder would predict from a uniform reference
    /// neighbourhood really does match. Get that wrong and the two sides
    /// desynchronise mid-row rather than producing a visibly wrong pixel.
    /// </remarks>
    public static byte[] EncodeRefinementRegion(
        Jbig2Bitmap bitmap,
        Jbig2Bitmap reference,
        int template,
        bool typicalPrediction,
        sbyte[] at,
        int dx = 0,
        int dy = 0)
    {
        var encoder = new Jbig2MqEncoder();
        var contexts = new byte[1 << RefinementRegionDecoder.ContextBits(template)];
        var sltpContext = RefinementRegionDecoder.TypicalPredictionContext(template);
        var ltp = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            if (typicalPrediction)
            {
                var predictable = true;
                for (var x = 0; x < bitmap.Width && predictable; x++)
                    if (RefinementRegionDecoder.IsTypical(reference, x - dx, y - dy, out var v))
                        predictable = bitmap.Data[y * bitmap.Width + x] == v;

                var toggle = predictable != (ltp == 1);
                encoder.Encode(contexts, sltpContext, toggle ? 1 : 0);
                if (toggle) ltp ^= 1;
            }

            for (var x = 0; x < bitmap.Width; x++)
            {
                if (ltp == 1 && RefinementRegionDecoder.IsTypical(reference, x - dx, y - dy, out _)) continue;

                var context = RefinementRegionDecoder.Context(
                    bitmap, x, y, reference, x - dx, y - dy, template, at);
                encoder.Encode(contexts, context, bitmap.Data[y * bitmap.Width + x]);
            }
        }

        return encoder.Flush();
    }

    /// <summary>A complete refinement-region segment data part (T.88 §7.4.7).</summary>
    public static byte[] RefinementRegionSegment(
        Jbig2Bitmap bitmap,
        Jbig2Bitmap reference,
        int x = 0,
        int y = 0,
        CombinationOperator op = CombinationOperator.Or,
        int template = 0,
        bool typicalPrediction = false,
        sbyte[]? at = null)
    {
        at ??= [.. RefinementRegionDecoder.NominalAt];

        var body = new List<byte>();
        WriteUInt32(body, (uint)bitmap.Width);
        WriteUInt32(body, (uint)bitmap.Height);
        WriteUInt32(body, (uint)x);
        WriteUInt32(body, (uint)y);
        body.Add((byte)op);

        // Refinement region flags: GRTEMPLATE in bit 0, TPGRON in bit 1.
        body.Add((byte)(template | (typicalPrediction ? 0x02 : 0)));

        // §7.4.7.3: the AT pixel list is present only for GRTEMPLATE 0.
        if (template == 0)
            for (var i = 0; i < 4; i++) body.Add((byte)at[i]);

        body.AddRange(EncodeRefinementRegion(bitmap, reference, template, typicalPrediction, at));
        return [.. body];
    }

    /// <summary>A page information segment data part (T.88 §7.4.8).</summary>
    /// <param name="allowOperatorOverride">
    /// §7.4.8.5 bit 6: whether a region may use a combination operator other than
    /// the page default. Needed by anything that composites with REPLACE.
    /// </param>
    public static byte[] PageInformation(
        int width,
        int height,
        bool defaultPixelBlack = false,
        bool stripedUnknownHeight = false,
        bool allowOperatorOverride = false)
    {
        var body = new List<byte>();
        WriteUInt32(body, (uint)width);
        WriteUInt32(body, stripedUnknownHeight ? uint.MaxValue : (uint)height);
        WriteUInt32(body, 0);   // X resolution, unspecified
        WriteUInt32(body, 0);   // Y resolution, unspecified
        body.Add((byte)((defaultPixelBlack ? 0x04 : 0x00) | (allowOperatorOverride ? 0x40 : 0x00)));
        body.Add(0);            // striping information, high byte
        body.Add(0);            // striping information, low byte
        return [.. body];
    }

    /// <summary>An end-of-stripe segment data part (T.88 §7.4.10): the row number of the stripe's last row.</summary>
    public static byte[] EndOfStripe(int lastRow)
    {
        var body = new List<byte>();
        WriteUInt32(body, (uint)lastRow);
        return [.. body];
    }

    /// <summary>
    /// Wraps a data part in a segment header (T.88 §7.2). Only the short
    /// referred-to form is emitted here; the long form is exercised by
    /// hand-assembled bytes in <c>Jbig2SegmentTests</c>.
    /// </summary>
    public static byte[] Segment(uint number, SegmentType type, uint page, byte[] data, uint[]? referredTo = null)
    {
        referredTo ??= [];
        if (referredTo.Length > 4) throw new ArgumentException("Short form holds at most 4 referred-to segments.", nameof(referredTo));

        var header = new List<byte>();
        WriteUInt32(header, number);

        var largePage = page > 255;
        header.Add((byte)((int)type | (largePage ? 0x40 : 0)));
        header.Add((byte)(referredTo.Length << 5));

        var referredSize = number <= 256 ? 1 : number <= 65536 ? 2 : 4;
        foreach (var reference in referredTo)
        {
            switch (referredSize)
            {
                case 1: header.Add((byte)reference); break;
                case 2: WriteUInt16(header, (ushort)reference); break;
                default: WriteUInt32(header, reference); break;
            }
        }

        if (largePage) WriteUInt32(header, page);
        else header.Add((byte)page);

        WriteUInt32(header, (uint)data.Length);

        return [.. header, .. data];
    }

    /// <summary>Concatenates segments into one embedded / sequential stream.</summary>
    public static byte[] Stream(params byte[][] segments) => [.. segments.SelectMany(s => s)];

    /// <summary>
    /// A standalone <c>.jb2</c> file (T.88 Annex D) in the sequential
    /// organization: header, then each segment's header followed immediately by
    /// its data.
    /// </summary>
    public static byte[] SequentialFile(params byte[][] segments)
    {
        var file = new List<byte>();
        file.AddRange(Jbig2Decoder.FileSignature);
        file.Add(0x01);           // sequential organization, page count known
        WriteUInt32(file, 1);     // one page
        foreach (var segment in segments) file.AddRange(segment);
        return [.. file];
    }

    /// <summary>
    /// A standalone <c>.jb2</c> file in the random-access organization: every
    /// segment header first, then every data part in the same order. Takes the
    /// headers and data parts already split, since the split is the whole point.
    /// </summary>
    public static byte[] RandomAccessFile(params (byte[] Header, byte[] Data)[] segments)
    {
        var file = new List<byte>();
        file.AddRange(Jbig2Decoder.FileSignature);
        file.Add(0x00);           // random-access organization, page count known
        WriteUInt32(file, 1);     // one page
        foreach (var segment in segments) file.AddRange(segment.Header);
        foreach (var segment in segments) file.AddRange(segment.Data);
        return [.. file];
    }

    /// <summary>Splits a sequential segment into its header and data parts, for <see cref="RandomAccessFile"/>.</summary>
    public static (byte[] Header, byte[] Data) Split(uint number, SegmentType type, uint page, byte[] data)
    {
        var whole = Segment(number, type, page, data);
        return (whole[..^data.Length], data);
    }

    /// <summary>Renders an ASCII picture into a bitmap: '#' is black (1), anything else white (0).</summary>
    public static Jbig2Bitmap FromRows(params string[] rows)
    {
        var bitmap = new Jbig2Bitmap(rows[0].Length, rows.Length);
        for (var y = 0; y < rows.Length; y++)
            for (var x = 0; x < rows[y].Length; x++)
                bitmap.Data[y * bitmap.Width + x] = rows[y][x] == '#' ? (byte)1 : (byte)0;

        return bitmap;
    }

    /// <summary>Renders a bitmap back to ASCII, so a failing assertion shows a picture rather than 400 bytes.</summary>
    public static string[] ToRows(int width, int height, ReadOnlySpan<byte> bits)
    {
        var rows = new string[height];
        for (var y = 0; y < height; y++)
        {
            var row = new char[width];
            for (var x = 0; x < width; x++) row[x] = bits[y * width + x] != 0 ? '#' : '.';
            rows[y] = new string(row);
        }

        return rows;
    }

    private static bool RowsMatch(Jbig2Bitmap bitmap, int a, int b) =>
        bitmap.Data.AsSpan(a * bitmap.Width, bitmap.Width)
            .SequenceEqual(bitmap.Data.AsSpan(b * bitmap.Width, bitmap.Width));

    private static bool RowIsBlank(Jbig2Bitmap bitmap, int y) =>
        !bitmap.Data.AsSpan(y * bitmap.Width, bitmap.Width).ContainsAnyExcept((byte)0);

    private static void WriteUInt16(List<byte> target, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        target.AddRange(buffer);
    }

    private static void WriteUInt32(List<byte> target, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        target.AddRange(buffer);
    }
}
