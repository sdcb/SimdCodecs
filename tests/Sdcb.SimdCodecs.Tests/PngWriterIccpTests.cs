using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Sdcb.SimdCodecs.Png;
using Sdcb.SimdCodecs.Color.Icc;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Verifies the optional <c>iCCP</c> chunk emission added to
/// <see cref="PngWriter"/>. Asserts chunk ordering (must follow IHDR, precede
/// IDAT), the keyword + null + compression-method layout, and that the
/// zlib-inflated payload exactly equals the input ICC bytes. Uses the
/// bundled <see cref="IccProfiles.SRgbV4"/> for the end-to-end check so the
/// resource loader is exercised on the same code path callers will use.
/// </summary>
public sealed class PngWriterIccpTests
{
    [Fact]
    public void Encode_NoIccProfile_OmitsIccpChunk()
    {
        var pixels = MakeRgbaCheckerboard(4, 4);
        var png = PngWriter.Encode(pixels, 4, 4);

        FindChunk(png, "iCCP"u8).ShouldBeNull();
        FindChunk(png, "IHDR"u8).ShouldNotBeNull();
        FindChunk(png, "IDAT"u8).ShouldNotBeNull();
    }

    [Fact]
    public void Encode_WithSRgbV4Profile_EmbedsAndRoundTrips()
    {
        var pixels = MakeRgbaCheckerboard(4, 4);
        var profile = IccProfiles.SRgbV4.Span;
        var png = PngWriter.Encode(pixels, 4, 4, profile);

        var iccp = FindChunk(png, "iCCP"u8);
        iccp.ShouldNotBeNull();

        // Inflate the chunk payload and compare to the original profile bytes.
        var inflated = InflateIccpPayload(png, iccp.Value);
        inflated.ShouldBe(profile.ToArray());

        // Order: iCCP must come after IHDR and strictly before the first IDAT.
        var ihdrEnd = ChunkEnd(FindChunk(png, "IHDR"u8)!.Value);
        var idatStart = FindChunk(png, "IDAT"u8)!.Value.HeaderStart;
        iccp.Value.HeaderStart.ShouldBeGreaterThan(ihdrEnd - 1);
        iccp.Value.HeaderStart.ShouldBeLessThan(idatStart);
    }

    [Fact]
    public void Encode_WithSRgbV4Profile_UsesIccProfileKeyword()
    {
        var pixels = MakeRgbaCheckerboard(4, 4);
        var png = PngWriter.Encode(pixels, 4, 4, IccProfiles.SRgbV4.Span);
        var iccp = FindChunk(png, "iCCP"u8)!.Value;

        // Chunk data starts at HeaderStart + 8 (4 length + 4 type). The
        // keyword is the bytes up to the first NUL.
        var dataStart = iccp.HeaderStart + 8;
        var nul = Array.IndexOf(png, (byte)0, dataStart);
        nul.ShouldBeGreaterThanOrEqualTo(0);
        Encoding.ASCII.GetString(png, dataStart, nul - dataStart).ShouldBe("ICC profile");

        // After the NUL: compression method byte. Spec requires 0.
        png[nul + 1].ShouldBe((byte)0);
    }

    private record struct ChunkLocation(int HeaderStart, int Length);

    private static int ChunkEnd(ChunkLocation chunk) => chunk.HeaderStart + 12 + chunk.Length;

    /// <summary>Locate the first chunk with the given 4-byte type.</summary>
    private static ChunkLocation? FindChunk(byte[] png, ReadOnlySpan<byte> type)
    {
        int pos = 8; // skip PNG signature
        while (pos + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos, 4));
            if (png.AsSpan(pos + 4, 4).SequenceEqual(type))
                return new ChunkLocation(pos, length);
            pos += 12 + length; // length(4) + type(4) + data + crc(4)
        }
        return null;
    }

    private static byte[] InflateIccpPayload(byte[] png, ChunkLocation iccp)
    {
        var dataStart = iccp.HeaderStart + 8;
        var dataEnd = dataStart + iccp.Length;

        // Skip keyword (up to NUL) and the 2 bytes after (NUL + comp method).
        var nul = Array.IndexOf(png, (byte)0, dataStart);
        var zlibStart = nul + 2;

        using var src = new MemoryStream(png, zlibStart, dataEnd - zlibStart);
        using var z = new ZLibStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        z.CopyTo(dst);
        return dst.ToArray();
    }

    private static byte[] MakeRgbaCheckerboard(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var v = (byte)(((x + y) & 1) == 0 ? 0 : 255);
            px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 0xFF;
        }
        return px;
    }
}
