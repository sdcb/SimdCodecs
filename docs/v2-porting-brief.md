# Porting a codec to Abstractions 2.0 (brief for sub-agents)

Branch `v2`. Abstractions 2.0 core is in `src/Sdcb.SimdCodecs.Abstractions` (already done; **do not edit it**;
if you think it needs a change, say so in your report instead). The design is `docs/abstractions-2.0-design.md`.
The reference port is **`src/Sdcb.SimdCodecs.Qoi`** (`QoiImageDecoder.cs`, `QoiDecoder.cs`, `QoiEncoder.cs`) —
read it first and follow its pattern.

## What changed in Abstractions

- `ImagePixelFormat` (enum, 5 values) is **deleted**. Use `PixelFormat` (readonly record struct):
  `PixelFormat.Rgba32 / Bgra32 / Rgb24 / Bgr24 / Gray8` replace the old values one for one; there are also
  `GrayAlpha16`, 16-bit (`Rgba64`, `Bgra64`, `Rgb48`, `Bgr48`, `Gray16`, `GrayAlpha32`) and premultiplied
  (`Rgba32Premultiplied`, …) formats. Members: `Layout` (`ChannelLayout`), `Sample`, `Alpha`, `Channels`,
  `BytesPerPixel`, `HasAlpha`, `IsGray`, `IsBgr`, `IsPremultiplied`, `IsBasic8` (true for exactly the 5 old
  formats), `To8Bit()`, `ForChannels(channels, sample)`.
  - `switch` over the old enum → `if (format == PixelFormat.X)` chains, or switch on `format.Layout` when
    `format.IsBasic8`. `format is ImagePixelFormat.Rgba32 or ImagePixelFormat.Bgra32` → `format.IsBasic8 && format.HasAlpha`
    (or `format == PixelFormat.Rgba32 || format == PixelFormat.Bgra32`).
  - A **default parameter value** `ImagePixelFormat format = ImagePixelFormat.Rgba32` cannot be a struct
    constant: use `PixelFormat? format = null` and `var f = format ?? PixelFormat.Rgba32;` (see `QoiDecoder.DecodeTo`).
- `PixelConverter`:
  - `BytesPerPixel(format)` is gone → `format.BytesPerPixel`.
  - `TryGetLayout(w, h, PixelFormat, stride, len, out rowStride)` — same semantics.
  - `ConvertRow(src, channels, SampleFormat, dst, PixelFormat, width)` still exists (source described as
    1-4 channels) **and** a new `ConvertRow(src, PixelFormat srcFormat, dst, PixelFormat dstFormat, width)`
    converts any format to any format (16↔8 bit, BGR, grey+alpha, premultiply/unpremultiply), in place allowed
    when pixel sizes are equal.
  - `Convert(src, srcStride, srcFormat, dst, dstStride, dstFormat, w, h)`, `ToPacked(...)` → new byte[] in a format.
  - `TryConvert(IDecodedImage, dst, stride, PixelFormat)` — same semantics.
- `IDecodedImage` is now `IDisposable` and has `BitDepth`, `Metadata`, `ToRgba16()`, `TryCopyTo(dst, stride, PixelFormat)`
  (default interface implementations exist for all but `Dispose`).
- `RasterImage` has **no public constructor**. Use the factories:
  - `RasterImage.Wrap(width, height, channels, sampleFormat, byte[] pixels, byte[]? icc = null, ColorEncoding? color = null, int bitDepth = 0, ImageMetadata? metadata = null)` — what `new RasterImage(...)` was, plus bit depth & metadata.
  - `RasterImage.Create(w, h, channels, sample, ArrayPool<byte>? pool, icc, color, bitDepth, metadata)` → fill `raster.WritablePixels`.
  - `RasterImage.Crop(image, PixelRect, pool)`.
  - `ExpandToRgba8(Span<byte>)` is gone: use `image.TryCopyTo(dst, 0, PixelFormat.Rgba32)` or `PixelConverter.ConvertRow`.
  Pass `bitDepth:` when the file's real precision is below the container (e.g. 10/12-bit in UInt16); 0 otherwise.
- `IImageDecoder` (all `static abstract`) — every adapter implements all of:
  ```csharp
  static int SignatureLength { get; }
  static bool CanDecode(ReadOnlySpan<byte> header);
  static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info);
  static bool TryGetClosestSize(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight, out int width, out int height, out int scaleDenominator);
  static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image, DecodeOptions? options = null, CancellationToken cancellationToken = default);
  static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format, DecodeOptions? options = null, CancellationToken cancellationToken = default);
  static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata);
  static bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail);
  ```
  Plus a public static `Registration` property (`ImageDecoderRegistration`), built from method groups
  (see QOI; multi-frame codecs pass `TryOpenFrames` as the 9th argument).
  Helpers in `ImageDecoderFallback`: `TryGetStoredSize` (codec without native scaling),
  `TryDecodeInto(TryDecode, …)` (generic decode-then-convert), `TryDecodeFrameInto(reader, …)`,
  `TryReadNoMetadata` (format without metadata), `NoThumbnail`.
- `DecodeOptions` (`ScaleDenominator` 1/2/4/8, `Region` (`PixelRect?`), `Pool`): a codec without native
  scaling/region calls `options?.Validate(w, h, supportsScale: false)` before decoding and
  `DecodeOptions.Apply(image, options)` after (crops for a region). In `TryDecodeInto`, if
  `options is { IsFullImage: false }` fall back to `ImageDecoderFallback.TryDecodeInto(TryDecode, …)`.
  **Do not catch** `NotSupportedException`/`ArgumentException` thrown by `Validate` — let them propagate
  (be careful with existing `catch (Exception e) when (e is … ArgumentException …)`: call `Validate` **outside** the try).
- `CancellationToken`: thread it into the codec's main decode loop and check
  `cancellationToken.ThrowIfCancellationRequested()` at **coarse** points only (every 64 rows, every MCU row,
  every tile/strip, every frame) — never in inner per-pixel loops. Let `OperationCanceledException`
  propagate (don't swallow it in `catch` blocks).
- `ImageInfo` gained init-only `ColorEncoding`, `Resolution` (`Resolution(double X, double Y, ResolutionUnit)`)
  and `HasThumbnail`. Fill `ColorEncoding` when the codec already knows it from the header (the same value the
  decoded image reports). Resolution / thumbnails / metadata extraction are a **later** milestone unless your
  task says otherwise — for now `TryReadMetadata` may return what the codec already exposes (ICC) and
  `TryDecodeThumbnail` → `ImageDecoderFallback.NoThumbnail`.
- `IImageFrameReader.TryDecodeFrame(int, out IDecodedImage?, DecodeOptions? = null, CancellationToken = default)`
  and `TryDecodeFrameInto(int, Span<byte>, int stride, PixelFormat, DecodeOptions? = null, CancellationToken = default)`.
- Encoders' public APIs that took `ImagePixelFormat` now take `PixelFormat` and must accept **any** `PixelFormat`
  (convert what they can't take natively with `PixelConverter.ConvertRow(src, srcFormat, dst, nativeFormat, n)` a
  row/chunk at a time — see `QoiEncoder.Write`). An 8-bit-only encoder converts 16-bit input with `>> 8` (that is
  what `PixelConverter` does); premultiplied input is unpremultiplied by the same call.

## Hard rules (from AGENTS.md — non-negotiable)

- **Existing output must not change by a single byte** for the 5 old formats: the native fast paths stay as they
  are; only their types change. Golden files (`tests/**/Fixtures/*-golden.tsv`) must not change.
- Never read, search for or paraphrase the source of ImageSharp, libjpeg(-turbo), libpng, libwebp, libheif,
  libde265, FFmpeg or any GPL code. Do not search the web.
- No new compiler warnings. Every public member keeps an English XML doc comment.
- Untrusted input → `false` from `Try*` (or `InvalidDataException` from throwing APIs); never a hang, unbounded
  allocation, `OverflowException`, `OutOfMemoryException`.
- Every vector kernel keeps a scalar twin with identical output.
- Do **not** `git commit`, `git push`, or change branches. Do not edit files outside your task's scope.

## Building and testing on this machine

- `dotnet test` does **not** work on this machine (permission issue). Do not use it; do not investigate it.
- Build one package: `dotnet build src/Sdcb.SimdCodecs.<Pkg> -c Release` (it builds its dependencies too).
- The test project and facade will not compile until every package is ported; your task says which build is
  your acceptance gate.
