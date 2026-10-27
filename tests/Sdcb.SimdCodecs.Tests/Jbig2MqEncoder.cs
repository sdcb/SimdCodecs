using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jbig2;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The MQ arithmetic <em>encoder</em> of ITU-T T.88 Annex E — a test-only
/// counterpart to the shipped <see cref="MqDecoder"/>.
/// <para>
/// It exists for two reasons. First, running the Annex H.2 conformance sequence
/// through the encoder as well as the decoder pins the coder from both
/// directions against the same published vector. Second, it lets the generic
/// region tests build genuine arithmetically-coded bitstreams instead of
/// hand-assembling bytes — hand-crafting an MQ codestream by hand the way
/// <c>LosslessJpegTests</c> hand-crafts Huffman bits is not practical, because
/// every bit's encoding depends on the adaptive state left by all the bits
/// before it.
/// </para>
/// <para>
/// It reads the probability table out of <see cref="MqDecoder"/> rather than
/// carrying its own copy, so the H.2 vector validates the shipped table (a wrong
/// <c>Qe</c> row would fail in both directions), not a private duplicate that
/// happens to agree with it.
/// </para>
/// <para>
/// Encoding JBIG2 is a stated non-goal of the shipped package; this lives in the
/// test project and stays there.
/// </para>
/// </summary>
internal sealed class Jbig2MqEncoder
{
    // A dummy predecessor byte so BYTEOUT's carry-propagation step ("increment
    // the previously emitted byte") always has somewhere to go — the spec's
    // INITENC points BP one byte before the output buffer for the same reason.
    // Dropped by Flush; asserted untouched, since a carry reaching it would mean
    // the model is wrong rather than the stream needing one more byte.
    private readonly List<byte> _bytes = [0x00];

    private uint _a = 0x8000;
    private uint _c;
    private int _ct = 12;

    /// <summary>
    /// ENCODE (T.88 E.3.1): codes one binary decision against the adaptive
    /// context stored at <paramref name="index"/>, updating it in place.
    /// </summary>
    public void Encode(Span<byte> contexts, int index, int bit)
    {
        var packed = contexts[index];
        var state = MqDecoder.StateAt(packed >> 1);
        var mps = (uint)(packed & 1);
        uint qe = state.Qe;

        if ((uint)bit == mps)
        {
            // CODEMPS (T.88 E.3.2).
            _a -= qe;
            if ((_a & 0x8000) == 0)
            {
                if (_a < qe) _a = qe;
                else _c += qe;

                contexts[index] = (byte)(((uint)state.Nmps << 1) | mps);
                Renormalize();
            }
            else
            {
                _c += qe;
            }
        }
        else
        {
            // CODELPS (T.88 E.3.2).
            _a -= qe;
            if (_a < qe) _c += qe;
            else _a = qe;

            if (state.Switch != 0) mps = 1 - mps;
            contexts[index] = (byte)(((uint)state.Nlps << 1) | mps);
            Renormalize();
        }
    }

    /// <summary>
    /// FLUSH (T.88 E.3.8): sets as many trailing bits to 1 as the interval
    /// allows, drains the code register, and appends the <c>FF AC</c>
    /// terminator. Returns the finished codestream.
    /// </summary>
    public byte[] Flush()
    {
        // SETBITS.
        var temp = _c + _a;
        _c |= 0xFFFF;
        if (_c >= temp) _c -= 0x8000;

        _c <<= _ct;
        ByteOut();
        _c <<= _ct;
        ByteOut();

        if (_bytes[^1] != 0xFF) _bytes.Add(0xFF);
        _bytes.Add(0xAC);

        if (_bytes[0] != 0x00)
            throw new InvalidOperationException("MQ encoder: a carry propagated past the first output byte.");

        _bytes.RemoveAt(0);
        return [.. _bytes];
    }

    /// <summary>RENORME (T.88 E.3.3) — shift first, then emit, the mirror image of the decoder's order.</summary>
    private void Renormalize()
    {
        do
        {
            _a <<= 1;
            _c <<= 1;
            _ct--;
            if (_ct == 0) ByteOut();
        }
        while ((_a & 0x8000) == 0);
    }

    /// <summary>BYTEOUT (T.88 E.3.7): emit a byte, stuffing after <c>0xFF</c> and propagating carries.</summary>
    private void ByteOut()
    {
        if (_bytes[^1] == 0xFF)
        {
            // Previous byte was 0xFF: emit only 7 bits so the pair can never look
            // like a marker.
            _bytes.Add((byte)(_c >> 20));
            _c &= 0xFFFFF;
            _ct = 7;
        }
        else if ((_c & 0x8000000) == 0)
        {
            _bytes.Add((byte)(_c >> 19));
            _c &= 0x7FFFF;
            _ct = 8;
        }
        else
        {
            // Carry out of the code register: bump the byte already emitted.
            _bytes[^1] = (byte)(_bytes[^1] + 1);
            if (_bytes[^1] == 0xFF)
            {
                _c &= 0x7FFFFFF;
                _bytes.Add((byte)(_c >> 20));
                _c &= 0xFFFFF;
                _ct = 7;
            }
            else
            {
                _bytes.Add((byte)(_c >> 19));
                _c &= 0x7FFFF;
                _ct = 8;
            }
        }
    }
}
