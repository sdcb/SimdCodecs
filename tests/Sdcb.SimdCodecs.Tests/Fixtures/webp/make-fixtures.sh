#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cwebp="${CWEBP:-$(command -v cwebp)}"
ffmpeg="${FFMPEG:-$(command -v ffmpeg)}"
webpinfo="${WEBPINFO:-$(command -v webpinfo)}"
img2webp="${IMG2WEBP:-$(command -v img2webp)}"
gif2webp="${GIF2WEBP:-$(command -v gif2webp)}"
webpmux="${WEBPMUX:-$(command -v webpmux)}"

python3 - "$tmp" "$root" <<'PY'
import struct
import sys
import zlib
from pathlib import Path

directory = Path(sys.argv[1])
fixture_directory = Path(sys.argv[2])

def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

def write_png(name, width, height, pixel):
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        for x in range(width):
            rows.extend(pixel(x, y))
    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    data = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")
    (directory / f"{name}.png").write_bytes(data)

def write_png_rgba(name, width, height, pixel):
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        for x in range(width):
            rows.extend(pixel(x, y))
    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    data = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")
    (directory / f"{name}.png").write_bytes(data)

write_png("flat-gray-16x16", 16, 16, lambda _x, _y: (128, 128, 128))
write_png("gradient-16x16", 16, 16, lambda x, y: (x * 17, y * 17, (x + y) * 8))

seed = 0x12345678
def noise(_x, _y):
    global seed
    seed = (1664525 * seed + 1013904223) & 0xffffffff
    return seed >> 24, (seed >> 16) & 255, (seed >> 8) & 255
write_png("noise-32x32", 32, 32, noise)
seed = 0x12345678
write_png("noise-17x17", 17, 17, noise)
write_png("odd-33x19", 33, 19, lambda x, y: (
    (x * 23 + y * 7) & 255,
    (x * 5 + y * 19) & 255,
    (x * 11 + y * 13) & 255,
))
write_png("lossless-1x1", 1, 1, lambda _x, _y: (17, 83, 211))
write_png("lossless-3x7", 3, 7, lambda x, y: ((x * 71 + y * 13) & 255,
                                                (x * 17 + y * 53) & 255,
                                                (x * 31 + y * 7) & 255))
for colors in (2, 4, 16, 200):
    palette = [(i * 47 & 255, i * 83 & 255, i * 131 & 255) for i in range(colors)]
    write_png(f"palette-{colors}", 40, 17,
              lambda x, y, palette=palette: palette[(x * 3 + y * 7) % len(palette)])
write_png_rgba("lossless-exact-alpha", 33, 19,
               lambda x, y: ((x * 23 + y * 7) & 255,
                             (x * 5 + y * 19) & 255,
                             (x * 11 + y * 13) & 255,
                             0 if (x + y) % 5 == 0 else (x * 17 + y * 31) & 255))
write_png_rgba("lossy-alpha", 33, 19,
               lambda x, y: ((x * 23 + y * 7) & 255,
                             (x * 5 + y * 19) & 255,
                             (x * 11 + y * 13) & 255,
                             0 if (x + y) % 7 == 0 else (x * 17 + y * 31) & 255))
write_png("anim-vp8l-alpha-hint-opaque", 33, 19,
          lambda x, y: ((x * 23 + y * 7) & 255,
                        (x * 5 + y * 19) & 255,
                        (x * 11 + y * 13) & 255))
write_png_rgba("anim-vp8l-alpha-hint", 33, 19,
               lambda x, y: ((x * 23 + y * 7) & 255,
                             (x * 5 + y * 19) & 255,
                             (x * 11 + y * 13) & 255,
                             0 if (x + y) % 5 == 0 else (x * 17 + y * 31) & 255))
(fixture_directory / "metadata.icc").write_bytes(bytes.fromhex("00000010776562702d6963632d74657374"))
(fixture_directory / "metadata.exif").write_bytes(bytes.fromhex("4578696600004d4d002a000000080000ff"))
(fixture_directory / "metadata.xmp").write_bytes(
    b"<x:xmpmeta xmlns:x='adobe:ns:meta/'>webp</x:xmpmeta>")
for frame in range(3):
    write_png(f"anim-frame-{frame}", 12, 10,
              lambda x, y, frame=frame: ((x * 31 + y * 7 + frame * 61) & 255,
                                         (x * 9 + y * 23 + frame * 37) & 255,
                                         (x * 13 + y * 17 + frame * 83) & 255))
for frame, width, height in ((0, 7, 5), (1, 5, 7), (2, 4, 3), (3, 6, 4), (4, 3, 5)):
    write_png(f"anim-mux-{frame}", width, height,
              lambda x, y, frame=frame: ((x * 37 + y * 11 + frame * 47) & 255,
                                         (x * 19 + y * 29 + frame * 31) & 255,
                                         (x * 7 + y * 41 + frame * 73) & 255))
for frame in range(4):
    write_png_rgba(f"anim-gif-{frame:02}", 16, 12,
                   lambda x, y, frame=frame: ((x * 23 + frame * 53) & 255,
                                              (y * 31 + frame * 37) & 255,
                                              ((x + y) * 17 + frame * 61) & 255,
                                              0 if ((x - frame * 2) % 16 < 5) else 255))
PY

cwebp -version > "$root/cwebp-version.txt" 2>&1
"$ffmpeg" -version > "$root/ffmpeg-version.txt" 2>&1
"$img2webp" -version > "$root/img2webp-version.txt" 2>&1
"$gif2webp" -version > "$root/gif2webp-version.txt" 2>&1
libvpx_version=""
if command -v pkg-config >/dev/null 2>&1 && pkg-config --exists vpx; then
    libvpx_version="$(pkg-config --modversion vpx)"
elif command -v brew >/dev/null 2>&1 && brew list --versions libvpx >/dev/null 2>&1; then
    libvpx_version="$(brew list --versions libvpx | awk '{ print $2 }')"
fi
printf 'libvpx %s\n' "${libvpx_version:-unknown}" > "$root/libvpx-version.txt"

cwebp -quiet -q 75 -m 0 -f 0 -segments 1 "$tmp/flat-gray-16x16.png" -o "$root/flat-gray-16x16.webp"
cwebp -quiet -q 75 -m 0 -f 0 "$tmp/gradient-16x16.png" -o "$root/gradient-16x16.webp"
cwebp -quiet -q 75 -m 0 -f 0 -segments 1 "$tmp/noise-32x32.png" -o "$root/noise-32x32.webp"
cwebp -quiet -q 75 -m 0 -f 0 -segments 4 "$tmp/noise-32x32.png" -o "$root/noise-32x32-segments4.webp"
cwebp -quiet -q 75 -m 0 -f 0 -segments 1 "$tmp/noise-17x17.png" -o "$root/noise-17x17.webp"
cwebp -quiet -q 75 -m 0 -f 30 -strong -sharpness 0 -segments 1 \
    "$tmp/noise-17x17.png" -o "$root/noise-17x17-f30-strong-s0.webp"
cwebp -quiet -q 75 -m 0 -f 30 -nostrong -sharpness 0 -segments 1 \
    "$tmp/noise-32x32.png" -o "$root/noise-32x32-f30-simple-s0.webp"
cwebp -quiet -q 75 -m 0 -f 30 -strong -sharpness 0 -segments 1 \
    "$tmp/noise-32x32.png" -o "$root/noise-32x32-f30-strong-s0.webp"
cwebp -quiet -q 75 -m 0 -f 30 -strong -sharpness 7 -segments 1 \
    "$tmp/noise-32x32.png" -o "$root/noise-32x32-f30-strong-s7.webp"

"$ffmpeg" -hide_banner -loglevel error -y -f lavfi \
    -i "nullsrc=s=64x48,geq=lum='random(1)*255':cb=128:cr=128" \
    -frames:v 1 "$tmp/noise-64x48.png"
for partitions in 2 4 8; do
    ivf="$tmp/noise-64x48-partitions$partitions.ivf"
    output="$root/noise-64x48-vp8-partitions$partitions.webp"
    "$ffmpeg" -hide_banner -loglevel error -y -i "$tmp/noise-64x48.png" \
        -c:v libvpx -slices "$partitions" -b:v 4M -frames:v 1 -f ivf "$ivf"
    python3 "$root/wrap-vp8-ivf.py" "$ivf" "$output"
    actual_partitions="$("$webpinfo" -bitstream_info "$output" | awk '/Total partitions:/ { print $3 }')"
    if [[ "$actual_partitions" != "$partitions" ]]; then
        printf 'Expected %s token partitions, got %s in %s\n' \
            "$partitions" "${actual_partitions:-unknown}" "$output" >&2
        exit 1
    fi
done

"$ffmpeg" -hide_banner -loglevel error -y -i "$tmp/odd-33x19.png" \
    -frames:v 1 "$tmp/odd-33x19-ffmpeg.png"
"$ffmpeg" -hide_banner -loglevel error -y -i "$tmp/odd-33x19-ffmpeg.png" \
    -c:v libvpx -slices 1 -b:v 4M -frames:v 1 -f ivf "$tmp/odd-33x19.ivf"
python3 "$root/wrap-vp8-ivf.py" "$tmp/odd-33x19.ivf" \
    "$root/odd-33x19-libvpx.webp"

"$ffmpeg" -hide_banner -loglevel error -y -f lavfi \
    -i "testsrc2=size=320x240:rate=1" -frames:v 1 "$tmp/testsrc2-320x240.png"
cwebp -quiet "$tmp/testsrc2-320x240.png" -o "$root/testsrc2-320x240-q75.webp"
cwebp -quiet -q 75 -f 0 "$tmp/testsrc2-320x240.png" \
    -o "$root/testsrc2-320x240-f0.webp"
cwebp -quiet -q 75 -m 0 -f 100 -sharpness 3 -segments 1 \
    "$tmp/testsrc2-320x240.png" -o "$root/testsrc2-320x240-q75-f100-s3.webp"
cwebp -quiet -q 60 -segments 4 -sns 100 -f 60 \
    "$tmp/testsrc2-320x240.png" -o "$root/testsrc2-320x240-q60-segments4-sns100-f60.webp"

# Low-quality lossy on photographic content: high filter levels with strong real edges are the only
# place RFC 6386 15.2's c(a + 4) >> 3 clamp bites, and neither noise nor testsrc2 at q75 gets there.
# The source is scikit-image's `astronaut` (NASA, public domain), area-downscaled to keep it small.
python3 -c "import sys, skimage.data, skimage.io; skimage.io.imsave(sys.argv[1], skimage.data.astronaut(), check_contrast=False)" \
    "$tmp/astronaut-512.png"
"$ffmpeg" -hide_banner -loglevel error -y -i "$tmp/astronaut-512.png" \
    -vf scale=192:192:flags=area "$tmp/astronaut-192.png"
for quality in 0 5 10 15 20; do
    cwebp -quiet -q "$quality" -strong "$tmp/astronaut-192.png" \
        -o "$root/astronaut-192-q$quality-normal.webp"
    cwebp -quiet -q "$quality" -nostrong "$tmp/astronaut-192.png" \
        -o "$root/astronaut-192-q$quality-simple.webp"
done

for quality in 0 5 50 95 100; do
    cwebp -quiet -q "$quality" -m 0 -f 0 -segments 1 \
        "$tmp/noise-32x32.png" -o "$root/noise-32x32-q$quality.webp"
done

for z in 0 6 9; do
    cwebp -quiet -lossless -z "$z" "$tmp/noise-17x17.png" \
        -o "$root/lossless-z$z.webp"
done
for dimensions in 1x1 3x7; do
    cwebp -quiet -lossless -z 6 "$tmp/lossless-$dimensions.png" \
        -o "$root/lossless-$dimensions.webp"
done
cwebp -quiet -lossless -exact "$tmp/lossless-exact-alpha.png" \
    -o "$root/lossless-exact-alpha.webp"
cwebp -quiet -lossless -near_lossless 60 "$tmp/noise-17x17.png" \
    -o "$root/lossless-near60.webp"
for colors in 2 4 16 200; do
    cwebp -quiet -lossless "$tmp/palette-$colors.png" \
        -o "$root/lossless-palette-$colors.webp"
done

for method in 0 1; do
    for filter in none fast best; do
        cwebp -quiet -q 75 -alpha_method "$method" -alpha_filter "$filter" \
            "$tmp/lossy-alpha.png" -o "$root/lossy-alpha-m${method}-${filter}.webp"
    done
done
cwebp -quiet -q 75 -alpha_method 1 -alpha_q 50 "$tmp/lossy-alpha.png" \
    -o "$root/lossy-alpha-q50.webp"

"$img2webp" -loop 3 -lossy -q 75 -m 0 "$tmp/anim-frame-0.png" \
    -d 75 -lossy -q 60 "$tmp/anim-frame-1.png" \
    -d 125 -lossy -q 85 "$tmp/anim-frame-2.png" \
    -o "$root/anim-img2webp-lossy.webp"
"$img2webp" -loop 2 -lossless "$tmp/anim-frame-0.png" \
    -d 80 -lossless "$tmp/anim-frame-1.png" \
    -d 120 -lossless "$tmp/anim-frame-2.png" \
    -o "$root/anim-img2webp-lossless.webp"
"$img2webp" -mixed -loop 1 -q 75 -m 0 "$tmp/anim-frame-0.png" \
    -d 60 "$tmp/anim-frame-1.png" -d 140 "$tmp/anim-frame-2.png" \
    -o "$root/anim-img2webp-mixed.webp"
cwebp -quiet -q 75 "$tmp/anim-frame-0.png" -o "$tmp/anim-mixed-lossy.webp"
cwebp -quiet -lossless "$tmp/anim-frame-0.png" -o "$tmp/anim-mixed-lossless.webp"
"$webpmux" \
    -frame "$tmp/anim-mixed-lossy.webp" +80+0+0+0-b \
    -frame "$tmp/anim-mixed-lossless.webp" +120+0+0+0-b \
    -loop 1 -o "$root/anim-webpmux-mixed-format.webp"

"$ffmpeg" -hide_banner -loglevel error -y -framerate 5 \
    -i "$tmp/anim-gif-%02d.png" -vf "palettegen=reserve_transparent=1" \
    "$tmp/anim-gif-palette.png"
"$ffmpeg" -hide_banner -loglevel error -y -framerate 5 \
    -i "$tmp/anim-gif-%02d.png" -i "$tmp/anim-gif-palette.png" \
    -filter_complex "[0:v][1:v]paletteuse=alpha_threshold=128" \
    -loop 0 "$tmp/anim-transparent.gif"
"$gif2webp" -quiet -loop_compatibility -q 75 \
    "$tmp/anim-transparent.gif" -o "$root/anim-gif-transparent.webp"

for frame in 0 1 2 3 4; do
    cwebp -quiet -lossless "$tmp/anim-mux-$frame.png" \
        -o "$tmp/anim-mux-$frame.webp"
done
"$webpmux" \
    -frame "$tmp/anim-mux-0.webp" +80+0+0+0+b \
    -frame "$tmp/anim-mux-1.webp" +90+2+2+0-b \
    -frame "$tmp/anim-mux-2.webp" +100+4+2+1+b \
    -frame "$tmp/anim-mux-3.webp" +110+6+4+1-b \
    -frame "$tmp/anim-mux-4.webp" +120+8+4+0+b \
    -loop 3 -bgcolor 200,210,40,255 -o "$root/anim-webpmux-combinations.webp"

cwebp -quiet -q 75 "$tmp/anim-vp8l-alpha-hint-opaque.png" \
    -o "$tmp/anim-vp8l-alpha-hint-vp8.webp"
cwebp -quiet -lossless -exact "$tmp/anim-vp8l-alpha-hint.png" \
    -o "$tmp/anim-vp8l-alpha-hint-vp8l.webp"
"$webpmux" \
    -frame "$tmp/anim-vp8l-alpha-hint-vp8.webp" +80+0+0+0-b \
    -frame "$tmp/anim-vp8l-alpha-hint-vp8l.webp" +120+0+0+0+b \
    -loop 1 -o "$tmp/anim-vp8l-alpha-hint.webp"
python3 - "$tmp/anim-vp8l-alpha-hint.webp" "$root/anim-vp8l-alpha-hint.webp" <<'PY'
import struct
import sys
from pathlib import Path

data = bytearray(Path(sys.argv[1]).read_bytes())
offset = 12
found = False
while offset < len(data):
    kind = data[offset:offset + 4]
    length = struct.unpack_from("<I", data, offset + 4)[0]
    if kind == b"ANMF":
        subchunk = offset + 8 + 16
        end = offset + 8 + length
        while subchunk < end:
            subkind = data[subchunk:subchunk + 4]
            sublength = struct.unpack_from("<I", data, subchunk + 4)[0]
            payload = subchunk + 8
            if subkind == b"VP8L":
                data[payload + 4] &= 0xEF
                found = True
            subchunk += 8 + sublength + (sublength & 1)
    offset += 8 + length + (length & 1)

if not found:
    raise SystemExit("no VP8L animation frame found")
Path(sys.argv[2]).write_bytes(data)
PY

"$webpmux" -set icc "$root/metadata.icc" "$root/flat-gray-16x16.webp" \
    -o "$tmp/metadata-icc.webp"
"$webpmux" -set exif "$root/metadata.exif" "$tmp/metadata-icc.webp" \
    -o "$tmp/metadata-icc-exif.webp"
"$webpmux" -set xmp "$root/metadata.xmp" "$tmp/metadata-icc-exif.webp" \
    -o "$root/webp-metadata.webp"
"$webpmux" -set icc "$root/metadata.icc" "$root/anim-webpmux-combinations.webp" \
    -o "$tmp/anim-metadata-icc.webp"
"$webpmux" -set exif "$root/metadata.exif" "$tmp/anim-metadata-icc.webp" \
    -o "$tmp/anim-metadata-icc-exif.webp"
"$webpmux" -set xmp "$root/metadata.xmp" "$tmp/anim-metadata-icc-exif.webp" \
    -o "$root/anim-webpmux-metadata.webp"
