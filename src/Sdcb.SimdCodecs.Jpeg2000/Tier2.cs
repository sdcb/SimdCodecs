using System;
using System.Collections.Generic;
using System.IO;

namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// Tier-2: the packet layer (T.800 B.9 and B.10). It reads packet headers to
/// discover <em>which</em> code-blocks carry data, how many coding passes each
/// got and how many bytes those passes occupy, then hands the byte ranges to
/// tier-1. It never touches a coefficient.
/// <para>
/// The envelope keeps the packet sequence simple: one tile, and maximal precincts
/// so each resolution of each component is a single precinct. Under LRCP that is
/// one packet per layer, resolution and component, nested in that order. The five
/// progression orders differ only in how they nest those loops, which is why
/// <see cref="CodestreamReader"/> still refuses the other four rather than
/// accepting them for free: with several layers and components they really do
/// visit the packets in different sequences, and only this one has a fixture.
/// </para>
/// <para>
/// <b>Layers are where tier-2 becomes stateful.</b> A code-block's inclusion,
/// zero bit-planes, <c>Lblock</c> and pass count, and its precinct's tag trees,
/// all carry over from one layer's packet to the next. A decoder that rebuilt any
/// of them per packet would read layer 0 correctly and every later layer wrongly,
/// which is hazard 5 of the roadmap and the reason the multi-layer fixtures exist.
/// </para>
/// </summary>
internal static class Tier2
{
    /// <summary>
    /// Reads every packet of the tile-part, populating each code-block's
    /// inclusion state and coded byte ranges.
    /// </summary>
    /// <param name="header">The parsed main header.</param>
    /// <param name="components">Each component's tile-component geometry to fill in.</param>
    /// <param name="data">The tile-part's coded data, starting just after SOD.</param>
    public static void ReadPackets(CodestreamHeader header, TileComponent[] components, ReadOnlySpan<byte> data)
    {
        var offset = 0;

        var resolutions = 0;
        foreach (var component in components) resolutions = Math.Max(resolutions, component.Resolutions.Length);

        // B.12.1.1, LRCP: layer, then resolution, then component, then precinct, the last of which is
        // length 1 here. A COC can give components different numbers of decomposition levels, and a
        // component simply has no packet at a resolution it does not have.
        for (var layer = 0; layer < header.Layers; layer++)
        {
            for (var r = 0; r < resolutions; r++)
            {
                foreach (var component in components)
                {
                    if (r >= component.Resolutions.Length) continue;
                    offset = ReadPacket(component.Resolutions[r], data, offset, layer);
                }
            }
        }
    }

    private static int ReadPacket(Resolution resolution, ReadOnlySpan<byte> data, int offset, int layer)
    {
        if (offset > data.Length)
            throw new InvalidDataException("JPEG 2000: ran out of tile-part data before the last packet.");

        var reader = new PacketBitReader(data[offset..]);

        // B.10.3: a leading zero says the packet is empty. It is not a corner
        // case to bolt on later -- a flat image produces one of these for almost
        // every resolution, because no code-block has anything to say.
        if (reader.ReadBit() == 0)
        {
            reader.AlignToByte();
            return offset + reader.Position;
        }

        var pending = new List<(CodeBlock Block, int Length)>();

        foreach (var band in resolution.Bands)
        {
            if (band.Bounds.IsEmpty) continue;

            // One precinct per resolution, so one pair of tag trees per band,
            // covering the whole code-block grid. Rung 3 makes these per-precinct
            // and must keep them alive across layers -- see TagTree's remarks.
            band.EnsureTagTrees();

            for (var by = 0; by < band.BlocksHigh; by++)
            {
                for (var bx = 0; bx < band.BlocksWide; bx++)
                {
                    var block = band.Blocks[by * band.BlocksWide + bx];

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
                        included = band.InclusionTree!.Decode(ref reader, bx, by, layer + 1);
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
                        while (!band.ZeroBitPlaneTree!.Decode(ref reader, bx, by, threshold))
                        {
                            if (++threshold > MaxZeroBitPlanes)
                                throw new InvalidDataException(
                                    $"JPEG 2000: a code-block declares more than {MaxZeroBitPlanes} " +
                                    "all-zero bit-planes, which no legal codestream does.");
                        }

                        block.ZeroBitPlanes = band.ZeroBitPlaneTree!.Value(bx, by);
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
        var bodyOffset = offset + reader.Position;
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
