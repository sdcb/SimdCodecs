using System.Buffers.Binary;
using Sdcb.SimdCodecs.Jbig2;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// A minimal, test-only walk over a <c>.jb2</c> file's segment headers.
/// <para>
/// Deliberately separate from the shipped <c>Jbig2Segment</c> parser: these tests
/// assert things <em>about</em> the fixtures — which flags jbig2enc set, where one
/// segment ends and the next begins — and using the parser under test to
/// establish them would make the assertions circular. It is a few dozen lines of
/// T.88 §7.2 either way.
/// </para>
/// </summary>
internal static class Jbig2FixtureReader
{
    /// <param name="Number">Segment number.</param>
    /// <param name="Type">Segment type code (T.88 §7.3).</param>
    /// <param name="Raw">The whole segment, header and data — enough to re-emit it in a different stream.</param>
    /// <param name="Data">Just the data part.</param>
    internal sealed record Segment(uint Number, int Type, byte[] Raw, byte[] Data);

    /// <summary>Walks a sequential <c>.jb2</c> file, or an embedded stream when it has no file header.</summary>
    public static List<Segment> ReadSegments(byte[] file)
    {
        var position = 0;
        if (file.Length >= 8 && file.AsSpan(0, 8).SequenceEqual(Jbig2Decoder.FileSignature))
        {
            var flags = file[8];
            position = 9;
            if ((flags & 0x02) == 0) position += 4;
            if ((flags & 0x01) == 0)
                throw new InvalidDataException("This reader only walks the sequential organization.");
        }

        var segments = new List<Segment>();
        while (position < file.Length)
        {
            var start = position;
            var number = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position));
            position += 4;

            var segmentFlags = file[position++];
            var type = segmentFlags & 0x3F;

            var countByte = file[position];
            int referredCount;
            if ((countByte >> 5) == 7)
            {
                referredCount = (int)(BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position)) & 0x1FFFFFFF);
                position += 4 + (referredCount + 8) / 8;
            }
            else
            {
                referredCount = countByte >> 5;
                position++;
            }

            position += referredCount * (number <= 256 ? 1 : number <= 65536 ? 2 : 4);
            position += (segmentFlags & 0x40) != 0 ? 4 : 1;

            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position));
            position += 4;

            segments.Add(new Segment(number, type, file[start..(position + length)], file[position..(position + length)]));
            position += length;
        }

        return segments;
    }
}
