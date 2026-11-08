# Native region and scale decode of lossy stills (2026-11-08)

Lossy (VP8) stills decode `DecodeOptions.Region` / `ScaleDenominator` natively: rows stream from the macroblock-row decoder through the box filter (`BoxDownscaler`), only the window's columns are converted, and decoding stops after the macroblock row that emits the last needed source row. The baseline is `origin/scaled-decode` (88a489d), which takes the generic path (whole decode, box filter, crop). Every case is checked byte-identical to the baseline's output before it is timed. The 8-bit RGB / RGBA emit of `BoxDownscaler` gained an exact 32-bit path (shift for power-of-two boxes) in the same change; without it the native 1/2 decode was no faster than the generic one (12.3 vs 11.8 ms), since the emit's 64-bit divisions dominated.

## Machine

- CPU: AMD Ryzen 7 5800X (16 logical processors)
- OS: Microsoft Windows 10.0.26200 (X64)
- .NET: SDK 10.0.400, runtime .NET 10.0.11 (win-x64)
- Vector128.IsHardwareAccelerated: True, Vector256.IsHardwareAccelerated: True, Vector512.IsHardwareAccelerated: False
- AVX2: True, AVX-512F: False, BMI2: True
- libwebp: 1.6.0 SIMD build (`.cache/libwebp.dll`)

## Time

`quick --scaled --rounds 15`, in-process medians of 15 interleaved rounds × 10 decodes, ms.

Whole-image decode (no regression; the lossless row does not use the changed code):

| Input | libwebp | baseline | current | current ÷ baseline | current ÷ libwebp |
|---|---:|---:|---:|---:|---:|
| `lossy-opaque-1080p.webp` | 7.357 | 6.121 | 6.264 | 1.02× | 0.85× |
| `lossy-alpha-1080p.webp` | 13.644 | 7.087 | 6.886 | 0.97× | 0.50× |
| `lossless-1080p.webp` | 8.800 | 6.204 | 5.526 | 0.89× | 0.63× |

Region / scale decodes (`WebpImageDecoder.TryDecodeInto`, RGBA; regions 640×270 at full scale):

| Input | case | current | baseline | current / baseline |
|---|---|---:|---:|---:|
| `lossy-opaque-1080p.webp` | 1/2 | 9.059 | 13.387 | 0.68× |
| `lossy-opaque-1080p.webp` | 1/4 | 7.336 | 9.620 | 0.76× |
| `lossy-opaque-1080p.webp` | 1/8 | 8.177 | 8.825 | 0.93× |
| `lossy-opaque-1080p.webp` | top | 1.300 | 7.028 | 0.18× |
| `lossy-opaque-1080p.webp` | middle | 2.591 | 7.600 | 0.34× |
| `lossy-opaque-1080p.webp` | bottom | 5.277 | 7.283 | 0.72× |
| `lossy-alpha-1080p.webp` | 1/2 | 11.446 | 15.332 | 0.75× |
| `lossy-alpha-1080p.webp` | 1/4 | 9.143 | 10.887 | 0.84× |
| `lossy-alpha-1080p.webp` | 1/8 | 7.129 | 9.393 | 0.76× |
| `lossy-alpha-1080p.webp` | top | 1.342 | 7.888 | 0.17× |
| `lossy-alpha-1080p.webp` | middle | 3.292 | 8.103 | 0.41× |
| `lossy-alpha-1080p.webp` | bottom | 6.994 | 8.457 | 0.83× |

The whole-image rows moved within run-to-run noise (an earlier run of the same build gave 0.97× / 0.99× / 1.00×).

## Peak memory

`peakmem --scaled --inputs lossy`, one fresh process per decode, median of 3, MiB. A region / scale decode allocates and touches only its output's RGBA before the measurement.

| Input | contestant | first-decode rise | steady peak | steady rise |
|---|---|---:|---:|---:|
| `lossy-opaque-1080p.webp` | LibWebp | 0.25 | 33.17 | 0.19 |
| `lossy-opaque-1080p.webp` | Baseline | 0.28 | 39.52 | 0.02 |
| `lossy-opaque-1080p.webp` | Current | 0.27 | 37.00 | 0.02 |
| `lossy-opaque-1080p.webp` | Current:1/2 | 0.29 | 31.45 | 0.02 |
| `lossy-opaque-1080p.webp` | Baseline:1/2 | 15.62 | 49.62 | 1.38 |
| `lossy-opaque-1080p.webp` | Current:1/4 | 0.36 | 30.02 | 0.02 |
| `lossy-opaque-1080p.webp` | Baseline:1/4 | 14.58 | 46.99 | 0.02 |
| `lossy-opaque-1080p.webp` | Current:1/8 | 0.36 | 29.61 | 0.02 |
| `lossy-opaque-1080p.webp` | Baseline:1/8 | 14.29 | 46.36 | 0.02 |
| `lossy-opaque-1080p.webp` | Current:top | 0.27 | 30.12 | 0.02 |
| `lossy-opaque-1080p.webp` | Baseline:top | 14.64 | 47.24 | 0.39 |
| `lossy-opaque-1080p.webp` | Current:middle | 0.29 | 30.06 | 0.02 |
| `lossy-opaque-1080p.webp` | Baseline:middle | 14.64 | 47.29 | 0.39 |
| `lossy-opaque-1080p.webp` | Current:bottom | 0.30 | 30.24 | 0.02 |
| `lossy-opaque-1080p.webp` | Baseline:bottom | 14.64 | 47.19 | 0.39 |
| `lossy-alpha-1080p.webp` | LibWebp | 2.47 | 35.45 | 2.43 |
| `lossy-alpha-1080p.webp` | Baseline | 3.06 | 42.66 | 0.02 |
| `lossy-alpha-1080p.webp` | Current | 3.07 | 40.16 | 0.02 |
| `lossy-alpha-1080p.webp` | Current:1/2 | 3.09 | 34.59 | 0.02 |
| `lossy-alpha-1080p.webp` | Baseline:1/2 | 13.07 | 47.28 | 1.87 |
| `lossy-alpha-1080p.webp` | Current:1/4 | 3.13 | 33.12 | 0.02 |
| `lossy-alpha-1080p.webp` | Baseline:1/4 | 11.61 | 44.28 | 0.39 |
| `lossy-alpha-1080p.webp` | Current:1/8 | 3.13 | 32.75 | 0.02 |
| `lossy-alpha-1080p.webp` | Baseline:1/8 | 11.24 | 43.47 | 0.02 |
| `lossy-alpha-1080p.webp` | Current:top | 3.08 | 33.11 | 0.02 |
| `lossy-alpha-1080p.webp` | Baseline:top | 11.69 | 44.54 | 0.55 |
| `lossy-alpha-1080p.webp` | Current:middle | 3.08 | 33.10 | 0.02 |
| `lossy-alpha-1080p.webp` | Baseline:middle | 11.69 | 44.52 | 0.55 |
| `lossy-alpha-1080p.webp` | Current:bottom | 3.08 | 33.12 | 0.02 |
| `lossy-alpha-1080p.webp` | Baseline:bottom | 11.69 | 44.50 | 0.55 |

The native path's working memory is the row caches (about 0.3 MiB); with alpha, the whole ALPH plane (1920 × 1080 bytes ≈ 2 MiB) is still decoded first. The generic path holds the full-size RGBA frame and the RGB raster.
