#!/usr/bin/env bash
# Generate synthetic HEIC test fixtures from CC0 / self-generated PNG sources.
#
# Requires:
#   heif-enc  — from libheif (build from source or install via your package manager)
#   magick    — ImageMagick 7 (for generating screen-type PNGs and 12-bit input)
#   curl      — for downloading CC0 source images
#
# Windows (Git Bash): build libheif from vcpkg source to get heif-enc.exe, or
# install it separately. The vcpkg port installs the library only.
#
# Run after fetch-oracle.sh confirms the oracle tools are available.
# Outputs land in tests/Sdcb.SimdCodecs.Tests/Fixtures/heif/; they are git-ignored.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="$HERE/../../Fixtures/heif"
TMP="$HERE/tmp-synth"
mkdir -p "$DEST" "$TMP"

require() {
    command -v "$1" &>/dev/null || {
        echo "[heif synth] '$1' not found. $2" >&2
        exit 1
    }
}
require heif-enc "Build libheif from source or install libheif-examples (Linux)."
require magick   "Install ImageMagick 7."
require curl     ""

upload_r2() {   # local-path key
    local src="$1" key="$2"
    local url="https://cv-public.sdcb.ai/simdcodecs/test-images/heif/$key"
    if curl -sI "$url" | grep -q "200"; then
        echo "[heif synth] $key already on R2, skipping upload."
        return
    fi
    echo "[heif synth] uploading $key to R2..."
    wrangler r2 object put "cv-public/simdcodecs/test-images/heif/$key" \
        --file "$src" --content-type "image/heic" --remote
}

sha256_of() { sha256sum "$1" | cut -d' ' -f1; }

echo "=== Synthetic HEIC fixture generator ==="

# ── 1. Synthetic PHOTO — 8-bit Main ──────────────────────────────────────────
# Source: NASA Apollo 11 surface photo AS11-40-5931 (US Government work, public domain)
# Cropped and resized to 720×480 to keep the file small.
NASA_URL="https://images-assets.nasa.gov/image/as11-40-5931/as11-40-5931~orig.jpg"
NASA_JPG="$TMP/nasa-as11-40-5931.jpg"
PHOTO_PNG="$TMP/synth-photo-720x480.png"
PHOTO_HEIC="$DEST/synth-photo-720x480-main8.heic"

if [ ! -f "$NASA_JPG" ]; then
    echo "[heif synth] downloading NASA source image..."
    curl --fail --location --retry 3 --silent --show-error "$NASA_URL" -o "$NASA_JPG"
fi
# Resize to 720×480, strip all metadata (clean-room: we own the fixture)
magick "$NASA_JPG" -resize 720x480! -strip "$PHOTO_PNG"
heif-enc --quality 80 --encoder x265 "$PHOTO_PNG" -o "$PHOTO_HEIC"
echo "[heif synth] synth-photo-720x480-main8.heic: $(sha256_of "$PHOTO_HEIC")"

# ── 2. Synthetic SCREEN — 8-bit Main ─────────────────────────────────────────
# Self-generated: flat gradient + geometric shapes, no third-party content.
SCREEN_PNG="$TMP/synth-screen-1280x720.png"
SCREEN_HEIC="$DEST/synth-screen-1280x720-main8.heic"

magick -size 1280x720 \
    gradient:"#1e3a5f-#c9d8ec" \
    \( -size 400x300 xc:"#f5a623" -fill "#1e3a5f" \
       -draw "rectangle 40,40 360,260" \
       -draw "circle 200,150 200,50" \) \
    -gravity Center -composite \
    -strip "$SCREEN_PNG"
heif-enc --quality 90 --encoder x265 "$SCREEN_PNG" -o "$SCREEN_HEIC"
echo "[heif synth] synth-screen-1280x720-main8.heic: $(sha256_of "$SCREEN_HEIC")"

# ── 3. Synthetic PHOTO — 12-bit Main 12 (BEYOND M1) ──────────────────────────
# Same NASA source, upsampled to 12-bit. M1 decoder is expected to throw
# InvalidDataException (or return a clear "unsupported profile" error).
PHOTO12_PNG="$TMP/synth-photo-720x480-16bit.png"   # 16-bit PNG as 12-bit carrier
PHOTO12_HEIC="$DEST/synth-photo-720x480-main12.heic"

magick "$NASA_JPG" -resize 720x480! -strip -depth 16 "$PHOTO12_PNG"
# x265 --profile main12 encodes 12-bit YUV 4:2:0
heif-enc --quality 80 --encoder x265 --bit-depth 12 "$PHOTO12_PNG" -o "$PHOTO12_HEIC" 2>/dev/null \
    || { echo "[heif synth] WARNING: heif-enc --bit-depth 12 not supported; skipping main12 fixture."; PHOTO12_HEIC=""; }
[ -n "$PHOTO12_HEIC" ] && echo "[heif synth] synth-photo-720x480-main12.heic: $(sha256_of "$PHOTO12_HEIC")"

# ── 4. Synthetic ANIMATION — HEVC image sequence (BEYOND M1) ─────────────────
# Three frames: solid colors. heif-enc can build an HEIC sequence from numbered PNGs.
ANIM_HEIC="$DEST/synth-anim-3frames-heics.heic"

magick -size 320x240 xc:"#e63946" -strip "$TMP/frame00.png"
magick -size 320x240 xc:"#457b9d" -strip "$TMP/frame01.png"
magick -size 320x240 xc:"#2a9d8f" -strip "$TMP/frame02.png"
heif-enc --encoder x265 "$TMP/frame00.png" "$TMP/frame01.png" "$TMP/frame02.png" \
    -o "$ANIM_HEIC" 2>/dev/null \
    || { echo "[heif synth] WARNING: heif-enc multi-frame not supported this way; skipping animation fixture."; ANIM_HEIC=""; }
[ -n "$ANIM_HEIC" ] && echo "[heif synth] synth-anim-3frames-heics.heic: $(sha256_of "$ANIM_HEIC")"

# ── Summary ───────────────────────────────────────────────────────────────────
echo ""
echo "[heif synth] Done. Files in $DEST:"
ls -lh "$DEST"/synth-*.heic 2>/dev/null || true
echo ""
echo "[heif synth] NOTE: Upload these to R2 and record their SHA-256 in fetch.sh."
echo "[heif synth] Run: wrangler r2 object put cv-public/simdcodecs/test-images/heif/<name>.heic --file <path> --content-type image/heic --remote"
