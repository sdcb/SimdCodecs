using Sdcb.SimdCodecs.Heif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Malformed and hostile HEIF input must fail with <see cref="InvalidDataException"/> — never hang, recurse
/// without bound, allocate without bound, or surface <see cref="OverflowException"/> /
/// <see cref="OutOfMemoryException"/> (AGENTS.md, "Untrusted input").
/// </summary>
public sealed class HeifRobustnessTests
{
    private const int MutationsPerFixture = 64;

    private static readonly string[] Fixtures =
    [
        "synth-screen-testsrc2-1280x720-main8.heic",
        "synth-photo-fuji-1600x900-alpha.heic",
        "hevc-main-3072x4096-grid.heic",
    ];

    public static IEnumerable<object[]> FixtureCases() => Fixtures.Select(static f => new object[] { f });

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void TruncationsFailCleanly(string fixture)
    {
        byte[] source = HeifTests.ReadFixture(fixture);
        // Every prefix of the header region, then a geometric sample of the payload.
        var lengths = new List<int>();
        for (int length = 0; length < Math.Min(source.Length, 4096); length++) lengths.Add(length);
        for (long length = 4096; length < source.Length; length = length * 3 / 2 + 1) lengths.Add((int)length);
        foreach (int length in lengths)
        {
            CheckCandidate(source.AsSpan(0, length).ToArray(), fixture, $"truncated at {length}");
        }
    }

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void MutationsFailCleanly(string fixture)
    {
        byte[] source = HeifTests.ReadFixture(fixture);
        var random = new Random(0x5eed_4e1f);
        // Mutations in the metadata region are the interesting ones: a damaged 'iloc' offset, 'ipma'
        // association or grid header must be caught by a bounds check, not crash the process.
        int metadataEnd = Math.Min(source.Length, 8192);
        for (int mutation = 0; mutation < MutationsPerFixture; mutation++)
        {
            byte[] changed = source.ToArray();
            int offset = random.Next(metadataEnd);
            changed[offset] = (byte)random.Next(256);
            CheckCandidate(changed, fixture, $"byte {offset} = 0x{changed[offset]:X2}");
        }
    }

    [Fact]
    public void DeepBoxNestingFailsCleanly()
    {
        // A 'meta' full of nested 'iprp' boxes would recurse forever without a depth cap.
        var builder = new MemoryStream();
        void WriteBox(string type, Action<MemoryStream> body)
        {
            using var inner = new MemoryStream();
            body(inner);
            Span<byte> header = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, (int)(inner.Length + 8));
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(header[4..]);
            builder.Write(header);
            inner.WriteTo(builder);
        }

        WriteBox("ftyp", stream =>
        {
            stream.Write("heic"u8);
            stream.Write([0, 0, 0, 0]);
            stream.Write("mif1"u8);
        });
        WriteBox("meta", stream =>
        {
            stream.Write([0, 0, 0, 0]); // version + flags
            for (int depth = 0; depth < 64; depth++)
            {
                long start = stream.Position;
                stream.Write(new byte[8]);
                if (depth > 0)
                {
                    stream.Seek(start, SeekOrigin.Begin);
                    int size = (int)(stream.Length - start);
                    stream.WriteByte((byte)(size >> 24));
                    stream.WriteByte((byte)(size >> 16));
                    stream.WriteByte((byte)(size >> 8));
                    stream.WriteByte((byte)size);
                    stream.Write("iprp"u8);
                }
            }
        });

        Should.Throw<InvalidDataException>(() => HeifDecoder.Decode(builder.ToArray()));
    }

    [Fact]
    public void EmptyAndNonsenseInputFailsCleanly()
    {
        Should.Throw<InvalidDataException>(() => HeifDecoder.Decode([]));
        Should.Throw<InvalidDataException>(() => HeifDecoder.Decode(new byte[64]));
        Should.Throw<InvalidDataException>(() => HeifDecoder.ReadInfo("not a heif file at all"u8));
    }

    [Fact]
    public void IlocExtentOutsideTheFileFailsCleanly()
    {
        byte[] source = HeifTests.ReadFixture("synth-screen-testsrc2-1280x720-main8.heic");
        byte[] changed = (byte[])source.Clone();
        // The 'iloc' extent length of the single item is the last 4-byte field of the item's entry; overwriting
        // its top byte with 0x7F makes it far larger than the file.
        int iloc = IndexOf(changed, "iloc"u8);
        iloc.ShouldBeGreaterThan(0);
        // version(4) sizes(2) count(2) item_id(2) data_ref(2) base_offset(4) extent_count(2) offset(4) length(4)
        int lengthField = iloc + 4 + 4 + 2 + 2 + 2 + 4 + 2 + 4;
        changed[lengthField] = 0x7F;
        Should.Throw<InvalidDataException>(() => HeifDecoder.Decode(changed));
    }

    private static void CheckCandidate(byte[] data, string fixture, string description)
    {
        try
        {
            using var image = HeifDecoder.Decode(data);
            // A mutation may legitimately still decode; it must then be a plausible image.
            image.Width.ShouldBeGreaterThan(0);
            image.Height.ShouldBeGreaterThan(0);
            image.Rgba.Length.ShouldBe(image.Width * image.Height * 4);
        }
        catch (InvalidDataException)
        {
        }
        catch (Exception e)
        {
            Assert.Fail($"{fixture} ({description}) threw {e.GetType().Name}: {e.Message}");
        }
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle)) return i;
        }
        return -1;
    }
}
