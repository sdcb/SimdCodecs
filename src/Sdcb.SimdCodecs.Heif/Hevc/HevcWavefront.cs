namespace Sdcb.SimdCodecs.Heif.Hevc;

/// <summary>
/// The WPP wavefront (H.265 §9.3.1, §9.3.2.4): with <c>entropy_coding_sync_enabled_flag</c> each CTB row is its own
/// substream, starting at an entry point of the slice header with the contexts the row above had after its second
/// CTB. Rows are decoded in parallel, row r running two CTBs behind row r − 1 — which also covers every
/// reconstructed sample and side-information value it reads (left, above-left, above and above-right of a CTB).
/// </summary>
/// <remarks>
/// <para>
/// Only a picture of one slice segment takes this path. Each row checks that it ends where the serial decoder would
/// start the next one (end_of_slice_segment_flag 0, end_of_subset_one_bit, byte alignment) and that this is the next
/// row's entry point; the last row must end the slice. Any mismatch or exception abandons the attempt and the
/// caller decodes the picture serially, so the output, and how malformed data fails, are those of the serial decoder.
/// </para>
/// <para>
/// Row workers are copies of the picture's decoder (<see cref="HevcFrameDecoder(HevcFrameDecoder)"/>): they share the
/// picture and its side information, and each has its own CABAC engine, quantization state and scratch. Each row
/// writes only its own CTBs; a row publishes its progress with a release store after a CTB is complete, and the row
/// below reads it with an acquire load before touching anything that CTB wrote.
/// </para>
/// </remarks>
internal sealed partial class HevcFrameDecoder
{
    /// <summary>Pictures decoded as a wavefront so far in this process (for tests: the path must not silently fall back).</summary>
    internal static int WavefrontDecodes;

    /// <summary>Thrown inside a worker to abandon the wavefront; the caller falls back to serial decoding.</summary>
    private sealed class WavefrontAbandoned : Exception;

    private sealed class Wavefront
    {
        internal required HevcSliceHeader Header;
        internal required byte[] Rbsp;
        internal required int[] RowStart;   // unescaped byte offset of each row's substream
        internal required int[] Progress;   // CTBs completed per row
        internal required byte[][] Contexts; // per row, the contexts after its second CTB
        internal int NextRow;
        internal volatile bool Failed;
        internal readonly object Gate = new();
        internal int Waiters;

        /// <summary>Publishes row progress and wakes blocked rows. The exchange is a full fence, so a waiter that registered
        /// before this read of <see cref="Waiters"/> is woken, and one that registers after it sees the new progress.</summary>
        internal void Publish(int row, int ctbs)
        {
            Interlocked.Exchange(ref Progress[row], ctbs);
            if (Volatile.Read(ref Waiters) > 0) Wake();
        }

        internal void Fail()
        {
            Failed = true;
            Wake();
        }

        private void Wake()
        {
            lock (Gate) Monitor.PulseAll(Gate);
        }
    }

    /// <summary>A row worker over <paramref name="shared"/>'s picture and side information.</summary>
    private HevcFrameDecoder(HevcFrameDecoder shared)
    {
        _sps = shared._sps;
        _pps = shared._pps;
        _pic = shared._pic;
        _w4 = shared._w4;
        _h4 = shared._h4;
        _blocks = shared._blocks;
        _ctDepth = shared._ctDepth;
        _intraMode = shared._intraMode;
        _qpY = shared._qpY;
        _noFilter = shared._noFilter;
        _decoded = shared._decoded;
        _edgeVer = shared._edgeVer;
        _edgeHor = shared._edgeHor;
        _ctbSliceAddr = shared._ctbSliceAddr;
        _ctbSlice = shared._ctbSlice;
        _sao = shared._sao;
        _zscan = shared._zscan;
        _zscanShift = shared._zscanShift;
        _qpBdOffsetY = shared._qpBdOffsetY;
        _qpBdOffsetC = shared._qpBdOffsetC;
        _log2MinCuQpDeltaSize = shared._log2MinCuQpDeltaSize;
        _parallelFilters = shared._parallelFilters;
    }

    /// <summary>
    /// Whether <paramref name="slices"/> may be decoded as a wavefront: one slice segment covering the picture, WPP,
    /// one entry point per CTB row after the first, at least two rows, and a machine with more than one core.
    /// </summary>
    internal bool CanDecodeWavefront(IReadOnlyList<(int NalType, byte[] Rbsp, int[] Removed)> slices)
    {
        if (!_parallelFilters || Environment.ProcessorCount < 2 || slices.Count != 1 || !_pps.EntropyCodingSyncEnabled) return false;
        if (_sps.PicHeightInCtbs < 2) return false;
        return true;
    }

    /// <summary>
    /// Decodes the single slice segment as a wavefront; false (with the picture state undefined) when the attempt is
    /// abandoned, and the caller then decodes the picture serially with a fresh decoder.
    /// </summary>
    internal bool TryDecodeWavefront((int NalType, byte[] Rbsp, int[] Removed) slice, out HevcPicture? picture)
    {
        picture = null;
        HevcSliceHeader header;
        try
        {
            header = HevcSliceHeader.Parse(slice.Rbsp, slice.NalType, _sps, _pps, null);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        var rows = _sps.PicHeightInCtbs;
        if (!header.FirstSliceSegmentInPic || header.SegmentAddress != 0 || header.DependentSliceSegment ||
            header.EntryPointOffsets.Length != rows - 1)
        {
            return false;
        }

        // Entry points: escaped offsets from the first byte of slice_segment_data().
        var rowStart = new int[rows];
        rowStart[0] = header.DataOffset;
        var escaped = Nal.ToEscaped(header.DataOffset, slice.Removed);
        for (var r = 1; r < rows; r++)
        {
            escaped += header.EntryPointOffsets[r - 1];
            var unescaped = Nal.ToUnescaped(escaped, slice.Removed);
            if (unescaped <= rowStart[r - 1] || unescaped >= slice.Rbsp.Length) return false;
            rowStart[r] = (int)unescaped;
        }

        var state = new Wavefront
        {
            Header = header,
            Rbsp = slice.Rbsp,
            RowStart = rowStart,
            Progress = new int[rows],
            Contexts = new byte[rows][],
        };
        var workers = Math.Min(Environment.ProcessorCount, rows);
        try
        {
            Parallel.For(0, workers, _ =>
            {
                var worker = new HevcFrameDecoder(this);
                int row;
                while (!state.Failed && (row = Interlocked.Increment(ref state.NextRow) - 1) < rows)
                {
                    try
                    {
                        worker.DecodeWavefrontRow(state, row);
                    }
                    catch
                    {
                        state.Fail();
                        throw;
                    }
                }
            });
        }
        catch (AggregateException)
        {
            return false;
        }
        if (state.Failed) return false;

        FilterPicture();
        picture = _pic;
        Interlocked.Increment(ref WavefrontDecodes);
        return true;
    }

    private void DecodeWavefrontRow(Wavefront state, int row)
    {
        var width = _sps.PicWidthInCtbs;
        var header = state.Header;
        _slice = header;
        _cabac = new HevcCabac(state.Rbsp, state.RowStart[row]);
        _firstQgInSlice = row == 0;
        _lastCuQpY = header.SliceQpY;
        _ctbAddrRs = row * width;

        for (var x = 0; x < width; x++)
        {
            // The row above must have completed CTB x + 1 (or its last CTB).
            if (row > 0) WaitFor(state, row - 1, Math.Min(x + 2, width));

            if (x == 0)
            {
                // 9.3.1: the first CTB of a row takes the contexts of the row above after its second CTB when that
                // CTB exists (it is in the same slice: there is only one), else initialises them.
                if (row == 0 || width < 2) _cabac.InitContexts(header.SliceQpY, 0);
                else _cabac.LoadContexts(state.Contexts[row - 1]);
                _firstQgInCtbRow = true;
            }

            _ctbSliceAddr[_ctbAddrRs] = header.SliceAddrRs;
            _ctbSlice[_ctbAddrRs] = header;
            CodingTreeUnit(x, row);

            if (x == 1)
            {
                var saved = new byte[HevcCabacTables.Count];
                _cabac.SaveContexts(saved);
                state.Contexts[row] = saved;
            }

            var endOfSlice = _cabac.DecodeTerminate() == 1;
            _ctbAddrRs++;
            if (x == width - 1)
            {
                if (row == state.RowStart.Length - 1)
                {
                    if (!endOfSlice) throw new WavefrontAbandoned();
                }
                else
                {
                    if (endOfSlice || _cabac.DecodeTerminate() != 1) throw new WavefrontAbandoned();
                    // Where the serial decoder restarts the engine (byte_alignment() after end_of_subset_one_bit).
                    if ((int)((_cabac.BitPosition + 7) >> 3) != state.RowStart[row + 1]) throw new WavefrontAbandoned();
                }
            }
            else if (endOfSlice)
            {
                throw new WavefrontAbandoned();
            }

            state.Publish(row, x + 1);
        }
    }

    private static void WaitFor(Wavefront state, int row, int ctbs)
    {
        if (Volatile.Read(ref state.Progress[row]) >= ctbs) return;
        // A short spin covers the usual case (the row above is a CTB or two ahead and running); then block, so a waiting
        // row never sleeps on the timer while the row it waits for needs the core.
        for (var spin = 0; spin < 64; spin++)
        {
            Thread.SpinWait(20);
            if (Volatile.Read(ref state.Progress[row]) >= ctbs) return;
        }
        Interlocked.Increment(ref state.Waiters);
        try
        {
            lock (state.Gate)
            {
                while (Volatile.Read(ref state.Progress[row]) < ctbs)
                {
                    if (state.Failed) throw new WavefrontAbandoned();
                    Monitor.Wait(state.Gate);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref state.Waiters);
        }
    }
}
