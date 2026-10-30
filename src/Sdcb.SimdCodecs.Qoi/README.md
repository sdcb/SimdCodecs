# Sdcb.SimdCodecs.Qoi

Pure-managed QOI ("Quite OK Image") decoding and encoding for .NET 10, written from the specification at
[qoiformat.org](https://qoiformat.org/qoi-specification.pdf). It decodes straight into the caller's buffer in any
8-bit pixel layout without allocating, and it encodes to an array or a stream.

```csharp
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Qoi;

byte[] data = File.ReadAllBytes("image.qoi");
QoiInfo info = QoiDecoder.ReadInfo(data);       // size, channels, colour space
QoiImage image = QoiDecoder.Decode(data);       // RGB or RGBA, as the header declares

var bgra = new byte[info.Width * info.Height * 4];
QoiDecoder.DecodeTo(data, bgra, ImagePixelFormat.Bgra32);
```

Malformed data throws `InvalidDataException`, and the `Try` methods and the `QoiImageDecoder` adapter return
`false` instead. Images above 2^28 pixels are refused, which bounds what a decode allocates.

## Encoding

```csharp
byte[] qoi = QoiEncoder.Encode(rgba, width, height, ImagePixelFormat.Rgba32);

using var file = File.Create("image.qoi");
QoiEncoder.Encode(bgr, width, height, ImagePixelFormat.Bgr24, file, QoiColorSpace.Linear);
```

The encoder takes RGBA, BGRA, RGB, BGR or grey input at any row stride. RGBA and BGRA input is written with 4
channels; the others are written with 3. For each pixel the encoder uses the first chunk that fits, trying them
in this order: run, index, diff, luma, RGB, RGBA.

## Decisions

The specification leaves these cases open. The table shows what this package does:

| Case | This package |
|---|---|
| The colour space byte | It is reported (`QoiInfo.ColorSpace`; through the facade, `ColorEncoding.Transfer` is `Linear` for 1). It never changes the decoded values. |
| A 3-channel image whose chunks carry alpha (an RGBA chunk, or an index slot that was never filled) | The alpha is ignored: every pixel is opaque, as the header declares. |
| A run longer than the pixels left | The image ends and the rest of the run is ignored. |
| The 8-byte end marker | It is not checked. The stream must contain every pixel, and data after the last chunk is ignored. |
| Width or height 0, channels other than 3 or 4, colour space above 1 | Refused. |
