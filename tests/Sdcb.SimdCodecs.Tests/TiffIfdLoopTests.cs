using System.Buffers.Binary;
using ImageMagick;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// A next-IFD pointer that leads back into the chain. Untrusted input must not hang the reader:
/// every walk of the chain ends at the first IFD it has already visited, so a looping file has
/// exactly the pages before the loop — for <see cref="TiffReader.Read(ReadOnlySpan{byte})"/>,
/// <see cref="TiffReader.ReadInto{TSink}"/>, <see cref="TiffReader.ReadPage(ReadOnlySpan{byte}, int)"/>,
/// <see cref="TiffReader.ReadLayout(ReadOnlySpan{byte}, int)"/> and the facade's page count alike. The
/// files are ImageMagick's, with one pointer patched.
/// </summary>
public sealed class TiffIfdLoopTests
{
    // Two pages of different heights, so a page read twice is recognisable.
    private static byte[] TwoPages(out int[] ifds)
    {
        using var pages = new MagickImageCollection();
        pages.Add(new MagickImage(MagickColors.Gray, 6, 3));
        pages.Add(new MagickImage(MagickColors.White, 6, 5));
        var tiff = pages.ToByteArray(MagickFormat.Tiff);
        tiff[0].ShouldBe((byte)'I', "fixture: little-endian");

        var chain = new List<int>();
        for (var ifd = BinaryPrimitives.ReadInt32LittleEndian(tiff.AsSpan(4)); ifd != 0; ifd = BinaryPrimitives.ReadInt32LittleEndian(tiff.AsSpan(NextField(tiff, ifd))))
            chain.Add(ifd);
        chain.Count.ShouldBe(2, "fixture: two pages");
        ifds = [.. chain];
        return tiff;
    }

    private static int NextField(byte[] tiff, int ifd) => ifd + 2 + BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd)) * 12;

    private static void PointNext(byte[] tiff, int ifd, int target) =>
        BinaryPrimitives.WriteInt32LittleEndian(tiff.AsSpan(NextField(tiff, ifd)), target);

    // Run on a worker so that a regression fails the test instead of hanging the run.
    private static T Bounded<T>(Func<T> read)
    {
        var task = Task.Run(read);
        task.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue("the IFD walk did not terminate");
        return task.Result;
    }

    public static TheoryData<string> Loops() => ["second page back to the first", "second page to itself", "first page to itself"];

    [Theory]
    [MemberData(nameof(Loops))]
    public void ALoopEndsTheChain(string loop)
    {
        var tiff = TwoPages(out var ifds);
        var expected = loop switch
        {
            "second page back to the first" => Patch(tiff, ifds[1], ifds[0], pages: 2),
            "second page to itself" => Patch(tiff, ifds[1], ifds[1], pages: 2),
            _ => Patch(tiff, ifds[0], ifds[0], pages: 1),
        };
        int[] heights = [3, 5];

        var document = Bounded(() => TiffReader.Read(tiff));
        document.Pages.Select(p => p.Height).ShouldBe(heights[..expected]);

        var sink = new CountingSink();
        var streamed = Bounded(() => TiffReader.ReadInto(tiff, ref sink));
        streamed.Pages.Count.ShouldBe(expected);
        sink.Pages.ShouldBe(expected);

        for (var page = 0; page < expected; page++)
        {
            TiffReader.ReadPage(tiff, page).Height.ShouldBe(heights[page]);
            TiffReader.ReadLayout(tiff, page).Height.ShouldBe(heights[page]);
            TiffReader.ReadLayout(new MemoryStream(tiff), page).Height.ShouldBe(heights[page]);
        }
        // The loop does not hand out a page it has already given as a later one.
        foreach (var page in new[] { expected, expected + 1, 7 })
        {
            Should.Throw<ArgumentOutOfRangeException>(() => TiffReader.ReadPage(tiff, page));
            Should.Throw<ArgumentOutOfRangeException>(() => TiffReader.ReadLayout(tiff, page));
        }

        SimdCodecs.TryReadInfo(tiff, out var info).ShouldBeTrue();
        info.FrameCount.ShouldBe(expected);
    }

    private static int Patch(byte[] tiff, int ifd, int target, int pages)
    {
        PointNext(tiff, ifd, target);
        return pages;
    }

    private struct CountingSink : ITiffStripSink
    {
        public int Pages;

        public bool BeginPage(int pageIndex, TiffPage description)
        {
            Pages++;
            return true;
        }

        public void Strip(int pageIndex, int firstRow, int rowCount, ReadOnlySpan<byte> samples) { }
    }
}
