using System;
using System.IO;

namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// The packet-header bit reader of T.800 B.10.1 — MSB-first bits with the
/// codestream's bit-stuffing rule.
/// <para>
/// The rule: whenever the byte just consumed was <c>0xFF</c>, the next byte
/// supplies only <b>seven</b> bits, its most significant bit being a stuffed
/// zero. That is what keeps a packet header from ever containing a two-byte
/// sequence in the marker range, so a parser scanning for <c>0xFF90</c> and
/// friends cannot be fooled by header data. It is also the single easiest thing
/// to leave out, and leaving it out desynchronises only on headers that happen
/// to contain an <c>0xFF</c> — so it survives small fixtures and fails on real
/// images.
/// </para>
/// </summary>
internal ref struct PacketBitReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;
    private int _current;
    private int _bitsLeft;

    /// <summary>How many bytes have been consumed, after <see cref="AlignToByte"/>.</summary>
    public readonly int Position => _position;

    /// <summary>Reads one bit, MSB first.</summary>
    public int ReadBit()
    {
        if (_bitsLeft == 0)
        {
            if (_position >= _data.Length)
                throw new InvalidDataException("JPEG 2000: packet header ran off the end of the tile-part.");

            // The stuffing test is on the byte we ALREADY consumed, not the one
            // about to be read.
            _bitsLeft = _current == 0xFF ? 7 : 8;
            _current = _data[_position++];
        }

        _bitsLeft--;
        return (_current >> _bitsLeft) & 1;
    }

    /// <summary>Reads <paramref name="count"/> bits as an unsigned big-endian value.</summary>
    public int ReadBits(int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++) value = (value << 1) | ReadBit();
        return value;
    }

    /// <summary>
    /// Ends the packet header (T.800 B.10.1): discard the rest of the current
    /// byte, and if that byte was <c>0xFF</c> skip the stuffed byte that must
    /// follow it. Missing the second half leaves the body pointer one byte
    /// early, which shows up as a code-block whose first coded byte is garbage.
    /// </summary>
    public void AlignToByte()
    {
        _bitsLeft = 0;
        if (_current == 0xFF)
        {
            if (_position >= _data.Length)
                throw new InvalidDataException("JPEG 2000: packet header ends on a stuffed 0xFF with no byte after it.");

            _position++;
            _current = 0;
        }
    }
}

/// <summary>
/// The tag tree decoder of T.800 B.10.2 — the run-length-ish code tier-2 uses
/// for two per-precinct quantities: which layer first includes each code-block,
/// and how many leading magnitude bit-planes each code-block has that are all
/// zero.
/// <para>
/// A tag tree is a quadtree over the code-blocks of a precinct whose every node
/// holds the minimum of its children. Decoding a leaf walks root-to-leaf reading
/// unary increments, and each node's partial knowledge is <b>kept</b> — a later
/// leaf sharing an ancestor pays only for what that ancestor has not already
/// revealed.
/// </para>
/// <para>
/// <b>Hazard 5.</b> That kept state spans the packets of successive quality
/// layers, not just the leaves within one packet. Re-creating the trees per
/// packet decodes layer 0 correctly and every later layer wrongly, and a
/// single-layer fixture cannot tell the difference — which is why rung 1 owning
/// this class does not mean rung 1 has tested it. That is rung 3's job, and it
/// needs a multi-layer fixture built for the purpose.
/// </para>
/// </summary>
internal sealed class TagTree
{
    private readonly int[] _levelOffsets;
    private readonly int[] _levelWidths;
    private readonly int _levels;
    private readonly int[] _values;
    private readonly bool[] _known;
    private readonly int _width;

    /// <summary>Builds a tree over a <paramref name="width"/> x <paramref name="height"/> grid of leaves.</summary>
    public TagTree(int width, int height)
    {
        _width = width;

        // Level 0 is the leaves; each level up halves both dimensions, rounding
        // up, until a single root node.
        var levels = 1;
        for (int w = width, h = height; w > 1 || h > 1; levels++)
        {
            w = (w + 1) / 2;
            h = (h + 1) / 2;
        }

        _levels = levels;
        _levelOffsets = new int[levels];
        _levelWidths = new int[levels];

        var total = 0;
        var levelWidth = width;
        var levelHeight = height;
        for (var level = 0; level < levels; level++)
        {
            _levelOffsets[level] = total;
            _levelWidths[level] = levelWidth;
            total += levelWidth * levelHeight;
            levelWidth = (levelWidth + 1) / 2;
            levelHeight = (levelHeight + 1) / 2;
        }

        _values = new int[total];
        _known = new bool[total];
    }

    /// <summary>The lower bound currently known for a leaf; exact once resolved.</summary>
    public int Value(int x, int y) => _values[_levelOffsets[0] + y * _width + x];

    /// <summary>
    /// Reads bits until the leaf's value is either known exactly or known to be
    /// at least <paramref name="threshold"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the value is now known exactly and is below
    /// <paramref name="threshold"/>; <c>false</c> when all that has been
    /// established is that it is <paramref name="threshold"/> or more.
    /// </returns>
    public bool Decode(ref PacketBitReader reader, int x, int y, int threshold)
    {
        // Walk root to leaf. A node's value can never be below its parent's, so
        // the parent's bound seeds the child's -- that inheritance is the whole
        // economy of the code.
        var lowerBound = 0;
        for (var level = _levels - 1; level >= 0; level--)
        {
            var index = _levelOffsets[level] + (y >> level) * _levelWidths[level] + (x >> level);

            if (_values[index] < lowerBound) _values[index] = lowerBound;

            while (!_known[index] && _values[index] < threshold)
            {
                if (reader.ReadBit() != 0) _known[index] = true;
                else _values[index]++;
            }

            lowerBound = _values[index];

            // Undetermined and already at the threshold: nothing more can be
            // said this call, and crucially nothing more may be READ -- the
            // encoder stopped emitting bits at exactly this point.
            if (!_known[index]) return false;
        }

        return true;
    }
}
