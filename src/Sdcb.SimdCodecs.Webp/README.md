# Sdcb.SimdCodecs.Webp

Pure-managed WebP decoding for .NET 10, derived from RFC 6386 and RFC 9649.
There is no encoder.

Supported input includes VP8 lossy, VP8L lossless, ALPH transparency, and
animated WebP with full-canvas RGBA frames, random seeking, and frame timing.
ICC, EXIF, and XMP metadata are preserved. The `Sdcb.SimdCodecs` facade
decodes a still image or frame 0 of an animation with `TryDecode`, and every
frame with `TryOpenFrames`; this package's animation API adds seeking by
timestamp and the frame rectangles. Images larger than 67,108,864 pixels
are rejected to bound decoder allocations.

```csharp
using Sdcb.SimdCodecs.Webp;

byte[] data = File.ReadAllBytes("image.webp");
WebpImage image = WebpDecoder.Decode(data);
Console.WriteLine($"{image.Width} × {image.Height}");
```

Decode into your own RGBA buffer (`stride` 0 = tightly packed). A tightly packed
destination is also the lossless decoder's working buffer, so a lossless decode
needs almost no memory of its own:

```csharp
WebpInfo info = WebpDecoder.ReadInfo(data);
byte[] rgba = new byte[info.Width * info.Height * 4];
WebpDecoder.DecodeTo(data, rgba);
```

Decoding is single-threaded by default. Opt in to decoding the transparency
plane of a lossy image on a second thread, concurrently with the colour image:

```csharp
WebpDecoder.DecodeTo(data, rgba, 0, new WebpDecodeOptions { Multithreaded = true });
```

Decode every frame into a full-canvas RGBA buffer:

```csharp
WebpAnimation animation = WebpDecoder.DecodeAnimation(data);
foreach (WebpFrame frame in animation.Frames)
{
    Console.WriteLine($"{frame.TimestampMs} ms: {frame.DurationMs} ms");
    byte[] rgba = frame.Rgba;
}
```

For on-demand playback, seek to the next frame to produce, then decode it:

```csharp
var decoder = new WebpAnimationDecoder(data);
decoder.Seek(12);
byte[] canvas = new byte[decoder.CanvasWidth * decoder.CanvasHeight * 4];
if (decoder.TryDecodeNextFrame(canvas, out WebpFrameInfo frameInfo))
{
    Console.WriteLine($"Displayed frame {frameInfo.Index}");
}
```

The decoder is spec-derived from RFC 6386 / RFC 9649; libwebp/libvpx source was
not consulted — their binaries are used only as test oracles.

Known oracle divergence: `anim-vp8l-alpha-hint.webp` has a VP8L animation frame
with transparent decoded pixels despite a cleared `alpha_is_used` hint. The
RFC 9649 blend flag requires alpha blending, which this decoder follows;
`anim_dump` 1.6.0 emits that frame uncomposited. It is excluded from the
`anim_dump` comparison but remains covered by compositing and
seek-versus-sequential tests.
