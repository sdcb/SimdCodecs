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
