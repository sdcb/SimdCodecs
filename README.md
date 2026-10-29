<img src="https://raw.githubusercontent.com/sdcb/SimdCodecs/main/assets/icon.png" width="64" align="right" alt="Sdcb.SimdCodecs icon">

# Sdcb.SimdCodecs [![CI](https://github.com/sdcb/SimdCodecs/actions/workflows/dotnet.yml/badge.svg)](https://github.com/sdcb/SimdCodecs/actions/workflows/dotnet.yml) [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**English** | [简体中文](README.zh-CN.md)

Pure-managed image codecs for .NET 10: JPEG, PNG, TIFF, WebP, GIF, JPEG 2000 and JBIG2, plus EXIF and ICC.
No native dependencies, NativeAOT-compatible, and SIMD-accelerated (AVX2, SSE, AdvSimd) with a scalar
fallback. The JPEG decoder is faster than libjpeg-turbo on the benchmark corpus.

![JPEG, PNG, WebP, TIFF, JPEG 2000 and JBIG2 images, all decoded by Sdcb.SimdCodecs](https://cv-public.sdcb.ai/simdcodecs/readme/showcase-v1.jpg)

<sub>Every tile above was decoded by this library; the files are the LINQPad samples' images
([how they were made](tools/sample-assets/README.md)).</sub>

## Quick start

```bash
dotnet add package Sdcb.SimdCodecs
```

```csharp
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;

byte[] file = File.ReadAllBytes("photo.webp");   // JPEG, PNG, TIFF, WebP, GIF, JPEG 2000 or JBIG2

// The facade sniffs the format from the magic bytes and decodes into a buffer you own (R G B A).
if (!SimdCodecs.TryReadInfo(file, out ImageInfo info)) throw new InvalidDataException("Not a supported image");
byte[] rgba = new byte[info.Width * info.Height * 4];
SimdCodecs.TryDecodeIntoRgba8(file, rgba);

// Encode it as a JPEG: SIMD, baseline / optimised Huffman / progressive, 4:2:0 or 4:4:4.
byte[] jpeg = JpegEncoder.Encode(rgba, info.Width, info.Height, channels: 4, new JpegEncodeOptions { Quality = 90 });
File.WriteAllBytes("photo.jpg", jpeg);
```

The rest of the facade:

- `SimdCodecs.TryDecodeInto(file, buffer, stride, format)` — BGRA (Windows / WPF / Skia), RGB, BGR (OpenCV)
  or grey (OCR) straight into your buffer, at any row stride. JPEG and PNG write these natively, with no RGBA
  frame in between; a colour JPEG's grey is its Y plane.
- `SimdCodecs.TryDecode(file, out image)` — the native layout: channel count (grey JPEG 1, colour JPEG 3),
  8/16-bit or float samples, ICC profile.
- `SimdCodecs.TryReadInfo(file, out info)` — from the headers alone: format, stored bit depth, EXIF
  orientation (reported, never applied), frame or page count.
- `SimdCodecs.DetectFormat(file)` — also names formats it recognises but does not decode (HEIF, AVIF,
  JPEG XL, BMP, …); `SimdCodecs.IsDecodable(format)` tells the two apart.
- `SimdCodecs.TryOpenFrames(file, out reader)` — every frame, by index, in any layout: animated WebP / GIF
  frames with durations, multi-page TIFF pages (any other image is one frame).
  `reader.EnumerateFramesInto(buffer, stride, format)` plays them in order through one buffer.

Each format is also its own package with a richer API: scaled JPEG decoding, TIFF segment reads, WebP
animation seeking by timestamp, Ultra HDR, and more.

## Formats

| Format | Decode | Encode | Highlights |
|---|:-:|:-:|---|
| JPEG | ✅ | ✅ | Decodes baseline and progressive DCT JPEG, any subsampling, CMYK / YCCK, scaled 1/2–1/8, zero-allocation `DecodeTo`; the separate `LosslessJpeg.FromMemory` API handles SOF3; encodes baseline / optimised Huffman / progressive with EXIF and XMP |
| Ultra HDR JPEG | ✅ | ✅ | Gain-map read, assemble and HDR reconstruction (Ultra HDR / Adobe) |
| PNG | ✅ | ✅ | All bit depths (1–16, indexed); text chunks; HDR signalling (cICP, mDCV, cLLI) |
| TIFF | ✅ | ✅ | Multi-page; 8/16/32-bit integer and float; strips or tiles; reads Deflate, LZW, PackBits, CCITT G3/G4 and single tiles; writes Deflate |
| WebP | ✅ | — | Lossy, lossless, alpha; animation with seeking; byte-exact with libwebp |
| GIF | ✅ | ✅ | 87a / 89a: interlacing, transparency, every disposal method; animation with seeking; encodes stills and animations (exact palette, or Wu quantization + dithering) |
| JPEG 2000 | ✅ | — | Part 1 (T.800), for PDF `/JPXDecode` |
| JBIG2 | ✅ | — | T.88, for PDF `/JBIG2Decode` |
| EXIF | ✅ | ✅ | From JPEG, TIFF, PNG and WebP; `ExifWriter` builds a block for the JPEG encoder |
| ICC profiles | ✅ | ✅ | Decoders return embedded profiles; `IccProfiles.Describe` names one; bundled sRGB v4 profile; splice a profile into an existing JPEG |

The table describes the individual format packages. The `Sdcb.SimdCodecs` facade intentionally exposes a narrower dispatch/display contract; for example, the separate `LosslessJpeg.FromMemory` API supports SOF3 directly, while `JpegDecoder` and the facade reject it. The other facade-specific limits are listed in [docs/known-gaps.md](docs/known-gaps.md) (in Chinese).

## Performance

Single-threaded, AMD Ryzen 7 5800X, .NET 10. Time relative to the native library (lower is faster):

| JPEG decode to RGBA, 446 files | Time | Managed allocation per decode |
|---|---:|---:|
| libjpeg-turbo 3.2.0 (native) | 1.00× | — |
| **Sdcb.SimdCodecs** | **0.82×** | **0 B** |
| ImageSharp 3.1.12 | 1.77× | about 16 KiB, plus its own image |

| WebP decode to RGBA, 1920×1080 | lossy | lossy + alpha | lossless |
|---|---:|---:|---:|
| libwebp 1.6.0 (native) | 1.00× | 1.00× | 1.00× |
| **Sdcb.SimdCodecs** | **0.88×** | **0.53×** | **0.72×** |

The JPEG decoder is also at least as accurate as libjpeg-turbo's default (about 1 dB higher RGB PSNR
against an exact reference where chroma is upsampled), and its output is bit-identical on AVX2, SSE,
AdvSimd, scalar and NativeAOT. WebP output is byte-identical to libwebp. Full tables, corpora and
methodology:
[JPEG](benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/results/2026-10-24-amd-ryzen-7-5800x-full-m5.md),
[WebP](benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks/optimization/m4-final-2026-10-25-amd-ryzen-7-5800x.md)
(on photo-like opaque lossy files WebP is on par: 0.87–1.13×).

## Packages

All packages ship in lockstep under one version.

| Package | NuGet | Contents |
|---|---|---|
| `Sdcb.SimdCodecs` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs) | Facade: sniffs the format and dispatches to the format decoders |
| `Sdcb.SimdCodecs.Jpeg` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Jpeg.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg) | JPEG decoder and encoder |
| `Sdcb.SimdCodecs.Jpeg.GainMap` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Jpeg.GainMap.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg.GainMap) | Ultra HDR / Adobe gain maps |
| `Sdcb.SimdCodecs.Jpeg.IccInjector` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Jpeg.IccInjector.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg.IccInjector) | Splice an ICC profile into a JPEG |
| `Sdcb.SimdCodecs.Png` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Png.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Png) | PNG decoder and encoder |
| `Sdcb.SimdCodecs.Tiff` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Tiff.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Tiff) | TIFF reader and writer |
| `Sdcb.SimdCodecs.Webp` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Webp.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Webp) | WebP decoder, including animation |
| `Sdcb.SimdCodecs.Gif` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Gif.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Gif) | GIF decoder and encoder, including animation |
| `Sdcb.SimdCodecs.Jpeg2000` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Jpeg2000.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg2000) | JPEG 2000 decoder |
| `Sdcb.SimdCodecs.Jbig2` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Jbig2.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jbig2) | JBIG2 decoder |
| `Sdcb.SimdCodecs.Exif` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Exif.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Exif) | EXIF reader and writer |
| `Sdcb.SimdCodecs.Color.Icc` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Color.Icc.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Color.Icc) | sRGB v4 ICC profile, `cicp` tag injector |
| `Sdcb.SimdCodecs.Abstractions` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Abstractions.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Abstractions) | Shared types: `IImageDecoder`, `RasterImage`, `ImagePixelFormat` / `PixelConverter`, `IImageFrameReader` (frames and pages), H.273 colour vocabulary |

<details>
<summary>Feature details per package</summary>

- **Jpeg** — T.81 decoder: baseline, progressive, restart intervals, any subsampling,
  grey / YCbCr / RGB / CMYK / YCCK, scaled 1/2–1/8 decode, lossless (SOF3). SIMD encoder: baseline,
  optimised Huffman or progressive; 4:4:4 / 4:2:0 / grey; embeds EXIF (APP1) and XMP.
- **Png** — every standard bit depth (including 1/2/4-bit grey and indexed), tEXt / zTXt / iTXt text
  chunks, PNG-3 cICP / mDCV / cLLI HDR signalling.
- **Tiff** — multi-page; 8/16/32-bit integer and float; Deflate / LZW (+ PackBits read); strips or
  tiles; chunky or planar; on-demand single strip or tile reads; reads 1/2/4-bit pages (expanded to
  8-bit), CCITT fax (Group 3 1D/2D, Group 4, Modified Huffman) and the floating-point predictor.
- **Webp** — RFC 6386 / RFC 9649: VP8 lossy, VP8L lossless, ALPH, animation with seek, ICC / EXIF / XMP.
  The facade's `TryDecode` returns a still or frame 0 of an animation, `TryOpenFrames` every frame;
  `WebpAnimationDecoder` adds seeking by timestamp and the frame rectangles.
- **Gif** — GIF89a: global / local colour tables, interlacing, transparency, all disposal methods, NETSCAPE2.0 loop
  count; composited frames with seek. The decisions the spec leaves open are listed in the package README. Encoder:
  stills and animations, exact palettes or Wu quantization with optional Floyd–Steinberg dithering, changed-rectangle
  frames for opaque animations.
- **Exif** — reads JPEG, TIFF, PNG and WebP; `ExifWriter` builds a blob for `JpegEncodeOptions.Exif`.

</details>

<details>
<summary>Dependency graph</summary>

```
Sdcb.SimdCodecs (facade)
 ├─ Abstractions
 ├─ Png ──────────┬─ Color.Icc ─ Abstractions
 │                └─ Abstractions
 ├─ Tiff ─────────┬─ Png
 │                └─ Abstractions
 ├─ Jpeg ─────────── Abstractions
 ├─ Jpeg.GainMap ─┬─ Jpeg
 │                ├─ Color.Icc
 │                └─ Abstractions
 ├─ Jbig2 ────────── Abstractions
 ├─ Jpeg2000 ─────── Abstractions
 ├─ Webp ─────────── Abstractions
 └─ Gif ──────────── Abstractions

Exif ─ Tiff        Jpeg.IccInjector (no dependencies)
```

The MQ arithmetic decoder (JBIG2, JPEG 2000), the T.4 fax code tables (JBIG2, TIFF) and the EXIF
orientation reader (JPEG, PNG, WebP) are shared as source (`src/Shared`), compiled into each package that
uses them: no package uses another's internals.

</details>

## LINQPad samples

Each package ships runnable samples in `linqpad-samples/`, which LINQPad lists in its NuGet browser
(source: `src/<package>/linqpad-samples/`):

| Package | Samples |
|---|---|
| `Sdcb.SimdCodecs` | `quick-start`, `all-formats`, `skiasharp`, `opencvsharp` |
| `Sdcb.SimdCodecs.Jpeg` | `encode` (quality, subsampling, progressive, EXIF/XMP), `scaled-decode` |
| `Sdcb.SimdCodecs.Jpeg.GainMap` | `ultrahdr` (write and read an Ultra HDR JPEG) |
| `Sdcb.SimdCodecs.Png` | `read-write` |
| `Sdcb.SimdCodecs.Tiff` | `multi-page` (incl. CCITT Group 4), `float-and-tiles`, `write` |
| `Sdcb.SimdCodecs.Exif` | `read-exif` |
| `Sdcb.SimdCodecs.Webp` | `decode`, `animation` |
| `Sdcb.SimdCodecs.Gif` | `decode`, `encode` |

They download their images from `https://cv-public.sdcb.ai/simdcodecs/samples/v1/`; how those were
made and where they come from is in [tools/sample-assets](tools/sample-assets/README.md).

## Build and test

```bash
dotnet build SimdCodecs.slnx -c Release
dotnet test  SimdCodecs.slnx -c Release
```

Oracle-backed tests skip when their reference tool is missing; set `REQUIRE_ORACLES=1` to make a
missing oracle fail instead (CI does). How to get each oracle is in [`AGENTS.md`](AGENTS.md), which
also holds the clean-room rules every contribution follows. Benchmarks are built on their own, by
path: see [`benchmarks/README.md`](benchmarks/README.md). Releasing: [`RELEASING.md`](RELEASING.md).

## Contact

![WeChat group QR code](https://io.starworks.cc:88/cv-public/2026/ocr-wxg-qr.png?1027)

If the WeChat group is full or the QR code has expired, join the QQ group [C#/.NET Computer Vision 579060605](https://qm.qq.com/q/bPw5jAK4qk).

## License

MIT — see [`LICENSE`](LICENSE). Derived from SharpAstro/Codecs (released under the Unlicense).
