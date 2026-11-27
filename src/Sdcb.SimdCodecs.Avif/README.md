# Sdcb.SimdCodecs.Avif

Pure-managed AVIF decoder for .NET 10. Reads ISOBMFF containers (ISO/IEC 14496-12), the HEIF / AVIF item and sequence models (ISO/IEC 23008-12, MIAF 23000-22, AOM AVIF v1.1.0), and decodes AV1 bitstreams (AOM AV1 Bitstream & Decoding Process Specification).

## Status (M0)

Package skeleton, shared ISOBMFF layer wiring (`src/Shared/Isobmff`), benchmark harness (`benchmarks/Sdcb.SimdCodecs.Avif.Benchmarks`), fixture generator (`tools/avif-fixtures`) and oracle definitions (`tests/Sdcb.SimdCodecs.Tests/Oracle/avif`).

Planned milestones:

- **M1 (Core still decode):** AV1 Profile 0 (Main, 8- and 10-bit, 4:2:0 and 4:0:0), single-item `av01` and `grid`, `av1C`, `colr`, `clap` / `irot` / `imir`, `auxl` alpha, Exif / XMP / `thmb` — byte-exact decoded Y / Cb / Cr / Alpha planes against pinned `libavif 1.4.2 + dav1d 1.5.5`.
- **M2 (All still variants + facade):** AV1 Profile 1 & 2 (4:4:4, 4:2:2, 12-bit), Palette, IntraBC, Superres, Film Grain, Segmentation / Delta-Q / QM, Multi-tile; `iovl` / `iden`, `prem` premultiplied alpha, layered `a1lx` / `a1op`, HDR (PQ / HLG) and ISO 21496-1 Gain Map (`tmap`); `IImageDecoder` / `IPlanarImageDecoder` facade integration, NativeAOT & Mono Wasm smoke tests, 300k fuzz soak.
- **M3 (Animated sequences `avis`):** ISOBMFF `moov` / `trak` / `stbl` sequence parsing, AV1 inter decoding (motion compensation, warped motion, OBMC, compound modes, reference management) and `TryOpenFrames`.
- **M4 (SIMD & concurrency):** AVX2 / Vector128 / scalar kernels (identical across every tier), tile / grid parallelism, direct-to-output low-memory conversion, native region and scaled decode, measured against `libavif[dav1d]` in the same process.

## Output contract

AV1 decoding is integer-arithmetic and deterministic (including Superres and Film Grain synthesis): the Y / Cb / Cr / Alpha planes of every coded image and frame equal the pinned reference decoder's samples bit for bit (`libavif 1.4.2 + dav1d 1.5.5`; see `tests/Sdcb.SimdCodecs.Tests/Oracle/avif/README.md`). Every vector kernel has a scalar twin and every tier (Vector256, Vector128, scalar; x64 and arm64) produces identical output.
