# JPEG decode on browser-wasm (`Sdcb.SimdCodecs.Jpeg.WasmBench`)

Whole-image JPEG decode time on browser-wasm (Mono AOT, `WasmEnableSIMD`, one thread, run under Node.js) next to the same code on the desktop JIT (`DOTNET_TieredCompilation=0`, `DOTNET_PROCESSOR_COUNT=1`), with the per-stage split by ablation (`JpegAblation`, internal).

```bash
bash benchmarks/Sdcb.SimdCodecs.Jpeg.WasmBench/run.sh                 # both sides (~2 min, most of it the wasm publish)
bash benchmarks/Sdcb.SimdCodecs.Jpeg.WasmBench/run.sh --wasm-only
bash benchmarks/Sdcb.SimdCodecs.Jpeg.WasmBench/run.sh --desktop-only
```

Needs the `wasm-tools` workload and Node.js. Not in `SimdCodecs.slnx` (the wasm project needs the workload); `desktop/` is the desktop-JIT twin: the same `Stages.cs` / `Program.cs` and the same embedded images, under the same assembly name (which is what `InternalsVisibleTo` names).

**Images** (git-ignored `.cache/images`, embedded at build time because browser-wasm has no file system): `photo.jpg` (1600×1067, q90, 4:2:0, baseline Huffman; fetched by URL + SHA-256), and `photo-444.jpg` / `photo-progressive.jpg`, re-encoded once from its pixels on the desktop by `JpegEncoder` (q90 4:4:4 baseline, q90 4:2:0 progressive).

**Equivalence first.** Before timing, `Verify.cs` runs the kernels this runtime dispatches to (IDCT sparse and full, both `YccRow` overloads, every upsampler) against their scalar twins on random and extreme inputs, 62801 checks; one mismatch fails the run. On browser-wasm that is the only test of the `PackedSimd` branches, which the desktop test suite cannot reach.

**Columns** (median of 10 rounds after 3 warm-ups, min in parentheses, ms):

| column | what runs |
|---|---|
| facade | `SimdCodecs.TryOpenFrames` + `TryDecodeFrame(0)` (the public path) |
| rgba | `JpegDiagnostics.DecodeTo(…, None)`: full-scale decode to RGBA |
| entropy | `SkipBlockSink`: headers and entropy decoding only |
| idct | rgba − `SkipIdct` |
| up+color | rgba − `SkipUpsample \| SkipColor` |

The checksum column (FNV-1a of the RGBA output) must be the same on both sides. Then come the row and block kernels alone (`kernel …` lines), sized like `photo.jpg`, to split a stage further.

**Reading the AOT output.** The per-assembly LLVM objects are in `obj/Release/net10.0/wasm/for-publish/*.dll.o`; the emscripten pack's `llvm-objdump -dr` disassembles them. `-p:WasmProbe=true` compiles `Probe.cs`: single `Vector128` / `PackedSimd` calls, one per method, to see which ones Mono AOT lowers to one wasm SIMD instruction and which become calls.

## Recorded (2026-11-05)

AMD Ryzen 7 5800X, Windows 11 Pro for Workstations 26200, .NET 10.0.11, wasm-tools 10.0.114, Node 24.18.1; facade median ms, wasm ÷ desktop in parentheses. "Before" is c0b38cc, "after" 33d52ea.

| image | wasm before | wasm after | desktop |
|---|---:|---:|---:|
| photo.jpg (4:2:0) | 22.04 (5.2×) | 8.15 (1.8×) | 4.23 / 4.42 |
| photo-444.jpg | 31.17 (5.5×) | 10.34 (1.8×) | 5.70 / 5.86 |
| photo-progressive.jpg | 29.52 (3.2×) | 15.56 (1.6×) | 9.18 / 9.48 |

photo.jpg by stage (ms, wasm before → after, desktop): entropy 2.91 → 2.84 (1.96–2.01), IDCT 6.68 → 2.16 (0.78–0.89), upsampling + colour 11.59 → 2.25 (0.73). The fixes, in order: PackedSimd shifts and conversions (Mono AOT made `Vector128.ShiftLeft` an out-of-line call and split conversions into 64-bit halves), an in-register Vector128 IDCT with the sparse half-passes, and saturating narrows instead of wasm's NaN-aware `f32x4.min` / `max` for the YCbCr clamp.
