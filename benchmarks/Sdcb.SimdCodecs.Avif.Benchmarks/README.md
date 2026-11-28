# Sdcb.SimdCodecs.Avif.Benchmarks

End-to-end AVIF decode benchmarks comparing `Sdcb.SimdCodecs.Avif` (from M1 onward) against `libavif` (`dav1d`, in-process via P/Invoke) and `Magick.NET`, on the same in-memory byte array.

## Prerequisites

- Fetch the AVIF fixtures: `bash tests/Sdcb.SimdCodecs.Tests/Oracle/avif/fetch.sh` (or point `AVIF_INPUTS` at a folder of `.avif` files).
- Native libraries (`avif.dll`, `dav1d.dll`, `aom.dll`, `SvtAv1Enc.dll`) are copied automatically from `C:\_\3rd\vcpkg\installed\x64-windows\bin` (override with `-p:AvifNativeSource=...`).

## Usage

```bash
dotnet run --project benchmarks/Sdcb.SimdCodecs.Avif.Benchmarks -c Release -- info
dotnet run --project benchmarks/Sdcb.SimdCodecs.Avif.Benchmarks -c Release -- --filter *AvifDecodeBenchmarks*
```

The same-process A/B used for every optimisation (see the repository's `benchmarks/README.md`):

```bash
dotnet run --project benchmarks/Sdcb.SimdCodecs.Avif.Benchmarks -c Release -- baseline HEAD      # build the commit to compare against
dotnet run --project benchmarks/Sdcb.SimdCodecs.Avif.Benchmarks -c Release -- ab [--multi] [--anim]
```

Peak memory, ours vs libavif[dav1d] (a fresh child process per fixture, decoder and thread count; private commit above a warmed-up baseline, output buffer included on both sides):

```bash
dotnet run --project benchmarks/Sdcb.SimdCodecs.Avif.Benchmarks -c Release -- memory [--fixtures RE]
```

## Results

- [2026-11-28, Ryzen 7 5800X, M4](results/2026-11-28-amd-ryzen-7-5800x-m4.md): single-core, multi-core (with Magick.NET), animations, peak memory, stage timers, ablation, and the A/B history of the M4 round.
