# JPEG 2000 threaded decode — Sdcb.SimdCodecs vs libopenjp2 (2026-11-05, AMD Ryzen 7 5800X)

Code-block (tier-1) decoding, the inverse wavelet (rows and column strips), the component transform and the level shift now run on several threads; the decode is bit-identical to the serial one (checked by the benchmark before it times anything, and by `Jpeg2000ParallelTests`). Compared with `libopenjp2` 2.5.4 at one thread and with `opj_codec_set_threads(16)`.

## Environment

- **CPU**: AMD Ryzen 7 5800X 8-Core Processor (8 cores, 16 logical processors)
- **OS**: Microsoft Windows 11 Pro for Workstations 10.0.26200 (X64)
- **Runtime**: .NET 10.0.11, RyuJIT x86-64-v3, AVX2 = True, AVX-512 = False
- **Reference**: OpenJPEG 2.5.4 (`openjp2.dll`), P/Invoke, `opj_codec_set_threads` for the threaded rows
- **Protocol**: in-process, median of 5 timed decodes after a warm-up (serial and threaded SimdCodecs decode first compared sample for sample); the same run measures SimdCodecs with `Jpeg2000Ablation.MaxDegreeOfParallelism = 1` (the serial baseline), SimdCodecs with 16 threads, libopenjp2 with 1 thread and with 16. The machine was not otherwise idle (other development sessions were open), so read ratios, not absolute milliseconds. `fuji-512-lossless` is the first case of a run and is timed cold (tier-0 code), the same in the baseline run below; ignore its 1-thread figure.

Run: `SIMDCODECS_JPEG2000_BENCH_ROOT=<main checkout's benchmark dir> dotnet run --project benchmarks/Sdcb.SimdCodecs.Jpeg2000.Benchmarks -c Release -- fuji`

## Serial vs threaded vs libopenjp2

| Case | Dimensions | Filter | File Size | Simd 1T | Simd 16T | Simd speed-up | opj 1T | opj 16T | opj speed-up | Simd 1T / opj 1T | Simd 16T / opj 16T | PSNR vs libopenjp2 |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `fuji-512-lossless` | 512x512 (3c) | 5/3 | 263.4 KB | 120.62 ms | **7.86 ms** | 15.35x | 29.39 ms | 4.49 ms | 6.55x | 4.10x | **1.75x** | Exact |
| `fuji-512-lossy` | 512x512 (3c) | 9/7 | 30.2 KB | 29.86 ms | **6.40 ms** | 4.67x | 6.98 ms | 2.66 ms | 2.63x | 4.28x | **2.41x** | 82.27 dB (peak=1) |
| `fuji-1024-lossless` | 1024x1024 (3c) | 5/3 | 1015.7 KB | 224.07 ms | **25.55 ms** | 8.77x | 112.71 ms | 15.78 ms | 7.14x | 1.99x | **1.62x** | Exact |
| `fuji-1024-lossy` | 1024x1024 (3c) | 9/7 | 93.1 KB | 51.78 ms | **11.61 ms** | 4.46x | 25.33 ms | 10.05 ms | 2.52x | 2.04x | **1.15x** | 83.85 dB (peak=1) |
| `fuji-1600-lossless` | 1600x1200 (3c) | 5/3 | 1878.7 KB | 390.96 ms | **46.57 ms** | 8.39x | 207.24 ms | 27.00 ms | 7.68x | 1.89x | **1.73x** | Exact |
| `fuji-1600-lossy` | 1600x1200 (3c) | 9/7 | 176.0 KB | 94.82 ms | **21.64 ms** | 4.38x | 43.67 ms | 18.34 ms | 2.38x | 2.17x | **1.18x** | 84.32 dB (peak=1) |
| `fuji-3000-lossless` | 3000x2000 (3c) | 5/3 | 5915.3 KB | 1221.32 ms | **144.12 ms** | 8.47x | 651.52 ms | 97.00 ms | 6.72x | 1.87x | **1.49x** | Exact |
| `fuji-3000-lossy` | 3000x2000 (3c) | 9/7 | 508.6 KB | 278.15 ms | **67.19 ms** | 4.14x | 139.57 ms | 56.28 ms | 2.48x | 1.99x | **1.19x** | 84.81 dB (peak=1) |

Threads do more for SimdCodecs than for libopenjp2 (4–9x against 2.4–7.7x), so the gap to libopenjp2 narrows from about 1.9–2.2x at one thread to 1.15–1.75x at 16. What is left serial: tier-2 packet parsing, `Interleave`, and the cache-bound parts of the 9/7 wavelet, which is why lossy images (little entropy-coded data, mostly wavelet) scale less than lossless ones.

## Single-thread did not regress

The commit before the threading (`afc3419`, extracted with `git archive` to a separate directory and built there) was run immediately before the threaded build, same machine, same inputs (its own original `Program`, one thread):

| Case | Before (`afc3419`) | After, `MaxDegreeOfParallelism = 1` |
|---|---:|---:|
| `fuji-512-lossless` | 120.75 ms | 120.62 ms |
| `fuji-512-lossy` | 29.39 ms | 29.86 ms |
| `fuji-1024-lossless` | 214.99 ms | 224.07 ms |
| `fuji-1024-lossy` | 51.19 ms | 51.78 ms |
| `fuji-1600-lossless` | 401.33 ms | 390.96 ms |
| `fuji-1600-lossy` | 91.73 ms | 94.82 ms |

Within run-to-run noise (about ±4%); the serial path still allocates the same amount (managed allocation per decode is measured at one thread because the counter is per thread: 8.4 MB at 512x512, 33.3 MB at 1024x1024, 60.9 MB at 1600x1200, 190 MB at 3000x2000). A threaded decode additionally allocates one `BlockState` per worker (about 20 KB each).
