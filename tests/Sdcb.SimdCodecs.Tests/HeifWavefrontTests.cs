using Sdcb.SimdCodecs.Heif;
using Sdcb.SimdCodecs.Heif.Hevc;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The WPP wavefront (CTB rows in parallel) and the parallel in-loop filter passes of a picture decoded alone must
/// produce exactly the serial decoder's planes; and the wavefront must actually run on the WPP fixtures (it falls
/// back to serial decoding on anything unexpected, which would otherwise hide here).
/// </summary>
public sealed class HeifWavefrontTests
{
    public static TheoryData<string> SingleItems() => new()
    {
        "synth-photo-fuji-1600x900-main8.heic",
        "synth-photo-mangrove-1599x1199-main8.heic",
        "synth-screen-testsrc2-1280x720-main8.heic",
        "synth-screen-testsrc2-640x360-hlg-nclx.heic",
        "synth-screen-testsrc2-640x360-pq-vui.heic",
    };

    [Theory]
    [MemberData(nameof(SingleItems))]
    public void Parallel_decode_equals_serial_decode(string fixture)
    {
        var data = HeifTests.ReadFixture(fixture);
        var container = HeifContainer.Parse(data);
        container.Tiles.Count.ShouldBe(1);
        var payload = new byte[Sdcb.SimdCodecs.Heif.Isobmff.ItemContainer.GetItemLength(container.Tiles[0])];
        container.Items.Resolve(data, container.Tiles[0], payload);
        var config = container.TileConfigs[0];

        var serial = HevcImageDecoder.Decode(config, payload, parallel: false);
        var before = Volatile.Read(ref HevcFrameDecoder.WavefrontDecodes);
        for (var round = 0; round < 3; round++)
        {
            var parallel = HevcImageDecoder.Decode(config, payload, parallel: true);
            for (var c = 0; c < 3; c++) parallel.Planes[c].ShouldBe(serial.Planes[c], $"plane {c}, round {round}");
        }
        if (Environment.ProcessorCount > 1 && config.ParseSps() is { } sps && sps.PicHeightInCtbs > 1 && IsWpp(config))
        {
            Volatile.Read(ref HevcFrameDecoder.WavefrontDecodes).ShouldBeGreaterThan(before, "the wavefront fell back to serial decoding");
        }
    }

    private static bool IsWpp(HevcDecoderConfiguration config)
    {
        foreach (var nal in config.NalUnits)
        {
            if (Nal.Type(nal) == Nal.PpsNut) return HevcPps.Parse(Nal.UnescapeToArray(nal)).EntropyCodingSyncEnabled;
        }
        return false;
    }
}
