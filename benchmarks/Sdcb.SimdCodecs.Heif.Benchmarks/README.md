# HEIF / HEIC Benchmarks

Measures HEIF still-image decode throughput and memory. Follows the rules in [`benchmarks/README.md`](../../benchmarks/README.md).

## Competitors

| Label | Implementation | Version pinned? |
|---|---|---|
| `Magick.NET` | libheif (via Magick.NET, black-box only) | `Directory.Packages.props` |
| `SimdCodecs` | `Sdcb.SimdCodecs.Heif` (this repo) | same build |

Magick.NET was the only competitor in M0. Since the M1 baseline the harness also times libde265 (HEVC stage) and libheif (full pipeline) in-process through P/Invoke; see [`results/2026-11-03-amd-ryzen-7-5800x-m1-baseline.md`](results/2026-11-03-amd-ryzen-7-5800x-m1-baseline.md). Performance work is milestone M3 (M2 is release readiness: facade adapter, fuzz soak, docs).

## Inputs

Three real-world HEIC files taken with a Realme GT5 Pro, CC0. Download them with the fetch script, then copy into `heif-inputs/`:

```bash
bash tests/Sdcb.SimdCodecs.Tests/Oracle/heif/fetch.sh
cp tests/Sdcb.SimdCodecs.Tests/Fixtures/heif/*.heic \
   benchmarks/Sdcb.SimdCodecs.Heif.Benchmarks/heif-inputs/
```

Or point `HEIF_INPUTS` at any directory that contains the three files.

| File | Resolution | Profile | Tiles |
|---|---|---|---|
| `hevc-main-3072x4096-grid.heic` | 3072×4096 | Main 8-bit | 48 (6×8) |
| `hevc-main10-3072x4096-grid.heic` | 3072×4096 | Main 10 10-bit | 48 (6×8) |
| `hevc-main-8192x6144-grid.heic` | 8192×6144 | Main 8-bit | 192 (16×12) |

## Running

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Heif.Benchmarks -- --filter "*"
```

Record the machine with every result (CPU, OS, .NET version). Never compare numbers across machines or against a result from an earlier session.
