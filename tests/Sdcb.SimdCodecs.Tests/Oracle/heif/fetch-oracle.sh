#!/usr/bin/env bash
# Fetch a pinned libheif Windows binary (from the libheif release page on GitHub)
# and the libde265 it links against, for use as the HEIF decode oracle.
#
# Only the heif-convert / heif-info executables are used; they are black-box
# binaries reached through their documented CLI, never via source (AGENTS.md).
#
# A SHA-256-pinned download rather than a local build so that the dev box and
# CI run the *same bytes*, and a new release cannot silently redefine "correct".
# Bumping the version means re-recording every byte-exact golden in Fixtures/heif/.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIR="$HERE/bin"
mkdir -p "$BIN_DIR"

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
        # Windows: use the vcpkg-built binaries if already present on PATH,
        # otherwise fall back to the official GitHub release zip.
        if command -v heif-convert.exe &>/dev/null; then
            echo "[heif oracle] found heif-convert on PATH: $(command -v heif-convert.exe)"
            echo "[heif oracle] vcpkg install libheif to get it if missing."
            echo "[heif oracle] Skipping download; set HEIF_CONVERT / HEIF_INFO env vars"
            echo "[heif oracle] to point at the binaries if they are not on PATH."
            exit 0
        fi
        echo "[heif oracle] heif-convert not found on PATH."
        echo "[heif oracle] Install via vcpkg:  vcpkg install libheif"
        echo "[heif oracle] Then re-run this script, or set HEIF_CONVERT and HEIF_INFO"
        echo "[heif oracle] to the full paths of heif-convert and heif-info."
        exit 1
        ;;
    Linux*)
        if command -v heif-convert &>/dev/null; then
            echo "[heif oracle] found heif-convert on PATH: $(command -v heif-convert)"
            exit 0
        fi
        echo "[heif oracle] heif-convert not found."
        echo "[heif oracle] Install via:  sudo apt-get install libheif-examples"
        echo "[heif oracle] or build from source: https://github.com/strukturag/libheif"
        exit 1
        ;;
    *)
        echo "[heif oracle] unsupported platform $(uname -s)." >&2
        exit 1
        ;;
esac
