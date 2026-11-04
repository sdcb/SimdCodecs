using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Heif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The transformative properties (clap, irot, imir) written straight into the output by <c>HeifCanvas</c>, against
/// the definition: decode the canvas, then apply each transform to the whole image in association order
/// (23008-12 §6.5.1), then convert to the requested layout. Random chains of up to four transforms — every
/// rotation, both mirrors, crops with odd offsets — on a grid fixture (tiles cross the crop edges) and a single
/// item, in every 8-bit layout, tight and padded strides.
/// </summary>
public sealed class HeifTransformTests
{
    public static TheoryData<string> Fixtures() => new() { "hevc-main-3072x4096-grid.heic", "synth-screen-testsrc2-1280x720-main8.heic" };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Transform_chains_match_whole_image_transforms(string fixture)
    {
        var data = HeifTests.ReadFixture(fixture);
        var plain = HeifContainer.Parse(data);
        plain.Transforms.Clear();
        int canvasW = plain.CanvasWidth, canvasH = plain.CanvasHeight;
        var canvas = new byte[canvasW * canvasH * 4];
        HeifDecoder.DecodeInto(data, plain, canvas, canvasW * 4, PixelFormat.Rgba32);

        var rng = new Random(fixture.Length * 7919);
        var formats = PixelConverterTests.Formats;
        for (var iteration = 0; iteration < 24; iteration++)
        {
            var container = HeifContainer.Parse(data);
            container.Transforms.Clear();
            var chain = rng.Next(1, 5);
            int w = canvasW, h = canvasH;
            for (var t = 0; t < chain; t++)
            {
                switch (rng.Next(3))
                {
                    case 0:
                        // A crop to roughly 40 .. 100 % of the current size, off centre by a few samples (odd edges).
                        var cw = Math.Max(1, (int)(w * (0.4 + 0.6 * rng.NextDouble())));
                        var ch = Math.Max(1, (int)(h * (0.4 + 0.6 * rng.NextDouble())));
                        var dx = rng.Next(-(w - cw) / 2, (w - cw) / 2 + 1);
                        var dy = rng.Next(-(h - ch) / 2, (h - ch) / 2 + 1);
                        container.Transforms.Add(new HeifContainer.Transform(HeifContainer.PropClap, 0, 0, [cw, 1, ch, 1, dx * 2 + 1, 2, dy * 2 - 1, 2]));
                        var r = HeifContainer.ClapRectangle(container.Transforms[^1].Clap!, w, h);
                        (w, h) = (r.Width, r.Height);
                        break;
                    case 1:
                        var turns = rng.Next(1, 4);
                        container.Transforms.Add(new HeifContainer.Transform(HeifContainer.PropIrot, turns, 0, null));
                        if ((turns & 1) != 0) (w, h) = (h, w);
                        break;
                    default:
                        container.Transforms.Add(new HeifContainer.Transform(HeifContainer.PropImir, rng.Next(2), 0, null));
                        break;
                }
            }

            var (expectedRgba, ew, eh) = ApplyTransforms(container.Transforms, canvas, canvasW, canvasH);
            container.DisplaySize().ShouldBe((ew, eh));
            var format = formats[rng.Next(formats.Length)];
            var bpp = format.BytesPerPixel;
            var stride = ew * bpp + rng.Next(2) * 5;
            var expected = new byte[(eh - 1) * stride + ew * bpp];
            var actual = new byte[expected.Length];
            Array.Fill(expected, (byte)0x5A);
            Array.Fill(actual, (byte)0x5A);
            for (var y = 0; y < eh; y++)
            {
                if (format == PixelFormat.Gray8 && HeifDecoder.MatrixCoefficients(container) != 0)
                {
                    // Gray8 is the stored luma, not the luma of the RGB: decode it the same way and transform it.
                    continue;
                }
                PixelConverter.ConvertRow(expectedRgba.AsSpan(y * ew * 4, ew * 4), 4, SampleFormat.UInt8,
                    expected.AsSpan(y * stride, ew * bpp), format, ew);
            }
            if (format == PixelFormat.Gray8 && HeifDecoder.MatrixCoefficients(container) != 0)
            {
                var grayCanvas = new byte[canvasW * canvasH];
                HeifDecoder.DecodeInto(data, plain, grayCanvas, canvasW, PixelFormat.Gray8);
                var (expectedGray, _, _) = ApplyTransforms(container.Transforms, grayCanvas, canvasW, canvasH, bytesPerPixel: 1);
                for (var y = 0; y < eh; y++) expectedGray.AsSpan(y * ew, ew).CopyTo(expected.AsSpan(y * stride));
            }

            HeifDecoder.DecodeInto(data, container, actual, stride, format);
            var label = $"iteration {iteration}: {string.Join(" ", container.Transforms.Select(Describe))} -> {format} stride {stride}";
            if (!actual.AsSpan().SequenceEqual(expected)) actual.ShouldBe(expected, label);
        }
    }

    private static string Describe(HeifContainer.Transform t) => t.Type switch
    {
        HeifContainer.PropClap => $"clap({string.Join(",", t.Clap!)})",
        HeifContainer.PropIrot => $"irot{t.A}",
        _ => $"imir{t.A}",
    };

    /// <summary>The definition: each transform applied to the whole image in turn (the pre-M3 decoder's algorithm).</summary>
    private static (byte[] Pixels, int Width, int Height) ApplyTransforms(List<HeifContainer.Transform> transforms, byte[] source,
        int width, int height, int bytesPerPixel = 4)
    {
        var bpp = bytesPerPixel;
        var current = source;
        foreach (var t in transforms)
        {
            byte[] next;
            int nextW = width, nextH = height;
            switch (t.Type)
            {
                case HeifContainer.PropClap:
                {
                    var (x0, y0, w, h) = HeifContainer.ClapRectangle(t.Clap!, width, height);
                    next = new byte[w * h * bpp];
                    for (var y = 0; y < h; y++) current.AsSpan(((y0 + y) * width + x0) * bpp, w * bpp).CopyTo(next.AsSpan(y * w * bpp));
                    nextW = w;
                    nextH = h;
                    break;
                }
                case HeifContainer.PropIrot:
                {
                    var quarterTurns = t.A; // anti-clockwise
                    if ((quarterTurns & 1) != 0) (nextW, nextH) = (height, width);
                    next = new byte[nextW * nextH * bpp];
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var (dx, dy) = quarterTurns switch
                            {
                                1 => (y, width - 1 - x),
                                2 => (width - 1 - x, height - 1 - y),
                                _ => (height - 1 - y, x),
                            };
                            current.AsSpan((y * width + x) * bpp, bpp).CopyTo(next.AsSpan((dy * nextW + dx) * bpp));
                        }
                    }
                    break;
                }
                default:
                {
                    next = new byte[width * height * bpp];
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var (dx, dy) = t.A == 0 ? (width - 1 - x, y) : (x, height - 1 - y);
                            current.AsSpan((y * width + x) * bpp, bpp).CopyTo(next.AsSpan((dy * width + dx) * bpp));
                        }
                    }
                    break;
                }
            }
            current = next;
            width = nextW;
            height = nextH;
        }
        return (current, width, height);
    }
}
