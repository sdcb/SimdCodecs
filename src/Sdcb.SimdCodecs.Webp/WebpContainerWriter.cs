using System.Buffers.Binary;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// Writes the RIFF container of RFC 9649 Section 2. A still image without metadata or a separate
/// alpha chunk is the simple format (<c>RIFF WEBP</c> + one <c>VP8 </c> / <c>VP8L</c> chunk); anything
/// else is the extended format, in the chunk order of Section 2.7: <c>VP8X</c>, <c>ICCP</c>,
/// <c>ANIM</c>, the image data (<c>ALPH</c> + <c>VP8 </c>, <c>VP8L</c>, or the <c>ANMF</c> frames), then
/// <c>EXIF</c> and <c>XMP </c>.
/// </summary>
internal ref struct WebpContainerWriter
{
    // VP8X flags (Section 2.7): ICC profile, alpha, Exif, XMP, animation.
    internal const byte IccFlag = 0x20, AlphaFlag = 0x10, ExifFlag = 0x08, XmpFlag = 0x04, AnimationFlag = 0x02;

    /// <summary>The RIFF size field is 32 bits; the file (its payload plus 8) must fit an array too.</summary>
    private const long MaxRiffPayload = int.MaxValue - 64;

    private readonly Span<byte> _file;
    private int _position;

    private WebpContainerWriter(Span<byte> file)
    {
        _file = file;
    }

    /// <summary>A still image: <paramref name="image"/> is <c>VP8L</c> data, or <c>VP8 </c> data with an optional <c>ALPH</c> payload.</summary>
    public static byte[] Still(int width, int height, bool hasAlpha, ReadOnlySpan<byte> fourCc, ReadOnlySpan<byte> image,
        ReadOnlySpan<byte> alph, WebpMetadata metadata)
    {
        bool extended = metadata.Any || !alph.IsEmpty;
        long size = 4 + ChunkSize(image.Length) + (alph.IsEmpty ? 0 : ChunkSize(alph.Length)) +
                    (extended ? ChunkSize(10) + MetadataSize(metadata) : 0);
        byte[] file = Allocate(size);
        var w = new WebpContainerWriter(file);
        w.Header((uint)size);
        if (extended)
        {
            w.Vp8X(width, height, (hasAlpha ? AlphaFlag : (byte)0) | metadata.Flags);
            w.Chunk("ICCP"u8, metadata.IccProfile);
        }

        if (!alph.IsEmpty)
        {
            w.Chunk("ALPH"u8, alph);
        }

        w.Chunk(fourCc, image);
        if (extended)
        {
            w.Chunk("EXIF"u8, metadata.Exif);
            w.Chunk("XMP "u8, metadata.Xmp);
        }

        return file;
    }

    /// <summary>
    /// An animation: the canvas, the <c>ANIM</c> parameters (background colour as stored, B, G, R, A
    /// in memory, and the loop count) and every frame's complete <c>ANMF</c> payload.
    /// </summary>
    public static byte[] Animation(int canvasWidth, int canvasHeight, bool hasAlpha, uint backgroundBgra, int loopCount,
        IReadOnlyList<byte[]> frames, WebpMetadata metadata)
    {
        long size = 4 + ChunkSize(10) + ChunkSize(6) + MetadataSize(metadata);
        foreach (byte[] f in frames)
        {
            size += ChunkSize(f.Length);
        }

        byte[] file = Allocate(size);
        var w = new WebpContainerWriter(file);
        w.Header((uint)size);
        w.Vp8X(canvasWidth, canvasHeight, AnimationFlag | (hasAlpha ? AlphaFlag : 0) | metadata.Flags);
        w.Chunk("ICCP"u8, metadata.IccProfile);
        Span<byte> anim = stackalloc byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(anim, backgroundBgra);
        BinaryPrimitives.WriteUInt16LittleEndian(anim[4..], (ushort)loopCount);
        w.Chunk("ANIM"u8, anim);
        foreach (byte[] f in frames)
        {
            w.Chunk("ANMF"u8, f);
        }

        w.Chunk("EXIF"u8, metadata.Exif);
        w.Chunk("XMP "u8, metadata.Xmp);
        return file;
    }

    /// <summary>The bytes of a chunk with this payload: header, payload and the pad byte to an even size.</summary>
    public static long ChunkSize(long payload) => 8 + payload + (payload & 1);

    private static long MetadataSize(WebpMetadata m) =>
        (m.IccProfile.IsEmpty ? 0 : ChunkSize(m.IccProfile.Length)) +
        (m.Exif.IsEmpty ? 0 : ChunkSize(m.Exif.Length)) +
        (m.Xmp.IsEmpty ? 0 : ChunkSize(m.Xmp.Length));

    private static byte[] Allocate(long riffPayload)
    {
        if (riffPayload > MaxRiffPayload)
        {
            throw new ArgumentException($"The WebP file would be {riffPayload + 8} bytes, more than this encoder can write.");
        }

        return new byte[riffPayload + 8];
    }

    private void Header(uint riffPayload)
    {
        "RIFF"u8.CopyTo(_file);
        BinaryPrimitives.WriteUInt32LittleEndian(_file[4..], riffPayload);
        "WEBP"u8.CopyTo(_file[8..]);
        _position = 12;
    }

    private void Vp8X(int width, int height, int flags)
    {
        Span<byte> p = stackalloc byte[10];
        p.Clear();
        p[0] = (byte)flags;
        WriteUInt24(p[4..], width - 1);
        WriteUInt24(p[7..], height - 1);
        Chunk("VP8X"u8, p);
    }

    /// <summary>Writes one chunk; an empty payload writes nothing (only used for optional metadata).</summary>
    private void Chunk(scoped ReadOnlySpan<byte> fourCc, scoped ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return;
        }

        // The array is zeroed, so the pad byte is already 0.
        Span<byte> span = _file[_position..];
        fourCc.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)payload.Length);
        payload.CopyTo(span[8..]);
        _position += 8 + payload.Length + (payload.Length & 1);
    }

    internal static void WriteUInt24(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }
}

/// <summary>The metadata chunks of an extended WebP file; empty spans are left out.</summary>
internal readonly ref struct WebpMetadata(ReadOnlySpan<byte> iccProfile, ReadOnlySpan<byte> exif, ReadOnlySpan<byte> xmp)
{
    public ReadOnlySpan<byte> IccProfile { get; } = iccProfile;
    public ReadOnlySpan<byte> Exif { get; } = exif;
    public ReadOnlySpan<byte> Xmp { get; } = xmp;

    public bool Any => !IccProfile.IsEmpty || !Exif.IsEmpty || !Xmp.IsEmpty;

    public int Flags => (IccProfile.IsEmpty ? 0 : WebpContainerWriter.IccFlag) | (Exif.IsEmpty ? 0 : WebpContainerWriter.ExifFlag) |
                        (Xmp.IsEmpty ? 0 : WebpContainerWriter.XmpFlag);
}
