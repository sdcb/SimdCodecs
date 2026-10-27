# M4 — final state of the WebP decoder optimisation (2026-10-25, AMD Ryzen 7 5800X)

## Machine

| | |
|---|---|
| CPU | AMD Ryzen 7 5800X (Zen 3), AVX2, BMI2, no AVX-512 (`Vector512.IsHardwareAccelerated` = false) |
| OS | Windows 11 25H2 (10.0.26200) |
| .NET | runtime 10.0.11, SDK 11.0.100-rc.2, X64 RyuJIT x86-64-v3 |
| libwebp | 1.6.0 from [`build-libwebp.sh`](build-libwebp.sh) (generic x86-64, SSE2/SSE4.1 dispatch), checked against the official `dwebp` |
| Commits | start `28222bd` (the decoder as it was, plus the benchmark tooling), final `7e23695` |

## Result — single thread, the three benchmark inputs (`ab --ablate`, BenchmarkDotNet medians, ms)

| Input | libwebp | start | now | now ÷ start | now ÷ libwebp |
|---|---:|---:|---:|---:|---:|
| lossy opaque | 5.384 | 10.868 | 4.713 | 0.43× | **0.88×** |
| lossy + alpha | 10.000 | 14.587 | 5.345 | 0.37× | **0.53×** |
| lossless | 7.383 | 8.337 | 5.322 | 0.64× | **0.72×** |

(At the start, in the [baseline file](baseline-2026-10-25-amd-ryzen-7-5800x.md): 2.04×, 1.50×, 1.09× of libwebp.)

Stage costs now (current − current with the stage skipped, ms): lossy opaque — entropy decoding (headers,
modes, tokens) 3.08 of 4.71, output 0.61, loop filter 0.24, residual 0.09; lossless — VP8L transforms 2.79
(predictor 2.16), output 0.24, the rest entropy decoding.

## Memory

| Input | managed bytes per `DecodeTo` (start → now) | `peakmem` first decode, MiB: libwebp | start | now | now steady |
|---|---|---:|---:|---:|---:|
| lossy opaque | 376 → **0** | 0.24 | 6.55 | **0.25** | 0.02 |
| lossy + alpha | 592 → **0** | 2.47 | 17.07 | 3.07 | 0.02 |
| lossless | 536 → **0** | 8.17 | 8.34 | **0.80** | 0.02 |

`peakmem`: fresh process per decode, after a warm-up on a tiny file of the same kind, RGBA destination
already touched; median of 3. A still-image `DecodeTo` into a caller buffer allocates nothing once warm
(`WebpAllocationTests`, tight and strided). Lossy decoding holds a few macroblock rows instead of frame
planes; a tightly packed lossless destination is the decoder's ARGB working buffer. Lossy + alpha is
0.6 MiB above libwebp: the alpha plane plus the VP8L sub-decoder's packed index image.

## The synthetic corpus — the honest picture

The benchmark inputs are flat `testsrc2` frames. The verification corpus (`corpus` / `verify`: 100 files
from the official `cwebp`, 20 option sets, photo-like noise + gradients + text, with and without alpha;
byte-identical to libwebp) is harder. `quick --files "*.webp" --rounds 8` in one run, start decoder as the
baseline:

| Content | Kind | files | now ÷ libwebp | start ÷ libwebp |
|---|---|---:|---|---|
| `alpha` 1920×1080 | lossy + alpha | 15 | 0.75 – 0.88 | 1.58 – 2.02 |
| `mixed` 1001×777 | lossy + alpha | 15 | 0.70 – 0.93 | 1.49 – 1.87 |
| `mixed` 1920×1080 | lossy (15)* | 15 | 0.98 – 1.05 | 2.13 – 2.78 |
| `noisy` 640×480 | lossy (15)* | 15 | 0.87 – 1.13 | 1.79 – 2.53 |
| all four, 640×480 – 1920×1080 | lossless (exact, near-lossless 60, z0, z6, z9) | 20 | 0.70 – 0.93 | 1.44 – 1.99 |
| `small` 33×17 | all kinds | 20 | 0.50 – 1.00 | 0.45 – 2.33 |

\* These two sources are opaque, so their four "alpha" option sets come out as plain lossy files (no ALPH
chunk; same size as `q75-simple`).

**Lossless, and lossy with a real alpha channel: faster than libwebp on every file. Opaque lossy on noisy,
token-heavy content: parity, not a win.** Separate runs of just those files put `mixed` 1920×1080 lossy at 0.95–1.03×,
0.94–0.97× and 1.01–1.05×, and `noisy` 640×480 lossy at 0.96–1.00× (the outlier above, 1.13×, was 1.00× when
re-run): within run-to-run variation of libwebp, sometimes ahead, sometimes behind. There, entropy decoding
is ~75% of the decode (`mixed-q75-m6`: 11.7 of 15.7 ms), and VP8's boolean decoder is one serial dependency
chain per bit — ~13 cycles per bool on this core whatever the source form (raw-chain microbenchmark, M1),
with data-dependent branches that mispredict at the content's entropy. Everything else in the lossy decode
(prediction, IDCT, loop filter, upsampling and conversion) is now ~25% of it. I found no further source-level
change to the token walk that the end-to-end numbers accepted (see the rejected experiments in M1–M3 and
below).

## Opt-in multithreading (reported separately)

`WebpDecodeOptions { Multithreaded = true }` (new `DecodeTo` / `TryDecodeTo` overloads; the default stays
single-threaded, like libwebp's simple API): the ALPH plane of a lossy image is decoded on a thread-pool
thread while the calling thread decodes the VP8 frame; rows emitted before the plane is published get their
alpha in a vector pass afterwards. Same run as the corpus table:

| Content | multithreaded ÷ single-threaded | multithreaded ÷ libwebp |
|---|---|---|
| `alpha` 1920×1080 lossy + alpha | 0.68 – 0.88 (another run) | 0.49 – 0.85 |
| `mixed` 1001×777 lossy + alpha | 0.65 – 0.85 | 0.49 – 0.80 |
| opaque lossy, lossless | 1.00 (same code path) | as single-threaded |

Not done, measured as an opportunity: a row pipeline for opaque lossy (loop filter + upsampling/conversion of
macroblock row *n* on a second thread while row *n + 1* is decoded). Their stage costs bound the gain at about
1.6 of 4.7 ms on the benchmark input and ~2 of 15.7 ms on `mixed-q75-m6`; it needs frame-height row caches
(memory) and a sink that can cross threads, so it was left out.

## What changed since the start (one commit each; numbers in the commit messages and in M1–M3)

- **Tooling**: same-process A/B against a baseline commit in its own `AssemblyLoadContext` (`baseline`, `ab`,
  `quick`), stage ablation hooks, `peakmem`, the `corpus` / `verify` byte-exactness corpus, `quick --files`,
  `--mt`, a warm-up floor so small files are not timed as tier-0 code.
- **Lossy** ([M1](m1-lossy-2026-10-25-amd-ryzen-7-5800x.md), [M2](m2-alpha-lossy-2026-10-25-amd-ryzen-7-5800x.md)):
  wide-window bool decoder keeping range − 1; per-macroblock token parsing with the state in locals;
  branch-free coefficient sign; macroblock modes parsed with the state in locals (`76f1de4`: `mixed` lossy
  1.00–1.07× → 0.94–0.97× of libwebp in that run, lossy opaque 5.14 → 4.75 ms); row pipeline into a small row
  cache with in-place reconstruction; 16-lane loop filter; vector IDCT fused with the add; vector 4×4
  prediction; AVX2 fancy upsampling and YUV→RGBA.
- **ALPH** (M2): colour indexing straight to alpha bytes, vector green extraction.
- **Lossless** ([M3](m3-lossless-2026-10-25-amd-ryzen-7-5800x.md)): two-level prefix-code tables, 64-bit bit
  reader, lazy colour cache, trivial-literal fast path; vector serial predictor modes; decode, transforms and
  swizzle in place in the caller's buffer.
- **Allocation** (`7e23695`): the container keeps a still image's frame as a struct; `DecodeTo` allocates 0 B.

## Correctness

- Every commit: `verify` 103/103 byte-identical with libwebp 1.6.0; the WebP tests in all three ISA tiers;
  for every new kernel or fast path an equivalence test against the scalar reference, and a deliberate break
  confirmed to fail it.
- Final: the full suite with `REQUIRE_ORACLES=1` — 1600 passed / 4 skipped (default), 1592 / 10
  (`DOTNET_EnableAVX2=0`), 1582 / 20 (`DOTNET_EnableHWIntrinsic=0`); the skips are the golden-regeneration
  helpers and ISA-gated equivalence tests, no oracle is skipped. `dotnet build SimdCodecs.slnx -c Release`:
  21 warnings, all pre-existing XML-doc warnings in other packages (Exif, Jbig2, Jpeg.GainMap, Png, Tiff),
  none in WebP.
- Goldens (`webp-oracle-golden.tsv`, `webp-animation-oracle-golden.tsv`) were never regenerated.

## Rejected experiments (since M3)

- **Branch-free extra bits** (THREE/FOUR, DCT_CAT1/2) like the sign: 0.96–1.00×, neutral.
- **Non-inlined token walk** owning the bool state (range back in a register): 0.99×, neutral.
- **VP8L 10-bit root tables**: 1.06–1.11× slower except z0. **`AggressiveOptimization` on the VP8L pixel
  loop**: ambiguous (0.91–1.11× by file).

## Status

The decoder is byte-exact with libwebp 1.6.0, allocation-free per still-image decode, at libwebp's memory
for lossy and far below it for lossless, and faster than SIMD libwebp on the benchmark inputs (0.88×, 0.53×,
0.72×), on every lossless file and every file with a real alpha channel in the corpus, and on small images. On opaque lossy images
with noisy, token-heavy content it is at parity with libwebp (0.94–1.05× across runs), bounded by the serial
boolean decoder. Measured on one machine (Zen 3, Windows); AdvSimd paths are covered by the equivalence tests
and CI, not timed here.
