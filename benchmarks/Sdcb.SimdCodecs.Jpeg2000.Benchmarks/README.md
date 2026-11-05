# Sdcb.SimdCodecs.Jpeg2000.Benchmarks

Performance, memory, and PSNR accuracy benchmark harness for JPEG 2000 decoding, comparing `Sdcb.SimdCodecs.Jpeg2000` directly against official `libopenjp2` (OpenJPEG 2.5.4).

Recorded results: [results/2026-10-30-amd-ryzen-7-5800x-baseline.md](results/2026-10-30-amd-ryzen-7-5800x-baseline.md) (single thread), [results/2026-11-05-amd-ryzen-7-5800x-parallel.md](results/2026-11-05-amd-ryzen-7-5800x-parallel.md) (serial vs threaded vs libopenjp2 at one and N threads).

## What it measures

- **Decode latency & throughput**: In-process wall-clock time (median of 5 runs) and Megapixels/sec (MP/s) comparing `SimdCodecs` against OpenJPEG's `libopenjp2` core decoding routine, each at one thread and at N threads (default: every logical processor; `--threads N`). SimdCodecs' serial row is the same code with `Jpeg2000Ablation.MaxDegreeOfParallelism = 1` (an internal switch, visible to this project through `InternalsVisibleTo`); libopenjp2's threaded row uses `opj_codec_set_threads`. The benchmark refuses to time a threaded decode that differs from the serial one.
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

# Thread count of the threaded rows (default: every logical processor)
dotnet run --project benchmarks/Sdcb.SimdCodecs.Jpeg2000.Benchmarks -c Release -- fuji --threads 8

# Filter specific cases (e.g. only 1024 or lossless)
dotnet run --project benchmarks/Sdcb.SimdCodecs.Jpeg2000.Benchmarks -c Release -- 1024
dotnet run --project benchmarks/Sdcb.SimdCodecs.Jpeg2000.Benchmarks -c Release -- lossless
```

## Comparing with an earlier commit

A number from an earlier session is not a baseline. To check that the single-thread path did not regress, extract the previous commit to a separate directory (`git archive <ref>` piped to `tar -x -C <dir>`), build the benchmark there, and run both back to back with `SIMDCODECS_JPEG2000_BENCH_ROOT` pointing at one benchmark directory, so both use the same generated inputs. Do not build while a run is timing.

## Competitor

- **libopenjp2**: Official OpenJPEG 2.5.4 release binary core decode routine without CLI/subprocess overhead.
