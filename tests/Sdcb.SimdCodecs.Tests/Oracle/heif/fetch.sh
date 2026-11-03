#!/usr/bin/env bash
# Fetch HEIF / HEIC test images for the oracle and benchmark tests.
# Images are CC0 (copyright owned by the repository author; Exif stripped).
# Run from anywhere; files land in tests/Sdcb.SimdCodecs.Tests/Fixtures/heif/.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="$HERE/../../Fixtures/heif"
mkdir -p "$DEST"

fetch() {   # url sha256 dest
    local url="$1" sha256="$2" dest="$3"
    if [ ! -f "$dest" ]; then
        echo "[heif] downloading $(basename "$dest") …"
        curl --fail --location --retry 3 --silent --show-error "$url" --output "$dest"
    fi
    echo "$sha256  $dest" | sha256sum -c -
    echo "[heif] ok: $(basename "$dest")"
}

BASE="https://cv-public.sdcb.ai/simdcodecs/test-images/heif"

# Realme GT5 Pro, HEVC Main 10 (10-bit 4:2:0), 3072×4096, 6×8 grid (48 tiles), Display P3 ICC.
fetch "$BASE/hevc-main10-3072x4096-grid.heic" \
    "a6dcdf0d612f0711d35782a1aa65b14cd3c6aa8fbdefb2166e6d914e5b76d584" \
    "$DEST/hevc-main10-3072x4096-grid.heic"

# Realme GT5 Pro, HEVC Main (8-bit 4:2:0), 3072×4096, 6×8 grid (50 items), ICC.
fetch "$BASE/hevc-main-3072x4096-grid.heic" \
    "8f38f08beb0867518264ccaa691993f612542253d2a411acd00afd11978c5cee" \
    "$DEST/hevc-main-3072x4096-grid.heic"

# Realme GT5 Pro, 50 MP mode, HEVC Main (8-bit 4:2:0), 8192×6144, 16×12 grid (192 tiles), ICC.
fetch "$BASE/hevc-main-8192x6144-grid.heic" \
    "ab1a3135b09206a1935d778577ad288b9f901dca9c071e260f390092e6c2cc7c" \
    "$DEST/hevc-main-8192x6144-grid.heic"

# ── Synthetic fixtures: libheif 1.23.1 + x265 4.2 (black boxes), recipes in README.md ──
# Within M1 scope (HEVC Main / Main Still Picture, 4:2:0 8-bit).
fetch "$BASE/synth-photo-fuji-1600x900-main8.heic" \
    "fbfaae1cf1f92fdafc6b12126ab1207f6f7679dee9fa1ccf06eda8ff2452d529" \
    "$DEST/synth-photo-fuji-1600x900-main8.heic"
fetch "$BASE/synth-photo-mangrove-1599x1199-main8.heic" \
    "a13514e1c1f52e839ec66a51118cbd119bb0bc1851ebebd225f51bad0ea94ab8" \
    "$DEST/synth-photo-mangrove-1599x1199-main8.heic"
fetch "$BASE/synth-screen-testsrc2-1280x720-main8.heic" \
    "d40c9a68e0d899c375d8334a6cc932f72aa91eb0ddbfc7284173733929705455" \
    "$DEST/synth-screen-testsrc2-1280x720-main8.heic"

# HDR transfer functions (8-bit Main Still Picture, BT.2020): TryDecodeInto must refuse them, TryDecode report them.
# PQ is signalled in the HEVC VUI only (no 'colr' box); HLG in an nclx 'colr' box as well.
fetch "$BASE/synth-screen-testsrc2-640x360-pq-vui.heic"     "190dd4830fe9d21c0bf0e51f82f15df977c4647e66ba89a5552c0e89b488ee5a"     "$DEST/synth-screen-testsrc2-640x360-pq-vui.heic"
fetch "$BASE/synth-screen-testsrc2-640x360-hlg-nclx.heic"     "ee7088a10607bf4d63d0e769400cf79fddd7df0630f563440c9a00b01cc973db"     "$DEST/synth-screen-testsrc2-640x360-hlg-nclx.heic"

# Beyond M1: the decoder must reject these with InvalidDataException (or decode them in a later milestone).
fetch "$BASE/synth-photo-fuji-1600x900-main10intra.heic" \
    "f9aef637bce363f9704d640c95c00297cf4f789c892700050043222af039a855" \
    "$DEST/synth-photo-fuji-1600x900-main10intra.heic"
fetch "$BASE/synth-photo-fuji-1600x900-main12.heic" \
    "92772e02547ebba2624efffa1e0f98386ecd9a7a7b86f56cc8e695b4430bd676" \
    "$DEST/synth-photo-fuji-1600x900-main12.heic"
fetch "$BASE/synth-screen-testsrc2-1280x720-444.heic" \
    "593518622cd0042c88a4821a5bcd096c2fcc00e49500baa513ddc96b652e56b7" \
    "$DEST/synth-screen-testsrc2-1280x720-444.heic"
fetch "$BASE/synth-photo-fuji-1600x900-alpha.heic" \
    "66e6cede940964f37b1b6915a641bf3a9189c9b3ce228872236a374a42ba3b1f" \
    "$DEST/synth-photo-fuji-1600x900-alpha.heic"
fetch "$BASE/synth-anim-testsrc2-640x360-8frames.heics" \
    "821fa68ee3786cbef1276689e179a405244934cf3bb1e2324e0ca87578acc4c0" \
    "$DEST/synth-anim-testsrc2-640x360-8frames.heics"

echo "[heif] all fixtures ready in $DEST"
