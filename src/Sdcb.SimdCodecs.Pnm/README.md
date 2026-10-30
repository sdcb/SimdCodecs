# Sdcb.SimdCodecs.Pnm

Pure-managed Netpbm decoding and encoding for .NET 10, written from the Netpbm format pages and the PFM
description. It decodes straight into the caller's buffer in any 8-bit pixel layout without allocating, or to the
file's native 8-bit, 16-bit or float samples. Its row kernels use 256-bit and 128-bit vectors, and each kernel has
a scalar version that gives the same bytes.

```csharp
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Pnm;

byte[] data = File.ReadAllBytes("image.ppm");
PnmInfo info = PnmDecoder.ReadInfo(data);       // size, format, channels, maximum value
PnmImage image = PnmDecoder.Decode(data);       // 8-bit, 16-bit or float samples, as the file holds them

var bgra = new byte[info.Width * info.Height * 4];
PnmDecoder.DecodeTo(data, bgra, ImagePixelFormat.Bgra32);
```

The decoder reads these formats:

| Magic | Format | Samples |
|---|---|---|
| P1 / P4 | PBM, plain / raw | 1 bit; 1 is black |
| P2 / P5 | PGM, plain / raw | Grey; maximum 1 to 65535 (raw: 1 byte up to 255, 2 bytes big-endian above) |
| P3 / P6 | PPM, plain / raw | RGB, same maxima |
| P7 | PAM | 1 to 4 channels (grey, grey + alpha, RGB, RGBA) by `DEPTH`, same maxima |
| `Pf` / `PF` | PFM | 32-bit float grey / RGB |

Malformed data throws `InvalidDataException`. The `Try` methods and the facade adapters (`PnmImageDecoder`,
`PfmImageDecoder`) return `false` instead. Images above 2^28 pixels are refused, which bounds what a decode
allocates.

## Encoding

```csharp
byte[] ppm = PnmEncoder.Encode(rgb, width, height, ImagePixelFormat.Rgb24);           // P6
byte[] pam = PnmEncoder.Encode(rgba, width, height, ImagePixelFormat.Rgba32);         // P7, RGB_ALPHA
byte[] plain = PnmEncoder.Encode(rgb, width, height, ImagePixelFormat.Rgb24,
    new PnmEncodeOptions { Format = PnmFormat.PlainGraymap, MaxValue = 1023 });       // P2
byte[] deep = PnmEncoder.Encode(samples16, width, height, channels: 3);               // P6, maximum 65535
byte[] pfm = PnmEncoder.EncodePfm(floats, width, height, channels: 3);                // PF
```

Every format can be written, plain or raw. The input can be any 8-bit layout at any stride, 8-bit or 16-bit samples
with 1 to 4 channels, or float samples for PFM.

How the encoder converts the input to the chosen format:
- A PBM or PGM takes the input's luma. A PBM pixel is black when the luma is below 128.
- A PPM drops alpha.
- A PAM keeps the channels it is given.

Plain output keeps every line within 70 characters and starts each image row on a new line. A PFM is written
little-endian, which is scale −1, with its rows bottom-up as the format stores them. The layout, 16-bit and PFM
overloads also have a `Stream` version that writes through one pooled buffer.

## Decisions

The format pages leave these cases open. The table shows what this package does:

| Case | This package |
|---|---|
| A maximum value `m` other than 255 or 65535 | Samples scale to the full range with rounding: `(v·255 + m/2) / m` for `m` up to 255, and `(v·65535 + m/2) / m` above, which gives 16-bit samples. An 8-bit layout takes a 16-bit sample's high byte, as `ImagePixelFormat` defines. |
| A sample above the maximum | It counts as the maximum, in raw and plain rasters alike. |
| A PAM tuple type | `DEPTH` alone decides the channels: 1 is grey, 2 grey + alpha, 3 RGB, 4 RGBA. The tuple type is reported (`PnmInfo.TupleType`), and unknown types are accepted. `BLACKANDWHITE` needs no special case, because at maximum 1 its 1 is already white. |
| A PAM depth above 4 | Refused. |
| PFM's scale | Its sign gives the byte order. Its magnitude is reported (`PnmInfo.Scale`) but never applied. Samples are returned as stored, with a linear, scene-referred `ColorEncoding`. |
| PFM to an 8-bit layout | Not done, because float samples have no 8-bit form without a tone policy. `DecodeTo` throws `NotSupportedException` and `TryDecodeInto` returns false. `PixelConverter.StretchToRgba8` makes a viewable image. |
| Several images in one file | Only the first is read. Bytes after it are ignored. |
| Comments | Accepted wherever whitespace is, in the header and between plain samples. P1 samples may run together (`0101`). |
