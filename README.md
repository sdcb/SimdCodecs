<img src="https://raw.githubusercontent.com/sdcb/SimdCodecs/main/assets/icon.png" width="64" align="right" alt="Sdcb.SimdCodecs icon">

# Sdcb.SimdCodecs [![CI](https://github.com/sdcb/SimdCodecs/actions/workflows/dotnet.yml/badge.svg)](https://github.com/sdcb/SimdCodecs/actions/workflows/dotnet.yml) [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**English** | [简体中文](README.zh-CN.md)

Pure-managed image codecs for .NET 10: JPEG, PNG, TIFF, WebP, GIF, BMP, ICO / CUR, QOI, Netpbm, JPEG 2000 and JBIG2, plus EXIF and ICC, and HEIF / HEIC decoding as an opt-in package.

- **No native dependencies.** Runs wherever .NET does, including NativeAOT and browser-wasm, with identical output on every target.
- **As fast as the native libraries.** SIMD (AVX2, SSE, AdvSimd) with a scalar fallback: JPEG decode is 0.82× libjpeg-turbo's time, PNG decode 0.88× libpng's on photos, WebP decode 0.53–0.88× libwebp's (see [Performance](#performance)).
- **Exact.** WebP decode is byte-identical to libwebp, HEIF's HEVC planes to ffmpeg, PNG and lossless JPEG 2000 are bit-exact; the JPEG decoder is about 1 dB more accurate than libjpeg-turbo's default where chroma is upsampled.
- **Low memory.** Decode straight into your own buffer in any pixel layout; JPEG decode allocates 0 bytes of managed memory.

![JPEG, PNG, WebP, TIFF, JPEG 2000 and JBIG2 images, all decoded by Sdcb.SimdCodecs](https://cv-public.sdcb.ai/simdcodecs/readme/showcase-v1.jpg)

<sub>Every tile above was decoded by this library; the files are the LINQPad samples' images ([how they were made](tools/sample-assets/README.md)).</sub>

## Quick start

```bash
dotnet add package Sdcb.SimdCodecs
```

```csharp
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;

byte[] file = File.ReadAllBytes("photo.webp");   // JPEG, PNG, TIFF, WebP, GIF, BMP, ICO, CUR, QOI, Netpbm, JPEG 2000 or JBIG2

// The facade sniffs the format from the magic bytes. One call gives the pixels in the layout you ask for,
// turned upright by the EXIF orientation; it throws NotSupportedException for a format it does not decode
// and InvalidDataException with the codec's reason for a damaged file.
using PixelBuffer image = ImageCodecs.Decode(file, PixelFormat.Rgba32, new DecodeOptions { ApplyOrientation = true });

// Encode it as a JPEG: SIMD, baseline / optimised Huffman / progressive, 4:2:0 or 4:4:4.
// Passing image.Metadata keeps the ICC profile and Exif (its Orientation is already reset to 1).
using FileStream output = File.Create("photo.jpg");
ImageCodecs.Encode(image.Pixels, image.Width, image.Height, image.Format, ImageFormat.Jpeg, output,
    stride: image.Stride, options: new JpegEncodeOptions { Quality = 90 }, metadata: image.Metadata);
```

Common tasks:

```csharp
// A server checks the header before it allocates anything.
if (!ImageCodecs.TryReadInfo(file, out ImageInfo info) || (long)info.Width * info.Height > 50_000_000)
    return Results.BadRequest();

// Decode into memory you own (a bitmap, a Mat, an upload buffer) in any layout, at any row stride.
// See the skiasharp and opencvsharp samples for an SKBitmap and a Mat.
ImageCodecs.TryDecodeInto(file, buffer, stride, PixelFormat.Bgra32Premultiplied);

// A thumbnail: JPEG scales by 1/2-1/8 in the DCT domain, every other format by a box filter.
using IDecodedImage small = ImageCodecs.Decode(file, new DecodeOptions { ScaleDenominator = 8 });

// HEIF / HEIC (iPhone photos): install Sdcb.SimdCodecs.Heif and register it once at start-up.
ImageCodecs.Register(HeifImageDecoder.Registration);
```

<details>
<summary>The whole facade</summary>

- `ImageCodecs.Decode(file, format, options)` / `TryDecodeInto(file, buffer, stride, format, options)`: any `PixelFormat` (RGBA, BGRA, `Bgra32Premultiplied` for WPF / Direct2D / Skia, RGB, BGR for OpenCV, grey for OCR, and the 16-bit `Rgba64`, `Gray16`, …) at any row stride. JPEG and PNG write the 8-bit layouts natively, with no RGBA frame in between; a colour JPEG's grey is its Y plane.
- `ImageCodecs.Decode(file, options)` / `TryDecode(file, out image, options)`: the native layout (channel count, 8/16-bit or float samples, real bit depth) with the file's ICC, Exif and XMP in `image.Metadata`. Dispose the image to return pooled memory.
- `DecodeOptions`: `Region` (a crop; TIFF and HEIF read only the strips or tiles it touches, JPEG, PNG and WebP convert only its rows), `ScaleDenominator` (any power of two for every codec: JPEG 1/2–1/8 and JPEG 2000 resolution levels natively, the rest by a box filter; `TryGetClosestSize` says what a codec does natively), `Pool` (an `ArrayPool<byte>` for the pixels), `ApplyOrientation` (EXIF / TIFF orientation applied, the Exif Orientation reset to 1; `GetOutputSize(info)` sizes a buffer). Every entry point takes a `CancellationToken`.
- `ImageCodecs.TryReadInfo(file, out info)`: from the headers alone: format, size, stored bit depth, colour encoding (PQ / HLG HDR?), resolution (DPI), EXIF orientation, frame or page count, whether there is a thumbnail.
- `ImageCodecs.TryReadMetadata(file, out metadata)` / `TryDecodeThumbnail(file, out thumb)`: the Exif, XMP and ICC blocks for a transcoder to pass on, and the embedded preview (Exif IFD1, HEIF `thmb`). `PixelTransform.Orient` turns pixels you decoded without `ApplyOrientation`.
- `ImageDecoder.Open(stream | path | memory)` / `OpenAsync(stream)`: a WIC-style session: `Format`, `FrameCount`, then `GetFrame(i).Decode(options)` or `.CopyPixels(region, format, buffer, stride)`. A file path is memory-mapped; a non-seekable stream such as an upload is buffered for you.
- `ImageCodecs.TryOpenFrames(file, out reader)`: every frame by index, in any layout: animated WebP / GIF / APNG frames with durations, multi-page TIFF and JBIG2 pages, the images of an icon. `reader.EnumerateFramesInto(buffer, stride, format)` plays them through one buffer.
- `ImageCodecs.Encode(image, format, stream | writer, options)` / `Encode(pixels, width, height, pixelFormat, format, …)` / `CreateEncoder(format, writer)`: every encoder takes every `PixelFormat`, rows at any stride, and writes the image's metadata; `GetClosestInputFormat` shows where an 8-bit format narrows. The options are the format's own type (`JpegEncodeOptions`, `WebpLossyEncodeOptions`, …; null for its defaults); WebP with no options is lossy. An encoder session takes the image in bands and several frames for an animation.
- `ImageCodecs.TryReadPlanarInfo` / `TryDecodePlanes`: the YCbCr planes as coded (HEIF), for video pipelines and GPU upload.
- `ImageCodecs.DetectFormat(file)`: also names formats it recognises but does not decode (AVIF, JPEG XL, …); `IsDecodable(format)` tells the two apart.

The facade covers what every format has in common. Each format package has a richer API of its own: TIFF segment reads, WebP animation seeking by timestamp, Ultra HDR reconstruction, lossless JPEG (`LosslessJpeg.FromMemory`, SOF3, which the facade rejects) and more. What the facade and each codec do not do is listed in [docs/known-gaps.md](docs/known-gaps.md) (in Chinese).

</details>

Upgrading from 1.x: [docs/migrating-to-2.0.md](docs/migrating-to-2.0.md).

## Formats

| Format | Package | Decode | Encode | Highlights |
|---|---|:-:|:-:|---|
| JPEG | [`Sdcb.SimdCodecs.Jpeg`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg) | ✅ | ✅ | Baseline and progressive, any subsampling, CMYK / YCCK, scaled 1/2–1/8, lossless (SOF3); encodes baseline / optimised Huffman / progressive with EXIF, XMP and ICC |
| Ultra HDR JPEG | [`Sdcb.SimdCodecs.Jpeg.GainMap`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg.GainMap) | ✅ | ✅ | Gain-map read, assemble and HDR reconstruction (Ultra HDR / Adobe) |
| PNG | [`Sdcb.SimdCodecs.Png`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Png) | ✅ | ✅ | All bit depths; lossy palette quantization (the pngquant use case); APNG; HDR signalling (cICP, mDCV, cLLI) |
| TIFF | [`Sdcb.SimdCodecs.Tiff`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Tiff) | ✅ | ✅ | Multi-page, BigTIFF, 8/16/32-bit integer and float, strips or tiles; JPEG, Deflate, LZW, PackBits, CCITT G3/G4 |
| WebP | [`Sdcb.SimdCodecs.Webp`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Webp) | ✅ | ✅ | Lossy, lossless, alpha, animation with seeking; byte-exact with libwebp; encodes lossy and lossless stills and animations |
| GIF | [`Sdcb.SimdCodecs.Gif`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Gif) | ✅ | ✅ | Animation with seeking; encodes exact or perceptually quantized palettes with dithering |
| BMP | [`Sdcb.SimdCodecs.Bmp`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Bmp) | ✅ | ✅ | Every Windows and OS/2 variant, RLE, bit fields, embedded PNG; pixel-for-pixel identical to WIC |
| ICO / CUR | [`Sdcb.SimdCodecs.Ico`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Ico) | ✅ | ✅ | Every image of an icon or cursor, PNG or DIB, hotspots; multi-size encoding |
| QOI | [`Sdcb.SimdCodecs.Qoi`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Qoi) | ✅ | ✅ | RGB and RGBA |
| Netpbm | [`Sdcb.SimdCodecs.Pnm`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Pnm) | ✅ | ✅ | PBM, PGM, PPM plain and raw up to 16-bit, PAM, PFM float |
| JPEG 2000 | [`Sdcb.SimdCodecs.Jpeg2000`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg2000) | ✅ | | Part 1, for PDF `/JPXDecode`; JP2 alpha; resolution-reduced decode; tiles decoded on all cores |
| JBIG2 | [`Sdcb.SimdCodecs.Jbig2`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jbig2) | ✅ | | T.88, for PDF `/JBIG2Decode` |
| HEIF / HEIC | [`Sdcb.SimdCodecs.Heif`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Heif) | ✅ | | Phone photos: HEVC Main / Main 10 / Main Still Picture, grids, 16-bit for Main 10, YCbCr planes; byte-exact with ffmpeg |
| EXIF | [`Sdcb.SimdCodecs.Exif`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Exif) | ✅ | ✅ | Reads JPEG, TIFF, PNG, WebP, HEIF and AVIF Exif; `ExifWriter` builds a block for the encoders |
| ICC | [`Sdcb.SimdCodecs.Color.Icc`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Color.Icc), [`Sdcb.SimdCodecs.Jpeg.IccInjector`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg.IccInjector) | ✅ | ✅ | Decoders return the embedded profile; a bundled sRGB v4 profile; splice a profile into an existing JPEG |

`Sdcb.SimdCodecs` is the facade and references every package above except HEIF; `Sdcb.SimdCodecs.Abstractions` holds the shared types (`PixelFormat`, `DecodeOptions`, `IImageDecoder`, …). All packages ship in lockstep under one version.

**HEIF is a separate, opt-in package.** HEVC is patent-encumbered and MIT conveys no patent licence, so the facade does not reference `Sdcb.SimdCodecs.Heif`: install it and call `ImageCodecs.Register(HeifImageDecoder.Registration)` once, after evaluating HEVC's patent obligations for your product.

<details>
<summary>Feature details per package</summary>

- **Jpeg**: T.81 decoder: baseline, progressive, restart intervals, any subsampling, grey / YCbCr / RGB / CMYK / YCCK, scaled 1/2–1/8 decode, lossless (SOF3, through `LosslessJpeg.FromMemory`). SIMD encoder: baseline, optimised Huffman or progressive; 4:4:4 / 4:2:0 / grey; restart intervals; embeds EXIF (APP1), XMP and an ICC profile (APP2).
- **Png**: every standard bit depth (including 1/2/4-bit grey and indexed), tEXt / zTXt / iTXt text chunks, PNG-3 cICP / mDCV / cLLI HDR signalling. `PngWriteOptions.Quantize` reduces an image to at most 256 colours, translucency included, the way pngquant does: smaller than pngquant's file at equal SSIM on the benchmark corpus (`benchmarks/Sdcb.SimdCodecs.Quantize.Benchmarks`). APNG: `PngAnimationDecoder` composites every dispose / blend operation and seeks; `PngAnimationEncoder` writes changed rectangles, merging unchanged frames.
- **Tiff**: multi-page, classic and BigTIFF; 8/16/32-bit integer and float; Deflate / LZW (+ PackBits and JPEG read, with JPEGTables); strips or tiles; chunky or planar; on-demand single strip or tile reads, and a region decode that reads only the segments it touches; reads 1/2/4-bit pages (expanded to 8-bit), CCITT fax (Group 3 1D/2D, Group 4, Modified Huffman) and the floating-point predictor; the facade presents palette, CMYK and YCbCr pages as RGB and extra samples as alpha. Writes Deflate.
- **Webp**: RFC 6386 / RFC 9649: VP8 lossy, VP8L lossless, ALPH, animation with seek, ICC / EXIF / XMP. `WebpAnimationDecoder` adds seeking by timestamp and the frame rectangles. `WebpEncoder.EncodeLossless` writes VP8L stills with alpha, effort 0–9; `WebpEncoder.EncodeLossy` writes VP8 stills, quality 0–100 and effort 0–6, with transparency in an ALPH plane; both can embed ICC / Exif / XMP. `WebpAnimationEncoder` and `WebpEncoder.EncodeAnimation` write animations, lossy, lossless or the smaller per frame, from whole canvases (reduced to the changed rectangle, unchanged frames merged) or from ANMF frames as given.
- **Gif**: GIF89a: global / local colour tables, interlacing, transparency, all disposal methods, NETSCAPE2.0 loop count; composited frames with seek. Encoder: stills and animations, exact palettes or perceptual quantization (OKLab box split + k-means) with optional edge-aware Floyd–Steinberg dithering, changed-rectangle frames for opaque animations. The decisions the spec leaves open are listed in the package README.
- **Bmp**: the Windows DIB and OS/2 bitmap: core, OS/2 2.x, 40-byte, V2–V5 headers; 1/2/4/8-bit palettes and 16/24/32-bit; `BI_BITFIELDS` / `BI_ALPHABITFIELDS`; RLE4 / RLE8 with deltas, OS/2 RLE24 and Huffman 1D; `BI_PNG`; top-down rows; V5 ICC; packed DIBs (clipboard, icon images). Encodes palettes (exact or quantized), RLE, 555 / 565, 24 / 32-bit. Checked pixel for pixel against WIC; the decisions the documentation leaves open are listed in the package README.
- **Ico**: icons and cursors over Bmp and Png: every image, the largest and deepest by default, hotspots; encodes multi-size icons and cursors, each image PNG or a 1/4/8/24/32-bit DIB.
- **Qoi**: the QOI specification: 3- and 4-channel images, the colour space reported (a linear one as a linear transfer); encodes from RGBA, BGRA, RGB, BGR or grey.
- **Pnm**: Netpbm: P1–P6 plain and raw at any maximum up to 65535 (16-bit samples above 255), PAM (P7) with 1–4 channels, PFM (`Pf` / `PF`) as float samples; only a file's first image. Encodes every format; plain output keeps lines within 70 characters. Matches ImageMagick exactly, both ways.
- **Jpeg2000**: T.800 Part 1, J2K codestreams and JP2 files; JP2 channel definitions (`cdef`) for alpha; decode at a reduced resolution level; tiles, code-blocks and the wavelet run on all cores. Lossless decode is bit-identical to libopenjp2.
- **Jbig2**: T.88 as embedded in PDF (`/JBIG2Decode`, with global segments) and standalone files: generic, refinement, text and halftone regions, pattern and symbol dictionaries.
- **Exif**: reads JPEG, TIFF, PNG, WebP, HEIF and AVIF; `ExifWriter` builds a blob for `JpegEncodeOptions.Exif` and the other encoders.
- **Heif**: ISO/IEC 23008-12 still images over the ISOBMFF item model: HEVC intra decoding (H.265 Main, Main 10, Main Still Picture, 4:2:0), `grid` tiles decoded in parallel, `clap` / `irot` / `imir` applied, ICC passed through, nclx / VUI colour reported; Main 10 decodes to 16-bit samples; PQ / HLG refuse the 8-bit layouts. Alpha, gain maps, sequences and range-extension profiles are not decoded. See the package README.

</details>

## Performance

Single-threaded except where noted, AMD Ryzen 7 5800X, .NET 10. Time relative to the reference library: below 1.00× is faster.

| Operation | Compared with | Sdcb.SimdCodecs | ImageSharp 3.1.12 |
|---|---|---:|---:|
| [JPEG decode to RGBA, 446 files](benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/results/2026-10-24-amd-ryzen-7-5800x-full-m5.md) | libjpeg-turbo 3.2.0 | **0.82×**, 0 B allocated | 1.77× |
| [PNG decode to RGBA, 1080p 8-bit RGB / RGBA photo](benchmarks/Sdcb.SimdCodecs.Png.Benchmarks/results/decode-2026-11-09-amd-ryzen-7-5800x.md) | libpng 1.6.39 | **0.88×** | 0.91× |
| [WebP decode, 1080p lossy / lossy + alpha / lossless](benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks/optimization/m4-final-2026-10-25-amd-ryzen-7-5800x.md) | libwebp 1.6.0 | **0.88× / 0.53× / 0.72×** | |
| [HEIF decode to RGBA, 8 cores](benchmarks/Sdcb.SimdCodecs.Heif.Benchmarks/results/2026-11-04-amd-ryzen-7-5800x-m3.md) | libheif 1.23.1 | **0.20–0.32×**, 75 MB peak (libheif 80 MB) | |
| [BMP decode / encode, 1080p](benchmarks/Sdcb.SimdCodecs.Bmp.Benchmarks/results/m1-tuned-2026-10-30-amd-ryzen-7-5800x.md) | WIC | **0.20× / 0.05–0.08×** | 1.62× / 0.06× |
| [QOI decode / encode, 1080p](benchmarks/Sdcb.SimdCodecs.QoiPnm.Benchmarks/results/tuned-2026-10-30-amd-ryzen-7-5800x.md) | ImageSharp 3.1.12 | **0.29× / 0.49×** | 1.00× |
| [Netpbm decode / encode, 1080p](benchmarks/Sdcb.SimdCodecs.QoiPnm.Benchmarks/results/tuned-2026-10-30-amd-ryzen-7-5800x.md) | ImageSharp 3.1.12 | **0.23× / 0.54×** | 1.00× |
| [JPEG 2000 decode, 1600×1200 lossless / lossy](benchmarks/Sdcb.SimdCodecs.Jpeg2000.Benchmarks/results/2026-10-30-amd-ryzen-7-5800x-baseline.md) | libopenjp2 2.5.4 | 1.96× / 2.03× | |

Each row links to its full results, corpus and method. JPEG 2000 is the one codec still slower than its native reference on one core; on all cores it is 1.15–1.75× libopenjp2 with its own threads on ([record](benchmarks/Sdcb.SimdCodecs.Jpeg2000.Benchmarks/results/2026-11-05-amd-ryzen-7-5800x-parallel.md)). How to measure, and the rules every change follows: [benchmarks/README.md](benchmarks/README.md).

## Migrating from ImageSharp

Sdcb.SimdCodecs reads and writes images; it does not resize, draw or filter them. Decode into an `SKBitmap` or an OpenCV `Mat` for that (the `skiasharp` and `opencvsharp` samples), or keep ImageSharp for processing and use this library for the codecs.

| ImageSharp | Sdcb.SimdCodecs |
|---|---|
| `Image.Load<Rgba32>(bytes)` | `ImageCodecs.Decode(bytes, PixelFormat.Rgba32)` returns a `PixelBuffer` (`Pixels`, `Width`, `Height`, `Stride`) |
| `image.CopyPixelDataTo(span)` | `ImageCodecs.TryDecodeInto(bytes, span, 0, PixelFormat.Rgba32)`: no intermediate image |
| `image.ProcessPixelRows(…)` / `image[x, y]` | `buffer.Pixels` / `buffer.WritablePixels`, rows `buffer.Stride` bytes apart |
| `Image.Identify(bytes)` | `ImageCodecs.TryReadInfo(bytes, out ImageInfo info)` |
| `Image.DetectFormat(bytes)` | `ImageCodecs.DetectFormat(bytes)` |
| `Image.LoadAsync(stream)` | `await ImageDecoder.OpenAsync(stream)`, then `decoder.GetFrame(0).Decode()` |
| `image.Mutate(x => x.AutoOrient())` | `new DecodeOptions { ApplyOrientation = true }`, or `PixelTransform.Orient` afterwards |
| `image.Mutate(x => x.Crop(rect))` | `new DecodeOptions { Region = new PixelRect(x, y, width, height) }`: only the region is converted |
| `new DecoderOptions { TargetSize = … }` | `new DecodeOptions { ScaleDenominator = 2 / 4 / 8 }`; `ImageCodecs.TryGetClosestSize` picks one for a target size |
| `image.SaveAsJpeg(stream, new JpegEncoder { Quality = 90 })` | `ImageCodecs.Encode(image, ImageFormat.Jpeg, stream, new JpegEncodeOptions { Quality = 90 })` |
| `image.SaveAsPng(stream)` | `ImageCodecs.Encode(image, ImageFormat.Png, stream)`; `PngWriteOptions { Quantize = new() }` for a pngquant-style palette PNG |
| `new WebpEncoder { FileFormat = WebpFileFormatType.Lossless }` | `new WebpLosslessEncodeOptions()` (with no options WebP is lossy) |
| `image.Metadata.ExifProfile` | `image.Metadata.Exif` (the raw block); `ExifReader.FromBytes(…)` from `Sdcb.SimdCodecs.Exif` parses it |
| `image.Frames[i]` (GIF, WebP, APNG, TIFF pages) | `ImageDecoder.Open(bytes).GetFrame(i)`, or `ImageCodecs.TryOpenFrames` |
| `Configuration.MemoryAllocator` | `new DecodeOptions { Pool = ArrayPool<byte>.Shared }` |

In the `Encode` rows `image` is an `IDecodedImage` from `ImageCodecs.Decode(bytes)`; a `PixelBuffer` is encoded with `ImageCodecs.Encode(buffer.Pixels, buffer.Width, buffer.Height, buffer.Format, format, stream, stride: buffer.Stride)`. Both are `IDisposable`, like an ImageSharp `Image`. Two things work differently: pixels come out as stored unless `ApplyOrientation` is set, and an 8-bit `PixelFormat` from a 16-bit or HDR source is a plain conversion, not tone mapping (a float or PQ / HLG image refuses the 8-bit layouts rather than guessing).

## LINQPad samples

Each package ships runnable samples in `linqpad-samples/`, which LINQPad lists in its NuGet browser (source: `src/<package>/linqpad-samples/`):

| Package | Samples |
|---|---|
| `Sdcb.SimdCodecs` | `quick-start` (one-call `Decode` to a `PixelFormat`, `ApplyOrientation`, decode into your own buffer, failure reasons), `all-formats`, `decode-options` (metadata, thumbnail, scaled / region / pooled decode, 16-bit and premultiplied output, `ImageDecoder` sessions), `encode` (`ImageCodecs.Encode` to every format, streamed and animated encoder sessions), `skiasharp` (straight into an `SKBitmap` in Skia's premultiplied native layout), `opencvsharp` (straight into a BGR or grey `Mat`) |
| `Sdcb.SimdCodecs.Jpeg` | `encode` (quality, subsampling, progressive, EXIF/XMP), `scaled-decode` |
| `Sdcb.SimdCodecs.Jpeg.GainMap` | `ultrahdr` (write and read an Ultra HDR JPEG) |
| `Sdcb.SimdCodecs.Png` | `read-write`, `quantize` (lossy palette PNG: size, quality, dithering) |
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

<details>
<summary>Package dependency graph</summary>

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

The MQ arithmetic decoder (JBIG2, JPEG 2000), the T.4 fax code tables (JBIG2, TIFF, BMP), the palette quantizer (GIF, BMP), the stream and array writers (QOI, Netpbm) and the EXIF orientation reader (JPEG, PNG, WebP; its reset to 1 also in Abstractions and the facade) are shared as source (`src/Shared`), compiled into each package that uses them: no package uses another's internals.

</details>

## Contact

![WeChat group QR code](https://io.starworks.cc:88/cv-public/2026/ocr-wxg-qr.png?1109)

If the WeChat group is full or the QR code has expired, join the QQ group [C#/.NET Computer Vision 579060605](https://qm.qq.com/q/bPw5jAK4qk).

## License

MIT — see [`LICENSE`](LICENSE). Derived from SharpAstro/Codecs (released under the Unlicense).
