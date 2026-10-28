# M5: decode into BGRA / RGB / BGR / grey

`IImageDecoder.TryDecodeInto(data, destination, stride, format)` replaces the RGBA-only
`TryDecodeIntoRgba8` in the interface. PNG writes each row in the requested layout as it is unfiltered
(8-bit grey, RGB, grey+alpha and RGBA without a transparent colour convert straight from the row; other
rows go through an RGBA row); Adam7 in a layout other than RGBA assembles RGBA first. The conversions
are `PixelConverter` (SSSE3 `pshufb` / arm64 `TBL`, scalar twin).

Default RGBA decode against HEAD (`2ce779e`, same process, `matrix decode`, 64 cases): 0.96–1.02 after
two fixes found by this A/B (8-bit grey RGBA kept on the expander; no bounded slice per Adam7 row).
Managed allocation per decode unchanged.

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
- Baseline: 2ce779e01d16 PNG: tRNS colour key decode; grey+alpha, tRNS, pHYs writing; lossless reduction

### Decode into each layout (in-process medians of 9 interleaved rounds, ms)

`native` = `PngImageDecoder.TryDecodeInto(format)`; `two-step` = `TryDecodeIntoRgba8` + `PixelConverter.ConvertRow` over the frame (what an RGBA-only API leaves a caller to do); `rgba` = `TryDecodeIntoRgba8` alone; libpng = simplified API `png_image_finish_read` in the same layout (its grey is its own luma, and it composites alpha it drops onto a background, so its bytes differ). Ratios below 1 mean native is faster.

| Case | Layout | rgba | native | two-step | libpng | native ÷ two-step | native ÷ rgba | native ÷ libpng |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `photo-g8` | Bgra32 | 13.77 | 13.77 | 14.19 | 15.73 | 0.97 | 1.00 | 0.88 |
| `photo-g8` | Rgb24 | 13.97 | 13.87 | 14.39 | 13.83 | 0.96 | 0.99 | 1.00 |
| `photo-g8` | Bgr24 | 14.07 | 13.97 | 14.46 | 14.43 | 0.97 | 0.99 | 0.97 |
| `photo-g8` | Gray8 | 13.74 | 13.52 | 14.46 | 12.47 | 0.94 | 0.98 | 1.08 |
| `photo-rgb8` | Bgra32 | 23.43 | 23.72 | 24.40 | 30.68 | 0.97 | 1.01 | 0.77 |
| `photo-rgb8` | Rgb24 | 23.82 | 23.42 | 24.07 | 28.56 | 0.97 | 0.98 | 0.82 |
| `photo-rgb8` | Bgr24 | 23.53 | 23.56 | 23.88 | 29.44 | 0.99 | 1.00 | 0.80 |
| `photo-rgb8` | Gray8 | 23.41 | 23.83 | 24.17 | 32.01 | 0.99 | 1.02 | 0.74 |
| `photo-rgb16` | Bgra32 | 39.27 | 39.36 | 39.98 | 61.97 | 0.98 | 1.00 | 0.64 |
| `photo-rgb16` | Rgb24 | 39.27 | 39.45 | 39.61 | 59.37 | 1.00 | 1.00 | 0.66 |
| `photo-rgb16` | Bgr24 | 39.05 | 39.26 | 39.30 | 60.04 | 1.00 | 1.01 | 0.65 |
| `photo-rgb16` | Gray8 | 39.10 | 39.63 | 39.73 | 60.48 | 1.00 | 1.01 | 0.66 |
| `photo-p8` | Bgra32 | 8.919 | 9.241 | 9.342 | 12.78 | 0.99 | 1.04 | 0.72 |
| `photo-p8` | Rgb24 | 8.919 | 9.147 | 9.292 | 10.76 | 0.98 | 1.03 | 0.85 |
| `photo-p8` | Bgr24 | 8.971 | 9.245 | 9.113 | 11.27 | 1.01 | 1.03 | 0.82 |
| `photo-p8` | Gray8 | 8.982 | 9.483 | 9.483 | 13.91 | 1.00 | 1.06 | 0.68 |
| `photo-rgba8` | Bgra32 | 25.92 | 26.31 | 26.50 | 35.23 | 0.99 | 1.02 | 0.75 |
| `photo-rgba8` | Rgb24 | 25.79 | 26.20 | 26.33 | 46.36 | 1.00 | 1.02 | 0.57 |
| `photo-rgba8` | Bgr24 | 25.91 | 26.37 | 26.25 | 47.33 | 1.00 | 1.02 | 0.56 |
| `photo-rgba8` | Gray8 | 25.81 | 26.52 | 26.44 | 40.45 | 1.00 | 1.03 | 0.66 |
| `photo-g8-adam7` | Bgra32 | 13.27 | 13.68 | 13.50 | 27.67 | 1.01 | 1.03 | 0.49 |
| `photo-g8-adam7` | Rgb24 | 13.43 | 13.49 | 13.50 | 25.42 | 1.00 | 1.00 | 0.53 |
| `photo-g8-adam7` | Bgr24 | 13.23 | 13.46 | 13.53 | 26.20 | 0.99 | 1.02 | 0.51 |
| `photo-g8-adam7` | Gray8 | 13.33 | 13.72 | 13.85 | 24.33 | 0.99 | 1.03 | 0.56 |
| `photo-rgb8-adam7` | Bgra32 | 26.52 | 26.98 | 26.82 | 43.39 | 1.01 | 1.02 | 0.62 |
| `photo-rgb8-adam7` | Rgb24 | 26.34 | 26.75 | 26.87 | 40.85 | 1.00 | 1.02 | 0.65 |
| `photo-rgb8-adam7` | Bgr24 | 26.23 | 26.62 | 26.72 | 41.55 | 1.00 | 1.01 | 0.64 |
| `photo-rgb8-adam7` | Gray8 | 26.48 | 27.26 | 27.09 | 44.87 | 1.01 | 1.03 | 0.61 |
| `photo-rgb16-adam7` | Bgra32 | 39.51 | 39.86 | 39.83 | 68.87 | 1.00 | 1.01 | 0.58 |
| `photo-rgb16-adam7` | Rgb24 | 39.31 | 40.06 | 40.32 | 67.07 | 0.99 | 1.02 | 0.60 |
| `photo-rgb16-adam7` | Bgr24 | 39.41 | 39.50 | 39.73 | 67.62 | 0.99 | 1.00 | 0.58 |
| `photo-rgb16-adam7` | Gray8 | 39.25 | 40.12 | 40.04 | 67.97 | 1.00 | 1.02 | 0.59 |
| `photo-p8-adam7` | Bgra32 | 10.64 | 11.38 | 10.81 | 25.31 | 1.05 | 1.07 | 0.45 |
| `photo-p8-adam7` | Rgb24 | 10.56 | 10.70 | 10.79 | 22.77 | 0.99 | 1.01 | 0.47 |
| `photo-p8-adam7` | Bgr24 | 10.84 | 11.11 | 10.75 | 23.80 | 1.03 | 1.02 | 0.47 |
| `photo-p8-adam7` | Gray8 | 10.40 | 11.20 | 11.22 | 25.92 | 1.00 | 1.08 | 0.43 |
| `photo-rgba8-adam7` | Bgra32 | 30.58 | 31.54 | 31.55 | 47.69 | 1.00 | 1.03 | 0.66 |
| `photo-rgba8-adam7` | Rgb24 | 30.40 | 30.95 | 31.21 | 48.23 | 0.99 | 1.02 | 0.64 |
| `photo-rgba8-adam7` | Bgr24 | 30.43 | 31.47 | 31.44 | 48.98 | 1.00 | 1.03 | 0.64 |
| `photo-rgba8-adam7` | Gray8 | 31.01 | 31.42 | 30.93 | 42.00 | 1.02 | 1.01 | 0.75 |
| `screen-g8` | Bgra32 | 2.067 | 2.160 | 2.340 | 7.437 | 0.92 | 1.05 | 0.29 |
| `screen-g8` | Rgb24 | 2.078 | 2.190 | 2.279 | 5.252 | 0.96 | 1.05 | 0.42 |
| `screen-g8` | Bgr24 | 2.050 | 2.209 | 2.284 | 6.086 | 0.97 | 1.08 | 0.36 |
| `screen-g8` | Gray8 | 2.053 | 1.960 | 2.629 | 4.140 | 0.75 | 0.95 | 0.47 |
| `screen-rgb8` | Bgra32 | 2.210 | 2.199 | 2.450 | 13.88 | 0.90 | 1.00 | 0.16 |
| `screen-rgb8` | Rgb24 | 2.158 | 2.006 | 2.433 | 11.64 | 0.82 | 0.93 | 0.17 |
| `screen-rgb8` | Bgr24 | 2.190 | 2.154 | 2.402 | 12.38 | 0.90 | 0.98 | 0.17 |
| `screen-rgb8` | Gray8 | 2.175 | 2.575 | 2.734 | 14.35 | 0.94 | 1.18 | 0.18 |
| `screen-rgb16` | Bgra32 | 2.706 | 2.957 | 2.962 | 32.63 | 1.00 | 1.09 | 0.09 |
| `screen-rgb16` | Rgb24 | 2.704 | 2.925 | 2.973 | 30.54 | 0.98 | 1.08 | 0.10 |
| `screen-rgb16` | Bgr24 | 2.757 | 3.084 | 3.066 | 31.43 | 1.01 | 1.12 | 0.10 |
| `screen-rgb16` | Gray8 | 2.653 | 3.221 | 3.206 | 30.03 | 1.00 | 1.21 | 0.11 |
| `screen-p8` | Bgra32 | 1.432 | 1.698 | 1.830 | 6.823 | 0.93 | 1.19 | 0.25 |
| `screen-p8` | Rgb24 | 1.423 | 1.681 | 1.708 | 4.658 | 0.98 | 1.18 | 0.36 |
| `screen-p8` | Bgr24 | 1.429 | 1.681 | 1.704 | 5.352 | 0.99 | 1.18 | 0.31 |
| `screen-p8` | Gray8 | 1.415 | 1.982 | 1.979 | 7.021 | 1.00 | 1.40 | 0.28 |
| `screen-rgba8` | Bgra32 | 2.082 | 2.345 | 2.388 | 16.82 | 0.98 | 1.13 | 0.14 |
| `screen-rgba8` | Rgb24 | 2.062 | 2.334 | 2.308 | 19.11 | 1.01 | 1.13 | 0.12 |
| `screen-rgba8` | Bgr24 | 2.082 | 2.344 | 2.355 | 19.85 | 1.00 | 1.13 | 0.12 |
| `screen-rgba8` | Gray8 | 2.059 | 2.629 | 2.607 | 19.31 | 1.01 | 1.28 | 0.14 |
| `screen-g8-adam7` | Bgra32 | 2.825 | 3.161 | 3.179 | 18.45 | 0.99 | 1.12 | 0.17 |
| `screen-g8-adam7` | Rgb24 | 2.858 | 3.170 | 3.159 | 16.52 | 1.00 | 1.11 | 0.19 |
| `screen-g8-adam7` | Bgr24 | 2.872 | 3.180 | 3.122 | 17.21 | 1.02 | 1.11 | 0.18 |
| `screen-g8-adam7` | Gray8 | 3.120 | 3.603 | 3.776 | 15.51 | 0.95 | 1.15 | 0.23 |
| `screen-rgb8-adam7` | Bgra32 | 3.042 | 3.405 | 3.405 | 25.08 | 1.00 | 1.12 | 0.14 |
| `screen-rgb8-adam7` | Rgb24 | 3.116 | 3.508 | 3.394 | 25.02 | 1.03 | 1.13 | 0.14 |
| `screen-rgb8-adam7` | Bgr24 | 3.078 | 3.336 | 3.273 | 23.98 | 1.02 | 1.08 | 0.14 |
| `screen-rgb8-adam7` | Gray8 | 3.105 | 3.835 | 3.699 | 25.81 | 1.04 | 1.24 | 0.15 |
| `screen-rgb16-adam7` | Bgra32 | 3.803 | 4.193 | 4.571 | 44.24 | 0.92 | 1.10 | 0.09 |
| `screen-rgb16-adam7` | Rgb24 | 3.699 | 3.994 | 3.884 | 42.15 | 1.03 | 1.08 | 0.09 |
| `screen-rgb16-adam7` | Bgr24 | 3.564 | 4.295 | 3.992 | 42.34 | 1.08 | 1.20 | 0.10 |
| `screen-rgb16-adam7` | Gray8 | 3.590 | 4.262 | 4.184 | 41.53 | 1.02 | 1.19 | 0.10 |
| `screen-p8-adam7` | Bgra32 | 2.212 | 2.601 | 2.481 | 17.90 | 1.05 | 1.18 | 0.15 |
| `screen-p8-adam7` | Rgb24 | 2.223 | 2.523 | 2.487 | 15.96 | 1.01 | 1.13 | 0.16 |
| `screen-p8-adam7` | Bgr24 | 2.216 | 2.481 | 2.443 | 16.66 | 1.02 | 1.12 | 0.15 |
| `screen-p8-adam7` | Gray8 | 2.179 | 2.800 | 2.750 | 18.11 | 1.02 | 1.29 | 0.15 |
| `screen-rgba8-adam7` | Bgra32 | 2.945 | 3.351 | 3.245 | 27.56 | 1.03 | 1.14 | 0.12 |
| `screen-rgba8-adam7` | Rgb24 | 2.946 | 3.343 | 3.247 | 19.06 | 1.03 | 1.13 | 0.18 |
| `screen-rgba8-adam7` | Bgr24 | 2.961 | 3.299 | 3.202 | 19.72 | 1.03 | 1.11 | 0.17 |
| `screen-rgba8-adam7` | Gray8 | 2.969 | 3.576 | 3.527 | 19.24 | 1.01 | 1.20 | 0.19 |
