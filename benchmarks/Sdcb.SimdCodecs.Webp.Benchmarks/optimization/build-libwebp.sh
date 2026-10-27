#!/usr/bin/env bash
# Build a SIMD-enabled libwebp shared library for the WebP benchmark's A/B comparison.
#
# libwebp is used strictly as a BLACK BOX here: the official release tarball is downloaded
# (SHA-256 pinned) and compiled; its sources are never read, copied or linked into
# Sdcb.SimdCodecs.Webp (see AGENTS.md). No CMake needed -- every C file is compiled directly.
#
# Why this exists: prebuilt libwebp.dll packages are not reliable references. The one in the
# Imazen.WebP.NativeRuntime.win-x64 1.6.1 NuGet package runs scalar code (as slow as
# `dwebp -noasm`), which makes any comparison against it meaningless. A build made here picks
# SSE2 / SSE4.1 at run time exactly like the official one -- confirm with the check in README.md.
#
# Usage:   bash benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks/optimization/build-libwebp.sh
# Env:     CC   compiler command (default: "zig cc" if zig is on PATH, else "cc"; clang/gcc work)
# Output:  .cache/libwebp.dll (Windows) | libwebp.so (Linux) | libwebp.dylib (macOS)
#          then: export LIBWEBP_PATH=<printed path>
set -euo pipefail

VERSION=1.6.0
SHA256=e4ab7009bf0629fd11982d4c2aa83964cf244cffba7347ecd39019a9e38c4564
URL="https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-$VERSION.tar.gz"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CACHE="$HERE/../.cache"
SRC="$CACHE/libwebp-$VERSION"
OBJ="$CACHE/obj"
mkdir -p "$CACHE"

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) OUT="$CACHE/libwebp.dll"; EXPORT='-DWEBP_EXTERN=__declspec(dllexport)' ;;
    Darwin*)              OUT="$CACHE/libwebp.dylib"; EXPORT='-DWEBP_EXTERN=extern' ;;
    *)                    OUT="$CACHE/libwebp.so"; EXPORT='-DWEBP_EXTERN=extern' ;;
esac

if [ -z "${CC:-}" ]; then
    if command -v zig >/dev/null 2>&1; then CC="zig cc"; else CC=cc; fi
fi

if [ ! -d "$SRC" ]; then
    curl --fail --location --retry 3 --silent --show-error "$URL" --output "$CACHE/libwebp.tar.gz"
    echo "$SHA256  $CACHE/libwebp.tar.gz" | sha256sum --check --status \
        || { echo "libwebp tarball SHA-256 mismatch" >&2; rm -f "$CACHE/libwebp.tar.gz"; exit 1; }
    tar -xzf "$CACHE/libwebp.tar.gz" -C "$CACHE"
fi

rm -rf "$OBJ"; mkdir -p "$OBJ"
cd "$SRC"
objects=()
# The *_sse41.c units need -msse4.1 (the project's own build does the same); SIMD units for other
# architectures compile to nothing here, so only the MIPS/MSA ones are left out.
for f in src/dec/*.c src/dsp/*.c src/enc/*.c src/utils/*.c sharpyuv/*.c; do
    case "$f" in *_mips*|*_msa*) continue ;; esac
    o="$OBJ/$(echo "$f" | tr '/' '_').o"
    flags=(-O2 -DNDEBUG -I. -Isrc -Isharpyuv "$EXPORT")
    # zig cc defaults to the host CPU (AVX2 auto-vectorisation); the reference must be the
    # generic x86-64 build a distribution ships, with SSE2/SSE4.1 chosen at run time.
    case "$(uname -m)" in
        x86_64|AMD64) case "$CC" in *zig*) flags+=(-mcpu=x86_64) ;; *) flags+=(-march=x86-64) ;; esac ;;
    esac
    case "$f" in *_sse41.c) flags+=(-msse4.1) ;; esac
    $CC -c "$f" -o "$o" "${flags[@]}"
    objects+=("$o")
done
$CC -shared -o "$OUT" "${objects[@]}"

# .NET needs a native path, not the MSYS spelling of it.
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) OUT="$(cd "$CACHE" && pwd -W)/libwebp.dll" ;; esac
echo "built $OUT"
echo "export LIBWEBP_PATH=\"$OUT\""