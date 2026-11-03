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

echo "[heif] all fixtures ready in $DEST"
