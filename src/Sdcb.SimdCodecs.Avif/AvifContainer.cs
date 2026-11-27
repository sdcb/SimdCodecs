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
    internal const uint RefDimg = 0x64696D67;   // 'dimg'
    internal const uint RefAuxl = 0x6175786C;   // 'auxl'
    internal const uint RefPrem = 0x7072656D;   // 'prem'
    internal const uint PropAv1C = 0x61763143;  // 'av1C'
    internal const uint PropIspe = 0x69737065;  // 'ispe'
    internal const uint PropPixi = 0x70697869;  // 'pixi'
    internal const uint PropColr = 0x636F6C72;  // 'colr'
    internal const uint PropClap = 0x636C6170;  // 'clap'
    internal const uint PropIrot = 0x69726F74;  // 'irot'
    internal const uint PropImir = 0x696D6972;  // 'imir'
    internal const uint PropAuxC = 0x61757843;  // 'auxC'
    internal const uint PropA1lx = 0x61316C78;  // 'a1lx'
    internal const uint PropA1op = 0x61316F70;  // 'a1op'
    private const uint ColrNclx = 0x6E636C78;   // 'nclx'
    private const uint ColrProf = 0x70726F66;   // 'prof'
    private const uint ColrRicc = 0x72494343;   // 'rICC'

    private const string AlphaUrn = "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha";

    internal const long MaxPixels = 1L << 28;
    internal const int MaxDimension = 65_535;

    internal readonly record struct Transform(uint Type, int A, int B, long[]? Clap);
    internal readonly record struct Nclx(int ColourPrimaries, int TransferCharacteristics, int MatrixCoefficients, bool FullRange);

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
    internal ImagePlan Primary { get; private set; } = null!;
    internal ImagePlan? Alpha { get; private set; }
    internal bool IsPremultiplied { get; private set; }
    internal byte[]? IccProfile { get; private set; }
    internal Nclx? NclxColour { get; private set; }
    internal List<Transform> Transforms { get; } = [];

    internal static AvifContainer Parse(ReadOnlySpan<byte> data)
    {
        var items = ItemContainer.Parse(data);
        if (items.MajorBrand == BrandAvis)
            throw new InvalidDataException("Animated AVIF sequences ('avis') are not supported in M1 (lands in M3).");

        if (items.PrimaryItemId == 0)
            throw new InvalidDataException("The AVIF file has no primary item ('pitm').");
        var primaryItem = items.Find(items.PrimaryItemId)
            ?? throw new InvalidDataException("The primary item is not declared in 'iinf'.");

        var container = new AvifContainer { Items = items };
        container.Primary = ParseImagePlan(data, items, primaryItem, isAlpha: false);

        // Colour: primary item's own 'colr', else first tile's.
        container.ReadColour(data, primaryItem.Id);
        if (container.IccProfile is null && container.NclxColour is null && container.Primary.Tiles[0] != primaryItem)
            container.ReadColour(data, container.Primary.Tiles[0].Id);

        container.ReadTransforms(data, primaryItem.Id);

        // Optional auxiliary alpha item ('auxl' referencing primary with AlphaUrn in 'auxC').
        var alphaItem = FindAlphaItem(data, items, primaryItem.Id);
        if (alphaItem is not null)
        {
            container.Alpha = ParseImagePlan(data, items, alphaItem, isAlpha: true);
            foreach (var r in items.References)
            {
                if (r.Type == RefPrem && r.From == primaryItem.Id && Array.IndexOf(r.To, alphaItem.Id) >= 0)
                {
                    container.IsPremultiplied = true;
                    break;
                }
            }
        }

        return container;
    }

    private static ImagePlan ParseImagePlan(ReadOnlySpan<byte> data, ItemContainer items, ItemContainer.Item root, bool isAlpha)
    {
        // Reject layered scalable AVIF ('a1lx' / 'a1op') cleanly in M1.
        if (!items.FindProperty(data, root.Id, PropA1lx).IsEmpty || !items.FindProperty(data, root.Id, PropA1op).IsEmpty)
            throw new InvalidDataException("Layered scalable AVIF ('a1lx'/'a1op') is not supported in M1 (lands in M2).");

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
            throw new InvalidDataException($"AVIF item type '{FourCC(root.Type)}' is not supported in M1; only 'av01' and 'grid' are.");
        }

        foreach (var tile in plan.Tiles)
        {
            if (tile.Type != ItemAv01)
                throw new InvalidDataException($"AVIF grid tile type '{FourCC(tile.Type)}' is not supported.");

            if (!items.FindProperty(data, tile.Id, PropA1lx).IsEmpty || !items.FindProperty(data, tile.Id, PropA1op).IsEmpty)
                throw new InvalidDataException("Layered scalable AVIF ('a1lx'/'a1op') is not supported in M1 (lands in M2).");

            var av1c = FindPropertyOffset(items, tile.Id, PropAv1C, out int len);
            if (av1c < 0)
                throw new InvalidDataException("An 'av01' item has no 'av1C' property.");
            var cfg = ParseAv1C(data, av1c, len);
            ValidateM1Config(cfg, isAlpha);
            plan.TileConfigs.Add(cfg);
        }

        return plan;
    }

    private static void ValidateM1Config(Av1Config cfg, bool isAlpha)
    {
        if (cfg.SeqProfile != 0)
            throw new InvalidDataException($"AV1 profile {cfg.SeqProfile} is not supported in M1 (Profile 0 only; Profile 1/2 land in M2).");
        if (cfg.TwelveBit)
            throw new InvalidDataException("12-bit AV1 is not supported in M1 (lands in M2).");
        if (!cfg.Monochrome && (cfg.SubsamplingX != 1 || cfg.SubsamplingY != 1))
            throw new InvalidDataException($"AV1 subsampling {cfg.SubsamplingX}x{cfg.SubsamplingY} is not supported in M1 (4:2:0 and 4:0:0 only).");
        if (isAlpha && !cfg.Monochrome)
            throw new InvalidDataException("AVIF alpha auxiliary item must be monochrome (4:0:0).");
    }

    private static Av1Config ParseAv1C(ReadOnlySpan<byte> data, int offset, int length)
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

    internal (int Width, int Height) DisplaySize()
    {
        int w = Primary.CanvasWidth, h = Primary.CanvasHeight;
        foreach (var t in Transforms)
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

    private static uint ReadU32(ReadOnlySpan<byte> s) => (uint)((s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3]);

    internal static string FourCC(uint type) =>
        new([(char)(type >> 24), (char)((type >> 16) & 0xFF), (char)((type >> 8) & 0xFF), (char)(type & 0xFF)]);
}
