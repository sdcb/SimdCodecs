# HEIF decode baseline — 2026-11-03 — AMD Ryzen 7 5800X (M1 baseline, re-recorded)

**Purpose:** Replace the m0 scratch-harness numbers with a correct in-process baseline so M3 has a
meaningful A/B starting point. The m0 record used `ffmpeg -benchmark` as a single-core reference,
which counts all threads' CPU time and includes process start-up; that number was not comparable to
our per-core wall time. This record fixes the measurement methodology.

## Machine

- CPU: AMD Ryzen 7 5800X (8 cores / 16 logical)
- OS: Microsoft Windows 11 Pro for Workstations 10.0.26200
- .NET: 10.0.11 (win-x64), process X64
- Vector128.IsHardwareAccelerated: True, Vector256.IsHardwareAccelerated: True, Vector512.IsHardwareAccelerated: False
- GC: workstation; no server GC; tiered compilation default

## Competitors (same process, same byte[], same CPU affinity)

| Competitor | Version | Role |
|---|---|---|
| **Sdcb.SimdCodecs.Heif** | M1 (commit `480280e`) | our decoder — what M3 optimises |
| **libde265** | 1.1.1 (vcpkg, `libde265.dll`) | peer at the HEVC stage: same NAL units, no container, no colour |
| **libheif** | 1.23.1 (vcpkg, `heif.dll`) | peer at the full pipeline (YCbCr + RGBA) |
| **Magick.NET** | 14.13.1 (libheif + Q16 HDRI) | ecosystem reference |

Correctness gate before every run: `verify` confirms HEVC stage vs libde265 is 0-diff on all
fixtures, and grid composition vs libde265-tiles is also 0-diff. All contracts passed.

Serial equivalence check: serial and parallel tile decode produce the identical RGBA buffer on all
fixtures (0 byte differences). See `SerialEquivalence` command.

## Multi-core, end-to-end to RGBA (median of 5, same-process interleaved)

| Fixture | MP | ours ms | libheif ms | Magick ms | ours alloc MB | ours MP/s | libheif MP/s | Magick MP/s | ours ÷ libheif | ours ÷ Magick |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `hevc-main-3072x4096-grid` (48 tiles, Main 8-bit) | 12.6 | 117 | 165 | 448 | 128 | 107.5 | 76.2 | 28.1 | **0.71×** | **0.26×** |
| `hevc-main10-3072x4096-grid` (48 tiles, Main 10-bit) | 12.6 | 145 | 266 | 517 | 132 | 86.6 | 47.4 | 24.3 | **0.55×** | **0.28×** |
| `hevc-main-8192x6144-grid` (192 tiles, 50 MP) | 50.3 | 454 | 817 | 1761 | 512 | 110.8 | 61.6 | 28.6 | **0.56×** | **0.26×** |
| `synth-photo-fuji-1600x900-main8` (1 item) | 1.4 | 65 | 38 | 80 | 15 | 22.3 | 37.9 | 18.0 | **1.71×** | **0.81×** |
| `synth-photo-mangrove-1599x1199-main8` (1 item, clap) | 1.9 | 100 | 57 | 114 | 41 | 19.1 | 33.5 | 16.8 | **1.75×** | **0.88×** |
| `synth-screen-testsrc2-1280x720-main8` (1 item) | 0.9 | 19 | 14 | 35 | 9 | 49.4 | 65.9 | 26.5 | **1.36×** | **0.54×** |

Ratio = ours ÷ competitor; below 1.00 means we are faster. Grid images benefit from our
`Parallel.For` over tiles; single-item images have no tile parallelism and show our deficit on the
HEVC stage.

## Single-core (process pinned to CPU 0, tile parallelism serial on every side, median of 5)

`libheif` uses `heif_context_set_max_decoding_threads(_, 0)` (tiles on the calling thread);
`Magick.NET` has `ResourceLimits.Thread = 1`; ours limits `ThreadPool.MaxThreads = 1`.

All times in ms.

| Fixture | stage ours | stage libde265 | ours YUV | ours RGBA | libheif YUV | libheif RGBA | Magick |
|---|---:|---:|---:|---:|---:|---:|---:|
| `hevc-main-3072x4096-grid` | 582 | 383 | 572 | 617 | 494 | 425 | 802 |
| `hevc-main10-3072x4096-grid` | 714 | 555 | 721 | 755 | 698 | 656 | 952 |
| `hevc-main-8192x6144-grid` | 2293 | 1661 | 2360 | 2487 | 2141 | 1913 | 3276 |
| `synth-photo-fuji-1600x900-main8` | 63 | 34 | 62 | 67 | 34 | 39 | 83 |
| `synth-photo-mangrove-1599x1199-main8` | 91 | 53 | 91 | 102 | 52 | 58 | 115 |
| `synth-screen-testsrc2-1280x720-main8` | 16 | 11 | 17 | 24 | 11 | 14 | 35 |

Single-core `ours ÷ libde265` at the HEVC stage: 1.52–1.85× slower per core.

## Peak working set (fresh process per decode, sampler thread, median of 3)

| Fixture | contestant | ms | peak MB | rise MB | output MB |
|---|---|---:|---:|---:|---:|
| `hevc-main-3072x4096-grid` | ours | 303 | 163 | 132 | 48 |
| `hevc-main-3072x4096-grid` | libheif | 171 | 107 | 78 | 48 |
| `hevc-main-3072x4096-grid` | Magick | 444 | 234 | 205 | 48 |
| `hevc-main10-3072x4096-grid` | ours | 351 | 169 | 135 | 48 |
| `hevc-main10-3072x4096-grid` | libheif | 232 | 130 | 98 | 48 |
| `hevc-main10-3072x4096-grid` | Magick | 507 | 289 | 258 | 48 |
| `hevc-main-8192x6144-grid` | ours | 803 | 550 | 516 | 192 |
| `hevc-main-8192x6144-grid` | libheif | 679 | 413 | 380 | 192 |
| `hevc-main-8192x6144-grid` | Magick | 1616 | 962 | 929 | 192 |
| `synth-photo-fuji-1600x900-main8` | ours | 195 | 44 | 17 | 5 |
| `synth-photo-fuji-1600x900-main8` | libheif | 42 | 36 | 8 | 5 |
| `synth-photo-fuji-1600x900-main8` | Magick | 111 | 56 | 28 | 5 |
| `synth-photo-mangrove-1599x1199-main8` | ours | 259 | 74 | 46 | 7 |
| `synth-photo-mangrove-1599x1199-main8` | libheif | 64 | 37 | 9 | 7 |
| `synth-photo-mangrove-1599x1199-main8` | Magick | 142 | 61 | 33 | 7 |
| `synth-screen-testsrc2-1280x720-main8` | ours | 84 | 40 | 12 | 4 |
| `synth-screen-testsrc2-1280x720-main8` | libheif | 18 | 34 | 6 | 4 |
| `synth-screen-testsrc2-1280x720-main8` | Magick | 66 | 48 | 20 | 4 |

Memory rise on the 12.6 MP grid: ours 132–135 MB vs libheif 78–98 MB for a 48 MB output.
At 50 MP: ours 516 MB vs libheif 380 MB for a 192 MB output. The gap is the 16-bit tile planes
kept in memory until the canvas is assembled — the main memory target for M3.

## What this says

- **Multi-core, grid images (our strength):** 0.55–0.71× vs libheif (1.4–1.8× faster) because
  our `Parallel.For` over tiles scales well and libheif's default thread model does not help
  single-image decode the same way. vs Magick: 0.26–0.28× (3.5–3.9× faster).
- **Single-item images (our deficit):** multi-core 1.36–1.75× slower than libheif because there
  is nothing to parallelise; single-core 1.52–1.85× slower than libde265 per core at the HEVC
  stage alone.
- **Single-core HEVC stage:** ours is 1.52–1.85× slower than libde265 (which uses no SIMD for
  an intra-only path here). This is the correctible gap M3 must close with IDCT/intra/colour SIMD.
- **Memory:** working-set rise is 2.7–2.8× the output on the grid images. Root cause: 16-bit
  tile planes are all held until the canvas is assembled. The fix (tile-immediate RGBA, keep only
  the canvas) is M3's memory target.
- **The m0 ffmpeg reference (89–101 MP/s single-core) was not comparable** to our per-core wall
  time: ffmpeg's `utime` summed all threads' CPU time across a multi-threaded process, and the
  pinned-CPU experiment showed rtime ≫ utime/nthreads. That reference is retired; libde265
  single-core replaces it.

## Reproducing

```bash
HEIF_NATIVE_DIR=C:/_/3rd/vcpkg/installed/x64-windows/bin \
  dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Heif.Benchmarks -- verify
HEIF_NATIVE_DIR=... dotnet run -c Release ... -- multi --reps 5
HEIF_NATIVE_DIR=... dotnet run -c Release ... -- single --reps 5
HEIF_NATIVE_DIR=... dotnet run -c Release ... -- peakmem --reps 3
```

BenchmarkDotNet (for stable inter-session A/B):
```bash
HEIF_NATIVE_DIR=... dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Heif.Benchmarks \
  -- --filter "*HeifDecodeBenchmarks*" --warmupCount 3 --iterationCount 10
```
