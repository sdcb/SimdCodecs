#!/usr/bin/env bash
# Fetch a native Windows jbig2dec for the JBIG2 oracle tests (Git Bash on Windows).
# Linux/CI uses `apt-get install jbig2dec` instead. See README.md for context.
#
# Upstream ships no Windows binary, so this pulls MSYS2's UCRT64 build: three
# packages (jbig2dec + the libpng/zlib DLLs it links), pinned by file name and
# SHA-256. jbig2dec is AGPL: it is only ever RUN as a black box, never read,
# linked or redistributed — dist/ is git-ignored.
#
# MSYS2 prunes old package versions from its repo over time. If a download
# 404s, pick the current file names from https://repo.msys2.org/mingw/ucrt64/,
# and update the names and SHA-256s below together.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
DIST="$HERE/dist"
CACHE="$HERE/.pkg"
BASE="https://repo.msys2.org/mingw/ucrt64"

PKGS=(
    "mingw-w64-ucrt-x86_64-jbig2dec-0.20-1-any.pkg.tar.zst df57282cc76e53557fcc36a812d4a07e437f1c3f36f2477d871ff416518522b3"
    "mingw-w64-ucrt-x86_64-libpng-1.6.59-1-any.pkg.tar.zst 7f8448e06eceba8f8ce130390addea49f767bdf8f6b7e7eede8ca6d595e3d3a8"
    "mingw-w64-ucrt-x86_64-zlib-1.3.2-2-any.pkg.tar.zst 841401182976d2f9e17e5c0ebaac51f2a8014140ea53d67625e91c8fb3c85ea0"
)

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) ;;
    *) echo "[jbig2 oracle] this script is for Windows; on Linux run: apt-get install -y jbig2dec" >&2; exit 1 ;;
esac

# Git Bash's GNU tar has no zstd; Windows' own bsdtar (System32\tar.exe) does.
WINTAR="$(cygpath -u "${SYSTEMROOT:-C:\\Windows}")/System32/tar.exe"
[ -x "$WINTAR" ] || { echo "[jbig2 oracle] $WINTAR not found (needs Windows 10 1803+)" >&2; exit 1; }

mkdir -p "$CACHE"
rm -rf "$DIST"
mkdir -p "$DIST"
for entry in "${PKGS[@]}"; do
    file="${entry%% *}"
    sha="${entry##* }"
    if [ ! -f "$CACHE/$file" ]; then
        echo "[jbig2 oracle] downloading $file"
        curl -fsSL "$BASE/$file" -o "$CACHE/$file"
    fi
    echo "$sha *$CACHE/$file" | sha256sum -c -
    (cd "$DIST" && "$WINTAR" -xf "$(cygpath -w "$CACHE/$file")" ucrt64/bin)
done

# Flatten ucrt64/bin -> bin so the harness has one stable path.
mv "$DIST/ucrt64/bin" "$DIST/bin"
rm -rf "$DIST/ucrt64"

"$DIST/bin/jbig2dec.exe" --version
echo "[jbig2 oracle] ready: $DIST/bin"
