using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="TiffReader.ReadInto{TSink}"/> hands a page's pixels over strip by strip, so a caller
/// converting them into its own representation never needs the whole raster resident beside its
/// destination. For a 13228x9354 RGB page that intermediate is 354 MiB.
///
/// <para><b>Equivalence is mostly covered elsewhere, on purpose.</b>
/// <see cref="TiffReader.Read(System.ReadOnlySpan{byte})"/> is now this same machinery with a sink
/// that concatenates, so the whole existing round-trip suite (uint8 / uint16 / float32 x
/// compressed / uncompressed x single / multi-strip x single / multi-page x predictor) exercises the
/// streaming core. What is left to pin here is the part only a streaming caller can see: strip
/// geometry, page declining, and whether the fast case really avoids the copy.</para>
/// </summary>
public class TiffStreamingReadTests
{
    /// <summary>
    /// The pass-through case, and the reason this API exists: an uncompressed page with no predictor
    /// needs no normalisation, so the reader must hand over a slice of the INPUT rather than a copy.
    /// Asserted with <c>Overlaps</c>, which answers exactly that question without unsafe code -- a
    /// byte-equality check could not tell a slice from a copy, which is the whole distinction.
    ///
    /// <para>Paired with a memory-mapped file this is what makes an uncompressed TIFF cost neither a
    /// file buffer nor a raster. Uncompressed matters more than it sounds: for such a page the
    /// "decode" is a memcpy, so today a caller pays twice for bytes already in the right layout.</para>
    /// </summary>
    [Fact]
    public async Task AnUncompressedPageWithNoPredictorIsHandedOverWithoutCopying()
    {
        const int width = 32, height = 16;
        var pixels = Ramp(width * height * 3);
        var tiff = await WriteAsync(pixels, width, height, TiffCompression.Uncompressed, 8, 3);

        var sink = new OverlapProbe(tiff);
        TiffReader.ReadInto(tiff, ref sink);

        sink.Strips.ShouldBeGreaterThan(0);
        sink.EveryStripPointedIntoTheInput.ShouldBeTrue(
            "an uncompressed, unpredicted page needs no rewrite, so nothing should be copied");
    }

    /// <summary>
    /// The counterpart: a compressed page cannot be handed over in place, so the span must be the
    /// reader's own scratch. Stated as a test because the two cases are one branch apart and a change
    /// that accidentally passed a decompressed strip as "the input" would be a use-after-free waiting
    /// to happen for a mapped file.
    /// </summary>
    [Fact]
    public async Task ACompressedPageIsHandedOverFromScratchNotFromTheInput()
    {
        const int width = 32, height = 16;
        var pixels = Ramp(width * height * 3);
        var tiff = await WriteAsync(pixels, width, height, TiffCompression.Deflate, 8, 3);

        var sink = new OverlapProbe(tiff);
        TiffReader.ReadInto(tiff, ref sink);

        sink.Strips.ShouldBeGreaterThan(0);
        sink.EveryStripPointedIntoTheInput.ShouldBeFalse();
    }

    /// <summary>
    /// A streaming caller writes into its own destination by row, so the strip geometry has to be
    /// right: the rows must start at 0, be contiguous, and cover the page exactly once. A reader that
    /// merely concatenated could get all of this wrong and still produce a correct raster, which is
    /// why it was never pinned before.
    /// </summary>
    /// <remarks>
    /// No LZW row, and that is not an oversight: TiffWriter cannot ENCODE LZW, so a fixture
    /// asking for it produced raw bytes LABELLED LZW -- a corrupt file. These tests originally
    /// had one and it PASSED, because a corrupt file decodes to the same garbage through both
    /// readers, so an equivalence assertion over it holds while asserting nothing whatsoever.
    /// LZW DECODE is covered properly by TiffLzwTests, with its own known-good encoder.
    /// </remarks>
    [Theory]
    [InlineData(TiffCompression.Uncompressed)]
    [InlineData(TiffCompression.Deflate)]
    public async Task StripRowsAreContiguousAndCoverThePageExactly(TiffCompression compression)
    {
        const int width = 24, height = 37;   // deliberately not a multiple of any likely RowsPerStrip
        var pixels = Ramp(width * height);
        var tiff = await WriteAsync(pixels, width, height, compression, 8, 1);

        var sink = new GeometryRecorder();
        var doc = TiffReader.ReadInto(tiff, ref sink);

        doc.Pages[0].Height.ShouldBe(height);
        sink.FirstRows.Count.ShouldBeGreaterThan(0);

        var expectedNextRow = 0;
        for (var i = 0; i < sink.FirstRows.Count; i++)
        {
            sink.FirstRows[i].ShouldBe(expectedNextRow, $"strip {i} must continue where {i - 1} stopped");
            expectedNextRow += sink.RowCounts[i];
        }
        expectedNextRow.ShouldBe(height, "the strips together must cover the page exactly once");
    }

    /// <summary>
    /// The reader returns metadata only -- the pixels went to the sink. Pinned because a caller that
    /// found <see cref="TiffPage.Pixels"/> populated here would quietly keep the raster this API
    /// exists to avoid.
    /// </summary>
    [Fact]
    public async Task TheReturnedPagesCarryMetadataButNoPixels()
    {
        const int width = 8, height = 4;
        var tiff = await WriteAsync(Ramp(width * height), width, height, TiffCompression.Deflate, 8, 1);

        var sink = new GeometryRecorder();
        var doc = TiffReader.ReadInto(tiff, ref sink);

        var page = doc.Pages[0];
        page.Width.ShouldBe(width);
        page.Height.ShouldBe(height);
        page.SamplesPerPixel.ShouldBe(1);
        page.Pixels.Length.ShouldBe(0);
    }

    /// <summary>
    /// Declining a page must skip its pixels entirely, which is how reading page 0 of a multi-page file
    /// costs only page 0. The metadata still comes back, so the caller can see what it declined.
    /// </summary>
    [Fact]
    public async Task DecliningAPageSkipsItsStripsButStillReportsIt()
    {
        const int width = 16, height = 8;
        var tiff = await WriteTwoPagesAsync(width, height);

        var sink = new DeclineSecondPage();
        var doc = TiffReader.ReadInto(tiff, ref sink);

        doc.Pages.Count.ShouldBe(2);
        sink.StripsByPage.ContainsKey(0).ShouldBeTrue();
        sink.StripsByPage.ContainsKey(1).ShouldBeFalse("page 1 was declined, so none of it should be decoded");
    }

    /// <summary>
    /// Belt and braces on the refactor: the streamed bytes reassembled must equal what the buffering
    /// overload returns, for every compression the reader supports. The existing suite already covers
    /// this implicitly (Read IS ReadInto plus a concatenating sink), but stating it directly is what
    /// makes a future divergence -- someone "optimising" one path -- fail here rather than in a
    /// consumer.
    /// </summary>
    [Theory]
    [InlineData(TiffCompression.Uncompressed, 8, 1)]
    [InlineData(TiffCompression.Uncompressed, 16, 3)]
    [InlineData(TiffCompression.Deflate, 8, 3)]
    [InlineData(TiffCompression.Deflate, 16, 1)]
    public async Task StreamedStripsReassembleToTheBufferedRaster(
        TiffCompression compression, int bitsPerSample, int samplesPerPixel)
    {
        const int width = 21, height = 13;
        var pixels = Ramp(width * height * samplesPerPixel * (bitsPerSample / 8));
        var tiff = await WriteAsync(pixels, width, height, compression, bitsPerSample, samplesPerPixel);

        var buffered = TiffReader.Read(tiff).Pages[0].Pixels.ToArray();

        var sink = new Reassembler(buffered.Length);
        TiffReader.ReadInto(tiff, ref sink);

        sink.Assembled.ShouldBe(buffered);
    }

    /// <summary>
    /// The bug those LZW fixtures were hiding. Asking for a compression the writer cannot apply used
    /// to write the bytes RAW while stamping the requested value into the IFD, so the data and the
    /// label disagreed and no reader could decode it -- silently, at a plausible size with correct
    /// dimensions. Refusing is the only safe answer: a caller can choose a supported compression, but
    /// nobody can recover a file that lies about its own encoding.
    /// </summary>
    [Fact]
    public async Task AskingForACompressionTheWriterCannotApplyIsRefusedRatherThanMislabelled()
    {
        var attempt = async () => await WriteAsync(Ramp(64), 8, 8, TiffCompression.Lzw, 8, 1);

        var ex = await attempt.ShouldThrowAsync<NotSupportedException>();
        ex.Message.ShouldContain("Lzw");
    }

    // ---------------------------------------------------------------- sinks

    /// <summary>Records whether each strip's span pointed into the input buffer.</summary>
    private sealed class OverlapProbe(byte[] input) : ITiffStripSink
    {
        public int Strips { get; private set; }
        public bool EveryStripPointedIntoTheInput { get; private set; } = true;

        public bool BeginPage(int pageIndex, TiffPage description) => true;

        public void Strip(int pageIndex, int firstRow, int rowCount, ReadOnlySpan<byte> samples)
        {
            Strips++;
            if (!samples.Overlaps(input))
            {
                EveryStripPointedIntoTheInput = false;
            }
        }
    }

    private sealed class GeometryRecorder : ITiffStripSink
    {
        public List<int> FirstRows { get; } = [];
        public List<int> RowCounts { get; } = [];

        public bool BeginPage(int pageIndex, TiffPage description) => true;

        public void Strip(int pageIndex, int firstRow, int rowCount, ReadOnlySpan<byte> samples)
        {
            FirstRows.Add(firstRow);
            RowCounts.Add(rowCount);
        }
    }

    private sealed class DeclineSecondPage : ITiffStripSink
    {
        public Dictionary<int, int> StripsByPage { get; } = [];

        public bool BeginPage(int pageIndex, TiffPage description) => pageIndex == 0;

        public void Strip(int pageIndex, int firstRow, int rowCount, ReadOnlySpan<byte> samples)
            => StripsByPage[pageIndex] = StripsByPage.GetValueOrDefault(pageIndex) + 1;
    }

    private sealed class Reassembler(int size) : ITiffStripSink
    {
        public byte[] Assembled { get; } = new byte[size];
        private int _position;

        public bool BeginPage(int pageIndex, TiffPage description) => true;

        public void Strip(int pageIndex, int firstRow, int rowCount, ReadOnlySpan<byte> samples)
        {
            var copy = Math.Min(samples.Length, Assembled.Length - _position);
            samples[..copy].CopyTo(Assembled.AsSpan(_position, copy));
            _position += copy;
        }
    }

    // -------------------------------------------------------------- helpers

    /// <summary>A deterministic ramp, so a misplaced strip shows up as a value mismatch rather than as
    /// a plausible-looking picture.</summary>
    private static byte[] Ramp(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i * 7 + 11);
        }
        return bytes;
    }

    private static async Task<byte[]> WriteAsync(byte[] pixels, int width, int height,
        TiffCompression compression, int bitsPerSample, int samplesPerPixel)
    {
        using var ms = new MemoryStream();
        await using (var writer = TiffWriter.Create(ms))
        {
            await writer.AddPageAsync(pixels, width, height, new TiffPageOptions
            {
                SamplesPerPixel = samplesPerPixel,
                BitsPerSample = bitsPerSample,
                Photometric = samplesPerPixel >= 3 ? TiffPhotometric.Rgb : TiffPhotometric.MinIsBlack,
                SampleFormat = TiffSampleFormat.Uint,
                Compression = compression,
            });
            await writer.FlushAsync();
        }
        return ms.ToArray();
    }

    private static async Task<byte[]> WriteTwoPagesAsync(int width, int height)
    {
        using var ms = new MemoryStream();
        await using (var writer = TiffWriter.Create(ms))
        {
            var options = new TiffPageOptions
            {
                SamplesPerPixel = 1,
                BitsPerSample = 8,
                Photometric = TiffPhotometric.MinIsBlack,
                SampleFormat = TiffSampleFormat.Uint,
                Compression = TiffCompression.Deflate,
            };
            await writer.AddPageAsync(Ramp(width * height), width, height, options);
            await writer.AddPageAsync(Ramp(width * height), width, height, options);
            await writer.FlushAsync();
        }
        return ms.ToArray();
    }
}
