# JPEG benchmark (`Sdcb.SimdCodecs.Jpeg.Benchmarks`)

Single-threaded, whole-image JPEG decode to RGBA (and, with `encode`, encode — see [Encoder](#encoder-encode)), measured against black-box competitors.
Baselines live in [`results/`](results/) — one file per recording, named `<date>-<cpu>.md`.

| Contestant | How it is called |
|---|---|
| Sdcb.SimdCodecs | `JpegDecoder.DecodeTo(data, rgba)` into a reused buffer |
| libjpeg-turbo 3.2.0 | official release binary, TurboJPEG 3 API via P/Invoke: `tj3DecompressHeader` + `tj3Decompress8(…, TJPF_RGBA)` into a reused buffer, default parameters (islow IDCT, fancy upsampling) |
| libjpeg-turbo (fast) | same, with `TJPARAM_FASTDCT` + `TJPARAM_FASTUPSAMPLE` — reference only |
| ImageSharp 3.1.12 | `Image.Load<Rgba32>` with `MaxDegreeOfParallelism = 1`; no decode-into-buffer API exists, so it allocates per call |

ImageSharp is pinned to 3.1.12 because 3.2.0 and 4.x refuse to build without a Six Labors
license key. Only the benchmark projects reference it; no shipped package does.

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

### Display layouts

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks -- formats [--groups RE] [--images RE] [--rounds N]
```

times `JpegImageDecoder.TryDecodeInto` in BGRA, RGB, BGR and grey against decode-to-RGBA + `PixelConverter`
(identical bytes are required before timing, except grey), with RGBA alone and libjpeg-turbo's `tj3Decompress8` in
the same layout, on the quick subset by default. Grey from a YCbCr JPEG is the Y plane for both (checked
against the plane the full decode reconstructs, with the largest difference from turbo's grey reported).
Report: `.cache/work/formats.md`.

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

## Encoder (`encode`)

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks -- encode --mode all --gate
```

Encodes the ten masters (box-downscaled to 2048 px on the long side, 25.8 MP in all) with
`JpegEncoder.Encode`, libjpeg-turbo (`tj3Compress8`, default islow DCT) and, in baseline mode,
ImageSharp; `--baseline` adds the `baseline` build's encoder. Every output is decoded by
libjpeg-turbo and scored against the source (size, PSNR on luma and RGB, SSIM on luma); the
timing is the median of `--reps` single-threaded encodes after a 2 s warm-up, contestants
interleaved per image. Modes: `baseline` (Annex K tables), `optimize` (optimised Huffman tables on
both sides) and `progressive`. `--gate` exits 1 unless, per configuration, our mean PSNR-Y is at
least libjpeg-turbo's and our total size at most 1% larger — the encoder's quality contract.

Recorded on a Ryzen 7 5800X, Windows 11, .NET 10.0.11, AVX2 (no AVX-512), MP/s:

| config | ours | libjpeg-turbo | ImageSharp | optimize ours / turbo | progressive ours / turbo |
|---|---:|---:|---:|---:|---:|
| q50 4:2:0 | 642 | 630 | 340 | 413 / 265 | 169 / 121 |
| q75 4:2:0 | 585 | 561 | 289 | 340 / 230 | 131 / 99 |
| q90 4:2:0 | 507 | 479 | 224 | 268 / 182 | 90 / 75 |
| q95 4:2:0 | 438 | 412 | 181 | 221 / 148 | 69 / 60 |
| q90 4:4:4 | 300 | 296 | 177 | 174 / 116 | 65 / 52 |
| q95 4:4:4 | 266 | 262 | 139 | 144 / 96 | 51 / 42 |

At the same quality our files are 0.2–2.3% smaller than libjpeg-turbo's in every mode and
PSNR-Y is 0.004–0.09 dB higher (float AAN DCT, unrounded colour conversion).
