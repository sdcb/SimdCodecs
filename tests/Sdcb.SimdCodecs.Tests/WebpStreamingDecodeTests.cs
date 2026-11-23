using System.Buffers.Binary;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Sdcb.SimdCodecs.Tests.Streaming;
using Sdcb.SimdCodecs.Webp;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="WebpImageDecoder.CreateRowReader"/>: a lossy still streams (one token partition from a window of the
/// input, several from the buffered chunk), everything else falls back; either way the rows equal
/// <see cref="WebpImageDecoder.TryDecodeInto"/>'s — and so, where the goldens and oracle tests pin those, libwebp's —
/// under every feed, and a file the span decoder refuses fails with <see cref="InvalidDataException"/>.
/// </summary>
public sealed class WebpStreamingDecodeTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static ImageDecoderRegistration Codec => WebpImageDecoder.Registration;

    private static readonly PixelFormat[] Formats =
    [
        PixelFormat.Rgba32, PixelFormat.Bgra32, PixelFormat.Rgb24, PixelFormat.Bgr24, PixelFormat.Gray8,
        PixelFormat.GrayAlpha16, PixelFormat.Rgba64, PixelFormat.Gray16, PixelFormat.Rgba32Premultiplied,
        PixelFormat.Bgra32Premultiplied,
    ];

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(FixtureDirectory, "*.webp").Order())
            data.Add(Path.GetFileName(path));
        data.Add("magick:rgba");
        data.Add("magick:tall");
        data.Add("encoder:wide");
        return data;
    }

    private static byte[] Load(string name) => name switch
    {
        "magick:rgba" => MagickLossy(171, 99, alpha: true),
        "magick:tall" => MagickLossy(37, 530, alpha: false),
        "encoder:wide" => EncodeLossy(611, 75, seed: 3, alpha: true),
        _ => File.ReadAllBytes(Path.Combine(FixtureDirectory, name)),
    };

    // Foreign bytes: a lossy WebP written by ImageMagick (libwebp's encoder inside) from a gradient with noise.
    private static byte[] MagickLossy(int width, int height, bool alpha)
    {
        var channels = alpha ? 4 : 3;
        var pixels = new byte[width * height * channels];
        var rng = new Random(width * 7919 + height);
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)((i / channels % width) * 255 / width ^ rng.Next(0, 40));
        using var image = new MagickImage();
        image.ReadPixels(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, alpha ? "RGBA" : "RGB"));
        image.Quality = 70;
        image.Settings.SetDefine(MagickFormat.WebP, "lossless", false);
        return image.ToByteArray(MagickFormat.WebP);
    }

    private static byte[] EncodeLossy(int width, int height, int seed, bool alpha, int quality = 60)
    {
        var pixels = new byte[width * height * 4];
        var rng = new Random(seed);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(x * 3 + rng.Next(0, 16));
                pixels[i + 1] = (byte)(y * 5 ^ x);
                pixels[i + 2] = (byte)((x + y) * 2);
                pixels[i + 3] = alpha ? (byte)(x * y % 256) : (byte)255;
            }
        }
        return WebpEncoder.EncodeLossy(pixels, width, height, PixelFormat.Rgba32,
            new WebpLossyEncodeOptions { Quality = quality, Effort = 0 });
    }

    private static bool Streams(byte[] file) => WebpDecoder.ReadInfo(file) is { Format: WebpFormat.Lossy };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Rows_equal_TryDecodeInto_for_every_format_and_feed(string fixture)
    {
        var file = Load(fixture);
        var streams = Streams(file);
        foreach (var format in Formats)
        {
            // One byte per read is slow; it runs on the default format only.
            var feeds = format == PixelFormat.Rgba32 ? StreamingAssert.AllFeeds
                : [StreamingAssert.Feed.RandomChunks, StreamingAssert.Feed.AsyncOnly, StreamingAssert.Feed.Whole];
            StreamingAssert.RowsEqualDecodeInto(Codec, file, format, expectStreaming: streams, feeds: feeds);
        }
    }

    [Theory]
    [InlineData("noise-64x48-vp8-partitions2.webp")]
    [InlineData("noise-64x48-vp8-partitions4.webp")]
    [InlineData("noise-64x48-vp8-partitions8.webp")]
    [InlineData("lossy-alpha-q50.webp")]
    [InlineData("testsrc2-320x240-q75.webp")]
    public void Lossy_stills_stream(string fixture)
    {
        var file = Load(fixture);
        Streams(file).ShouldBeTrue();
        StreamingAssert.RowsEqualDecodeInto(Codec, file, PixelFormat.Rgba32, expectStreaming: true,
            feeds: [StreamingAssert.Feed.RandomChunks]);
    }

    [Theory]
    [InlineData("lossless-z6.webp")]
    [InlineData("anim-img2webp-lossy.webp")]
    [InlineData("anim-webpmux-metadata.webp")]
    public void Lossless_and_animated_input_fall_back(string fixture)
    {
        var file = Load(fixture);
        StreamingAssert.RowsEqualDecodeInto(Codec, file, PixelFormat.Rgba32, expectStreaming: false,
            feeds: [StreamingAssert.Feed.RandomChunks, StreamingAssert.Feed.AsyncOnly]);
    }

    [Fact]
    public void Options_other_than_the_whole_image_fall_back()
    {
        var file = Load("testsrc2-320x240-q75.webp");
        StreamingAssert.RowsEqualDecodeInto(Codec, file, PixelFormat.Rgb24, new DecodeOptions { ScaleDenominator = 2 },
            expectStreaming: false, feeds: [StreamingAssert.Feed.RandomChunks]);
        StreamingAssert.RowsEqualDecodeInto(Codec, file, PixelFormat.Rgba32,
            new DecodeOptions { Region = new PixelRect(5, 17, 100, 61) }, expectStreaming: false,
            feeds: [StreamingAssert.Feed.AsyncOnly]);
    }

    [Theory]
    [InlineData("webp-metadata.webp")]
    [InlineData("lossy-alpha-q50.webp")]
    [InlineData("testsrc2-320x240-q75.webp")]
    public void Info_is_TryReadInfo_for_chunks_before_the_image(string fixture)
    {
        var file = Load(fixture);
        WebpImageDecoder.TryReadInfo(file, out var expected).ShouldBeTrue();
        using var reader = WebpImageDecoder.CreateRowReader(new StreamInput(new ChunkedStream(file, 64)), PixelFormat.Rgba32, null);
        reader.Initialize(Ct);
        var info = reader.Info;
        (info.Width, info.Height, info.Channels, info.Format, info.FrameCount).ShouldBe(
            (expected.Width, expected.Height, expected.Channels, expected.Format, expected.FrameCount));
        info.ColorEncoding.ShouldBe(expected.ColorEncoding);
    }

    [Fact]
    public async Task End_to_end_async_only_stream_equals_TryDecodeInto_and_dwebp_goldens()
    {
        // lossy-alpha-q50.webp is in webp-oracle-golden.tsv, so TryDecodeInto's RGBA is libwebp 1.6.0's.
        var path = Path.Combine(FixtureDirectory, "lossy-alpha-q50.webp");
        var file = await File.ReadAllBytesAsync(path, Ct);
        WebpImageDecoder.TryReadInfo(file, out var info).ShouldBeTrue();
        var expected = new byte[info.Width * info.Height * 4];
        WebpImageDecoder.TryDecodeInto(file, expected, 0, PixelFormat.Rgba32, cancellationToken: Ct).ShouldBeTrue();

        foreach (var make in new Func<Stream>[]
                 {
                     () => new ChunkedStream(file, maxChunk: 61, asyncOnly: true),
                     () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 512, useAsync: true),
                 })
        {
            await using var reader = Codec.CreateRowReader!(new StreamInput(make(), initialCapacity: 16), PixelFormat.Rgba32, null);
            await reader.InitializeAsync(Ct);
            reader.IsStreaming.ShouldBeTrue();
            var actual = new byte[expected.Length];
            var rowBytes = info.Width * 4;
            while (reader.RowsRead < reader.Height)
                (await reader.ReadRowsAsync(actual.AsMemory(reader.RowsRead * rowBytes), 5, 0, Ct)).ShouldBeGreaterThan(0);
            actual.ShouldBe(expected);
        }
    }

    [Fact]
    public void First_rows_read_only_a_prefix()
    {
        var file = EncodeLossy(512, 512, seed: 9, alpha: false);
        var stream = new ChunkedStream(file, maxChunk: 256);
        using var reader = WebpImageDecoder.CreateRowReader(new StreamInput(stream, initialCapacity: 16), PixelFormat.Rgba32, null);
        var band = new byte[512 * 4 * 16];
        reader.ReadRows(band, 16, 0, Ct).ShouldBe(16);
        reader.IsStreaming.ShouldBeTrue();
        stream.BytesRead.ShouldBeLessThan(file.Length / 4, $"read {stream.BytesRead} of {file.Length} bytes for 16 of 512 rows");
    }

    private static readonly Lazy<byte[]> Huge = new(() => EncodeLossy(8192, 8192, seed: 5, alpha: false, quality: 30));

    [Fact]
    public void A_large_single_partition_image_streams_in_bounded_memory()
    {
        var file = Huge.Value;
        const int width = 8192;
        var band = new byte[width * 4 * 16];
        long peakWindow = 0;
        StreamingAssert.AllocatesLessThan(1 << 20, () =>
        {
            var stream = new ChunkedStream(file, maxChunk: 64 * 1024);
            using var reader = WebpImageDecoder.CreateRowReader(new StreamInput(stream, initialCapacity: 4096), PixelFormat.Rgba32, null);
            reader.Initialize(Ct);
            reader.IsStreaming.ShouldBeTrue();
            while (reader.RowsRead < reader.Height)
            {
                var rows = reader.RowsRead;
                reader.ReadRows(band, 16, 0, Ct).ShouldBeGreaterThan(0);
                if (rows == 0)
                    peakWindow = stream.BytesRead;
            }
        }, "8192x8192 streamed decode");
        // The whole image is 256 MiB of RGBA; the first band needed a small part of the file.
        peakWindow.ShouldBeLessThan(file.Length / 8);
    }

    [Theory]
    [InlineData("lossy-alpha-q50.webp")]
    [InlineData("noise-64x48-vp8-partitions4.webp")]
    [InlineData("webp-metadata.webp")]
    [InlineData("odd-33x19-libvpx.webp")]
    public void A_truncated_stream_fails_where_TryDecodeInto_does(string fixture)
    {
        var file = Load(fixture);
        WebpImageDecoder.TryReadInfo(file, out var info).ShouldBeTrue();
        var buffer = new byte[info.Width * info.Height * 4];
        for (var cut = 0; cut < file.Length; cut += Math.Max(1, file.Length / 97))
        {
            var truncated = file[..cut];
            WebpImageDecoder.TryDecodeInto(truncated, buffer, 0, PixelFormat.Rgba32, cancellationToken: Ct).ShouldBeFalse();
            Should.Throw<InvalidDataException>(() => StreamingAssert.ReadAll(truncated, StreamingAssert.Feed.RandomChunks,
                input => WebpImageDecoder.CreateRowReader(input, PixelFormat.Rgba32, null), out _, out _, out _), $"cut at {cut}");
        }
    }

    [Theory]
    [InlineData("testsrc2-320x240-q75.webp")]
    [InlineData("lossy-alpha-q50.webp")]
    [InlineData("noise-64x48-vp8-partitions2.webp")]
    public void A_short_token_partition_decodes_as_the_span_decoder_does(string fixture)
    {
        // The chunk and RIFF sizes say the image ends early: the span decoder reads zero bits past its partitions'
        // end, and so must the streaming one, at the same place.
        var file = Load(fixture);
        var vp8 = FindChunk(file, "VP8 "u8);
        var length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(vp8 + 4));
        var firstPartition = (file[vp8 + 8] | file[vp8 + 9] << 8 | file[vp8 + 10] << 16) >> 5;
        var tokens = length - 10 - firstPartition;
        foreach (var drop in new[] { 1, 2, 7, 8, 9, tokens / 3, tokens / 2, tokens - 30 })
        {
            if (drop <= 0 || drop >= tokens) continue;
            var shortened = Shorten(file, vp8, length, drop);
            var buffer = new byte[4 << 20];
            if (WebpImageDecoder.TryDecodeInto(shortened, buffer, 0, PixelFormat.Rgba32, cancellationToken: Ct))
                StreamingAssert.RowsEqualDecodeInto(Codec, shortened, PixelFormat.Rgba32, expectStreaming: true,
                    feeds: [StreamingAssert.Feed.OneByte, StreamingAssert.Feed.RandomChunks]);
            else
                Should.Throw<InvalidDataException>(() => StreamingAssert.ReadAll(shortened, StreamingAssert.Feed.RandomChunks,
                    input => WebpImageDecoder.CreateRowReader(input, PixelFormat.Rgba32, null), out _, out _, out _));
        }
    }

    [Fact]
    public void A_late_alpha_chunk_is_refused()
    {
        // A simple-format file with an ALPH chunk after its VP8 chunk: the span decoder applies it, a streaming
        // decoder has emitted the rows by then, so it refuses the file instead of returning different pixels.
        var file = Load("testsrc2-320x240-q75.webp");
        var alpha = new byte[8 + 1 + 320 * 240];
        "ALPH"u8.CopyTo(alpha);
        BinaryPrimitives.WriteInt32LittleEndian(alpha.AsSpan(4), 1 + 320 * 240);
        alpha.AsSpan(9).Fill(77);
        var late = file.Concat(alpha).Concat(new byte[alpha.Length & 1]).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(late.AsSpan(4), late.Length - 8);
        WebpImageDecoder.TryDecodeInto(late, new byte[320 * 240 * 4], 0, PixelFormat.Rgba32, cancellationToken: Ct).ShouldBeTrue();
        Should.Throw<InvalidDataException>(() => StreamingAssert.ReadAll(late, StreamingAssert.Feed.Whole,
            input => WebpImageDecoder.CreateRowReader(input, PixelFormat.Rgba32, null), out _, out _, out _));
    }

    [Fact]
    public void Streaming_fuzz()
    {
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("WEBP_STREAM_FUZZ_ITERATIONS"), out var n) ? n : 1500;
        var seeds = new[]
        {
            Load("lossy-alpha-q50.webp"), Load("noise-64x48-vp8-partitions4.webp"), Load("webp-metadata.webp"),
            Load("noise-32x32-segments4.webp"), Load("odd-33x19-libvpx.webp"), Load("tiny.webp"), Load("lossless-3x7.webp"),
        };
        var rng = new Random(20261123);
        var buffer = new byte[1 << 22];
        for (var i = 0; i < iterations; i++)
        {
            Ct.ThrowIfCancellationRequested();
            var file = Mutate(seeds[rng.Next(seeds.Length)], rng);
            // A mutated header can declare a canvas of up to 2^26 pixels: such files are not compared (ReadAll would
            // allocate the whole canvas), only those small enough for the buffer.
            if (WebpImageDecoder.TryReadInfo(file, out var info) && (long)info.Width * info.Height * 4 > buffer.Length)
                continue;
            var spanOk = WebpImageDecoder.TryDecodeInto(file, buffer, 0, PixelFormat.Rgba32, cancellationToken: Ct);
            byte[] rows;
            try
            {
                rows = StreamingAssert.ReadAll(file, StreamingAssert.Feed.RandomChunks,
                    input => WebpImageDecoder.CreateRowReader(input, PixelFormat.Rgba32, null), out _, out _, out _, seed: i);
            }
            catch (InvalidDataException) when (!spanOk || HasLateImageChunk(file))
            {
                continue;
            }

            spanOk.ShouldBeTrue($"iteration {i}: the stream decoded a file TryDecodeInto refuses " +
                $"({Convert.ToHexString(file.AsSpan(0, Math.Min(64, file.Length)))})");
            rows.AsSpan().SequenceEqual(buffer.AsSpan(0, rows.Length)).ShouldBeTrue($"iteration {i}: rows differ");
        }
    }

    private static byte[] Mutate(byte[] seed, Random rng)
    {
        var file = (byte[])seed.Clone();
        switch (rng.Next(5))
        {
            case 0: // flip bytes
                for (var k = rng.Next(1, 6); k > 0; k--)
                    file[rng.Next(file.Length)] ^= (byte)rng.Next(1, 256);
                return file;
            case 1: // truncate
                return file[..rng.Next(file.Length)];
            case 2: // flip a byte inside the token data only, so the container stays valid
                file[rng.Next(Math.Min(file.Length - 1, 40), file.Length)] = (byte)rng.Next(256);
                return file;
            case 3: // flip a length field
                {
                    var at = rng.Next(4) switch { 0 => 4, 1 => 16, _ => rng.Next(12, Math.Max(13, file.Length - 4)) };
                    if (at + 4 <= file.Length)
                        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), (uint)rng.Next(0, file.Length + 64));
                    return file;
                }
            default: // append a random chunk and fix the RIFF size
                {
                    string[] types = ["EXIF", "XMP ", "ICCP", "ALPH", "VP8X", "ANIM", "ANMF", "abcd", "VP8 "];
                    var payload = new byte[rng.Next(0, 24)];
                    rng.NextBytes(payload);
                    var chunk = new byte[8 + payload.Length + (payload.Length & 1)];
                    System.Text.Encoding.ASCII.GetBytes(types[rng.Next(types.Length)]).CopyTo(chunk, 0);
                    BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), payload.Length);
                    payload.CopyTo(chunk, 8);
                    var grown = file.Concat(chunk).ToArray();
                    if (grown.Length >= 8)
                        BinaryPrimitives.WriteInt32LittleEndian(grown.AsSpan(4), grown.Length - 8);
                    return grown;
                }
        }
    }

    // The two layouts the streaming reader refuses although the span decoder may accept them.
    private static bool HasLateImageChunk(byte[] file)
    {
        var sawImage = false;
        for (var offset = 12; offset + 8 <= file.Length;)
        {
            var type = file.AsSpan(offset, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset + 4));
            if (type.SequenceEqual("VP8 "u8)) sawImage = true;
            else if (sawImage && (type.SequenceEqual("ALPH"u8) || type.SequenceEqual("VP8X"u8))) return true;
            var next = offset + 8L + length + (length & 1);
            if (next > file.Length) break;
            offset = (int)next;
        }
        return false;
    }

    private static int FindChunk(byte[] file, ReadOnlySpan<byte> type)
    {
        for (var offset = 12; offset + 8 <= file.Length;)
        {
            if (file.AsSpan(offset, 4).SequenceEqual(type)) return offset;
            var length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset + 4));
            offset += 8 + length + (length & 1);
        }
        throw new InvalidOperationException("No such chunk.");
    }

    // The file with the last drop bytes of its VP8 chunk removed and the chunk / RIFF sizes fixed.
    private static byte[] Shorten(byte[] file, int vp8, int length, int drop)
    {
        var end = vp8 + 8 + length;
        var newLength = length - drop;
        var head = file.AsSpan(0, vp8 + 8 + newLength).ToArray();
        var pad = new byte[newLength & 1];
        var tail = file.AsSpan(end + (length & 1)).ToArray();
        var result = head.Concat(pad).Concat(tail).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(vp8 + 4), newLength);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length - 8);
        return result;
    }
}
