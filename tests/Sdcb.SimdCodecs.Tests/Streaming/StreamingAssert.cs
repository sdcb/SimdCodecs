using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests.Streaming;

/// <summary>
/// The checks every streaming codec is held to: its rows, read every way a stream can arrive, are exactly
/// <see cref="IImageDecoder.TryDecodeInto"/>'s; its encoder hands bytes over before <c>Commit</c>; its memory stays
/// bounded.
/// </summary>
internal static class StreamingAssert
{
    /// <summary>How a test feeds the stream to a reader.</summary>
    public enum Feed
    {
        /// <summary>Synchronous, one byte per read, one row per call.</summary>
        OneByte,
        /// <summary>Synchronous, 1..4096 bytes per read, random bands of rows.</summary>
        RandomChunks,
        /// <summary>Asynchronous only (a synchronous read throws), 1..997 bytes per read, random bands.</summary>
        AsyncOnly,
        /// <summary>A seekable <see cref="MemoryStream"/>, all rows in one call.</summary>
        Whole,
    }

    public static readonly Feed[] AllFeeds = [Feed.OneByte, Feed.RandomChunks, Feed.AsyncOnly, Feed.Whole];

    /// <summary>
    /// Reads <paramref name="file"/> through <paramref name="codec"/>'s <see cref="ImageDecoderRegistration.CreateRowReader"/>
    /// fed every <see cref="Feed"/> (<paramref name="feeds"/>; one-byte feeding of a large file is slow, so a test may
    /// leave it out), and asserts each equals <see cref="ImageDecoderRegistration.TryDecodeInto"/> byte for byte.
    /// </summary>
    /// <param name="expectStreaming">When set, the reader's <see cref="IImageRowReader.IsStreaming"/> must equal it.</param>
    /// <returns>The rows, tightly packed.</returns>
    public static byte[] RowsEqualDecodeInto(ImageDecoderRegistration codec, byte[] file, PixelFormat format,
        DecodeOptions? options = null, bool? expectStreaming = null, Feed[]? feeds = null)
    {
        var create = codec.CreateRowReader ?? throw new ShouldAssertException($"{codec.Format} has no CreateRowReader.");
        codec.TryReadInfo(file, out var info).ShouldBeTrue("TryReadInfo");
        var (w, h) = (options ?? DecodeOptions.Default).GetOutputSize(info);
        var rowBytes = w * format.BytesPerPixel;
        var expected = new byte[rowBytes * h];
        codec.TryDecodeInto(file, expected, rowBytes, format, options, CancellationToken.None).ShouldBeTrue("TryDecodeInto");
        foreach (var feed in feeds ?? AllFeeds)
        {
            var actual = ReadAll(file, feed, input => create(input, format, options), out var streaming, out var rw, out var rh);
            (rw, rh).ShouldBe((w, h), $"{feed}: reader size");
            if (expectStreaming is { } e)
                streaming.ShouldBe(e, $"{feed}: IsStreaming");
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                var first = actual.AsSpan().CommonPrefixLength(expected);
                throw new ShouldAssertException($"{feed}: rows differ from TryDecodeInto at byte {first} (row {first / rowBytes}).");
            }
        }
        return expected;
    }

    /// <summary>Opens a reader over <paramref name="file"/> fed as <paramref name="feed"/> says and reads every row.</summary>
    public static byte[] ReadAll(byte[] file, Feed feed, Func<StreamInput, StreamingRowReader> create, out bool isStreaming,
        out int width, out int height, int seed = 1)
    {
        Stream stream = feed switch
        {
            Feed.OneByte => new ChunkedStream(file, maxChunk: 1, seed),
            Feed.RandomChunks => new ChunkedStream(file, maxChunk: 4096, seed),
            Feed.AsyncOnly => new ChunkedStream(file, maxChunk: 997, seed, asyncOnly: true),
            _ => new MemoryStream(file, writable: false),
        };
        // A small first buffer makes the input refill and grow across unit boundaries.
        using var reader = create(new StreamInput(stream, initialCapacity: feed == Feed.Whole ? StreamInput.DefaultCapacity : 16));
        var random = new Random(seed);
        if (feed == Feed.AsyncOnly)
            reader.InitializeAsync(TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult();
        else
            reader.Initialize(TestContext.Current.CancellationToken);
        width = reader.Width;
        height = reader.Height;
        var rowBytes = width * reader.Format.BytesPerPixel;
        var all = new byte[(long)rowBytes * height];
        while (reader.RowsRead < height)
        {
            var band = feed switch
            {
                Feed.OneByte => 1,
                Feed.Whole => height,
                _ => random.Next(1, 17),
            };
            var at = reader.RowsRead * rowBytes;
            var n = feed == Feed.AsyncOnly
                ? reader.ReadRowsAsync(all.AsMemory(at), band, 0, TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult()
                : reader.ReadRows(all.AsSpan(at), band, 0, TestContext.Current.CancellationToken);
            n.ShouldBeGreaterThan(0, $"{feed}: ReadRows returned 0 at row {reader.RowsRead} of {height}");
        }
        reader.ReadRows(Span<byte>.Empty, 1, 0, TestContext.Current.CancellationToken).ShouldBe(0, $"{feed}: rows after the last");
        isStreaming = reader.IsStreaming;
        return all;
    }

    /// <summary>
    /// Encodes <paramref name="pixels"/> through a session from <paramref name="create"/>, asserting it is streaming and
    /// that bytes reached the writer by the time half the rows were written (the image must be large enough for the
    /// codec's first band to complete). Returns the file.
    /// </summary>
    public static byte[] EncoderEmitsBeforeCommit(Func<IBufferWriter<byte>, IImageEncoderSession> create, byte[] pixels,
        int width, int height, PixelFormat format)
    {
        var output = new ArrayBufferWriter<byte>();
        using var session = create(output);
        session.IsStreaming.ShouldBeTrue("IsStreaming");
        var frame = session.AddFrame(width, height, format);
        var rowBytes = width * format.BytesPerPixel;
        var half = height / 2;
        frame.WriteRows(pixels.AsSpan(0, half * rowBytes), half, 0, TestContext.Current.CancellationToken);
        output.WrittenCount.ShouldBeGreaterThan(0, "no bytes reached the writer after half the rows");
        frame.WriteRows(pixels.AsSpan(half * rowBytes), height - half, 0, TestContext.Current.CancellationToken);
        session.Commit(TestContext.Current.CancellationToken);
        return output.WrittenSpan.ToArray();
    }

    /// <summary>Runs <paramref name="action"/> (synchronously, on this thread) and asserts it allocated less than
    /// <paramref name="maxBytes"/> on the managed heap. Pooled arrays count only when the pool has to make them.</summary>
    public static void AllocatesLessThan(long maxBytes, Action action, string? what = null)
    {
        action(); // warm the pools and JIT
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.ShouldBeLessThan(maxBytes, what);
    }
}
