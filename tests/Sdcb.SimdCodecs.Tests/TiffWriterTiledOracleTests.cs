using System.Buffers.Binary;
using ImageMagick;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The tiled write path, read back by libtiff.
/// </summary>
/// <remarks>
/// <para><b><see cref="TiffLayout.Tiled"/> shipped with no coverage at all.</b> Every other test of
/// this writer goes through <c>TiffWriterRoundTripTests</c>'s in-test mini-reader, which is
/// strip-only by design, and <see cref="TiffReader"/> reads strips too -- it has no
/// <c>TileOffsets</c> handling. So nothing in this repository has ever read a tile back, and the
/// tiled branch of <see cref="PixelSource"/> and of the IFD builder were both write-only code.</para>
/// <para><b>The oracle is libtiff through Magick.NET</b>, following <c>Group4Tiff</c>: already a
/// package reference, no build step, nothing to install, so it runs everywhere including CI and
/// needs no <c>OracleGate</c>. What makes it an oracle rather than a round-trip is that it shares
/// no code with the writer -- our own reader could not do this job even if it handled tiles.</para>
/// <para><b>Why this needed an outside reader specifically.</b> A wrong tiled file is the silent
/// kind this codec has produced twice before (Predictor 2 decoding to a derivative; a compression
/// this writer could not apply being stamped on raw bytes). Tile order, edge padding and the
/// per-tile compression boundary are all invisible to a reader that shares the writer's
/// assumptions, and all three produce a file with the right dimensions, the right byte count and no
/// exception.</para>
/// </remarks>
public sealed class TiffWriterTiledOracleTests
{
    /// <summary>
    /// A value that depends on BOTH axes and repeats nowhere in a page, so a transposed tile, a
    /// tile written in column-major order, or one row of padding in the wrong place all change a
    /// pixel rather than merely moving an equal one.
    /// </summary>
    private static ushort Gray(int x, int y) => (ushort)(((y * 313) + (x * 7)) & 0xFFFF);

    private static byte[] GrayRaster(int width, int height)
    {
        var pixels = new byte[width * height * 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    pixels.AsSpan(((y * width) + x) * 2, 2), Gray(x, y));
            }
        }
        return pixels;
    }

    /// <summary>
    /// Tile sizes that divide the image exactly, and sizes that leave a partial tile on the right,
    /// the bottom, and both. The partial cases are the point: a tile is always written full-size
    /// with the overhang zero-padded (TIFF 6.0 p.67), and getting that wrong still produces a file
    /// of exactly the right length.
    /// </summary>
    [Theory]
    // Exact multiples: 2 x 3 tiles, nothing padded.
    [InlineData(64, 48, 32, 16, TiffCompression.Deflate)]
    [InlineData(64, 48, 32, 16, TiffCompression.Uncompressed)]
    [InlineData(64, 48, 32, 16, TiffCompression.ZlibPkzip)]
    // Partial on the right only.
    [InlineData(70, 48, 32, 16, TiffCompression.Deflate)]
    // Partial on the bottom only.
    [InlineData(64, 50, 32, 16, TiffCompression.Deflate)]
    // Partial on both, and more tiles across than down so a row/column swap cannot pass.
    [InlineData(70, 50, 32, 16, TiffCompression.Deflate)]
    [InlineData(70, 50, 32, 16, TiffCompression.Uncompressed)]
    // One tile larger than the image: every edge is padding.
    [InlineData(20, 12, 32, 16, TiffCompression.Deflate)]
    // The smallest tile the spec allows, so the tile count is high and ordering is well exercised.
    [InlineData(50, 34, 16, 16, TiffCompression.Deflate)]
    public async Task TiledGray16_ReadByLibTiff_MatchesPixelForPixel(
        int width, int height, int tileWidth, int tileHeight, TiffCompression compression)
    {
        var pixels = GrayRaster(width, height);
        var bytes = await WriteTiledAsync(pixels, width, height, new TiffPageOptions
        {
            SamplesPerPixel = 1,
            BitsPerSample = 16,
            Photometric = TiffPhotometric.MinIsBlack,
            SampleFormat = TiffSampleFormat.Uint,
            Compression = compression,
            Layout = TiffLayout.Tiled,
            TileWidth = tileWidth,
            TileHeight = tileHeight,
        });

        // The premise, asserted before the pixels: a writer that quietly fell back to strips would
        // otherwise pass this whole file, since libtiff reads both and the pixels would be right.
        AssertIsTiled(bytes, tileWidth, tileHeight);

        using var oracle = new MagickImage(bytes);
        oracle.Width.ShouldBe((uint)width);
        oracle.Height.ShouldBe((uint)height);

        using var px = oracle.GetPixels();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Q16-HDRI carries a 16-bit sample exactly, so this is an equality and not a
                // tolerance -- any difference is a real one.
                var got = (ushort)px.GetPixel(x, y).ToArray()![0];
                got.ShouldBe(Gray(x, y), $"pixel ({x}, {y}) of {width}x{height} in {tileWidth}x{tileHeight} tiles");
            }
        }
    }

    /// <summary>
    /// Three samples per pixel, so a tile's row stride is not its pixel count and an interleave
    /// mistake inside <c>ExtractTile</c> shows as a colour shift rather than as a wrong number.
    /// </summary>
    [Theory]
    [InlineData(TiffCompression.Deflate)]
    [InlineData(TiffCompression.Uncompressed)]
    public async Task TiledRgb16_ReadByLibTiff_MatchesPixelForPixel(TiffCompression compression)
    {
        const int width = 70;
        const int height = 34;
        const int tile = 32;

        var pixels = new byte[width * height * 3 * 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (((y * width) + x) * 3) * 2;
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(at, 2), Gray(x, y));
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(at + 2, 2), (ushort)(Gray(x, y) ^ 0x5555));
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(at + 4, 2), (ushort)(Gray(y, x) ^ 0xAAAA));
            }
        }

        var bytes = await WriteTiledAsync(pixels, width, height, new TiffPageOptions
        {
            SamplesPerPixel = 3,
            BitsPerSample = 16,
            Photometric = TiffPhotometric.Rgb,
            SampleFormat = TiffSampleFormat.Uint,
            Compression = compression,
            Layout = TiffLayout.Tiled,
            TileWidth = tile,
            TileHeight = tile,
        });

        AssertIsTiled(bytes, tile, tile);

        using var oracle = new MagickImage(bytes);
        using var px = oracle.GetPixels();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var got = px.GetPixel(x, y).ToArray()!;
                ((ushort)got[0]).ShouldBe(Gray(x, y), $"R({x}, {y})");
                ((ushort)got[1]).ShouldBe((ushort)(Gray(x, y) ^ 0x5555), $"G({x}, {y})");
                ((ushort)got[2]).ShouldBe((ushort)(Gray(y, x) ^ 0xAAAA), $"B({x}, {y})");
            }
        }
    }

    /// <summary>
    /// The same raster written both ways decodes to the same picture. Strips are the covered path,
    /// so this pins the tiled one against it without either reader being the judge.
    /// </summary>
    [Fact]
    public async Task TiledAndStripped_AreTheSamePicture()
    {
        const int width = 70;
        const int height = 50;
        var pixels = GrayRaster(width, height);

        TiffPageOptions Common(TiffLayout layout) => new()
        {
            SamplesPerPixel = 1,
            BitsPerSample = 16,
            Photometric = TiffPhotometric.MinIsBlack,
            SampleFormat = TiffSampleFormat.Uint,
            Compression = TiffCompression.Deflate,
            Layout = layout,
        };

        var tiled = await WriteTiledAsync(pixels, width, height, Common(TiffLayout.Tiled) with
        {
            TileWidth = 32,
            TileHeight = 16,
        });
        var stripped = await WriteTiledAsync(pixels, width, height, Common(TiffLayout.Strip) with
        {
            RowsPerStrip = 8,
        });

        using var a = new MagickImage(tiled);
        using var b = new MagickImage(stripped);

        // Signatures compare the decoded rasters, not the files -- the encodings differ by design.
        a.Signature.ShouldBe(b.Signature, "the layout is a storage choice, not a change to the image");
    }

    /// <summary>
    /// A tiled page must carry the tile tags and NONE of the strip ones. TIFF 6.0 p.68: the two
    /// layouts are exclusive, and a file with both is what a half-finished fallback would emit.
    /// </summary>
    private static void AssertIsTiled(byte[] tiff, int tileWidth, int tileHeight)
    {
        var tags = ReadFirstIfdTags(tiff);

        tags.ShouldContainKey((ushort)322, "TileWidth is missing, so this page is not tiled");
        tags.ShouldContainKey((ushort)323, "TileLength is missing, so this page is not tiled");
        tags.ShouldContainKey((ushort)324, "TileOffsets is missing");
        tags.ShouldContainKey((ushort)325, "TileByteCounts is missing");
        tags[322].ShouldBe((uint)tileWidth);
        tags[323].ShouldBe((uint)tileHeight);

        tags.ShouldNotContainKey((ushort)273, "StripOffsets on a tiled page: the layouts are exclusive");
        tags.ShouldNotContainKey((ushort)279, "StripByteCounts on a tiled page: the layouts are exclusive");
        tags.ShouldNotContainKey((ushort)278, "RowsPerStrip on a tiled page: the layouts are exclusive");
    }

    /// <summary>
    /// Tag id -> first value of the first IFD. Deliberately tiny and deliberately NOT
    /// <see cref="TiffReader"/>: this has to be able to describe a file our own reader rejects,
    /// which a tiled one is.
    /// </summary>
    private static Dictionary<ushort, uint> ReadFirstIfdTags(byte[] tiff)
    {
        BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(0, 2)).ShouldBe((ushort)0x4949, "little-endian TIFF expected");
        BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(2, 2)).ShouldBe((ushort)42);

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

            // Only the first value, and only for the two widths this writer emits. A value that does
            // not fit the 4-byte field is a pointer; follow it, since TileOffsets always is one.
            var inline = type switch
            {
                3 => n * 2u <= 4u,       // SHORT
                4 => n * 4u <= 4u,       // LONG
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

    private static async Task<byte[]> WriteTiledAsync(
        byte[] pixels, int width, int height, TiffPageOptions options)
    {
        using var ms = new MemoryStream();
        await using (var writer = TiffWriter.Create(ms))
        {
            await writer.AddPageAsync(pixels, width, height, options);
            await writer.FlushAsync();
        }
        return ms.ToArray();
    }
}
