using Sdcb.SimdCodecs.Abstractions;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="PixelFormat"/>: the sixteen named formats and what each says about itself, the constructor refusals,
/// <see cref="PixelFormat.ForChannels"/> and <see cref="PixelFormat.All"/>.
/// </summary>
public sealed class PixelFormatTests
{
    private static readonly (string Name, PixelFormat Format, ChannelLayout Layout, SampleFormat Sample, AlphaMode Alpha, int Channels, int BytesPerPixel)[] Table =
    [
        ("Rgba32", PixelFormat.Rgba32, ChannelLayout.Rgba, SampleFormat.UInt8, AlphaMode.Straight, 4, 4),
        ("Bgra32", PixelFormat.Bgra32, ChannelLayout.Bgra, SampleFormat.UInt8, AlphaMode.Straight, 4, 4),
        ("Rgb24", PixelFormat.Rgb24, ChannelLayout.Rgb, SampleFormat.UInt8, AlphaMode.Straight, 3, 3),
        ("Bgr24", PixelFormat.Bgr24, ChannelLayout.Bgr, SampleFormat.UInt8, AlphaMode.Straight, 3, 3),
        ("Gray8", PixelFormat.Gray8, ChannelLayout.Gray, SampleFormat.UInt8, AlphaMode.Straight, 1, 1),
        ("GrayAlpha16", PixelFormat.GrayAlpha16, ChannelLayout.GrayAlpha, SampleFormat.UInt8, AlphaMode.Straight, 2, 2),
        ("Rgba64", PixelFormat.Rgba64, ChannelLayout.Rgba, SampleFormat.UInt16, AlphaMode.Straight, 4, 8),
        ("Bgra64", PixelFormat.Bgra64, ChannelLayout.Bgra, SampleFormat.UInt16, AlphaMode.Straight, 4, 8),
        ("Rgb48", PixelFormat.Rgb48, ChannelLayout.Rgb, SampleFormat.UInt16, AlphaMode.Straight, 3, 6),
        ("Bgr48", PixelFormat.Bgr48, ChannelLayout.Bgr, SampleFormat.UInt16, AlphaMode.Straight, 3, 6),
        ("Gray16", PixelFormat.Gray16, ChannelLayout.Gray, SampleFormat.UInt16, AlphaMode.Straight, 1, 2),
        ("GrayAlpha32", PixelFormat.GrayAlpha32, ChannelLayout.GrayAlpha, SampleFormat.UInt16, AlphaMode.Straight, 2, 4),
        ("Rgba32Premultiplied", PixelFormat.Rgba32Premultiplied, ChannelLayout.Rgba, SampleFormat.UInt8, AlphaMode.Premultiplied, 4, 4),
        ("Bgra32Premultiplied", PixelFormat.Bgra32Premultiplied, ChannelLayout.Bgra, SampleFormat.UInt8, AlphaMode.Premultiplied, 4, 4),
        ("Rgba64Premultiplied", PixelFormat.Rgba64Premultiplied, ChannelLayout.Rgba, SampleFormat.UInt16, AlphaMode.Premultiplied, 4, 8),
        ("Bgra64Premultiplied", PixelFormat.Bgra64Premultiplied, ChannelLayout.Bgra, SampleFormat.UInt16, AlphaMode.Premultiplied, 4, 8),
    ];

    public static TheoryData<int> Indices() => [.. Enumerable.Range(0, 16)];

    [Fact]
    public void TheTableHasSixteenRows() => Table.Length.ShouldBe(16);

    [Theory]
    [MemberData(nameof(Indices))]
    public void EveryStaticFormatDescribesItself(int index)
    {
        var (name, format, layout, sample, alpha, channels, bytesPerPixel) = Table[index];
        format.Layout.ShouldBe(layout, name);
        format.Sample.ShouldBe(sample, name);
        format.Alpha.ShouldBe(alpha, name);
        format.Channels.ShouldBe(channels, name);
        format.BytesPerSample.ShouldBe(sample == SampleFormat.UInt8 ? 1 : 2, name);
        format.BytesPerPixel.ShouldBe(bytesPerPixel, name);
        format.ToString().ShouldBe(name);
        format.IsPremultiplied.ShouldBe(alpha == AlphaMode.Premultiplied, name);
        format.HasAlpha.ShouldBe(layout is ChannelLayout.Rgba or ChannelLayout.Bgra or ChannelLayout.GrayAlpha, name);
        format.IsGray.ShouldBe(layout is ChannelLayout.Gray or ChannelLayout.GrayAlpha, name);
        format.IsBgr.ShouldBe(layout is ChannelLayout.Bgra or ChannelLayout.Bgr, name);
        format.IsBasic8.ShouldBe(name is "Rgba32" or "Bgra32" or "Rgb24" or "Bgr24" or "Gray8", name);

        // The constructor rebuilds exactly the static value (and equality is by value).
        new PixelFormat(layout, sample, alpha).ShouldBe(format, name);
    }

    [Fact]
    public void DefaultIsRgba32()
    {
        default(PixelFormat).ShouldBe(PixelFormat.Rgba32);
        default(PixelFormat).ToString().ShouldBe("Rgba32");
        new PixelFormat(ChannelLayout.Rgba, SampleFormat.UInt8).ShouldBe(default);
    }

    [Fact]
    public void AllHasSixteenDistinctValuesInTheDocumentedOrder()
    {
        PixelFormat.All.Length.ShouldBe(16);
        PixelFormat.All.ToArray().Distinct().Count().ShouldBe(16);
        PixelFormat.All.ToArray().ShouldBe(Table.Select(t => t.Format).ToArray());
        PixelFormat.All.ToArray().Select(f => f.ToString()).Distinct().Count().ShouldBe(16);
    }

    [Theory]
    [InlineData(ChannelLayout.Gray)]
    [InlineData(ChannelLayout.GrayAlpha)]
    [InlineData(ChannelLayout.Rgb)]
    [InlineData(ChannelLayout.Bgr)]
    public void PremultipliedNeedsColourAndAlpha(ChannelLayout layout)
    {
        foreach (var sample in new[] { SampleFormat.UInt8, SampleFormat.UInt16 })
            Should.Throw<ArgumentException>(() => new PixelFormat(layout, sample, AlphaMode.Premultiplied));
    }

    [Fact]
    public void Float32SamplesAreRefused()
    {
        foreach (var layout in Enum.GetValues<ChannelLayout>())
            Should.Throw<ArgumentException>(() => new PixelFormat(layout, SampleFormat.Float32));
    }

    [Fact]
    public void UndefinedEnumValuesAreRefused()
    {
        Should.Throw<ArgumentException>(() => new PixelFormat((ChannelLayout)6, SampleFormat.UInt8));
        Should.Throw<ArgumentException>(() => new PixelFormat((ChannelLayout)255, SampleFormat.UInt8));
        Should.Throw<ArgumentException>(() => new PixelFormat(ChannelLayout.Rgba, (SampleFormat)3));
        Should.Throw<ArgumentException>(() => new PixelFormat(ChannelLayout.Rgba, (SampleFormat)(-1)));
        Should.Throw<ArgumentException>(() => new PixelFormat(ChannelLayout.Rgba, SampleFormat.UInt8, (AlphaMode)2));
        Should.Throw<ArgumentException>(() => new PixelFormat(ChannelLayout.Rgba, SampleFormat.UInt8, (AlphaMode)255));
    }

    [Fact]
    public void ForChannelsPicksTheStraightLayoutOfThatWidth()
    {
        PixelFormat.ForChannels(1, SampleFormat.UInt8).ShouldBe(PixelFormat.Gray8);
        PixelFormat.ForChannels(2, SampleFormat.UInt8).ShouldBe(PixelFormat.GrayAlpha16);
        PixelFormat.ForChannels(3, SampleFormat.UInt8).ShouldBe(PixelFormat.Rgb24);
        PixelFormat.ForChannels(4, SampleFormat.UInt8).ShouldBe(PixelFormat.Rgba32);
        PixelFormat.ForChannels(1, SampleFormat.UInt16).ShouldBe(PixelFormat.Gray16);
        PixelFormat.ForChannels(2, SampleFormat.UInt16).ShouldBe(PixelFormat.GrayAlpha32);
        PixelFormat.ForChannels(3, SampleFormat.UInt16).ShouldBe(PixelFormat.Rgb48);
        PixelFormat.ForChannels(4, SampleFormat.UInt16).ShouldBe(PixelFormat.Rgba64);
    }

    [Fact]
    public void ForChannelsRefusesWhatIsNotAnIntegerLayout()
    {
        foreach (var channels in new[] { 0, 5, -1, int.MaxValue })
            Should.Throw<ArgumentOutOfRangeException>(() => PixelFormat.ForChannels(channels, SampleFormat.UInt8));
        for (var channels = 1; channels <= 4; channels++)
        {
            var c = channels;
            Should.Throw<ArgumentException>(() => PixelFormat.ForChannels(c, SampleFormat.Float32));
        }
    }

    [Fact]
    public void WithersChangeOnePropertyAndKeepTheRest()
    {
        PixelFormat.Rgba32Premultiplied.To16Bit().ShouldBe(PixelFormat.Rgba64Premultiplied);
        PixelFormat.Bgra64Premultiplied.To8Bit().ShouldBe(PixelFormat.Bgra32Premultiplied);
        PixelFormat.Bgra64Premultiplied.ToStraight().ShouldBe(PixelFormat.Bgra64);
        PixelFormat.Rgba32.WithLayout(ChannelLayout.Bgra).ShouldBe(PixelFormat.Bgra32);
        PixelFormat.Rgba32Premultiplied.WithLayout(ChannelLayout.Bgra).ShouldBe(PixelFormat.Bgra32Premultiplied);
        // A layout that cannot be premultiplied comes out straight.
        PixelFormat.Rgba32Premultiplied.WithLayout(ChannelLayout.Rgb).ShouldBe(PixelFormat.Rgb24);
        PixelFormat.Gray16.WithSample(SampleFormat.UInt8).ShouldBe(PixelFormat.Gray8);
    }
}
