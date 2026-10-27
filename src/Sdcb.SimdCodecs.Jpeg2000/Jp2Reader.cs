using System;
using System.Buffers.Binary;
using System.IO;

namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// The enumerated colour spaces of a JP2 <c>colr</c> box (T.800 Table I.10). Part 1 defines these
/// three; any other number is kept as it was read, so a caller can see what it was and refuse it.
/// </summary>
public enum Jp2EnumeratedColourSpace
{
    /// <summary>sRGB (IEC 61966-2-1).</summary>
    Srgb = 16,

    /// <summary>Greyscale, with sRGB's transfer curve.</summary>
    Greyscale = 17,

    /// <summary>sYCC: YCbCr derived from sRGB, which still has to be converted to RGB to be shown.</summary>
    Sycc = 18,
}

/// <summary>
/// What a JP2 file's first <c>colr</c> box says the decoded components mean (T.800 I.5.3.3).
/// <para>
/// Reported, never applied. The codestream's own component transform is undone by the decoder,
/// because that is part of how the samples were coded; this is a statement about what the samples
/// then ARE, and the right response to it depends on the caller. A PDF reader in particular has its
/// own rule (ISO 32000-2 8.9.5, the image dictionary's <c>/ColorSpace</c>): when the dictionary
/// names a colour space, this specification is ignored, and only when it names none is this one
/// used. Only the caller holds the dictionary, so only the caller can apply that.
/// </para>
/// </summary>
/// <param name="Method">METH: 1 for an enumerated colour space, 2 for a restricted ICC profile.</param>
/// <param name="EnumeratedColourSpace">The EnumCS value when <paramref name="Method"/> is 1.</param>
/// <param name="IccProfile">The profile bytes when <paramref name="Method"/> is 2.</param>
public sealed record Jp2Colour(int Method, Jp2EnumeratedColourSpace? EnumeratedColourSpace, byte[]? IccProfile);

/// <summary>Where a JP2 file's codestream is, and what its header said about colour.</summary>
internal readonly record struct Jp2Layout(int CodestreamStart, int CodestreamLength, Jp2Colour? Colour);

/// <summary>
/// Reads the JP2 file format of T.800 Annex I far enough to find the codestream and its colour
/// specification: the signature and file type boxes, the header superbox with its image header and
/// colour boxes, and the contiguous codestream box.
/// <para>
/// <b>What is refused, and why each is a refusal rather than something skipped.</b> The palette
/// (<c>pclr</c>), component mapping (<c>cmap</c>) and channel definition (<c>cdef</c>) boxes each
/// change what the decoded components mean: a palette turns one component of indices into three of
/// colour, a mapping reorders them, a channel definition says one is alpha. A reader that skipped
/// them would hand back samples whose meaning it had silently got wrong, which is the failure this
/// decoder exists to avoid. None of the JP2 images found in real PDFs carries one.
/// </para>
/// <para>
/// The image header's own width, height and component count are not checked against the
/// codestream's. Where they disagree the codestream is what decodes, and refusing a file over the
/// header's mistake would refuse images other readers show.
/// </para>
/// </summary>
internal static class Jp2Reader
{
    /// <summary>The signature box, which must be the file's first twelve bytes (I.5.1).</summary>
    private static ReadOnlySpan<byte> Signature =>
        [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    private const uint FileType = 0x66747970;        // 'ftyp'
    private const uint Header = 0x6A703268;          // 'jp2h'
    private const uint ImageHeader = 0x69686472;     // 'ihdr'
    private const uint ColourSpecification = 0x636F6C72; // 'colr'
    private const uint Palette = 0x70636C72;         // 'pclr'
    private const uint ComponentMapping = 0x636D6170; // 'cmap'
    private const uint ChannelDefinition = 0x63646566; // 'cdef'
    private const uint Codestream = 0x6A703263;      // 'jp2c'

    /// <summary>True when <paramref name="data"/> begins with the JP2 signature box.</summary>
    public static bool LooksLikeJp2(ReadOnlySpan<byte> data) =>
        data.Length >= Signature.Length && data[..Signature.Length].SequenceEqual(Signature);

    /// <summary>Finds the codestream and the colour specification.</summary>
    /// <exception cref="InvalidDataException">The box structure is malformed or has no codestream.</exception>
    /// <exception cref="NotSupportedException">A box changes the meaning of the components in a way not implemented.</exception>
    public static Jp2Layout Read(ReadOnlySpan<byte> data)
    {
        if (!LooksLikeJp2(data))
            throw new InvalidDataException("JPEG 2000: not a JP2 file — it does not begin with the signature box.");

        var offset = Signature.Length;
        var sawFileType = false;
        var sawHeader = false;
        Jp2Colour? colour = null;

        while (offset < data.Length)
        {
            var (type, bodyStart, bodyLength) = ReadBox(data, offset);
            var body = data.Slice(bodyStart, bodyLength);

            switch (type)
            {
                case FileType:
                    // I.5.2: the brand list must name 'jp2 '. A file that does not claim Part 1
                    // compatibility is Part 2 (JPX) or something else, and is not this decoder's.
                    if (!ListsJp2Brand(body))
                        throw new NotSupportedException(
                            "JPEG 2000: the file type box does not list the 'jp2 ' brand, so this is not a " +
                            "Part 1 JP2 file (JPX, Part 2, is refused).");
                    sawFileType = true;
                    break;

                case Header:
                    if (!sawFileType)
                        throw new InvalidDataException("JPEG 2000: the JP2 header box comes before the file type box.");
                    colour = ReadHeader(body);
                    sawHeader = true;
                    break;

                case Codestream:
                    if (!sawHeader)
                        throw new InvalidDataException("JPEG 2000: the codestream box comes before the JP2 header box.");

                    // The first codestream is the image (I.5.4); anything after it is not read.
                    return new Jp2Layout(bodyStart, bodyLength, colour);
            }

            // Every other top-level box (resolution, XML, UUID, IPR) is metadata about the image,
            // not part of decoding it, and is skipped.
            offset = bodyStart + bodyLength;
        }

        throw new InvalidDataException("JPEG 2000: the JP2 file has no contiguous codestream box.");
    }

    /// <summary>
    /// The header superbox: the image header first (I.5.3.1), then at least one colour
    /// specification, of which only the first counts (I.5.3.3).
    /// </summary>
    private static Jp2Colour? ReadHeader(ReadOnlySpan<byte> header)
    {
        Jp2Colour? colour = null;
        var offset = 0;
        var first = true;

        while (offset < header.Length)
        {
            var (type, bodyStart, bodyLength) = ReadBox(header, offset);
            var body = header.Slice(bodyStart, bodyLength);

            if (first && type != ImageHeader)
                throw new InvalidDataException("JPEG 2000: the JP2 header box does not begin with an image header box.");
            first = false;

            switch (type)
            {
                case ImageHeader:
                    // HEIGHT(4) WIDTH(4) NC(2) BPC(1) C(1) UnkC(1) IPR(1). C is the compression type,
                    // and 7 is the only value Part 1 defines: JPEG 2000 itself.
                    if (body.Length < 14)
                        throw new InvalidDataException("JPEG 2000: the image header box is too short.");
                    if (body[11] != 7)
                        throw new NotSupportedException(
                            $"JPEG 2000: the image header names compression type {body[11]}; only 7, JPEG 2000 " +
                            "itself, is defined by Part 1.");
                    break;

                case ColourSpecification:
                    colour ??= ReadColour(body);
                    break;

                case Palette:
                    throw new NotSupportedException(
                        "JPEG 2000: the JP2 file has a palette box (pclr), which turns a component of " +
                        "indices into colours. It is not implemented, and ignoring it would return the " +
                        "indices as if they were the picture.");

                case ComponentMapping:
                    throw new NotSupportedException(
                        "JPEG 2000: the JP2 file has a component mapping box (cmap), which is not implemented.");

                case ChannelDefinition:
                    throw new NotSupportedException(
                        "JPEG 2000: the JP2 file has a channel definition box (cdef), which can say a " +
                        "component is alpha or reorder the colour channels. It is not implemented.");
            }

            offset = bodyStart + bodyLength;
        }

        if (first) throw new InvalidDataException("JPEG 2000: the JP2 header box is empty.");
        return colour;
    }

    /// <summary>
    /// A colour specification box (I.5.3.3): METH(1) PREC(1) APPROX(1), then EnumCS(4) for an
    /// enumerated colour space, or the ICC profile for a restricted one.
    /// </summary>
    private static Jp2Colour ReadColour(ReadOnlySpan<byte> body)
    {
        if (body.Length < 3)
            throw new InvalidDataException("JPEG 2000: the colour specification box is too short.");

        var method = body[0];
        switch (method)
        {
            case 1:
                if (body.Length < 7)
                    throw new InvalidDataException("JPEG 2000: the colour specification box is too short for EnumCS.");
                return new Jp2Colour(method, (Jp2EnumeratedColourSpace)BinaryPrimitives.ReadUInt32BigEndian(body[3..]), null);

            case 2:
                return new Jp2Colour(method, null, body[3..].ToArray());

            default:
                // Part 2 adds methods for any ICC profile and vendor colour; Part 1 defines two.
                throw new NotSupportedException(
                    $"JPEG 2000: colour specification method {method} is not defined by Part 1 JP2.");
        }
    }

    private static bool ListsJp2Brand(ReadOnlySpan<byte> body)
    {
        // BR(4) MinV(4), then the compatibility list CL, four bytes an entry.
        for (var i = 8; i + 4 <= body.Length; i += 4)
        {
            if (body[i] == (byte)'j' && body[i + 1] == (byte)'p' && body[i + 2] == (byte)'2' && body[i + 3] == (byte)' ')
                return true;
        }

        return false;
    }

    /// <summary>
    /// One box header (I.4): LBox(4) and TBox(4), then XLBox(8) when LBox is 1. An LBox of 0 means
    /// the box runs to the end of what contains it; any other value below 8 cannot hold even its
    /// own header.
    /// </summary>
    private static (uint Type, int BodyStart, int BodyLength) ReadBox(ReadOnlySpan<byte> data, int offset)
    {
        if (data.Length - offset < 8)
            throw new InvalidDataException("JPEG 2000: a JP2 box header runs past the end of the file.");

        long length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        var type = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
        var headerLength = 8;

        if (length == 1)
        {
            if (data.Length - offset < 16)
                throw new InvalidDataException("JPEG 2000: a JP2 box's extended length runs past the end of the file.");
            length = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(offset + 8)..]);
            headerLength = 16;
        }
        else if (length == 0)
        {
            length = data.Length - offset;
        }

        // A negative length here is a 64-bit XLBox above long.MaxValue, which is as malformed as one
        // too short for its own header or longer than the file.
        if (length < headerLength || length > data.Length - offset)
            throw new InvalidDataException(
                $"JPEG 2000: a JP2 box at offset {offset} declares {length} bytes, which does not fit.");

        return (type, offset + headerLength, (int)length - headerLength);
    }
}
