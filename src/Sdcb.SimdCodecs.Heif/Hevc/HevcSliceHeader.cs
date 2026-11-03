namespace Sdcb.SimdCodecs.Heif.Hevc;

/// <summary>The slice segment header fields an intra decoder needs (H.265 §7.3.6.1).</summary>
internal sealed class HevcSliceHeader
{
    internal const int SliceTypeB = 0;
    internal const int SliceTypeP = 1;
    internal const int SliceTypeI = 2;

    internal bool FirstSliceSegmentInPic;
    internal bool DependentSliceSegment;
    internal int SegmentAddress;

    /// <summary>SliceAddrRs: the address of the first CTB of the (independent) slice this segment belongs to.</summary>
    internal int SliceAddrRs;

    internal int SliceType;
    internal bool SaoLuma;
    internal bool SaoChroma;
    internal int SliceQpY;
    internal int CbQpOffset;
    internal int CrQpOffset;
    internal bool DeblockingDisabled;
    internal int BetaOffsetDiv2;
    internal int TcOffsetDiv2;
    internal bool LoopFilterAcrossSlices;

    /// <summary>Byte offset of slice_segment_data() in the unescaped RBSP.</summary>
    internal int DataOffset;

    /// <summary>
    /// Parses a slice segment header. <paramref name="previous"/> is the header of the preceding slice segment
    /// of the same picture, whose fields a dependent slice segment inherits.
    /// </summary>
    internal static HevcSliceHeader Parse(ReadOnlySpan<byte> rbsp, int nalType, HevcSps sps, HevcPps pps, HevcSliceHeader? previous)
    {
        var r = new HevcBitReader(rbsp, 2);
        var h = new HevcSliceHeader { FirstSliceSegmentInPic = r.ReadFlag() };
        if (nalType is >= 16 and <= 23) r.Skip(1); // no_output_of_prior_pics_flag
        var ppsId = r.ReadUe(63, "slice_pic_parameter_set_id");
        if (ppsId != pps.Id) throw new InvalidDataException("The slice refers to a PPS that is not in the image's decoder configuration.");

        var picSizeInCtbs = sps.PicWidthInCtbs * sps.PicHeightInCtbs;
        if (!h.FirstSliceSegmentInPic)
        {
            if (pps.DependentSliceSegmentsEnabled) h.DependentSliceSegment = r.ReadFlag();
            var bits = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(picSizeInCtbs - 1));
            h.SegmentAddress = (int)r.ReadBits(bits);
            if (h.SegmentAddress >= picSizeInCtbs) throw new InvalidDataException("slice_segment_address is out of range.");
        }

        if (h.DependentSliceSegment)
        {
            if (previous is null) throw new InvalidDataException("A dependent slice segment has no preceding slice segment.");
            h.SliceAddrRs = previous.SliceAddrRs;
            h.SliceType = previous.SliceType;
            h.SaoLuma = previous.SaoLuma;
            h.SaoChroma = previous.SaoChroma;
            h.SliceQpY = previous.SliceQpY;
            h.CbQpOffset = previous.CbQpOffset;
            h.CrQpOffset = previous.CrQpOffset;
            h.DeblockingDisabled = previous.DeblockingDisabled;
            h.BetaOffsetDiv2 = previous.BetaOffsetDiv2;
            h.TcOffsetDiv2 = previous.TcOffsetDiv2;
            h.LoopFilterAcrossSlices = previous.LoopFilterAcrossSlices;
        }
        else
        {
            h.SliceAddrRs = h.SegmentAddress;
            r.Skip(pps.NumExtraSliceHeaderBits);
            h.SliceType = r.ReadUe(2, "slice_type");
            if (h.SliceType != SliceTypeI)
            {
                throw new InvalidDataException("Only intra (I) slices are supported in HEIF image items.");
            }
            if (pps.OutputFlagPresent) r.Skip(1);
            if (nalType is not (19 or 20))
            {
                // Not IDR: slice_pic_order_cnt_lsb and the reference picture sets. An I slice of a CRA or
                // trailing picture still signals them; they do not affect intra decoding, so they are skipped.
                r.Skip(sps.Log2MaxPocLsb);
                if (!r.ReadFlag())
                {
                    SkipShortTermRps(ref r, sps.NumShortTermRefPicSets);
                }
                else if (sps.NumShortTermRefPicSets > 1)
                {
                    var bits = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(sps.NumShortTermRefPicSets - 1));
                    r.Skip(bits);
                }
                if (sps.LongTermRefPicsPresent)
                {
                    var numLtSps = sps.NumLongTermRefPicsSps > 0 ? r.ReadUe(sps.NumLongTermRefPicsSps, "num_long_term_sps") : 0;
                    var numLtPics = r.ReadUe(32, "num_long_term_pics");
                    for (var i = 0; i < numLtSps + numLtPics; i++)
                    {
                        if (i < numLtSps)
                        {
                            if (sps.NumLongTermRefPicsSps > 1)
                            {
                                r.Skip(32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(sps.NumLongTermRefPicsSps - 1)));
                            }
                        }
                        else
                        {
                            r.Skip(sps.Log2MaxPocLsb + 1);
                        }
                        if (r.ReadFlag()) r.ReadUe();
                    }
                }
                if (sps.TemporalMvpEnabled) r.Skip(1);
            }
            if (sps.SaoEnabled)
            {
                h.SaoLuma = r.ReadFlag();
                if (sps.ChromaFormatIdc != 0) h.SaoChroma = r.ReadFlag();
            }

            var qpBdOffsetY = 6 * (sps.BitDepthY - 8);
            h.SliceQpY = pps.InitQp + r.ReadSe(-87, 77, "slice_qp_delta");
            if (h.SliceQpY < -qpBdOffsetY || h.SliceQpY > 51) throw new InvalidDataException("SliceQpY is out of range.");
            if (pps.SliceChromaQpOffsetsPresent)
            {
                h.CbQpOffset = r.ReadSe(-12, 12, "slice_cb_qp_offset");
                h.CrQpOffset = r.ReadSe(-12, 12, "slice_cr_qp_offset");
            }

            var overrideFlag = pps.DeblockingFilterOverrideEnabled && r.ReadFlag();
            h.DeblockingDisabled = pps.DeblockingFilterDisabled;
            h.BetaOffsetDiv2 = pps.BetaOffsetDiv2;
            h.TcOffsetDiv2 = pps.TcOffsetDiv2;
            if (overrideFlag)
            {
                h.DeblockingDisabled = r.ReadFlag();
                if (!h.DeblockingDisabled)
                {
                    h.BetaOffsetDiv2 = r.ReadSe(-6, 6, "slice_beta_offset_div2");
                    h.TcOffsetDiv2 = r.ReadSe(-6, 6, "slice_tc_offset_div2");
                }
            }
            h.LoopFilterAcrossSlices = pps.LoopFilterAcrossSlicesEnabled;
            if (pps.LoopFilterAcrossSlicesEnabled && (h.SaoLuma || h.SaoChroma || !h.DeblockingDisabled))
            {
                h.LoopFilterAcrossSlices = r.ReadFlag();
            }
        }

        if (pps.TilesEnabled || pps.EntropyCodingSyncEnabled)
        {
            // Entry points are not needed: substreams follow each other byte-aligned, and the CABAC engine
            // tracks its exact bit position (see HevcCabac).
            var count = r.ReadUe(picSizeInCtbs, "num_entry_point_offsets");
            if (count > 0)
            {
                var offsetBits = r.ReadUe(31, "offset_len_minus1") + 1;
                for (var i = 0; i < count; i++) r.Skip(offsetBits);
            }
        }
        if (pps.SliceHeaderExtensionPresent)
        {
            var length = r.ReadUe(256, "slice_segment_header_extension_length");
            r.Skip(length * 8);
        }

        // byte_alignment(): one 1 bit, then 0 bits to the byte boundary.
        if (r.ReadBit() != 1) throw new InvalidDataException("The slice header's byte_alignment() is malformed.");
        while (!r.ByteAligned) r.Skip(1);
        h.DataOffset = r.BytePosition;
        return h;
    }

    private static void SkipShortTermRps(ref HevcBitReader r, int numSets)
    {
        // st_ref_pic_set( num_short_term_ref_pic_sets ) in the slice header. Inter RPS prediction would need the
        // SPS sets' sizes; those only occur for inter pictures, so a predicted set here is rejected.
        if (numSets != 0 && r.ReadFlag())
        {
            throw new InvalidDataException("Inter-predicted reference picture sets are not supported.");
        }
        var negative = r.ReadUe(16, "num_negative_pics");
        var positive = r.ReadUe(16, "num_positive_pics");
        for (var i = 0; i < negative + positive; i++)
        {
            r.ReadUe();
            r.Skip(1);
        }
    }
}
