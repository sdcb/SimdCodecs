# Migrating to 2.0

2.0 makes its breaking changes to `Sdcb.SimdCodecs.Abstractions` and the facade at once, and shapes the API so that the usual additions in 2.x need no further break. Most code needs a rename and nothing else. Every 8-bit decode output is byte-for-byte what 1.x produced; the decoded pixels change only where 2.0 keeps precision 1.x threw away (HEIF Main 10, see below).

## Renames

| 1.x | 2.0 |
|---|---|
| `SimdCodecs.TryDecode(…)` (the facade class) | `ImageCodecs.TryDecode(…)`: a class named like its namespace segment failed to bind (CS0234) in any project whose namespace contains `SimdCodecs` |
| `ImagePixelFormat` (enum) | `PixelFormat` (readonly record struct): `PixelFormat.Rgba32`, `.Bgra32`, `.Rgb24`, `.Bgr24`, `.Gray8` are the same layouts |
| `PixelConverter.BytesPerPixel(format)` | `format.BytesPerPixel` |
| `new RasterImage(w, h, channels, sample, pixels, icc, color)` | `RasterImage.Wrap(w, h, channels, sample, pixels, color, metadata: new ImageMetadata { Icc = icc })` |
| `image.IccProfile` | `image.Metadata.Icc` (the ICC profile now travels with the Exif and XMP blocks) |
| `rasterImage.ExpandToRgba8(destination)` | `image.TryCopyTo(destination, 0, PixelFormat.Rgba32)` |
| `PixelConverter.ConvertRow(src, channels, sample, dst, ImagePixelFormat, n)` | the same call with a `PixelFormat`; or the new `ConvertRow(src, srcFormat, dst, dstFormat, n)` from any format to any format |
| `new ImageDecoderRegistration(format, canDecode, readInfo, decode, decodeInto[, openFrames])` | use the codec's `XxxImageDecoder.Registration` property; one built by hand is an object initializer: `new ImageDecoderRegistration { Format = …, CanDecode = …, … }` with the eight required 2.0 members |

Code that `switch`ed over `ImagePixelFormat` switches over `format.Layout` (`ChannelLayout.Rgba`, …) or compares with `==`. A method that took `ImagePixelFormat format = ImagePixelFormat.Rgba32` takes `PixelFormat? format = null`. `[InlineData(ImagePixelFormat.X)]` in tests becomes a name or an index: a struct is not an attribute constant.

## Behaviour that changed

- **`IDecodedImage` is `IDisposable`.** Dispose a decoded image when done; it matters only for pooled memory (`DecodeOptions.Pool`), but `using var image = …` is the habit to adopt. Reading `Pixels` after `Dispose` throws `ObjectDisposedException`.
- **HEIF Main 10 decodes to 16-bit samples** (`SampleFormat.UInt16`, `BitDepth` 10) instead of 8-bit. `TryDecodeInto` to an 8-bit format is unchanged (the frozen 8-bit conversion from the coded samples), so it can differ by one code value from narrowing the 16-bit image; `TryDecodeInto` to `Rgba64` gives the full precision.
- **New members on interfaces.** A third-party `IImageDecoder` implements `TryGetClosestSize`, `TryReadMetadata`, `TryDecodeThumbnail` (`ImageDecoderFallback` has one-line implementations for a codec without native support) and the `DecodeOptions?` / `CancellationToken` parameters of `TryDecode` / `TryDecodeInto`, and `Decode`, the throwing form of `TryDecode` (an `InvalidDataException` with the reason; `ImageDecoderFallback.TryDecode` then implements `TryDecode` over it). Set `Decode` in its `ImageDecoderRegistration` too; a registration without it still works, and the facade reports its failures without a reason. The helpers for codec authors (`ImageDecoderFallback`, `BufferedEncoderSession`, `DecodeOptions.Validate` / `Apply`) are in `Sdcb.SimdCodecs.Abstractions.CodecSupport`.
- **WebP with no encode options is lossy** (`WebpLossyEncodeOptions` defaults), through `WebpImageEncoder` and `ImageCodecs.Encode` / `CreateEncoder`; the 2.0 previews wrote lossless. Pass `new WebpLosslessEncodeOptions()` to keep lossless output.
- **An encoder given another format's options throws `ArgumentException`** (e.g. `JpegEncodeOptions` to WebP) instead of silently using its defaults. `null` still means the defaults; ICO and QOI take no options and accept only `null`.
- **Cancellation.** Every decode and encode entry point takes a `CancellationToken`; a cancelled token throws `OperationCanceledException` (it never makes a `Try` method return false).
- **`ImageInfo` reports more**: `ColorEncoding`, `Resolution`, `HasThumbnail`. Code comparing whole `ImageInfo` values (`info.ShouldBe(new ImageInfo(...))`) sees the resolution a file declares (a TIFF's 96 dpi, a JPEG's JFIF density).

## New in 2.0

- 16-bit and premultiplied output: `PixelFormat.Rgba64`, `Gray16`, `Bgra32Premultiplied`, … in every `TryDecodeInto`.
- `DecodeOptions`: region, scaled decode (any power of two for every codec: JPEG 1/2–1/8 and JPEG 2000 resolution levels natively, the rest by a frozen box filter after the native decode), pooled memory, `ApplyOrientation` (EXIF / TIFF orientation applied, replacing hand-written rotation; `GetOutputSize(info)` gives the displayed size, and `PixelTransform.Orient` turns pixels already decoded); `TryGetClosestSize` negotiation (native sizes only).
- Metadata: `TryReadMetadata` (Exif, XMP, ICC as raw blocks), `TryDecodeThumbnail`, `IDecodedImage.Metadata`. A JPEG's XMP has its Extended XMP (the part of a packet over 64 KB that sits in further APP1 segments) joined into the standard packet: the extended `rdf:RDF` content is inserted before the standard packet's `</rdf:RDF>`, and the standard packet comes back unchanged when the extension is incomplete or fails its MD5.
- Failures with a reason: `ImageCodecs.Decode(data)` throws `InvalidDataException` naming the format and the codec's reason (`TryDecode` still returns false), and `NotSupportedException` for data no codec takes, naming what `DetectFormat` saw (HEIF: install and register `Sdcb.SimdCodecs.Heif`). `ImageDecoder.Open` gives the same messages.
- `ImageCodecs.Decode(data, PixelFormat.Rgba32)`: header, buffer and decode in one call, to a `PixelBuffer` in any `PixelFormat` (BGR and premultiplied included, which an `IDecodedImage` cannot hold) with the file's metadata.
- `ImageDecoder` / `ImageFrame`: a session over a `Stream`, a file (memory-mapped) or memory, with `OpenAsync`.
- `IImageEncoder` and `ImageCodecs.Encode` / `CreateEncoder`: every encoder through one API, every `PixelFormat` in.
- `IPlanarImageDecoder`: YCbCr planes as coded (HEIF).

The design and its reasons are in [api-design.md](api-design.md).
