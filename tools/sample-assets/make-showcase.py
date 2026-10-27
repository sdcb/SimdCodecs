# Builds showcase.jpg, the image at the top of README.md, from the RGBA dumps decode-all.cs writes:
#   python make-showcase.py decoded showcase.jpg
# Needs Pillow and a sans-serif TrueType font (Segoe UI, DejaVu Sans or Arial).
import os, re, sys
from PIL import Image, ImageDraw, ImageFont

S = 2                        # rendered at 2x so it stays sharp on high-DPI screens
TW, TH, LABEL, GAP = 390 * S, 260 * S, 52 * S, 14 * S
COLS = 4

# (file, crop, format, detail). crop: "cover" fills the tile, "contain" fits it over a checkerboard,
# or (x, y, w, h) in source pixels.
TILES = [
    ("photo.jpg", "cover", "JPEG", "camera original, EXIF"),
    ("fuji-16bit.png", (420, 120, 600, 400), "PNG", "16-bit RGB"),
    ("cutout-lossless.webp", "contain", "WebP", "lossless + alpha"),
    ("mandelbrot-zoom.webp", "cover", "WebP", "animation, 121 frames"),
    ("fuji-gray-float32.tif", "cover", "TIFF", "float32, predictor 3"),
    ("fuji-16bit-tiled.tif", (0, 270, 600, 400), "TIFF", "16-bit, 256x256 tiles"),
    ("fuji.jp2", (620, 120, 540, 360), "JPEG 2000", "lossy, 40:1"),
    ("text-page.jb2", (200, 230, 1170, 780), "JBIG2", "symbol coding, 300 dpi"),
]


def font(size, bold=False):
    names = ["segoeuib.ttf", "DejaVuSans-Bold.ttf", "arialbd.ttf"] if bold else ["segoeui.ttf", "DejaVuSans.ttf", "arial.ttf"]
    for name in names:
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    sys.exit("no TrueType font found")


def load(dumps, name):
    for f in os.listdir(dumps):
        m = re.fullmatch(re.escape(name) + r"\.(\d+)x(\d+)\.rgba", f)
        if m:
            w, h = int(m[1]), int(m[2])
            return Image.frombytes("RGBA", (w, h), open(os.path.join(dumps, f), "rb").read())
    sys.exit(f"{name}: no dump in {dumps}")


def tile(im, crop):
    if crop == "contain":
        board = Image.new("RGB", (TW, TH), "white")
        d = ImageDraw.Draw(board)
        c = 12 * S
        for y in range(0, TH, c):
            for x in range(0, TW, c):
                if (x // c + y // c) % 2:
                    d.rectangle((x, y, x + c - 1, y + c - 1), fill=(222, 222, 222))
        im = im.copy()
        im.thumbnail((TW - 16 * S, TH - 16 * S), Image.LANCZOS)
        board.paste(im, ((TW - im.width) // 2, (TH - im.height) // 2), im)
        return board
    if crop != "cover":
        x, y, w, h = crop
        im = im.crop((x, y, x + w, y + h))
    scale = max(TW / im.width, TH / im.height)
    im = im.resize((round(im.width * scale), round(im.height * scale)), Image.LANCZOS)
    x, y = (im.width - TW) // 2, (im.height - TH) // 2
    return im.crop((x, y, x + TW, y + TH)).convert("RGB")


def main(dumps, out):
    rows = (len(TILES) + COLS - 1) // COLS
    sheet = Image.new("RGB", (COLS * TW + (COLS + 1) * GAP, rows * (TH + LABEL) + (rows + 1) * GAP), "white")
    d = ImageDraw.Draw(sheet)
    bold, regular = font(17 * S, bold=True), font(15 * S)
    for i, (name, crop, fmt, detail) in enumerate(TILES):
        x = GAP + (i % COLS) * (TW + GAP)
        y = GAP + (i // COLS) * (TH + LABEL + GAP)
        sheet.paste(tile(load(dumps, name), crop), (x, y))
        d.rectangle((x, y, x + TW - 1, y + TH - 1), outline=(208, 215, 222), width=S)
        ty = y + TH + 12 * S
        d.text((x + 2 * S, ty), fmt, fill=(31, 35, 40), font=bold)
        d.text((x + 2 * S + d.textlength(fmt + "  ", font=bold), ty + 2 * S), detail, fill=(101, 109, 118), font=regular)
    sheet.save(out, quality=84, optimize=True, progressive=True)
    print(out, sheet.size, os.path.getsize(out), "bytes")


main(*sys.argv[1:3])
