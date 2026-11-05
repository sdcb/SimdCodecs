# JPEG 2000 oracle — OpenJPEG's `opj_compress` / `opj_decompress`

The reference implementation `Sdcb.SimdCodecs.Jpeg2000` is checked against. Two scripts here:

| Script | What it does | When you run it |
|---|---|---|
| `fetch.sh` | Downloads the pinned OpenJPEG release build into `dist/` (git-ignored) and proves it runs. | Once per clone; CI runs it every build. |
| `make-fixtures.sh` | Regenerates `Fixtures/jpeg2000/*.{pgm,j2k}` and verifies each pair is lossless. | Only when adding or changing fixtures. |

## Licence: binary yes, source no

OpenJPEG is **BSD-2**, which is permissive but *notice-retaining*, and this repo (MIT) keeps every file first-party under its clean-room rules, so `openjpeg`'s C is **not** a port source — the decoder is clean-room from ITU-T T.800. Running `opj_decompress` is not linking to it and its output is just pixels, so as an oracle binary it is fine. Exactly the line already drawn around jbig2dec (AGPL), jbig2enc (Apache-2.0) and libjpeg / libjpeg-turbo (see `AGENTS.md` at the repo root).

## Why this one downloads instead of building

This oracle is not compiled from source, and the reason is not laziness: OpenJPEG wants CMake, and upstream publishes official per-platform builds covering both platforms that matter here (Windows x64 dev box, `ubuntu-latest` CI). A verified download is less machinery *and* a stronger guarantee — dev box and CI run the same **bytes**, where two local CMake builds would only share the same source.

The version and both archive SHA-256s are pinned in `fetch.sh`, for the reason `STB_SHA256` is pinned: an oracle upstream can move is an oracle that can redefine "correct" underneath a green build.

`apt-get install libopenjp2-tools` was considered and rejected on this evidence: it is OpenJPEG **2.4.0** on jammy and 2.5.x on noble, so the dev box and CI would silently disagree about what the reference is.

## Probe results — what the tools actually do

Check the oracle's output conventions rather than assume them — the JBIG2 work lost time to ImageMagick's photometric tag. Checked, on `v2.5.4`:

- **`opj_decompress` picks its writer from the output file extension.** `.pgm` for one component, `.ppm` for three; `PBM|PGM|PPM|PNM|PAM|PGX|PNG|BMP|TIF|RAW|YUV|RAWL|TGA` are all accepted.
- **It stamps a comment line into the PNM header** — `P5\n#OpenJPEG-2.5.4\n…`. So a decoded `.pgm` is *never* byte-identical to the source `.pgm` even when every pixel matches. Compare parsed payloads, not files. Both the fixture script and `OpenJpegOracle` skip `#` comments when tokenising, and the version in that comment is why a committed *expected* PNM would have been a bad idea.
- **Above 8 bits it writes 16-bit big-endian samples** with `maxval 65535`, which is the PNM spec's own byte order. Round-trips exactly.
- **Reversible (5/3) really is exact**, verified per fixture rather than assumed: source payload and decoded payload are equal byte-for-byte at 8 and 16 bits, greyscale and RGB.
- **`opj_compress` turns RCT on by itself** for three components (`COD` SGcod MCT byte = `01`) when the transform is reversible.
- **`-h` exits non-zero.** It is the usage path. Any availability probe that pipes it under `set -o pipefail`, or that gates on exit code alone, gets the wrong answer.

## The fixtures need no oracle at test time

This is the payoff, and it is a better position than JBIG2 got. A reversible codestream decodes to its source raster **exactly**, so the committed `.pgm` *is* the expected output: the test asserts byte equality, with no tolerance and no subprocess. `make-fixtures.sh` verifies that claim with `opj_decompress` before it will commit a pair, so a fixture that is somehow not lossless is refused rather than baked in as a wrong answer.

The live oracle stays for what committed bytes cannot cover — chiefly the lossy 9/7 path, whose expected output is not the input and is only ever "whatever OpenJPEG computes", and which therefore needs a tolerance sourced from T.803 rather than invented. Keep those two assertions apart: **an exact-match test on the reversible path is the sharpest tool this format offers, and a global tolerance throws it away.**

## The fixture matrix

Everything in `Fixtures/jpeg2000/` is lossless — 8-bit unsigned components, one tile, maximal precincts, LRCP, reversible 5/3 — and varies one thing the decoder must get right. The first block is rung 1's: one component, one layer, raw J2K.

| Fixture | Varies | Why it earns its place |
|---|---|---|
| `nodwt-struct32`, `nodwt-noise32` | `-n 1` (zero decomposition levels) | No DWT at all, so a failure is tier-1 or tier-2 and *cannot* be the wavelet. Debug against these first. |
| `dwt1-struct32`, `dwt5-struct64` | `-n 2`, default `-n 6` | Bisects DWT depth once the no-DWT case passes. |
| `flat64` | constant image | No code-block is ever included in a packet. A decoder written only against busy images gets this wrong. |
| `ramp64` | smooth gradient | Where a dropped sign-XOR bit (hazard 4) is visible; on detail it is not. |
| `noise64` | dense high frequency | Every coding pass on every bit-plane. |
| `cblk16-noise64`, `cblk4-struct64` | `-b 16,16`, `-b 4,4` | A *grid* of code-blocks per subband, which is what makes tag trees and inclusion signalling do real work. At the default 64x64 there is one per subband and the tag tree is trivial. |
| `odd37x23`, `odd5x64`, `odd64x5` | non-aligned dimensions | Partial code-blocks, odd-length lifting, and subband sizes that are not a plain halving. |
| `odd1x1` | 1x1 | The degenerate limit. |
| `rgb-struct64`, `rgb-noise64`, `rgb-odd37x23` | three components (`.ppm`) | Rung 2's exact half: opj_compress applies RCT to three reversibly coded components by itself, so these pin RCT, and its integer floor, with no tolerance. |
| `rgb-nomct-struct64` | `-mct 0` | Three components and no transform: each must decode on its own, and a QCC patched onto one of them must change it alone. |
| `layers3-noise64`, `layers3-struct64` | `-r 40,10,1 -b 16,16`, `-r 20,5,1 -b 8,8` | Several quality layers over a grid of code-blocks, ending lossless. Hazard 5: rebuilding the tag trees per packet fails these, where rung 1's corpus could not tell. |
| `rgb-layers2-struct64` | RGB, `-r 10,1 -b 16,16` | The shape of the four lossless images measured in real PDFs: RCT and two layers. |
| `jp2-rgb-struct64.jp2`, `jp2-gray-struct64.jp2` | `.jp2` output | The JP2 file format around a lossless codestream, with an enumerated `colr` box: sRGB, greyscale. |
| `deep12-ramp64` | a PGM with maxval 4095 | Twelve bits, which the facade has to scale into its 16-bit format. |

`Fixtures/jpeg2000-raw/cmyk16.j2k` has four components, which no PNM carries, so its source is the planar `cmyk16.raw` opj_compress read (`-F 16,16,4,8,u -mct 0`), verified lossless the same way. It is what the facade refuses (four components with no channel definition: the extra channel could be alpha or black) and the decoder reads exactly.

`Fixtures/jpeg2000-alpha/` holds the two files `opj_compress` writes a channel definition box (`cdef`) for: `rgba-struct37x29.jp2` (RGBA: colours 1 to 3, then an opacity for the whole image) and `graya-struct37x29.jp2` (grey + alpha). The input is a synthetic PNG built by `gen-sources.py` (closed-form pixels, a straight alpha ramp with a fully transparent corner; `opj_compress` reads PNG, not PAM), the lossless codestream decodes to it exactly, and the expected output is committed beside each as a PAM (`P7`, `TUPLTYPE RGB_ALPHA` / `GRAYSCALE_ALPHA`), which is also what `opj_decompress` writes for an image with alpha (a `.pnm` output name), so `Pnm` reads both with one parser. `make-fixtures.sh` checks each pair before it is committed, as for the PNM fixtures. Everything `opj_compress` does not write (a reordered box, premultiplied opacity, a box that contradicts itself) is built in `Jpeg2000ChannelDefinitionTests` around a committed codestream.

`LocalJpeg2000ChannelDefinitionCorpusTests` is the real-file check for the box and is skipped unless `SIMDCODECS_TESTIMAGES_DIR` names a directory of JP2 / JPF files (their licences are unknown, so none is in the repository): every file with a `cdef` that the facade presents and that is 8-bit is compared with `opj_decompress` (within 1, exact on 5/3), and the per-file outcome goes to the test output.

`Fixtures/jpeg2000-lossy/` holds codestreams with no committed expected output, because their expected output is not their source. Their tests decode them with `opj_decompress` at run time.

| Fixture | Varies | Asserted |
|---|---|---|
| `lossy53-struct64`, `lossy53-rgb-layers2` | 5/3 with a rate (`-r 6`; `-r 20,6`) | **Exactly**, since nothing irrational happens on the 5/3 path. Pins T.800 E.1.1.2's midpoint reconstruction of coefficients whose low bits were cut. |
| `lossy97-struct64`, `lossy97-noise64`, `lossy97-odd37x23` | `-I` (9/7), one component | Within the measured tolerance: every sample within 1, at most 1% differing. |
| `lossy97-rgb-struct64`, `lossy97-rgb-nomct-struct64` | `-I`, three components, with and without ICT | The same. |
| `lossy97-rgb-layers3` | `-I -r 40,20,8 -b 16,16` | The shape of the two 9/7 images measured in real PDFs: ICT and several layers. |

Sources are regenerated by `gen-sources.py` from closed-form functions of `(x, y)` and one fixed LCG — no library PRNG, no floating point — so a regeneration on another machine or Python version reproduces the same bytes.

## Made at test time: tiles, precincts, progressions, markers, palettes

`Jpeg2000TilingAndProgressionTests` commits no codestream at all. It writes a picture, has `opj_compress` code it with the option under test (`-t`, `-d`/`-T` for an odd origin, `-TP`, `-c`, `-p RLCP|RPCL|PCRL|CPRL`, `-SOP -EPH`, `-M 2|16|32`, `-I`), and compares with `opj_decompress` on the same bytes. Two `opj_compress` 2.5.4 shapes are kept out because OpenJPEG cannot round-trip them itself: a tile one sample wide (its own decode is not lossless there) and `-TP L` on 48x48 tiles of the 157x123 picture (it cannot decode what it wrote).

## Issue #1's corpus

The 16 JPEG 2000 files attached to issue #1 have no known licence, so they are not committed or fetched by a test. `Issue1Jpeg2000CorpusTests` runs them when `SIMDCODECS_ISSUE1_DIR` names the unzipped attachments (the URLs are in that class's remarks); run it after changing the decoder.
