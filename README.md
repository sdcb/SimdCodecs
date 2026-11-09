<img src="https://raw.githubusercontent.com/sdcb/SimdCodecs/main/assets/icon.png" width="64" align="right" alt="Sdcb.SimdCodecs icon">

# Sdcb.SimdCodecs [![CI](https://github.com/sdcb/SimdCodecs/actions/workflows/dotnet.yml/badge.svg)](https://github.com/sdcb/SimdCodecs/actions/workflows/dotnet.yml) [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**English** | [简体中文](README.zh-CN.md)

Pure-managed image codecs for .NET 10: JPEG, PNG, TIFF, WebP, GIF, BMP, ICO / CUR, QOI, Netpbm (PBM / PGM / PPM / PAM / PFM), JPEG 2000 and JBIG2, plus EXIF and ICC — and HEIF / HEIC decoding as an opt-in package. No native dependencies, NativeAOT-compatible, and SIMD-accelerated (AVX2, SSE, AdvSimd) with a scalar fallback. The JPEG decoder is faster than libjpeg-turbo on the benchmark corpus.

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

byte[] file = File.ReadAllBytes("photo.webp");   // JPEG, PNG, TIFF, WebP, GIF, BMP, ICO, CUR, QOI, Netpbm, JPEG 2000 or JBIG2

// The facade sniffs the format from the magic bytes and decodes into a buffer you own (R G B A).
if (!ImageCodecs.TryReadInfo(file, out ImageInfo info)) throw new InvalidDataException("Not a supported image");
byte[] rgba = new byte[info.Width * info.Height * 4];
ImageCodecs.TryDecodeIntoRgba8(file, rgba);

// Encode it as a JPEG: SIMD, baseline / optimised Huffman / progressive, 4:2:0 or 4:4:4.
byte[] jpeg = JpegEncoder.Encode(rgba, info.Width, info.Height, channels: 4, new JpegEncodeOptions { Quality = 90 });
File.WriteAllBytes("photo.jpg", jpeg);
```

The rest of the facade:

- `ImageCodecs.TryDecodeInto(file, buffer, stride, format)` — any `PixelFormat` straight into your buffer, at any row stride: BGRA (Windows / Skia), `Bgra32Premultiplied` (WPF, Direct2D, compositors), RGB, BGR (OpenCV), grey (OCR), and their 16-bit twins (`Rgba64`, `Gray16`, …) without losing a 16-bit PNG's or a 10-bit HEIC's precision. JPEG and PNG write the 8-bit layouts natively, with no RGBA frame in between; a colour JPEG's grey is its Y plane.
- `ImageCodecs.TryDecode(file, out image, options, cancellationToken)` — the native layout: channel count, 8/16-bit or float samples, real bit depth, ICC, Exif / XMP (`image.Metadata`). `DecodeOptions` asks for a region, a scaled decode (any power of two for every codec: JPEG 1/2–1/8 in the DCT domain and JPEG 2000 by resolution level natively, the rest by a box filter after a full decode; `TryGetClosestSize` says what a codec does natively) or pooled memory (dispose the image to return it). Every entry point takes a `CancellationToken`.
- `ImageCodecs.TryReadInfo(file, out info)` — from the headers alone: format, stored bit depth, colour encoding (is it PQ / HLG HDR?), resolution (DPI), EXIF orientation (reported, never applied), frame or page count, thumbnail.
- `ImageCodecs.TryReadMetadata(file, out metadata)` / `TryDecodeThumbnail(file, out thumb)` — the Exif, XMP and ICC blocks for a transcoder to pass on, and the embedded preview (Exif IFD1, HEIF `thmb`).
- `ImageDecoder.Open(stream | path | memory)` — a WIC-style session: `Format`, `FrameCount`, then `GetFrame(i).Decode(options)` or `.CopyPixels(region, format, buffer, stride)`; `OpenAsync` for a non-seekable stream.
- `ImageCodecs.Encode(image, ImageFormat.Png, stream)` / `CreateEncoder(format, writer)` — the encoder abstraction: every encoder takes every `PixelFormat` (premultiplied BGRA from a UI framework included), rows pushed in any stride, and writes the image's metadata; `GetClosestInputFormat` shows where an 8-bit-only format narrows. The options must be the format's own type (null: its defaults); another format's options throw `ArgumentException`. WebP with no options is lossy; pass `WebpLosslessEncodeOptions` for lossless. Raw pixels go to a `byte[]`, a `Stream` or an `IBufferWriter<byte>` with `ImageCodecs.Encode(pixels, width, height, pixelFormat, format, …)`.
- `ImageCodecs.TryReadPlanarInfo` / `TryDecodePlanes` — the YCbCr planes as coded (HEIF), for video pipelines and GPU upload.
- `ImageCodecs.DetectFormat(file)` — also names formats it recognises but does not decode (AVIF, JPEG XL, …); `ImageCodecs.IsDecodable(format)` tells the two apart. HEIF is decodable once its package is registered.
- `ImageCodecs.Register(HeifImageDecoder.Registration)` — enables HEIF / HEIC in every entry point above. HEVC is patent-encumbered, so `Sdcb.SimdCodecs.Heif` is not part of the facade package: install and register it explicitly (it conveys no patent licence).
- `ImageCodecs.TryOpenFrames(file, out reader)` — every frame, by index, in any layout: animated WebP / GIF frames with durations, multi-page TIFF and JBIG2 pages, the images of an icon (any other image is one frame). `reader.EnumerateFramesInto(buffer, stride, format)` plays them in order through one buffer.

Upgrading from 1.x: see [docs/migrating-to-2.0.md](docs/migrating-to-2.0.md).

Each format is also its own package with a richer API: scaled JPEG decoding, TIFF segment reads, WebP animation seeking by timestamp, Ultra HDR, and more.

## Formats

| Format | Decode | Encode | Highlights |
|---|:-:|:-:|---|
| JPEG | ✅ | ✅ | Decodes baseline and progressive DCT JPEG, any subsampling, CMYK / YCCK, scaled 1/2–1/8, zero-allocation `DecodeTo`; the separate `LosslessJpeg.FromMemory` API handles SOF3; encodes baseline / optimised Huffman / progressive with EXIF, XMP and ICC |
| Ultra HDR JPEG | ✅ | ✅ | Gain-map read, assemble and HDR reconstruction (Ultra HDR / Adobe) |
| PNG | ✅ | ✅ | All bit depths (1–16, indexed); lossy palette quantization (the pngquant use case); APNG animation, decode with seeking and encode with change rectangles; text chunks; HDR signalling (cICP, mDCV, cLLI) |
| TIFF | ✅ | ✅ | Multi-page, BigTIFF; 8/16/32-bit integer and float; grey, RGB, palette, CMYK, YCbCr, alpha; strips or tiles; reads JPEG, Deflate, LZW, PackBits, CCITT G3/G4, single tiles and regions; writes Deflate |
| WebP | ✅ | ✅ | Lossy, lossless, alpha; animation with seeking; byte-exact with libwebp; encodes lossless (VP8L) and lossy (VP8, with an ALPH plane when transparent) stills and animations, with ICC / Exif / XMP |
| GIF | ✅ | ✅ | 87a / 89a: interlacing, transparency, every disposal method; animation with seeking; encodes stills and animations (exact palette, or perceptual quantization + dithering) |
| BMP | ✅ | ✅ | Every Windows and OS/2 variant: core to V5 headers, 1–32 bits, any channel masks, RLE4 / RLE8 / RLE24, OS/2 Huffman 1D, embedded PNG, ICC; SIMD rows into your buffer with no allocation; encodes palettes (exact or quantized), RLE, 555 / 565, 24 / 32-bit |
| ICO / CUR | ✅ | ✅ | Every image of an icon or cursor, PNG or DIB with its AND mask, hotspots; multi-size encoding |
| QOI | ✅ | ✅ | RGB and RGBA, from the specification; decodes into your buffer with no allocation; encodes to an array or a stream |
| Netpbm | ✅ | ✅ | PBM, PGM, PPM plain and raw at any maximum up to 65535, PAM with 1–4 channels, PFM float; native 8/16-bit or float samples, or any layout into your buffer with no allocation; encodes every variant |
| JPEG 2000 | ✅ | — | Part 1 (T.800), for PDF `/JPXDecode`; JP2 alpha (`cdef`); resolution-reduced decode; tiles decoded on all cores |
| JBIG2 | ✅ | — | T.88, for PDF `/JBIG2Decode` |
| HEIF / HEIC | ✅ | — | Separate opt-in package, registered explicitly (HEVC patents; no patent licence conveyed). HEVC Main / Main 10 / Main Still Picture stills and phone-camera grids, byte-exact with ffmpeg; ICC, nclx, Exif / XMP items, `thmb` thumbnails, `clap` / `irot` / `imir`; 8-bit, or 16-bit for Main 10; YCbCr planes |
| EXIF | ✅ | ✅ | From JPEG, TIFF, PNG, WebP, HEIF and AVIF; `ExifWriter` builds a block for the JPEG encoder |
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

| JPEG 2000 decode, 1600×1200 | lossless (5/3) | lossy (9/7, q=40) | PSNR vs native |
|---|---:|---:|---:|
| libopenjp2 2.5.4 (native) | 1.00× (221 ms) | 1.00× (49 ms) | baseline |
| **Sdcb.SimdCodecs** | **1.96×** (433 ms) | **2.03×** (99 ms) | Lossless Exact / Lossy 84 dB |

| HEIF / HEIC decode | HEVC stage, 1 core | to RGBA, 8 cores | peak memory, 12.6 MP photo |
|---|---:|---:|---:|
| libde265 1.1.1 / libheif 1.23.1 (native) | 1.00× | 1.00× | 80 MB |
| **Sdcb.SimdCodecs.Heif** | **0.68–0.87×** | **0.20–0.32×** | **75 MB** |
| Magick.NET 14.13.1 | — | 1.9–2.3× | 220 MB |

| BMP, 1920×1080, geometric mean | decode (22 layouts) | encode 24/32-bit to a stream (4) |
|---|---:|---:|
| WIC (Windows' native codec) | 1.00× | 1.00× |
| **Sdcb.SimdCodecs** | **0.20×** | **0.08×** from RGBA, **0.05×** from BGR(A) |
| ImageSharp 3.1.12 | 1.62× | 0.06× |

BMP decoding writes into your buffer in any layout with no allocation and is pixel-for-pixel identical to WIC (apart from the documented decisions); every RLE, palette and mask layout decodes faster than both WIC and ImageSharp, and 5-6-5 and palette encoding are 2–6× faster than ImageSharp.

| 1920×1080, geometric mean vs. ImageSharp 3.1.12 | decode | encode to a stream |
|---|---:|---:|
| QOI (RGB, RGBA) | **0.29×** | **0.49×** |
| Netpbm (P1–P7, 8 and 16-bit, plain) | **0.23×** | **0.54×** |

QOI and Netpbm decode into your buffer with no allocation; both match ImageMagick exactly, both ways.

The JPEG decoder is also at least as accurate as libjpeg-turbo's default (about 1 dB higher RGB PSNR against an exact reference where chroma is upsampled), and its output is bit-identical on AVX2, SSE, AdvSimd, scalar and NativeAOT. WebP output is byte-identical to libwebp. JPEG 2000 lossless decode is bit-identical to libopenjp2, and lossy decode achieves >84 dB PSNR. HEIF's HEVC planes are byte-identical to ffmpeg's; the HEIF row is the one table here that uses all cores (grid tiles, and WPP rows of a single image, decode in parallel); its first column is one core. Full tables, corpora and methodology: [JPEG](benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/results/2026-10-24-amd-ryzen-7-5800x-full-m5.md), [WebP](benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks/optimization/m4-final-2026-10-25-amd-ryzen-7-5800x.md), and [JPEG 2000](benchmarks/Sdcb.SimdCodecs.Jpeg2000.Benchmarks/results/2026-10-30-amd-ryzen-7-5800x-baseline.md), [BMP / ICO](benchmarks/Sdcb.SimdCodecs.Bmp.Benchmarks/results/m1-tuned-2026-10-30-amd-ryzen-7-5800x.md), [QOI / Netpbm](benchmarks/Sdcb.SimdCodecs.QoiPnm.Benchmarks/results/tuned-2026-10-30-amd-ryzen-7-5800x.md), [HEIF](benchmarks/Sdcb.SimdCodecs.Heif.Benchmarks/results/2026-11-04-amd-ryzen-7-5800x-m3.md) (on photo-like opaque lossy files WebP is on par: 0.87–1.13×; JPEG 2000 pure managed single-threaded decode is currently about 2× of libopenjp2; on all cores it is 1.15–1.75× libopenjp2 with its threads on: [parallel record](benchmarks/Sdcb.SimdCodecs.Jpeg2000.Benchmarks/results/2026-11-05-amd-ryzen-7-5800x-parallel.md)).

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
| `Sdcb.SimdCodecs.Webp` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Webp.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Webp) | WebP decoder, including animation, and lossless and lossy encoder |
| `Sdcb.SimdCodecs.Gif` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Gif.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Gif) | GIF decoder and encoder, including animation |
| `Sdcb.SimdCodecs.Bmp` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Bmp.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Bmp) | BMP / DIB decoder and encoder |
| `Sdcb.SimdCodecs.Ico` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Ico.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Ico) | ICO / CUR decoder and encoder |
| `Sdcb.SimdCodecs.Qoi` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Qoi.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Qoi) | QOI decoder and encoder |
| `Sdcb.SimdCodecs.Pnm` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Pnm.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Pnm) | Netpbm (PBM, PGM, PPM, PAM, PFM) decoder and encoder |
| `Sdcb.SimdCodecs.Jpeg2000` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Jpeg2000.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg2000) | JPEG 2000 decoder |
| `Sdcb.SimdCodecs.Jbig2` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Jbig2.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jbig2) | JBIG2 decoder |
| `Sdcb.SimdCodecs.Heif` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Heif.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Heif) | HEIF / HEIC decoder (HEVC). Not referenced by `Sdcb.SimdCodecs`: install it and call `ImageCodecs.Register(HeifImageDecoder.Registration)`. Conveys no HEVC patent licence |
| `Sdcb.SimdCodecs.Exif` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Exif.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Exif) | EXIF reader and writer |
| `Sdcb.SimdCodecs.Color.Icc` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Color.Icc.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Color.Icc) | sRGB v4 ICC profile, `cicp` tag injector |
| `Sdcb.SimdCodecs.Abstractions` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Abstractions.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Abstractions) | Shared types: `IImageDecoder` / `IImageEncoder`, `IDecodedImage` / `RasterImage`, `PixelFormat` / `PixelConverter`, `DecodeOptions`, `ImageMetadata`, `IPlanarImageDecoder`, `IImageFrameReader` (frames and pages), H.273 colour vocabulary |

<details>
<summary>Feature details per package</summary>

- **Jpeg** — T.81 decoder: baseline, progressive, restart intervals, any subsampling, grey / YCbCr / RGB / CMYK / YCCK, scaled 1/2–1/8 decode, lossless (SOF3). SIMD encoder: baseline, optimised Huffman or progressive; 4:4:4 / 4:2:0 / grey; restart intervals; embeds EXIF (APP1), XMP and an ICC profile (APP2).
- **Png** — every standard bit depth (including 1/2/4-bit grey and indexed), tEXt / zTXt / iTXt text chunks, PNG-3 cICP / mDCV / cLLI HDR signalling. `PngWriteOptions.Quantize` reduces an image to at most 256 colours, translucency included, the way pngquant does: smaller than pngquant's file at equal SSIM on the benchmark corpus (`benchmarks/Sdcb.SimdCodecs.Quantize.Benchmarks`). APNG: `PngAnimationDecoder` composites every dispose / blend operation and seeks; the facade's `TryOpenFrames` plays it; `PngAnimationEncoder` (and a multi-frame facade encode) writes changed rectangles, merging unchanged frames.
- **Tiff** — multi-page, classic and BigTIFF; 8/16/32-bit integer and float; Deflate / LZW (+ PackBits and JPEG read, with JPEGTables); strips or tiles; chunky or planar; on-demand single strip or tile reads, and a region decode that reads only the segments it touches; reads 1/2/4-bit pages (expanded to 8-bit), CCITT fax (Group 3 1D/2D, Group 4, Modified Huffman) and the floating-point predictor; the facade presents palette, CMYK and YCbCr pages as RGB and extra samples as alpha.
- **Webp** — RFC 6386 / RFC 9649: VP8 lossy, VP8L lossless, ALPH, animation with seek, ICC / EXIF / XMP. The facade's `TryDecode` returns a still or frame 0 of an animation, `TryOpenFrames` every frame; `WebpAnimationDecoder` adds seeking by timestamp and the frame rectangles. `WebpEncoder.EncodeLossless` writes VP8L stills with alpha, effort 0–9; `WebpEncoder.EncodeLossy` writes VP8 stills, quality 0–100 and effort 0–6, with transparency in an ALPH plane (lossless by default, `AlphaQuality` below 100 reduces its levels); both write to an array or a stream and can embed ICC / Exif / XMP. `WebpAnimationEncoder` (frame by frame) and `WebpEncoder.EncodeAnimation` write animations, lossy (default), lossless or the smaller per frame, from whole canvases (reduced to the changed rectangle, blended or drawn over, unchanged frames merged) or from ANMF frames as given; `MaxKeyframeDistance` bounds seeking.
- **Gif** — GIF89a: global / local colour tables, interlacing, transparency, all disposal methods, NETSCAPE2.0 loop count; composited frames with seek. The decisions the spec leaves open are listed in the package README. Encoder: stills and animations, exact palettes or perceptual quantization (OKLab box split + k-means) with optional edge-aware Floyd–Steinberg dithering, changed-rectangle frames for opaque animations.
- **Bmp** — the Windows DIB and OS/2 bitmap: core, OS/2 2.x, 40-byte, V2–V5 headers; 1/2/4/8-bit palettes and 16/24/32-bit; `BI_BITFIELDS` / `BI_ALPHABITFIELDS`; RLE4 / RLE8 with deltas, OS/2 RLE24 and Huffman 1D; `BI_PNG`; top-down rows; V5 ICC; packed DIBs (clipboard, icon images). Decodes into any `PixelFormat` without allocating; checked pixel for pixel against WIC. The decisions the documentation leaves open are listed in the package README.
- **Ico** — icons and cursors over Bmp and Png: every image, the largest and deepest by default, hotspots; encodes multi-size icons and cursors, each image PNG or a 1/4/8/24/32-bit DIB.
- **Qoi** — the QOI specification: 3- and 4-channel images, the colour space reported (a linear one as a linear transfer); decodes into any `PixelFormat` without allocating; encodes from RGBA, BGRA, RGB, BGR or grey.
- **Pnm** — Netpbm: P1–P6 plain and raw at any maximum up to 65535 (16-bit samples above 255), PAM (P7) with 1–4 channels, PFM (`Pf` / `PF`) as float samples; only a file's first image. Encodes every format; plain output keeps lines within 70 characters. The decisions the format pages leave open are listed in the package README.
- **Exif** — reads JPEG, TIFF, PNG, WebP, HEIF and AVIF; `ExifWriter` builds a blob for `JpegEncodeOptions.Exif`.
- **Heif** — ISO/IEC 23008-12 still images over the ISOBMFF item model: HEVC intra decoding (H.265 Main, Main 10, Main Still Picture, 4:2:0), `grid` tiles decoded in parallel, `clap` / `irot` / `imir` applied, ICC passed through, nclx / VUI colour reported; PQ / HLG refuse the 8-bit projection. 1.x outputs 8-bit samples; alpha, gain maps, sequences and range-extension profiles are not decoded. See the package README.

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
 ├─ Gif ──────────── Abstractions
 ├─ Bmp ──────────┬─ Png
 │                └─ Abstractions
 ├─ Ico ──────────┬─ Bmp
 │                ├─ Png
 │                └─ Abstractions
 ├─ Qoi ──────────── Abstractions
 └─ Pnm ──────────── Abstractions

Exif ─ Tiff        Jpeg.IccInjector (no dependencies)
Heif ─ Abstractions   (not referenced by the facade: registered by the application)
```

The MQ arithmetic decoder (JBIG2, JPEG 2000), the T.4 fax code tables (JBIG2, TIFF, BMP), the palette quantizer (GIF, BMP), the stream and array writers (QOI, Netpbm) and the EXIF orientation reader (JPEG, PNG, WebP) are shared as source (`src/Shared`), compiled into each package that uses them: no package uses another's internals.

</details>

## LINQPad samples

Each package ships runnable samples in `linqpad-samples/`, which LINQPad lists in its NuGet browser (source: `src/<package>/linqpad-samples/`):

| Package | Samples |
|---|---|
| `Sdcb.SimdCodecs` | `quick-start`, `all-formats`, `decode-options` (metadata, thumbnail, scaled / region / pooled decode, 16-bit and premultiplied output, `ImageDecoder` sessions), `encode` (`ImageCodecs.Encode` to every format, streamed and animated encoder sessions), `skiasharp`, `opencvsharp` |
| `Sdcb.SimdCodecs.Jpeg` | `encode` (quality, subsampling, progressive, EXIF/XMP), `scaled-decode` |
| `Sdcb.SimdCodecs.Jpeg.GainMap` | `ultrahdr` (write and read an Ultra HDR JPEG) |
| `Sdcb.SimdCodecs.Png` | `read-write` |
| `Sdcb.SimdCodecs.Tiff` | `multi-page` (incl. CCITT Group 4), `float-and-tiles`, `write` |
| `Sdcb.SimdCodecs.Exif` | `read-exif` |
| `Sdcb.SimdCodecs.Webp` | `decode`, `animation` |
| `Sdcb.SimdCodecs.Gif` | `decode`, `encode` |
| `Sdcb.SimdCodecs.Bmp` | `bmp` (every layout written, read back and compared) |
| `Sdcb.SimdCodecs.Ico` | `icons` (a multi-size icon and a cursor, written and read back) |
| `Sdcb.SimdCodecs.Qoi` | `qoi` (QOI against PNG on size and speed, decoding into a padded BGRA buffer) |
| `Sdcb.SimdCodecs.Pnm` | `netpbm` (P1–P7 plain and raw, a hand-written PPM, 16-bit PGM, PFM) |
| `Sdcb.SimdCodecs.Heif` | `decode` (phone-camera grids, Main 10, a `clap` crop; `ReadInfo` / `Decode` / `DecodeTo`), `facade` (registering with the facade; BGRA with a padded stride, Gray8) |

They download their images from `https://cv-public.sdcb.ai/simdcodecs/samples/v1/`; how those were made and where they come from is in [tools/sample-assets](tools/sample-assets/README.md). The HEIF samples use the HEIF test images (`…/test-images/heif/`, CC0; see `tests/Sdcb.SimdCodecs.Tests/Oracle/heif/fetch.sh`).

## Build and test

```bash
dotnet build SimdCodecs.slnx -c Release
dotnet test  SimdCodecs.slnx -c Release
```

Oracle-backed tests skip when their reference tool is missing; set `REQUIRE_ORACLES=1` to make a missing oracle fail instead (CI does). How to get each oracle is in [`AGENTS.md`](AGENTS.md), which also holds the clean-room rules every contribution follows. Benchmarks are built on their own, by path: see [`benchmarks/README.md`](benchmarks/README.md).

## Contact

![WeChat group QR code](https://io.starworks.cc:88/cv-public/2026/ocr-wxg-qr.png?1027)

If the WeChat group is full or the QR code has expired, join the QQ group [C#/.NET Computer Vision 579060605](https://qm.qq.com/q/bPw5jAK4qk).

## License

MIT — see [`LICENSE`](LICENSE). Derived from SharpAstro/Codecs (released under the Unlicense).
