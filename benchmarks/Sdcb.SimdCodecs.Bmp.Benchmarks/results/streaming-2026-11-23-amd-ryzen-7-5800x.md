# BMP streaming reader and encoder session (240c053 vs 533b03e)

`matrix all --rounds 31`, run while other jobs shared the machine: differences under ~5% between `current` and `baseline` are noise. `rows ÷ current` is the streaming reader over a `MemoryStream` against the span decoder on bytes already in memory (the reader copies every byte into its buffer, twice for a bottom-up file); `rows ÷ read whole` is against reading the stream into a buffer and then decoding it, which is what streaming replaces.

- CPU: AMD Ryzen 7 5800X 8-Core Processor
- OS: Microsoft Windows 10.0.26200 (X64)
- .NET: .NET 10.0.15 (win-x64), process X64
- Logical processors: 16
- Vector128.IsHardwareAccelerated: True, Vector256.IsHardwareAccelerated: True, Vector512.IsHardwareAccelerated: False
- AVX2: True, AVX-512F: False
- Bitmap size: 1920×1080; icons 256×256 and 48×48
- `photo` source: centre crop of landscape-almsee.png (JPEG benchmark corpus master)
- `screen` source: synthetic
- ImageSharp: 3.1.12
- WIC: WindowsCodecs.dll 10.0.26100.8115 (WinBuild.160101.0800), through WPF
- Baseline: 533b03e2f35a Merge feature/streaming-foundation: streaming plumbing in Abstractions

### Decode (in-process medians of 31 interleaved rounds, ms)

`current` = `BmpImageDecoder` / `IcoImageDecoder.TryDecodeInto` to RGBA in a caller buffer; `current BGR(A)` = the same to BGRA, the layout WIC produces; ImageSharp = `Image.Load<Rgba32>` + `CopyPixelDataTo`, one thread (it reads no icons); WIC = WPF's `BitmapDecoder` + `FormatConvertedBitmap` to BGRA. Ratios below 1 mean we are faster: current ÷ ImageSharp (both RGBA), current BGR(A) ÷ WIC (both BGRA). Every timed output was checked against the others first. `current rows ← stream` = the streaming reader (`BmpImageDecoder.CreateRowReader`) over a seekable `MemoryStream`, RGBA rows 64 at a time; `current ← read whole stream` = the same stream read whole into a reused buffer, then `TryDecodeInto`: what streaming replaces. `rows ÷ current` is what streaming costs over decoding bytes already in memory, `rows ÷ read whole` over reading the stream first.

| Case | KB | MP | current | current rows ← stream | current ← read whole stream | current BGR(A) | baseline | ImageSharp | WIC | current MP/s | ÷ ImageSharp | ÷ WIC | ÷ baseline | rows ÷ current | rows ÷ read whole |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-rgb24` | 6075 | 2.07 | 0.2921 | 0.5290 | 0.6277 | 0.2978 | 0.3002 | 1.251 | 2.241 | 7098 | 0.23× | 0.13× | 0.97× | 1.81× | 0.84× |
| `photo-rgb24-topdown` | 6075 | 2.07 | 0.2814 | 0.3956 | 0.6313 | 0.2859 | 0.2785 | 1.238 | 2.292 | 7369 | 0.23× | 0.12× | 1.01× | 1.41× | 0.63× |
| `photo-bgra32` | 8100 | 2.07 | 0.2409 | 0.6070 | 0.8679 | 0.2339 | 0.2355 | 5.814 | 2.295 | 8606 | 0.04× | 0.10× | 1.02× | 2.52× | 0.70× |
| `photo-argb32-v4` | 8100 | 2.07 | 0.2023 | 0.5328 | 0.7614 | 0.2144 | 0.2104 | 5.719 | 2.134 | 10251 | 0.04× | 0.10× | 0.96× | 2.63× | 0.70× |
| `photo-rgb16` | 4050 | 2.07 | 0.6309 | 0.7667 | 0.9076 | 0.5015 | 0.5037 | 7.668 | 5.636 | 3287 | 0.08× | 0.09× | 1.25× | 1.22× | 0.84× |
| `photo-rgb565` | 4050 | 2.07 | 0.3917 | 0.5617 | 0.6006 | 0.3949 | 0.3995 | 6.237 | 4.562 | 5294 | 0.06× | 0.09× | 0.98× | 1.43× | 0.94× |
| `photo-p8` | 2026 | 2.07 | 0.9288 | 1.041 | 1.022 | 0.9295 | 0.9302 | 5.135 | 2.599 | 2233 | 0.18× | 0.36× | 1.00× | 1.12× | 1.02× |
| `photo-p4` | 1013 | 2.07 | 0.7079 | 0.7689 | 0.7380 | 0.7058 | 0.6453 | 5.083 | 1.784 | 2929 | 0.14× | 0.40× | 1.10× | 1.09× | 1.04× |
| `photo-p1` | 253 | 2.07 | 0.5579 | 0.5701 | 0.5666 | 0.5602 | 0.5605 | 4.776 | 1.203 | 3717 | 0.12× | 0.47× | 1.00× | 1.02× | 1.01× |
| `photo-rle8` | 2074 | 2.07 | 2.077 | 2.971 | 2.200 | 2.096 | 2.132 | 6.314 | 4.135 | 998 | 0.33× | 0.51× | 0.97× | 1.43× | 1.35× |
| `photo-rle4` | 1345 | 2.07 | 4.342 | 5.134 | 4.403 | 4.369 | 4.304 | 9.830 | 5.432 | 478 | 0.44× | 0.80× | 1.01× | 1.18× | 1.17× |
| `photo-ico256-bmp32` | 264 | 0.07 | 0.0075 | — | — | 0.0077 | 0.0082 | — | 0.4061 | 8778 | — | 0.02× | 0.91× | — | — |
| `photo-ico256-png` | 129 | 0.07 | 1.008 | — | — | 1.062 | 1.007 | — | 0.9823 | 65 | — | 1.08× | 1.00× | — | — |
| `photo-ico48-bmp8` | 4 | 0.00 | 0.0042 | — | — | 0.0055 | 0.0035 | — | 0.0727 | 542 | — | 0.08× | 1.23× | — | — |
| `screen-rgb24` | 6075 | 2.07 | 0.2869 | 0.5112 | 0.6107 | 0.2859 | 0.2878 | 1.234 | 2.158 | 7227 | 0.23× | 0.13× | 1.00× | 1.78× | 0.84× |
| `screen-rgb24-topdown` | 6075 | 2.07 | 0.2756 | 0.3922 | 0.5945 | 0.2743 | 0.2761 | 1.168 | 2.228 | 7523 | 0.24× | 0.12× | 1.00× | 1.42× | 0.66× |
| `screen-bgra32` | 8100 | 2.07 | 0.1871 | 0.4828 | 0.6944 | 0.1847 | 0.1724 | 5.533 | 2.093 | 11084 | 0.03× | 0.09× | 1.08× | 2.58× | 0.70× |
| `screen-argb32-v4` | 8100 | 2.07 | 0.2239 | 0.5292 | 0.8339 | 0.2033 | 0.2414 | 6.313 | 2.585 | 9261 | 0.04× | 0.08× | 0.93× | 2.36× | 0.63× |
| `screen-rgb16` | 4050 | 2.07 | 0.4115 | 0.5886 | 0.6529 | 0.4021 | 0.4021 | 6.253 | 4.934 | 5039 | 0.07× | 0.08× | 1.02× | 1.43× | 0.90× |
| `screen-rgb565` | 4050 | 2.07 | 0.3945 | 0.5577 | 0.6036 | 0.3972 | 0.4044 | 6.330 | 4.640 | 5256 | 0.06× | 0.09× | 0.98× | 1.41× | 0.92× |
| `screen-p8` | 2026 | 2.07 | 0.9111 | 1.017 | 1.000 | 0.9124 | 0.9160 | 5.058 | 2.486 | 2276 | 0.18× | 0.37× | 0.99× | 1.12× | 1.02× |
| `screen-p4` | 1013 | 2.07 | 0.6931 | 0.7504 | 0.7220 | 0.6921 | 0.6342 | 5.026 | 1.736 | 2992 | 0.14× | 0.40× | 1.09× | 1.08× | 1.04× |
| `screen-p1` | 253 | 2.07 | 0.5298 | 0.5559 | 0.5349 | 0.5365 | 0.5347 | 4.548 | 1.151 | 3914 | 0.12× | 0.47× | 0.99× | 1.05× | 1.04× |
| `screen-rle8` | 190 | 2.07 | 0.5376 | 0.7986 | 0.5612 | 0.5292 | 0.5792 | 4.772 | 2.528 | 3857 | 0.11× | 0.21× | 0.93× | 1.49× | 1.42× |
| `screen-rle4` | 145 | 2.07 | 0.5862 | 0.9181 | 0.6189 | 0.5962 | 0.6053 | 5.177 | 2.867 | 3537 | 0.11× | 0.21× | 0.97× | 1.57× | 1.48× |
| `screen-ico256-bmp32` | 264 | 0.07 | 0.0080 | — | — | 0.0079 | 0.0077 | — | 0.4001 | 8228 | — | 0.02× | 1.03× | — | — |
| `screen-ico256-png` | 1 | 0.07 | 0.1713 | — | — | 0.2234 | 0.1719 | — | 0.2353 | 383 | — | 0.95× | 1.00× | — | — |
| `screen-ico48-bmp8` | 3 | 0.00 | 0.0035 | — | — | 0.0038 | 0.0032 | — | 0.0537 | 666 | — | 0.07× | 1.07× | — | — |

Geometric means over the cases both decode:

- current ÷ ImageSharp, photo: 0.13× (11 cases)
- current ÷ ImageSharp, screen: 0.10× (11 cases)
- current BGR(A) ÷ WIC, photo: 0.18× (14 cases)
- current BGR(A) ÷ WIC, screen: 0.15× (14 cases)
- current ÷ baseline, photo: 1.03× (14 cases)
- current ÷ baseline, screen: 1.00× (14 cases)
- current rows ← stream ÷ current, photo: 1.46× (11 cases)
- current rows ← stream ÷ current, screen: 1.51× (11 cases)
- current rows ← stream ÷ current ← read whole stream, photo: 0.91× (11 cases)
- current rows ← stream ÷ current ← read whole stream, screen: 0.93× (11 cases)

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool and WIC allocates natively, so neither shows here: compare working sets with `peakmem`.

| Case | current | current rows ← stream | current ← read whole stream | current BGR(A) | baseline | ImageSharp | WIC |
|---|---:|---:|---:|---:|---:|---:|---:|
| `photo-rgb24` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 2.1 | 2.2 |
| `photo-rgb24-topdown` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 2.1 | 2.2 |
| `photo-bgra32` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 2.1 | 2.2 |
| `photo-argb32-v4` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 2.1 | 2.2 |
| `photo-rgb16` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 2.1 | 2.2 |
| `photo-rgb565` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 2.1 | 2.2 |
| `photo-p8` | 0.0 | 1.5 | 0.1 | 0.0 | 0.0 | 3.2 | 23.4 |
| `photo-p4` | 0.0 | 1.5 | 0.1 | 0.0 | 0.0 | 2.2 | 3.6 |
| `photo-p1` | 0.0 | 1.5 | 0.1 | 0.0 | 0.0 | 2.2 | 2.6 |
| `photo-rle8` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 215.5 | 2.2 |
| `photo-rle4` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 2.4 | 2.2 |
| `photo-ico256-bmp32` | 0.1 | — | — | 0.1 | 0.1 | — | 2.7 |
| `photo-ico256-png` | 1.8 | — | — | 0.8 | 1.8 | — | 2.7 |
| `photo-ico48-bmp8` | 0.1 | — | — | 0.1 | 0.1 | — | 23.9 |
| `screen-rgb24` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 2.1 | 2.2 |
| `screen-rgb24-topdown` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 2.1 | 2.2 |
| `screen-bgra32` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 2.1 | 2.2 |
| `screen-argb32-v4` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 2.1 | 2.2 |
| `screen-rgb16` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 2.1 | 2.2 |
| `screen-rgb565` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 2.1 | 2.2 |
| `screen-p8` | 0.0 | 1.5 | 0.1 | 0.0 | 0.0 | 3.2 | 23.4 |
| `screen-p4` | 0.0 | 1.5 | 0.1 | 0.0 | 0.0 | 2.2 | 3.6 |
| `screen-p1` | 0.0 | 1.5 | 0.1 | 0.0 | 0.0 | 2.2 | 2.6 |
| `screen-rle8` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 3.3 | 2.2 |
| `screen-rle4` | 0.0 | 0.5 | 0.1 | 0.0 | 0.0 | 2.4 | 2.2 |
| `screen-ico256-bmp32` | 0.1 | — | — | 0.1 | 0.1 | — | 2.7 |
| `screen-ico256-png` | 1.8 | — | — | 0.8 | 1.8 | — | 2.7 |
| `screen-ico48-bmp8` | 0.1 | — | — | 0.1 | 0.1 | — | 8.1 |

### Encode (in-process medians of 31 interleaved rounds, ms; sizes in KB)

`current` = `BmpEncoder.Encode` from RGBA to a new array (`IcoEncoder.Encode` for the icon set); `current → stream` = the `Stream` overload into a reused stream, as ImageSharp and WIC write; `baseline` / `baseline → stream` = the same two for the previous commit; `current BGR(A)` = that from the B, G, R(, A) input WIC gets; ImageSharp = `BmpEncoder` with the same bits per pixel into a reused stream, one thread, from an `Image<Rgba32>` built beforehand; WIC = WPF's `BmpBitmapEncoder` over a `BitmapSource` of B, G, R(, A), into a reused stream (24- and 32-bit only); `current session → writer` = `BmpImageEncoder.Create`, 64 rows per `WriteRows` into a reused `ArrayBufferWriter` (streaming only for the top-down cases). Every output decodes, and the lossless layouts give the source back.

| Case | current | KB | current → stream | current session → writer | current BGR(A) | baseline | baseline → stream | ImageSharp | KB | WIC | KB | stream ÷ ImageSharp | BGR(A) ÷ WIC | ÷ baseline | stream ÷ baseline | session ÷ stream |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-rgb24` | 0.5800 | 6075 | 0.3636 | — | 0.3303 | 0.5943 | 0.4398 | 0.3062 | 6075 | 4.840 | 6075 | 1.19× | 0.07× | 0.98× | 0.83× | — |
| `photo-argb32` | 0.9200 | 8100 | 0.2433 | — | 0.2332 | 0.7971 | 0.2574 | 0.2363 | 8100 | 5.335 | 8100 | 1.03× | 0.04× | 1.15× | 0.95× | — |
| `photo-rgb24-topdown` | 0.5797 | 6075 | 0.3439 | 0.3106 | 0.1745 | 0.5167 | 0.3342 | 0.3018 | 6075 | 4.314 | 6075 | 1.14× | 0.04× | 1.12× | 1.03× | 0.90× |
| `photo-argb32-topdown` | 0.6951 | 8100 | 0.4278 | 0.2958 | 0.4432 | 0.7222 | 0.4091 | 0.2304 | 8100 | 5.166 | 8100 | 1.86× | 0.09× | 0.96× | 1.05× | 0.69× |
| `photo-rgb565` | 2.176 | 4050 | 2.226 | — | — | 2.128 | 2.228 | 14.7 | 4050 | — | — | 0.15× | — | 1.02× | 1.00× | — |
| `photo-p8` | 100.8 | 2026 | 103.8 | — | — | 100.5 | 100.6 | 216.4 | 2026 | — | — | 0.48× | — | 1.00× | 1.03× | — |
| `photo-rle8` | 104.6 | 2074 | 104.5 | — | — | 103.7 | 103.2 | — | — | — | — | — | — | 1.01× | 1.01× | — |
| `photo-ico-set` | 9.561 | 144 | — | — | — | — | — | — | — | — | — | — | — | — | — | — |
| `screen-rgb24` | 0.8539 | 6075 | 0.7664 | — | 0.2450 | 0.8190 | 0.4614 | 0.6124 | 6075 | 6.978 | 6075 | 1.25× | 0.04× | 1.04× | 1.66× | — |
| `screen-argb32` | 0.6229 | 8100 | 0.2715 | — | 0.2340 | 0.6506 | 0.2800 | 0.2260 | 8100 | 5.339 | 8100 | 1.20× | 0.04× | 0.96× | 0.97× | — |
| `screen-rgb24-topdown` | 0.5381 | 6075 | 0.3549 | 0.3152 | 0.2979 | 0.4647 | 0.3423 | 0.3035 | 6075 | 4.352 | 6075 | 1.17× | 0.07× | 1.16× | 1.04× | 0.89× |
| `screen-argb32-topdown` | 1.173 | 8100 | 0.2759 | 0.2728 | 0.2283 | 0.8637 | 0.2748 | 0.2314 | 8100 | 5.844 | 8100 | 1.19× | 0.04× | 1.36× | 1.00× | 0.99× |
| `screen-rgb565` | 2.396 | 4050 | 2.212 | — | — | 2.348 | 2.194 | 14.9 | 4050 | — | — | 0.15× | — | 1.02× | 1.01× | — |
| `screen-p8` | 44.1 | 2026 | 49.6 | — | — | 50.5 | 49.4 | 105.4 | 2026 | — | — | 0.47× | — | 0.87× | 1.00× | — |
| `screen-rle8` | 72.6 | 190 | 66.8 | — | — | 69.7 | 69.2 | — | — | — | — | — | — | 1.04× | 0.96× | — |
| `screen-ico-set` | 4.517 | 16 | — | — | — | — | — | — | — | — | — | — | — | — | — | — |

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool and WIC allocates natively, so neither shows here: compare working sets with `peakmem`.

| Case | current | current → stream | current session → writer | current BGR(A) | baseline | baseline → stream | ImageSharp | WIC |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-rgb24` | 6075.1 | 0.0 | — | 0.0 | 6075.1 | 0.0 | 0.2 | 6102.1 |
| `photo-argb32` | 8100.1 | 0.0 | — | 0.0 | 8100.1 | 0.0 | 0.2 | 8127.1 |
| `photo-rgb24-topdown` | 6075.1 | 0.0 | 0.2 | 0.0 | 6075.1 | 0.0 | 0.2 | 6102.1 |
| `photo-argb32-topdown` | 8100.1 | 0.0 | 0.3 | 0.0 | 8100.1 | 0.0 | 0.2 | 8127.1 |
| `photo-rgb565` | 4050.1 | 0.0 | — | — | 4050.1 | 0.0 | 0.2 | — |
| `photo-p8` | 35764.2 | 33738.2 | — | — | 35764.2 | 33738.1 | 25896.7 | — |
| `photo-rle8` | 44972.6 | 42898.7 | — | — | 44972.6 | 42898.7 | — | — |
| `photo-ico-set` | 1051.7 | — | — | — | — | — | — | — |
| `screen-rgb24` | 6075.1 | 0.0 | — | 0.0 | 6075.1 | 0.0 | 0.2 | 6102.1 |
| `screen-argb32` | 8100.1 | 0.0 | — | 0.0 | 8100.1 | 0.0 | 0.2 | 8127.1 |
| `screen-rgb24-topdown` | 6075.1 | 0.0 | 0.2 | 0.0 | 6075.1 | 0.0 | 0.2 | 6102.1 |
| `screen-argb32-topdown` | 8100.1 | 0.0 | 0.3 | 0.0 | 8100.1 | 0.0 | 0.2 | 8127.1 |
| `screen-rgb565` | 4050.1 | 0.0 | — | — | 4050.1 | 0.0 | 0.2 | — |
| `screen-p8` | 16526.9 | 14500.8 | — | — | 16526.9 | 14500.8 | 3088.0 | — |
| `screen-rle8` | 15892.2 | 15702.2 | — | — | 15892.2 | 15702.2 | — | — |
| `screen-ico-set` | 293.0 | — | — | — | — | — | — | — |

Notes:

- `photo-rgb565`: ImageSharp's 16-bit layout is its own (it may be 5-5-5); sizes are equal either way
- `photo-ico-set`: 16, 32 and 48 as 32-bit DIBs, 256 as PNG; neither ImageSharp 3.1 nor WIC writes icons
- `screen-rgb565`: ImageSharp's 16-bit layout is its own (it may be 5-5-5); sizes are equal either way
- `screen-ico-set`: 16, 32 and 48 as 32-bit DIBs, 256 as PNG; neither ImageSharp 3.1 nor WIC writes icons
