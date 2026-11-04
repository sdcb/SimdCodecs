using System;
using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;
using Sdcb.SimdCodecs.Pnm;
using Sdcb.SimdCodecs.Qoi;
using Shouldly;
using Xunit;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Unit tests for <see cref="QoiImageEncoder"/>, <see cref="PnmImageEncoder"/>, and <see cref="BmpImageEncoder"/> adapters.
/// Verifies byte-for-byte equivalence against each codec's existing encoder for every format in <see cref="PixelFormat.All"/>,
/// and validates session frame count limits and missing-rows commit constraints.
/// </summary>
public sealed class EncoderAdapterQoiPnmBmpTests
{
    private const int Width = 37;
    private const int Height = 11;

    public static TheoryData<PixelFormat> AllFormats
    {
        get
        {
            var data = new TheoryData<PixelFormat>();
            foreach (var format in PixelFormat.All)
                data.Add(format);
            return data;
        }
    }

    private static byte[] MakeRandomPaddedImage(int width, int height, PixelFormat format, out int stride)
    {
        var bpp = format.BytesPerPixel;
        var rowBytes = width * bpp;
        stride = rowBytes + 7;
        var pixels = new byte[stride * height];
        var rng = new Random(width * 1000 + height * 10 + (int)format.Layout * 7 + (int)format.Sample);
        rng.NextBytes(pixels);
        return pixels;
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void Qoi_AllFormats_MatchesExistingEncoder(PixelFormat format)
    {
        var pixels = MakeRandomPaddedImage(Width, Height, format, out var stride);
        var ct = TestContext.Current.CancellationToken;

        var writer = new ArrayBufferWriter<byte>();
        using var session = QoiImageEncoder.Create(writer, null);
        var frame = session.AddFrame(Width, Height, format);

        const int chunk1 = 5;
        const int chunk2 = Height - chunk1;
        frame.WriteRows(pixels.AsSpan(0, chunk1 * stride), chunk1, stride, cancellationToken: ct);
        frame.WriteRows(pixels.AsSpan(chunk1 * stride), chunk2, stride, cancellationToken: ct);
        session.Commit(ct);

        var target = QoiImageEncoder.GetClosestInputFormat(format);
        var converted = PixelConverter.ToPacked(pixels, stride, format, target, Width, Height);
        var expected = QoiEncoder.Encode(converted, Width, Height, target);

        writer.WrittenSpan.ToArray().ShouldBe(expected);
    }

    [Fact]
    public void Qoi_SecondAddFrame_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        using var session = QoiImageEncoder.Create(writer, null);
        session.AddFrame(Width, Height, PixelFormat.Rgba32);
        Should.Throw<InvalidOperationException>(() => session.AddFrame(Width, Height, PixelFormat.Rgba32));
    }

    [Fact]
    public void Qoi_CommitMissingRows_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var writer = new ArrayBufferWriter<byte>();
        using var session = QoiImageEncoder.Create(writer, null);
        var frame = session.AddFrame(Width, Height, PixelFormat.Rgba32);
        frame.WriteRows(new byte[Width * 4 * 5], 5, 0, cancellationToken: ct);
        Should.Throw<InvalidOperationException>(() => session.Commit(ct));
    }

    [Fact]
    public void Qoi_LinearColorSpace_WritesLinearByte()
    {
        var ct = TestContext.Current.CancellationToken;
        var writer = new ArrayBufferWriter<byte>();
        using var session = QoiImageEncoder.Create(writer, null);
        var frame = session.AddFrame(1, 1, PixelFormat.Rgba32, color: new ColorEncoding { Transfer = TransferFunction.Linear });
        frame.WriteRows(new byte[4], 1, 0, cancellationToken: ct);
        session.Commit(ct);

        var bytes = writer.WrittenSpan.ToArray();
        bytes[13].ShouldBe((byte)1); // QoiColorSpace.Linear
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void Pnm_AllFormats_MatchesExistingEncoder(PixelFormat format)
    {
        var pixels = MakeRandomPaddedImage(Width, Height, format, out var stride);
        var ct = TestContext.Current.CancellationToken;
        var options = new PnmEncodeOptions();

        var writer = new ArrayBufferWriter<byte>();
        using var session = PnmImageEncoder.Create(writer, options);
        var frame = session.AddFrame(Width, Height, format);

        const int chunk1 = 5;
        const int chunk2 = Height - chunk1;
        frame.WriteRows(pixels.AsSpan(0, chunk1 * stride), chunk1, stride, cancellationToken: ct);
        frame.WriteRows(pixels.AsSpan(chunk1 * stride), chunk2, stride, cancellationToken: ct);
        session.Commit(ct);

        var target = PnmImageEncoder.GetClosestInputFormat(format);
        var converted = PixelConverter.ToPacked(pixels, stride, format, target, Width, Height);
        var expected = PnmEncoder.Encode(converted, Width, Height, target, options);

        writer.WrittenSpan.ToArray().ShouldBe(expected);
    }

    [Fact]
    public void Pnm_SecondAddFrame_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        using var session = PnmImageEncoder.Create(writer, null);
        session.AddFrame(Width, Height, PixelFormat.Rgba32);
        Should.Throw<InvalidOperationException>(() => session.AddFrame(Width, Height, PixelFormat.Rgba32));
    }

    [Fact]
    public void Pnm_CommitMissingRows_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var writer = new ArrayBufferWriter<byte>();
        using var session = PnmImageEncoder.Create(writer, null);
        var frame = session.AddFrame(Width, Height, PixelFormat.Rgba32);
        frame.WriteRows(new byte[Width * 4 * 5], 5, 0, cancellationToken: ct);
        Should.Throw<InvalidOperationException>(() => session.Commit(ct));
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void Bmp_AllFormats_MatchesExistingEncoder(PixelFormat format)
    {
        var pixels = MakeRandomPaddedImage(Width, Height, format, out var stride);
        var ct = TestContext.Current.CancellationToken;
        var options = new BmpEncodeOptions();

        var writer = new ArrayBufferWriter<byte>();
        using var session = BmpImageEncoder.Create(writer, options);
        var frame = session.AddFrame(Width, Height, format);

        const int chunk1 = 5;
        const int chunk2 = Height - chunk1;
        frame.WriteRows(pixels.AsSpan(0, chunk1 * stride), chunk1, stride, cancellationToken: ct);
        frame.WriteRows(pixels.AsSpan(chunk1 * stride), chunk2, stride, cancellationToken: ct);
        session.Commit(ct);

        var target = BmpImageEncoder.GetClosestInputFormat(format);
        var converted = PixelConverter.ToPacked(pixels, stride, format, target, Width, Height);
        var expected = BmpEncoder.Encode(converted, Width, Height, target, options);

        writer.WrittenSpan.ToArray().ShouldBe(expected);
    }

    [Fact]
    public void Bmp_SecondAddFrame_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        using var session = BmpImageEncoder.Create(writer, null);
        session.AddFrame(Width, Height, PixelFormat.Rgba32);
        Should.Throw<InvalidOperationException>(() => session.AddFrame(Width, Height, PixelFormat.Rgba32));
    }

    [Fact]
    public void Bmp_CommitMissingRows_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var writer = new ArrayBufferWriter<byte>();
        using var session = BmpImageEncoder.Create(writer, null);
        var frame = session.AddFrame(Width, Height, PixelFormat.Rgba32);
        frame.WriteRows(new byte[Width * 4 * 5], 5, 0, cancellationToken: ct);
        Should.Throw<InvalidOperationException>(() => session.Commit(ct));
    }

    [Fact]
    public void Bmp_FrameMetadataIcc_PropagatesToOptions()
    {
        var ct = TestContext.Current.CancellationToken;
        byte[] icc = [1, 2, 3, 4, 5, 6, 7, 8];
        var writer = new ArrayBufferWriter<byte>();
        using var session = BmpImageEncoder.Create(writer, null);
        var frame = session.AddFrame(1, 1, PixelFormat.Rgb24, metadata: new ImageMetadata { Icc = icc });
        frame.WriteRows(new byte[3], 1, 0, cancellationToken: ct);
        session.Commit(ct);

        var bytes = writer.WrittenSpan.ToArray();
        var expected = BmpEncoder.Encode(new byte[3], 1, 1, PixelFormat.Rgb24, new BmpEncodeOptions { IccProfile = icc });
        bytes.ShouldBe(expected);
    }

    [Fact]
    public void Registrations_HaveExpectedFormats()
    {
        QoiImageEncoder.Registration.Format.ShouldBe(ImageFormat.Qoi);
        PnmImageEncoder.Registration.Format.ShouldBe(ImageFormat.Pnm);
        BmpImageEncoder.Registration.Format.ShouldBe(ImageFormat.Bmp);
    }
}
