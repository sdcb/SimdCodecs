# WebP encode baseline — 2026-11-02, AMD Ryzen 7 5800X

The reference for the encoder work after the lossless encoder (b42e81f): the same lossless encoder,
measured with the full harness (rate-distortion, lossy-alpha and animation modes). Command:
`encode --mode all --reps 3 --efforts 0,1,2,3,4,5,6,7,8,9 --gate`, with cwebp, img2webp and anim_dump
1.6.0 from the release archive.

The gate fails here, as expected: the lossless default is 3.4% larger than libwebp, and the bytes are
not monotonic in effort (effort 4 is larger than effort 3). Lossy, lossy-alpha and anim have no encoder
of ours yet; those tables are the competitors only.

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
| lossless | 243.5 ms | 249.0 ms | 271.0 ms | no (791238 vs 791294) |
| lossy q75 | 44.9 ms | 43.0 ms | 90.0 ms | yes |

### lossless

| encoder | MB | bpp | size vs libwebp | MP/s | time vs libwebp | lossless |
|---|---:|---:|---:|---:|---:|---|
| libwebp | 7.773 | 6.837 | 1.000x | 2.61 | 1.00x | visible |
| cwebp -z 0 | 8.885 | 7.815 | 1.143x | 32.60 | 0.08x | visible |
| cwebp -z 3 | 7.814 | 6.873 | 1.005x | 3.52 | 0.74x | visible |
| cwebp -z 6 | 7.773 | 6.837 | 1.000x | 2.63 | 0.99x | visible |
| cwebp -z 9 | 7.558 | 6.648 | 0.972x | 0.13 | 20.35x | visible |
| ImageSharp | 7.772 | 6.836 | 1.000x | 2.01 | 1.30x | visible |
| ours -e 0 | 8.541 | 7.512 | 1.099x | 8.57 | 0.31x | visible |
| ours -e 1 | 8.499 | 7.475 | 1.093x | 6.53 | 0.40x | visible |
| ours -e 2 | 8.109 | 7.132 | 1.043x | 5.64 | 0.46x | visible |
| ours -e 3 | 8.076 | 7.104 | 1.039x | 5.29 | 0.49x | visible |
| ours -e 4 | 8.081 | 7.108 | 1.040x | 4.82 | 0.54x | visible |
| ours -e 5 | 8.040 | 7.072 | 1.034x | 4.19 | 0.62x | visible |
| ours | 8.036 | 7.068 | 1.034x | 3.94 | 0.66x | visible |
| ours -e 7 | 8.004 | 7.040 | 1.030x | 3.27 | 0.80x | visible |
| ours -e 8 | 7.940 | 6.984 | 1.021x | 2.18 | 1.20x | visible |
| ours -e 9 | 7.933 | 6.978 | 1.021x | 1.70 | 1.54x | visible |

Bytes per input:

| input | libwebp | cwebp -z 0 | cwebp -z 3 | cwebp -z 6 | cwebp -z 9 | ImageSharp | ours -e 0 | ours -e 1 | ours -e 2 | ours -e 3 | ours -e 4 | ours -e 5 | ours | ours -e 7 | ours -e 8 | ours -e 9 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| photo-architecture-teatro | 791238 | 871806 | 791950 | 791294 | 768812 | 791568 | 867328 | 856356 | 809006 | 803150 | 802986 | 796698 | 796338 | 790960 | 781660 | 780810 |
| photo-foliage-mangrove | 704308 | 755784 | 715392 | 705126 | 690106 | 703976 | 738048 | 747034 | 723640 | 721192 | 720808 | 716514 | 715796 | 713680 | 705454 | 703702 |
| photo-landscape-almsee | 941230 | 1035750 | 955340 | 941452 | 909530 | 941102 | 1048336 | 1031800 | 966416 | 960474 | 961660 | 957082 | 957116 | 953880 | 947532 | 947112 |
| photo-night-alexanderplatz | 696196 | 774758 | 701080 | 695842 | 705184 | 695816 | 765534 | 771982 | 733054 | 729212 | 729518 | 726482 | 726124 | 723112 | 719306 | 718384 |
| photo-night-brussels | 195670 | 275094 | 197920 | 195670 | 192726 | 195422 | 277652 | 272118 | 266588 | 264254 | 262052 | 260446 | 260076 | 257418 | 256200 | 255288 |
| photo-panorama-lakeview | 459794 | 508586 | 460916 | 459734 | 446980 | 459856 | 505892 | 496492 | 468642 | 466176 | 465906 | 463014 | 462936 | 462072 | 459568 | 459336 |
| photo-portrait-carter | 951194 | 1032280 | 954396 | 951274 | 910660 | 950640 | 1000502 | 1007380 | 969192 | 970732 | 975500 | 973158 | 974292 | 973988 | 965630 | 965850 |
| photo-sea-ibiza | 449038 | 552552 | 449768 | 449272 | 444572 | 449874 | 524032 | 538452 | 506960 | 508734 | 509722 | 508436 | 504956 | 506336 | 502976 | 502410 |
| photo-sunrise-fuji | 603984 | 694438 | 604614 | 603752 | 585130 | 603828 | 699282 | 689638 | 643544 | 640456 | 641886 | 637484 | 637598 | 627120 | 624102 | 623946 |
| photo-texture-ground | 1108782 | 1124976 | 1108636 | 1108866 | 1057366 | 1108898 | 1127604 | 1127638 | 1119794 | 1117146 | 1117130 | 1113064 | 1113056 | 1112554 | 1104790 | 1104768 |
| photo-alpha | 863424 | 964242 | 865618 | 862984 | 841870 | 863188 | 975366 | 950060 | 892488 | 885510 | 884382 | 878088 | 877794 | 872370 | 862748 | 862176 |
| screen | 3664 | 141648 | 3656 | 3664 | 2116 | 3598 | 4624 | 4336 | 4128 | 4178 | 4274 | 4382 | 4358 | 4568 | 4526 | 4368 |
| screen-alpha | 3658 | 150716 | 3660 | 3654 | 2192 | 3598 | 5046 | 4326 | 4066 | 4136 | 4238 | 4364 | 4356 | 4578 | 4428 | 4382 |
| palette | 910 | 2238 | 930 | 908 | 902 | 908 | 1642 | 1190 | 1100 | 1044 | 1046 | 1084 | 996 | 964 | 934 | 892 |

[gate] ours 8035792 bytes vs libwebp 7773090 (1.0338x): LARGER
[gate] ours 2.310 s vs libwebp 3.480 s (0.66x): ok
[gate] effort 0, 1, 2, 3, 4, 5, 6, 7, 8, 9: bytes never grow with effort: NOT MONOTONIC

### lossy q50

| encoder | MB | bpp | size vs libwebp | PSNR-Y | PSNR-RGB | SSIM-Y | MP/s | time vs libwebp |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| libwebp q50 | 0.408 | 0.421 | 1.000x | 37.211 | 34.417 | 0.9423 | 19.11 | 1.00x |
| cwebp -q 50 -m 0 | 0.538 | 0.554 | 1.318x | 37.544 | 34.618 | 0.9430 | 83.51 | 0.23x |
| cwebp -q 50 -m 4 | 0.408 | 0.421 | 1.000x | 37.211 | 34.417 | 0.9423 | 19.71 | 0.97x |
| cwebp -q 50 -m 6 | 0.376 | 0.388 | 0.922x | 36.863 | 34.235 | 0.9381 | 10.67 | 1.79x |
| ImageSharp q50 | 0.397 | 0.409 | 0.973x | 37.161 | 34.370 | 0.9408 | 5.20 | 3.67x |

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
| libwebp q75 | 0.583 | 0.600 | 1.000x | 39.194 | 35.879 | 0.9616 | 17.79 | 1.00x |
| cwebp -q 75 -m 0 | 0.743 | 0.766 | 1.275x | 39.467 | 36.116 | 0.9610 | 75.40 | 0.24x |
| cwebp -q 75 -m 4 | 0.583 | 0.600 | 1.000x | 39.194 | 35.879 | 0.9616 | 18.32 | 0.97x |
| cwebp -q 75 -m 6 | 0.546 | 0.562 | 0.936x | 38.948 | 35.802 | 0.9588 | 9.08 | 1.96x |
| ImageSharp q75 | 0.572 | 0.590 | 0.982x | 39.133 | 35.859 | 0.9609 | 4.95 | 3.60x |

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
| libwebp q90 | 1.229 | 1.266 | 1.000x | 44.389 | 39.195 | 0.9865 | 14.72 | 1.00x |
| cwebp -q 90 -m 0 | 1.393 | 1.435 | 1.133x | 44.194 | 39.160 | 0.9847 | 43.14 | 0.34x |
| cwebp -q 90 -m 4 | 1.229 | 1.266 | 1.000x | 44.389 | 39.195 | 0.9865 | 15.14 | 0.97x |
| cwebp -q 90 -m 6 | 1.189 | 1.225 | 0.967x | 44.177 | 39.088 | 0.9856 | 6.48 | 2.27x |
| ImageSharp q90 | 1.241 | 1.279 | 1.010x | 44.409 | 39.213 | 0.9866 | 4.35 | 3.38x |

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

### lossy: rate-distortion (BD-rate vs libwebp)

| encoder | BD-rate (PSNR-Y) | BD-rate (SSIM-Y) | bpp range |
|---|---:|---:|---|
| libwebp | +0.00% | +0.00% | 0.07–3.84 |
| cwebp -m 6 | -1.37% | -0.10% | 0.06–3.81 |
| ImageSharp | +0.21% | +0.68% | 0.06–4.03 |

BD-rate per input (PSNR-Y / SSIM-Y):

| encoder | photo-architecture-teatro | photo-foliage-mangrove | photo-landscape-almsee | photo-night-alexanderplatz | photo-night-brussels | photo-panorama-lakeview | photo-portrait-carter | photo-sea-ibiza | photo-sunrise-fuji | photo-texture-ground | screen | palette |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| libwebp | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% | +0.00% / +0.00% |
| cwebp -m 6 | -0.86% / +0.62% | -2.37% / -1.51% | -1.91% / -0.80% | -1.97% / -0.60% | -4.48% / -3.31% | -1.69% / -0.55% | -1.99% / -2.21% | +0.46% / +5.19% | -1.39% / +0.07% | -1.71% / -1.50% | +1.43% / +1.53% | +0.01% / +1.87% |
| ImageSharp | +0.46% / +0.85% | -0.14% / +0.06% | -0.60% / -0.48% | +0.03% / +0.17% | +0.20% / +0.31% | -0.33% / -0.03% | -0.66% / -0.59% | +0.03% / +0.20% | +0.02% / -0.14% | +1.56% / +1.86% | +0.49% / +1.61% | +1.45% / +4.32% |

### lossy-alpha: rate-distortion (BD-rate vs libwebp)

| encoder | BD-rate (visible PSNR-RGB) | alpha exact | bpp range |
|---|---:|---|---|
| libwebp | +0.00% | yes | 0.07–3.10 |

BD-rate per input (visible PSNR-RGB):

| encoder | photo-alpha | screen-alpha | sticker |
|---|---:|---:|---:|
| libwebp | +0.00% | +0.00% | +0.00% |

### anim (5 sequences, 18.43 MP of frames; img2webp time is process wall clock)

| sequence | canvas | frames | alpha |
|---|---|---:|---|
| scroll | 480×320 | 24 | no |
| sprite | 480×320 | 24 | no |
| fade | 480×320 | 24 | no |
| alpha-sprite | 480×320 | 24 | yes |
| slides | 480×320 | 24 | no |

| encoder | MB | bpp | size vs img2webp -lossless | MP/s | accuracy |
|---|---:|---:|---:|---:|---|
| img2webp -lossless | 6.027 | 2.616 | 1.000x | 3.23 | exact |
| img2webp -mixed | 0.847 | 0.368 | 0.141x | 24.26 | lowest PSNR 22.8 dB |

| encoder | BD-rate (visible PSNR-RGB) vs img2webp -lossy | per sequence |
|---|---:|---|
| img2webp -lossy | +0.00% | scroll +0.0%, sprite +0.0%, fade +0.0%, alpha-sprite +0.0%, slides +0.0% |
