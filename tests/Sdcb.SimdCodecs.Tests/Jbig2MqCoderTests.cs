using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Conformance tests for the MQ arithmetic coder of ITU-T T.88 Annex E.
/// <para>
/// The core of the suite is the published test sequence of Annex H.2: 256 known
/// decisions and the 30-byte codestream they must produce. That vector is
/// spec-derived, so it carries no licence contamination, and it is an absolute
/// check rather than a self-consistency one — encoding it and decoding it are
/// two independent ways to be wrong, and both are exercised here. Everything
/// else in the JBIG2 package sits on top of this coder, so if these pass the
/// rest of the debugging is about templates and segment layout, never about
/// arithmetic.
/// </para>
/// </summary>
public sealed class Jbig2MqCoderTests
{
    // T.88 Annex H.2, the 256-bit test sequence, MSB first.
    private static readonly byte[] TestSequence =
    [
        0x00, 0x02, 0x00, 0x51, 0x00, 0x00, 0x00, 0xC0, 0x03, 0x52, 0x87, 0x2A, 0xAA, 0xAA, 0xAA, 0xAA,
        0x82, 0xC0, 0x20, 0x00, 0xFC, 0xD7, 0x9E, 0xF6, 0xBF, 0x7F, 0xED, 0x90, 0x4F, 0x46, 0xA3, 0xBF,
    ];

    // T.88 Annex H.2, the codestream those decisions encode to.
    private static readonly byte[] TestCodestream =
    [
        0x84, 0xC7, 0x3B, 0xFC, 0xE1, 0xA1, 0x43, 0x04, 0x02, 0x20, 0x00, 0x00, 0x41, 0x0D, 0xBB, 0x86,
        0xF4, 0x31, 0x7F, 0xFF, 0x88, 0xFF, 0x37, 0x47, 0x1A, 0xDB, 0x6A, 0xDF, 0xFF, 0xAC,
    ];

    [Fact]
    public void QeTable_MatchesT88TableE1Invariants()
    {
        MqDecoder.StateCount.ShouldBe(47);

        // Every transition must land inside the table, and Qe values are all
        // strictly below the 0x8000 half-interval.
        for (var i = 0; i < MqDecoder.StateCount; i++)
        {
            var state = MqDecoder.StateAt(i);
            state.Qe.ShouldBeGreaterThan((ushort)0, $"row {i}");
            state.Qe.ShouldBeLessThan((ushort)0x8000, $"row {i}");
            state.Nmps.ShouldBeLessThan((byte)MqDecoder.StateCount, $"row {i}");
            state.Nlps.ShouldBeLessThan((byte)MqDecoder.StateCount, $"row {i}");
            state.Switch.ShouldBeLessThanOrEqualTo((byte)1, $"row {i}");
        }

        // The switch flag is set on exactly the three rows T.88 marks it on, and
        // row 46 is the terminal state that never leaves itself.
        MqDecoder.StateAt(0).Switch.ShouldBe((byte)1);
        MqDecoder.StateAt(6).Switch.ShouldBe((byte)1);
        MqDecoder.StateAt(14).Switch.ShouldBe((byte)1);
        MqDecoder.StateAt(46).Nmps.ShouldBe((byte)46);
        MqDecoder.StateAt(46).Nlps.ShouldBe((byte)46);
    }

    [Fact]
    public void Encode_T88AnnexH2TestSequence_ProducesThePublishedCodestream()
    {
        var encoder = new Jbig2MqEncoder();
        var contexts = new byte[1];

        foreach (var bit in Bits(TestSequence))
            encoder.Encode(contexts, 0, bit);

        encoder.Flush().ShouldBe(TestCodestream);
    }

    [Fact]
    public void Decode_T88AnnexH2Codestream_RecoversTheTestSequence()
    {
        var decoder = new MqDecoder(TestCodestream);
        var contexts = new byte[1];
        var decoded = new List<int>();

        for (var i = 0; i < 256; i++)
            decoded.Add(decoder.Decode(contexts, 0));

        decoded.ShouldBe(Bits(TestSequence));
    }

    [Fact]
    public void Decode_PastTheEndOfTheData_KeepsProducingDecisions()
    {
        // T.88 E.3.4 feeds 0xFF once the coded data runs out rather than
        // faulting, so a truncated region degrades instead of throwing. Decode
        // well past the 256 real decisions and require only that it terminates.
        var decoder = new MqDecoder(TestCodestream);
        var contexts = new byte[1];

        for (var i = 0; i < 4096; i++)
            decoder.Decode(contexts, 0);
    }

    [Fact]
    public void RoundTrip_MultipleContexts_IsLossless()
    {
        // A deterministic pseudo-random bit sequence spread over 16 contexts, so
        // the adaptive state machines actually diverge from one another.
        var random = new Random(20260728);
        var bits = new int[5000];
        var indices = new int[bits.Length];
        for (var i = 0; i < bits.Length; i++)
        {
            // Biased towards 0 so the estimator climbs into the low-Qe rows
            // instead of hovering near the top of the table.
            bits[i] = random.Next(10) == 0 ? 1 : 0;
            indices[i] = random.Next(16);
        }

        var encoder = new Jbig2MqEncoder();
        var encodeContexts = new byte[16];
        for (var i = 0; i < bits.Length; i++)
            encoder.Encode(encodeContexts, indices[i], bits[i]);
        var coded = encoder.Flush();

        var decoder = new MqDecoder(coded);
        var decodeContexts = new byte[16];
        for (var i = 0; i < bits.Length; i++)
            decoder.Decode(decodeContexts, indices[i]).ShouldBe(bits[i], $"decision {i}");

        // The skewed input must actually compress, or the test is passing on a
        // pathological path that would hide estimator bugs.
        coded.Length.ShouldBeLessThan(bits.Length / 8);
    }

    private static List<int> Bits(byte[] bytes)
    {
        var bits = new List<int>(bytes.Length * 8);
        foreach (var b in bytes)
            for (var shift = 7; shift >= 0; shift--)
                bits.Add((b >> shift) & 1);

        return bits;
    }
}
