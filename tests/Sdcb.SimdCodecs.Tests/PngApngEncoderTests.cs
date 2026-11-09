using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The APNG encoder: what it writes decodes, with our decoder and with ffmpeg's, to exactly the frames it was given; unchanged frames
/// merge; the file is laid out as the specification requires; the facade's session takes several frames; stills are untouched.
/// </summary>
public sealed class PngApngEncoderTests
{
    private const int W = 40, H = 24;

    private static byte[] Wide(byte[] rgba8)
    {
        // 16-bit samples that are not 8-bit values (v·257 less one on every fifth sample), transparent pixels exactly zero.
        var wide = new ushort[rgba8.Length];
        for (var i = 0; i < rgba8.Length; i++)
            wide[i] = rgba8[i] == 0 ? (ushort)0 : (ushort)(rgba8[i] * 257 - (i % 5 == 0 ? 1 : 0));
        return MemoryMarshal.AsBytes(wide.AsSpan()).ToArray();
    }

    private static byte[] EncodeFrames(IReadOnlyList<byte[]> frames, bool wide, PngWriteOptions? options = null, TimeSpan? delay = null, int width = W, int height = H)
    {
        var encoder = new PngAnimationEncoder(width, height, options, wide);
        foreach (var f in frames)
            encoder.AddFrame(f, wide ? PixelFormat.Rgba64 : PixelFormat.Rgba32, delay ?? TimeSpan.FromMilliseconds(100));
        return encoder.Encode();
    }

    private static List<byte[]> Decode(byte[] file) => PngApngOracleTests.OurFrames(file, out _, out _, out _);

    /// <summary>The chunks of a PNG after the signature: type and payload.</summary>
    private static List<(string Type, byte[] Data)> Chunks(byte[] file)
    {
        var list = new List<(string, byte[])>();
        for (var pos = 8; pos < file.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos));
            list.Add((System.Text.Encoding.ASCII.GetString(file, pos + 4, 4), file.AsSpan(pos + 8, length).ToArray()));
            pos += 12 + length;
        }
        return list;
    }

    public static TheoryData<bool, bool, bool> Variants()
    {
        var data = new TheoryData<bool, bool, bool>();
        foreach (var wide in new[] { false, true })
        foreach (var reduce in new[] { false, true })
        foreach (var opaqueBackground in new[] { false, true })
            data.Add(wide, reduce, opaqueBackground);
        return data;
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void WhatTheEncoderWritesDecodesToExactlyTheFramesGiven(bool wide, bool reduce, bool opaqueBackground)
    {
        var input = PngApngOracleTests.Sequence(W, H, 9, opaqueBackground).Select(f => wide ? Wide(f) : f).ToList();
        var file = EncodeFrames(input, wide, new PngWriteOptions { LosslessReduction = reduce });
        var decoded = Decode(file);
        decoded.Count.ShouldBe(input.Count);
        for (var i = 0; i < input.Count; i++)
            decoded[i].ShouldBe(input[i], $"frame {i}");
        new PngAnimationDecoder(file).BitDepth.ShouldBe(wide ? 16 : 8);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void FfmpegDecodesWhatTheEncoderWritesToTheSameFrames(bool wide, bool reduce, bool opaqueBackground)
    {
        var ffmpeg = ApngOracle.Require();
        var input = PngApngOracleTests.Sequence(W, H, 9, opaqueBackground).Select(f => wide ? Wide(f) : f).ToList();
        var file = EncodeFrames(input, wide, new PngWriteOptions { LosslessReduction = reduce });
        var theirs = ApngOracle.DecodeFrames(ffmpeg, file, wide);
        var frameBytes = W * H * (wide ? 8 : 4);
        (theirs.Length / frameBytes).ShouldBe(input.Count);
        for (var i = 0; i < input.Count; i++)
            theirs.AsSpan(i * frameBytes, frameBytes).ToArray().ShouldBe(input[i], $"frame {i} as ffmpeg decodes it");
    }

    [Fact]
    public void FramesEqualToTheOneBeforeOnlyLengthenIt()
    {
        var f = PngApngOracleTests.Sequence(W, H, 3, true);
        var encoder = new PngAnimationEncoder(W, H);
        encoder.AddFrame(f[0], PixelFormat.Rgba32, TimeSpan.FromMilliseconds(40));
        encoder.AddFrame(f[0], PixelFormat.Rgba32, TimeSpan.FromMilliseconds(60));
        encoder.AddFrame(f[1], PixelFormat.Rgba32, TimeSpan.FromMilliseconds(70));
        encoder.AddFrame(f[1], PixelFormat.Rgba32, TimeSpan.FromMilliseconds(30));
        encoder.AddFrame(f[1], PixelFormat.Rgba32, TimeSpan.FromMilliseconds(30));
        encoder.AddFrame(f[2], PixelFormat.Rgba32, TimeSpan.FromMilliseconds(10));
        encoder.FrameCount.ShouldBe(3);
        var decoder = new PngAnimationDecoder(encoder.Encode());
        decoder.FrameCount.ShouldBe(3);
        decoder.Frames.Select(x => x.Delay.TotalMilliseconds).ShouldBe([100, 130, 10]);
    }

    [Fact]
    public void ALongStillIsSplitRatherThanLostOverTheDelayField()
    {
        // 65535 ms is the most a delay holds at millisecond resolution; a longer run of equal frames becomes more than one frame.
        var f = PngApngOracleTests.Sequence(W, H, 1, true)[0];
        var encoder = new PngAnimationEncoder(W, H);
        for (var i = 0; i < 3; i++)
            encoder.AddFrame(f, PixelFormat.Rgba32, TimeSpan.FromSeconds(30));
        var decoder = new PngAnimationDecoder(encoder.Encode());
        decoder.Frames.Sum(x => x.Delay.TotalMilliseconds).ShouldBe(90000);
        decoder.Frames.ShouldAllBe(x => x.Delay <= TimeSpan.FromMilliseconds(65535));
        var again = Decode(encoder.Encode());
        again.ShouldAllBe(x => x.SequenceEqual(f));
    }

    [Fact]
    public void ADelayTooLongForMillisecondsFallsBackToCoarserFractions()
    {
        var f = PngApngOracleTests.Sequence(W, H, 1, true)[0];
        foreach (var (seconds, expectedMs) in new[] { (100.0, 100000.0), (700.0, 700000.0), (65535.0, 65535000.0) })
        {
            var encoder = new PngAnimationEncoder(W, H);
            encoder.AddFrame(f, PixelFormat.Rgba32, TimeSpan.FromSeconds(seconds));
            new PngAnimationDecoder(encoder.Encode()).Frames[0].Delay.TotalMilliseconds.ShouldBe(expectedMs);
        }
    }

    [Fact]
    public void TheFileIsLaidOutAsTheSpecificationRequires()
    {
        var input = PngApngOracleTests.Sequence(W, H, 8, false);
        var file = EncodeFrames(input, false, new PngWriteOptions { LoopCount = 5 });
        var chunks = Chunks(file);
        var types = chunks.Select(c => c.Type).ToList();

        types[0].ShouldBe("IHDR");
        types[^1].ShouldBe("IEND");
        types.Count(t => t == "acTL").ShouldBe(1);
        types.IndexOf("acTL").ShouldBeLessThan(types.IndexOf("IDAT"));
        types.Count(t => t == "IDAT").ShouldBe(1);
        types.IndexOf("fcTL").ShouldBeLessThan(types.IndexOf("IDAT"));

        var actl = chunks.Single(c => c.Type == "acTL").Data;
        BinaryPrimitives.ReadUInt32BigEndian(actl).ShouldBe((uint)types.Count(t => t == "fcTL"));
        BinaryPrimitives.ReadUInt32BigEndian(actl.AsSpan(4)).ShouldBe(5u);

        // One sequence over fcTL and fdAT, from 0, with no gaps; every frame inside the canvas; every frame after the first has fdAT data.
        uint expected = 0;
        var frameCount = 0;
        var dataChunksOfFrame = 0;
        foreach (var (type, data) in chunks)
        {
            if (type == "fcTL")
            {
                if (frameCount > 1) dataChunksOfFrame.ShouldBeGreaterThan(0);
                BinaryPrimitives.ReadUInt32BigEndian(data).ShouldBe(expected++);
                var (fw, fh, fx, fy) = (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)), BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8)),
                    BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12)), BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16)));
                (fw + fx).ShouldBeLessThanOrEqualTo((uint)W);
                (fh + fy).ShouldBeLessThanOrEqualTo((uint)H);
                if (frameCount == 0)
                    (fw, fh, fx, fy).ShouldBe(((uint)W, (uint)H, 0u, 0u)); // the default image is the whole canvas
                frameCount++;
                dataChunksOfFrame = 0;
            }
            else if (type == "fdAT")
            {
                BinaryPrimitives.ReadUInt32BigEndian(data).ShouldBe(expected++);
                dataChunksOfFrame++;
            }
        }
        frameCount.ShouldBe(8);
    }

    [Fact]
    public void ADecoderThatKnowsNoAnimationShowsTheFirstFrame()
    {
        var input = PngApngOracleTests.Sequence(W, H, 5, true);
        var file = EncodeFrames(input, false);
        PngReader.Decode(file, TestContext.Current.CancellationToken).ToRgba8().ShouldBe(input[0]);
        ImageCodecs.TryDecode(file, out var still, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        still.ToRgba8().ShouldBe(input[0]);
    }

    [Fact]
    public void ARectangleIsSmallerThanTheCanvasWhenLittleChanged()
    {
        // A noisy canvas that a small sprite crosses: whole frames cost a noisy PNG each, rectangles almost nothing.
        var canvas = Noise(9, W * H);
        var input = new List<byte[]>();
        for (var f = 0; f < 6; f++)
        {
            var frame = (byte[])canvas.Clone();
            for (var y = 4; y < 8; y++)
            for (var x = 3 * f; x < 3 * f + 4; x++)
                (frame[(y * W + x) * 4], frame[(y * W + x) * 4 + 1], frame[(y * W + x) * 4 + 2]) = (255, 0, (byte)(f * 40));
            input.Add(frame);
        }
        var file = EncodeFrames(input, false);
        file.Length.ShouldBeLessThan(PngWriter.Encode(input[0], W, H).Length * 3 / 2);
        Decode(file).ShouldBe(input);
        var rects = new PngAnimationDecoder(file).Frames.Skip(1).ToList();
        rects.ShouldAllBe(r => r.Width * r.Height < W * H);
    }

    // ---------------------------------------------------------------- the choices

    private static byte[] Noise(int seed, int pixels, bool opaque = true)
    {
        var rng = new Random(seed);
        var data = new byte[pixels * 4];
        rng.NextBytes(data);
        if (opaque)
        {
            for (var i = 3; i < data.Length; i += 4)
                data[i] = 255;
        }
        return data;
    }

    private static (PngDisposeOp Dispose, PngBlendOp Blend) Ops(byte[] file, int frame)
    {
        var info = new PngAnimationDecoder(file).Frames[frame];
        return (info.Dispose, info.Blend);
    }

    [Fact]
    public void ScatteredChangesOverANoisyCanvasAreBlended()
    {
        // Two opaque pixels far apart change on a noisy opaque canvas: drawing their bounding rectangle would carry noise, so the
        // rectangle is blended with everything but the two pixels transparent.
        var a = Noise(1, W * H);
        var b = (byte[])a.Clone();
        foreach (var (x, y) in new[] { (2, 1), (W - 3, H - 2) })
            (b[(y * W + x) * 4], b[(y * W + x) * 4 + 1]) = ((byte)(a[(y * W + x) * 4] ^ 0x55), (byte)(a[(y * W + x) * 4 + 1] ^ 0x33));
        var file = EncodeFrames([a, b], false);
        Ops(file, 1).Blend.ShouldBe(PngBlendOp.Over);
        Decode(file)[1].ShouldBe(b);
    }

    [Fact]
    public void ARegionThatReturnsToWhatWasUnderItIsRestoredNotRedrawn()
    {
        // A noisy opaque canvas, a noisy rectangle laid over it, then the canvas again with one pixel changed: Previous restores
        // what was under the rectangle, where leaving it would redraw the noise of the canvas there.
        var a = Noise(2, W * H);
        var popup = (byte[])a.Clone();
        var noise = Noise(3, 16 * 10);
        for (var y = 0; y < 10; y++)
            Array.Copy(noise, y * 16 * 4, popup, ((y + 6) * W + 10) * 4, 16 * 4);
        var c = (byte[])a.Clone();
        c[(2 * W + 3) * 4] ^= 0x80;
        var file = EncodeFrames([a, popup, c], false);
        Ops(file, 1).Dispose.ShouldBe(PngDisposeOp.Previous);
        var decoded = Decode(file);
        decoded[0].ShouldBe(a);
        decoded[1].ShouldBe(popup);
        decoded[2].ShouldBe(c);
    }

    [Fact]
    public void ARegionThatBecomesTransparentIsClearedNotRedrawn()
    {
        // A noisy sprite appears on a transparent canvas and goes again while a pixel elsewhere changes: Background clears it.
        var empty = new byte[W * H * 4];
        var withSprite = (byte[])empty.Clone();
        var noise = Noise(4, 16 * 10);
        for (var y = 0; y < 10; y++)
            Array.Copy(noise, y * 16 * 4, withSprite, ((y + 6) * W + 10) * 4, 16 * 4);
        var next = (byte[])empty.Clone();
        (next[0], next[3]) = (200, 255);
        var file = EncodeFrames([empty, withSprite, next], false);
        Ops(file, 1).Dispose.ShouldBe(PngDisposeOp.Background);
        Decode(file)[2].ShouldBe(next);
    }

    [Fact]
    public void RandomEditsOfACanvasRoundTripWhateverTheEncoderPicks()
    {
        // Random edits of a small canvas, frame after frame: whatever the encoder picks must round-trip.
        var rng = new Random(77);
        var canvas = new byte[W * H * 4];
        var frames = new List<byte[]>();
        for (var f = 0; f < 120; f++)
        {
            switch (rng.Next(5))
            {
                case 0: // an opaque noisy rectangle
                {
                    int w = rng.Next(1, 14), h = rng.Next(1, 10), x0 = rng.Next(W - w), y0 = rng.Next(H - h);
                    for (var y = y0; y < y0 + h; y++)
                    for (var x = x0; x < x0 + w; x++)
                        (canvas[(y * W + x) * 4], canvas[(y * W + x) * 4 + 1], canvas[(y * W + x) * 4 + 2], canvas[(y * W + x) * 4 + 3]) =
                            ((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 255);
                    break;
                }
                case 1: // a rectangle made transparent
                {
                    int w = rng.Next(1, 14), h = rng.Next(1, 10), x0 = rng.Next(W - w), y0 = rng.Next(H - h);
                    for (var y = y0; y < y0 + h; y++)
                        Array.Clear(canvas, (y * W + x0) * 4, w * 4);
                    break;
                }
                case 2: // a few scattered pixels, some translucent
                    for (var k = 0; k < 4; k++)
                    {
                        var p = rng.Next(W * H) * 4;
                        (canvas[p], canvas[p + 1], canvas[p + 2], canvas[p + 3]) = ((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)(rng.Next(3) == 0 ? 120 : 255));
                    }
                    break;
                case 3: // back to an earlier frame
                    if (frames.Count > 0) frames[rng.Next(frames.Count)].CopyTo(canvas, 0);
                    break;
                default: // nothing
                    break;
            }
            frames.Add((byte[])canvas.Clone());
        }

        var file = EncodeFrames(frames, false);
        var decoder = new PngAnimationDecoder(file);
        var merged = frames.Where((f, i) => i == 0 || !f.SequenceEqual(frames[i - 1])).Count();
        decoder.FrameCount.ShouldBeLessThanOrEqualTo(frames.Count);
        decoder.FrameCount.ShouldBeGreaterThanOrEqualTo(merged);
        // Each decoded frame is the canvas of the input frame it stands for.
        var shown = Decode(file);
        var at = 0;
        var elapsed = TimeSpan.Zero;
        for (var i = 0; i < frames.Count; i++)
        {
            while (elapsed + decoder.Frames[at].Delay <= TimeSpan.FromMilliseconds(100) * i && at + 1 < decoder.FrameCount)
            {
                elapsed += decoder.Frames[at].Delay;
                at++;
            }
            shown[at].ShouldBe(frames[i], $"input frame {i} (shown by output frame {at})");
        }
    }

    // ---------------------------------------------------------------- the facade

    private static byte[] Session(IReadOnlyList<(byte[] Pixels, PixelFormat Format, TimeSpan? Duration)> frames, EncodeOptions? options = null,
        int width = W, int height = H)
    {
        var writer = new ArrayBufferWriter<byte>();
        using (var session = ImageCodecs.CreateEncoder(ImageFormat.Png, writer, options))
        {
            foreach (var (pixels, format, duration) in frames)
            {
                var frame = session.AddFrame(width, height, format, duration: duration);
                frame.WriteRows(pixels, height, 0, TestContext.Current.CancellationToken);
            }
            session.Commit(TestContext.Current.CancellationToken);
        }
        return writer.WrittenSpan.ToArray();
    }

    [Fact]
    public void ASessionOfSeveralFramesWritesAnApngWithTheirDurations()
    {
        var input = PngApngOracleTests.Sequence(W, H, 4, false);
        var file = Session([.. input.Select((f, i) => (f, PixelFormat.Rgba32, (TimeSpan?)TimeSpan.FromMilliseconds(30 + 10 * i)))],
            new PngWriteOptions { LoopCount = 2 });
        ImageCodecs.DetectFormat(file).ShouldBe(ImageFormat.Png);
        ImageCodecs.TryOpenFrames(file, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.FrameCount.ShouldBe(4);
            reader.LoopCount.ShouldBe(2);
            for (var i = 0; i < 4; i++)
            {
                reader.TryGetFrameInfo(i, out var info).ShouldBeTrue();
                info.Duration.ShouldBe(TimeSpan.FromMilliseconds(30 + 10 * i));
                var pixels = new byte[W * H * 4];
                reader.TryDecodeFrameInto(i, pixels, 0, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
                pixels.ShouldBe(input[i], $"frame {i}");
            }
            var buffer = new byte[W * H * 4];
            var seen = reader.EnumerateFramesInto(buffer, 0, PixelFormat.Rgba32).Select(t => t.Index).ToList();
            seen.ShouldBe([0, 1, 2, 3]);
        }
    }

    [Fact]
    public void ASessionTakesFramesInDifferentFormatsAndOneSixteenBitFrameMakesTheAnimationSixteenBit()
    {
        var a = PngApngOracleTests.Sequence(W, H, 2, true);
        var rgb = new byte[W * H * 3];
        for (var i = 0; i < W * H; i++)
            Array.Copy(a[0], i * 4, rgb, i * 3, 3);
        var opaque0 = (byte[])a[0].Clone();
        for (var i = 0; i < W * H; i++) opaque0[i * 4 + 3] = 255;
        var wideFrame = Wide(a[1]);
        var file = Session([(rgb, PixelFormat.Rgb24, null), (wideFrame, PixelFormat.Rgba64, null)]);
        var decoder = new PngAnimationDecoder(file);
        decoder.BitDepth.ShouldBe(16);
        decoder.FrameCount.ShouldBe(2);
        var frames = Decode(file);
        // The 8-bit frame, widened as v·257; the 16-bit frame as given.
        MemoryMarshal.Cast<byte, ushort>(frames[0]).ToArray().ShouldBe(opaque0.Select(v => (ushort)(v * 257)).ToArray());
        frames[1].ShouldBe(wideFrame);
    }

    [Fact]
    public void ASessionOfOneFrameIsTheStillPngByteForByte()
    {
        var pixels = PngApngOracleTests.Sequence(W, H, 1, false)[0];
        Session([(pixels, PixelFormat.Rgba32, TimeSpan.FromMilliseconds(40))]).ShouldBe(PngWriter.Encode(pixels, W, H));
        var options = new PngWriteOptions { LosslessReduction = true, CompressionLevel = System.IO.Compression.CompressionLevel.SmallestSize };
        Session([(pixels, PixelFormat.Rgba32, null)], options).ShouldBe(PngWriter.Encode(pixels, W, H, options));
        var file = Session([(pixels, PixelFormat.Rgba32, null)]);
        file.AsSpan().IndexOf("acTL"u8).ShouldBe(-1);
    }

    [Fact]
    public void ASessionWhoseFramesDifferInSizeIsRefused()
    {
        var writer = new ArrayBufferWriter<byte>();
        using var session = ImageCodecs.CreateEncoder(ImageFormat.Png, writer);
        session.AddFrame(4, 4, PixelFormat.Rgba32).WriteRows(new byte[64], 4, 0, TestContext.Current.CancellationToken);
        session.AddFrame(5, 4, PixelFormat.Rgba32).WriteRows(new byte[80], 4, 0, TestContext.Current.CancellationToken);
        Should.Throw<ArgumentException>(() => session.Commit(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheEncoderRefusesWhatItCannotDo()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new PngAnimationEncoder(0, 5));
        Should.Throw<ArgumentException>(() => new PngAnimationEncoder(5, 5, new PngWriteOptions { DiscardAlpha = true }));
        Should.Throw<ArgumentException>(() => new PngAnimationEncoder(5, 5, new PngWriteOptions { TransparentColor = [0, 0, 0] }));
        Should.Throw<ArgumentOutOfRangeException>(() => new PngAnimationEncoder(5, 5, new PngWriteOptions { LoopCount = -1 }));
        var encoder = new PngAnimationEncoder(2, 2);
        Should.Throw<InvalidOperationException>(() => encoder.Encode());
        Should.Throw<ArgumentOutOfRangeException>(() => encoder.AddFrame(new byte[16], PixelFormat.Rgba32, TimeSpan.FromSeconds(-1)));
        Should.Throw<ArgumentException>(() => encoder.AddFrame(new byte[15], PixelFormat.Rgba32, TimeSpan.Zero));
        encoder.AddFrame(new byte[16], PixelFormat.Rgba32, TimeSpan.Zero);
        encoder.Encode();
        Should.Throw<InvalidOperationException>(() => encoder.AddFrame(new byte[16], PixelFormat.Rgba32, TimeSpan.Zero));
    }

    [Fact]
    public void ParallelFragmentsAndTextMetadataCarryOver()
    {
        var input = PngApngOracleTests.Sequence(W, H, 3, true);
        var options = new PngWriteOptions
        {
            ParallelFragments = 4, PhysicalDimensions = PngPhysicalDimensions.FromDpi(144), Text = [new PngText("Title", "an animation")],
            Cicp = CicpChunk.Srgb, Exif = [0x49, 0x49, 0x2A, 0, 8, 0, 0, 0, 0, 0],
        };
        var file = EncodeFrames(input, false, options);
        Decode(file).ShouldBe(input);
        var header = PngReader.Decode(file, TestContext.Current.CancellationToken);
        header.PhysicalDimensions.ShouldNotBeNull().DpiX!.Value.ShouldBe(144, 0.5);
        header.Text.Single().Text.ShouldBe("an animation");
        header.Cicp.ShouldNotBeNull();
    }

    [Fact]
    public void ReductionFindsOneLayoutForTheWholeAnimation()
    {
        // Greyscale frames with binary alpha, then a coloured one: the whole animation needs colour, and a palette serves all of them.
        var frames = new List<byte[]>();
        for (var f = 0; f < 4; f++)
        {
            var px = new byte[W * H * 4];
            for (var i = 0; i < W * H; i++)
            {
                if ((i + f) % 3 == 0) continue; // transparent
                var v = (byte)(((i + f) % 4) * 85);
                (px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]) = (v, v, v, 255);
            }
            frames.Add(px);
        }
        var plain = new PngAnimationDecoder(EncodeFrames(frames, false, new PngWriteOptions { LosslessReduction = true }));
        plain.Header.ColorType.ShouldBe(3); // five colours, transparent included: indexed, at 4 bits
        plain.Header.SourceBitDepth.ShouldBe(4);
        frames.Add([.. frames[0].Select((v, i) => i % 4 == 0 && i < 400 && frames[0][i + 3] == 255 ? (byte)255 : v)]);
        var file = EncodeFrames(frames, false, new PngWriteOptions { LosslessReduction = true });
        var coloured = new PngAnimationDecoder(file);
        coloured.Header.ColorType.ShouldBe(3); // six colours: still indexed
        Decode(file).Select((f, i) => f.SequenceEqual(frames[i])).ShouldAllBe(x => x);
    }
}
