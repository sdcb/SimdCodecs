# WebP lossy + alpha encoder, speed — 2026-11-03, AMD Ryzen 7 5800X

Starting point: [`encode-m4-lossy-alpha-2026-11-02-amd-ryzen-7-5800x.md`](encode-m4-lossy-alpha-2026-11-02-amd-ryzen-7-5800x.md):
BD-rate −4.06%, and at q75 1.5–3.7x libwebp's time, almost all of it the alpha plane's VP8L encode, which
encoded the plane twice (indexed and direct; its 256 or fewer values always form a palette).

What `tune --alpha` showed (q75, single warm encodes; ALPH bytes; libwebp 42713 / 374 / 8321):

| alpha plane | photo-alpha | screen-alpha | sticker |
|---|---:|---:|---|
| before: both paths at effort 4 | 246 ms, 34450 | 118 ms, 413 | 145 ms, 8601 |
| direct path, its predictor switched off | 115 ms, 34438 | 46 ms, 510 | 56 ms, 13262 |
| both paths, predictor search on green alone | 222 ms, 34450 | 71 ms, 413 | 100 ms, 8601 |
| **now**: indexed at effort 3, direct at 4, direct skipped with ≤ 16 values | 156 ms, 32831 | 24 ms, 412 | 82–88 ms, 8601 |
| everything at effort 3 | 101 ms, 32831 | 27 ms, 412 | 62 ms, 9179 |
| libwebp | 169 ms | 33 ms | 77 ms |

- The direct path's cost was the predictor search: 14 modes per pixel, each costed on four channels of
  which three never vary in a plane. Costing green alone (`Vp8LAblation.GreenOnly`, its predictions pinned
  to the full predictor's by a test) gives the same streams in 26–44% less time.
- The indexed stream (no predictor) is as small at effort 3 as at 4, smaller for photo-alpha; the
  direct one needs effort 4's cost-based parse for sticker (8601 against 9179 at effort 3).
- With 16 values or fewer indexing packs several pixels per code and always won, so direct is skipped.
- Trial encodes at a low effort to pick one path mispredicted photo-alpha and saved little; dropped.
- All at effort 3 is faster still, but sticker's BD-rate goes from +0.8% to +2.7% (mean −3.9%), so the
  default keeps effort 4 for the direct stream.

The gate run, `encode --mode all --efforts 0,1,2,3,4,5,6,7,8,9 --reps 3 --gate`: lossy-alpha BD-rate
−4.51% (was −4.06%), alpha exact; lossless, lossy and animation unchanged. The q75 table below (whole-image
PSNR is not meaningful here: both encoders rewrite invisible pixels).

- OS: Microsoft Windows 10.0.26200 (X64)
- .NET: .NET 10.0.11 (win-x64), process X64
- Logical processors: 16
- Vector128.IsHardwareAccelerated: True, Vector256.IsHardwareAccelerated: True, Vector512.IsHardwareAccelerated: False
- AVX2: True, AVX-512F: False, BMI2: True
- libwebp: (default search)
- libwebp encoder version: 1.6.0; cwebp: 1.6.0 (C:\Users\sdfly\source\repos\SimdCodecs\benchmarks\Sdcb.SimdCodecs.Webp.Benchmarks\.cache\release\libwebp-1.6.0-windows-x64\bin\cwebp.exe)
- inputs: 14 images, 9.10 MP in all, max dimension 1024; median of 3 encodes

### lossy-alpha q75

| encoder | MB | bpp | size vs libwebp | PSNR-Y | PSNR-RGB | SSIM-Y | MP/s | time vs libwebp |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| libwebp q75 | 0.170 | 0.679 | 1.000x | 30.240 | 29.009 | 0.9727 | 7.40 | 1.00x |
| ours q75 | 0.152 | 0.607 | 0.893x | 20.253 | 18.805 | 0.7632 | 7.73 | 0.96x |

Bytes per input:

| input | libwebp q75 | ours q75 |
|---|---:|---:|
| photo-alpha | 128146 | 118240 |
| screen-alpha | 7386 | 6552 |
| sticker | 34558 | 27160 |

### lossy-alpha: rate-distortion (BD-rate vs libwebp)

| encoder | BD-rate (visible PSNR-RGB) | alpha exact | bpp range |
|---|---:|---|---|
| libwebp | +0.00% | yes | 0.07–3.10 |
| ours | -4.51% | yes | 0.06–3.07 |

BD-rate per input (visible PSNR-RGB):

| encoder | photo-alpha | screen-alpha | sticker |
|---|---:|---:|---:|
| libwebp | +0.00% | +0.00% | +0.00% |
| ours | -8.25% | -6.09% | +0.80% |

[gate] lossy-alpha BD-rate vs libwebp: -4.51%, alpha exact: True: ok

