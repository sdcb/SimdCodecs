# Decode into BGRA / RGB / BGR / grey

`JpegPixelFormat` gains `Bgr24` and `Gray8`; `JpegImageDecoder.TryDecodeInto` maps every
`ImagePixelFormat` onto `JpegDecoder.DecodeTo`. BGRA comes out of the colour conversion directly; RGB and
BGR are packed from the converted row. Grey from a greyscale or YCbCr frame is its Y plane: the chroma
blocks are entropy-decoded (the scans interleave them) but never inverse-transformed, upsampled or
colour-converted, as libjpeg-turbo does; RGB-coded, CMYK and YCCK frames take the BT.601 luma of the RGB
row. No change to the decoder's arithmetic: RGBA, BGRA and RGB output is byte-identical to the
baseline's, and grey to the Y plane the full decode reconstructs (both checked before timing).

Against the baseline (`2ce779e`, same process; three repeats on a subset): RGBA 0.97–1.00, BGRA
0.96–1.00, RGB 0.93–1.01.

- CPU: AMD Ryzen 7 5800X 8-Core Processor
- OS: Microsoft Windows 10.0.26200 (X64)
- .NET: .NET 10.0.11 (win-x64), process X64
- Logical processors: 16
- Vector128.IsHardwareAccelerated: True, Vector256.IsHardwareAccelerated: True, Vector512.IsHardwareAccelerated: False
- AVX2: True, AVX-512F: False, BMI2: True
- GC: workstation, non-concurrent; tiered compilation default

### Decode into each layout (in-process medians of 9 interleaved rounds, ms)

`native` = `JpegImageDecoder.TryDecodeInto(format)`; `two-step` = decode to RGBA + `PixelConverter.ConvertRow` over the frame (what an RGBA-only API leaves a caller to do); `rgba` = decode to RGBA alone; turbo = libjpeg-turbo `tj3Decompress8` in the same layout (accurate IDCT, fancy upsampling). Grey from a YCbCr JPEG is its Y plane for both (chroma is never inverse-transformed, upsampled or converted), and `two-step` is then the luma of the RGB, a different (slower) result shown for scale; `max ∆ turbo` is the largest difference from turbo's grey. Ratios below 1 mean native is faster. `÷ baseline`: native against the last commit's decoder in the same layout (built with `baseline`; it had RGBA, BGRA and RGB only), with RGBA's own ratio in brackets.

| Case | MP | Layout | rgba | native | two-step | turbo | native ÷ two-step | native ÷ rgba | native ÷ turbo | ÷ baseline | max ∆ turbo |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `420-q50-3mp/landscape-almsee` | 3.0 | Bgra32 | 4.767 | 4.596 | 5.286 | 5.223 | 0.87 | 0.96 | 0.88 | 1.00 (rgba 1.05) | 3 |
| `420-q50-3mp/landscape-almsee` | 3.0 | Rgb24 | 4.717 | 5.030 | 5.114 | 5.431 | 0.98 | 1.07 | 0.93 | 1.00 (rgba 1.04) | 3 |
| `420-q50-3mp/landscape-almsee` | 3.0 | Bgr24 | 4.862 | 5.031 | 5.143 | 5.323 | 0.98 | 1.03 | 0.95 | — | 3 |
| `420-q50-3mp/landscape-almsee` | 3.0 | Gray8 | 4.770 | 3.283 | 5.517 | 3.818 | 0.60 | 0.69 | 0.86 | — | 1 |
| `420-q90-3mp/landscape-almsee` | 3.0 | Bgra32 | 7.379 | 7.218 | 8.247 | 8.541 | 0.88 | 0.98 | 0.85 | 0.99 (rgba 1.03) | 4 |
| `420-q90-3mp/landscape-almsee` | 3.0 | Rgb24 | 7.408 | 7.603 | 7.872 | 8.563 | 0.97 | 1.03 | 0.89 | 1.00 (rgba 1.03) | 4 |
| `420-q90-3mp/landscape-almsee` | 3.0 | Bgr24 | 7.485 | 7.613 | 7.733 | 8.526 | 0.98 | 1.02 | 0.89 | — | 4 |
| `420-q90-3mp/landscape-almsee` | 3.0 | Gray8 | 7.398 | 5.703 | 8.283 | 6.926 | 0.69 | 0.77 | 0.82 | — | 1 |
| `444-q50-3mp/landscape-almsee` | 3.0 | Bgra32 | 6.075 | 5.779 | 6.358 | 7.111 | 0.91 | 0.95 | 0.81 | 0.99 (rgba 1.05) | 3 |
| `444-q50-3mp/landscape-almsee` | 3.0 | Rgb24 | 6.053 | 6.072 | 6.261 | 7.176 | 0.97 | 1.00 | 0.85 | 0.97 (rgba 1.05) | 3 |
| `444-q50-3mp/landscape-almsee` | 3.0 | Bgr24 | 6.048 | 6.134 | 6.283 | 7.194 | 0.98 | 1.01 | 0.85 | — | 3 |
| `444-q50-3mp/landscape-almsee` | 3.0 | Gray8 | 6.055 | 3.937 | 6.810 | 4.870 | 0.58 | 0.65 | 0.81 | — | 1 |
| `444-q90-3mp/landscape-almsee` | 3.0 | Bgra32 | 10.06 | 9.600 | 10.27 | 11.48 | 0.93 | 0.95 | 0.84 | 1.01 (rgba 1.04) | 3 |
| `444-q90-3mp/landscape-almsee` | 3.0 | Rgb24 | 9.831 | 10.35 | 10.49 | 11.67 | 0.99 | 1.05 | 0.89 | 1.05 (rgba 1.04) | 3 |
| `444-q90-3mp/landscape-almsee` | 3.0 | Bgr24 | 9.759 | 10.02 | 10.03 | 11.59 | 1.00 | 1.03 | 0.86 | — | 3 |
| `444-q90-3mp/landscape-almsee` | 3.0 | Gray8 | 9.863 | 7.182 | 10.36 | 8.625 | 0.69 | 0.73 | 0.83 | — | 1 |
| `420-q50-3mp/night-alexanderplatz` | 3.0 | Bgra32 | 4.085 | 3.973 | 4.523 | 4.434 | 0.88 | 0.97 | 0.90 | 1.01 (rgba 1.05) | 3 |
| `420-q50-3mp/night-alexanderplatz` | 3.0 | Rgb24 | 4.027 | 4.273 | 4.397 | 4.460 | 0.97 | 1.06 | 0.96 | 1.00 (rgba 1.04) | 3 |
| `420-q50-3mp/night-alexanderplatz` | 3.0 | Bgr24 | 3.999 | 4.266 | 4.435 | 4.547 | 0.96 | 1.07 | 0.94 | — | 3 |
| `420-q50-3mp/night-alexanderplatz` | 3.0 | Gray8 | 4.045 | 2.589 | 4.757 | 3.072 | 0.54 | 0.64 | 0.84 | — | 1 |
| `420-q90-3mp/night-alexanderplatz` | 3.0 | Bgra32 | 6.367 | 6.230 | 6.791 | 7.382 | 0.92 | 0.98 | 0.84 | 1.01 (rgba 1.02) | 3 |
| `420-q90-3mp/night-alexanderplatz` | 3.0 | Rgb24 | 6.452 | 6.641 | 6.743 | 7.377 | 0.98 | 1.03 | 0.90 | 0.99 (rgba 1.05) | 3 |
| `420-q90-3mp/night-alexanderplatz` | 3.0 | Bgr24 | 6.411 | 6.622 | 6.888 | 7.394 | 0.96 | 1.03 | 0.90 | — | 3 |
| `420-q90-3mp/night-alexanderplatz` | 3.0 | Gray8 | 6.445 | 4.866 | 7.090 | 5.910 | 0.69 | 0.76 | 0.82 | — | 1 |
| `444-q50-3mp/night-alexanderplatz` | 3.0 | Bgra32 | 5.055 | 4.797 | 5.640 | 6.029 | 0.85 | 0.95 | 0.80 | 1.00 (rgba 1.04) | 3 |
| `444-q50-3mp/night-alexanderplatz` | 3.0 | Rgb24 | 5.088 | 5.202 | 5.559 | 6.267 | 0.94 | 1.02 | 0.83 | 0.96 (rgba 1.03) | 3 |
| `444-q50-3mp/night-alexanderplatz` | 3.0 | Bgr24 | 5.118 | 5.291 | 5.485 | 6.159 | 0.96 | 1.03 | 0.86 | — | 3 |
| `444-q50-3mp/night-alexanderplatz` | 3.0 | Gray8 | 5.079 | 3.187 | 6.076 | 3.849 | 0.52 | 0.63 | 0.83 | — | 1 |
| `444-q90-3mp/night-alexanderplatz` | 3.0 | Bgra32 | 8.246 | 7.942 | 8.701 | 9.847 | 0.91 | 0.96 | 0.81 | 0.99 (rgba 1.05) | 3 |
| `444-q90-3mp/night-alexanderplatz` | 3.0 | Rgb24 | 8.434 | 8.451 | 8.469 | 9.903 | 1.00 | 1.00 | 0.85 | 1.00 (rgba 1.05) | 3 |
| `444-q90-3mp/night-alexanderplatz` | 3.0 | Bgr24 | 8.349 | 8.375 | 8.608 | 9.910 | 0.97 | 1.00 | 0.85 | — | 3 |
| `444-q90-3mp/night-alexanderplatz` | 3.0 | Gray8 | 8.354 | 6.020 | 9.079 | 7.349 | 0.66 | 0.72 | 0.82 | — | 1 |
| `420-q75-3mp-rst/landscape-almsee` | 3.0 | Bgra32 | 5.693 | 5.533 | 6.247 | 6.447 | 0.89 | 0.97 | 0.86 | 0.96 (rgba 1.04) | 3 |
| `420-q75-3mp-rst/landscape-almsee` | 3.0 | Rgb24 | 5.683 | 5.947 | 6.152 | 6.533 | 0.97 | 1.05 | 0.91 | 1.01 (rgba 1.03) | 3 |
| `420-q75-3mp-rst/landscape-almsee` | 3.0 | Bgr24 | 5.773 | 6.120 | 6.542 | 6.998 | 0.94 | 1.06 | 0.87 | — | 3 |
| `420-q75-3mp-rst/landscape-almsee` | 3.0 | Gray8 | 5.712 | 4.170 | 6.476 | 5.002 | 0.64 | 0.73 | 0.83 | — | 1 |
| `420-q75-3mp-prog/landscape-almsee` | 3.0 | Bgra32 | 15.35 | 15.64 | 16.36 | 21.17 | 0.96 | 1.02 | 0.74 | 0.99 (rgba 0.97) | 3 |
| `420-q75-3mp-prog/landscape-almsee` | 3.0 | Rgb24 | 15.08 | 15.59 | 16.16 | 20.33 | 0.96 | 1.03 | 0.77 | 1.01 (rgba 0.99) | 3 |
| `420-q75-3mp-prog/landscape-almsee` | 3.0 | Bgr24 | 14.88 | 15.32 | 15.48 | 20.64 | 0.99 | 1.03 | 0.74 | — | 3 |
| `420-q75-3mp-prog/landscape-almsee` | 3.0 | Gray8 | 14.85 | 13.63 | 15.76 | 19.04 | 0.86 | 0.92 | 0.72 | — | 1 |
| `422-q75-3mp/landscape-almsee` | 3.0 | Bgra32 | 6.371 | 6.185 | 6.938 | 7.195 | 0.89 | 0.97 | 0.86 | 1.01 (rgba 1.04) | 4 |
| `422-q75-3mp/landscape-almsee` | 3.0 | Rgb24 | 6.380 | 6.514 | 6.757 | 7.225 | 0.96 | 1.02 | 0.90 | 0.98 (rgba 1.05) | 4 |
| `422-q75-3mp/landscape-almsee` | 3.0 | Bgr24 | 6.527 | 6.718 | 6.882 | 7.591 | 0.98 | 1.03 | 0.89 | — | 4 |
| `422-q75-3mp/landscape-almsee` | 3.0 | Gray8 | 6.416 | 4.530 | 7.449 | 5.357 | 0.61 | 0.71 | 0.85 | — | 1 |
| `gray-q75-3mp/landscape-almsee` | 3.0 | Bgra32 | 3.962 | 3.680 | 4.457 | 6.102 | 0.83 | 0.93 | 0.60 | 0.99 (rgba 1.03) | 1 |
| `gray-q75-3mp/landscape-almsee` | 3.0 | Rgb24 | 3.945 | 4.083 | 4.341 | 5.881 | 0.94 | 1.03 | 0.69 | 0.94 (rgba 1.02) | 1 |
| `gray-q75-3mp/landscape-almsee` | 3.0 | Bgr24 | 4.014 | 4.107 | 4.723 | 5.851 | 0.87 | 1.02 | 0.70 | — | 1 |
| `gray-q75-3mp/landscape-almsee` | 3.0 | Gray8 | 4.005 | 3.430 | 4.713 | 4.816 | 0.73 | 0.86 | 0.71 | — | 1 |
| `camera/cam-pixel7` | 12.5 | Bgra32 | 26.69 | 26.04 | 30.03 | 29.22 | 0.87 | 0.98 | 0.89 | 1.01 (rgba 1.04) | 3 |
| `camera/cam-pixel7` | 12.5 | Rgb24 | 26.97 | 27.46 | 29.81 | 29.26 | 0.92 | 1.02 | 0.94 | 0.99 (rgba 1.06) | 3 |
| `camera/cam-pixel7` | 12.5 | Bgr24 | 26.22 | 27.50 | 29.17 | 29.41 | 0.94 | 1.05 | 0.94 | — | 3 |
| `camera/cam-pixel7` | 12.5 | Gray8 | 26.35 | 19.94 | 29.90 | 23.37 | 0.67 | 0.76 | 0.85 | — | 1 |

