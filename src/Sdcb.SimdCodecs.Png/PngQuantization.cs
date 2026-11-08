using System.Buffers;
using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// <see cref="PngWriteOptions.Quantize"/>: an 8- or 16-bit image of 1–4 channels reduced to a palette with the shared
/// <see cref="PaletteQuantizer"/>, handed back as the quantized 8-bit RGBA image, which the lossless reduction then
/// writes as PLTE + tRNS. Decoding the file gives back exactly these pixels.
/// </summary>
internal static class PngQuantization
{
    // Quality → target RMS distance ×100 in premultiplied OKLab (with alpha), interpolated linearly. Set by measuring
    // pngquant 2.17 as a black box (`calibrate` in benchmarks/Sdcb.SimdCodecs.Quantize.Benchmarks): the median, over
    // that corpus, of the error its `--quality 0-q --nofs` reaches. Quality 100 is a target of 0: every entry allowed.
    private static ReadOnlySpan<double> Knots => [16.0, 3.2, 2.7, 2.2, 2.0, 1.85, 1.7, 1.5, 1.3, 1.08, 0.83, 0];
    private static ReadOnlySpan<int> KnotQualities => [0, 5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];

    /// <summary>The target mean squared error (comparison-space units) a quality stands for.</summary>
    internal static double TargetError(int quality)
    {
        var q = KnotQualities;
        var i = 1;
        while (q[i] < quality) i++;
        var t = (double)(quality - q[i - 1]) / (q[i] - q[i - 1]);
        var rms = (Knots[i - 1] + t * (Knots[i] - Knots[i - 1])) / 100;
        return rms * rms;
    }

    internal static void Validate(PngQuantizeOptions o)
    {
        if (o.MaxColors is < 2 or > 256)
            throw new ArgumentOutOfRangeException(nameof(PngWriteOptions.Quantize), o.MaxColors, "MaxColors must be 2 to 256.");
        if (o.MinQuality is < 0 or > 100 || o.MaxQuality is < 0 or > 100 || o.MinQuality > o.MaxQuality)
            throw new ArgumentOutOfRangeException(nameof(PngWriteOptions.Quantize), $"{o.MinQuality}-{o.MaxQuality}",
                "MinQuality and MaxQuality must be 0 to 100, MinQuality no more than MaxQuality.");
    }

    /// <summary>
    /// The quantized image as 8-bit RGBA, or null when even <see cref="PngQuantizeOptions.MaxColors"/> colours stay
    /// below <see cref="PngQuantizeOptions.MinQuality"/>. <paramref name="dropAlpha"/> treats every pixel as opaque.
    /// </summary>
    internal static byte[]? Quantize(ReadOnlySpan<byte> samples, int width, int height, int sampleBytes, int channels,
        bool dropAlpha, PngQuantizeOptions options)
    {
        var n = width * height;
        var argb = ArrayPool<int>.Shared.Rent(n);
        try
        {
            var pixels = argb.AsSpan(0, n);
            ToArgb(samples, pixels, sampleBytes, channels, dropAlpha);

            var quantizer = new PaletteQuantizer(alpha: true);
            foreach (var p in pixels) quantizer.Add(p);
            var palette = quantizer.BuildPalette(options.MaxColors, TargetError(options.MaxQuality));
            if (options.MinQuality > 0 && quantizer.MeanSquaredError > TargetError(options.MinQuality))
                return null;
            if (quantizer.Exact)
            {
                // The colours fit as they are: write the pixels themselves (a nearest-entry search could confuse two
                // faint colours whose premultiplied coordinates nearly coincide).
                var exact = new byte[n * 4];
                for (var i = 0; i < n; i++)
                {
                    var c = (uint)pixels[i] < 0x01000000u ? 0 : pixels[i]; // every fully transparent pixel is one colour
                    (exact[4 * i], exact[4 * i + 1], exact[4 * i + 2], exact[4 * i + 3]) = ((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24));
                }
                return exact;
            }

            var indices = ArrayPool<byte>.Shared.Rent(n);
            try
            {
                new PaletteQuantizer.Mapper(palette, alpha: true).Map(pixels, width, height, indices, -1, options.Dither);
                var rgba = new byte[n * 4];
                for (var i = 0; i < n; i++)
                {
                    var c = palette[indices[i]];
                    rgba[4 * i] = (byte)(c >> 16);
                    rgba[4 * i + 1] = (byte)(c >> 8);
                    rgba[4 * i + 2] = (byte)c;
                    rgba[4 * i + 3] = (byte)(c >> 24);
                }
                return rgba;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(indices);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(argb);
        }
    }

    // Host-order samples (1 grey, 2 grey + alpha, 3 RGB, 4 RGBA; 16-bit rounded to 8) to 0xAARRGGBB.
    private static void ToArgb(ReadOnlySpan<byte> samples, Span<int> argb, int sampleBytes, int channels, bool dropAlpha)
    {
        var wide = sampleBytes == 2 ? MemoryMarshal.Cast<byte, ushort>(samples) : default;
        Span<int> s = stackalloc int[4];
        for (var i = 0; i < argb.Length; i++)
        {
            for (var c = 0; c < channels; c++)
                s[c] = sampleBytes == 1 ? samples[i * channels + c] : (wide[i * channels + c] + 128) / 257;
            int r, g, b, a = 255;
            switch (channels)
            {
                case 1: r = g = b = s[0]; break;
                case 2: r = g = b = s[0]; a = s[1]; break;
                case 3: r = s[0]; g = s[1]; b = s[2]; break;
                default: r = s[0]; g = s[1]; b = s[2]; a = s[3]; break;
            }
            if (dropAlpha) a = 255;
            argb[i] = a << 24 | r << 16 | g << 8 | b;
        }
    }
}
