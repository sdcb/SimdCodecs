using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Heif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// HEIF's native region decode (<see cref="DecodeOptions.Region"/>): the output must be exactly the same rectangle cut
/// out of the whole decode, through any chain of clap / irot / imir (the region is in display coordinates), in 8- and
/// 16-bit formats, for single images and grids — where tiles outside the region are not decoded at all.
/// </summary>
public sealed class HeifRegionTests
{
    public static IEnumerable<object[]> Fixtures() =>
    [
        ["synth-screen-testsrc2-1280x720-main8.heic"],
        ["hevc-main-3072x4096-grid.heic"],
        ["hevc-main10-3072x4096-grid.heic"],
    ];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Region_is_the_crop_of_the_whole_decode(string fixture)
    {
        var data = HeifTests.ReadFixture(fixture);
        var rng = new Random(fixture.Length * 104729);
        var ct = TestContext.Current.CancellationToken;
        var grid = fixture.Contains("grid");
        var iterations = grid ? 2 : 4;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var container = HeifContainer.Parse(data);
            container.Transforms.Clear();
            int w = container.CanvasWidth, h = container.CanvasHeight;
            if (iteration > 0)
            {
                var turns = rng.Next(0, 4);
                if (turns != 0)
                {
                    container.Transforms.Add(new HeifContainer.Transform(HeifContainer.PropIrot, turns, 0, null));
                    if ((turns & 1) != 0) (w, h) = (h, w);
                }
                if (rng.Next(2) == 0)
                    container.Transforms.Add(new HeifContainer.Transform(HeifContainer.PropImir, rng.Next(2), 0, null));
            }

            foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Bgr24, PixelFormat.Gray8, PixelFormat.Rgba64 })
            {
                var bpp = format.BytesPerPixel;
                var whole = new byte[w * h * bpp];
                HeifDecoder.DecodeInto(data, container, whole, w * bpp, format, ct);

                // A region spanning tile edges (grid tiles are 512 wide here), and one inside a single tile.
                foreach (var region in new[]
                {
                    new PixelRect(rng.Next(0, w / 2), rng.Next(0, h / 2), rng.Next(1, w / 2), rng.Next(1, h / 2)),
                    new PixelRect(rng.Next(0, w - 40), rng.Next(0, h - 30), 37, 29),
                })
                {
                    var stride = region.Width * bpp + 5;
                    var part = new byte[stride * region.Height];
                    HeifDecoder.DecodeInto(data, container, part, stride, format, ct, region);
                    for (var y = 0; y < region.Height; y++)
                    {
                        var expected = whole.AsSpan(((region.Y + y) * w + region.X) * bpp, region.Width * bpp);
                        if (!part.AsSpan(y * stride, region.Width * bpp).SequenceEqual(expected))
                            Assert.Fail($"{fixture} iteration {iteration} {format} region {region}: row {y} differs.");
                    }
                }
            }
        }
    }

    [Fact]
    public void Facade_region_equals_the_crop_and_rejects_one_outside()
    {
        var data = HeifTests.ReadFixture("synth-screen-testsrc2-1280x720-main8.heic");
        var ct = TestContext.Current.CancellationToken;
        HeifImageDecoder.TryDecode(data, out var whole, cancellationToken: ct).ShouldBeTrue();
        using (whole)
        {
            var region = new PixelRect(101, 57, 300, 200);
            HeifImageDecoder.TryDecode(data, out var part, new DecodeOptions { Region = region }, ct).ShouldBeTrue();
            using (part)
            {
                (part.Width, part.Height).ShouldBe((300, 200));
                using var crop = RasterImage.Crop(whole, region);
                part.Pixels.ToArray().ShouldBe(crop.Pixels.ToArray());
            }

            var into = new byte[300 * 200 * 4];
            HeifImageDecoder.TryDecodeInto(data, into, 0, PixelFormat.Rgba32, new DecodeOptions { Region = region }, ct).ShouldBeTrue();
            var expected = new byte[300 * 200 * 4];
            using (var crop = RasterImage.Crop(whole, region))
                crop.TryCopyTo(expected, 0, PixelFormat.Rgba32).ShouldBeTrue();
            into.ShouldBe(expected);

            Should.Throw<ArgumentException>(() =>
                HeifImageDecoder.TryDecode(data, out _, new DecodeOptions { Region = new PixelRect(1200, 0, 100, 10) }, ct));
        }
    }
}
