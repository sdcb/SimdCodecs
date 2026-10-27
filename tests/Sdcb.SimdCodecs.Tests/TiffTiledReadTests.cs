using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ImageMagick;
using Sdcb.SimdCodecs.Png;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The tiled READ path: a tiled page decoded back into the same raster a stripped one would give.
/// </summary>
/// <remarks>
/// <para><b>Why the reader needed this at all.</b> <see cref="TiffLayout.Tiled"/> was write-only --
/// the writer could emit a file this package could not open, which made a tiled export a one-way
/// door and put every tiled file from any other tool out of reach. It also meant the writer could
/// only ever be checked against an outside decoder
/// (<see cref="TiffWriterTiledOracleTests"/>), never round-tripped.</para>
///
/// <para><b>A round trip is not enough on its own here, so it is not the only test.</b> The writer
/// and the reader agree about tile ORDER by construction, so a consistent transpose in both would
/// survive a round trip unnoticed. Three independent checks close that: the same raster written
/// tiled and stripped must decode identically (the stripped path is the covered one); a file
/// written by <b>libtiff</b> must read pixel-for-pixel (an encoder that shares no code with this
/// one, and which pairs its tiles with Predictor 2 -- the case a reader gets silently wrong); and a
/// tiled decode must equal the PNG of the same pixels, which is the golden shape a caller
/// actually compares against.</para>
///
/// <para><b>The trap this path is built around</b> is the predictor. Horizontal differencing
/// restarts at every row of every TILE, and a tile's row is <c>TileWidth</c> wide, not the image's.
/// Running it at image width decodes with no error into a picture that is correct down its first
/// tile column and drifts across the rest -- which is why the libtiff fixture below is written with
/// Predictor 2 on and a width that is not a multiple of the tile width.</para>
/// </remarks>
public sealed class TiffTiledReadTests
{
    /// <summary>
    /// A sample that depends on x, y AND the channel, and repeats nowhere in a page, so a
    /// transposed tile, a mis-strided row or a dropped edge column each move a pixel to a value
    /// that belongs to no other position.
    /// </summary>
    private static ushort Sample16(int x, int y, int c) => (ushort)(((y * 313) + (x * 7) + (c * 24593)) & 0xFFFF);

    private static byte Sample8(int x, int y, int c) => (byte)(((y * 37) + (x * 11) + (c * 83)) & 0xFF);

    private static float Sample32(int x, int y, int c) => (y * 1024f) + x + (c * 0.25f);

    private static byte[] Raster(int width, int height, int samplesPerPixel, int bitsPerSample)
    {
        var bytesPerSample = bitsPerSample / 8;
        var raster = new byte[width * height * samplesPerPixel * bytesPerSample];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < samplesPerPixel; c++)
                {
                    var at = ((((y * width) + x) * samplesPerPixel) + c) * bytesPerSample;
                    switch (bitsPerSample)
                    {
                        case 8:
                            raster[at] = Sample8(x, y, c);
                            break;
                        case 16:
                            BinaryPrimitives.WriteUInt16LittleEndian(raster.AsSpan(at, 2), Sample16(x, y, c));
                            break;
                        default:
                            BinaryPrimitives.WriteSingleLittleEndian(raster.AsSpan(at, 4), Sample32(x, y, c));
                            break;
                    }
                }
            }
        }
        return raster;
    }

    /// <summary>
    /// Geometries where the tiles divide the image exactly, and where they leave an overhang on the
    /// right, the bottom, and both -- the reader has to DROP the padding an edge tile carries, and
    /// keeping it silently shifts every row after the first tile column.
    /// </summary>
    [Theory]
    // Exact multiples, all three compressions: 2 x 3 tiles, no padding anywhere.
    [InlineData(64, 48, 32, 16, 1, 16, TiffCompression.Deflate)]
    [InlineData(64, 48, 32, 16, 1, 16, TiffCompression.Uncompressed)]
    [InlineData(64, 48, 32, 16, 1, 16, TiffCompression.ZlibPkzip)]
    // Overhang on the right, the bottom, and both.
    [InlineData(70, 48, 32, 16, 1, 16, TiffCompression.Deflate)]
    [InlineData(64, 50, 32, 16, 1, 16, TiffCompression.Deflate)]
    [InlineData(70, 50, 32, 16, 1, 16, TiffCompression.Deflate)]
    [InlineData(70, 50, 32, 16, 1, 16, TiffCompression.Uncompressed)]
    // One tile bigger than the whole image: every edge is padding and there is one segment.
    [InlineData(20, 12, 32, 16, 1, 16, TiffCompression.Deflate)]
    [InlineData(20, 12, 32, 16, 1, 16, TiffCompression.Uncompressed)]
    // The smallest tile the spec allows, so the tile count is high and the ordering well exercised.
    [InlineData(50, 34, 16, 16, 1, 16, TiffCompression.Deflate)]
    // A tile exactly as wide as the image: one tile per band, and the tile's row stride, the band's
    // and the image's are all the same number -- so a decoder confusing any two of them still passes
    // here and has to be caught by the rows above.
    [InlineData(32, 40, 32, 16, 1, 16, TiffCompression.Deflate)]
    [InlineData(32, 40, 32, 16, 1, 16, TiffCompression.Uncompressed)]
    // 8-bit RGB: three bytes per pixel, so a tile row's stride shares no factor with its pixel
    // count and an interleave or stride slip shows up as a colour rather than a number.
    [InlineData(70, 50, 32, 16, 3, 8, TiffCompression.Deflate)]
    [InlineData(70, 50, 32, 16, 3, 8, TiffCompression.Uncompressed)]
    // 16-bit RGB, and 32-bit float, which is what this package writes for astronomy data.
    [InlineData(70, 50, 32, 16, 3, 16, TiffCompression.Deflate)]
    [InlineData(37, 21, 16, 16, 1, 32, TiffCompression.Deflate)]
    [InlineData(37, 21, 16, 16, 1, 32, TiffCompression.Uncompressed)]
    public async Task Tiled_RoundTripsThroughTheReader(
        int width, int height, int tileWidth, int tileHeight, int samplesPerPixel, int bitsPerSample,
        TiffCompression compression)
    {
        var raster = Raster(width, height, samplesPerPixel, bitsPerSample);
        var tiff = await WriteAsync(raster, width, height, Options(samplesPerPixel, bitsPerSample, compression) with
        {
            Layout = TiffLayout.Tiled,
            TileWidth = tileWidth,
            TileHeight = tileHeight,
        });

        var page = TiffReader.Read(tiff).Pages[0];

        page.Width.ShouldBe(width);
        page.Height.ShouldBe(height);
        page.SamplesPerPixel.ShouldBe(samplesPerPixel);
        page.BitsPerSample.ShouldBe(bitsPerSample);
        // The layout the file used survives the decode: a caller re-writing a page has no other way
        // to know, and a reader that quietly took the strip path would report zeroes here.
        page.TileWidth.ShouldBe(tileWidth);
        page.TileHeight.ShouldBe(tileHeight);
        page.RowsPerStrip.ShouldBe(0, "a tiled page has no RowsPerStrip; reporting one would be an invention");

        page.Pixels.ShouldBe(raster);
    }

    /// <summary>
    /// The same raster stored both ways decodes to the same bytes. Strips are the long-covered path,
    /// so this pins the tiled one against it -- and unlike the round trip above it cannot be passed
    /// by a writer and reader that agree on a wrong tile order, because the stripped file has no
    /// tile order to agree about.
    /// </summary>
    [Theory]
    [InlineData(TiffCompression.Deflate)]
    [InlineData(TiffCompression.Uncompressed)]
    public async Task TiledAndStripped_DecodeToTheSameRaster(TiffCompression compression)
    {
        const int width = 70;
        const int height = 50;
        var raster = Raster(width, height, 3, 16);
        var options = Options(3, 16, compression);

        var tiled = await WriteAsync(raster, width, height, options with
        {
            Layout = TiffLayout.Tiled,
            TileWidth = 32,
            TileHeight = 16,
        });
        var stripped = await WriteAsync(raster, width, height, options with
        {
            Layout = TiffLayout.Strip,
            RowsPerStrip = 7,
        });

        var fromTiles = TiffReader.Read(tiled).Pages[0];
        var fromStrips = TiffReader.Read(stripped).Pages[0];

        fromTiles.TileWidth.ShouldBe(32);
        fromStrips.TileWidth.ShouldBe(0);
        fromTiles.Pixels.ShouldBe(fromStrips.Pixels);
    }

    /// <summary>
    /// A tiled file written by <b>libtiff</b>, read by us. This is the direction the oracle has to
    /// run for a READER: an encoder that shares no code with this package, and one whose ZIP output
    /// carries Predictor 2 -- so a reader that inverts the predictor at image width instead of tile
    /// width fails here and nowhere else.
    /// </summary>
    /// <remarks>
    /// The geometry is deliberately 70x50 in 32x16 tiles: three tiles across (the last one 6 pixels
    /// wide) and four down (the last one 2 rows tall), so neither axis divides and the tile grid is
    /// not square.
    /// </remarks>
    [Theory]
    [InlineData(CompressionMethod.Zip, 2)]      // predictor on, the default pairing for ZIP
    [InlineData(CompressionMethod.Zip, 1)]      // predictor off, same compression
    [InlineData(CompressionMethod.NoCompression, 1)]
    public void TiledGray16_WrittenByLibTiff_ReadsPixelForPixel(CompressionMethod compression, int predictor)
    {
        const int width = 70;
        const int height = 50;

        var tiff = LibTiffTiled(width, height, samplesPerPixel: 1, tile: "32x16", compression, predictor);

        // The premise: libtiff really did tile it. A define it ignored would leave a stripped file
        // that this reader handles on its old path, and the test would pass having proved nothing.
        var tags = FirstIfdTags(tiff);
        tags.ShouldContainKey((ushort)322, "libtiff ignored tile-geometry, so this fixture is not tiled");
        tags[322].ShouldBe(32u);
        tags[323].ShouldBe(16u);
        tags.ShouldNotContainKey((ushort)273, "a tiled page carries no StripOffsets");
        tags.GetValueOrDefault((ushort)317, 1u).ShouldBe((uint)predictor, "the fixture's predictor is the point of the case");

        var page = TiffReader.Read(tiff).Pages[0];
        page.TileWidth.ShouldBe(32);
        page.TileHeight.ShouldBe(16);

        var got = MemoryMarshal.Cast<byte, ushort>(page.Pixels.AsSpan());
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                got[(y * width) + x].ShouldBe(Sample16(x, y, 0),
                    $"pixel ({x}, {y}) of a libtiff-written 70x50 page in 32x16 tiles");
            }
        }
    }

    /// <summary>
    /// The same, three samples per pixel: a tile's row stride is no longer its pixel count, so an
    /// interleave mistake in the band assembly shows as a colour shift rather than a wrong number.
    /// </summary>
    [Fact]
    public void TiledRgb16_WrittenByLibTiff_ReadsPixelForPixel()
    {
        const int width = 70;
        const int height = 50;

        var tiff = LibTiffTiled(width, height, samplesPerPixel: 3, tile: "32x16", CompressionMethod.Zip, predictor: 2);
        FirstIfdTags(tiff).ShouldContainKey((ushort)322);

        var page = TiffReader.Read(tiff).Pages[0];
        page.SamplesPerPixel.ShouldBe(3);

        var got = MemoryMarshal.Cast<byte, ushort>(page.Pixels.AsSpan());
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    got[((((y * width) + x) * 3) + c)].ShouldBe(Sample16(x, y, c), $"sample ({x}, {y}, {c})");
                }
            }
        }
    }

    /// <summary>
    /// A tiled TIFF and a PNG of the same pixels decode to the same samples. This is what the read
    /// path was wanted for: a tiled export can now be compared against a PNG golden with no outside
    /// tool in the loop, and the two decoders share nothing but the raster they are given.
    /// </summary>
    [Theory]
    [InlineData(TiffCompression.Deflate)]
    [InlineData(TiffCompression.Uncompressed)]
    public async Task TiledTiff_AndPngOfTheSamePixels_Agree(TiffCompression compression)
    {
        const int width = 70;
        const int height = 50;

        var samples = new ushort[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                samples[(y * width) + x] = Sample16(x, y, 0);
            }
        }

        var png = PngWriter.EncodeGray16(samples, width, height);
        var raster = Raster(width, height, 1, 16);
        var tiff = await WriteAsync(raster, width, height, Options(1, 16, compression) with
        {
            Layout = TiffLayout.Tiled,
            TileWidth = 32,
            TileHeight = 16,
        });

        var fromPng = PngReader.Decode(png).AsUInt16Samples();
        var fromTiff = MemoryMarshal.Cast<byte, ushort>(TiffReader.Read(tiff).Pages[0].Pixels.AsSpan()).ToArray();

        fromTiff.ShouldBe(fromPng);
    }

    /// <summary>
    /// The streaming read over a tiled page. A tile holds part of a row, so the reader owes the sink
    /// whole rows anyway: it assembles one ROW of tiles and hands it over as a band. Asserted here
    /// because it is a contract, not an implementation detail -- a caller sizing a destination from
    /// <c>firstRow</c> and <c>rowCount</c> has to be able to trust both.
    /// </summary>
    [Fact]
    public async Task ReadInto_HandsATiledPageOverAsBandsOfWholeRows()
    {
        const int width = 70;
        const int height = 50;
        const int tileHeight = 16;

        var raster = Raster(width, height, 1, 16);
        var tiff = await WriteAsync(raster, width, height, Options(1, 16, TiffCompression.Deflate) with
        {
            Layout = TiffLayout.Tiled,
            TileWidth = 32,
            TileHeight = tileHeight,
        });

        var sink = new RecordingSink();
        var doc = TiffReader.ReadInto(tiff, ref sink);

        doc.Pages[0].Pixels.ShouldBeEmpty("the streaming overload returns metadata; the sink has the pixels");
        sink.Bands.Count.ShouldBe(4, "ceil(50 / 16) bands");
        sink.Bands[0].FirstRow.ShouldBe(0);
        sink.Bands[1].FirstRow.ShouldBe(16);
        sink.Bands[3].FirstRow.ShouldBe(48);
        sink.Bands[3].RowCount.ShouldBe(2, "the last band is the remainder, not a padded tile");
        foreach (var band in sink.Bands)
        {
            band.Bytes.Length.ShouldBe(band.RowCount * width * 2, "a band is whole rows of the IMAGE, padding dropped");
        }

        // And the bands in order are the picture -- the same bytes the buffering overload returns.
        sink.Bands.SelectMany(b => b.Bytes).ToArray().ShouldBe(raster);
    }

    /// <summary>
    /// TIFF 6.0 p.68: a page is stripped or tiled, never both. A file carrying both sets of tags is
    /// ambiguous, and picking one silently is how a reader ends up decoding a layout the writer did
    /// not mean. Built by renaming a real page's RowsPerStrip tag, so everything else about the file
    /// stays valid.
    /// </summary>
    [Fact]
    public async Task APageWithBothStripAndTileTags_IsRefused()
    {
        var raster = Raster(16, 16, 1, 16);
        var tiff = await WriteAsync(raster, 16, 16, Options(1, 16, TiffCompression.Uncompressed) with
        {
            Layout = TiffLayout.Strip,
            RowsPerStrip = 8,
        });

        RenameFirstIfdTag(tiff, from: 278 /* RowsPerStrip */, to: 322 /* TileWidth */);

        Should.Throw<InvalidDataException>(() => TiffReader.Read(tiff))
            .Message.ShouldContain("exclusive");
    }

    /// <summary>
    /// A tiled page whose tile list cannot cover its own dimensions is truncated or mis-declared.
    /// Refused rather than decoded into a part-blank raster, which is the shape this codebase has
    /// been bitten by before: right size, no exception, wrong picture. Built by halving TileWidth on
    /// a real tiled file, which quadruples the tiles needed while leaving twelve listed.
    /// </summary>
    [Fact]
    public async Task ATiledPageWithTooFewTiles_IsRefused()
    {
        var raster = Raster(70, 50, 1, 16);
        var tiff = await WriteAsync(raster, 70, 50, Options(1, 16, TiffCompression.Deflate) with
        {
            Layout = TiffLayout.Tiled,
            TileWidth = 32,
            TileHeight = 16,
        });

        SetFirstIfdLongValue(tiff, tag: 322 /* TileWidth */, value: 8);

        Should.Throw<InvalidDataException>(() => TiffReader.Read(tiff))
            .Message.ShouldContain("tiles");
    }

    // ---- helpers -----------------------------------------------------------

    private readonly record struct Band(int FirstRow, int RowCount, byte[] Bytes);

    /// <summary>
    /// A mutable struct sink, which is the shape <see cref="ITiffStripSink"/> asks for: passed by
    /// ref to a constrained generic, so it is neither boxed nor allocated.
    /// </summary>
    private struct RecordingSink : ITiffStripSink
    {
        public RecordingSink() { }

        public List<Band> Bands { get; } = [];

        public bool BeginPage(int pageIndex, TiffPage description) => true;

        public void Strip(int pageIndex, int firstRow, int rowCount, ReadOnlySpan<byte> samples) =>
            Bands.Add(new Band(firstRow, rowCount, samples.ToArray()));
    }

    private static TiffPageOptions Options(int samplesPerPixel, int bitsPerSample, TiffCompression compression) => new()
    {
        SamplesPerPixel = samplesPerPixel,
        BitsPerSample = bitsPerSample,
        Photometric = samplesPerPixel == 1 ? TiffPhotometric.MinIsBlack : TiffPhotometric.Rgb,
        SampleFormat = bitsPerSample == 32 ? TiffSampleFormat.IeeeFloat : TiffSampleFormat.Uint,
        Compression = compression,
    };

    private static async Task<byte[]> WriteAsync(byte[] pixels, int width, int height, TiffPageOptions options)
    {
        using var ms = new MemoryStream();
        await using (var writer = TiffWriter.Create(ms))
        {
            await writer.AddPageAsync(pixels, width, height, options);
            await writer.FlushAsync();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// A tiled TIFF from libtiff, via Magick.NET -- already a package reference here, so this needs
    /// no build step and no <see cref="OracleGate"/> (the <c>Group4Tiff</c> precedent). The raster
    /// goes in as raw LSB-first samples and comes back out as a TIFF, so the only thing under test
    /// is the file libtiff chose to write.
    /// </summary>
    private static byte[] LibTiffTiled(int width, int height, int samplesPerPixel, string tile,
        CompressionMethod compression, int predictor)
    {
        var raster = Raster(width, height, samplesPerPixel, 16);
        var settings = new MagickReadSettings
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = samplesPerPixel == 1 ? MagickFormat.Gray : MagickFormat.Rgb,
            Depth = 16,
            Endian = Endian.LSB,
        };

        using var image = new MagickImage(raster, settings);
        image.Settings.SetDefine(MagickFormat.Tiff, "tile-geometry", tile);
        image.Settings.SetDefine(MagickFormat.Tiff, "predictor", predictor.ToString());
        image.Settings.Compression = compression;
        return image.ToByteArray(MagickFormat.Tiff);
    }

    /// <summary>
    /// Tag id -> first value of the first IFD. Deliberately hand-rolled rather than
    /// <see cref="TiffReader"/>: a premise assertion that leans on the code under test asserts
    /// nothing.
    /// </summary>
    private static Dictionary<ushort, uint> FirstIfdTags(byte[] tiff)
    {
        BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(0, 2)).ShouldBe((ushort)0x4949, "little-endian TIFF expected");

        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4, 4));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd, 2));

        var tags = new Dictionary<ushort, uint>(count);
        for (var i = 0; i < count; i++)
        {
            var at = ifd + 2 + (i * 12);
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(at, 2));
            var type = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(at + 2, 2));
            var n = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(at + 4, 4));
            var valueField = tiff.AsSpan(at + 8, 4);

            var inline = type switch
            {
                3 => n * 2u <= 4u,   // SHORT
                4 => n * 4u <= 4u,   // LONG
                _ => false,
            };
            var span = inline
                ? valueField
                : tiff.AsSpan((int)BinaryPrimitives.ReadUInt32LittleEndian(valueField), 4);

            tags[tag] = type switch
            {
                3 => BinaryPrimitives.ReadUInt16LittleEndian(span[..2]),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(span[..4]),
                _ => 0u,
            };
        }
        return tags;
    }

    private static void RenameFirstIfdTag(byte[] tiff, ushort from, ushort to) =>
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(FindFirstIfdEntry(tiff, from), 2), to);

    private static void SetFirstIfdLongValue(byte[] tiff, ushort tag, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(FindFirstIfdEntry(tiff, tag) + 8, 4), value);

    private static int FindFirstIfdEntry(byte[] tiff, ushort tag)
    {
        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4, 4));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd, 2));
        for (var i = 0; i < count; i++)
        {
            var at = ifd + 2 + (i * 12);
            if (BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(at, 2)) == tag) return at;
        }
        throw new InvalidOperationException($"tag {tag} is not in the first IFD, so this fixture cannot be built");
    }
}
