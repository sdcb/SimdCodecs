# M1 — lossy (VP8) decode at libwebp parity (2026-10-25, AMD Ryzen 7 5800X)

## Machine

| | |
|---|---|
| CPU | AMD Ryzen 7 5800X (Zen 3), AVX2, BMI2, no AVX-512 (`Vector512.IsHardwareAccelerated` = false) |
| OS | Windows 11 25H2 (10.0.26200) |
| .NET | runtime 10.0.11, SDK 11.0.100-rc.2, X64 RyuJIT x86-64-v3 |
| libwebp | 1.6.0 from [`build-libwebp.sh`](build-libwebp.sh) (generic x86-64, SSE2/SSE4.1 dispatch) |
| Commits | baseline `28222bd` (decoder as at the start), current `2a34495` |

## Result (`ab --ablate`, BenchmarkDotNet medians, one run, ms)

| Input | libwebp | start | now | now ÷ start | now ÷ libwebp |
|---|---:|---:|---:|---:|---:|
| lossy opaque | 5.407 | 10.659 | 5.582 | 0.52× | 1.03× |
| lossy + alpha | 9.787 | 13.845 | 8.638 | 0.62× | 0.88× |
| lossless | 7.389 | 8.026 | 8.350 | (untouched) | 1.13× |

Lossy opaque is within run-to-run noise of libwebp (other runs in this session: 1.02×, 1.04×, 1.06×); it
is not yet clearly faster. Lossy + alpha is faster because the VP8 part now is; its ALPH part is
untouched (M2).

Stage costs now (current − current with the stage skipped, ms):

| Input | alpha | loop filter | residual | prediction | MB copy | output | entropy only |
|---|---:|---:|---:|---:|---:|---:|---:|
| lossy opaque | — | 0.34 | 0.24 | 0.24 | (−0.36)* | 0.58 | 3.57 |
| lossy + alpha | 2.87 | 0.82 | (−0.03)* | 0.50 | 0.13 | 0.90 | 3.58 |

\* below the noise of the run; "MB copy" is now only the per-macroblock above-row setup and the once-per-row
copy of the unfiltered bottom row. At the start the split was entropy 5.6, loop filter 3.2, output 1.2,
prediction 0.8 ms (m0). Entropy decoding is now ~64% of the lossy decode.

## Memory

Managed allocation per `DecodeTo` (MemoryDiagnoser): 376 → 320 B (lossy opaque), 592 → 536 B (alpha),
536 B (lossless). libwebp allocates natively, which that column cannot see; `peakmem` (fresh process per
decode, after a warm-up on a tiny file of the same kind, destination already touched; MiB):

| Input | libwebp first decode | start first decode | now first decode | now steady |
|---|---:|---:|---:|---:|
| lossy opaque | 0.24 | 6.58 | 0.35 | 0.02 |
| lossy + alpha | 2.48 | 17.07 | 11.05 | 0.02 |
| lossless | 8.17 | 8.34 | 8.34 | 0.03 |

The lossy decoder no longer holds frame-sized planes: rows are reconstructed into a cache of
16 + 8 luma / 8 + 6 chroma rows and go to RGBA while still cached. The remaining 11 MiB of the alpha
input is the ALPH plane (VP8L sub-decode into a full ARGB `int[]`, then a byte plane) — M2.

## What changed (one commit each, numbers in the commit messages)

1. **Wide-window boolean decoder** (`5f297c8`): 56 buffered bits refilled 7 bytes at a time with one
   big-endian load; LZCNT renormalisation. Checked against an independent arithmetic encoder and a
   bit-serial RFC 6386 §7 decoder.
2. **Per-macroblock token parsing** (`0ddbd18`): one inlined token walk for all 25 blocks with the arithmetic
   state in registers (a struct reached by ref kept it in memory: a store/load round trip per bit), the
   coefficient tree unrolled, coefficients dequantised (16-bit wrap kept) straight into their raster slot,
   bit-mask non-zero contexts, per-segment quantisers once per frame.
3. **Row pipeline** (`5d59602`): row cache instead of frame planes, loop filter per macroblock row, rows go to
   a sink (RGBA or tight planes for `Vp8Yuv`) while cached; no plane copy. Memory 6.6 → 0.9 MiB.
4. **16-lane loop filter** (`81a6722`): every edge as eight 16-byte vectors (16×8 transposes for vertical
   edges, U and V side by side), saturating signed-byte arithmetic, which is RFC 6386's c() exactly. Loop
   filter 2.8 → 0.55 ms.
5. **AVX2 YUV→RGBA** (`45cce79`): each (s·k) >> 8 term is one `vpmulhuw` with s in the high byte of a lane.
6. **AVX2 fancy upsampling** (`0d94335`): 32 vertical / 16 horizontal pairs per step.
7. **In-cache reconstruction** (`2a34495`): prediction reads its row above from a small per-macroblock buffer
   (saved unfiltered) and its left column from the cache, so macroblocks are predicted and corrected in
   place; no scratch, no copy. Memory 0.9 → 0.35 MiB.

Tooling added on the way: `corpus` / `verify` (100 synthetic files encoded by the official `cwebp` across 20
option sets, compared byte for byte with libwebp: 103/103 identical after every step), `peakmem`, and a
separate `SkipMacroblockCopy` ablation.

## Rejected experiments

- **Branch-free bool decoder update** (arithmetic select instead of `?:`): entropy-only 3.54/3.63 → 4.01/3.98 ms.
  The JIT does not if-convert inside loops and turns the `?:` selects into three branches on the same bit;
  that version predicts better than any branch-free form tried. A raw-chain microbenchmark puts the
  dependency chain at 3.0 ns (13.5 cycles) per bool either way; the 571 K token bools of the opaque input
  cost ≥ 1.7 ms on the chain alone.
- **State update inside the bit branch** (classic if/else): entropy-only 3.73/3.72 ms.
- **Table-driven block order + dequantise after parsing + packed position table** (frees registers: the range
  stays in a register instead of a stack slot): entropy-only 4.02/3.98 ms, 3.99/4.12 with DCT_CAT1/2 back
  inline. Rejected.
- **Mode parsing with register-held state and unrolled key-frame trees** (90 K header bools): 3.57/3.86 vs
  3.54/3.63 ms, neutral.
- `DOTNET_TieredPGO=0` makes entropy decoding 20% slower (4.26 ms): dynamic PGO's block layout matters here.

## Where lossy goes next

Entropy decoding (3.6 ms) is the floor-setter. Remaining non-entropy work is ~2 ms (output 0.6, loop filter
0.3–0.8, prediction 0.25–0.5, residual 0.25): candidates are a SIMD IDCT fused with the add, vectorised
4×4 prediction, and pairing luma rows in the upsampler.
