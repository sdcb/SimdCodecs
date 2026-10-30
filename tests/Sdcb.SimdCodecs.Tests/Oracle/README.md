# Oracles, goldens and quality gates

The rules are in [`AGENTS.md`](../../../AGENTS.md) ("Output contracts", "Validation"); this is how they
are implemented and how to get the tools.

## Oracles

Every oracle test **skips** when its tool is missing, so a clean clone stays green. A skipped oracle is
not a passing one: read the skip messages, and run with `REQUIRE_ORACLES=1` (CI does), which turns a
missing oracle into a failure. All of them are black boxes — released binaries and documented
interfaces only.

| Oracle | Used for | Get it (Linux / Windows Git Bash) |
|---|---|---|
| `jpegenc` (stb_image_write, PD) | JPEG encoder byte-exactness | `bash tests/Sdcb.SimdCodecs.Tests/Oracle/jpegenc/build.sh` — pinned header (SHA-256), built with clang or `zig cc` into `Oracle/bin/` |
| OpenJPEG 2.5.4 (BSD-2, binary only) | JPEG 2000 lossy path | `bash tests/Sdcb.SimdCodecs.Tests/Oracle/jpeg2000/fetch.sh` — official release, SHA-256 pinned |
| jbig2dec (AGPL, binary only) | JBIG2 conformance | Linux: `apt-get install jbig2dec`; Windows: `bash tests/Sdcb.SimdCodecs.Tests/Oracle/jbig2/fetch-windows.sh` (MSYS2 build, SHA-256 pinned) |
| libwebp `dwebp` / `anim_dump` (BSD-3, binary only) | WebP live comparisons (`WebpOracleTests`, `WebpAnimationOracleTests`); the digests in `Fixtures/webp/*-golden.tsv` run without them | Linux: `apt-get install webp`; or libwebp release binaries on `PATH` / `DWEBP` + `ANIM_DUMP`; `bash tests/Sdcb.SimdCodecs.Tests/Oracle/webp/build.sh` builds `anim_dump` 1.6.0 (the version the digests were made with) |
| Magick.NET (NuGet) | JPEG inputs, libjpeg tolerance check, PNG (libpng) decode of every colour type and depth, Group 4 MMR, tiled TIFF, GIF (stills and animations it writes, its coalesced frames, and reading back our encoder's output), BMP and ICO (the bitmaps and icons it writes, and reading back every encoder layout) | nothing to install |
| WIC (Windows, binary only) | BMP and ICO decode: a corpus of every header, depth, mask and compression, and every icon image, pixel for pixel (`BmpWicTests`, `IcoWicTests`) | part of Windows, reached through WPF from Windows PowerShell (`WicOracle`); skipped as not applicable elsewhere, so Linux CI does not run it |
| libjpeg-turbo 3.2.0 (binary only) | JPEG decode benchmark + accuracy tables | `bash benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/scripts/fetch-libjpeg-turbo.sh` — official release, SHA-256 pinned, only `bin/` extracted (Windows needs 7-Zip) |
| ImageSharp 3.1.12 (NuGet, binary only) | JPEG, PNG and BMP benchmarks | benchmark projects only; 3.2.0 / 4.x need a Six Labors license key to build |
| libpng (binary only, optional) | PNG benchmark | any libpng16 shared library via `LIBPNG_PATH` (see the PNG benchmark README) |

Per-codec details: [`jbig2/`](jbig2/README.md), [`jpeg2000/`](jpeg2000/README.md), [`webp/`](webp/README.md).

## Goldens

- **`Fixtures/jpeg-oracle-golden.tsv`** pins the JPEG decoder's RGBA output: the arithmetic defined in
  `JpegIdct` (single-precision T.81 IDCT), `JpegResample` (16×-scale linear chroma interpolation) and
  `JpegColor` (T.871), frozen once the accuracy tables showed it at least as close to a
  double-precision reference as libjpeg-turbo. Every ISA tier and fast path reproduces that operation
  sequence bit for bit; `JpegIdctEquivalenceTests` compares the pre-rounding floats, because a
  reordered sum rarely moves a rounded byte.
- **`Fixtures/webp/webp-oracle-golden.tsv`** and **`webp-animation-oracle-golden.tsv`** pin the WebP
  decoder's RGBA output against libwebp 1.6.0; the vector paths (VP8L predictors, fancy upsampling)
  are asserted equal to their scalar twins by the `*EquivalenceTests`.
- **PNG** has no golden: decoding is lossless, so the tests compare against libpng (Magick.NET) live,
  exactly, for every colour type, depth and a spread of widths (`PngRgba8ExpansionTests`), and the
  unfilter against the specification's formulas (`PngPredictorTests`).
- **BMP and ICO** have no golden either: lossless, compared live and exactly against ImageMagick and WIC, and
  against the test's own model of every layout in streams `TestBmpWriter` assembles. Where WIC, ImageMagick
  and ImageSharp disagree with each other (alpha in a 32-bit `BI_RGB` pixel, channels narrower than 4 bits,
  ImageMagick's own RLE8), the tests say which one the decoder follows and compare the rest; the decisions are
  in the BMP package README.
- A golden moves only for an intentional, reviewed change (`REGEN_JPEG_ORACLE=1` and friends), with the
  diff in the review.

## Quality gates (JPEG, in `benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks`)

- **Decoder arithmetic change:** `dotnet run -c Release -- accuracy --gate` against a `baseline` of the
  parent commit, on the matrix: per group RGB PSNR within 0.2 dB of the baseline, ≥ 0.5 dB above
  libjpeg-turbo where chroma is interpolated (4:4:4: not below it), peak error ≤ 2, IEEE 1180 limits
  met. The change lands as its own commit with the gate table in the message.
- **Encoder:** `encode --gate`, per configuration on the corpus: PSNR-Y ≥ libjpeg-turbo and size ≤ +1%,
  in baseline, optimised-Huffman and progressive mode. Optimised / progressive streams decode to
  exactly the baseline's pixels, and the kernels stay identical across ISA tiers
  (`JpegEncoderEquivalenceTests`), so output does not depend on the machine.

## Independent test writers

`TestJpegWriter` (tests) is a baseline JPEG writer for streams stock encoders never produce — any
sampling, non-interleaved scans, colour restarts, YCCK/RGB, cut streams. It shares no code with the
codec, and its output is checked against libjpeg before anything is pinned with it.

`TestBmpWriter` (tests) does the same for bitmaps: every header size, any masks, short palettes, RLE4 /
RLE8 / RLE24 streams built op by op (each op paints the test's expected image as it is written), OS/2
Huffman 1D from the T.4 tables. Its streams are also decoded by WIC wherever WIC reads the variant.
