using System.Buffers.Binary;
using System.Diagnostics;
using Sdcb.SimdCodecs.Jpeg2000;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Hazard 6: hostile codestreams, bounded at rung 1 rather than after shipping.
/// <para>
/// <c>Sdcb.SimdCodecs.Jbig2</c> needed a 3.8 release to add this, and the shape of
/// the problem is identical. Every number that sizes the work — image extent,
/// tile extent, decomposition levels, code-block size, coded segment lengths —
/// is read from a codestream that in the primary use case arrives inside a PDF.
/// And <b>running out of input is not a backstop</b>: T.800 C.3.4, like T.88
/// E.3.4, has the MQ decoder read every byte past the end of its data as
/// <c>0xFF</c>, deliberately and for ever, so a truncated segment keeps
/// producing decisions instead of stopping.
/// </para>
/// <para>
/// This is the only file here that builds codestreams which <b>lie about their
/// size</b>. Everything else decodes bytes OpenJPEG produced, and an encoder can
/// only ever declare dimensions it told the truth about.
/// </para>
/// <para>
/// It carries the counterweight too. A ceiling that rejects real scans is also a
/// bug, so A4 at 300, 600 and 1200 dpi must stay comfortably inside every limit.
/// </para>
/// </summary>
public class Jpeg2000ResourceLimitTests
{
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000");

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".j2k"));

    /// <summary>
    /// Rewrites SIZ's image extent in place, leaving everything else — including
    /// the coded data — untouched. The result is a legal-looking header attached
    /// to a few hundred bytes of real packets, which is exactly the shape of a
    /// decompression bomb: enormous declared work, negligible input.
    /// </summary>
    private static byte[] WithDeclaredSize(string fixture, uint width, uint height)
    {
        var bytes = Fixture(fixture);

        // SOC(2) + SIZ marker(2) + Lsiz(2) + Rsiz(2) = 8, then Xsiz, Ysiz.
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), height);

        // XTsiz / YTsiz sit at 24 and 28. Growing the tile with the image keeps
        // it a single-tile codestream, so the refusal under test is a resource
        // limit rather than the multi-tile one.
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28), height);

        return bytes;
    }

    /// <summary>
    /// The headline case: a tiny file declaring a gigantic image. It must be
    /// refused from the declared geometry alone, before anything is allocated
    /// and without reading a coded byte.
    /// </summary>
    [Theory]
    [InlineData(65536u, 65536u)]      // 4 Gsample
    [InlineData(46341u, 46341u)]      // just past int.MaxValue when multiplied
    [InlineData(1u, 2147483647u)]     // degenerate shape, enormous area
    [InlineData(2147483647u, 1u)]
    public void ADeclaredImageOverTheCeiling_IsRefused(uint width, uint height)
    {
        var hostile = WithDeclaredSize("dwt5-struct64", width, height);

        var thrown = Should.Throw<InvalidDataException>(() => Jpeg2000Decoder.Decode(hostile));
        thrown.Message.ShouldContain("JPEG 2000");
    }

    /// <summary>
    /// And it must be refused <em>quickly</em>. A limit that is only reached
    /// after twenty seconds of work is a slower bomb, not a defused one.
    /// </summary>
    [Fact]
    public void ADeclaredImageOverTheCeiling_IsRefusedImmediately()
    {
        var hostile = WithDeclaredSize("dwt5-struct64", 65536, 65536);

        var stopwatch = Stopwatch.StartNew();
        Should.Throw<InvalidDataException>(() => Jpeg2000Decoder.Decode(hostile));
        stopwatch.Stop();

        stopwatch.ElapsedMilliseconds.ShouldBeLessThan(1000);
    }

    /// <summary>
    /// A 32-bit extent read into <see cref="int"/> arrives negative, and every
    /// bound after it is a comparison a negative number passes. Caught at the
    /// parse, not left to overflow something later.
    /// </summary>
    [Theory]
    [InlineData(0x80000000u, 64u)]
    [InlineData(64u, 0x80000000u)]
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu)]
    public void ADeclaredExtentPastIntMax_IsRefused(uint width, uint height)
    {
        var hostile = WithDeclaredSize("dwt5-struct64", width, height);

        Should.Throw<InvalidDataException>(() => Jpeg2000Decoder.Decode(hostile));
    }

    /// <summary>
    /// The counterweight. A limit that rejects a real scanned page is a bug in
    /// the other direction, so the sizes the format exists to carry must all sit
    /// inside the budget with room over.
    /// </summary>
    [Theory]
    [InlineData(2480, 3508, "A4 at 300 dpi")]
    [InlineData(4960, 7016, "A4 at 600 dpi")]
    [InlineData(9921, 14031, "A4 at 1200 dpi")]
    public void RealScanSizes_AreComfortablyInsideEveryLimit(int width, int height, string what)
    {
        var samples = (long)width * height;

        samples.ShouldBeLessThan(Jpeg2000Limits.MaxTileComponentSamples, what);
        Jpeg2000Limits.BudgetFor(width, height, 1).ShouldBeGreaterThan(samples * 2, what);

        // And the code-block count at the default 64x64, across all resolutions,
        // stays far inside its own ceiling.
        var blocks = (samples / (64 * 64)) * 2;
        blocks.ShouldBeLessThan(Jpeg2000Limits.MaxCodeBlocks, what);
    }

    /// <summary>
    /// The budget is an allowance, not a per-call check: it is shared across
    /// every band and resolution precisely so a codestream cannot get around it
    /// by splitting the same work into many individually plausible pieces. The
    /// same reasoning as <c>Jbig2PixelBudget</c> being shared across segments.
    /// </summary>
    [Fact]
    public void TheSampleBudget_IsSpentDownAcrossManySmallCharges()
    {
        var budget = new Jpeg2000SampleBudget(1000);

        budget.Charge(10, 10);   // 100
        budget.Charge(20, 20);   // 400, running total 500
        budget.Charge(10, 40);   // 400, running total 900

        Should.Throw<InvalidDataException>(() => budget.Charge(20, 20));
    }

    /// <summary>An unmetered budget is for unit tests only and must never run out.</summary>
    [Fact]
    public void AnUnmeteredBudget_NeverRunsOut()
    {
        var budget = Jpeg2000SampleBudget.Unmetered();

        for (var i = 0; i < 100; i++) budget.Charge(1 << 15, 1 << 15);
    }

    /// <summary>
    /// Mutation-fuzzing the committed fixtures, the technique that found JBIG2's
    /// 2 GiB-from-82-bytes case. Every single-byte flip must end in a decoded
    /// image or a named exception — never a hang, never an out-of-range, never an
    /// <see cref="OverflowException"/> or <see cref="OutOfMemoryException"/>
    /// escaping the contract.
    /// </summary>
    [Theory]
    [InlineData("dwt5-struct64")]
    [InlineData("cblk4-struct64")]
    [InlineData("odd37x23")]
    [InlineData("flat64")]
    public void SingleByteMutations_NeverEscapeTheContract(string name)
    {
        var original = Fixture(name);

        // A fixed sweep rather than a random one: the same bytes are tried on
        // every run, so a failure is reproducible from the test name alone.
        for (var offset = 0; offset < original.Length; offset += 7)
        {
            foreach (var xor in new byte[] { 0x01, 0x80, 0xFF })
            {
                var mutated = (byte[])original.Clone();
                mutated[offset] ^= xor;

                try
                {
                    var image = Jpeg2000Decoder.Decode(mutated);

                    // If it decoded, the result must at least be self-consistent.
                    image.Samples.Length.ShouldBe(image.Width * image.Height);
                }
                catch (InvalidDataException)
                {
                    // Malformed input, reported as such.
                }
                catch (NotSupportedException)
                {
                    // A mutation can easily flip a flag into a legal feature this
                    // rung does not implement. That is the right answer too.
                }
            }
        }
    }
}
