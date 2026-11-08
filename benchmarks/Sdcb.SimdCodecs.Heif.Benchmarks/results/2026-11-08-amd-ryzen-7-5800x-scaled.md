# HEIF scaled decode — 2026-11-08 — AMD Ryzen 7 5800X

**Purpose:** compare the native scaled decode (`DecodeOptions.ScaleDenominator` 2 / 4 / 8: each tile boxes the pixels it converts, written straight into the scaled image) with the path it replaces (decode the full Rgb24 / Rgb48 image, then `BoxDownscaler.Downscale`). The two give the same bytes; `quick --scaled` checks that before it times anything.

## Machine

- CPU: AMD Ryzen 7 5800X (8 cores / 16 logical)
- OS: Windows 11 Pro for Workstations 10.0.26200
- .NET: SDK 10.0.400, runtime 10.0.11 (win-x64)
- Vector128 / Vector256 hardware-accelerated, Vector512 not
- Other work was running on the machine at the same time, so single runs are noisy; the time table gives medians over three alternating rounds.

## How

A is `origin/scaled-decode` (7874b76) with this branch's harness files (`Quick.cs`, `PeakMemory.cs`, `Program.cs`) copied in; they use only public API, so A's "native" column is the old adapter path. B is this branch. The two builds ran alternately, A then B, in the same session:

```bash
HEIF_BENCH_CPU=all  dotnet <A|B>/…/Sdcb.SimdCodecs.Heif.Benchmarks.dll quick --scaled --reps 9    # 3 rounds
                    dotnet <A|B>/…/Sdcb.SimdCodecs.Heif.Benchmarks.dll peakmem --reps 3 --contestants "^(ours|scaled)"
```

`HEIF_BENCH_CPU=all` uses every core with the default thread pool; the adapter decodes the tiles in parallel. Every fixture takes the native path: the grids have 512-pixel tiles and a size that is a multiple of 8, and the single items have no seams.

## Time (all cores, ms, median of 3 rounds × 9 reps)

| fixture | d | A (current) | B (native) | B / A |
|---|---:|---:|---:|---:|
| 3072×4096 Main grid | 2 | 111.3 | 76.1 | 0.68 |
| | 4 | 106.0 | 69.5 | 0.66 |
| | 8 | 85.7 | 98.8 | 1.15 |
| 3072×4096 Main 10 grid | 2 | 141.8 | 89.9 | 0.63 |
| | 4 | 113.5 | 94.3 | 0.83 |
| | 8 | 104.1 | 87.6 | 0.84 |
| 8192×6144 Main grid | 2 | 249.3 | 187.0 | 0.75 |
| | 4 | 242.5 | 225.0 | 0.93 |
| | 8 | 220.8 | 182.5 | 0.83 |
| fuji 1600×900 (1 item) | 2 | 13.7 | 12.2 | 0.89 |
| | 4 | 13.3 | 11.9 | 0.90 |
| | 8 | 12.3 | 11.8 | 0.96 |
| mangrove 1599×1199 (1 item, clap) | 2 | 15.7 | 13.5 | 0.86 |
| | 4 | 14.5 | 13.3 | 0.92 |
| | 8 | 14.2 | 13.3 | 0.94 |
| testsrc2 1280×720 (1 item) | 2 | 7.0 | 6.2 | 0.89 |
| | 4 | 6.8 | 6.4 | 0.94 |
| | 8 | 6.6 | 6.3 | 0.96 |

The HEVC decode is most of the time and is unchanged; the saving is the full-size image (allocating, writing and reading it again). The 3072×4096 d8 row is the one where B was slower; its three rounds were 82–112 ms against A's 84–94 ms, within the noise of the loaded machine. On one CPU (`quick --scaled`, 2 rounds, not tabulated) the two paths were within ±10% of each other on every fixture, as expected when the decode dominates.

## Peak working set (one fresh process per decode, median of 3, MB)

`rise` is the peak over the working set before the decode; `output` is the decoded image (full-size RGBA for `ours`, the scaled Rgb24 / Rgb48 image for `scaled-dN`).

| fixture | contestant | A rise | B rise | output |
|---|---|---:|---:|---:|
| 3072×4096 Main grid | full RGBA | 74 | 74 | 48 |
| | d2 | 72 | 38 | 9 |
| | d4 | 64 | 32 | 2 |
| | d8 | 61 | 30 | 1 |
| 3072×4096 Main 10 grid | full RGBA | 78 | 78 | 48 |
| | d2 | 115 | 52 | 18 |
| | d4 | 103 | 37 | 4 |
| | d8 | 100 | 32 | 1 |
| 8192×6144 Main grid | full RGBA | 229 | 229 | 192 |
| | d2 | 218 | 82 | 36 |
| | d4 | 192 | 55 | 9 |
| | d8 | 183 | 47 | 2 |
| fuji 1600×900 | d2 / d4 / d8 | 22 / 21 / 20 | 17 / 16 / 16 | 1 / 0 / 0 |
| mangrove 1599×1199 | d2 / d4 / d8 | 24 / 23 / 24 | 19 / 18 / 19 | 1 / 0 / 0 |
| testsrc2 1280×720 | d2 / d4 / d8 | 16 / 15 / 15 | 13 / 13 / 13 | 1 / 0 / 0 |

A scaled grid decode now rises by its output plus the working set of the tiles in flight (about 30 MB here: JIT, sixteen thread-pool threads, one tile's planes each), not by the full-size image: a 50 MP photo at d8 needs 47 MB instead of 183 MB.
