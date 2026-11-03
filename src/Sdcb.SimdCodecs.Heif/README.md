# Sdcb.SimdCodecs.Heif

Pure-managed HEIF / HEIC decoder for .NET 10. Reads ISOBMFF containers
(ISO/IEC 14496-12), the HEIF item model (ISO/IEC 23008-12 / MIAF 23000-22),
and decodes HEVC intra frames (ITU-T H.265 Main and Main 10 profiles) — the
encoding used by every modern Android and iPhone camera.

> **Patent notice.** HEVC (H.265) is patent-encumbered. This package is MIT
> and conveys **no patent licence**. Users are responsible for evaluating HEVC
> patent obligations in their jurisdiction before deploying this package.

This package is **not** included in the `Sdcb.SimdCodecs` meta-package, and the facade does not reference it:
an MIT package cannot convey HEVC's patent licensing, so using HEVC is a decision each application makes for
itself. Install it explicitly and register it with the facade once, at start-up:

```csharp
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Heif;

SimdCodecs.Register(HeifImageDecoder.Registration);

// From here on every facade entry point handles HEIF / HEIC like any built-in format.
byte[] data = File.ReadAllBytes("photo.heic");
if (SimdCodecs.TryReadInfo(data, out var info))
{
    var bgra = new byte[info.Width * info.Height * 4];
    SimdCodecs.TryDecodeInto(data, bgra, 0, ImagePixelFormat.Bgra32);
}
```

Registering is idempotent and thread-safe, and cannot be undone. A registered codec is sniffed after the
built-in ones. Before registration, `SimdCodecs.DetectFormat` still reports `ImageFormat.Heif` (so an
application can tell the user what it cannot open) while `IsDecodable(ImageFormat.Heif)`, `CanDecode` and the
`Try*` methods return false.

`HeifImageDecoder` is the `IImageDecoder` adapter behind the registration; it can also be called directly
without the facade. So can the codec's own API:

```csharp
using HeifImage image = HeifDecoder.Decode(data);  // RGBA8, display-oriented
HeifInfo header = HeifDecoder.ReadInfo(data);       // size, coded bit depth, tiles, ICC — no HEVC decoding
```

## What the facade returns

- `TryReadInfo`: the **displayed** size (`clap`, `irot` and `imir` are already applied, so `Orientation` is
  always `Normal`), 3 channels, `UInt8`, `BitDepth` 8, one frame, `Format = Heif`.
- `TryDecode`: an 8-bit RGB `RasterImage` with the container's ICC profile passed through. `ColorEncoding`
  comes from the `colr` nclx box (primaries, transfer); with only an ICC profile it is the assumed-sRGB
  default (it is never derived from a profile); with no `colr` box it comes from the HEVC VUI. It is always
  full range: limited-range Y′CbCr is expanded by the conversion.
- `TryDecodeInto`: every `ImagePixelFormat`, any stride. The pixels equal `ToRgba8()` of the decoded image
  converted by `PixelConverter.ConvertRow`, except `Gray8`, which is the stored luma (Y) range-mapped to
  8 bits — the formula is frozen in `HeifColor` — rather than the luma of the RGB; an identity-matrix (GBR)
  image takes the luma of the RGB. A PQ or HLG transfer function returns false (its code values are encoded
  for an HDR display), as PNG does for cICP PQ / HLG; `TryDecode` still decodes it and reports the transfer.
- `TryOpenFrames`: a one-frame reader over the still image.

## Limitations in 1.4

- **10-bit sources are output as 8-bit samples.** Main 10 decodes exactly, but the 1.x interfaces get 8-bit
  RGB; a 16-bit fidelity output is planned for 2.0.
- **Not decoded:** alpha auxiliary images (the colour image decodes, opaque), HDR gain maps, depth maps and
  other auxiliary items; Exif is not read.
- **Not supported:** image sequences (`.heics`, `msf1`) and the HEVC range-extension profiles (Main 10 Intra,
  Main 12, 4:4:4, …) — `InvalidDataException`, or false from the `Try*` methods.
- **Speed:** tiled phone photos decode faster than libheif (0.55–0.71× its time on 8 cores), but a single-item
  image, which has no tiles to decode in parallel, takes 1.4–1.8× libheif's time. Not tuned yet.
- **Memory:** `TryDecodeInto` writes tightly packed `Rgba32` straight into the caller's buffer; every other
  layout and stride decodes a full RGBA frame first, and `TryDecode` converts a full RGBA frame to RGB.

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
- Output is 8-bit; 10-bit input is converted with the correct matrix and range

## Output contract

HEVC decoding is integer-arithmetic and deterministic: the Y / Cb / Cr planes of
every coded image equal a pinned reference decoder's samples bit-for-bit
(ffmpeg 8.1.2; see `tests/Sdcb.SimdCodecs.Tests/Oracle/heif/README.md`). The
YCbCr → RGB conversion (`HeifColor`) is defined in-repo from the `colr` / `nclx`
parameters and frozen like `JpegColor`, and so is the grey projection (`HeifColor.ToGray`); no SIMD path
exists yet, and when one is added every tier must produce identical output.
