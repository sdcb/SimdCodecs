using System;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>The channels of an interleaved pixel, in memory order.</summary>
public enum ChannelLayout : byte
{
    /// <summary>Red, green, blue, alpha.</summary>
    Rgba,

    /// <summary>Blue, green, red, alpha — the layout of most Windows, WPF, Direct2D and Skia surfaces.</summary>
    Bgra,

    /// <summary>Red, green, blue.</summary>
    Rgb,

    /// <summary>Blue, green, red — OpenCV's default.</summary>
    Bgr,

    /// <summary>One grey (luma) sample.</summary>
    Gray,

    /// <summary>Grey, then alpha.</summary>
    GrayAlpha,
}

/// <summary>How the colour samples of a pixel with alpha relate to its alpha.</summary>
public enum AlphaMode : byte
{
    /// <summary>Colour is independent of alpha (unassociated alpha), as every codec stores it.</summary>
    Straight,

    /// <summary>Colour has been multiplied by alpha (associated alpha): <c>round(c × a / max)</c>. Only
    /// <see cref="ChannelLayout.Rgba"/> and <see cref="ChannelLayout.Bgra"/> carry it.</summary>
    Premultiplied,
}

/// <summary>
/// An interleaved pixel layout: which channels, in what order (<see cref="Layout"/>), at what sample width
/// (<see cref="Sample"/>, 8 or 16 bits) and with what alpha semantics (<see cref="Alpha"/>). It is what a decoder
/// writes into a caller's buffer and what an encoder reads from one.
/// <para>
/// The set is closed and every value is valid: the constructor refuses a combination without meaning (a
/// premultiplied layout without alpha, float samples), so code switching on a format never meets an
/// out-of-range one. 16-bit samples are in host byte order, as in <see cref="IDecodedImage.Pixels"/>.
/// <c>default(PixelFormat)</c> is <see cref="Rgba32"/>.
/// </para>
/// <para>
/// Conversions between formats are <see cref="PixelConverter"/>'s, and fixed: 8 → 16 bits is <c>v × 257</c>,
/// 16 → 8 bits keeps the high byte (<c>v &gt;&gt; 8</c>), depth changes before layout, grey broadcasts to R, G
/// and B, colour reaches grey as BT.601 luma, a missing alpha is opaque, and a layout without alpha drops it
/// (never composites).
/// </para>
/// </summary>
public readonly record struct PixelFormat
{
    // Stored as bytes so that default(PixelFormat) is Rgba / UInt8 / Straight: Rgba32.
    private readonly ChannelLayout _layout;
    private readonly byte _sample;
    private readonly AlphaMode _alpha;

    /// <summary>Describes a pixel layout.</summary>
    /// <param name="layout">The channels, in memory order.</param>
    /// <param name="sample"><see cref="SampleFormat.UInt8"/> or <see cref="SampleFormat.UInt16"/>.</param>
    /// <param name="alpha">The alpha semantics; <see cref="AlphaMode.Premultiplied"/> needs
    /// <see cref="ChannelLayout.Rgba"/> or <see cref="ChannelLayout.Bgra"/>.</param>
    /// <exception cref="ArgumentException">An argument is not a defined member, <paramref name="sample"/> is
    /// <see cref="SampleFormat.Float32"/>, or <paramref name="alpha"/> is premultiplied for a layout without
    /// colour and alpha.</exception>
    public PixelFormat(ChannelLayout layout, SampleFormat sample, AlphaMode alpha = AlphaMode.Straight)
    {
        if (layout > ChannelLayout.GrayAlpha)
            throw new ArgumentException($"Not a defined channel layout: {(int)layout}.", nameof(layout));
        if (sample is not (SampleFormat.UInt8 or SampleFormat.UInt16))
            throw new ArgumentException(
                $"A pixel format has 8- or 16-bit integer samples, not {sample}; float rasters go through IDecodedImage.ToFloats.",
                nameof(sample));
        if (alpha > AlphaMode.Premultiplied)
            throw new ArgumentException($"Not a defined alpha mode: {(int)alpha}.", nameof(alpha));
        if (alpha == AlphaMode.Premultiplied && layout is not (ChannelLayout.Rgba or ChannelLayout.Bgra))
            throw new ArgumentException($"Only Rgba and Bgra can be premultiplied, not {layout}.", nameof(alpha));
        _layout = layout;
        _sample = (byte)sample;
        _alpha = alpha;
    }

    /// <summary>The channels, in memory order.</summary>
    public ChannelLayout Layout => _layout;

    /// <summary>The sample width: <see cref="SampleFormat.UInt8"/> or <see cref="SampleFormat.UInt16"/>.</summary>
    public SampleFormat Sample => (SampleFormat)_sample;

    /// <summary>The alpha semantics. Always <see cref="AlphaMode.Straight"/> for a layout other than
    /// <see cref="ChannelLayout.Rgba"/> and <see cref="ChannelLayout.Bgra"/>.</summary>
    public AlphaMode Alpha => _alpha;

    /// <summary>Samples per pixel: 4, 3, 1 or 2.</summary>
    public int Channels => _layout switch
    {
        ChannelLayout.Rgba or ChannelLayout.Bgra => 4,
        ChannelLayout.Rgb or ChannelLayout.Bgr => 3,
        ChannelLayout.Gray => 1,
        _ => 2,
    };

    /// <summary>Bytes per sample: 1 or 2.</summary>
    public int BytesPerSample => _sample + 1;

    /// <summary>Bytes per pixel: <see cref="Channels"/> × <see cref="BytesPerSample"/>.</summary>
    public int BytesPerPixel => Channels * BytesPerSample;

    /// <summary>True for <see cref="ChannelLayout.Rgba"/>, <see cref="ChannelLayout.Bgra"/> and
    /// <see cref="ChannelLayout.GrayAlpha"/>.</summary>
    public bool HasAlpha => _layout is ChannelLayout.Rgba or ChannelLayout.Bgra or ChannelLayout.GrayAlpha;

    /// <summary>True for <see cref="ChannelLayout.Gray"/> and <see cref="ChannelLayout.GrayAlpha"/>.</summary>
    public bool IsGray => _layout is ChannelLayout.Gray or ChannelLayout.GrayAlpha;

    /// <summary>True when blue comes before red: <see cref="ChannelLayout.Bgra"/> and <see cref="ChannelLayout.Bgr"/>.</summary>
    public bool IsBgr => _layout is ChannelLayout.Bgra or ChannelLayout.Bgr;

    /// <summary>True for <see cref="AlphaMode.Premultiplied"/>.</summary>
    public bool IsPremultiplied => _alpha == AlphaMode.Premultiplied;

    /// <summary>The same channels at <paramref name="sample"/> width, keeping the alpha mode.</summary>
    public PixelFormat WithSample(SampleFormat sample) => new(_layout, sample, _alpha);

    /// <summary>The 8-bit format with the same channels and alpha mode (the format itself when it is 8-bit).</summary>
    public PixelFormat To8Bit() => WithSample(SampleFormat.UInt8);

    /// <summary>The 16-bit format with the same channels and alpha mode (the format itself when it is 16-bit).</summary>
    public PixelFormat To16Bit() => WithSample(SampleFormat.UInt16);

    /// <summary>The same channels and sample width with straight alpha (the format itself when it is straight).</summary>
    public PixelFormat ToStraight() => new(_layout, Sample);

    /// <summary>The same sample width and alpha mode with <paramref name="layout"/>'s channels (straight alpha when
    /// <paramref name="layout"/> cannot be premultiplied).</summary>
    public PixelFormat WithLayout(ChannelLayout layout) =>
        new(layout, Sample, layout is ChannelLayout.Rgba or ChannelLayout.Bgra ? _alpha : AlphaMode.Straight);

    /// <summary>
    /// True for the five layouts of 1.x — <see cref="Rgba32"/>, <see cref="Bgra32"/>, <see cref="Rgb24"/>,
    /// <see cref="Bgr24"/> and <see cref="Gray8"/>: 8-bit, straight alpha, not grey + alpha. The formats most codecs
    /// have native paths for.
    /// </summary>
    public bool IsBasic8 => _sample == 0 && _alpha == AlphaMode.Straight && _layout != ChannelLayout.GrayAlpha;

    /// <summary>4 bytes per pixel: R, G, B, A. A missing alpha is 255.</summary>
    public static PixelFormat Rgba32 => new(ChannelLayout.Rgba, SampleFormat.UInt8);

    /// <summary>4 bytes per pixel: B, G, R, A. A missing alpha is 255.</summary>
    public static PixelFormat Bgra32 => new(ChannelLayout.Bgra, SampleFormat.UInt8);

    /// <summary>3 bytes per pixel: R, G, B. Alpha is dropped, not composited.</summary>
    public static PixelFormat Rgb24 => new(ChannelLayout.Rgb, SampleFormat.UInt8);

    /// <summary>3 bytes per pixel: B, G, R. Alpha is dropped, not composited.</summary>
    public static PixelFormat Bgr24 => new(ChannelLayout.Bgr, SampleFormat.UInt8);

    /// <summary>
    /// 1 byte per pixel: the image's luma. Grey sources keep their samples. A decoder for a codec that stores
    /// luma returns it as stored (a YCbCr JPEG's Y plane), which can differ by a code value or two from the luma
    /// of the decoded RGB. Other colour sources take the BT.601 luma of their code values,
    /// <c>(77·R + 150·G + 29·B + 128) &gt;&gt; 8</c>. Alpha is dropped, not composited.
    /// </summary>
    public static PixelFormat Gray8 => new(ChannelLayout.Gray, SampleFormat.UInt8);

    /// <summary>2 bytes per pixel: 8-bit grey, then 8-bit alpha.</summary>
    public static PixelFormat GrayAlpha16 => new(ChannelLayout.GrayAlpha, SampleFormat.UInt8);

    /// <summary>8 bytes per pixel: 16-bit R, G, B, A. A missing alpha is 65535.</summary>
    public static PixelFormat Rgba64 => new(ChannelLayout.Rgba, SampleFormat.UInt16);

    /// <summary>8 bytes per pixel: 16-bit B, G, R, A. A missing alpha is 65535.</summary>
    public static PixelFormat Bgra64 => new(ChannelLayout.Bgra, SampleFormat.UInt16);

    /// <summary>6 bytes per pixel: 16-bit R, G, B.</summary>
    public static PixelFormat Rgb48 => new(ChannelLayout.Rgb, SampleFormat.UInt16);

    /// <summary>6 bytes per pixel: 16-bit B, G, R.</summary>
    public static PixelFormat Bgr48 => new(ChannelLayout.Bgr, SampleFormat.UInt16);

    /// <summary>2 bytes per pixel: 16-bit grey (luma, as for <see cref="Gray8"/>, computed on 16-bit samples).</summary>
    public static PixelFormat Gray16 => new(ChannelLayout.Gray, SampleFormat.UInt16);

    /// <summary>4 bytes per pixel: 16-bit grey, then 16-bit alpha.</summary>
    public static PixelFormat GrayAlpha32 => new(ChannelLayout.GrayAlpha, SampleFormat.UInt16);

    /// <summary><see cref="Rgba32"/> with premultiplied alpha.</summary>
    public static PixelFormat Rgba32Premultiplied => new(ChannelLayout.Rgba, SampleFormat.UInt8, AlphaMode.Premultiplied);

    /// <summary><see cref="Bgra32"/> with premultiplied alpha: the pixel format of WPF's <c>Pbgra32</c>, Direct2D
    /// and most compositors.</summary>
    public static PixelFormat Bgra32Premultiplied => new(ChannelLayout.Bgra, SampleFormat.UInt8, AlphaMode.Premultiplied);

    /// <summary><see cref="Rgba64"/> with premultiplied alpha.</summary>
    public static PixelFormat Rgba64Premultiplied => new(ChannelLayout.Rgba, SampleFormat.UInt16, AlphaMode.Premultiplied);

    /// <summary><see cref="Bgra64"/> with premultiplied alpha.</summary>
    public static PixelFormat Bgra64Premultiplied => new(ChannelLayout.Bgra, SampleFormat.UInt16, AlphaMode.Premultiplied);

    /// <summary>Every format, in a fixed order: the 8-bit straight layouts, their 16-bit twins, then the
    /// premultiplied ones.</summary>
    public static ReadOnlySpan<PixelFormat> All => s_all;

    private static readonly PixelFormat[] s_all =
    [
        Rgba32, Bgra32, Rgb24, Bgr24, Gray8, GrayAlpha16,
        Rgba64, Bgra64, Rgb48, Bgr48, Gray16, GrayAlpha32,
        Rgba32Premultiplied, Bgra32Premultiplied, Rgba64Premultiplied, Bgra64Premultiplied,
    ];

    /// <summary>The format whose layout holds the samples of an <see cref="IDecodedImage"/>: 1, 2, 3 or 4
    /// channels (grey, grey + alpha, RGB, RGBA) of <paramref name="sample"/>, straight alpha.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channels"/> is not 1..4.</exception>
    /// <exception cref="ArgumentException"><paramref name="sample"/> is not an integer format.</exception>
    public static PixelFormat ForChannels(int channels, SampleFormat sample) => channels switch
    {
        1 => new(ChannelLayout.Gray, sample),
        2 => new(ChannelLayout.GrayAlpha, sample),
        3 => new(ChannelLayout.Rgb, sample),
        4 => new(ChannelLayout.Rgba, sample),
        _ => throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be 1..4."),
    };

    /// <summary>The format's name, as the static property that returns it (<c>Bgra32Premultiplied</c>).</summary>
    public override string ToString()
    {
        var bits = Channels * (Sample == SampleFormat.UInt8 ? 8 : 16);
        var name = _layout switch
        {
            ChannelLayout.Rgba => "Rgba",
            ChannelLayout.Bgra => "Bgra",
            ChannelLayout.Rgb => "Rgb",
            ChannelLayout.Bgr => "Bgr",
            ChannelLayout.Gray => "Gray",
            _ => "GrayAlpha",
        };
        return IsPremultiplied ? $"{name}{bits}Premultiplied" : $"{name}{bits}";
    }
}
