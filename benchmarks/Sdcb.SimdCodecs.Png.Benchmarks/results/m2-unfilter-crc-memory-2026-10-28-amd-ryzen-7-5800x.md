# Vector unfilter, slicing-by-8 CRC, memory measurement — 2026-10-28, AMD Ryzen 7 5800X

Same-process A/B against `2453e7f` (baseline). Commands: `LIBPNG_PATH=…/vcpkg/installed/x64-windows/bin/libpng16.dll dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Png.Benchmarks -- matrix all`, then `peakmem decode --cases …` and `peakmem encode --cases …`.

- Unfilter: Up sixteen/thirty-two bytes at a time; Sub, Average, Paeth one pixel per vector step (bpp 2-8), storing exactly the pixel's bytes (no partial-overlap store-to-load stalls); Paeth branch-free in 16-bit lanes.
- Chunk CRC: slicing-by-8 (ARM64: CRC32X), shared by reader and writer.

Decode, current ÷ libpng (geometric mean): photo 0.61×, screen 0.25×; worst single case `photo-g8` 0.91× (one-byte pixels: Average stays a serial byte chain). Current ÷ ImageSharp: photo 0.62×, screen 0.32×. Current ÷ baseline: photo 0.73×, screen 0.57×. Encode at `optimal`: 0.29–0.99× of libpng's time. Store encode: 0.55× of ImageSharp (was 1.21×).

Stage costs before/after on `photo-rgb8` (ablation): CRC 5.9 → 1.6 ms, unfilter 9.9 → 2.5 ms; inflate (the BCL's zlib-ng) 18.9 ms is now most of the 23 ms.

- CPU: AMD Ryzen 7 5800X 8-Core Processor
- OS: Microsoft Windows 10.0.26200 (X64)
- .NET: .NET 10.0.11 (win-x64), process X64
- Logical processors: 16
- Vector128.IsHardwareAccelerated: True, Vector256.IsHardwareAccelerated: True, Vector512.IsHardwareAccelerated: False
- AVX2: True, AVX-512F: False
- Image size: 1920×1080
- `photo` source: centre crop of landscape-almsee.png (JPEG benchmark corpus master)
- `screen` source: synthetic
- ImageSharp: 3.1.12
- libpng: libpng 1.6.39 (C:\_\3rd\vcpkg\installed\x64-windows\bin\libpng16.dll)
- Baseline: 2453e7f06581 PNG: decode RGBA8 a row at a time, shuffle/table expansion, unfiltered stored rows

### Decode to 8-bit RGBA (in-process medians of 7 interleaved rounds, ms)

`current` = `PngImageDecoder.TryDecodeIntoRgba8` into a caller buffer; `native` = `PngReader.Decode` (samples as stored, no RGBA expansion); ImageSharp = `Image.Load<Rgba32>` + `CopyPixelDataTo`, one thread; libpng = simplified API `png_image_finish_read` to RGBA. Ratios below 1 mean we are faster. Inputs are encoded by ImageSharp (adaptive filters, zlib level 6).

| Case | KB | current | native | baseline | ImageSharp | libpng | current MP/s | ÷ ImageSharp | ÷ libpng | ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-g1` | 28 | 1.805 | 1.114 | 1.735 | 3.500 | 4.048 | 1149 | 0.52× | 0.45× | 1.04× |
| `photo-g2` | 167 | 2.587 | 2.436 | 2.788 | 4.602 | 5.916 | 802 | 0.56× | 0.44× | 0.93× |
| `photo-g4` | 427 | 4.382 | 4.084 | 4.861 | 6.222 | 8.047 | 473 | 0.70× | 0.54× | 0.90× |
| `photo-g8` | 1297 | 13.8 | 13.6 | 16.6 | 14.5 | 15.2 | 150 | 0.95× | 0.91× | 0.84× |
| `photo-g16` | 3588 | 14.4 | 14.4 | 23.8 | 17.8 | — | 144 | 0.81× | — | 0.60× |
| `photo-rgb8` | 3309 | 23.1 | 23.0 | 34.0 | 26.5 | 29.4 | 90 | 0.87× | 0.78× | 0.68× |
| `photo-rgb16` | 10762 | 39.4 | 39.5 | 81.8 | 68.3 | — | 53 | 0.58× | — | 0.48× |
| `photo-p1` | 123 | 2.604 | 2.029 | 2.764 | 8.109 | 5.797 | 796 | 0.32× | 0.45× | 0.94× |
| `photo-p2` | 268 | 3.372 | 3.176 | 3.683 | 9.187 | 7.085 | 615 | 0.37× | 0.48× | 0.92× |
| `photo-p4` | 567 | 5.114 | 4.826 | 5.797 | 10.8 | 9.012 | 406 | 0.47× | 0.57× | 0.88× |
| `photo-p8` | 1406 | 8.776 | 8.084 | 10.5 | 13.3 | 11.9 | 236 | 0.66× | 0.74× | 0.84× |
| `photo-p8a` | 1051 | 7.731 | 7.204 | 9.006 | 18.1 | 10.3 | 268 | 0.43× | 0.75× | 0.86× |
| `photo-ga8` | 1772 | 14.2 | 14.2 | 22.7 | 18.8 | 19.4 | 146 | 0.75× | 0.73× | 0.63× |
| `photo-ga16` | 6121 | 30.3 | 30.3 | 60.5 | 30.4 | — | 69 | 1.00× | — | 0.50× |
| `photo-rgba8` | 3809 | 25.3 | 25.6 | 39.2 | 24.6 | 34.0 | 82 | 1.03× | 0.74× | 0.65× |
| `photo-rgba16` | 13672 | 52.9 | 53.5 | 141.1 | 99.5 | — | 39 | 0.53× | — | 0.37× |
| `photo-g1-adam7` | 35 | — | — | — | 5.102 | 15.3 | — | — | — | — |
| `photo-g2-adam7` | 197 | — | — | — | 6.256 | 17.4 | — | — | — | — |
| `photo-g4-adam7` | 490 | — | — | — | 8.149 | 19.9 | — | — | — | — |
| `photo-g8-adam7` | 1418 | — | — | — | 13.9 | 26.2 | — | — | — | — |
| `photo-g16-adam7` | 3689 | — | — | — | 18.5 | — | — | — | — | — |
| `photo-rgb8-adam7` | 3830 | — | — | — | 28.9 | 41.3 | — | — | — | — |
| `photo-rgb16-adam7` | 11055 | — | — | — | 50.8 | — | — | — | — | — |
| `photo-p1-adam7` | 130 | — | — | — | 9.406 | 17.0 | — | — | — | — |
| `photo-p2-adam7` | 289 | — | — | — | 10.8 | 18.5 | — | — | — | — |
| `photo-p4-adam7` | 620 | — | — | — | 12.7 | 21.0 | — | — | — | — |
| `photo-p8-adam7` | 1499 | — | — | — | 14.2 | 23.7 | — | — | — | — |
| `photo-p8a-adam7` | 1174 | — | — | — | 19.4 | 22.2 | — | — | — | — |
| `photo-ga8-adam7` | 1998 | — | — | — | 17.9 | 30.1 | — | — | — | — |
| `photo-ga16-adam7` | 6382 | — | — | — | 31.5 | — | — | — | — | — |
| `photo-rgba8-adam7` | 4414 | — | — | — | 29.1 | 46.6 | — | — | — | — |
| `photo-rgba16-adam7` | 14072 | — | — | — | 104.5 | — | — | — | — | — |
| `screen-g1` | 20 | 1.539 | 0.926 | 1.561 | 3.152 | 3.819 | 1347 | 0.49× | 0.40× | 0.99× |
| `screen-g2` | 36 | 1.245 | 1.005 | 1.275 | 3.271 | 4.661 | 1665 | 0.38× | 0.27× | 0.98× |
| `screen-g4` | 45 | 1.482 | 1.291 | 1.522 | 3.434 | 5.549 | 1399 | 0.43× | 0.27× | 0.97× |
| `screen-g8` | 59 | 1.995 | 1.950 | 2.795 | 3.350 | 6.546 | 1039 | 0.60× | 0.30× | 0.71× |
| `screen-g16` | 83 | 2.226 | 2.370 | 4.936 | 5.203 | — | 931 | 0.43× | — | 0.45× |
| `screen-rgb8` | 81 | 2.111 | 2.209 | 6.245 | 3.805 | 12.9 | 982 | 0.55× | 0.16× | 0.34× |
| `screen-rgb16` | 97 | 2.692 | 3.060 | 11.5 | 19.8 | — | 770 | 0.14× | — | 0.23× |
| `screen-p1` | 47 | 1.770 | 1.111 | 1.827 | 7.294 | 4.947 | 1171 | 0.24× | 0.36× | 0.97× |
| `screen-p2` | 67 | 1.431 | 1.195 | 1.505 | 7.329 | 5.368 | 1449 | 0.20× | 0.27× | 0.95× |
| `screen-p4` | 66 | 1.596 | 1.398 | 1.656 | 7.434 | 5.920 | 1299 | 0.21× | 0.27× | 0.96× |
| `screen-p8` | 84 | 1.397 | 0.774 | 1.471 | 6.359 | 5.888 | 1484 | 0.22× | 0.24× | 0.95× |
| `screen-p8a` | 84 | 1.382 | 0.865 | 1.473 | 12.0 | 5.052 | 1501 | 0.12× | 0.27× | 0.94× |
| `screen-ga8` | 71 | 1.930 | 1.957 | 4.457 | 4.206 | 9.460 | 1074 | 0.46× | 0.20× | 0.43× |
| `screen-ga16` | 101 | 2.711 | 2.913 | 9.052 | 5.750 | — | 765 | 0.47× | — | 0.30× |
| `screen-rgba8` | 85 | 2.059 | 2.482 | 7.937 | 2.672 | 16.0 | 1007 | 0.77× | 0.13× | 0.26× |
| `screen-rgba16` | 106 | 3.020 | 3.657 | 15.1 | 14.3 | — | 687 | 0.21× | — | 0.20× |
| `screen-g1-adam7` | 23 | — | — | — | 4.843 | 14.9 | — | — | — | — |
| `screen-g2-adam7` | 39 | — | — | — | 4.494 | 15.8 | — | — | — | — |
| `screen-g4-adam7` | 51 | — | — | — | 4.604 | 16.6 | — | — | — | — |
| `screen-g8-adam7` | 67 | — | — | — | 3.916 | 17.6 | — | — | — | — |
| `screen-g16-adam7` | 92 | — | — | — | 4.788 | — | — | — | — | — |
| `screen-rgb8-adam7` | 92 | — | — | — | 6.133 | 23.6 | — | — | — | — |
| `screen-rgb16-adam7` | 113 | — | — | — | 12.4 | — | — | — | — | — |
| `screen-p1-adam7` | 50 | — | — | — | 8.821 | 16.1 | — | — | — | — |
| `screen-p2-adam7` | 72 | — | — | — | 8.583 | 16.6 | — | — | — | — |
| `screen-p4-adam7` | 72 | — | — | — | 8.561 | 17.0 | — | — | — | — |
| `screen-p8-adam7` | 99 | — | — | — | 6.696 | 16.9 | — | — | — | — |
| `screen-p8a-adam7` | 100 | — | — | — | 12.3 | 16.0 | — | — | — | — |
| `screen-ga8-adam7` | 82 | — | — | — | 4.957 | 20.3 | — | — | — | — |
| `screen-ga16-adam7` | 115 | — | — | — | 5.495 | — | — | — | — | — |
| `screen-rgba8-adam7` | 99 | — | — | — | 4.399 | 26.6 | — | — | — | — |
| `screen-rgba16-adam7` | 124 | — | — | — | 16.0 | — | — | — | — | — |

Geometric means of current ÷ competitor over the cases both decode:

- vs ImageSharp, photo: 0.62× (16 cases)
- vs ImageSharp, screen: 0.32× (16 cases)
- vs libpng, photo: 0.61× (12 cases)
- vs libpng, screen: 0.25× (12 cases)
- vs baseline, photo: 0.73× (16 cases)
- vs baseline, screen: 0.57× (16 cases)

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool and libpng allocates natively, so neither shows here: compare working sets with `peakmem`.

| Case | current | native | baseline | ImageSharp | libpng |
|---|---:|---:|---:|---:|---:|
| `photo-g1` | 1.7 | 2026.3 | 1.7 | 105.0 | 0.0 |
| `photo-g2` | 1.7 | 2026.8 | 1.7 | 105.0 | 0.0 |
| `photo-g4` | 1.8 | 2027.8 | 1.8 | 105.4 | 0.0 |
| `photo-g8` | 1.2 | 2028.2 | 1.2 | 5.4 | 0.0 |
| `photo-g16` | 1.7 | 4055.6 | 1.7 | 8.8 | — |
| `photo-rgb8` | 1.7 | 6082.5 | 1.7 | 8.3 | 0.0 |
| `photo-rgb16` | 4.7 | 12166.1 | 4.7 | 19.3 | — |
| `photo-p1` | 1.7 | 2026.3 | 1.7 | 105.1 | 0.0 |
| `photo-p2` | 1.8 | 2026.9 | 1.8 | 105.4 | 0.0 |
| `photo-p4` | 2.0 | 2028.0 | 2.0 | 106.0 | 0.0 |
| `photo-p8` | 3.0 | 2029.0 | 3.0 | 10.4 | 0.0 |
| `photo-p8a` | 3.2 | 2029.2 | 3.2 | 18.3 | 0.0 |
| `photo-ga8` | 1.2 | 4055.1 | 1.2 | 6.1 | 0.0 |
| `photo-ga16` | 2.7 | 8110.4 | 2.7 | 12.5 | — |
| `photo-rgba8` | 9.2 | 8109.3 | 9.2 | 9.1 | 0.0 |
| `photo-rgba16` | 4.7 | 16219.9 | 4.7 | 23.5 | — |
| `photo-g1-adam7` | — | — | — | 193.4 | 0.0 |
| `photo-g2-adam7` | — | — | — | 193.7 | 0.0 |
| `photo-g4-adam7` | — | — | — | 194.0 | 0.0 |
| `photo-g8-adam7` | — | — | — | 5.6 | 0.0 |
| `photo-g16-adam7` | — | — | — | 8.9 | — |
| `photo-rgb8-adam7` | — | — | — | 9.1 | 0.0 |
| `photo-rgb16-adam7` | — | — | — | 19.7 | — |
| `photo-p1-adam7` | — | — | — | 193.8 | 0.0 |
| `photo-p2-adam7` | — | — | — | 194.0 | 0.0 |
| `photo-p4-adam7` | — | — | — | 194.7 | 0.0 |
| `photo-p8-adam7` | — | — | — | 10.6 | 0.0 |
| `photo-p8a-adam7` | — | — | — | 18.5 | 0.0 |
| `photo-ga8-adam7` | — | — | — | 6.5 | 0.0 |
| `photo-ga16-adam7` | — | — | — | 12.8 | — |
| `photo-rgba8-adam7` | — | — | — | 9.9 | 0.0 |
| `photo-rgba16-adam7` | — | — | — | 24.1 | — |
| `screen-g1` | 1.7 | 2026.3 | 1.7 | 104.8 | 0.0 |
| `screen-g2` | 1.7 | 2026.8 | 1.7 | 104.8 | 0.0 |
| `screen-g4` | 1.7 | 2027.7 | 1.7 | 104.8 | 0.0 |
| `screen-g8` | 0.6 | 2027.7 | 0.6 | 3.5 | 0.0 |
| `screen-g16` | 0.6 | 4054.5 | 0.6 | 3.6 | — |
| `screen-rgb8` | 0.6 | 6081.4 | 0.6 | 3.6 | 0.0 |
| `screen-rgb16` | 0.6 | 12162.0 | 0.6 | 3.6 | — |
| `screen-p1` | 1.7 | 2026.3 | 1.7 | 105.0 | 0.0 |
| `screen-p2` | 1.7 | 2026.8 | 1.7 | 105.1 | 0.0 |
| `screen-p4` | 1.7 | 2027.8 | 1.7 | 105.3 | 0.0 |
| `screen-p8` | 2.4 | 2028.4 | 2.4 | 8.5 | 0.0 |
| `screen-p8a` | 2.7 | 2028.7 | 2.7 | 16.9 | 0.0 |
| `screen-ga8` | 0.6 | 4054.5 | 0.6 | 3.6 | 0.0 |
| `screen-ga16` | 0.6 | 8108.3 | 0.6 | 3.6 | — |
| `screen-rgba8` | 8.2 | 8108.3 | 8.2 | 3.6 | 0.0 |
| `screen-rgba16` | 0.6 | 16215.8 | 0.6 | 3.6 | — |
| `screen-g1-adam7` | — | — | — | 193.4 | 0.0 |
| `screen-g2-adam7` | — | — | — | 193.4 | 0.0 |
| `screen-g4-adam7` | — | — | — | 193.4 | 0.0 |
| `screen-g8-adam7` | — | — | — | 3.6 | 0.0 |
| `screen-g16-adam7` | — | — | — | 3.6 | — |
| `screen-rgb8-adam7` | — | — | — | 3.6 | 0.0 |
| `screen-rgb16-adam7` | — | — | — | 3.6 | — |
| `screen-p1-adam7` | — | — | — | 193.6 | 0.0 |
| `screen-p2-adam7` | — | — | — | 193.7 | 0.0 |
| `screen-p4-adam7` | — | — | — | 193.9 | 0.0 |
| `screen-p8-adam7` | — | — | — | 8.5 | 0.0 |
| `screen-p8a-adam7` | — | — | — | 16.9 | 0.0 |
| `screen-ga8-adam7` | — | — | — | 3.6 | 0.0 |
| `screen-ga16-adam7` | — | — | — | 3.7 | — |
| `screen-rgba8-adam7` | — | — | — | 3.7 | 0.0 |
| `screen-rgba16-adam7` | — | — | — | 3.7 | — |

Notes:

- `photo-g16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgb16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-ga16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgba16`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-g1-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-g1-adam7`: baseline: unsupported
- `photo-g2-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-g2-adam7`: baseline: unsupported
- `photo-g4-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-g4-adam7`: baseline: unsupported
- `photo-g8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-g8-adam7`: baseline: unsupported
- `photo-g16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-g16-adam7`: baseline: unsupported
- `photo-g16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgb8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-rgb8-adam7`: baseline: unsupported
- `photo-rgb16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-rgb16-adam7`: baseline: unsupported
- `photo-rgb16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-p1-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-p1-adam7`: baseline: unsupported
- `photo-p2-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-p2-adam7`: baseline: unsupported
- `photo-p4-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-p4-adam7`: baseline: unsupported
- `photo-p8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-p8-adam7`: baseline: unsupported
- `photo-p8a-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-p8a-adam7`: baseline: unsupported
- `photo-ga8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-ga8-adam7`: baseline: unsupported
- `photo-ga16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-ga16-adam7`: baseline: unsupported
- `photo-ga16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `photo-rgba8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-rgba8-adam7`: baseline: unsupported
- `photo-rgba16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `photo-rgba16-adam7`: baseline: unsupported
- `photo-rgba16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-g16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgb16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-ga16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgba16`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-g1-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-g1-adam7`: baseline: unsupported
- `screen-g2-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-g2-adam7`: baseline: unsupported
- `screen-g4-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-g4-adam7`: baseline: unsupported
- `screen-g8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-g8-adam7`: baseline: unsupported
- `screen-g16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-g16-adam7`: baseline: unsupported
- `screen-g16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgb8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-rgb8-adam7`: baseline: unsupported
- `screen-rgb16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-rgb16-adam7`: baseline: unsupported
- `screen-rgb16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-p1-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-p1-adam7`: baseline: unsupported
- `screen-p2-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-p2-adam7`: baseline: unsupported
- `screen-p4-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-p4-adam7`: baseline: unsupported
- `screen-p8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-p8-adam7`: baseline: unsupported
- `screen-p8a-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-p8a-adam7`: baseline: unsupported
- `screen-ga8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-ga8-adam7`: baseline: unsupported
- `screen-ga16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-ga16-adam7`: baseline: unsupported
- `screen-ga16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed
- `screen-rgba8-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-rgba8-adam7`: baseline: unsupported
- `screen-rgba16-adam7`: Sdcb.SimdCodecs.Png: unsupported (Adam7)
- `screen-rgba16-adam7`: baseline: unsupported
- `screen-rgba16-adam7`: libpng: output differs from ImageSharp by up to 72, not timed

### Encode (in-process medians of 7 interleaved rounds, ms; sizes in KB)

`current` = `PngWriter.Encode*` with `PngWriteOptions.CompressionLevel`, one thread; ImageSharp = `PngEncoder` at the same zlib level (store 0, fastest 1, optimal 6, smallest 9), adaptive filters, one thread; libpng = simplified API `png_image_write_to_memory`, which has no level setting, so it appears on the optimal rows (its default). Every output is decoded and must reproduce the samples exactly.

| Case | current | KB | baseline | ImageSharp | KB | libpng | KB | time ÷ ImageSharp | size ÷ ImageSharp | time ÷ libpng | size ÷ libpng | time ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 2.154 | 2026 | 4.281 | 3.683 | 2027 | — | — | 0.58× | 1.000 | — | — | 0.50× |
| `photo-gray8-fastest` | 38.5 | 1899 | 41.1 | 38.3 | 1336 | — | — | 1.00× | 1.421 | — | — | 0.94× |
| `photo-gray8-optimal` | 57.7 | 1253 | 59.3 | 74.6 | 1297 | 104.3 | 1201 | 0.77× | 0.966 | 0.55× | 1.043 | 0.97× |
| `photo-gray8-smallest` | 84.7 | 1318 | 86.2 | 75.0 | 1297 | — | — | 1.13× | 1.016 | — | — | 0.98× |
| `photo-gray16-store` | 3.873 | 4052 | 11.3 | 7.985 | 4052 | — | — | 0.48× | 1.000 | — | — | 0.34× |
| `photo-gray16-fastest` | 83.6 | 4274 | 88.1 | 107.5 | 3618 | — | — | 0.78× | 1.181 | — | — | 0.95× |
| `photo-gray16-optimal` | 136.2 | 3577 | 140.2 | 116.5 | 3588 | 137.3 | 3581 | 1.17× | 0.997 | 0.99× | 0.999 | 0.97× |
| `photo-gray16-smallest` | 136.5 | 3616 | 141.4 | 116.3 | 3588 | — | — | 1.17× | 1.008 | — | — | 0.96× |
| `photo-rgb8-store` | 6.507 | 6077 | 17.4 | 10.3 | 6078 | — | — | 0.63× | 1.000 | — | — | 0.37× |
| `photo-rgb8-fastest` | 90.7 | 4860 | 96.8 | 99.2 | 3414 | — | — | 0.91× | 1.423 | — | — | 0.94× |
| `photo-rgb8-optimal` | 149.1 | 3304 | 153.4 | 396.7 | 3309 | 517.8 | 3404 | 0.38× | 0.998 | 0.29× | 0.970 | 0.97× |
| `photo-rgb8-smallest` | 361.5 | 3273 | 365.2 | 530.7 | 3299 | — | — | 0.68× | 0.992 | — | — | 0.99× |
| `photo-rgb16-store` | 11.8 | 12153 | 27.0 | 25.3 | 12155 | — | — | 0.47× | 1.000 | — | — | 0.44× |
| `photo-rgb16-fastest` | 238.8 | 12803 | 253.5 | 325.0 | 10752 | — | — | 0.73× | 1.191 | — | — | 0.94× |
| `photo-rgb16-optimal` | 400.1 | 10752 | 412.1 | 369.4 | 10762 | 426.2 | 10767 | 1.08× | 0.999 | 0.94× | 0.999 | 0.97× |
| `photo-rgb16-smallest` | 440.8 | 10735 | 447.9 | 389.9 | 10762 | — | — | 1.13× | 0.997 | — | — | 0.98× |
| `photo-rgba8-store` | 8.072 | 8102 | 20.0 | 13.5 | 8104 | — | — | 0.60× | 1.000 | — | — | 0.40× |
| `photo-rgba8-fastest` | 111.2 | 5643 | 119.2 | 129.6 | 4051 | — | — | 0.86× | 1.393 | — | — | 0.93× |
| `photo-rgba8-optimal` | 240.9 | 3805 | 245.1 | 632.5 | 3809 | 776.7 | 3911 | 0.38× | 0.999 | 0.31× | 0.973 | 0.98× |
| `photo-rgba8-smallest` | 585.5 | 3787 | 590.9 | 1041.9 | 3788 | — | — | 0.56× | 1.000 | — | — | 0.99× |
| `photo-rgba16-store` | 14.2 | 16204 | 35.0 | 28.5 | 16206 | — | — | 0.50× | 1.000 | — | — | 0.41× |
| `photo-rgba16-fastest` | 315.7 | 16819 | 335.8 | 419.5 | 13699 | — | — | 0.75× | 1.228 | — | — | 0.94× |
| `photo-rgba16-optimal` | 527.9 | 13676 | 542.2 | 605.9 | 13672 | — | — | 0.87× | 1.000 | — | — | 0.97× |
| `photo-rgba16-smallest` | 623.8 | 13680 | 641.2 | 611.5 | 13672 | — | — | 1.02× | 1.001 | — | — | 0.97× |
| `screen-gray8-store` | 2.111 | 2026 | 4.584 | 3.038 | 2027 | — | — | 0.69× | 1.000 | — | — | 0.46× |
| `screen-gray8-fastest` | 12.4 | 115 | 12.6 | 5.610 | 83 | — | — | 2.22× | 1.388 | — | — | 0.98× |
| `screen-gray8-optimal` | 15.5 | 60 | 15.9 | 21.1 | 59 | 28.2 | 59 | 0.74× | 1.019 | 0.55× | 1.025 | 0.98× |
| `screen-gray8-smallest` | 45.6 | 57 | 46.0 | 120.5 | 57 | — | — | 0.38× | 0.999 | — | — | 0.99× |
| `screen-gray16-store` | 3.866 | 4052 | 12.4 | 7.170 | 4052 | — | — | 0.54× | 1.000 | — | — | 0.31× |
| `screen-gray16-fastest` | 24.0 | 195 | 24.7 | 10.8 | 130 | — | — | 2.23× | 1.493 | — | — | 0.97× |
| `screen-gray16-optimal` | 30.2 | 83 | 30.8 | 32.1 | 83 | 45.5 | 84 | 0.94× | 0.998 | 0.66× | 0.987 | 0.98× |
| `screen-gray16-smallest` | 76.9 | 82 | 77.7 | 148.0 | 81 | — | — | 0.52× | 1.005 | — | — | 0.99× |
| `screen-rgb8-store` | 6.054 | 6077 | 15.5 | 9.448 | 6078 | — | — | 0.64× | 1.000 | — | — | 0.39× |
| `screen-rgb8-fastest` | 34.9 | 186 | 36.4 | 13.1 | 133 | — | — | 2.68× | 1.398 | — | — | 0.96× |
| `screen-rgb8-optimal` | 40.7 | 78 | 41.5 | 40.0 | 81 | 60.3 | 82 | 1.02× | 0.965 | 0.67× | 0.955 | 0.98× |
| `screen-rgb8-smallest` | 83.1 | 76 | 84.1 | 184.3 | 77 | — | — | 0.45× | 0.982 | — | — | 0.99× |
| `screen-rgb16-store` | 11.5 | 12153 | 27.7 | 23.6 | 12155 | — | — | 0.49× | 1.000 | — | — | 0.42× |
| `screen-rgb16-fastest` | 67.6 | 298 | 72.1 | 26.2 | 176 | — | — | 2.58× | 1.696 | — | — | 0.94× |
| `screen-rgb16-optimal` | 77.6 | 96 | 79.1 | 63.2 | 97 | 104.2 | 100 | 1.23× | 0.984 | 0.74× | 0.961 | 0.98× |
| `screen-rgb16-smallest` | 131.3 | 90 | 133.3 | 258.3 | 91 | — | — | 0.51× | 0.988 | — | — | 0.99× |
| `screen-rgba8-store` | 7.136 | 8102 | 21.0 | 12.6 | 8104 | — | — | 0.57× | 1.000 | — | — | 0.34× |
| `screen-rgba8-fastest` | 46.5 | 250 | 48.7 | 16.2 | 151 | — | — | 2.88× | 1.657 | — | — | 0.95× |
| `screen-rgba8-optimal` | 54.0 | 85 | 55.3 | 47.7 | 85 | 75.2 | 87 | 1.13× | 0.998 | 0.72× | 0.980 | 0.98× |
| `screen-rgba8-smallest` | 97.7 | 82 | 98.7 | 233.9 | 83 | — | — | 0.42× | 0.989 | — | — | 0.99× |
| `screen-rgba16-store` | 14.6 | 16204 | 36.4 | 30.8 | 16206 | — | — | 0.47× | 1.000 | — | — | 0.40× |
| `screen-rgba16-fastest` | 91.6 | 341 | 98.3 | 34.1 | 214 | — | — | 2.68× | 1.596 | — | — | 0.93× |
| `screen-rgba16-optimal` | 102.7 | 103 | 106.2 | 78.4 | 106 | — | — | 1.31× | 0.980 | — | — | 0.97× |
| `screen-rgba16-smallest` | 181.0 | 99 | 185.2 | 293.8 | 99 | — | — | 0.62× | 0.996 | — | — | 0.98× |

Geometric means of current ÷ ImageSharp per compression level (time / size):

- store: time 0.55×, size 1.000×
- fastest: time 1.45×, size 1.413×
- optimal: time 0.86×, size 0.992×
- smallest: time 0.66×, size 0.998×

Opt-in parallel deflate (`ParallelFragments = 16`), not part of the single-thread comparison above:

| Case | current (1 thread) | parallel | speed-up | KB | size ÷ 1 thread |
|---|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 2.154 | 3.118 | 0.69× | 2026 | 1.000 |
| `photo-gray8-fastest` | 38.5 | 10.7 | 3.59× | 1903 | 1.002 |
| `photo-gray8-optimal` | 57.7 | 13.9 | 4.16× | 1252 | 1.000 |
| `photo-gray8-smallest` | 84.7 | 21.6 | 3.92× | 1319 | 1.001 |
| `photo-gray16-store` | 3.873 | 6.288 | 0.62× | 4052 | 1.000 |
| `photo-gray16-fastest` | 83.6 | 13.2 | 6.34× | 4274 | 1.000 |
| `photo-gray16-optimal` | 136.2 | 17.0 | 8.01× | 3577 | 1.000 |
| `photo-gray16-smallest` | 136.5 | 18.4 | 7.43× | 3616 | 1.000 |
| `photo-rgb8-store` | 6.507 | 8.449 | 0.77× | 6077 | 1.000 |
| `photo-rgb8-fastest` | 90.7 | 14.6 | 6.20× | 4871 | 1.002 |
| `photo-rgb8-optimal` | 149.1 | 18.5 | 8.04× | 3315 | 1.003 |
| `photo-rgb8-smallest` | 361.5 | 44.8 | 8.08× | 3286 | 1.004 |
| `photo-rgb16-store` | 11.8 | 14.9 | 0.79× | 12153 | 1.000 |
| `photo-rgb16-fastest` | 238.8 | 35.6 | 6.72× | 12804 | 1.000 |
| `photo-rgb16-optimal` | 400.1 | 46.4 | 8.62× | 10753 | 1.000 |
| `photo-rgb16-smallest` | 440.8 | 52.7 | 8.37× | 10737 | 1.000 |
| `photo-rgba8-store` | 8.072 | 11.5 | 0.70× | 8102 | 1.000 |
| `photo-rgba8-fastest` | 111.2 | 17.0 | 6.54× | 5652 | 1.002 |
| `photo-rgba8-optimal` | 240.9 | 28.7 | 8.38× | 3818 | 1.003 |
| `photo-rgba8-smallest` | 585.5 | 69.1 | 8.47× | 3801 | 1.004 |
| `photo-rgba16-store` | 14.2 | 19.2 | 0.74× | 16204 | 1.000 |
| `photo-rgba16-fastest` | 315.7 | 47.3 | 6.67× | 16822 | 1.000 |
| `photo-rgba16-optimal` | 527.9 | 59.3 | 8.90× | 13676 | 1.000 |
| `photo-rgba16-smallest` | 623.8 | 77.3 | 8.07× | 13683 | 1.000 |
| `screen-gray8-store` | 2.111 | 2.870 | 0.74× | 2026 | 1.000 |
| `screen-gray8-fastest` | 12.4 | 2.929 | 4.25× | 115 | 1.001 |
| `screen-gray8-optimal` | 15.5 | 3.369 | 4.61× | 60 | 0.995 |
| `screen-gray8-smallest` | 45.6 | 10.3 | 4.40× | 57 | 1.002 |
| `screen-gray16-store` | 3.866 | 4.705 | 0.82× | 4052 | 1.000 |
| `screen-gray16-fastest` | 24.0 | 3.933 | 6.11× | 195 | 1.002 |
| `screen-gray16-optimal` | 30.2 | 4.610 | 6.55× | 83 | 0.998 |
| `screen-gray16-smallest` | 76.9 | 10.8 | 7.12× | 82 | 1.001 |
| `screen-rgb8-store` | 6.054 | 7.872 | 0.77× | 6077 | 1.000 |
| `screen-rgb8-fastest` | 34.9 | 5.496 | 6.36× | 186 | 1.001 |
| `screen-rgb8-optimal` | 40.7 | 6.012 | 6.77× | 78 | 1.002 |
| `screen-rgb8-smallest` | 83.1 | 10.6 | 7.87× | 76 | 1.003 |
| `screen-rgb16-store` | 11.5 | 14.6 | 0.79× | 12153 | 1.000 |
| `screen-rgb16-fastest` | 67.6 | 10.5 | 6.43× | 298 | 1.000 |
| `screen-rgb16-optimal` | 77.6 | 11.5 | 6.77× | 96 | 1.004 |
| `screen-rgb16-smallest` | 131.3 | 16.9 | 7.75× | 90 | 1.005 |
| `screen-rgba8-store` | 7.136 | 10.1 | 0.71× | 8102 | 1.000 |
| `screen-rgba8-fastest` | 46.5 | 7.138 | 6.51× | 250 | 0.999 |
| `screen-rgba8-optimal` | 54.0 | 7.946 | 6.80× | 86 | 1.004 |
| `screen-rgba8-smallest` | 97.7 | 12.5 | 7.82× | 83 | 1.007 |
| `screen-rgba16-store` | 14.6 | 19.0 | 0.77× | 16204 | 1.000 |
| `screen-rgba16-fastest` | 91.6 | 14.1 | 6.49× | 341 | 1.000 |
| `screen-rgba16-optimal` | 102.7 | 15.0 | 6.83× | 104 | 1.003 |
| `screen-rgba16-smallest` | 181.0 | 23.5 | 7.70× | 99 | 1.005 |

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool and libpng allocates natively, so neither shows here: compare working sets with `peakmem`.

| Case | current | parallel | baseline | ImageSharp | libpng |
|---|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 6136.7 | 7410.4 | 6136.7 | 6142.2 | — |
| `photo-gray8-fastest` | 5717.6 | 7291.6 | 5717.6 | 5512.4 | — |
| `photo-gray8-optimal` | 5362.9 | 4581.0 | 5362.9 | 5490.8 | 1200.9 |
| `photo-gray8-smallest` | 5427.9 | 4830.6 | 5427.9 | 5490.8 | — |
| `photo-gray16-store` | 12279.5 | 13375.6 | 12279.5 | 12380.7 | — |
| `photo-gray16-fastest` | 20042.4 | 14072.1 | 20042.4 | 12190.2 | — |
| `photo-gray16-optimal` | 11805.0 | 11573.0 | 11805.0 | 12193.6 | 3581.1 |
| `photo-gray16-smallest` | 11844.3 | 11672.0 | 11844.3 | 12193.6 | — |
| `photo-rgb8-store` | 22539.9 | 19282.6 | 22539.9 | 22528.8 | — |
| `photo-rgb8-fastest` | 15965.5 | 15772.0 | 15965.5 | 11785.4 | — |
| `photo-rgb8-optimal` | 11531.6 | 11173.4 | 11531.6 | 11689.6 | 3404.3 |
| `photo-rgb8-smallest` | 11501.0 | 11071.2 | 11501.0 | 11678.3 | — |
| `photo-rgb16-store` | 45085.8 | 38556.4 | 45085.8 | 45153.9 | — |
| `photo-rgb16-fastest` | 45736.1 | 40529.7 | 45736.1 | 44413.3 | — |
| `photo-rgb16-optimal` | 43685.0 | 33426.3 | 43685.0 | 44561.1 | 10767.2 |
| `photo-rgb16-smallest` | 43667.7 | 33377.2 | 43667.7 | 44561.1 | — |
| `photo-rgba8-store` | 24565.2 | 25707.3 | 24565.2 | 24554.0 | — |
| `photo-rgba8-fastest` | 18441.9 | 18347.3 | 18441.9 | 12440.3 | — |
| `photo-rgba8-optimal` | 12033.0 | 13007.0 | 12033.0 | 12195.5 | 3911.4 |
| `photo-rgba8-smallest` | 12014.9 | 12983.9 | 12014.9 | 12173.6 | — |
| `photo-rgba16-store` | 49136.4 | 51405.9 | 49136.4 | 49204.1 | — |
| `photo-rgba16-fastest` | 82692.3 | 53537.5 | 82692.3 | 47495.2 | — |
| `photo-rgba16-optimal` | 46608.4 | 42870.1 | 46608.4 | 47640.4 | — |
| `photo-rgba16-smallest` | 46613.1 | 42860.6 | 46613.1 | 47640.3 | — |
| `screen-gray8-store` | 6136.7 | 7410.4 | 6136.7 | 6142.1 | — |
| `screen-gray8-fastest` | 370.8 | 511.4 | 370.8 | 346.5 | — |
| `screen-gray8-optimal` | 181.4 | 335.1 | 181.4 | 194.5 | 58.7 |
| `screen-gray8-smallest` | 178.5 | 326.8 | 178.5 | 192.7 | — |
| `screen-gray16-store` | 12279.5 | 13375.7 | 12279.5 | 12380.7 | — |
| `screen-gray16-fastest` | 707.0 | 910.6 | 707.0 | 754.9 | — |
| `screen-gray16-optimal` | 332.9 | 448.1 | 332.9 | 449.4 | 84.0 |
| `screen-gray16-smallest` | 331.7 | 444.4 | 331.7 | 446.5 | — |
| `screen-rgb8-store` | 22539.9 | 20464.1 | 22539.9 | 22528.8 | — |
| `screen-rgb8-fastest` | 698.5 | 824.7 | 698.5 | 657.6 | — |
| `screen-rgb8-optimal` | 327.9 | 444.6 | 327.9 | 345.8 | 81.6 |
| `screen-rgb8-smallest` | 326.0 | 434.5 | 326.0 | 342.5 | — |
| `screen-rgb16-store` | 45085.8 | 38556.4 | 45085.8 | 45153.9 | — |
| `screen-rgb16-fastest` | 1322.7 | 1416.6 | 1322.7 | 803.8 | — |
| `screen-rgb16-optimal` | 345.8 | 699.7 | 345.8 | 466.0 | 99.7 |
| `screen-rgb16-smallest` | 339.6 | 676.0 | 339.6 | 458.1 | — |
| `screen-rgba8-store` | 24565.2 | 25707.4 | 24565.2 | 24554.0 | — |
| `screen-rgba8-fastest` | 762.8 | 1102.4 | 762.8 | 676.6 | — |
| `screen-rgba8-optimal` | 335.2 | 533.6 | 335.2 | 350.5 | 86.9 |
| `screen-rgba8-smallest` | 332.2 | 521.0 | 332.2 | 348.3 | — |
| `screen-rgba16-store` | 49136.4 | 51405.8 | 49136.4 | 49204.1 | — |
| `screen-rgba16-fastest` | 1365.4 | 1715.4 | 1365.4 | 843.8 | — |
| `screen-rgba16-optimal` | 353.4 | 855.0 | 353.4 | 475.3 | — |
| `screen-rgba16-smallest` | 348.6 | 835.8 | 348.6 | 467.7 | — |

Notes:

- `photo-rgba16-optimal`: LibPng: output does not round-trip (samples differ), not timed
- `screen-rgba16-optimal`: LibPng: output does not round-trip (samples differ), not timed

### Peak working set per decode, MiB (one fresh process per run, median of 3)

"first" is the working-set rise during the first full-size operation, after a warm-up on a 16×16 image of the same case (JIT and library loading excluded) and with the operation's own inputs and outputs already allocated and touched — the 1920×1080 RGBA destination (7.91 MiB) is not part of it. "steady" is the rise of a second operation, pooled buffers already resident. Native and unmanaged memory counts, so libpng and ImageSharp are measured on the same footing as the managed decoders.

| Case | Current first / steady | CurrentNative first / steady | Baseline first / steady | ImageSharp first / steady | LibPng first / steady |
|---|---:|---:|---:|---:|---:|
| `photo-g1` | 0.05 / 0.02 | 1.91 / 2.00 | 0.05 / 0.02 | 8.36 / 0.02 | 0.07 / 0.06 |
| `photo-g8` | 0.06 / 0.02 | 1.91 / 2.00 | 0.07 / 0.02 | 8.29 / 0.02 | 0.09 / 0.02 |
| `photo-rgb8` | 0.06 / 0.02 | 5.87 / 5.95 | 0.06 / 0.02 | 7.89 / 0.02 | 0.08 / 0.03 |
| `photo-p8` | 0.07 / 0.02 | 1.92 / 2.00 | 0.06 / 0.02 | 8.15 / 0.02 | 0.07 / 0.03 |
| `photo-rgba8` | 0.07 / 0.02 | 7.85 / 7.93 | 0.06 / 0.02 | 8.19 / 0.02 | 0.08 / 0.03 |
| `photo-rgba16` | 0.06 / 0.02 | 15.75 / 15.86 | 0.06 / 0.02 | 8.23 / 0.49 | — |
| `screen-g1` | 0.05 / 0.02 | 1.91 / 2.00 | 0.05 / 0.02 | 8.37 / 0.02 | 0.09 / 0.04 |
| `screen-g8` | 0.06 / 0.04 | 1.91 / 2.02 | 0.06 / 0.02 | 8.26 / 0.02 | 0.09 / 0.02 |
| `screen-rgb8` | 0.05 / 0.04 | 5.86 / 5.97 | 0.06 / 0.02 | 8.12 / 0.02 | 0.07 / 0.02 |
| `screen-p8` | 0.06 / 0.02 | 1.91 / 2.00 | 0.05 / 0.02 | 7.94 / 0.02 | 0.08 / 0.02 |
| `screen-rgba8` | 0.06 / 0.04 | 7.84 / 7.95 | 0.07 / 0.02 | 8.15 / 0.02 | 0.09 / 0.03 |
| `screen-rgba16` | 0.06 / 0.02 | 15.75 / 15.84 | 0.06 / 0.02 | 8.19 / 0.02 | — |

### Peak working set per encode, MiB (one fresh process per run, median of 3)

"first" is the working-set rise during the first full-size operation, after a warm-up on a 16×16 image of the same case (JIT and library loading excluded) and with the operation's own inputs and outputs already allocated and touched — the source samples (and ImageSharp's source image) are not part of it, the encoded output is. "steady" is the rise of a second operation, pooled buffers already resident. Native and unmanaged memory counts, so libpng and ImageSharp are measured on the same footing as the managed decoders.

| Case | Current first / steady | CurrentParallel first / steady | Baseline first / steady | ImageSharp first / steady | LibPng first / steady |
|---|---:|---:|---:|---:|---:|
| `photo-rgb8-store` | 16.07 / 11.92 | 27.84 / 20.79 | 16.07 / 11.90 | 33.55 / 19.58 | — |
| `photo-rgb8-optimal` | 6.74 / 6.52 | 11.80 / 6.12 | 7.11 / 6.38 | 21.80 / 10.95 | 4.46 / 3.35 |
| `photo-rgba8-store` | 20.04 / 19.82 | 38.72 / 25.50 | 20.04 / 19.82 | 39.53 / 23.56 | — |
| `photo-rgba8-optimal` | 8.59 / 4.02 | 12.55 / 7.61 | 11.42 / 4.02 | 24.31 / 11.27 | 4.41 / 3.85 |
| `photo-rgba16-store` | 47.80 / 31.70 | 87.71 / 21.61 | 47.78 / 31.68 | 63.29 / 39.49 | — |
| `photo-rgba16-optimal` | 32.34 / 26.63 | 46.95 / 22.48 | 28.97 / 26.64 | 43.88 / 34.53 | — |
| `screen-rgb8-store` | 16.25 / 11.77 | 31.14 / 21.68 | 16.25 / 11.77 | 24.13 / 11.70 | — |
| `screen-rgb8-optimal` | 0.33 / 0.02 | 5.94 / 4.35 | 0.32 / 0.02 | 0.41 / 0.02 | 0.38 / 0.02 |
| `screen-rgba8-store` | 22.18 / 15.88 | 43.83 / 28.12 | 22.18 / 15.88 | 30.04 / 15.74 | — |
| `screen-rgba8-optimal` | 0.32 / 0.02 | 6.38 / 6.09 | 0.32 / 0.02 | 0.34 / 0.02 | 0.28 / 0.02 |
| `screen-rgba16-store` | 39.98 / 16.10 | 80.77 / 20.47 | 39.98 / 16.10 | 69.97 / 23.95 | — |
| `screen-rgba16-optimal` | 0.27 / 0.68 | 6.02 / 4.82 | 0.27 / 0.65 | 1.06 / 0.03 | — |
