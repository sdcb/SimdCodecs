# Palette quantizer benchmark

Measures lossy reduction to a palette (what pngquant does) on quality, size and speed: the repository's quantizer against pngquant and ImageSharp.

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Quantize.Benchmarks -- rd [--max-dim 1024] [--images TEXT] [--colors 256,128,64,32,16] [--dither off|on|both] [--reps 3] [--pngquant PATH] [--speeds 4] [--contestants LIST] [--ablate] [--ablate-dither]
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Quantize.Benchmarks -- stages [--images TEXT]   # ours per stage: histogram, palette, mapping
```

## Contestants

| Name | What | Notes |
|---|---|---|
| `pngquant-s<N>` | pngquant CLI at `--speed N`, `--nofs` when not dithering | GPL: a black-box binary only, never read. The reference for the summary. Its time is the whole process (PNG decode, quantize, PNG encode, start-up). |
| `ours` | `src/Shared/PaletteQuantizer.cs` (as GIF compiles it) in RGBA mode, as the PNG encoder uses it | |
| `baseline-wu` | the quantizer at an earlier commit, compiled from `.cache/baseline/` | only when that file exists (see below) |
| `imagesharp-wu`, `imagesharp-octree` | ImageSharp's `WuQuantizer` / `OctreeQuantizer` through the `Quantize` processor | black box |

`--ablate` adds `ours` with a stage switched off or resized (`ours-no-kmeans`, `ours-rgb-space`, `ours-kmeans4` / `20`, `ours-hist64k` / `16k`); `--ablate-dither` adds dithering variants (`ours-raster`, `ours-full-fs` for plain Floyd–Steinberg, `ours-no-edge`, `ours-diff16`, `ours-edge16` / `64`).

pngquant is a competitor, not an oracle: quantization has no single right answer, so nothing here compares bytes or pixels with it.

### Baseline

To compare against the quantizer of an earlier commit in the same run, put that commit's file under its own namespace:

```bash
git show <ref>:src/Shared/PaletteQuantizer.cs | python -c "import sys,re; sys.stdout.write(re.sub(r'#if SIMDCODECS_GIF.*?#endif\n', 'namespace Sdcb.SimdCodecs.Quantize.Benchmarks.Baseline;\n', sys.stdin.read(), flags=re.S))" > benchmarks/Sdcb.SimdCodecs.Quantize.Benchmarks/.cache/baseline/PaletteQuantizer.cs
```

`baseline-wu` drives it through the old API (RGB only, 1-bit alpha); after an API change, adjust `BaselineWu` in `Contestants.cs` or delete the file.

## Inputs

The photos are the JPEG benchmark's masters (CC0 / Public Domain, `Sdcb.SimdCodecs.Jpeg.Benchmarks/corpus/manifest.tsv`; run its `scripts/fetch-corpus.sh` and `prepare` first), box-downscaled to `--max-dim`. The rest is synthetic and deterministic (`Corpus.cs`, shared with the WebP encode benchmark): `photo-alpha` (soft vignette into transparency plus a hard cut-out), `screen` / `screen-alpha` (UI panels, glyphs, translucent shadows), `palette` (12 flat colours) and `sticker` (anti-aliased disc and drop shadow on transparency, the typical pngquant asset).

## Metrics

- **colors / used**: palette size asked for, and distinct RGBA values in the result (all fully transparent pixels count as one). A result over the limit fails the run.
- **common-B**: the quantized image written by one PNG writer (ours, `LosslessReduction`, default level), so sizes compare palettes and index maps, not deflate implementations. **native-B** is pngquant's own file.
- **SSIM-dB**: luma SSIM (Wang et al. 2004, 8×8 windows, stride 4) as −10·log10(1 − SSIM); for images with alpha, the mean of compositing over black and over white.
- **OKLab-RMS**: RMS distance in OKLab of premultiplied colour plus alpha, ×100. A transparent pixel's colour does not count. This is close to what `ours` minimises, so read it next to SSIM, never alone.
- **blurred**: OKLab-RMS after blurring both images (3×3 box twice, about σ 1.15 px): what is seen from a normal distance, where dither noise averages out and banding does not. SSIM and plain OKLab error penalise dither noise; this is the measure of what dithering is for.
- **Summary**: per image, the Bjøntegaard delta rate against `pngquant-s<first speed>` over the `--colors` sweep (bytes at equal SSIM, equal OKLab error and equal blurred error; negative = smaller), averaged over images, and the geometric-mean time ratio.

## Results

Recorded runs are in [`results/`](results/), each with its machine.
