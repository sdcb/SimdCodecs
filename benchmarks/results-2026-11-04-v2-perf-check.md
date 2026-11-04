# 2.0 performance check — 2026-11-04, AMD Ryzen 7 5800X

The Abstractions 2.0 port must not slow the tuned codecs down (AGENTS.md). Each benchmark ran current `v2`
against `main` (f0b6850, the last 1.x commit) as the in-process baseline, in the same run.

Machine: AMD Ryzen 7 5800X 8-core (16 logical), Windows 10.0.26200 x64, .NET 10.0.11, AVX2 (no AVX-512),
workstation non-concurrent GC.

## Output

- JPEG decode: `ab` reports `current/baseline identical` for all 13 quick-subset files.
- JPEG decode `accuracy --gate`: **PASS**.
- JPEG encode `encode --gate --baseline`: **passed**.
- All five 1.x golden files hash-identical to `main`; the full test suite fails exactly the 177 tests `main` fails on
  this machine (WebP oracle tests: dwebp cannot write `%TEMP%` here), no more.

## Speed (current ÷ baseline; < 1 is faster)

| Benchmark | geomean | notes |
|---|---:|---|
| JPEG decode `ab` (13 files) | 1.02× | outliers 1.15× / 1.18× did not reproduce: re-runs 1.00× / 1.01× and 1.03× / 1.12× |
| PNG decode `matrix` (64 cases) | 0.995× | max 1.13× on one case |
| PNG encode `matrix` (48 cases) | 1.015× | max 1.17× on one case |
| WebP decode `ab` (3 files) | lossy 0.93× / 0.98×, lossless 1.19× | lossless re-runs: 1.02×, 0.98× |

Every difference is inside the run-to-run noise these benchmarks show on this machine; none reproduces.
