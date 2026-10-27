#!/usr/bin/env bash
# Fetch the official libjpeg-turbo release binaries used by the JPEG decode
# benchmark and accuracy tooling. Windows (Git Bash) and Linux x64.
#
# Clean-room: libjpeg-turbo is used strictly as a BLACK BOX — only the shared
# library and command-line tools are extracted. Headers, docs and example
# sources inside the packages are deliberately NOT extracted; never read them.
# The P/Invoke surface in TurboJpeg.cs comes from the published API docs.
#
# Pinned: version + SHA-256 of each asset (taken from the GitHub release's
# published digests). A mismatch means the release was re-cut or the download
# was tampered with — stop, do not carry on with different bytes.
set -euo pipefail

LJT_VERSION=3.2.0
BASE="https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/$LJT_VERSION"

HERE="$(cd "$(dirname "$0")/.." && pwd)"
CACHE="$HERE/.cache/libjpeg-turbo"
mkdir -p "$CACHE"

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
        RID=win-x64
        ASSET="libjpeg-turbo-$LJT_VERSION-vc-x64.exe"
        ASSET_SHA256=662761d8ba8dae04aec74023ebaeceb856c2b56b9b59cfd180759d26300dda42
        ;;
    Linux*)
        RID=linux-x64
        ASSET="libjpeg-turbo-official_${LJT_VERSION}_amd64.deb"
        ASSET_SHA256=21297da4a4eb34ebefc54afca5d8dd86c0fdd6a9dfe49b1b962c5d1eeeafd8ec
        ;;
    *)
        echo "[libjpeg-turbo] no pinned asset for $(uname -s)" >&2
        exit 1
        ;;
esac

ARCHIVE="$CACHE/$ASSET"
if [ ! -f "$ARCHIVE" ]; then
    echo "[libjpeg-turbo] downloading $ASSET"
    curl -fsSL "$BASE/$ASSET" -o "$ARCHIVE"
fi
echo "$ASSET_SHA256 *$ARCHIVE" | sha256sum -c -

OUT="$CACHE/$RID"
rm -rf "$OUT"
mkdir -p "$OUT"
TMP="$CACHE/.unpack"
rm -rf "$TMP"
mkdir -p "$TMP"

case "$RID" in
    win-x64)
        # The Windows release is an NSIS installer; 7-Zip unpacks it without running it.
        SEVENZIP="$(command -v 7z || true)"
        if [ -z "$SEVENZIP" ] && [ -x "/c/Program Files/7-Zip/7z.exe" ]; then SEVENZIP="/c/Program Files/7-Zip/7z.exe"; fi
        [ -n "$SEVENZIP" ] || { echo "[libjpeg-turbo] 7-Zip (7z) is required to unpack the installer" >&2; exit 1; }
        "$SEVENZIP" x -y -o"$(cygpath -w "$TMP")" "$(cygpath -w "$ARCHIVE")" 'bin\turbojpeg.dll' 'bin\jpeg62.dll' 'bin\cjpeg.exe' 'bin\djpeg.exe' >/dev/null
        cp "$TMP"/bin/* "$OUT"/
        ;;
    linux-x64)
        if command -v dpkg-deb >/dev/null 2>&1; then
            dpkg-deb -x "$ARCHIVE" "$TMP"
        else
            (cd "$TMP" && ar x "$ARCHIVE" && tar xf data.tar.*)
        fi
        cp -L "$TMP"/opt/libjpeg-turbo/lib64/libturbojpeg.so.0 "$OUT"/libturbojpeg.so
        cp "$TMP"/opt/libjpeg-turbo/bin/cjpeg "$TMP"/opt/libjpeg-turbo/bin/djpeg "$OUT"/
        ;;
esac
rm -rf "$TMP"

ls -la "$OUT"
echo "[libjpeg-turbo] ready: $OUT"
