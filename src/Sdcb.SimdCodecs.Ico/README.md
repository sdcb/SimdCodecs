# Sdcb.SimdCodecs.Ico

Pure-managed Windows icon (`.ico`) and cursor (`.cur`) decoding and encoding for .NET 10, from Microsoft's
documentation of the icon file. An icon is a directory of images, each a PNG stream (read and written by
`Sdcb.SimdCodecs.Png`) or a packed DIB with an AND mask (by `Sdcb.SimdCodecs.Bmp`, so every DIB depth and its
SIMD row kernels come along). Every image decodes to 8-bit RGBA.

```csharp
using Sdcb.SimdCodecs.Ico;

byte[] data = File.ReadAllBytes("app.ico");
IcoInfo info = IcoDecoder.ReadInfo(data);       // kind, every image's size / depth / PNG-or-DIB / hotspot
IcoImage best = IcoDecoder.Decode(data);        // the largest, deepest image (info.Best)
IcoImage small = IcoDecoder.Decode(data, 0);    // any image, by directory index
```

Through the `Sdcb.SimdCodecs` facade an icon is `ImageFormat.Ico` and a cursor `ImageFormat.Cur`; `TryDecode`
gives the largest, deepest image, and `TryOpenFrames` every image as a page of its own size.

## Encoding

```csharp
byte[] ico = IcoEncoder.Encode(
[
    new IcoFrame(rgba16, 16, 16),
    new IcoFrame(rgba32, 32, 32),
    new IcoFrame(rgba256, 256, 256),                                  // PNG
    new IcoFrame(rgba48, 48, 48, Encoding: IcoImageEncoding.Bmp8),     // 256-colour DIB
]);

byte[] cur = IcoEncoder.EncodeCursor([new IcoFrame(rgba32, 32, 32, HotspotX: 4, HotspotY: 2)]);
```

Each image is 1 to 256 pixels a side, in any `ImagePixelFormat`, stored as `Png`, `Bmp32` (alpha, plus an AND mask
from it), or `Bmp24` / `Bmp8` / `Bmp4` / `Bmp1` (transparency in the AND mask only; a palette is exact when the
colours fit, otherwise Wu-quantized). `Auto` is PNG at 256 pixels and 32-bit BMP below: compact where icons get large, and readable everywhere else.
Alpha below 128 is transparent in an AND mask, and a transparent pixel of a DIB without alpha is written black, as
the mask needs to draw it.

## Decisions

| Case | Decoded as |
|---|---|
| Which image `Decode` / the facade's `TryDecode` returns | The largest by area, then the deepest, then the first |
| An image's size | The image's own header: a directory entry's byte (0 for 256) can disagree, and is not used |
| An image size that runs past the end of the file | Cut to the file |
| An image that starts inside the directory or past the end | The file is refused |
| A DIB image | Its colour bitmap and AND mask (see `Sdcb.SimdCodecs.Bmp`): 32-bit pixels' alpha unless all 0, otherwise the mask; without a mask, opaque. Compressed DIBs are refused |
| A PNG image | Whatever PNG holds, converted to 8-bit RGBA; it may be larger than 256 |
| The colour of a transparent pixel | As stored (WIC agrees; ImageMagick may differ, and only alpha is compared there) |

Images above 2^28 pixels are refused. Every image of a corpus of hand-built DIB icons, PNG images and every encoder
layout decodes to WIC's pixels, frame for frame, and ImageMagick reads the encoder's output back exactly.
