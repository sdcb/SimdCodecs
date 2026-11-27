using System.Buffers;

namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// What the parse pass of a tile leaves for its reconstruction pass (<see cref="Av1TileDecoder"/> in
/// <c>Parse</c> / <c>Recon</c> mode): one <see cref="BlockRecord"/> per coded block in decode order, grouped by
/// superblock, one <see cref="TxRecord"/> per transform block that reads coefficients, and the nonzero coefficients
/// themselves. Entropy decoding never depends on reconstructed samples, so a tile can be parsed straight through and
/// reconstructed afterwards, superblock rows in a wavefront on several threads.
/// </summary>
internal sealed class Av1TileRecords : IDisposable
{
    internal readonly int MiRowStart, MiRowEnd, MiColStart, MiColEnd;
    internal readonly int SbSize4, SbRows, SbCols;

    /// <summary>First block of each superblock (raster order within the tile); entry SbRows * SbCols ends the list.</summary>
    internal readonly int[] SbBlockStart;

    /// <summary>
    /// Superblocks parsed so far (raster order), published after their records are complete, so a reconstruction
    /// worker can follow the parse pass superblock by superblock.
    /// </summary>
    internal readonly Av1Progress Parsed = new();

    // Arrays outgrown while a reconstruction worker may still be reading them: returned to the pool on Dispose, not
    // on Grow. A worker that read the old reference sees every record published before the swap in it.
    private readonly List<object> _retired = [];

    internal BlockRecord[] Blocks = ArrayPool<BlockRecord>.Shared.Rent(256);
    internal int BlockCount;
    internal TxRecord[] Txs = ArrayPool<TxRecord>.Shared.Rent(1024);
    internal int TxCount;

    /// <summary>Nonzero coefficients as <c>(value &lt;&lt; 10) | position</c>: |value| &lt; 2^20 and position &lt; 1024.</summary>
    internal int[] Coeffs = ArrayPool<int>.Shared.Rent(4096);
    internal int CoeffCount;

    /// <summary>Palette colours (3 x 8) and warp samples, addressed by <see cref="BlockRecord.ExtraStart"/>.</summary>
    internal int[] Extra = ArrayPool<int>.Shared.Rent(256);
    internal int ExtraCount;

    /// <summary>Palette colour maps (luma then chroma, rows of the block's width), from <see cref="BlockRecord.MapStart"/>.</summary>
    internal byte[] Maps = ArrayPool<byte>.Shared.Rent(1024);
    internal int MapCount;

    internal Av1TileRecords(int miRowStart, int miRowEnd, int miColStart, int miColEnd, int sbSize4)
    {
        MiRowStart = miRowStart;
        MiRowEnd = miRowEnd;
        MiColStart = miColStart;
        MiColEnd = miColEnd;
        SbSize4 = sbSize4;
        SbRows = (miRowEnd - miRowStart + sbSize4 - 1) / sbSize4;
        SbCols = (miColEnd - miColStart + sbSize4 - 1) / sbSize4;
        SbBlockStart = new int[SbRows * SbCols + 1];
    }

    internal ref BlockRecord AddBlock()
    {
        if (BlockCount == Blocks.Length)
            Grow(ref Blocks, BlockCount + 1);
        return ref Blocks[BlockCount++];
    }

    internal ref TxRecord AddTx()
    {
        if (TxCount == Txs.Length)
            Grow(ref Txs, TxCount + 1);
        return ref Txs[TxCount++];
    }

    internal Span<int> AddCoeffs(int count)
    {
        if (CoeffCount + count > Coeffs.Length)
            Grow(ref Coeffs, CoeffCount + count);
        var span = Coeffs.AsSpan(CoeffCount, count);
        CoeffCount += count;
        return span;
    }

    internal Span<int> AddExtra(int count)
    {
        if (ExtraCount + count > Extra.Length)
            Grow(ref Extra, ExtraCount + count);
        var span = Extra.AsSpan(ExtraCount, count);
        ExtraCount += count;
        return span;
    }

    internal Span<byte> AddMap(int count)
    {
        if (MapCount + count > Maps.Length)
            Grow(ref Maps, MapCount + count);
        var span = Maps.AsSpan(MapCount, count);
        MapCount += count;
        return span;
    }

    private void Grow<T>(ref T[] array, int needed)
    {
        // A frame's records are bounded by its size (a block or transform block per 4x4 luma unit at most, a
        // coefficient per sample), so doubling up to the request is bounded as well.
        var bigger = ArrayPool<T>.Shared.Rent(Math.Max(needed, array.Length * 2));
        array.AsSpan().CopyTo(bigger);
        _retired.Add(array);
        Volatile.Write(ref array, bigger);
    }

    public void Dispose()
    {
        foreach (object old in _retired)
        {
            switch (old)
            {
                case BlockRecord[] b: ArrayPool<BlockRecord>.Shared.Return(b); break;
                case TxRecord[] t: ArrayPool<TxRecord>.Shared.Return(t); break;
                case int[] i: ArrayPool<int>.Shared.Return(i); break;
                case byte[] m: ArrayPool<byte>.Shared.Return(m); break;
            }
        }
        _retired.Clear();
        ArrayPool<BlockRecord>.Shared.Return(Blocks);
        ArrayPool<TxRecord>.Shared.Return(Txs);
        ArrayPool<int>.Shared.Return(Coeffs);
        ArrayPool<int>.Shared.Return(Extra);
        ArrayPool<byte>.Shared.Return(Maps);
        Blocks = [];
        Txs = [];
        Coeffs = [];
        Extra = [];
        Maps = [];
    }
}

/// <summary>The block-level state the reconstruction of one coded block reads (§5.11.5 onwards).</summary>
internal struct BlockRecord
{
    internal int MiRow, MiCol, MiSize;
    internal BlockFlags Flags;
    internal byte SegmentId, YMode, UvMode, FilterIntraMode, PaletteSizeY, PaletteSizeUv, TxSize;
    internal sbyte AngleDeltaY, AngleDeltaUv, CflAlphaU, CflAlphaV;
    internal short QIndex;
    internal byte MotionMode, InterIntraMode, WedgeIndex, WedgeSign, CompoundType, MaskType, NumWarpSamples;
    internal int TxStart, ExtraStart, MapStart;
}

[Flags]
internal enum BlockFlags : ushort
{
    None = 0,
    HasChroma = 1 << 0,
    AvailU = 1 << 1,
    AvailL = 1 << 2,
    AvailUChroma = 1 << 3,
    AvailLChroma = 1 << 4,
    Skip = 1 << 5,
    Lossless = 1 << 6,
    IsInter = 1 << 7,
    UseIntrabc = 1 << 8,
    UseFilterIntra = 1 << 9,
    IsInterIntra = 1 << 10,
    WedgeInterIntra = 1 << 11,
}

/// <summary>One transform block that read coefficients: its type, end of block and nonzero coefficients.</summary>
internal struct TxRecord
{
    internal int Eob;
    internal int CoeffStart;
    internal int CoeffCount;
    internal byte TxType;
}

/// <summary>
/// A monotonically increasing count that one thread publishes and others wait on: a short spin, then a real wait,
/// so idle reconstruction workers do not take CPU time (or an SMT sibling's share) from the parse threads.
/// </summary>
internal sealed class Av1Progress
{
    private int _value;
    private int _waiters;

    internal int Value => Volatile.Read(ref _value);

    internal void Publish(int value)
    {
        // A full fence (not a release store), so the waiter count read below cannot pass the value write: a waiter that
        // registered and then saw the old value is always pulsed.
        Interlocked.Exchange(ref _value, value);
        if (Volatile.Read(ref _waiters) != 0)
        {
            lock (this)
                Monitor.PulseAll(this);
        }
    }

    /// <summary>Wakes every waiter (after a failure elsewhere; they re-check the failure flag they wait with).</summary>
    internal void Abort()
    {
        lock (this)
            Monitor.PulseAll(this);
    }

    /// <summary>Waits until the count reaches <paramref name="target"/>; false if <paramref name="failed"/> got set.</summary>
    internal bool WaitFor(int target, ref int failed)
    {
        var spin = new SpinWait();
        for (int i = 0; i < 64; i++)
        {
            if (Volatile.Read(ref _value) >= target) return true;
            if (Volatile.Read(ref failed) != 0) return false;
            spin.SpinOnce(sleep1Threshold: -1); // never Sleep(1): that is a whole timer tick on Windows
        }
        lock (this)
        {
            Interlocked.Increment(ref _waiters);
            try
            {
                while (Volatile.Read(ref _value) < target)
                {
                    if (Volatile.Read(ref failed) != 0) return false;
                    // A timeout as well, so a missed pulse costs a millisecond and never a hang.
                    Monitor.Wait(this, 1);
                }
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _waiters);
            }
        }
    }
}
