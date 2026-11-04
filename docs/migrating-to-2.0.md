# Migrating to 2.0

2.0 makes all its breaking changes to `Sdcb.SimdCodecs.Abstractions` and the facade at once, so that 2.x can add
without breaking again. Most code needs a rename and nothing else. Every 8-bit decode output is byte-for-byte what
1.x produced; the decoded pixels change only where 2.0 keeps precision 1.x threw away (HEIF Main 10, see below).

## Renames

| 1.x | 2.0 |
|---|---|
| `ImagePixelFormat` (enum) | `PixelFormat` (readonly record struct): `PixelFormat.Rgba32`, `.Bgra32`, `.Rgb24`, `.Bgr24`, `.Gray8` are the same layouts |
| `PixelConverter.BytesPerPixel(format)` | `format.BytesPerPixel` |
| `new RasterImage(w, h, channels, sample, pixels, icc, color)` | `RasterImage.Wrap(w, h, channels, sample, pixels, icc, color)` |
| `rasterImage.ExpandToRgba8(destination)` | `image.TryCopyTo(destination, 0, PixelFormat.Rgba32)` |
| `PixelConverter.ConvertRow(src, channels, sample, dst, ImagePixelFormat, n)` | the same call with a `PixelFormat`; or the new `ConvertRow(src, srcFormat, dst, dstFormat, n)` from any format to any format |
| `new ImageDecoderRegistration(format, canDecode, readInfo, decode, decodeInto[, openFrames])` | use the codec's `XxxImageDecoder.Registration` property; building one by hand takes the eight 2.0 members |

Code that `switch`ed over `ImagePixelFormat` switches over `format.Layout` (`ChannelLayout.Rgba`, …) or compares with
`==`. A method that took `ImagePixelFormat format = ImagePixelFormat.Rgba32` takes `PixelFormat? format = null`.
`[InlineData(ImagePixelFormat.X)]` in tests becomes a name or an index: a struct is not an attribute constant.

## Behaviour that changed

- **`IDecodedImage` is `IDisposable`.** Dispose a decoded image when done; it matters only for pooled memory
  (`DecodeOptions.Pool`), but `using var image = …` is the habit to adopt. Reading `Pixels` after `Dispose` throws
  `ObjectDisposedException`.
- **HEIF Main 10 decodes to 16-bit samples** (`SampleFormat.UInt16`, `BitDepth` 10) instead of 8-bit. `TryDecodeInto`
  to an 8-bit format is unchanged (the frozen 8-bit conversion from the coded samples), so it can differ by one code
  value from narrowing the 16-bit image; `TryDecodeInto` to `Rgba64` gives the full precision.
- **New members on interfaces.** A third-party `IImageDecoder` implements `TryGetClosestSize`, `TryReadMetadata`,
  `TryDecodeThumbnail` (`ImageDecoderFallback` has one-line implementations for a codec without native support) and
  the `DecodeOptions?` / `CancellationToken` parameters of `TryDecode` / `TryDecodeInto`.
- **Cancellation.** Every decode and encode entry point takes a `CancellationToken`; a cancelled token throws
  `OperationCanceledException` (it never makes a `Try` method return false).
- **`ImageInfo` reports more**: `ColorEncoding`, `Resolution`, `HasThumbnail`. Code comparing whole `ImageInfo` values
  (`info.ShouldBe(new ImageInfo(...))`) sees the resolution a file declares (a TIFF's 96 dpi, a JPEG's JFIF density).

## New in 2.0

- 16-bit and premultiplied output: `PixelFormat.Rgba64`, `Gray16`, `Bgra32Premultiplied`, … in every `TryDecodeInto`.
- `DecodeOptions`: region, scaled decode (JPEG 1/2–1/8), pooled memory; `TryGetClosestSize` negotiation.
- Metadata: `TryReadMetadata` (Exif, XMP, ICC as raw blocks), `TryDecodeThumbnail`, `IDecodedImage.Metadata`.
- `ImageDecoder` / `ImageFrame`: a session over a `Stream`, a file (memory-mapped) or memory, with `OpenAsync`.
- `IImageEncoder` and `SimdCodecs.Encode` / `CreateEncoder`: every encoder through one API, every `PixelFormat` in.
- `IPlanarImageDecoder`: YCbCr planes as coded (HEIF).

The design and its reasons are in [api-design.md](api-design.md).
