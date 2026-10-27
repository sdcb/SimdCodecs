#!/usr/bin/env bash
# Generate the committed JPEG 2000 fixtures under Fixtures/jpeg2000/.
#
# Run this ONLY to add or regenerate fixtures; the tests do not need it, and
# they do not need OpenJPEG either. That is the point. A reversible (5/3)
# codestream decodes to its source raster EXACTLY -- verified below, per
# fixture, before anything is committed -- so the committed .pgm IS the
# expected output and the assertion is byte equality with no tolerance and no
# oracle process. Same shape as the committed jbig2enc fixtures, but stronger:
# there the expected raster had to come from jbig2dec because symbol matching
# is lossy, and here the encoder's own input is the answer.
#
# The live oracle (OpenJpegOracle) still exists, and covers what committed
# bytes cannot: the lossy 9/7 path, whose reference output is whatever
# OpenJPEG computes.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
BIN="$HERE/dist/bin"
OUT="$HERE/../../Fixtures/jpeg2000"
LOSSY_OUT="$HERE/../../Fixtures/jpeg2000-lossy"

# Windows dev boxes here have `python` (3.x) and no `python3`; ubuntu-latest has
# `python3` and no bare `python`. Probe by RUNNING each candidate: Windows ships
# a python3.exe App Execution Alias that resolves on PATH but only opens the
# Microsoft Store. Same probe as Oracle/build.sh -- keep them in step.
PY=""
for candidate in python3 python; do
    if command -v "$candidate" > /dev/null 2>&1 && "$candidate" -c "" > /dev/null 2>&1; then
        PY="$candidate"; break
    fi
done
[ -n "$PY" ] || { echo "[jpeg2000 fixtures] ERROR: need python 3.x on PATH." >&2; exit 1; }

COMPRESS="$BIN/opj_compress"
[ -x "$COMPRESS" ] || COMPRESS="$BIN/opj_compress.exe"
[ -x "$COMPRESS" ] || { echo "[jpeg2000 fixtures] ERROR: run fetch.sh first." >&2; exit 1; }
DECOMPRESS="$BIN/opj_decompress"
[ -x "$DECOMPRESS" ] || DECOMPRESS="$BIN/opj_decompress.exe"
export LD_LIBRARY_PATH="$HERE/dist/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

mkdir -p "$OUT"
SCRATCH="$(mktemp -d)"
trap 'rm -rf "$SCRATCH"' EXIT

# name | pattern | w | h | extra opj_compress args
#
# Every entry is lossless and inside the decoder's envelope -- 8-bit unsigned
# components, one tile, maximal precincts, LRCP, reversible 5/3, raw J2K -- and
# varies one thing the decoder must get right. The first block is rung 1's: one
# component, one layer. Anything lossy goes in LOSSY below, since its expected
# output is not its source; anything needing a second tile or 9/7 belongs to a
# later rung.
FIXTURES=(
    # The DWT is switched off entirely (-n 1 = zero decomposition levels), so a
    # failure here is tier-1 or tier-2 and cannot be the wavelet. This is the
    # first fixture that should pass and the one to debug against.
    "nodwt-struct32|struct|32|32|-n 1"
    "nodwt-noise32|noise|32|32|-n 1"
    # One lifting stage, then the default five. Bisects DWT depth.
    "dwt1-struct32|struct|32|32|-n 2"
    "dwt5-struct64|struct|64|64|"
    # Degenerate content. flat = no code-block is ever included in a packet;
    # ramp = smooth, where a dropped sign XOR bit shows up.
    "flat64|flat|64|64|"
    "ramp64|ramp|64|64|"
    "noise64|noise|64|64|"
    # Code-blocks smaller than the subband, so a subband holds a GRID of them:
    # this is what makes the tag trees and the inclusion signalling do work.
    # At 64x64 default they are one-per-subband and the tag tree is trivial.
    "cblk16-noise64|noise|64|64|-b 16,16"
    "cblk4-struct64|struct|64|64|-b 4,4"
    # Dimensions divisible by nothing: partial code-blocks, odd-length lifting,
    # and subbands whose size is not what a halving would predict.
    "odd37x23|struct|37|23|-n 3"
    "odd1x1|struct|1|1|-n 1"
    "odd5x64|struct|5|64|-n 2"
    "odd64x5|struct|64|5|-n 2"

    # Rung 2's exact half: three components. opj_compress applies RCT to three
    # reversibly coded components unless told -mct 0, so these are RCT except
    # the one that says otherwise, which pins that components decode
    # independently when no transform is signalled.
    "rgb-struct64|rgbstruct|64|64|"
    "rgb-noise64|rgbnoise|64|64|"
    "rgb-nomct-struct64|rgbstruct|64|64|-mct 0"
    "rgb-odd37x23|rgbstruct|37|23|-n 3"

    # Quality layers, brought forward from rung 3. The last layer's ratio of 1
    # makes the codestream lossless, so the source is still the answer, but
    # every code-block's passes arrive split across layers. The code-block grid
    # is what gives the tag trees state to carry from one layer to the next:
    # hazard 5, which a single-layer fixture cannot see.
    "layers3-noise64|noise|64|64|-r 40,10,1 -b 16,16"
    "layers3-struct64|struct|64|64|-r 20,5,1 -b 8,8"
    # The shape of the four reversible images measured in real PDFs: RGB, RCT,
    # two layers.
    "rgb-layers2-struct64|rgbstruct|64|64|-r 10,1 -b 16,16"

    # The JP2 file format around a lossless codestream, which is what a .jp2
    # output name makes opj_compress write: signature, file type, a header with
    # an enumerated colour specification (sRGB for three components, greyscale
    # for one), then the codestream.
    "jp2-rgb-struct64|rgbstruct|64|64|"
    "jp2-gray-struct64|struct|64|64|"

    # Twelve bits, from a PGM whose maxval is 4095: the depth past 8 at which
    # the facade has to scale samples into its 16-bit format.
    "deep12-ramp64|ramp12|64|64|"
)

# Lossy codestreams: the 5/3 filter with a rate that stops short of lossless,
# so code-blocks lose their last passes and the source raster is NOT the
# answer. These go to their own directory without a source raster, and their
# tests compare against opj_decompress at run time. Their expected output is a
# reconstruction rule (T.800 E.1.1.2's midpoint), not the encoder's input.
LOSSY=(
    "lossy53-struct64|struct|64|64|-r 6"
    "lossy53-rgb-layers2|rgbstruct|64|64|-r 20,6 -b 16,16"

    # The 9/7 wavelet (-I). Lossy by construction, even with every pass kept,
    # because its coefficients are quantized; with three components opj_compress
    # pairs it with ICT unless told -mct 0. Its expected output is OpenJPEG's
    # float arithmetic, so these are compared within a tolerance, in their own
    # test file, and never alongside the exact cases.
    "lossy97-struct64|struct|64|64|-I"
    "lossy97-noise64|noise|64|64|-I -r 4"
    "lossy97-odd37x23|struct|37|23|-I -n 3"
    "lossy97-rgb-struct64|rgbstruct|64|64|-I"
    "lossy97-rgb-nomct-struct64|rgbstruct|64|64|-I -mct 0"
    # The shape of the two 9/7 images measured in real PDFs: RGB, ICT, several
    # layers.
    "lossy97-rgb-layers3|rgbstruct|64|64|-I -r 40,20,8 -b 16,16"
)

source_extension() {
    case "$1" in
        rgb*) echo ppm ;;
        *) echo pgm ;;
    esac
}

echo "[jpeg2000 fixtures] writing to $OUT"
for spec in "${FIXTURES[@]}"; do
    IFS='|' read -r name pattern w h extra <<< "$spec"
    ext="$(source_extension "$pattern")"
    src="$OUT/$name.$ext"
    # opj_compress picks its container from the output name: .jp2 wraps the
    # codestream in the JP2 file format, .j2k writes it bare.
    container=j2k
    case "$name" in jp2-*) container=jp2 ;; esac
    j2k="$OUT/$name.$container"

    "$PY" "$HERE/gen-sources.py" "$src" "$pattern" "$w" "$h"
    # shellcheck disable=SC2086 -- $extra is a deliberate argument list.
    "$COMPRESS" -i "$src" -o "$j2k" $extra > /dev/null 2>&1

    # Verify the lossless claim rather than trusting the flag. If OpenJPEG's
    # own decoder does not reproduce the source byte-for-byte then the fixture
    # is not a valid expected-output pair, and committing it would bake a wrong
    # answer into a test that asserts exact equality.
    "$DECOMPRESS" -i "$j2k" -o "$SCRATCH/rt.$ext" > /dev/null 2>&1
    "$PY" - "$src" "$SCRATCH/rt.$ext" <<'PYEOF'
import sys

def payload(path):
    b = open(path, 'rb').read()
    i, toks = 0, []
    while len(toks) < 4:
        while i < len(b) and b[i:i + 1].isspace():
            i += 1
        if b[i:i + 1] == b'#':          # opj stamps "#OpenJPEG-2.5.4" here,
            while i < len(b) and b[i] != 0x0A:   # so the HEADERS never match
                i += 1                  # even when every pixel does.
            continue
        j = i
        while j < len(b) and not b[j:j + 1].isspace():
            j += 1
        toks.append(b[i:j]); i = j
    return toks[1], toks[2], b[i + 1:]

a, b_ = payload(sys.argv[1]), payload(sys.argv[2])
if a != b_:
    sys.exit("    NOT LOSSLESS -- refusing to commit this fixture")
PYEOF
    printf '  %-24s %6s bytes  %s\n' "$name.$container" "$(wc -c < "$j2k")" "${extra:-(defaults)}"
done

echo "[jpeg2000 fixtures] done -- $(ls "$OUT"/*.j2k "$OUT"/*.jp2 | wc -l) files, each verified lossless"

mkdir -p "$LOSSY_OUT"
echo "[jpeg2000 fixtures] writing lossy codestreams to $LOSSY_OUT"
for spec in "${LOSSY[@]}"; do
    IFS='|' read -r name pattern w h extra <<< "$spec"
    src="$SCRATCH/$name.$(source_extension "$pattern")"
    j2k="$LOSSY_OUT/$name.j2k"

    "$PY" "$HERE/gen-sources.py" "$src" "$pattern" "$w" "$h" > /dev/null
    # shellcheck disable=SC2086 -- $extra is a deliberate argument list.
    "$COMPRESS" -i "$src" -o "$j2k" $extra > /dev/null 2>&1
    printf '  %-24s %6s bytes  %s\n' "$name.j2k" "$(wc -c < "$j2k")" "$extra"
done

# Four components, which no PNM carries, so the source is a planar .raw (what
# opj_compress reads with -F) and lives in a directory of its own beside the
# codestream. It is lossless with no component transform, so the raw is the
# expected output, verified here the same way as the PNM fixtures.
RAW_OUT="$HERE/../../Fixtures/jpeg2000-raw"
mkdir -p "$RAW_OUT"
echo "[jpeg2000 fixtures] writing raw-sourced codestreams to $RAW_OUT"
"$PY" "$HERE/gen-sources.py" "$RAW_OUT/cmyk16.raw" planar4 16 16 > /dev/null
"$COMPRESS" -i "$RAW_OUT/cmyk16.raw" -o "$RAW_OUT/cmyk16.j2k" -F 16,16,4,8,u -mct 0 -n 3 > /dev/null 2>&1
"$DECOMPRESS" -i "$RAW_OUT/cmyk16.j2k" -o "$SCRATCH/rt.raw" > /dev/null 2>&1
cmp -s "$RAW_OUT/cmyk16.raw" "$SCRATCH/rt.raw" || { echo "    cmyk16 NOT LOSSLESS -- refusing to commit it" >&2; exit 1; }
printf '  %-24s %6s bytes  %s\n' "cmyk16.j2k" "$(wc -c < "$RAW_OUT/cmyk16.j2k")" "-F 16,16,4,8,u -mct 0 -n 3"
