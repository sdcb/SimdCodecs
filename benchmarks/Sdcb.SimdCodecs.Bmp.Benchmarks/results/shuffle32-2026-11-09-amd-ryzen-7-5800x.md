# BMP 32-bit byte shuffle (`BmpKernels.Shuffle32`), 2026-11-09, AMD Ryzen 7 5800X

The change: `Mask32` builds the shuffle mask in registers and is inlined, instead of a call that fills a `stackalloc` span; the 256-bit loop does four vectors a step. Measured against `6448ba5` (the commit before it) loaded side by side as `baseline`. Command: `dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Bmp.Benchmarks -- matrix all --cases 'bgra32|argb32' --rounds 15` (three runs) and `matrix encode --cases argb32 --rounds 31` (two runs). Other builds were running on the machine at the same time, so single numbers move by up to 40% between runs; the ratios against `baseline` come from the same rounds and are steadier.

- CPU: AMD Ryzen 7 5800X 8-Core Processor
- OS: Microsoft Windows 10.0.26200 (X64)
- .NET: .NET 10.0.11 (win-x64), process X64
- Logical processors: 16
- Vector128.IsHardwareAccelerated: True, Vector256.IsHardwareAccelerated: True, Vector512.IsHardwareAccelerated: False
- AVX2: True, AVX-512F: False
- Bitmap size: 1920×1080
- `photo` source: synthetic (no JPEG corpus master in the worktree's cache)
- `screen` source: synthetic
- ImageSharp: 3.1.12
- WIC: WindowsCodecs.dll 10.0.26100.8115 (WinBuild.160101.0800), through WPF
- Baseline: 6448ba529395 Merge scaled-decode: scaled and region decode for every codec (#10)

## Cause

RGBA → 32-bit BMP into a stream took about 0.36 ms against 0.22 ms from BGRA input, which is a plain copy. Ablation, with temporary switches in the encoder, one process, 5 repeats of 9 interleaved rounds (ms, range of the 5 medians), old kernel:

| Variant | ms |
|---|---:|
| RGBA → Argb32 | 0.359–0.378 |
| RGBA → Rgb32 | 0.349–0.370 |
| BGRA → Argb32 (copy) | 0.216–0.229 |
| RGBA → Argb32, `Shuffle32` replaced by `CopyTo` | 0.216–0.228 |
| RGBA → Argb32, no `Stream.Write` | 0.242–0.251 |
| BGRA → Argb32, no `Stream.Write` | 0.110–0.115 |
| RGBA → Argb32, no row kernel | 0.120–0.124 |
| no row kernel, no `Stream.Write` | 0.005 |

Swapping the shuffle for a copy closes the whole gap, and the stream write costs the same 0.12 ms on both inputs: the extra 0.13 ms is in `Shuffle32` alone, which ran at half the speed of a copy. The JIT's code (`DOTNET_JitDisasm=Shuffle32`) showed why: the loop index `x` lived in a stack slot across the 256-bit loop (`mov edx, [rsp+0x64]` … `mov [rsp+0x64], edx` every 32 bytes), a store-to-load chain of about 4–5 cycles per step. It was spilled because it is live across the two `Mask32` calls, which the JIT does not inline (they `stackalloc`). With `Mask32` inlined and built from a broadcast, a compare and a select, the loop keeps everything in registers. The per-row cost the task suspected (mask built per call, the `seen` reduction, the 128-bit and scalar tails) is not visible: a 1920-pixel row is 60 steps of the 256-bit loop and nothing else.

The same probe after the change (a separate process, so read it as attribution, not as the A/B):

| Variant | ms |
|---|---:|
| RGBA → Argb32, mask fix only | 0.272–0.290 |
| RGBA → Argb32, mask fix + four vectors a step | 0.250–0.267 |
| RGBA → Rgb32, mask fix + four vectors a step | 0.247–0.270 |
| BGRA → Argb32 (copy) | 0.203–0.210 |
| RGBA → Argb32, no `Stream.Write`, mask fix only | 0.161–0.164 |
| RGBA → Argb32, no `Stream.Write`, four vectors a step | 0.138–0.143 |
| BGRA → Argb32, no `Stream.Write` | 0.101–0.103 |

Tried and dropped: software prefetch 1 KB ahead (no gain). The kernel is still about 0.04 ms behind a copy of the same bytes; a load-store-only loop of the same shape measured between the two, so what is left is the shuffle's place in the load-to-store chain, not loop overhead. Vector512 was not tried: this CPU has no AVX-512 and no BMP kernel has a 512-bit tier.

## A/B, encode (ms, medians per run; `stream ÷ baseline` and `stream ÷ ImageSharp` are per run)

| Run | Case | current → stream | baseline → stream | current BGR(A) | ImageSharp | stream ÷ baseline | stream ÷ ImageSharp |
|---|---|---:|---:|---:|---:|---:|---:|
| 1 | `photo-argb32` | 0.2562 | 0.3890 | 0.2232 | 0.2218 | 0.66× | 1.15× |
| 1 | `screen-argb32` | 0.2579 | 0.3802 | 0.3382 | 0.2621 | 0.68× | 0.98× |
| 2 | `photo-argb32` | 0.2639 | 0.3569 | 0.2701 | 0.2623 | 0.74× | 1.01× |
| 2 | `screen-argb32` | 0.2718 | 0.4538 | 0.3549 | 0.3424 | 0.60× | 0.79× |
| 3 | `photo-argb32` | 0.2833 | 0.3461 | 0.2765 | 0.2826 | 0.82× | 1.00× |
| 3 | `screen-argb32` | 0.3627 | 0.3666 | 0.3683 | 0.2673 | 0.99× | 1.36× |
| 4 | `photo-argb32` | 0.2746 | 0.3545 | 0.2519 | 0.2179 | 0.77× | 1.26× |
| 4 | `screen-argb32` | 0.4043 | 0.5499 | 0.4013 | 0.3214 | 0.74× | 1.26× |
| 5 | `photo-argb32` | 0.3221 | 0.5172 | 0.2857 | 0.3181 | 0.62× | 1.01× |
| 5 | `screen-argb32` | 0.3202 | 0.4621 | 0.2934 | 0.2488 | 0.69× | 1.29× |

Into a stream, RGBA input is now 0.60–0.82× the baseline (one run at 0.99×), and within 0–15% of the BGRA copy path in most runs. Against ImageSharp it is 0.79–1.36× by run, median 1.08×. The array overload (`current`, 0.64–0.95 ms) is dominated by allocating and first touching the 8 MB result, so the kernel moves it less: 0.79–1.10× the baseline by run.

## A/B, decode (ms, runs 1–3)

`Shuffle32` is also the decoder's 32-bit path to RGBA, so decode moves too.

| Case | current | baseline | ÷ baseline, runs 1–3 |
|---|---:|---:|---|
| `photo-bgra32` | 0.1843–0.3533 | 0.2642–0.4484 | 0.79×, 0.70×, 0.70× |
| `photo-argb32-v4` | 0.1687–0.2048 | 0.2630–0.2749 | 0.65×, 0.64×, 0.75× |
| `screen-bgra32` | 0.1858–0.1961 | 0.2385–0.2527 | 0.78×, 0.77×, 0.78× |
| `screen-argb32-v4` | 0.1695–0.1951 | 0.2388–0.2475 | 0.79×, 0.71×, 0.70× |
