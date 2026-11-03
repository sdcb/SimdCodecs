using Sdcb.SimdCodecs.Heif;
using Sdcb.SimdCodecs.Heif.Hevc;

namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>
/// One HEVC-coded image item split into the NAL units both decoders consume: the parameter sets from
/// <c>hvcC</c> followed by the item payload's units, with the length prefix removed.
/// </summary>
internal sealed class Tile
{
    public required HevcDecoderConfiguration Config { get; init; }

    /// <summary>The item payload: length-prefixed NAL units, as <c>iloc</c> stores them.</summary>
    public required byte[] Payload { get; init; }

    /// <summary>The same units as bare NAL units, which is what libde265's push API takes.</summary>
    public required byte[][] NalUnits { get; init; }
}

/// <summary>Splits a HEIF file into its coded items once, so the timed region is the decode alone.</summary>
internal static class Tiles
{
    /// <summary>Parses the container and returns one <see cref="Tile"/> per coded image, in grid order.</summary>
    public static Tile[] Extract(byte[] data)
    {
        var container = HeifContainer.Parse(data);
        var items = container.Tiles;
        var configs = container.TileConfigs;
        var tiles = new Tile[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            long length = Isobmff.ItemContainer.GetItemLength(items[i]);
            if (length > data.Length) throw new InvalidDataException("An image item is larger than the file.");
            var payload = new byte[length];
            container.Items.Resolve(data, items[i], payload);
            tiles[i] = new Tile
            {
                Config = configs[i],
                Payload = payload,
                NalUnits = SplitNalUnits(configs[i], payload),
            };
        }
        return tiles;
    }

    /// <summary>Decodes one tile with our decoder: the HEVC stage on its own, no container, no colour.</summary>
    public static HevcPicture Decode(Tile tile) => HevcImageDecoder.Decode(tile.Config, tile.Payload);

    private static byte[][] SplitNalUnits(HevcDecoderConfiguration config, byte[] payload)
    {
        var units = new List<byte[]>(config.NalUnits);
        int lengthSize = config.LengthSize;
        var p = 0;
        while (p + lengthSize <= payload.Length)
        {
            int nalLength = 0;
            for (var k = 0; k < lengthSize; k++) nalLength = (nalLength << 8) | payload[p + k];
            p += lengthSize;
            if (nalLength <= 0 || nalLength > payload.Length - p)
            {
                throw new InvalidDataException("A NAL unit length field does not fit the item payload.");
            }
            units.Add(payload.AsSpan(p, nalLength).ToArray());
            p += nalLength;
        }
        return [.. units];
    }
}
