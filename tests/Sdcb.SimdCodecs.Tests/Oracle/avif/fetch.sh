#!/usr/bin/env bash
# Fetch AVIF test images for the oracle and benchmark tests.
# All images are CC0 / Public Domain (encoded from the CC0 corpus originals and original synthetic
# screen/animation rasters using libavif 1.4.2 + libaom 3.15.3 / SVT-AV1 4.3.1 as black boxes).
# Run from anywhere; files land in tests/Sdcb.SimdCodecs.Tests/Fixtures/avif/.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="$HERE/../../Fixtures/avif"
mkdir -p "$DEST"

fetch() {   # url sha256 dest
    local url="$1" sha256="$2" dest="$3"
    if [ ! -f "$dest" ]; then
        echo "[avif] downloading $(basename "$dest") …"
        curl --fail --location --retry 3 --silent --show-error "$url" --output "$dest"
    fi
    echo "$sha256  $dest" | sha256sum -c -
    echo "[avif] ok: $(basename "$dest")"
}

BASE="https://cv-public.sdcb.ai/simdcodecs/test-images/avif"

# ── Group A: High-resolution performance & peak-memory benchmarks (8 files, ~18.0 MB) ──
fetch "$BASE/perf-photo-mangrove-8192x6144-grid-8b.avif" \
    "20f815e609c9f3b6ceca6076bcb2a1f12dfd865878365417c56ec47c251cd5b3" \
    "$DEST/perf-photo-mangrove-8192x6144-grid-8b.avif"
fetch "$BASE/perf-photo-almsee-4096x3072-grid-10b.avif" \
    "0ab139cb861b2d82b1a87ea13fac7ea3d459bf1497657bd487becabb0dd1c08f" \
    "$DEST/perf-photo-almsee-4096x3072-grid-10b.avif"
fetch "$BASE/perf-photo-teatro-4096x2632-tiles-svt.avif" \
    "ee8b725cd7180076e820dac1f8363568ad132f8c84bfdba3c0148e81e4af0890" \
    "$DEST/perf-photo-teatro-4096x2632-tiles-svt.avif"
fetch "$BASE/perf-photo-fuji-2560x1440-lossless-444.avif" \
    "5fb2a246eb51a1d7054bed323d9c71d540331554dc4aa156cae0a6aae8bf0ce6" \
    "$DEST/perf-photo-fuji-2560x1440-lossless-444.avif"
fetch "$BASE/perf-screen-ui-3840x2160-444-8b.avif" \
    "3251e27fb10eb6a4e9f486978aee4b6b5e933cdaadf698dc896b5ab94b590b99" \
    "$DEST/perf-screen-ui-3840x2160-444-8b.avif"
fetch "$BASE/perf-photo-night-3840x2160-grain-10b.avif" \
    "67ca06952a784da0a9ca41b5c723cb5a70597390f0518a0617b74125e809440c" \
    "$DEST/perf-photo-night-3840x2160-grain-10b.avif"
fetch "$BASE/perf-photo-carter-3840x2560-p2-12b-422.avif" \
    "625220ee4471bb60d6076668ca2f49847eebedc974cc083ab8d882bf5a0950e4" \
    "$DEST/perf-photo-carter-3840x2560-p2-12b-422.avif"
fetch "$BASE/perf-photo-fuji-2560x1440-alpha-10b.avif" \
    "495e51abd4dfb5eb4249ad7cad41cdc6454a95d348869a780b0dd08d37fa61b7" \
    "$DEST/perf-photo-fuji-2560x1440-alpha-10b.avif"

# ── Group B: AV1 profiles, depths, subsamplings & syntax tools (16 files, ~3.5 MB) ──
fetch "$BASE/photo-fuji-1920x1080-p0-8b-420-aom.avif" \
    "bcdb72b39d7b69b8322758110fb201eaa2c0fbe435d3432869e6548c6d0edafc" \
    "$DEST/photo-fuji-1920x1080-p0-8b-420-aom.avif"
fetch "$BASE/photo-fuji-1920x1080-p0-8b-420-svt.avif" \
    "016b039ac38884e1705974eed5dab45c073ea0ac8155b942e02e08a0a8714cd8" \
    "$DEST/photo-fuji-1920x1080-p0-8b-420-svt.avif"
fetch "$BASE/photo-fuji-1920x1080-p0-10b-420-aom.avif" \
    "685b59f3c70b77d3852c99fed8e8b65fb9ac523232679ef2644bf2782fbb4c26" \
    "$DEST/photo-fuji-1920x1080-p0-10b-420-aom.avif"
fetch "$BASE/photo-carter-1440x1680-p0-8b-400-mono.avif" \
    "1005d21835f0f40916a5de9d2058d67833b94d60a36b4d6e8e25c749f1885d45" \
    "$DEST/photo-carter-1440x1680-p0-8b-400-mono.avif"
fetch "$BASE/photo-carter-1440x1680-p0-10b-400-mono.avif" \
    "b6c0c4ef7bb958f9f1bbadfc062247f6a56a3a4b3028732fbb7f57b624574e88" \
    "$DEST/photo-carter-1440x1680-p0-10b-400-mono.avif"
fetch "$BASE/screen-ui-1920x1080-p1-8b-444-gbr.avif" \
    "1e741d50b67279276510ab504c5e61524ee4c9d0cf114aace1e0814e9fb21e88" \
    "$DEST/screen-ui-1920x1080-p1-8b-444-gbr.avif"
fetch "$BASE/screen-ui-1920x1080-p1-10b-444-bt709.avif" \
    "4bee0d51b61682c0fb8753167b9abdd6bdc092c6067cea5de548aaaf2919a786" \
    "$DEST/screen-ui-1920x1080-p1-10b-444-bt709.avif"
fetch "$BASE/photo-almsee-1920x1080-p2-8b-422.avif" \
    "ef1854091b727dad7a90a2af28b3de87fb8434dede9b823bfcbccf980c3dafd3" \
    "$DEST/photo-almsee-1920x1080-p2-8b-422.avif"
fetch "$BASE/photo-almsee-1920x1080-p2-12b-420.avif" \
    "0d94b61ffc30ea1745ace26b31d288fc88f9fc58d96600caf0d3ab6792cccf15" \
    "$DEST/photo-almsee-1920x1080-p2-12b-420.avif"
fetch "$BASE/photo-almsee-960x540-p2-12b-444-lossless.avif" \
    "923612b9c7b1d7b5d627213218c892e8084d3cfaefd6826cb3016cd7478a1e14" \
    "$DEST/photo-almsee-960x540-p2-12b-444-lossless.avif"
fetch "$BASE/screen-text-1280x720-palette-intrabc.avif" \
    "97bbf112e98e84503d0795ec7ca58ce9d568b6cbe025906a54149feb507141cf" \
    "$DEST/screen-text-1280x720-palette-intrabc.avif"
fetch "$BASE/photo-mangrove-1600x1200-superres.avif" \
    "7024e4fa99eb6a4f60876b6221df334ca4d6cbc3c123d7604cb994df09b73c3c" \
    "$DEST/photo-mangrove-1600x1200-superres.avif"
fetch "$BASE/photo-night-1280x720-filmgrain-8b.avif" \
    "c222a5b6050c98c161dd31099c015aa40e9aac97aa81f5e9573db82eaec2a097" \
    "$DEST/photo-night-1280x720-filmgrain-8b.avif"
fetch "$BASE/photo-mangrove-1280x960-seg-deltaq-qm.avif" \
    "b0f8e5c40ac016f076a339eb6c4936341eb4bfebc72090e36b6b133e9da2a131" \
    "$DEST/photo-mangrove-1280x960-seg-deltaq-qm.avif"
fetch "$BASE/screen-testsrc2-1280x720-lr-wiener-sgr.avif" \
    "1822dd1924fc671ffd4d79047d61d0197a9dadc1fa816a202cb289f9034fee6b" \
    "$DEST/screen-testsrc2-1280x720-lr-wiener-sgr.avif"
fetch "$BASE/photo-fuji-1280x720-chroma-colocated.avif" \
    "b4d009ee8c4608f24965d01dd4315fef41867f79ea54417044868fa32eea4e16" \
    "$DEST/photo-fuji-1280x720-chroma-colocated.avif"

# ── Group C: Container, transforms, HDR, gain map, metadata & edge sizes (13 files, ~1.1 MB) ──
fetch "$BASE/container-mangrove-1599x1199-odd-clap.avif" \
    "826d12bec883896525c0cecfb17c780e10ef2dc40384f92fa4686ea0ce0472bf" \
    "$DEST/container-mangrove-1599x1199-odd-clap.avif"
fetch "$BASE/container-fuji-1280x720-clap-irot-imir.avif" \
    "bae95d9d19aee5d32f320ff579ef1eaaf15b9a8845e3f66c78244f292c881029" \
    "$DEST/container-fuji-1280x720-clap-irot-imir.avif"
fetch "$BASE/container-grid-1500x990-3x2-crop.avif" \
    "1b50e54ad1d85d4773a18796d12ebf39a092399f20ce4cd3cf16252b824c6735" \
    "$DEST/container-grid-1500x990-3x2-crop.avif"
fetch "$BASE/container-grid-alpha-1024x768-prem.avif" \
    "52519f93ab1f63bd4f2ff5e790f0e1603019a1858973eb5675835d72f2403d10" \
    "$DEST/container-grid-alpha-1024x768-prem.avif"
fetch "$BASE/container-iovl-overlay-800x600.avif" \
    "2c3b7e176b8274512599a890d0972125965f0f2c47fcdc46d6b8ebbf3bd8c998" \
    "$DEST/container-iovl-overlay-800x600.avif"
fetch "$BASE/container-iden-chain-640x480.avif" \
    "197a65e1108a267d14c7b5b1bdf4ef95e1753385e4ba0ed3e8511d97823def78" \
    "$DEST/container-iden-chain-640x480.avif"
fetch "$BASE/container-tiny-1x1.avif" \
    "9449a4f6be8f0d4e2dffc9984c5127222db65acd9bf053d4b4ed02a1a010c89b" \
    "$DEST/container-tiny-1x1.avif"
fetch "$BASE/container-tiny-3x7.avif" \
    "752cf7bb40415ffb350b49331db7eb0a86b1d63eac8c27448f459633c4a7e9a3" \
    "$DEST/container-tiny-3x7.avif"
fetch "$BASE/color-testsrc2-1280x720-pq-10b-nclx.avif" \
    "e498575a4ade1a227b31c3c71c543fd6874c5b9080eb3c4d3bef1d36b85a0c3e" \
    "$DEST/color-testsrc2-1280x720-pq-10b-nclx.avif"
fetch "$BASE/color-testsrc2-1280x720-hlg-10b-vui.avif" \
    "4cf6c6b812662e4afc2e0a67a33c830cec2ec73e031bb9b7e92d7e0255ccca8b" \
    "$DEST/color-testsrc2-1280x720-hlg-10b-vui.avif"
fetch "$BASE/color-fuji-1280x720-icc-exif-xmp-thmb.avif" \
    "49407078acdb8d37e8f8498a477b99a1dcd32786232c37a482bb18da22fd9a55" \
    "$DEST/color-fuji-1280x720-icc-exif-xmp-thmb.avif"
fetch "$BASE/color-fuji-1280x720-gainmap-tmap.avif" \
    "d1070cf5169de96b49ae3d3ceba0bd530324ad57d78293e259337864fd35fc94" \
    "$DEST/color-fuji-1280x720-gainmap-tmap.avif"
fetch "$BASE/container-layered-a1lx-a1op-1280x720.avif" \
    "995d08d59a2202920520c43a7d438d090d98f6b9a427fc9af7809cb97a93aaf2" \
    "$DEST/container-layered-a1lx-a1op-1280x720.avif"

# ── Group D: Animated AVIF sequences (avis) (5 files, ~5.3 MB) ──
fetch "$BASE/anim-mandelbrot-1280x720-60f-inter.avif" \
    "effd7b0292179230ea77c110bff93fd9c39a5e8be7baa573ab791a5f9f1b78f7" \
    "$DEST/anim-mandelbrot-1280x720-60f-inter.avif"
fetch "$BASE/anim-photo-fuji-1920x1080-30f-10b.avif" \
    "95468b0424f77415e5688b404b0fb000fac74a856b0fc09a205a0aefa54a0107" \
    "$DEST/anim-photo-fuji-1920x1080-30f-10b.avif"
fetch "$BASE/anim-screen-testsrc2-1280x720-24f-444.avif" \
    "613b095e44a5349ac3ba14c6779af33b040f1106ef97623e22c1b3892b830054" \
    "$DEST/anim-screen-testsrc2-1280x720-24f-444.avif"
fetch "$BASE/anim-alpha-640x360-24f-varfps.avif" \
    "95a48533de15aa984ea0bbba8a6b27d1ae4195097fd86129bf76be436f19f3c4" \
    "$DEST/anim-alpha-640x360-24f-varfps.avif"
fetch "$BASE/anim-alltools-320x240-8f.avif" \
    "53734075bea4eae7f2d9a5212220d96217ccb197faeacf8b574d16cd86dac310" \
    "$DEST/anim-alltools-320x240-8f.avif"

echo "[avif] all 42 fixtures ready in $DEST"
