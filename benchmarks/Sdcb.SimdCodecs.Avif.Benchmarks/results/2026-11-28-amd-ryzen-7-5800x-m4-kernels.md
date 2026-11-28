# AVIF decode benchmark, 2026-11-28, M4 kernels round (d248bc2)

Machine: AMD Ryzen 7 5800X 8-Core Processor, Windows 11 Pro 10.0.26200 x64, .NET 10.0.15, 16 logical CPUs, Vector128 / Vector256 true, Vector512 false. Competitors and conventions as in [the M4 results](2026-11-28-amd-ryzen-7-5800x-m4.md): libavif 1.4.2 with dav1d 1.5.5 in-process, Magick.NET in the multi-core table, medians in milliseconds.

This round went after the three targets that file left: deblocking, the colour paths still per pixel, and prediction. Along the way the stage ablation pointed at the inverse transform, loop restoration and zeroed `stackalloc` buffers, so those were taken too.

Commands, from the repository root (`dotnet run --project benchmarks/Sdcb.SimdCodecs.Avif.Benchmarks -c Release -- …`):

```
verify; equivalence; single --reps 5; multi --reps 7 --magick; anim --reps 5; memory
DOTNET_TieredPGO=0 ablate --reps 5;  DOTNET_TieredPGO=0 ablate --anim --reps 3
baseline 9433b49 + ab [--multi] [--anim]   (the cumulative A/B below; per-commit A/B against each predecessor)
```

## Summary

- **The round, same process against 9433b49 (byte-identical output):** single core 0.68x to 0.88x the time on stills (carter 12-bit 0.68, teatro 0.76, the rest 0.80 to 0.88) and 0.79x to 0.92x on animations; 16 threads 0.77x to 0.89x on grids, teatro, ui and the animations, unchanged on the images whose 16-thread time is the serial entropy decode of one tile.
- **Against libavif[dav1d], single core:** 1.96x to 2.75x its time on stills (was 2.4x to 4.0x), 1.48x to 3.93x on animations (was 1.44x to 4.44x). On 16 threads: grids at 0.31x, the 10-bit alpha photo level (0.95x), single-item images 1.29x to 2.29x.
- **Measurement caveat found this round:** Dynamic PGO splits processes into a fast and a slow mode for the entropy-bound images (A/A runs gave B/A 0.99 or 1.09 to 1.13 at random). Rows of the A/B history were decided with `DOTNET_TieredPGO=0`; `benchmarks/README.md` has the details.

## What changed

| Commit | Change | Single-core B/A (vs its predecessor) |
|---|---|---|
| 3d7fa62 | Deblocking: the four samples of an edge segment as Vector128 lanes; each decision (mask, hev, flat, flat2) a lane mask; ablation runs its cases round-robin | photos 0.90 to 0.94, mandelbrot anim 0.91 |
| 654f5d7 | Dequantization four coefficients at a time: only 24 bits of `|c|·q` survive the spec's mask, and the low 24 bits of a product depend only on the low bits of its factors, so the wrapping 32-bit product is exact | photos 0.92 to 0.95, lossless 0.93 |
| 30c7260 | 10- and 12-bit animation frames into RGBA8 in one pass (the high bytes of the RGBA16 sums), not RGBA16 then a narrowing pass | fuji anim 0.96 |
| 7986f2b | Single-reference integer-motion blocks copied straight from the reference into the frame (the filters are identities, the shifts cancel, the clamp is a no-op); the other integer copies vectorised | fuji anim 0.93, screen anim 0.93 |
| 8d2a2da | SSSE3 block warp: pmaddwd dot product per output, phaddd for four, int16 intermediate kept transposed (range checked against the filter table at run time) | mandelbrot anim 0.915 |
| b385c09 | Vector Paeth and chroma-from-luma; intra edges as plain copy-then-fill loops | ui 0.88, alpha 0.92 (PGO off) |
| 1b4cdf7 | 12-bit row transform vectorised: each butterfly input split as hi·4096 + lo keeps the 33-bit products exact in int32 (a struct type argument selects it, so 8/10-bit code is unchanged); kernel-vs-scalar tests for every new kernel | carter 0.85 |
| d248bc2 | Loop restoration from a gathered source block (per-row source choice once, sliding box sums, a2 by table, vector Wiener and SGR rows); `[SkipLocalsInit]` on audited hot methods | teatro 0.86 from loop restoration; mangrove 0.94, lossless 0.94, carter 0.92 from SkipLocalsInit |

Two scalar overflows surfaced and were fixed on the way, both outside the range real streams reach: the identity transform multiplied 20-bit inputs in int32 at 12-bit depth (the new twin tests caught it), and SGR's B term needs 33 bits at 12-bit depth.

Tried and dropped: building only the w / h intra edge samples non-directional modes read (no gain); `AggressiveOptimization` on `DecodeCoeffs` (removes the PGO split by pinning it slow, 6% worse); scanning for zero rows before dequantization (no gain until the loop itself was vectorised).

## Cumulative A/B (baseline 9433b49 vs d248bc2)

```
1T, DOTNET_TieredPGO=0                            A ms      B ms     B/A
perf-photo-mangrove-8192x6144-grid-8b.avif      1893.1    1591.7   0.841
perf-photo-almsee-4096x3072-grid-10b.avif        800.6     663.1   0.828
perf-photo-teatro-4096x2632-tiles-svt.avif       603.1     456.2   0.756
perf-photo-fuji-2560x1440-lossless-444.avif      850.9     736.5   0.866
perf-screen-ui-3840x2160-444-8b.avif             151.8     133.1   0.877
perf-photo-night-3840x2160-grain-10b.avif        602.6     506.9   0.841
perf-photo-carter-3840x2560-p2-12b-422.avif      888.6     606.8   0.683
perf-photo-fuji-2560x1440-alpha-10b.avif         272.6     225.7   0.828

1T, default PGO                                   A ms      B ms     B/A
perf-photo-mangrove-8192x6144-grid-8b.avif      1749.0    1406.6   0.804
perf-photo-almsee-4096x3072-grid-10b.avif        727.3     595.5   0.819
perf-photo-teatro-4096x2632-tiles-svt.avif       558.9     424.4   0.759
perf-photo-fuji-2560x1440-lossless-444.avif      789.6     694.2   0.879
perf-screen-ui-3840x2160-444-8b.avif             132.2     115.5   0.873
perf-photo-night-3840x2160-grain-10b.avif        553.4     473.0   0.855
perf-photo-carter-3840x2560-p2-12b-422.avif      799.4     565.5   0.707
perf-photo-fuji-2560x1440-alpha-10b.avif         259.2     206.1   0.795

16T                                               A ms      B ms     B/A
perf-photo-mangrove-8192x6144-grid-8b.avif       237.3     193.0   0.813
perf-photo-almsee-4096x3072-grid-10b.avif        113.2      98.6   0.872
perf-photo-teatro-4096x2632-tiles-svt.avif        91.4      70.2   0.768
perf-photo-fuji-2560x1440-lossless-444.avif      219.1     214.3   0.978
perf-screen-ui-3840x2160-444-8b.avif              54.3      48.3   0.890
perf-photo-night-3840x2160-grain-10b.avif        153.0     152.7   0.998
perf-photo-carter-3840x2560-p2-12b-422.avif      158.5     162.1   1.022
perf-photo-fuji-2560x1440-alpha-10b.avif          67.3      66.2   0.983

Animations, every frame                           A ms      B ms     B/A
1T  anim-photo-fuji-1920x1080-30f-10b.avif       748.8     692.2   0.924
1T  anim-mandelbrot-1280x720-60f-inter.avif     1854.4    1458.4   0.786
1T  anim-screen-testsrc2-1280x720-24f-444.avif   462.4     406.0   0.878
16T anim-photo-fuji-1920x1080-30f-10b.avif       561.2     530.8   0.946
16T anim-mandelbrot-1280x720-60f-inter.avif     1395.2    1211.2   0.868
16T anim-screen-testsrc2-1280x720-24f-444.avif   382.6     339.1   0.887
```

Night, carter and alpha do not move at 16 threads: their 16-thread time is one tile's entropy decode, which none of this touched.

## Single core (pinned to CPU 2, one thread on every decoder)

```
fixture                                       ours YUV  ours RGBA  dav1d YUV  dav1d RGBA   ratio  alloc MB
perf-photo-mangrove-8192x6144-grid-8b.avif      1379.5     1402.7      450.0       555.5   2.53x     524.6
perf-photo-almsee-4096x3072-grid-10b.avif        578.9      563.6      220.1       288.0   1.96x     136.5
perf-photo-teatro-4096x2632-tiles-svt.avif       416.8      407.6      134.0       152.7   2.67x      93.7
perf-photo-fuji-2560x1440-lossless-444.avif      656.0      714.6      266.6       270.8   2.64x      57.9
perf-screen-ui-3840x2160-444-8b.avif             134.9      112.5       36.1        40.8   2.75x     113.4
perf-photo-night-3840x2160-grain-10b.avif        493.9      476.9      171.6       215.9   2.21x     125.7
perf-photo-carter-3840x2560-p2-12b-422.avif      533.0      567.6      214.2       206.4   2.75x     100.4
perf-photo-fuji-2560x1440-alpha-10b.avif         186.8      196.0       67.4        87.6   2.24x      59.8
```

## Multi-core (16T)

```
fixture                                       ours ms  dav1d ms   ratio  magick ms  alloc MB  ours MP/s  dav1d MP/s
perf-photo-mangrove-8192x6144-grid-8b.avif      214.2     697.8   0.31x     2514.8     528.0      235.0        72.1
perf-photo-almsee-4096x3072-grid-10b.avif       129.1     413.4   0.31x      874.8     136.7       97.5        30.4
perf-photo-teatro-4096x2632-tiles-svt.avif      106.8      57.6   1.85x     1384.6      98.3      100.9       187.1
perf-photo-fuji-2560x1440-lossless-444.avif     239.3     114.6   2.09x     1030.1      57.9       15.4        32.2
perf-screen-ui-3840x2160-444-8b.avif             54.8      23.9   2.29x      510.0     115.9      151.3       346.6
perf-photo-night-3840x2160-grain-10b.avif       195.4     151.2   1.29x     1152.4     125.7       42.5        54.9
perf-photo-carter-3840x2560-p2-12b-422.avif     150.9      82.3   1.83x     1474.8     100.4       65.1       119.5
perf-photo-fuji-2560x1440-alpha-10b.avif         59.7      62.8   0.95x      581.7      59.9       61.7        58.7
```

The machine ran warmer during this table than in the M4 file (dav1d's own times are 10 to 40% higher in both columns); compare ratios, and take speed-ups from the A/B above.

## Animated sequences (every frame to RGBA through the frame reader)

```
fixture                                      frames   ours 1T  dav1d 1T  ratio 1T   ours MT  dav1d MT  alloc MB
anim-photo-fuji-1920x1080-30f-10b.avif           30     900.0     608.5     1.48x     605.2     613.1     852.7
anim-mandelbrot-1280x720-60f-inter.avif          60    1424.6     455.3     3.13x    1452.4     458.2     593.6
anim-screen-testsrc2-1280x720-24f-444.avif       24     408.7     104.0     3.93x     337.7     104.2     354.4
```

## Peak memory

Unchanged by this round (it did not touch buffer management); private commit above a warmed-up baseline, one decode, fresh process per cell, output included on both sides:

```
fixture                                       RGBA MB  ours 1T  dav1d 1T ours 16T dav1d 16T
perf-photo-mangrove-8192x6144-grid-8b.avif      192.0    429.4     268.2    392.1     272.7
perf-photo-almsee-4096x3072-grid-10b.avif        48.0    130.8      85.9    145.9      90.6
perf-photo-teatro-4096x2632-tiles-svt.avif       41.1    201.2      62.8    211.2      68.8
perf-photo-fuji-2560x1440-lossless-444.avif      14.1     66.1      27.7    181.1      32.9
perf-screen-ui-3840x2160-444-8b.avif             31.6    119.4      59.5    119.1      67.3
perf-photo-night-3840x2160-grain-10b.avif        31.6    131.0      85.1    205.3      91.6
perf-photo-carter-3840x2560-p2-12b-422.avif      37.5    168.2     101.0    223.8     108.1
perf-photo-fuji-2560x1440-alpha-10b.avif         14.1     65.6      36.6     94.3      48.1
anim-photo-fuji-1920x1080-30f-10b.avif            7.9    238.7      75.0    254.2      79.6
anim-mandelbrot-1280x720-60f-inter.avif           3.5    120.2      17.9    172.0      24.4
anim-screen-testsrc2-1280x720-24f-444.avif        3.5    196.5      32.9    158.2      38.1
```

## Where the time goes now (single core, `DOTNET_TieredPGO=0`, cases round-robin)

```
fixture                                      full   -intra   -inter      -tx     -lpf    -cdef      -lr   -grain   -color    parse
perf-photo-mangrove-8192x6144-grid-8b.avif   1495.8   1419.8   1472.2   1315.9   1330.6   1415.7   1487.3   1531.0   1408.9    837.5
  share %                                             5.1%     1.6%    12.0%    11.0%     5.4%     0.6%    -2.4%     5.8%    44.0%
perf-photo-almsee-4096x3072-grid-10b.avif    738.8    642.5    671.7    573.1    643.2    674.6    693.0    669.9    623.2    436.1
  share %                                            13.0%     9.1%    22.4%    12.9%     8.7%     6.2%     9.3%    15.6%    41.0%
perf-photo-teatro-4096x2632-tiles-svt.avif    483.8    450.9    500.8    404.1    455.6    447.7    461.9    479.2    488.8    264.3
  share %                                             6.8%    -3.5%    16.5%     5.8%     7.5%     4.5%     1.0%    -1.0%    45.4%
perf-photo-fuji-2560x1440-lossless-444.avif    832.6    765.4    818.5    742.1    837.6    845.8    833.8    830.5    770.9    616.7
  share %                                             8.1%     1.7%    10.9%    -0.6%    -1.6%    -0.1%     0.3%     7.4%    25.9%
perf-screen-ui-3840x2160-444-8b.avif        175.2    148.3    154.6    160.9    199.5    170.1    167.5    181.5    190.4    113.5
  share %                                            15.3%    11.7%     8.1%   -13.9%     2.9%     4.4%    -3.6%    -8.7%    35.2%
perf-photo-night-3840x2160-grain-10b.avif    577.8    514.6    555.2    480.4    544.0    601.2    561.1    484.7    601.3    335.2
  share %                                            10.9%     3.9%    16.9%     5.8%    -4.0%     2.9%    16.1%    -4.1%    42.0%
perf-photo-carter-3840x2560-p2-12b-422.avif    692.1    675.3    788.1    724.1    611.1    700.9    712.6    691.9    673.8    403.4
  share %                                             2.4%   -13.9%    -4.6%    11.7%    -1.3%    -3.0%     0.0%     2.6%    41.7%
perf-photo-fuji-2560x1440-alpha-10b.avif    205.4    190.5    210.8    179.3    184.7    205.0    204.5    205.3    194.0    118.9
  share %                                             7.2%    -2.6%    12.7%    10.1%     0.2%     0.4%     0.0%     5.6%    42.1%

anim-photo-fuji-1920x1080-30f-10b.avif      753.9    714.8    593.8    687.7    718.2    664.5    718.3    736.1    639.5    430.2
  share %                                             5.2%    21.2%     8.8%     4.7%    11.9%     4.7%     2.4%    15.2%    42.9%
anim-mandelbrot-1280x720-60f-inter.avif    1466.6   1405.2   1148.9   1354.9   1260.2   1301.5   1445.5   1454.0   1382.9    678.8
  share %                                             4.2%    21.7%     7.6%    14.1%    11.3%     1.4%     0.9%     5.7%    53.7%
anim-screen-testsrc2-1280x720-24f-444.avif    392.7    427.5    312.7    376.6    389.3    349.0    400.7    392.8    393.0    221.4
  share %                                            -8.9%    20.4%     4.1%     0.9%    11.1%    -2.0%    -0.0%    -0.1%    43.6%
```

This run was noisier than the A/B runs (the ui and carter rows have several shares beyond ±10% that no change explains); an earlier clean run of the same build put deblocking at 7 to 11% of photos and the transform at 10 to 16% (carter 30% before the 12-bit row pass). Entropy decoding (`parse`) is 35 to 54% of single-core time and is now the larger half on every image.

## Next targets

- Entropy decoding: 35 to 54% of single-core time and the whole 16-thread time of one-tile images. The arithmetic decoder is dependency-chain bound; what is left is less work around it (coefficient context derivation, the per-block mode info stores) and parallelism across tiles and frames.
- Deblocking (7 to 11%): half of its time is the per-edge decision code around the filter, not the filter; two adjacent edge segments per 8-lane vector and a per-block level map would cut both.
- Inter prediction in animations (20 to 22%): the remaining scalar paths are the generic scaled / edge-clamped filter and chroma blocks narrower than four samples; CDEF (11% of animations) runs its edge blocks through the scalar kernel.
- Memory: animations still allocate their planes per frame (3x to 10x dav1d's peak).
