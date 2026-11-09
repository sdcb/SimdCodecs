using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Facade tests for the JBIG2 adapter — the courtesy path, for standalone
/// <c>.jb2</c> files that actually have a header to sniff. The format's real use
/// (PDF's <c>/JBIG2Decode</c>) cannot come through the facade at all, and that
/// asymmetry is asserted here too: an embedded stream has no signature, so
/// <see cref="ImageCodecs.CanDecode"/> must not claim it.
/// </summary>
public sealed class Jbig2FacadeTests
{
    private static readonly string[] Glyph =
    [
        "..######..",
        "..#....#..",
        "..#.##.#..",
        "..#....#..",
        "..######..",
    ];

    internal static byte[] BuildFile()
    {
        var source = Jbig2StreamBuilder.FromRows(Glyph);
        return Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(source.Width, source.Height)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(source)),
            Jbig2StreamBuilder.Segment(2, SegmentType.EndOfFile, 0, []));
    }

    [Fact]
    public void CanDecode_RecognisesTheJb2FileSignature()
    {
        ImageCodecs.CanDecode(BuildFile()).ShouldBeTrue();
        Jbig2ImageDecoder.CanDecode(Jbig2Decoder.FileSignature).ShouldBeTrue();
    }

    [Fact]
    public void CanDecode_DoesNotClaimAnEmbeddedStream()
    {
        // A PDF-embedded stream starts with a segment header, not a signature.
        // Sniffing must not claim it — the caller has to go to Jbig2Decoder.Decode
        // with the globals and dimensions the PDF holds.
        var source = Jbig2StreamBuilder.FromRows(Glyph);
        var embedded = Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
            Jbig2StreamBuilder.GenericRegionSegment(source));

        Jbig2ImageDecoder.CanDecode(embedded).ShouldBeFalse();
    }

    [Fact]
    public void TryReadInfo_ReportsGrayEightBit()
    {
        ImageCodecs.TryReadInfo(BuildFile(), out var info).ShouldBeTrue();

        info.Width.ShouldBe(10);
        info.Height.ShouldBe(5);
        info.Channels.ShouldBe(1);
        info.SampleFormat.ShouldBe(SampleFormat.UInt8);
    }

    [Fact]
    public void TryDecode_ProducesTheGrayFidelityRaster()
    {
        ImageCodecs.TryDecode(BuildFile(), out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();

        image!.Width.ShouldBe(10);
        image.Height.ShouldBe(5);
        image.Channels.ShouldBe(1);
        image.SampleFormat.ShouldBe(SampleFormat.UInt8);

        var expected = Jbig2StreamBuilder.FromRows(Glyph);
        for (var i = 0; i < expected.Data.Length; i++)
            image.Pixels[i].ShouldBe(expected.Data[i] != 0 ? (byte)0 : (byte)255, $"pixel {i}");
    }

    [Fact]
    public void TryDecodeIntoRgba8_ExpandsGrayAcrossRgbWithOpaqueAlpha()
    {
        var destination = new byte[10 * 5 * 4];
        ImageCodecs.TryDecodeIntoRgba8(BuildFile(), destination).ShouldBeTrue();

        var expected = Jbig2StreamBuilder.FromRows(Glyph);
        for (var i = 0; i < expected.Data.Length; i++)
        {
            var value = expected.Data[i] != 0 ? (byte)0 : (byte)255;
            destination[i * 4].ShouldBe(value);
            destination[i * 4 + 1].ShouldBe(value);
            destination[i * 4 + 2].ShouldBe(value);
            destination[i * 4 + 3].ShouldBe((byte)255);
        }
    }

    [Fact]
    public void TryDecodeIntoRgba8_RejectsAnUndersizedDestination()
    {
        Jbig2ImageDecoder.TryDecodeIntoRgba8(BuildFile(), new byte[16]).ShouldBeFalse();
    }

    /// <summary>
    /// Symbol-coded pages come through the facade too — the adapter is not
    /// limited to the generic regions it was written against. This is a real
    /// jbig2enc file, so it exercises the whole §6.4/§6.5 path behind a plain
    /// <c>TryDecode</c>.
    /// </summary>
    [Fact]
    public void TryDecode_SymbolCodedFile_ProducesTheSamePageAsTheDirectApi()
    {
        var file = File.ReadAllBytes(Path.Combine(Jbig2SymbolFixtureTests.FixtureDirectory, "sym.jb2"));

        ImageCodecs.CanDecode(file).ShouldBeTrue();
        ImageCodecs.TryDecode(file, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();

        var direct = Jbig2Decoder.DecodeFile(file, cancellationToken: TestContext.Current.CancellationToken);
        image!.Width.ShouldBe(direct.Width);
        image.Height.ShouldBe(direct.Height);
        image.Pixels.ToArray().ShouldBe(direct.ToGray8());
    }

    /// <summary>
    /// And a halftone page, which reaches the facade through an entirely
    /// different §6.6/§6.7 path — pattern dictionary, Gray-coded bitplanes, and a
    /// grid of stamped patterns rather than a coded raster.
    /// </summary>
    [Fact]
    public void TryDecode_HalftoneFile_ProducesTheSamePageAsTheDirectApi()
    {
        var (segments, expected) = Jbig2HalftoneTests.BuildSegments(levels: 4);
        var file = Jbig2StreamBuilder.SequentialFile([.. segments,
            Jbig2StreamBuilder.Segment(9, SegmentType.EndOfFile, 0, [])]);

        ImageCodecs.TryDecode(file, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image!.Width.ShouldBe(expected.Width);
        image.Pixels.ToArray().ShouldBe(new Jbig2Image(expected.Width, expected.Height, expected.Data).ToGray8());
    }

    [Fact]
    public void TryDecode_OnAFileNeedingAnUnimplementedFeature_ReturnsFalse()
    {
        // The facade contract is "false for undecodable", not "throws". Every
        // region type decodes now, so the case has to be a genuinely refused
        // feature: a symbol dictionary that imports arithmetic contexts.
        var dictionary = Jbig2SymbolBuilder.SymbolDictionarySegment(Jbig2TextRegionTests.Alphabet());
        dictionary[0] |= 0x01;   // bitmap coding context used (bit 8 of the flags)

        var file = Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(8, 8)),
            Jbig2StreamBuilder.Segment(1, SegmentType.SymbolDictionary, 1, dictionary));

        Jbig2ImageDecoder.TryDecode(file, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
        image.ShouldBeNull();
        ImageCodecs.TryDecode(file, out _, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
    }

    [Fact]
    public void TryDecode_OnATruncatedFile_ReturnsFalse()
    {
        var file = BuildFile();

        Jbig2ImageDecoder.TryDecode(file.AsSpan(0, 20).ToArray(), out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
        image.ShouldBeNull();
    }

    /// <summary>
    /// Every page of a multi-page file is a frame through the facade: frame <c>i</c> has page <c>i + 1</c>'s own
    /// size and pixels, exactly what <see cref="Jbig2Decoder.DecodeFile(ReadOnlySpan{byte}, int, CancellationToken)"/>
    /// gives that page decoded on its own, and the bitmap the page was built from. Frames are read out of order to
    /// show that pages are independent.
    /// </summary>
    [Fact]
    public void TryOpenFrames_MultiPageFile_DecodesEveryPage()
    {
        var ct = TestContext.Current.CancellationToken;
        Jbig2Bitmap[] sources =
        [
            Jbig2ExtendedOracleTests.Pattern(30, 20),
            Jbig2ExtendedOracleTests.Pattern(41, 13, seed: 5),
            Jbig2ExtendedOracleTests.Pattern(17, 26, seed: 11),
        ];
        var file = Jbig2ExtendedOracleTests.MultiPageFile(sources);

        ImageCodecs.TryReadInfo(file, out var info).ShouldBeTrue();
        (info.Width, info.Height, info.FrameCount).ShouldBe((30, 20, 3));

        ImageCodecs.TryOpenFrames(file, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.FrameCount.ShouldBe(3);
            reader.LoopCount.ShouldBeNull();
            foreach (var index in new[] { 2, 0, 1 })
            {
                var direct = Jbig2Decoder.DecodeFile(file, index + 1, ct);
                var expected = direct.ToGray8();
                (direct.Width, direct.Height).ShouldBe((sources[index].Width, sources[index].Height));
                expected.ShouldBe(new Jbig2Image(sources[index].Width, sources[index].Height, sources[index].Data).ToGray8());

                reader.TryGetFrameInfo(index, out var frame).ShouldBeTrue();
                frame.Duration.ShouldBeNull();
                (frame.Info.Width, frame.Info.Height, frame.Info.Channels, frame.Info.SampleFormat, frame.Info.BitDepth,
                    frame.Info.FrameCount, frame.Info.Format)
                    .ShouldBe((direct.Width, direct.Height, 1, SampleFormat.UInt8, 1, 1, ImageFormat.Jbig2), $"frame {index}");

                reader.TryDecodeFrame(index, out var image, cancellationToken: ct).ShouldBeTrue();
                using (image)
                {
                    (image.Width, image.Height, image.Channels).ShouldBe((direct.Width, direct.Height, 1));
                    image.Pixels.ToArray().ShouldBe(expected, $"frame {index}");
                }

                var gray = new byte[direct.Width * direct.Height];
                reader.TryDecodeFrameInto(index, gray, 0, PixelFormat.Gray8, cancellationToken: ct).ShouldBeTrue();
                gray.ShouldBe(expected, $"frame {index} into");

                // A region goes through the decode-then-crop fallback, still of this page.
                var region = new PixelRect(3, 2, 9, 7);
                var crop = new byte[region.Width * region.Height];
                reader.TryDecodeFrameInto(index, crop, 0, PixelFormat.Gray8, new DecodeOptions { Region = region }, ct).ShouldBeTrue();
                for (var y = 0; y < region.Height; y++)
                    crop.AsSpan(y * region.Width, region.Width).ToArray().ShouldBe(
                        expected.AsSpan((region.Y + y) * direct.Width + region.X, region.Width).ToArray(), $"frame {index} region row {y}");

                reader.TryDecodeFrameInto(index, new byte[gray.Length - 1], 0, PixelFormat.Gray8, cancellationToken: ct).ShouldBeFalse();
            }

            Should.Throw<ArgumentOutOfRangeException>(() => reader.TryGetFrameInfo(3, out _));
            Should.Throw<ArgumentOutOfRangeException>(() => reader.TryDecodeFrame(-1, out _, cancellationToken: ct));
            Should.Throw<ArgumentOutOfRangeException>(() => reader.TryDecodeFrameInto(3, new byte[1 << 12], 0, PixelFormat.Gray8, cancellationToken: ct));
        }

        Should.Throw<ObjectDisposedException>(() => reader.TryGetFrameInfo(0, out _));
        Should.Throw<ObjectDisposedException>(() => reader.TryDecodeFrame(0, out _, cancellationToken: ct));
        Should.Throw<ObjectDisposedException>(() => reader.TryDecodeFrameInto(0, new byte[30 * 20], 0, PixelFormat.Gray8, cancellationToken: ct));
    }

    /// <summary>A file that is not JBIG2, or whose header is truncated, opens no frame reader.</summary>
    [Fact]
    public void TryOpenFrames_OnAMalformedFile_ReturnsFalse()
    {
        Jbig2ImageDecoder.TryOpenFrames(BuildFile().AsMemory(0, 10), out var reader).ShouldBeFalse();
        reader.ShouldBeNull();
        Jbig2ImageDecoder.TryOpenFrames(new byte[16], out reader).ShouldBeFalse();
        reader.ShouldBeNull();
    }

    [Fact]
    public void Jb2Signature_DoesNotCollideWithTheOtherRegisteredCodecs()
    {
        // 0x97 leads nothing else in the family, but the registry is order-
        // sensitive, so assert the sniff actually reaches JBIG2 rather than being
        // swallowed by a codec registered ahead of it.
        ImageCodecs.TryReadInfo(BuildFile(), out var info).ShouldBeTrue();
        info.Channels.ShouldBe(1);
        info.SampleFormat.ShouldBe(SampleFormat.UInt8);
    }
}
