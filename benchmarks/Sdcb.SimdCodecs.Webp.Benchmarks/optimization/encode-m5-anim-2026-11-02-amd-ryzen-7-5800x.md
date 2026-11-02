# WebP animation encoder (M5) — 2026-11-02, AMD Ryzen 7 5800X

`WebpAnimationEncoder` against `img2webp` 1.6.0 (libwebp's animation encoder; its time is the process wall
clock, frames read from PAM). From `encode --mode all --efforts 0,1,2,3,4,5,6,7,8,9 --reps 3 --gate`;
the lossless, lossy and lossy-alpha gates passed in the same run with the numbers of the earlier recordings.

- Lossless: 0.931x img2webp's bytes, every frame exact in anim_dump; 4.46 MP/s against 3.09.
- Lossy: BD-rate on visible PSNR-RGB −10.95% (scroll −3.5%, sprite −14.8%, alpha-sprite −22.5%,
  slides −2.9%). `fade` has none: img2webp's lossy output keeps small changes as "unchanged" and its
  quality never rises above 30.8 dB, ours starts at 31.1 dB (q90: 917104 bytes at 37.3 dB against
  890962 at 30.8 dB), so the curves share no quality interval.
- Per sequence (`tune --anim`), lossless bytes ours / img2webp: scroll 190748 / 362552, sprite
  262712 / 254518, fade 4394796 / 4642092, alpha-sprite 31252 / 28658, slides 733392 / 738832.

How the encoder decides, and what `tune --anim` showed:

- Unchanged frames lengthen the previous one; otherwise the bounding box of the changed pixels (offsets
  even) is drawn over the canvas, or blended with its unchanged pixels transparent, and the whole canvas
  is a candidate when the changes cover half of it; the smallest file wins.
- Blending is used only where every changed pixel is opaque. Blending partly transparent pixels over a
  transparent canvas was first allowed (the format's arithmetic returns the source there), and
  anim_dump's output differed from ours by one in RGB: its rounding is not this package's decoder's.
  See docs/known-gaps.md.
- Clearing the previous frame is tried where the new frame is transparent over it (or always at the
  exhaustive level). The exhaustive search (`Search=2`, lossy effort 6 / lossless effort 8 and up)
  saved 2% on scroll lossless only, at 1.7–12x the time; the default level matches it elsewhere.

- OS: Microsoft Windows 10.0.26200 (X64)
- .NET: .NET 10.0.11 (win-x64), process X64
- Logical processors: 16
- Vector128.IsHardwareAccelerated: True, Vector256.IsHardwareAccelerated: True, Vector512.IsHardwareAccelerated: False
- AVX2: True, AVX-512F: False, BMI2: True
- libwebp: (default search)
- libwebp encoder version: 1.6.0; cwebp: 1.6.0 (C:\Users\sdfly\source\repos\SimdCodecs\benchmarks\Sdcb.SimdCodecs.Webp.Benchmarks\.cache\release\libwebp-1.6.0-windows-x64\bin\cwebp.exe)
- inputs: 14 images, 9.10 MP in all, max dimension 1024; median of 3 encodes

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
| img2webp -lossless | 6.027 | 2.616 | 1.000x | 3.09 | exact |
| img2webp -mixed | 0.847 | 0.368 | 0.141x | 23.24 | lowest PSNR 22.8 dB |
| ours | 5.613 | 2.436 | 0.931x | 4.46 | exact |
| ours mixed | 0.768 | 0.333 | 0.127x | 2.94 | lowest PSNR 31.5 dB |

[gate] anim lossless ours 5612900 bytes vs img2webp -lossless 6026652: ok

| encoder | BD-rate (visible PSNR-RGB) vs img2webp -lossy | per sequence |
|---|---:|---|
| img2webp -lossy | +0.00% | scroll +0.0%, sprite +0.0%, fade +0.0%, alpha-sprite +0.0%, slides +0.0% |
| ours lossy | -10.95% | scroll -3.5%, sprite -14.8%, fade NaN%, alpha-sprite -22.5%, slides -2.9% |

[gate] anim lossy BD-rate vs img2webp: -10.95%: ok
