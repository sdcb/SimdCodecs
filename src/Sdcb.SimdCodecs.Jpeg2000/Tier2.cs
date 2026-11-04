using System;
using System.Collections.Generic;
using System.IO;

namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// Tier-2: the packet layer (T.800 B.9 to B.12). It reads packet headers to
/// discover <em>which</em> code-blocks carry data, how many coding passes each
/// got and how many bytes those passes occupy, then hands the byte ranges to
/// tier-1. It never touches a coefficient.
/// <para>
/// A tile's packets are one per layer, resolution, component and precinct, and
/// the progression order of COD says how those four loops nest (B.12.1). The two
/// layer-and-resolution orders, LRCP and RLCP, walk precincts by their index. The
/// three position-driven ones, RPCL, PCRL and CPRL, walk the reference grid and
/// take each precinct at the first grid point that lands in it; that point is
/// worked out once per precinct (<see cref="Precinct.VisitX"/>), so the walk
/// becomes a sort by position rather than a visit of every grid point.
/// </para>
/// <para>
/// <b>Layers are where tier-2 becomes stateful.</b> A code-block's inclusion,
/// zero bit-planes, <c>Lblock</c> and pass count, and its precinct's tag trees,
/// all carry over from one layer's packet to the next. A decoder that rebuilt any
/// of them per packet would read layer 0 correctly and every later layer wrongly.
/// </para>
/// </summary>
internal static class Tier2
{
    /// <summary>
    /// Reads every packet of a single-tile codestream's one tile. See
    /// <see cref="ReadPackets(CodestreamHeader, TileComponent[], ReadOnlySpan{byte}, Rect)"/>.
    /// </summary>
    public static void ReadPackets(CodestreamHeader header, TileComponent[] components, ReadOnlySpan<byte> data)
    {
        var siz = header.Siz;
        ReadPackets(header, components, data, new Rect(siz.X0, siz.Y0, siz.X1, siz.Y1));
    }

    /// <summary>
    /// Reads every packet of one tile, populating each code-block's inclusion state and coded byte
    /// ranges.
    /// </summary>
    /// <param name="header">The parsed main header.</param>
    /// <param name="components">Each component's tile-component geometry to fill in.</param>
    /// <param name="data">The tile's coded data: every tile-part's bytes after SOD, in order.</param>
    /// <param name="tile">The tile's extent on the reference grid, unused here but kept for the caller's symmetry.</param>
    public static void ReadPackets(CodestreamHeader header, TileComponent[] components, ReadOnlySpan<byte> data, Rect tile)
    {
        _ = tile;
        var reader = new PacketStream(header.UseSopMarkers, header.UseEphMarkers);
        var layers = header.Layers;

        var resolutions = 0;
        foreach (var component in components) resolutions = Math.Max(resolutions, component.Resolutions.Length);

        // A COC can give components different numbers of decomposition levels, and a component
        // simply has no packet at a resolution it does not have.
        switch (header.Progression)
        {
            case ProgressionOrder.Lrcp:
                // B.12.1.1: layer, resolution, component, precinct.
                for (var layer = 0; layer < layers; layer++)
                    for (var r = 0; r < resolutions; r++)
                        foreach (var component in components)
                        {
                            if (r >= component.Resolutions.Length) continue;
                            foreach (var precinct in component.Resolutions[r].Precincts)
                                reader.Read(precinct, data, layer);
                        }
                break;

            case ProgressionOrder.Rlcp:
                // B.12.1.2: resolution, layer, component, precinct.
                for (var r = 0; r < resolutions; r++)
                    for (var layer = 0; layer < layers; layer++)
                        foreach (var component in components)
                        {
                            if (r >= component.Resolutions.Length) continue;
                            foreach (var precinct in component.Resolutions[r].Precincts)
                                reader.Read(precinct, data, layer);
                        }
                break;

            case ProgressionOrder.Rpcl:
                // B.12.1.3: resolution, then position (y, x), then component, then layer.
                for (var r = 0; r < resolutions; r++)
                {
                    var visits = new List<Visit>();
                    for (var c = 0; c < components.Length; c++)
                        if (r < components[c].Resolutions.Length)
                            AddVisits(visits, components[c].Resolutions[r], c, r);

                    visits.Sort(static (a, b) => Compare(a, b, componentFirst: true));
                    ReadAllLayers(ref reader, visits, data, layers);
                }
                break;

            case ProgressionOrder.Pcrl:
            {
                // B.12.1.4: position (y, x), then component, then resolution, then layer.
                var visits = new List<Visit>();
                for (var c = 0; c < components.Length; c++)
                    for (var r = 0; r < components[c].Resolutions.Length; r++)
                        AddVisits(visits, components[c].Resolutions[r], c, r);

                visits.Sort(static (a, b) => Compare(a, b, componentFirst: true));
                ReadAllLayers(ref reader, visits, data, layers);
                break;
            }

            case ProgressionOrder.Cprl:
                // B.12.1.5: component, then position (y, x), then resolution, then layer.
                for (var c = 0; c < components.Length; c++)
                {
                    var visits = new List<Visit>();
                    for (var r = 0; r < components[c].Resolutions.Length; r++)
                        AddVisits(visits, components[c].Resolutions[r], c, r);

                    visits.Sort(static (a, b) => Compare(a, b, componentFirst: false));
                    ReadAllLayers(ref reader, visits, data, layers);
                }
                break;

            default:
                throw new InvalidDataException($"JPEG 2000: progression order {header.Progression} is not defined.");
        }
    }

    /// <summary>One precinct, with what the position-driven orders sort it by.</summary>
    private readonly record struct Visit(Precinct Precinct, long Y, long X, int Component, int Resolution);

    private static void AddVisits(List<Visit> visits, Resolution resolution, int component, int r)
    {
        foreach (var precinct in resolution.Precincts)
            visits.Add(new Visit(precinct, precinct.VisitY, precinct.VisitX, component, r));
    }

    /// <summary>
    /// The position orders sort by y, then x, then the two inner loops: component before resolution
    /// for RPCL (where the resolution is fixed anyway) and PCRL, resolution alone for CPRL (where the
    /// component is). Two precincts of the same component and resolution never share a visiting
    /// point, so the order is total.
    /// </summary>
    private static int Compare(Visit a, Visit b, bool componentFirst)
    {
        var order = a.Y.CompareTo(b.Y);
        if (order != 0) return order;
        order = a.X.CompareTo(b.X);
        if (order != 0) return order;
        if (componentFirst)
        {
            order = a.Component.CompareTo(b.Component);
            return order != 0 ? order : a.Resolution.CompareTo(b.Resolution);
        }

        return a.Resolution.CompareTo(b.Resolution);
    }

    private static void ReadAllLayers(ref PacketStream reader, List<Visit> visits, ReadOnlySpan<byte> data, int layers)
    {
        foreach (var visit in visits)
            for (var layer = 0; layer < layers; layer++)
                reader.Read(visit.Precinct, data, layer);
    }

    /// <summary>The read position through a tile's packets, and the two optional markers around each.</summary>
    private struct PacketStream(bool sop, bool eph)
    {
        private int _offset;

        public void Read(Precinct precinct, ReadOnlySpan<byte> data, int layer) =>
            _offset = ReadPacket(precinct, data, _offset, layer, sop, eph);
    }

    private static int ReadPacket(Precinct precinct, ReadOnlySpan<byte> data, int offset, int layer, bool sop, bool eph)
    {
        if (offset > data.Length)
            throw new InvalidDataException("JPEG 2000: ran out of tile-part data before the last packet.");

        // A.8.1: when COD allows SOP markers, each packet MAY start with one: FF91, a length of 4
        // and a 16-bit sequence number. Nothing in it changes how the packet is read.
        if (sop && offset + 6 <= data.Length && data[offset] == 0xFF && data[offset + 1] == 0x91)
            offset += 6;

        var reader = new PacketBitReader(data[offset..]);

        // B.10.3: a leading zero says the packet is empty. It is not a corner
        // case to bolt on later -- a flat image produces one of these for almost
        // every resolution, because no code-block has anything to say.
        if (reader.ReadBit() == 0)
        {
            reader.AlignToByte();
            return SkipEph(data, offset + reader.Position, eph);
        }

        var pending = new List<(CodeBlock Block, int Length)>();

        foreach (var part in precinct.Bands)
        {
            if (part.BlocksWide == 0 || part.BlocksHigh == 0) continue;

            var band = part.Band;
            part.EnsureTagTrees();

            // B.10: the precinct's code-blocks in each band, in raster order.
            for (var by = 0; by < part.BlocksHigh; by++)
            {
                for (var bx = 0; bx < part.BlocksWide; bx++)
                {
                    var block = band.Blocks[(part.BlockY0 + by) * band.BlocksWide + part.BlockX0 + bx];

                    bool included;
                    if (block.Included)
                    {
                        // Already carrying data from an earlier layer: one bit
                        // says whether this layer adds more.
                        included = reader.ReadBit() != 0;
                    }
                    else
                    {
                        // Not yet included: the tag tree holds the layer number in
                        // which it first will be, so "included now" is "that
                        // number is at most this layer".
                        included = part.InclusionTree!.Decode(ref reader, bx, by, layer + 1);
                    }

                    if (!included) continue;

                    if (!block.Included)
                    {
                        block.Included = true;

                        // B.10.5: the number of all-zero leading bit-planes, read
                        // by raising the threshold until the value resolves.
                        // Unbounded in the spec; bounded here because the loop is
                        // driven by attacker-supplied bits and must be guaranteed
                        // to advance.
                        var threshold = 1;
                        while (!part.ZeroBitPlaneTree!.Decode(ref reader, bx, by, threshold))
                        {
                            if (++threshold > MaxZeroBitPlanes)
                                throw new InvalidDataException(
                                    $"JPEG 2000: a code-block declares more than {MaxZeroBitPlanes} " +
                                    "all-zero bit-planes, which no legal codestream does.");
                        }

                        block.ZeroBitPlanes = part.ZeroBitPlaneTree!.Value(bx, by);
                    }

                    var newPasses = ReadPassCount(ref reader);

                    // B.10.7.1: unary 1-bits each grow Lblock, which then sizes
                    // the length field. Bounded for the same reason as above.
                    while (reader.ReadBit() != 0)
                    {
                        if (++block.LBlock > MaxLBlock)
                            throw new InvalidDataException(
                                "JPEG 2000: a code-block's Lblock grew past any plausible value.");
                    }

                    // B.10.7.1: the length field is Lblock bits plus however many
                    // bits the pass count itself needs.
                    var lengthBits = block.LBlock + Log2Floor(newPasses);
                    if (lengthBits > 31)
                        throw new InvalidDataException("JPEG 2000: a code-block declares an unreadable length field.");

                    var length = reader.ReadBits(lengthBits);
                    if (length < 0)
                        throw new InvalidDataException("JPEG 2000: a code-block declares a negative length.");

                    block.PassCount += newPasses;
                    pending.Add((block, length));
                }
            }
        }

        reader.AlignToByte();

        // The bodies follow the header in the same order the header described
        // them, back to back with nothing between.
        var bodyOffset = SkipEph(data, offset + reader.Position, eph);
        foreach (var (block, length) in pending)
        {
            if (length > data.Length - bodyOffset)
                throw new InvalidDataException(
                    $"JPEG 2000: a packet declares a {length}-byte code-block body, but only " +
                    $"{data.Length - bodyOffset} bytes of tile-part data remain.");

            block.Segments.Add((bodyOffset, length));
            bodyOffset += length;
        }

        return bodyOffset;
    }

    /// <summary>
    /// A code-block cannot have more all-zero bit-planes than there are
    /// bit-planes, and Mb is bounded by the guard bits plus an 8-bit exponent.
    /// 74 is well past any legal value and keeps the tag-tree loop finite.
    /// </summary>
    private const int MaxZeroBitPlanes = 74;

    /// <summary>
    /// A.8.2: when COD allows EPH markers, FF92 may follow each packet header, before the bodies.
    /// Skipped when present; a header without one is read as though it were not allowed.
    /// </summary>
    private static int SkipEph(ReadOnlySpan<byte> data, int offset, bool eph) =>
        eph && offset + 2 <= data.Length && data[offset] == 0xFF && data[offset + 1] == 0x92 ? offset + 2 : offset;

    /// <summary>Lblock starts at 3 and only grows; 32 already makes the length field unreadable.</summary>
    private const int MaxLBlock = 32;

    /// <summary>
    /// T.800 Table B.4: the number of new coding passes, in a code whose first
    /// two values are one bit each and whose last spans 7.
    /// </summary>
    private static int ReadPassCount(ref PacketBitReader reader)
    {
        if (reader.ReadBit() == 0) return 1;
        if (reader.ReadBit() == 0) return 2;

        var value = reader.ReadBits(2);
        if (value < 3) return 3 + value;

        value = reader.ReadBits(5);
        if (value < 31) return 6 + value;

        return 37 + reader.ReadBits(7);
    }

    private static int Log2Floor(int value)
    {
        var bits = 0;
        while (value > 1)
        {
            value >>= 1;
            bits++;
        }

        return bits;
    }
}
