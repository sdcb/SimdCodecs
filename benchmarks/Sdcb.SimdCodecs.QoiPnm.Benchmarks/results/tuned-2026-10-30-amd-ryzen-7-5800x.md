# QOI / Netpbm: tuned vs. the first commit (2026-10-30)

`dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.QoiPnm.Benchmarks -- matrix all`, with `baseline 7b0b2f2` (the packages as first committed) built beforehand.

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
- Baseline: 7b0b2f281dea QOI and Netpbm: Sdcb.SimdCodecs.Qoi and Sdcb.SimdCodecs.Pnm

### Decode (in-process medians of 9 interleaved rounds, ms)

`current` = `QoiImageDecoder` / `PnmImageDecoder.TryDecodeInto` to RGBA in a caller buffer (PFM: `PnmDecoder.Decode` to floats); ImageSharp = `Image.Load<Rgba32>` + `CopyPixelDataTo`, one thread. Every timed output was checked against ours first.

| Case | KB | current | baseline | ImageSharp | current ÷ ImageSharp | ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|
| `photo-qoi-rgba` | 4684 | 7.915 | 7.918 | 16.6 | 0.48× | 1.00× |
| `photo-qoi-rgb` | 3672 | 8.205 | 8.114 | 16.4 | 0.50× | 1.01× |
| `photo-ppm` | 6075 | 0.3252 | 0.3104 | 1.135 | 0.29× | 1.05× |
| `photo-pgm` | 2025 | 0.3787 | 0.3452 | 1.702 | 0.22× | 1.10× |
| `photo-pbm` | 253 | 0.4276 | 0.4258 | 3.387 | 0.13× | 1.00× |
| `photo-ppm16` | 12150 | 0.5638 | 0.4996 | — | — | 1.13× |
| `photo-ppm-plain` | 18838 | 30.8 | 32.2 | 58.3 | 0.53× | 0.96× |
| `photo-pam-rgba` | 8100 | 0.2767 | 0.2745 | — | — | 1.01× |
| `photo-pfm` | 24300 | 1.719 | — | — | — | — |
| `screen-qoi-rgba` | 255 | 0.9395 | 0.9701 | 5.718 | 0.16× | 0.97× |
| `screen-qoi-rgb` | 235 | 0.9603 | 0.9166 | 5.668 | 0.17× | 1.05× |
| `screen-ppm` | 6075 | 0.3045 | 0.3052 | 1.133 | 0.27× | 1.00× |
| `screen-pgm` | 2025 | 0.3431 | 0.3432 | 1.687 | 0.20× | 1.00× |
| `screen-pbm` | 253 | 0.4234 | 0.4464 | 3.579 | 0.12× | 0.95× |
| `screen-ppm16` | 12150 | 0.4498 | 0.4952 | 3.585 | 0.13× | 0.91× |
| `screen-ppm-plain` | 21926 | 31.8 | 33.1 | 62.9 | 0.51× | 0.96× |
| `screen-pam-rgba` | 8100 | 0.2953 | 0.1910 | — | — | 1.55× |
| `screen-pfm` | 24300 | 1.656 | — | — | — | — |

- QOI: current ÷ ImageSharp geometric mean 0.29× (4 cases)
- PNM: current ÷ ImageSharp geometric mean 0.23× (9 cases)
- QOI: current ÷ baseline geometric mean 1.01× (4 cases)
- PNM: current ÷ baseline geometric mean 1.04× (12 cases)

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool, which does not show here.

| Case | current | baseline | ImageSharp |
|---|---:|---:|---:|
| `photo-qoi-rgba` | 0.0 | 0.0 | 2.2 |
| `photo-qoi-rgb` | 0.0 | 0.0 | 2.2 |
| `photo-ppm` | 0.0 | 0.0 | 1.9 |
| `photo-pgm` | 0.0 | 0.0 | 1.9 |
| `photo-pbm` | 0.0 | 0.0 | 1.9 |
| `photo-ppm16` | 0.0 | 0.0 | — |
| `photo-ppm-plain` | 0.0 | 0.0 | 1.9 |
| `photo-pam-rgba` | 0.0 | 0.0 | — |
| `photo-pfm` | 24300.1 | — | — |
| `screen-qoi-rgba` | 0.0 | 0.0 | 2.0 |
| `screen-qoi-rgb` | 0.0 | 0.0 | 2.0 |
| `screen-ppm` | 0.0 | 0.0 | 1.9 |
| `screen-pgm` | 0.0 | 0.0 | 1.9 |
| `screen-pbm` | 0.0 | 0.0 | 1.9 |
| `screen-ppm16` | 0.0 | 0.0 | 1.9 |
| `screen-ppm-plain` | 0.0 | 0.0 | 1.9 |
| `screen-pam-rgba` | 0.0 | 0.0 | — |
| `screen-pfm` | 24300.1 | — | — |

Notes:

- `photo-ppm16`: ImageSharp differs from current by up to 254, not timed (it reads 16-bit samples least significant byte first)
- `photo-pfm`: to native float samples; ImageSharp reads no PFM
- `screen-pfm`: to native float samples; ImageSharp reads no PFM

### Encode (in-process medians of 9 interleaved rounds, ms)

`current` = `QoiEncoder` / `PnmEncoder.Encode` to a new array; `current → stream` = the `Stream` overload into a reused stream, as ImageSharp writes; ImageSharp = its encoder from an image built beforehand into a reused stream, one thread. Every output decodes to the input.

| Case | KB | current | current → stream | baseline | ImageSharp | current → stream ÷ ImageSharp | ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|
| `photo-qoi-rgba` | 4684 | 9.715 | 9.435 | 11.3 | 23.0 | 0.41× | 0.83× |
| `photo-qoi-rgb` | 3672 | 10.4 | 10.0 | 10.7 | 20.8 | 0.48× | 0.94× |
| `photo-ppm` | 6075 | 0.5943 | 0.1123 | 1.406 | 0.2205 | 0.51× | 0.08× |
| `photo-pgm` | 2025 | 0.1833 | 0.0346 | 0.4909 | 0.0437 | 0.79× | 0.07× |
| `photo-pbm` | 253 | 0.1229 | 0.1098 | 0.1501 | 1.676 | 0.07× | 0.73× |
| `photo-ppm16` | 12150 | 0.9172 | 0.4803 | — | — | — | — |
| `photo-ppm-plain` | 18838 | 25.7 | 21.5 | 57.6 | 25.5 | 0.85× | 0.37× |
| `photo-pam-rgba` | 8100 | 0.8143 | 0.4327 | 2.104 | — | — | 0.21× |
| `photo-pfm` | 24300 | 1.849 | 1.839 | — | — | — | — |
| `screen-qoi-rgba` | 255 | 1.668 | 1.639 | 2.082 | 3.256 | 0.50× | 0.79× |
| `screen-qoi-rgb` | 234 | 1.951 | 1.942 | 2.381 | 3.286 | 0.59× | 0.82× |
| `screen-ppm` | 6075 | 0.7017 | 0.1659 | 1.542 | 0.1529 | 1.09× | 0.11× |
| `screen-pgm` | 2025 | 0.2621 | 0.0788 | 0.5821 | 0.0427 | 1.84× | 0.14× |
| `screen-pbm` | 253 | 0.1413 | 0.1095 | 0.1814 | 1.626 | 0.07× | 0.60× |
| `screen-ppm16` | 12150 | 1.037 | 0.6470 | — | 0.4921 | 1.31× | — |
| `screen-ppm-plain` | 21926 | 24.0 | 24.5 | 59.4 | 25.9 | 0.95× | 0.41× |
| `screen-pam-rgba` | 8100 | 0.9771 | 0.1461 | 2.306 | — | — | 0.06× |
| `screen-pfm` | 24300 | 1.881 | 1.872 | — | — | — | — |

- QOI: current → stream ÷ ImageSharp geometric mean 0.49× (4 cases)
- PNM: current → stream ÷ ImageSharp geometric mean 0.54× (9 cases)
- QOI: current → stream ÷ baseline geometric mean 0.84× (4 cases)
- PNM: current → stream ÷ baseline geometric mean 0.19× (10 cases)

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool, which does not show here.

| Case | current | current → stream | baseline | ImageSharp |
|---|---:|---:|---:|---:|
| `photo-qoi-rgba` | 4685.1 | 0.0 | 18860.1 | 0.3 |
| `photo-qoi-rgb` | 3673.0 | 0.0 | 9747.7 | 0.3 |
| `photo-ppm` | 6075.8 | 0.1 | 12150.2 | 0.2 |
| `photo-pgm` | 2025.8 | 0.1 | 4050.2 | 0.2 |
| `photo-pbm` | 253.9 | 0.2 | 506.4 | 0.2 |
| `photo-ppm16` | 12150.7 | 0.1 | — | — |
| `photo-ppm-plain` | 43139.7 | 0.2 | 43139.1 | 0.3 |
| `photo-pam-rgba` | 8101.0 | 0.3 | 16200.5 | — |
| `photo-pfm` | 24300.7 | 0.1 | — | — |
| `screen-qoi-rgba` | 255.9 | 0.0 | 2280.5 | 0.3 |
| `screen-qoi-rgb` | 234.6 | 0.0 | 2259.2 | 0.3 |
| `screen-ppm` | 6075.8 | 0.1 | 12150.2 | 0.2 |
| `screen-pgm` | 2025.8 | 0.1 | 4050.2 | 0.2 |
| `screen-pbm` | 253.9 | 0.2 | 506.4 | 0.2 |
| `screen-ppm16` | 12150.7 | 0.1 | — | 0.2 |
| `screen-ppm-plain` | 46227.6 | 0.2 | 46227.0 | 0.3 |
| `screen-pam-rgba` | 8101.0 | 0.3 | 16200.5 | — |
| `screen-pfm` | 24300.7 | 0.1 | — | — |

Notes:

- `photo-ppm16`: ImageSharp's output decodes to other pixels, not timed (it writes 16-bit samples least significant byte first)
- `photo-pam-rgba`: ImageSharp writes no PAM
- `photo-pfm`: ImageSharp writes no PFM
- `screen-pam-rgba`: ImageSharp writes no PAM
- `screen-pfm`: ImageSharp writes no PFM
