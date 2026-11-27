namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// Parsed AV1 Sequence Header OBU (§5.5).
/// </summary>
internal sealed class Av1SequenceHeader
{
    internal const int SelectScreenContentTools = 2;
    internal const int SelectIntegerMv = 2;

    internal int SeqProfile { get; private set; }
    internal bool StillPicture { get; private set; }
    internal bool ReducedStillPictureHeader { get; private set; }
    internal bool TimingInfoPresent { get; private set; }
    internal bool EqualPictureInterval { get; private set; }
    internal bool DecoderModelInfoPresent { get; private set; }
    internal int BufferDelayLengthMinus1 { get; private set; }
    internal int BufferRemovalTimeLengthMinus1 { get; private set; }
    internal int FramePresentationTimeLengthMinus1 { get; private set; }
    internal int OperatingPointsCntMinus1 { get; private set; }
    internal int[] OperatingPointIdc { get; private set; } = [];
    internal bool[] DecoderModelPresentForOp { get; private set; } = [];

    internal int FrameWidthBitsMinus1 { get; private set; }
    internal int FrameHeightBitsMinus1 { get; private set; }
    internal int MaxFrameWidth { get; private set; }
    internal int MaxFrameHeight { get; private set; }

    internal bool FrameIdNumbersPresent { get; private set; }
    internal int DeltaFrameIdLengthMinus2 { get; private set; }
    internal int AdditionalFrameIdLengthMinus1 { get; private set; }

    internal bool Use128x128Superblock { get; private set; }
    internal bool EnableFilterIntra { get; private set; }
    internal bool EnableIntraEdgeFilter { get; private set; }
    internal bool EnableInterIntraCompound { get; private set; }
    internal bool EnableMaskedCompound { get; private set; }
    internal bool EnableWarpedMotion { get; private set; }
    internal bool EnableDualFilter { get; private set; }
    internal bool EnableOrderHint { get; private set; }
    internal bool EnableJntComp { get; private set; }
    internal bool EnableRefFrameMvs { get; private set; }
    internal int SeqForceScreenContentTools { get; private set; } = SelectScreenContentTools;
    internal int SeqForceIntegerMv { get; private set; } = SelectIntegerMv;
    internal int OrderHintBits { get; private set; }

    internal bool EnableSuperres { get; private set; }
    internal bool EnableCdef { get; private set; }
    internal bool EnableRestoration { get; private set; }

    // Color config (§5.5.2)
    internal int BitDepth { get; private set; } = 8;
    internal bool Monochrome { get; private set; }
    internal int NumPlanes { get; private set; } = 3;
    internal int ColorPrimaries { get; private set; } = 2;
    internal int TransferCharacteristics { get; private set; } = 2;
    internal int MatrixCoefficients { get; private set; } = 2;
    internal bool ColorRangeFull { get; private set; }
    internal int SubsamplingX { get; private set; } = 1;
    internal int SubsamplingY { get; private set; } = 1;
    internal int ChromaSamplePosition { get; private set; }
    internal bool SeparateUvDeltaQ { get; private set; }
    internal bool FilmGrainParamsPresent { get; private set; }

    internal static Av1SequenceHeader Parse(ReadOnlySpan<byte> payload)
    {
        var br = new Av1BitReader(payload);
        var s = new Av1SequenceHeader
        {
            SeqProfile = (int)br.ReadBits(3),
            StillPicture = br.ReadBool(),
            ReducedStillPictureHeader = br.ReadBool(),
        };

        if (s.SeqProfile > 2)
            throw new InvalidDataException($"Unsupported AV1 sequence profile {s.SeqProfile}.");

        if (s.ReducedStillPictureHeader)
        {
            s.OperatingPointsCntMinus1 = 0;
            s.OperatingPointIdc = [0];
            s.DecoderModelPresentForOp = [false];
            _ = br.ReadBits(5); // seq_level_idx[0]
        }
        else
        {
            s.TimingInfoPresent = br.ReadBool();
            if (s.TimingInfoPresent)
            {
                _ = br.ReadBits(32); // num_units_in_display_tick
                _ = br.ReadBits(32); // time_scale
                s.EqualPictureInterval = br.ReadBool();
                if (s.EqualPictureInterval)
                    _ = br.ReadUvlc(); // num_ticks_per_picture_minus_1

                s.DecoderModelInfoPresent = br.ReadBool();
                if (s.DecoderModelInfoPresent)
                {
                    s.BufferDelayLengthMinus1 = (int)br.ReadBits(5);
                    _ = br.ReadBits(32); // num_units_in_decoding_tick
                    s.BufferRemovalTimeLengthMinus1 = (int)br.ReadBits(5);
                    s.FramePresentationTimeLengthMinus1 = (int)br.ReadBits(5);
                }
            }

            bool initialDisplayDelayPresent = br.ReadBool();
            s.OperatingPointsCntMinus1 = (int)br.ReadBits(5);
            int opCount = s.OperatingPointsCntMinus1 + 1;
            s.OperatingPointIdc = new int[opCount];
            s.DecoderModelPresentForOp = new bool[opCount];
            for (int i = 0; i < opCount; i++)
            {
                s.OperatingPointIdc[i] = (int)br.ReadBits(12);
                int levelIdx = (int)br.ReadBits(5);
                if (levelIdx > 7)
                    _ = br.ReadBool(); // seq_tier[i]

                if (s.DecoderModelInfoPresent)
                {
                    s.DecoderModelPresentForOp[i] = br.ReadBool();
                    if (s.DecoderModelPresentForOp[i])
                    {
                        int n = s.BufferDelayLengthMinus1 + 1;
                        _ = br.ReadBits(n); // decoder_buffer_delay
                        _ = br.ReadBits(n); // encoder_buffer_delay
                        _ = br.ReadBool();  // low_delay_mode_flag
                    }
                }

                if (initialDisplayDelayPresent)
                {
                    if (br.ReadBool())
                        _ = br.ReadBits(4); // initial_display_delay_minus_1
                }
            }
        }

        s.FrameWidthBitsMinus1 = (int)br.ReadBits(4);
        s.FrameHeightBitsMinus1 = (int)br.ReadBits(4);
        s.MaxFrameWidth = (int)br.ReadBits(s.FrameWidthBitsMinus1 + 1) + 1;
        s.MaxFrameHeight = (int)br.ReadBits(s.FrameHeightBitsMinus1 + 1) + 1;

        if (!s.ReducedStillPictureHeader)
            s.FrameIdNumbersPresent = br.ReadBool();
        if (s.FrameIdNumbersPresent)
        {
            s.DeltaFrameIdLengthMinus2 = (int)br.ReadBits(4);
            s.AdditionalFrameIdLengthMinus1 = (int)br.ReadBits(3);
        }

        s.Use128x128Superblock = br.ReadBool();
        s.EnableFilterIntra = br.ReadBool();
        s.EnableIntraEdgeFilter = br.ReadBool();

        if (!s.ReducedStillPictureHeader)
        {
            s.EnableInterIntraCompound = br.ReadBool();
            s.EnableMaskedCompound = br.ReadBool();
            s.EnableWarpedMotion = br.ReadBool();
            s.EnableDualFilter = br.ReadBool();
            s.EnableOrderHint = br.ReadBool();
            if (s.EnableOrderHint)
            {
                s.EnableJntComp = br.ReadBool();
                s.EnableRefFrameMvs = br.ReadBool();
            }

            if (br.ReadBool())
                s.SeqForceScreenContentTools = SelectScreenContentTools;
            else
                s.SeqForceScreenContentTools = br.ReadBit();

            if (s.SeqForceScreenContentTools > 0)
            {
                if (br.ReadBool())
                    s.SeqForceIntegerMv = SelectIntegerMv;
                else
                    s.SeqForceIntegerMv = br.ReadBit();
            }
            else
            {
                s.SeqForceIntegerMv = SelectIntegerMv;
            }

            if (s.EnableOrderHint)
                s.OrderHintBits = (int)br.ReadBits(3) + 1;
        }

        s.EnableSuperres = br.ReadBool();
        s.EnableCdef = br.ReadBool();
        s.EnableRestoration = br.ReadBool();

        ParseColorConfig(ref br, s);
        s.FilmGrainParamsPresent = br.ReadBool();

        return s;
    }

    private static void ParseColorConfig(ref Av1BitReader br, Av1SequenceHeader s)
    {
        bool highBitdepth = br.ReadBool();
        if (s.SeqProfile == 2 && highBitdepth)
        {
            bool twelveBit = br.ReadBool();
            s.BitDepth = twelveBit ? 12 : 10;
        }
        else
        {
            s.BitDepth = highBitdepth ? 10 : 8;
        }

        s.Monochrome = s.SeqProfile != 1 && br.ReadBool();
        s.NumPlanes = s.Monochrome ? 1 : 3;

        if (br.ReadBool()) // color_description_present_flag
        {
            s.ColorPrimaries = (int)br.ReadBits(8);
            s.TransferCharacteristics = (int)br.ReadBits(8);
            s.MatrixCoefficients = (int)br.ReadBits(8);
        }

        if (s.Monochrome)
        {
            s.ColorRangeFull = br.ReadBool();
            s.SubsamplingX = 1;
            s.SubsamplingY = 1;
            s.ChromaSamplePosition = 0;
            s.SeparateUvDeltaQ = false;
            return;
        }

        if (s.ColorPrimaries == 1 && s.TransferCharacteristics == 13 && s.MatrixCoefficients == 0)
        {
            s.ColorRangeFull = true;
            s.SubsamplingX = 0;
            s.SubsamplingY = 0;
        }
        else
        {
            s.ColorRangeFull = br.ReadBool();
            if (s.SeqProfile == 0)
            {
                s.SubsamplingX = 1;
                s.SubsamplingY = 1;
            }
            else if (s.SeqProfile == 1)
            {
                s.SubsamplingX = 0;
                s.SubsamplingY = 0;
            }
            else
            {
                if (s.BitDepth == 12)
                {
                    s.SubsamplingX = br.ReadBit();
                    s.SubsamplingY = s.SubsamplingX != 0 ? br.ReadBit() : 0;
                }
                else
                {
                    s.SubsamplingX = 1;
                    s.SubsamplingY = 0;
                }
            }

            if (s.SubsamplingX != 0 && s.SubsamplingY != 0)
                s.ChromaSamplePosition = (int)br.ReadBits(2);
        }

        s.SeparateUvDeltaQ = br.ReadBool();
    }
}
