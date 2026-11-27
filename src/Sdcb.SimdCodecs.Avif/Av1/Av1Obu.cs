namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// AV1 Open Bitstream Unit (OBU) framing parser (§5.3).
/// </summary>
internal readonly record struct Av1Obu(int Type, int TemporalId, int SpatialId, int PayloadOffset, int PayloadLength);

internal static class Av1ObuParser
{
    /// <summary>
    /// Parses all OBUs in an AV1 low-overhead bitstream (as stored in AVIF <c>av01</c> items).
    /// </summary>
    internal static List<Av1Obu> ParseAll(ReadOnlySpan<byte> data)
    {
        var list = new List<Av1Obu>(4);
        int offset = 0;
        while (offset < data.Length)
        {
            byte header = data[offset++];
            if ((header & 0x80) != 0)
                throw new InvalidDataException("AV1 OBU forbidden bit is set.");

            int type = (header >> 3) & 0x0F;
            bool hasExtension = (header & 0x04) != 0;
            bool hasSize = (header & 0x02) != 0;

            int temporalId = 0;
            int spatialId = 0;
            if (hasExtension)
            {
                if (offset >= data.Length)
                    throw new InvalidDataException("Truncated AV1 OBU extension header.");
                byte ext = data[offset++];
                temporalId = (ext >> 5) & 0x07;
                spatialId = (ext >> 3) & 0x03;
            }

            int payloadLength;
            if (hasSize)
            {
                ulong sz = Av1BitReader.ReadLeb128(data, ref offset);
                if (sz > (ulong)(data.Length - offset))
                    throw new InvalidDataException("AV1 OBU payload extends past end of buffer.");
                payloadLength = (int)sz;
            }
            else
            {
                payloadLength = data.Length - offset;
            }

            list.Add(new Av1Obu(type, temporalId, spatialId, offset, payloadLength));
            offset += payloadLength;
        }
        return list;
    }
}
