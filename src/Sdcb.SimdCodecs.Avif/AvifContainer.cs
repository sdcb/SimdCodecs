using System.Text;
using Sdcb.SimdCodecs.Avif.Isobmff;

namespace Sdcb.SimdCodecs.Avif;

/// <summary>
/// AVIF container interpretation (AOM AVIF v1.1.0 / ISO/IEC 23008-12) over the shared ISOBMFF <see cref="ItemContainer"/>:
/// primary <c>av01</c> or <c>grid</c> image, optional <c>auxl</c> alpha image (single or grid), <c>av1C</c>, <c>ispe</c>,
/// <c>colr</c>, <c>clap</c>, <c>irot</c>, <c>imir</c>, and <c>prem</c>.
/// </summary>
internal sealed class AvifContainer
{
    internal const uint BrandAvis = 0x61766973; // 'avis'
    internal const uint ItemAv01 = 0x61763031;  // 'av01'
    internal const uint ItemGrid = 0x67726964;  // 'grid'
    internal const uint ItemIden = 0x6964656E;  // 'iden'
    internal const uint ItemIovl = 0x696F766C;  // 'iovl'
    internal const uint ItemTmap = 0x746D6170;  // 'tmap'
    internal const uint RefDimg = 0x64696D67;   // 'dimg'
    internal const uint RefAuxl = 0x6175786C;   // 'auxl'
    internal const uint RefPrem = 0x7072656D;   // 'prem'
    internal const uint PropAv1C = 0x61763143;  // 'av1C'
    internal const uint PropIspe = 0x69737065;  // 'ispe'
    internal const uint PropPixi = 0x70697869;  // 'pixi'
    internal const uint PropColr = 0x636F6C72;  // 'colr'
    internal const uint PropClli = 0x636C6C69;  // 'clli'
    internal const uint PropClap = 0x636C6170;  // 'clap'
    internal const uint PropIrot = 0x69726F74;  // 'irot'
    internal const uint PropImir = 0x696D6972;  // 'imir'
    internal const uint PropAuxC = 0x61757843;  // 'auxC'
    internal const uint PropA1lx = 0x61316C78;  // 'a1lx'
    internal const uint PropA1op = 0x61316F70;  // 'a1op'
    private const uint ColrNclx = 0x6E636C78;   // 'nclx'
    private const uint ColrProf = 0x70726F66;   // 'prof'
    private const uint ColrRicc = 0x72494343;   // 'rICC'

    internal const string AlphaUrn = "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha";
    private const int MaxDerivedDepth = 16;

    internal const long MaxPixels = 1L << 28;
    internal const int MaxDimension = 65_535;

    internal readonly record struct Transform(uint Type, int A, int B, long[]? Clap);
    internal readonly record struct Nclx(int ColourPrimaries, int TransferCharacteristics, int MatrixCoefficients, bool FullRange);
    internal readonly record struct Clli(ushort MaxContentLightLevel, ushort MaxPicAverageLightLevel);

    internal readonly record struct OverlayLayer(AvifContainer Image, int HorizontalOffset, int VerticalOffset);

    internal sealed class OverlayPlan
    {
        internal int CanvasWidth { get; init; }
        internal int CanvasHeight { get; init; }
        internal ushort FillR { get; init; }
        internal ushort FillG { get; init; }
        internal ushort FillB { get; init; }
        internal ushort FillA { get; init; }
        internal List<OverlayLayer> Layers { get; } = [];
    }

    internal readonly record struct Av1Config(
        int SeqProfile,
        int SeqLevelIdx0,
        bool HighBitdepth,
        bool TwelveBit,
        bool Monochrome,
        int SubsamplingX,
        int SubsamplingY,
        int ChromaSamplePosition,
        int ConfigObusOffset,
        int ConfigObusLength)
    {
        internal int BitDepth => TwelveBit ? 12 : HighBitdepth ? 10 : 8;
    }

    internal sealed class ImagePlan
    {
        internal ItemContainer.Item Root { get; init; } = null!;
        internal List<ItemContainer.Item> Tiles { get; } = [];
        internal List<Av1Config> TileConfigs { get; } = [];
        internal int GridRows { get; set; } = 1;
        internal int GridColumns { get; set; } = 1;
        internal int CanvasWidth { get; set; }
        internal int CanvasHeight { get; set; }
    }

    private AvifContainer() { }

    internal ItemContainer Items { get; private set; } = null!;
    internal uint RootItemId { get; private set; }
    internal ImagePlan Primary { get; private set; } = null!;
    internal OverlayPlan? Overlay { get; private set; }
    internal ImagePlan? Alpha { get; private set; }
    internal bool IsPremultiplied { get; private set; }
    internal byte[]? IccProfile { get; private set; }
    internal Nclx? NclxColour { get; private set; }
    internal Clli? ContentLightLevel { get; private set; }
    internal uint GainMapItemId { get; private set; }
    internal AvifGainMapMetadata? GainMapMetadata { get; private set; }
    internal List<Transform> Transforms { get; } = [];

    internal int BitDepth => Overlay is not null
        ? (Overlay.Layers.Count > 0 ? Overlay.Layers.Max(static l => l.Image.BitDepth) : 8)
        : Primary.TileConfigs[0].BitDepth;

    internal int TileCount => Overlay is not null
        ? Overlay.Layers.Sum(static l => l.Image.TileCount)
        : Primary.Tiles.Count;

    internal bool HasAlpha => Alpha is not null || (Overlay is not null && Overlay.FillA < 65535);

    internal static AvifContainer Parse(ReadOnlySpan<byte> data, uint targetItemId = 0)
    {
        var items = ItemContainer.Parse(data);
        uint itemId = targetItemId != 0 ? targetItemId : items.PrimaryItemId;
        if (itemId == 0)
            throw new InvalidDataException("The AVIF file has no primary item ('pitm').");
        return ParseItem(data, items, itemId, depth: 0);
    }

    private static AvifContainer ParseItem(ReadOnlySpan<byte> data, ItemContainer items, uint rootId, int depth)
    {
        if (depth > MaxDerivedDepth)
            throw new InvalidDataException("AVIF derived item chain exceeds maximum nesting depth.");

        var rootItem = items.Find(rootId)
            ?? throw new InvalidDataException($"AVIF item {rootId} is not declared in 'iinf'.");

        // Unwrap 'iden' identity derived item chains (ISO/IEC 23008-12 §6.6.2.2) while preserving
        // inner-to-outer transform order and outer-to-inner colour/alpha precedence.
        var idenChain = new List<uint>();
        var visited = new HashSet<uint> { rootId };
        var current = rootItem;
        while (current.Type == ItemIden)
        {
            idenChain.Add(current.Id);
            if (idenChain.Count > MaxDerivedDepth)
                throw new InvalidDataException("AVIF 'iden' chain exceeds maximum depth.");

            uint nextId = 0;
            foreach (var r in items.References)
            {
                if (r.Type == RefDimg && r.From == current.Id)
                {
                    if (r.To.Length != 1)
                        throw new InvalidDataException($"AVIF 'iden' item must reference exactly 1 item, found {r.To.Length}.");
                    nextId = r.To[0];
                    break;
                }
            }
            if (nextId == 0 || !visited.Add(nextId))
                throw new InvalidDataException("Invalid or cyclic AVIF 'iden' reference.");
            current = items.Find(nextId)
                ?? throw new InvalidDataException($"AVIF 'iden' target item {nextId} is not declared.");
        }

        var container = new AvifContainer { Items = items, RootItemId = rootId };

        if (current.Type == ItemIovl)
        {
            container.Overlay = ParseOverlay(data, items, current, depth + 1);
            container.Primary = new ImagePlan
            {
                Root = current,
                CanvasWidth = container.Overlay.CanvasWidth,
                CanvasHeight = container.Overlay.CanvasHeight,
            };
        }
        else
        {
            container.Primary = ParseImagePlan(data, items, current, isAlpha: false);
        }

        // Colour & CLLI: outer-to-inner along iden chain, then underlying item, then first tile.
        foreach (uint id in idenChain)
        {
            container.ReadColour(data, id);
            container.ReadClli(data, id);
        }
        container.ReadColour(data, current.Id);
        container.ReadClli(data, current.Id);
        if (container.IccProfile is null && container.NclxColour is null && container.Primary.Tiles.Count > 0 && container.Primary.Tiles[0] != current)
            container.ReadColour(data, container.Primary.Tiles[0].Id);

        // Transforms: inner item first, then outer 'iden' items from innermost to outermost.
        container.ReadTransforms(data, current.Id);
        for (int i = idenChain.Count - 1; i >= 0; i--)
            container.ReadTransforms(data, idenChain[i]);

        // Optional auxiliary alpha item ('auxl' referencing root or underlying item with AlphaUrn in 'auxC').
        ItemContainer.Item? alphaItem = FindAlphaItem(data, items, rootId);
        uint alphaTargetId = rootId;
        if (alphaItem is null && current.Id != rootId)
        {
            alphaItem = FindAlphaItem(data, items, current.Id);
            alphaTargetId = current.Id;
        }
        if (alphaItem is not null)
        {
            container.Alpha = ParseImagePlan(data, items, alphaItem, isAlpha: true);
            foreach (var r in items.References)
            {
                if (r.Type == RefPrem && r.From == alphaTargetId && Array.IndexOf(r.To, alphaItem.Id) >= 0)
                {
                    container.IsPremultiplied = true;
                    break;
                }
            }
        }

        // Optional ISO/IEC 21496-1 'tmap' gain map referencing (base = rootId, gainMap).
        if (depth == 0)
            container.DetectGainMap(data, items, rootId);

        return container;
    }

    private static OverlayPlan ParseOverlay(ReadOnlySpan<byte> data, ItemContainer items, ItemContainer.Item iovl, int depth)
    {
        long length = ItemContainer.GetItemLength(iovl);
        if (length < 14 || length > 65_536)
            throw new InvalidDataException("AVIF 'iovl' item payload has an invalid size.");
        var payload = new byte[length];
        items.Resolve(data, iovl, payload);

        if (payload[0] != 0)
            throw new InvalidDataException($"Unsupported AVIF 'iovl' version {payload[0]}.");
        bool wide = (payload[1] & 1) != 0;
        ushort fillR = ReadU16(payload.AsSpan(2));
        ushort fillG = ReadU16(payload.AsSpan(4));
        ushort fillB = ReadU16(payload.AsSpan(6));
        ushort fillA = ReadU16(payload.AsSpan(8));

        int pos = 10;
        int canvasW, canvasH;
        if (wide)
        {
            if (length < 18) throw new InvalidDataException("Truncated 32-bit AVIF 'iovl' payload.");
            uint w = ReadU32(payload.AsSpan(pos));
            uint h = ReadU32(payload.AsSpan(pos + 4));
            pos += 8;
            if (w == 0 || h == 0 || w > MaxDimension || h > MaxDimension || (long)w * h > MaxPixels)
                throw new InvalidDataException("Invalid AVIF 'iovl' canvas dimensions.");
            canvasW = (int)w;
            canvasH = (int)h;
        }
        else
        {
            canvasW = ReadU16(payload.AsSpan(pos));
            canvasH = ReadU16(payload.AsSpan(pos + 2));
            pos += 4;
            if (canvasW <= 0 || canvasH <= 0 || (long)canvasW * canvasH > MaxPixels)
                throw new InvalidDataException("Invalid AVIF 'iovl' canvas dimensions.");
        }

        ItemContainer.Reference? dimg = null;
        foreach (var r in items.References)
        {
            if (r.Type == RefDimg && r.From == iovl.Id)
            {
                dimg = r;
                break;
            }
        }
        if (dimg is not { } refs || refs.To.Length == 0)
            throw new InvalidDataException("AVIF 'iovl' item has no 'dimg' references.");

        int offsetBytes = wide ? 8 : 4;
        if (pos + refs.To.Length * offsetBytes > payload.Length)
            throw new InvalidDataException("AVIF 'iovl' payload is truncated for its layer count.");

        var plan = new OverlayPlan
        {
            CanvasWidth = canvasW,
            CanvasHeight = canvasH,
            FillR = fillR,
            FillG = fillG,
            FillB = fillB,
            FillA = fillA,
        };

        foreach (uint layerId in refs.To)
        {
            int hOff, vOff;
            if (wide)
            {
                hOff = unchecked((int)ReadU32(payload.AsSpan(pos)));
                vOff = unchecked((int)ReadU32(payload.AsSpan(pos + 4)));
                pos += 8;
            }
            else
            {
                hOff = unchecked((short)ReadU16(payload.AsSpan(pos)));
                vOff = unchecked((short)ReadU16(payload.AsSpan(pos + 2)));
                pos += 4;
            }
            var layerContainer = ParseItem(data, items, layerId, depth + 1);
            plan.Layers.Add(new OverlayLayer(layerContainer, hOff, vOff));
        }

        return plan;
    }

    private void DetectGainMap(ReadOnlySpan<byte> data, ItemContainer items, uint primaryId)
    {
        foreach (var r in items.References)
        {
            if (r.Type != RefDimg || r.To.Length != 2 || r.To[0] != primaryId)
                continue;
            var tmapItem = items.Find(r.From);
            if (tmapItem is null || tmapItem.Type != ItemTmap)
                continue;
            long len = ItemContainer.GetItemLength(tmapItem);
            if (len < 62 || len > 4096)
                continue;
            var buf = new byte[len];
            try
            {
                items.Resolve(data, tmapItem, buf);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            var meta = AvifGainMapMetadata.TryParse(buf);
            if (meta is not null)
            {
                GainMapItemId = r.To[1];
                GainMapMetadata = meta;
                return;
            }
        }
    }

    private void ReadClli(ReadOnlySpan<byte> data, uint itemId)
    {
        if (ContentLightLevel is not null) return;
        var clli = Items.FindProperty(data, itemId, PropClli);
        if (clli.Length >= 4)
            ContentLightLevel = new Clli(ReadU16(clli), ReadU16(clli[2..]));
    }

    private static ImagePlan ParseImagePlan(ReadOnlySpan<byte> data, ItemContainer items, ItemContainer.Item root, bool isAlpha)
    {
        var plan = new ImagePlan { Root = root };
        if (root.Type == ItemAv01)
        {
            plan.Tiles.Add(root);
            var ispe = items.FindProperty(data, root.Id, PropIspe);
            if (ispe.Length >= 12)
            {
                uint w = ReadU32(ispe[4..]);
                uint h = ReadU32(ispe[8..]);
                if (w == 0 || h == 0 || w > MaxDimension || h > MaxDimension || (long)w * h > MaxPixels)
                    throw new InvalidDataException("Invalid 'ispe' dimensions in AVIF item.");
                plan.CanvasWidth = (int)w;
                plan.CanvasHeight = (int)h;
            }
        }
        else if (root.Type == ItemGrid)
        {
            ParseGrid(data, items, root, plan);
        }
        else
        {
            throw new InvalidDataException($"AVIF item type '{FourCC(root.Type)}' is not supported.");
        }

        foreach (var tile in plan.Tiles)
        {
            if (tile.Type != ItemAv01)
                throw new InvalidDataException($"AVIF grid tile type '{FourCC(tile.Type)}' is not supported.");

            var av1c = FindPropertyOffset(items, tile.Id, PropAv1C, out int len);
            if (av1c < 0)
                throw new InvalidDataException("An 'av01' item has no 'av1C' property.");
            var cfg = ParseAv1C(data, av1c, len);
            ValidateConfig(cfg, isAlpha);
            plan.TileConfigs.Add(cfg);
        }

        return plan;
    }

    private static void ValidateConfig(Av1Config cfg, bool isAlpha)
    {
        if (cfg.SeqProfile > 2)
            throw new InvalidDataException($"Unsupported AV1 profile {cfg.SeqProfile}.");
        if (isAlpha && !cfg.Monochrome)
            throw new InvalidDataException("AVIF alpha auxiliary item must be monochrome (4:0:0).");
    }

    internal static Av1Config ParseAv1C(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (length < 4)
            throw new InvalidDataException("Truncated 'av1C' property.");
        var b = data.Slice(offset, length);
        if ((b[0] & 0x80) == 0 || (b[0] & 0x7F) != 1)
            throw new InvalidDataException($"Unsupported 'av1C' marker/version 0x{b[0]:X2}.");

        int profile = (b[1] >> 5) & 7;
        int level0 = b[1] & 31;
        bool highBitdepth = (b[2] & 0x40) != 0;
        bool twelveBit = (b[2] & 0x20) != 0;
        bool monochrome = (b[2] & 0x10) != 0;
        int subX = (b[2] >> 3) & 1;
        int subY = (b[2] >> 2) & 1;
        int csp = b[2] & 3;
        return new Av1Config(profile, level0, highBitdepth, twelveBit, monochrome, subX, subY, csp, offset + 4, length - 4);
    }

    private static void ParseGrid(ReadOnlySpan<byte> data, ItemContainer items, ItemContainer.Item grid, ImagePlan plan)
    {
        long length = ItemContainer.GetItemLength(grid);
        if (length < 8 || length > 64)
            throw new InvalidDataException("The 'grid' item payload has an invalid size.");
        Span<byte> payload = stackalloc byte[(int)length];
        items.Resolve(data, grid, payload);
        if (payload[0] != 0)
            throw new InvalidDataException($"Unsupported 'grid' version {payload[0]}.");
        bool wide = (payload[1] & 1) != 0;
        plan.GridRows = payload[2] + 1;
        plan.GridColumns = payload[3] + 1;
        if (wide)
        {
            if (length < 12) throw new InvalidDataException("The 'grid' item payload is truncated.");
            uint w = ReadU32(payload[4..]);
            uint h = ReadU32(payload[8..]);
            if (w > MaxDimension || h > MaxDimension) throw new InvalidDataException("The grid is too large.");
            plan.CanvasWidth = (int)w;
            plan.CanvasHeight = (int)h;
        }
        else
        {
            plan.CanvasWidth = (payload[4] << 8) | payload[5];
            plan.CanvasHeight = (payload[6] << 8) | payload[7];
        }

        if (plan.CanvasWidth <= 0 || plan.CanvasHeight <= 0 || (long)plan.CanvasWidth * plan.CanvasHeight > MaxPixels)
            throw new InvalidDataException("The grid dimensions are invalid.");

        ItemContainer.Reference? dimg = null;
        foreach (var r in items.References)
        {
            if (r.Type == RefDimg && r.From == grid.Id)
            {
                dimg = r;
                break;
            }
        }
        if (dimg is not { } refs)
            throw new InvalidDataException("The 'grid' item has no 'dimg' references.");
        if (refs.To.Length != plan.GridRows * plan.GridColumns)
            throw new InvalidDataException($"The grid is {plan.GridRows}x{plan.GridColumns} but references {refs.To.Length} tiles.");

        foreach (uint id in refs.To)
            plan.Tiles.Add(items.Find(id) ?? throw new InvalidDataException($"Grid tile item {id} is not declared."));
    }

    private static ItemContainer.Item? FindAlphaItem(ReadOnlySpan<byte> data, ItemContainer items, uint primaryId)
    {
        foreach (var r in items.References)
        {
            if (r.Type != RefAuxl || Array.IndexOf(r.To, primaryId) < 0) continue;
            var auxC = items.FindProperty(data, r.From, PropAuxC);
            if (auxC.Length <= 4) continue;
            // auxC is a FullBox (4-byte version+flags) followed by null-terminated UTF-8 URN.
            var urnBytes = auxC[4..];
            int nul = urnBytes.IndexOf((byte)0);
            if (nul >= 0) urnBytes = urnBytes[..nul];
            if (Encoding.ASCII.GetString(urnBytes) == AlphaUrn)
                return items.Find(r.From);
        }
        return null;
    }

    private void ReadColour(ReadOnlySpan<byte> data, uint itemId)
    {
        if (!Items.Associations.TryGetValue(itemId, out var indices)) return;
        foreach (int index in indices)
        {
            if (index <= 0 || index > Items.Properties.Count) continue;
            var property = Items.Properties[index - 1];
            if (property.Type != PropColr || property.Length < 4) continue;
            var body = data.Slice(property.Offset, property.Length);
            uint colourType = ReadU32(body);
            if (colourType == ColrNclx && body.Length >= 11 && NclxColour is null)
            {
                NclxColour = new Nclx((body[4] << 8) | body[5], (body[6] << 8) | body[7], (body[8] << 8) | body[9], (body[10] & 0x80) != 0);
            }
            else if ((colourType == ColrProf || colourType == ColrRicc) && IccProfile is null)
            {
                IccProfile = body[4..].ToArray();
            }
        }
    }

    private void ReadTransforms(ReadOnlySpan<byte> data, uint itemId)
    {
        if (!Items.Associations.TryGetValue(itemId, out var indices)) return;
        foreach (int index in indices)
        {
            if (index <= 0 || index > Items.Properties.Count) continue;
            var property = Items.Properties[index - 1];
            var body = data.Slice(property.Offset, property.Length);
            switch (property.Type)
            {
                case PropIrot when body.Length >= 1:
                    Transforms.Add(new Transform(PropIrot, body[0] & 3, 0, null));
                    break;
                case PropImir when body.Length >= 1:
                    Transforms.Add(new Transform(PropImir, body[0] & 1, 0, null));
                    break;
                case PropClap when body.Length >= 32:
                    var clap = new long[8];
                    for (int i = 0; i < 8; i++)
                    {
                        uint raw = ReadU32(body[(i * 4)..]);
                        clap[i] = i is 4 or 6 ? unchecked((int)raw) : raw;
                    }
                    Transforms.Add(new Transform(PropClap, 0, 0, clap));
                    break;
            }
        }
    }

    private static int FindPropertyOffset(ItemContainer items, uint itemId, uint type, out int length)
    {
        length = 0;
        if (!items.Associations.TryGetValue(itemId, out var indices)) return -1;
        foreach (int index in indices)
        {
            if (index <= 0 || index > items.Properties.Count) continue;
            var property = items.Properties[index - 1];
            if (property.Type != type) continue;
            length = property.Length;
            return property.Offset;
        }
        return -1;
    }

    internal (int Width, int Height) DisplaySize() => DisplaySize(Primary.CanvasWidth, Primary.CanvasHeight, Transforms);

    internal static (int Width, int Height) DisplaySize(int w, int h, List<Transform> transforms)
    {
        foreach (var t in transforms)
        {
            if (t.Type == PropClap)
            {
                var r = ClapRectangle(t.Clap!, w, h);
                w = r.Width;
                h = r.Height;
            }
            else if (t.Type == PropIrot && (t.A & 1) != 0)
            {
                (w, h) = (h, w);
            }
        }
        return (w, h);
    }

    internal static (int X, int Y, int Width, int Height) ClapRectangle(long[] clap, int width, int height)
    {
        long wN = clap[0], wD = clap[1], hN = clap[2], hD = clap[3], xN = clap[4], xD = clap[5], yN = clap[6], yD = clap[7];
        if (wD == 0 || hD == 0 || xD == 0 || yD == 0)
            throw new InvalidDataException("A 'clap' denominator is zero.");
        double clapW = (double)wN / wD;
        double clapH = (double)hN / hD;
        double left = (double)xN / xD + (width - clapW) / 2;
        double top = (double)yN / yD + (height - clapH) / 2;
        int x0 = (int)Math.Clamp(Math.Round(left, MidpointRounding.AwayFromZero), 0, width);
        int y0 = (int)Math.Clamp(Math.Round(top, MidpointRounding.AwayFromZero), 0, height);
        int x1 = (int)Math.Clamp(Math.Round(left + clapW, MidpointRounding.AwayFromZero), x0, width);
        int y1 = (int)Math.Clamp(Math.Round(top + clapH, MidpointRounding.AwayFromZero), y0, height);
        if (x1 == x0 || y1 == y0)
            throw new InvalidDataException("The 'clap' clean aperture is empty.");
        return (x0, y0, x1 - x0, y1 - y0);
    }

    private static ushort ReadU16(ReadOnlySpan<byte> s) => (ushort)((s[0] << 8) | s[1]);
    private static uint ReadU32(ReadOnlySpan<byte> s) => (uint)((s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3]);

    internal static string FourCC(uint type) =>
        new([(char)(type >> 24), (char)((type >> 16) & 0xFF), (char)((type >> 8) & 0xFF), (char)(type & 0xFF)]);
}
