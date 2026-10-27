# M2 — ALPH, and lossy past libwebp (2026-10-25, AMD Ryzen 7 5800X)

## Machine

| | |
|---|---|
| CPU | AMD Ryzen 7 5800X (Zen 3), AVX2, BMI2, no AVX-512 (`Vector512.IsHardwareAccelerated` = false) |
| OS | Windows 11 25H2 (10.0.26200) |
| .NET | runtime 10.0.11, SDK 11.0.100-rc.2, X64 RyuJIT x86-64-v3 |
| libwebp | 1.6.0 from [`build-libwebp.sh`](build-libwebp.sh) (generic x86-64, SSE2/SSE4.1 dispatch) |
| Commits | baseline `d23a110` (M1), current `41bd644` |

## Result (`ab --ablate`, BenchmarkDotNet medians, one run, ms)

| Input | libwebp | M1 | now | now ÷ M1 | now ÷ libwebp |
|---|---:|---:|---:|---:|---:|
| lossy opaque | 5.370 | 5.879 | 5.146 | 0.88× | 0.96× |
| lossy + alpha | 9.689 | 8.808 | 5.230 | 0.59× | 0.54× |
| lossless | 7.554 | 8.262 | 8.275 | (untouched) | 1.10× |

Stage costs now (current − current with the stage skipped, ms; single BDN run, ±0.2 ms):

| Input | alpha | loop filter | residual | prediction | output | entropy only |
|---|---:|---:|---:|---:|---:|---:|
| lossy opaque | — | 0.67 | 0.12 | 0.33 | 0.96 | 3.51 |
| lossy + alpha | 0.13 | 0.67 | 0.17 | (0.00)* | 0.75 | 3.10 |

\* below the noise of the run. The ALPH stage was 2.87 ms at M1.

### The synthetic corpus (`quick --files "*-1920x1080-*.webp" --rounds 10`, in-process medians)

The benchmark inputs are ffmpeg `testsrc2` frames: flat colour, few tokens. The `corpus` files
(`mixed`: photo-like noise + gradients + text; `alpha`: the same with a noisy alpha channel) are
harder and show the real spread:

| Group | now ÷ libwebp |
|---|---|
| `mixed` lossy (q10 … q100, 11 option sets) | 0.99× – 1.05× |
| `mixed` lossy + alpha (4 ALPH variants) | 1.00× – 1.05× |
| `alpha` lossy + alpha (16 option sets) | 0.85× – 0.96× |
| lossless (`mixed` and `alpha`, z0 … z9, exact, near-lossless) | 1.41× – 2.31× (M3) |

On token-heavy lossy files we are at parity, not ahead: entropy decoding is ~75% of the decode there
(`mixed-q75-m6`: entropy only 11.7 of 15.7 ms).

## Memory

Managed allocation per `DecodeTo` (MemoryDiagnoser): 320 B (lossy opaque), 536 B (alpha), 536 B
(lossless), unchanged. `peakmem` (fresh process per decode, after a warm-up on a tiny file of the same
kind, destination already touched; MiB, median of 3):

| Input | libwebp first decode | M1 first decode | now first decode | now steady |
|---|---:|---:|---:|---:|
| lossy opaque | 0.25 | 0.33 | 0.25 | 0.02 |
| lossy + alpha | 2.48 | 11.05 | 3.03 | 0.02 |
| lossless | 8.20 | 8.34 | 8.38 | 0.03 |

The ALPH plane no longer goes through a full-size ARGB `int[]`: with colour indexing as the last
transform (what `cwebp` emits for alpha), palette indices are expanded straight to alpha bytes. What
remains is the alpha plane itself plus the VP8L sub-decoder's packed index image.

## What changed (one commit each, numbers in the commit messages)

1. **ALPH straight to bytes** (`8fc6808`): when the alpha VP8L stream ends with colour indexing, a 256-entry
   LUT maps each packed index byte to its 1/2/4/8 alpha bytes (green of the palette entry, 0 past the
   palette); green extraction for the other case is vectorised. ALPH stage 2.87 → 0.36 ms, memory
   11.05 → 3.04 MiB.
2. **Bool decoder keeps range − 1** (`5738e59`): split − 1 = ((range − 1) · p) >> 8 is one multiply; the state
   update moved inside the bit branch. `mixed-q75-m6` 19.36 → 18.33 ms. Also adds `quick --files`.
3. **Vector IDCT fused with the add** (`60330a9`): both passes on four 32-bit lanes with 4×4 transposes,
   pass-1 results wrapped to 16 bits like the scalar code. `mixed-q75-m6` residual stage 2.6 → 1.33 ms.
4. **Branch-free coefficient sign** (`dc8f127`): the sign is a coin flip; the branch on it mispredicted half the
   time. Entropy only 12.61 → 11.72 ms on `mixed-q75-m6`; lossy opaque 5.70 → 5.06 ms in the same run
   (9–11%).
5. **Vector 4×4 intra prediction** (`41bd644`): all ten modes from one 16-byte edge vector; every directional
   pixel is an Avg3 or Avg2 of edge bytes (two rounding averages are exact), picked per mode with two byte
   shuffles. Prediction stage on `mixed-q75-m6` 1.20 → ~0.5 ms.

Every step: `verify` 103/103 byte-identical with libwebp 1.6.0, tests in all three ISA tiers, and an
equivalence test against the scalar reference that a deliberate break (wrong constant / wrong table row)
fails.

## Rejected experiments

- **Branch-free extra bits** (THREE/FOUR, DCT_CAT1/2 extras, same arithmetic as the sign): 0.96/1.00× on
  `mixed-q75-m6`, 0.99/1.00× lossy. Unlike the sign, these bits are rare enough not to matter.
- **Non-inlined token walk with its own copy of the bool state**: the inlined walk in `ParseResiduals`
  keeps range − 1 in a stack slot (register pressure from the per-block state); out of line it gets a
  register, but the call and the per-block load/store cost the same: 0.99/0.99×. The JIT also
  materialises each decoded bit and re-tests it after the join, which no source form tried avoids.
- VP8L: `Avx2.Shuffle` instead of `Vector256.Shuffle` for subtract-green / ARGB→RGBA: no change (the JIT
  already emits `vpshufb`).

## Where it goes next

Lossless (M3) is the largest gap (1.4–2.3× on the corpus). Entropy ≈ 5.3 ms and transforms ≈ 2.4 ms on
`lossless-1080p`: a 64-bit bit reader, table-driven Huffman decoding, vectorised predictor modes and
in-place transforms.
