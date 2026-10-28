# Sdcb.SimdCodecs.Gif

Pure-managed GIF (87a / 89a) decoding for .NET 10, derived from the GIF89a specification: global and local
colour tables, interlacing, transparency, every disposal method and the NETSCAPE2.0 loop count. A frame is
the whole canvas as a viewer shows it, every earlier frame composited in.

```csharp
using Sdcb.SimdCodecs.Gif;

byte[] data = File.ReadAllBytes("image.gif");
GifInfo info = GifDecoder.ReadInfo(data);          // canvas, frame count, alpha, loop count
GifImage first = GifDecoder.Decode(data);          // the first frame, RGBA
```

Every frame, by index or in order, through the `Sdcb.SimdCodecs` frame reader:

```csharp
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;

if (SimdCodecs.TryOpenFrames(data, out IImageFrameReader? reader))
using (reader)
{
    var buffer = new byte[info.Width * info.Height * 4];
    foreach (FrameTiming frame in reader.EnumerateFramesInto(buffer, 0, ImagePixelFormat.Bgra32))
        Show(buffer, frame.Duration);
}
```

## Decisions

The specification leaves these to the decoder; they are made as browsers make them.

| Case | Decoded as |
|---|---|
| Canvas before the first frame, disposal to background | Transparent black; the background colour index is not used. An opaque GIF (every frame covers the canvas, none has a transparent index) uses opaque black and decodes as RGB |
| Disposal methods 4–7 (undefined) | 1, do not dispose |
| An image extending past the canvas | Clipped |
| A logical screen of zero width or height | The images' bounding box |
| No colour table, or an index past the table | Opaque black |
| Image data cut off, or an undefined LZW code | Drawn as far as the data goes; the rest of the image keeps the canvas |
| The file ends early, or an unknown block | The images read so far; a file with no image is refused |
| A full LZW table without a clear code | Decoding continues with the table frozen |
| Frame delay | As stored (centiseconds), including 0: a viewer that plays 0 as 100 ms decides that itself |
| Loop count | NETSCAPE2.0 counts repetitions after the first play: a stored `n` plays `n + 1` times (the frame reader's `LoopCount`; `GifInfo.LoopCount` is the stored value), 0 forever; an animation without it plays once |

Canvases and images above 2^26 pixels are refused, to bound what a decode allocates.
