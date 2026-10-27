#!/usr/bin/env bash
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION=1.6.0
SOURCE_DIR="$HERE/src/libwebp-$VERSION"
ARCHIVE="$HERE/src/libwebp-$VERSION.tar.gz"
BUILD_DIR="$HERE/build"
BIN_DIR="$HERE/bin"
URL="https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-$VERSION.tar.gz"

mkdir -p "$HERE/src" "$BIN_DIR"
if [ ! -d "$SOURCE_DIR" ]; then
    curl --fail --location --retry 3 --silent --show-error "$URL" --output "$ARCHIVE"
    tar -xzf "$ARCHIVE" -C "$HERE/src"
fi

cmake -S "$SOURCE_DIR" -B "$BUILD_DIR" -G "Unix Makefiles" \
    -DCMAKE_BUILD_TYPE=Release \
    -DWEBP_BUILD_ANIM_UTILS=ON \
    -DWEBP_BUILD_EXTRAS=OFF \
    -DWEBP_BUILD_CWEBP=OFF \
    -DWEBP_BUILD_DWEBP=OFF \
    -DWEBP_BUILD_GIF2WEBP=OFF \
    -DWEBP_BUILD_IMG2WEBP=OFF \
    -DWEBP_BUILD_VWEBP=OFF \
    -DWEBP_BUILD_WEBPINFO=OFF \
    -DWEBP_BUILD_WEBPMUX=OFF
cmake --build "$BUILD_DIR" --target anim_dump --parallel

ANIM_DUMP_BIN="$(find "$BUILD_DIR" -type f -name anim_dump -perm -111 -print -quit)"
if [ -z "$ANIM_DUMP_BIN" ]; then
    echo "[oracle] anim_dump target built without a discoverable executable" >&2
    exit 1
fi
install -m 755 "$ANIM_DUMP_BIN" "$BIN_DIR/anim_dump"
"$BIN_DIR/anim_dump" -h
