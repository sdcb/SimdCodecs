# BMP / ICO baseline, 2026-10-30, AMD Ryzen 7 5800X

First measurement of `Sdcb.SimdCodecs.Bmp` / `.Ico` at commit `6ef64fd` (benchmark project uncommitted), before any tuning.
Command: `dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Bmp.Benchmarks -- matrix all`.

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
- Baseline: (none built)

### Decode (in-process medians of 9 interleaved rounds, ms)

`current` = `BmpImageDecoder` / `IcoImageDecoder.TryDecodeInto` to RGBA in a caller buffer; `current BGR(A)` = the same to BGRA, the layout WIC produces; ImageSharp = `Image.Load<Rgba32>` + `CopyPixelDataTo`, one thread (it reads no icons); WIC = WPF's `BitmapDecoder` + `FormatConvertedBitmap` to BGRA. Ratios below 1 mean we are faster: current ÷ ImageSharp (both RGBA), current BGR(A) ÷ WIC (both BGRA). Every timed output was checked against the others first.

| Case | KB | MP | current | current BGR(A) | baseline | ImageSharp | WIC | current MP/s | ÷ ImageSharp | ÷ WIC | ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-rgb24` | 6075 | 2.07 | 0.2766 | 0.2804 | — | 1.249 | 2.207 | 7498 | 0.22× | 0.13× | — |
| `photo-rgb24-topdown` | 6075 | 2.07 | 0.3233 | 0.2968 | — | 1.238 | 2.264 | 6414 | 0.26× | 0.13× | — |
| `photo-bgra32` | 8100 | 2.07 | 0.3541 | 0.3464 | — | 5.782 | 2.126 | 5856 | 0.06× | 0.16× | — |
| `photo-argb32-v4` | 8100 | 2.07 | 0.2964 | 0.2850 | — | 5.629 | 2.078 | 6995 | 0.05× | 0.14× | — |
| `photo-rgb16` | 4050 | 2.07 | 0.3991 | 0.4011 | — | 5.445 | 4.654 | 5196 | 0.07× | 0.09× | — |
| `photo-rgb565` | 4050 | 2.07 | 0.3746 | 0.3863 | — | 6.038 | 4.473 | 5536 | 0.06× | 0.09× | — |
| `photo-p8` | 2026 | 2.07 | 0.9201 | 0.9221 | — | 5.223 | 2.656 | 2254 | 0.18× | 0.35× | — |
| `photo-p4` | 1013 | 2.07 | 5.185 | 5.190 | — | 4.957 | 1.803 | 400 | 1.05× | 2.88× | — |
| `photo-p1` | 253 | 2.07 | 5.183 | 5.225 | — | 4.737 | 1.225 | 400 | 1.09× | 4.26× | — |
| `photo-rle8` | 2067 | 2.07 | 8.833 | 9.036 | — | 6.661 | 4.639 | 235 | 1.33× | 1.95× | — |
| `photo-rle4` | 1255 | 2.07 | 12.5 | 12.5 | — | 9.918 | 5.715 | 166 | 1.26× | 2.19× | — |
| `photo-ico256-bmp32` | 264 | 0.07 | 0.0146 | 0.0148 | — | — | 0.3883 | 4497 | — | 0.04× | — |
| `photo-ico256-png` | 129 | 0.07 | 1.019 | 1.031 | — | — | 0.9795 | 64 | — | 1.05× | — |
| `photo-ico48-bmp8` | 3 | 0.00 | 0.0033 | 0.0033 | — | — | 0.0527 | 704 | — | 0.06× | — |
| `screen-rgb24` | 6075 | 2.07 | 0.2766 | 0.2781 | — | 1.239 | 2.143 | 7496 | 0.22× | 0.13× | — |
| `screen-rgb24-topdown` | 6075 | 2.07 | 0.2636 | 0.2598 | — | 1.163 | 2.120 | 7866 | 0.23× | 0.12× | — |
| `screen-bgra32` | 8100 | 2.07 | 0.2626 | 0.2611 | — | 5.477 | 2.111 | 7896 | 0.05× | 0.12× | — |
| `screen-argb32-v4` | 8100 | 2.07 | 0.2685 | 0.2733 | — | 5.537 | 2.145 | 7724 | 0.05× | 0.13× | — |
| `screen-rgb16` | 4050 | 2.07 | 0.3958 | 0.4041 | — | 5.445 | 4.662 | 5239 | 0.07× | 0.09× | — |
| `screen-rgb565` | 4050 | 2.07 | 0.3966 | 0.3660 | — | 6.051 | 4.400 | 5229 | 0.07× | 0.08× | — |
| `screen-p8` | 2026 | 2.07 | 0.9176 | 0.9204 | — | 5.174 | 2.575 | 2260 | 0.18× | 0.36× | — |
| `screen-p4` | 1013 | 2.07 | 5.185 | 5.159 | — | 4.974 | 1.715 | 400 | 1.04× | 3.01× | — |
| `screen-p1` | 253 | 2.07 | 5.166 | 5.158 | — | 4.696 | 1.142 | 401 | 1.10× | 4.51× | — |
| `screen-rle8` | 195 | 2.07 | 0.9064 | 0.9043 | — | 4.799 | 2.710 | 2288 | 0.19× | 0.33× | — |
| `screen-rle4` | 143 | 2.07 | 9.104 | 9.082 | — | 4.896 | 2.894 | 228 | 1.86× | 3.14× | — |
| `screen-ico256-bmp32` | 264 | 0.07 | 0.0151 | 0.0150 | — | — | 0.3863 | 4345 | — | 0.04× | — |
| `screen-ico256-png` | 1 | 0.07 | 0.1669 | 0.1778 | — | — | 0.2295 | 393 | — | 0.77× | — |
| `screen-ico48-bmp8` | 3 | 0.00 | 0.0033 | 0.0034 | — | — | 0.0530 | 692 | — | 0.06× | — |

Geometric means over the cases both decode:

- current ÷ ImageSharp, photo: 0.25× (11 cases)
- current ÷ ImageSharp, screen: 0.21× (11 cases)
- current BGR(A) ÷ WIC, photo: 0.32× (14 cases)
- current BGR(A) ÷ WIC, screen: 0.28× (14 cases)

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool and WIC allocates natively, so neither shows here: compare working sets with `peakmem`.

| Case | current | current BGR(A) | baseline | ImageSharp | WIC |
|---|---:|---:|---:|---:|---:|
| `photo-rgb24` | 0.0 | 0.0 | — | 2.1 | 2.2 |
| `photo-rgb24-topdown` | 0.0 | 0.0 | — | 2.1 | 2.2 |
| `photo-bgra32` | 0.0 | 0.0 | — | 2.1 | 2.2 |
| `photo-argb32-v4` | 0.0 | 0.0 | — | 2.1 | 2.2 |
| `photo-rgb16` | 0.0 | 0.0 | — | 2.1 | 2.2 |
| `photo-rgb565` | 0.0 | 0.0 | — | 2.1 | 2.2 |
| `photo-p8` | 0.0 | 0.0 | — | 3.2 | 23.4 |
| `photo-p4` | 0.0 | 0.0 | — | 2.2 | 3.6 |
| `photo-p1` | 0.0 | 0.0 | — | 2.2 | 2.6 |
| `photo-rle8` | 0.0 | 0.0 | — | 100.3 | 2.2 |
| `photo-rle4` | 0.0 | 0.0 | — | 2.4 | 2.2 |
| `photo-ico256-bmp32` | 0.1 | 0.1 | — | — | 2.7 |
| `photo-ico256-png` | 1.8 | 0.7 | — | — | 2.7 |
| `photo-ico48-bmp8` | 0.1 | 0.1 | — | — | 8.0 |
| `screen-rgb24` | 0.0 | 0.0 | — | 2.1 | 2.2 |
| `screen-rgb24-topdown` | 0.0 | 0.0 | — | 2.1 | 2.2 |
| `screen-bgra32` | 0.0 | 0.0 | — | 2.1 | 2.2 |
| `screen-argb32-v4` | 0.0 | 0.0 | — | 2.1 | 2.2 |
| `screen-rgb16` | 0.0 | 0.0 | — | 2.1 | 2.2 |
| `screen-rgb565` | 0.0 | 0.0 | — | 2.1 | 2.2 |
| `screen-p8` | 0.0 | 0.0 | — | 3.2 | 23.4 |
| `screen-p4` | 0.0 | 0.0 | — | 2.2 | 3.6 |
| `screen-p1` | 0.0 | 0.0 | — | 2.2 | 2.6 |
| `screen-rle8` | 0.0 | 0.0 | — | 3.3 | 2.2 |
| `screen-rle4` | 0.0 | 0.0 | — | 2.4 | 2.2 |
| `screen-ico256-bmp32` | 0.1 | 0.1 | — | — | 2.7 |
| `screen-ico256-png` | 1.8 | 0.7 | — | — | 2.7 |
| `screen-ico48-bmp8` | 0.1 | 0.1 | — | — | 8.1 |

### Encode (in-process medians of 9 interleaved rounds, ms; sizes in KB)

`current` = `BmpEncoder.Encode` from RGBA (`IcoEncoder.Encode` for the icon set); `current BGR(A)` = the same from the B, G, R(, A) input WIC gets; ImageSharp = `BmpEncoder` with the same bits per pixel into a reused stream, one thread, from an `Image<Rgba32>` built beforehand; WIC = WPF's `BmpBitmapEncoder` over a `BitmapSource` of B, G, R(, A), into a reused stream (24- and 32-bit only). Every output decodes, and the lossless layouts give the source back.

| Case | current | KB | current BGR(A) | baseline | ImageSharp | KB | WIC | KB | ÷ ImageSharp | ÷ WIC | ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-rgb24` | 0.7911 | 6075 | 0.7596 | — | 0.3280 | 6075 | 4.723 | 6075 | 2.41× | 0.16× | — |
| `photo-argb32` | 1.239 | 8100 | 1.016 | — | 0.3212 | 8100 | 5.650 | 8100 | 3.86× | 0.18× | — |
| `photo-rgb565` | 7.519 | 4050 | — | — | 14.6 | 4050 | — | — | 0.52× | — | — |
| `photo-p8` | 268.6 | 2026 | — | — | 209.7 | 2026 | — | — | 1.28× | — | — |
| `photo-rle8` | 264.3 | 2067 | — | — | — | — | — | — | — | — | — |
| `photo-ico-set` | 6.945 | 144 | — | — | — | — | — | — | — | — | — |
| `screen-rgb24` | 0.8421 | 6075 | 0.8179 | — | 0.2887 | 6075 | 4.515 | 6075 | 2.92× | 0.18× | — |
| `screen-argb32` | 0.9089 | 8100 | 1.029 | — | 0.2691 | 8100 | 5.427 | 8100 | 3.38× | 0.19× | — |
| `screen-rgb565` | 7.447 | 4050 | — | — | 14.6 | 4050 | — | — | 0.51× | — | — |
| `screen-p8` | 30.6 | 2026 | — | — | 76.3 | 2026 | — | — | 0.40× | — | — |
| `screen-rle8` | 31.2 | 195 | — | — | — | — | — | — | — | — | — |
| `screen-ico-set` | 1.581 | 16 | — | — | — | — | — | — | — | — | — |

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool and WIC allocates natively, so neither shows here: compare working sets with `peakmem`.

| Case | current | current BGR(A) | baseline | ImageSharp | WIC |
|---|---:|---:|---:|---:|---:|
| `photo-rgb24` | 6075.1 | 6075.1 | — | 0.2 | 6102.1 |
| `photo-argb32` | 8100.1 | 8100.1 | — | 0.2 | 8127.1 |
| `photo-rgb565` | 4050.1 | — | — | 0.2 | — |
| `photo-p8` | 13906.2 | — | — | 25896.7 | — |
| `photo-rle8` | 23100.7 | — | — | — | — |
| `photo-ico-set` | 1051.7 | — | — | — | — |
| `screen-rgb24` | 6075.1 | 6075.1 | — | 0.2 | 6102.1 |
| `screen-argb32` | 8100.1 | 8100.1 | — | 0.2 | 8127.1 |
| `screen-rgb565` | 4050.1 | — | — | 0.2 | — |
| `screen-p8` | 13906.2 | — | — | 3088.0 | — |
| `screen-rle8` | 13280.7 | — | — | — | — |
| `screen-ico-set` | 293.0 | — | — | — | — |

Notes:

- `photo-rgb565`: ImageSharp's 16-bit layout is its own (it may be 5-5-5); sizes are equal either way
- `photo-ico-set`: 16, 32 and 48 as 32-bit DIBs, 256 as PNG; neither ImageSharp 3.1 nor WIC writes icons
- `screen-rgb565`: ImageSharp's 16-bit layout is its own (it may be 5-5-5); sizes are equal either way
- `screen-ico-set`: 16, 32 and 48 as 32-bit DIBs, 256 as PNG; neither ImageSharp 3.1 nor WIC writes icons
