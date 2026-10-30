# Sdcb.SimdCodecs.Jpeg2000.Benchmarks

Performance, memory, and PSNR accuracy benchmark harness for JPEG 2000 decoding, comparing
`Sdcb.SimdCodecs.Jpeg2000` directly against official `libopenjp2` (OpenJPEG 2.5.4).

Recorded baselines: [results/2026-10-30-amd-ryzen-7-5800x-baseline.md](results/2026-10-30-amd-ryzen-7-5800x-baseline.md).

## What it measures

- **Decode latency & throughput**: In-process wall-clock time (median of 5 runs) and Megapixels/sec (MP/s)
  comparing `SimdCodecs` against OpenJPEG's `libopenjp2` core decoding routine.
- **Accuracy & PSNR**:
  - Reversible 5/3 filter (Lossless): Exact bit-by-bit match percentage (target: 100%, PSNR = inf).
  - Irreversible 9/7 filter (Lossy): PSNR (dB) and peak absolute error vs libopenjp2 (target: PSNR > 75 dB, peak error <= 1).
- **Memory allocations**: Managed heap memory allocated per decode (`GC.GetAllocatedBytesForCurrentThread`).
- **File size & bpp**: Compressed bitstream bytes and bits-per-pixel (bpp).

## Running

From the repository root:

```bash
# Run all benchmark cases
dotnet run --project benchmarks/Sdcb.SimdCodecs.Jpeg2000.Benchmarks -c Release

# Filter specific cases (e.g. only 1024 or lossless)
dotnet run --project benchmarks/Sdcb.SimdCodecs.Jpeg2000.Benchmarks -c Release -- 1024
dotnet run --project benchmarks/Sdcb.SimdCodecs.Jpeg2000.Benchmarks -c Release -- lossless
```

## Competitor

- **libopenjp2**: Official OpenJPEG 2.5.4 release binary core decode routine without CLI/subprocess overhead.
