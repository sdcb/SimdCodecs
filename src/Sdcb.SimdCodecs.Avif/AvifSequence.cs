using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Sdcb.SimdCodecs.Avif.Av1;
using Sdcb.SimdCodecs.Avif.Isobmff;

namespace Sdcb.SimdCodecs.Avif;

/// <summary>
/// ISOBMFF <c>moov</c>/<c>trak</c>/<c>stbl</c> parser and stateful multi-frame decoder for animated
/// AVIF sequences (<c>avis</c>, ISO/IEC 14496-12 + AOM AVIF v1.1.0).
/// </summary>
internal sealed class AvifSequence
{
    private const uint BoxMoov = 0x6D6F6F76; // 'moov'
    private const uint BoxMvhd = 0x6D766864; // 'mvhd'
    private const uint BoxTrak = 0x7472616B; // 'trak'
    private const uint BoxTkhd = 0x746B6864; // 'tkhd'
    private const uint BoxTref = 0x74726566; // 'tref'
    private const uint BoxEdts = 0x65647473; // 'edts'
    private const uint BoxElst = 0x656C7374; // 'elst'
    private const uint BoxMdia = 0x6D646961; // 'mdia'
    private const uint BoxMdhd = 0x6D646864; // 'mdhd'
    private const uint BoxHdlr = 0x68646C72; // 'hdlr'
    private const uint BoxMinf = 0x6D696E66; // 'minf'
    private const uint BoxStbl = 0x7374626C; // 'stbl'
    private const uint BoxStsd = 0x73747364; // 'stsd'
    private const uint BoxStts = 0x73747473; // 'stts'
    private const uint BoxStsc = 0x73747363; // 'stsc'
    private const uint BoxStsz = 0x7374737A; // 'stsz'
    private const uint BoxStco = 0x7374636F; // 'stco'
    private const uint BoxCo64 = 0x636F3634; // 'co64'
    private const uint BoxStss = 0x73747373; // 'stss'
    private const uint BoxAuxi = 0x61757869; // 'auxi'
    private const uint HdlrPict = 0x70696374; // 'pict'
    private const uint HdlrVide = 0x76696465; // 'vide'
    private const uint HdlrAuxv = 0x61757876; // 'auxv'
    private const uint ColrNclx = 0x6E636C78;
    private const uint ColrProf = 0x70726F66;
    private const uint ColrRicc = 0x72494343;

    private const int MaxFrames = 100_000;

    internal readonly record struct SampleSlice(int Offset, int Length);

    internal sealed class Track
    {
        internal uint TrackId;
        internal uint HandlerType;
        internal uint MediaTimescale;
        internal ulong MediaDuration;
        internal ulong TrackDuration;
        internal bool TrackDurationInfinite;
        internal bool EditListRepeat;
        internal ulong EditSegmentDuration;
        internal int Width;
        internal int Height;
        internal AvifContainer.Av1Config? Av1Config;
        internal AvifContainer.Nclx? NclxColour;
        internal byte[]? IccProfile;
        internal AvifContainer.Clli? ContentLightLevel;
        internal List<AvifContainer.Transform> Transforms { get; } = [];
        internal List<uint> AuxlRefs { get; } = [];
        internal string? AuxTypeUrn;
        internal SampleSlice[] Samples = [];
        internal uint[] SampleDeltas = [];
        internal int[]? SyncSamples; // 0-based sorted indices, or null if all samples are sync
    }

    internal Track Primary { get; }
    internal Track? Alpha { get; }
    internal int FrameCount { get; }
    internal int LoopCount { get; }
    internal TimeSpan[] Durations { get; }

    internal int Width => Primary.Width;
    internal int Height => Primary.Height;
    internal int BitDepth => Primary.Av1Config!.Value.BitDepth;
    internal bool HasAlpha => Alpha is not null;
    internal byte[]? IccProfile => Primary.IccProfile;
    internal AvifContainer.Nclx? NclxColour => Primary.NclxColour;
    internal List<AvifContainer.Transform> Transforms => Primary.Transforms;

    private AvifSequence(Track primary, Track? alpha, int frameCount, int loopCount, TimeSpan[] durations)
    {
        Primary = primary;
        Alpha = alpha;
        FrameCount = frameCount;
        LoopCount = loopCount;
        Durations = durations;
    }

    internal (int Width, int Height) DisplaySize() => AvifContainer.DisplaySize(Width, Height, Transforms);

    internal static bool TryParse(ReadOnlySpan<byte> data, [NotNullWhen(true)] out AvifSequence? sequence)
    {
        sequence = null;
        try
        {
            var top = new BoxReader(data);
            int moovStart = -1, moovEnd = -1;
            while (top.TryReadBox(out uint type, out int pStart, out int bEnd))
            {
                if (type == BoxMoov)
                {
                    moovStart = pStart;
                    moovEnd = bEnd;
                    break;
                }
            }
            if (moovStart < 0)
                return false;

            uint movieTimescale = 1000;
            ulong movieDuration = 0;
            bool movieDurationInfinite = false;
            var tracks = new List<Track>();

            var moov = new BoxReader(data, moovStart, moovEnd);
            while (moov.TryReadBox(out uint type, out int pStart, out int bEnd))
            {
                if (type == BoxMvhd)
                {
                    ParseMvhd(data[pStart..bEnd], out movieTimescale, out movieDuration, out movieDurationInfinite);
                }
                else if (type == BoxTrak)
                {
                    var track = ParseTrack(data, pStart, bEnd);
                    if (track is not null)
                        tracks.Add(track);
                }
            }

            Track? primary = null;
            foreach (var t in tracks)
            {
                if (t.HandlerType == HdlrPict && t.Av1Config is not null && t.Samples.Length > 0)
                {
                    primary = t;
                    break;
                }
            }
            if (primary is null)
            {
                foreach (var t in tracks)
                {
                    if (t.HandlerType == HdlrVide && t.Av1Config is not null && t.Samples.Length > 0)
                    {
                        primary = t;
                        break;
                    }
                }
            }
            if (primary is null)
                return false;

            Track? alpha = null;
            foreach (var t in tracks)
            {
                if (ReferenceEquals(t, primary) || t.Av1Config is null || t.Samples.Length == 0)
                    continue;
                if (!t.AuxlRefs.Contains(primary.TrackId))
                    continue;
                bool isAlphaUrn = t.AuxTypeUrn == AvifContainer.AlphaUrn;
                bool isAuxvMono = t.HandlerType == HdlrAuxv && t.Av1Config.Value.Monochrome;
                if (isAlphaUrn || isAuxvMono)
                {
                    alpha = t;
                    break;
                }
            }

            int frameCount = alpha is not null
                ? Math.Min(primary.Samples.Length, alpha.Samples.Length)
                : primary.Samples.Length;
            if (frameCount <= 0)
                return false;

            // Compute loop count per ISO/IEC 14496-12 elst + MIAF/AVIF avis:
            // 0 = infinite loop, >= 1 = finite play count.
            int loopCount;
            if (primary.EditListRepeat)
            {
                if (movieDurationInfinite || primary.TrackDurationInfinite || primary.EditSegmentDuration == 0)
                {
                    loopCount = 0;
                }
                else
                {
                    ulong totalDur = primary.TrackDuration > 0 ? primary.TrackDuration : movieDuration;
                    double repeats = (double)totalDur / primary.EditSegmentDuration;
                    loopCount = repeats > 0 && repeats < int.MaxValue
                        ? Math.Max(1, (int)Math.Round(repeats))
                        : 0;
                }
            }
            else
            {
                loopCount = 1;
            }

            uint ts = primary.MediaTimescale > 0 ? primary.MediaTimescale : (movieTimescale > 0 ? movieTimescale : 1000u);
            var durations = new TimeSpan[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                uint delta = i < primary.SampleDeltas.Length ? primary.SampleDeltas[i] : 0;
                long ticks = (long)Math.Round((double)delta * TimeSpan.TicksPerSecond / ts);
                durations[i] = TimeSpan.FromTicks(Math.Max(0, ticks));
            }

            sequence = new AvifSequence(primary, alpha, frameCount, loopCount, durations);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static void ParseMvhd(ReadOnlySpan<byte> body, out uint timescale, out ulong duration, out bool infinite)
    {
        timescale = 1000;
        duration = 0;
        infinite = false;
        if (body.Length < 4) return;
        byte ver = body[0];
        if (ver == 0 && body.Length >= 20)
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
            uint d = BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
            duration = d;
            infinite = d == uint.MaxValue;
        }
        else if (ver == 1 && body.Length >= 32)
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(body[20..]);
            ulong d = BinaryPrimitives.ReadUInt64BigEndian(body[24..]);
            duration = d;
            infinite = d == ulong.MaxValue;
        }
    }

    private static Track? ParseTrack(ReadOnlySpan<byte> data, int start, int end)
    {
        var track = new Track();
        var reader = new BoxReader(data, start, end);
        while (reader.TryReadBox(out uint type, out int pStart, out int bEnd))
        {
            switch (type)
            {
                case BoxTkhd:
                    ParseTkhd(data[pStart..bEnd], track);
                    break;
                case BoxTref:
                {
                    var tref = new BoxReader(data, pStart, bEnd);
                    while (tref.TryReadBox(out uint rt, out int rs, out int re))
                    {
                        if (rt == AvifContainer.RefAuxl)
                        {
                            for (int p = rs; p + 4 <= re; p += 4)
                                track.AuxlRefs.Add(BinaryPrimitives.ReadUInt32BigEndian(data[p..]));
                        }
                    }
                    break;
                }
                case BoxEdts:
                {
                    var edts = new BoxReader(data, pStart, bEnd);
                    while (edts.TryReadBox(out uint et, out int es, out int ee))
                    {
                        if (et == BoxElst)
                            ParseElst(data[es..ee], track);
                    }
                    break;
                }
                case BoxMdia:
                    ParseMdia(data, pStart, bEnd, track);
                    break;
            }
        }
        return track.Samples.Length > 0 ? track : null;
    }

    private static void ParseTkhd(ReadOnlySpan<byte> body, Track track)
    {
        if (body.Length < 4) return;
        byte ver = body[0];
        if (ver == 0 && body.Length >= 24)
        {
            track.TrackId = BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
            uint d = BinaryPrimitives.ReadUInt32BigEndian(body[20..]);
            track.TrackDuration = d;
            track.TrackDurationInfinite = d == uint.MaxValue;
        }
        else if (ver == 1 && body.Length >= 36)
        {
            track.TrackId = BinaryPrimitives.ReadUInt32BigEndian(body[20..]);
            ulong d = BinaryPrimitives.ReadUInt64BigEndian(body[28..]);
            track.TrackDuration = d;
            track.TrackDurationInfinite = d == ulong.MaxValue;
        }
    }

    private static void ParseElst(ReadOnlySpan<byte> body, Track track)
    {
        if (body.Length < 8) return;
        byte ver = body[0];
        uint flags = ((uint)body[1] << 16) | ((uint)body[2] << 8) | body[3];
        track.EditListRepeat = (flags & 1) != 0;
        uint count = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
        int pos = 8;
        for (uint i = 0; i < count; i++)
        {
            ulong segDur;
            long mediaTime;
            if (ver == 0)
            {
                if (pos + 12 > body.Length) break;
                segDur = BinaryPrimitives.ReadUInt32BigEndian(body[pos..]);
                mediaTime = BinaryPrimitives.ReadInt32BigEndian(body[(pos + 4)..]);
                pos += 12;
            }
            else if (ver == 1)
            {
                if (pos + 20 > body.Length) break;
                segDur = BinaryPrimitives.ReadUInt64BigEndian(body[pos..]);
                mediaTime = BinaryPrimitives.ReadInt64BigEndian(body[(pos + 8)..]);
                pos += 20;
            }
            else
            {
                break;
            }
            if (mediaTime >= 0 && track.EditSegmentDuration == 0)
                track.EditSegmentDuration = segDur;
        }
    }

    private static void ParseMdia(ReadOnlySpan<byte> data, int start, int end, Track track)
    {
        var mdia = new BoxReader(data, start, end);
        while (mdia.TryReadBox(out uint type, out int pStart, out int bEnd))
        {
            switch (type)
            {
                case BoxMdhd:
                {
                    var body = data[pStart..bEnd];
                    if (body.Length >= 4)
                    {
                        byte ver = body[0];
                        if (ver == 0 && body.Length >= 20)
                        {
                            track.MediaTimescale = BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
                            track.MediaDuration = BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
                        }
                        else if (ver == 1 && body.Length >= 32)
                        {
                            track.MediaTimescale = BinaryPrimitives.ReadUInt32BigEndian(body[20..]);
                            track.MediaDuration = BinaryPrimitives.ReadUInt64BigEndian(body[24..]);
                        }
                    }
                    break;
                }
                case BoxHdlr:
                {
                    var body = data[pStart..bEnd];
                    if (body.Length >= 12)
                        track.HandlerType = BinaryPrimitives.ReadUInt32BigEndian(body[8..]);
                    break;
                }
                case BoxMinf:
                {
                    var minf = new BoxReader(data, pStart, bEnd);
                    while (minf.TryReadBox(out uint mt, out int ms, out int me))
                    {
                        if (mt == BoxStbl)
                            ParseStbl(data, ms, me, track);
                    }
                    break;
                }
            }
        }
    }

    private static void ParseStbl(ReadOnlySpan<byte> data, int start, int end, Track track)
    {
        var stscEntries = new List<(uint FirstChunk, uint SamplesPerChunk)>();
        uint uniformSampleSize = 0;
        uint sampleCount = 0;
        uint[]? sampleSizes = null;
        ulong[]? chunkOffsets = null;
        var sttsEntries = new List<(uint Count, uint Delta)>();

        var stbl = new BoxReader(data, start, end);
        while (stbl.TryReadBox(out uint type, out int pStart, out int bEnd))
        {
            var body = data[pStart..bEnd];
            switch (type)
            {
                case BoxStsd:
                    ParseStsd(data, pStart, bEnd, track);
                    break;
                case BoxStts:
                    if (body.Length >= 8)
                    {
                        uint cnt = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                        int maxEntries = Math.Min((int)Math.Min(cnt, MaxFrames), (body.Length - 8) / 8);
                        for (int i = 0; i < maxEntries; i++)
                        {
                            uint sc = BinaryPrimitives.ReadUInt32BigEndian(body[(8 + i * 8)..]);
                            uint sd = BinaryPrimitives.ReadUInt32BigEndian(body[(12 + i * 8)..]);
                            sttsEntries.Add((sc, sd));
                        }
                    }
                    break;
                case BoxStsc:
                    if (body.Length >= 8)
                    {
                        uint cnt = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                        int maxEntries = Math.Min((int)Math.Min(cnt, MaxFrames), (body.Length - 8) / 12);
                        for (int i = 0; i < maxEntries; i++)
                        {
                            uint fc = BinaryPrimitives.ReadUInt32BigEndian(body[(8 + i * 12)..]);
                            uint spc = BinaryPrimitives.ReadUInt32BigEndian(body[(12 + i * 12)..]);
                            stscEntries.Add((fc, spc));
                        }
                    }
                    break;
                case BoxStsz:
                    if (body.Length >= 12)
                    {
                        uniformSampleSize = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                        sampleCount = BinaryPrimitives.ReadUInt32BigEndian(body[8..]);
                        if (sampleCount > MaxFrames)
                            throw new InvalidDataException($"AVIF sequence sample count {sampleCount} exceeds limit.");
                        if (uniformSampleSize == 0 && sampleCount > 0)
                        {
                            if (12 + (long)sampleCount * 4 > body.Length)
                                throw new InvalidDataException("Truncated 'stsz' sample size table.");
                            sampleSizes = new uint[sampleCount];
                            for (int i = 0; i < sampleCount; i++)
                                sampleSizes[i] = BinaryPrimitives.ReadUInt32BigEndian(body[(12 + i * 4)..]);
                        }
                    }
                    break;
                case BoxStco:
                    if (body.Length >= 8)
                    {
                        uint cnt = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                        if (cnt > MaxFrames || 8 + (long)cnt * 4 > body.Length)
                            throw new InvalidDataException("Invalid 'stco' chunk offset table.");
                        chunkOffsets = new ulong[cnt];
                        for (int i = 0; i < cnt; i++)
                            chunkOffsets[i] = BinaryPrimitives.ReadUInt32BigEndian(body[(8 + i * 4)..]);
                    }
                    break;
                case BoxCo64:
                    if (body.Length >= 8)
                    {
                        uint cnt = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                        if (cnt > MaxFrames || 8 + (long)cnt * 8 > body.Length)
                            throw new InvalidDataException("Invalid 'co64' chunk offset table.");
                        chunkOffsets = new ulong[cnt];
                        for (int i = 0; i < cnt; i++)
                            chunkOffsets[i] = BinaryPrimitives.ReadUInt64BigEndian(body[(8 + i * 8)..]);
                    }
                    break;
                case BoxStss:
                    if (body.Length >= 8)
                    {
                        uint cnt = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                        if (cnt > MaxFrames || 8 + (long)cnt * 4 > body.Length)
                            throw new InvalidDataException("Invalid 'stss' sync sample table.");
                        var sync = new int[cnt];
                        for (int i = 0; i < cnt; i++)
                        {
                            uint oneBased = BinaryPrimitives.ReadUInt32BigEndian(body[(8 + i * 4)..]);
                            if (oneBased == 0 || oneBased > MaxFrames)
                                throw new InvalidDataException($"Invalid sync sample number {oneBased} in 'stss'.");
                            sync[i] = (int)(oneBased - 1);
                        }
                        Array.Sort(sync);
                        track.SyncSamples = sync;
                    }
                    break;
            }
        }

        if (sampleCount == 0 || chunkOffsets is null || chunkOffsets.Length == 0 || stscEntries.Count == 0)
            return;

        // Build per-sample byte ranges (ISO/IEC 14496-12 §8.7.4)
        var samples = new SampleSlice[sampleCount];
        int sampleIdx = 0;
        for (int e = 0; e < stscEntries.Count && sampleIdx < sampleCount; e++)
        {
            uint firstChunk = stscEntries[e].FirstChunk;
            uint spc = stscEntries[e].SamplesPerChunk;
            if (firstChunk == 0 || spc == 0)
                throw new InvalidDataException("Invalid 'stsc' entry.");
            uint nextFirstChunk = e + 1 < stscEntries.Count ? stscEntries[e + 1].FirstChunk : (uint)chunkOffsets.Length + 1;
            if (nextFirstChunk < firstChunk)
                throw new InvalidDataException("Non-monotonic 'stsc' first_chunk.");

            for (uint chunk1 = firstChunk; chunk1 < nextFirstChunk && sampleIdx < sampleCount; chunk1++)
            {
                if (chunk1 - 1 >= (uint)chunkOffsets.Length)
                    throw new InvalidDataException("Chunk index exceeds 'stco'/'co64' count.");
                ulong offset = chunkOffsets[chunk1 - 1];
                for (uint s = 0; s < spc && sampleIdx < sampleCount; s++)
                {
                    uint len = uniformSampleSize != 0 ? uniformSampleSize : sampleSizes![sampleIdx];
                    if (len == 0 || offset > (ulong)data.Length || len > (ulong)data.Length - offset)
                        throw new InvalidDataException("AVIF sequence sample lies outside file bounds.");
                    samples[sampleIdx++] = new SampleSlice((int)offset, (int)len);
                    offset += len;
                }
            }
        }
        if (sampleIdx != sampleCount)
            throw new InvalidDataException("AVIF sequence 'stsc'/'stco' does not cover all samples.");
        if (track.SyncSamples is { Length: > 0 } syncList && syncList[^1] >= sampleCount)
            throw new InvalidDataException("AVIF sequence 'stss' references a sample beyond sample count.");
        track.Samples = samples;

        // Expand 'stts' deltas
        var deltas = new uint[sampleCount];
        int dIdx = 0;
        foreach (var (count, delta) in sttsEntries)
        {
            int take = (int)Math.Min(count, (uint)(sampleCount - dIdx));
            for (int k = 0; k < take; k++)
                deltas[dIdx++] = delta;
            if (dIdx >= sampleCount) break;
        }
        track.SampleDeltas = deltas;
    }

    private static void ParseStsd(ReadOnlySpan<byte> data, int start, int end, Track track)
    {
        if (end - start < 8) return;
        var entries = new BoxReader(data, start + 8, end);
        while (entries.TryReadBox(out uint entryType, out int eStart, out int eEnd))
        {
            if (entryType != AvifContainer.ItemAv01 || eEnd - eStart < 78)
                continue;

            int w = BinaryPrimitives.ReadUInt16BigEndian(data[(eStart + 24)..]);
            int h = BinaryPrimitives.ReadUInt16BigEndian(data[(eStart + 26)..]);
            if (w <= 0 || h <= 0 || w > AvifContainer.MaxDimension || h > AvifContainer.MaxDimension || (long)w * h > AvifContainer.MaxPixels)
                throw new InvalidDataException("Invalid 'av01' VisualSampleEntry dimensions.");
            track.Width = w;
            track.Height = h;

            var children = new BoxReader(data, eStart + 78, eEnd);
            while (children.TryReadBox(out uint ct, out int cs, out int ce))
            {
                var body = data[cs..ce];
                switch (ct)
                {
                    case AvifContainer.PropAv1C:
                        track.Av1Config = AvifContainer.ParseAv1C(data, cs, ce - cs);
                        break;
                    case AvifContainer.PropColr when body.Length >= 4:
                    {
                        uint colrType = BinaryPrimitives.ReadUInt32BigEndian(body);
                        if (colrType == ColrNclx && body.Length >= 11 && track.NclxColour is null)
                            track.NclxColour = new AvifContainer.Nclx((body[4] << 8) | body[5], (body[6] << 8) | body[7], (body[8] << 8) | body[9], (body[10] & 0x80) != 0);
                        else if ((colrType == ColrProf || colrType == ColrRicc) && track.IccProfile is null)
                            track.IccProfile = body[4..].ToArray();
                        break;
                    }
                    case AvifContainer.PropClli when body.Length >= 4:
                        track.ContentLightLevel = new AvifContainer.Clli(
                            BinaryPrimitives.ReadUInt16BigEndian(body),
                            BinaryPrimitives.ReadUInt16BigEndian(body[2..]));
                        break;
                    case AvifContainer.PropIrot when body.Length >= 1:
                        track.Transforms.Add(new AvifContainer.Transform(AvifContainer.PropIrot, body[0] & 3, 0, null));
                        break;
                    case AvifContainer.PropImir when body.Length >= 1:
                        track.Transforms.Add(new AvifContainer.Transform(AvifContainer.PropImir, body[0] & 1, 0, null));
                        break;
                    case AvifContainer.PropClap when body.Length >= 32:
                    {
                        var clap = new long[8];
                        for (int i = 0; i < 8; i++)
                        {
                            uint raw = BinaryPrimitives.ReadUInt32BigEndian(body[(i * 4)..]);
                            clap[i] = i is 4 or 6 ? unchecked((int)raw) : raw;
                        }
                        track.Transforms.Add(new AvifContainer.Transform(AvifContainer.PropClap, 0, 0, clap));
                        break;
                    }
                    case BoxAuxi when body.Length > 4:
                    {
                        var urnBytes = body[4..];
                        int nul = urnBytes.IndexOf((byte)0);
                        if (nul >= 0) urnBytes = urnBytes[..nul];
                        track.AuxTypeUrn = Encoding.ASCII.GetString(urnBytes);
                        break;
                    }
                }
            }
            break;
        }
    }
}

/// <summary>
/// Stateful sequential / random-access decoder over an <see cref="AvifSequence"/>.
/// Maintains persistent AV1 reference-frame sessions for primary and optional alpha tracks,
/// seeking to the nearest preceding sync sample (<c>stss</c>) on out-of-order access.
/// </summary>
internal sealed class AvifSequenceDecoder
{
    private readonly AvifSequence _seq;
    private readonly Av1DecoderSession _primarySession = new();
    private readonly Av1DecoderSession? _alphaSession;
    private int _lastIndex = -1;
    private AvifDecodedPlanes? _lastPlanes;

    internal AvifSequenceDecoder(AvifSequence sequence, ReadOnlySpan<byte> data)
    {
        _seq = sequence;
        InitConfig(_primarySession, sequence.Primary.Av1Config!.Value, data);
        if (sequence.Alpha is not null)
        {
            _alphaSession = new Av1DecoderSession();
            InitConfig(_alphaSession, sequence.Alpha.Av1Config!.Value, data);
        }
    }

    private static void InitConfig(Av1DecoderSession session, AvifContainer.Av1Config cfg, ReadOnlySpan<byte> data)
    {
        if (cfg.ConfigObusLength > 0)
            session.ParseConfigObus(data.Slice(cfg.ConfigObusOffset, cfg.ConfigObusLength));
    }

    internal AvifDecodedPlanes DecodeFramePlanes(ReadOnlySpan<byte> data, int index, CancellationToken cancellationToken = default)
    {
        if (index == _lastIndex && _lastPlanes is not null)
            return _lastPlanes;

        int startFrom;
        if (_lastIndex < 0 || index < _lastIndex)
        {
            startFrom = FindNearestSync(index);
            _primarySession.Reset();
            _alphaSession?.Reset();
        }
        else
        {
            int nearestSync = FindNearestSync(index);
            if (nearestSync > _lastIndex)
            {
                startFrom = nearestSync;
                _primarySession.Reset();
                _alphaSession?.Reset();
            }
            else
            {
                startFrom = _lastIndex + 1;
            }
        }

        Av1Picture? primaryPic = null;
        Av1Picture? alphaPic = null;
        for (int i = startFrom; i <= index; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ps = _seq.Primary.Samples[i];
            primaryPic = _primarySession.DecodeTemporalUnit(data.Slice(ps.Offset, ps.Length));

            if (_seq.Alpha is not null && _alphaSession is not null)
            {
                var aslice = _seq.Alpha.Samples[i];
                alphaPic = _alphaSession.DecodeTemporalUnit(data.Slice(aslice.Offset, aslice.Length));
            }
        }

        var planes = BuildDecodedPlanes(primaryPic!, alphaPic);
        _lastIndex = index;
        _lastPlanes = planes;
        return planes;
    }

    private int FindNearestSync(int index)
    {
        int[]? sync = _seq.Primary.SyncSamples;
        if (sync is null) return index; // every sample is a sync sample
        int best = 0;
        foreach (int s in sync)
        {
            if (s <= index) best = s;
            else break;
        }
        return best;
    }

    private AvifDecodedPlanes BuildDecodedPlanes(Av1Picture primary, Av1Picture? alpha)
    {
        int w = primary.Width;
        int h = primary.Height;
        int subX = primary.SubsamplingX;
        int subY = primary.SubsamplingY;
        bool mono = primary.Monochrome;

        var yPlane = ExtractTightPlane(primary, 0, w, h);
        ushort[]? cbPlane = mono ? null : ExtractTightPlane(primary, 1, (w + subX) >> subX, (h + subY) >> subY);
        ushort[]? crPlane = mono ? null : ExtractTightPlane(primary, 2, (w + subX) >> subX, (h + subY) >> subY);
        ushort[]? aPlane = null;
        int aBd = 8;
        bool aFull = true;
        if (alpha is not null)
        {
            if (alpha.Width != w || alpha.Height != h)
                throw new InvalidDataException($"AVIF sequence alpha dimensions {alpha.Width}x{alpha.Height} do not match primary {w}x{h}.");
            aPlane = ExtractTightPlane(alpha, 0, w, h);
            aBd = alpha.BitDepth;
            aFull = alpha.FullRange;
        }

        int matrix = _seq.NclxColour is { } nclx ? nclx.MatrixCoefficients : primary.MatrixCoefficients;
        bool fullRange = _seq.NclxColour is { } nclx2 ? nclx2.FullRange : primary.FullRange;

        return new AvifDecodedPlanes
        {
            Width = w,
            Height = h,
            BitDepth = primary.BitDepth,
            SubsamplingX = subX,
            SubsamplingY = subY,
            Monochrome = mono,
            Y = yPlane,
            Cb = cbPlane,
            Cr = crPlane,
            Alpha = aPlane,
            AlphaBitDepth = aBd,
            AlphaFullRange = aFull,
            MatrixCoefficients = matrix,
            FullRange = fullRange,
            IsPremultiplied = false,
        };
    }

    private static ushort[] ExtractTightPlane(Av1Picture pic, int planeIdx, int outW, int outH)
    {
        var dst = new ushort[outW * outH];
        ushort[] src = pic.Planes[planeIdx];
        int stride = pic.PlaneStrides[planeIdx];
        for (int y = 0; y < outH; y++)
            src.AsSpan(y * stride, outW).CopyTo(dst.AsSpan(y * outW));
        return dst;
    }
}
