using Sdcb.SimdCodecs.Heif.Hevc;
using Sdcb.SimdCodecs.Heif.Isobmff;

namespace Sdcb.SimdCodecs.Heif;

/// <summary>
/// The HEIF interpretation (ISO/IEC 23008-12) of an ISOBMFF item container: the primary image item, a
/// <c>grid</c> derived image and its tiles, and the properties a decoder needs (<c>hvcC</c>, <c>ispe</c>,
/// <c>colr</c>, <c>clap</c>, <c>irot</c>, <c>imir</c>).
/// </summary>
internal sealed class HeifContainer
{
    internal const uint ItemHvc1 = 0x68766331; // 'hvc1'
    internal const uint ItemGrid = 0x67726964; // 'grid'
    internal const uint RefDimg = 0x64696D67;  // 'dimg'
    internal const uint PropHvcC = 0x68766343; // 'hvcC'
    internal const uint PropIspe = 0x69737065; // 'ispe'
    internal const uint PropColr = 0x636F6C72; // 'colr'
    internal const uint PropClap = 0x636C6170; // 'clap'
    internal const uint PropIrot = 0x69726F74; // 'irot'
    internal const uint PropImir = 0x696D6972; // 'imir'
    private const uint ColrNclx = 0x6E636C78;  // 'nclx'
    private const uint ColrProf = 0x70726F66;  // 'prof'
    private const uint ColrRicc = 0x72494343;  // 'rICC'

    /// <summary>The largest canvas accepted, in pixels (larger than any phone camera's output by a wide margin).</summary>
    internal const long MaxPixels = 1L << 28;

    /// <summary>The largest canvas side accepted.</summary>
    internal const int MaxDimension = 65_535;

    internal readonly record struct Transform(uint Type, int A, int B, long[]? Clap);

    internal readonly record struct Nclx(int ColourPrimaries, int TransferCharacteristics, int MatrixCoefficients, bool FullRange);

    private HeifContainer() { }

    internal ItemContainer Items { get; private set; } = null!;
    internal ItemContainer.Item Primary { get; private set; } = null!;

    /// <summary>The coded image items, in grid order (row-major); one entry for a non-derived primary image.</summary>
    internal List<ItemContainer.Item> Tiles { get; } = [];

    internal int GridRows { get; private set; } = 1;
    internal int GridColumns { get; private set; } = 1;

    /// <summary>The canvas size before transforms: the grid's output size, or the coded image's size.</summary>
    internal int CanvasWidth { get; private set; }

    internal int CanvasHeight { get; private set; }

    /// <summary>The decoder configuration of each distinct <c>hvcC</c>, by tile index.</summary>
    internal List<HevcDecoderConfiguration> TileConfigs { get; } = [];

    internal byte[]? IccProfile { get; private set; }
    internal Nclx? NclxColour { get; private set; }

    /// <summary>Transformative properties of the primary item, in association order (23008-12 §6.5.1).</summary>
    internal List<Transform> Transforms { get; } = [];

    /// <exception cref="InvalidDataException">The file is not a decodable HEIF still image.</exception>
    internal static HeifContainer Parse(ReadOnlySpan<byte> data) => Parse(data, 0);

    /// <summary>
    /// <see cref="Parse(ReadOnlySpan{byte})"/> with item <paramref name="itemId"/> in place of the primary item (0: the
    /// primary): how a <c>thmb</c> thumbnail, an image item of its own, is decoded.
    /// </summary>
    /// <exception cref="InvalidDataException">The file or the item is not decodable.</exception>
    internal static HeifContainer Parse(ReadOnlySpan<byte> data, uint itemId)
    {
        var items = ItemContainer.Parse(data);
        var container = new HeifContainer { Items = items };
        if (itemId == 0) itemId = items.PrimaryItemId;
        if (itemId == 0) throw new InvalidDataException("The HEIF file has no primary item ('pitm').");
        var primary = items.Find(itemId) ?? throw new InvalidDataException("The image item is not declared in 'iinf'.");
        container.Primary = primary;

        if (primary.Type == ItemHvc1)
        {
            container.Tiles.Add(primary);
        }
        else if (primary.Type == ItemGrid)
        {
            container.ParseGrid(data, primary);
        }
        else
        {
            throw new InvalidDataException($"HEIF primary item type '{FourCC(primary.Type)}' is not supported; only 'hvc1' and 'grid' are.");
        }

        HevcDecoderConfiguration? previousConfig = null;
        var previousOffset = -1;
        foreach (var tile in container.Tiles)
        {
            if (tile.Type != ItemHvc1) throw new InvalidDataException($"Grid tile type '{FourCC(tile.Type)}' is not supported.");
            var hvcc = FindPropertyOffset(items, tile.Id, PropHvcC, out var length);
            if (hvcc < 0) throw new InvalidDataException("An 'hvc1' item has no 'hvcC' property.");
            if (hvcc != previousOffset)
            {
                previousConfig = HevcDecoderConfiguration.Parse(data.Slice(hvcc, length));
                previousOffset = hvcc;
            }
            container.TileConfigs.Add(previousConfig!);
        }

        // Colour: the primary item's own 'colr', else the first tile's (phones put it on the tiles).
        container.ReadColour(data, primary.Id);
        if (container.IccProfile is null && container.NclxColour is null && container.Tiles[0] != primary)
        {
            container.ReadColour(data, container.Tiles[0].Id);
        }

        container.ReadTransforms(data, primary.Id);

        if (primary.Type == ItemHvc1)
        {
            // The coded size (after the conformance window) is authoritative; take it from the SPS.
            var sps = container.TileConfigs[0].ParseSps();
            container.CanvasWidth = sps.Width - sps.ConfLeft - sps.ConfRight;
            container.CanvasHeight = sps.Height - sps.ConfTop - sps.ConfBottom;
            if ((long)container.CanvasWidth * container.CanvasHeight > MaxPixels) throw new InvalidDataException("The image is too large.");
        }
        container.ValidateGeometry();
        return container;
    }

    /// <summary>
    /// Checks, from the <c>hvcC</c> parameter sets and before anything is decoded or allocated, that the coded
    /// images agree in size, cover the canvas, and are not so large in total that a small file could demand an
    /// unbounded allocation (each tile's SPS may declare up to 16 888² samples). <see cref="HevcImageDecoder"/>
    /// holds an in-band SPS to the size declared here.
    /// </summary>
    private void ValidateGeometry()
    {
        long codedSamples = 0;
        int tileW = -1, tileH = -1;
        HevcDecoderConfiguration? parsed = null;
        HevcSps sps = null!;
        foreach (var config in TileConfigs)
        {
            if (!ReferenceEquals(config, parsed))
            {
                sps = config.ParseSps();
                parsed = config;
            }

            int w = sps.Width - sps.ConfLeft - sps.ConfRight, h = sps.Height - sps.ConfTop - sps.ConfBottom;
            if (tileW < 0) (tileW, tileH) = (w, h);
            else if (w != tileW || h != tileH) throw new InvalidDataException("The grid tiles differ in size.");
            codedSamples += (long)sps.Width * sps.Height;
        }

        if (codedSamples > 2 * MaxPixels) throw new InvalidDataException("The coded images are too large.");
        if ((long)tileW * GridColumns < CanvasWidth || (long)tileH * GridRows < CanvasHeight)
        {
            throw new InvalidDataException("The grid tiles do not cover the output image.");
        }
    }

    private void ParseGrid(ReadOnlySpan<byte> data, ItemContainer.Item grid)
    {
        // ImageGrid (23008-12 §6.6.2.3.2): version, flags, rows_minus_one, columns_minus_one, output size.
        var length = ItemContainer.GetItemLength(grid);
        if (length < 8 || length > 64) throw new InvalidDataException("The 'grid' item payload has an invalid size.");
        Span<byte> payload = stackalloc byte[(int)length];
        Items.Resolve(data, grid, payload);
        if (payload[0] != 0) throw new InvalidDataException($"Unsupported 'grid' version {payload[0]}.");
        var wide = (payload[1] & 1) != 0;
        GridRows = payload[2] + 1;
        GridColumns = payload[3] + 1;
        if (wide)
        {
            if (length < 12) throw new InvalidDataException("The 'grid' item payload is truncated.");
            var w = ReadU32(payload[4..]);
            var h = ReadU32(payload[8..]);
            if (w > MaxDimension || h > MaxDimension) throw new InvalidDataException("The grid is too large.");
            CanvasWidth = (int)w;
            CanvasHeight = (int)h;
        }
        else
        {
            CanvasWidth = (payload[4] << 8) | payload[5];
            CanvasHeight = (payload[6] << 8) | payload[7];
        }
        if (CanvasWidth == 0 || CanvasHeight == 0) throw new InvalidDataException("The grid has zero size.");
        if ((long)CanvasWidth * CanvasHeight > MaxPixels) throw new InvalidDataException("The grid is too large.");

        ItemContainer.Reference? dimg = null;
        foreach (var reference in Items.References)
        {
            if (reference.Type == RefDimg && reference.From == grid.Id)
            {
                dimg = reference;
                break;
            }
        }
        if (dimg is not { } refs) throw new InvalidDataException("The 'grid' item has no 'dimg' references.");
        if (refs.To.Length != GridRows * GridColumns)
        {
            throw new InvalidDataException($"The grid is {GridRows}x{GridColumns} but references {refs.To.Length} tiles.");
        }
        foreach (var id in refs.To)
        {
            Tiles.Add(Items.Find(id) ?? throw new InvalidDataException($"Grid tile item {id} is not declared."));
        }
    }

    private void ReadColour(ReadOnlySpan<byte> data, uint itemId)
    {
        if (!Items.Associations.TryGetValue(itemId, out var indices)) return;
        foreach (var index in indices)
        {
            if (index <= 0 || index > Items.Properties.Count) continue;
            var property = Items.Properties[index - 1];
            if (property.Type != PropColr || property.Length < 4) continue;
            var body = data.Slice(property.Offset, property.Length);
            var colourType = ReadU32(body);
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
        foreach (var index in indices)
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
                    for (var i = 0; i < 8; i++)
                    {
                        var raw = ReadU32(body[(i * 4)..]);
                        // horizOffN (4) and vertOffN (6) are signed; the rest are unsigned.
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
        foreach (var index in indices)
        {
            if (index <= 0 || index > items.Properties.Count) continue;
            var property = items.Properties[index - 1];
            if (property.Type != type) continue;
            length = property.Length;
            return property.Offset;
        }
        return -1;
    }

    /// <summary>The display size after the transformative properties.</summary>
    internal (int Width, int Height) DisplaySize()
    {
        int w = CanvasWidth, h = CanvasHeight;
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

    /// <summary>
    /// The crop rectangle of a clean aperture (ISO/IEC 14496-12 §12.1.4) on a <paramref name="width"/> ×
    /// <paramref name="height"/> image: the aperture is centred at ((width - 1) / 2 + horizOff, (height - 1) / 2 + vertOff).
    /// Fractional edges are rounded to the nearest sample; the rectangle is clipped to the image.
    /// </summary>
    internal static (int X, int Y, int Width, int Height) ClapRectangle(long[] clap, int width, int height)
    {
        long wN = clap[0], wD = clap[1], hN = clap[2], hD = clap[3], xN = clap[4], xD = clap[5], yN = clap[6], yD = clap[7];
        if (wD == 0 || hD == 0 || xD == 0 || yD == 0) throw new InvalidDataException("A 'clap' denominator is zero.");
        var clapW = (double)wN / wD;
        var clapH = (double)hN / hD;
        var left = (double)xN / xD + (width - clapW) / 2;
        var top = (double)yN / yD + (height - clapH) / 2;
        var x0 = (int)Math.Clamp(Math.Round(left, MidpointRounding.AwayFromZero), 0, width);
        var y0 = (int)Math.Clamp(Math.Round(top, MidpointRounding.AwayFromZero), 0, height);
        var x1 = (int)Math.Clamp(Math.Round(left + clapW, MidpointRounding.AwayFromZero), x0, width);
        var y1 = (int)Math.Clamp(Math.Round(top + clapH, MidpointRounding.AwayFromZero), y0, height);
        if (x1 == x0 || y1 == y0) throw new InvalidDataException("The 'clap' clean aperture is empty.");
        return (x0, y0, x1 - x0, y1 - y0);
    }

    private static uint ReadU32(ReadOnlySpan<byte> s) => (uint)((s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3]);

    internal static string FourCC(uint type) =>
        new([(char)(type >> 24), (char)((type >> 16) & 0xFF), (char)((type >> 8) & 0xFF), (char)(type & 0xFF)]);
}
