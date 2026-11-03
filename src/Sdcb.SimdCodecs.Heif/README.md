# Sdcb.SimdCodecs.Heif

Pure-managed HEIF / HEIC decoder for .NET 10. Reads ISOBMFF containers
(ISO/IEC 14496-12), the HEIF item model (ISO/IEC 23008-12 / MIAF 23000-22),
and decodes HEVC intra frames (ITU-T H.265 Main and Main 10 profiles) — the
encoding used by every modern Android and iPhone camera.

> **Patent notice.** HEVC (H.265) is patent-encumbered. This package is MIT
> and conveys **no patent licence**. Users are responsible for evaluating HEVC
> patent obligations in their jurisdiction before deploying this package.

This package is **not** included in `Sdcb.SimdCodecs` — it is not registered with the facade,
because HEVC's patent licensing cannot be conveyed by an MIT package; call it directly:

```csharp
using Sdcb.SimdCodecs.Heif;

byte[] data = File.ReadAllBytes("photo.heic");
using HeifImage image = HeifDecoder.Decode(data);
Console.WriteLine($"{image.Width} × {image.Height}, {image.BitDepth}-bit");

// Or read the header without decoding:
HeifInfo info = HeifDecoder.ReadInfo(data);
```

## What is supported

- HEIC still images (`ftyp` major brand `heic` / `heix`, compatible brand `mif1`)
- Tiled images (`grid` item type) — the layout used by all phone cameras
- HEVC Main (8-bit), Main 10 (10-bit) and Main Still Picture profiles, 4:2:0
- Arbitrary HEVC intra syntax: WPP, sign data hiding, strong intra smoothing, AMP,
  transform skip, deblocking and SAO
- `hvcC` (decoder configuration), `ispe`, `pixi`
- `colr` box: `nclx` colour signalling (matrix from H.273) and ICC profile passthrough
  (`prof` / `rICC`, returned on `HeifImage.IccProfile`, not applied)
- `clap` clean aperture, `irot` rotation and `imir` mirroring, applied in association order
  so the pixels come out as the file intends them to be displayed
- Output is RGBA8; 10-bit input is converted with the correct matrix and range

## What is not yet supported

- HEIC image sequences and Live Photos (`heics`) — rejected with `InvalidDataException`
- HEVC range-extension profiles (Main 10 Intra, Main 12, Main 4:4:4, …) — rejected
- Alpha auxiliary images (`auxC` / `urn:mpeg:hevc:2015:auxid:1`); the colour primary
  decodes, its alpha is reported as 255
- Apple HDR gain map auxiliary images
- Depth maps and other auxiliary item types
- AVIF (AV1-coded items) — use `Sdcb.SimdCodecs.Avif` when available

## Output contract

HEVC decoding is integer-arithmetic and deterministic: the Y / Cb / Cr planes of
every coded image equal a pinned reference decoder's samples bit-for-bit
(ffmpeg 8.1.2; see `tests/Sdcb.SimdCodecs.Tests/Oracle/heif/README.md`). The
YCbCr → RGB conversion (`HeifColor`) is defined in-repo from the `colr` / `nclx`
parameters and frozen like `JpegColor`; no SIMD path exists yet, and when one is
added every tier must produce identical output.
