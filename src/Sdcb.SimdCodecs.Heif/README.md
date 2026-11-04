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

## Performance

AVX2 / Vector128 (SSE, AdvSimd) / scalar kernels for the inverse transform, intra prediction, SAO and the colour
conversion, a CABAC engine with a 64-bit look-ahead window, grid tiles decoded in parallel and, for an image
without tiles, a WPP wavefront (CTB rows in parallel) with parallel deblocking, SAO and colour passes. Measured on
a Ryzen 7 5800X against libde265 1.1.1 and libheif 1.23.1 in the same process
([M3 record](https://github.com/sdcb/SimdCodecs/blob/main/benchmarks/Sdcb.SimdCodecs.Heif.Benchmarks/results/2026-11-04-amd-ryzen-7-5800x-m3.md)):

- one core, HEVC decoding alone: 0.68–0.87× libde265's time;
- 8 cores, to RGBA: 0.20–0.32× libheif's time (tiled phone photos and single-item images alike), 0.09–0.14×
  Magick.NET's;
- memory: each tile is converted straight into the output as soon as it is decoded, in every pixel layout and
  stride, so the only image-sized buffer is the output. Peak working-set rise for a 12.6 MP phone photo (48 MB
  of RGBA): 75 MB, libheif 80 MB; for 50 MP (192 MB): 230 MB, libheif 373 MB.

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
parameters and frozen like `JpegColor`, and so is the grey projection (`HeifColor.ToGrayRow`). Every vector
kernel has a scalar twin and every tier (Vector256, Vector128, scalar; x64 and arm64) produces identical output;
CI runs the HEIF tests on each.
