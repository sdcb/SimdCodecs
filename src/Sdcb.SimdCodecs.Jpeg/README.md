# Sdcb.SimdCodecs.Jpeg

Pure-managed JPEG (ITU-T T.81) decoder and encoder for .NET 10. Baseline sequential and progressive DCT, restart intervals, all chroma subsampling layouts, grayscale, YCbCr, RGB-marked, and Adobe CMYK / YCCK streams. Output is 8-bit RGBA by default, or BGRA / RGB at any row stride.

```csharp
using Sdcb.SimdCodecs.Jpeg;

byte[] data = File.ReadAllBytes("photo.jpg");
JpegImage image = JpegDecoder.Decode(data);
Console.WriteLine($"{image.Width} × {image.Height}");
```

Decode into a caller-supplied buffer — no heap allocation in the decoder itself when combined with a pooled destination:

```csharp
JpegInfo info = JpegDecoder.ReadInfo(data);
byte[] rgba = new byte[info.Width * info.Height * 4];
JpegDecoder.DecodeTo(data, rgba);
```

Scaled decode (`JpegScale.Half` and below) runs a reduced inverse DCT so neither the full-resolution sample planes nor the full-resolution output ever exist — useful for decoding large scans straight to thumbnail size:

```csharp
JpegDecoder.DecodeTo(data, rgba, JpegScale.Quarter);
```

Encode to a baseline JPEG:

```csharp
byte[] jpeg = JpegEncoder.Encode(rgba, width, height, quality: 85);
```

`JpegEncodeOptions` selects chroma subsampling, optimised Huffman tables, progressive mode, and an optional Exif blob.

## JPEG inside another container

A container that stores JPEG streams can carry their tables apart from them and say itself what the components mean, as TIFF does (JPEGTables and PhotometricInterpretation, TIFF Technical Note 2). `JpegDecodeOptions` takes both:

```csharp
var options = new JpegDecodeOptions
{
    Tables = jpegTables,                        // SOI, DQT / DHT ..., EOI (T.81 B.5 abbreviated format)
    ColorTransform = JpegColorTransform.None,   // Auto (default) | None | YCbCr
};
JpegDecoder.DecodeTo(strip, destination, stride, JpegPixelFormat.Rgb24, options, JpegScale.Full, cancellationToken);
```

`Auto` is the stream's own signal (JFIF, the Adobe APP14 transform, component IDs `R G B`). `None` writes the components as they are: three as R, G, B (an RGB-coded stream whose IDs are 1, 2, 3 would otherwise be taken for YCbCr), four as R, G, B, A with the fourth in A. `YCbCr` converts from Y, Cb, Cr (or YCCK) whatever the markers say. Only which conversion runs is selected; the conversions are the frozen arithmetic below, and a stream decoded with its tables supplied equals the stream with them spliced in.

## Allocation contract

`JpegDecoder.DecodeTo` into a caller-supplied buffer allocates nothing on the managed heap (`JpegAllocationTests` enforces this). All intermediate state is kept in a per-thread `DecoderState` cache that is rented at the start and returned at the end of every decode call. The cache is fully reset on every rent — any new field added to `DecoderState`, `JpegHuffman`, or `Component` must be initialised in the corresponding `Reset()` method, not in a constructor, or the cache will carry stale state across calls.

## Arithmetic contracts

The decoder's arithmetic is frozen:

- **`JpegIdct`** — single-precision T.81 IDCT; no FMA, no reassociation.
- **`JpegResample`** — 16×-scale unrounded linear chroma interpolation.
- **`JpegColor`** — T.871 YCbCr → RGB / RGBA conversion.

Every ISA tier (Vector256 / Vector128 / scalar) and every fast path reproduces the same operation sequence bit for bit. A change to any of these three classes requires passing `accuracy --gate` first and lands as its own commit with the gate table in the message.

The encoder's contract is quality, not bytes: `encode --gate` must pass, and optimised / progressive streams decode to exactly the baseline's pixels. Encoder kernels (`JpegEncoderKernels`) are `NoInlining | AggressiveOptimization` and produce identical output on every ISA tier (`JpegEncoderEquivalenceTests`).
