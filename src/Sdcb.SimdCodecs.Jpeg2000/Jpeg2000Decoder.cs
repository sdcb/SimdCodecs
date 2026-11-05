using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// A decoded JPEG 2000 image: unsigned samples for every component, with the
/// precision the codestream declared.
/// <para>
/// The components are whatever the codestream coded, after the inverse
/// multiple component transform when COD asked for one: so three components
/// coded with RCT or ICT come back as the three the encoder started from,
/// conventionally red, green and blue. What they mean is said by the JP2
/// <c>colr</c> box or the PDF image dictionary, not by the codestream, and this
/// type does not guess.
/// </para>
/// </summary>
/// <param name="Width">Samples across.</param>
/// <param name="Height">Samples down.</param>
/// <param name="BitDepth">Bits per sample, 1 to 16, the same for every component.</param>
/// <param name="Samples">
/// Row-major samples, the components of each pixel together
/// (<c>Samples[(y * Width + x) * Components + c]</c>), already DC-level-shifted and
/// clamped to the declared precision.
/// </param>
/// <param name="Components">Samples per pixel.</param>
public sealed record Jpeg2000Image(int Width, int Height, int BitDepth, ushort[] Samples, int Components = 1)
{
    /// <summary>
    /// The samples projected to 8 bits, for callers that just want to look at
    /// a one-component picture. A precision above 8 is shifted down, never
    /// rescaled, so the mapping stays exact and reversible for the common 8-bit
    /// case.
    /// </summary>
    /// <exception cref="InvalidOperationException">The image has more than one component; use <see cref="To8Bit"/>.</exception>
    public byte[] ToGray8()
    {
        if (Components != 1)
            throw new InvalidOperationException(
                $"This image has {Components} components, so it has no single grey plane. Use To8Bit for " +
                "every component, and decide from the container or the PDF what they mean.");

        return To8Bit();
    }

    /// <summary>
    /// Every component projected to 8 bits, interleaved as <see cref="Samples"/> is. A precision
    /// above 8 is shifted down, never rescaled, as in <see cref="ToGray8"/>.
    /// </summary>
    public byte[] To8Bit()
    {
        var shift = Math.Max(0, BitDepth - 8);
        var bytes = new byte[Samples.Length];
        for (var i = 0; i < Samples.Length; i++) bytes[i] = (byte)(Samples[i] >> shift);

        return bytes;
    }

    /// <summary>
    /// What the JP2 file's colour specification says the components mean, or null for a raw
    /// codestream, which says nothing about colour at all. See <see cref="Jp2Colour"/> for why
    /// this is reported rather than applied.
    /// </summary>
    public Jp2Colour? Colour { get; init; }
}

/// <summary>
/// What <see cref="Jpeg2000Decoder.ReadInfo"/> reads from a JPEG 2000 image's headers.
/// </summary>
/// <param name="Width">Samples across, at full resolution.</param>
/// <param name="Height">Samples down, at full resolution.</param>
/// <param name="Components">Components per pixel.</param>
/// <param name="BitDepth">Bits per sample of the first component; the decoder requires them all to match.</param>
/// <param name="DecompositionLevels">How many resolution levels a decode can leave out: the fewest of any component's.</param>
/// <param name="Colour">The JP2 colour specification, or null for a raw codestream.</param>
public sealed record Jpeg2000Info(
    int Width, int Height, int Components, int BitDepth, int DecompositionLevels, Jp2Colour? Colour)
{
    /// <summary>The image region on the reference grid, whose origin decides how a halving rounds.</summary>
    internal Rect Region { get; init; }

    /// <summary>
    /// The size <see cref="Jpeg2000Decoder.Decode(ReadOnlySpan{byte}, int, CancellationToken)"/> produces at this
    /// reduction, clamped as it clamps: each level left out halves each side, rounding up on the
    /// reference grid (T.800 Equation B-14), so a caller can know what a decode will cost before it
    /// runs one.
    /// </summary>
    public (int Width, int Height) SizeAt(int reduce)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reduce);
        reduce = Math.Min(reduce, DecompositionLevels);
        return (TileComponent.CeilDivPow2(Region.X1, reduce) - TileComponent.CeilDivPow2(Region.X0, reduce),
                TileComponent.CeilDivPow2(Region.Y1, reduce) - TileComponent.CeilDivPow2(Region.Y0, reduce));
    }
}

/// <summary>
/// A pure-managed JPEG 2000 decoder, clean-room from ITU-T T.800 (ISO/IEC
/// 15444-1, Part 1).
/// <para>
/// <b>What this decodes today.</b> A raw J2K codestream, or one in a JP2 file
/// (with or without a palette and component mapping, but no channel definition),
/// holding any number of 1-to-16-bit unsigned components of one precision, in any
/// number of tiles and tile-parts, coded in any number of quality layers, with any
/// precinct sizes, in any of the five progression orders, with or without SOP and
/// EPH markers: losslessly with the reversible 5/3 wavelet, or lossily with the
/// irreversible 9/7 wavelet and scalar quantization, with or without the matching
/// component transform (RCT or ICT). Of the code-block styles, context reset,
/// predictable termination and segmentation symbols are decoded. Anything outside
/// it (POC, PPM / PPT, RGN, tile-part header overrides, the bypass, per-pass
/// termination and vertically causal code-block styles, subsampling, signed or
/// mixed-precision components) raises <see cref="NotSupportedException"/> naming
/// the feature — never a plausible-looking wrong raster.
/// </para>
/// <para>
/// Issue #1's JPEG 2000 files are the real-world check on all of that; see
/// <c>Issue1Jpeg2000CorpusTests</c> in the test project for how to run them after
/// changing this decoder.
/// </para>
/// <para>
/// <b>Threads.</b> A decode uses every logical processor: within a tile the code-blocks, the wavelet's
/// rows and column strips, the component transform and the level shift are shared out, and the output
/// is bit-identical to a one-thread decode. Tiles are decoded one after the other, so memory stays at
/// the output plus one tile's working set. Cancellation is honoured between tiles and between blocks.
/// </para>
/// <para>
/// <b>Why there is no partial-credit mode.</b> JPEG 2000 has no early pipeline
/// stage that produces an image on its own: marker parsing, tier-2 packet
/// headers, tier-1 EBCOT, the inverse DWT and the DC level shift must all be
/// right before a single correct pixel exists. So the staging is by feature, not
/// by stage, and a codestream either falls inside the envelope or is refused at
/// the header.
/// </para>
/// </summary>
public static class Jpeg2000Decoder
{
    /// <summary>
    /// True when <paramref name="data"/> begins with a raw JPEG 2000 codestream
    /// (SOC followed by SIZ).
    /// </summary>
    public static bool IsCodestream(ReadOnlySpan<byte> data) => CodestreamReader.LooksLikeCodestream(data);

    /// <summary>
    /// True when <paramref name="data"/> begins with the JP2 file format's signature box
    /// (<c>00 00 00 0C 6A 50 20 20 0D 0A 87 0A</c>).
    /// </summary>
    public static bool IsJp2(ReadOnlySpan<byte> data) => Jp2Reader.LooksLikeJp2(data);

    /// <summary>
    /// Decodes a JPEG 2000 image: a raw codestream, or a JP2 file, whose colour specification
    /// comes back on <see cref="Jpeg2000Image.Colour"/>.
    /// </summary>
    /// <param name="data">The JPEG 2000 image data.</param>
    /// <param name="cancellationToken">A cancellation token to monitor for cancellation requests.</param>
    /// <exception cref="InvalidDataException">The data is malformed, truncated or over the resource limits.</exception>
    /// <exception cref="NotSupportedException">It is well-formed but uses a feature this decoder does not implement.</exception>
    public static Jpeg2000Image Decode(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default) =>
        Decode(data, reduce: 0, cancellationToken);

    /// <summary>
    /// Decodes a JPEG 2000 image at reduced resolution: the <paramref name="reduce"/> finest
    /// resolution levels are left out, so each side is halved that many times, rounding up. This is
    /// rung 5 of the roadmap, and it is a decode of less, not a decode then a shrink: the levels left
    /// out are never entropy-decoded and never get coefficient storage, so a reduction of 2 does about
    /// a sixteenth of the full image's tier-1 and wavelet work. Their packet headers are still read,
    /// because in the codestream they sit between the packets that matter.
    /// <para>
    /// <paramref name="reduce"/> is clamped to the decomposition levels the codestream has (the
    /// fewest of any component's), so asking for more than there are gives the smallest image there
    /// is. The result's <see cref="Jpeg2000Image.Width"/> and <see cref="Jpeg2000Image.Height"/> say
    /// what was produced. <see cref="ReductionFor"/> picks a reduction for a wanted size. It is
    /// <c>opj_decompress</c>'s <c>-r</c>, and checked against it.
    /// </para>
    /// </summary>
    /// <param name="data">The JPEG 2000 image data.</param>
    /// <param name="reduce">How many resolution levels to leave out (0 for full resolution).</param>
    /// <param name="cancellationToken">A cancellation token to monitor for cancellation requests.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reduce"/> is negative.</exception>
    /// <exception cref="InvalidDataException">The data is malformed, truncated or over the resource limits.</exception>
    /// <exception cref="NotSupportedException">It is well-formed but uses a feature this decoder does not implement.</exception>
    public static Jpeg2000Image Decode(ReadOnlySpan<byte> data, int reduce, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reduce);
        if (!Jp2Reader.LooksLikeJp2(data)) return DecodeCodestream(data, reduce, cancellationToken);

        var layout = Jp2Reader.Read(data);
        var image = DecodeCodestream(data.Slice(layout.CodestreamStart, layout.CodestreamLength), reduce, cancellationToken);
        if (layout.Channels is { } channels) image = ApplyChannels(image, channels);
        return image with { Colour = layout.Colour };
    }

    /// <summary>
    /// What a JPEG 2000 image is, read from its headers without decoding a coded byte: its size,
    /// components, precision, how far it can be reduced, and its JP2 colour. Cheap enough to ask of
    /// every image before deciding whether, and how small, to decode it.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is malformed or truncated.</exception>
    /// <exception cref="NotSupportedException">It uses a feature this decoder does not implement, so it would not decode either.</exception>
    public static Jpeg2000Info ReadInfo(ReadOnlySpan<byte> data)
    {
        var codestream = data;
        Jp2Colour? colour = null;
        Jp2ChannelMapping? channels = null;
        if (Jp2Reader.LooksLikeJp2(data))
        {
            var layout = Jp2Reader.Read(data);
            codestream = data.Slice(layout.CodestreamStart, layout.CodestreamLength);
            colour = layout.Colour;
            channels = layout.Channels;
        }

        var header = CodestreamReader.Read(codestream);
        var siz = header.Siz;
        var levels = int.MaxValue;
        foreach (var coding in header.ComponentCoding) levels = Math.Min(levels, coding.DecompositionLevels);

        var components = siz.Components.Length;
        var depth = siz.Components[0].BitDepth;
        if (channels is not null)
        {
            depth = channels.OutputDepth(ComponentDepths(components, depth));
            components = channels.Map.Length;
        }

        return new Jpeg2000Info(siz.Width, siz.Height, components, depth, levels, colour)
        {
            Region = new Rect(siz.X0, siz.Y0, siz.X1, siz.Y1),
        };
    }

    /// <summary>
    /// The largest reduction for <see cref="Decode(ReadOnlySpan{byte}, int, CancellationToken)"/> that still leaves an
    /// image whose longer side is at least <paramref name="minLongEdge"/>: what a caller wants that
    /// will shrink the result to its own size, as a thumbnail does, and wants no less detail than
    /// that size can show.
    /// </summary>
    /// <param name="width">The full image's width.</param>
    /// <param name="height">The full image's height.</param>
    /// <param name="minLongEdge">The smallest long edge the decoded image may have.</param>
    public static int ReductionFor(int width, int height, int minLongEdge)
    {
        var edge = Math.Max(width, height);
        var reduce = 0;
        while (edge > 1 && (edge + 1) / 2 >= minLongEdge)
        {
            edge = (edge + 1) / 2;
            reduce++;
        }

        return reduce;
    }

    private static int[] ComponentDepths(int components, int depth)
    {
        var depths = new int[components];
        Array.Fill(depths, depth);
        return depths;
    }

    /// <summary>
    /// The JP2 component mapping (T.800 I.5.3.5) applied to the decoded components: each output
    /// channel is either a component as it is or a palette column looked up by a component's sample.
    /// An index past the last palette entry takes the last entry, which only a damaged file needs.
    /// </summary>
    private static Jpeg2000Image ApplyChannels(Jpeg2000Image image, Jp2ChannelMapping channels)
    {
        var depth = channels.OutputDepth(ComponentDepths(image.Components, image.BitDepth));
        var map = channels.Map;
        var pixels = (long)image.Width * image.Height;
        var output = new ushort[pixels * map.Length];

        for (var i = 0; i < map.Length; i++)
        {
            var (component, column) = map[i];
            var source = image.Samples;
            var stride = image.Components;
            for (long p = 0, o = i, s = component; p < pixels; p++, o += map.Length, s += stride)
            {
                var value = source[s];
                output[o] = column < 0
                    ? value
                    : channels.Values[Math.Min(value, channels.Entries - 1) * channels.Columns + column];
            }
        }

        return new Jpeg2000Image(image.Width, image.Height, depth, output, map.Length);
    }

    private static Jpeg2000Image DecodeCodestream(ReadOnlySpan<byte> data, int reduce, CancellationToken cancellationToken = default)
    {
        var header = CodestreamReader.Read(data);
        var siz = header.Siz;

        // A component can have its own number of decomposition levels (COC), and every component
        // must come out the same size, so the reduction is capped by the one with the fewest.
        var levels = int.MaxValue;
        foreach (var coding in header.ComponentCoding) levels = Math.Min(levels, coding.DecompositionLevels);
        reduce = Math.Min(reduce, levels);

        var tiles = GroupTileParts(header);
        var componentCount = siz.Components.Length;

        // The decoded image's region on the reference grid at this reduction (Equation B-14): the
        // tiles' reduced regions, rounded the same way, cover it exactly.
        var region = new Rect(
            TileComponent.CeilDivPow2(siz.X0, reduce), TileComponent.CeilDivPow2(siz.Y0, reduce),
            TileComponent.CeilDivPow2(siz.X1, reduce), TileComponent.CeilDivPow2(siz.Y1, reduce));
        var total = region.Area * componentCount;
        if (total > Array.MaxLength)
            throw new InvalidDataException(
                $"JPEG 2000: a {region.Width}x{region.Height} image of {componentCount} components is " +
                $"{total:N0} samples, more than one array can hold.");

        // Allocated once and written tile by tile, so a decode holds the output and one tile's
        // working set at a time. A codestream must carry at least one tile-part per tile (checked
        // above), so the tile count, and with it this loop, is bounded by the data itself.
        var output = new ushort[total];
        var tilesWide = (int)siz.TilesWide;

        for (var t = 0; t < tiles.Length; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // T.800 Equation B-7: the tile's extent, clipped to the image region.
            var p = t % tilesWide;
            var q = t / tilesWide;
            var tile = new Rect(
                (int)Math.Max(siz.TileX0 + (long)p * siz.TileWidth, siz.X0),
                (int)Math.Max(siz.TileY0 + (long)q * siz.TileHeight, siz.Y0),
                (int)Math.Min(siz.TileX0 + (long)(p + 1) * siz.TileWidth, siz.X1),
                (int)Math.Min(siz.TileY0 + (long)(q + 1) * siz.TileHeight, siz.Y1));

            DecodeTile(header, data, tiles[t], tile, reduce, region, output, cancellationToken);
        }

        return new Jpeg2000Image(region.Width, region.Height, siz.Components[0].BitDepth, output, componentCount);
    }

    /// <summary>
    /// Every tile's tile-parts, in TPsot order. T.800 A.4.2 lets the parts of different tiles
    /// interleave, but each tile's own parts carry its packets in sequence, and every tile has at
    /// least one.
    /// </summary>
    private static List<TilePart>[] GroupTileParts(CodestreamHeader header)
    {
        var count = (int)header.Siz.TileCount;
        if (header.TileParts.Count < count)
            throw new InvalidDataException(
                $"JPEG 2000: SIZ declares {count:N0} tiles, but the codestream has only " +
                $"{header.TileParts.Count:N0} tile-parts.");

        var tiles = new List<TilePart>[count];
        foreach (var part in header.TileParts) (tiles[part.TileIndex] ??= []).Add(part);

        for (var t = 0; t < count; t++)
        {
            var parts = tiles[t]
                ?? throw new InvalidDataException($"JPEG 2000: tile {t} has no tile-part.");
            parts.Sort(static (a, b) => a.PartIndex.CompareTo(b.PartIndex));
            for (var i = 0; i < parts.Count; i++)
            {
                if (parts[i].PartIndex != i)
                    throw new InvalidDataException(
                        $"JPEG 2000: tile {t}'s tile-parts are numbered {parts[i].PartIndex} where {i} was due.");
            }
        }

        return tiles;
    }

    /// <summary>One tile, from its packets to its samples in <paramref name="output"/>.</summary>
    private static void DecodeTile(
        CodestreamHeader header, ReadOnlySpan<byte> data, List<TilePart> parts, Rect tile, int reduce, Rect region,
        ushort[] output, CancellationToken cancellationToken)
    {
        var siz = header.Siz;

        // Each tile is metered against its own extent: the budget bounds amplification, which is
        // per tile, and a tile's buffers are released before the next one is built.
        var budget = new Jpeg2000SampleBudget(
            Jpeg2000Limits.BudgetFor(tile.Width, tile.Height, siz.Components.Length));

        var components = new TileComponent[siz.Components.Length];
        for (var c = 0; c < components.Length; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            components[c] = TileComponent.Build(
                header, c, tile, budget, header.ComponentCoding[c].ResolutionCount - reduce);
        }

        // The packets run on from one tile-part into the next, so a tile split into parts is read
        // as their bytes put back together. Always an array (never the caller's span): the code-block
        // workers below read it from other threads.
        var length = 0;
        foreach (var part in parts) length += part.Length;
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var offset = 0;
            foreach (var part in parts)
            {
                data.Slice(part.Start, part.Length).CopyTo(rented.AsSpan(offset));
                offset += part.Length;
            }

            var tileData = rented.AsMemory(0, length);

            // Tier-2 first, over the whole tile: it discovers which code-blocks carry data and
            // where, without decoding any of it. The components' packets are interleaved, so it
            // reads them all at once.
            Tier2.ReadPackets(header, components, tileData.Span, tile);

            // Then tier-1. Code-blocks are independent (each reads its own coded bytes and writes its
            // own rectangle of its band), so they are decoded by as many threads as the machine has;
            // the result is bit-identical to the serial order, which the tests compare.
            DecodeBlocks(components, tileData, cancellationToken);

            // The inverse wavelet, component by component: in integers for a 5/3 component, in floats
            // for a 9/7 one. The budget is charged serially before any of them allocates; the
            // components then reconstruct in parallel (each owns its buffers).
            var integers = new int[components.Length][];
            var reals = new float[components.Length][];
            foreach (var tileComponent in components)
            {
                var decodedBounds = tileComponent.Resolutions[tileComponent.DecodedResolutions - 1].Bounds;
                budget.Charge(decodedBounds.Width, decodedBounds.Height);
            }

            RunParallel(components.Length, cancellationToken, c =>
            {
                var tileComponent = components[c];
                if (tileComponent.Irreversible)
                    reals[c] = InverseWavelet.ReconstructIrreversible(tileComponent, tileComponent.DecodedResolutions);
                else
                    integers[c] = InverseWavelet.Reconstruct(tileComponent, tileComponent.DecodedResolutions);
            });

            // G.1.2: the component transform comes between the wavelet and the level shift. The
            // reader has already checked that components 0 to 2 share one wavelet, which picks RCT
            // for the 5/3 and ICT for the 9/7.
            if (header.MultipleComponentTransform)
            {
                if (components[0].Irreversible) ComponentTransform.InverseIct(reals[0], reals[1], reals[2]);
                else ComponentTransform.InverseRct(integers[0], integers[1], integers[2]);
            }

            var first = components[0];
            LevelShift(integers, reals, first.Resolutions[first.DecodedResolutions - 1].Bounds,
                siz.Components[0].BitDepth, output, region);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Tier-1 over every code-block of the tile. The blocks are flattened into one list, and each
    /// worker thread owns a <see cref="BlockState"/> (its scratch), created on its first block.
    /// </summary>
    private static void DecodeBlocks(TileComponent[] components, ReadOnlyMemory<byte> tileData, CancellationToken cancellationToken)
    {
        var work = new List<(Subband Band, CodeBlock Block)>();
        foreach (var tileComponent in components)
        {
            for (var r = 0; r < tileComponent.DecodedResolutions; r++)
            {
                foreach (var band in tileComponent.Resolutions[r].Bands)
                {
                    foreach (var block in band.Blocks)
                    {
                        if (block.PassCount > 0) work.Add((band, block));
                    }
                }
            }
        }

        var degree = Jpeg2000Ablation.EffectiveDegree;
        if (degree == 1 || work.Count < 2)
        {
            var state = new BlockState();
            for (var i = 0; i < work.Count; i++)
            {
                if ((i & 15) == 0) cancellationToken.ThrowIfCancellationRequested();
                BlockDecoder.Decode(work[i].Band, work[i].Block, tileData.Span, state);
            }

            return;
        }

        try
        {
            Parallel.For(0, work.Count,
                new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken },
                static () => new BlockState(),
                (i, _, state) =>
                {
                    BlockDecoder.Decode(work[i].Band, work[i].Block, tileData.Span, state);
                    return state;
                },
                static _ => { });
        }
        catch (AggregateException e) when (e.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
        }
    }

    /// <summary>Runs <paramref name="body"/> for 0..count-1, in parallel when the machine and the switch allow.</summary>
    private static void RunParallel(int count, CancellationToken cancellationToken, Action<int> body)
    {
        var degree = Jpeg2000Ablation.EffectiveDegree;
        if (degree == 1 || count < 2)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                body(i);
            }

            return;
        }

        try
        {
            Parallel.For(0, count,
                new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken }, body);
        }
        catch (AggregateException e) when (e.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
        }
    }

    /// <summary>
    /// T.800 G.1.2: undo the DC level shift the encoder applied to make an
    /// unsigned component signed, then clamp to the declared precision, and
    /// interleave the components into one sample per component per pixel, at the
    /// tile's place in the image.
    /// <para>
    /// The clamp is not belt-and-braces. A lossless codestream reconstructs
    /// exactly and needs none, but a truncated or hostile one can produce
    /// coefficients outside the range the component claims, and writing those
    /// into a <see cref="ushort"/> unchecked would wrap a bright sample to a
    /// dark one.
    /// </para>
    /// <para>
    /// A 9/7 component arrives in floats and is rounded here, to nearest with
    /// ties to even, which is the one rounding of the irreversible path: the
    /// wavelet and ICT both work on unrounded values.
    /// </para>
    /// </summary>
    /// <param name="integers">Each reversible component's samples, null for an irreversible one.</param>
    /// <param name="reals">Each irreversible component's samples, null for a reversible one.</param>
    /// <param name="bounds">The tile's decoded extent, on the same grid as <paramref name="region"/>.</param>
    /// <param name="output">The whole image's interleaved samples.</param>
    /// <param name="region">The whole image's decoded extent.</param>
    private static void LevelShift(
        int[]?[] integers, float[]?[] reals, Rect bounds, int bitDepth, ushort[] output, Rect region)
    {
        var shift = 1 << (bitDepth - 1);
        var maximum = (1 << bitDepth) - 1;
        var count = integers.Length;
        var width = bounds.Width;

        // Rows of the tile land on disjoint rows of the output, so threads take bands of them.
        Jpeg2000Parallel.ForRanges(bounds.Height, 16, (long)width * count, (yStart, yEnd) =>
        {
            for (var c = 0; c < count; c++)
            {
                for (var y = yStart; y < yEnd; y++)
                {
                    var o = (((long)(bounds.Y0 - region.Y0 + y) * region.Width) + (bounds.X0 - region.X0)) * count + c;
                    var i = y * width;
                    if (integers[c] is { } plane)
                    {
                        for (var x = 0; x < width; x++, i++, o += count)
                            output[o] = (ushort)Math.Clamp(plane[i] + shift, 0, maximum);
                    }
                    else
                    {
                        var real = reals[c]!;
                        for (var x = 0; x < width; x++, i++, o += count)
                        {
                            // Clamped as a float, so a value no int can hold still lands on a rail.
                            output[o] = (ushort)Math.Clamp(MathF.Round(real[i]) + shift, 0f, maximum);
                        }
                    }
                }
            }
        });
    }
}
