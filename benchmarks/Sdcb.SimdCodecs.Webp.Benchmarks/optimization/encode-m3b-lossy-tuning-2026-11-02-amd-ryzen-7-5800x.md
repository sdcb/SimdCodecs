# WebP lossy encoder, high-rate tuning — 2026-11-02, AMD Ryzen 7 5800X

Starting point: [`encode-m3-lossy-2026-11-02-amd-ryzen-7-5800x.md`](encode-m3-lossy-2026-11-02-amd-ryzen-7-5800x.md),
BD-rate against libwebp (method 4) PSNR-Y −0.92%, SSIM-Y −1.49%, weak at high rate. Tuned with the `tune`
command (12 opaque inputs, max dimension 1024; BD-rate over the whole sweep, over q ≤ 60 and over q ≥ 70).

What moved the numbers, each against the old default (mean BD-rate, PSNR-Y / SSIM-Y):

| change | all | q ≤ 60 | q ≥ 70 |
|---|---:|---:|---:|
| old default | −0.92% / −1.49% | −7.23% / −0.52% | +2.85% / −2.42% |
| one segment (no adaptive quantizer) | −8.86% / −0.92% | −10.74% / −0.35% | −8.63% / −1.76% |
| segment spread in proportion to (index + 8), 12 steps at index 40 | −5.32% / −1.35% | −7.34% / −0.62% | −4.36% / −2.25% |
| … spread 16 | −4.82% / −1.71% | −6.75% / −1.13% | −3.86% / −2.60% |
| … + λ 7 (all three) | −5.60% / −2.72% | −7.58% / −2.15% | −4.60% / −3.65% |
| … + λ 7 for I16 only | −1.48% / +1.61% | −2.16% / +3.24% | −1.20% / +0.10% |
| … + λ 4, AC rounding 95 | −6.41% / −4.35% | −8.23% / −3.55% | −5.34% / −5.37% |
| **new default**: spread 18, λ 4, rounding DC 100 / AC 95 | **−6.22% / −4.77%** | −8.06% / −4.18% | −5.43% / −5.54% |

- The fixed segment spread (12 index steps whatever the base index) was the high-rate loss: near index
  10 it more than doubles the busiest segment's step size, which costs PSNR with nothing gained in SSIM.
  Scaling it with the index removes it; the extra spread (18) buys SSIM back at low rate.
- λ must move for all three mode decisions together: λ for the 16×16 modes alone against the 4×4
  modes' tilts the I16 / B_PRED choice and loses 3–4%.
- No gain (within ±0.2%): loop filter strength 0.3–0.5 × index, a filter level per segment, sharpness,
  chroma λ apart from luma. They were tried and left out.
- Same settings at max dimension 2048 (other resolutions of the same photos): old default +0.03% / −1.23%,
  new −5.37% / −4.87%.
- The quality table was recalibrated (`calibrate`) so that each quality again writes about libwebp's bytes.

The gate run with the new defaults, `encode --mode lossy --reps 3 --gate`: every input has a negative
BD-rate on both metrics; at q90, 1.5% more bytes at +0.34 dB PSNR-Y; q75 takes 0.93x libwebp's time.

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
| lossless | 243.9 ms | 249.0 ms | 272.0 ms | no (791238 vs 791294) |
| lossy q75 | 45.1 ms | 43.0 ms | 89.0 ms | yes |

### lossy q50

| encoder | MB | bpp | size vs libwebp | PSNR-Y | PSNR-RGB | SSIM-Y | MP/s | time vs libwebp |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| libwebp q50 | 0.408 | 0.421 | 1.000x | 37.211 | 34.417 | 0.9423 | 18.39 | 1.00x |
| cwebp -q 50 -m 0 | 0.538 | 0.554 | 1.318x | 37.544 | 34.618 | 0.9430 | 84.41 | 0.22x |
| cwebp -q 50 -m 4 | 0.408 | 0.421 | 1.000x | 37.211 | 34.417 | 0.9423 | 19.66 | 0.94x |
| cwebp -q 50 -m 6 | 0.376 | 0.388 | 0.922x | 36.863 | 34.235 | 0.9381 | 10.65 | 1.73x |
| ImageSharp q50 | 0.397 | 0.409 | 0.973x | 37.161 | 34.370 | 0.9408 | 4.99 | 3.68x |
| ours q50 | 0.409 | 0.421 | 1.001x | 37.616 | 34.516 | 0.9441 | 20.14 | 0.91x |

Bytes per input:

| input | libwebp q50 | cwebp -q 50 -m 0 | cwebp -q 50 -m 4 | cwebp -q 50 -m 6 | ImageSharp q50 | ours q50 |
|---|---:|---:|---:|---:|---:|---:|
| photo-architecture-teatro | 62510 | 83468 | 62510 | 58064 | 61022 | 62930 |
| photo-foliage-mangrove | 34792 | 46586 | 34792 | 32146 | 33606 | 34610 |
| photo-landscape-almsee | 62168 | 77742 | 62168 | 57254 | 59994 | 63428 |
| photo-night-alexanderplatz | 37968 | 56604 | 37968 | 34912 | 36982 | 38150 |
| photo-night-brussels | 8086 | 11964 | 8086 | 7354 | 7986 | 8480 |
| photo-panorama-lakeview | 43962 | 62796 | 43962 | 39730 | 42356 | 46500 |
| photo-portrait-carter | 36024 | 49580 | 36024 | 34856 | 35428 | 36450 |
| photo-sea-ibiza | 14550 | 21516 | 14550 | 13566 | 14344 | 13492 |
| photo-sunrise-fuji | 32246 | 42478 | 32246 | 29958 | 31422 | 32086 |
| photo-texture-ground | 62958 | 60728 | 62958 | 55812 | 61034 | 60448 |
| screen | 6262 | 10558 | 6262 | 6220 | 6336 | 5636 |
| palette | 6742 | 14188 | 6742 | 6552 | 6868 | 6320 |

### lossy q75

| encoder | MB | bpp | size vs libwebp | PSNR-Y | PSNR-RGB | SSIM-Y | MP/s | time vs libwebp |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| libwebp q75 | 0.583 | 0.600 | 1.000x | 39.194 | 35.879 | 0.9616 | 17.39 | 1.00x |
| cwebp -q 75 -m 0 | 0.743 | 0.766 | 1.275x | 39.467 | 36.116 | 0.9610 | 75.40 | 0.23x |
| cwebp -q 75 -m 4 | 0.583 | 0.600 | 1.000x | 39.194 | 35.879 | 0.9616 | 18.36 | 0.95x |
| cwebp -q 75 -m 6 | 0.546 | 0.562 | 0.936x | 38.948 | 35.802 | 0.9588 | 9.05 | 1.92x |
| ImageSharp q75 | 0.572 | 0.590 | 0.982x | 39.133 | 35.859 | 0.9609 | 4.69 | 3.71x |
| ours q75 | 0.581 | 0.598 | 0.996x | 39.576 | 35.985 | 0.9628 | 18.63 | 0.93x |

Bytes per input:

| input | libwebp q75 | cwebp -q 75 -m 0 | cwebp -q 75 -m 4 | cwebp -q 75 -m 6 | ImageSharp q75 | ours q75 |
|---|---:|---:|---:|---:|---:|---:|
| photo-architecture-teatro | 85452 | 109326 | 85452 | 81206 | 84374 | 85460 |
| photo-foliage-mangrove | 51894 | 69008 | 51894 | 47474 | 50722 | 49962 |
| photo-landscape-almsee | 89102 | 107774 | 89102 | 83236 | 86778 | 89284 |
| photo-night-alexanderplatz | 56128 | 76666 | 56128 | 51912 | 54780 | 55998 |
| photo-night-brussels | 11894 | 16238 | 11894 | 10670 | 11646 | 12092 |
| photo-panorama-lakeview | 59222 | 82090 | 59222 | 55396 | 58238 | 61730 |
| photo-portrait-carter | 49934 | 64860 | 49934 | 48408 | 49414 | 49962 |
| photo-sea-ibiza | 20078 | 30492 | 20078 | 18712 | 19830 | 18344 |
| photo-sunrise-fuji | 44324 | 58004 | 44324 | 41626 | 43886 | 44122 |
| photo-texture-ground | 100588 | 100776 | 100588 | 92924 | 98450 | 100418 |
| screen | 6920 | 12142 | 6920 | 6912 | 7040 | 6258 |
| palette | 7234 | 15924 | 7234 | 7166 | 7162 | 7050 |

### lossy q90

| encoder | MB | bpp | size vs libwebp | PSNR-Y | PSNR-RGB | SSIM-Y | MP/s | time vs libwebp |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| libwebp q90 | 1.229 | 1.266 | 1.000x | 44.389 | 39.195 | 0.9865 | 14.39 | 1.00x |
| cwebp -q 90 -m 0 | 1.393 | 1.435 | 1.133x | 44.194 | 39.160 | 0.9847 | 43.14 | 0.33x |
| cwebp -q 90 -m 4 | 1.229 | 1.266 | 1.000x | 44.389 | 39.195 | 0.9865 | 15.14 | 0.95x |
| cwebp -q 90 -m 6 | 1.189 | 1.225 | 0.967x | 44.177 | 39.088 | 0.9856 | 6.47 | 2.23x |
| ImageSharp q90 | 1.241 | 1.279 | 1.010x | 44.409 | 39.213 | 0.9866 | 4.18 | 3.44x |
| ours q90 | 1.248 | 1.285 | 1.015x | 44.729 | 39.141 | 0.9875 | 14.11 | 1.02x |

Bytes per input:

| input | libwebp q90 | cwebp -q 90 -m 0 | cwebp -q 90 -m 4 | cwebp -q 90 -m 6 | ImageSharp q90 | ours q90 |
|---|---:|---:|---:|---:|---:|---:|
| photo-architecture-teatro | 158622 | 180854 | 158622 | 155816 | 159062 | 163090 |
| photo-foliage-mangrove | 118882 | 144452 | 118882 | 111582 | 120826 | 120606 |
| photo-landscape-almsee | 188326 | 204398 | 188326 | 182816 | 187266 | 186298 |
| photo-night-alexanderplatz | 124664 | 140062 | 124664 | 120414 | 126510 | 127170 |
| photo-night-brussels | 43040 | 40980 | 43040 | 36604 | 43112 | 46076 |
| photo-panorama-lakeview | 108824 | 137092 | 108824 | 107884 | 109272 | 111922 |
| photo-portrait-carter | 109406 | 115248 | 109406 | 105162 | 109188 | 113078 |
| photo-sea-ibiza | 43136 | 67718 | 43136 | 40946 | 43602 | 38366 |
| photo-sunrise-fuji | 91128 | 106090 | 91128 | 89438 | 92046 | 89564 |
| photo-texture-ground | 224862 | 223596 | 224862 | 220062 | 232038 | 234390 |
| screen | 8922 | 16318 | 8922 | 8848 | 9018 | 8072 |
| palette | 9346 | 16244 | 9346 | 9274 | 9372 | 8976 |

### lossy: rate-distortion (BD-rate vs libwebp)

| encoder | BD-rate (PSNR-Y) | BD-rate (SSIM-Y) | bpp range |
|---|---:|---:|---|
| libwebp | +0.00% | +0.00% | 0.07–3.84 |
| cwebp -m 6 | -1.37% | -0.10% | 0.06–3.81 |
| ImageSharp | +0.21% | +0.68% | 0.06–4.03 |
| ours | -6.22% | -4.77% | 0.05–4.28 |

BD-rate per input (PSNR-Y / SSIM-Y):

| encoder | photo-architecture-teatro | photo-foliage-mangrove | photo-landscape-almsee | photo-night-alexanderplatz | photo-night-brussels | photo-panorama-lakeview | photo-portrait-carter | photo-sea-ibiza | photo-sunrise-fuji | photo-texture-ground | screen | palette |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| libwebp | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% |
| cwebp -m 6 | -0.86% / +0.62% | -2.37% / -1.51% | -1.91% / -0.80% | -1.97% / -0.60% | -4.48% / -3.31% | -1.69% / -0.55% | -1.99% / -2.21% | +0.46% / +5.19% | -1.39% / +0.07% | -1.71% / -1.50% | +1.43% / +1.53% | +0.01% / +1.87% |
| ImageSharp | +0.46% / +0.85% | -0.14% / +0.06% | -0.60% / -0.48% | +0.03% / +0.17% | +0.20% / +0.31% | -0.33% / -0.03% | -0.66% / -0.59% | +0.03% / +0.20% | +0.02% / -0.14% | +1.56% / +1.86% | +0.49% / +1.61% | +1.45% / +4.32% |
| ours | -4.30% / -2.94% | -6.19% / -3.61% | -5.91% / -1.72% | -4.86% / -2.53% | -5.35% / -3.67% | -5.46% / -1.22% | -3.82% / -5.89% | -8.25% / -8.50% | -6.39% / -5.53% | -4.38% / -2.09% | -10.30% / -13.26% | -9.48% / -6.35% |

[gate] lossy q75 ours 0.417 s vs libwebp 0.447 s (0.93x): ok

[gate] lossy BD-rate vs libwebp: PSNR-Y -6.22%, SSIM-Y -4.77%: ok
