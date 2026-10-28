using System.IO.Compression;

namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// Optional metadata for <see cref="PngWriter"/>. All fields default to
/// "don't emit the chunk"; passing a populated <see cref="PngWriteOptions"/>
/// to one of the <c>Encode(..., PngWriteOptions)</c> overloads adds the
/// corresponding ancillary chunks to the PNG output, in the chunk-order
/// PNG spec §5.6 requires (most ancillary chunks before IDAT; eXIf may be
/// before or after IDAT, we emit before).
/// </summary>
public sealed record PngWriteOptions
{
    /// <summary>
    /// Raw ICC profile bytes. When non-null, an <c>iCCP</c> chunk is
    /// emitted with <see cref="IccProfileName"/> as the keyword. Use
    /// <c>Sdcb.SimdCodecs.Color.Icc.IccProfiles.SRgbV4</c> for a pre-bundled
    /// sRGB v4 profile.
    /// </summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>Keyword for the <c>iCCP</c> chunk; defaults to "ICC profile" (matches libpng / Adobe convention).</summary>
    public string IccProfileName { get; init; } = "ICC profile";

    /// <summary>
    /// When set, emit an <c>sRGB</c> chunk declaring the rendering intent.
    /// Per PNG spec: 0=Perceptual, 1=Relative Colorimetric, 2=Saturation,
    /// 3=Absolute Colorimetric. The spec also says <c>iCCP</c> and
    /// <c>sRGB</c> are mutually exclusive — if both are populated, only
    /// <c>iCCP</c> is emitted (with a warning suppressed; calling code is
    /// expected to pick one).
    /// </summary>
    public byte? SrgbRenderingIntent { get; init; }

    /// <summary>
    /// When set, emit a <c>gAMA</c> chunk with this image gamma (e.g.
    /// 0.45455 for sRGB-style 1/2.2). PNG stores it as a u32 of
    /// <c>round(gamma × 100000)</c>.
    /// </summary>
    public double? Gamma { get; init; }

    /// <summary>When set, emit a <c>pHYs</c> chunk: the intended pixel size (e.g. <see cref="PngPhysicalDimensions.FromDpi"/>) or aspect ratio.</summary>
    public PngPhysicalDimensions? PhysicalDimensions { get; init; }

    /// <summary>
    /// When set, emit a <c>tRNS</c> chunk making the pixels of exactly this colour fully transparent: one
    /// sample for a greyscale image, three (red, green, blue) for an RGB one, in the image's sample range
    /// (0-255 for 8-bit, 0-65535 for 16-bit). Only for output without an alpha channel — the greyscale and
    /// RGB entry points, or RGBA with <see cref="DiscardAlpha"/> — and not together with
    /// <see cref="LosslessReduction"/>, which finds a transparent colour itself where one fits; either
    /// misuse is an <see cref="System.ArgumentException"/>.
    /// </summary>
    public ushort[]? TransparentColor { get; init; }

    /// <summary>
    /// Write the smallest PNG layout that still holds every sample exactly, found by one scan of the image
    /// before it is encoded: opaque alpha dropped, binary alpha turned into a tRNS transparent colour,
    /// equal channels written as greyscale, 16-bit samples that are 8-bit values (v·257) written as 8-bit,
    /// greyscale on a coarser scale written at 4, 2 or 1 bits, and at most 256 colours written as indexed
    /// colour (PLTE, with tRNS alpha), at 1, 2, 4 or 8 bits per pixel.
    /// </summary>
    /// <remarks>
    /// <para>Lossless in the PNG sense: every decoder that reads the file back to the caller's layout
    /// (8-bit RGBA, 16-bit RGBA, ...) gets exactly the samples that were written, since PNG widens an 8-bit
    /// value v to 16 bits as v·257 and a sub-byte grey level by the same bit replication. What changes is
    /// the file's colour type and depth, so <see cref="PngReader.Decode"/> returns the reduced layout
    /// (indexed, say, with <see cref="PngImage.Palette"/>) where it used to return the caller's; the RGBA
    /// projections do not change. Off by default for that reason.</para>
    /// <para>Screenshots, UI, charts, icons, line art and scanned text typically shrink severalfold; a
    /// photograph keeps its layout and pays only the scan, which ends its palette search within a few
    /// hundred pixels. Nothing is quantised: an image with more than 256 colours stays truecolour.</para>
    /// <para>With an <see cref="IccProfile"/> the colour model is kept (greyscale stays greyscale, colour
    /// stays colour), because a profile describes one or the other.</para>
    /// </remarks>
    public bool LosslessReduction { get; init; }

    /// <summary>When set, emit a <c>cHRM</c> chunk with the supplied primaries + white point.</summary>
    public ChromaticityChunk? Chromaticity { get; init; }

    /// <summary>When set, emit an <c>eXIf</c> chunk with the raw EXIF blob.</summary>
    public byte[]? Exif { get; init; }

    /// <summary>
    /// Textual entries (<see cref="PngText"/>), each written as an <c>iTXt</c> chunk after the image data, in
    /// order: UTF-8 text of any length, deflated when over <see cref="PngWriter.TextCompressionThreshold"/> bytes.
    /// A keyword the spec does not allow (<see cref="PngText.IsValidKeyword"/>) is an <see cref="System.ArgumentException"/>.
    /// </summary>
    public IReadOnlyList<PngText>? Text { get; init; }

    /// <summary>
    /// When set, emit a PNG-3 <c>cICP</c> chunk — Coding-Independent Code
    /// Points declaring color primaries + transfer function. This is how
    /// PNG-3 signals HDR (e.g. <see cref="CicpChunk.Hdr10Pq"/>).
    /// </summary>
    public CicpChunk? Cicp { get; init; }

    /// <summary>When set, emit a PNG-3 <c>mDCv</c> Mastering Display Color Volume chunk.</summary>
    public MdcvChunk? Mdcv { get; init; }

    /// <summary>When set, emit a PNG-3 <c>cLLI</c> Content Light Level Information chunk.</summary>
    public ClliChunk? Clli { get; init; }

    /// <summary>
    /// Write RGB (PNG colour type 2) rather than RGBA, discarding the alpha channel of an RGBA
    /// input (or greyscale rather than greyscale + alpha). Ignored by the grayscale and packed-RGB
    /// entry points, which have no alpha to drop.
    /// </summary>
    /// <remarks>
    /// <para>A render with no transparency -- an astronomical frame, a chart, anything already
    /// composited against its background -- still carries a constant-0xFF(FF) alpha plane, and that
    /// plane is filtered, scored, deflated and stored exactly like real data. Dropping it removes a
    /// quarter of the encoder's per-byte work as well as a quarter of the bytes handed to deflate,
    /// so this is a speed option at least as much as a size one.</para>
    /// <para>The caller asserts the image is opaque: the alpha samples are not examined, because
    /// discovering a transparent pixel halfway through a multi-second encode is no more useful than
    /// not having looked (<see cref="LosslessReduction"/> is the option that looks). When the source is
    /// already packed RGB, use <c>PngWriter.EncodeRgb8</c> / <c>PngWriter.EncodeRgb16</c> instead.</para>
    /// </remarks>
    public bool DiscardAlpha { get; init; }

    /// <summary>
    /// Deflate level for the IDAT stream. Defaults to <see cref="CompressionLevel.Optimal"/> (zlib
    /// level 6), which is what every caller got before this was settable.
    /// </summary>
    /// <remarks>
    /// Deflate is the single largest term in a large encode, so this is the one knob that moves it
    /// without changing a pixel. It is exposed rather than re-defaulted because the trade belongs to
    /// the caller: an interactive "save this frame" wants the seconds back, an archival write does
    /// not.
    /// </remarks>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Optimal;

    /// <summary>
    /// Split the image's compressed data into this many independently deflated fragments, produced
    /// in parallel. <c>1</c> (the default) is a single deflate stream, exactly as before.
    /// </summary>
    /// <remarks>
    /// <para>Deflate is the largest single cost in a big encode and it is inherently serial, so the
    /// only way to move it is to run several of it. Each fragment ends at a SYNC FLUSH rather than a
    /// final block, which leaves it byte-aligned and open-ended, so the fragments CONCATENATE into
    /// one ordinary deflate stream. Nothing on the reading side needs to know or cooperate: every
    /// PNG decoder already handles this, because it is just a stream whose blocks happen not to
    /// reference across certain points.</para>
    /// <para><b>The output is deterministic for a given fragment count and differs between counts</b>,
    /// since a fragment can only back-reference its own data. A golden-file test therefore has to pin
    /// the value it was generated with, and deriving the value from <c>Environment.ProcessorCount</c>
    /// would make a file's bytes depend on the machine that wrote it. That is why this is a count the
    /// caller chooses rather than an "auto" flag.</para>
    /// <para>Row FILTERING is split along the same seams, not just the compression, because a PNG
    /// row is predicted from the unfiltered row above it and so depends on the source image rather
    /// than on anything the previous band produced. That is what makes the whole encode scale rather
    /// than just its deflate: 31.3 MP of 16-bit RGB, measured, 5013 ms in one stream against 1718 at
    /// four fragments, 968 at eight and 825 at twelve.</para>
    /// <para><b>Pick a fixed number rather than the core count.</b> More fragments than there are
    /// cores costs nothing but the size fraction above, so a constant like 8 gets most of the benefit
    /// on a large machine and stays correct, and byte-identical, on a small one. That is the whole
    /// reason to prefer a constant: the file does not change when the hardware does.</para>
    /// <para>The costs are a fraction of a percent of file size (a join cannot back-reference across
    /// itself; measured +0.1% at 24 fragments) and holding the compressed fragments in memory while
    /// they are assembled, which roughly doubles the encoder's peak footprint over the single-stream
    /// path. Neither scales with the fragment count in any way worth tuning.</para>
    /// <para>A value above what the image can usefully carry is reduced silently: fragments below
    /// <see cref="PngWriter.MinimumParallelFragmentBytes"/> of payload are not worth their overhead,
    /// and a fragment must hold at least two rows.</para>
    /// </remarks>
    public int ParallelFragments { get; init; } = 1;
}
