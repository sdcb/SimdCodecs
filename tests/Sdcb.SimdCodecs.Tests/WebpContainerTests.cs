using System.Buffers.Binary;
using Sdcb.SimdCodecs.Webp;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

public sealed class WebpContainerTests
{
    [Fact]
    public void ReadInfo_ReportsLosslessHeaderDimensionsAndAlpha()
    {
        WebpInfo info = WebpDecoder.ReadInfo(BuildFile(
            ("VP8L", BuildVp8L(9, 7, hasAlpha: true))));

        info.ShouldBe(new WebpInfo(9, 7, true, WebpFormat.Lossless, 1));
    }

    [Fact]
    public void ReadInfo_UsesExtendedCanvasAndCountsAnimationFrames()
    {
        byte[] vp8x = new byte[10];
        vp8x[0] = 0x12;
        WriteUInt24(vp8x.AsSpan(4), 16);
        WriteUInt24(vp8x.AsSpan(7), 8);
        byte[] data = BuildFile(
            ("VP8X", vp8x),
            ("ANIM", new byte[6]),
            ("ANMF", BuildAnimationFrame(BuildVp8L(9, 7, hasAlpha: false), 9, 7)));

        WebpDecoder.ReadInfo(data).ShouldBe(new WebpInfo(17, 9, true, WebpFormat.Animated, 1));
    }

    [Fact]
    public void TryReadInfo_ReturnsFalseForMalformedContainers()
    {
        WebpDecoder.TryReadInfo("not a webp"u8, out _).ShouldBeFalse();
        WebpDecoder.TryReadInfo(BuildFile(("VP8L", [0x2f, 0, 0])), out _).ShouldBeFalse();
    }

    [Fact]
    public void ReadInfoRejectsAnimationFramesOutsideCanvas()
    {
        byte[] vp8x = new byte[10];
        vp8x[0] = 0x12;
        WriteUInt24(vp8x.AsSpan(4), 3);
        WriteUInt24(vp8x.AsSpan(7), 3);
        byte[] data = BuildFile(
            ("VP8X", vp8x),
            ("ANIM", new byte[6]),
            ("ANMF", BuildAnimationFrame(BuildVp8L(1, 1, hasAlpha: false), 1, 1, x: 6)));

        Should.Throw<InvalidDataException>(() => WebpDecoder.ReadInfo(data));
    }

    [Fact]
    public void ReadInfoRejectsCanvasAbovePixelLimit()
    {
        byte[] data = BuildFile(("VP8L", BuildVp8L(8193, 8193, hasAlpha: false)));

        Should.Throw<InvalidDataException>(() => WebpDecoder.ReadInfo(data));
    }

    [Fact]
    public void ReadInfoRejectsAnimationChunkWithoutAnimationFlag()
    {
        byte[] data = BuildFile(
            ("VP8X", new byte[10]),
            ("ANIM", new byte[6]),
            ("VP8L", BuildVp8L(1, 1, hasAlpha: false)));

        Should.Throw<InvalidDataException>(() => WebpDecoder.ReadInfo(data));
    }

    [Fact]
    public void TryDecodeTo_ReturnsFalseForUnsupportedLaterRungs()
    {
        WebpDecoder.TryDecodeTo(BuildFile(
            ("VP8L", BuildVp8L(2, 2, hasAlpha: false))), new byte[16]).ShouldBeFalse();
    }

    [Fact]
    public void LossyFixture_DecodesToOpaqueRgba()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "webp", "tiny.webp"));

        WebpImage image = WebpDecoder.Decode(file);

        image.Width.ShouldBe(2);
        image.Height.ShouldBe(2);
        image.HasAlpha.ShouldBeFalse();
        image.Rgba.Length.ShouldBe(16);
        for (int index = 3; index < image.Rgba.Length; index += 4)
        {
            image.Rgba[index].ShouldBe((byte)255);
        }
    }

    // libwebp refuses a still VP8X whose canvas differs from its bitstream (BITSTREAM_ERROR). Accepting
    // it padded a 3x7 image out to the declared canvas, or a 1x1 one to 8192x8192 (256 MiB from ~56
    // bytes) through the facade.
    [Theory]
    [InlineData("lossless-3x7.webp", 64, 64)]
    [InlineData("lossless-3x7.webp", 3, 8)]
    [InlineData("lossless-1x1.webp", 8192, 8192)]
    [InlineData("tiny.webp", 4, 2)]
    public void StillVp8xCanvasThatDisagreesWithTheBitstream_IsRefused(string fixture, int width, int height)
    {
        byte[] data = WrapStillInVp8x(fixture, width, height);

        Should.Throw<InvalidDataException>(() => WebpDecoder.ReadInfo(data));
        Should.Throw<InvalidDataException>(() => WebpDecoder.Decode(data));
        WebpDecoder.TryReadInfo(data, out _).ShouldBeFalse();
        SimdCodecs.TryDecode(data, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("lossless-3x7.webp")]
    [InlineData("tiny.webp")]
    public void StillVp8xCanvasThatMatchesTheBitstream_StillDecodes(string fixture)
    {
        byte[] plain = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp", fixture));
        WebpInfo info = WebpDecoder.ReadInfo(plain);

        WebpImage image = WebpDecoder.Decode(WrapStillInVp8x(fixture, info.Width, info.Height));

        image.Rgba.ShouldBe(WebpDecoder.Decode(plain).Rgba);
    }

    private static byte[] WrapStillInVp8x(string fixture, int width, int height)
    {
        byte[] plain = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp", fixture));
        string type = System.Text.Encoding.ASCII.GetString(plain, 12, 4);
        type.ShouldBeOneOf("VP8 ", "VP8L");
        byte[] bitstream = plain.AsSpan(20, BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(16))).ToArray();

        byte[] vp8x = new byte[10];
        WriteUInt24(vp8x.AsSpan(4), width - 1);
        WriteUInt24(vp8x.AsSpan(7), height - 1);
        return BuildFile(("VP8X", vp8x), (type, bitstream));
    }

    // Pins what DecodeTo's documentation promises about stride: rows land at stride offsets, the
    // pixels match a tightly packed decode, and the padding after each row is never written.
    [Theory]
    [InlineData("testsrc2-320x240-q75.webp")]
    [InlineData("lossy-alpha-q50.webp")]
    [InlineData("lossless-z6.webp")]
    [InlineData("anim-img2webp-mixed.webp")]
    public void DecodeTo_WithStride_WritesRowsAndLeavesPaddingUntouched(string fixture)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp", fixture));
        WebpInfo info = WebpDecoder.ReadInfo(file);
        int rowBytes = info.Width * 4;
        int stride = rowBytes + 12;
        byte[] packed = new byte[rowBytes * info.Height];
        WebpDecoder.DecodeTo(file, packed);

        byte[] strided = new byte[(info.Height - 1) * stride + rowBytes];
        Array.Fill(strided, (byte)0xA5);
        WebpDecoder.DecodeTo(file, strided, stride);

        for (int row = 0; row < info.Height; row++)
        {
            strided.AsSpan(row * stride, rowBytes).ToArray()
                .ShouldBe(packed.AsSpan(row * rowBytes, rowBytes).ToArray(), $"row {row}");
            if (row < info.Height - 1)
            {
                strided.AsSpan(row * stride + rowBytes, stride - rowBytes).ToArray()
                    .ShouldAllBe(b => b == 0xA5, $"padding after row {row}");
            }
        }

        Should.Throw<ArgumentException>(() => WebpDecoder.DecodeTo(file, strided, rowBytes - 1));
        Should.Throw<ArgumentException>(() => WebpDecoder.DecodeTo(file, strided.AsSpan(1).ToArray(), stride));
        WebpDecoder.TryDecodeTo(file, strided.AsSpan(1).ToArray(), stride).ShouldBeFalse();
    }

    // A tightly packed destination is used as the lossless decoder's ARGB working buffer; it need
    // not be 4-byte aligned.
    [Theory]
    [InlineData("lossless-z6.webp")]
    [InlineData("lossless-palette-2.webp")]
    public void DecodeTo_LosslessIntoMisalignedTightDestination_MatchesDecode(string fixture)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp", fixture));
        WebpImage image = WebpDecoder.Decode(file);
        byte[] buffer = new byte[image.Rgba.Length + 3];
        WebpDecoder.DecodeTo(file, buffer.AsSpan(1, image.Rgba.Length));
        buffer.AsSpan(1, image.Rgba.Length).SequenceEqual(image.Rgba).ShouldBeTrue();
        buffer[0].ShouldBe((byte)0);
        buffer[^2].ShouldBe((byte)0);
    }

    private static byte[] BuildVp8L(int width, int height, bool hasAlpha)
    {
        var data = new byte[5];
        data[0] = 0x2f;
        uint bits = (uint)(width - 1) | (uint)(height - 1) << 14;
        if (hasAlpha) bits |= 1u << 28;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1), bits);
        return data;
    }

    private static byte[] BuildFile(params (string Type, byte[] Data)[] chunks)
    {
        using var output = new MemoryStream();
        output.Write("RIFF"u8);
        output.Write(new byte[4]);
        output.Write("WEBP"u8);
        foreach ((string type, byte[] payload) in chunks)
        {
            output.Write(System.Text.Encoding.ASCII.GetBytes(type));
            byte[] length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)payload.Length);
            output.Write(length);
            output.Write(payload);
            if ((payload.Length & 1) != 0) output.WriteByte(0);
        }

        byte[] file = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(file.Length - 8));
        return file;
    }

    private static byte[] BuildAnimationFrame(byte[] image, int width, int height, int x = 0, int y = 0)
    {
        using var output = new MemoryStream();
        byte[] header = new byte[16];
        WriteUInt24(header, x / 2);
        WriteUInt24(header.AsSpan(3), y / 2);
        WriteUInt24(header.AsSpan(6), width - 1);
        WriteUInt24(header.AsSpan(9), height - 1);
        output.Write(header);
        output.Write("VP8L"u8);
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)image.Length);
        output.Write(length);
        output.Write(image);
        if ((image.Length & 1) != 0) output.WriteByte(0);
        return output.ToArray();
    }

    private static void WriteUInt24(Span<byte> data, int value)
    {
        data[0] = (byte)value;
        data[1] = (byte)(value >> 8);
        data[2] = (byte)(value >> 16);
    }
}
