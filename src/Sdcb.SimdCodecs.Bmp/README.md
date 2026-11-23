# Sdcb.SimdCodecs.Bmp

Pure-managed BMP / DIB decoding and encoding for .NET 10, derived from Microsoft's documentation of the Windows DIB and IBM's of the OS/2 bitmap. Rows are converted with AVX2 / SSSE3 / AdvSimd kernels, each with a scalar twin that gives the same bytes, and decoded straight into the caller's buffer in any pixel layout without allocating.

```csharp
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;

byte[] data = File.ReadAllBytes("image.bmp");
BmpInfo info = BmpDecoder.ReadInfo(data);       // size, depth, compression, header, alpha
BmpImage image = BmpDecoder.Decode(data);       // RGB, or RGBA when the bitmap can carry alpha

var bgra = new byte[info.Width * info.Height * 4];
BmpDecoder.DecodeTo(data, bgra, PixelFormat.Bgra32);
```

What it reads:

| | |
|---|---|
| Headers | `BITMAPCOREHEADER` (OS/2 1.x), OS/2 2.x `BITMAPINFOHEADER2` (16 to 64 bytes), `BITMAPINFOHEADER`, the 52- and 56-byte V2 / V3, `BITMAPV4HEADER`, `BITMAPV5HEADER`; the first bitmap of an OS/2 bitmap array (`BA`) |
| Depths | 1, 2, 4 and 8-bit palettes (3- or 4-byte entries); 16, 24 and 32-bit |
| Compression | `BI_RGB`; `BI_BITFIELDS` and `BI_ALPHABITFIELDS` with any masks; `BI_RLE4` / `BI_RLE8` with deltas and early ends; OS/2 RLE24 and Huffman 1D; `BI_PNG` |
| Layout | Bottom-up and top-down rows |
| Metadata | A V5 header's embedded ICC profile (`BmpImage.IccProfile`) |
| Packed DIBs | `BmpDecoder.ReadDibInfo` / `DecodeDib` / `DecodeDibTo`: the same without the file header, as the clipboard (`CF_DIB`, `CF_DIBV5`) holds it; `iconImage: true` reads an icon or cursor image, its AND mask as alpha |

`BI_JPEG` and 64-bit bitmaps are refused, as is anything malformed (`InvalidDataException`, or `false` from the `Try` methods and the `BmpImageDecoder` adapter). Images above 2^28 pixels are refused, to bound what a decode allocates.

## Encoding

```csharp
byte[] bmp = BmpEncoder.Encode(rgba, width, height, PixelFormat.Rgba32);

byte[] rle = BmpEncoder.Encode(rgba, width, height, PixelFormat.Rgba32,
    new BmpEncodeOptions { Format = BmpEncodeFormat.Indexed8, Rle = true, Dither = true });
```

| Option | |
|---|---|
| `Format` | `Indexed1` / `Indexed4` / `Indexed8` (exact when the colours fit, otherwise quantized by the shared perceptual quantizer, optionally dithered), `Rgb555`, `Rgb565`, `Rgb24`, `Rgb32`, `Argb32`. `Auto` (the default): 32-bit with alpha when the input's alpha is not all 255, an 8-bit grey palette for grey input, 24-bit otherwise |
| `Rle` | `BI_RLE4` / `BI_RLE8` for a 4- or 8-bit palette |
| `Header` | `Core`, `Info`, `V4` or `V5`. `Auto`: V5 with an ICC profile, V4 for alpha (`BI_BITFIELDS` with an alpha mask), `Info` otherwise |
| `TopDown` | A negative height, top row first |
| `IccProfile` | Embedded in a V5 header |

Input is any `PixelFormat` (B, G, R input written as 24- or 32-bit is copied as it is), or 1 / 3 / 4 channels. `BmpEncoder.EncodeDib` writes a packed DIB; with `iconImage: true`, an icon or cursor image: a 40-byte header with the doubled height, then the AND mask (alpha below 128 is transparent).

## Streaming

`BmpImageDecoder.CreateRowReader` reads rows top to bottom from a `StreamInput`, synchronously or asynchronously, holding a band of rows rather than the file. Its rows equal `TryDecodeInto`'s byte for byte, and its `Info` equals `TryReadInfo`'s.

| Bitmap | Stream that cannot seek | Seekable stream |
|---|---|---|
| Uncompressed (`BI_RGB`, `BI_BITFIELDS`, `BI_ALPHABITFIELDS`, every depth and palette), top-down | Streams | Streams |
| The same, bottom-up | Falls back | Streams: one forward pass to the end of the pixels, then a seek back to each band of about 256 KiB of stored rows, handed out last stored row first. The pixels are read twice |
| 32-bit `BI_RGB` read to a layout with alpha | Falls back: its fourth byte is alpha only if some pixel's is not 0, which the last pixel can settle | Streams after a forward pass that reads every fourth byte (the bottom-up pass when there is one) |
| RLE, Huffman 1D, `BI_PNG`; a layout other than 8-bit RGBA / BGRA / RGB / BGR / grey; decode options other than the whole image; a pixel offset into the headers or more than 1 MiB in; a V5 profile stored after the pixels | Falls back | Falls back |

A `StreamInput` made with `retainConsumed: true` counts as seekable here: it goes back within the bytes it keeps (the file, then, but not a decoded frame). A reader that falls back reads the whole stream and decodes it with `TryDecodeInto` behind the same API (`IsStreaming` false). The profile case falls back because the image's `ColorEncoding` comes from the profile, and the reader's `Info` is settled before any row. A truncated stream throws `InvalidDataException` when the rows reach the missing bytes (at `Initialize` for a seekable stream, whose length says so).

`BmpImageEncoder.Create` returns a streaming session (`IsStreaming` true) when `TopDown` is set, `Format` names a direct-colour layout (`Rgb555`, `Rgb565`, `Rgb24`, `Rgb32`, `Argb32`) and `Rle` is off: the file's size and offsets are then known before any pixel, so the headers go out with the frame and each band of rows as it is written, the profile at `Commit`. The bytes equal the buffered session's for the same options. Bottom-up files, `Auto` (which scans the image for alpha) and palettes are collected and encoded at `Commit`.

## Decisions

Where the documentation leaves a choice to the decoder, it is made as WIC (Windows' own decoder) makes it, with the exceptions marked; the test suite holds every one of them against WIC, ImageMagick or a hand-built stream.

| Case | Decoded as |
|---|---|
| A channel narrower than 8 bits | Its bits repeated to fill the byte (5-bit `v` is `v << 3 \| v >> 2`; a 3-bit 7 is 255), as WIC, ImageMagick and ImageSharp do for 5- and 6-bit channels. WIC writes channels of 1 to 3 bits only twice (a 3-bit 7 is 0xFC); this decoder keeps white white |
| A channel wider than 8 bits (10-10-10-2, …) | Its top 8 bits, as WIC and ImageMagick |
| An index past the palette | Opaque black, as WIC |
| A palette longer than the indices reach, or one the pixel offset cuts short | The entries the indices reach that lie before the pixels |
| Pixels an RLE stream skips (a delta, an early end of line or bitmap, data that runs out) | Opaque black, as WIC and ImageSharp (browsers make them transparent) |
| An RLE run or literal past the right edge | Clipped |
| The fourth byte of a 32-bit `BI_RGB` pixel | Alpha, unless it is 0 in every pixel, then opaque: as browsers, ImageMagick and ImageSharp. WIC always ignores it |
| An alpha mask (`BI_BITFIELDS` in a V3 to V5 header, `BI_ALPHABITFIELDS`) | Alpha, in any layout and depth, as ImageMagick and ImageSharp. WIC reads it only as the top byte of a B, G, R, A 32-bit pixel |
| A file offset to the pixels that points into the headers, or past the end | The pixels follow the palette |
| The last row without its padding | Accepted |
| A V5 profile that does not fit the data | Ignored |
| Compression 3 on a 1-bit bitmap, 4 on a 24-bit one | OS/2 Huffman 1D and RLE24 (Windows' `BI_BITFIELDS` and `BI_JPEG` take other depths) |
| OS/2 Huffman 1D | ITU-T T.4 Modified Huffman runs, each row starting white; white is index 0. Rows are not padded; EOL codes before a row are skipped. No tool at hand reads this compression, so these choices are checked only against streams built from the T.4 tables |
| OS/2 RLE24 | RLE8's codes, each run a B, G, R triple and each literal three bytes a pixel, padded to 16 bits. Unchecked against another decoder for the same reason |
| An icon image | 32-bit: alpha from the pixels, unless all 0, then from the AND mask; other depths: the AND mask. Without an AND mask, opaque |
