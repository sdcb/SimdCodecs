# JPEG encoder oracle — `jpegenc` from stb_image_write

This directory hosts a tiny CLI wrapper around the JPEG writer in
[`stb_image_write.h`](https://github.com/nothings/stb/blob/master/stb_image_write.h)
(public domain / MIT) — the exact reference `Sdcb.SimdCodecs.Jpeg.JpegEncoder` is a
faithful port of. `JpegEncoderOracleTests` shells out to `jpegenc.exe` to assert
our encoder is **byte-for-byte identical** to the reference for the same pixels,
quality, and (quality-derived) subsampling.

The header is pinned by commit SHA + SHA-256 in `build.sh`; byte-exactness
depends on the exact reference version. The downloaded header and the built
binary are git-ignored — the committed source of truth is
`jpegenc.c` + `build.sh`.

## Build (one-time)

```bash
bash tests/Sdcb.SimdCodecs.Tests/Oracle/jpegenc/build.sh
```

The script downloads the pinned `stb_image_write.h`, verifies its SHA-256, and
compiles `jpegenc.exe` into `../bin/` with clang (the one shipping with the Swift
for Windows toolchain, or any clang on PATH). If `bin/jpegenc.exe` is absent at
test time, the oracle tests skip with a clear message rather than failing — and
the committed `jpeg-encoder-golden.tsv` digests still guard the encoder in CI.

## Usage

```
jpegenc <width> <height> <channels> <quality> <input.raw> <output.jpg>
```

`input.raw` is `width*height*channels` bytes of interleaved 8-bit samples.
Subsampling is not an argument — the reference derives it from quality
(≤ 90 → 4:2:0), which is what `JpegSubsampling.Auto` reproduces.
