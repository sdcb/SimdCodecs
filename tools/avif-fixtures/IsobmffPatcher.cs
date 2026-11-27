using System.Buffers.Binary;
using System.Text;

namespace AvifFixtures;

/// <summary>
/// Minimal ISO/IEC 14496-12 &amp; 23008-12 ISOBMFF box builder/patcher for container variants that
/// libavif's encoder API does not emit directly: <c>iovl</c> overlay, <c>iden</c> derived chain,
/// <c>thmb</c> thumbnail reference, and stripping the <c>colr</c> box so HDR transfer is VUI-only.
/// </summary>
internal static class IsobmffPatcher
{
    internal static byte[] Box(string fourCC, ReadOnlySpan<byte> payload)
    {
        var result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)result.Length);
        Encoding.ASCII.GetBytes(fourCC, result.AsSpan(4, 4));
        payload.CopyTo(result.AsSpan(8));
        return result;
    }

    internal static byte[] FullBox(string fourCC, byte version, uint flags, ReadOnlySpan<byte> payload)
    {
        var full = new byte[4 + payload.Length];
        full[0] = version;
        full[1] = (byte)((flags >> 16) & 0xFF);
        full[2] = (byte)((flags >> 8) & 0xFF);
        full[3] = (byte)(flags & 0xFF);
        payload.CopyTo(full.AsSpan(4));
        return Box(fourCC, full);
    }

    internal static byte[] Concat(params ReadOnlySpan<byte[]> chunks)
    {
        int total = 0;
        foreach (var c in chunks) total += c.Length;
        var dst = new byte[total];
        int off = 0;
        foreach (var c in chunks)
        {
            c.CopyTo(dst, off);
            off += c.Length;
        }
        return dst;
    }

    /// <summary>
    /// Extracts the primary <c>av01</c> item's raw AV1 OBU payload and its <c>av1C</c> box payload
    /// from a single-image AVIF file produced by libavif.
    /// </summary>
    internal static (byte[] Obu, byte[] Av1cPayload, byte[] PixiPayload) ExtractPrimaryAv1(ReadOnlySpan<byte> avif)
    {
        // Locate meta box, parse iloc for item 1, and extract av1C + pixi from ipco.
        int metaStart = -1, metaEnd = -1;
        for (int pos = 0; pos + 8 <= avif.Length;)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(avif.Slice(pos, 4));
            string type = Encoding.ASCII.GetString(avif.Slice(pos + 4, 4));
            int end = size == 0 ? avif.Length : pos + (int)size;
            if (type == "meta") { metaStart = pos + 12; metaEnd = end; break; }
            pos = end;
        }
        if (metaStart < 0) throw new InvalidDataException("No meta box.");

        byte[]? av1c = null, pixi = null;
        int itemOffset = -1, itemLen = -1;
        for (int pos = metaStart; pos + 8 <= metaEnd;)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(avif.Slice(pos, 4));
            string type = Encoding.ASCII.GetString(avif.Slice(pos + 4, 4));
            int end = size == 0 ? metaEnd : pos + (int)size;
            if (type == "iprp")
            {
                for (int p2 = pos + 8; p2 + 8 <= end;)
                {
                    uint s2 = BinaryPrimitives.ReadUInt32BigEndian(avif.Slice(p2, 4));
                    string t2 = Encoding.ASCII.GetString(avif.Slice(p2 + 4, 4));
                    int e2 = s2 == 0 ? end : p2 + (int)s2;
                    if (t2 == "ipco")
                    {
                        for (int p3 = p2 + 8; p3 + 8 <= e2;)
                        {
                            uint s3 = BinaryPrimitives.ReadUInt32BigEndian(avif.Slice(p3, 4));
                            string t3 = Encoding.ASCII.GetString(avif.Slice(p3 + 4, 4));
                            int e3 = s3 == 0 ? e2 : p3 + (int)s3;
                            if (t3 == "av1C" && av1c is null) av1c = avif.Slice(p3 + 8, e3 - p3 - 8).ToArray();
                            if (t3 == "pixi" && pixi is null) pixi = avif.Slice(p3 + 8, e3 - p3 - 8).ToArray();
                            p3 = e3;
                        }
                    }
                    p2 = e2;
                }
            }
            else if (type == "iloc")
            {
                int p = pos + 8;
                byte version = avif[p]; p += 4;
                int offSize = avif[p] >> 4, lenSize = avif[p] & 0xF; p++;
                int baseOffSize = avif[p] >> 4, idxSize = version >= 1 ? (avif[p] & 0xF) : 0; p++;
                uint itemCount = version < 2 ? BinaryPrimitives.ReadUInt16BigEndian(avif.Slice(p, 2)) : BinaryPrimitives.ReadUInt32BigEndian(avif.Slice(p, 4));
                p += version < 2 ? 2 : 4;
                for (uint i = 0; i < itemCount; i++)
                {
                    uint id = version < 2 ? BinaryPrimitives.ReadUInt16BigEndian(avif.Slice(p, 2)) : BinaryPrimitives.ReadUInt32BigEndian(avif.Slice(p, 4));
                    p += version < 2 ? 2 : 4;
                    if (version >= 1) p += 2; // construction_method
                    p += 2; // data_ref_index
                    long baseOff = ReadInt(avif, ref p, baseOffSize);
                    ushort extCount = BinaryPrimitives.ReadUInt16BigEndian(avif.Slice(p, 2)); p += 2;
                    for (int e = 0; e < extCount; e++)
                    {
                        if (version >= 1) p += idxSize;
                        long extOff = ReadInt(avif, ref p, offSize);
                        long extLen = ReadInt(avif, ref p, lenSize);
                        if (id == 1 && e == 0)
                        {
                            itemOffset = (int)(baseOff + extOff);
                            itemLen = (int)extLen;
                        }
                    }
                }
            }
            pos = end;
        }
        if (av1c is null || pixi is null || itemOffset < 0)
            throw new InvalidDataException("Failed to extract av01 item from libavif output.");
        return (avif.Slice(itemOffset, itemLen).ToArray(), av1c, pixi);
    }

    private static long ReadInt(ReadOnlySpan<byte> s, ref int pos, int size)
    {
        long v = 0;
        for (int i = 0; i < size; i++) v = (v << 8) | s[pos++];
        return v;
    }

    /// <summary>
    /// Replaces any <c>colr</c> property box inside <c>ipco</c> with a same-size <c>free</c> box so the
    /// file offsets (<c>iloc</c>) remain untouched while the colour signalling comes strictly from the AV1 OBU.
    /// </summary>
    internal static byte[] StripColrBoxInPlace(byte[] avif)
    {
        var copy = (byte[])avif.Clone();
        int metaStart = -1, metaEnd = -1;
        for (int pos = 0; pos + 8 <= copy.Length;)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(pos, 4));
            string type = Encoding.ASCII.GetString(copy, pos + 4, 4);
            int end = size == 0 ? copy.Length : pos + (int)size;
            if (type == "meta") { metaStart = pos + 12; metaEnd = end; break; }
            pos = end;
        }
        if (metaStart < 0) throw new InvalidDataException("No meta box.");

        bool patched = false;
        for (int pos = metaStart; pos + 8 <= metaEnd;)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(pos, 4));
            string type = Encoding.ASCII.GetString(copy, pos + 4, 4);
            int end = size == 0 ? metaEnd : pos + (int)size;
            if (type == "iprp")
            {
                for (int p2 = pos + 8; p2 + 8 <= end;)
                {
                    uint s2 = BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(p2, 4));
                    string t2 = Encoding.ASCII.GetString(copy, p2 + 4, 4);
                    int e2 = s2 == 0 ? end : p2 + (int)s2;
                    if (t2 == "ipco")
                    {
                        for (int p3 = p2 + 8; p3 + 8 <= e2;)
                        {
                            uint s3 = BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(p3, 4));
                            string t3 = Encoding.ASCII.GetString(copy, p3 + 4, 4);
                            int e3 = s3 == 0 ? e2 : p3 + (int)s3;
                            if (t3 == "colr")
                            {
                                Encoding.ASCII.GetBytes("free", copy.AsSpan(p3 + 4, 4));
                                Array.Clear(copy, p3 + 8, e3 - p3 - 8);
                                patched = true;
                            }
                            p3 = e3;
                        }
                    }
                    p2 = e2;
                }
            }
            pos = end;
        }
        if (!patched) throw new InvalidOperationException("No colr box found to strip.");
        return copy;
    }

    /// <summary>
    /// Builds a clean ISO/IEC 23008-12 AVIF containing a primary <c>av01</c>, a <c>thmb</c> thumbnail
    /// <c>av01</c>, ICC profile, Exif item and XMP mime item.
    /// </summary>
    internal static byte[] BuildWithThumbnailAndMetadata(
        byte[] primaryObu, int w, int h, byte[] primaryAv1c, byte[] primaryPixi,
        byte[] thumbObu, int tw, int th, byte[] thumbAv1c, byte[] thumbPixi,
        byte[] icc, byte[] exifTiff, byte[] xmp)
    {
        // Items: 1 = primary av01, 2 = thumb av01, 3 = Exif, 4 = XMP (mime)
        // Strip leading JPEG/WebP "Exif\0\0" header if present so exif_tiff_header_offset = 0 points straight to II*/MM*.
        ReadOnlySpan<byte> tiffSpan = exifTiff;
        if (tiffSpan.Length >= 6 && tiffSpan[0] == (byte)'E' && tiffSpan[1] == (byte)'x' && tiffSpan[2] == (byte)'i' && tiffSpan[3] == (byte)'f' && tiffSpan[4] == 0 && tiffSpan[5] == 0)
            tiffSpan = tiffSpan[6..];
        var exifPayload = new byte[4 + tiffSpan.Length]; // 4-byte exif_tiff_header_offset = 0
        tiffSpan.CopyTo(exifPayload.AsSpan(4));

        var ftyp = BuildFtyp("avif", ["avif", "mif1", "miaf", "MA1B"]);
        var hdlr = BuildPictHdlr();
        var pitm = FullBox("pitm", 0, 0, U16(1));

        var infe1 = BuildInfe(1, "av01", "Color");
        var infe2 = BuildInfe(2, "av01", "Thumb");
        var infe3 = BuildInfe(3, "Exif", "Exif");
        var infe4 = BuildMimeInfe(4, "application/rdf+xml");
        var iinf = FullBox("iinf", 0, 0, Concat(U16(4), infe1, infe2, infe3, infe4));

        var iref = FullBox("iref", 0, 0, Concat(
            Box("thmb", Concat(U16(2), U16(1), U16(1))), // thumb(2) -> primary(1)
            Box("cdsc", Concat(U16(3), U16(1), U16(1))), // exif(3)  -> primary(1)
            Box("cdsc", Concat(U16(4), U16(1), U16(1)))  // xmp(4)   -> primary(1)
        ));

        // Properties (1-based): 1=ispe(primary), 2=pixi(primary), 3=av1C(primary), 4=colr(prof), 5=ispe(thumb), 6=pixi(thumb), 7=av1C(thumb)
        var colr = Box("colr", Concat(Encoding.ASCII.GetBytes("prof"), icc));
        var ipco = Box("ipco", Concat(
            BuildIspe(w, h), Box("pixi", primaryPixi), Box("av1C", primaryAv1c), colr,
            BuildIspe(tw, th), Box("pixi", thumbPixi), Box("av1C", thumbAv1c)));
        var ipma = BuildIpma([
            (1, [(1, false), (2, false), (3, true), (4, false)]),
            (2, [(5, false), (6, false), (7, true), (4, false)]),
        ]);
        var iprp = Box("iprp", Concat(ipco, ipma));

        return AssembleMetaAndMdat(ftyp, [hdlr, pitm, iinf, iref, iprp], [
            (1, primaryObu),
            (2, thumbObu),
            (3, exifPayload),
            (4, xmp),
        ]);
    }

    /// <summary>
    /// Builds a clean ISO/IEC 23008-12 <c>iden</c> derived-image AVIF wrapping a single <c>av01</c> item
    /// with <c>irot</c> (90° CCW) and <c>imir</c> (horizontal) applied on the <c>iden</c> item.
    /// </summary>
    internal static byte[] BuildIdenDerived(byte[] av1Obu, int w, int h, byte[] av1c, byte[] pixi)
    {
        // Item 1 = iden (primary, 0-byte data), Item 2 = av01 source
        var ftyp = BuildFtyp("avif", ["avif", "mif1", "miaf", "MA1B"]);
        var hdlr = BuildPictHdlr();
        var pitm = FullBox("pitm", 0, 0, U16(1));
        var iinf = FullBox("iinf", 0, 0, Concat(U16(2), BuildInfe(1, "iden", "Iden"), BuildInfe(2, "av01", "Src")));
        var iref = FullBox("iref", 0, 0, Box("dimg", Concat(U16(1), U16(1), U16(2))));
        // Props: 1=ispe, 2=pixi, 3=av1C, 4=irot(1 => 90°), 5=imir(1 => horizontal)
        var ipco = Box("ipco", Concat(
            BuildIspe(w, h), Box("pixi", pixi), Box("av1C", av1c),
            Box("irot", [1]), Box("imir", [1])));
        var ipma = BuildIpma([
            (1, [(1, false), (2, false), (4, true), (5, true)]),
            (2, [(1, false), (2, false), (3, true)]),
        ]);
        var iprp = Box("iprp", Concat(ipco, ipma));
        return AssembleMetaAndMdat(ftyp, [hdlr, pitm, iinf, iref, iprp], [(2, av1Obu)]);
    }

    /// <summary>
    /// Builds a clean ISO/IEC 23008-12 <c>iovl</c> overlay derived-image AVIF placing multiple <c>av01</c>
    /// items onto a canvas with background colour and explicit (x, y) offsets.
    /// </summary>
    internal static byte[] BuildIovlOverlay(int canvasW, int canvasH, (byte[] Obu, int W, int H, int X, int Y, byte[] Av1c, byte[] Pixi)[] layers)
    {
        // Item 1 = iovl (primary), Items 2..N+1 = av01 layers
        var ftyp = BuildFtyp("avif", ["avif", "mif1", "miaf", "MA1B"]);
        var hdlr = BuildPictHdlr();
        var pitm = FullBox("pitm", 0, 0, U16(1));

        var infes = new List<byte[]> { U16((ushort)(layers.Length + 1)), BuildInfe(1, "iovl", "Overlay") };
        var dimgTargets = new List<byte[]> { U16(1), U16((ushort)layers.Length) };
        var ipcoBoxes = new List<byte[]> { BuildIspe(canvasW, canvasH), Box("pixi", layers[0].Pixi) };
        var ipmaEntries = new List<(ushort ItemId, (byte PropIdx, bool Essential)[] Props)>
        {
            (1, [(1, false), (2, false)])
        };

        // iovl item payload (ISO/IEC 23008-12 §6.6.2.4.2):
        // version(1)=0, flags(1)=0 (16-bit), canvas_fill_value[4](8), output_width(2), output_height(2),
        // then per layer: horizontal_offset(s16), vertical_offset(s16).
        var iovlPayload = new byte[1 + 1 + 8 + 4 + layers.Length * 4];
        // Dark slate background in 16-bit RGBA
        BinaryPrimitives.WriteUInt16BigEndian(iovlPayload.AsSpan(2, 2), 0x1800);
        BinaryPrimitives.WriteUInt16BigEndian(iovlPayload.AsSpan(4, 2), 0x2200);
        BinaryPrimitives.WriteUInt16BigEndian(iovlPayload.AsSpan(6, 2), 0x3300);
        BinaryPrimitives.WriteUInt16BigEndian(iovlPayload.AsSpan(8, 2), 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(iovlPayload.AsSpan(10, 2), (ushort)canvasW);
        BinaryPrimitives.WriteUInt16BigEndian(iovlPayload.AsSpan(12, 2), (ushort)canvasH);

        var mdatItems = new List<(ushort Id, byte[] Data)> { (1, iovlPayload) };
        for (int i = 0; i < layers.Length; i++)
        {
            ushort id = (ushort)(i + 2);
            var l = layers[i];
            BinaryPrimitives.WriteInt16BigEndian(iovlPayload.AsSpan(14 + i * 4, 2), (short)l.X);
            BinaryPrimitives.WriteInt16BigEndian(iovlPayload.AsSpan(16 + i * 4, 2), (short)l.Y);
            infes.Add(BuildInfe(id, "av01", $"Layer{i}"));
            dimgTargets.Add(U16(id));
            byte ispeIdx = (byte)(ipcoBoxes.Count + 1);
            ipcoBoxes.Add(BuildIspe(l.W, l.H));
            byte av1cIdx = (byte)(ipcoBoxes.Count + 1);
            ipcoBoxes.Add(Box("av1C", l.Av1c));
            ipmaEntries.Add((id, [(ispeIdx, false), (2, false), (av1cIdx, true)]));
            mdatItems.Add((id, l.Obu));
        }

        var iinf = FullBox("iinf", 0, 0, Concat(infes.ToArray()));
        var iref = FullBox("iref", 0, 0, Box("dimg", Concat(dimgTargets.ToArray())));
        var ipco = Box("ipco", Concat(ipcoBoxes.ToArray()));
        var ipma = BuildIpma(ipmaEntries.ToArray());
        var iprp = Box("iprp", Concat(ipco, ipma));

        return AssembleMetaAndMdat(ftyp, [hdlr, pitm, iinf, iref, iprp], mdatItems.ToArray());
    }

    private static byte[] BuildFtyp(string major, string[] compatible)
    {
        var parts = new List<byte[]> { Encoding.ASCII.GetBytes(major), U32(0) };
        foreach (var b in compatible) parts.Add(Encoding.ASCII.GetBytes(b));
        return Box("ftyp", Concat(parts.ToArray()));
    }

    private static byte[] BuildPictHdlr()
    {
        var p = new byte[21];
        Encoding.ASCII.GetBytes("pict", p.AsSpan(4, 4));
        return FullBox("hdlr", 0, 0, p);
    }

    private static byte[] BuildInfe(ushort id, string type, string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name + "\0");
        return FullBox("infe", 2, 0, Concat(U16(id), U16(0), Encoding.ASCII.GetBytes(type), nameBytes));
    }

    private static byte[] BuildMimeInfe(ushort id, string contentType)
    {
        var ctBytes = Encoding.ASCII.GetBytes(contentType + "\0");
        return FullBox("infe", 2, 0, Concat(U16(id), U16(0), Encoding.ASCII.GetBytes("mime"), [0], ctBytes));
    }

    private static byte[] BuildIspe(int w, int h) => FullBox("ispe", 0, 0, Concat(U32((uint)w), U32((uint)h)));

    private static byte[] BuildIpma((ushort ItemId, (byte PropIdx, bool Essential)[] Props)[] entries)
    {
        var parts = new List<byte[]> { U32((uint)entries.Length) };
        foreach (var (id, props) in entries)
        {
            parts.Add(U16(id));
            parts.Add([(byte)props.Length]);
            foreach (var (idx, ess) in props)
                parts.Add([(byte)((ess ? 0x80 : 0) | (idx & 0x7F))]);
        }
        return FullBox("ipma", 0, 0, Concat(parts.ToArray()));
    }

    private static byte[] AssembleMetaAndMdat(byte[] ftyp, byte[][] metaChildrenNoIloc, (ushort Id, byte[] Data)[] items)
    {
        // Two-pass iloc with 4-byte offsets and 4-byte lengths (version 0).
        byte[] BuildIloc(int mdatPayloadStart)
        {
            var parts = new List<byte[]> { new byte[] { 0x44, 0x00 }, U16((ushort)items.Length) };
            int cur = mdatPayloadStart;
            foreach (var (id, data) in items)
            {
                parts.Add(U16(id));
                parts.Add(U16(0)); // data_ref_index
                parts.Add(U16(1)); // extent_count
                parts.Add(U32((uint)cur));
                parts.Add(U32((uint)data.Length));
                cur += data.Length;
            }
            return FullBox("iloc", 0, 0, Concat(parts.ToArray()));
        }

        byte[] dummyIloc = BuildIloc(0);
        byte[] dummyMeta = FullBox("meta", 0, 0, Concat([.. metaChildrenNoIloc, dummyIloc]));
        int mdatPayloadStart = ftyp.Length + dummyMeta.Length + 8;
        byte[] realIloc = BuildIloc(mdatPayloadStart);
        byte[] realMeta = FullBox("meta", 0, 0, Concat([.. metaChildrenNoIloc, realIloc]));
        byte[] mdatPayload = Concat(items.Select(i => i.Data).ToArray());
        byte[] mdat = Box("mdat", mdatPayload);
        return Concat(ftyp, realMeta, mdat);
    }

    private static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); return b; }
    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
}
