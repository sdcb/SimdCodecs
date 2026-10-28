# tRNS colour key, grey + alpha / tRNS / pHYs writing, lossless reduction — 2026-10-28, AMD Ryzen 7 5800X

Same-process A/B against `eccb09c` (baseline). Command: `LIBPNG_PATH=…/vcpkg/installed/x64-windows/bin/libpng16.dll dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Png.Benchmarks -- matrix all` (decode below), `matrix encode` (encode below, after the reduction's scan was rewritten), and `matrix encode --cases store --rounds 31` (the store rows again, to settle their noise).

- Decode: a greyscale or RGB image's tRNS transparent colour is applied by every RGBA projection (packed grey through the expansion table, 8-bit by one vector compare pass over the expanded row, 16-bit at full precision against the source row), and the fidelity tier gains an alpha channel. None of the benchmark's inputs carry one, so these rows measure that the change costs the other paths nothing: current ÷ baseline 1.01× photo, 1.00× screen.
- Encode: the writer goes through `PngEncodePlan`; written as before, it produces the same bytes (current ÷ baseline 0.99–1.03× on the 31-round store rows, where a per-row cost would show most; the 7-round full matrix is noise-level the other way).
- `LosslessReduction` (opt-in, its own table): one scan of the image, specialised per source layout, that stops as soon as nothing is left to learn, with the opacity check vectorised. Where it cannot shrink the image it costs 0–5% at fastest / optimal / smallest and up to 1.30× at store (a full scan is a visible fraction of a 2–8 ms copy). Where it applies here (the screen 16-bit sources, which hold 8-bit values) it is 0.56–0.76× the time and 0.50–0.85× the size. The synthetic screen has gradients and soft shadows (more than 256 colours, non-binary alpha), so it does not exercise indexed output; the tests do (`PngWriterReductionTests.AScreenshotShrinks`: flat UI to indexed 2-bit at under half the size).

- CPU: AMD Ryzen 7 5800X 8-Core Processor
- OS: Microsoft Windows 10.0.26200 (X64)
- .NET: .NET 10.0.11 (win-x64), process X64
- Logical processors: 16
- Vector128.IsHardwareAccelerated: True, Vector256.IsHardwareAccelerated: True, Vector512.IsHardwareAccelerated: False
- AVX2: True, AVX-512F: False
- Image size: 1920×1080
- `photo` source: centre crop of landscape-almsee.png (JPEG benchmark corpus master)
- `screen` source: synthetic
- ImageSharp: 3.1.12
- libpng: libpng 1.6.39 (C:\_\3rd\vcpkg\installed\x64-windows\bin\libpng16.dll)
- Baseline: eccb09c8ff7f PNG: decode Adam7 interlaced images; refuse impossible image-data sizes

### Decode to 8-bit RGBA (in-process medians of 7 interleaved rounds, ms)

`current` = `PngImageDecoder.TryDecodeIntoRgba8` into a caller buffer; `native` = `PngReader.Decode` (samples as stored, no RGBA expansion); ImageSharp = `Image.Load<Rgba32>` + `CopyPixelDataTo`, one thread; libpng = simplified API `png_image_finish_read` to RGBA. Ratios below 1 mean we are faster. Inputs are encoded by ImageSharp (adaptive filters, zlib level 6).

| Case | KB | current | native | baseline | ImageSharp | libpng | current MP/s | ÷ ImageSharp | ÷ libpng | ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-g1` | 28 | 1.730 | 1.048 | 1.714 | 3.447 | 4.118 | 1199 | 0.50× | 0.42× | 1.01× |
| `photo-g2` | 167 | 2.670 | 2.434 | 2.588 | 4.723 | 6.001 | 777 | 0.57× | 0.44× | 1.03× |
| `photo-g4` | 427 | 4.405 | 4.106 | 4.374 | 6.322 | 8.182 | 471 | 0.70× | 0.54× | 1.01× |
| `photo-g8` | 1297 | 13.7 | 13.5 | 13.9 | 14.2 | 15.2 | 151 | 0.96× | 0.90× | 0.99× |
| `photo-g16` | 3588 | 14.9 | 14.7 | 14.6 | 17.9 | — | 139 | 0.83× | — | 1.02× |
| `photo-rgb8` | 3309 | 23.5 | 23.5 | 23.9 | 26.7 | 29.6 | 88 | 0.88× | 0.79× | 0.98× |
| `photo-rgb16` | 10762 | 38.7 | 39.2 | 39.2 | 64.2 | — | 54 | 0.60× | — | 0.99× |
| `photo-p1` | 123 | 2.794 | 2.074 | 2.682 | 8.607 | 6.359 | 742 | 0.32× | 0.44× | 1.04× |
| `photo-p2` | 268 | 3.396 | 3.119 | 3.364 | 9.439 | 7.257 | 611 | 0.36× | 0.47× | 1.01× |
| `photo-p4` | 567 | 5.163 | 4.811 | 5.222 | 10.9 | 9.090 | 402 | 0.47× | 0.57× | 0.99× |
| `photo-p8` | 1406 | 8.761 | 8.096 | 8.729 | 13.2 | 11.9 | 237 | 0.66× | 0.74× | 1.00× |
| `photo-p8a` | 1051 | 7.732 | 7.058 | 7.722 | 18.1 | 10.3 | 268 | 0.43× | 0.75× | 1.00× |
| `photo-ga8` | 1772 | 14.2 | 14.3 | 13.9 | 18.5 | 19.0 | 146 | 0.77× | 0.75× | 1.02× |
| `photo-ga16` | 6121 | 30.2 | 30.5 | 30.1 | 30.2 | — | 69 | 1.00× | — | 1.00× |
| `photo-rgba8` | 3809 | 25.8 | 26.2 | 25.5 | 24.8 | 34.1 | 80 | 1.04× | 0.76× | 1.01× |
| `photo-rgba16` | 13672 | 53.2 | 54.1 | 52.9 | 98.2 | — | 39 | 0.54× | — | 1.00× |
| `photo-g1-adam7` | 35 | 2.501 | 2.457 | 2.382 | 4.671 | 15.2 | 829 | 0.54× | 0.16× | 1.05× |
| `photo-g2-adam7` | 197 | 3.751 | 3.882 | 3.701 | 6.294 | 17.6 | 553 | 0.60× | 0.21× | 1.01× |
| `photo-g4-adam7` | 490 | 5.889 | 6.011 | 5.848 | 8.262 | 20.0 | 352 | 0.71× | 0.30× | 1.01× |
| `photo-g8-adam7` | 1418 | 13.0 | 13.3 | 12.9 | 13.9 | 26.4 | 160 | 0.93× | 0.49× | 1.01× |
| `photo-g16-adam7` | 3689 | 15.8 | 16.7 | 15.5 | 18.2 | — | 132 | 0.86× | — | 1.02× |
| `photo-rgb8-adam7` | 3830 | 26.1 | 32.1 | 25.9 | 28.2 | 41.5 | 79 | 0.93× | 0.63× | 1.01× |
| `photo-rgb16-adam7` | 11055 | 38.4 | 43.3 | 38.7 | 48.5 | — | 54 | 0.79× | — | 0.99× |
| `photo-p1-adam7` | 130 | 3.408 | 3.417 | 3.387 | 9.377 | 17.2 | 608 | 0.36× | 0.20× | 1.01× |
| `photo-p2-adam7` | 289 | 4.647 | 4.968 | 4.486 | 10.8 | 18.6 | 446 | 0.43× | 0.25× | 1.04× |
| `photo-p4-adam7` | 620 | 6.527 | 6.622 | 6.508 | 12.6 | 20.9 | 318 | 0.52× | 0.31× | 1.00× |
| `photo-p8-adam7` | 1499 | 10.3 | 9.958 | 10.3 | 14.4 | 23.9 | 202 | 0.71× | 0.43× | 0.99× |
| `photo-p8a-adam7` | 1174 | 9.534 | 9.265 | 9.520 | 19.6 | 22.5 | 217 | 0.49× | 0.42× | 1.00× |
| `photo-ga8-adam7` | 1998 | 15.7 | 16.1 | 15.4 | 17.7 | 30.1 | 132 | 0.88× | 0.52× | 1.01× |
| `photo-ga16-adam7` | 6382 | 31.9 | 34.1 | 31.9 | 31.4 | — | 65 | 1.02× | — | 1.00× |
| `photo-rgba8-adam7` | 4414 | 30.1 | 32.6 | 30.0 | 29.5 | 46.6 | 69 | 1.02× | 0.65× | 1.00× |
| `photo-rgba16-adam7` | 14072 | 53.6 | 57.6 | 53.5 | 101.8 | — | 39 | 0.53× | — | 1.00× |
| `screen-g1` | 20 | 1.586 | 0.986 | 1.542 | 3.219 | 3.948 | 1307 | 0.49× | 0.40× | 1.03× |
| `screen-g2` | 36 | 1.266 | 1.088 | 1.230 | 3.322 | 4.737 | 1639 | 0.38× | 0.27× | 1.03× |
| `screen-g4` | 45 | 1.508 | 1.316 | 1.493 | 3.578 | 5.659 | 1375 | 0.42× | 0.27× | 1.01× |
| `screen-g8` | 59 | 2.015 | 1.927 | 2.001 | 3.465 | 6.611 | 1029 | 0.58× | 0.30× | 1.01× |
| `screen-g16` | 83 | 2.235 | 2.355 | 2.224 | 5.171 | — | 928 | 0.43× | — | 1.00× |
| `screen-rgb8` | 81 | 2.104 | 2.162 | 2.128 | 3.735 | 12.9 | 985 | 0.56× | 0.16× | 0.99× |
| `screen-rgb16` | 97 | 2.595 | 3.288 | 2.622 | 16.8 | — | 799 | 0.15× | — | 0.99× |
| `screen-p1` | 47 | 1.769 | 1.113 | 1.762 | 7.320 | 4.936 | 1172 | 0.24× | 0.36× | 1.00× |
| `screen-p2` | 67 | 1.448 | 1.210 | 1.419 | 7.375 | 5.365 | 1432 | 0.20× | 0.27× | 1.02× |
| `screen-p4` | 66 | 1.598 | 1.309 | 1.587 | 7.543 | 6.032 | 1298 | 0.21× | 0.26× | 1.01× |
| `screen-p8` | 84 | 1.385 | 0.771 | 1.376 | 6.421 | 5.916 | 1497 | 0.22× | 0.23× | 1.01× |
| `screen-p8a` | 84 | 1.383 | 0.848 | 1.377 | 12.0 | 5.046 | 1499 | 0.11× | 0.27× | 1.00× |
| `screen-ga8` | 71 | 1.909 | 1.926 | 1.900 | 4.190 | 9.501 | 1086 | 0.46× | 0.20× | 1.01× |
| `screen-ga16` | 101 | 2.720 | 3.140 | 2.704 | 5.812 | — | 762 | 0.47× | — | 1.01× |
| `screen-rgba8` | 85 | 2.122 | 2.465 | 2.111 | 2.759 | 16.0 | 977 | 0.77× | 0.13× | 1.01× |
| `screen-rgba16` | 106 | 2.926 | 4.219 | 2.942 | 14.0 | — | 709 | 0.21× | — | 0.99× |
| `screen-g1-adam7` | 23 | 2.313 | 2.004 | 2.193 | 4.451 | 15.1 | 897 | 0.52× | 0.15× | 1.05× |
| `screen-g2-adam7` | 39 | 2.001 | 2.169 | 2.096 | 4.635 | 15.8 | 1036 | 0.43× | 0.13× | 0.95× |
| `screen-g4-adam7` | 51 | 2.184 | 2.362 | 2.168 | 4.674 | 16.8 | 950 | 0.47× | 0.13× | 1.01× |
| `screen-g8-adam7` | 67 | 2.788 | 3.221 | 2.926 | 3.999 | 17.7 | 744 | 0.70× | 0.16× | 0.95× |
| `screen-g16-adam7` | 92 | 2.188 | 2.686 | 2.182 | 4.845 | — | 948 | 0.45× | — | 1.00× |
| `screen-rgb8-adam7` | 92 | 2.838 | 8.198 | 2.853 | 5.912 | 23.8 | 731 | 0.48× | 0.12× | 0.99× |
| `screen-rgb16-adam7` | 113 | 3.417 | 8.356 | 3.431 | 12.1 | — | 607 | 0.28× | — | 1.00× |
| `screen-p1-adam7` | 50 | 2.414 | 2.547 | 2.422 | 8.449 | 16.2 | 859 | 0.29× | 0.15× | 1.00× |
| `screen-p2-adam7` | 72 | 2.241 | 2.426 | 2.217 | 8.649 | 16.7 | 925 | 0.26× | 0.13× | 1.01× |
| `screen-p4-adam7` | 72 | 2.337 | 2.502 | 2.334 | 8.668 | 17.1 | 887 | 0.27× | 0.14× | 1.00× |
| `screen-p8-adam7` | 99 | 2.133 | 1.966 | 2.118 | 6.764 | 17.0 | 972 | 0.32× | 0.13× | 1.01× |
| `screen-p8a-adam7` | 100 | 2.133 | 1.966 | 2.123 | 12.4 | 16.1 | 972 | 0.17× | 0.13× | 1.00× |
| `screen-ga8-adam7` | 82 | 2.686 | 3.972 | 2.685 | 4.886 | 20.5 | 772 | 0.55× | 0.13× | 1.00× |
| `screen-ga16-adam7` | 115 | 2.647 | 4.808 | 2.657 | 5.460 | — | 783 | 0.48× | — | 1.00× |
| `screen-rgba8-adam7` | 99 | 2.849 | 3.644 | 2.876 | 4.494 | 26.7 | 728 | 0.63× | 0.11× | 0.99× |
| `screen-rgba16-adam7` | 124 | 3.829 | 7.781 | 3.788 | 15.6 | — | 542 | 0.25× | — | 1.01× |

Geometric means of current ÷ competitor over the cases both decode:

- vs ImageSharp, photo: 0.65× (32 cases)
- vs ImageSharp, screen: 0.35× (32 cases)
- vs libpng, photo: 0.46× (24 cases)
- vs libpng, screen: 0.18× (24 cases)
- vs baseline, photo: 1.01× (32 cases)
- vs baseline, screen: 1.00× (32 cases)

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool and libpng allocates natively, so neither shows here: compare working sets with `peakmem`.

| Case | current | native | baseline | ImageSharp | libpng |
|---|---:|---:|---:|---:|---:|
| `photo-g1` | 1.7 | 2026.3 | 1.7 | 105.0 | 0.0 |
| `photo-g2` | 1.7 | 2026.7 | 1.7 | 105.0 | 0.0 |
| `photo-g4` | 1.8 | 2027.8 | 1.8 | 105.4 | 0.0 |
| `photo-g8` | 1.2 | 2028.2 | 1.2 | 5.4 | 0.0 |
| `photo-g16` | 1.7 | 4055.6 | 1.7 | 8.8 | — |
| `photo-rgb8` | 1.7 | 6082.5 | 1.7 | 8.3 | 0.0 |
| `photo-rgb16` | 4.8 | 12166.2 | 4.7 | 19.3 | — |
| `photo-p1` | 1.7 | 2026.3 | 1.7 | 105.1 | 0.0 |
| `photo-p2` | 1.8 | 2026.9 | 1.8 | 105.4 | 0.0 |
| `photo-p4` | 2.0 | 2028.0 | 2.0 | 106.0 | 0.0 |
| `photo-p8` | 3.0 | 2029.0 | 3.0 | 10.4 | 0.0 |
| `photo-p8a` | 3.2 | 2029.3 | 3.2 | 18.3 | 0.0 |
| `photo-ga8` | 1.2 | 4055.1 | 1.2 | 6.1 | 0.0 |
| `photo-ga16` | 2.7 | 8110.4 | 2.7 | 12.5 | — |
| `photo-rgba8` | 9.2 | 8109.4 | 9.2 | 9.1 | 0.0 |
| `photo-rgba16` | 4.8 | 16219.9 | 4.7 | 23.5 | — |
| `photo-g1-adam7` | 1.7 | 2025.7 | 1.6 | 193.4 | 0.0 |
| `photo-g2-adam7` | 1.6 | 2025.7 | 1.6 | 193.7 | 0.0 |
| `photo-g4-adam7` | 1.7 | 2025.8 | 1.7 | 194.0 | 0.0 |
| `photo-g8-adam7` | 1.1 | 2026.2 | 1.1 | 5.6 | 0.0 |
| `photo-g16-adam7` | 1.6 | 4051.8 | 1.6 | 8.9 | — |
| `photo-rgb8-adam7` | 1.6 | 6076.8 | 1.6 | 9.1 | 0.0 |
| `photo-rgb16-adam7` | 4.7 | 12154.8 | 4.7 | 19.7 | — |
| `photo-p1-adam7` | 1.7 | 2025.8 | 1.6 | 193.8 | 0.0 |
| `photo-p2-adam7` | 1.8 | 2025.9 | 1.7 | 194.0 | 0.0 |
| `photo-p4-adam7` | 1.9 | 2026.0 | 1.9 | 194.7 | 0.0 |
| `photo-p8-adam7` | 2.9 | 2027.0 | 2.9 | 10.6 | 0.0 |
| `photo-p8a-adam7` | 3.2 | 2027.3 | 3.2 | 18.5 | 0.0 |
| `photo-ga8-adam7` | 1.1 | 4051.2 | 1.1 | 6.5 | 0.0 |
| `photo-ga16-adam7` | 2.7 | 8102.8 | 2.6 | 12.8 | — |
| `photo-rgba8-adam7` | 2.7 | 8102.8 | 2.6 | 9.9 | 0.0 |
| `photo-rgba16-adam7` | 4.7 | 16204.8 | 4.7 | 24.1 | — |
| `screen-g1` | 1.7 | 2026.3 | 1.7 | 104.8 | 0.0 |
| `screen-g2` | 1.7 | 2026.7 | 1.7 | 104.8 | 0.0 |
| `screen-g4` | 1.7 | 2027.7 | 1.7 | 104.8 | 0.0 |
| `screen-g8` | 0.7 | 2027.7 | 0.6 | 3.5 | 0.0 |
| `screen-g16` | 0.7 | 4054.6 | 0.6 | 3.6 | — |
| `screen-rgb8` | 0.7 | 6081.5 | 0.6 | 3.6 | 0.0 |
| `screen-rgb16` | 0.7 | 12162.1 | 0.6 | 3.6 | — |
| `screen-p1` | 1.7 | 2026.3 | 1.7 | 105.0 | 0.0 |
| `screen-p2` | 1.7 | 2026.8 | 1.7 | 105.1 | 0.0 |
| `screen-p4` | 1.8 | 2027.7 | 1.7 | 105.3 | 0.0 |
| `screen-p8` | 2.5 | 2028.5 | 2.4 | 8.5 | 0.0 |
| `screen-p8a` | 2.7 | 2028.8 | 2.7 | 16.9 | 0.0 |
| `screen-ga8` | 0.7 | 4054.6 | 0.6 | 3.6 | 0.0 |
| `screen-ga16` | 0.7 | 8108.3 | 0.6 | 3.6 | — |
| `screen-rgba8` | 8.2 | 8108.3 | 8.2 | 3.6 | 0.0 |
| `screen-rgba16` | 0.7 | 16215.8 | 0.6 | 3.6 | — |
| `screen-g1-adam7` | 1.6 | 2025.7 | 1.6 | 193.4 | 0.0 |
| `screen-g2-adam7` | 1.6 | 2025.7 | 1.6 | 193.4 | 0.0 |
| `screen-g4-adam7` | 1.6 | 2025.7 | 1.6 | 193.4 | 0.0 |
| `screen-g8-adam7` | 0.6 | 2025.7 | 0.6 | 3.6 | 0.0 |
| `screen-g16-adam7` | 0.6 | 4050.7 | 0.6 | 3.6 | — |
| `screen-rgb8-adam7` | 0.6 | 6075.7 | 0.6 | 3.6 | 0.0 |
| `screen-rgb16-adam7` | 0.6 | 12150.7 | 0.6 | 3.6 | — |
| `screen-p1-adam7` | 1.7 | 2025.8 | 1.6 | 193.6 | 0.0 |
| `screen-p2-adam7` | 1.7 | 2025.8 | 1.6 | 193.7 | 0.0 |
| `screen-p4-adam7` | 1.7 | 2025.8 | 1.7 | 193.9 | 0.0 |
| `screen-p8-adam7` | 2.4 | 2026.5 | 2.4 | 8.5 | 0.0 |
| `screen-p8a-adam7` | 2.7 | 2026.8 | 2.7 | 16.9 | 0.0 |
| `screen-ga8-adam7` | 0.6 | 4050.7 | 0.6 | 3.6 | 0.0 |
| `screen-ga16-adam7` | 0.6 | 8100.7 | 0.6 | 3.6 | — |
| `screen-rgba8-adam7` | 0.6 | 8100.7 | 0.6 | 3.6 | 0.0 |
| `screen-rgba16-adam7` | 0.6 | 16200.7 | 0.6 | 3.6 | — |

Notes:

- `photo-g16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgb16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-ga16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgba16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-g16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgb16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-ga16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgba16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-g16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgb16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-ga16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgba16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-g16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgb16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-ga16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgba16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed

### Encode (in-process medians of 7 interleaved rounds, ms; sizes in KB)

`current` = `PngWriter.Encode*` with `PngWriteOptions.CompressionLevel`, one thread; ImageSharp = `PngEncoder` at the same zlib level (store 0, fastest 1, optimal 6, smallest 9), adaptive filters, one thread; libpng = simplified API `png_image_write_to_memory`, which has no level setting, so it appears on the optimal rows (its default). Every output is decoded and must reproduce the samples exactly.

| Case | current | KB | baseline | ImageSharp | KB | libpng | KB | time ÷ ImageSharp | size ÷ ImageSharp | time ÷ libpng | size ÷ libpng | time ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 1.952 | 2026 | 2.079 | 3.557 | 2027 | — | — | 0.55× | 1.000 | — | — | 0.94× |
| `photo-gray8-fastest` | 58.5 | 1899 | 59.3 | 54.7 | 1336 | — | — | 1.07× | 1.421 | — | — | 0.99× |
| `photo-gray8-optimal` | 59.6 | 1253 | 61.0 | 77.0 | 1297 | 106.7 | 1201 | 0.77× | 0.966 | 0.56× | 1.043 | 0.98× |
| `photo-gray8-smallest` | 87.2 | 1318 | 86.9 | 77.0 | 1297 | — | — | 1.13× | 1.016 | — | — | 1.00× |
| `photo-gray16-store` | 3.869 | 4052 | 3.772 | 7.834 | 4052 | — | — | 0.49× | 1.000 | — | — | 1.03× |
| `photo-gray16-fastest` | 85.7 | 4274 | 86.2 | 111.5 | 3618 | — | — | 0.77× | 1.181 | — | — | 0.99× |
| `photo-gray16-optimal` | 139.8 | 3577 | 140.2 | 118.2 | 3588 | 141.0 | 3581 | 1.18× | 0.997 | 0.99× | 0.999 | 1.00× |
| `photo-gray16-smallest` | 139.9 | 3616 | 139.4 | 119.5 | 3588 | — | — | 1.17× | 1.008 | — | — | 1.00× |
| `photo-rgb8-store` | 6.038 | 6077 | 5.846 | 10.3 | 6078 | — | — | 0.59× | 1.000 | — | — | 1.03× |
| `photo-rgb8-fastest` | 92.1 | 4860 | 93.6 | 100.8 | 3414 | — | — | 0.91× | 1.423 | — | — | 0.98× |
| `photo-rgb8-optimal` | 155.3 | 3304 | 153.2 | 406.4 | 3309 | 525.4 | 3404 | 0.38× | 0.998 | 0.30× | 0.970 | 1.01× |
| `photo-rgb8-smallest` | 368.7 | 3273 | 369.4 | 531.8 | 3299 | — | — | 0.69× | 0.992 | — | — | 1.00× |
| `photo-rgb16-store` | 11.0 | 12153 | 11.3 | 24.3 | 12155 | — | — | 0.45× | 1.000 | — | — | 0.98× |
| `photo-rgb16-fastest` | 239.7 | 12803 | 246.8 | 330.4 | 10752 | — | — | 0.73× | 1.191 | — | — | 0.97× |
| `photo-rgb16-optimal` | 404.9 | 10752 | 411.6 | 373.5 | 10762 | 440.5 | 10767 | 1.08× | 0.999 | 0.92× | 0.999 | 0.98× |
| `photo-rgb16-smallest` | 420.0 | 10735 | 425.9 | 375.9 | 10762 | — | — | 1.12× | 0.997 | — | — | 0.99× |
| `photo-rgba8-store` | 7.668 | 8102 | 7.877 | 14.4 | 8104 | — | — | 0.53× | 1.000 | — | — | 0.97× |
| `photo-rgba8-fastest` | 114.5 | 5643 | 109.5 | 124.2 | 4051 | — | — | 0.92× | 1.393 | — | — | 1.05× |
| `photo-rgba8-optimal` | 236.0 | 3805 | 238.4 | 625.9 | 3809 | 747.7 | 3911 | 0.38× | 0.999 | 0.32× | 0.973 | 0.99× |
| `photo-rgba8-smallest` | 595.8 | 3787 | 595.0 | 1042.0 | 3788 | — | — | 0.57× | 1.000 | — | — | 1.00× |
| `photo-rgba16-store` | 16.0 | 16204 | 15.6 | 33.3 | 16206 | — | — | 0.48× | 1.000 | — | — | 1.02× |
| `photo-rgba16-fastest` | 318.7 | 16819 | 322.3 | 429.3 | 13699 | — | — | 0.74× | 1.228 | — | — | 0.99× |
| `photo-rgba16-optimal` | 532.0 | 13676 | 535.5 | 618.8 | 13672 | — | — | 0.86× | 1.000 | — | — | 0.99× |
| `photo-rgba16-smallest` | 633.8 | 13680 | 640.3 | 626.0 | 13672 | — | — | 1.01× | 1.001 | — | — | 0.99× |
| `screen-gray8-store` | 1.803 | 2026 | 1.743 | 3.166 | 2027 | — | — | 0.57× | 1.000 | — | — | 1.03× |
| `screen-gray8-fastest` | 12.4 | 115 | 13.5 | 5.619 | 83 | — | — | 2.21× | 1.388 | — | — | 0.92× |
| `screen-gray8-optimal` | 15.6 | 60 | 16.2 | 21.3 | 59 | 28.7 | 59 | 0.73× | 1.019 | 0.54× | 1.025 | 0.97× |
| `screen-gray8-smallest` | 46.3 | 57 | 46.9 | 125.8 | 57 | — | — | 0.37× | 0.999 | — | — | 0.99× |
| `screen-gray16-store` | 3.394 | 4052 | 3.527 | 7.758 | 4052 | — | — | 0.44× | 1.000 | — | — | 0.96× |
| `screen-gray16-fastest` | 23.7 | 195 | 25.7 | 11.0 | 130 | — | — | 2.15× | 1.493 | — | — | 0.92× |
| `screen-gray16-optimal` | 30.3 | 83 | 32.0 | 33.1 | 83 | 46.4 | 84 | 0.91× | 0.998 | 0.65× | 0.987 | 0.95× |
| `screen-gray16-smallest` | 77.8 | 82 | 79.4 | 155.1 | 81 | — | — | 0.50× | 1.005 | — | — | 0.98× |
| `screen-rgb8-store` | 5.695 | 6077 | 5.647 | 10.1 | 6078 | — | — | 0.56× | 1.000 | — | — | 1.01× |
| `screen-rgb8-fastest` | 34.6 | 186 | 37.4 | 13.2 | 133 | — | — | 2.62× | 1.398 | — | — | 0.93× |
| `screen-rgb8-optimal` | 40.7 | 78 | 42.8 | 40.4 | 81 | 61.4 | 82 | 1.01× | 0.965 | 0.66× | 0.955 | 0.95× |
| `screen-rgb8-smallest` | 84.4 | 76 | 86.0 | 193.2 | 77 | — | — | 0.44× | 0.982 | — | — | 0.98× |
| `screen-rgb16-store` | 12.6 | 12153 | 11.4 | 24.2 | 12155 | — | — | 0.52× | 1.000 | — | — | 1.10× |
| `screen-rgb16-fastest` | 68.5 | 298 | 71.7 | 26.9 | 176 | — | — | 2.54× | 1.696 | — | — | 0.96× |
| `screen-rgb16-optimal` | 78.9 | 96 | 81.9 | 63.3 | 97 | 106.3 | 100 | 1.25× | 0.984 | 0.74× | 0.961 | 0.96× |
| `screen-rgb16-smallest` | 132.8 | 90 | 135.8 | 268.4 | 91 | — | — | 0.49× | 0.988 | — | — | 0.98× |
| `screen-rgba8-store` | 7.862 | 8102 | 6.838 | 13.3 | 8104 | — | — | 0.59× | 1.000 | — | — | 1.15× |
| `screen-rgba8-fastest` | 46.4 | 250 | 48.6 | 16.5 | 151 | — | — | 2.81× | 1.657 | — | — | 0.96× |
| `screen-rgba8-optimal` | 54.4 | 85 | 57.0 | 49.2 | 85 | 76.3 | 87 | 1.11× | 0.998 | 0.71× | 0.980 | 0.95× |
| `screen-rgba8-smallest` | 97.9 | 82 | 100.1 | 244.3 | 83 | — | — | 0.40× | 0.989 | — | — | 0.98× |
| `screen-rgba16-store` | 15.3 | 16204 | 14.8 | 30.8 | 16206 | — | — | 0.50× | 1.000 | — | — | 1.03× |
| `screen-rgba16-fastest` | 90.6 | 341 | 94.8 | 34.7 | 214 | — | — | 2.61× | 1.596 | — | — | 0.96× |
| `screen-rgba16-optimal` | 101.5 | 103 | 105.6 | 77.8 | 106 | — | — | 1.30× | 0.980 | — | — | 0.96× |
| `screen-rgba16-smallest` | 181.4 | 99 | 184.3 | 302.1 | 99 | — | — | 0.60× | 0.996 | — | — | 0.98× |

Geometric means of current ÷ ImageSharp per compression level (time / size):

- store: time 0.52×, size 1.000×
- fastest: time 1.45×, size 1.413×
- optimal: time 0.85×, size 0.992×
- smallest: time 0.65×, size 0.998×

Opt-in parallel deflate (`ParallelFragments = 16`), not part of the single-thread comparison above:

| Case | current (1 thread) | parallel | speed-up | KB | size ÷ 1 thread |
|---|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 1.952 | 3.075 | 0.63× | 2026 | 1.000 |
| `photo-gray8-fastest` | 58.5 | 10.5 | 5.60× | 1903 | 1.002 |
| `photo-gray8-optimal` | 59.6 | 13.5 | 4.42× | 1252 | 1.000 |
| `photo-gray8-smallest` | 87.2 | 21.3 | 4.08× | 1319 | 1.001 |
| `photo-gray16-store` | 3.869 | 5.675 | 0.68× | 4052 | 1.000 |
| `photo-gray16-fastest` | 85.7 | 12.7 | 6.73× | 4274 | 1.000 |
| `photo-gray16-optimal` | 139.8 | 16.3 | 8.59× | 3577 | 1.000 |
| `photo-gray16-smallest` | 139.9 | 18.1 | 7.74× | 3616 | 1.000 |
| `photo-rgb8-store` | 6.038 | 8.805 | 0.69× | 6077 | 1.000 |
| `photo-rgb8-fastest` | 92.1 | 15.3 | 6.03× | 4871 | 1.002 |
| `photo-rgb8-optimal` | 155.3 | 18.4 | 8.45× | 3315 | 1.003 |
| `photo-rgb8-smallest` | 368.7 | 45.5 | 8.10× | 3286 | 1.004 |
| `photo-rgb16-store` | 11.0 | 15.4 | 0.71× | 12153 | 1.000 |
| `photo-rgb16-fastest` | 239.7 | 35.0 | 6.85× | 12804 | 1.000 |
| `photo-rgb16-optimal` | 404.9 | 45.3 | 8.94× | 10753 | 1.000 |
| `photo-rgb16-smallest` | 420.0 | 51.6 | 8.14× | 10737 | 1.000 |
| `photo-rgba8-store` | 7.668 | 12.6 | 0.61× | 8102 | 1.000 |
| `photo-rgba8-fastest` | 114.5 | 17.5 | 6.55× | 5652 | 1.002 |
| `photo-rgba8-optimal` | 236.0 | 27.7 | 8.53× | 3818 | 1.003 |
| `photo-rgba8-smallest` | 595.8 | 67.0 | 8.90× | 3801 | 1.004 |
| `photo-rgba16-store` | 16.0 | 19.0 | 0.84× | 16204 | 1.000 |
| `photo-rgba16-fastest` | 318.7 | 44.6 | 7.14× | 16822 | 1.000 |
| `photo-rgba16-optimal` | 532.0 | 60.3 | 8.82× | 13676 | 1.000 |
| `photo-rgba16-smallest` | 633.8 | 76.2 | 8.31× | 13683 | 1.000 |
| `screen-gray8-store` | 1.803 | 2.638 | 0.68× | 2026 | 1.000 |
| `screen-gray8-fastest` | 12.4 | 3.859 | 3.22× | 115 | 1.001 |
| `screen-gray8-optimal` | 15.6 | 4.517 | 3.45× | 60 | 0.995 |
| `screen-gray8-smallest` | 46.3 | 11.5 | 4.02× | 57 | 1.002 |
| `screen-gray16-store` | 3.394 | 4.888 | 0.69× | 4052 | 1.000 |
| `screen-gray16-fastest` | 23.7 | 3.812 | 6.23× | 195 | 1.002 |
| `screen-gray16-optimal` | 30.3 | 4.564 | 6.63× | 83 | 0.998 |
| `screen-gray16-smallest` | 77.8 | 10.9 | 7.14× | 82 | 1.001 |
| `screen-rgb8-store` | 5.695 | 7.766 | 0.73× | 6077 | 1.000 |
| `screen-rgb8-fastest` | 34.6 | 5.217 | 6.63× | 186 | 1.001 |
| `screen-rgb8-optimal` | 40.7 | 5.823 | 6.99× | 78 | 1.002 |
| `screen-rgb8-smallest` | 84.4 | 10.2 | 8.31× | 76 | 1.003 |
| `screen-rgb16-store` | 12.6 | 14.0 | 0.90× | 12153 | 1.000 |
| `screen-rgb16-fastest` | 68.5 | 10.1 | 6.80× | 298 | 1.000 |
| `screen-rgb16-optimal` | 78.9 | 10.7 | 7.36× | 96 | 1.004 |
| `screen-rgb16-smallest` | 132.8 | 16.4 | 8.12× | 90 | 1.005 |
| `screen-rgba8-store` | 7.862 | 11.2 | 0.70× | 8102 | 1.000 |
| `screen-rgba8-fastest` | 46.4 | 6.765 | 6.86× | 250 | 0.999 |
| `screen-rgba8-optimal` | 54.4 | 7.701 | 7.07× | 86 | 1.004 |
| `screen-rgba8-smallest` | 97.9 | 11.9 | 8.22× | 83 | 1.007 |
| `screen-rgba16-store` | 15.3 | 20.7 | 0.74× | 16204 | 1.000 |
| `screen-rgba16-fastest` | 90.6 | 13.3 | 6.81× | 341 | 1.000 |
| `screen-rgba16-optimal` | 101.5 | 14.0 | 7.26× | 104 | 1.003 |
| `screen-rgba16-smallest` | 181.4 | 22.1 | 8.20× | 99 | 1.005 |

Opt-in lossless reduction (`LosslessReduction = true`: the smallest exact layout, found by one scan of the image), not part of the comparison above:

| Case | current | reduced | time ÷ current | layout | KB | size ÷ current |
|---|---:|---:|---:|---|---:|---:|
| `photo-gray8-store` | 1.952 | 2.035 | 1.04× | grey 8-bit | 2026 | 1.000 |
| `photo-gray8-fastest` | 58.5 | 58.4 | 1.00× | grey 8-bit | 1899 | 1.000 |
| `photo-gray8-optimal` | 59.6 | 59.6 | 1.00× | grey 8-bit | 1253 | 1.000 |
| `photo-gray8-smallest` | 87.2 | 87.0 | 1.00× | grey 8-bit | 1318 | 1.000 |
| `photo-gray16-store` | 3.869 | 4.481 | 1.16× | grey 16-bit | 4052 | 1.000 |
| `photo-gray16-fastest` | 85.7 | 86.0 | 1.00× | grey 16-bit | 4274 | 1.000 |
| `photo-gray16-optimal` | 139.8 | 140.3 | 1.00× | grey 16-bit | 3577 | 1.000 |
| `photo-gray16-smallest` | 139.9 | 140.5 | 1.00× | grey 16-bit | 3616 | 1.000 |
| `photo-rgb8-store` | 6.038 | 7.517 | 1.25× | RGB 8-bit | 6077 | 1.000 |
| `photo-rgb8-fastest` | 92.1 | 92.0 | 1.00× | RGB 8-bit | 4860 | 1.000 |
| `photo-rgb8-optimal` | 155.3 | 152.3 | 0.98× | RGB 8-bit | 3304 | 1.000 |
| `photo-rgb8-smallest` | 368.7 | 366.8 | 0.99× | RGB 8-bit | 3273 | 1.000 |
| `photo-rgb16-store` | 11.0 | 12.5 | 1.14× | RGB 16-bit | 12153 | 1.000 |
| `photo-rgb16-fastest` | 239.7 | 241.1 | 1.01× | RGB 16-bit | 12803 | 1.000 |
| `photo-rgb16-optimal` | 404.9 | 411.7 | 1.02× | RGB 16-bit | 10752 | 1.000 |
| `photo-rgb16-smallest` | 420.0 | 420.6 | 1.00× | RGB 16-bit | 10735 | 1.000 |
| `photo-rgba8-store` | 7.668 | 10.1 | 1.31× | RGBA 8-bit | 8102 | 1.000 |
| `photo-rgba8-fastest` | 114.5 | 109.1 | 0.95× | RGBA 8-bit | 5643 | 1.000 |
| `photo-rgba8-optimal` | 236.0 | 233.2 | 0.99× | RGBA 8-bit | 3805 | 1.000 |
| `photo-rgba8-smallest` | 595.8 | 595.2 | 1.00× | RGBA 8-bit | 3787 | 1.000 |
| `photo-rgba16-store` | 16.0 | 16.0 | 1.00× | RGBA 16-bit | 16204 | 1.000 |
| `photo-rgba16-fastest` | 318.7 | 316.9 | 0.99× | RGBA 16-bit | 16819 | 1.000 |
| `photo-rgba16-optimal` | 532.0 | 534.7 | 1.01× | RGBA 16-bit | 13676 | 1.000 |
| `photo-rgba16-smallest` | 633.8 | 642.4 | 1.01× | RGBA 16-bit | 13680 | 1.000 |
| `screen-gray8-store` | 1.803 | 1.938 | 1.07× | grey 8-bit | 2026 | 1.000 |
| `screen-gray8-fastest` | 12.4 | 12.6 | 1.02× | grey 8-bit | 115 | 1.000 |
| `screen-gray8-optimal` | 15.6 | 16.1 | 1.03× | grey 8-bit | 60 | 1.000 |
| `screen-gray8-smallest` | 46.3 | 46.8 | 1.01× | grey 8-bit | 57 | 1.000 |
| `screen-gray16-store` | 3.394 | 5.536 | 1.63× | grey 16-bit | 4052 | 1.000 |
| `screen-gray16-fastest` | 23.7 | 24.9 | 1.05× | grey 16-bit | 195 | 1.000 |
| `screen-gray16-optimal` | 30.3 | 30.7 | 1.01× | grey 16-bit | 83 | 1.000 |
| `screen-gray16-smallest` | 77.8 | 77.8 | 1.00× | grey 16-bit | 82 | 1.000 |
| `screen-rgb8-store` | 5.695 | 6.859 | 1.20× | RGB 8-bit | 6077 | 1.000 |
| `screen-rgb8-fastest` | 34.6 | 35.8 | 1.04× | RGB 8-bit | 186 | 1.000 |
| `screen-rgb8-optimal` | 40.7 | 42.0 | 1.03× | RGB 8-bit | 78 | 1.000 |
| `screen-rgb8-smallest` | 84.4 | 84.7 | 1.00× | RGB 8-bit | 76 | 1.000 |
| `screen-rgb16-store` | 12.6 | 9.192 | 0.73× | RGB 8-bit | 6077 | 0.500 |
| `screen-rgb16-fastest` | 68.5 | 40.4 | 0.59× | RGB 8-bit | 186 | 0.624 |
| `screen-rgb16-optimal` | 78.9 | 44.2 | 0.56× | RGB 8-bit | 78 | 0.813 |
| `screen-rgb16-smallest` | 132.8 | 87.2 | 0.66× | RGB 8-bit | 76 | 0.849 |
| `screen-rgba8-store` | 7.862 | 10.5 | 1.34× | RGBA 8-bit | 8102 | 1.000 |
| `screen-rgba8-fastest` | 46.4 | 48.6 | 1.05× | RGBA 8-bit | 250 | 1.000 |
| `screen-rgba8-optimal` | 54.4 | 55.8 | 1.03× | RGBA 8-bit | 85 | 1.000 |
| `screen-rgba8-smallest` | 97.9 | 99.3 | 1.01× | RGBA 8-bit | 82 | 1.000 |
| `screen-rgba16-store` | 15.3 | 11.6 | 0.76× | RGBA 8-bit | 8102 | 0.500 |
| `screen-rgba16-fastest` | 90.6 | 57.8 | 0.64× | RGBA 8-bit | 250 | 0.734 |
| `screen-rgba16-optimal` | 101.5 | 62.8 | 0.62× | RGBA 8-bit | 85 | 0.824 |
| `screen-rgba16-smallest` | 181.4 | 103.1 | 0.57× | RGBA 8-bit | 82 | 0.834 |

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool and libpng allocates natively, so neither shows here: compare working sets with `peakmem`.

| Case | current | parallel | reduced | baseline | ImageSharp | libpng |
|---|---:|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 6136.8 | 7410.4 | 6142.1 | 6136.7 | 6142.2 | — |
| `photo-gray8-fastest` | 5717.7 | 7291.7 | 5723.0 | 5717.6 | 5512.4 | — |
| `photo-gray8-optimal` | 5363.0 | 4581.0 | 5368.3 | 5362.9 | 5490.8 | 1200.9 |
| `photo-gray8-smallest` | 5428.0 | 4830.7 | 5433.3 | 5427.9 | 5490.8 | — |
| `photo-gray16-store` | 12279.6 | 13375.7 | 12282.8 | 12279.5 | 12380.7 | — |
| `photo-gray16-fastest` | 20042.5 | 14072.0 | 20045.7 | 20042.4 | 12190.2 | — |
| `photo-gray16-optimal` | 11805.0 | 11573.2 | 11808.2 | 11805.0 | 12193.6 | 3581.1 |
| `photo-gray16-smallest` | 11844.3 | 11671.9 | 11847.5 | 11844.3 | 12193.6 | — |
| `photo-rgb8-store` | 22539.9 | 19282.6 | 22545.2 | 22539.9 | 22528.8 | — |
| `photo-rgb8-fastest` | 15965.6 | 15771.9 | 15970.9 | 15965.5 | 11785.4 | — |
| `photo-rgb8-optimal` | 11531.7 | 11173.5 | 11537.0 | 11531.6 | 11689.6 | 3404.3 |
| `photo-rgb8-smallest` | 11501.1 | 11071.2 | 11506.4 | 11501.0 | 11678.3 | — |
| `photo-rgb16-store` | 45085.9 | 38556.5 | 45089.0 | 45085.8 | 45153.9 | — |
| `photo-rgb16-fastest` | 45736.2 | 40529.8 | 45739.3 | 45736.1 | 44413.3 | — |
| `photo-rgb16-optimal` | 43685.1 | 33426.2 | 43688.2 | 43685.0 | 44561.1 | 10767.2 |
| `photo-rgb16-smallest` | 43667.8 | 33377.3 | 43670.9 | 43667.7 | 44561.1 | — |
| `photo-rgba8-store` | 24565.2 | 25707.1 | 24570.6 | 24565.2 | 24554.0 | — |
| `photo-rgba8-fastest` | 18441.9 | 18347.3 | 18447.3 | 18441.9 | 12440.3 | — |
| `photo-rgba8-optimal` | 12033.1 | 13007.2 | 12038.4 | 12033.0 | 12195.5 | 3911.4 |
| `photo-rgba8-smallest` | 12015.0 | 12984.4 | 12020.3 | 12014.9 | 12173.6 | — |
| `photo-rgba16-store` | 49136.5 | 51406.0 | 49139.7 | 49136.4 | 49204.1 | — |
| `photo-rgba16-fastest` | 82692.3 | 53537.4 | 82695.5 | 82692.3 | 47495.2 | — |
| `photo-rgba16-optimal` | 46608.4 | 42870.2 | 46611.6 | 46608.4 | 47640.4 | — |
| `photo-rgba16-smallest` | 46613.2 | 42860.6 | 46616.4 | 46613.1 | 47640.3 | — |
| `screen-gray8-store` | 6136.8 | 7410.4 | 6140.5 | 6136.7 | 6142.1 | — |
| `screen-gray8-fastest` | 370.9 | 511.6 | 374.7 | 370.8 | 346.5 | — |
| `screen-gray8-optimal` | 181.5 | 335.2 | 185.3 | 181.4 | 194.5 | 58.7 |
| `screen-gray8-smallest` | 178.5 | 326.9 | 182.3 | 178.5 | 192.7 | — |
| `screen-gray16-store` | 12279.6 | 13375.9 | 12282.8 | 12279.5 | 12380.7 | — |
| `screen-gray16-fastest` | 707.1 | 910.7 | 710.2 | 707.0 | 754.9 | — |
| `screen-gray16-optimal` | 332.9 | 448.1 | 336.1 | 332.9 | 449.4 | 84.0 |
| `screen-gray16-smallest` | 331.7 | 444.5 | 334.9 | 331.7 | 446.5 | — |
| `screen-rgb8-store` | 22539.9 | 19282.5 | 22545.2 | 22539.9 | 22528.8 | — |
| `screen-rgb8-fastest` | 698.6 | 824.6 | 703.9 | 698.5 | 657.6 | — |
| `screen-rgb8-optimal` | 328.0 | 444.7 | 333.3 | 327.9 | 345.8 | 81.6 |
| `screen-rgb8-smallest` | 326.1 | 434.5 | 331.4 | 326.0 | 342.5 | — |
| `screen-rgb16-store` | 45085.9 | 38556.5 | 22545.2 | 45085.8 | 45153.9 | — |
| `screen-rgb16-fastest` | 1322.7 | 1416.7 | 703.9 | 1322.7 | 803.8 | — |
| `screen-rgb16-optimal` | 345.9 | 699.7 | 333.3 | 345.8 | 466.0 | 99.7 |
| `screen-rgb16-smallest` | 339.6 | 676.1 | 331.4 | 339.6 | 458.1 | — |
| `screen-rgba8-store` | 24565.2 | 25707.2 | 24570.6 | 24565.2 | 24554.0 | — |
| `screen-rgba8-fastest` | 762.9 | 1102.4 | 768.2 | 762.8 | 676.6 | — |
| `screen-rgba8-optimal` | 335.3 | 533.7 | 340.6 | 335.2 | 350.5 | 86.9 |
| `screen-rgba8-smallest` | 332.3 | 521.2 | 337.6 | 332.2 | 348.3 | — |
| `screen-rgba16-store` | 49136.5 | 51406.0 | 24570.6 | 49136.4 | 49204.1 | — |
| `screen-rgba16-fastest` | 1365.5 | 1715.5 | 768.2 | 1365.4 | 843.8 | — |
| `screen-rgba16-optimal` | 353.5 | 855.1 | 340.6 | 353.4 | 475.3 | — |
| `screen-rgba16-smallest` | 348.7 | 835.9 | 337.6 | 348.6 | 467.7 | — |

Notes:

- `photo-rgba16-optimal`: LibPng: output does not round-trip (samples differ), not timed
- `screen-rgba16-optimal`: LibPng: output does not round-trip (samples differ), not timed

### Encode, store level only (in-process medians of 31 interleaved rounds, ms; sizes in KB)

`current` = `PngWriter.Encode*` with `PngWriteOptions.CompressionLevel`, one thread; ImageSharp = `PngEncoder` at the same zlib level (store 0, fastest 1, optimal 6, smallest 9), adaptive filters, one thread; libpng = simplified API `png_image_write_to_memory`, which has no level setting, so it appears on the optimal rows (its default). Every output is decoded and must reproduce the samples exactly.

| Case | current | KB | baseline | ImageSharp | KB | libpng | KB | time ÷ ImageSharp | size ÷ ImageSharp | time ÷ libpng | size ÷ libpng | time ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 1.982 | 2026 | 1.972 | 3.597 | 2027 | — | — | 0.55× | 1.000 | — | — | 1.01× |
| `photo-gray16-store` | 3.864 | 4052 | 3.907 | 7.945 | 4052 | — | — | 0.49× | 1.000 | — | — | 0.99× |
| `photo-rgb8-store` | 6.408 | 6077 | 6.201 | 10.7 | 6078 | — | — | 0.60× | 1.000 | — | — | 1.03× |
| `photo-rgb16-store` | 12.2 | 12153 | 12.2 | 24.9 | 12155 | — | — | 0.49× | 1.000 | — | — | 1.00× |
| `photo-rgba8-store` | 7.720 | 8102 | 7.717 | 13.9 | 8104 | — | — | 0.56× | 1.000 | — | — | 1.00× |
| `photo-rgba16-store` | 15.0 | 16204 | 14.8 | 31.0 | 16206 | — | — | 0.48× | 1.000 | — | — | 1.01× |
| `screen-gray8-store` | 1.877 | 2026 | 1.877 | 3.441 | 2027 | — | — | 0.55× | 1.000 | — | — | 1.00× |
| `screen-gray16-store` | 3.807 | 4052 | 3.759 | 7.771 | 4052 | — | — | 0.49× | 1.000 | — | — | 1.01× |
| `screen-rgb8-store` | 6.025 | 6077 | 6.059 | 10.3 | 6078 | — | — | 0.58× | 1.000 | — | — | 0.99× |
| `screen-rgb16-store` | 11.6 | 12153 | 11.7 | 24.5 | 12155 | — | — | 0.47× | 1.000 | — | — | 0.99× |
| `screen-rgba8-store` | 7.337 | 8102 | 7.363 | 13.0 | 8104 | — | — | 0.56× | 1.000 | — | — | 1.00× |
| `screen-rgba16-store` | 15.3 | 16204 | 15.0 | 33.7 | 16206 | — | — | 0.45× | 1.000 | — | — | 1.03× |

Geometric means of current ÷ ImageSharp per compression level (time / size):

- store: time 0.52×, size 1.000×

Opt-in parallel deflate (`ParallelFragments = 16`), not part of the single-thread comparison above:

| Case | current (1 thread) | parallel | speed-up | KB | size ÷ 1 thread |
|---|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 1.982 | 3.002 | 0.66× | 2026 | 1.000 |
| `photo-gray16-store` | 3.864 | 5.578 | 0.69× | 4052 | 1.000 |
| `photo-rgb8-store` | 6.408 | 8.842 | 0.72× | 6077 | 1.000 |
| `photo-rgb16-store` | 12.2 | 15.5 | 0.78× | 12153 | 1.000 |
| `photo-rgba8-store` | 7.720 | 11.9 | 0.65× | 8102 | 1.000 |
| `photo-rgba16-store` | 15.0 | 20.1 | 0.75× | 16204 | 1.000 |
| `screen-gray8-store` | 1.877 | 2.882 | 0.65× | 2026 | 1.000 |
| `screen-gray16-store` | 3.807 | 5.244 | 0.73× | 4052 | 1.000 |
| `screen-rgb8-store` | 6.025 | 7.965 | 0.76× | 6077 | 1.000 |
| `screen-rgb16-store` | 11.6 | 14.7 | 0.79× | 12153 | 1.000 |
| `screen-rgba8-store` | 7.337 | 11.5 | 0.64× | 8102 | 1.000 |
| `screen-rgba16-store` | 15.3 | 18.5 | 0.83× | 16204 | 1.000 |

Opt-in lossless reduction (`LosslessReduction = true`: the smallest exact layout, found by one scan of the image), not part of the comparison above:

| Case | current | reduced | time ÷ current | layout | KB | size ÷ current |
|---|---:|---:|---:|---|---:|---:|
| `photo-gray8-store` | 1.982 | 2.023 | 1.02× | grey 8-bit | 2026 | 1.000 |
| `photo-gray16-store` | 3.864 | 4.536 | 1.17× | grey 16-bit | 4052 | 1.000 |
| `photo-rgb8-store` | 6.408 | 7.057 | 1.10× | RGB 8-bit | 6077 | 1.000 |
| `photo-rgb16-store` | 12.2 | 12.2 | 1.00× | RGB 16-bit | 12153 | 1.000 |
| `photo-rgba8-store` | 7.720 | 8.738 | 1.13× | RGBA 8-bit | 8102 | 1.000 |
| `photo-rgba16-store` | 15.0 | 14.6 | 0.97× | RGBA 16-bit | 16204 | 1.000 |
| `screen-gray8-store` | 1.877 | 2.076 | 1.11× | grey 8-bit | 2026 | 1.000 |
| `screen-gray16-store` | 3.807 | 4.093 | 1.07× | grey 16-bit | 4052 | 1.000 |
| `screen-rgb8-store` | 6.025 | 7.844 | 1.30× | RGB 8-bit | 6077 | 1.000 |
| `screen-rgb16-store` | 11.6 | 8.820 | 0.76× | RGB 8-bit | 6077 | 0.500 |
| `screen-rgba8-store` | 7.337 | 8.762 | 1.19× | RGBA 8-bit | 8102 | 1.000 |
| `screen-rgba16-store` | 15.3 | 11.0 | 0.72× | RGBA 8-bit | 8102 | 0.500 |

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool and libpng allocates natively, so neither shows here: compare working sets with `peakmem`.

| Case | current | parallel | reduced | baseline | ImageSharp | libpng |
|---|---:|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 6136.8 | 7410.4 | 6142.1 | 6136.7 | 6142.2 | — |
| `photo-gray16-store` | 12279.6 | 13375.7 | 12282.8 | 12279.5 | 12380.7 | — |
| `photo-rgb8-store` | 22539.9 | 19282.6 | 22545.2 | 22539.9 | 22528.8 | — |
| `photo-rgb16-store` | 45085.9 | 38556.6 | 45089.0 | 45085.8 | 45153.9 | — |
| `photo-rgba8-store` | 24565.2 | 25707.3 | 24570.6 | 24565.2 | 24554.0 | — |
| `photo-rgba16-store` | 49136.5 | 51406.0 | 49139.7 | 49136.4 | 49204.1 | — |
| `screen-gray8-store` | 6136.8 | 7410.5 | 6140.5 | 6136.7 | 6142.1 | — |
| `screen-gray16-store` | 12279.6 | 13375.5 | 12282.8 | 12279.5 | 12380.7 | — |
| `screen-rgb8-store` | 22539.9 | 19282.8 | 22545.2 | 22539.9 | 22528.8 | — |
| `screen-rgb16-store` | 45085.9 | 38556.4 | 22545.2 | 45085.8 | 45153.9 | — |
| `screen-rgba8-store` | 24565.2 | 25707.4 | 24570.6 | 24565.2 | 24554.0 | — |
| `screen-rgba16-store` | 49136.5 | 51406.0 | 24570.6 | 49136.4 | 49204.1 | — |
