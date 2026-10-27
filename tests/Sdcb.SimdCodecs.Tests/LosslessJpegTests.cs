using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Unit tests for the hand-written SOF3 lossless JPEG decoder. The suite feeds
/// the decoder hand-crafted minimal bitstreams that exercise the predictors and
/// Huffman path without depending on external fixtures. Real camera raw strips
/// (e.g. Canon CR2 IFD3) are too large to commit and are not exercised here.
/// </summary>
public sealed class LosslessJpegTests
{
    [Fact]
    public void Predictor1_AllZeroDifferences_FillsImageWithDefaultPredictor()
    {
        // Hand-crafted 4x2 8-bit single-component lossless JPEG. Huffman table
        // has one code of length 1 mapping to symbol 0 (ssss=0, diff=0). The
        // entropy segment is therefore 8 zero bits = one 0x00 byte. Default
        // predictor for 8-bit / Pt=0 is 2^(8-0-1) = 128, propagated by the
        // predictor-1 chain across all subsequent pixels.
        var jpeg = BuildSyntheticLosslessJpeg(
            width: 4,
            height: 2,
            precision: 8,
            predictor: 1,
            pointTransform: 0,
            // One DC table: bits=[1,0,...], huffval=[0]. 1-bit code "0" = symbol 0.
            bits: new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            huffval: new byte[] { 0 },
            // Entropy: 8 samples × 1 bit each = 8 bits = single 0x00 byte.
            entropyBytes: new byte[] { 0x00 });

        var result = LosslessJpeg.FromMemory(jpeg);
        result.Width.ShouldBe(4);
        result.Height.ShouldBe(2);
        result.Precision.ShouldBe(8);
        result.Components.ShouldBe(1);
        result.Samples.Length.ShouldBe(8);
        // Every sample collapses to the default predictor (128).
        foreach (var s in result.Samples)
            s.ShouldBe((ushort)128);
    }

    [Fact]
    public void Predictor1_DiffsModulatePredictorChain()
    {
        // 4×2 single-component 8-bit lossless JPEG, predictor 1. Huffman
        // table: symbol 0 → "0" (1 bit), symbol 3 → "10" (2 bits).
        // Encoded sample-by-sample:
        //   px0: ssss=0 "0"                 → diff=0, sample = 128 (default)
        //   px1: ssss=3 "10" + diff bits "100" (+4)
        //                                   → sample = 128 + 4 = 132
        //   px2,px3: ssss=0 "0"             → sample = 132, 132 (predictor=Ra)
        //   px4 (row 1 col 0): ssss=0       → predictor = Rb = row0[0] = 128
        //   px5-px7: ssss=0                 → 128, 128, 128
        // Bit sequence MSB-first: 0 10 100 0 0 0 0 0 0 0  (= 11 bits)
        //   byte 0 = 0101 0000 = 0x50  (bits b7..b0 = 0,1,0,1,0,0,0,0)
        //   byte 1 = 000_ _____ = 0x00 (last 3 bits + 5 zero pad)
        var jpeg = BuildSyntheticLosslessJpeg(
            width: 4,
            height: 2,
            precision: 8,
            predictor: 1,
            pointTransform: 0,
            bits: new byte[] { 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            huffval: new byte[] { 0, 3 },
            entropyBytes: new byte[] { 0x50, 0x00 });

        var result = LosslessJpeg.FromMemory(jpeg);
        // Row 0: 128, 132, 132, 132.   Row 1: 128, 128, 128, 128.
        result.Samples.ShouldBe(new ushort[] { 128, 132, 132, 132, 128, 128, 128, 128 });
    }

    [Fact]
    public void SOF0_BaselineJpeg_ThrowsHelpfully()
    {
        // FF D8 + FF C0 ... — SOF0 marker. We don't care about the rest;
        // the parser must reject SOF0 before reading further.
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x08, 0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01 };
        var ex = Should.Throw<InvalidDataException>(() => LosslessJpeg.FromMemory(bytes));
        ex.Message.ShouldContain("baseline/progressive");
    }

    [Fact]
    public void NoSOI_ThrowsImmediately()
    {
        Should.Throw<InvalidDataException>(() => LosslessJpeg.FromMemory(new byte[] { 0x00, 0x00 }));
    }

    // -----------------------------------------------------------------
    // Synthetic-bitstream builder for the predictor unit tests
    // -----------------------------------------------------------------

    /// <summary>
    /// Build a minimal SOF3 single-component lossless JPEG bitstream with the
    /// supplied Huffman table + entropy bytes. Only one DC Huffman table (Th=0)
    /// is emitted; the scan references it. Width/height/precision/predictor are
    /// substituted directly into the SOF3 / SOS segments.
    /// </summary>
    private static byte[] BuildSyntheticLosslessJpeg(
        int width, int height, int precision, int predictor, int pointTransform,
        byte[] bits, byte[] huffval, byte[] entropyBytes)
    {
        using var ms = new MemoryStream();
        // SOI
        ms.WriteByte(0xFF); ms.WriteByte(0xD8);

        // DHT: marker, length, [TcTh + BITS(16) + HUFFVAL(n)]
        ms.WriteByte(0xFF); ms.WriteByte(0xC4);
        var dhtBody = 1 + 16 + huffval.Length;
        WriteBE16(ms, (ushort)(dhtBody + 2));
        ms.WriteByte(0x00); // TcTh: DC table 0
        ms.Write(bits, 0, 16);
        ms.Write(huffval, 0, huffval.Length);

        // SOF3: precision (1) + Y (2) + X (2) + Nf (1) + Nf×(Ci,HV,Tq)
        ms.WriteByte(0xFF); ms.WriteByte(0xC3);
        WriteBE16(ms, 8 + 3);             // length = 8 + 3*Nf with Nf=1
        ms.WriteByte((byte)precision);
        WriteBE16(ms, (ushort)height);
        WriteBE16(ms, (ushort)width);
        ms.WriteByte(0x01);               // Nf=1
        ms.WriteByte(0x01);               // Ci=1
        ms.WriteByte(0x11);               // H=1, V=1
        ms.WriteByte(0x00);               // Tq (ignored)

        // SOS: Ns + Ns*(Cs,TdTa) + Ss + Se + Ah/Al
        ms.WriteByte(0xFF); ms.WriteByte(0xDA);
        WriteBE16(ms, 6 + 2);             // length = 6 + 2*Ns with Ns=1
        ms.WriteByte(0x01);               // Ns=1
        ms.WriteByte(0x01);               // Cs=1
        ms.WriteByte(0x00);               // TdTa: DC table 0
        ms.WriteByte((byte)predictor);    // Ss
        ms.WriteByte(0x00);               // Se=0
        ms.WriteByte((byte)(pointTransform & 0x0F));

        // Entropy data
        ms.Write(entropyBytes, 0, entropyBytes.Length);

        // EOI
        ms.WriteByte(0xFF); ms.WriteByte(0xD9);
        return ms.ToArray();
    }

    private static void WriteBE16(MemoryStream s, ushort v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)(v & 0xFF));
    }
}
