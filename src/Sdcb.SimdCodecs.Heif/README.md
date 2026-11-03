# Sdcb.SimdCodecs.Heif

Pure-managed HEIF / HEIC decoder for .NET 10. Reads ISOBMFF containers
(ISO/IEC 14496-12), the HEIF item model (ISO/IEC 23008-12 / MIAF 23000-22),
and decodes HEVC intra frames (ITU-T H.265 Main and Main 10 profiles) — the
encoding used by every modern Android and iPhone camera.

> **Patent notice.** HEVC (H.265) is patent-encumbered. This package is MIT
> and conveys **no patent licence**. Users are responsible for evaluating HEVC
> patent obligations in their jurisdiction before deploying this package.

This package is **not** included in `Sdcb.SimdCodecs`. Install it explicitly
and register the decoder when your application starts:

```csharp
using Sdcb.SimdCodecs.Heif;

// Register once at startup (not thread-safe during registration).
HeifImageDecoder.Register();
```

Then decode through the shared facade:

```csharp
using Sdcb.SimdCodecs;

byte[] data = File.ReadAllBytes("photo.heic");
if (SimdCodecs.TryDecode(data, out IDecodedImage? image))
    Console.WriteLine($"{image.Width} × {image.Height}");
```

Or directly through the HEIF API:

```csharp
using Sdcb.SimdCodecs.Heif;

byte[] data = File.ReadAllBytes("photo.heic");
HeifImage image = HeifDecoder.Decode(data);
Console.WriteLine($"{image.Width} × {image.Height}  {image.BitDepth}-bit");
```

## What is supported

- HEIC still images (`ftyp` major brand `heic` / `heix`, compatible brand `mif1`)
- Tiled images (`grid` item type) — the layout used by all phone cameras
- HEVC Main profile (8-bit) and Main 10 profile (10-bit)
- `ispe` (image dimensions), `hvcC` (HEVC decoder configuration)
- `colr` box: nclx colour signalling and ICC profile passthrough
- `irot` / `imir` orientation applied at decode time
- Exif orientation (`cdsc` auxiliary item) applied at decode time

## What is not yet supported

- HEIC image sequences and Live Photos (`heics`)
- Alpha auxiliary images (`auxC` / `urn:mpeg:hevc:2015:auxid:1`)
- Apple HDR gain map auxiliary images
- Depth maps and other auxiliary item types
- AVIF (AV1-coded items) — use `Sdcb.SimdCodecs.Avif` when available

## Output contract

HEVC decoding is integer-arithmetic and deterministic: the Y / Cb / Cr planes
equal a pinned reference decoder's output bit-for-bit (oracle version recorded
in `Oracle/README.md`). The YCbCr → RGB conversion step is defined in-repo
from the `colr` / nclx parameters and frozen like `JpegColor`; every ISA tier
(Vector256 / Vector128 / scalar) produces identical output.
