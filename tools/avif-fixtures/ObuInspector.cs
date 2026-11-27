using System.Buffers.Binary;
using System.Text;

namespace AvifFixtures;

/// <summary>
/// Spec-based (AOM AV1 Bitstream Specification §5) OBU Sequence Header &amp; Uncompressed Frame Header
/// inspector used purely to verify that generated fixtures genuinely exercise the target AV1 syntax
/// features (profile, depth, subsampling, lossless, superres, film grain, intrabc, qmatrix, delta-q,
/// segmentation, cdef, loop restoration, tiles, inter/warped/obmc).
/// </summary>
internal static class ObuInspector
{
    internal sealed class ObuSummary
    {
        public int Profile;
        public int BitDepth;
        public string Subsampling = "?";
        public int ChromaPos;
        public int TileCols = 1;
        public int TileRows = 1;
        public bool HasSuperres;
        public bool HasFilmGrain;
        public bool HasIntraBc;
        public bool HasLossless;
        public bool HasQMatrix;
        public bool HasDeltaQ;
        public bool HasDeltaLf;
        public bool HasSegmentation;
        public bool HasCdef;
        public bool HasLoopRestoration;
        public bool HasInter;
        public bool HasWarpedMotion;
        public bool HasScreenContentTools;
        public int FrameHeadersParsed;
        public HashSet<string> Boxes = [];

        public string FeatureTags()
        {
            var tags = new List<string> { $"P{Profile}", $"{BitDepth}b", Subsampling };
            if (TileCols > 1 || TileRows > 1) tags.Add($"tiles={TileCols}x{TileRows}");
            if (HasLossless) tags.Add("lossless");
            if (HasSuperres) tags.Add("superres");
            if (HasFilmGrain) tags.Add("grain");
            if (HasIntraBc) tags.Add("intrabc");
            if (HasScreenContentTools && !HasIntraBc) tags.Add("scc");
            if (HasQMatrix) tags.Add("qm");
            if (HasDeltaQ) tags.Add("deltaq");
            if (HasDeltaLf) tags.Add("deltalf");
            if (HasSegmentation) tags.Add("seg");
            if (HasCdef) tags.Add("cdef");
            if (HasLoopRestoration) tags.Add("lr");
            if (HasInter) tags.Add("inter");
            if (HasWarpedMotion) tags.Add("warped");
            foreach (var b in new[] { "grid", "iovl", "iden", "auxl", "prem", "clap", "irot", "imir", "tmap", "a1lx", "a1op", "clli", "Exif", "thmb", "moov" })
                if (Boxes.Contains(b)) tags.Add(b);
            return string.Join(",", tags);
        }
    }

    internal static ObuSummary InspectAvif(ReadOnlySpan<byte> avif)
    {
        var summary = new ObuSummary();
        ScanBoxes(avif, 0, avif.Length, summary.Boxes);

        // Scan mdat for AV1 OBUs (for tmap/iovl, skip leading non-OBU item bytes by finding first valid OBU_SEQUENCE_HEADER).
        for (int pos = 0; pos + 8 <= avif.Length;)
        {
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(avif.Slice(pos, 4));
            string type = Encoding.ASCII.GetString(avif.Slice(pos + 4, 4));
            int hdr = 8;
            long size = size32;
            if (size == 1 && pos + 16 <= avif.Length)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(avif.Slice(pos + 8, 8));
                hdr = 16;
            }
            int end = size == 0 ? avif.Length : (int)Math.Min(avif.Length, pos + size);
            if (type == "mdat" && end > pos + hdr)
            {
                ReadOnlySpan<byte> mdat = avif.Slice(pos + hdr, end - pos - hdr);
                int startOff = 0;
                // If mdat starts with an iovl/tmap/grid payload instead of an OBU_SEQUENCE_HEADER (0x0A or 0x08), scan forward up to 256 bytes.
                if (mdat.Length > 4 && (mdat[0] != 0x0A && mdat[0] != 0x12))
                {
                    for (int k = 0; k < Math.Min(256, mdat.Length - 2); k++)
                    {
                        if (mdat[k] == 0x0A && mdat[k + 1] >= 4 && mdat[k + 1] <= 40) { startOff = k; break; }
                    }
                }
                ParseObuStream(mdat[startOff..], summary);
            }
            if (end <= pos) break;
            pos = end;
        }
        return summary;
    }

    private static void ScanBoxes(ReadOnlySpan<byte> data, int start, int end, HashSet<string> boxes, int depth = 0)
    {
        if (depth > 8) return;
        for (int pos = start; pos + 8 <= end;)
        {
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
            string type = Encoding.ASCII.GetString(data.Slice(pos + 4, 4));
            int hdr = 8;
            long size = size32;
            if (size == 1 && pos + 16 <= end)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(pos + 8, 8));
                hdr = 16;
            }
            int boxEnd = size == 0 ? end : (int)Math.Min(end, pos + size);
            if (boxEnd <= pos + hdr && size != 8) break;
            boxes.Add(type);

            // Detect item types inside infe (grid, iovl, iden, tmap, Exif)
            if (type == "infe" && boxEnd - pos >= 20)
            {
                byte ver = data[pos + 8];
                if (ver >= 2)
                {
                    int off = pos + 12 + (ver == 2 ? 4 : 6);
                    if (off + 4 <= boxEnd)
                        boxes.Add(Encoding.ASCII.GetString(data.Slice(off, 4)));
                }
            }
            else if (type == "meta" && pos + 12 < boxEnd)
            {
                ScanBoxes(data, pos + 12, boxEnd, boxes, depth + 1);
            }
            else if (type is "iprp" or "ipco" or "iinf" or "iref" or "moov" or "trak" or "mdia" or "minf" or "stbl")
            {
                int childStart = type is "iinf" or "iref" ? pos + (type == "iinf" ? (data[pos + 8] == 0 ? 14 : 16) : 12) : pos + hdr;
                if (childStart < boxEnd) ScanBoxes(data, childStart, boxEnd, boxes, depth + 1);
            }
            pos = boxEnd;
        }
    }

    private static void ParseObuStream(ReadOnlySpan<byte> stream, ObuSummary s)
    {
        bool haveSeq = false;
        SeqHdr seq = default;

        int pos = 0;
        while (pos < stream.Length)
        {
            byte h = stream[pos++];
            if ((h & 0x80) != 0) break; // obu_forbidden_bit must be 0
            int obuType = (h >> 3) & 0xF;
            bool hasExt = (h & 4) != 0;
            bool hasSize = (h & 2) != 0;
            if (hasExt)
            {
                if (pos >= stream.Length) break;
                pos++;
            }
            int obuSize;
            if (hasSize)
            {
                if (!TryReadLeb128(stream, ref pos, out obuSize)) break;
            }
            else
            {
                obuSize = stream.Length - pos;
            }
            if (obuSize < 0 || pos + obuSize > stream.Length) break;
            ReadOnlySpan<byte> payload = stream.Slice(pos, obuSize);
            pos += obuSize;

            if (obuType == 1) // OBU_SEQUENCE_HEADER
            {
                if (TryParseSeqHeader(payload, out var parsed))
                {
                    // Keep the first (colour) sequence header as primary if already set, unless higher profile/depth.
                    if (!haveSeq)
                    {
                        s.Profile = parsed.Profile;
                        s.BitDepth = parsed.BitDepth;
                        s.Subsampling = parsed.Subsampling;
                        s.ChromaPos = parsed.ChromaPos;
                    }
                    seq = parsed;
                    haveSeq = true;
                }
            }
            else if ((obuType == 3 || obuType == 6) && haveSeq) // OBU_FRAME_HEADER or OBU_FRAME
            {
                TryParseFrameHeader(payload, seq, s);
            }
        }
    }

    private static bool TryReadLeb128(ReadOnlySpan<byte> data, ref int pos, out int value)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++)
        {
            if (pos >= data.Length) { value = 0; return false; }
            byte b = data[pos++];
            v |= (ulong)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0) { value = (int)Math.Min(v, int.MaxValue); return true; }
        }
        value = 0;
        return false;
    }

    private struct SeqHdr
    {
        public int Profile, BitDepth, ChromaPos;
        public string Subsampling;
        public bool ReducedStill, MonoChrome, Use128x128, EnableSuperres, EnableCdef, EnableRestoration, FilmGrainPresent;
        public bool EnableOrderHint, EnableRefFrameMvs, EnableWarpedMotion, FrameIdNumbersPresent;
        public int FrameWidthBits, FrameHeightBits, MaxWidth, MaxHeight, OrderHintBits, DeltaFrameIdLen, AdditionalFrameIdLen;
        public int SubsamplingX, SubsamplingY;
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bit;
        public bool Ok => _bit <= _data.Length * 8;
        public uint F(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++)
            {
                int byteIdx = _bit >> 3;
                if (byteIdx >= _data.Length) { _bit = _data.Length * 8 + 1; return 0; }
                v = (v << 1) | (uint)((_data[byteIdx] >> (7 - (_bit & 7))) & 1);
                _bit++;
            }
            return v;
        }
        public bool B() => F(1) != 0;
        public uint Uvlc()
        {
            int zeros = 0;
            while (zeros < 32 && !B()) zeros++;
            if (zeros >= 32) return uint.MaxValue;
            return F(zeros) + (1u << zeros) - 1;
        }
        public uint Ns(uint n)
        {
            int w = 0;
            uint t = n;
            while (t > 0) { w++; t >>= 1; }
            uint m = (1u << w) - n;
            uint v = F(w - 1);
            if (v < m) return v;
            uint extra = F(1);
            return (v << 1) - m + extra;
        }
        public int Su(int n)
        {
            int v = (int)F(n);
            int signMask = 1 << (n - 1);
            return (v & signMask) != 0 ? v - 2 * signMask : v;
        }
    }

    private static bool TryParseSeqHeader(ReadOnlySpan<byte> payload, out SeqHdr h)
    {
        h = default;
        var br = new BitReader(payload);
        h.Profile = (int)br.F(3);
        bool stillPicture = br.B();
        h.ReducedStill = br.B();
        if (h.ReducedStill)
        {
            br.F(5); // seq_level_idx[0]
        }
        else
        {
            bool timingPresent = br.B();
            bool decoderModelPresent = false;
            int bufferDelayLen = 0;
            if (timingPresent)
            {
                br.F(32); br.F(32); // num_units_in_display_tick, time_scale
                if (br.B()) br.Uvlc(); // equal_picture_interval -> num_ticks_per_picture_minus_1
                decoderModelPresent = br.B();
                if (decoderModelPresent)
                {
                    bufferDelayLen = (int)br.F(5) + 1;
                    br.F(32); // num_units_in_decoding_tick
                    br.F(5);  // buffer_removal_time_length_minus_1
                    br.F(5);  // frame_presentation_time_length_minus_1
                }
            }
            bool initDelay = br.B(); // initial_display_delay_present_flag (always present when !reduced_still)
            int opCnt = (int)br.F(5);
            for (int i = 0; i <= opCnt; i++)
            {
                br.F(12); // operating_point_idc
                uint lvl = br.F(5);
                if (lvl > 7) br.F(1); // seq_tier
                if (decoderModelPresent)
                {
                    if (br.B()) // decoder_model_present_for_this_op
                    {
                        br.F(bufferDelayLen);
                        br.F(bufferDelayLen);
                        br.F(1);
                    }
                }
                if (initDelay && br.B()) br.F(4);
            }
        }
        h.FrameWidthBits = (int)br.F(4) + 1;
        h.FrameHeightBits = (int)br.F(4) + 1;
        h.MaxWidth = (int)br.F(h.FrameWidthBits) + 1;
        h.MaxHeight = (int)br.F(h.FrameHeightBits) + 1;
        if (!h.ReducedStill)
        {
            h.FrameIdNumbersPresent = br.B();
            if (h.FrameIdNumbersPresent)
            {
                h.DeltaFrameIdLen = (int)br.F(4) + 2;
                h.AdditionalFrameIdLen = (int)br.F(3) + 1;
            }
        }
        h.Use128x128 = br.B();
        br.B(); // enable_filter_intra
        br.B(); // enable_intra_edge_filter
        if (!h.ReducedStill)
        {
            br.B(); // enable_interintra_compound
            br.B(); // enable_masked_compound
            h.EnableWarpedMotion = br.B();
            br.B(); // enable_dual_filter
            h.EnableOrderHint = br.B();
            if (h.EnableOrderHint)
            {
                br.B(); // enable_jnt_comp
                h.EnableRefFrameMvs = br.B();
            }
            bool chooseScreen = br.B();
            uint forceScreen = chooseScreen ? 2u : br.F(1);
            if (forceScreen > 0)
            {
                if (!br.B()) br.F(1);
            }
            if (h.EnableOrderHint) h.OrderHintBits = (int)br.F(3) + 1;
        }
        h.EnableSuperres = br.B();
        h.EnableCdef = br.B();
        h.EnableRestoration = br.B();

        // color_config()
        bool highBitDepth = br.B();
        if (h.Profile == 2 && highBitDepth) h.BitDepth = br.B() ? 12 : 10;
        else h.BitDepth = highBitDepth ? 10 : 8;

        h.MonoChrome = h.Profile != 1 && br.B();
        bool colorDesc = br.B();
        uint cp = 2, tc = 2, mc = 2;
        if (colorDesc) { cp = br.F(8); tc = br.F(8); mc = br.F(8); }
        if (h.MonoChrome)
        {
            br.B(); // color_range
            h.SubsamplingX = 1; h.SubsamplingY = 1;
            h.Subsampling = "400";
        }
        else if (cp == 1 && tc == 13 && mc == 0)
        {
            h.SubsamplingX = 0; h.SubsamplingY = 0;
            h.Subsampling = "444";
        }
        else
        {
            br.B(); // color_range
            if (h.Profile == 0) { h.SubsamplingX = 1; h.SubsamplingY = 1; }
            else if (h.Profile == 1) { h.SubsamplingX = 0; h.SubsamplingY = 0; }
            else
            {
                if (h.BitDepth == 12) { h.SubsamplingX = br.B() ? 1 : 0; h.SubsamplingY = h.SubsamplingX != 0 && br.B() ? 1 : 0; }
                else { h.SubsamplingX = 1; h.SubsamplingY = 0; }
            }
            if (h.SubsamplingX == 1 && h.SubsamplingY == 1) h.ChromaPos = (int)br.F(2);
            h.Subsampling = (h.SubsamplingX, h.SubsamplingY) switch { (0, 0) => "444", (1, 0) => "422", _ => "420" };
        }
        if (!h.MonoChrome) br.B(); // separate_uv_delta_q
        h.FilmGrainPresent = br.B();
        return br.Ok;
    }

    private static void TryParseFrameHeader(ReadOnlySpan<byte> payload, SeqHdr seq, ObuSummary s)
    {
        var br = new BitReader(payload);
        if (seq.FrameIdNumbersPresent) { /* handled below */ }
        int frameType = 0; // KEY_FRAME
        bool showFrame = true;
        bool errorResilient = true;
        bool frameIsIntra = true;
        int w = seq.MaxWidth, h = seq.MaxHeight;

        if (!seq.ReducedStill)
        {
            bool showExisting = br.B();
            if (showExisting) return;
            frameType = (int)br.F(2);
            frameIsIntra = frameType is 0 or 2;
            showFrame = br.B();
            if (!showFrame) { /* showable_frame */ br.B(); }
            if (frameType == 3 || (frameType == 0 && showFrame)) errorResilient = true;
            else errorResilient = br.B();
        }
        if (!frameIsIntra) s.HasInter = true;
        if (seq.FilmGrainPresent) s.HasFilmGrain = true;
        if (seq.EnableWarpedMotion && !frameIsIntra) s.HasWarpedMotion = true;

        bool disableCdfUpdate = br.B();
        bool allowScc = br.B(); // simplified for default seq_force_screen_content_tools == SELECT_SCREEN_CONTENT_TOOLS
        if (allowScc) s.HasScreenContentTools = true;
        if (allowScc)
        {
            if (br.B()) { } // force_integer_mv
        }
        if (seq.FrameIdNumbersPresent) br.F(seq.DeltaFrameIdLen + seq.AdditionalFrameIdLen);
        bool frameSizeOverride = !seq.ReducedStill && (frameType == 3 || br.B());
        if (seq.EnableOrderHint) br.F(seq.OrderHintBits);
        if (!frameIsIntra && !errorResilient) br.F(3); // primary_ref_frame
        if (!seq.ReducedStill && !(frameType == 0 && showFrame)) br.F(8); // refresh_frame_flags

        if (!frameIsIntra)
        {
            // Keep parser lightweight for inter frames; we already captured key flags above.
            s.FrameHeadersParsed++;
            return;
        }

        // Intra frame size + superres
        if (frameSizeOverride)
        {
            w = (int)br.F(seq.FrameWidthBits) + 1;
            h = (int)br.F(seq.FrameHeightBits) + 1;
        }
        if (seq.EnableSuperres && br.B())
        {
            br.F(3); // coded_denom
            s.HasSuperres = true;
        }
        if (br.B()) // render_and_frame_size_different
        {
            br.F(16); br.F(16);
        }
        if (allowScc)
        {
            if (br.B()) s.HasIntraBc = true;
        }

        // tile_info()
        int sbCols = (w + (seq.Use128x128 ? 127 : 63)) / (seq.Use128x128 ? 128 : 64);
        int sbRows = (h + (seq.Use128x128 ? 127 : 63)) / (seq.Use128x128 ? 128 : 64);
        int maxTileWidthSb = 4096 / (seq.Use128x128 ? 128 : 64);
        int minLog2Cols = TileLog2(maxTileWidthSb, sbCols);
        int maxLog2Cols = TileLog2(1, Math.Min(sbCols, 64));
        int maxTileAreaSb = (4096 * 2304) / ((seq.Use128x128 ? 128 : 64) * (seq.Use128x128 ? 128 : 64));
        bool uniformTiles = br.B();
        int tileCols = 1, tileRows = 1;
        if (uniformTiles)
        {
            int log2Cols = minLog2Cols;
            while (log2Cols < maxLog2Cols && br.B()) log2Cols++;
            int tileWidthSb = (sbCols + (1 << log2Cols) - 1) >> log2Cols;
            tileCols = (sbCols + tileWidthSb - 1) / tileWidthSb;
            int minLog2Tiles = Math.Max(minLog2Cols, TileLog2(maxTileAreaSb, sbCols * sbRows));
            int minLog2Rows = Math.Max(minLog2Tiles - log2Cols, 0);
            int maxLog2Rows = TileLog2(1, Math.Min(sbRows, 64));
            int log2Rows = minLog2Rows;
            while (log2Rows < maxLog2Rows && br.B()) log2Rows++;
            int tileHeightSb = (sbRows + (1 << log2Rows) - 1) >> log2Rows;
            tileRows = (sbRows + tileHeightSb - 1) / tileHeightSb;
            if (log2Cols > 0 || log2Rows > 0) { br.F(log2Cols + log2Rows); br.F(2); }
        }
        s.TileCols = Math.Max(s.TileCols, tileCols);
        s.TileRows = Math.Max(s.TileRows, tileRows);

        // quantization_params()
        uint baseQIdx = br.F(8);
        int dYDc = br.B() ? br.Su(7) : 0;
        int dUDc = 0, dUAc = 0, dVDc = 0, dVAc = 0;
        if (!seq.MonoChrome)
        {
            dUDc = br.B() ? br.Su(7) : 0;
            dUAc = br.B() ? br.Su(7) : 0;
            dVDc = dUDc; dVAc = dUAc;
        }
        bool usingQm = br.B();
        if (usingQm)
        {
            s.HasQMatrix = true;
            br.F(4); br.F(4);
            if (!seq.MonoChrome) br.F(4);
        }
        bool codedLossless = baseQIdx == 0 && dYDc == 0 && dUDc == 0 && dUAc == 0 && dVDc == 0 && dVAc == 0;
        if (codedLossless) s.HasLossless = true;

        // segmentation_params()
        bool segEnabled = br.B();
        if (segEnabled)
        {
            s.HasSegmentation = true;
            int[] featBits = [8, 6, 6, 6, 6, 3, 0, 0];
            bool[] featSigned = [true, true, true, true, true, false, false, false];
            for (int i = 0; i < 8; i++)
                for (int j = 0; j < 8; j++)
                    if (br.B())
                    {
                        if (featBits[j] > 0) { if (featSigned[j]) br.Su(1 + featBits[j]); else br.F(featBits[j]); }
                    }
        }

        // delta_q_params() & delta_lf_params()
        bool deltaQPresent = baseQIdx > 0 && br.B();
        if (deltaQPresent)
        {
            s.HasDeltaQ = true;
            br.F(2); // delta_q_res
            if (!s.HasIntraBc && br.B())
            {
                s.HasDeltaLf = true;
                br.F(2); br.B();
            }
        }

        // loop_filter_params()
        if (!codedLossless && !s.HasIntraBc)
        {
            uint lf0 = br.F(6), lf1 = br.F(6);
            if (!seq.MonoChrome && (lf0 > 0 || lf1 > 0)) { br.F(6); br.F(6); }
            br.F(3);
            if (br.B() && br.B())
            {
                for (int i = 0; i < 8; i++) if (br.B()) br.Su(7);
                for (int i = 0; i < 2; i++) if (br.B()) br.Su(7);
            }
        }

        // cdef_params()
        if (!codedLossless && !s.HasIntraBc && seq.EnableCdef)
        {
            br.F(2); // damping
            int bits = (int)br.F(2);
            bool anyCdef = false;
            for (int i = 0; i < (1 << bits); i++)
            {
                uint ys = br.F(6);
                uint uvs = seq.MonoChrome ? 0u : br.F(6);
                if (ys != 0 || uvs != 0) anyCdef = true;
            }
            if (anyCdef) s.HasCdef = true;
        }

        // lr_params()
        if (!codedLossless && !s.HasIntraBc && seq.EnableRestoration)
        {
            bool anyLr = false;
            int planes = seq.MonoChrome ? 1 : 3;
            for (int i = 0; i < planes; i++)
                if (br.F(2) != 0) anyLr = true;
            if (anyLr) s.HasLoopRestoration = true;
        }

        s.FrameHeadersParsed++;
    }

    private static int TileLog2(int blkSize, int target)
    {
        int k = 0;
        while ((blkSize << k) < target) k++;
        return k;
    }
}
