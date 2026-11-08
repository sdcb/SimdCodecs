# Region and scale decode (non-interlaced): native window decode vs the generic path

Branch `scaled-png` against a baseline built from `origin/scaled-decode` (88a489d) with `baseline origin/scaled-decode`, loaded in the same process. On the baseline every region or scale decode takes the generic path: the whole image is decoded, then `BoxDownscaler` and a crop. The native path decodes only the rows down to the last one the output needs, unfilters only as far right as its last column, and box-filters rows as they arrive. Both give identical pixels: the benchmark checks before timing, and the test suite checks every colour type, depth, tRNS, scale, region and pixel format.

Other jobs ran on the machine at the same time, so absolute numbers move by 10-30% between runs. Ratios within one run are what count.

Commands: `dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Png.Benchmarks -- scaled`, `scaled --rounds 21 --cases '^screen-(g8|rgba8)$'`, `scaled-peakmem --cases '^(photo-rgba8|photo-rgb16|screen-p8)$'`, `matrix decode --rounds 11 --cases '^(photo|screen)-(g4|g8|rgb8|p8|rgba8|rgb16)$'`. .NET SDK 10.0.400.

How to read the numbers:

- A region that ends high in the image costs a fraction of the full decode (512² top-left: 0.11-0.49× of the generic path).
- A region at the bottom still inflates every row above it, so it costs about as much as the full decode. Inflating is most of the time; see the README's lessons.
- Scale-only output costs about what the generic path costs (0.75-1.1×). Box-filtering every source row is the same work either way, and on the screen images it costs more than the decode itself. For 16-bit (`rgb16`) the native path is 0.5-0.9×, because it skips the generic path's full-size 16-bit copies.
- The gain for scale-only output is memory. The generic path holds the full-size image; the native one holds the output and a few rows (see the peak working set below).

- CPU: AMD Ryzen 7 5800X 8-Core Processor
- OS: Microsoft Windows 10.0.26200 (X64)
- .NET: .NET 10.0.11 (win-x64), process X64
- Logical processors: 16
- Vector128.IsHardwareAccelerated: True, Vector256.IsHardwareAccelerated: True, Vector512.IsHardwareAccelerated: False
- AVX2: True, AVX-512F: False
- Image size: 1920×1080
- `photo` source: synthetic (no JPEG corpus master found; run the JPEG benchmark's scripts/fetch-corpus.sh for a real photo)
- `screen` source: synthetic
- ImageSharp: 3.1.12
- libpng: not found (tried libpng16.dll, libpng.dll; set LIBPNG_PATH)
- Baseline: 88a489d47a39 Merge scaled-decode-l1: every codec accepts any power-of-two ScaleDenominator (native, then a frozen box downscale) (#6)

### Region and scale decode (`TryDecode` with `DecodeOptions`; in-process medians of 7 interleaved rounds, ms)

`native` = the current `TryDecode(options)`; `baseline` = the baseline's (`88a489d47a39 Merge scaled-decode-l1: every codec accepts any power-of-two ScaleDenominator (native, then a frozen box downscale) (#6)`), which decodes the whole image, then box-filters and crops; `full decode` = `TryDecode` without options, for scale. Both give identical pixels (checked first). Allocated = managed bytes of one warm operation.

| Case | Mode | full decode | baseline | native | native ÷ baseline | native ÷ full decode | alloc baseline | alloc native |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `photo-g8` | region 512² top-left | 14.13 | 14.35 | 5.019 | 0.35 | 0.36 | 2284 KB | 257 KB |
| `photo-g8` | region 512² centre | 18.38 | 18.42 | 12.41 | 0.67 | 0.67 | 2284 KB | 257 KB |
| `photo-g8` | region 512² bottom-right | 18.33 | 18.44 | 18.30 | 0.99 | 1.00 | 2284 KB | 257 KB |
| `photo-g8` | scale 1/2 | 18.38 | 23.94 | 23.94 | 1.00 | 1.30 | 2542 KB | 515 KB |
| `photo-g8` | scale 1/4 | 17.18 | 20.29 | 20.24 | 1.00 | 1.18 | 2163 KB | 135 KB |
| `photo-g8` | scale 1/8 | 18.36 | 19.14 | 19.13 | 1.00 | 1.04 | 2068 KB | 41 KB |
| `photo-g8` | 1/4 + region 128² centre | 18.54 | 18.88 | 12.59 | 0.67 | 0.68 | 2047 KB | 19 KB |
| `photo-rgb8` | region 512² top-left | 34.50 | 34.27 | 16.05 | 0.47 | 0.47 | 6851 KB | 770 KB |
| `photo-rgb8` | region 512² centre | 34.53 | 34.96 | 26.04 | 0.74 | 0.75 | 6851 KB | 770 KB |
| `photo-rgb8` | region 512² bottom-right | 33.38 | 33.04 | 32.42 | 0.98 | 0.97 | 6851 KB | 770 KB |
| `photo-rgb8` | scale 1/2 | 32.58 | 42.55 | 42.00 | 0.99 | 1.29 | 7624 KB | 1543 KB |
| `photo-rgb8` | scale 1/4 | 32.04 | 35.04 | 35.16 | 1.00 | 1.10 | 6485 KB | 404 KB |
| `photo-rgb8` | scale 1/8 | 34.49 | 36.38 | 36.31 | 1.00 | 1.05 | 6200 KB | 119 KB |
| `photo-rgb8` | 1/4 + region 128² centre | 33.71 | 34.15 | 26.20 | 0.77 | 0.78 | 6137 KB | 56 KB |
| `photo-rgb16` | region 512² top-left | 60.33 | 61.24 | 24.56 | 0.40 | 0.41 | 38002 KB | 1541 KB |
| `photo-rgb16` | region 512² centre | 62.18 | 61.54 | 37.59 | 0.61 | 0.60 | 38002 KB | 1541 KB |
| `photo-rgb16` | region 512² bottom-right | 62.52 | 69.66 | 51.32 | 0.74 | 0.82 | 38002 KB | 1541 KB |
| `photo-rgb16` | scale 1/2 | 61.45 | 71.26 | 61.03 | 0.86 | 0.99 | 39527 KB | 3065 KB |
| `photo-rgb16` | scale 1/4 | 54.32 | 57.49 | 50.66 | 0.88 | 0.93 | 37237 KB | 776 KB |
| `photo-rgb16` | scale 1/8 | 58.84 | 65.03 | 56.82 | 0.87 | 0.97 | 36662 KB | 200 KB |
| `photo-rgb16` | 1/4 + region 128² centre | 61.08 | 60.45 | 37.42 | 0.62 | 0.61 | 36566 KB | 104 KB |
| `photo-p8` | region 512² top-left | 10.18 | 10.78 | 4.641 | 0.43 | 0.46 | 11154 KB | 1027 KB |
| `photo-p8` | region 512² centre | 13.45 | 13.51 | 9.188 | 0.68 | 0.68 | 11154 KB | 1027 KB |
| `photo-p8` | region 512² bottom-right | 13.15 | 14.04 | 12.01 | 0.86 | 0.91 | 11154 KB | 1027 KB |
| `photo-p8` | scale 1/2 | 10.29 | 17.39 | 16.13 | 0.93 | 1.57 | 12185 KB | 2058 KB |
| `photo-p8` | scale 1/4 | 13.35 | 18.17 | 17.58 | 0.97 | 1.32 | 10667 KB | 540 KB |
| `photo-p8` | scale 1/8 | 13.21 | 16.28 | 15.86 | 0.97 | 1.20 | 10287 KB | 160 KB |
| `photo-p8` | 1/4 + region 128² centre | 12.17 | 13.99 | 9.607 | 0.69 | 0.79 | 10202 KB | 75 KB |
| `photo-rgba8` | region 512² top-left | 32.70 | 33.10 | 16.08 | 0.49 | 0.49 | 9134 KB | 1026 KB |
| `photo-rgba8` | region 512² centre | 36.71 | 35.70 | 26.06 | 0.73 | 0.71 | 9134 KB | 1026 KB |
| `photo-rgba8` | region 512² bottom-right | 37.58 | 36.14 | 36.14 | 1.00 | 0.96 | 9134 KB | 1026 KB |
| `photo-rgba8` | scale 1/2 | 37.23 | 45.64 | 47.29 | 1.04 | 1.27 | 10165 KB | 2057 KB |
| `photo-rgba8` | scale 1/4 | 33.77 | 40.95 | 38.35 | 0.94 | 1.14 | 8646 KB | 538 KB |
| `photo-rgba8` | scale 1/8 | 39.17 | 40.62 | 41.21 | 1.01 | 1.05 | 8266 KB | 159 KB |
| `photo-rgba8` | 1/4 + region 128² centre | 38.65 | 39.33 | 29.67 | 0.75 | 0.77 | 8182 KB | 74 KB |
| `screen-g8` | region 512² top-left | 3.490 | 2.384 | 0.718 | 0.30 | 0.21 | 2284 KB | 257 KB |
| `screen-g8` | region 512² centre | 3.142 | 3.715 | 1.535 | 0.41 | 0.49 | 2284 KB | 257 KB |
| `screen-g8` | region 512² bottom-right | 3.938 | 2.371 | 2.694 | 1.14 | 0.68 | 2284 KB | 257 KB |
| `screen-g8` | scale 1/2 | 3.840 | 9.506 | 9.465 | 1.00 | 2.46 | 2542 KB | 515 KB |
| `screen-g8` | scale 1/4 | 3.935 | 4.334 | 4.711 | 1.09 | 1.20 | 2162 KB | 135 KB |
| `screen-g8` | scale 1/8 | 3.053 | 3.149 | 4.686 | 1.49 | 1.53 | 2067 KB | 40 KB |
| `screen-g8` | 1/4 + region 128² centre | 4.005 | 4.228 | 2.791 | 0.66 | 0.70 | 2046 KB | 19 KB |
| `screen-rgb8` | region 512² top-left | 3.445 | 3.795 | 1.010 | 0.27 | 0.29 | 6850 KB | 769 KB |
| `screen-rgb8` | region 512² centre | 3.426 | 3.832 | 2.148 | 0.56 | 0.63 | 6850 KB | 769 KB |
| `screen-rgb8` | region 512² bottom-right | 3.464 | 3.769 | 3.127 | 0.83 | 0.90 | 6850 KB | 769 KB |
| `screen-rgb8` | scale 1/2 | 3.065 | 11.93 | 11.64 | 0.98 | 3.80 | 7623 KB | 1542 KB |
| `screen-rgb8` | scale 1/4 | 3.410 | 6.886 | 6.058 | 0.88 | 1.78 | 6484 KB | 403 KB |
| `screen-rgb8` | scale 1/8 | 3.150 | 5.644 | 4.236 | 0.75 | 1.34 | 6199 KB | 118 KB |
| `screen-rgb8` | 1/4 + region 128² centre | 3.423 | 4.508 | 2.517 | 0.56 | 0.74 | 6136 KB | 55 KB |
| `screen-rgb16` | region 512² top-left | 14.16 | 14.33 | 1.636 | 0.11 | 0.12 | 37998 KB | 1537 KB |
| `screen-rgb16` | region 512² centre | 15.46 | 15.16 | 3.102 | 0.20 | 0.20 | 37998 KB | 1537 KB |
| `screen-rgb16` | region 512² bottom-right | 15.01 | 15.31 | 4.436 | 0.29 | 0.30 | 37998 KB | 1537 KB |
| `screen-rgb16` | scale 1/2 | 13.46 | 20.20 | 12.95 | 0.64 | 0.96 | 39523 KB | 3061 KB |
| `screen-rgb16` | scale 1/4 | 12.88 | 18.03 | 9.908 | 0.55 | 0.77 | 37233 KB | 772 KB |
| `screen-rgb16` | scale 1/8 | 10.80 | 15.21 | 7.656 | 0.50 | 0.71 | 36658 KB | 196 KB |
| `screen-rgb16` | 1/4 + region 128² centre | 15.21 | 15.64 | 3.686 | 0.24 | 0.24 | 36562 KB | 100 KB |
| `screen-p8` | region 512² top-left | 3.421 | 3.328 | 0.916 | 0.28 | 0.27 | 11154 KB | 1027 KB |
| `screen-p8` | region 512² centre | 2.590 | 2.714 | 0.943 | 0.35 | 0.36 | 11154 KB | 1027 KB |
| `screen-p8` | region 512² bottom-right | 2.371 | 2.734 | 0.975 | 0.36 | 0.41 | 11154 KB | 1027 KB |
| `screen-p8` | scale 1/2 | 3.424 | 13.91 | 13.16 | 0.95 | 3.85 | 12185 KB | 2058 KB |
| `screen-p8` | scale 1/4 | 3.591 | 8.352 | 7.549 | 0.90 | 2.10 | 10666 KB | 539 KB |
| `screen-p8` | scale 1/8 | 2.401 | 5.596 | 3.897 | 0.70 | 1.62 | 10287 KB | 159 KB |
| `screen-p8` | 1/4 + region 128² centre | 2.958 | 2.954 | 1.255 | 0.42 | 0.42 | 10202 KB | 75 KB |
| `screen-rgba8` | region 512² top-left | 4.050 | 3.763 | 1.090 | 0.29 | 0.27 | 9133 KB | 1025 KB |
| `screen-rgba8` | region 512² centre | 3.136 | 3.787 | 2.316 | 0.61 | 0.74 | 9133 KB | 1025 KB |
| `screen-rgba8` | region 512² bottom-right | 3.941 | 4.196 | 3.151 | 0.75 | 0.80 | 9133 KB | 1025 KB |
| `screen-rgba8` | scale 1/2 | 2.984 | 10.04 | 8.670 | 0.86 | 2.91 | 10164 KB | 2056 KB |
| `screen-rgba8` | scale 1/4 | 3.900 | 9.079 | 7.170 | 0.79 | 1.84 | 8645 KB | 537 KB |
| `screen-rgba8` | scale 1/8 | 3.790 | 5.337 | 6.016 | 1.13 | 1.59 | 8265 KB | 157 KB |
| `screen-rgba8` | 1/4 + region 128² centre | 4.252 | 4.612 | 2.763 | 0.60 | 0.65 | 8181 KB | 73 KB |

The noisiest rows above (`screen-g8` scale 1/8 at 1.49×, `screen-rgba8` scale 1/8 at 1.13×) re-run with 21 rounds:

| Case | Mode | full decode | baseline | native | native ÷ baseline | native ÷ full decode | alloc baseline | alloc native |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `screen-g8` | region 512² top-left | 2.116 | 2.149 | 0.534 | 0.25 | 0.25 | 2284 KB | 257 KB |
| `screen-g8` | region 512² centre | 4.005 | 3.985 | 2.608 | 0.65 | 0.65 | 2284 KB | 257 KB |
| `screen-g8` | region 512² bottom-right | 3.972 | 3.565 | 3.896 | 1.09 | 0.98 | 2284 KB | 257 KB |
| `screen-g8` | scale 1/2 | 3.972 | 8.816 | 9.487 | 1.08 | 2.39 | 2542 KB | 515 KB |
| `screen-g8` | scale 1/4 | 3.925 | 5.737 | 5.744 | 1.00 | 1.46 | 2162 KB | 135 KB |
| `screen-g8` | scale 1/8 | 4.071 | 4.811 | 4.587 | 0.95 | 1.13 | 2067 KB | 40 KB |
| `screen-g8` | 1/4 + region 128² centre | 3.990 | 3.977 | 2.808 | 0.71 | 0.70 | 2046 KB | 19 KB |
| `screen-rgba8` | region 512² top-left | 3.800 | 3.723 | 1.132 | 0.30 | 0.30 | 9133 KB | 1025 KB |
| `screen-rgba8` | region 512² centre | 3.680 | 4.140 | 2.343 | 0.57 | 0.64 | 9133 KB | 1025 KB |
| `screen-rgba8` | region 512² bottom-right | 3.774 | 3.889 | 3.440 | 0.88 | 0.91 | 9133 KB | 1025 KB |
| `screen-rgba8` | scale 1/2 | 3.965 | 15.10 | 14.83 | 0.98 | 3.74 | 10164 KB | 2056 KB |
| `screen-rgba8` | scale 1/4 | 3.807 | 8.697 | 8.326 | 0.96 | 2.19 | 8645 KB | 537 KB |
| `screen-rgba8` | scale 1/8 | 3.640 | 6.402 | 5.212 | 0.81 | 1.43 | 8265 KB | 157 KB |
| `screen-rgba8` | 1/4 + region 128² centre | 2.836 | 3.153 | 1.835 | 0.58 | 0.65 | 8181 KB | 73 KB |

### Peak working set per region / scale decode, MiB (one fresh process per run, median of 3)

"first" is the working-set rise during the first full-size operation, after a warm-up on a 64×64 image of the same case and mode (JIT excluded). It includes the decoded output, which for the region modes is 1.00 MiB at most (512² RGBA); the 1920×1080 full decode's samples are 7.91 MiB for RGBA8.

| Case | Mode | full decode first | baseline first | native first |
|---|---|---:|---:|---:|
| `photo-rgb16` | region 512² top-left | 35.70 | 13.50 | 1.43 |
| `photo-rgb16` | region 512² centre | ″ | 13.50 | 1.43 |
| `photo-rgb16` | region 512² bottom-right | ″ | 13.50 | 1.43 |
| `photo-rgb16` | scale 1/2 | ″ | 14.99 | 2.89 |
| `photo-rgb16` | scale 1/4 | ″ | 12.76 | 0.67 |
| `photo-rgb16` | scale 1/8 | ″ | 12.19 | 0.05 |
| `photo-rgb16` | 1/4 + region 128² centre | ″ | 12.10 | 0.05 |
| `photo-rgba8` | region 512² top-left | 8.50 | 8.99 | 0.93 |
| `photo-rgba8` | region 512² centre | ″ | 9.00 | 0.93 |
| `photo-rgba8` | region 512² bottom-right | ″ | 9.00 | 0.93 |
| `photo-rgba8` | scale 1/2 | ″ | 9.96 | 1.91 |
| `photo-rgba8` | scale 1/4 | ″ | 8.48 | 0.42 |
| `photo-rgba8` | scale 1/8 | ″ | 8.09 | 0.05 |
| `photo-rgba8` | 1/4 + region 128² centre | ″ | 7.97 | 0.05 |
| `screen-p8` | region 512² top-left | 10.07 | 10.81 | 0.92 |
| `screen-p8` | region 512² centre | ″ | 10.81 | 0.92 |
| `screen-p8` | region 512² bottom-right | ″ | 10.82 | 0.92 |
| `screen-p8` | scale 1/2 | ″ | 11.80 | 1.91 |
| `screen-p8` | scale 1/4 | ″ | 10.32 | 0.43 |
| `screen-p8` | scale 1/8 | ″ | 9.95 | 0.06 |
| `screen-p8` | 1/4 + region 128² centre | ″ | 9.82 | 0.05 |

## Full-image decode: no regression

The full-image paths run the same code as before: `TryDecodeInto` without options only gained a branch it does not take. `current` against `baseline` below is `TryDecodeIntoRgba8` on both builds; the geometric mean is 1.01× on both content sets. Single cases deviate by up to 1.15× in either direction. That is noise: an A/A run with the baseline built from this branch's own commit showed the same spread (`screen-g4`: current 4.22 / baseline 3.83 ms in one run, 2.71 / 2.69 ms in the next).

### Decode to 8-bit RGBA (in-process medians of 11 interleaved rounds, ms)

`current` = `PngImageDecoder.TryDecodeIntoRgba8` into a caller buffer; `native` = `PngReader.Decode` (samples as stored, no RGBA expansion); ImageSharp = `Image.Load<Rgba32>` + `CopyPixelDataTo`, one thread; libpng = simplified API `png_image_finish_read` to RGBA. Ratios below 1 mean we are faster. Inputs are encoded by ImageSharp (adaptive filters, zlib level 6).

| Case | KB | current | native | baseline | ImageSharp | libpng | current MP/s | ÷ ImageSharp | ÷ libpng | ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-g4` | 427 | 5.817 | 5.793 | 5.563 | 8.419 | — | 356 | 0.69× | — | 1.05× |
| `photo-g8` | 1297 | 17.7 | 16.6 | 18.8 | 19.4 | — | 117 | 0.91× | — | 0.94× |
| `photo-rgb8` | 3309 | 25.5 | 26.4 | 26.5 | 30.7 | — | 81 | 0.83× | — | 0.96× |
| `photo-rgb16` | 10762 | 46.5 | 44.9 | 47.2 | 81.9 | — | 45 | 0.57× | — | 0.99× |
| `photo-p8` | 1406 | 9.266 | 8.467 | 9.232 | 14.8 | — | 224 | 0.63× | — | 1.00× |
| `photo-rgba8` | 3809 | 39.0 | 39.6 | 34.1 | 35.1 | — | 53 | 1.11× | — | 1.15× |
| `screen-g4` | 45 | 2.035 | 1.703 | 1.974 | 6.087 | — | 1019 | 0.33× | — | 1.03× |
| `screen-g8` | 59 | 4.236 | 3.982 | 4.302 | 5.052 | — | 489 | 0.84× | — | 0.98× |
| `screen-rgb8` | 81 | 3.991 | 3.870 | 4.368 | 7.242 | — | 520 | 0.55× | — | 0.91× |
| `screen-rgb16` | 97 | 6.080 | 5.684 | 5.279 | 25.6 | — | 341 | 0.24× | — | 1.15× |
| `screen-p8` | 84 | 2.508 | 1.301 | 2.460 | 10.5 | — | 827 | 0.24× | — | 1.02× |
| `screen-rgba8` | 85 | 2.750 | 3.062 | 2.885 | 3.299 | — | 754 | 0.83× | — | 0.95× |

Geometric means of current ÷ competitor over the cases both decode:

- vs ImageSharp, photo: 0.77× (6 cases)
- vs ImageSharp, screen: 0.44× (6 cases)
- vs baseline, photo: 1.01× (6 cases)
- vs baseline, screen: 1.01× (6 cases)

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool and libpng allocates natively, so neither shows here: compare working sets with `peakmem`.

| Case | current | native | baseline | ImageSharp | libpng |
|---|---:|---:|---:|---:|---:|
| `photo-g4` | 1.8 | 2027.8 | 1.8 | 105.5 | — |
| `photo-g8` | 1.2 | 2028.2 | 1.2 | 5.4 | — |
| `photo-rgb8` | 1.7 | 6082.5 | 1.7 | 8.3 | — |
| `photo-rgb16` | 4.8 | 12166.2 | 4.8 | 19.3 | — |
| `photo-p8` | 3.0 | 2029.0 | 3.0 | 10.4 | — |
| `photo-rgba8` | 9.2 | 8109.4 | 9.2 | 9.1 | — |
| `screen-g4` | 1.7 | 2027.7 | 1.7 | 104.8 | — |
| `screen-g8` | 0.7 | 2027.7 | 0.7 | 3.5 | — |
| `screen-rgb8` | 0.7 | 6081.5 | 0.7 | 3.6 | — |
| `screen-rgb16` | 0.7 | 12162.1 | 0.7 | 3.6 | — |
| `screen-p8` | 2.5 | 2028.5 | 2.5 | 8.5 | — |
| `screen-rgba8` | 8.2 | 8108.3 | 8.2 | 3.6 | — |
