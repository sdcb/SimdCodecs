# Sdcb.SimdCodecs

Pure-managed image codecs for .NET 10 — AOT-compatible, no native dependencies.

Derived from SharpAstro/Codecs (released under the Unlicense); this repository is MIT-licensed.

## Packages

All packages version in lockstep; release versions come from the Git tag.

| Package | What it is |
|---|---|
| `Sdcb.SimdCodecs` | Facade: sniffs magic bytes and dispatches to the codecs below (`SimdCodecs.TryDecode`, `TryDecodeIntoRgba8`, …). |
| `Sdcb.SimdCodecs.Abstractions` | `IImageDecoder`, `IDecodedImage`, `RasterImage`, ITU-T H.273 colour vocabulary, `ColorEncoding`. |
| `Sdcb.SimdCodecs.Jpeg` | JPEG (T.81) decoder — baseline, progressive, restart intervals, any subsampling, grey/YCbCr/RGB/CMYK/YCCK, scaled 1/2–1/8 decode, lossless SOF3 — and a SIMD encoder (baseline, optimised Huffman, or progressive; 4:4:4 / 4:2:0 / grey) that can embed EXIF (APP1) and XMP packets. |
| `Sdcb.SimdCodecs.Jpeg.GainMap` | Ultra HDR / Adobe gain-map JPEG read, assemble and reconstruct. |
| `Sdcb.SimdCodecs.Jpeg.IccInjector` | Splice an ICC profile (APP2) into an existing JPEG. |
| `Sdcb.SimdCodecs.Png` | PNG encoder + decoder: every standard bit depth (incl. 1/2/4-bit grey and indexed), tEXt / zTXt / iTXt text chunks, PNG-3 cICP / mDCV / cLLI HDR signalling. |
| `Sdcb.SimdCodecs.Tiff` | TIFF reader/writer: multi-page, 8/16/32-bit int and float, Deflate/LZW (+ PackBits read), strips or tiles, chunky or planar, on-demand single strip/tile reads. |
| `Sdcb.SimdCodecs.Exif` | EXIF reader for JPEG / TIFF / PNG; `ExifWriter` builds a blob for `JpegEncodeOptions.Exif`. |
| `Sdcb.SimdCodecs.Color.Icc` | Bundled sRGB v4 ICC profile and an ICC `cicp` tag injector. |
| `Sdcb.SimdCodecs.Jbig2` | JBIG2 (T.88) decoder aimed at PDF `/JBIG2Decode`. |
| `Sdcb.SimdCodecs.Jpeg2000` | JPEG 2000 Part 1 (T.800) decoder aimed at PDF `/JPXDecode`. |
| `Sdcb.SimdCodecs.Webp` | WebP decoder (RFC 6386 / RFC 9649): VP8 lossy, VP8L lossless, ALPH, animation with seek, ICC/EXIF/XMP metadata. No encoder. The facade returns still images and frame 0 of an animation; use `WebpDecoder.DecodeAnimation` / `WebpAnimationDecoder` for the timeline. |

## Dependency graph

```
Sdcb.SimdCodecs (facade)
 ├─ Abstractions
 ├─ Png ──────────┬─ Color.Icc ─ Abstractions
 │                └─ Abstractions
 ├─ Tiff ─────────┬─ Png
 │                └─ Abstractions
 ├─ Jpeg ─────────── Abstractions
 ├─ Jpeg.GainMap ─┬─ Jpeg
 │                └─ Abstractions
 ├─ Jbig2 ────────── Abstractions (shares the internal MQ decoder)
 ├─ Jpeg2000 ─────── Abstractions (shares the internal MQ decoder)
 └─ Webp ─────────── Abstractions

Exif ─ Tiff        Jpeg.IccInjector (no dependencies)
```

## Build and test

```bash
dotnet build SimdCodecs.slnx -c Release
dotnet test  SimdCodecs.slnx -c Release
```

Oracle-backed tests skip when their reference tool is missing; set `REQUIRE_ORACLES=1`
to make a missing oracle fail instead (CI does). How to get each oracle is described in
[`AGENTS.md`](AGENTS.md).

Benchmarks are not part of the solution and are built on their own, by path:

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks -- help
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks -- --filter '*'
```

See [`benchmarks/README.md`](benchmarks/README.md) for the overview, the
[JPEG](benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/README.md) benchmark (corpus, libjpeg-turbo /
ImageSharp comparison, accuracy tables, [recorded baselines](benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/results/))
and the [WebP](benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks/README.md) benchmark (libwebp comparison).

## Publishing

Pushing a version tag runs the build and publishes its packages to NuGet.org. For
example, `v1.0.0` publishes version `1.0.0` for every package; `v1.0.0-rc.1`
publishes a prerelease. Tags must use `vMAJOR.MINOR.PATCH`, optionally followed by
a SemVer prerelease suffix. Build metadata is not accepted because NuGet does not
include it in the package version identity.

Publishing uses [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing):

1. In NuGet.org, create a trusted publishing policy for the appropriate package owner
   and scopes, with repository owner `sdcb`, repository `SimdCodecs`, and workflow
   file `dotnet.yml`. Leave the environment empty.
2. Set the GitHub Actions repository variable `NUGET_USER` to the NuGet.org username
   used to publish the packages (not an email address).
3. After a successful tag publication, revoke the old long-lived NuGet API key and
   remove the `NUGET_API_KEY` GitHub secret if no other workflows need it.

The workflow exchanges a GitHub OIDC token for a short-lived NuGet API key; no
long-lived API key needs to be stored in GitHub.

## License

MIT — see [`LICENSE`](LICENSE).
