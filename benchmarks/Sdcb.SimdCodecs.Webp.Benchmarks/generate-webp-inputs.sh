#!/usr/bin/env bash
set -euo pipefail

here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
input_dir="${WEBP_BENCHMARK_INPUT_DIR:-"$here/webp-inputs"}"
temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT

mkdir -p "$input_dir"

ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i "testsrc2=s=1920x1080:r=1" -frames:v 1 "$temp_dir/opaque.png"
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i "testsrc2=s=1920x1080:r=1" \
  -vf "format=rgba,colorchannelmixer=aa=0.5" -frames:v 1 "$temp_dir/alpha.png"
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i "testsrc2=s=640x360:r=10" -frames:v 10 "$temp_dir/animation-%02d.png"

cwebp -quiet -q 75 -m 6 "$temp_dir/opaque.png" -o "$input_dir/lossy-opaque-1080p.webp"
cwebp -quiet -q 75 -m 6 -alpha_q 100 "$temp_dir/alpha.png" -o "$input_dir/lossy-alpha-1080p.webp"
cwebp -quiet -lossless -z 6 "$temp_dir/opaque.png" -o "$input_dir/lossless-1080p.webp"
img2webp -loop 0 -d 100 "$temp_dir"/animation-*.png -o "$input_dir/animated-10x640.webp"

printf 'Benchmark inputs generated in %s\n' "$input_dir"
