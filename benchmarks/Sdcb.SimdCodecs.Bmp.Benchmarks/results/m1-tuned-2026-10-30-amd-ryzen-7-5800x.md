# BMP / ICO after tuning, 2026-10-30, AMD Ryzen 7 5800X

`Sdcb.SimdCodecs.Bmp` / `.Ico` at `0864df3`, with the untuned first commit (`8cf145d`) loaded side by side as `baseline`, so `÷ baseline` is the whole tuning. Command: `dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Bmp.Benchmarks -- matrix all`.

- CPU: AMD Ryzen 7 5800X 8-Core Processor
- OS: Microsoft Windows 10.0.26200 (X64)
- .NET: .NET 10.0.11 (win-x64), process X64
- Logical processors: 16
- Vector128.IsHardwareAccelerated: True, Vector256.IsHardwareAccelerated: True, Vector512.IsHardwareAccelerated: False
- AVX2: True, AVX-512F: False
- Bitmap size: 1920×1080; icons 256×256 and 48×48
- `photo` source: centre crop of landscape-almsee.png (JPEG benchmark corpus master)
- `screen` source: synthetic
- ImageSharp: 3.1.12
- WIC: WindowsCodecs.dll 10.0.26100.8115 (WinBuild.160101.0800), through WPF
- Baseline: 8cf145d1e31d Benchmarks: BMP / ICO harness and baseline

### Decode (in-process medians of 9 interleaved rounds, ms)

`current` = `BmpImageDecoder` / `IcoImageDecoder.TryDecodeInto` to RGBA in a caller buffer; `current BGR(A)` = the same to BGRA, the layout WIC produces; ImageSharp = `Image.Load<Rgba32>` + `CopyPixelDataTo`, one thread (it reads no icons); WIC = WPF's `BitmapDecoder` + `FormatConvertedBitmap` to BGRA. Ratios below 1 mean we are faster: current ÷ ImageSharp (both RGBA), current BGR(A) ÷ WIC (both BGRA). Every timed output was checked against the others first.

| Case | KB | MP | current | current BGR(A) | baseline | ImageSharp | WIC | current MP/s | ÷ ImageSharp | ÷ WIC | ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-rgb24` | 6075 | 2.07 | 0.2805 | 0.2812 | 0.2833 | 1.236 | 2.192 | 7394 | 0.23× | 0.13× | 0.99× |
| `photo-rgb24-topdown` | 6075 | 2.07 | 0.3162 | 0.3399 | 0.3462 | 1.278 | 2.358 | 6558 | 0.25× | 0.14× | 0.91× |
| `photo-bgra32` | 8100 | 2.07 | 0.3112 | 0.3246 | 0.3137 | 5.526 | 2.130 | 6664 | 0.06× | 0.15× | 0.99× |
| `photo-argb32-v4` | 8100 | 2.07 | 0.3423 | 0.2800 | 0.3046 | 5.614 | 2.060 | 6057 | 0.06× | 0.14× | 1.12× |
| `photo-rgb16` | 4050 | 2.07 | 0.4039 | 0.3991 | 0.3945 | 5.800 | 4.675 | 5134 | 0.07× | 0.09× | 1.02× |
| `photo-rgb565` | 4050 | 2.07 | 0.4160 | 0.3944 | 0.3944 | 6.028 | 4.439 | 4985 | 0.07× | 0.09× | 1.05× |
| `photo-p8` | 2026 | 2.07 | 0.9109 | 0.9244 | 0.9050 | 5.012 | 2.750 | 2276 | 0.18× | 0.34× | 1.01× |
| `photo-p4` | 1013 | 2.07 | 0.6244 | 0.6301 | 5.191 | 4.900 | 1.726 | 3321 | 0.13× | 0.37× | 0.12× |
| `photo-p1` | 253 | 2.07 | 0.5458 | 0.5397 | 5.168 | 4.480 | 1.182 | 3799 | 0.12× | 0.46× | 0.11× |
| `photo-rle8` | 2067 | 2.07 | 2.204 | 2.205 | 9.149 | 6.497 | 4.362 | 941 | 0.34× | 0.51× | 0.24× |
| `photo-rle4` | 1255 | 2.07 | 4.095 | 4.113 | 12.7 | 9.594 | 5.659 | 506 | 0.43× | 0.73× | 0.32× |
| `photo-ico256-bmp32` | 264 | 0.07 | 0.0143 | 0.0139 | 0.0148 | — | 0.3771 | 4577 | — | 0.04× | 0.97× |
| `photo-ico256-png` | 129 | 0.07 | 1.012 | 1.024 | 1.014 | — | 0.9835 | 65 | — | 1.04× | 1.00× |
| `photo-ico48-bmp8` | 3 | 0.00 | 0.0033 | 0.0033 | 0.0032 | — | 0.0522 | 707 | — | 0.06× | 1.02× |
| `screen-rgb24` | 6075 | 2.07 | 0.2832 | 0.2854 | 0.2757 | 1.214 | 2.206 | 7321 | 0.23× | 0.13× | 1.03× |
| `screen-rgb24-topdown` | 6075 | 2.07 | 0.2844 | 0.3088 | 0.3158 | 1.212 | 2.266 | 7290 | 0.23× | 0.14× | 0.90× |
| `screen-bgra32` | 8100 | 2.07 | 0.3674 | 0.3697 | 0.3831 | 5.563 | 2.221 | 5643 | 0.07× | 0.17× | 0.96× |
| `screen-argb32-v4` | 8100 | 2.07 | 0.3361 | 0.4430 | 0.3318 | 5.523 | 2.132 | 6170 | 0.06× | 0.21× | 1.01× |
| `screen-rgb16` | 4050 | 2.07 | 0.4038 | 0.3942 | 0.4050 | 5.743 | 4.724 | 5136 | 0.07× | 0.08× | 1.00× |
| `screen-rgb565` | 4050 | 2.07 | 0.4051 | 0.3741 | 0.3831 | 6.017 | 4.392 | 5119 | 0.07× | 0.09× | 1.06× |
| `screen-p8` | 2026 | 2.07 | 0.9032 | 0.9026 | 0.9066 | 4.982 | 2.462 | 2296 | 0.18× | 0.37× | 1.00× |
| `screen-p4` | 1013 | 2.07 | 0.6292 | 0.6343 | 5.210 | 4.902 | 1.726 | 3296 | 0.13× | 0.37× | 0.12× |
| `screen-p1` | 253 | 2.07 | 0.5276 | 0.5332 | 5.192 | 4.486 | 1.145 | 3930 | 0.12× | 0.47× | 0.10× |
| `screen-rle8` | 195 | 2.07 | 0.4997 | 0.4984 | 0.9110 | 4.757 | 2.651 | 4150 | 0.11× | 0.19× | 0.55× |
| `screen-rle4` | 143 | 2.07 | 0.5124 | 0.5073 | 9.444 | 4.883 | 2.899 | 4047 | 0.10× | 0.17× | 0.05× |
| `screen-ico256-bmp32` | 264 | 0.07 | 0.0149 | 0.0139 | 0.0145 | — | 0.3931 | 4385 | — | 0.04× | 1.03× |
| `screen-ico256-png` | 1 | 0.07 | 0.1655 | 0.1762 | 0.1656 | — | 0.2275 | 396 | — | 0.77× | 1.00× |
| `screen-ico48-bmp8` | 3 | 0.00 | 0.0032 | 0.0033 | 0.0032 | — | 0.0518 | 711 | — | 0.06× | 1.01× |

Geometric means over the cases both decode:

- current ÷ ImageSharp, photo: 0.14× (11 cases)
- current ÷ ImageSharp, screen: 0.11× (11 cases)
- current BGR(A) ÷ WIC, photo: 0.20× (14 cases)
- current BGR(A) ÷ WIC, screen: 0.17× (14 cases)
- current ÷ baseline, photo: 0.61× (14 cases)
- current ÷ baseline, screen: 0.57× (14 cases)

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool and WIC allocates natively, so neither shows here: compare working sets with `peakmem`.

| Case | current | current BGR(A) | baseline | ImageSharp | WIC |
|---|---:|---:|---:|---:|---:|
| `photo-rgb24` | 0.0 | 0.0 | 0.0 | 2.1 | 2.2 |
| `photo-rgb24-topdown` | 0.0 | 0.0 | 0.0 | 2.1 | 2.2 |
| `photo-bgra32` | 0.0 | 0.0 | 0.0 | 2.1 | 2.2 |
| `photo-argb32-v4` | 0.0 | 0.0 | 0.0 | 2.1 | 2.2 |
| `photo-rgb16` | 0.0 | 0.0 | 0.0 | 2.1 | 2.2 |
| `photo-rgb565` | 0.0 | 0.0 | 0.0 | 2.1 | 2.2 |
| `photo-p8` | 0.0 | 0.0 | 0.0 | 3.2 | 23.4 |
| `photo-p4` | 0.0 | 0.0 | 0.0 | 2.2 | 3.6 |
| `photo-p1` | 0.0 | 0.0 | 0.0 | 2.2 | 2.6 |
| `photo-rle8` | 0.0 | 0.0 | 0.0 | 100.3 | 2.2 |
| `photo-rle4` | 0.0 | 0.0 | 0.0 | 2.4 | 2.2 |
| `photo-ico256-bmp32` | 0.1 | 0.1 | 0.1 | — | 2.7 |
| `photo-ico256-png` | 1.8 | 0.7 | 1.8 | — | 2.7 |
| `photo-ico48-bmp8` | 0.1 | 0.1 | 0.1 | — | 8.0 |
| `screen-rgb24` | 0.0 | 0.0 | 0.0 | 2.1 | 2.2 |
| `screen-rgb24-topdown` | 0.0 | 0.0 | 0.0 | 2.1 | 2.2 |
| `screen-bgra32` | 0.0 | 0.0 | 0.0 | 2.1 | 2.2 |
| `screen-argb32-v4` | 0.0 | 0.0 | 0.0 | 2.1 | 2.2 |
| `screen-rgb16` | 0.0 | 0.0 | 0.0 | 2.1 | 2.2 |
| `screen-rgb565` | 0.0 | 0.0 | 0.0 | 2.1 | 2.2 |
| `screen-p8` | 0.0 | 0.0 | 0.0 | 3.2 | 23.4 |
| `screen-p4` | 0.0 | 0.0 | 0.0 | 2.2 | 3.6 |
| `screen-p1` | 0.0 | 0.0 | 0.0 | 2.2 | 2.6 |
| `screen-rle8` | 0.0 | 0.0 | 0.0 | 3.3 | 2.2 |
| `screen-rle4` | 0.0 | 0.0 | 0.0 | 2.4 | 2.2 |
| `screen-ico256-bmp32` | 0.1 | 0.1 | 0.1 | — | 2.7 |
| `screen-ico256-png` | 1.8 | 0.7 | 1.8 | — | 2.7 |
| `screen-ico48-bmp8` | 0.1 | 0.1 | 0.1 | — | 8.1 |

### Encode (in-process medians of 9 interleaved rounds, ms; sizes in KB)

`current` = `BmpEncoder.Encode` from RGBA to a new array (`IcoEncoder.Encode` for the icon set); `current → stream` = the `Stream` overload into a reused stream, as ImageSharp and WIC write; `current BGR(A)` = that from the B, G, R(, A) input WIC gets; ImageSharp = `BmpEncoder` with the same bits per pixel into a reused stream, one thread, from an `Image<Rgba32>` built beforehand; WIC = WPF's `BmpBitmapEncoder` over a `BitmapSource` of B, G, R(, A), into a reused stream (24- and 32-bit only). Every output decodes, and the lossless layouts give the source back.

| Case | current | KB | current → stream | current BGR(A) | baseline | ImageSharp | KB | WIC | KB | stream ÷ ImageSharp | BGR(A) ÷ WIC | ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-rgb24` | 0.5951 | 6075 | 0.3321 | 0.1968 | 0.8829 | 0.2844 | 6075 | 4.503 | 6075 | 1.17× | 0.04× | 0.67× |
| `photo-argb32` | 1.180 | 8100 | 0.4061 | 0.2292 | 1.467 | 0.2347 | 8100 | 5.663 | 8100 | 1.73× | 0.04× | 0.80× |
| `photo-rgb565` | 2.487 | 4050 | 2.233 | — | 15.1 | 14.4 | 4050 | — | — | 0.16× | — | 0.17× |
| `photo-p8` | 97.3 | 2026 | 96.9 | — | 253.9 | 208.2 | 2026 | — | — | 0.47× | — | 0.38× |
| `photo-rle8` | 102.6 | 2067 | 102.7 | — | 260.3 | — | — | — | — | — | — | 0.39× |
| `photo-ico-set` | 6.979 | 144 | — | — | — | — | — | — | — | — | — | — |
| `screen-rgb24` | 0.5403 | 6075 | 0.3320 | 0.1614 | 0.7608 | 0.3413 | 6075 | 4.525 | 6075 | 0.97× | 0.04× | 0.71× |
| `screen-argb32` | 0.7175 | 8100 | 0.4740 | 0.3559 | 1.055 | 0.3765 | 8100 | 5.231 | 8100 | 1.26× | 0.07× | 0.68× |
| `screen-rgb565` | 2.071 | 4050 | 2.118 | — | 14.6 | 14.4 | 4050 | — | — | 0.15× | — | 0.14× |
| `screen-p8` | 27.5 | 2026 | 27.5 | — | 30.2 | 76.4 | 2026 | — | — | 0.36× | — | 0.91× |
| `screen-rle8` | 30.3 | 195 | 30.5 | — | 32.8 | — | — | — | — | — | — | 0.93× |
| `screen-ico-set` | 1.704 | 16 | — | — | — | — | — | — | — | — | — | — |

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool and WIC allocates natively, so neither shows here: compare working sets with `peakmem`.

| Case | current | current → stream | current BGR(A) | baseline | ImageSharp | WIC |
|---|---:|---:|---:|---:|---:|---:|
| `photo-rgb24` | 6075.1 | 0.0 | 0.0 | 6075.1 | 0.2 | 6102.1 |
| `photo-argb32` | 8100.1 | 0.0 | 0.0 | 8100.1 | 0.2 | 8127.1 |
| `photo-rgb565` | 4050.1 | 0.0 | — | 4050.1 | 0.2 | — |
| `photo-p8` | 14271.7 | 12245.6 | — | 13906.2 | 25896.7 | — |
| `photo-rle8` | 23466.1 | 21399.1 | — | 23100.7 | — | — |
| `photo-ico-set` | 1051.7 | — | — | — | — | — |
| `screen-rgb24` | 6075.1 | 0.0 | 0.0 | 6075.1 | 0.2 | 6102.1 |
| `screen-argb32` | 8100.1 | 0.0 | 0.0 | 8100.1 | 0.2 | 8127.1 |
| `screen-rgb565` | 4050.1 | 0.0 | — | 4050.1 | 0.2 | — |
| `screen-p8` | 14230.4 | 12204.4 | — | 13906.2 | 3088.0 | — |
| `screen-rle8` | 13605.0 | 13410.4 | — | 13280.7 | — | — |
| `screen-ico-set` | 293.0 | — | — | — | — | — |

Notes:

- `photo-rgb565`: ImageSharp's 16-bit layout is its own (it may be 5-5-5); sizes are equal either way
- `photo-ico-set`: 16, 32 and 48 as 32-bit DIBs, 256 as PNG; neither ImageSharp 3.1 nor WIC writes icons
- `screen-rgb565`: ImageSharp's 16-bit layout is its own (it may be 5-5-5); sizes are equal either way
- `screen-ico-set`: 16, 32 and 48 as 32-bit DIBs, 256 as PNG; neither ImageSharp 3.1 nor WIC writes icons
