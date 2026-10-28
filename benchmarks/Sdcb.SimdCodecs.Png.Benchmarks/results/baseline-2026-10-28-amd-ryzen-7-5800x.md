# PNG baseline — 2026-10-28, AMD Ryzen 7 5800X

First recording of the full matrix, at `4f96c28` (the 0.1.0 packages) plus this benchmark project.
Command: `LIBPNG_PATH=…/vcpkg/installed/x64-windows/bin/libpng16.dll dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Png.Benchmarks -- matrix all`
(in-process interleaved medians, not BenchmarkDotNet).

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
- Baseline: (none built)

### Decode to 8-bit RGBA (in-process medians of 7 interleaved rounds, ms)

`current` = `PngImageDecoder.TryDecodeIntoRgba8` into a caller buffer; `native` = `PngReader.Decode` (samples as stored, no RGBA expansion); ImageSharp = `Image.Load<Rgba32>` + `CopyPixelDataTo`, one thread; libpng = simplified API `png_image_finish_read` to RGBA. Ratios below 1 mean we are faster. Inputs are encoded by ImageSharp (adaptive filters, zlib level 6).

| Case | KB | current | native | baseline | ImageSharp | libpng | current MP/s | ÷ ImageSharp | ÷ libpng | ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-g1` | 28 | 6.564 | 3.257 | — | 3.417 | 4.076 | 316 | 1.92× | 1.61× | — |
| `photo-g2` | 167 | 7.237 | 4.693 | — | 4.609 | 6.010 | 287 | 1.57× | 1.20× | — |
| `photo-g4` | 427 | 9.227 | 6.633 | — | 6.215 | 8.049 | 225 | 1.48× | 1.15× | — |
| `photo-g8` | 1297 | 19.1 | 16.4 | — | 14.2 | 15.0 | 109 | 1.35× | 1.27× | — |
| `photo-g16` | 3588 | 26.3 | 23.6 | — | 17.8 | — | 79 | 1.48× | — | — |
| `photo-rgb8` | 3309 | 37.9 | 34.0 | — | 26.4 | 29.4 | 55 | 1.44× | 1.29× | — |
| `photo-rgb16` | 10762 | 86.1 | 82.6 | — | 64.0 | — | 24 | 1.34× | — | — |
| `photo-p1` | 123 | 9.050 | 3.870 | — | 8.048 | 5.667 | 229 | 1.12× | 1.60× | — |
| `photo-p2` | 268 | 10.7 | 5.284 | — | 9.445 | 7.317 | 194 | 1.13× | 1.46× | — |
| `photo-p4` | 567 | 12.5 | 7.224 | — | 10.8 | 9.106 | 166 | 1.15× | 1.37× | — |
| `photo-p8` | 1406 | 15.1 | 9.819 | — | 13.2 | 12.1 | 138 | 1.14× | 1.25× | — |
| `photo-p8a` | 1051 | 14.4 | 8.323 | — | 18.3 | 10.3 | 144 | 0.78× | 1.40× | — |
| `photo-ga8` | 1772 | 26.3 | 22.4 | — | 18.4 | 18.9 | 79 | 1.43× | 1.39× | — |
| `photo-ga16` | 6121 | 65.0 | 61.1 | — | 30.1 | — | 32 | 2.16× | — | — |
| `photo-rgba8` | 3809 | 43.7 | 39.4 | — | 24.6 | 33.9 | 47 | 1.78× | 1.29× | — |
| `photo-rgba16` | 13672 | 147.4 | 143.2 | — | 97.9 | — | 14 | 1.51× | — | — |
| `photo-g1-adam7` | 35 | — | — | — | 4.881 | 15.2 | — | — | — | — |
| `photo-g2-adam7` | 197 | — | — | — | 6.396 | 17.3 | — | — | — | — |
| `photo-g4-adam7` | 490 | — | — | — | 8.380 | 19.8 | — | — | — | — |
| `photo-g8-adam7` | 1418 | — | — | — | 14.1 | 26.2 | — | — | — | — |
| `photo-g16-adam7` | 3689 | — | — | — | 18.4 | — | — | — | — | — |
| `photo-rgb8-adam7` | 3830 | — | — | — | 28.0 | 41.3 | — | — | — | — |
| `photo-rgb16-adam7` | 11055 | — | — | — | 48.0 | — | — | — | — | — |
| `photo-p1-adam7` | 130 | — | — | — | 9.339 | 16.9 | — | — | — | — |
| `photo-p2-adam7` | 289 | — | — | — | 10.7 | 18.5 | — | — | — | — |
| `photo-p4-adam7` | 620 | — | — | — | 12.6 | 20.7 | — | — | — | — |
| `photo-p8-adam7` | 1499 | — | — | — | 14.3 | 23.6 | — | — | — | — |
| `photo-p8a-adam7` | 1174 | — | — | — | 19.8 | 22.2 | — | — | — | — |
| `photo-ga8-adam7` | 1998 | — | — | — | 17.7 | 30.0 | — | — | — | — |
| `photo-ga16-adam7` | 6382 | — | — | — | 31.2 | — | — | — | — | — |
| `photo-rgba8-adam7` | 4414 | — | — | — | 29.1 | 46.2 | — | — | — | — |
| `photo-rgba16-adam7` | 14072 | — | — | — | 100.8 | — | — | — | — | — |
| `screen-g1` | 20 | 5.710 | 3.063 | — | 3.240 | 3.863 | 363 | 1.76× | 1.48× | — |
| `screen-g2` | 36 | 5.957 | 3.284 | — | 3.276 | 4.702 | 348 | 1.82× | 1.27× | — |
| `screen-g4` | 45 | 5.926 | 3.336 | — | 3.428 | 5.505 | 350 | 1.73× | 1.08× | — |
| `screen-g8` | 59 | 5.362 | 2.766 | — | 3.400 | 6.525 | 387 | 1.58× | 0.82× | — |
| `screen-g16` | 83 | 7.698 | 5.110 | — | 5.227 | — | 269 | 1.47× | — | — |
| `screen-rgb8` | 81 | 10.3 | 6.377 | — | 3.687 | 13.0 | 201 | 2.80× | 0.79× | — |
| `screen-rgb16` | 97 | 16.2 | 12.3 | — | 17.2 | — | 128 | 0.94× | — | — |
| `screen-p1` | 47 | 8.198 | 2.954 | — | 7.301 | 4.965 | 253 | 1.12× | 1.65× | — |
| `screen-p2` | 67 | 8.310 | 3.128 | — | 7.292 | 5.335 | 250 | 1.14× | 1.56× | — |
| `screen-p4` | 66 | 8.182 | 2.981 | — | 7.447 | 5.908 | 253 | 1.10× | 1.38× | — |
| `screen-p8` | 84 | 6.067 | 0.873 | — | 6.363 | 5.886 | 342 | 0.95× | 1.03× | — |
| `screen-p8a` | 84 | 6.932 | 0.881 | — | 12.4 | 5.071 | 299 | 0.56× | 1.37× | — |
| `screen-ga8` | 71 | 8.527 | 4.617 | — | 4.140 | 9.426 | 243 | 2.06× | 0.90× | — |
| `screen-ga16` | 101 | 13.3 | 9.217 | — | 5.767 | — | 156 | 2.31× | — | — |
| `screen-rgba8` | 85 | 12.6 | 8.311 | — | 2.700 | 15.9 | 164 | 4.68× | 0.79× | — |
| `screen-rgba16` | 106 | 20.2 | 15.7 | — | 14.0 | — | 103 | 1.45× | — | — |
| `screen-g1-adam7` | 23 | — | — | — | 4.677 | 15.0 | — | — | — | — |
| `screen-g2-adam7` | 39 | — | — | — | 4.711 | 15.8 | — | — | — | — |
| `screen-g4-adam7` | 51 | — | — | — | 4.843 | 16.6 | — | — | — | — |
| `screen-g8-adam7` | 67 | — | — | — | 4.134 | 17.5 | — | — | — | — |
| `screen-g16-adam7` | 92 | — | — | — | 4.960 | — | — | — | — | — |
| `screen-rgb8-adam7` | 92 | — | — | — | 5.816 | 23.6 | — | — | — | — |
| `screen-rgb16-adam7` | 113 | — | — | — | 12.0 | — | — | — | — | — |
| `screen-p1-adam7` | 50 | — | — | — | 8.511 | 16.1 | — | — | — | — |
| `screen-p2-adam7` | 72 | — | — | — | 8.597 | 16.6 | — | — | — | — |
| `screen-p4-adam7` | 72 | — | — | — | 8.593 | 17.0 | — | — | — | — |
| `screen-p8-adam7` | 99 | — | — | — | 6.709 | 16.8 | — | — | — | — |
| `screen-p8a-adam7` | 100 | — | — | — | 12.7 | 16.0 | — | — | — | — |
| `screen-ga8-adam7` | 82 | — | — | — | 4.779 | 20.3 | — | — | — | — |
| `screen-ga16-adam7` | 115 | — | — | — | 5.388 | — | — | — | — | — |
| `screen-rgba8-adam7` | 99 | — | — | — | 4.413 | 26.6 | — | — | — | — |
| `screen-rgba16-adam7` | 124 | — | — | — | 15.5 | — | — | — | — | — |

Geometric means of current ÷ competitor over the cases both decode:

- vs ImageSharp, photo: 1.39× (16 cases)
- vs ImageSharp, screen: 1.52× (16 cases)
- vs libpng, photo: 1.35× (12 cases)
- vs libpng, screen: 1.14× (12 cases)

Notes:

- `photo-g16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgb16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-ga16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgba16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-g1-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-g2-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-g4-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-g8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-g16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-g16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgb8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-rgb16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-rgb16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-p1-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-p2-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-p4-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-p8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-p8a-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-ga8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-ga16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-ga16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgba8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-rgba16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-rgba16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-g16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgb16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-ga16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgba16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-g1-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-g2-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-g4-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-g8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-g16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-g16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgb8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-rgb16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-rgb16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-p1-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-p2-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-p4-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-p8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-p8a-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-ga8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-ga16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-ga16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgba8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-rgba16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-rgba16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed

### Encode (in-process medians of 7 interleaved rounds, ms; sizes in KB)

`current` = `PngWriter.Encode*` with `PngWriteOptions.CompressionLevel`, one thread; ImageSharp = `PngEncoder` at the same zlib level (store 0, fastest 1, optimal 6, smallest 9), adaptive filters, one thread; libpng = simplified API `png_image_write_to_memory`, which has no level setting, so it appears on the optimal rows (its default). Every output is decoded and must reproduce the samples exactly.

| Case | current | KB | baseline | ImageSharp | KB | libpng | KB | time ÷ ImageSharp | size ÷ ImageSharp | time ÷ libpng | size ÷ libpng | time ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 28.2 | 2026 | — | 3.756 | 2027 | — | — | 7.51× | 1.000 | — | — | — |
| `photo-gray8-fastest` | 41.9 | 1899 | — | 39.0 | 1336 | — | — | 1.07× | 1.421 | — | — | — |
| `photo-gray8-optimal` | 59.4 | 1253 | — | 75.2 | 1297 | 105.1 | 1201 | 0.79× | 0.966 | 0.57× | 1.043 | — |
| `photo-gray8-smallest` | 87.1 | 1318 | — | 75.1 | 1297 | — | — | 1.16× | 1.016 | — | — | — |
| `photo-gray16-store` | 56.8 | 4052 | — | 8.250 | 4052 | — | — | 6.89× | 1.000 | — | — | — |
| `photo-gray16-fastest` | 89.4 | 4274 | — | 108.8 | 3618 | — | — | 0.82× | 1.181 | — | — | — |
| `photo-gray16-optimal` | 141.0 | 3577 | — | 117.0 | 3588 | 136.6 | 3581 | 1.21× | 0.997 | 1.03× | 0.999 | — |
| `photo-gray16-smallest` | 141.8 | 3616 | — | 116.8 | 3588 | — | — | 1.21× | 1.008 | — | — | — |
| `photo-rgb8-store` | 66.8 | 6077 | — | 10.9 | 6078 | — | — | 6.14× | 1.000 | — | — | — |
| `photo-rgb8-fastest` | 96.3 | 4860 | — | 99.9 | 3414 | — | — | 0.96× | 1.423 | — | — | — |
| `photo-rgb8-optimal` | 152.5 | 3304 | — | 396.7 | 3309 | 523.0 | 3404 | 0.38× | 0.998 | 0.29× | 0.970 | — |
| `photo-rgb8-smallest` | 365.3 | 3273 | — | 528.4 | 3299 | — | — | 0.69× | 0.992 | — | — | — |
| `photo-rgb16-store` | 158.1 | 12153 | — | 25.4 | 12155 | — | — | 6.23× | 1.000 | — | — | — |
| `photo-rgb16-fastest` | 253.4 | 12803 | — | 321.7 | 10752 | — | — | 0.79× | 1.191 | — | — | — |
| `photo-rgb16-optimal` | 413.5 | 10752 | — | 375.0 | 10762 | 425.3 | 10767 | 1.10× | 0.999 | 0.97× | 0.999 | — |
| `photo-rgb16-smallest` | 426.9 | 10735 | — | 369.0 | 10762 | — | — | 1.16× | 0.997 | — | — | — |
| `photo-rgba8-store` | 83.7 | 8102 | — | 13.7 | 8104 | — | — | 6.09× | 1.000 | — | — | — |
| `photo-rgba8-fastest` | 113.3 | 5643 | — | 123.3 | 4051 | — | — | 0.92× | 1.393 | — | — | — |
| `photo-rgba8-optimal` | 234.5 | 3805 | — | 614.1 | 3809 | 739.3 | 3911 | 0.38× | 0.999 | 0.32× | 0.973 | — |
| `photo-rgba8-smallest` | 591.2 | 3787 | — | 1039.5 | 3788 | — | — | 0.57× | 1.000 | — | — | — |
| `photo-rgba16-store` | 206.5 | 16204 | — | 30.0 | 16206 | — | — | 6.87× | 1.000 | — | — | — |
| `photo-rgba16-fastest` | 336.2 | 16819 | — | 420.4 | 13699 | — | — | 0.80× | 1.228 | — | — | — |
| `photo-rgba16-optimal` | 541.7 | 13676 | — | 610.7 | 13672 | — | — | 0.89× | 1.000 | — | — | — |
| `photo-rgba16-smallest` | 639.2 | 13680 | — | 610.4 | 13672 | — | — | 1.05× | 1.001 | — | — | — |
| `screen-gray8-store` | 15.5 | 2026 | — | 3.148 | 2027 | — | — | 4.93× | 1.000 | — | — | — |
| `screen-gray8-fastest` | 12.3 | 115 | — | 5.588 | 83 | — | — | 2.21× | 1.388 | — | — | — |
| `screen-gray8-optimal` | 15.6 | 60 | — | 21.2 | 59 | 28.3 | 59 | 0.73× | 1.019 | 0.55× | 1.025 | — |
| `screen-gray8-smallest` | 45.8 | 57 | — | 121.0 | 57 | — | — | 0.38× | 0.999 | — | — | — |
| `screen-gray16-store` | 30.6 | 4052 | — | 7.714 | 4052 | — | — | 3.97× | 1.000 | — | — | — |
| `screen-gray16-fastest` | 24.0 | 195 | — | 11.3 | 130 | — | — | 2.13× | 1.493 | — | — | — |
| `screen-gray16-optimal` | 29.8 | 83 | — | 32.7 | 83 | 45.5 | 84 | 0.91× | 0.998 | 0.65× | 0.987 | — |
| `screen-gray16-smallest` | 76.8 | 82 | — | 149.6 | 81 | — | — | 0.51× | 1.005 | — | — | — |
| `screen-rgb8-store` | 45.4 | 6077 | — | 10.5 | 6078 | — | — | 4.34× | 1.000 | — | — | — |
| `screen-rgb8-fastest` | 34.5 | 186 | — | 13.1 | 133 | — | — | 2.64× | 1.398 | — | — | — |
| `screen-rgb8-optimal` | 40.2 | 78 | — | 39.7 | 81 | 60.4 | 82 | 1.01× | 0.965 | 0.67× | 0.955 | — |
| `screen-rgb8-smallest` | 83.2 | 76 | — | 185.6 | 77 | — | — | 0.45× | 0.982 | — | — | — |
| `screen-rgb16-store` | 88.8 | 12153 | — | 25.8 | 12155 | — | — | 3.45× | 1.000 | — | — | — |
| `screen-rgb16-fastest` | 67.8 | 298 | — | 28.0 | 176 | — | — | 2.42× | 1.696 | — | — | — |
| `screen-rgb16-optimal` | 77.0 | 96 | — | 63.0 | 97 | 111.9 | 100 | 1.22× | 0.984 | 0.69× | 0.961 | — |
| `screen-rgb16-smallest` | 130.8 | 90 | — | 259.6 | 91 | — | — | 0.50× | 0.988 | — | — | — |
| `screen-rgba8-store` | 59.2 | 8102 | — | 12.7 | 8104 | — | — | 4.67× | 1.000 | — | — | — |
| `screen-rgba8-fastest` | 46.0 | 250 | — | 16.8 | 151 | — | — | 2.74× | 1.657 | — | — | — |
| `screen-rgba8-optimal` | 53.5 | 85 | — | 48.9 | 85 | 75.5 | 87 | 1.09× | 0.998 | 0.71× | 0.980 | — |
| `screen-rgba8-smallest` | 96.5 | 82 | — | 236.0 | 83 | — | — | 0.41× | 0.989 | — | — | — |
| `screen-rgba16-store` | 118.6 | 16204 | — | 31.5 | 16206 | — | — | 3.77× | 1.000 | — | — | — |
| `screen-rgba16-fastest` | 89.9 | 341 | — | 39.3 | 214 | — | — | 2.29× | 1.596 | — | — | — |
| `screen-rgba16-optimal` | 99.8 | 103 | — | 78.1 | 106 | — | — | 1.28× | 0.980 | — | — | — |
| `screen-rgba16-smallest` | 178.3 | 99 | — | 291.6 | 99 | — | — | 0.61× | 0.996 | — | — | — |

Geometric means of current ÷ ImageSharp per compression level (time / size):

- store: time 5.24×, size 1.000×
- fastest: time 1.46×, size 1.413×
- optimal: time 0.86×, size 0.992×
- smallest: time 0.66×, size 0.998×

Opt-in parallel deflate (`ParallelFragments = 16`), not part of the single-thread comparison above:

| Case | current (1 thread) | parallel | speed-up | KB | size ÷ 1 thread |
|---|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 28.2 | 10.7 | 2.65× | 2026 | 1.000 |
| `photo-gray8-fastest` | 41.9 | 13.3 | 3.16× | 1903 | 1.002 |
| `photo-gray8-optimal` | 59.4 | 15.6 | 3.82× | 1252 | 1.000 |
| `photo-gray8-smallest` | 87.1 | 23.2 | 3.75× | 1319 | 1.001 |
| `photo-gray16-store` | 56.8 | 14.6 | 3.89× | 4052 | 1.000 |
| `photo-gray16-fastest` | 89.4 | 18.2 | 4.90× | 4274 | 1.000 |
| `photo-gray16-optimal` | 141.0 | 21.3 | 6.61× | 3577 | 1.000 |
| `photo-gray16-smallest` | 141.8 | 22.9 | 6.18× | 3616 | 1.000 |
| `photo-rgb8-store` | 66.8 | 19.8 | 3.38× | 6077 | 1.000 |
| `photo-rgb8-fastest` | 96.3 | 20.1 | 4.79× | 4871 | 1.002 |
| `photo-rgb8-optimal` | 152.5 | 22.8 | 6.68× | 3315 | 1.003 |
| `photo-rgb8-smallest` | 365.3 | 48.6 | 7.52× | 3286 | 1.004 |
| `photo-rgb16-store` | 158.1 | 40.6 | 3.90× | 12153 | 1.000 |
| `photo-rgb16-fastest` | 253.4 | 51.5 | 4.92× | 12804 | 1.000 |
| `photo-rgb16-optimal` | 413.5 | 58.9 | 7.02× | 10753 | 1.000 |
| `photo-rgb16-smallest` | 426.9 | 64.2 | 6.65× | 10737 | 1.000 |
| `photo-rgba8-store` | 83.7 | 26.4 | 3.17× | 8102 | 1.000 |
| `photo-rgba8-fastest` | 113.3 | 24.1 | 4.70× | 5652 | 1.002 |
| `photo-rgba8-optimal` | 234.5 | 33.1 | 7.09× | 3818 | 1.003 |
| `photo-rgba8-smallest` | 591.2 | 71.9 | 8.22× | 3801 | 1.004 |
| `photo-rgba16-store` | 206.5 | 55.7 | 3.71× | 16204 | 1.000 |
| `photo-rgba16-fastest` | 336.2 | 66.8 | 5.03× | 16822 | 1.000 |
| `photo-rgba16-optimal` | 541.7 | 76.2 | 7.11× | 13676 | 1.000 |
| `photo-rgba16-smallest` | 639.2 | 95.2 | 6.72× | 13683 | 1.000 |
| `screen-gray8-store` | 15.5 | 8.111 | 1.91× | 2026 | 1.000 |
| `screen-gray8-fastest` | 12.3 | 4.279 | 2.88× | 115 | 1.001 |
| `screen-gray8-optimal` | 15.6 | 4.890 | 3.18× | 60 | 0.995 |
| `screen-gray8-smallest` | 45.8 | 11.8 | 3.88× | 57 | 1.002 |
| `screen-gray16-store` | 30.6 | 12.9 | 2.37× | 4052 | 1.000 |
| `screen-gray16-fastest` | 24.0 | 4.352 | 5.51× | 195 | 1.002 |
| `screen-gray16-optimal` | 29.8 | 4.971 | 5.99× | 83 | 0.998 |
| `screen-gray16-smallest` | 76.8 | 11.4 | 6.75× | 82 | 1.001 |
| `screen-rgb8-store` | 45.4 | 18.4 | 2.46× | 6077 | 1.000 |
| `screen-rgb8-fastest` | 34.5 | 5.696 | 6.05× | 186 | 1.001 |
| `screen-rgb8-optimal` | 40.2 | 6.471 | 6.21× | 78 | 1.002 |
| `screen-rgb8-smallest` | 83.2 | 10.6 | 7.84× | 76 | 1.003 |
| `screen-rgb16-store` | 88.8 | 36.0 | 2.47× | 12153 | 1.000 |
| `screen-rgb16-fastest` | 67.8 | 10.9 | 6.20× | 298 | 1.000 |
| `screen-rgb16-optimal` | 77.0 | 11.6 | 6.65× | 96 | 1.004 |
| `screen-rgb16-smallest` | 130.8 | 17.3 | 7.57× | 90 | 1.005 |
| `screen-rgba8-store` | 59.2 | 24.4 | 2.42× | 8102 | 1.000 |
| `screen-rgba8-fastest` | 46.0 | 7.523 | 6.11× | 250 | 0.999 |
| `screen-rgba8-optimal` | 53.5 | 8.199 | 6.52× | 86 | 1.004 |
| `screen-rgba8-smallest` | 96.5 | 12.6 | 7.63× | 83 | 1.007 |
| `screen-rgba16-store` | 118.6 | 48.5 | 2.45× | 16204 | 1.000 |
| `screen-rgba16-fastest` | 89.9 | 14.4 | 6.24× | 341 | 1.000 |
| `screen-rgba16-optimal` | 99.8 | 15.3 | 6.52× | 104 | 1.003 |
| `screen-rgba16-smallest` | 178.3 | 23.3 | 7.64× | 99 | 1.005 |

Notes:

- `photo-rgba16-optimal`: LibPng: output does not round-trip (samples differ), not timed
- `screen-rgba16-optimal`: LibPng: output does not round-trip (samples differ), not timed
