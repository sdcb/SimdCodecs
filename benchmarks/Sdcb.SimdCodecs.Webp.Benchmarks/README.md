# WebP benchmark (`Sdcb.SimdCodecs.Webp.Benchmarks`)

Whole-image WebP decode to RGBA with BenchmarkDotNet (`[MemoryDiagnoser]`), against libwebp as a
black box, and encode speed / size / accuracy / memory (see [Encoder](#encoder-encode)). Run it by
path from the repository root. Recorded results are in [`optimization/`](optimization/).

| Benchmark | Inputs | How it is called |
|---|---|---|
| `WebpDecodeBenchmarks` — libwebp (**baseline**) | lossy opaque, lossy + alpha, lossless (all 1920×1080) | libwebp's `WebPDecodeRGBAInto` into a reused buffer, via P/Invoke into the released shared library |
| `WebpDecodeBenchmarks` — Sdcb.SimdCodecs.Webp | same | `WebpDecoder.DecodeTo(data, rgba)` into a reused buffer |
| `WebpAnimationDecodeBenchmarks` | 10-frame 640×360 animation | `WebpDecoder.DecodeAnimation(data)`; no libwebp counterpart |

The still-image ratio column is relative to libwebp, because it carries `Baseline = true`. Before timing,
the setup checks that libwebp and this decoder agree on the image dimensions; pixel-exactness against
libwebp is covered by the test suite's golden digests and live `dwebp` / `anim_dump` oracles, not here.

**Clean-room:** libwebp is a black box. Only its released binary and documented C API are used; its
sources are never read (see [`AGENTS.md`](../../AGENTS.md)).

## Inputs

The four files in [`webp-inputs/`](webp-inputs/) are committed (about 400 KB). They are synthetic —
ffmpeg `testsrc2` frames encoded with `cwebp` / `img2webp` — so there is no third-party image
involved. To regenerate them (needs `ffmpeg`, `cwebp` and `img2webp` on `PATH`):

```bash
bash benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks/generate-webp-inputs.sh
```

`WEBP_BENCHMARK_INPUT_DIR` points the benchmarks and the script at a different directory.

## Running

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks -- --filter '*'
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks -- --profile-webp-lossy
```

The second command is a 12-second single-threaded loop over the lossy 1080p input, meant for attaching
a profiler; it does not use BenchmarkDotNet. See the benchmarking lessons in
[`AGENTS.md`](../../AGENTS.md) before drawing conclusions from a profile.

### Same-process A/B and stage costs

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks -- baseline [git-ref]   # default HEAD
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks -- ab [--ablate] [--inputs RE]
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks -- quick [--ablate] [--inputs RE|--files PATTERN] [--rounds N] [--mt]
```

`baseline` builds a commit's decoder into `.cache/baseline/` (through a temporary git worktree) and loads
it into its own `AssemblyLoadContext`, so `ab` times baseline, current and libwebp in one BenchmarkDotNet
run, interleaved per input — never against a number from an earlier session. Both `ab` and `quick` first
check that the current decoder's output is byte-identical to libwebp and refuse to time it otherwise.
`--ablate` adds runs with one stage switched off (internal `WebpAblation` flags via `WebpDiagnostics`):
a stage's cost is the time that disappears (the set of ablations is chosen from the file's content:
VP8 stages, ALPH, or VP8L transforms and the predictor transform alone). `quick` is an in-process
interleaved loop that answers in seconds; use it while iterating and `ab` for the numbers that go into a
commit. It warms every contestant for at least 300 ms first, so small files are not timed as tier-0 code.
`--files PATTERN` times files of the verification corpus (below) instead of the three inputs — the inputs
are flat `testsrc2` frames, and the corpus' noisy content is where entropy decoding dominates. `--mt` adds
the opt-in multithreaded decode (`WebpDecodeOptions.Multithreaded`) as a separate table; it is never
part of the single-thread comparison.

### Memory

`ab` runs with BenchmarkDotNet's `MemoryDiagnoser` and reports managed bytes allocated per decode.
libwebp allocates natively, which that cannot see, so

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks -- peakmem [--inputs RE]
```

measures the peak working set of each decode in a fresh process (current, baseline, libwebp): after a
warm-up decode of a tiny file of the same kind (JIT excluded) and with the RGBA destination already
touched, the rise during the first full-size decode is the decoder's working memory; a second decode
shows the steady state with pooled buffers resident.

### Verification corpus

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks -- corpus <path-to-cwebp>
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks -- verify
```

`corpus` encodes deterministic synthetic images (1920×1080 down to 33×17, opaque and with alpha) with
the official `cwebp` across 20 option sets into `.cache/corpus/`; `verify` decodes every file there plus
the benchmark inputs with this decoder and with libwebp and compares the bytes.

## Encoder (`encode`)

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks -- encode [--mode lossless|lossy|all] [--reps 5] [--max-dim 1024] [--images TEXT] [--cwebp PATH] [--gate]
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks -- encode-peakmem [--mode lossless|lossy] [--max-dim 1024] [--images TEXT]
```

**Inputs.** The ten JPEG benchmark corpus masters (CC0 / Public Domain photographs; fetch them with
that project's `scripts/fetch-corpus.sh` and `prepare`), box-downscaled to `--max-dim` on the long
side, plus synthetic, deterministic content: `photo-alpha` (the first master with a soft vignette into
full transparency and a hard cut-out), `screen` / `screen-alpha` (flat UI panels, glyph-like text, one
gradient) and `palette` (a 12-colour chart). Without the masters only the synthetic three run.
`--images` keeps the inputs whose name contains the text.

**Contestants**, single-threaded, interleaved image by image:

| name | what | timed as |
|---|---|---|
| `libwebp` / `libwebp qQ` | the SIMD libwebp shared library (below), simple API: `WebPEncodeLosslessRGBA` / `WebPEncodeRGBA` with the library defaults; **every ratio is against it** | in process, `Stopwatch` around the call |
| `cwebp -z N` (0, 3, 6, 9) / `cwebp -q Q -m M` (0, 4, 6) | the official 1.6.0 release binary, for the effort levels the simple API cannot select | cwebp's own `-v` "Time to encode picture" (millisecond resolution; excludes process start and reading the input) |
| `ImageSharp` | the managed competitor (`WebpEncoder`, defaults) | in process |

The simple API's documented counterpart for choosing effort is the advanced API (`WebPConfig`,
`WebPPicture`), whose full layout the published documentation does not give, so it is not used:
interop comes from the documentation only (see [`AGENTS.md`](../../AGENTS.md)).

**Correctness before timing.** Every output is decoded by libwebp (not by our decoder) first.
Lossless output must reproduce every alpha value and the RGB of every visible pixel; the `lossless`
column says `exact` when the RGB of fully transparent pixels is kept too and `visible` when it was
rewritten (libwebp's default, "exact" off). A contestant that gets a pixel wrong is reported as
`WRONG` and not timed, and `--gate` then exits 1. Lossy output is scored on the opaque inputs against
the source: PSNR on luma and RGB, and SSIM on luma (8×8 windows, stride 4).

Timing is the median of `--reps` encodes per image, after the in-process contestants have warmed up
for 1.5 s. Each mode prints a summary (MB, bpp, size and time relative to `libwebp`, MP/s, accuracy)
and the bytes of every input. Before the modes, a **SIMD check** encodes the first input with the
in-process library, `cwebp` and `cwebp -noasm` at the same settings: the library must land near
`cwebp`, not near `-noasm`; for lossy q75 the two also produce the same bytes. The report is written
to `.cache/work/encode-<mode>.md`.

`encode-peakmem` measures, in one fresh process per encode, the peak working-set rise of the first
and of a second (steady) full encode after a warm-up on a 64×64 crop, plus the managed bytes allocated
by the first one, for the in-process contestants; the input is resident before the baseline.

`cwebp` is found through `--cwebp`, `CWEBP`, the official release archive unpacked under
`.cache/release/` (`libwebp-1.6.0-windows-x64.zip` from the WebP downloads page), or `PATH`. The
Windows release reads everything but WebP through WIC, so its input is written as an RGBA PNG.

## libwebp

The comparison needs a libwebp shared library. It is looked up as `libwebp.dll` / `webp.dll` on
Windows, `libwebp.dylib` (including the Homebrew locations) on macOS and `libwebp.so.7` / `libwebp.so`
on Linux. The official Windows release archive ships only a static library, so on Windows set
`LIBWEBP_PATH` to the full path of a webp DLL (for example vcpkg's `libwebp.dll`). `LIBWEBP_PATH`
wins over the default names on every platform.

**Use a SIMD-enabled libwebp, and check it.** libwebp picks SSE2 / SSE4.1 (or NEON) at run time, and a
build without them is about 2× slower, which makes any comparison meaningless. The `libwebp.dll` in the
`Imazen.WebP.NativeRuntime.win-x64` 1.6.1 NuGet package is such a build: it decodes at the speed of the
official `dwebp -noasm`. To check a candidate, decode one of the inputs with the official `dwebp` with
and without `-noasm` (`dwebp in.webp -pam -v -o out.pam` prints the decode time) and make sure the
library you benchmark lands near the first number, not the second. A build that targets the host CPU
(`zig cc` does by default) can also come out faster than the generic x86-64 library a distribution ships,
so build the reference the way a distribution would.

[`optimization/build-libwebp.sh`](optimization/build-libwebp.sh) does that (the library includes the
encoder): it downloads the official 1.6.0
tarball (SHA-256 pinned), compiles it with `zig cc` (or `CC=clang` / `gcc`) as generic x86-64 with
SSE2/SSE4.1 dispatch, and writes `.cache/libwebp.{dll,so,dylib}`; set `LIBWEBP_PATH` to the path it prints.

## Optimization

[`optimization/`](optimization/) holds the recorded baseline
([`baseline-2026-10-25-amd-ryzen-7-5800x.md`](optimization/baseline-2026-10-25-amd-ryzen-7-5800x.md)),
the libwebp build script, and one file per optimisation milestone: tooling (m0), lossy (m1), ALPH and
more lossy (m2), lossless (m3) and the final state with the opt-in multithreaded decode
([`m4-final-2026-10-25-amd-ryzen-7-5800x.md`](optimization/m4-final-2026-10-25-amd-ryzen-7-5800x.md)).
Encoding starts from [`encode-baseline-2026-11-01-amd-ryzen-7-5800x.md`](optimization/encode-baseline-2026-11-01-amd-ryzen-7-5800x.md):
libwebp, cwebp's effort levels and ImageSharp, before this repository had an encoder.
Put each new recording there too.
