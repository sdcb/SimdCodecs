# 2.1.0

2.1 adds streaming image input and output for request bodies, pipes, and other streams that cannot be rewound. The release keeps the 2.0 facade and codec behavior while adding row readers, asynchronous frame operations, and encoder sessions that report whether the selected codec and options are truly streaming.

## Highlights

- `ImageCodecs.OpenRowReader` and `OpenRowReaderAsync` expose `IImageRowReader`, which reads decoded rows top to bottom and supports synchronous or asynchronous consumers.
- `ImageCodecs.CreateEncoder` exposes `StreamEncoderSession` and `StreamFrameEncoder`, with `WriteRowsAsync` and `CommitAsync` for back-pressure-friendly output.
- JPEG, PNG, WebP, BMP, TIFF, QOI, and Netpbm have streaming row-reader paths for the variants documented by each package. PNG, JPEG, BMP, TIFF, QOI, and Netpbm also have streaming encoder paths for their supported options.
- Variants that need the complete image, such as progressive JPEG, interlaced or animated PNG, scaled or oriented reads, palette quantization, and other format-specific cases use the same API with a buffered fallback. Inspect `IsStreaming` when memory behavior matters.
- `ImageDecoder.Open` and `OpenAsync` can decode frame zero from a forward-only stream as rows arrive. Use `forwardOnly: true` when the stream must not retain bytes for later frame operations.
- The streaming implementation has dedicated equivalence, cancellation, truncation, async-only, facade, and benchmark coverage.

## Compatibility

The existing 2.0 decode, encode, metadata, frame, and facade entry points remain available. The new public members are recorded in each package's public API baseline. Applications that only use the existing byte-array and buffered stream methods do not need source changes.

Third-party codec registrations can omit `CreateRowReader`. The facade then provides a buffered row reader. Codec authors implementing the new streaming abstractions should expose the concrete static method through `ImageDecoderRegistration.CreateRowReader` so NativeAOT and browser-wasm keep the same delegate binding rules.

## Known limitations

Streaming support depends on the image variant and options. Read [docs/known-gaps.md](docs/known-gaps.md) and the package README before relying on bounded memory. TIFF streaming requires a seekable input for the layouts that use directory offsets. HEIF remains an opt-in decode-only package because HEVC patent obligations are outside this project.

## Validation

The release gate is `dotnet build SimdCodecs.slnx -c Release`, `REQUIRE_ORACLES=1 dotnet test SimdCodecs.slnx -c Release`, the SIMD-tier matrix, and the NativeAOT and browser-wasm smoke tests. Benchmark records for the streaming paths are under `benchmarks/*/results/`.
