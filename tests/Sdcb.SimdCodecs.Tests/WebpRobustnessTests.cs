using System.Buffers.Binary;
using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs.Tests;

public sealed class WebpRobustnessTests
{
    private const int ExhaustiveTruncationLimit = 4096;
    private const int MutationsPerFixture = 192;
    private const int MaximumMutationDecodePixels = 1_000_000;
    private const long MaximumMutationAnimationPixels = 1L << 22;

    [Fact]
    public void TruncationsAndBitFlipsAcrossFixturesStayWithinDecodeContracts()
    {
        string[] fixtures = Directory.GetFiles(FixtureDirectory(), "*.webp")
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(fixtures);

        var random = new Random(0x5eed_9649);
        foreach (string fixture in fixtures)
        {
            byte[] source = File.ReadAllBytes(fixture);
            IEnumerable<int> truncationLengths = source.Length <= ExhaustiveTruncationLimit
                ? Enumerable.Range(0, source.Length)
                : SampleTruncationLengths(source.Length);
            foreach (int length in truncationLengths)
            {
                CheckCandidate(source[..length], fixture, $"truncated at {length}");
            }

            // Three mutation shapes, a third each: a single bit flip, a byte overwritten with a random
            // value, and a burst of 2-8 flips anywhere in the file (a header and its payload at once).
            for (int mutation = 0; mutation < MutationsPerFixture; mutation++)
            {
                byte[] changed = source.ToArray();
                int offset = random.Next(changed.Length);
                string description;
                switch (mutation % 3)
                {
                    case 0:
                        int bit = random.Next(8);
                        changed[offset] ^= (byte)(1 << bit);
                        description = $"bit flip at {offset}, bit {bit}";
                        break;
                    case 1:
                        byte value = (byte)random.Next(256);
                        changed[offset] = value;
                        description = $"byte at {offset} = 0x{value:X2}";
                        break;
                    default:
                        int flips = random.Next(2, 9);
                        for (int flip = 0; flip < flips; flip++)
                        {
                            changed[random.Next(changed.Length)] ^= (byte)(1 << random.Next(8));
                        }

                        description = $"{flips} random bit flips (seeded)";
                        break;
                }

                CheckCandidate(changed, fixture, $"mutation {mutation}: {description}");
            }
        }
    }

    // The decompression bomb from review: one-pixel ANMF frames cost ~50 bytes each, but every frame
    // DecodeAnimation returns is a full-canvas copy, so 64 of them on a 4096x4096 canvas (a ~3 KB
    // file) used to allocate 4 GiB. It must now be refused before the first canvas is allocated.
    [Fact]
    public void DecodeAnimation_RefusesManyTinyFramesOnALargeCanvasBeforeAllocating()
    {
        byte[] bomb = BuildTinyFrameAnimation(4096, 4096, frames: 64);
        Assert.True(bomb.Length < 4096, $"bomb is {bomb.Length} bytes");

        long before = GC.GetAllocatedBytesForCurrentThread();
        var exception = Assert.Throws<InvalidDataException>(() => WebpDecoder.DecodeAnimation(bomb));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("WebpAnimationDecoder", exception.Message);
        // One 4096x4096 RGBA canvas is 64 MiB; refusing up front must cost far less than that.
        Assert.True(allocated < 4 << 20, $"refusal allocated {allocated:N0} bytes");
    }

    // The same stream shape is a real animation, not merely malformed: the limit is what refuses it.
    [Fact]
    public void DecodeAnimation_TotalPixelLimitIsExactAndTheStreamIsOtherwiseValid()
    {
        byte[] animation = BuildTinyFrameAnimation(64, 32, frames: 8);
        const long exact = 64 * 32 * 8;

        WebpAnimation decoded = WebpDecoder.DecodeAnimation(animation, exact);
        Assert.Equal(8, decoded.Frames.Count);
        Assert.All(decoded.Frames, frame => Assert.Equal(64 * 32 * 4, frame.Rgba.Length));

        Assert.Throws<InvalidDataException>(() => WebpDecoder.DecodeAnimation(animation, exact - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebpDecoder.DecodeAnimation(animation, 0));

        // The streaming decoder holds one canvas whatever the frame count, so it stays available.
        var streaming = new WebpAnimationDecoder(BuildTinyFrameAnimation(4096, 4096, frames: 64));
        Assert.Equal(64, streaming.FrameCount);
    }

    private static byte[] BuildTinyFrameAnimation(int canvasWidth, int canvasHeight, int frames)
    {
        // A real 1x1 VP8L bitstream, lifted from a committed cwebp fixture.
        byte[] still = File.ReadAllBytes(Path.Combine(FixtureDirectory(), "lossless-1x1.webp"));
        Assert.Equal("VP8L"u8.ToArray(), still[12..16]);
        byte[] vp8l = still.AsSpan(20, BinaryPrimitives.ReadInt32LittleEndian(still.AsSpan(16))).ToArray();

        byte[] vp8x = new byte[10];
        vp8x[0] = 0x02; // animation
        WriteUInt24(vp8x.AsSpan(4), canvasWidth - 1);
        WriteUInt24(vp8x.AsSpan(7), canvasHeight - 1);

        using var frame = new MemoryStream();
        byte[] header = new byte[16]; // X/2, Y/2 = 0; width-1, height-1 = 0; duration 0; flags 0
        frame.Write(header);
        WriteChunk(frame, "VP8L", vp8l);
        byte[] anmf = frame.ToArray();

        using var file = new MemoryStream();
        file.Write("RIFF"u8);
        file.Write(new byte[4]);
        file.Write("WEBP"u8);
        WriteChunk(file, "VP8X", vp8x);
        WriteChunk(file, "ANIM", new byte[6]);
        for (int index = 0; index < frames; index++)
        {
            WriteChunk(file, "ANMF", anmf);
        }

        byte[] bytes = file.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }

    private static void WriteChunk(Stream output, string type, byte[] payload)
    {
        output.Write(System.Text.Encoding.ASCII.GetBytes(type));
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)payload.Length);
        output.Write(length);
        output.Write(payload);
        if ((payload.Length & 1) != 0)
        {
            output.WriteByte(0);
        }
    }

    private static void WriteUInt24(Span<byte> data, int value)
    {
        data[0] = (byte)value;
        data[1] = (byte)(value >> 8);
        data[2] = (byte)(value >> 16);
    }

    private static IEnumerable<int> SampleTruncationLengths(int length) =>
        new HashSet<int>
        {
            0,
            1,
            11,
            12,
            16,
            length / 4,
            length / 2,
            length * 3 / 4,
            length - 1,
        }.Where(sample => sample >= 0 && sample < length).OrderBy(static sample => sample);

    private static void CheckCandidate(byte[] data, string fixture, string mutation)
    {
        string context = $"{Path.GetFileName(fixture)} {mutation}";
        bool infoSucceeded = AssertAllowedOutcome(context, () => WebpDecoder.ReadInfo(data));
        if (!infoSucceeded)
        {
            Assert.False(WebpDecoder.TryReadInfo(data, out _), context);
            Assert.False(WebpImageDecoder.TryReadInfo(data, out _), context);
        }

        // A flipped dimension bit can legitimately declare a canvas near the 2^26 limit, and decoding
        // that for real is up to 256 MiB per call -- several GB once xUnit runs tests in parallel. So
        // the allocating entry points only run on small declared canvases, and DecodeAnimation gets a
        // small explicit budget; refusal paths are still exercised above those sizes.
        _ = AssertAllowedOutcome(context, () => WebpDecoder.DecodeAnimation(data, MaximumMutationAnimationPixels));

        bool hasInfo = WebpDecoder.TryReadInfo(data, out WebpInfo info);
        if (hasInfo && (long)info.Width * info.Height <= MaximumMutationDecodePixels)
        {
            bool decoded = AssertAllowedOutcome(context, () => WebpDecoder.Decode(data));
            bool adapterDecoded = WebpImageDecoder.TryDecode(data, out _);
            Assert.True(decoded == adapterDecoded, context);

            byte[] destination = new byte[checked(info.Width * info.Height * 4)];
            bool decodeToSucceeded = AssertAllowedOutcome(context, () => WebpDecoder.DecodeTo(data, destination));
            Assert.True(decodeToSucceeded == WebpDecoder.TryDecodeTo(data, destination), context);
            Assert.True(
                decodeToSucceeded == WebpImageDecoder.TryDecodeIntoRgba8(data, destination),
                context);
        }
        else
        {
            Assert.False(WebpDecoder.TryDecodeTo(data, Span<byte>.Empty), context);
            Assert.False(WebpImageDecoder.TryDecodeIntoRgba8(data, Span<byte>.Empty), context);
        }

        if (!hasInfo)
        {
            Assert.False(WebpImageDecoder.TryDecode(data, out _), context);
        }
    }

    private static bool AssertAllowedOutcome(string context, Action decode)
    {
        try
        {
            decode();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (Exception exception)
        {
            Assert.Fail($"{context} threw unexpected {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static string FixtureDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp");
}
