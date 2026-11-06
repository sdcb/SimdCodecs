using System.Buffers.Binary;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Exif;
using Sdcb.SimdCodecs.Heif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The Exif block of a HEIF file: <see cref="ExifReader.FromHeif"/> reads it as the file records it, while the HEIF
/// decoder's <see cref="ImageMetadata.Exif"/> carries Orientation = 1 because its pixels are already display-oriented
/// (irot / imir applied). Plus the orientation reset itself (<c>ExifOrientation.TryReset</c>,
/// <see cref="ExifWriter.ResetOrientation"/>) on hand-built blocks of both byte orders.
/// </summary>
public sealed class HeifExifTests
{
    private const string ExifFixture = "hevc-main-3072x4096-grid.heic";
    private const string NoExifFixture = "synth-screen-testsrc2-640x360-pq-vui.heic";

    // ---- the orientation reset on hand-built blocks ------------------------------------------------------------

    /// <summary>A TIFF with one IFD0 entry: tag, type, count, and a value field holding a SHORT.</summary>
    private static byte[] Tiff(bool le, ushort tag, ushort type, uint count, ushort value)
    {
        var b = new byte[8 + 2 + 12 + 4];
        void W16(int at, ushort v) { if (le) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v); else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), v); }
        void W32(int at, uint v) { if (le) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v); else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v); }
        b[0] = b[1] = (byte)(le ? 'I' : 'M');
        W16(2, 42);
        W32(4, 8);
        W16(8, 1);
        W16(10, tag);
        W16(12, type);
        W32(14, count);
        W16(18, value);
        return b;
    }

    [Theory]
    [InlineData(true, 6)]
    [InlineData(false, 6)]
    [InlineData(true, 8)]
    [InlineData(false, 3)]
    public void ResetOrientation_sets_one_in_either_byte_order(bool le, ushort original)
    {
        var tiff = Tiff(le, 0x0112, 3, 1, original);
        var reset = ExifWriter.ResetOrientation(tiff);
        reset.ShouldBe(Tiff(le, 0x0112, 3, 1, 1));
        tiff.ShouldBe(Tiff(le, 0x0112, 3, 1, original)); // the input is not modified
        ExifReader.FromTiff(reset).ShouldNotBeNull().Orientation.ShouldBe((ushort?)1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResetOrientation_keeps_a_leading_Exif_signature(bool le)
    {
        byte[] signature = "Exif\0\0"u8.ToArray();
        var signed = signature.Concat(Tiff(le, 0x0112, 3, 1, 6)).ToArray();
        ExifWriter.ResetOrientation(signed).ShouldBe(signature.Concat(Tiff(le, 0x0112, 3, 1, 1)).ToArray());
    }

    private static byte[][] NothingToReset() =>
    [
        Tiff(true, 0x010F, 3, 1, 6),            // another tag
        Tiff(true, 0x0112, 4, 1, 6),            // LONG, not SHORT
        Tiff(false, 0x0112, 3, 2, 6),           // count 2
        Tiff(true, 0x0112, 3, 1, 1),            // already 1
        Tiff(true, 0x0112, 3, 1, 6)[..15],      // cut inside the entry
        Tiff(true, 0x0112, 3, 1, 6)[..9],       // cut inside the entry count
        [],                                     // empty
        "II"u8.ToArray(),                       // header cut short
        "MM\0*\0\0\0\x08\0"u8.ToArray(),        // IFD0 count cut short
        "nope nope nope"u8.ToArray(),           // not TIFF
    ];

    [Fact]
    public void ResetOrientation_returns_an_unchanged_copy_when_there_is_nothing_to_reset()
    {
        foreach (var input in NothingToReset())
        {
            var copy = ExifWriter.ResetOrientation(input);
            copy.ShouldBe(input);
            if (input.Length > 0) copy.ShouldNotBeSameAs(input); // an empty copy is the shared empty array
        }
    }

    [Fact]
    public void TryReset_reports_whether_it_changed_a_byte()
    {
        foreach (var input in NothingToReset())
        {
            var before = input.ToArray();
            Sdcb.SimdCodecs.Exif.ExifOrientation.TryReset(input).ShouldBeFalse();
            input.ShouldBe(before);
            Sdcb.SimdCodecs.Heif.ExifOrientation.TryReset(input).ShouldBeFalse();
            input.ShouldBe(before);
        }

        foreach (var le in new[] { true, false })
        {
            var a = Tiff(le, 0x0112, 3, 1, 5);
            Sdcb.SimdCodecs.Exif.ExifOrientation.TryReset(a).ShouldBeTrue();
            a.ShouldBe(Tiff(le, 0x0112, 3, 1, 1));
            Sdcb.SimdCodecs.Exif.ExifOrientation.TryReset(a).ShouldBeFalse();

            var b = Tiff(le, 0x0112, 3, 1, 7);
            Sdcb.SimdCodecs.Heif.ExifOrientation.TryReset(b).ShouldBeTrue();
            b.ShouldBe(Tiff(le, 0x0112, 3, 1, 1));
        }
    }

    // ---- HEIC files ----------------------------------------------------------------------------------------------
    // The phone fixtures declare an Exif item but its data is empty (the photos' Exif was stripped), so the tests put a
    // block into one: its (offset, length 0) iloc extent is rewritten to point at a payload appended as a 'free' box.

    /// <summary>A TIFF with Make = "Acme" and Orientation, IFD0 in tag order.</summary>
    private static byte[] ExifBlock(bool le, ushort orientation)
    {
        var b = new byte[8 + 2 + 2 * 12 + 4 + 5];
        void W16(int at, ushort v) { if (le) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v); else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), v); }
        void W32(int at, uint v) { if (le) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v); else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v); }
        b[0] = b[1] = (byte)(le ? 'I' : 'M');
        W16(2, 42);
        W32(4, 8);
        W16(8, 2);
        W16(10, 0x010F); W16(12, 2); W32(14, 5); W32(18, 38);   // Make: ASCII, 5 bytes, stored after the IFD
        W16(22, 0x0112); W16(24, 3); W32(26, 1); W16(30, orientation);
        "Acme\0"u8.CopyTo(b.AsSpan(38));
        return b;
    }

    /// <summary>
    /// The file with <paramref name="tiff"/> as its Exif item: the 4-byte offset header (23008-12 A.2.1), the optional
    /// <c>Exif\0\0</c> signature counted in that offset, then the block.
    /// </summary>
    private static byte[] WithExif(byte[] file, byte[] tiff, bool signature = false)
    {
        var items = Sdcb.SimdCodecs.Heif.Isobmff.ItemContainer.Parse(file);
        var item = items.Items.Single(i => i.Type == 0x45786966);
        var extent = item.Extents.Single();
        extent.Length.ShouldBe(0);

        // The extent is (offset, length) as two big-endian 32-bit fields in iloc, inside the meta box at the start.
        var pattern = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(pattern, (uint)extent.Offset);
        var meta = file.AsSpan(0, Math.Min(file.Length, 16384));
        var at = meta.IndexOf(pattern);
        at.ShouldBeGreaterThanOrEqualTo(0, "iloc extent of the Exif item not found with 4-byte fields");
        meta[(at + 1)..].IndexOf(pattern).ShouldBe(-1);

        var payload = new byte[4 + (signature ? 6 : 0) + tiff.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload, signature ? 6u : 0u);
        if (signature) "Exif\0\0"u8.CopyTo(payload.AsSpan(4));
        tiff.CopyTo(payload, 4 + (signature ? 6 : 0));

        var result = new byte[file.Length + 8 + payload.Length];
        file.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(file.Length), (uint)(8 + payload.Length));
        "free"u8.CopyTo(result.AsSpan(file.Length + 4));
        payload.CopyTo(result, file.Length + 8);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(at), (uint)(file.Length + 8));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(at + 4), (uint)payload.Length);
        return result;
    }

    [Fact]
    public void FromHeif_returns_the_recorded_orientation_and_the_decoder_reports_one()
    {
        var rotated = WithExif(HeifTests.ReadFixture(ExifFixture), ExifBlock(true, 6));

        ExifReader.FromHeif(rotated).ShouldNotBeNull().Orientation.ShouldBe((ushort?)6);

        HeifImageDecoder.TryReadMetadata(rotated, out var metadata).ShouldBeTrue();
        metadata.Exif.IsEmpty.ShouldBeFalse();
        ExifReader.FromTiff(metadata.Exif.Span).ShouldNotBeNull().Orientation.ShouldBe((ushort?)1);
    }

    [Fact]
    public void The_decoded_image_metadata_carries_orientation_one()
    {
        var rotated = WithExif(HeifTests.ReadFixture(ExifFixture), ExifBlock(false, 8), signature: true);
        HeifImageDecoder.TryDecode(rotated, out var image, new DecodeOptions { Region = new PixelRect(0, 0, 64, 64) },
            TestContext.Current.CancellationToken).ShouldBeTrue();
        using (image)
        {
            ExifReader.FromTiff(image.Metadata.Exif.Span).ShouldNotBeNull().Orientation.ShouldBe((ushort?)1);
        }
    }

    [Fact]
    public void FromHeif_agrees_with_FromTiff_on_the_decoders_block_except_the_orientation()
    {
        var file = WithExif(HeifTests.ReadFixture(ExifFixture), ExifBlock(false, 3));
        var viaHeif = ExifReader.FromHeif(file).ShouldNotBeNull();
        HeifImageDecoder.TryReadMetadata(file, out var metadata).ShouldBeTrue();
        var viaTiff = ExifReader.FromTiff(metadata.Exif.Span).ShouldNotBeNull();

        viaHeif.Orientation.ShouldBe((ushort?)3);
        viaTiff.Orientation.ShouldBe((ushort?)1);
        (viaHeif with { Orientation = 1, RawTags = viaTiff.RawTags }).ShouldBe(viaTiff);
        viaHeif.RawTags.Keys.Order().ShouldBe(viaTiff.RawTags.Keys.Order());
        foreach (var (tag, value) in viaHeif.RawTags)
        {
            if (tag == 0x0112) continue;
            viaTiff.RawTags[tag].Type.ShouldBe(value.Type);
            viaTiff.RawTags[tag].Count.ShouldBe(value.Count);
            viaTiff.RawTags[tag].Bytes.ShouldBe(value.Bytes);
        }
        viaHeif.Make.ShouldBe("Acme");
    }

    [Fact]
    public void FromBytes_dispatches_a_HEIF_file_to_FromHeif()
    {
        var file = WithExif(HeifTests.ReadFixture(ExifFixture), ExifBlock(true, 5));
        var exif = ExifReader.FromBytes(file).ShouldNotBeNull();
        exif.Make.ShouldBe("Acme");
        exif.Orientation.ShouldBe((ushort?)5);
    }

    [Fact]
    public void A_HEIF_file_without_an_Exif_item_reads_as_null()
    {
        var file = HeifTests.ReadFixture(NoExifFixture);
        ExifReader.FromHeif(HeifTests.ReadFixture(ExifFixture)).ShouldBeNull(); // an Exif item with no data
        ExifReader.FromHeif(file).ShouldBeNull();
        ExifReader.FromBytes(file).ShouldBeNull();
    }

    [Fact]
    public void Malformed_input_reads_as_null()
    {
        var file = HeifTests.ReadFixture(ExifFixture);
        ExifReader.FromHeif([]).ShouldBeNull();
        ExifReader.FromHeif("nonsense"u8).ShouldBeNull();
        ExifReader.FromHeif(file.AsSpan(0, 8)).ShouldBeNull();       // ftyp header only
        ExifReader.FromHeif(file.AsSpan(0, 40)).ShouldBeNull();      // ftyp, no meta
        ExifReader.FromBytes(file.AsSpan(0, 40)).ShouldBeNull();
        // A box whose size claims more than the file holds.
        var huge = file.AsSpan(0, 64).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(huge, 0x7FFFFFF0);
        ExifReader.FromHeif(huge).ShouldBeNull();
        // ftyp with a HEIF brand followed by zeros.
        var junk = new byte[256];
        "\0\0\0\x18ftypheic\0\0\0\0mif1heic"u8.CopyTo(junk);
        ExifReader.FromBytes(junk).ShouldBeNull();
    }

    [Fact]
    public void Mutated_files_never_throw()
    {
        var file = WithExif(HeifTests.ReadFixture(ExifFixture), ExifBlock(true, 5));
        var rng = new Random(20261106);
        var window = Math.Min(file.Length, 8192); // the ftyp / meta boxes
        for (var i = 0; i < 4000; i++)
        {
            var edits = new List<(int At, byte Old)>();
            for (var k = rng.Next(1, 5); k > 0; k--)
            {
                var at = rng.Next(window);
                edits.Add((at, file[at]));
                file[at] = rng.Next(4) == 0 ? (byte)0xFF : (byte)rng.Next(256);
            }
            try
            {
                var length = rng.Next(8) == 0 ? rng.Next(file.Length) : file.Length;
                _ = ExifReader.FromHeif(file.AsSpan(0, length));
                _ = ExifReader.FromBytes(file.AsSpan(0, length));
            }
            finally
            {
                foreach (var (at, old) in edits) file[at] = old;
            }
        }
    }
}
