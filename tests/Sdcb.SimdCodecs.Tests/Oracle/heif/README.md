# HEIF / HEIC Oracle

## Oracle binary: `heif-convert` / `heif-info`

Used for byte-exact YUV comparison against our HEVC decoder in `HeifOracleTests`
(M1 and later). The oracle binary is **libheif** (`heif-convert`), black-box only —
never read its source (see AGENTS.md).

**Version contract:** pin the version in `fetch-oracle.sh`. Bumping requires
re-recording every TSV golden in `Fixtures/heif/`.

### Getting the oracle

**Windows (Git Bash):**
```bash
# Install via vcpkg (library + examples; heif-enc and heif-convert come from
# the examples sub-build — build from source):
C:\_\3rd\vcpkg\vcpkg.exe install libheif[hevc] --triplet x64-windows
# Or install the pre-built Windows binary from the libheif GitHub releases.
bash tests/Sdcb.SimdCodecs.Tests/Oracle/heif/fetch-oracle.sh
```

**Linux:**
```bash
sudo apt-get install libheif-examples   # provides heif-convert and heif-info
# OR:
bash tests/Sdcb.SimdCodecs.Tests/Oracle/heif/fetch-oracle.sh
```

Set `HEIF_CONVERT` and `HEIF_INFO` environment variables to point at the
binaries if they are not on `PATH`.

## Test images (fixtures)

Download with:
```bash
bash tests/Sdcb.SimdCodecs.Tests/Oracle/heif/fetch.sh
```

### Real-world phone photos (CC0 — shot on Realme GT5 Pro, Exif stripped)

| File | Resolution | Profile | Tiles | Notes |
|---|---|---|---|---|
| `hevc-main-3072x4096-grid.heic` | 3072×4096 | Main 8-bit | 50 (6×8) | |
| `hevc-main10-3072x4096-grid.heic` | 3072×4096 | Main 10 10-bit | 48 (6×8) | |
| `hevc-main-8192x6144-grid.heic` | 8192×6144 | Main 8-bit | 192 (16×12) | 50 MP mode |

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
| `synth-photo-mangrove-1599x1199-main8.heic` | 1599×1199 | Main Still Picture | mangrove, q50, 4:2:0; odd size (coded 1600×1200 + conformance window) | ✅ |
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
