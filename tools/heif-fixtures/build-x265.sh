#!/usr/bin/env bash
# Build high-bit-depth libx265 shared libraries (black-box encoder for the synthetic HEIF fixtures).
#
# vcpkg's x265 port is 8-bit only, so libheif cannot write 10- or 12-bit HEVC with it. This builds
# two single-depth libraries from the pinned x265 4.2 release archive (the same bytes vcpkg pins):
#   out/x265-10/  HIGH_BIT_DEPTH=ON              -> 10-bit
#   out/x265-12/  HIGH_BIT_DEPTH=ON MAIN12=ON    -> 12-bit
# Assembly is off (no nasm needed; only small fixtures are encoded).
#
# x265 is GPL-2.0: the binaries are local build products for generating test data. They are never
# committed and never published. Its sources are not read (AGENTS.md) -- only built.
#
# Windows (Git Bash): needs Visual Studio's C++ tools; set VCVARS64 if vswhere cannot find them.
# Linux: needs cmake and a C++ compiler.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$HERE/out"
VERSION=4.2
URL="https://bitbucket.org/multicoreware/x265_git/get/$VERSION.tar.gz"
# SHA-512 of this archive is the one vcpkg's x265 port pins; SHA-256 taken from the same bytes.
SHA256=0a3d41f6b4e2fe5a49d783d6631bd1a49c44cdf999074a3ff09e2cb71ac8ed33

mkdir -p "$OUT"
ARCHIVE="$OUT/x265-$VERSION.tar.gz"
[ -f "$ARCHIVE" ] || curl --fail --location --retry 3 --silent --show-error "$URL" -o "$ARCHIVE"
echo "$SHA256 *$ARCHIVE" | sha256sum -c -

SRC="$OUT/x265-$VERSION"
if [ ! -d "$SRC" ]; then
    mkdir -p "$SRC"
    tar xzf "$ARCHIVE" -C "$SRC" --strip-components=1
fi

COMMON=(-DCMAKE_BUILD_TYPE=Release -DENABLE_SHARED=ON -DENABLE_CLI=OFF -DENABLE_ASSEMBLY=OFF
        -DHIGH_BIT_DEPTH=ON -DCMAKE_POLICY_VERSION_MINIMUM=3.5)

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
        if [ -z "${VCVARS64:-}" ]; then
            VSWHERE="/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe"
            VS="$("$VSWHERE" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | tr -d '\r')"
            VCVARS64="$VS\\VC\\Auxiliary\\Build\\vcvars64.bat"
        fi
        build() {   # build-dir extra-flag
            local bat="$OUT/build-$1.bat"
            printf 'call "%s"\r\ncmake -S "%s" -B "%s" -G "NMake Makefiles" %s %s || exit /b 1\r\ncmake --build "%s" || exit /b 1\r\n' \
                "$VCVARS64" "$(cygpath -w "$SRC/source")" "$(cygpath -w "$OUT/$1")" "${COMMON[*]}" "$2" "$(cygpath -w "$OUT/$1")" > "$bat"
            cmd //c "$(cygpath -w "$bat")"
        }
        LIB=libx265.dll
        ;;
    *)
        build() { cmake -S "$SRC/source" -B "$OUT/$1" "${COMMON[@]}" $2 && cmake --build "$OUT/$1" --parallel; }
        LIB=libx265.so
        ;;
esac

build x265-10 "-DMAIN12=OFF"
build x265-12 "-DMAIN12=ON"

for d in x265-10 x265-12; do
    [ -f "$OUT/$d/$LIB" ] || { echo "[x265] $OUT/$d/$LIB was not produced" >&2; exit 1; }
done
echo "[x265] ready: $OUT/x265-10/$LIB, $OUT/x265-12/$LIB"
