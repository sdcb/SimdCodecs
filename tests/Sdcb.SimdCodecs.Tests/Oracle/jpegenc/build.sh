#!/usr/bin/env bash
# Build the jpegenc oracle for the Sdcb.SimdCodecs.Jpeg encoder byte-exactness tests.
# Downloads a PINNED stb_image_write.h (byte-exactness depends on the exact
# reference version) and compiles jpegenc.exe with clang — no CMake/MSVC needed.
# See README.md for context.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
BIN="$HERE/../bin"
mkdir -p "$BIN"

# Pinned to the exact revision JpegEncoder was ported against. If upstream ever
# changes the JPEG writer, bump both and re-freeze the golden.
STB_SHA=1ee679ca2ef753a528db5ba6801e1067b40481b8
STB_SHA256=cbd5f0ad7a9cf4468affb36354a1d2338034f2c12473cf1a8e32053cb6914a05
HDR="$HERE/stb_image_write.h"

if [ ! -f "$HDR" ]; then
    echo "[jpegenc oracle] downloading pinned stb_image_write.h ($STB_SHA)"
    curl -sSL "https://raw.githubusercontent.com/nothings/stb/$STB_SHA/stb_image_write.h" -o "$HDR"
fi
echo "$STB_SHA256 *$HDR" | sha256sum -c -

# Any clang driver works: $CC if set, else clang, else `zig cc` (clang bundled in a
# single download — handy on Windows boxes without an LLVM install).
if [ -z "${CC:-}" ]; then
    if command -v clang >/dev/null 2>&1; then CC=clang
    elif command -v zig >/dev/null 2>&1; then CC="zig cc"
    else echo "[jpegenc oracle] no clang or zig on PATH (set CC=...)" >&2; exit 1
    fi
fi

echo "[jpegenc oracle] building jpegenc.exe with $CC"
# -ffp-contract=off is load-bearing: clang on aarch64 (and any FMA target) would
# otherwise fuse the DCT/colour multiply-adds into single fma() ops with different
# rounding than the managed encoder's separate multiply+add. Off = the canonical,
# platform-independent stbiw result that JpegEncoder reproduces bit-for-bit.
$CC -O2 -w -ffp-contract=off -I"$HERE" -o "$BIN/jpegenc.exe" "$HERE/jpegenc.c"

ls -la "$BIN/jpegenc.exe"
echo "[jpegenc oracle] done"
