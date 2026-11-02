# WebP lossy encoder (M3) — 2026-11-02, AMD Ryzen 7 5800X

The first lossy (VP8) encoder, at its default effort (4), against libwebp 1.6.0's default (method 4),
cwebp's methods and ImageSharp. Command: `encode --mode lossy --reps 3 --gate`.

The gate passes: BD-rate against libwebp is PSNR-Y −0.92% and SSIM-Y −1.49% (mean of the 12 opaque
inputs), and at q75 our encoder takes 0.95x libwebp's time. The weak end is high rate: at q90 our files
are 1.3% larger with 0.7 dB lower PSNR-Y; below q75 they are better.

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
| lossless | 248.6 ms | 249.0 ms | 270.0 ms | no (791238 vs 791294) |
| lossy q75 | 44.9 ms | 44.0 ms | 89.0 ms | yes |

### lossy q50

| encoder | MB | bpp | size vs libwebp | PSNR-Y | PSNR-RGB | SSIM-Y | MP/s | time vs libwebp |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| libwebp q50 | 0.408 | 0.421 | 1.000x | 37.211 | 34.417 | 0.9423 | 17.54 | 1.00x |
| cwebp -q 50 -m 0 | 0.538 | 0.554 | 1.318x | 37.544 | 34.618 | 0.9430 | 83.51 | 0.21x |
| cwebp -q 50 -m 4 | 0.408 | 0.421 | 1.000x | 37.211 | 34.417 | 0.9423 | 19.76 | 0.89x |
| cwebp -q 50 -m 6 | 0.376 | 0.388 | 0.922x | 36.863 | 34.235 | 0.9381 | 10.68 | 1.64x |
| ImageSharp q50 | 0.397 | 0.409 | 0.973x | 37.161 | 34.370 | 0.9408 | 4.98 | 3.52x |
| ours q50 | 0.413 | 0.426 | 1.012x | 37.704 | 34.652 | 0.9425 | 20.26 | 0.87x |

Bytes per input:

| input | libwebp q50 | cwebp -q 50 -m 0 | cwebp -q 50 -m 4 | cwebp -q 50 -m 6 | ImageSharp q50 | ours q50 |
|---|---:|---:|---:|---:|---:|---:|
| photo-architecture-teatro | 62510 | 83468 | 62510 | 58064 | 61022 | 63858 |
| photo-foliage-mangrove | 34792 | 46586 | 34792 | 32146 | 33606 | 34876 |
| photo-landscape-almsee | 62168 | 77742 | 62168 | 57254 | 59994 | 63830 |
| photo-night-alexanderplatz | 37968 | 56604 | 37968 | 34912 | 36982 | 38688 |
| photo-night-brussels | 8086 | 11964 | 8086 | 7354 | 7986 | 8594 |
| photo-panorama-lakeview | 43962 | 62796 | 43962 | 39730 | 42356 | 46656 |
| photo-portrait-carter | 36024 | 49580 | 36024 | 34856 | 35428 | 37298 |
| photo-sea-ibiza | 14550 | 21516 | 14550 | 13566 | 14344 | 14024 |
| photo-sunrise-fuji | 32246 | 42478 | 32246 | 29958 | 31422 | 33246 |
| photo-texture-ground | 62958 | 60728 | 62958 | 55812 | 61034 | 59422 |
| screen | 6262 | 10558 | 6262 | 6220 | 6336 | 5930 |
| palette | 6742 | 14188 | 6742 | 6552 | 6868 | 6664 |

### lossy q75

| encoder | MB | bpp | size vs libwebp | PSNR-Y | PSNR-RGB | SSIM-Y | MP/s | time vs libwebp |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| libwebp q75 | 0.583 | 0.600 | 1.000x | 39.194 | 35.879 | 0.9616 | 17.67 | 1.00x |
| cwebp -q 75 -m 0 | 0.743 | 0.766 | 1.275x | 39.467 | 36.116 | 0.9610 | 76.14 | 0.23x |
| cwebp -q 75 -m 4 | 0.583 | 0.600 | 1.000x | 39.194 | 35.879 | 0.9616 | 18.36 | 0.96x |
| cwebp -q 75 -m 6 | 0.546 | 0.562 | 0.936x | 38.948 | 35.802 | 0.9588 | 9.12 | 1.94x |
| ImageSharp q75 | 0.572 | 0.590 | 0.982x | 39.133 | 35.859 | 0.9609 | 4.89 | 3.61x |
| ours q75 | 0.584 | 0.602 | 1.003x | 39.504 | 35.956 | 0.9618 | 18.54 | 0.95x |

Bytes per input:

| input | libwebp q75 | cwebp -q 75 -m 0 | cwebp -q 75 -m 4 | cwebp -q 75 -m 6 | ImageSharp q75 | ours q75 |
|---|---:|---:|---:|---:|---:|---:|
| photo-architecture-teatro | 85452 | 109326 | 85452 | 81206 | 84374 | 84712 |
| photo-foliage-mangrove | 51894 | 69008 | 51894 | 47474 | 50722 | 50470 |
| photo-landscape-almsee | 89102 | 107774 | 89102 | 83236 | 86778 | 88970 |
| photo-night-alexanderplatz | 56128 | 76666 | 56128 | 51912 | 54780 | 55272 |
| photo-night-brussels | 11894 | 16238 | 11894 | 10670 | 11646 | 12130 |
| photo-panorama-lakeview | 59222 | 82090 | 59222 | 55396 | 58238 | 62338 |
| photo-portrait-carter | 49934 | 64860 | 49934 | 48408 | 49414 | 51010 |
| photo-sea-ibiza | 20078 | 30492 | 20078 | 18712 | 19830 | 18738 |
| photo-sunrise-fuji | 44324 | 58004 | 44324 | 41626 | 43886 | 44774 |
| photo-texture-ground | 100588 | 100776 | 100588 | 92924 | 98450 | 102526 |
| screen | 6920 | 12142 | 6920 | 6912 | 7040 | 6462 |
| palette | 7234 | 15924 | 7234 | 7166 | 7162 | 7020 |

### lossy q90

| encoder | MB | bpp | size vs libwebp | PSNR-Y | PSNR-RGB | SSIM-Y | MP/s | time vs libwebp |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| libwebp q90 | 1.229 | 1.266 | 1.000x | 44.389 | 39.195 | 0.9865 | 14.72 | 1.00x |
| cwebp -q 90 -m 0 | 1.393 | 1.435 | 1.133x | 44.194 | 39.160 | 0.9847 | 43.14 | 0.34x |
| cwebp -q 90 -m 4 | 1.229 | 1.266 | 1.000x | 44.389 | 39.195 | 0.9865 | 15.20 | 0.97x |
| cwebp -q 90 -m 6 | 1.189 | 1.225 | 0.967x | 44.177 | 39.088 | 0.9856 | 6.48 | 2.27x |
| ImageSharp q90 | 1.241 | 1.279 | 1.010x | 44.409 | 39.213 | 0.9866 | 4.30 | 3.42x |
| ours q90 | 1.245 | 1.282 | 1.013x | 43.677 | 38.643 | 0.9864 | 14.38 | 1.02x |

Bytes per input:

| input | libwebp q90 | cwebp -q 90 -m 0 | cwebp -q 90 -m 4 | cwebp -q 90 -m 6 | ImageSharp q90 | ours q90 |
|---|---:|---:|---:|---:|---:|---:|
| photo-architecture-teatro | 158622 | 180854 | 158622 | 155816 | 159062 | 153596 |
| photo-foliage-mangrove | 118882 | 144452 | 118882 | 111582 | 120826 | 114712 |
| photo-landscape-almsee | 188326 | 204398 | 188326 | 182816 | 187266 | 185632 |
| photo-night-alexanderplatz | 124664 | 140062 | 124664 | 120414 | 126510 | 126138 |
| photo-night-brussels | 43040 | 40980 | 43040 | 36604 | 43112 | 50034 |
| photo-panorama-lakeview | 108824 | 137092 | 108824 | 107884 | 109272 | 107194 |
| photo-portrait-carter | 109406 | 115248 | 109406 | 105162 | 109188 | 113296 |
| photo-sea-ibiza | 43136 | 67718 | 43136 | 40946 | 43602 | 35210 |
| photo-sunrise-fuji | 91128 | 106090 | 91128 | 89438 | 92046 | 82858 |
| photo-texture-ground | 224862 | 223596 | 224862 | 220062 | 232038 | 259674 |
| screen | 8922 | 16318 | 8922 | 8848 | 9018 | 7740 |
| palette | 9346 | 16244 | 9346 | 9274 | 9372 | 8804 |

### lossy: rate-distortion (BD-rate vs libwebp)

| encoder | BD-rate (PSNR-Y) | BD-rate (SSIM-Y) | bpp range |
|---|---:|---:|---|
| libwebp | +0.00% | +0.00% | 0.07–3.84 |
| cwebp -m 6 | -1.37% | -0.10% | 0.06–3.81 |
| ImageSharp | +0.21% | +0.68% | 0.06–4.03 |
| ours | -0.92% | -1.49% | 0.06–4.33 |

BD-rate per input (PSNR-Y / SSIM-Y):

| encoder | photo-architecture-teatro | photo-foliage-mangrove | photo-landscape-almsee | photo-night-alexanderplatz | photo-night-brussels | photo-panorama-lakeview | photo-portrait-carter | photo-sea-ibiza | photo-sunrise-fuji | photo-texture-ground | screen | palette |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| libwebp | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% |
| cwebp -m 6 | -0.86% / +0.62% | -2.37% / -1.51% | -1.91% / -0.80% | -1.97% / -0.60% | -4.48% / -3.31% | -1.69% / -0.55% | -1.99% / -2.21% | +0.46% / +5.19% | -1.39% / +0.07% | -1.71% / -1.50% | +1.43% / +1.53% | +0.01% / +1.87% |
| ImageSharp | +0.46% / +0.85% | -0.14% / +0.06% | -0.60% / -0.48% | +0.03% / +0.17% | +0.20% / +0.31% | -0.33% / -0.03% | -0.66% / -0.59% | +0.03% / +0.20% | +0.02% / -0.14% | +1.56% / +1.86% | +0.49% / +1.61% | +1.45% / +4.32% |
| ours | -1.75% / -0.47% | -0.88% / +0.82% | -1.13% / +1.68% | -0.31% / +0.74% | +4.08% / -1.89% | -2.53% / +1.69% | +5.98% / -0.43% | -1.75% / -5.75% | -0.51% / -1.65% | +1.68% / +3.83% | -6.83% / -10.42% | -7.08% / -6.07% |

[gate] lossy q75 ours 0.419 s vs libwebp 0.440 s (0.95x): ok

[gate] lossy BD-rate vs libwebp: PSNR-Y -0.92%, SSIM-Y -1.49%: ok
