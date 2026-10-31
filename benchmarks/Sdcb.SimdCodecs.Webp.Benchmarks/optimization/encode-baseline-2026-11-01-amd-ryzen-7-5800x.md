# WebP encode baseline — 2026-11-01, AMD Ryzen 7 5800X

Where WebP encoding starts: the competitors only, before this repository had an encoder. libwebp is
the SIMD build from `build-libwebp.sh` (in process), cwebp the official 1.6.0 Windows release, ImageSharp
3.1.12. Re-measure A and B back to back before claiming anything (see `AGENTS.md`).

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks -- encode --mode all
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks -- encode-peakmem --mode lossless --images screen
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks -- encode-peakmem --mode lossless --images photo-sea
```

Reading it: libwebp's simple lossless API is cwebp's default `-z 6` effort (sizes within 0.1%; cwebp's
self-reported time leaves out the RGBA → ARGB import that the in-process call pays). ImageSharp lands
on libwebp's sizes to within 0.1% at 0.8× its speed. Lossless photos cost about 6.8 bpp; the synthetic
screen and palette images are where effort levels matter most (`-z 9` is 42% smaller on `screen`).
libwebp's lossless encoder holds 32 MiB (0.66 MP screen) to 53 MiB (0.59 MP photo) of working memory.

- CPU: AMD Ryzen 7 5800X 8-Core Processor
- OS: Microsoft Windows 10.0.26200 (X64)
- .NET: .NET 10.0.11 (win-x64), process X64
- Logical processors: 16
- Vector128.IsHardwareAccelerated: True, Vector256.IsHardwareAccelerated: True, Vector512.IsHardwareAccelerated: False
- AVX2: True, AVX-512F: False, BMI2: True
- libwebp: (default search)
- libwebp encoder version: 1.6.0; cwebp: 1.6.0 (C:\Users\sdfly\source\repos\SimdCodecs\benchmarks\Sdcb.SimdCodecs.Webp.Benchmarks\.cache\release\libwebp-1.6.0-windows-x64\bin\cwebp.exe)
- inputs: 14 images, 9.10 MP in all, max dimension 1024; median of 5 encodes

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

SIMD check on photo-architecture-teatro (median of 5; in-process libwebp should match cwebp, not cwebp -noasm):

| setting | libwebp (in process) | cwebp | cwebp -noasm | same bytes as cwebp |
|---|---:|---:|---:|---|
| lossless | 325.0 ms | 256.0 ms | 279.0 ms | no (791238 vs 791294) |
| lossy q75 | 64.2 ms | 44.0 ms | 92.0 ms | yes |

### lossless

| encoder | MB | bpp | size vs libwebp | MP/s | time vs libwebp | lossless |
|---|---:|---:|---:|---:|---:|---|
| libwebp | 7.773 | 6.837 | 1.000x | 1.88 | 1.00x | visible |
| cwebp -z 0 | 8.885 | 7.815 | 1.143x | 32.60 | 0.06x | visible |
| cwebp -z 3 | 7.814 | 6.873 | 1.005x | 3.52 | 0.53x | visible |
| cwebp -z 6 | 7.773 | 6.837 | 1.000x | 2.63 | 0.71x | visible |
| cwebp -z 9 | 7.558 | 6.648 | 0.972x | 0.11 | 17.75x | visible |
| ImageSharp | 7.772 | 6.836 | 1.000x | 1.49 | 1.26x | visible |

Bytes per input:

| input | libwebp | cwebp -z 0 | cwebp -z 3 | cwebp -z 6 | cwebp -z 9 | ImageSharp |
|---|---:|---:|---:|---:|---:|---:|
| photo-architecture-teatro | 791238 | 871806 | 791950 | 791294 | 768812 | 791568 |
| photo-foliage-mangrove | 704308 | 755784 | 715392 | 705126 | 690106 | 703976 |
| photo-landscape-almsee | 941230 | 1035750 | 955340 | 941452 | 909530 | 941102 |
| photo-night-alexanderplatz | 696196 | 774758 | 701080 | 695842 | 705184 | 695816 |
| photo-night-brussels | 195670 | 275094 | 197920 | 195670 | 192726 | 195422 |
| photo-panorama-lakeview | 459794 | 508586 | 460916 | 459734 | 446980 | 459856 |
| photo-portrait-carter | 951194 | 1032280 | 954396 | 951274 | 910660 | 950640 |
| photo-sea-ibiza | 449038 | 552552 | 449768 | 449272 | 444572 | 449874 |
| photo-sunrise-fuji | 603984 | 694438 | 604614 | 603752 | 585130 | 603828 |
| photo-texture-ground | 1108782 | 1124976 | 1108636 | 1108866 | 1057366 | 1108898 |
| photo-alpha | 863424 | 964242 | 865618 | 862984 | 841870 | 863188 |
| screen | 3664 | 141648 | 3656 | 3664 | 2116 | 3598 |
| screen-alpha | 3658 | 150716 | 3660 | 3654 | 2192 | 3598 |
| palette | 910 | 2238 | 930 | 908 | 902 | 908 |

### lossy q50

| encoder | MB | bpp | size vs libwebp | PSNR-Y | PSNR-RGB | SSIM-Y | MP/s | time vs libwebp |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| libwebp q50 | 0.408 | 0.421 | 1.000x | 37.211 | 34.417 | 0.9423 | 15.14 | 1.00x |
| cwebp -q 50 -m 0 | 0.538 | 0.554 | 1.318x | 37.544 | 34.618 | 0.9430 | 80.90 | 0.19x |
| cwebp -q 50 -m 4 | 0.408 | 0.421 | 1.000x | 37.211 | 34.417 | 0.9423 | 19.42 | 0.78x |
| cwebp -q 50 -m 6 | 0.376 | 0.388 | 0.922x | 36.863 | 34.235 | 0.9381 | 10.45 | 1.45x |
| ImageSharp q50 | 0.397 | 0.409 | 0.973x | 37.161 | 34.370 | 0.9408 | 3.99 | 3.80x |

Bytes per input:

| input | libwebp q50 | cwebp -q 50 -m 0 | cwebp -q 50 -m 4 | cwebp -q 50 -m 6 | ImageSharp q50 |
|---|---:|---:|---:|---:|---:|
| photo-architecture-teatro | 62510 | 83468 | 62510 | 58064 | 61022 |
| photo-foliage-mangrove | 34792 | 46586 | 34792 | 32146 | 33606 |
| photo-landscape-almsee | 62168 | 77742 | 62168 | 57254 | 59994 |
| photo-night-alexanderplatz | 37968 | 56604 | 37968 | 34912 | 36982 |
| photo-night-brussels | 8086 | 11964 | 8086 | 7354 | 7986 |
| photo-panorama-lakeview | 43962 | 62796 | 43962 | 39730 | 42356 |
| photo-portrait-carter | 36024 | 49580 | 36024 | 34856 | 35428 |
| photo-sea-ibiza | 14550 | 21516 | 14550 | 13566 | 14344 |
| photo-sunrise-fuji | 32246 | 42478 | 32246 | 29958 | 31422 |
| photo-texture-ground | 62958 | 60728 | 62958 | 55812 | 61034 |
| screen | 6262 | 10558 | 6262 | 6220 | 6336 |
| palette | 6742 | 14188 | 6742 | 6552 | 6868 |

### lossy q75

| encoder | MB | bpp | size vs libwebp | PSNR-Y | PSNR-RGB | SSIM-Y | MP/s | time vs libwebp |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| libwebp q75 | 0.583 | 0.600 | 1.000x | 39.194 | 35.879 | 0.9616 | 17.56 | 1.00x |
| cwebp -q 75 -m 0 | 0.743 | 0.766 | 1.275x | 39.467 | 36.116 | 0.9610 | 72.58 | 0.24x |
| cwebp -q 75 -m 4 | 0.583 | 0.600 | 1.000x | 39.194 | 35.879 | 0.9616 | 18.06 | 0.97x |
| cwebp -q 75 -m 6 | 0.546 | 0.562 | 0.936x | 38.948 | 35.802 | 0.9588 | 8.89 | 1.98x |
| ImageSharp q75 | 0.572 | 0.590 | 0.982x | 39.133 | 35.859 | 0.9609 | 4.82 | 3.64x |

Bytes per input:

| input | libwebp q75 | cwebp -q 75 -m 0 | cwebp -q 75 -m 4 | cwebp -q 75 -m 6 | ImageSharp q75 |
|---|---:|---:|---:|---:|---:|
| photo-architecture-teatro | 85452 | 109326 | 85452 | 81206 | 84374 |
| photo-foliage-mangrove | 51894 | 69008 | 51894 | 47474 | 50722 |
| photo-landscape-almsee | 89102 | 107774 | 89102 | 83236 | 86778 |
| photo-night-alexanderplatz | 56128 | 76666 | 56128 | 51912 | 54780 |
| photo-night-brussels | 11894 | 16238 | 11894 | 10670 | 11646 |
| photo-panorama-lakeview | 59222 | 82090 | 59222 | 55396 | 58238 |
| photo-portrait-carter | 49934 | 64860 | 49934 | 48408 | 49414 |
| photo-sea-ibiza | 20078 | 30492 | 20078 | 18712 | 19830 |
| photo-sunrise-fuji | 44324 | 58004 | 44324 | 41626 | 43886 |
| photo-texture-ground | 100588 | 100776 | 100588 | 92924 | 98450 |
| screen | 6920 | 12142 | 6920 | 6912 | 7040 |
| palette | 7234 | 15924 | 7234 | 7166 | 7162 |

### lossy q90

| encoder | MB | bpp | size vs libwebp | PSNR-Y | PSNR-RGB | SSIM-Y | MP/s | time vs libwebp |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| libwebp q90 | 1.229 | 1.266 | 1.000x | 44.389 | 39.195 | 0.9865 | 14.26 | 1.00x |
| cwebp -q 90 -m 0 | 1.393 | 1.435 | 1.133x | 44.194 | 39.160 | 0.9847 | 42.21 | 0.34x |
| cwebp -q 90 -m 4 | 1.229 | 1.266 | 1.000x | 44.389 | 39.195 | 0.9865 | 14.79 | 0.96x |
| cwebp -q 90 -m 6 | 1.189 | 1.225 | 0.967x | 44.177 | 39.088 | 0.9856 | 6.23 | 2.29x |
| ImageSharp q90 | 1.241 | 1.279 | 1.010x | 44.409 | 39.213 | 0.9866 | 4.20 | 3.40x |

Bytes per input:

| input | libwebp q90 | cwebp -q 90 -m 0 | cwebp -q 90 -m 4 | cwebp -q 90 -m 6 | ImageSharp q90 |
|---|---:|---:|---:|---:|---:|
| photo-architecture-teatro | 158622 | 180854 | 158622 | 155816 | 159062 |
| photo-foliage-mangrove | 118882 | 144452 | 118882 | 111582 | 120826 |
| photo-landscape-almsee | 188326 | 204398 | 188326 | 182816 | 187266 |
| photo-night-alexanderplatz | 124664 | 140062 | 124664 | 120414 | 126510 |
| photo-night-brussels | 43040 | 40980 | 43040 | 36604 | 43112 |
| photo-panorama-lakeview | 108824 | 137092 | 108824 | 107884 | 109272 |
| photo-portrait-carter | 109406 | 115248 | 109406 | 105162 | 109188 |
| photo-sea-ibiza | 43136 | 67718 | 43136 | 40946 | 43602 |
| photo-sunrise-fuji | 91128 | 106090 | 91128 | 89438 | 92046 |
| photo-texture-ground | 224862 | 223596 | 224862 | 220062 | 232038 |
| screen | 8922 | 16318 | 8922 | 8848 | 9018 |
| palette | 9346 | 16244 | 9346 | 9274 | 9372 |

## Peak memory, lossless

Peak working set rise of one lossless encode, fresh process each, median of 3 (MiB); the input (width × height × 4 bytes) is resident before the baseline and not part of the rise.

| input | encoder | first-encode rise | steady rise | managed alloc |
|---|---|---:|---:|---:|
| screen | libwebp | 32.11 | 32.12 | 0.00 |
| screen | ImageSharp | 42.30 | 41.97 | 7.14 |
| screen-alpha | libwebp | 32.41 | 32.14 | 0.00 |
| screen-alpha | ImageSharp | 42.23 | 41.88 | 7.13 |
| photo-sea-ibiza | libwebp | 52.83 | 51.82 | 0.43 |
| photo-sea-ibiza | ImageSharp | 48.20 | 45.11 | 7.50 |
