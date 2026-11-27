# Sdcb.SimdCodecs.Avif

Pure-managed AVIF decoder for .NET 10. Reads ISOBMFF containers (ISO/IEC 14496-12), the HEIF / AVIF item and sequence models (ISO/IEC 23008-12, MIAF 23000-22, AOM AVIF v1.1.0), and decodes AV1 bitstreams (AOM AV1 Bitstream & Decoding Process Specification).

## Status (M3)

Complete still-image and animated AVIF decoder integrated into the `Sdcb.SimdCodecs` facade (`AvifImageDecoder` implementing `IImageDecoder`, `IPlanarImageDecoder`, and `IMultiFrameImageDecoder`). Supports AV1 Profiles 0, 1 and 2 (8-, 10- and 12-bit; 4:2:0, 4:2:2, 4:4:4 and 4:0:0; lossless WHT), Intra + full Inter prediction (8-tap / 4-tap subpel filters, scaled references, temporal motion field projection, global and local warped motion, OBMC, compound average / distance / wedge / diff-weighted / inter-intra, `show_existing_frame`, 8-slot reference store with saved CDFs), Palette, IntraBC, Superres (§7.16), Film Grain synthesis (§7.18.3), Segmentation, Delta-Q / Delta-LF, Quantization Matrices, Multi-tile, Deblocking, CDEF, and Wiener / SGR Loop Restoration. Container support covers `av01`, `grid`, `iden`, `iovl` overlay, layered scalable `a1lx` / `a1op`, animated `avis` sequences (`moov` / `trak` / `stbl` + synchronized `auxv` alpha track, variable `stts` durations, `elst` loop counts, `stss` random-access keyframe seek via `ImageDecoder.Open` / `TryOpenFrames`), straight and premultiplied `auxl` alpha, `clap` / `irot` / `imir` transforms, `colr` (nclx & ICC) + `clli`, HDR PQ / HLG 16-bit output, ISO/IEC 21496-1 `tmap` gain maps (dual-path linear HDR floats + SDR base), and Exif / XMP / `thmb` metadata. All 40/40 fixtures (35 stills + 5 animated sequences, 181 frames total) in `Fixtures/avif/avif-golden.tsv` match `libavif 1.4.2 + dav1d 1.5.5` plane-for-plane with zero bit error.

Planned milestones:

- **M1 (Core still decode — done):** AV1 Profile 0 (Main, 8- and 10-bit, 4:2:0 and 4:0:0), single-item `av01` and `grid`, `av1C`, `colr`, `clap` / `irot` / `imir`, `auxl` alpha, Exif / XMP / `thmb` — byte-exact decoded Y / Cb / Cr / Alpha planes against pinned `libavif 1.4.2 + dav1d 1.5.5`.
- **M2 (All still variants + facade — done):** AV1 Profile 1 & 2 (4:4:4, 4:2:2, 12-bit), Palette, IntraBC, Superres, Film Grain, Segmentation / Delta-Q / QM, Multi-tile; `iovl` / `iden`, `prem` premultiplied alpha, HDR (PQ / HLG) and ISO 21496-1 Gain Map (`tmap`); `IImageDecoder` / `IPlanarImageDecoder` facade integration, NativeAOT & Mono Wasm smoke tests, 300k fuzz soak.
- **M3 (Inter + layered + animated `avis` — done):** Full AV1 inter decoding (motion compensation, warped motion, OBMC, compound modes, reference management), layered scalable `a1lx` / `a1op`, ISOBMFF `moov` / `trak` / `stbl` animated sequence parsing with auxiliary alpha track, and `IMultiFrameImageDecoder.TryOpenFrames` (40/40 goldens bit-exact + 300k fuzz soak).
- **M4 (SIMD & concurrency):** AVX2 / Vector128 / scalar kernels (identical across every tier), tile / grid parallelism, direct-to-output low-memory conversion, native region and scaled decode, measured against `libavif[dav1d]` in the same process.

## Output contract

AV1 decoding is integer-arithmetic and deterministic (including Superres and Film Grain synthesis): the Y / Cb / Cr / Alpha planes of every coded image and frame equal the pinned reference decoder's samples bit for bit (`libavif 1.4.2 + dav1d 1.5.5`; see `tests/Sdcb.SimdCodecs.Tests/Oracle/avif/README.md`). Every vector kernel has a scalar twin and every tier (Vector256, Vector128, scalar; x64 and arm64) produces identical output.
