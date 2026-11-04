using System.Buffers.Binary;
using System.Text;
using Sdcb.SimdCodecs.Jpeg2000;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Tier-2 beyond one tile and one precinct, made live by <c>opj_compress</c> and checked against
/// <c>opj_decompress</c>: several tiles (odd sizes, an odd image and tile origin), tiles split into
/// tile-parts, custom precincts, all five progression orders, SOP and EPH markers, the code-block
/// styles that are decoded, and the JP2 palette. The reversible codestreams must decode to OpenJPEG's
/// samples exactly; the irreversible ones to within one, as in <see cref="Jpeg2000IrreversibleTests"/>.
/// <para>
/// Issue #1's corpus (<see cref="Issue1Jpeg2000CorpusTests"/>) is what showed these were missing;
/// these are the committed, licence-clean version of the same coverage.
/// </para>
/// </summary>
public class Jpeg2000TilingAndProgressionTests
{
    /// <summary>
    /// A deterministic test picture: a gradient with structure and some noise, so every subband has
    /// something to code and no two tiles look alike.
    /// </summary>
    internal static byte[] Picture(int width, int height, int components)
    {
        var header = Encoding.ASCII.GetBytes($"P{(components == 3 ? 6 : 5)}\n{width} {height}\n255\n");
        var pixels = new byte[width * height * components];
        var seed = 12345u;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < components; c++)
                {
                    seed = seed * 1664525 + 1013904223;
                    var smooth = (x * (3 + c) + y * (5 - c)) % 256;
                    var stripe = ((x / 7 + y / 5 + c) & 1) * 40;
                    pixels[(y * width + x) * components + c] = (byte)Math.Clamp(smooth / 2 + stripe + (int)(seed >> 28), 0, 255);
                }
            }
        }

        return [.. header, .. pixels];
    }

    public static TheoryData<string, string> Reversible => new()
    {
        { "tiles, odd sizes", "-t 37,29 -n 3" },
        { "tiles, odd image and tile origin", "-t 37,29 -d 3,5 -T 1,2 -n 3" },
        { "tiles, odd origin, one-sample resolutions", "-t 9,7 -d 1,1 -n 3" },
        { "RLCP, layers", "-p RLCP -r 20,5,1 -t 64,64" },
        { "RPCL, precincts", "-p RPCL -c [32,32],[16,16] -r 10,1" },
        { "PCRL, precincts", "-p PCRL -c [64,64],[32,32],[16,16] -r 8,2" },
        { "CPRL, precincts", "-p CPRL -c [32,32] -n 4 -r 6,1" },
        { "LRCP, precincts and tiles", "-c [16,16] -t 48,40 -n 3 -r 5,1" },
        { "precincts smaller than code-blocks", "-c [16,16] -b 16,16 -n 4" },
        { "SOP and EPH", "-SOP -EPH -t 64,64 -r 10,1" },
        { "tile-parts by resolution", "-TP R -t 64,64 -p RPCL -r 10,1" },
        { "tile-parts by component", "-TP C -t 48,48 -p CPRL" },
        { "tile-parts by layer", "-TP L -t 64,64 -r 20,5,1" },
        { "segmentation symbols", "-M 32 -t 64,64" },
        { "context reset", "-M 2" },
        { "predictable termination", "-M 16" },
        { "reset, termination and segmentation together", "-M 50 -p RPCL -c [32,32]" },
    };

    [Theory]
    [MemberData(nameof(Reversible))]
    public void GreyCodestream_DecodesExactlyAsOpenJpegDoes(string what, string options)
    {
        OpenJpegOracle.RequireOrSkip();
        _ = what;

        var codestream = OpenJpegOracle.Compress(Picture(157, 123, 1), options.Split(' '));
        AssertMatches(codestream, ".pgm", tolerance: 0);
    }

    [Theory]
    [MemberData(nameof(Reversible))]
    public void RgbCodestream_DecodesExactlyAsOpenJpegDoes(string what, string options)
    {
        OpenJpegOracle.RequireOrSkip();
        _ = what;

        var codestream = OpenJpegOracle.Compress(Picture(101, 89, 3), options.Split(' '));
        AssertMatches(codestream, ".ppm", tolerance: 0);
    }

    public static TheoryData<string, string> Irreversible => new()
    {
        { "tiles, odd origin", "-I -t 37,29 -d 3,5 -T 1,2 -n 3 -r 20" },
        { "tiles, odd origin, one-sample resolutions", "-I -t 9,7 -d 1,1 -n 3" },
        { "RPCL, precincts, SOP, EPH, segmentation", "-I -p RPCL -c [32,32],[16,16] -SOP -EPH -M 32 -t 64,64 -r 40,10" },
        { "PCRL, tile-parts", "-I -p PCRL -TP R -t 48,48 -r 30" },
        { "CPRL, layers", "-I -p CPRL -c [64,64] -r 50,20,5" },
    };

    [Theory]
    [MemberData(nameof(Irreversible))]
    public void IrreversibleCodestream_DecodesWithinOneOfOpenJpeg(string what, string options)
    {
        OpenJpegOracle.RequireOrSkip();
        _ = what;

        AssertMatches(OpenJpegOracle.Compress(Picture(157, 123, 1), options.Split(' ')), ".pgm", tolerance: 1);
        AssertMatches(OpenJpegOracle.Compress(Picture(101, 89, 3), options.Split(' ')), ".ppm", tolerance: 1);
    }

    /// <summary>
    /// A reduced decode of a tiled image: every tile is reduced on its own, and the reduced tiles
    /// must still meet edge to edge as OpenJPEG's <c>-r</c> puts them.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TiledCodestream_ReducesAsOpenJpegDoes(int reduce)
    {
        OpenJpegOracle.RequireOrSkip();

        var codestream = OpenJpegOracle.Compress(Picture(157, 123, 1), "-t", "37,29", "-d", "3,5", "-T", "1,2", "-n", "4", "-p", "RPCL");
        var decoded = Jpeg2000Decoder.Decode(codestream, reduce, TestContext.Current.CancellationToken);
        var expected = OpenJpegOracle.Decode(codestream, ".pgm", "-r", reduce.ToString());

        decoded.Width.ShouldBe(expected.Width);
        decoded.Height.ShouldBe(expected.Height);
        decoded.Samples.ShouldBe(expected.Samples);
    }

    /// <summary>
    /// A JP2 palette (pclr) with its component mapping (cmap): one component of indices turned into
    /// three of colour, checked against OpenJPEG decoding the same file.
    /// </summary>
    [Fact]
    public void PalettedJp2_DecodesAsOpenJpegDoes()
    {
        OpenJpegOracle.RequireOrSkip();

        // Indices 0..199 into a 200-entry palette.
        const int width = 61, height = 47, entries = 200;
        var indices = new byte[width * height];
        for (var i = 0; i < indices.Length; i++) indices[i] = (byte)((i * 7 + i / width * 3) % entries);
        byte[] pgm = [.. Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n"), .. indices];
        var codestream = OpenJpegOracle.Compress(pgm, "-t", "32,32");

        var palette = new List<byte> { (byte)(entries >> 8), (byte)(entries & 0xFF), 3, 7, 7, 7 };
        for (var e = 0; e < entries; e++) palette.AddRange([(byte)(e * 5 % 256), (byte)(255 - e), (byte)(e * e % 256)]);

        var file = Jp2(
            Box("jp2h", [
                .. Box("ihdr", [.. BigEndian32(height), .. BigEndian32(width), 0, 1, 7, 7, 0, 0]),
                .. Box("colr", [1, 0, 0, .. BigEndian32(16)]),
                .. Box("pclr", [.. palette]),
                .. Box("cmap", [0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 2]),
            ]),
            Box("jp2c", codestream));

        var decoded = Jpeg2000Decoder.Decode(file, TestContext.Current.CancellationToken);
        var expected = OpenJpegOracle.DecodeFile(file, ".jp2", ".ppm");

        decoded.Components.ShouldBe(3);
        decoded.BitDepth.ShouldBe(8);
        decoded.Width.ShouldBe(expected.Width);
        decoded.Samples.ShouldBe(expected.Samples);

        var info = Jpeg2000Decoder.ReadInfo(file);
        info.Components.ShouldBe(3);
        info.BitDepth.ShouldBe(8);
    }

    private static void AssertMatches(byte[] codestream, string extension, int tolerance)
    {
        var decoded = Jpeg2000Decoder.Decode(codestream, TestContext.Current.CancellationToken);
        var expected = OpenJpegOracle.Decode(codestream, extension);

        decoded.Width.ShouldBe(expected.Width);
        decoded.Height.ShouldBe(expected.Height);
        decoded.Components.ShouldBe(expected.Components);

        if (tolerance == 0)
        {
            decoded.Samples.ShouldBe(expected.Samples);
            return;
        }

        var worst = 0;
        for (var i = 0; i < decoded.Samples.Length; i++)
            worst = Math.Max(worst, Math.Abs(decoded.Samples[i] - expected.Samples[i]));
        worst.ShouldBeLessThanOrEqualTo(tolerance);
    }

    internal static byte[] Jp2(params byte[][] boxes)
    {
        byte[] signature = [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];
        var fileType = Box("ftyp", [.. "jp2 "u8, 0, 0, 0, 0, .. "jp2 "u8]);
        return [.. signature, .. fileType, .. boxes.SelectMany(b => b)];
    }

    internal static byte[] Box(string type, byte[] body) =>
        [.. BigEndian32(8 + body.Length), .. Encoding.ASCII.GetBytes(type), .. body];

    internal static byte[] BigEndian32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}
