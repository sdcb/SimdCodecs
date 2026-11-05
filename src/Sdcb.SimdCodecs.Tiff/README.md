# Sdcb.SimdCodecs.Tiff

Pure-managed TIFF reading and writing for .NET 10, from the TIFF 6.0 specification, the BigTIFF specification and TIFF Technical Note 2 (JPEG). Multi-page, both byte orders, classic TIFF and BigTIFF; pages are read whole, streamed strip by strip, or one strip or tile at a time.

```csharp
using Sdcb.SimdCodecs.Tiff;

byte[] data = File.ReadAllBytes("scan.tif");
TiffDocument document = TiffReader.Read(data);          // every page, samples as stored
TiffPage page = TiffReader.ReadPage(data, pageIndex: 0); // one page, the others never interpreted

TiffPageLayout layout = TiffReader.ReadLayout("huge.tif");      // strips / tiles, no pixel decoded
var tile = new byte[layout.SegmentBytes(0)];
TiffReader.ReadSegment("huge.tif", layout, 0, tile);              // reads only that tile's bytes
```

Through the `Sdcb.SimdCodecs` facade (`TiffImageDecoder`) a page comes out as grey or RGB, with straight alpha, at 8 or 16 bits or as float.

## What it reads

| | |
|---|---|
| Container | Classic TIFF (`II*\0`, `MM\0*`) and BigTIFF (`II+\0`, `MM\0+`: 8-byte offsets and counts, 20-byte entries, LONG8 / IFD8); every IFD of the chain; a looping chain ends at the first repeated IFD |
| Layout | Strips and tiles; PlanarConfiguration 1 and 2 |
| Samples | 8, 16, 32-bit unsigned and signed integers, 16 and 32-bit IEEE float; 1, 2, 4-bit for one sample per pixel |
| Compression | None, LZW, Deflate / Adobe Deflate, PackBits, CCITT Modified Huffman, Group 3 (1D and 2D) and Group 4 fax, JPEG (Compression 7, with or without JPEGTables) |
| Predictors | Horizontal differencing (2) and floating point (3) on uncompressed, LZW, Deflate and PackBits data; ignored on fax and JPEG pages, where they mean nothing |
| Metadata | ICC profile, XMP, Exif / GPS / Interoperability IFDs (reassembled as an Exif block, from a BigTIFF too), resolution, orientation |

Old-style JPEG (Compression 6), CIELab, subsampled YCbCr stored in separate planes or with a predictor, and JPEG YCbCr in separate planes are refused. Untrusted input fails with `InvalidDataException` or `NotSupportedException` (and `false` from the facade); every size derived from the file is checked before a buffer is allocated (`TiffRobustnessTests`, `TIFF_FUZZ_ITERATIONS`).

## How the facade presents a page

`TiffReader` delivers samples as the file stores them (host byte order, predictors undone, planes interleaved, 1/2/4-bit grey scaled to 8 bits, palette indices unscaled, YCbCr data units expanded to one Y, Cb, Cr per pixel). `TiffImageDecoder` then converts by arithmetic defined in this package (`TiffColor`), frozen like the JPEG decoder's colour conversion:

- **MinIsWhite** grey is inverted.
- **Palette** (ColorMap): 1/2/4/8-bit indices give 8-bit RGB, each 16-bit map entry reduced by `v >> 8`; 16-bit indices give the 16-bit entries.
- **CMYK** (InkSet 1, 0 = no ink): `R = round((M − C)(M − K) / M)`, likewise G from magenta and B from yellow, `M` = 255 or 65535. At 8 bits `t = x·y + 128; (t + (t >> 8)) >> 8`, at 16 bits `(x·y + 32767) / 65535`; both are exactly `round(x·y / M)`.
- **YCbCr** (not JPEG): codes are expanded by ReferenceBlackWhite (`Y' = (Y − B0)·255 / (W0 − B0)`, `Cb' = (Cb − B1)·127 / (W1 − B1)`, `Cr'` alike; an absent tag means 0, 255, 128, 255, 128, 255), then `R = Cr'·(2 − 2·LumaRed) + Y'`, `B = Cb'·(2 − 2·LumaBlue) + Y'`, `G = (Y' − LumaBlue·B − LumaRed·R) / LumaGreen` in double precision, each `floor(x + 0.5)` clamped to 0..255. YCbCrCoefficients default to 299/587/114. Chroma is repeated over its data unit; YCbCrPositioning is not used.
- **JPEG pages**: Photometric RGB is decoded with no colour transform, YCbCr is converted by the JPEG decoder (its own chroma upsampling) and the page then reports RGB, grey as grey, CMYK inks raw and converted as above.
- **ExtraSamples**: the first extra sample marked associated (1) or unassociated (2) alpha is the alpha channel; associated alpha is un-premultiplied, `c = min(M, (c·M + a/2) / a)` (0 when `a` = 0), `c / a` for floats. Extras marked 0 (unspecified) and any further extras are dropped. A page with extra samples and no ExtraSamples tag keeps the first as alpha.

A `DecodeOptions.Region` reads only the strips or tiles it intersects, and `TryDecodeInto` writes integer pages band by band into the caller's buffer without a page raster; both give exactly the pixels of decoding the page whole and then cropping and converting it (`TiffRegionDecodeTests`).

## Writing

`TiffWriter` writes classic TIFF (never BigTIFF): uncompressed or Deflate, strips or tiles, 8/16/32-bit integer and float samples, with ICC, XMP and Exif.
