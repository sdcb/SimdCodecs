#!/usr/bin/env bash
# JPEG decode time on browser-wasm (Mono AOT, SIMD, one thread, Node.js) next to the desktop JIT
# (TieredCompilation off, one core), same sources and images; total plus per-stage split by ablation.
#
# Usage: bash benchmarks/Sdcb.SimdCodecs.Jpeg.WasmBench/run.sh [--wasm-only|--desktop-only]
# Needs the wasm-tools workload (`dotnet workload install wasm-tools`) and Node.js.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
cache="$here/.cache"
images="$cache/images"
mkdir -p "$images"

# photo.jpg: 1600x1067, q90, 4:2:0, baseline Huffman (the repo's LINQPad sample image).
url=https://cv-public.sdcb.ai/simdcodecs/samples/v1/photo.jpg
sha=f38b24ad5d0a8fbe77a99744faaca785e32cc7c17b761a2d62939fb8339893ba
if [ ! -f "$images/photo.jpg" ] || ! echo "$sha  $images/photo.jpg" | sha256sum -c --status; then
  curl -fsSL "$url" -o "$images/photo.jpg.part"
  echo "$sha  $images/photo.jpg.part" | sha256sum -c --status || { echo "photo.jpg: SHA-256 mismatch" >&2; exit 1; }
  mv "$images/photo.jpg.part" "$images/photo.jpg"
fi

desktop="$here/desktop/Sdcb.SimdCodecs.Jpeg.WasmBench.Desktop.csproj"
dotnet build "$desktop" -c Release -nologo -v:q > "$cache/desktop-build.log" || { cat "$cache/desktop-build.log"; exit 1; }
dll="$here/desktop/bin/Release/net10.0/Sdcb.SimdCodecs.Jpeg.WasmBench.dll"

# 4:4:4 and progressive re-encodes of the same photo, made once on the desktop (our encoder is
# allowed to differ by tier, so the wasm side must not regenerate them).
if [ ! -f "$images/photo-444.jpg" ] || [ ! -f "$images/photo-progressive.jpg" ]; then
  dotnet "$dll" prepare "$images/photo.jpg" "$images"
  # The images are embedded: rebuild so the desktop binary carries the new ones.
  dotnet build "$desktop" -c Release -nologo -v:q > "$cache/desktop-build.log" || { cat "$cache/desktop-build.log"; exit 1; }
fi

if [ "${1:-}" != "--wasm-only" ]; then
  DOTNET_TieredCompilation=0 DOTNET_PROCESSOR_COUNT=1 dotnet "$dll" | tee "$cache/desktop.tsv"
fi

if [ "${1:-}" != "--desktop-only" ]; then
  dotnet publish "$here/Sdcb.SimdCodecs.Jpeg.WasmBench.csproj" -c Release -o "$cache/publish" -nologo > "$cache/publish.log" \
    || { cat "$cache/publish.log"; exit 1; }
  cp "$here/main.mjs" "$cache/publish/wwwroot/"
  (cd "$cache/publish/wwwroot" && node main.mjs) | tr -d '\r' | tee "$cache/wasm.tsv"
fi
