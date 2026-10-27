# JPEG decode benchmark (`Sdcb.SimdCodecs.Jpeg.Benchmarks`)

Single-threaded, whole-image JPEG decode to RGBA, measured against black-box competitors.
Baselines live in [`results/`](results/) — one file per recording, named `<date>-<cpu>.md`.

| Contestant | How it is called |
|---|---|
| Sdcb.SimdCodecs | `JpegDecoder.DecodeTo(data, rgba)` into a reused buffer |
| libjpeg-turbo 3.2.0 | official release binary, TurboJPEG 3 API via P/Invoke: `tj3DecompressHeader` + `tj3Decompress8(…, TJPF_RGBA)` into a reused buffer, default parameters (islow IDCT, fancy upsampling) |
| libjpeg-turbo (fast) | same, with `TJPARAM_FASTDCT` + `TJPARAM_FASTUPSAMPLE` — reference only |
| ImageSharp 3.1.12 | `Image.Load<Rgba32>` with `MaxDegreeOfParallelism = 1`; no decode-into-buffer API exists, so it allocates per call |

ImageSharp is pinned to 3.1.12 because 3.2.0 and 4.x refuse to build without a Six Labors
license key. It is referenced **only** by this project, which is not in `SimdCodecs.slnx`.

**Clean-room:** libjpeg-turbo and ImageSharp are black boxes. Only their binaries and documented
APIs are used; their sources are never read (see [`AGENTS.md`](../../AGENTS.md)). The TurboJPEG P/Invoke surface in
`TurboJpeg.cs` comes from the published API documentation and is self-checked at start-up.

## Getting the inputs

```bash
bash benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/scripts/fetch-libjpeg-turbo.sh   # Windows (Git Bash, needs 7-Zip) or Linux
bash benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/scripts/fetch-corpus.sh          # ~440 MB of originals
```

Both verify SHA-256 and write only to the git-ignored `.cache/`.

**Corpus** (`corpus/manifest.tsv`): 16 Wikimedia Commons photos, all
CC0 or Public Domain (no Unsplash/Pexels-sourced files). Ten high-resolution photos (landscape,
sunrise gradient, calm sea, two night scenes, portrait, ground texture, foliage, architecture,
panorama) become the encoded matrix; six unedited in-camera JPEGs (Sony, Fujifilm, Olympus,
Canon, Pixel 7, Galaxy S21 Ultra — EXIF software is camera firmware) are benchmarked as shot.

## Running

All commands run from the repository root; `.cache/`, `corpus/` and `results/` below are relative to this directory.

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks -- prepare    # Lanczos-3 downscale (≤ ½) to PNG masters, crops, libjpeg-turbo matrix
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks -- accuracy   # coefficient / component-plane / RGB accuracy tables (~1 min)
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks -- bench      # correctness gate, then BenchmarkDotNet (~95 min for the full matrix)
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks -- peakmem    # peak working set, one fresh process per decode
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks -- report     # writes results/<date>-<cpu>.md
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks -- help       # every command and option
```

`bench` and `validate` take `--groups <regex>` / `--images <regex>` to run a subset, e.g.
`dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks -- bench --groups "^420-q90-12mp$"`.

### The optimisation loop: same-process A/B

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks -- baseline          # build HEAD's decoder into .cache/baseline (or: baseline <git-ref>)
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks -- ab                # quick subset (13 files): current vs baseline vs libjpeg-turbo (~3 min)
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks -- ab --ablate       # + per-stage cost by ablation (~7 min)
```

The baseline commit's `Sdcb.SimdCodecs.Jpeg` is loaded in its own `AssemblyLoadContext`, so both
decoders run in the same BenchmarkDotNet session, interleaved per file — never compared against a
number from an earlier run. Before timing, `ab` prints each file's PSNR against libjpeg-turbo and
against the baseline (`identical` when the output did not change — which is what a pure
performance change must show). `--ablate` adds the current decoder with the IDCT, upsampling or
colour conversion switched off (`JpegAblation`, internal); a stage's cost is the time that
disappears. Results go to `.cache/work/ab.md`. `--groups` / `--images` replace the quick subset.

### Matrix

- Main: {4:4:4, 4:2:0} × quality {50, 75, 90, 95, 100} × size {12 MP (4000×3000), 3 MP
  (2000×1500), 0.5 MP (816×612)}, baseline, standard Huffman tables, no restarts.
- At 3 MP, q75/q95: restart interval every MCU row, optimized Huffman tables, progressive (4:2:0),
  and 4:2:2 / grayscale.
- Camera originals.

### Method

- **Correctness gate first.** Every file is decoded by every contestant; ours, libjpeg-turbo and
  ImageSharp must agree pairwise at RGB PSNR > 40 dB or the run aborts — a fast number from a
  broken path is worthless.
- BenchmarkDotNet, Release, x64, out-of-process, 1 launch × 3 warm-up × 9 iterations (≥ 200 ms).
  A custom orderer runs all contestants on one file before moving on, so drift hits everyone.
- Reported: per-file median, MP/s, ratio to libjpeg-turbo, per-group geometric means, managed
  allocations, and peak working set from separate processes (covers native/unmanaged memory).

### Accuracy

- **Coefficients:** libjpeg-turbo's quantized coefficients via the documented `tj3Transform`
  custom filter, compared one by one with ours (`JpegDiagnostics`, internal).
- **Component planes** (after IDCT, before upsampling): `tj3DecompressToYUVPlanes8` and ours vs a
  double-precision IDCT evaluated directly from T.81 A.3.3 (`ReferenceIdct.cs`), with the
  IEEE 1180-1990 limits (peak ≤ 1, per-position MSE ≤ 0.06 and |mean| ≤ 0.015, overall MSE ≤ 0.02
  and |mean| ≤ 0.0015) applied both to real images and to a 1180-style random-block test whose
  coefficients are written exactly into real JPEG streams.
- **RGB output:** every contestant vs reference IDCT + bilinear chroma interpolation at T.871's
  centred sample positions + double-precision JFIF (T.871) conversion, for 4:4:4, 4:2:2, 4:2:0
  and the camera originals; the A/B baseline build is included as an extra row when present.
