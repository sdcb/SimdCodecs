using System.Buffers.Binary;
using Sdcb.SimdCodecs.Jpeg2000;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// What rung 2's exact half added: several components, the reversible component transform, the
/// per-component overrides COC and QCC, and quality layers brought forward from rung 3.
/// <para>
/// The lossless fixtures already run through <see cref="Jpeg2000DecoderTests"/>, exact, because
/// their source raster is their expected output. This file holds what that cannot say: that an
/// override is applied to its own component and no other, that a lossy reversible codestream
/// decodes as OpenJPEG decodes it, and that a header combining features illegally is refused as
/// malformed rather than decoded.
/// </para>
/// </summary>
public class Jpeg2000ComponentsAndLayersTests
{
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000");

    private static string LossyDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000-lossy");

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".j2k"));

    /// <summary>
    /// The lossy REVERSIBLE fixtures only. The 9/7 ones beside them are compared within a
    /// tolerance, in <see cref="Jpeg2000IrreversibleTests"/>, and must never reach the exact
    /// assertion here, nor this one theirs.
    /// </summary>
    public static TheoryData<string> LossyCodestreams
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in Directory.GetFiles(LossyDirectory, "lossy53-*.j2k").OrderBy(p => p))
                data.Add(Path.GetFileNameWithoutExtension(path));

            return data;
        }
    }

    /// <summary>
    /// A reversible codestream cut short by its rate decodes to OpenJPEG's samples exactly. Its
    /// expected output is not its source, because code-blocks lost their last passes, so what
    /// this pins is the reconstruction rule: T.800 E.1.1.2 puts a coefficient whose low bits were
    /// cut in the middle of the range they leave open. Without it, one of the four reversible
    /// images measured in real PDFs decoded 1,007 samples one away from OpenJPEG's.
    /// </summary>
    [Theory]
    [MemberData(nameof(LossyCodestreams))]
    public void LossyReversibleCodestream_DecodesExactlyAsOpenJpegDoes(string name)
    {
        OpenJpegOracle.RequireOrSkip();

        var bytes = File.ReadAllBytes(Path.Combine(LossyDirectory, name + ".j2k"));
        var decoded = Jpeg2000Decoder.Decode(bytes);
        var expected = OpenJpegOracle.Decode(bytes, decoded.Components == 1 ? ".pgm" : ".ppm");

        decoded.Width.ShouldBe(expected.Width);
        decoded.Height.ShouldBe(expected.Height);
        decoded.Components.ShouldBe(expected.Components);
        decoded.Samples.ShouldBe(expected.Samples);
    }

    /// <summary>
    /// The lossy fixtures really are lossy: decoded, they are not the raster they were made from.
    /// Without this, a regenerated fixture that came out lossless would make the oracle test above
    /// pass while testing nothing it claims to.
    /// </summary>
    [Fact]
    public void LossyFixtures_AreNotLossless()
    {
        var decoded = Jpeg2000Decoder.Decode(File.ReadAllBytes(Path.Combine(LossyDirectory, "lossy53-struct64.j2k")));
        var source = Jpeg2000Decoder.Decode(Fixture("dwt5-struct64"));

        decoded.Samples.ShouldNotBe(source.Samples);
    }

    /// <summary>
    /// A three-component image has no single grey plane, and asking for one is a caller error, not
    /// a reason to hand back the first component as if it were the picture.
    /// </summary>
    [Fact]
    public void ThreeComponents_HaveNoGreyPlane()
    {
        var decoded = Jpeg2000Decoder.Decode(Fixture("rgb-struct64"));

        decoded.Components.ShouldBe(3);
        Should.Throw<InvalidOperationException>(() => decoded.ToGray8());

        var source = Pnm.Read(Jpeg2000FixtureTests.SourceRaster("rgb-struct64"));
        decoded.To8Bit().ShouldBe(source.Samples.Select(s => (byte)s).ToArray());
    }

    /// <summary>
    /// A COC and a QCC that restate COD and QCD for one component change nothing. This is the
    /// plumbing check: they are read, resolved against the main header, and the decode still runs
    /// on the values they carry.
    /// </summary>
    [Fact]
    public void OverridesThatRestateTheDefaults_ChangeNothing()
    {
        var bytes = Fixture("rgb-struct64");
        var (cod, qcd) = (MainHeaderSegment(bytes, 0x52), MainHeaderSegment(bytes, 0x5C));

        // COC: Ccoc, then Scoc (only COD's precinct bit), then COD's SPcod unchanged.
        byte[] coc = [1, (byte)(cod[0] & 0x01), .. cod[5..]];
        // QCC: Cqcc, then QCD's body unchanged.
        byte[] qcc = [1, .. qcd];

        var patched = InsertBeforeFirstSot(bytes, Segment(0x53, coc), Segment(0x5D, qcc));
        var expected = Pnm.Read(Jpeg2000FixtureTests.SourceRaster("rgb-struct64"));

        Jpeg2000Decoder.Decode(patched).Samples.ShouldBe(expected.Samples);
    }

    /// <summary>
    /// A QCC is applied to its own component and to no other. With no component transform the
    /// components decode independently, so giving component 2 one more guard bit than it was coded
    /// with must corrupt component 2, and must leave 0 and 1 exactly as they were. A QCC read and
    /// then ignored, or applied to every component, fails one half or the other.
    /// </summary>
    [Fact]
    public void AQcc_IsAppliedToItsComponentAlone()
    {
        var bytes = Fixture("rgb-nomct-struct64");
        var qcd = MainHeaderSegment(bytes, 0x5C);

        // Sqcd's top three bits are the guard bits; one more moves every band's Mb up a plane.
        var sqcc = (byte)(qcd[0] + (1 << 5));
        byte[] qcc = [2, sqcc, .. qcd[1..]];

        var patched = InsertBeforeFirstSot(bytes, Segment(0x5D, qcc));
        var expected = Pnm.Read(Jpeg2000FixtureTests.SourceRaster("rgb-nomct-struct64")).Samples;
        var decoded = Jpeg2000Decoder.Decode(patched).Samples;

        Plane(decoded, 0).ShouldBe(Plane(expected, 0));
        Plane(decoded, 1).ShouldBe(Plane(expected, 1));
        Plane(decoded, 2).ShouldNotBe(Plane(expected, 2));
    }

    /// <summary>
    /// A COC is applied too: one that gives component 1 a code-block style flag reaches the
    /// envelope check, which refuses the flag by name. Ignored, it would decode cleanly.
    /// </summary>
    [Fact]
    public void ACoc_IsApplied()
    {
        var bytes = Fixture("rgb-struct64");
        var cod = MainHeaderSegment(bytes, 0x52);

        byte[] spcoc = [.. cod[5..]];
        spcoc[3] = 0x01; // selective arithmetic coding bypass
        byte[] coc = [1, (byte)(cod[0] & 0x01), .. spcoc];

        var patched = InsertBeforeFirstSot(bytes, Segment(0x53, coc));

        Should.Throw<NotSupportedException>(() => Jpeg2000Decoder.Decode(patched))
            .Message.ShouldContain("code-block style");
    }

    /// <summary>An override naming a component SIZ does not declare is malformed.</summary>
    [Fact]
    public void AQccForAComponentThatDoesNotExist_IsMalformed()
    {
        var bytes = Fixture("rgb-struct64");
        var qcd = MainHeaderSegment(bytes, 0x5C);

        var patched = InsertBeforeFirstSot(bytes, Segment(0x5D, [3, .. qcd]));

        Should.Throw<InvalidDataException>(() => Jpeg2000Decoder.Decode(patched));
    }

    /// <summary>
    /// The component transform is defined on components 0, 1 and 2 (A.6.1). Set on a one-component
    /// image it names nothing, and the codestream is malformed rather than merely unusual.
    /// </summary>
    [Fact]
    public void TheComponentTransformOnOneComponent_IsMalformed()
    {
        var bytes = Fixture("dwt5-struct64");
        var cod = MainHeaderOffset(bytes, 0x52);

        // FF52, Lcod, Scod, progression, layers (2) — then the MCT byte.
        bytes[cod + 2 + 2 + 1 + 1 + 2] = 1;

        Should.Throw<InvalidDataException>(() => Jpeg2000Decoder.Decode(bytes))
            .Message.ShouldContain("component transform");
    }

    /// <summary>
    /// The fixtures with several layers really have them, and with a grid of code-blocks under
    /// each band, which is what gives the tag trees state to carry between layers. Without this a
    /// regeneration that came out single-layer would leave hazard 5 untested and every test green.
    /// </summary>
    [Theory]
    [InlineData("layers3-noise64", 3)]
    [InlineData("layers3-struct64", 3)]
    [InlineData("rgb-layers2-struct64", 2)]
    public void LayeredFixtures_HaveTheirLayers(string name, int layers)
    {
        var header = CodestreamReader.Read(Fixture(name));

        header.Layers.ShouldBe(layers);
        header.Cod.CodeBlockWidthExponent.ShouldBeLessThan(6);
    }

    private static ushort[] Plane(ushort[] interleaved, int component) =>
        interleaved.Where((_, i) => i % 3 == component).ToArray();

    /// <summary>A marker segment <c>FF</c><paramref name="marker"/>: a length that counts itself, then the body.</summary>
    private static byte[] Segment(byte marker, byte[] body)
    {
        var segment = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(segment, (ushort)(0xFF00 | marker));
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(body.Length + 2));
        body.CopyTo(segment.AsSpan(4));
        return segment;
    }

    /// <summary>
    /// The codestream with <paramref name="segments"/> added at the end of the main header. Nothing
    /// after them needs adjusting: Psot counts from its own SOT marker, and these fixtures carry no
    /// TLM index that would record the old offsets.
    /// </summary>
    private static byte[] InsertBeforeFirstSot(byte[] codestream, params byte[][] segments)
    {
        var at = MainHeaderOffset(codestream, 0x90);
        return [.. codestream[..at], .. segments.SelectMany(s => s), .. codestream[at..]];
    }

    /// <summary>The body of the first main-header segment with this marker, without its length.</summary>
    private static byte[] MainHeaderSegment(byte[] codestream, byte marker)
    {
        var at = MainHeaderOffset(codestream, marker);
        var length = BinaryPrimitives.ReadUInt16BigEndian(codestream.AsSpan(at + 2));
        return codestream[(at + 4)..(at + 2 + length)];
    }

    /// <summary>
    /// Where the first marker <c>FF</c><paramref name="marker"/> starts, walking the main header by
    /// segment lengths rather than scanning for the bytes, which a segment body can contain.
    /// </summary>
    private static int MainHeaderOffset(byte[] codestream, byte marker)
    {
        var at = 2; // past SOC
        while (true)
        {
            codestream[at].ShouldBe((byte)0xFF);
            if (codestream[at + 1] == marker) return at;
            codestream[at + 1].ShouldNotBe((byte)0x90, $"no FF{marker:X2} in the main header");

            at += 2 + BinaryPrimitives.ReadUInt16BigEndian(codestream.AsSpan(at + 2));
        }
    }
}
