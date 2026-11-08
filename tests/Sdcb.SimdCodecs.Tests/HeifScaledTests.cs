using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Heif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// HEIF's native scaled decode (<see cref="DecodeOptions.ScaleDenominator"/> &gt; 1): each tile boxes the pixels it
/// converts. The output must be exactly what the box-filter fallback gives — the box filter (an independent reference,
/// <see cref="BoxDownscaleTests.Reference"/>) of the whole Rgb24 / Rgb48 decode, cropped to the region, converted to the
/// format with <see cref="PixelConverter"/> — for every format, through clap / irot / imir chains, single images and
/// grids, Main and Main 10. Bytes between the destination's rows stay untouched.
/// </summary>
public sealed class HeifScaledTests
{
    private static readonly int[] Factors = [2, 4, 8, 16];

    public static TheoryData<string> Fixtures() => new()
    {
        "synth-screen-testsrc2-1280x720-main8.heic",
        "synth-photo-mangrove-1599x1199-main8.heic",
        "hevc-main-3072x4096-grid.heic",
        "hevc-main10-3072x4096-grid.heic",
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Native_scaled_decode_is_the_box_filter_of_the_whole_decode(string fixture)
    {
        var data = HeifTests.ReadFixture(fixture);
        var ct = TestContext.Current.CancellationToken;
        var rng = new Random(fixture.Length * 6007);
        var grid = HeifContainer.Parse(data).Tiles.Count > 1;
        var native = 0;
        var fallback = 0;
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var container = HeifContainer.Parse(data);
            container.Transforms.Clear();
            if (iteration > 0)
                AddTransforms(container, rng, aligned: iteration < 4);
            var (w, h) = container.DisplaySize();
            var depth = container.TileConfigs[0].ParseSps().BitDepthY;
            var layout = depth > 8 ? PixelFormat.Rgb48 : PixelFormat.Rgb24;
            var whole = new byte[w * h * layout.BytesPerPixel];
            HeifDecoder.DecodeInto(data, container, whole, w * layout.BytesPerPixel, layout, ct);

            foreach (var d in Factors)
            {
                var boxed = BoxDownscaleTests.Reference(whole, w, h, 3, layout.Sample, d);
                int sw = (w + d - 1) / d, sh = (h + d - 1) / d;
                PixelRect?[] regions =
                [
                    null,
                    new PixelRect(rng.Next(0, sw / 2), rng.Next(0, sh / 2), rng.Next(1, sw / 2 + 1), rng.Next(1, sh / 2 + 1)),
                    // A small one at the far corner: partial boxes at the right and bottom edges.
                    new PixelRect(sw - Math.Min(sw, 7), sh - Math.Min(sh, 5), Math.Min(sw, 7), Math.Min(sh, 5)),
                ];
                if (!HeifDecoder.ScalesNatively(container, d, null))
                {
                    // No tile is boxed natively when the seams miss the box edges; the adapter then takes the fallback.
                    iteration.ShouldBe(4, $"{fixture} d{d}: only the misaligned chain may miss the box edges");
                    grid.ShouldBeTrue($"{fixture} d{d}: a single coded image always scales natively");
                    fallback++;
                    continue;
                }
                foreach (var region in regions)
                {
                    HeifDecoder.ScalesNatively(container, d, region).ShouldBeTrue();
                    var r = region ?? new PixelRect(0, 0, sw, sh);
                    var crop = Crop(boxed, sw, layout.BytesPerPixel, r);
                    // Every format for a single image or a small region; a rotating pair for a whole grid.
                    var all = PixelFormat.All.ToArray();
                    var formats = grid && region is null ? [all[(iteration + d) % all.Length], all[(iteration + d + 7) % all.Length]] : all;
                    foreach (var format in formats)
                    {
                        var bpp = format.BytesPerPixel;
                        var stride = r.Width * bpp + 3;
                        var expected = new byte[(r.Height - 1) * stride + r.Width * bpp];
                        var actual = new byte[expected.Length];
                        Array.Fill(expected, (byte)0xA5);
                        Array.Fill(actual, (byte)0xA5);
                        PixelConverter.Convert(crop, 0, layout, expected, stride, format, r.Width, r.Height);
                        HeifDecoder.DecodeInto(data, container, actual, stride, format, ct, region, d);
                        if (!actual.AsSpan().SequenceEqual(expected))
                            Assert.Fail($"{fixture} iteration {iteration} d{d} {format} region {region}: {FirstDifference(actual, expected, stride, bpp)}");
                        native++;
                    }
                }
            }
        }
        native.ShouldBeGreaterThan(0);
        if (grid) fallback.ShouldBeGreaterThan(0, $"{fixture}: the misaligned chain should take the fallback");
    }

    /// <summary>The adapter end to end, at every format: native (no transform, aligned grid seams) equals the box filter
    /// of the full <see cref="HeifImageDecoder.TryDecode"/>, converted.</summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Adapter_scaled_decode_equals_the_converted_box_filter(string fixture)
    {
        var data = HeifTests.ReadFixture(fixture);
        var ct = TestContext.Current.CancellationToken;
        HeifImageDecoder.TryDecode(data, out var full, cancellationToken: ct).ShouldBeTrue();
        using (full)
        {
            var layout = PixelFormat.ForChannels(full.Channels, full.SampleFormat);
            foreach (var d in Factors)
            {
                HeifDecoder.ScalesNatively(HeifContainer.Parse(data), d, null).ShouldBeTrue($"{fixture} d{d}");
                var boxed = BoxDownscaleTests.Reference(full.Pixels, full.Width, full.Height, 3, full.SampleFormat, d);
                var (sw, sh) = new DecodeOptions { ScaleDenominator = d }.GetScaledSize(full.Width, full.Height);
                foreach (var region in new PixelRect?[] { null, new PixelRect(sw / 3, sh / 5, sw / 2, sh / 3) })
                {
                    var r = region ?? new PixelRect(0, 0, sw, sh);
                    var crop = Crop(boxed, sw, layout.BytesPerPixel, r);
                    var options = new DecodeOptions { ScaleDenominator = d, Region = region };
                    HeifImageDecoder.TryDecode(data, out var image, options, ct).ShouldBeTrue();
                    using (image)
                    {
                        (image.Width, image.Height, image.BitDepth, image.SampleFormat).ShouldBe((r.Width, r.Height, full.BitDepth, full.SampleFormat));
                        image.Pixels.SequenceEqual(crop).ShouldBeTrue($"{fixture} d{d} {region}");
                    }
                    foreach (var format in PixelFormat.All)
                    {
                        var expected = new byte[r.Width * r.Height * format.BytesPerPixel];
                        PixelConverter.Convert(crop, 0, layout, expected, 0, format, r.Width, r.Height);
                        var into = new byte[expected.Length];
                        HeifImageDecoder.TryDecodeInto(data, into, 0, format, options, ct).ShouldBeTrue();
                        into.AsSpan().SequenceEqual(expected).ShouldBeTrue($"{fixture} d{d} {region} {format}");
                        HeifImageDecoder.TryDecodeInto(data, into.AsSpan(0, expected.Length - 1), 0, format, options, ct)
                            .ShouldBeFalse($"{fixture} d{d} {region} {format} short destination");
                    }
                }
            }
        }
    }

    [Fact]
    public void Misaligned_grid_seams_take_the_fallback()
    {
        var data = HeifTests.ReadFixture("hevc-main-3072x4096-grid.heic");
        var container = HeifContainer.Parse(data);
        container.Transforms.Clear();
        // A crop starting one pixel in: every vertical seam lands one pixel off a box edge.
        container.Transforms.Add(new HeifContainer.Transform(HeifContainer.PropClap, 0, 0,
            [container.CanvasWidth - 2, 1, container.CanvasHeight, 1, 0, 1, 0, 1]));
        HeifDecoder.ScalesNatively(container, 2, null).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() =>
            HeifDecoder.DecodeInto(data, container, new byte[1536 * 2048 * 3], 1535 * 3, PixelFormat.Rgb24, factor: 2));
        // The grid's own 512-pixel seams line up with every factor up to 512, and with none beyond it.
        container.Transforms.Clear();
        HeifDecoder.ScalesNatively(container, 512, null).ShouldBeTrue();
        HeifDecoder.ScalesNatively(container, 1024, null).ShouldBeFalse();
    }

    // Random clap / irot / imir chains. Aligned: quarter turns and mirrors only, whose seams stay on multiples of 16 for
    // these fixtures (sizes that are multiples of 16, or a single image); misaligned: a crop one pixel in from the left.
    private static void AddTransforms(HeifContainer container, Random rng, bool aligned)
    {
        int w = container.CanvasWidth, h = container.CanvasHeight;
        if (!aligned)
        {
            var cw = w - 2 - rng.Next(0, 9);
            var ch = h - rng.Next(0, 9);
            // Centre offset (cw − w + 2) / 2 ... places the left edge at x = 1.
            container.Transforms.Add(new HeifContainer.Transform(HeifContainer.PropClap, 0, 0,
                [cw, 1, ch, 1, 2 + cw - w, 2, ch - h, 2]));
        }
        for (var t = rng.Next(1, 4); t > 0; t--)
        {
            if (rng.Next(2) == 0)
                container.Transforms.Add(new HeifContainer.Transform(HeifContainer.PropIrot, rng.Next(1, 4), 0, null));
            else
                container.Transforms.Add(new HeifContainer.Transform(HeifContainer.PropImir, rng.Next(2), 0, null));
        }
    }

    private static byte[] Crop(byte[] pixels, int width, int bpp, PixelRect r)
    {
        var result = new byte[r.Width * r.Height * bpp];
        for (var y = 0; y < r.Height; y++)
            pixels.AsSpan(((r.Y + y) * width + r.X) * bpp, r.Width * bpp).CopyTo(result.AsSpan(y * r.Width * bpp));
        return result;
    }

    private static string FirstDifference(byte[] actual, byte[] expected, int stride, int bpp)
    {
        var i = actual.AsSpan().CommonPrefixLength(expected);
        return $"first difference at row {i / stride}, pixel {i % stride / bpp} (byte {i}): {actual[i]} != {expected[i]}";
    }
}
