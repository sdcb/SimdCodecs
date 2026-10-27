# WebP decode baseline — 2026-10-25, AMD Ryzen 7 5800X

The starting point for decode optimisation. Re-measure A and B back to back before claiming anything
(see `AGENTS.md`); these numbers only say where the work started.

## Machine

| | |
|---|---|
| CPU | AMD Ryzen 7 5800X (Zen 3), 8C/16T, AVX2, no AVX-512 (`Vector512.IsHardwareAccelerated` = false) |
| OS | Windows 11 25H2 (10.0.26200) |
| .NET | runtime 10.0.11, SDK 11.0.100-rc.2, X64 RyuJIT x86-64-v3 |
| BenchmarkDotNet | 0.15.8, DefaultJob, `[MemoryDiagnoser]`, single thread |
| Commit | `52051b9` (Sdcb.SimdCodecs.Webp as ported from SharpAstro.Codecs `7ad1e24`) |

## Reference: libwebp 1.6.0, SIMD enabled

Built by [`build-libwebp.sh`](build-libwebp.sh): official 1.6.0 tarball (SHA-256 pinned), `zig cc -O2`,
generic x86-64 (`-mcpu=x86_64`) plus `-msse4.1` on the `*_sse41.c` units, i.e. the SSE2/SSE4.1
run-time dispatch a distribution build has. The official Windows release `dwebp` agrees with it
(table below), so it is a fair stand-in for "libwebp as shipped".

## Still-image decode, 1920×1080 → RGBA (`WebpDecodeBenchmarks`)

Output is byte-identical to `dwebp -pam` on all three inputs. (BenchmarkDotNet truncates long parameter
values: `[22]` is `lossy-alpha-1080p.webp`, `[23]` is `lossy-opaque-1080p.webp`.)

| Input | libwebp (SIMD) | Sdcb.SimdCodecs.Webp | Ratio (Sdcb / libwebp) | Allocated (Sdcb) |
|---|---:|---:|---:|---:|
| lossy opaque | 5.477 ms | 11.146 ms | 2.04 | 376 B |
| lossy + alpha | 9.908 ms | 14.877 ms | 1.50 | 592 B |
| lossless | 7.697 ms | 8.366 ms | 1.09 | 536 B |

Animation, `WebpAnimationDecodeBenchmarks.DecodeAnimation` (10 × 640×360, 17.5 MB allocated, no libwebp
counterpart): 23.6 ms.

## Cross-checks

Official libwebp 1.6.0 Windows release, `dwebp in.webp -pam -v` ("Time to decode picture", 1 ms
resolution, median of 15 runs):

| Input | default | `-noasm` |
|---|---:|---:|
| lossy opaque | 6 ms | 16 ms |
| lossy + alpha | 11 ms | 18 ms |
| lossless | 8 ms | 15 ms |

Our lossy-opaque decode with the instruction-set ladder (`--profile-webp-lossy` 12 s loop):

| Configuration | ms / decode |
|---|---:|
| default (AVX2) | 11.0 |
| `DOTNET_EnableAVX2=0` (Vector128) | 13.4 |
| `DOTNET_EnableHWIntrinsic=0` (scalar) | 22.2 |

## Pitfalls seen while measuring

- **The libwebp build decides the answer.** The `libwebp.dll` in the `Imazen.WebP.NativeRuntime.win-x64`
  1.6.1 NuGet package runs scalar code (14.5 / 16.5 / 14.3 ms for lossless / alpha / opaque, the speed of
  `dwebp -noasm`) and made this decoder look 15–40% *faster* than libwebp. A `zig cc` build that targets the
  host CPU (the default when no target is given) auto-vectorises and decoded lossy opaque in 4.9 ms. Use
  `build-libwebp.sh` and the `dwebp` / `-noasm` check in the README.
- libwebp's x86 decoder dispatches to SSE2/SSE4.1 only; there is no AVX2 path to match, which is where a
  managed decoder can pull ahead — but only the wide per-row work (YUV→RGBA, upsampling, VP8L transforms)
  benefits. Its 4×4 blocks and 16-pixel loop-filter edges fit a 128-bit register.