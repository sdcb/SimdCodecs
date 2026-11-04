using System;
using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Gif;
using Sdcb.SimdCodecs.Ico;
using Shouldly;
using Xunit;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Unit tests for <see cref="GifImageEncoder"/> and <see cref="IcoImageEncoder"/> adapters.
/// </summary>
public sealed class EncoderAdapterGifIcoTests
{
    public static TheoryData<int> FormatIndices()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < PixelFormat.All.Length; i++)
            data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(FormatIndices))]
    public void Gif_single_frame_matches_existing_encoder(int formatIndex)
    {
        var ct = TestContext.Current.CancellationToken;
        var format = PixelFormat.All[formatIndex];
        const int w = 37, h = 11;
        const int chunk1 = 5, chunk2 = 6;
        var padding = 13;
        var stride = w * format.BytesPerPixel + padding;
        var pixels = new byte[stride * h];
        new Random(42 + formatIndex).NextBytes(pixels);

        var writer = new ArrayBufferWriter<byte>();
        using (var session = GifImageEncoder.Create(writer))
        {
            var frame = session.AddFrame(w, h, format);
            frame.WriteRows(pixels.AsSpan(0, stride * chunk1), chunk1, stride, cancellationToken: ct);
            frame.WriteRows(pixels.AsSpan(stride * chunk1), chunk2, stride, cancellationToken: ct);
            session.Commit(cancellationToken: ct);
        }
        var actual = writer.WrittenSpan.ToArray();

        var targetFormat = GifImageEncoder.GetClosestInputFormat(format);
        var converted = PixelConverter.ToPacked(pixels, stride, format, targetFormat, w, h);
        var expected = GifEncoder.Encode(converted, w, h, targetFormat.Channels);
        actual.ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(FormatIndices))]
    public void Ico_single_frame_matches_existing_encoder(int formatIndex)
    {
        var ct = TestContext.Current.CancellationToken;
        var format = PixelFormat.All[formatIndex];
        const int w = 37, h = 11;
        const int chunk1 = 5, chunk2 = 6;
        var padding = 13;
        var stride = w * format.BytesPerPixel + padding;
        var pixels = new byte[stride * h];
        new Random(1042 + formatIndex).NextBytes(pixels);

        var writer = new ArrayBufferWriter<byte>();
        using (var session = IcoImageEncoder.Create(writer))
        {
            var frame = session.AddFrame(w, h, format);
            frame.WriteRows(pixels.AsSpan(0, stride * chunk1), chunk1, stride, cancellationToken: ct);
            frame.WriteRows(pixels.AsSpan(stride * chunk1), chunk2, stride, cancellationToken: ct);
            session.Commit(cancellationToken: ct);
        }
        var actual = writer.WrittenSpan.ToArray();

        var targetFormat = IcoImageEncoder.GetClosestInputFormat(format);
        var converted = PixelConverter.ToPacked(pixels, stride, format, targetFormat, w, h);
        var expected = IcoEncoder.Encode(converted, w, h, targetFormat);
        actual.ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(FormatIndices))]
    public void Gif_animation_with_durations_matches_EncodeAnimation(int formatIndex)
    {
        var ct = TestContext.Current.CancellationToken;
        var format = PixelFormat.All[formatIndex];
        const int w = 37, h = 11;
        const int chunk1 = 5, chunk2 = 6;
        var padding = 13;
        var stride = w * format.BytesPerPixel + padding;
        var durations = new[]
        {
            TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(160),
            TimeSpan.FromMilliseconds(240),
        };

        var framePixels = new byte[3][];
        var rng = new Random(2000 + formatIndex);
        for (var i = 0; i < 3; i++)
        {
            framePixels[i] = new byte[stride * h];
            rng.NextBytes(framePixels[i]);
        }

        var writer = new ArrayBufferWriter<byte>();
        using (var session = GifImageEncoder.Create(writer))
        {
            for (var i = 0; i < 3; i++)
            {
                var frame = session.AddFrame(w, h, format, duration: durations[i]);
                frame.WriteRows(framePixels[i].AsSpan(0, stride * chunk1), chunk1, stride, cancellationToken: ct);
                frame.WriteRows(framePixels[i].AsSpan(stride * chunk1), chunk2, stride, cancellationToken: ct);
            }
            session.Commit(cancellationToken: ct);
        }
        var actual = writer.WrittenSpan.ToArray();

        var targetFormat = GifImageEncoder.GetClosestInputFormat(format);
        var animFrames = new GifAnimationFrame[3];
        for (var i = 0; i < 3; i++)
        {
            var converted = PixelConverter.ToPacked(framePixels[i], stride, format, targetFormat, w, h);
            animFrames[i] = new GifAnimationFrame(converted, durations[i]);
        }
        var expected = GifEncoder.EncodeAnimation(animFrames, w, h, targetFormat.Channels);
        actual.ShouldBe(expected);
    }

    [Fact]
    public void Gif_animation_with_null_duration_uses_default()
    {
        var ct = TestContext.Current.CancellationToken;
        const int w = 37, h = 11;
        const int chunk1 = 5, chunk2 = 6;
        var format = PixelFormat.Rgba32;
        var stride = w * format.BytesPerPixel + 7;

        var framePixels = new byte[3][];
        var rng = new Random(999);
        for (var i = 0; i < 3; i++)
        {
            framePixels[i] = new byte[stride * h];
            rng.NextBytes(framePixels[i]);
        }

        var durations = new TimeSpan?[]
        {
            TimeSpan.FromMilliseconds(100),
            null,
            TimeSpan.FromMilliseconds(300),
        };

        var writer = new ArrayBufferWriter<byte>();
        using (var session = GifImageEncoder.Create(writer))
        {
            for (var i = 0; i < 3; i++)
            {
                var frame = session.AddFrame(w, h, format, duration: durations[i]);
                frame.WriteRows(framePixels[i].AsSpan(0, stride * chunk1), chunk1, stride, cancellationToken: ct);
                frame.WriteRows(framePixels[i].AsSpan(stride * chunk1), chunk2, stride, cancellationToken: ct);
            }
            session.Commit(cancellationToken: ct);
        }
        var actual = writer.WrittenSpan.ToArray();

        var targetFormat = GifImageEncoder.GetClosestInputFormat(format);
        var animFrames = new GifAnimationFrame[3];
        for (var i = 0; i < 3; i++)
        {
            var converted = PixelConverter.ToPacked(framePixels[i], stride, format, targetFormat, w, h);
            animFrames[i] = new GifAnimationFrame(converted, durations[i] ?? default(GifAnimationFrame).Duration);
        }
        var expected = GifEncoder.EncodeAnimation(animFrames, w, h, targetFormat.Channels);
        actual.ShouldBe(expected);
    }

    [Fact]
    public void Gif_commit_with_missing_rows_throws_InvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        var writer = new ArrayBufferWriter<byte>();
        using var session = GifImageEncoder.Create(writer);
        var frame = session.AddFrame(37, 11, PixelFormat.Rgba32);
        var stride = 37 * 4 + 7;
        var pixels = new byte[stride * 5];
        frame.WriteRows(pixels, 5, stride, cancellationToken: ct);
        Should.Throw<InvalidOperationException>(() => session.Commit(cancellationToken: ct));
    }

    [Fact]
    public void Ico_commit_with_missing_rows_throws_InvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        var writer = new ArrayBufferWriter<byte>();
        using var session = IcoImageEncoder.Create(writer);
        var frame = session.AddFrame(37, 11, PixelFormat.Rgba32);
        var stride = 37 * 4 + 7;
        var pixels = new byte[stride * 5];
        frame.WriteRows(pixels, 5, stride, cancellationToken: ct);
        Should.Throw<InvalidOperationException>(() => session.Commit(cancellationToken: ct));
    }

    [Fact]
    public void Ico_dimension_over_256_throws_ArgumentException()
    {
        var writer = new ArrayBufferWriter<byte>();
        using var session = IcoImageEncoder.Create(writer);
        Should.Throw<ArgumentException>(() => session.AddFrame(257, 10, PixelFormat.Rgba32));
        Should.Throw<ArgumentException>(() => session.AddFrame(10, 257, PixelFormat.Rgba32));
    }

    [Fact]
    public void Ico_multiple_frames_matches_existing_encoder()
    {
        var ct = TestContext.Current.CancellationToken;
        var sizes = new[] { 16, 32 };
        var framesPixels = new byte[sizes.Length][];
        var strides = new int[sizes.Length];
        for (var i = 0; i < sizes.Length; i++)
        {
            var s = sizes[i];
            strides[i] = s * 4 + 5;
            framesPixels[i] = new byte[strides[i] * s];
            new Random(500 + i).NextBytes(framesPixels[i]);
        }

        var writer = new ArrayBufferWriter<byte>();
        using (var session = IcoImageEncoder.Create(writer))
        {
            for (var i = 0; i < sizes.Length; i++)
            {
                var s = sizes[i];
                var frame = session.AddFrame(s, s, PixelFormat.Rgba32);
                frame.WriteRows(framesPixels[i], s, strides[i], cancellationToken: ct);
            }
            session.Commit(cancellationToken: ct);
        }
        var actual = writer.WrittenSpan.ToArray();

        var expectedFrames = new IcoFrame[sizes.Length];
        for (var i = 0; i < sizes.Length; i++)
        {
            var s = sizes[i];
            var converted = PixelConverter.ToPacked(framesPixels[i], strides[i], PixelFormat.Rgba32, PixelFormat.Rgba32, s, s);
            expectedFrames[i] = new IcoFrame(converted, s, s, PixelFormat.Rgba32);
        }
        var expected = IcoEncoder.Encode(expectedFrames);
        actual.ShouldBe(expected);
    }

    [Fact]
    public void Encoder_registrations_are_correct()
    {
        GifImageEncoder.Format.ShouldBe(ImageFormat.Gif);
        GifImageEncoder.Registration.Format.ShouldBe(ImageFormat.Gif);

        IcoImageEncoder.Format.ShouldBe(ImageFormat.Ico);
        IcoImageEncoder.Registration.Format.ShouldBe(ImageFormat.Ico);
    }
}
