# M0 — measurement tooling and first stage split (2026-10-25, AMD Ryzen 7 5800X)

No decoder change: this milestone adds the tools every later step is measured with.

## Machine

| | |
|---|---|
| CPU | AMD Ryzen 7 5800X (Zen 3), AVX2, BMI2, no AVX-512 (`Vector512.IsHardwareAccelerated` = false) |
| OS | Windows 11 25H2 (10.0.26200) |
| .NET | runtime 10.0.11, SDK 11.0.100-rc.2, X64 RyuJIT x86-64-v3 |
| libwebp | 1.6.0 from [`build-libwebp.sh`](build-libwebp.sh) (generic x86-64, SSE2/SSE4.1 dispatch) |
| Commit | baseline `7c7290a`, current = this commit (ablation hooks only) |

The official `libwebp-1.6.0-windows-x64.zip` (SHA-256 `48886f50…950c93f1`) supplies `dwebp` / `anim_dump` as
the test oracles (copied into the git-ignored `tests/Sdcb.SimdCodecs.Tests/Oracle/webp/bin/`). Its `dwebp -v`
reports 7 / 11 / 8 ms for lossy opaque / lossy + alpha / lossless, the SIMD speed; the benchmark's
`libwebp.dll` lands at 5.5 / 10.6 / 7.9 ms, so it is not a scalar build.

## Tools added

- `baseline [git-ref]` builds a commit's `Sdcb.SimdCodecs.Webp` into `.cache/baseline/<sha>` (temporary git
  worktree) and loads it into its own `AssemblyLoadContext`, so A and B run in the same process.
- `ab [--ablate] [--inputs RE]`: BenchmarkDotNet run of baseline vs current vs libwebp, interleaved per input,
  after checking that the current output is byte-identical to libwebp (and reporting whether it still equals the
  baseline). `--ablate` adds the current decoder with one stage switched off (`WebpAblation`, internal, through
  `WebpDiagnostics.DecodeTo`); a stage's cost is the time that disappears.
- `quick [--ablate] [--inputs RE] [--rounds N]`: the same comparison in-process, interleaved rounds, medians —
  seconds instead of minutes, for the inner loop only.

## Starting point (`ab --ablate`, BenchmarkDotNet medians, ms)

| Input | libwebp | baseline | current | current ÷ libwebp |
|---|---:|---:|---:|---:|
| lossy opaque | 5.460 | 11.331 | 11.037 | 2.02× |
| lossy + alpha | 10.581 | 15.019 | 14.782 | 1.40× |
| lossless | 7.883 | 8.805 | 8.591 | 1.09× |

Stage costs (current − current with the stage skipped):

| Input | alpha | loop filter | residual | prediction | output (upsample + YUV→RGBA) | VP8L transforms | entropy only |
|---|---:|---:|---:|---:|---:|---:|---:|
| lossy opaque | — | 3.19 | (−1.38)* | 0.82 | 1.19 | — | 5.57 |
| lossy + alpha | 4.0** | 2.59 | 0.43 | 0.80 | 1.12 | — | 5.68 |
| lossless | — | — | — | — | 0.42 | 2.89 | — |

\* noise: that run's `Current` had a 1.3 ms standard deviation; the in-process `quick --ablate` measured 1.41 ms.
\*\* from `quick --ablate`; the BenchmarkDotNet `NoAlpha` process was disturbed (25.6 ms median, 18 ms StdDev).

"Entropy only" still includes the frame setup, the border initialisation and the copy of the padded planes into
tight Y/U/V planes. Readings:

- **Lossy**: the entropy side alone (5.6 ms) costs what libwebp needs for the whole decode, on a 45 KB stream
  (≈ 0.68 µs per macroblock): per-block overhead, not bit decoding. The loop filter is the next largest stage.
- **Alpha**: the ALPH plane here is a single-colour palette (112 bytes) and still costs ≈ 4 ms — the same as
  libwebp's alpha overhead (10.6 − 5.5 ms). This is the cheapest place to beat libwebp.
- **Lossless**: the inverse transforms (only subtract-green here) cost 2.9 ms of 8.6 ms.
