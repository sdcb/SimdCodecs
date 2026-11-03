namespace Sdcb.SimdCodecs.Heif.Hevc;

/// <summary>
/// The HEVC decoder configuration record of ISO/IEC 14496-15 §8.3.3 (the <c>hvcC</c> property): the NAL unit
/// length size and the parameter-set NAL units an image item is decoded with.
/// </summary>
internal sealed class HevcDecoderConfiguration
{
    internal int LengthSize;
    internal readonly List<byte[]> NalUnits = [];

    /// <summary>Hard cap on parameter-set NAL units in one record.</summary>
    private const int MaxNalUnits = 256;

    internal static HevcDecoderConfiguration Parse(ReadOnlySpan<byte> hvcc)
    {
        // 22 fixed bytes (configurationVersion .. lengthSizeMinusOne), then numOfArrays.
        if (hvcc.Length < 23) throw new InvalidDataException("The 'hvcC' box is truncated.");
        if (hvcc[0] != 1) throw new InvalidDataException($"Unsupported 'hvcC' configurationVersion {hvcc[0]}.");
        var config = new HevcDecoderConfiguration { LengthSize = (hvcc[21] & 3) + 1 };
        if (config.LengthSize == 3) throw new InvalidDataException("An 'hvcC' NAL length size of 3 bytes is not allowed.");

        var arrays = hvcc[22];
        var p = 23;
        for (var i = 0; i < arrays; i++)
        {
            if (p + 3 > hvcc.Length) throw new InvalidDataException("The 'hvcC' box is truncated.");
            var count = (hvcc[p + 1] << 8) | hvcc[p + 2];
            p += 3;
            for (var k = 0; k < count; k++)
            {
                if (p + 2 > hvcc.Length) throw new InvalidDataException("The 'hvcC' box is truncated.");
                var length = (hvcc[p] << 8) | hvcc[p + 1];
                p += 2;
                if (length < 2 || length > hvcc.Length - p) throw new InvalidDataException("An 'hvcC' NAL unit is truncated.");
                if (config.NalUnits.Count >= MaxNalUnits) throw new InvalidDataException("Too many NAL units in 'hvcC'.");
                config.NalUnits.Add(hvcc.Slice(p, length).ToArray());
                p += length;
            }
        }
        return config;
    }

    /// <summary>The SPS of the record (the first one), parsed; used for header-only reads.</summary>
    internal HevcSps ParseSps()
    {
        foreach (var nal in NalUnits)
        {
            if (Nal.Type(nal) == Nal.SpsNut) return HevcSps.Parse(Nal.UnescapeToArray(nal));
        }
        throw new InvalidDataException("The 'hvcC' box has no SPS.");
    }
}

/// <summary>Decodes one HEVC-coded image item: parameter sets from <c>hvcC</c> plus the item's length-prefixed NAL units.</summary>
internal static class HevcImageDecoder
{
    /// <summary>Hard cap on NAL units in one image item.</summary>
    private const int MaxNalUnits = 4096;

    internal static HevcPicture Decode(HevcDecoderConfiguration config, ReadOnlySpan<byte> data)
    {
        HevcSps? sps = null;
        HevcPps? pps = null;
        var slices = new List<(int, byte[])>();

        void Accept(ReadOnlySpan<byte> nal)
        {
            if (nal.Length < 2) throw new InvalidDataException("A NAL unit is shorter than its header.");
            if ((nal[0] & 0x80) != 0) throw new InvalidDataException("forbidden_zero_bit is set.");
            var type = Nal.Type(nal);
            var layerId = ((nal[0] & 1) << 5) | (nal[1] >> 3);
            if (layerId != 0) return; // other layers (e.g. multilayer extensions) are ignored
            switch (type)
            {
                case Nal.SpsNut:
                    sps = HevcSps.Parse(Nal.UnescapeToArray(nal));
                    break;
                case Nal.PpsNut:
                    pps = HevcPps.Parse(Nal.UnescapeToArray(nal));
                    break;
                case <= 21 when type is <= 9 or >= 16:
                    // VCL NAL units: TRAIL/TSA/STSA/RADL/RASL (0..9) and IRAP (16..21).
                    slices.Add((type, Nal.UnescapeToArray(nal)));
                    break;
            }
        }

        foreach (var nal in config.NalUnits) Accept(nal);

        var p = 0;
        var lengthSize = config.LengthSize;
        while (p < data.Length)
        {
            if (data.Length - p < lengthSize) throw new InvalidDataException("A NAL unit length field is truncated.");
            long length = 0;
            for (var i = 0; i < lengthSize; i++) length = (length << 8) | data[p + i];
            p += lengthSize;
            if (length > data.Length - p) throw new InvalidDataException("A NAL unit runs past the end of the item.");
            if (slices.Count >= MaxNalUnits) throw new InvalidDataException("Too many NAL units in one image item.");
            if (length > 0) Accept(data.Slice(p, (int)length));
            p += (int)length;
        }

        if (sps is null) throw new InvalidDataException("The image item has no SPS.");
        if (pps is null) throw new InvalidDataException("The image item has no PPS.");
        if (pps.SpsId != sps.Id) throw new InvalidDataException("The PPS refers to an SPS that is not present.");
        if (slices.Count == 0) throw new InvalidDataException("The image item has no coded slices.");
        // The container checked its geometry (and bounded the allocation) against the hvcC SPS; an in-band SPS
        // may not change the picture size.
        var declared = config.ParseSps();
        if (sps.Width != declared.Width || sps.Height != declared.Height || sps.ConfLeft != declared.ConfLeft ||
            sps.ConfRight != declared.ConfRight || sps.ConfTop != declared.ConfTop || sps.ConfBottom != declared.ConfBottom)
        {
            throw new InvalidDataException("An in-band SPS changes the picture size declared in 'hvcC'.");
        }
        return new HevcFrameDecoder(sps, pps).Decode(slices);
    }
}
