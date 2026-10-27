#!/usr/bin/env bash
# Download the benchmark corpus originals listed in corpus/manifest.tsv into the
# git-ignored .cache/corpus/originals/, verifying each file's SHA-256.
#
# Every entry is a Wikimedia Commons file whose licence is CC0 or Public Domain
# (see the manifest's licence and description-page columns). Nothing here is
# committed: the manifest (URL + SHA-256) is the source of truth. If Commons
# serves different bytes (a re-upload), the check fails — pick the new version
# deliberately rather than benchmarking against silently different input.
#
# Works on Linux and on Windows (Git Bash). After fetching, run
#   dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks -- prepare
# to build the master PNGs and the libjpeg-turbo-encoded test matrix.
set -euo pipefail

HERE="$(cd "$(dirname "$0")/.." && pwd)"
MANIFEST="$HERE/corpus/manifest.tsv"
DST="$HERE/.cache/corpus/originals"
mkdir -p "$DST"

# Wikimedia asks API/download clients to identify themselves.
UA="Sdcb.SimdCodecs-bench-corpus/0.1 (https://github.com/sdcb/SimdCodecs)"

fail=0
while IFS=$'\t' read -r name category license width height sha256 url page; do
    case "$name" in ''|'#'*|name) continue ;; esac
    file="$DST/$name.jpg"
    if [ ! -f "$file" ]; then
        echo "[corpus] downloading $name"
        curl -fsSL --retry 4 --retry-delay 10 -A "$UA" "$url" -o "$file.part"
        mv "$file.part" "$file"
    fi
    if ! echo "$sha256 *$file" | sha256sum -c --quiet -; then
        echo "[corpus] SHA-256 mismatch: $name ($url)" >&2
        fail=1
    fi
done < "$MANIFEST"

[ "$fail" -eq 0 ] || exit 1
echo "[corpus] ready: $DST"
