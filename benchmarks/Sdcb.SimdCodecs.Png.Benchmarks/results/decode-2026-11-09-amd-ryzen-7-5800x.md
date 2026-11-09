# PNG decode matrix at 2.0 release prep — 2026-11-09, AMD Ryzen 7 5800X

Command: `LIBPNG_PATH=…/vcpkg/installed/x64-windows/bin/libpng16.dll dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Png.Benchmarks -- matrix decode` at `0cc7f0c`. The README quotes the 8-bit RGB / RGBA photo rows (`photo-rgb8`, `photo-rgba8`, geometric mean): 0.88× libpng, ImageSharp 0.91× libpng; the same rows on the synthetic screen content are 0.20× (ImageSharp 0.28×).

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
- Baseline: f0b6850cdfc3 docs: split known-gaps.md into one file per format

### Decode to 8-bit RGBA (in-process medians of 7 interleaved rounds, ms)

`current` = `PngImageDecoder.TryDecodeIntoRgba8` into a caller buffer; `native` = `PngReader.Decode` (samples as stored, no RGBA expansion); ImageSharp = `Image.Load<Rgba32>` + `CopyPixelDataTo`, one thread; libpng = simplified API `png_image_finish_read` to RGBA. Ratios below 1 mean we are faster. Inputs are encoded by ImageSharp (adaptive filters, zlib level 6).

| Case | KB | current | native | baseline | ImageSharp | libpng | current MP/s | ÷ ImageSharp | ÷ libpng | ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-g1` | 28 | 3.381 | 1.981 | 3.443 | 6.016 | 8.401 | 613 | 0.56× | 0.40× | 0.98× |
| `photo-g2` | 167 | 4.474 | 4.036 | 4.654 | 8.718 | 12.1 | 464 | 0.51× | 0.37× | 0.96× |
| `photo-g4` | 427 | 7.682 | 7.746 | 7.863 | 12.5 | 14.2 | 270 | 0.62× | 0.54× | 0.98× |
| `photo-g8` | 1297 | 20.9 | 19.9 | 20.3 | 22.5 | 24.3 | 99 | 0.93× | 0.86× | 1.03× |
| `photo-g16` | 3588 | 23.8 | 23.6 | 22.4 | 27.1 | — | 87 | 0.88× | — | 1.06× |
| `photo-rgb8` | 3309 | 40.0 | 38.2 | 38.2 | 44.6 | 44.5 | 52 | 0.90× | 0.90× | 1.05× |
| `photo-rgb16` | 10762 | 57.8 | 58.1 | 57.5 | 95.4 | — | 36 | 0.61× | — | 1.00× |
| `photo-p1` | 123 | 4.588 | 3.369 | 4.957 | 15.5 | 11.7 | 452 | 0.30× | 0.39× | 0.93× |
| `photo-p2` | 268 | 6.131 | 6.256 | 5.815 | 16.9 | 13.6 | 338 | 0.36× | 0.45× | 1.05× |
| `photo-p4` | 567 | 8.760 | 8.801 | 8.905 | 19.1 | 16.3 | 237 | 0.46× | 0.54× | 0.98× |
| `photo-p8` | 1406 | 14.2 | 13.1 | 14.1 | 22.0 | 20.0 | 146 | 0.64× | 0.71× | 1.00× |
| `photo-p8a` | 1051 | 13.1 | 11.4 | 12.5 | 36.4 | 17.1 | 158 | 0.36× | 0.77× | 1.05× |
| `photo-ga8` | 1772 | 24.9 | 25.0 | 24.5 | 32.3 | 30.4 | 83 | 0.77× | 0.82× | 1.02× |
| `photo-ga16` | 6121 | 46.6 | 47.8 | 44.7 | 50.9 | — | 45 | 0.91× | — | 1.04× |
| `photo-rgba8` | 3809 | 44.0 | 49.4 | 42.7 | 41.7 | 50.9 | 47 | 1.05× | 0.86× | 1.03× |
| `photo-rgba16` | 13672 | 78.2 | 85.7 | 79.3 | 151.4 | — | 27 | 0.52× | — | 0.99× |
| `photo-g1-adam7` | 35 | 5.320 | 4.983 | 5.471 | 10.8 | 26.8 | 390 | 0.49× | 0.20× | 0.97× |
| `photo-g2-adam7` | 197 | 6.940 | 8.443 | 6.733 | 13.3 | 31.2 | 299 | 0.52× | 0.22× | 1.03× |
| `photo-g4-adam7` | 490 | 10.5 | 11.0 | 11.5 | 15.5 | 31.4 | 197 | 0.68× | 0.34× | 0.92× |
| `photo-g8-adam7` | 1418 | 22.6 | 22.0 | 21.3 | 22.4 | 41.7 | 92 | 1.01× | 0.54× | 1.06× |
| `photo-g16-adam7` | 3689 | 27.3 | 26.3 | 26.7 | 31.2 | — | 76 | 0.88× | — | 1.02× |
| `photo-rgb8-adam7` | 3830 | 42.7 | 51.4 | 43.6 | 48.2 | 63.3 | 49 | 0.89× | 0.68× | 0.98× |
| `photo-rgb16-adam7` | 11055 | 57.1 | 69.4 | 61.5 | 80.3 | — | 36 | 0.71× | — | 0.93× |
| `photo-p1-adam7` | 130 | 6.241 | 6.930 | 6.844 | 18.1 | 29.2 | 332 | 0.34× | 0.21× | 0.91× |
| `photo-p2-adam7` | 289 | 8.302 | 9.329 | 9.160 | 20.9 | 32.5 | 250 | 0.40× | 0.26× | 0.91× |
| `photo-p4-adam7` | 620 | 12.9 | 13.7 | 13.0 | 23.6 | 35.7 | 161 | 0.54× | 0.36× | 0.99× |
| `photo-p8-adam7` | 1499 | 18.8 | 17.4 | 18.9 | 24.5 | 39.7 | 111 | 0.77× | 0.47× | 0.99× |
| `photo-p8a-adam7` | 1174 | 16.4 | 15.9 | 17.0 | 37.2 | 36.5 | 126 | 0.44× | 0.45× | 0.97× |
| `photo-ga8-adam7` | 1998 | 28.2 | 29.4 | 28.5 | 30.6 | 46.0 | 73 | 0.92× | 0.61× | 0.99× |
| `photo-ga16-adam7` | 6382 | 50.0 | 53.1 | 51.6 | 52.2 | — | 41 | 0.96× | — | 0.97× |
| `photo-rgba8-adam7` | 4414 | 49.8 | 54.2 | 49.4 | 48.8 | 69.5 | 42 | 1.02× | 0.72× | 1.01× |
| `photo-rgba16-adam7` | 14072 | 79.5 | 89.9 | 79.7 | 155.4 | — | 26 | 0.51× | — | 1.00× |
| `screen-g1` | 20 | 2.934 | 1.650 | 3.024 | 6.017 | 8.081 | 707 | 0.49× | 0.36× | 0.97× |
| `screen-g2` | 36 | 2.427 | 1.959 | 2.415 | 6.159 | 9.940 | 854 | 0.39× | 0.24× | 1.01× |
| `screen-g4` | 45 | 3.077 | 2.568 | 3.075 | 7.048 | 10.4 | 674 | 0.44× | 0.30× | 1.00× |
| `screen-g8` | 59 | 4.483 | 3.779 | 4.501 | 6.025 | 11.0 | 463 | 0.74× | 0.41× | 1.00× |
| `screen-g16` | 83 | 2.362 | 2.414 | 2.310 | 5.464 | — | 878 | 0.43× | — | 1.02× |
| `screen-rgb8` | 81 | 3.446 | 4.083 | 3.773 | 6.363 | 16.5 | 602 | 0.54× | 0.21× | 0.91× |
| `screen-rgb16` | 97 | 5.134 | 5.434 | 4.758 | 21.4 | — | 404 | 0.24× | — | 1.08× |
| `screen-p1` | 47 | 2.272 | 1.765 | 2.890 | 9.046 | 7.457 | 913 | 0.25× | 0.30× | 0.79× |
| `screen-p2` | 67 | 2.233 | 1.799 | 1.844 | 9.699 | 7.509 | 929 | 0.23× | 0.30× | 1.21× |
| `screen-p4` | 66 | 2.280 | 2.407 | 1.831 | 12.0 | 10.1 | 910 | 0.19× | 0.23× | 1.25× |
| `screen-p8` | 84 | 1.888 | 1.018 | 1.826 | 8.608 | 7.042 | 1098 | 0.22× | 0.27× | 1.03× |
| `screen-p8a` | 84 | 1.531 | 1.461 | 2.439 | 18.3 | 7.016 | 1355 | 0.08× | 0.22× | 0.63× |
| `screen-ga8` | 71 | 2.911 | 2.910 | 2.689 | 6.360 | 12.5 | 712 | 0.46× | 0.23× | 1.08× |
| `screen-ga16` | 101 | 3.198 | 4.033 | 4.360 | 6.843 | — | 648 | 0.47× | — | 0.73× |
| `screen-rgba8` | 85 | 3.822 | 4.341 | 3.230 | 4.112 | 19.9 | 543 | 0.93× | 0.19× | 1.18× |
| `screen-rgba16` | 106 | 5.323 | 6.940 | 5.187 | 21.3 | — | 390 | 0.25× | — | 1.03× |
| `screen-g1-adam7` | 23 | 4.067 | 3.523 | 4.702 | 9.574 | 23.7 | 510 | 0.42× | 0.17× | 0.86× |
| `screen-g2-adam7` | 39 | 2.346 | 3.876 | 2.887 | 9.134 | 19.3 | 884 | 0.26× | 0.12× | 0.81× |
| `screen-g4-adam7` | 51 | 2.829 | 3.135 | 2.726 | 5.678 | 17.7 | 733 | 0.50× | 0.16× | 1.04× |
| `screen-g8-adam7` | 67 | 3.299 | 3.637 | 3.194 | 5.035 | 19.2 | 629 | 0.66× | 0.17× | 1.03× |
| `screen-g16-adam7` | 92 | 4.060 | 3.918 | 3.177 | 8.623 | — | 511 | 0.47× | — | 1.28× |
| `screen-rgb8-adam7` | 92 | 4.111 | 12.2 | 4.345 | 8.918 | 26.9 | 504 | 0.46× | 0.15× | 0.95× |
| `screen-rgb16-adam7` | 113 | 4.516 | 10.2 | 4.449 | 14.0 | — | 459 | 0.32× | — | 1.02× |
| `screen-p1-adam7` | 50 | 4.151 | 3.770 | 4.108 | 14.3 | 20.0 | 500 | 0.29× | 0.21× | 1.01× |
| `screen-p2-adam7` | 72 | 3.065 | 2.748 | 2.766 | 12.9 | 23.7 | 677 | 0.24× | 0.13× | 1.11× |
| `screen-p4-adam7` | 72 | 2.709 | 4.237 | 3.775 | 14.0 | 21.7 | 765 | 0.19× | 0.12× | 0.72× |
| `screen-p8-adam7` | 99 | 2.517 | 2.587 | 2.426 | 7.566 | 19.8 | 824 | 0.33× | 0.13× | 1.04× |
| `screen-p8a-adam7` | 100 | 3.279 | 3.586 | 2.933 | 18.2 | 23.9 | 632 | 0.18× | 0.14× | 1.12× |
| `screen-ga8-adam7` | 82 | 3.349 | 3.891 | 4.566 | 5.862 | 22.5 | 619 | 0.57× | 0.15× | 0.73× |
| `screen-ga16-adam7` | 115 | 4.768 | 5.639 | 3.395 | 6.561 | — | 435 | 0.73× | — | 1.40× |
| `screen-rgba8-adam7` | 99 | 5.772 | 7.358 | 5.352 | 7.657 | 33.1 | 359 | 0.75× | 0.17× | 1.08× |
| `screen-rgba16-adam7` | 124 | 4.210 | 6.428 | 4.517 | 17.7 | — | 493 | 0.24× | — | 0.93× |

Geometric means of current ÷ competitor over the cases both decode:

- vs ImageSharp, photo: 0.63× (32 cases)
- vs ImageSharp, screen: 0.36× (32 cases)
- vs libpng, photo: 0.48× (24 cases)
- vs libpng, screen: 0.20× (24 cases)
- vs baseline, photo: 0.99× (32 cases)
- vs baseline, screen: 0.99× (32 cases)

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool and libpng allocates natively, so neither shows here: compare working sets with `peakmem`.

| Case | current | native | baseline | ImageSharp | libpng |
|---|---:|---:|---:|---:|---:|
| `photo-g1` | 1.7 | 2026.3 | 1.7 | 105.0 | 0.0 |
| `photo-g2` | 1.7 | 2026.8 | 1.7 | 105.0 | 0.0 |
| `photo-g4` | 1.8 | 2027.8 | 1.8 | 105.4 | 0.0 |
| `photo-g8` | 1.2 | 2028.2 | 1.2 | 5.4 | 0.0 |
| `photo-g16` | 1.7 | 4055.6 | 1.7 | 8.8 | — |
| `photo-rgb8` | 1.7 | 6082.5 | 1.7 | 8.3 | 0.0 |
| `photo-rgb16` | 4.8 | 12166.2 | 4.8 | 19.3 | — |
| `photo-p1` | 1.7 | 2026.4 | 1.7 | 105.1 | 0.0 |
| `photo-p2` | 1.8 | 2026.9 | 1.8 | 105.4 | 0.0 |
| `photo-p4` | 2.0 | 2028.0 | 2.0 | 106.0 | 0.0 |
| `photo-p8` | 3.0 | 2029.0 | 3.0 | 10.4 | 0.0 |
| `photo-p8a` | 3.2 | 2029.3 | 3.2 | 18.3 | 0.0 |
| `photo-ga8` | 1.2 | 4055.1 | 1.2 | 6.1 | 0.0 |
| `photo-ga16` | 2.7 | 8110.4 | 2.7 | 12.5 | — |
| `photo-rgba8` | 9.2 | 8109.4 | 9.2 | 9.1 | 0.0 |
| `photo-rgba16` | 4.8 | 16219.9 | 4.8 | 23.5 | — |
| `photo-g1-adam7` | 1.7 | 2025.8 | 1.7 | 193.4 | 0.0 |
| `photo-g2-adam7` | 1.7 | 2025.8 | 1.6 | 193.7 | 0.0 |
| `photo-g4-adam7` | 1.8 | 2025.9 | 1.7 | 194.0 | 0.0 |
| `photo-g8-adam7` | 1.2 | 2026.3 | 1.1 | 5.6 | 0.0 |
| `photo-g16-adam7` | 1.7 | 4051.8 | 1.6 | 8.9 | — |
| `photo-rgb8-adam7` | 1.7 | 6076.8 | 1.6 | 9.1 | 0.0 |
| `photo-rgb16-adam7` | 4.8 | 12154.9 | 4.7 | 19.7 | — |
| `photo-p1-adam7` | 1.7 | 2025.8 | 1.7 | 193.8 | 0.0 |
| `photo-p2-adam7` | 1.8 | 2025.9 | 1.8 | 194.0 | 0.0 |
| `photo-p4-adam7` | 2.0 | 2026.1 | 1.9 | 194.7 | 0.0 |
| `photo-p8-adam7` | 3.0 | 2027.1 | 2.9 | 10.6 | 0.0 |
| `photo-p8a-adam7` | 3.2 | 2027.4 | 3.2 | 18.5 | 0.0 |
| `photo-ga8-adam7` | 1.2 | 4051.3 | 1.1 | 6.5 | 0.0 |
| `photo-ga16-adam7` | 2.7 | 8102.9 | 2.7 | 12.8 | — |
| `photo-rgba8-adam7` | 2.7 | 8102.9 | 2.7 | 9.9 | 0.0 |
| `photo-rgba16-adam7` | 4.8 | 16204.9 | 4.7 | 24.1 | — |
| `screen-g1` | 1.7 | 2026.3 | 1.7 | 104.8 | 0.0 |
| `screen-g2` | 1.7 | 2026.8 | 1.7 | 104.8 | 0.0 |
| `screen-g4` | 1.7 | 2027.7 | 1.7 | 104.8 | 0.0 |
| `screen-g8` | 0.7 | 2027.7 | 0.7 | 3.5 | 0.0 |
| `screen-g16` | 0.7 | 4054.6 | 0.7 | 3.6 | — |
| `screen-rgb8` | 0.7 | 6081.5 | 0.7 | 3.6 | 0.0 |
| `screen-rgb16` | 0.7 | 12162.1 | 0.7 | 3.6 | — |
| `screen-p1` | 1.7 | 2026.4 | 1.7 | 105.0 | 0.0 |
| `screen-p2` | 1.7 | 2026.8 | 1.7 | 105.1 | 0.0 |
| `screen-p4` | 1.8 | 2027.8 | 1.8 | 105.3 | 0.0 |
| `screen-p8` | 2.5 | 2028.5 | 2.5 | 8.5 | 0.0 |
| `screen-p8a` | 2.7 | 2028.8 | 2.7 | 16.9 | 0.0 |
| `screen-ga8` | 0.7 | 4054.6 | 0.7 | 3.6 | 0.0 |
| `screen-ga16` | 0.7 | 8108.3 | 0.7 | 3.6 | — |
| `screen-rgba8` | 8.2 | 8108.3 | 8.2 | 3.6 | 0.0 |
| `screen-rgba16` | 0.7 | 16215.8 | 0.7 | 3.6 | — |
| `screen-g1-adam7` | 1.7 | 2025.8 | 1.6 | 193.4 | 0.0 |
| `screen-g2-adam7` | 1.7 | 2025.8 | 1.6 | 193.4 | 0.0 |
| `screen-g4-adam7` | 1.7 | 2025.8 | 1.6 | 195.2 | 0.0 |
| `screen-g8-adam7` | 0.7 | 2025.8 | 0.6 | 3.6 | 0.0 |
| `screen-g16-adam7` | 0.7 | 4050.8 | 0.6 | 3.6 | — |
| `screen-rgb8-adam7` | 0.7 | 6075.8 | 0.6 | 3.6 | 0.0 |
| `screen-rgb16-adam7` | 0.7 | 12150.8 | 0.6 | 3.6 | — |
| `screen-p1-adam7` | 1.7 | 2025.8 | 1.7 | 193.6 | 0.0 |
| `screen-p2-adam7` | 1.7 | 2025.8 | 1.7 | 193.7 | 0.0 |
| `screen-p4-adam7` | 1.8 | 2025.9 | 1.7 | 195.7 | 0.0 |
| `screen-p8-adam7` | 2.5 | 2026.6 | 2.4 | 8.5 | 0.0 |
| `screen-p8a-adam7` | 2.7 | 2026.9 | 2.7 | 16.9 | 0.0 |
| `screen-ga8-adam7` | 0.7 | 4050.8 | 0.6 | 3.6 | 0.0 |
| `screen-ga16-adam7` | 0.7 | 8100.8 | 0.6 | 3.6 | — |
| `screen-rgba8-adam7` | 0.7 | 8100.8 | 0.6 | 3.6 | 0.0 |
| `screen-rgba16-adam7` | 0.7 | 16200.8 | 0.6 | 3.6 | — |

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
