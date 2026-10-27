using System;
using System.IO;

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
    /// The size <see cref="Jpeg2000Decoder.Decode(ReadOnlySpan{byte}, int)"/> produces at this
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
/// with no palette, component mapping or channel definition, holding any number of
/// 1-to-16-bit unsigned components of one precision, in a single tile, at full
/// resolution, coded in any number of quality layers with maximal precincts and
/// LRCP progression: losslessly with the reversible 5/3 wavelet, or lossily with
/// the irreversible 9/7 wavelet and scalar quantization, with or without the
/// matching component transform (RCT or ICT). Quality layers are supported
/// because every <c>/JPXDecode</c> image measured in real PDFs has several.
/// Anything outside it raises <see cref="NotSupportedException"/> naming the
/// feature and the rung that owns it — never a plausible-looking wrong raster.
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
    /// <exception cref="InvalidDataException">The data is malformed, truncated or over the resource limits.</exception>
    /// <exception cref="NotSupportedException">It is well-formed but uses a feature this decoder does not implement.</exception>
    public static Jpeg2000Image Decode(ReadOnlySpan<byte> data) => Decode(data, reduce: 0);

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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reduce"/> is negative.</exception>
    /// <exception cref="InvalidDataException">The data is malformed, truncated or over the resource limits.</exception>
    /// <exception cref="NotSupportedException">It is well-formed but uses a feature this decoder does not implement.</exception>
    public static Jpeg2000Image Decode(ReadOnlySpan<byte> data, int reduce)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reduce);
        if (!Jp2Reader.LooksLikeJp2(data)) return DecodeCodestream(data, reduce);

        var layout = Jp2Reader.Read(data);
        return DecodeCodestream(data.Slice(layout.CodestreamStart, layout.CodestreamLength), reduce) with
        {
            Colour = layout.Colour,
        };
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
        if (Jp2Reader.LooksLikeJp2(data))
        {
            var layout = Jp2Reader.Read(data);
            codestream = data.Slice(layout.CodestreamStart, layout.CodestreamLength);
            colour = layout.Colour;
        }

        var header = CodestreamReader.Read(codestream);
        var siz = header.Siz;
        var levels = int.MaxValue;
        foreach (var coding in header.ComponentCoding) levels = Math.Min(levels, coding.DecompositionLevels);

        return new Jpeg2000Info(siz.Width, siz.Height, siz.Components.Length, siz.Components[0].BitDepth, levels, colour)
        {
            Region = new Rect(siz.X0, siz.Y0, siz.X1, siz.Y1),
        };
    }

    /// <summary>
    /// The largest reduction for <see cref="Decode(ReadOnlySpan{byte}, int)"/> that still leaves an
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

    private static Jpeg2000Image DecodeCodestream(ReadOnlySpan<byte> data, int reduce)
    {
        var header = CodestreamReader.Read(data);
        var siz = header.Siz;

        // A component can have its own number of decomposition levels (COC), and every component
        // must come out the same size, so the reduction is capped by the one with the fewest.
        var levels = int.MaxValue;
        foreach (var coding in header.ComponentCoding) levels = Math.Min(levels, coding.DecompositionLevels);
        reduce = Math.Min(reduce, levels);

        var budget = new Jpeg2000SampleBudget(
            Jpeg2000Limits.BudgetFor(siz.Width, siz.Height, siz.Components.Length));

        var components = new TileComponent[siz.Components.Length];
        for (var c = 0; c < components.Length; c++)
        {
            components[c] = TileComponent.Build(
                header, c, budget, header.ComponentCoding[c].ResolutionCount - reduce);
        }

        var part = header.TileParts[0];
        var tilePartData = data.Slice(part.Start, part.Length);

        // Tier-2 first, over the whole tile-part: it discovers which code-blocks
        // carry data and where, without decoding any of it. The components'
        // packets are interleaved in the tile-part, so it reads them all at once.
        Tier2.ReadPackets(header, components, tilePartData);

        // Then tier-1, block by block, and the inverse wavelet, component by
        // component: in integers for a 5/3 component, in floats for a 9/7 one.
        var integers = new int[components.Length][];
        var reals = new float[components.Length][];
        var blockState = new BlockState();
        for (var c = 0; c < components.Length; c++)
        {
            var tile = components[c];
            for (var r = 0; r < tile.DecodedResolutions; r++)
            {
                foreach (var band in tile.Resolutions[r].Bands)
                {
                    foreach (var block in band.Blocks)
                    {
                        BlockDecoder.Decode(band, block, tilePartData, blockState);
                    }
                }
            }

            var decodedBounds = tile.Resolutions[tile.DecodedResolutions - 1].Bounds;
            budget.Charge(decodedBounds.Width, decodedBounds.Height);
            if (tile.Irreversible) reals[c] = InverseWavelet.ReconstructIrreversible(tile, tile.DecodedResolutions);
            else integers[c] = InverseWavelet.Reconstruct(tile, tile.DecodedResolutions);
        }

        // G.1.2: the component transform comes between the wavelet and the
        // level shift. The reader has already checked that components 0 to 2
        // share one wavelet, which picks RCT for the 5/3 and ICT for the 9/7.
        if (header.MultipleComponentTransform)
        {
            if (components[0].Irreversible) ComponentTransform.InverseIct(reals[0], reals[1], reals[2]);
            else ComponentTransform.InverseRct(integers[0], integers[1], integers[2]);
        }

        var first = components[0];
        return LevelShift(
            integers, reals, first.Resolutions[first.DecodedResolutions - 1].Bounds, siz.Components[0].BitDepth);
    }

    /// <summary>
    /// T.800 G.1.2: undo the DC level shift the encoder applied to make an
    /// unsigned component signed, then clamp to the declared precision, and
    /// interleave the components into one sample per component per pixel.
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
    private static Jpeg2000Image LevelShift(int[]?[] integers, float[]?[] reals, Rect bounds, int bitDepth)
    {
        var shift = 1 << (bitDepth - 1);
        var maximum = (1 << bitDepth) - 1;
        var count = integers.Length;

        var output = new ushort[(long)bounds.Width * bounds.Height * count];
        for (var c = 0; c < count; c++)
        {
            if (integers[c] is { } plane)
            {
                for (int i = 0, o = c; i < plane.Length; i++, o += count)
                {
                    output[o] = (ushort)Math.Clamp(plane[i] + shift, 0, maximum);
                }
            }
            else
            {
                var real = reals[c]!;
                for (int i = 0, o = c; i < real.Length; i++, o += count)
                {
                    // Clamped as a float, so a value no int can hold still lands on a rail.
                    output[o] = (ushort)Math.Clamp(MathF.Round(real[i]) + shift, 0f, maximum);
                }
            }
        }

        return new Jpeg2000Image(bounds.Width, bounds.Height, bitDepth, output, count);
    }
}
