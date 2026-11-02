# WebP lossy + alpha encoder (M4) — 2026-11-02, AMD Ryzen 7 5800X

`WebpEncoder.EncodeLossy` on images with transparency: VP8 for the colour, the alpha plane in an ALPH
chunk as a VP8L image stream (lossless, effort 4 at the default lossy effort; no filter, all four at
effort 6; raw when that is smaller), and the RGB under alpha 0 replaced by each macroblock's mean
visible colour. Command: `encode --mode lossy-alpha --gate` (also part of `encode --mode all --gate`).

The gate passes: BD-rate on visible PSNR-RGB against libwebp's default is −4.06% (photo-alpha −6.93%,
screen-alpha −6.06%, sticker +0.80%), every alpha value exact.

What `tune --alpha` showed along the way (q75, ALPH bytes; libwebp 42713 / 374 / 8321):

| alpha plane | photo-alpha | screen-alpha | sticker |
|---|---:|---:|---:|
| first version: filter by entropy estimate (picked horizontal), effort 4 | 54791 | 530 | 9139 |
| no filter, effort 4 (default) | 34450 | 413 | 8601 |
| no filter, effort 3 | 32831 | 412 | 9179 |
| no filter, effort 9 | 26334 | 435 | 8797 |
| best of the four filters, effort 5 | 32656 | 428 | 8527 |

- The lossless stream's own predictors beat every ALPH filter on all three planes; an estimate that
  ranked the filters by residual entropy chose badly, so it was dropped. Filters are tried at effort 6.
- Subtract green and cross colour are not searched for the plane (only green varies): about 10% less time.
- Diffusing colour into transparent pixels (4 or 16 passes of neighbour averaging after the block
  mean) made BD-rate worse (−2.1% / −1.7%) and was left out.
- Speed is the weak point, single encodes at q75: photo-alpha 249 ms vs libwebp 165 ms, screen-alpha
  118 vs 32 ms, sticker 139 vs 76 ms. The VP8L encode of the plane takes most of it.

- OS: Microsoft Windows 10.0.26200 (X64)
- .NET: .NET 10.0.11 (win-x64), process X64
- Logical processors: 16
- Vector128.IsHardwareAccelerated: True, Vector256.IsHardwareAccelerated: True, Vector512.IsHardwareAccelerated: False
- AVX2: True, AVX-512F: False, BMI2: True
- libwebp: (default search)
- libwebp encoder version: 1.6.0; cwebp: 1.6.0 (C:\Users\sdfly\source\repos\SimdCodecs\benchmarks\Sdcb.SimdCodecs.Webp.Benchmarks\.cache\release\libwebp-1.6.0-windows-x64\bin\cwebp.exe)
- inputs: 14 images, 9.10 MP in all, max dimension 1024; median of 3 encodes

| input | kind | size | MP |
|---|---|---|---:|
| photo-architecture-teatro | photo | 1024×658 | 0.67 |
| photo-foliage-mangrove | photo | 1024×768 | 0.79 |
| photo-landscape-almsee | photo | 1024×768 | 0.79 |
| photo-night-alexanderplatz | photo | 1024×683 | 0.70 |
| photo-night-brussels | photo | 1024×428 | 0.44 |
| photo-panorama-lakeview | photo | 1024×354 | 0.36 |
| photo-portrait-carter | photo | 873×1024 | 0.89 |
| photo-sea-ibiza | photo | 1024×576 | 0.59 |
| photo-sunrise-fuji | photo | 1024×576 | 0.59 |
| photo-texture-ground | photo | 1024×620 | 0.63 |
| photo-alpha | photo+alpha | 1024×658 | 0.67 |
| screen | screen | 1024×640 | 0.66 |
| screen-alpha | screen+alpha | 1024×640 | 0.66 |
| palette | palette | 1024×640 | 0.66 |

SIMD check on photo-architecture-teatro (median of 3; in-process libwebp should match cwebp, not cwebp -noasm):

| setting | libwebp (in process) | cwebp | cwebp -noasm | same bytes as cwebp |
|---|---:|---:|---:|---|
| lossless | 276.6 ms | 258.0 ms | 287.0 ms | no (791238 vs 791294) |
| lossy q75 | 48.5 ms | 44.0 ms | 92.0 ms | yes |

### lossy-alpha: rate-distortion (BD-rate vs libwebp)

| encoder | BD-rate (visible PSNR-RGB) | alpha exact | bpp range |
|---|---:|---|---|
| libwebp | +0.00% | yes | 0.07–3.10 |
| ours | -4.06% | yes | 0.06–3.09 |

BD-rate per input (visible PSNR-RGB):

| encoder | photo-alpha | screen-alpha | sticker |
|---|---:|---:|---:|
| libwebp | +0.00% | +0.00% | +0.00% |
| ours | -6.93% | -6.06% | +0.80% |

[gate] lossy-alpha BD-rate vs libwebp: -4.06%, alpha exact: True: ok
