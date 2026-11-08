using System.Reflection;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Webp.Benchmarks;

/// <summary>
/// Region and scale decodes through the facade adapter (<see cref="WebpImageDecoder.TryDecodeInto"/>, RGBA): 1/2, 1/4,
/// 1/8 of the whole image, and a 640×270 window at the top, middle and bottom of a 1920×1080 frame at full scale. The
/// current build decodes lossy stills natively; the baseline (a build from before that) takes the generic path, a whole
/// decode, box filter and crop.
/// </summary>
internal static class ScaledCases
{
    public sealed record Case(string Name, int Scale, string? Where);

    public static readonly Case[] All =
    [
        new("1/2", 2, null), new("1/4", 4, null), new("1/8", 8, null),
        new("top", 1, "top"), new("middle", 1, "middle"), new("bottom", 1, "bottom"),
    ];

    public static Case Find(string name) => All.Single(c => c.Name == name);

    /// <summary>The region of <paramref name="c"/> in the scaled image, or null for the whole scaled image.</summary>
    public static PixelRect? Region(Case c, int width, int height)
    {
        if (c.Where is null)
            return null;
        int w = Math.Min(640, width), h = Math.Min(270, height);
        int x = (width - w) / 2;
        int y = c.Where switch { "top" => 0, "middle" => (height - h) / 2, _ => height - h };
        return new PixelRect(x, y, w, h);
    }

    public static (int Width, int Height) OutputSize(Case c, WebpInfo info) =>
        new DecodeOptions { ScaleDenominator = c.Scale, Region = Region(c, info.Width, info.Height) }.GetOutputSize(info.Width, info.Height);

    public static Action Current(byte[] source, byte[] rgba, Case c)
    {
        WebpInfo info = WebpDecoder.ReadInfo(source);
        var options = new DecodeOptions { ScaleDenominator = c.Scale, Region = Region(c, info.Width, info.Height) };
        return () =>
        {
            if (!WebpImageDecoder.TryDecodeInto(source, rgba, 0, PixelFormat.Rgba32, options))
                throw new InvalidOperationException($"The current decoder refused {c.Name}.");
        };
    }

    public static Action Baseline(byte[] source, byte[] rgba, Case c)
    {
        WebpInfo info = WebpDecoder.ReadInfo(source);
        object options = BaselineImageDecoder.CreateOptions(c.Scale, Region(c, info.Width, info.Height));
        return () =>
        {
            if (!BaselineImageDecoder.TryDecodeInto(source, rgba, options))
                throw new InvalidOperationException($"The baseline decoder refused {c.Name}.");
        };
    }

    /// <summary>Correctness before timing: the native output must equal the baseline's generic one byte for byte.</summary>
    public static void Check(byte[] source, Case c)
    {
        WebpInfo info = WebpDecoder.ReadInfo(source);
        var (w, h) = OutputSize(c, info);
        byte[] current = new byte[w * h * 4], baseline = new byte[w * h * 4];
        Current(source, current, c)();
        Baseline(source, baseline, c)();
        if (!current.AsSpan().SequenceEqual(baseline))
            throw new InvalidOperationException($"Region/scale case {c.Name}: the current output differs from the baseline's.");
    }
}

/// <summary>The baseline's <c>WebpImageDecoder.TryDecodeInto</c> with its own <c>DecodeOptions</c>, found by name.</summary>
internal static unsafe class BaselineImageDecoder
{
    private static readonly Lazy<(IntPtr Fn, Assembly Abstractions)> Loaded = new(() =>
    {
        Assembly asm = BaselineDecoder.Assembly.Value.Assembly;
        Type type = asm.GetType("Sdcb.SimdCodecs.Webp.WebpImageDecoder", throwOnError: true)!;
        MethodInfo method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "TryDecodeInto" && m.GetParameters().Length == 6);
        Assembly abstractions = method.GetParameters()[4].ParameterType.Assembly;
        return (method.MethodHandle.GetFunctionPointer(), abstractions);
    });

    /// <summary>A <c>DecodeOptions</c> of the baseline's own Abstractions.</summary>
    public static object CreateOptions(int scale, PixelRect? region)
    {
        Assembly abstractions = Loaded.Value.Abstractions;
        Type optionsType = abstractions.GetType("Sdcb.SimdCodecs.Abstractions.DecodeOptions", throwOnError: true)!;
        Type rectType = abstractions.GetType("Sdcb.SimdCodecs.Abstractions.PixelRect", throwOnError: true)!;
        object options = Activator.CreateInstance(optionsType)!;
        optionsType.GetProperty("ScaleDenominator")!.SetValue(options, scale);
        if (region is { } r)
            optionsType.GetProperty("Region")!.SetValue(options, Activator.CreateInstance(rectType, r.X, r.Y, r.Width, r.Height));
        return options;
    }

    /// <summary>
    /// TryDecodeInto to tight RGBA. The baseline's <c>PixelFormat</c> is the same struct (same source, same layout) as the
    /// current one, so the current <see cref="PixelFormat.Rgba32"/> is passed as is.
    /// </summary>
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> rgba, object options)
    {
        var fn = (delegate*<ReadOnlySpan<byte>, Span<byte>, int, PixelFormat, object?, CancellationToken, bool>)Loaded.Value.Fn;
        return fn(data, rgba, 0, PixelFormat.Rgba32, options, CancellationToken.None);
    }
}
