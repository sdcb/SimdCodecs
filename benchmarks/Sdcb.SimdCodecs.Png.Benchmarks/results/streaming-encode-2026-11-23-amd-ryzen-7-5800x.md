# Streaming PNG encode, 2026-11-23

The `PngImageEncoder` streaming session (`streaming`, `streaming parallel`) against the buffered `PngWriter` call with the same options, and the encoder against the `baseline` build of main (533b03e), whose `PngWriter` code this change does not alter (geometric mean current ÷ baseline over the 48 cases: 1.005×; the scatter of single cases, 0.85–1.20×, is this machine's noise: other builds were running). `matrix encode --rounds 7`, synthetic sources.

- CPU: AMD Ryzen 7 5800X 8-Core Processor
- OS: Microsoft Windows 10.0.26200 (X64)
- .NET: .NET 10.0.15 (win-x64), process X64
- Logical processors: 16
- Vector128.IsHardwareAccelerated: True, Vector256.IsHardwareAccelerated: True, Vector512.IsHardwareAccelerated: False
- AVX2: True, AVX-512F: False
- Image size: 1920×1080
- `photo` source: synthetic (no JPEG corpus master found; run the JPEG benchmark's scripts/fetch-corpus.sh for a real photo)
- `screen` source: synthetic
- ImageSharp: 3.1.12
- libpng: not found (tried libpng16.dll, libpng.dll; set LIBPNG_PATH)
- Baseline: 533b03e2f35a Merge feature/streaming-foundation: streaming plumbing in Abstractions

### Encode (in-process medians of 7 interleaved rounds, ms; sizes in KB)

`current` = `PngWriter.Encode*` with `PngWriteOptions.CompressionLevel`, one thread; ImageSharp = `PngEncoder` at the same zlib level (store 0, fastest 1, optimal 6, smallest 9), adaptive filters, one thread; libpng = simplified API `png_image_write_to_memory`, which has no level setting, so it appears on the optimal rows (its default). Every output is decoded and must reproduce the samples exactly.

| Case | current | KB | baseline | ImageSharp | KB | libpng | KB | time ÷ ImageSharp | size ÷ ImageSharp | time ÷ libpng | size ÷ libpng | time ÷ baseline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 1.795 | 2026 | 1.850 | 3.417 | 2027 | — | — | 0.53× | 1.000 | — | — | 0.97× |
| `photo-gray8-fastest` | 38.2 | 1737 | 37.7 | 30.4 | 1174 | — | — | 1.26× | 1.479 | — | — | 1.01× |
| `photo-gray8-optimal` | 51.7 | 1095 | 52.4 | 85.3 | 1129 | — | — | 0.61× | 0.970 | — | — | 0.99× |
| `photo-gray8-smallest` | 101.4 | 1136 | 100.4 | 85.4 | 1129 | — | — | 1.19× | 1.007 | — | — | 1.01× |
| `photo-gray16-store` | 3.721 | 4052 | 4.041 | 7.991 | 4052 | — | — | 0.47× | 1.000 | — | — | 0.92× |
| `photo-gray16-fastest` | 85.5 | 4270 | 85.4 | 112.8 | 3524 | — | — | 0.76× | 1.212 | — | — | 1.00× |
| `photo-gray16-optimal` | 140.0 | 3492 | 145.4 | 126.6 | 3501 | — | — | 1.11× | 0.997 | — | — | 0.96× |
| `photo-gray16-smallest` | 144.2 | 3521 | 145.7 | 125.3 | 3501 | — | — | 1.15× | 1.006 | — | — | 0.99× |
| `photo-rgb8-store` | 7.257 | 6077 | 6.800 | 12.5 | 6078 | — | — | 0.58× | 1.000 | — | — | 1.07× |
| `photo-rgb8-fastest` | 87.2 | 4360 | 88.2 | 94.7 | 2984 | — | — | 0.92× | 1.461 | — | — | 0.99× |
| `photo-rgb8-optimal` | 129.2 | 2727 | 126.6 | 429.2 | 2703 | — | — | 0.30× | 1.009 | — | — | 1.02× |
| `photo-rgb8-smallest` | 386.6 | 2693 | 421.8 | 500.9 | 2695 | — | — | 0.77× | 0.999 | — | — | 0.92× |
| `photo-rgb16-store` | 12.1 | 12153 | 12.4 | 26.1 | 12155 | — | — | 0.47× | 1.000 | — | — | 0.98× |
| `photo-rgb16-fastest` | 276.8 | 12801 | 292.7 | 370.0 | 10602 | — | — | 0.75× | 1.207 | — | — | 0.95× |
| `photo-rgb16-optimal` | 445.7 | 10571 | 438.7 | 397.4 | 10578 | — | — | 1.12× | 0.999 | — | — | 1.02× |
| `photo-rgb16-smallest` | 513.5 | 10593 | 570.1 | 438.7 | 10578 | — | — | 1.17× | 1.001 | — | — | 0.90× |
| `photo-rgba8-store` | 8.451 | 8102 | 8.714 | 14.4 | 8104 | — | — | 0.59× | 1.000 | — | — | 0.97× |
| `photo-rgba8-fastest` | 114.2 | 5019 | 109.0 | 121.4 | 3424 | — | — | 0.94× | 1.466 | — | — | 1.05× |
| `photo-rgba8-optimal` | 298.4 | 3146 | 274.5 | 1021.5 | 3142 | — | — | 0.29× | 1.001 | — | — | 1.09× |
| `photo-rgba8-smallest` | 635.1 | 3120 | 629.3 | 1095.1 | 3117 | — | — | 0.58× | 1.001 | — | — | 1.01× |
| `photo-rgba16-store` | 15.6 | 16204 | 17.6 | 32.1 | 16206 | — | — | 0.49× | 1.000 | — | — | 0.89× |
| `photo-rgba16-fastest` | 347.9 | 16779 | 338.4 | 444.9 | 13529 | — | — | 0.78× | 1.240 | — | — | 1.03× |
| `photo-rgba16-optimal` | 576.9 | 13500 | 549.5 | 667.4 | 13508 | — | — | 0.86× | 0.999 | — | — | 1.05× |
| `photo-rgba16-smallest` | 679.6 | 13508 | 660.4 | 652.3 | 13508 | — | — | 1.04× | 1.000 | — | — | 1.03× |
| `screen-gray8-store` | 1.840 | 2026 | 1.819 | 3.180 | 2027 | — | — | 0.58× | 1.000 | — | — | 1.01× |
| `screen-gray8-fastest` | 15.9 | 115 | 18.6 | 7.077 | 83 | — | — | 2.24× | 1.388 | — | — | 0.85× |
| `screen-gray8-optimal` | 16.8 | 60 | 16.2 | 22.1 | 59 | — | — | 0.76× | 1.019 | — | — | 1.03× |
| `screen-gray8-smallest` | 47.0 | 57 | 46.5 | 129.5 | 57 | — | — | 0.36× | 0.999 | — | — | 1.01× |
| `screen-gray16-store` | 3.715 | 4052 | 3.628 | 7.774 | 4052 | — | — | 0.48× | 1.000 | — | — | 1.02× |
| `screen-gray16-fastest` | 24.4 | 195 | 24.1 | 11.1 | 130 | — | — | 2.20× | 1.493 | — | — | 1.01× |
| `screen-gray16-optimal` | 30.4 | 83 | 30.1 | 32.8 | 83 | — | — | 0.93× | 0.998 | — | — | 1.01× |
| `screen-gray16-smallest` | 77.1 | 82 | 76.0 | 153.6 | 81 | — | — | 0.50× | 1.005 | — | — | 1.01× |
| `screen-rgb8-store` | 5.496 | 6077 | 5.515 | — | — | — | — | — | — | — | — | 1.00× |
| `screen-rgb8-fastest` | 35.7 | 186 | 36.2 | — | — | — | — | — | — | — | — | 0.99× |
| `screen-rgb8-optimal` | 41.6 | 78 | 41.1 | — | — | — | — | — | — | — | — | 1.01× |
| `screen-rgb8-smallest` | 86.7 | 76 | 84.4 | 199.8 | 77 | — | — | 0.43× | 0.982 | — | — | 1.03× |
| `screen-rgb16-store` | 13.3 | 12153 | 14.0 | 29.3 | 12155 | — | — | 0.45× | 1.000 | — | — | 0.95× |
| `screen-rgb16-fastest` | 88.3 | 298 | 85.3 | 32.5 | 176 | — | — | 2.72× | 1.696 | — | — | 1.04× |
| `screen-rgb16-optimal` | 87.3 | 96 | 82.0 | 71.0 | 97 | — | — | 1.23× | 0.984 | — | — | 1.06× |
| `screen-rgb16-smallest` | 138.1 | 90 | 135.2 | 284.0 | 91 | — | — | 0.49× | 0.988 | — | — | 1.02× |
| `screen-rgba8-store` | 7.844 | 8102 | 7.147 | 13.6 | 8104 | — | — | 0.58× | 1.000 | — | — | 1.10× |
| `screen-rgba8-fastest` | 56.6 | 250 | 47.2 | 18.3 | 151 | — | — | 3.09× | 1.657 | — | — | 1.20× |
| `screen-rgba8-optimal` | 57.3 | 85 | 56.9 | 51.1 | 85 | — | — | 1.12× | 0.998 | — | — | 1.01× |
| `screen-rgba8-smallest` | 100.2 | 82 | 96.7 | 240.2 | 83 | — | — | 0.42× | 0.989 | — | — | 1.04× |
| `screen-rgba16-store` | 17.5 | 16204 | 17.1 | 36.0 | 16206 | — | — | 0.48× | 1.000 | — | — | 1.02× |
| `screen-rgba16-fastest` | 93.8 | 341 | 88.4 | 32.9 | 214 | — | — | 2.85× | 1.596 | — | — | 1.06× |
| `screen-rgba16-optimal` | 104.5 | 103 | 99.8 | 78.0 | 106 | — | — | 1.34× | 0.980 | — | — | 1.05× |
| `screen-rgba16-smallest` | 247.7 | 99 | 260.5 | 473.3 | 99 | — | — | 0.52× | 0.996 | — | — | 0.95× |

Geometric means of current ÷ ImageSharp per compression level (time / size):

- store: time 0.51×, size 1.000×
- fastest: time 1.44×, size 1.436×
- optimal: time 0.79×, size 0.996×
- smallest: time 0.66×, size 0.998×

Opt-in parallel deflate (`ParallelFragments = 16`), not part of the single-thread comparison above:

| Case | current (1 thread) | parallel | speed-up | KB | size ÷ 1 thread |
|---|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 1.795 | 2.756 | 0.65× | 2026 | 1.000 |
| `photo-gray8-fastest` | 38.2 | 8.401 | 4.55× | 1743 | 1.004 |
| `photo-gray8-optimal` | 51.7 | 11.7 | 4.42× | 1095 | 1.000 |
| `photo-gray8-smallest` | 101.4 | 20.7 | 4.89× | 1142 | 1.005 |
| `photo-gray16-store` | 3.721 | 5.340 | 0.70× | 4052 | 1.000 |
| `photo-gray16-fastest` | 85.5 | 18.7 | 4.59× | 4271 | 1.000 |
| `photo-gray16-optimal` | 140.0 | 24.5 | 5.72× | 3492 | 1.000 |
| `photo-gray16-smallest` | 144.2 | 25.6 | 5.63× | 3522 | 1.000 |
| `photo-rgb8-store` | 7.257 | 9.039 | 0.80× | 6077 | 1.000 |
| `photo-rgb8-fastest` | 87.2 | 19.6 | 4.45× | 4372 | 1.003 |
| `photo-rgb8-optimal` | 129.2 | 23.5 | 5.50× | 2748 | 1.008 |
| `photo-rgb8-smallest` | 386.6 | 73.7 | 5.25× | 2714 | 1.008 |
| `photo-rgb16-store` | 12.1 | 15.7 | 0.77× | 12153 | 1.000 |
| `photo-rgb16-fastest` | 276.8 | 55.7 | 4.97× | 12801 | 1.000 |
| `photo-rgb16-optimal` | 445.7 | 73.1 | 6.10× | 10571 | 1.000 |
| `photo-rgb16-smallest` | 513.5 | 90.5 | 5.67× | 10595 | 1.000 |
| `photo-rgba8-store` | 8.451 | 10.4 | 0.81× | 8102 | 1.000 |
| `photo-rgba8-fastest` | 114.2 | 25.5 | 4.48× | 5025 | 1.001 |
| `photo-rgba8-optimal` | 298.4 | 46.4 | 6.43× | 3164 | 1.006 |
| `photo-rgba8-smallest` | 635.1 | 75.2 | 8.45× | 3137 | 1.006 |
| `photo-rgba16-store` | 15.6 | 21.5 | 0.73× | 16204 | 1.000 |
| `photo-rgba16-fastest` | 347.9 | 71.3 | 4.88× | 16782 | 1.000 |
| `photo-rgba16-optimal` | 576.9 | 87.0 | 6.63× | 13500 | 1.000 |
| `photo-rgba16-smallest` | 679.6 | 98.9 | 6.87× | 13511 | 1.000 |
| `screen-gray8-store` | 1.840 | 2.491 | 0.74× | 2026 | 1.000 |
| `screen-gray8-fastest` | 15.9 | 3.841 | 4.14× | 115 | 1.001 |
| `screen-gray8-optimal` | 16.8 | 4.017 | 4.18× | 60 | 0.995 |
| `screen-gray8-smallest` | 47.0 | 10.9 | 4.33× | 57 | 1.002 |
| `screen-gray16-store` | 3.715 | 4.730 | 0.79× | 4052 | 1.000 |
| `screen-gray16-fastest` | 24.4 | 3.670 | 6.66× | 195 | 1.002 |
| `screen-gray16-optimal` | 30.4 | 4.556 | 6.68× | 83 | 0.998 |
| `screen-gray16-smallest` | 77.1 | 10.7 | 7.17× | 82 | 1.001 |
| `screen-rgb8-store` | 5.496 | 6.918 | 0.79× | 6077 | 1.000 |
| `screen-rgb8-fastest` | 35.7 | 7.608 | 4.70× | 186 | 1.001 |
| `screen-rgb8-optimal` | 41.6 | 7.314 | 5.69× | 78 | 1.002 |
| `screen-rgb8-smallest` | 86.7 | 11.6 | 7.46× | 76 | 1.003 |
| `screen-rgb16-store` | 13.3 | 18.4 | 0.73× | 12153 | 1.000 |
| `screen-rgb16-fastest` | 88.3 | 19.7 | 4.48× | 298 | 1.000 |
| `screen-rgb16-optimal` | 87.3 | 18.7 | 4.67× | 96 | 1.004 |
| `screen-rgb16-smallest` | 138.1 | 24.0 | 5.75× | 90 | 1.005 |
| `screen-rgba8-store` | 7.844 | 10.5 | 0.75× | 8102 | 1.000 |
| `screen-rgba8-fastest` | 56.6 | 12.0 | 4.71× | 250 | 0.999 |
| `screen-rgba8-optimal` | 57.3 | 12.5 | 4.59× | 86 | 1.004 |
| `screen-rgba8-smallest` | 100.2 | 14.0 | 7.18× | 83 | 1.007 |
| `screen-rgba16-store` | 17.5 | 23.2 | 0.75× | 16204 | 1.000 |
| `screen-rgba16-fastest` | 93.8 | 18.4 | 5.10× | 341 | 1.000 |
| `screen-rgba16-optimal` | 104.5 | 19.2 | 5.43× | 104 | 1.003 |
| `screen-rgba16-smallest` | 247.7 | 41.7 | 5.94× | 99 | 1.005 |

Streaming session (`PngImageEncoder.Create`, rows pushed 16 at a time, IDATs leaving per band) against the buffered `PngWriter` call with the same options: one thread, then `ParallelFragments = 16`:

| Case | current | streaming | time ÷ current | size ÷ current | parallel | streaming parallel | time ÷ parallel | size ÷ parallel |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 1.795 | 1.426 | 0.79× | 1.00018 | 2.756 | 2.165 | 0.79× | 1.00018 |
| `photo-gray8-fastest` | 38.2 | 37.8 | 0.99× | 1.00018 | 8.401 | 7.255 | 0.86× | 1.00018 |
| `photo-gray8-optimal` | 51.7 | 51.3 | 0.99× | 1.00018 | 11.7 | 11.6 | 0.99× | 1.00018 |
| `photo-gray8-smallest` | 101.4 | 100.7 | 0.99× | 1.00018 | 20.7 | 20.2 | 0.97× | 1.00017 |
| `photo-gray16-store` | 3.721 | 2.787 | 0.75× | 1.00018 | 5.340 | 3.940 | 0.74× | 1.00018 |
| `photo-gray16-fastest` | 85.5 | 84.3 | 0.99× | 1.00018 | 18.7 | 15.0 | 0.80× | 1.00018 |
| `photo-gray16-optimal` | 140.0 | 138.4 | 0.99× | 1.00018 | 24.5 | 23.4 | 0.95× | 1.00018 |
| `photo-gray16-smallest` | 144.2 | 147.7 | 1.02× | 1.00018 | 25.6 | 22.7 | 0.89× | 1.00018 |
| `photo-rgb8-store` | 7.257 | 5.169 | 0.71× | 1.00018 | 9.039 | 5.558 | 0.61× | 1.00018 |
| `photo-rgb8-fastest` | 87.2 | 91.8 | 1.05× | 1.00018 | 19.6 | 15.4 | 0.78× | 1.00018 |
| `photo-rgb8-optimal` | 129.2 | 127.1 | 0.98× | 1.00018 | 23.5 | 20.2 | 0.86× | 1.00018 |
| `photo-rgb8-smallest` | 386.6 | 400.6 | 1.04× | 1.00018 | 73.7 | 55.6 | 0.75× | 1.00018 |
| `photo-rgb16-store` | 12.1 | 9.102 | 0.75× | 1.00018 | 15.7 | 11.1 | 0.71× | 1.00018 |
| `photo-rgb16-fastest` | 276.8 | 310.4 | 1.12× | 1.00018 | 55.7 | 47.0 | 0.84× | 1.00018 |
| `photo-rgb16-optimal` | 445.7 | 457.1 | 1.03× | 1.00018 | 73.1 | 65.2 | 0.89× | 1.00018 |
| `photo-rgb16-smallest` | 513.5 | 567.8 | 1.11× | 1.00018 | 90.5 | 75.6 | 0.83× | 1.00018 |
| `photo-rgba8-store` | 8.451 | 5.623 | 0.67× | 1.00018 | 10.4 | 6.804 | 0.66× | 1.00018 |
| `photo-rgba8-fastest` | 114.2 | 106.3 | 0.93× | 1.00018 | 25.5 | 21.7 | 0.85× | 1.00018 |
| `photo-rgba8-optimal` | 298.4 | 313.4 | 1.05× | 1.00018 | 46.4 | 41.5 | 0.90× | 1.00018 |
| `photo-rgba8-smallest` | 635.1 | 626.6 | 0.99× | 1.00018 | 75.2 | 72.2 | 0.96× | 1.00018 |
| `photo-rgba16-store` | 15.6 | 12.1 | 0.77× | 1.00018 | 21.5 | 15.4 | 0.71× | 1.00018 |
| `photo-rgba16-fastest` | 347.9 | 342.4 | 0.98× | 1.00018 | 71.3 | 58.4 | 0.82× | 1.00018 |
| `photo-rgba16-optimal` | 576.9 | 555.4 | 0.96× | 1.00018 | 87.0 | 77.8 | 0.89× | 1.00018 |
| `photo-rgba16-smallest` | 679.6 | 673.4 | 0.99× | 1.00018 | 98.9 | 90.4 | 0.91× | 1.00018 |
| `screen-gray8-store` | 1.840 | 1.528 | 0.83× | 1.00018 | 2.491 | 2.140 | 0.86× | 1.00018 |
| `screen-gray8-fastest` | 15.9 | 17.1 | 1.07× | 1.00010 | 3.841 | 4.675 | 1.22× | 1.00010 |
| `screen-gray8-optimal` | 16.8 | 17.3 | 1.03× | 1.00000 | 4.017 | 4.494 | 1.12× | 1.00000 |
| `screen-gray8-smallest` | 47.0 | 47.0 | 1.00× | 1.00000 | 10.9 | 10.9 | 1.01× | 1.00000 |
| `screen-gray16-store` | 3.715 | 2.784 | 0.75× | 1.00018 | 4.730 | 3.815 | 0.81× | 1.00018 |
| `screen-gray16-fastest` | 24.4 | 24.4 | 1.00× | 1.00018 | 3.670 | 3.713 | 1.01× | 1.00018 |
| `screen-gray16-optimal` | 30.4 | 30.6 | 1.01× | 1.00014 | 4.556 | 4.604 | 1.01× | 1.00014 |
| `screen-gray16-smallest` | 77.1 | 77.5 | 1.01× | 1.00014 | 10.7 | 10.6 | 0.99× | 1.00014 |
| `screen-rgb8-store` | 5.496 | 4.149 | 0.75× | 1.00018 | 6.918 | 5.260 | 0.76× | 1.00018 |
| `screen-rgb8-fastest` | 35.7 | 35.8 | 1.00× | 1.00013 | 7.608 | 7.000 | 0.92× | 1.00013 |
| `screen-rgb8-optimal` | 41.6 | 41.3 | 0.99× | 1.00015 | 7.314 | 7.164 | 0.98× | 1.00015 |
| `screen-rgb8-smallest` | 86.7 | 86.7 | 1.00× | 1.00015 | 11.6 | 11.8 | 1.01× | 1.00015 |
| `screen-rgb16-store` | 13.3 | 9.399 | 0.71× | 1.00018 | 18.4 | 13.4 | 0.73× | 1.00018 |
| `screen-rgb16-fastest` | 88.3 | 93.0 | 1.05× | 1.00016 | 19.7 | 18.8 | 0.96× | 1.00016 |
| `screen-rgb16-optimal` | 87.3 | 87.5 | 1.00× | 1.00012 | 18.7 | 18.8 | 1.01× | 1.00012 |
| `screen-rgb16-smallest` | 138.1 | 139.6 | 1.01× | 1.00013 | 24.0 | 21.7 | 0.90× | 1.00013 |
| `screen-rgba8-store` | 7.844 | 5.741 | 0.73× | 1.00018 | 10.5 | 6.981 | 0.67× | 1.00018 |
| `screen-rgba8-fastest` | 56.6 | 53.8 | 0.95× | 1.00014 | 12.0 | 11.7 | 0.98× | 1.00014 |
| `screen-rgba8-optimal` | 57.3 | 58.4 | 1.02× | 1.00014 | 12.5 | 13.0 | 1.05× | 1.00014 |
| `screen-rgba8-smallest` | 100.2 | 98.9 | 0.99× | 1.00014 | 14.0 | 12.9 | 0.92× | 1.00014 |
| `screen-rgba16-store` | 17.5 | 11.9 | 0.68× | 1.00018 | 23.2 | 14.9 | 0.64× | 1.00018 |
| `screen-rgba16-fastest` | 93.8 | 95.2 | 1.01× | 1.00017 | 18.4 | 18.1 | 0.98× | 1.00017 |
| `screen-rgba16-optimal` | 104.5 | 105.6 | 1.01× | 1.00011 | 19.2 | 18.6 | 0.97× | 1.00011 |
| `screen-rgba16-smallest` | 247.7 | 302.6 | 1.22× | 1.00012 | 41.7 | 43.8 | 1.05× | 1.00012 |

Geometric mean streaming ÷ current: 0.938×
Geometric mean streaming parallel ÷ parallel: 0.876×

Opt-in lossless reduction (`LosslessReduction = true`: the smallest exact layout, found by one scan of the image), not part of the comparison above:

| Case | current | reduced | time ÷ current | layout | KB | size ÷ current |
|---|---:|---:|---:|---|---:|---:|
| `photo-gray8-store` | 1.795 | 1.923 | 1.07× | grey 8-bit | 2026 | 1.000 |
| `photo-gray8-fastest` | 38.2 | 38.3 | 1.00× | grey 8-bit | 1737 | 1.000 |
| `photo-gray8-optimal` | 51.7 | 52.4 | 1.01× | grey 8-bit | 1095 | 1.000 |
| `photo-gray8-smallest` | 101.4 | 101.4 | 1.00× | grey 8-bit | 1136 | 1.000 |
| `photo-gray16-store` | 3.721 | 4.146 | 1.11× | grey 16-bit | 4052 | 1.000 |
| `photo-gray16-fastest` | 85.5 | 86.0 | 1.01× | grey 16-bit | 4270 | 1.000 |
| `photo-gray16-optimal` | 140.0 | 146.3 | 1.04× | grey 16-bit | 3492 | 1.000 |
| `photo-gray16-smallest` | 144.2 | 147.4 | 1.02× | grey 16-bit | 3521 | 1.000 |
| `photo-rgb8-store` | 7.257 | 6.856 | 0.94× | RGB 8-bit | 6077 | 1.000 |
| `photo-rgb8-fastest` | 87.2 | 95.1 | 1.09× | RGB 8-bit | 4360 | 1.000 |
| `photo-rgb8-optimal` | 129.2 | 128.7 | 1.00× | RGB 8-bit | 2727 | 1.000 |
| `photo-rgb8-smallest` | 386.6 | 420.0 | 1.09× | RGB 8-bit | 2693 | 1.000 |
| `photo-rgb16-store` | 12.1 | 11.9 | 0.98× | RGB 16-bit | 12153 | 1.000 |
| `photo-rgb16-fastest` | 276.8 | 313.6 | 1.13× | RGB 16-bit | 12801 | 1.000 |
| `photo-rgb16-optimal` | 445.7 | 454.0 | 1.02× | RGB 16-bit | 10571 | 1.000 |
| `photo-rgb16-smallest` | 513.5 | 541.1 | 1.05× | RGB 16-bit | 10593 | 1.000 |
| `photo-rgba8-store` | 8.451 | 7.887 | 0.93× | RGBA 8-bit | 8102 | 1.000 |
| `photo-rgba8-fastest` | 114.2 | 114.6 | 1.00× | RGBA 8-bit | 5019 | 1.000 |
| `photo-rgba8-optimal` | 298.4 | 280.3 | 0.94× | RGBA 8-bit | 3146 | 1.000 |
| `photo-rgba8-smallest` | 635.1 | 627.8 | 0.99× | RGBA 8-bit | 3120 | 1.000 |
| `photo-rgba16-store` | 15.6 | 16.3 | 1.04× | RGBA 16-bit | 16204 | 1.000 |
| `photo-rgba16-fastest` | 347.9 | 340.3 | 0.98× | RGBA 16-bit | 16779 | 1.000 |
| `photo-rgba16-optimal` | 576.9 | 558.5 | 0.97× | RGBA 16-bit | 13500 | 1.000 |
| `photo-rgba16-smallest` | 679.6 | 669.4 | 0.99× | RGBA 16-bit | 13508 | 1.000 |
| `screen-gray8-store` | 1.840 | 1.968 | 1.07× | grey 8-bit | 2026 | 1.000 |
| `screen-gray8-fastest` | 15.9 | 18.7 | 1.18× | grey 8-bit | 115 | 1.000 |
| `screen-gray8-optimal` | 16.8 | 16.7 | 0.99× | grey 8-bit | 60 | 1.000 |
| `screen-gray8-smallest` | 47.0 | 47.4 | 1.01× | grey 8-bit | 57 | 1.000 |
| `screen-gray16-store` | 3.715 | 3.770 | 1.01× | grey 16-bit | 4052 | 1.000 |
| `screen-gray16-fastest` | 24.4 | 25.2 | 1.03× | grey 16-bit | 195 | 1.000 |
| `screen-gray16-optimal` | 30.4 | 30.9 | 1.01× | grey 16-bit | 83 | 1.000 |
| `screen-gray16-smallest` | 77.1 | 77.1 | 1.00× | grey 16-bit | 82 | 1.000 |
| `screen-rgb8-store` | 5.496 | 5.795 | 1.05× | RGB 8-bit | 6077 | 1.000 |
| `screen-rgb8-fastest` | 35.7 | 37.1 | 1.04× | RGB 8-bit | 186 | 1.000 |
| `screen-rgb8-optimal` | 41.6 | 43.7 | 1.05× | RGB 8-bit | 78 | 1.000 |
| `screen-rgb8-smallest` | 86.7 | 87.1 | 1.00× | RGB 8-bit | 76 | 1.000 |
| `screen-rgb16-store` | 13.3 | 9.774 | 0.73× | RGB 8-bit | 6077 | 0.500 |
| `screen-rgb16-fastest` | 88.3 | 48.0 | 0.54× | RGB 8-bit | 186 | 0.624 |
| `screen-rgb16-optimal` | 87.3 | 60.1 | 0.69× | RGB 8-bit | 78 | 0.813 |
| `screen-rgb16-smallest` | 138.1 | 93.1 | 0.67× | RGB 8-bit | 76 | 0.849 |
| `screen-rgba8-store` | 7.844 | 7.684 | 0.98× | RGBA 8-bit | 8102 | 1.000 |
| `screen-rgba8-fastest` | 56.6 | 50.1 | 0.89× | RGBA 8-bit | 250 | 1.000 |
| `screen-rgba8-optimal` | 57.3 | 64.6 | 1.13× | RGBA 8-bit | 85 | 1.000 |
| `screen-rgba8-smallest` | 100.2 | 99.6 | 0.99× | RGBA 8-bit | 82 | 1.000 |
| `screen-rgba16-store` | 17.5 | 14.2 | 0.81× | RGBA 8-bit | 8102 | 0.500 |
| `screen-rgba16-fastest` | 93.8 | 51.4 | 0.55× | RGBA 8-bit | 250 | 0.734 |
| `screen-rgba16-optimal` | 104.5 | 59.2 | 0.57× | RGBA 8-bit | 85 | 0.824 |
| `screen-rgba16-smallest` | 247.7 | 139.5 | 0.56× | RGBA 8-bit | 82 | 0.834 |

Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel buffers in its own unmanaged pool and libpng allocates natively, so neither shows here: compare working sets with `peakmem`.

| Case | current | parallel | reduced | streaming | streaming parallel | baseline | ImageSharp | libpng |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `photo-gray8-store` | 6136.8 | 7410.5 | 6141.1 | 1.2 | 4.1 | 6136.8 | 6142.1 | — |
| `photo-gray8-fastest` | 5347.9 | 5931.2 | 5352.2 | 1.2 | 4.2 | 5347.9 | 5335.9 | — |
| `photo-gray8-optimal` | 5205.3 | 3890.3 | 5209.6 | 1.2 | 4.2 | 5205.3 | 5294.8 | — |
| `photo-gray8-smallest` | 5246.6 | 4034.7 | 5250.9 | 1.2 | 4.3 | 5246.6 | 5294.8 | — |
| `photo-gray16-store` | 12279.6 | 13375.7 | 12282.8 | 1.2 | 7.3 | 12279.6 | 12380.7 | — |
| `photo-gray16-fastest` | 20001.2 | 14994.4 | 20004.5 | 1.2 | 7.3 | 20001.2 | 12084.5 | — |
| `photo-gray16-optimal` | 11719.4 | 11120.4 | 11722.6 | 1.2 | 7.2 | 11719.4 | 12104.0 | — |
| `photo-gray16-smallest` | 11749.1 | 11212.7 | 11752.4 | 1.2 | 7.3 | 11749.1 | 12104.0 | — |
| `photo-rgb8-store` | 22539.9 | 20621.8 | 22545.3 | 1.2 | 7.9 | 22539.9 | 22528.8 | — |
| `photo-rgb8-fastest` | 13350.4 | 22620.3 | 13355.8 | 1.2 | 8.0 | 13350.4 | 11336.3 | — |
| `photo-rgb8-optimal` | 10954.8 | 13611.7 | 10960.2 | 1.2 | 8.0 | 10954.8 | 11037.3 | — |
| `photo-rgb8-smallest` | 10921.0 | 13459.0 | 10926.4 | 1.2 | 7.9 | 10921.0 | 11028.0 | — |
| `photo-rgb16-store` | 45085.9 | 38556.5 | 45089.1 | 1.2 | 7.9 | 45085.9 | 45153.9 | — |
| `photo-rgb16-fastest` | 45733.9 | 40467.1 | 45737.1 | 1.2 | 8.0 | 45733.9 | 44258.2 | — |
| `photo-rgb16-optimal` | 43503.7 | 32354.6 | 43506.9 | 1.2 | 7.9 | 43503.7 | 44384.6 | — |
| `photo-rgb16-smallest` | 43526.1 | 33546.6 | 43529.3 | 1.2 | 8.1 | 43526.1 | 44384.6 | — |
| `photo-rgba8-store` | 24565.2 | 27492.6 | 24570.6 | 1.2 | 7.9 | 24565.2 | 24554.0 | — |
| `photo-rgba8-fastest` | 24470.8 | 16375.7 | 24476.2 | 1.2 | 7.9 | 24470.8 | 11791.3 | — |
| `photo-rgba8-optimal` | 11373.4 | 9551.9 | 11378.8 | 1.2 | 8.0 | 11373.4 | 11491.1 | — |
| `photo-rgba8-smallest` | 11347.4 | 9492.1 | 11352.8 | 1.2 | 8.2 | 11347.4 | 11464.9 | — |
| `photo-rgba16-store` | 49136.5 | 54556.4 | 49139.7 | 1.2 | 7.9 | 49136.5 | 49204.1 | — |
| `photo-rgba16-fastest` | 82652.1 | 53385.8 | 82655.4 | 1.2 | 8.0 | 82652.1 | 47310.9 | — |
| `photo-rgba16-optimal` | 46433.0 | 41661.4 | 46436.2 | 1.2 | 8.1 | 46433.0 | 47467.4 | — |
| `photo-rgba16-smallest` | 46441.0 | 41475.3 | 46444.2 | 1.2 | 8.0 | 46441.0 | 47467.4 | — |
| `screen-gray8-store` | 6136.8 | 7410.2 | 6140.6 | 1.2 | 4.1 | 6136.8 | 6142.1 | — |
| `screen-gray8-fastest` | 370.9 | 511.5 | 374.7 | 1.2 | 4.1 | 370.9 | 346.5 | — |
| `screen-gray8-optimal` | 181.5 | 335.2 | 185.3 | 1.2 | 4.3 | 181.5 | 194.5 | — |
| `screen-gray8-smallest` | 178.5 | 326.8 | 182.4 | 1.2 | 4.2 | 178.5 | 192.7 | — |
| `screen-gray16-store` | 12279.6 | 13375.5 | 12282.8 | 1.2 | 7.3 | 12279.6 | 12380.7 | — |
| `screen-gray16-fastest` | 707.1 | 910.7 | 710.3 | 1.2 | 7.3 | 707.1 | 754.9 | — |
| `screen-gray16-optimal` | 332.9 | 448.2 | 336.2 | 1.2 | 7.4 | 332.9 | 449.4 | — |
| `screen-gray16-smallest` | 331.7 | 579.8 | 335.0 | 1.2 | 7.4 | 331.7 | 446.5 | — |
| `screen-rgb8-store` | 22539.9 | 19282.5 | 22545.3 | 1.2 | 7.8 | 22539.9 | — | — |
| `screen-rgb8-fastest` | 698.6 | 824.6 | 704.0 | 1.2 | 8.2 | 698.6 | — | — |
| `screen-rgb8-optimal` | 328.0 | 613.6 | 333.4 | 1.2 | 8.1 | 328.0 | — | — |
| `screen-rgb8-smallest` | 326.1 | 434.5 | 331.5 | 1.2 | 8.1 | 326.1 | 342.5 | — |
| `screen-rgb16-store` | 45085.9 | 38556.6 | 22545.3 | 1.2 | 7.8 | 45085.9 | 45153.9 | — |
| `screen-rgb16-fastest` | 1322.7 | 1799.3 | 704.0 | 1.2 | 8.1 | 1322.7 | 803.8 | — |
| `screen-rgb16-optimal` | 345.9 | 1082.4 | 333.4 | 1.2 | 8.1 | 345.9 | 466.0 | — |
| `screen-rgb16-smallest` | 339.6 | 1059.0 | 331.5 | 1.2 | 7.9 | 339.6 | 458.1 | — |
| `screen-rgba8-store` | 24565.2 | 27282.7 | 24570.6 | 1.2 | 7.3 | 24565.2 | 24554.0 | — |
| `screen-rgba8-fastest` | 762.9 | 1357.7 | 768.3 | 1.2 | 8.2 | 762.9 | 676.6 | — |
| `screen-rgba8-optimal` | 335.3 | 533.6 | 340.7 | 1.2 | 8.0 | 335.3 | 350.5 | — |
| `screen-rgba8-smallest` | 332.3 | 776.4 | 337.7 | 1.2 | 8.2 | 332.3 | 348.3 | — |
| `screen-rgba16-store` | 49136.5 | 51406.1 | 24570.6 | 1.2 | 7.9 | 49136.5 | 49204.1 | — |
| `screen-rgba16-fastest` | 1365.5 | 2165.6 | 768.3 | 1.2 | 8.1 | 1365.5 | 843.8 | — |
| `screen-rgba16-optimal` | 353.5 | 1305.2 | 340.7 | 1.2 | 8.1 | 353.5 | 475.3 | — |
| `screen-rgba16-smallest` | 348.7 | 1286.1 | 337.7 | 1.2 | 8.1 | 348.7 | 467.7 | — |

Notes:

- `screen-rgb8-store`: ImageSharp: output does not round-trip (samples differ), not timed
- `screen-rgb8-fastest`: ImageSharp: output does not round-trip (samples differ), not timed
- `screen-rgb8-optimal`: ImageSharp: output does not round-trip (samples differ), not timed
