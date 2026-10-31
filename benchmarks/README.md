# Benchmarks

Performance and accuracy benchmarks for the codecs. All of them are in `SimdCodecs.slnx`, so the
solution build (and CI) compiles them; nothing runs them automatically. Run each by path from the
repository root. Corpora, inputs and competitor binaries live in each project's git-ignored `.cache/`.

| Project | Measures | Compared against | Docs |
|---|---|---|---|
| [`Sdcb.SimdCodecs.QoiPnm.Benchmarks`](Sdcb.SimdCodecs.QoiPnm.Benchmarks/) | QOI and Netpbm decode and encode speed (QOI RGB / RGBA; P1–P7, 16-bit, plain, PFM), sizes, managed allocations, a same-process A/B against the previous commit | ImageSharp | [recorded results](Sdcb.SimdCodecs.QoiPnm.Benchmarks/results/) |
| [`Sdcb.SimdCodecs.Bmp.Benchmarks`](Sdcb.SimdCodecs.Bmp.Benchmarks/) | BMP and ICO decode and encode speed (every depth, RLE, masks, icons), sizes, managed allocations, peak working set, a same-process A/B against the previous commit | ImageSharp, WIC (Windows) | [README](Sdcb.SimdCodecs.Bmp.Benchmarks/README.md), [recorded results](Sdcb.SimdCodecs.Bmp.Benchmarks/results/) |
| [`Sdcb.SimdCodecs.Jpeg.Benchmarks`](Sdcb.SimdCodecs.Jpeg.Benchmarks/) | JPEG decode speed, memory and accuracy, a same-process A/B against the previous commit, and encode speed / size / quality | libjpeg-turbo, ImageSharp | [README](Sdcb.SimdCodecs.Jpeg.Benchmarks/README.md), [recorded results](Sdcb.SimdCodecs.Jpeg.Benchmarks/results/) |
| [`Sdcb.SimdCodecs.Jpeg2000.Benchmarks`](Sdcb.SimdCodecs.Jpeg2000.Benchmarks/) | JPEG 2000 decode speed, memory and PSNR accuracy vs libopenjp2 | libopenjp2 2.5.4 | [README](Sdcb.SimdCodecs.Jpeg2000.Benchmarks/README.md), [recorded results](Sdcb.SimdCodecs.Jpeg2000.Benchmarks/results/) |
| [`Sdcb.SimdCodecs.Png.Benchmarks`](Sdcb.SimdCodecs.Png.Benchmarks/) | PNG decode and encode speed (every colour type × bit depth × interlace; every layout × compression level), encoded size, a same-process A/B against the previous commit | ImageSharp, libpng | [README](Sdcb.SimdCodecs.Png.Benchmarks/README.md), [recorded results](Sdcb.SimdCodecs.Png.Benchmarks/results/) |
| [`Sdcb.SimdCodecs.Webp.Benchmarks`](Sdcb.SimdCodecs.Webp.Benchmarks/) | WebP decode speed, allocations and peak memory; encode speed, size, lossless exactness / lossy PSNR and SSIM, peak memory | libwebp (SIMD build), cwebp levels, ImageSharp (encode) | [README](Sdcb.SimdCodecs.Webp.Benchmarks/README.md) |

## Rules that apply to every benchmark

- **Clean-room.** Competitors are black boxes: released binaries and documented APIs only, never their
  source (see [`AGENTS.md`](../AGENTS.md)).
- **Correctness before timing.** A benchmark must not time a decoder whose output is wrong.
- **Compare in the same run.** A number from an earlier session is not a baseline; machine state moves
  results by more than 10%. Measure A and B back to back.
- **Record the machine.** Every recorded result states the CPU, OS, .NET version and
  `Vector256/512.IsHardwareAccelerated`.
- **End-to-end decides.** A kernel that is faster in isolation is not a faster decoder until the
  whole decode says so.

To add a benchmark project, put it in `benchmarks/Sdcb.SimdCodecs.<Codec>.Benchmarks/` with its own
README and add a row above.

## Lessons (read before optimising anything)

Each of these cost a wrong conclusion or a lost week once. [`AGENTS.md`](../AGENTS.md) keeps the rules
they produced; this is the why.

### Measuring

- **Attribute time by instrumentation + ablation, not by a sampling profiler.** A sampled-thread-time
  `dotnet-trace` once attributed JPEG decode time to `Monitor.Enter_Slowpath`; an A/B run disproved it
  immediately. Time stages with explicit timers, and measure what disappears when a stage is switched
  off (`JpegAblation` via `ab --ablate`, `PngAblation` via `matrix decode --ablate`, `WebpAblation`).
- **A kernel's isolated speed-up is not the decoder's speed-up.** A kernel timed alone in a hot loop has
  the cache and memory bandwidth to itself. One change measured 5× faster in isolation and moved the
  end-to-end decode by under 6%. A "faster" vectorised colour converter once measured 42% *slower* in
  the real pipeline (it was store-bound).
- **A number from a previous session is not a baseline.** Machine state moves results by more than 10%.
  The `baseline [git-ref]` command loads the previous commit's codec side by side with the current one,
  so A and B run back to back in one process. BenchmarkDotNet rebuilds from source when it starts: to
  keep editing while it runs, measure from a `git worktree` of the commit with `SIMDCODECS_BENCH_ROOT`
  pointing at the main checkout's benchmark directory (corpus, competitor binaries and baseline caches
  live there), and do not build while it is timing.
- **Compare like with like.** `ab --ablate` also times libjpeg-turbo decoding to component planes
  (entropy + IDCT, no upsampling or colour), giving its stage split as a black box; the PNG matrix
  puts libpng's total next to our stage costs. Decide which stage is behind only from such a split.
- **Dynamic PGO can make one file 2–3× slower than its neighbours.** Tiered PGO inlined the hot IDCT
  call chain into the scan loop and ran out of inlining budget for the `AggressiveInlining` Vector256
  helpers, which then passed vectors through memory — only for files whose warm-up profile tipped it
  (444-q95 sea-ibiza: 36 ms, 13.8 ms with `DOTNET_TieredPGO=0`). A single-file outlier that its `-opt`
  twin does not share is a JIT effect: re-run it with `DOTNET_TieredPGO=0`. This is why vector kernels
  are `NoInlining | AggressiveOptimization`.
- BenchmarkDotNet `--filter` matches `Namespace.Type.Method`, not `Description` text.

### Reading the machine

- **Read the JIT's code before guessing.** `DOTNET_JitDisasm=<method>` with `DOTNET_JitStdOutFile=<file>`
  (and `DOTNET_TieredCompilation=0` in a throwaway probe, or tier-0 code gets timed) found every one of
  these, each worth 5–18% end to end:
  - Since .NET 9, `Vector128/256.Min/Max` on floats follow IEEE 754-2019 (NaN, ±0) and `ConvertToInt32`
    saturates: each is a compare/blend sequence (~10 instructions per vector). Use `MinNative` /
    `MaxNative` / `ConvertToInt32Native` where inputs are never NaN.
  - An `out` argument pins the caller's local to the stack for the whole method: a slow-path helper
    with `out len` put a store + reload on the Huffman decoder's critical chain. Return packed values
    instead. The same goes for `ref` to non-inlined callees.
  - A branch on a data bit (a coefficient sign, a progressive correction bit, PNG's Paeth choice)
    mispredicts about half the time on real images; make it arithmetic.
- **A load that partly overlaps an earlier store waits for it.** The first per-pixel vector PNG
  unfilter stored 4 bytes per 3-byte RGB pixel (the fourth written back unchanged); the next pixel's
  4-byte load then started inside that store, store-to-load forwarding failed, and RGB unfiltering got
  *slower* than the scalar code (9.9 → 12.5 ms) while RGBA, whose loads and stores line up, got 4×
  faster. Storing exactly the pixel's bytes fixed it (2.5 ms).
- **Don't count multiplies; count what the core is short of.** Zen 3 runs float adds and
  multiplies/FMAs on separate pipe pairs, plus shuffles: a hand-derived 5-multiply AAN IDCT was no
  faster than the 22-multiply direct form (same adds), and the IDCT's real cost was codegen, register
  spills and the Windows ABI's xmm6–15 saves.
- **Interleave latency-bound and throughput-bound work.** Entropy decoding is one serial dependency chain
  (~8 cycles per symbol); the IDCT is wide float work. Decoding a block and transforming it straight
  away lets the out-of-order core overlap the two; decoding a whole MCU first and transforming
  afterwards measured 2–7% *slower*.
- **Know which costs are not ours.** PNG's inflate is the BCL's `ZLibStream` (zlib-ng): about
  370–440 MB/s of output on photographs, and reading it a row at a time costs only ~5% over one big
  read. Past that, the remaining photo decode time is the inflater's, not something to tune around.

### Black boxes

- Interop with a competitor comes from its published API documentation, then is verified against the
  binary's behaviour (`TurboJpeg.SelfCheck`, `LibPng.SelfCheck`) — never from its headers or sources.
- A competitor's library build matters: a libwebp without SSE2/SSE4.1 is about 2× slower, and one built
  for the host CPU can beat a distribution's generic build. Build or pick the reference the way a
  distribution ships it, and check it (see the WebP benchmark README).
