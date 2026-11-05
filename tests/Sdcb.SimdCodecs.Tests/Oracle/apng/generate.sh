#!/usr/bin/env bash
# Regenerates the APNG fixtures in tests/Sdcb.SimdCodecs.Tests/Fixtures/png/apng and apng-golden.tsv: synthetic sequences from ffmpeg's own
# lavfi sources (nothing third-party), encoded by ffmpeg's APNG encoder, with the MD5 of every frame as ffmpeg's decoder composites it.
# The files and digests are committed, so CI (which has no ffmpeg) still compares our decode against them; PngApngOracleTests also compares
# the files with a live ffmpeg where one is installed. Run this only to add a fixture: a golden is never regenerated to make a test pass.
#
#   FFMPEG=/path/to/ffmpeg bash tests/Sdcb.SimdCodecs.Tests/Oracle/apng/generate.sh          (ffmpeg 8.1.2, the pinned version)
set -euo pipefail
ffmpeg="${FFMPEG:-ffmpeg}"
out="$(cd "$(dirname "$0")/../../Fixtures/png/apng" && pwd)"
golden="$out/apng-golden.tsv"

sprite="color=c=black@0.0:s=48x32:r=10,format=rgba[bg];testsrc2=s=16x16:r=10,format=rgba[fg];[bg][fg]overlay=x='t*20':y=8:format=auto"
half="color=c=black@0.0:s=48x32:r=10,format=rgba[bg];testsrc2=s=20x20:r=10,format=rgba,colorchannelmixer=aa=0.5[fg];[bg][fg]overlay=x='t*16':y='4+t*2':format=auto"

encode() { # name, pixel format, plays, filter graph ('' for plain testsrc2)
  local name="$1" fmt="$2" plays="$3" graph="$4"
  if [ -z "$graph" ]; then
    "$ffmpeg" -v error -y -f lavfi -i "testsrc2=s=48x32:r=10" -frames:v 8 -plays "$plays" -pix_fmt "$fmt" -f apng "$out/$name.apng"
  else
    "$ffmpeg" -v error -y -filter_complex "$graph" -frames:v 8 -plays "$plays" -pix_fmt "$fmt" -f apng "$out/$name.apng"
  fi
}

mkdir -p "$out"
encode synth-testsrc2-rgb24 rgb24 0 ""
encode synth-testsrc2-rgb48 rgb48be 2 ""
encode synth-testsrc2-gray gray 0 ""
encode synth-sprite-rgba rgba 3 "$sprite"
encode synth-sprite-pal8 pal8 0 "$sprite"
encode synth-sprite-ya8 ya8 0 "$sprite"
encode synth-sprite-rgba64 rgba64be 0 "$sprite"
encode synth-halfalpha-rgba rgba 0 "$half"

{
  echo "# fixture, frame, MD5 of the frame's RGBA as ffmpeg 8.1.2 decodes it (rgba; rgba64le for the 16-bit files)"
  for f in "$out"/synth-*.apng; do
    name="$(basename "$f")"
    case "$name" in *rgb48*|*rgba64*) pix=rgba64le ;; *) pix=rgba ;; esac
    "$ffmpeg" -v error -i "$f" -fps_mode passthrough -pix_fmt "$pix" -f framemd5 - | grep -v '^#' | awk -v n="$name" '{ printf "%s\t%d\t%s\n", n, NR - 1, $NF }'
  done
} > "$golden"
echo "wrote $(ls "$out"/*.apng | wc -l) fixtures and $golden"
