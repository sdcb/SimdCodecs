namespace Sdcb.SimdCodecs.Heif.Hevc;

/// <summary>Scan order arrays of H.265 §6.5.3–6.5.5 for block sizes 1, 2, 4 and 8.</summary>
internal static class HevcScan
{
    /// <summary>[log2BlockSize 0..3][scanIdx 0..2] → packed positions, x in the low nibble, y in the high nibble.</summary>
    internal static readonly byte[][][] Order = Build();

    private static byte[][][] Build()
    {
        var result = new byte[4][][];
        for (var log2 = 0; log2 < 4; log2++)
        {
            var size = 1 << log2;
            var diag = new byte[size * size];
            var hor = new byte[size * size];
            var ver = new byte[size * size];

            // 6.5.3 up-right diagonal.
            int i = 0, x = 0, y = 0;
            while (i < size * size)
            {
                while (y >= 0)
                {
                    if (x < size && y < size) diag[i++] = (byte)(x | (y << 4));
                    y--;
                    x++;
                }
                y = x;
                x = 0;
            }

            i = 0;
            for (y = 0; y < size; y++)
                for (x = 0; x < size; x++)
                    hor[i++] = (byte)(x | (y << 4));
            i = 0;
            for (x = 0; x < size; x++)
                for (y = 0; y < size; y++)
                    ver[i++] = (byte)(x | (y << 4));

            result[log2] = [diag, hor, ver];
        }
        return result;
    }
}

/// <summary>
/// The scaling factors m[x][y] of §7.4.5, expanded per (sizeId, matrixId) to the full block size and stored
/// row-major (index y * size + x).
/// </summary>
internal sealed class HevcScalingList
{
    internal readonly byte[][][] Factors = new byte[4][][];

    private static readonly byte[] DefaultIntra8 =
    [
        16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 17, 16, 17, 16, 17, 18,
        17, 18, 18, 17, 18, 21, 19, 20, 21, 20, 19, 21, 24, 22, 22, 24,
        24, 22, 22, 24, 25, 25, 27, 30, 27, 25, 25, 29, 31, 35, 35, 31,
        29, 36, 41, 44, 41, 36, 47, 54, 54, 47, 65, 70, 65, 88, 88, 115,
    ];

    private static readonly byte[] DefaultInter8 =
    [
        16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 17, 17, 17, 17, 17, 18,
        18, 18, 18, 18, 18, 20, 20, 20, 20, 20, 20, 20, 24, 24, 24, 24,
        24, 24, 24, 24, 25, 25, 25, 25, 25, 25, 25, 28, 28, 28, 28, 28,
        28, 33, 33, 33, 33, 33, 41, 41, 41, 41, 54, 54, 54, 71, 71, 91,
    ];

    /// <summary>The default lists of Table 7-5 / 7-6.</summary>
    internal static HevcScalingList CreateDefault()
    {
        var lists = new byte[4][][];
        var dc = new int[4][];
        for (var sizeId = 0; sizeId < 4; sizeId++)
        {
            lists[sizeId] = new byte[6][];
            dc[sizeId] = new int[6];
            for (var matrixId = 0; matrixId < 6; matrixId++)
            {
                lists[sizeId][matrixId] = sizeId == 0
                    ? Enumerable.Repeat((byte)16, 16).ToArray()
                    : (byte[])(matrixId < 3 ? DefaultIntra8 : DefaultInter8).Clone();
                dc[sizeId][matrixId] = 16;
            }
        }
        return Expand(lists, dc);
    }

    /// <summary>scaling_list_data() of §7.3.4.</summary>
    internal static HevcScalingList Parse(ref HevcBitReader reader)
    {
        var defaults = CreateDefaultLists(out var defaultDc);
        var lists = new byte[4][][];
        var dc = new int[4][];
        for (var sizeId = 0; sizeId < 4; sizeId++)
        {
            lists[sizeId] = new byte[6][];
            dc[sizeId] = new int[6];
            var coefNum = Math.Min(64, 1 << (4 + (sizeId << 1)));
            for (var matrixId = 0; matrixId < 6; matrixId += sizeId == 3 ? 3 : 1)
            {
                var predModeFlag = reader.ReadFlag();
                if (!predModeFlag)
                {
                    var maxDelta = sizeId == 3 ? matrixId / 3 : matrixId;
                    var delta = reader.ReadUe(maxDelta, "scaling_list_pred_matrix_id_delta");
                    if (delta == 0)
                    {
                        lists[sizeId][matrixId] = (byte[])defaults[sizeId][matrixId].Clone();
                        dc[sizeId][matrixId] = defaultDc;
                    }
                    else
                    {
                        var refMatrixId = matrixId - delta * (sizeId == 3 ? 3 : 1);
                        lists[sizeId][matrixId] = (byte[])lists[sizeId][refMatrixId].Clone();
                        dc[sizeId][matrixId] = dc[sizeId][refMatrixId];
                    }
                }
                else
                {
                    var nextCoef = 8;
                    if (sizeId > 1)
                    {
                        nextCoef = reader.ReadSe(-7, 247, "scaling_list_dc_coef_minus8") + 8;
                        dc[sizeId][matrixId] = nextCoef;
                    }
                    var list = new byte[coefNum];
                    for (var i = 0; i < coefNum; i++)
                    {
                        var deltaCoef = reader.ReadSe(-128, 127, "scaling_list_delta_coef");
                        nextCoef = (nextCoef + deltaCoef + 256) % 256;
                        if (nextCoef == 0) throw new InvalidDataException("A scaling list entry is zero.");
                        list[i] = (byte)nextCoef;
                    }
                    lists[sizeId][matrixId] = list;
                }
            }
        }

        // 32x32 chroma matrices (1, 2, 4, 5) only matter for 4:4:4, which is not decoded; give them the
        // 16x16 lists so the table is complete.
        for (var matrixId = 0; matrixId < 6; matrixId++)
        {
            if (matrixId % 3 == 0) continue;
            lists[3][matrixId] = lists[2][matrixId];
            dc[3][matrixId] = dc[2][matrixId];
        }
        return Expand(lists, dc);
    }

    private static byte[][][] CreateDefaultLists(out int dc)
    {
        dc = 16;
        var lists = new byte[4][][];
        for (var sizeId = 0; sizeId < 4; sizeId++)
        {
            lists[sizeId] = new byte[6][];
            for (var matrixId = 0; matrixId < 6; matrixId++)
            {
                lists[sizeId][matrixId] = sizeId == 0
                    ? Enumerable.Repeat((byte)16, 16).ToArray()
                    : (matrixId < 3 ? DefaultIntra8 : DefaultInter8);
            }
        }
        return lists;
    }

    private static HevcScalingList Expand(byte[][][] lists, int[][] dc)
    {
        var result = new HevcScalingList();
        for (var sizeId = 0; sizeId < 4; sizeId++)
        {
            var size = 4 << sizeId;
            result.Factors[sizeId] = new byte[6][];
            for (var matrixId = 0; matrixId < 6; matrixId++)
            {
                var factor = new byte[size * size];
                var list = lists[sizeId][matrixId];
                if (sizeId == 0)
                {
                    var scan = HevcScan.Order[2][0];
                    for (var i = 0; i < 16; i++)
                    {
                        int x = scan[i] & 15, y = scan[i] >> 4;
                        factor[y * 4 + x] = list[i];
                    }
                }
                else
                {
                    // 8x8 list upsampled by 1, 2 or 4 (7-45, 7-46, 7-48).
                    var scan = HevcScan.Order[3][0];
                    var ratio = size / 8;
                    for (var i = 0; i < 64; i++)
                    {
                        int x = scan[i] & 15, y = scan[i] >> 4;
                        for (var j = 0; j < ratio; j++)
                            for (var k = 0; k < ratio; k++)
                                factor[(y * ratio + j) * size + x * ratio + k] = list[i];
                    }
                    if (sizeId >= 2) factor[0] = (byte)dc[sizeId][matrixId];
                }
                result.Factors[sizeId][matrixId] = factor;
            }
        }
        return result;
    }
}

/// <summary>Sequence parameter set fields used by an intra decoder (H.265 §7.3.2.2).</summary>
internal sealed class HevcSps
{
    internal int Id;
    internal int ProfileIdc;
    internal uint ProfileCompatibility; // general_profile_compatibility_flag[j] at bit 31 - j
    internal int ChromaFormatIdc;
    internal int Width;
    internal int Height;
    internal int ConfLeft, ConfRight, ConfTop, ConfBottom; // in luma samples
    internal int BitDepthY;
    internal int BitDepthC;
    internal int Log2MaxPocLsb;
    internal int MinCbLog2SizeY;
    internal int CtbLog2SizeY;
    internal int MinTbLog2SizeY;
    internal int MaxTbLog2SizeY;
    internal int MaxTransformHierarchyDepthIntra;
    internal bool ScalingListEnabled;
    internal HevcScalingList? ScalingList;
    internal bool AmpEnabled;
    internal bool SaoEnabled;
    internal bool PcmEnabled;
    internal int PcmBitDepthY, PcmBitDepthC, Log2MinPcmCbSizeY, Log2MaxPcmCbSizeY;
    internal bool PcmLoopFilterDisabled;
    internal int NumShortTermRefPicSets;
    internal bool LongTermRefPicsPresent;
    internal int NumLongTermRefPicsSps;
    internal bool TemporalMvpEnabled;
    internal bool StrongIntraSmoothingEnabled;

    // VUI video signal type (E.2.1); defaults per the VUI semantics when absent.
    internal bool VideoFullRange;
    internal int ColourPrimaries = 2;
    internal int TransferCharacteristics = 2;
    internal int MatrixCoefficients = 2;

    // VUI chroma_sample_loc_type_top_field (E.2.1); 0 (left) when absent, or when the VUI ends before it.
    internal int ChromaSampleLocType;

    internal int CtbSizeY => 1 << CtbLog2SizeY;
    internal int PicWidthInCtbs => (Width + CtbSizeY - 1) >> CtbLog2SizeY;
    internal int PicHeightInCtbs => (Height + CtbSizeY - 1) >> CtbLog2SizeY;

    /// <summary>The largest picture this decoder accepts, per side; level 6.2 allows 8192 × 4320.</summary>
    internal const int MaxDimension = 16_888;

    internal static HevcSps Parse(ReadOnlySpan<byte> rbsp)
    {
        var r = new HevcBitReader(rbsp, 2); // skip the 2-byte NAL header
        var sps = new HevcSps();
        r.Skip(4); // sps_video_parameter_set_id
        var maxSubLayersMinus1 = (int)r.ReadBits(3);
        if (maxSubLayersMinus1 > 6) throw new InvalidDataException("sps_max_sub_layers_minus1 is out of range.");
        r.Skip(1); // sps_temporal_id_nesting_flag
        sps.ProfileIdc = ProfileTierLevel(ref r, maxSubLayersMinus1, out sps.ProfileCompatibility);
        if (!IsSupportedProfile(sps.ProfileIdc, sps.ProfileCompatibility))
        {
            throw new InvalidDataException(
                $"HEVC general_profile_idc {sps.ProfileIdc} is not supported; only Main (1), Main 10 (2) and Main Still Picture (3) are.");
        }
        sps.Id = r.ReadUe(15, "sps_seq_parameter_set_id");
        sps.ChromaFormatIdc = r.ReadUe(3, "chroma_format_idc");
        if (sps.ChromaFormatIdc == 3 && r.ReadFlag())
        {
            throw new InvalidDataException("separate_colour_plane_flag is not supported.");
        }
        sps.Width = r.ReadUe(MaxDimension, "pic_width_in_luma_samples");
        sps.Height = r.ReadUe(MaxDimension, "pic_height_in_luma_samples");
        if (sps.Width == 0 || sps.Height == 0) throw new InvalidDataException("The HEVC picture has zero size.");

        var subWidth = sps.ChromaFormatIdc is 1 or 2 ? 2 : 1;
        var subHeight = sps.ChromaFormatIdc == 1 ? 2 : 1;
        if (r.ReadFlag())
        {
            sps.ConfLeft = r.ReadUe(MaxDimension, "conf_win_left_offset") * subWidth;
            sps.ConfRight = r.ReadUe(MaxDimension, "conf_win_right_offset") * subWidth;
            sps.ConfTop = r.ReadUe(MaxDimension, "conf_win_top_offset") * subHeight;
            sps.ConfBottom = r.ReadUe(MaxDimension, "conf_win_bottom_offset") * subHeight;
            if (sps.ConfLeft + sps.ConfRight >= sps.Width || sps.ConfTop + sps.ConfBottom >= sps.Height)
            {
                throw new InvalidDataException("The conformance window is empty.");
            }
        }

        sps.BitDepthY = 8 + r.ReadUe(8, "bit_depth_luma_minus8");
        sps.BitDepthC = 8 + r.ReadUe(8, "bit_depth_chroma_minus8");
        sps.Log2MaxPocLsb = 4 + r.ReadUe(12, "log2_max_pic_order_cnt_lsb_minus4");
        var subLayerOrderingInfo = r.ReadFlag();
        for (var i = subLayerOrderingInfo ? 0 : maxSubLayersMinus1; i <= maxSubLayersMinus1; i++)
        {
            r.ReadUe();
            r.ReadUe();
            r.ReadUe();
        }

        sps.MinCbLog2SizeY = 3 + r.ReadUe(3, "log2_min_luma_coding_block_size_minus3");
        sps.CtbLog2SizeY = sps.MinCbLog2SizeY + r.ReadUe(3, "log2_diff_max_min_luma_coding_block_size");
        sps.MinTbLog2SizeY = 2 + r.ReadUe(3, "log2_min_luma_transform_block_size_minus2");
        sps.MaxTbLog2SizeY = sps.MinTbLog2SizeY + r.ReadUe(3, "log2_diff_max_min_luma_transform_block_size");
        if (sps.CtbLog2SizeY is < 4 or > 6 || sps.MinTbLog2SizeY >= sps.MinCbLog2SizeY ||
            sps.MaxTbLog2SizeY > Math.Min(sps.CtbLog2SizeY, 5))
        {
            throw new InvalidDataException("The HEVC coding / transform block sizes are out of range.");
        }
        // 7.4.3.2.1: the picture size is a multiple of MinCbSizeY, so every coding block lies inside the planes.
        if (sps.Width % (1 << sps.MinCbLog2SizeY) != 0 || sps.Height % (1 << sps.MinCbLog2SizeY) != 0)
        {
            throw new InvalidDataException("The HEVC picture size is not a multiple of the minimum coding block size.");
        }
        r.ReadUe(); // max_transform_hierarchy_depth_inter
        sps.MaxTransformHierarchyDepthIntra = r.ReadUe(sps.CtbLog2SizeY - sps.MinTbLog2SizeY, "max_transform_hierarchy_depth_intra");

        sps.ScalingListEnabled = r.ReadFlag();
        if (sps.ScalingListEnabled)
        {
            sps.ScalingList = r.ReadFlag() ? HevcScalingList.Parse(ref r) : HevcScalingList.CreateDefault();
        }
        sps.AmpEnabled = r.ReadFlag();
        sps.SaoEnabled = r.ReadFlag();
        sps.PcmEnabled = r.ReadFlag();
        if (sps.PcmEnabled)
        {
            sps.PcmBitDepthY = 1 + (int)r.ReadBits(4);
            sps.PcmBitDepthC = 1 + (int)r.ReadBits(4);
            sps.Log2MinPcmCbSizeY = 3 + r.ReadUe(2, "log2_min_pcm_luma_coding_block_size_minus3");
            sps.Log2MaxPcmCbSizeY = sps.Log2MinPcmCbSizeY + r.ReadUe(2, "log2_diff_max_min_pcm_luma_coding_block_size");
            sps.PcmLoopFilterDisabled = r.ReadFlag();
            if (sps.PcmBitDepthY > sps.BitDepthY || sps.PcmBitDepthC > sps.BitDepthC || sps.Log2MaxPcmCbSizeY > Math.Min(sps.CtbLog2SizeY, 5))
            {
                throw new InvalidDataException("The HEVC PCM parameters are out of range.");
            }
        }

        sps.NumShortTermRefPicSets = r.ReadUe(64, "num_short_term_ref_pic_sets");
        var deltaPocCounts = new int[sps.NumShortTermRefPicSets + 1];
        for (var i = 0; i < sps.NumShortTermRefPicSets; i++)
        {
            deltaPocCounts[i] = SkipShortTermRefPicSet(ref r, i, sps.NumShortTermRefPicSets, deltaPocCounts);
        }
        sps.LongTermRefPicsPresent = r.ReadFlag();
        if (sps.LongTermRefPicsPresent)
        {
            sps.NumLongTermRefPicsSps = r.ReadUe(32, "num_long_term_ref_pics_sps");
            for (var i = 0; i < sps.NumLongTermRefPicsSps; i++)
            {
                r.Skip(sps.Log2MaxPocLsb);
                r.Skip(1);
            }
        }
        sps.TemporalMvpEnabled = r.ReadFlag();
        sps.StrongIntraSmoothingEnabled = r.ReadFlag();
        if (r.ReadFlag()) ParseVuiSignalType(ref r, sps);

        // sps_extension: the range / multilayer / 3D / SCC extensions are outside Main and Main 10, whose
        // conforming streams leave sps_range_extension_flag and the rest at 0. Anything after the VUI
        // colour description is not needed, so it is not parsed.
        return sps;
    }

    /// <summary>profile_tier_level( 1, maxNumSubLayersMinus1 ) (§7.3.3); returns general_profile_idc.</summary>
    /// <summary>
    /// Main, Main 10 and Main Still Picture (A.3.2 – A.3.4), by profile_idc or, for profile_idc 0, by a set
    /// compatibility flag. Everything else (the range extensions with profile_idc 4 and up, SCC, the multilayer
    /// profiles) uses tools this decoder does not implement.
    /// </summary>
    internal static bool IsSupportedProfile(int profileIdc, uint compatibility)
    {
        if (profileIdc is 1 or 2 or 3) return true;
        if (profileIdc != 0) return false;
        const uint main = 1u << (31 - 1), main10 = 1u << (31 - 2), stillPicture = 1u << (31 - 3);
        return (compatibility & (main | main10 | stillPicture)) != 0;
    }

    internal static int ProfileTierLevel(ref HevcBitReader r, int maxSubLayersMinus1, out uint compatibility)
    {
        // General profile: profile_space (2), tier (1), profile_idc (5), 32 compatibility flags, 4 source
        // flags, 43 constraint bits, 1 inbld/reserved bit, then general_level_idc (8): 96 bits.
        r.Skip(3);
        var profileIdc = (int)r.ReadBits(5);
        compatibility = r.ReadBits(32);
        r.Skip(4 + 43 + 1 + 8);
        Span<bool> profilePresent = stackalloc bool[8];
        Span<bool> levelPresent = stackalloc bool[8];
        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            profilePresent[i] = r.ReadFlag();
            levelPresent[i] = r.ReadFlag();
        }
        if (maxSubLayersMinus1 > 0)
        {
            for (var i = maxSubLayersMinus1; i < 8; i++) r.Skip(2);
        }
        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            if (profilePresent[i]) r.Skip(88);
            if (levelPresent[i]) r.Skip(8);
        }
        return profileIdc;
    }

    /// <summary>st_ref_pic_set( stRpsIdx ) (§7.3.7), skipped; returns NumDeltaPocs for later inter prediction.</summary>
    private static int SkipShortTermRefPicSet(ref HevcBitReader r, int index, int count, int[] deltaPocCounts)
    {
        var interPrediction = index != 0 && r.ReadFlag();
        if (interPrediction)
        {
            var deltaIdx = 1;
            if (index == count) deltaIdx = r.ReadUe(index, "delta_idx_minus1") + 1;
            r.Skip(1); // delta_rps_sign
            r.ReadUe(); // abs_delta_rps_minus1
            var refIndex = index - deltaIdx;
            if (refIndex < 0) throw new InvalidDataException("Invalid short-term RPS reference.");
            var total = 0;
            for (var j = 0; j <= deltaPocCounts[refIndex]; j++)
            {
                var used = r.ReadFlag();
                var useDelta = used || r.ReadFlag();
                if (useDelta) total++;
            }
            return total;
        }

        var negative = r.ReadUe(16, "num_negative_pics");
        var positive = r.ReadUe(16, "num_positive_pics");
        for (var i = 0; i < negative + positive; i++)
        {
            r.ReadUe();
            r.Skip(1);
        }
        return negative + positive;
    }

    private static void ParseVuiSignalType(ref HevcBitReader r, HevcSps sps)
    {
        if (r.ReadFlag()) // aspect_ratio_info_present_flag
        {
            if (r.ReadBits(8) == 255) r.Skip(32); // EXTENDED_SAR: sar_width, sar_height
        }
        if (r.ReadFlag()) r.Skip(1); // overscan_info_present_flag → overscan_appropriate_flag
        if (r.ReadFlag()) // video_signal_type_present_flag
        {
            r.Skip(3); // video_format
            sps.VideoFullRange = r.ReadFlag();
            if (r.ReadFlag()) // colour_description_present_flag
            {
                sps.ColourPrimaries = (int)r.ReadBits(8);
                sps.TransferCharacteristics = (int)r.ReadBits(8);
                sps.MatrixCoefficients = (int)r.ReadBits(8);
            }
        }

        // chroma_loc_info_present_flag, then two ue(v) (top and bottom field). It is informational (the planar output
        // reports it; the RGB conversion replicates chroma regardless), so a VUI that ends here, or a value outside
        // the 0..5 the standard defines, reads as the default 0 instead of failing a picture that decodes today.
        if (r.BitsLeft < 1 || !r.ReadFlag())
            return;
        var probe = r;
        try
        {
            var top = probe.ReadUe();
            sps.ChromaSampleLocType = top <= 5 ? (int)top : 0;
        }
        catch (InvalidDataException)
        {
            sps.ChromaSampleLocType = 0;
        }
    }
}

/// <summary>Picture parameter set fields used by an intra decoder (H.265 §7.3.2.3).</summary>
internal sealed class HevcPps
{
    internal int Id;
    internal int SpsId;
    internal bool DependentSliceSegmentsEnabled;
    internal bool OutputFlagPresent;
    internal int NumExtraSliceHeaderBits;
    internal bool SignDataHidingEnabled;
    internal bool CabacInitPresent;
    internal int InitQp; // 26 + init_qp_minus26
    internal bool ConstrainedIntraPred;
    internal bool TransformSkipEnabled;
    internal bool CuQpDeltaEnabled;
    internal int DiffCuQpDeltaDepth;
    internal int CbQpOffset;
    internal int CrQpOffset;
    internal bool SliceChromaQpOffsetsPresent;
    internal bool TransquantBypassEnabled;
    internal bool TilesEnabled;
    internal bool EntropyCodingSyncEnabled;
    internal bool LoopFilterAcrossSlicesEnabled;
    internal bool DeblockingFilterOverrideEnabled;
    internal bool DeblockingFilterDisabled;
    internal int BetaOffsetDiv2;
    internal int TcOffsetDiv2;
    internal HevcScalingList? ScalingList;
    internal bool ListsModificationPresent;
    internal bool SliceHeaderExtensionPresent;
    internal int Log2MaxTransformSkipSize = 2;

    internal static HevcPps Parse(ReadOnlySpan<byte> rbsp)
    {
        var r = new HevcBitReader(rbsp, 2);
        var pps = new HevcPps
        {
            Id = r.ReadUe(63, "pps_pic_parameter_set_id"),
            SpsId = r.ReadUe(15, "pps_seq_parameter_set_id"),
        };
        pps.DependentSliceSegmentsEnabled = r.ReadFlag();
        pps.OutputFlagPresent = r.ReadFlag();
        pps.NumExtraSliceHeaderBits = (int)r.ReadBits(3);
        pps.SignDataHidingEnabled = r.ReadFlag();
        pps.CabacInitPresent = r.ReadFlag();
        r.ReadUe(); // num_ref_idx_l0_default_active_minus1
        r.ReadUe(); // num_ref_idx_l1_default_active_minus1
        pps.InitQp = 26 + r.ReadSe(-(26 + 6 * 8), 25, "init_qp_minus26");
        pps.ConstrainedIntraPred = r.ReadFlag();
        pps.TransformSkipEnabled = r.ReadFlag();
        pps.CuQpDeltaEnabled = r.ReadFlag();
        if (pps.CuQpDeltaEnabled) pps.DiffCuQpDeltaDepth = r.ReadUe(3, "diff_cu_qp_delta_depth");
        pps.CbQpOffset = r.ReadSe(-12, 12, "pps_cb_qp_offset");
        pps.CrQpOffset = r.ReadSe(-12, 12, "pps_cr_qp_offset");
        pps.SliceChromaQpOffsetsPresent = r.ReadFlag();
        r.Skip(2); // weighted_pred_flag, weighted_bipred_flag
        pps.TransquantBypassEnabled = r.ReadFlag();
        pps.TilesEnabled = r.ReadFlag();
        pps.EntropyCodingSyncEnabled = r.ReadFlag();
        if (pps.TilesEnabled)
        {
            // Parsed so the rest of the PPS is read correctly; decoding tiles is rejected by the slice decoder.
            var columns = r.ReadUe(255, "num_tile_columns_minus1");
            var rows = r.ReadUe(255, "num_tile_rows_minus1");
            if (!r.ReadFlag())
            {
                for (var i = 0; i < columns; i++) r.ReadUe();
                for (var i = 0; i < rows; i++) r.ReadUe();
            }
            r.Skip(1); // loop_filter_across_tiles_enabled_flag
        }
        pps.LoopFilterAcrossSlicesEnabled = r.ReadFlag();
        if (r.ReadFlag()) // deblocking_filter_control_present_flag
        {
            pps.DeblockingFilterOverrideEnabled = r.ReadFlag();
            pps.DeblockingFilterDisabled = r.ReadFlag();
            if (!pps.DeblockingFilterDisabled)
            {
                pps.BetaOffsetDiv2 = r.ReadSe(-6, 6, "pps_beta_offset_div2");
                pps.TcOffsetDiv2 = r.ReadSe(-6, 6, "pps_tc_offset_div2");
            }
        }
        if (r.ReadFlag()) pps.ScalingList = HevcScalingList.Parse(ref r);
        pps.ListsModificationPresent = r.ReadFlag();
        r.ReadUe(); // log2_parallel_merge_level_minus2
        pps.SliceHeaderExtensionPresent = r.ReadFlag();
        if (r.ReadFlag()) // pps_extension_present_flag
        {
            var rangeExtension = r.ReadFlag();
            r.Skip(7); // multilayer, 3d, scc, 4 bits
            if (rangeExtension)
            {
                // pps_range_extension(): only the leading transform-skip size is meaningful for Main / Main 10;
                // any other non-default value is a range-extension tool this decoder does not implement.
                if (pps.TransformSkipEnabled) pps.Log2MaxTransformSkipSize = 2 + r.ReadUe(3, "log2_max_transform_skip_block_size_minus2");
                if (r.ReadFlag()) throw new InvalidDataException("cross_component_prediction is not supported (HEVC range extensions).");
                if (r.ReadFlag()) throw new InvalidDataException("chroma_qp_offset_list is not supported (HEVC range extensions).");
                if (r.ReadUe() != 0 || r.ReadUe() != 0) throw new InvalidDataException("SAO offset scaling is not supported (HEVC range extensions).");
            }
        }
        return pps;
    }
}
