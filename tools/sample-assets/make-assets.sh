#!/usr/bin/env bash
# Generates the LINQPad sample assets inside the tool image built from ./Dockerfile:
#   docker build -t simdcodecs-assets .
#   docker run --rm -v "$PWD:/w" -w /w simdcodecs-assets bash make-assets.sh
# Every encoder here is run as a black-box binary; inputs are CC0 (Wikimedia) or the
# repository owner's own work.
set -euo pipefail

IN=in
OUT=out
rm -rf "$OUT"; mkdir -p "$OUT"

# --- photo.jpg: Sony A7R III camera JPEG, downscaled, EXIF kept (no embedded thumbnail).
python3 - <<'PY'
from PIL import Image
im = Image.open("in/cam-sony-a7r3.jpg")
exif = im.getexif()
exif.pop(0x0201, None); exif.pop(0x0202, None)  # drop IFD1 thumbnail pointers if present
im.thumbnail((1600, 1600), Image.LANCZOS)
im.save("out/photo.jpg", quality=90, subsampling="4:2:0", exif=exif.tobytes(), optimize=True)
PY

# --- fuji master (1600 px wide, 16-bit working copy) shared by the scenery assets.
magick "$IN/sunrise-fuji.jpg" -strip -colorspace sRGB -resize 1600x -depth 16 /tmp/fuji16.png
magick /tmp/fuji16.png -depth 8 /tmp/fuji8.png

magick /tmp/fuji8.png  -define png:compression-level=9 "PNG24:$OUT/fuji.png"
magick /tmp/fuji16.png -resize 1200x -define png:compression-level=9 "PNG48:$OUT/fuji-16bit.png"
magick /tmp/fuji8.png  -resize 512x -colors 64 -define png:compression-level=9 "PNG8:$OUT/fuji-indexed.png"

cwebp -quiet -q 80 -m 6 /tmp/fuji8.png -o "$OUT/fuji.webp"
opj_compress -i /tmp/fuji8.png -o "$OUT/fuji.jp2" -r 40 -n 6 >/dev/null

# Lossless WebP with alpha: a rounded-rectangle cut-out with a soft edge.
python3 - <<'PY'
from PIL import Image, ImageDraw, ImageFilter
im = Image.open("/tmp/fuji8.png").convert("RGB")
im = im.resize((960, im.height * 960 // im.width), Image.LANCZOS)
mask = Image.new("L", im.size, 0)
ImageDraw.Draw(mask).rounded_rectangle((24, 24, im.width - 24, im.height - 24), radius=96, fill=255)
mask = mask.filter(ImageFilter.GaussianBlur(10))
im.putalpha(mask)
im.save("/tmp/cutout.png")
PY
cwebp -quiet -lossless -z 9 -exact /tmp/cutout.png -o "$OUT/cutout-lossless.webp"

# --- Animated GIF: a small first-party animation with a moving transparent window.
python3 - <<'PY'
from math import sin
from PIL import Image, ImageDraw

width, height = 320, 180
frames = []
for i in range(8):
    image = Image.new("RGBA", (width, height), (18, 24, 48, 255))
    draw = ImageDraw.Draw(image)
    for y in range(height):
        c = int(40 + 90 * y / (height - 1))
        draw.line((0, y, width, y), fill=(c // 2, c // 3, c, 255))
    x = 32 + i * 34
    y = 90 + int(35 * sin(i * 0.8))
    draw.ellipse((x - 28, y - 28, x + 28, y + 28), fill=(255, 185, 32, 255), outline=(255, 245, 180, 255), width=3)
    draw.ellipse((x - 10, y - 12, x - 2, y - 4), fill=(20, 25, 45, 255))
    draw.ellipse((x + 2, y - 12, x + 10, y - 4), fill=(20, 25, 45, 255))
    draw.arc((x - 14, y - 2, x + 14, y + 20), 15, 165, fill=(20, 25, 45, 255), width=3)
    frames.append(image)
frames[0].save("out/animated.gif", save_all=True, append_images=frames[1:], duration=[120] * len(frames),
               loop=0, disposal=2, transparency=0, optimize=False)
PY

# --- TIFF variants.
magick /tmp/fuji8.png  -compress Zip "$OUT/fuji-deflate.tif"
magick /tmp/fuji16.png -resize 1200x -compress Zip -define tiff:predictor=2 -define tiff:tile-geometry=256x256 "$OUT/fuji-16bit-tiled.tif"
magick /tmp/fuji16.png -resize 1200x -colorspace Gray -define quantum:format=floating-point -depth 32 -define tiff:predictor=3 \
    -compress Zip "$OUT/fuji-gray-float32.tif"

# --- Text page for JBIG2 (original prose, rendered with DejaVu Serif at 300 dpi, A4).
python3 - <<'PY'
from PIL import Image, ImageDraw, ImageFont
import textwrap
W, H = 2480, 3508
page = Image.new("1", (W, H), 1)
d = ImageDraw.Draw(page)
title = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSerif-Bold.ttf", 72)
body = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf", 44)
d.text((220, 260), "Sdcb.SimdCodecs - JBIG2 sample page", font=title, fill=0)
paras = [
    "This page was rendered in black and white at 300 dots per inch and then compressed "
    "with a JBIG2 encoder in symbol mode. Symbol mode collects the distinct glyph shapes "
    "on the page into a dictionary and stores each letter as a reference to one of them, "
    "which is why scanned text pages shrink so well.",
    "PDF files use the same format through the JBIG2Decode filter. A decoder only has to "
    "rebuild the dictionary once and then place every symbol at its position; the bitmap "
    "it produces is identical to the one the encoder saw, apart from glyphs that the "
    "encoder judged close enough to merge.",
    "The quick brown fox jumps over the lazy dog. 0123456789. Pack my box with five dozen "
    "liquor jugs. Sphinx of black quartz, judge my vow.",
]
y = 480
for p in paras * 3:
    for line in textwrap.wrap(p, 78):
        d.text((220, y), line, font=body, fill=0); y += 66
    y += 50
page.save("/tmp/page.png")
PY
jbig2 -s /tmp/page.png > "$OUT/text-page.jb2" 2>/dev/null

# --- Multi-page TIFF: colour, greyscale, and a 1-bit CCITT Group 4 text page.
magick /tmp/fuji8.png -resize 1200x -compress Zip /tmp/p1.tif
magick out/photo.jpg -strip -resize 1200x -colorspace Gray -compress LZW /tmp/p2.tif
magick /tmp/page.png -type Bilevel -compress Group4 /tmp/p3.tif
# tiffcp keeps each page's own compression; magick would rewrite them all with one setting.
tiffcp /tmp/p1.tif /tmp/p2.tif /tmp/p3.tif "$OUT/multipage.tif"

# --- Animated WebP: the repository owner's own Mandelbrot deep-zoom recording, as published.
cp "$IN/mandelbrot.webp" "$OUT/mandelbrot-zoom.webp"

cd "$OUT"
sha256sum * > SHA256SUMS
ls -l
