namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// Receives a TIFF page's pixels strip by strip, instead of as one assembled raster.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> <see cref="TiffReader.Read(System.ReadOnlySpan{byte})"/> returns a
/// COMPLETE raster, so a caller that only wants to convert those bytes into its own representation
/// has both fully resident at once. For a 13228x9354 RGB page that is 354 MiB of intermediate beside
/// the destination -- and the reader already decodes strip by strip, so the whole-raster buffer is an
/// artefact of the API shape rather than anything the format requires.</para>
///
/// <para><b>The span may point straight into the file.</b> When a strip needs no normalisation -- no
/// endian swap (8-bit samples, or a file whose order matches the host) and
/// <see cref="TiffPredictor.None"/> -- and the page is uncompressed, the reader hands over a slice of
/// the input with nothing copied at all. That is the case worth caring about: for an uncompressed
/// page the "decode" is a memcpy, so a caller reading from a memory-mapped file through this
/// interface materialises NEITHER the file bytes nor a raster. Otherwise the span is a reused
/// per-strip scratch buffer.</para>
///
/// <para><b>A tiled page arrives here too, as bands of whole rows.</b> Tiles do not each hold a
/// run of rows, so the reader decodes one ROW of tiles and hands it over as a
/// <c>TileLength</c>-tall band: one contract for both layouts, and a caller need not know which
/// one the file used. The zero-copy case above is therefore strips only -- assembling a band is a
/// copy by definition.</para>
///
/// <para><b>So the span is valid only for the duration of the call.</b> Copy or convert what you
/// need; do not retain it. It is read-only because a mapped view is, and because a shared scratch
/// buffer must not be edited by one strip's handler and observed by the next.</para>
///
/// <para>Implement as a mutable <c>struct</c> and pass it by <c>ref</c> to
/// <see cref="TiffReader.ReadInto{TSink}"/>: the reader takes it as a constrained generic, so a
/// struct sink is neither boxed nor allocated, which matters on a path whose reason for existing is
/// to stop allocating.</para>
/// </remarks>
public interface ITiffStripSink
{
    /// <summary>
    /// Called once per page, with its metadata complete, BEFORE any of its pixels are decoded --
    /// which is the point: a caller can size its own destination from
    /// <paramref name="description"/> and never allocate a raster.
    /// </summary>
    /// <param name="pageIndex">0-based index in IFD chain order.</param>
    /// <param name="description">
    /// The page, with <see cref="TiffPage.Pixels"/> EMPTY. Everything else is final.
    /// </param>
    /// <returns>
    /// <c>false</c> to skip this page's pixels entirely -- its strips are not decoded and
    /// <see cref="Strip"/> is not called for it. This is how a caller reads page 0 of a multi-page
    /// file without paying for the rest.
    /// </returns>
    bool BeginPage(int pageIndex, TiffPage description);

    /// <summary>
    /// One strip's samples, already endian-normalised to host order and with any predictor inverted,
    /// in the same interleaved layout as <see cref="TiffPage.Pixels"/>.
    /// </summary>
    /// <param name="pageIndex">The page these rows belong to.</param>
    /// <param name="firstRow">0-based row this strip starts at.</param>
    /// <param name="rowCount">
    /// Rows in this band. Every one but the last holds <see cref="TiffPage.RowsPerStrip"/> rows on a
    /// stripped page and <see cref="TiffPage.TileHeight"/> on a tiled one; the last holds the
    /// remainder. A truncated file can yield fewer bytes than <paramref name="rowCount"/> rows would
    /// imply, so trust <paramref name="samples"/>.Length.
    /// </param>
    /// <param name="samples">
    /// The strip's bytes. Valid ONLY for this call -- see the remarks on the interface.
    /// </param>
    void Strip(int pageIndex, int firstRow, int rowCount, System.ReadOnlySpan<byte> samples);
}
