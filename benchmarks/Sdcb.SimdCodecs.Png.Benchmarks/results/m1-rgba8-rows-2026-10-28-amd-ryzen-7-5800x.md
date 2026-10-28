# Row-wise RGBA8 decode, table/shuffle expansion, unfiltered stored rows — 2026-10-28, AMD Ryzen 7 5800X

Same-process A/B against `244ec85` (baseline). Command: `LIBPNG_PATH=…/vcpkg/installed/x64-windows/bin/libpng16.dll dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Png.Benchmarks -- matrix all`
(in-process interleaved medians).

- `TryDecodeIntoRgba8` / `TryDecodeRgba8` decode a row at a time straight into the RGBA destination (no image-sized sample buffer; RGBA8 unfiltered in place).
- RGBA expansion: SSSE3 / AdvSimd byte shuffles for grey, grey+alpha, RGB, 16-bit; a 256-entry RGBA table for indexed and packed 1/2/4-bit grey.
- `CompressionLevel.NoCompression`: every row filter None (no five-way filter choice).

Decode, current ÷ baseline (geometric mean): photo 0.63×, screen 0.38×. Store encode: 0.15–0.29× of the baseline; other levels unchanged (0.96–1.05×).

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
- Baseline: 244ec853b17e Add the PNG encode/decode benchmark; put every benchmark in SimdCodecs.slnx

### Decode to 8-bit RGBA (in-process medians of 7 interleaved rounds, ms)

`current` = `PngImageDecoder.TryDecodeIntoRgba8` into a caller buffer; `native` = `PngReader.Decode` (samples as stored, no RGBA expansion); ImageSharp = `Image.Load<Rgba32>` + `CopyPixelDataTo`, one thread; libpng = simplified API `png_image_finish_read` to RGBA. Ratios below 1 mean we are faster. Inputs are encoded by ImageSharp (adaptive filters, zlib level 6).

| Case | KB | current | native | baseline | ImageSharp | libpng | current MP/s | ÷ ImageSharp | ÷ libpng | ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-g1` | 28 | 1.759 | 1.181 | 6.263 | 4.122 | 4.057 | 1179 | 0.43× | 0.43× | 0.28× |
| `photo-g2` | 167 | 2.800 | 2.631 | 7.374 | 4.903 | 5.905 | 741 | 0.57× | 0.47× | 0.38× |
| `photo-g4` | 427 | 5.040 | 4.725 | 9.456 | 6.640 | 8.387 | 411 | 0.76× | 0.60× | 0.53× |
| `photo-g8` | 1297 | 16.5 | 16.2 | 18.9 | 14.4 | 15.0 | 126 | 1.14× | 1.10× | 0.87× |
| `photo-g16` | 3588 | 23.8 | 23.7 | 26.4 | 18.1 | — | 87 | 1.31× | — | 0.90× |
| `photo-rgb8` | 3309 | 33.9 | 33.8 | 37.8 | 26.3 | 29.4 | 61 | 1.29× | 1.15× | 0.90× |
| `photo-rgb16` | 10762 | 81.5 | 81.6 | 85.9 | 66.8 | — | 25 | 1.22× | — | 0.95× |
| `photo-p1` | 123 | 2.759 | 2.132 | 9.229 | 8.098 | 5.740 | 751 | 0.34× | 0.48× | 0.30× |
| `photo-p2` | 268 | 3.712 | 3.451 | 10.4 | 9.175 | 7.103 | 559 | 0.40× | 0.52× | 0.36× |
| `photo-p4` | 567 | 5.796 | 5.603 | 12.4 | 10.8 | 9.042 | 358 | 0.54× | 0.64× | 0.47× |
| `photo-p8` | 1406 | 10.5 | 10.1 | 15.2 | 13.3 | 12.0 | 198 | 0.79× | 0.88× | 0.69× |
| `photo-p8a` | 1051 | 8.982 | 8.459 | 14.6 | 18.2 | 10.3 | 231 | 0.49× | 0.87× | 0.62× |
| `photo-ga8` | 1772 | 22.7 | 22.7 | 26.7 | 18.5 | 19.2 | 91 | 1.23× | 1.18× | 0.85× |
| `photo-ga16` | 6121 | 60.3 | 60.7 | 64.6 | 30.5 | — | 34 | 1.98× | — | 0.93× |
| `photo-rgba8` | 3809 | 39.2 | 39.5 | 43.9 | 24.8 | 34.3 | 53 | 1.58× | 1.14× | 0.89× |
| `photo-rgba16` | 13672 | 140.7 | 141.0 | 145.4 | 98.2 | — | 15 | 1.43× | — | 0.97× |
| `photo-g1-adam7` | 35 | — | — | — | 5.217 | 15.3 | — | — | — | — |
| `photo-g2-adam7` | 197 | — | — | — | 6.492 | 17.5 | — | — | — | — |
| `photo-g4-adam7` | 490 | — | — | — | 8.529 | 20.0 | — | — | — | — |
| `photo-g8-adam7` | 1418 | — | — | — | 14.2 | 26.3 | — | — | — | — |
| `photo-g16-adam7` | 3689 | — | — | — | 18.6 | — | — | — | — | — |
| `photo-rgb8-adam7` | 3830 | — | — | — | 27.9 | 41.3 | — | — | — | — |
| `photo-rgb16-adam7` | 11055 | — | — | — | 48.3 | — | — | — | — | — |
| `photo-p1-adam7` | 130 | — | — | — | 9.230 | 16.9 | — | — | — | — |
| `photo-p2-adam7` | 289 | — | — | — | 10.6 | 18.6 | — | — | — | — |
| `photo-p4-adam7` | 620 | — | — | — | 12.6 | 20.8 | — | — | — | — |
| `photo-p8-adam7` | 1499 | — | — | — | 14.2 | 23.7 | — | — | — | — |
| `photo-p8a-adam7` | 1174 | — | — | — | 19.4 | 22.2 | — | — | — | — |
| `photo-ga8-adam7` | 1998 | — | — | — | 17.6 | 30.0 | — | — | — | — |
| `photo-ga16-adam7` | 6382 | — | — | — | 31.5 | — | — | — | — | — |
| `photo-rgba8-adam7` | 4414 | — | — | — | 29.3 | 46.2 | — | — | — | — |
| `photo-rgba16-adam7` | 14072 | — | — | — | 101.5 | — | — | — | — | — |
| `screen-g1` | 20 | 1.580 | 0.917 | 5.772 | 3.550 | 3.813 | 1312 | 0.45× | 0.41× | 0.27× |
| `screen-g2` | 36 | 1.290 | 1.048 | 5.686 | 3.582 | 4.634 | 1608 | 0.36× | 0.28× | 0.23× |
| `screen-g4` | 45 | 1.523 | 1.271 | 5.769 | 3.700 | 5.535 | 1361 | 0.41× | 0.28× | 0.26× |
| `screen-g8` | 59 | 2.767 | 2.698 | 5.338 | 3.769 | 6.561 | 749 | 0.73× | 0.42× | 0.52× |
| `screen-g16` | 83 | 4.945 | 5.075 | 7.758 | 5.452 | — | 419 | 0.91× | — | 0.64× |
| `screen-rgb8` | 81 | 6.313 | 6.328 | 10.4 | 3.713 | 13.0 | 328 | 1.70× | 0.49× | 0.61× |
| `screen-rgb16` | 97 | 11.5 | 12.1 | 16.3 | 19.6 | — | 181 | 0.59× | — | 0.71× |
| `screen-p1` | 47 | 1.826 | 1.167 | 8.206 | 7.293 | 4.990 | 1136 | 0.25× | 0.37× | 0.22× |
| `screen-p2` | 67 | 1.546 | 1.278 | 8.190 | 7.291 | 5.415 | 1341 | 0.21× | 0.29× | 0.19× |
| `screen-p4` | 66 | 1.664 | 1.415 | 8.208 | 7.340 | 5.917 | 1246 | 0.23× | 0.28× | 0.20× |
| `screen-p8` | 84 | 1.479 | 0.953 | 6.125 | 6.385 | 5.883 | 1402 | 0.23× | 0.25× | 0.24× |
| `screen-p8a` | 84 | 1.489 | 0.904 | 6.973 | 12.0 | 5.033 | 1393 | 0.12× | 0.30× | 0.21× |
| `screen-ga8` | 71 | 4.491 | 4.610 | 8.565 | 4.129 | 9.453 | 462 | 1.09× | 0.48× | 0.52× |
| `screen-ga16` | 101 | 8.994 | 9.442 | 13.5 | 5.914 | — | 231 | 1.52× | — | 0.67× |
| `screen-rgba8` | 85 | 7.947 | 8.386 | 12.9 | 2.720 | 16.0 | 261 | 2.92× | 0.50× | 0.62× |
| `screen-rgba16` | 106 | 15.1 | 15.7 | 20.9 | 13.9 | — | 137 | 1.09× | — | 0.73× |
| `screen-g1-adam7` | 23 | — | — | — | 4.709 | 15.0 | — | — | — | — |
| `screen-g2-adam7` | 39 | — | — | — | 4.790 | 15.8 | — | — | — | — |
| `screen-g4-adam7` | 51 | — | — | — | 5.009 | 16.6 | — | — | — | — |
| `screen-g8-adam7` | 67 | — | — | — | 4.243 | 17.6 | — | — | — | — |
| `screen-g16-adam7` | 92 | — | — | — | 4.948 | — | — | — | — | — |
| `screen-rgb8-adam7` | 92 | — | — | — | 5.847 | 23.7 | — | — | — | — |
| `screen-rgb16-adam7` | 113 | — | — | — | 12.0 | — | — | — | — | — |
| `screen-p1-adam7` | 50 | — | — | — | 8.427 | 16.2 | — | — | — | — |
| `screen-p2-adam7` | 72 | — | — | — | 8.541 | 16.6 | — | — | — | — |
| `screen-p4-adam7` | 72 | — | — | — | 8.624 | 17.1 | — | — | — | — |
| `screen-p8-adam7` | 99 | — | — | — | 6.690 | 16.8 | — | — | — | — |
| `screen-p8a-adam7` | 100 | — | — | — | 12.4 | 16.0 | — | — | — | — |
| `screen-ga8-adam7` | 82 | — | — | — | 4.763 | 20.3 | — | — | — | — |
| `screen-ga16-adam7` | 115 | — | — | — | 5.447 | — | — | — | — | — |
| `screen-rgba8-adam7` | 99 | — | — | — | 4.403 | 26.7 | — | — | — | — |
| `screen-rgba16-adam7` | 124 | — | — | — | 15.5 | — | — | — | — | — |

Geometric means of current ÷ competitor over the cases both decode:

- vs ImageSharp, photo: 0.85× (16 cases)
- vs ImageSharp, screen: 0.55× (16 cases)
- vs libpng, photo: 0.74× (12 cases)
- vs libpng, screen: 0.35× (12 cases)
- vs baseline, photo: 0.63× (16 cases)
- vs baseline, screen: 0.38× (16 cases)

Notes:

- `photo-g16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgb16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-ga16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgba16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-g1-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-g1-adam7`: baseline: unsupported
- `photo-g2-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-g2-adam7`: baseline: unsupported
- `photo-g4-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-g4-adam7`: baseline: unsupported
- `photo-g8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-g8-adam7`: baseline: unsupported
- `photo-g16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-g16-adam7`: baseline: unsupported
- `photo-g16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgb8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-rgb8-adam7`: baseline: unsupported
- `photo-rgb16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-rgb16-adam7`: baseline: unsupported
- `photo-rgb16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-p1-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-p1-adam7`: baseline: unsupported
- `photo-p2-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-p2-adam7`: baseline: unsupported
- `photo-p4-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-p4-adam7`: baseline: unsupported
- `photo-p8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-p8-adam7`: baseline: unsupported
- `photo-p8a-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-p8a-adam7`: baseline: unsupported
- `photo-ga8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-ga8-adam7`: baseline: unsupported
- `photo-ga16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-ga16-adam7`: baseline: unsupported
- `photo-ga16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgba8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-rgba8-adam7`: baseline: unsupported
- `photo-rgba16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-rgba16-adam7`: baseline: unsupported
- `photo-rgba16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-g16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgb16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-ga16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgba16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-g1-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-g1-adam7`: baseline: unsupported
- `screen-g2-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-g2-adam7`: baseline: unsupported
- `screen-g4-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-g4-adam7`: baseline: unsupported
- `screen-g8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-g8-adam7`: baseline: unsupported
- `screen-g16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-g16-adam7`: baseline: unsupported
- `screen-g16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgb8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-rgb8-adam7`: baseline: unsupported
- `screen-rgb16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-rgb16-adam7`: baseline: unsupported
- `screen-rgb16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-p1-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-p1-adam7`: baseline: unsupported
- `screen-p2-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-p2-adam7`: baseline: unsupported
- `screen-p4-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-p4-adam7`: baseline: unsupported
- `screen-p8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-p8-adam7`: baseline: unsupported
- `screen-p8a-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-p8a-adam7`: baseline: unsupported
- `screen-ga8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-ga8-adam7`: baseline: unsupported
- `screen-ga16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-ga16-adam7`: baseline: unsupported
- `screen-ga16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgba8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-rgba8-adam7`: baseline: unsupported
- `screen-rgba16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-rgba16-adam7`: baseline: unsupported
- `screen-rgba16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed

### Encode (in-process medians of 7 interleaved rounds, ms; sizes in KB)

`current` = `PngWriter.Encode*` with `PngWriteOptions.CompressionLevel`, one thread; ImageSharp = `PngEncoder` at the same zlib level (store 0, fastest 1, optimal 6, smallest 9), adaptive filters, one thread; libpng = simplified API `png_image_write_to_memory`, which has no level setting, so it appears on the optimal rows (its default). Every output is decoded and must reproduce the samples exactly.

| Case | current | KB | baseline | ImageSharp | KB | libpng | KB | time ÷ ImageSharp | size ÷ ImageSharp | time ÷ libpng | size ÷ libpng | time ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 4.355 | 2026 | 28.2 | 3.793 | 2027 | — | — | 1.15× | 1.000 | — | — | 0.15× |
| `photo-gray8-fastest` | 41.3 | 1899 | 40.7 | 38.2 | 1336 | — | — | 1.08× | 1.421 | — | — | 1.01× |
| `photo-gray8-optimal` | 59.5 | 1253 | 58.9 | 74.6 | 1297 | 104.6 | 1201 | 0.80× | 0.966 | 0.57× | 1.043 | 1.01× |
| `photo-gray8-smallest` | 86.8 | 1318 | 86.2 | 74.9 | 1297 | — | — | 1.16× | 1.016 | — | — | 1.01× |
| `photo-gray16-store` | 8.452 | 4052 | 56.0 | 7.620 | 4052 | — | — | 1.11× | 1.000 | — | — | 0.15× |
| `photo-gray16-fastest` | 88.9 | 4274 | 87.8 | 108.3 | 3618 | — | — | 0.82× | 1.181 | — | — | 1.01× |
| `photo-gray16-optimal` | 140.6 | 3577 | 140.1 | 115.2 | 3588 | 137.7 | 3581 | 1.22× | 0.997 | 1.02× | 0.999 | 1.00× |
| `photo-gray16-smallest` | 141.4 | 3616 | 140.7 | 115.6 | 3588 | — | — | 1.22× | 1.008 | — | — | 1.00× |
| `photo-rgb8-store` | 13.3 | 6077 | 65.6 | 10.3 | 6078 | — | — | 1.28× | 1.000 | — | — | 0.20× |
| `photo-rgb8-fastest` | 97.2 | 4860 | 96.2 | 99.7 | 3414 | — | — | 0.98× | 1.423 | — | — | 1.01× |
| `photo-rgb8-optimal` | 154.0 | 3304 | 152.2 | 398.2 | 3309 | 517.7 | 3404 | 0.39× | 0.998 | 0.30× | 0.970 | 1.01× |
| `photo-rgb8-smallest` | 366.9 | 3273 | 365.6 | 530.8 | 3299 | — | — | 0.69× | 0.992 | — | — | 1.00× |
| `photo-rgb16-store` | 26.6 | 12153 | 156.8 | 24.0 | 12155 | — | — | 1.11× | 1.000 | — | — | 0.17× |
| `photo-rgb16-fastest` | 256.0 | 12803 | 251.8 | 324.9 | 10752 | — | — | 0.79× | 1.191 | — | — | 1.02× |
| `photo-rgb16-optimal` | 414.7 | 10752 | 413.3 | 365.9 | 10762 | 428.0 | 10767 | 1.13× | 0.999 | 0.97× | 0.999 | 1.00× |
| `photo-rgb16-smallest` | 427.6 | 10735 | 425.6 | 368.1 | 10762 | — | — | 1.16× | 0.997 | — | — | 1.00× |
| `photo-rgba8-store` | 17.8 | 8102 | 85.2 | 13.3 | 8104 | — | — | 1.34× | 1.000 | — | — | 0.21× |
| `photo-rgba8-fastest` | 113.9 | 5643 | 113.5 | 122.7 | 4051 | — | — | 0.93× | 1.393 | — | — | 1.00× |
| `photo-rgba8-optimal` | 235.2 | 3805 | 232.7 | 616.9 | 3809 | 740.1 | 3911 | 0.38× | 0.999 | 0.32× | 0.973 | 1.01× |
| `photo-rgba8-smallest` | 592.6 | 3787 | 591.7 | 1044.1 | 3788 | — | — | 0.57× | 1.000 | — | — | 1.00× |
| `photo-rgba16-store` | 34.9 | 16204 | 206.0 | 30.4 | 16206 | — | — | 1.15× | 1.000 | — | — | 0.17× |
| `photo-rgba16-fastest` | 337.9 | 16819 | 334.7 | 421.5 | 13699 | — | — | 0.80× | 1.228 | — | — | 1.01× |
| `photo-rgba16-optimal` | 546.4 | 13676 | 545.4 | 608.4 | 13672 | — | — | 0.90× | 1.000 | — | — | 1.00× |
| `photo-rgba16-smallest` | 646.2 | 13680 | 640.1 | 609.4 | 13672 | — | — | 1.06× | 1.001 | — | — | 1.01× |
| `screen-gray8-store` | 4.296 | 2026 | 15.6 | 3.098 | 2027 | — | — | 1.39× | 1.000 | — | — | 0.27× |
| `screen-gray8-fastest` | 12.7 | 115 | 12.7 | 5.573 | 83 | — | — | 2.29× | 1.388 | — | — | 1.01× |
| `screen-gray8-optimal` | 15.9 | 60 | 15.7 | 21.2 | 59 | 28.3 | 59 | 0.75× | 1.019 | 0.56× | 1.025 | 1.02× |
| `screen-gray8-smallest` | 46.0 | 57 | 45.8 | 121.3 | 57 | — | — | 0.38× | 0.999 | — | — | 1.01× |
| `screen-gray16-store` | 8.390 | 4052 | 30.7 | 7.091 | 4052 | — | — | 1.18× | 1.000 | — | — | 0.27× |
| `screen-gray16-fastest` | 24.6 | 195 | 24.8 | 10.8 | 130 | — | — | 2.29× | 1.493 | — | — | 0.99× |
| `screen-gray16-optimal` | 30.7 | 83 | 30.9 | 32.5 | 83 | 45.5 | 84 | 0.94× | 0.998 | 0.68× | 0.987 | 0.99× |
| `screen-gray16-smallest` | 77.7 | 82 | 78.1 | 149.3 | 81 | — | — | 0.52× | 1.005 | — | — | 1.00× |
| `screen-rgb8-store` | 12.8 | 6077 | 47.5 | 9.646 | 6078 | — | — | 1.32× | 1.000 | — | — | 0.27× |
| `screen-rgb8-fastest` | 36.0 | 186 | 36.3 | 13.1 | 133 | — | — | 2.75× | 1.398 | — | — | 0.99× |
| `screen-rgb8-optimal` | 41.6 | 78 | 41.7 | 40.0 | 81 | 60.4 | 82 | 1.04× | 0.965 | 0.69× | 0.955 | 1.00× |
| `screen-rgb8-smallest` | 84.5 | 76 | 84.1 | 185.7 | 77 | — | — | 0.46× | 0.982 | — | — | 1.01× |
| `screen-rgb16-store` | 26.0 | 12153 | 90.3 | 24.4 | 12155 | — | — | 1.07× | 1.000 | — | — | 0.29× |
| `screen-rgb16-fastest` | 73.3 | 298 | 71.8 | 26.8 | 176 | — | — | 2.74× | 1.696 | — | — | 1.02× |
| `screen-rgb16-optimal` | 83.0 | 96 | 79.3 | 62.0 | 97 | 104.7 | 100 | 1.34× | 0.984 | 0.79× | 0.961 | 1.05× |
| `screen-rgb16-smallest` | 136.3 | 90 | 132.6 | 259.1 | 91 | — | — | 0.53× | 0.988 | — | — | 1.03× |
| `screen-rgba8-store` | 16.7 | 8102 | 60.5 | 12.5 | 8104 | — | — | 1.33× | 1.000 | — | — | 0.28× |
| `screen-rgba8-fastest` | 47.5 | 250 | 47.4 | 16.4 | 151 | — | — | 2.90× | 1.657 | — | — | 1.00× |
| `screen-rgba8-optimal` | 55.2 | 85 | 55.2 | 47.4 | 85 | 75.3 | 87 | 1.17× | 0.998 | 0.73× | 0.980 | 1.00× |
| `screen-rgba8-smallest` | 98.3 | 82 | 98.0 | 234.5 | 83 | — | — | 0.42× | 0.989 | — | — | 1.00× |
| `screen-rgba16-store` | 34.8 | 16204 | 124.1 | 31.6 | 16206 | — | — | 1.10× | 1.000 | — | — | 0.28× |
| `screen-rgba16-fastest` | 94.7 | 341 | 98.8 | 34.5 | 214 | — | — | 2.75× | 1.596 | — | — | 0.96× |
| `screen-rgba16-optimal` | 105.0 | 103 | 106.8 | 76.2 | 106 | — | — | 1.38× | 0.980 | — | — | 0.98× |
| `screen-rgba16-smallest` | 183.3 | 99 | 182.9 | 293.2 | 99 | — | — | 0.63× | 0.996 | — | — | 1.00× |

Geometric means of current ÷ ImageSharp per compression level (time / size):

- store: time 1.21×, size 1.000×
- fastest: time 1.53×, size 1.413×
- optimal: time 0.89×, size 0.992×
- smallest: time 0.67×, size 0.998×

Opt-in parallel deflate (`ParallelFragments = 16`), not part of the single-thread comparison above:

| Case | current (1 thread) | parallel | speed-up | KB | size ÷ 1 thread |
|---|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 4.355 | 5.553 | 0.78× | 2026 | 1.000 |
| `photo-gray8-fastest` | 41.3 | 12.9 | 3.21× | 1903 | 1.002 |
| `photo-gray8-optimal` | 59.5 | 15.4 | 3.86× | 1252 | 1.000 |
| `photo-gray8-smallest` | 86.8 | 23.2 | 3.74× | 1319 | 1.001 |
| `photo-gray16-store` | 8.452 | 10.1 | 0.84× | 4052 | 1.000 |
| `photo-gray16-fastest` | 88.9 | 18.6 | 4.78× | 4274 | 1.000 |
| `photo-gray16-optimal` | 140.6 | 20.7 | 6.79× | 3577 | 1.000 |
| `photo-gray16-smallest` | 141.4 | 22.6 | 6.25× | 3616 | 1.000 |
| `photo-rgb8-store` | 13.3 | 15.7 | 0.85× | 6077 | 1.000 |
| `photo-rgb8-fastest` | 97.2 | 20.2 | 4.81× | 4871 | 1.002 |
| `photo-rgb8-optimal` | 154.0 | 22.6 | 6.82× | 3315 | 1.003 |
| `photo-rgb8-smallest` | 366.9 | 49.0 | 7.49× | 3286 | 1.004 |
| `photo-rgb16-store` | 26.6 | 28.6 | 0.93× | 12153 | 1.000 |
| `photo-rgb16-fastest` | 256.0 | 50.7 | 5.05× | 12804 | 1.000 |
| `photo-rgb16-optimal` | 414.7 | 59.2 | 7.00× | 10753 | 1.000 |
| `photo-rgb16-smallest` | 427.6 | 64.7 | 6.61× | 10737 | 1.000 |
| `photo-rgba8-store` | 17.8 | 23.4 | 0.76× | 8102 | 1.000 |
| `photo-rgba8-fastest` | 113.9 | 24.1 | 4.73× | 5652 | 1.002 |
| `photo-rgba8-optimal` | 235.2 | 32.4 | 7.27× | 3818 | 1.003 |
| `photo-rgba8-smallest` | 592.6 | 73.3 | 8.09× | 3801 | 1.004 |
| `photo-rgba16-store` | 34.9 | 37.8 | 0.92× | 16204 | 1.000 |
| `photo-rgba16-fastest` | 337.9 | 66.2 | 5.10× | 16822 | 1.000 |
| `photo-rgba16-optimal` | 546.4 | 77.6 | 7.05× | 13676 | 1.000 |
| `photo-rgba16-smallest` | 646.2 | 95.0 | 6.80× | 13683 | 1.000 |
| `screen-gray8-store` | 4.296 | 5.317 | 0.81× | 2026 | 1.000 |
| `screen-gray8-fastest` | 12.7 | 2.773 | 4.60× | 115 | 1.001 |
| `screen-gray8-optimal` | 15.9 | 3.461 | 4.60× | 60 | 0.995 |
| `screen-gray8-smallest` | 46.0 | 11.8 | 3.90× | 57 | 1.002 |
| `screen-gray16-store` | 8.390 | 9.722 | 0.86× | 4052 | 1.000 |
| `screen-gray16-fastest` | 24.6 | 4.369 | 5.64× | 195 | 1.002 |
| `screen-gray16-optimal` | 30.7 | 5.095 | 6.03× | 83 | 0.998 |
| `screen-gray16-smallest` | 77.7 | 11.5 | 6.75× | 82 | 1.001 |
| `screen-rgb8-store` | 12.8 | 15.4 | 0.83× | 6077 | 1.000 |
| `screen-rgb8-fastest` | 36.0 | 5.794 | 6.21× | 186 | 1.001 |
| `screen-rgb8-optimal` | 41.6 | 6.324 | 6.59× | 78 | 1.002 |
| `screen-rgb8-smallest` | 84.5 | 10.7 | 7.89× | 76 | 1.003 |
| `screen-rgb16-store` | 26.0 | 29.0 | 0.90× | 12153 | 1.000 |
| `screen-rgb16-fastest` | 73.3 | 11.3 | 6.51× | 298 | 1.000 |
| `screen-rgb16-optimal` | 83.0 | 11.9 | 6.97× | 96 | 1.004 |
| `screen-rgb16-smallest` | 136.3 | 17.3 | 7.86× | 90 | 1.005 |
| `screen-rgba8-store` | 16.7 | 19.8 | 0.84× | 8102 | 1.000 |
| `screen-rgba8-fastest` | 47.5 | 7.729 | 6.15× | 250 | 0.999 |
| `screen-rgba8-optimal` | 55.2 | 8.313 | 6.64× | 86 | 1.004 |
| `screen-rgba8-smallest` | 98.3 | 12.8 | 7.69× | 83 | 1.007 |
| `screen-rgba16-store` | 34.8 | 37.5 | 0.93× | 16204 | 1.000 |
| `screen-rgba16-fastest` | 94.7 | 14.8 | 6.39× | 341 | 1.000 |
| `screen-rgba16-optimal` | 105.0 | 15.5 | 6.79× | 104 | 1.003 |
| `screen-rgba16-smallest` | 183.3 | 23.6 | 7.76× | 99 | 1.005 |

Notes:

- `photo-rgba16-optimal`: LibPng: output does not round-trip (samples differ), not timed
- `screen-rgba16-optimal`: LibPng: output does not round-trip (samples differ), not timed
