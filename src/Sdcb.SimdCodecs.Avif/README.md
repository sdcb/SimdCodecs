# Sdcb.SimdCodecs.Avif

Pure-managed AVIF decoder for .NET 10. Reads ISOBMFF containers (ISO/IEC 14496-12), the HEIF / AVIF item and sequence models (ISO/IEC 23008-12, MIAF 23000-22, AOM AVIF v1.1.0), and decodes AV1 bitstreams (AOM AV1 Bitstream & Decoding Process Specification).

## Status (M2)

Complete still-image AVIF decoder integrated into the `Sdcb.SimdCodecs` facade (`AvifImageDecoder` implementing `IImageDecoder` and `IPlanarImageDecoder`). Supports AV1 Profiles 0, 1 and 2 (8-, 10- and 12-bit; 4:2:0, 4:2:2, 4:4:4 and 4:0:0; lossless WHT), Palette, IntraBC, Superres (§7.16), Film Grain synthesis (§7.18.3), Segmentation, Delta-Q / Delta-LF, Quantization Matrices, Multi-tile, Deblocking, CDEF, and Wiener / SGR Loop Restoration. Container support covers `av01`, `grid`, `iden`, `iovl` overlay, straight and premultiplied `auxl` alpha (single and grid), `clap` / `irot` / `imir` transforms, `colr` (nclx & ICC) + `clli`, HDR PQ / HLG 16-bit output, ISO/IEC 21496-1 `tmap` gain maps (dual-path linear HDR floats + SDR base), and Exif / XMP / `thmb` metadata. All 34 intra still fixtures in `Fixtures/avif/avif-golden.tsv` match `libavif 1.4.2 + dav1d 1.5.5` plane-for-plane with zero bit error (the remaining layered `a1lx`/`a1op` fixture uses `INTER_FRAME` enhancement layers and lands with M3 Inter decoding).

Planned milestones:

- **M1 (Core still decode):** AV1 Profile 0 (Main, 8- and 10-bit, 4:2:0 and 4:0:0), single-item `av01` and `grid`, `av1C`, `colr`, `clap` / `irot` / `imir`, `auxl` alpha, Exif / XMP / `thmb` — byte-exact decoded Y / Cb / Cr / Alpha planes against pinned `libavif 1.4.2 + dav1d 1.5.5`.
- **M2 (All still variants + facade):** AV1 Profile 1 & 2 (4:4:4, 4:2:2, 12-bit), Palette, IntraBC, Superres, Film Grain, Segmentation / Delta-Q / QM, Multi-tile; `iovl` / `iden`, `prem` premultiplied alpha, layered `a1lx` / `a1op`, HDR (PQ / HLG) and ISO 21496-1 Gain Map (`tmap`); `IImageDecoder` / `IPlanarImageDecoder` facade integration, NativeAOT & Mono Wasm smoke tests, 300k fuzz soak.
- **M3 (Animated sequences `avis`):** ISOBMFF `moov` / `trak` / `stbl` sequence parsing, AV1 inter decoding (motion compensation, warped motion, OBMC, compound modes, reference management) and `TryOpenFrames`.
- **M4 (SIMD & concurrency):** AVX2 / Vector128 / scalar kernels (identical across every tier), tile / grid parallelism, direct-to-output low-memory conversion, native region and scaled decode, measured against `libavif[dav1d]` in the same process.

## Output contract

AV1 decoding is integer-arithmetic and deterministic (including Superres and Film Grain synthesis): the Y / Cb / Cr / Alpha planes of every coded image and frame equal the pinned reference decoder's samples bit for bit (`libavif 1.4.2 + dav1d 1.5.5`; see `tests/Sdcb.SimdCodecs.Tests/Oracle/avif/README.md`). Every vector kernel has a scalar twin and every tier (Vector256, Vector128, scalar; x64 and arm64) produces identical output.
