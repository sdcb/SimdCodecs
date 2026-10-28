# PNG encode / decode benchmark (`Sdcb.SimdCodecs.Png.Benchmarks`)

Every PNG decode and encode combination, timed against ImageSharp and (optionally) libpng as black
boxes, with encoded sizes. Run it by path from the repository root. Recorded results go in
[`results/`](results/).

## Cases

All cases are `1920×1080` by default (`--size WxH`) and come from two source images:

- **`photo`** — natural content (texture, noise, gradients): a centre crop of `landscape-almsee.png`, a
  master of the JPEG benchmark's CC0 / Public Domain corpus, when that cache exists
  (`bash benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/scripts/fetch-corpus.sh` and its `corpus` command);
  otherwise a synthetic stand-in. `PNG_BENCH_PHOTO` points at any other image. 16-bit cuts carry the
  8-bit pixel in the high byte and deterministic noise in the low byte.
- **`screen`** — synthetic UI content: flat windows, text-like glyphs, a gradient thumbnail, drop shadows.

The alpha variants (for the formats that have alpha) add a vignette and a hard cut-out to the photo and
make the screen's desktop transparent. Every report states which sources were used.

| Matrix | Axes | Count |
|---|---|---|
| decode | content × {grey 1/2/4/8/16, RGB 8/16, palette 1/2/4/8, palette 8 + tRNS, grey+alpha 8/16, RGBA 8/16} × {non-interlaced, Adam7} | 64 |
| encode | content × `PngWriter` layouts {Gray8, Gray16, Rgb8, Rgb16, Rgba8, Rgba16} × `CompressionLevel` {NoCompression, Fastest, Optimal, SmallestSize} | 48 |

Case names are `<content>-<format>[-adam7]` (decode, e.g. `screen-p4-adam7`) and
`<content>-<layout>-<store|fastest|optimal|smallest>` (encode, e.g. `photo-rgba8-optimal`); `--cases RE`
selects by name.

Decode inputs are encoded by ImageSharp (adaptive filters, zlib level 6) — a foreign encoder, so the
decoder is not reading its own writer's habits — and cached in `.cache/inputs/<size>/`. The generator
checks IHDR, so a case is exactly the colour type, depth and interlace method it is named for.

## Contestants

| Name | Decode | Encode |
|---|---|---|
| `current` | `PngImageDecoder.TryDecodeIntoRgba8` into a reused buffer | `PngWriter.Encode*` with `PngWriteOptions.CompressionLevel` |
| `native` (decode) | `PngReader.Decode`: samples as stored, no RGBA expansion | — |
| `parallel` (encode) | — | the same with `ParallelFragments = Environment.ProcessorCount`; reported in its own table, never in the single-thread ratios |
| `reduced` (encode) | — | the same with `LosslessReduction = true` (the smallest exact layout: indexed, sub-byte grey, tRNS, ...); its own table, with the layout chosen |
| `baseline` | the previous commit's decoder, same process (see below) | the previous commit's encoder |
| ImageSharp 3.1.12 | `Image.Load<Rgba32>` + `CopyPixelDataTo`, `MaxDegreeOfParallelism = 1` | `PngEncoder` with the same colour type, depth and zlib level (0 / 1 / 6 / 9), adaptive filters |
| libpng | simplified API: `png_image_begin_read_from_memory` + `png_image_finish_read` to RGBA | `png_image_write_to_memory`; the simplified API has no level setting, so libpng appears on the `optimal` rows only (its default level) |

**Correctness before timing.** Decode: every contestant's RGBA must equal ImageSharp's — exactly, or
within 1 for 16-bit sources (narrowing 16 → 8 bits by truncation or by rounding are both legitimate). If
`current` disagrees the run stops; a disagreeing competitor is dropped from that case with a note. Encode:
every output is decoded and must reproduce the source samples exactly, in the requested colour type and
depth (`reduced`: in whatever layout it chose, compared as 16-bit RGBA widened the way PNG widens samples). Known notes: libpng's simplified API converts 16-bit sources differently when reading to 8-bit
RGBA (it disagrees by far more than 1, so it is not timed there), and its RGBA16 write does not
reproduce the samples.

`verify` runs exactly these checks over every selected case without timing anything.

**Clean-room:** ImageSharp and libpng are black boxes. Only their binaries and documented APIs are
used; their sources are never read (see [`AGENTS.md`](../../AGENTS.md)). The libpng interop in
`LibPng.cs` follows the simplified-API section of `libpng-manual.txt` and is self-checked against the
binary at start-up (`LibPng.SelfCheck`).

## Running

```bash
# Optional: a libpng shared library (Windows: needs its zlib1.dll next to it, e.g. vcpkg's x64-windows/bin)
export LIBPNG_PATH=/path/to/libpng16.dll

dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Png.Benchmarks -- verify
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Png.Benchmarks -- matrix [decode|encode|all] [--cases RE] [--rounds N]
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Png.Benchmarks -- bdn decode|encode --cases RE
```

- `matrix` times every selected case in-process: each contestant is warmed for at least 300 ms, then
  timed in interleaved rounds (default 7, each at least 30 ms of work) and the median is reported. The
  whole matrix takes a few minutes; the report goes to `.cache/work/matrix.md`, with the machine
  facts, per-case times, MP/s, sizes, ratios and geometric means.
- `bdn` runs the selected cases under BenchmarkDotNet (with `MemoryDiagnoser`), interleaved per case,
  and renders the same tables from its medians plus managed bytes allocated per operation
  (`.cache/work/bdn-decode.md` / `bdn-encode.md`). Use it for the numbers that go into a commit, on
  the cases the change is about — the whole matrix under BenchmarkDotNet takes hours.

### Stage costs

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Png.Benchmarks -- matrix decode --ablate [--cases RE]
```

adds runs of `current` with one stage switched off (internal `PngAblation` flags through
`PngDiagnostics`): chunk CRC, unfilter, RGBA expansion, and all three ("inflate only"). A stage's cost
is the time that disappears without it — attribution by ablation, not sampling (see `AGENTS.md`). The
report gains a stage-cost table next to libpng's total. `bdn decode --ablate` times the same variants.

### Memory

Two measures, because they see different things:

- **Managed bytes per operation** — every `matrix` (and `bdn`) report has a table of the managed bytes
  one warm operation allocates (`GC.GetAllocatedBytesForCurrentThread`; BenchmarkDotNet's
  `MemoryDiagnoser` under `bdn`). ImageSharp keeps pixel buffers in its own unmanaged pool and libpng
  allocates natively, so their columns here are near zero by construction.
- **Peak working set** — everything, native included:

  ```bash
  dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Png.Benchmarks -- peakmem decode|encode [--cases RE]
  ```

  runs each case × contestant in a fresh process (median of three): a warm-up on a 16×16 image of the
  same case (JIT and library loading excluded), the full-size inputs and outputs allocated and touched,
  then the working-set rise during the first full-size operation ("first") and during a second one with
  pooled buffers resident ("steady"). One process per measurement makes it slow: select cases.
  Report: `.cache/work/peakmem-decode.md` / `peakmem-encode.md`.

### Same-process A/B

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Png.Benchmarks -- baseline [git-ref]   # default HEAD
```

builds that commit's `Sdcb.SimdCodecs.Png` into `.cache/baseline/` (through a temporary git worktree)
and loads it into its own `AssemblyLoadContext`; from then on `matrix`, `bdn` and `verify` include it
as `baseline`, and the reports gain `÷ baseline` columns. A baseline decode that differs from the
current one is flagged in the notes. Never compare against a number from an earlier session.
