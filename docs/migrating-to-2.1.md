# Migrating to 2.1

2.1 is an additive release for applications using the 2.0 facade and buffered codec APIs. Existing byte-array, memory, path, and buffered stream calls keep their 2.0 behavior. The new APIs are useful when an image arrives or leaves through a forward-only stream and the application wants rows and output bytes to flow incrementally.

## Reading rows from a stream

Use `ImageCodecs.OpenRowReader` or `OpenRowReaderAsync` with a `PixelFormat`. Read rows into a caller-owned buffer with `ReadRows` or `ReadRowsAsync`, and dispose the reader when the frame is complete.

```csharp
await using IImageRowReader reader = await ImageCodecs.OpenRowReaderAsync(request.Body, PixelFormat.Rgba32);
byte[] rows = new byte[reader.Width * reader.Format.BytesPerPixel * 16];
for (int count; (count = await reader.ReadRowsAsync(rows, 16)) > 0;)
    await ProcessRowsAsync(rows.AsMemory(0, count * reader.Width * reader.Format.BytesPerPixel));
```

`IsStreaming` reports whether the selected codec and options decode as input arrives. A false value means the same reader contract is provided after buffering and decoding the complete input. Progressive JPEG, interlaced or animated PNG, scaling, cropping, orientation, and other format-specific options can require this fallback.

## Writing rows to a stream

Use `ImageCodecs.CreateEncoder` to create a `StreamEncoderSession`, add a frame, write its rows, and commit the file.

```csharp
await using StreamEncoderSession session = ImageCodecs.CreateEncoder(ImageFormat.Png, response.Body);
StreamFrameEncoder frame = session.AddFrame(width, height, PixelFormat.Rgba32);
await frame.WriteRowsAsync(rows, rowCount);
await session.CommitAsync();
```

`IImageEncoderSession.IsStreaming` reports whether bytes are emitted while rows are written. Options that require a complete image continue to buffer and write at `Commit` behind the same API. Always call `Commit` or `CommitAsync` after the final row.

## ImageDecoder stream sessions

`ImageDecoder.Open(stream, leaveOpen, forwardOnly: true)` and its asynchronous counterpart allow a single forward-only decode without retaining the input for later operations. The default stream session can retain bytes when a later frame, metadata, or decode operation may need them. Code that revisits frames should keep the default behavior or use a seekable stream.

## Codec registrations

Custom `ImageDecoderRegistration` values remain valid without a `CreateRowReader` delegate. The facade supplies a buffered row reader in that case. Codec packages that implement `IStreamingImageDecoder` should set the delegate to their concrete `CreateRowReader` method.

## Validation and errors

Streaming rows are byte-for-byte equivalent to `TryDecodeInto` for the same input, format, and options. Truncated or malformed input throws `InvalidDataException`, and cancelled operations throw `OperationCanceledException`, matching the existing decode and encode contracts.

See [RELEASE_NOTES.md](../RELEASE_NOTES.md), [docs/known-gaps.md](known-gaps.md), and each codec package README for the variants that stream and the variants that use the buffered fallback.
