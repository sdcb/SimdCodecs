# HEIF / HEIC Oracle

## Oracle: ffmpeg (HEVC sample decoder)

M1 compares **decoded samples**, not pixels: the Y / Cb / Cr planes of every coded image (every grid
tile) and of the composed primary image must equal a reference decoder's, bit for bit (AGENTS.md,
"HEVC decode is byte-exact"). The reference is **ffmpeg 8.1.2**, run as a black box — its HEVC decoder
`HeifOracle.DecodeToYuv` drives through the documented CLI only. ffmpeg's and libde265's sources are on
the clean-room list; they are never read (AGENTS.md).

**Version contract:** `HeifOracle.PinnedVersion` is `8.1.2`, and `HeifOracle` checks `ffmpeg -version`
before comparing anything: a different build is treated as unavailable (skipped, or failed under
`REQUIRE_ORACLES=1`). Bumping the pin means re-recording every digest in `Fixtures/heif/heif-golden.tsv`
and re-reviewing the change.

### Getting the oracle

This machine has ffmpeg 8.1.2 at `C:/_/3rd/ffmpeg-bin/ffmpeg.exe`; `HeifOracle` finds it through the
`FFMPEG` variable, `PATH`, or that directory. Elsewhere:

```bash
# Linux: distro packages are usually fine, but only the pinned version runs the tests
sudo apt-get install ffmpeg && ffmpeg -version | head -1
# Windows: the gyan.dev "full" build (ffmpeg -version reports 8.1.2-full_build-...)
```

Set `FFMPEG` to the executable when it is not on `PATH`.

### Why not `heif-convert`

`heif-convert` (libheif's example program) was the original plan, but vcpkg's `libheif` port installs
the library only, and building the examples against it did not produce a working binary here. ffmpeg
decodes the HEVC payload directly, needs no extra install, and — importantly — can be pinned and
version-checked. The committed digests run without any oracle; the live comparison is the stricter
check and runs whenever the pinned tool is present.

### Goldens

`Fixtures/heif/heif-golden.tsv` holds, per fixture, the size, bit depth and SHA-256 of the three planes
(length-prefixed, little-endian samples; see `HeifTests.Digest`). Record or refresh with
`REGEN_HEIF_ORACLE=1`, which only rewrites the manifest — review the diff. The RGBA cross-check against
Magick.NET (libheif) samples every ~200 000th pixel and allows ±2 per channel, because the colour
conversion is defined in-repo (`HeifColor`) while libheif uses its own; they must agree that closely,
and that comparison is what catches a systematically wrong conversion in either direction.

## Test images (fixtures)

Download with:
```bash
bash tests/Sdcb.SimdCodecs.Tests/Oracle/heif/fetch.sh
```

### Real-world phone photos (CC0 — shot on Realme GT5 Pro, Exif stripped)

| File | Resolution | Profile | Tiles | Notes |
|---|---|---|---|---|
| `hevc-main-3072x4096-grid.heic` | 3072×4096 | Main 8-bit | 48 (6×8) | 50 items: 48 tiles, the grid, Exif |
| `hevc-main10-3072x4096-grid.heic` | 3072×4096 | Main 10 10-bit | 48 (6×8) | |
| `hevc-main-8192x6144-grid.heic` | 8192×6144 | Main 8-bit | 192 (16×12) | 50 MP mode; 194 items |

### Synthetic fixtures

Single-image (no grid) files written entirely by **libheif 1.23.1** (container) with **x265 4.2**
(HEVC payload), both black boxes, at libheif's quality scale. They are encoded at ordinary settings,
not at a phone's bitrate, so they exercise a different rate/QP range from the phone photos.

Sources: `sunrise-fuji` and `foliage-mangrove` are the CC0 Wikimedia Commons photos in
`benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/corpus/manifest.tsv`; `testsrc2` is ffmpeg's built-in
test pattern (screen-like content). ffmpeg only downscaled the sources (lanczos) to raw RGB; libheif
did the RGB → YCbCr conversion and wrote its own `colr` signalling.

| File | Size | HEVC profile (ffprobe) | Recipe | M1? |
|---|---|---|---|---|
| `synth-photo-fuji-1600x900-main8.heic` | 1600×900 | Main Still Picture | fuji, q50, 4:2:0 | ✅ |
| `synth-photo-mangrove-1599x1199-main8.heic` | 1599×1199 | Main Still Picture | mangrove, q50, 4:2:0; odd size: coded 1600×1200, a `clap` clean aperture of 1599×1199 at a half-sample horizontal offset (no SPS conformance window) | ✅ |
| `synth-screen-testsrc2-1280x720-main8.heic` | 1280×720 | Main Still Picture | testsrc2, q75, 4:2:0 | ✅ |
| `synth-photo-fuji-1600x900-main10intra.heic` | 1600×900 | RExt (Main 10 Intra) | fuji, q50, 4:2:0 10-bit | ❌ |
| `synth-photo-fuji-1600x900-main12.heic` | 1600×900 | RExt (Main 12 Intra) | fuji, q50, 4:2:0 12-bit | ❌ |
| `synth-screen-testsrc2-1280x720-444.heic` | 1280×720 | RExt (4:4:4) | testsrc2, q75, `chroma=444` | ❌ |
| `synth-photo-fuji-1600x900-alpha.heic` | 1600×900 | Main Still Picture + RExt monochrome alpha | fuji, q50, radial alpha → `auxl` alpha item | ❌ (colour item ✅, alpha ❌) |
| `synth-anim-testsrc2-640x360-8frames.heics` | 640×360 | Main | testsrc2, 8 frames × 100 ms, q50, HEIF image sequence (`pict` track) | ❌ |

❌ files must fail cleanly in M1 (`InvalidDataException` / `false` from `Try*`), never hang or
mis-decode silently. With x265's all-intra settings a 10-bit still comes out as the RExt
*Main 10 Intra* profile, not Main 10; the 10-bit **Main 10** case is covered by the phone photo
`hevc-main10-3072x4096-grid.heic`.

vcpkg's x265 is 8-bit only; the 10- and 12-bit files used the libraries that
[`tools/heif-fixtures/build-x265.sh`](../../../../tools/heif-fixtures/build-x265.sh) builds from the
pinned x265 4.2 archive (`HIGH_BIT_DEPTH=ON`, plus `MAIN12=ON` for 12-bit; assembly off), loaded by
libheif in place of vcpkg's `libx265.dll`. Regenerating means re-running the recipes above and
re-pinning the SHA-256s in `fetch.sh`.
