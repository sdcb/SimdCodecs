# Benchmarks

Performance and accuracy benchmarks for the codecs. None of these projects is in `SimdCodecs.slnx`:
build and run each by path from the repository root, so CI never restores BenchmarkDotNet, ImageSharp
or the corpora.

| Project | Measures | Compared against | Docs |
|---|---|---|---|
| [`Sdcb.SimdCodecs.Jpeg.Benchmarks`](Sdcb.SimdCodecs.Jpeg.Benchmarks/) | JPEG decode speed, memory and accuracy, a same-process A/B against the previous commit, and encode speed / size / quality | libjpeg-turbo, ImageSharp | [README](Sdcb.SimdCodecs.Jpeg.Benchmarks/README.md), [recorded results](Sdcb.SimdCodecs.Jpeg.Benchmarks/results/) |
| [`Sdcb.SimdCodecs.Webp.Benchmarks`](Sdcb.SimdCodecs.Webp.Benchmarks/) | WebP decode speed and allocations | libwebp | [README](Sdcb.SimdCodecs.Webp.Benchmarks/README.md) |

## Rules that apply to every benchmark

- **Clean-room.** Competitors are black boxes: released binaries and documented APIs only, never their
  source (see [`AGENTS.md`](../AGENTS.md)).
- **Correctness before timing.** A benchmark must not time a decoder whose output is wrong.
- **Compare in the same run.** A number from an earlier session is not a baseline; machine state moves
  results by more than 10%. Measure A and B back to back.
- **Record the machine.** Every recorded result states the CPU, OS, .NET version and
  `Vector256/512.IsHardwareAccelerated`.
- **End-to-end decides.** A kernel that is faster in isolation is not a faster decoder until the
  whole decode says so.

To add a benchmark project, put it in `benchmarks/Sdcb.SimdCodecs.<Codec>.Benchmarks/` with its own
README and add a row above.
