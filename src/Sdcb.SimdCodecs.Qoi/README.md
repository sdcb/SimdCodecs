# Sdcb.SimdCodecs.Qoi

Pure-managed QOI ("Quite OK Image") decoding and encoding for .NET 10, written from the specification at [qoiformat.org](https://qoiformat.org/qoi-specification.pdf). It decodes straight into the caller's buffer in any 8-bit pixel layout without allocating, and it encodes to an array or a stream.

```csharp
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Qoi;

byte[] data = File.ReadAllBytes("image.qoi");
QoiInfo info = QoiDecoder.ReadInfo(data);       // size, channels, colour space
QoiImage image = QoiDecoder.Decode(data);       // RGB or RGBA, as the header declares

var bgra = new byte[info.Width * info.Height * 4];
QoiDecoder.DecodeTo(data, bgra, PixelFormat.Bgra32);
```

Malformed data throws `InvalidDataException`, and the `Try` methods and the `QoiImageDecoder` adapter return `false` instead. Images above 2^28 pixels are refused, which bounds what a decode allocates.

## Encoding

```csharp
byte[] qoi = QoiEncoder.Encode(rgba, width, height, PixelFormat.Rgba32);

using var file = File.Create("image.qoi");
QoiEncoder.Encode(bgr, width, height, PixelFormat.Bgr24, file, QoiColorSpace.Linear);
```

The encoder takes RGBA, BGRA, RGB, BGR or grey input at any row stride. RGBA and BGRA input is written with 4 channels; the others are written with 3. For each pixel the encoder uses the first chunk that fits, trying them in this order: run, index, diff, luma, RGB, RGBA.

## Streaming

`QoiImageDecoder.CreateRowReader` decodes from a forward-only stream (a network or request body that cannot seek), a band of rows at a time, top to bottom, synchronously (`ReadRows`) or asynchronously (`ReadRowsAsync`). It holds the decoder state and about one row of the file, and its rows are exactly those `TryDecodeInto` writes. A row is decoded once all of its chunks have arrived; a truncated stream throws `InvalidDataException`. Scaling, cropping or orientation options read the stream to its end and decode it whole (`IsStreaming` is false).

```csharp
using var reader = QoiImageDecoder.CreateRowReader(new StreamInput(requestBody), PixelFormat.Rgba32, null);
await reader.InitializeAsync();
var band = new byte[reader.Width * 4 * 16];
int rows;
while ((rows = await reader.ReadRowsAsync(band, 16)) > 0)
    Consume(band, rows);
```

`QoiImageEncoder.Create` returns a streaming session: `AddFrame` writes the header, each `WriteRows` band is encoded and written to the output as it arrives (a run goes on across bands), and `Commit` writes the end marker. The bytes are those `QoiEncoder.Encode` writes for the whole image, however the rows are banded.
## Decisions

The specification leaves these cases open. The table shows what this package does:

| Case | This package |
|---|---|
| The colour space byte | It is reported (`QoiInfo.ColorSpace`; through the facade, `ColorEncoding.Transfer` is `Linear` for 1). It never changes the decoded values. |
| A 3-channel image whose chunks carry alpha (an RGBA chunk, or an index slot that was never filled) | The alpha is ignored: every pixel is opaque, as the header declares. |
| A run longer than the pixels left | The image ends and the rest of the run is ignored. |
| The 8-byte end marker | It is not checked. The stream must contain every pixel, and data after the last chunk is ignored. |
| Width or height 0, channels other than 3 or 4, colour space above 1 | Refused. |
