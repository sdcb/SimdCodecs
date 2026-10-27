#!/usr/bin/env bash
# Fetch the OpenJPEG reference tools (opj_compress / opj_decompress / opj_dump)
# for the JPEG 2000 oracle tests. See README.md for context.
#
# Unlike the other two oracle scripts here, this one DOWNLOADS a prebuilt
# binary rather than compiling from source. OpenJPEG needs CMake, and upstream
# publishes official per-platform builds for exactly the two platforms that
# matter (this dev box and ubuntu-latest CI), so a verified download is both
# less machinery and a stronger guarantee: dev box and CI then run the *same
# bytes*, where two local CMake builds would only run the same source.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
DIST="$HERE/dist"

# Pinned, for the same reason STB_SHA256 is pinned: an oracle
# that upstream can move is an oracle that can redefine "correct" underneath a
# green build. The distributed archives carry no upstream checksum file, so the
# SHA-256s below were taken from the assets at pin time and are the contract --
# a mismatch means the release was re-cut or the download was tampered with, and
# either way the right move is to stop, not to carry on with different bytes.
#
# Bumping the version means re-checking every tolerance in the 9/7 irreversible
# tests: the reversible 5/3 path is exact and cannot drift, but the lossy path's
# reference output is whatever this build computes.
OPJ_VERSION=v2.5.4
OPJ_BASE="https://github.com/uclouvain/openjpeg/releases/download/$OPJ_VERSION"

case "$(uname -s)" in
    Linux*)
        ASSET="openjpeg-$OPJ_VERSION-linux-x86_64.tar.gz"
        ASSET_SHA256=77915284c4823bbb5f75053d2b6ec8af11378c9fc5f3d1742a17e1bed984277d
        ;;
    MINGW*|MSYS*|CYGWIN*)
        ASSET="openjpeg-$OPJ_VERSION-windows-x64.zip"
        ASSET_SHA256=655f6111449da83f5424f76d74873116bc01ce50cc10361d2b0b4667c3e5e8c3
        ;;
    *)
        echo "[jpeg2000 oracle] no pinned OpenJPEG build for $(uname -s)." >&2
        echo "[jpeg2000 oracle] add the asset + SHA-256 here, or install the tools yourself" >&2
        echo "[jpeg2000 oracle] and point OPJ_HOME at the prefix containing bin/opj_decompress." >&2
        exit 1
        ;;
esac

ARCHIVE="$HERE/$ASSET"
if [ ! -f "$ARCHIVE" ]; then
    echo "[jpeg2000 oracle] downloading $ASSET"
    curl -sSL "$OPJ_BASE/$ASSET" -o "$ARCHIVE"
fi
echo "$ASSET_SHA256 *$ARCHIVE" | sha256sum -c -

# Extract into dist/, flattening the versioned top-level directory so the
# harness has one stable path to resolve. Note for anyone re-running this from
# Git Bash on Windows: the LINUX tarball carries symlinks (libopenjp2.so ->
# .so.7) that Git Bash cannot create without developer mode, which is why each
# platform only ever unpacks its own archive rather than both.
rm -rf "$DIST"
mkdir -p "$DIST"
case "$ASSET" in
    *.tar.gz) tar xzf "$ARCHIVE" -C "$DIST" --strip-components=1 ;;
    *.zip)
        TMP="$HERE/.unzip-tmp"
        rm -rf "$TMP"
        unzip -q "$ARCHIVE" -d "$TMP"
        mv "$TMP"/*/* "$DIST/"
        rm -rf "$TMP"
        ;;
esac

# Prove it runs before declaring success. On Linux this is not a formality: the
# binaries have no RUNPATH, so a system libopenjp2 (Ubuntu jammy ships 2.4.0)
# wins the search and the tool dies with `undefined symbol:
# opj_decoder_set_strict_mode`. LD_LIBRARY_PATH fixes it, and OpenJpegOracle
# sets the same variable when it shells out -- if you change one, change both.
EXE="$DIST/bin/opj_decompress"
[ -x "$EXE" ] || EXE="$DIST/bin/opj_decompress.exe"
export LD_LIBRARY_PATH="$DIST/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
# `-h` exits NON-ZERO (it is the usage path), so the output is captured rather
# than piped -- under `set -euo pipefail` a pipeline would fail on the exit code
# even when the banner is right there. The banner is the real check anyway: it
# reports the library the tool actually LOADED, so it catches the Linux case
# above where the binary is the pinned one and the .so is the system's.
BANNER="$("$EXE" -h 2>&1 || true)"
case "$BANNER" in
    *"openjp2 library v${OPJ_VERSION#v}"*) ;;
    *)
        echo "[jpeg2000 oracle] ERROR: $EXE did not report openjp2 v${OPJ_VERSION#v}." >&2
        echo "$BANNER" | head -5 >&2
        exit 1
        ;;
esac

echo "[jpeg2000 oracle] ready: $DIST/bin"
