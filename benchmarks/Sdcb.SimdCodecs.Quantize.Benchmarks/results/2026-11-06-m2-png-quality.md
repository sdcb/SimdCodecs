# M2: PNG lossy palette mode and its quality scale, 2026-11-06

`PngWriter.Encode` with `PngWriteOptions.Quantize { MaxQuality = q, Dither = … }` (written at `CompressionLevel.SmallestSize`, as pngquant writes at its strongest deflate) against `pngquant --quality 0-q --speed 4 [--nofs]`, each writing its own file, q = 5, 10, 20, …, 100.

- Machine: AMD Ryzen 7 5800X (8 cores / 16 threads), Windows 11 10.0.26200, .NET 10.0.11, x64, Vector256 accelerated, Vector512 not.
- pngquant 2.17.0 (September 2021).
- Commands: `calibrate` (pngquant's error per quality, which set the quality table in `PngQuantization.cs`), then `quality`.

## The quality scale

Our quality q is a target RMS error in premultiplied OKLab, interpolated from a table set to the median of what pngquant reaches at `--quality 0-q --nofs` on this corpus (`calibrate`). pngquant's own reach varies by image (at q = 20 from 1.1 on flat synthetic images to 3.3 on a dark photo), so at the same number we use more colours than pngquant on textured photos and fewer on flat UI / chart images. Over the whole sweep the files come out the same size on average (×1.04 without dithering, ×0.98 with); at q = 20 without dithering (pngquant-web's setting) ours are 11% larger in total, with higher SSIM on 8 of the 10 photos.

## Rate–distortion over the quality sweep (own files, BD-rate against pngquant)

- Without dithering: −8.4% bytes at equal SSIM, −4.7% at equal blurred OKLab error.
- With dithering: −15.4% at equal SSIM, −8.8% at equal blurred error.
- The one image clearly behind is `photo-night-brussels` (224 colours in dark shades; +9% SSIM, +31% blurred): at low qualities pngquant's 4–5 colours fit it better.
- Deflate level matters: at the default `Optimal`, the 11-colour `palette` chart was 66% larger than pngquant's file; at `SmallestSize`, 4% larger. The `Quantize` docs recommend `SmallestSize`.

## Tried and dropped

- Weighting pixels by local activity (texture masks error), for the error target and the k-means: SSIM −1.2 points better, blurred error 9 points worse; did not narrow the spread of pngquant's per-image reach either. Removed.
- A per-box error cap on the early stop (so two distinct flat colours are not merged into one entry): worse at both caps tried (16× and 4× the target). Removed.

```
  photo-architecture-teatro: BD-rate SSIM -12.3%, blurred -2.6%
  photo-foliage-mangrove: BD-rate SSIM -5.5%, blurred -2.0%
  photo-landscape-almsee: BD-rate SSIM -2.9%, blurred -2.7%
  photo-night-alexanderplatz: BD-rate SSIM -7.5%, blurred -8.6%
  photo-night-brussels: BD-rate SSIM +8.8%, blurred +30.9%
  photo-panorama-lakeview: BD-rate SSIM -5.0%, blurred -17.0%
  photo-portrait-carter: BD-rate SSIM -9.2%, blurred -0.7%
  photo-sea-ibiza: BD-rate SSIM -13.1%, blurred -9.3%
  photo-sunrise-fuji: BD-rate SSIM -6.7%, blurred +4.8%
  photo-texture-ground: BD-rate SSIM -16.9%, blurred -15.0%
  photo-alpha: BD-rate SSIM -1.7%, blurred -19.8%
  screen: BD-rate SSIM -18.7%, blurred -2.0%
  screen-alpha: BD-rate SSIM -22.1%, blurred -3.4%
  palette: BD-rate SSIM +6.9%, blurred -1.7%
  sticker: BD-rate SSIM -20.7%, blurred -21.5%
summary, dither off: BD-rate vs pngquant (own files) SSIM -8.4%, blurred -4.7%; bytes at the same quality number ×1.04; time ×0.39
  photo-architecture-teatro: BD-rate SSIM -16.9%, blurred -9.8%
  photo-foliage-mangrove: BD-rate SSIM -14.8%, blurred -6.4%
  photo-landscape-almsee: BD-rate SSIM -2.5%, blurred -7.1%
  photo-night-alexanderplatz: BD-rate SSIM -8.8%, blurred -5.1%
  photo-night-brussels: BD-rate SSIM +7.4%, blurred +31.1%
  photo-panorama-lakeview: BD-rate SSIM -11.5%, blurred -20.3%
  photo-portrait-carter: BD-rate SSIM -15.4%, blurred -3.6%
  photo-sea-ibiza: BD-rate SSIM -21.1%, blurred -20.8%
  photo-sunrise-fuji: BD-rate SSIM -18.4%, blurred -5.0%
  photo-texture-ground: BD-rate SSIM -15.1%, blurred -16.0%
  photo-alpha: BD-rate SSIM -24.6%, blurred -14.3%
  screen: BD-rate SSIM -26.4%, blurred -13.2%
  screen-alpha: BD-rate SSIM -24.6%, blurred -8.3%
  palette: BD-rate SSIM NaN%, blurred NaN%
  sticker: BD-rate SSIM -22.4%, blurred -24.5%
summary, dither on: BD-rate vs pngquant (own files) SSIM -15.4%, blurred -8.8%; bytes at the same quality number ×0.98; time ×0.42
```

Full output:

```

dither off
image                        q | pngquant: colors     bytes  SSIM-dB blurred | ours: colors     bytes  SSIM-dB blurred    ms
photo-architecture-teatro     5 |            15    100708    11.96   2.130 |       15    113336    13.44   1.990   319
photo-architecture-teatro    10 |            23    125302    13.12   1.693 |       21    137971    15.00   1.725   358
photo-architecture-teatro    20 |            30    141058    14.12   1.526 |       33    160218    16.10   1.394   173
photo-architecture-teatro    30 |            39    159221    15.21   1.373 |       43    174078    16.92   1.188   154
photo-architecture-teatro    40 |            49    179855    16.32   1.289 |       55    188808    17.69   1.086   152
photo-architecture-teatro    50 |            66    195794    17.22   1.109 |       68    207274    18.87   1.014   157
photo-architecture-teatro    60 |            94    234364    18.48   1.043 |       95    227469    19.80   0.850   162
photo-architecture-teatro    70 |           156    276918    20.07   0.817 |      137    258673    20.81   0.746   180
photo-architecture-teatro    80 |           256    329603    21.34   0.684 |      234    300620    21.84   0.558   213
photo-architecture-teatro    90 |           256    329608    21.34   0.684 |      256    307936    22.00   0.539   217
photo-architecture-teatro   100 |           256    329608    21.34   0.684 |      256    307936    22.00   0.539   218
  photo-architecture-teatro: BD-rate SSIM -12.3%, blurred -2.6%
photo-foliage-mangrove        5 |             7     47689     7.83   2.743 |        9     69148     9.20   2.257    76
photo-foliage-mangrove       10 |            10     70030     9.17   2.155 |       11     73479     9.44   1.872    75
photo-foliage-mangrove       20 |            14     94186    10.37   1.711 |       15    100231    11.07   1.597    86
photo-foliage-mangrove       30 |            17    103253    10.62   1.600 |       18    124511    12.03   1.518   174
photo-foliage-mangrove       40 |            20    113326    11.33   1.435 |       20    129524    12.39   1.425   167
photo-foliage-mangrove       50 |            25    123215    11.90   1.300 |       23    131045    12.72   1.268   168
photo-foliage-mangrove       60 |            34    148579    13.13   1.155 |       31    146766    13.32   0.999   165
photo-foliage-mangrove       70 |            50    180081    14.95   0.855 |       43    171532    14.42   0.871   157
photo-foliage-mangrove       80 |            81    221907    16.36   0.689 |       59    201722    16.20   0.760   154
photo-foliage-mangrove       90 |           189    296586    18.91   0.480 |      111    261569    18.62   0.575   130
photo-foliage-mangrove      100 |           256    325437    19.69   0.419 |      256    331845    21.15   0.403   130
  photo-foliage-mangrove: BD-rate SSIM -5.5%, blurred -2.0%
photo-landscape-almsee        5 |            12    104238     8.86   2.555 |       14    134136    11.02   2.148   118
photo-landscape-almsee       10 |            17    146723    11.21   2.099 |       19    160520    12.08   1.904   175
photo-landscape-almsee       20 |            24    164086    11.94   1.790 |       30    197708    13.28   1.391   165
photo-landscape-almsee       30 |            30    186364    12.61   1.610 |       37    213630    13.72   1.215   163
photo-landscape-almsee       40 |            36    195325    13.05   1.388 |       46    227755    14.32   1.043   162
photo-landscape-almsee       50 |            46    218473    13.55   1.154 |       55    240056    14.85   0.962   167
photo-landscape-almsee       60 |            63    247717    14.77   0.982 |       74    269653    15.79   0.829   167
photo-landscape-almsee       70 |            97    292463    16.50   0.800 |      104    295770    16.70   0.710   165
photo-landscape-almsee       80 |           161    346666    18.33   0.655 |      173    346711    18.47   0.559   180
photo-landscape-almsee       90 |           256    405588    19.95   0.538 |      256    389204    19.91   0.467   187
photo-landscape-almsee      100 |           256    405588    19.95   0.538 |      256    389204    19.91   0.467   187
  photo-landscape-almsee: BD-rate SSIM -2.9%, blurred -2.7%
photo-night-alexanderplatz    5 |             7     67267     8.86   1.953 |        7     67319     9.69   1.707    69
photo-night-alexanderplatz   10 |             7     59043     8.50   2.008 |        8     75679    10.35   1.609    64
photo-night-alexanderplatz   20 |             9     79125    10.42   1.635 |       12    109989    12.75   1.269    74
photo-night-alexanderplatz   30 |            15    114567    13.37   1.301 |       13    116964    13.37   1.249    75
photo-night-alexanderplatz   40 |            15    114567    13.37   1.301 |       15    126970    14.08   1.197    75
photo-night-alexanderplatz   50 |            15    114564    13.37   1.300 |       18    153049    15.62   1.173   197
photo-night-alexanderplatz   60 |            25    164778    16.06   1.034 |       25    184076    17.38   1.012   197
photo-night-alexanderplatz   70 |            33    182611    16.89   0.983 |       36    218712    19.06   0.861   208
photo-night-alexanderplatz   80 |            66    261157    19.67   0.822 |       63    251358    20.63   0.647   190
photo-night-alexanderplatz   90 |           197    385150    22.89   0.574 |      149    324232    24.36   0.470   164
photo-night-alexanderplatz  100 |           256    412756    23.51   0.533 |      256    375692    25.61   0.357   138
  photo-night-alexanderplatz: BD-rate SSIM -7.5%, blurred -8.6%
photo-night-brussels          5 |             2       480     4.10   5.700 |        3     21882     8.29   2.041    15
photo-night-brussels         10 |             2       480     4.10   5.700 |        4     48552     9.88   1.518    18
photo-night-brussels         20 |             4     11361     7.03   2.458 |        5     52339    10.37   1.017    47
photo-night-brussels         30 |             4     11361     7.03   2.458 |        5     52339    10.37   1.017    42
photo-night-brussels         40 |             4     11657     7.25   2.341 |        6     57550    11.39   0.816    41
photo-night-brussels         50 |             4     18244     8.20   1.807 |        7     70911    12.76   0.730    42
photo-night-brussels         60 |             5     24421     8.66   1.609 |        7     70911    12.76   0.730    42
photo-night-brussels         70 |             6     26773     8.98   1.465 |        8     90352    14.11   0.599    43
photo-night-brussels         80 |             7     58333    11.48   0.735 |       10     93325    14.79   0.415    39
photo-night-brussels         90 |            10     90082    14.72   0.492 |       14    122656    17.57   0.397    30
photo-night-brussels        100 |           224    256090   100.00   0.000 |      224    228830   100.00   0.000    44
  photo-night-brussels: BD-rate SSIM +8.8%, blurred +30.9%
photo-panorama-lakeview       5 |            18     79633    10.03   2.644 |       13     63717     9.93   1.969    68
photo-panorama-lakeview      10 |            22     84688    10.90   2.491 |       17     77218    10.86   1.615    94
photo-panorama-lakeview      20 |            29     96865    12.82   1.886 |       24     94979    12.16   1.384    77
photo-panorama-lakeview      30 |            33    102420    13.42   1.844 |       28     99433    13.17   1.243    76
photo-panorama-lakeview      40 |            42    113069    13.90   1.775 |       33    108906    13.95   1.122    74
photo-panorama-lakeview      50 |            52    122080    14.84   0.860 |       39    116172    14.64   1.017    79
photo-panorama-lakeview      60 |            64    131282    15.58   0.780 |       50    125389    15.66   0.883    76
photo-panorama-lakeview      70 |            94    149180    17.49   0.675 |       71    139629    16.74   0.724    78
photo-panorama-lakeview      80 |           164    181191    19.12   0.560 |      111    160485    18.88   0.590    85
photo-panorama-lakeview      90 |           256    208552    20.64   0.484 |      231    196600    21.21   0.456    99
photo-panorama-lakeview     100 |           256    208552    20.64   0.484 |      256    202405    21.38   0.439   102
  photo-panorama-lakeview: BD-rate SSIM -5.0%, blurred -17.0%
photo-portrait-carter         5 |             9     76198     8.51   2.579 |       11     97547     9.62   2.219    99
photo-portrait-carter        10 |            15    112405    10.24   1.835 |       15    118976    10.82   1.860    97
photo-portrait-carter        20 |            20    134229    11.40   1.486 |       20    136393    11.78   1.441   171
photo-portrait-carter        30 |            23    141213    11.87   1.380 |       23    142176    12.03   1.321   172
photo-portrait-carter        40 |            28    158833    12.46   1.289 |       27    153073    12.55   1.250   171
photo-portrait-carter        50 |            34    178731    13.12   1.174 |       32    178769    13.86   1.172   177
photo-portrait-carter        60 |            46    204693    14.11   0.998 |       43    210639    15.57   1.066   173
photo-portrait-carter        70 |            67    252579    15.86   0.886 |       63    247716    16.42   0.856   164
photo-portrait-carter        80 |           112    310922    17.29   0.752 |      104    287564    17.61   0.687   163
photo-portrait-carter        90 |           256    430238    19.63   0.519 |      208    379823    20.23   0.494   175
photo-portrait-carter       100 |           256    430238    19.63   0.519 |      256    397858    20.69   0.436   155
  photo-portrait-carter: BD-rate SSIM -9.2%, blurred -0.7%
photo-sea-ibiza               5 |             6     22709    10.94   2.076 |        5     18997    10.88   2.405    36
photo-sea-ibiza              10 |            11     29940    11.73   1.386 |        7     24029    10.87   1.828    39
photo-sea-ibiza              20 |            12     31885    12.20   1.288 |       11     31608    11.80   1.439    40
photo-sea-ibiza              30 |            13     36084    12.51   1.238 |       12     34903    12.46   1.318    39
photo-sea-ibiza              40 |            18     48273    13.59   1.076 |       14     40554    12.99   1.230    43
photo-sea-ibiza              50 |            24     54388    14.23   0.973 |       16     42018    13.38   1.078    47
photo-sea-ibiza              60 |            30     64518    15.33   0.785 |       20     44613    13.70   0.939    65
photo-sea-ibiza              70 |            44     78920    16.52   0.667 |       26     51895    14.24   0.810    71
photo-sea-ibiza              80 |            65     98030    17.74   0.547 |       38     68123    16.63   0.635    87
photo-sea-ibiza              90 |           162    161207    21.19   0.378 |       68     86690    18.14   0.457    95
photo-sea-ibiza             100 |           256    203515    22.72   0.319 |      256    163544    23.13   0.267   116
  photo-sea-ibiza: BD-rate SSIM -13.1%, blurred -9.3%
photo-sunrise-fuji            5 |            21     61784     8.80   2.295 |       18     66240     9.31   2.371   110
photo-sunrise-fuji           10 |            32     78588     9.72   1.795 |       25     76695    10.02   1.966   113
photo-sunrise-fuji           20 |            44     86666    10.31   1.509 |       39     93359    11.04   1.508   116
photo-sunrise-fuji           30 |            53     94256    10.58   1.362 |       48    101473    11.56   1.340   116
photo-sunrise-fuji           40 |            67    105682    11.31   1.196 |       57    107808    11.97   1.225   121
photo-sunrise-fuji           50 |            87    116285    11.91   1.068 |       70    118060    12.52   1.110   128
photo-sunrise-fuji           60 |           118    132545    12.88   0.924 |       98    133450    13.27   0.939   136
photo-sunrise-fuji           70 |           169    151584    14.03   0.808 |      140    149869    14.40   0.796   137
photo-sunrise-fuji           80 |           256    179151    15.21   0.694 |      226    175416    15.69   0.631   155
photo-sunrise-fuji           90 |           256    179151    15.21   0.694 |      256    181293    16.03   0.590   157
photo-sunrise-fuji          100 |           256    179151    15.21   0.694 |      256    181293    16.03   0.590   158
  photo-sunrise-fuji: BD-rate SSIM -6.7%, blurred +4.8%
photo-texture-ground          5 |             2     73970     6.42   2.932 |        4    131040     9.00   1.575    67
photo-texture-ground         10 |             3    108170     6.73   2.094 |        5    164788    10.30   1.405   106
photo-texture-ground         20 |             5    163353     8.75   1.623 |        8    201731    12.15   1.108    96
photo-texture-ground         30 |             6    177714     9.62   1.432 |       10    220972    13.34   1.036    84
photo-texture-ground         40 |             7    194237    10.64   1.373 |       12    241279    14.53   0.977    83
photo-texture-ground         50 |            10    230415    12.12   1.121 |       15    260977    14.87   0.840    88
photo-texture-ground         60 |            15    277730    13.08   1.014 |       21    312599    15.61   0.724   121
photo-texture-ground         70 |            24    333398    15.22   0.811 |       31    351848    17.42   0.632   118
photo-texture-ground         80 |            49    414428    17.28   0.623 |       57    416595    19.29   0.501   127
photo-texture-ground         90 |           175    579854    20.69   0.366 |      134    510681    21.78   0.358   152
photo-texture-ground        100 |           256    631813    21.75   0.326 |      256    595423    23.40   0.278   182
  photo-texture-ground: BD-rate SSIM -16.9%, blurred -15.0%
photo-alpha                   5 |            80    121028    10.88   2.559 |       78    112367    10.51   2.143   177
photo-alpha                  10 |           127    147612    12.13   2.092 |      120    134874    11.63   1.756   191
photo-alpha                  20 |           205    172399    13.34   1.634 |      190    158459    12.77   1.399   217
photo-alpha                  30 |           256    189789    14.01   1.533 |      248    173471    13.66   1.254   231
photo-alpha                  40 |           256    189769    14.01   1.529 |      256    175212    13.71   1.234   300
photo-alpha                  50 |           256    189762    14.01   1.530 |      256    175212    13.71   1.234   322
photo-alpha                  60 |           256    189762    14.01   1.530 |      256    175212    13.71   1.234   303
photo-alpha                  70 |           256    189762    14.01   1.530 |      256    175212    13.71   1.234   283
photo-alpha                  80 |           256    189762    14.01   1.530 |      256    175212    13.71   1.234   231
photo-alpha                  90 |           256    189762    14.01   1.530 |      256    175212    13.71   1.234   226
photo-alpha                 100 |           256    189762    14.01   1.530 |      256    175212    13.71   1.234   223
  photo-alpha: BD-rate SSIM -1.7%, blurred -19.8%
screen                        5 |            16      4158    18.49   1.726 |        8      3507    18.57   2.766    21
screen                       10 |            22      5545    18.31   1.248 |       10      3667    19.33   2.307    21
screen                       20 |            27      5802    18.17   1.056 |       13      3819    19.36   1.770    22
screen                       30 |            31      5742    18.23   0.983 |       15      4257    19.56   1.509    26
screen                       40 |            35      6354    18.41   0.925 |       15      4257    19.56   1.509    25
screen                       50 |            39      6943    19.09   0.848 |       16      4380    19.47   1.437    25
screen                       60 |            45      6439    19.37   0.776 |       19      5433    19.39   1.229    32
screen                       70 |            57      6961    19.63   0.627 |       24      5605    19.69   1.075    30
screen                       80 |            78      8020    20.04   0.484 |       32      5982    19.82   0.832    30
screen                       90 |           133      8553    20.83   0.366 |       45      6677    20.30   0.632    32
screen                      100 |           256     10648    23.10   0.205 |      256     12955    24.78   0.177    52
  screen: BD-rate SSIM -18.7%, blurred -2.0%
screen-alpha                  5 |            28      5571    18.00   1.182 |       10      3786    17.72   2.599    26
screen-alpha                 10 |            31      6451    17.91   1.067 |       13      3709    18.06   2.092    25
screen-alpha                 20 |            34      6068    18.10   0.982 |       17      5212    18.67   1.623    28
screen-alpha                 30 |            37      6586    18.14   0.915 |       18      5272    18.74   1.429    27
screen-alpha                 40 |            40      6619    18.23   0.872 |       19      5309    18.73   1.332    26
screen-alpha                 50 |            47      6744    19.26   0.774 |       21      5359    18.71   1.224    27
screen-alpha                 60 |            55      6979    19.34   0.693 |       24      5747    19.18   1.127    29
screen-alpha                 70 |            66      7744    19.52   0.593 |       31      5840    19.46   0.917    32
screen-alpha                 80 |            86      8162    19.98   0.480 |       38      6276    19.75   0.804    31
screen-alpha                 90 |           146      9777    21.04   0.332 |       54      6886    20.44   0.593    33
screen-alpha                100 |           256     11891    22.89   0.206 |      256     12787    24.68   0.177    51
  screen-alpha: BD-rate SSIM -22.1%, blurred -3.4%
palette                       5 |             9      1935    10.60   2.875 |        7      1927    10.53   2.442    32
palette                      10 |            10      2224    35.36   1.065 |        8      1930    10.57   1.418    28
palette                      20 |            10      2224    35.36   1.065 |        8      1930    10.57   1.418    28
palette                      30 |            10      2224    35.36   1.065 |        9      1939    10.64   0.845    27
palette                      40 |            10      2224    35.36   1.065 |        9      1939    10.64   0.845    28
palette                      50 |            11      2128   100.00   0.000 |        9      1939    10.64   0.845    28
palette                      60 |            11      2128   100.00   0.000 |       10      2201    33.15   0.620    27
palette                      70 |            11      2128   100.00   0.000 |       10      2201    33.15   0.620    28
palette                      80 |            11      2128   100.00   0.000 |       10      2201    33.15   0.620    27
palette                      90 |            11      2128   100.00   0.000 |       11      2208   100.00   0.000    29
palette                     100 |            11      2128   100.00   0.000 |       11      2208   100.00   0.000     9
  palette: BD-rate SSIM +6.9%, blurred -1.7%
sticker                       5 |            32     49395    16.56   1.210 |       10     27136    14.83   1.655    44
sticker                      10 |            42     55069    18.34   0.863 |       13     32259    16.21   1.408    47
sticker                      20 |            60     66268    19.62   0.763 |       17     40743    17.61   1.125    54
sticker                      30 |            67     69594    19.87   0.760 |       20     41078    18.10   1.069    55
sticker                      40 |            88     75501    22.17   0.623 |       24     43849    18.82   0.917    57
sticker                      50 |           110     81502    22.41   0.564 |       29     50700    19.80   0.816    57
sticker                      60 |           130     87109    23.89   0.542 |       35     54462    20.69   0.738    59
sticker                      70 |           188     98120    25.60   0.477 |       44     56514    21.54   0.671    61
sticker                      80 |           256    108735    26.61   0.439 |       67     67524    22.72   0.489    65
sticker                      90 |           256    108723    26.62   0.439 |      117     81224    25.06   0.386    75
sticker                     100 |           256    108723    26.62   0.439 |      256    103758    27.87   0.262    93
  sticker: BD-rate SSIM -20.7%, blurred -21.5%
summary, dither off: BD-rate vs pngquant (own files) SSIM -8.4%, blurred -4.7%; bytes at the same quality number ×1.04; time ×0.39

dither on
image                        q | pngquant: colors     bytes  SSIM-dB blurred | ours: colors     bytes  SSIM-dB blurred    ms
photo-architecture-teatro     5 |            15    139597     9.49   1.794 |       15    135305    11.49   1.795   137
photo-architecture-teatro    10 |            23    172304    10.53   1.405 |       21    160289    12.57   1.571   197
photo-architecture-teatro    20 |            30    187683    11.75   1.340 |       33    183160    13.34   1.270   191
photo-architecture-teatro    30 |            39    208359    12.71   1.227 |       43    201647    14.08   1.067   197
photo-architecture-teatro    40 |            49    227585    13.76   1.173 |       55    217167    14.63   0.962   199
photo-architecture-teatro    50 |            66    245430    14.24   0.990 |       68    233221    16.35   0.914   204
photo-architecture-teatro    60 |            94    279139    16.05   0.957 |       95    256559    17.39   0.770   205
photo-architecture-teatro    70 |           156    320262    17.94   0.745 |      137    286860    18.64   0.684   220
photo-architecture-teatro    80 |           256    360579    19.73   0.637 |      234    332620    19.46   0.502   241
photo-architecture-teatro    90 |           256    360583    19.73   0.637 |      256    340083    19.54   0.483   252
photo-architecture-teatro   100 |           256    360583    19.73   0.637 |      256    340083    19.54   0.483   248
  photo-architecture-teatro: BD-rate SSIM -16.9%, blurred -9.8%
photo-foliage-mangrove        5 |             7     84967     4.62   2.103 |        9     98015     6.14   1.779   135
photo-foliage-mangrove       10 |            10    107538     5.85   1.738 |       11    105833     6.59   1.523   130
photo-foliage-mangrove       20 |            14    131706     6.91   1.419 |       15    128548     8.11   1.377   130
photo-foliage-mangrove       30 |            17    147572     6.82   1.242 |       18    152044     8.81   1.324   272
photo-foliage-mangrove       40 |            20    158760     7.55   1.154 |       20    158087     9.39   1.260   258
photo-foliage-mangrove       50 |            25    171534     7.93   1.045 |       23    161895     9.89   1.116   257
photo-foliage-mangrove       60 |            34    195376     9.72   0.978 |       31    178679    10.53   0.822   247
photo-foliage-mangrove       70 |            50    227569    11.39   0.715 |       43    205307    11.60   0.720   229
photo-foliage-mangrove       80 |            81    269263    13.19   0.587 |       59    236497    13.38   0.641   206
photo-foliage-mangrove       90 |           189    341305    16.55   0.424 |      111    299581    16.21   0.494   174
photo-foliage-mangrove      100 |           256    369072    17.53   0.371 |      256    375817    19.20   0.347   169
  photo-foliage-mangrove: BD-rate SSIM -14.8%, blurred -6.4%
photo-landscape-almsee        5 |            12    138649     6.04   2.126 |       14    156717     8.10   1.832   157
photo-landscape-almsee       10 |            17    177047     7.90   1.821 |       19    182232     9.02   1.675   251
photo-landscape-almsee       20 |            24    199538     8.64   1.539 |       30    219265    10.37   1.222   233
photo-landscape-almsee       30 |            30    220727     9.42   1.399 |       37    237558    11.02   1.061   226
photo-landscape-almsee       40 |            36    231250    10.41   1.235 |       46    252672    11.94   0.915   224
photo-landscape-almsee       50 |            46    254232    11.05   0.992 |       55    266803    12.39   0.839   222
photo-landscape-almsee       60 |            63    282678    12.74   0.885 |       74    297589    13.51   0.722   220
photo-landscape-almsee       70 |            97    322585    15.05   0.735 |      104    324945    14.57   0.620   221
photo-landscape-almsee       80 |           161    371606    17.33   0.621 |      173    373165    16.84   0.501   228
photo-landscape-almsee       90 |           256    427218    19.01   0.511 |      256    415578    18.64   0.421   238
photo-landscape-almsee      100 |           256    427218    19.01   0.511 |      256    415578    18.64   0.421   238
  photo-landscape-almsee: BD-rate SSIM -2.5%, blurred -7.1%
photo-night-alexanderplatz    5 |             7     83175     7.78   1.626 |        7     76884     9.01   1.541   101
photo-night-alexanderplatz   10 |             7     74082     7.42   1.672 |        8     84859     9.69   1.491    96
photo-night-alexanderplatz   20 |             9     91264     9.36   1.438 |       12    119754    12.12   1.200   111
photo-night-alexanderplatz   30 |            15    128760    12.08   1.139 |       13    126095    12.71   1.188   109
photo-night-alexanderplatz   40 |            15    128760    12.08   1.139 |       15    134828    13.45   1.145   106
photo-night-alexanderplatz   50 |            15    128792    12.07   1.139 |       18    157713    15.10   1.147   230
photo-night-alexanderplatz   60 |            25    174810    15.16   0.980 |       25    189659    16.64   0.983   227
photo-night-alexanderplatz   70 |            33    194223    16.27   0.935 |       36    226377    18.34   0.839   236
photo-night-alexanderplatz   80 |            66    263418    19.48   0.770 |       63    261506    19.73   0.609   203
photo-night-alexanderplatz   90 |           197    387548    22.75   0.582 |      149    338952    23.59   0.439   184
photo-night-alexanderplatz  100 |           256    415007    23.50   0.548 |      256    385786    24.80   0.337   163
  photo-night-alexanderplatz: BD-rate SSIM -8.8%, blurred -5.1%
photo-night-brussels          5 |             2      2327     4.63   4.802 |        3     32399     7.21   1.778    32
photo-night-brussels         10 |             2      2327     4.63   4.802 |        4     54693     8.90   1.313    35
photo-night-brussels         20 |             4     23120     5.78   1.618 |        5     58471     9.47   0.792    58
photo-night-brussels         30 |             4     23120     5.78   1.618 |        5     58471     9.47   0.792    61
photo-night-brussels         40 |             4     23274     5.83   1.623 |        6     62981    10.56   0.674    59
photo-night-brussels         50 |             4     31734     6.55   1.296 |        7     73642    12.08   0.647    54
photo-night-brussels         60 |             5     41233     7.01   1.000 |        7     73642    12.08   0.647    55
photo-night-brussels         70 |             6     43412     7.52   0.980 |        8     94092    13.29   0.505    53
photo-night-brussels         80 |             7     64902    10.42   0.528 |       10     96836    14.03   0.322    53
photo-night-brussels         90 |            10     93345    14.07   0.411 |       14    125885    17.04   0.311    45
photo-night-brussels        100 |           224    256090   100.00   0.000 |      224    228830   100.00   0.000    40
  photo-night-brussels: BD-rate SSIM +7.4%, blurred +31.1%
photo-panorama-lakeview       5 |            18     92013     8.17   2.472 |       13     71923     8.29   1.737    82
photo-panorama-lakeview      10 |            22     98006     9.22   2.391 |       17     86156     9.11   1.394   121
photo-panorama-lakeview      20 |            29    110912    10.48   1.783 |       24    103951    10.29   1.215   112
photo-panorama-lakeview      30 |            33    116572    11.12   1.766 |       28    108597    11.57   1.142   107
photo-panorama-lakeview      40 |            42    128004    11.32   1.696 |       33    118291    12.24   1.026   105
photo-panorama-lakeview      50 |            52    137082    12.21   0.735 |       39    125316    12.89   0.929   104
photo-panorama-lakeview      60 |            64    146546    13.28   0.679 |       50    134484    13.94   0.809   102
photo-panorama-lakeview      70 |            94    164255    15.53   0.607 |       71    149016    15.27   0.672   106
photo-panorama-lakeview      80 |           164    195354    17.14   0.523 |      111    170470    17.34   0.548   108
photo-panorama-lakeview      90 |           256    221013    19.10   0.461 |      231    208809    19.84   0.425   126
photo-panorama-lakeview     100 |           256    221013    19.10   0.461 |      256    214263    20.14   0.409   121
  photo-panorama-lakeview: BD-rate SSIM -11.5%, blurred -20.3%
photo-portrait-carter         5 |             9    131765     5.57   2.126 |       11    139176     7.52   1.983   157
photo-portrait-carter        10 |            15    172504     7.22   1.476 |       15    159771     8.73   1.704   156
photo-portrait-carter        20 |            20    193752     8.62   1.168 |       20    178265     9.91   1.250   278
photo-portrait-carter        30 |            23    200783     9.12   1.125 |       23    184463    10.24   1.152   278
photo-portrait-carter        40 |            28    217520     9.69   1.061 |       27    197258    10.77   1.094   268
photo-portrait-carter        50 |            34    237955    10.41   0.976 |       32    220084    12.22   1.046   257
photo-portrait-carter        60 |            46    260075    11.73   0.867 |       43    247759    14.14   0.975   252
photo-portrait-carter        70 |            67    299956    13.93   0.799 |       63    286503    14.77   0.776   227
photo-portrait-carter        80 |           112    351358    15.76   0.699 |      104    332147    16.26   0.621   214
photo-portrait-carter        90 |           256    455655    18.77   0.489 |      208    423194    18.85   0.428   202
photo-portrait-carter       100 |           256    455655    18.77   0.489 |      256    441790    19.40   0.375   209
  photo-portrait-carter: BD-rate SSIM -15.4%, blurred -3.6%
photo-sea-ibiza               5 |             6     59529     5.33   1.667 |        5     44261     5.64   1.956    96
photo-sea-ibiza              10 |            11     74470     7.21   1.063 |        7     53128     6.34   1.459    85
photo-sea-ibiza              20 |            12     77069     7.19   0.990 |       11     62403     7.56   1.148    89
photo-sea-ibiza              30 |            13     82768     7.76   0.981 |       12     65960     8.62   1.108    90
photo-sea-ibiza              40 |            18     99103     9.39   0.912 |       14     72822     9.40   1.061    93
photo-sea-ibiza              50 |            24    108284    10.45   0.824 |       16     72500     9.48   0.876    87
photo-sea-ibiza              60 |            30    118975    11.53   0.629 |       20     77156    10.20   0.762   174
photo-sea-ibiza              70 |            44    131194    13.59   0.553 |       26     84053    11.13   0.661   176
photo-sea-ibiza              80 |            65    143880    15.26   0.467 |       38     98381    14.23   0.561   178
photo-sea-ibiza              90 |           162    184007    19.99   0.350 |       68    120359    16.05   0.402   164
photo-sea-ibiza             100 |           256    213545    22.12   0.304 |      256    194788    21.49   0.243   147
  photo-sea-ibiza: BD-rate SSIM -21.1%, blurred -20.8%
photo-sunrise-fuji            5 |            21    121959     4.62   1.457 |       18    108752     5.72   1.719   195
photo-sunrise-fuji           10 |            32    144598     5.57   1.170 |       25    123666     6.62   1.425   186
photo-sunrise-fuji           20 |            44    156411     6.35   1.002 |       39    141044     7.87   1.110   182
photo-sunrise-fuji           30 |            53    163670     6.85   0.920 |       48    151476     8.45   0.980   175
photo-sunrise-fuji           40 |            67    176252     7.60   0.824 |       57    158806     8.88   0.902   176
photo-sunrise-fuji           50 |            87    188372     8.56   0.762 |       70    169046     9.64   0.835   175
photo-sunrise-fuji           60 |           118    203854     9.70   0.692 |       98    186775    10.54   0.703   177
photo-sunrise-fuji           70 |           169    224642    11.00   0.644 |      140    205588    11.81   0.605   188
photo-sunrise-fuji           80 |           256    250976    12.44   0.580 |      226    232439    13.30   0.485   196
photo-sunrise-fuji           90 |           256    250976    12.44   0.580 |      256    239024    13.79   0.462   203
photo-sunrise-fuji          100 |           256    250976    12.44   0.580 |      256    239024    13.79   0.462   201
  photo-sunrise-fuji: BD-rate SSIM -18.4%, blurred -5.0%
photo-texture-ground          5 |             2     75136     5.94   2.906 |        4    134928     8.80   1.517    96
photo-texture-ground         10 |             3    111362     6.34   2.069 |        5    169720     9.98   1.333   122
photo-texture-ground         20 |             5    169758     8.40   1.570 |        8    205979    11.81   1.052   126
photo-texture-ground         30 |             6    184134     9.26   1.367 |       10    225054    13.01   0.985   115
photo-texture-ground         40 |             7    198715    10.25   1.328 |       12    244775    14.09   0.923   115
photo-texture-ground         50 |            10    233348    11.81   1.077 |       15    264315    14.54   0.800   116
photo-texture-ground         60 |            15    279915    12.82   0.970 |       21    316579    15.26   0.688   148
photo-texture-ground         70 |            24    335292    14.97   0.785 |       31    355404    17.02   0.602   145
photo-texture-ground         80 |            49    415086    17.09   0.609 |       57    419780    18.78   0.469   158
photo-texture-ground         90 |           175    580124    20.62   0.357 |      134    514479    21.25   0.325   189
photo-texture-ground        100 |           256    631789    21.68   0.320 |      256    598587    22.91   0.252   213
  photo-texture-ground: BD-rate SSIM -15.1%, blurred -16.0%
photo-alpha                   5 |            80    194123     8.35   1.649 |       78    160298     9.08   1.756   219
photo-alpha                  10 |           127    223663     9.42   1.350 |      120    182810    10.27   1.450   233
photo-alpha                  20 |           205    252629    10.81   1.116 |      190    206670    11.64   1.197   267
photo-alpha                  30 |           256    272116    11.53   1.049 |      248    222361    12.54   1.080   276
photo-alpha                  40 |           256    272204    11.54   1.051 |      256    224528    12.60   1.059   281
photo-alpha                  50 |           256    272133    11.54   1.051 |      256    224528    12.60   1.059   272
photo-alpha                  60 |           256    272133    11.54   1.051 |      256    224528    12.60   1.059   286
photo-alpha                  70 |           256    272133    11.54   1.051 |      256    224528    12.60   1.059   291
photo-alpha                  80 |           256    272133    11.54   1.051 |      256    224528    12.60   1.059   387
photo-alpha                  90 |           256    272133    11.54   1.051 |      256    224528    12.60   1.059   274
photo-alpha                 100 |           256    272133    11.54   1.051 |      256    224528    12.60   1.059   279
  photo-alpha: BD-rate SSIM -24.6%, blurred -14.3%
screen                        5 |            16     15014    12.62   1.021 |        8     12145    12.04   2.187    38
screen                       10 |            22     18950    12.93   0.654 |       10     11820    12.68   1.780    40
screen                       20 |            27     19250    13.26   0.534 |       13     12019    13.20   1.261    37
screen                       30 |            31     19561    13.53   0.493 |       15     12798    13.81   1.035    39
screen                       40 |            35     19917    13.74   0.460 |       15     12798    13.81   1.035    41
screen                       50 |            39     19647    14.28   0.417 |       16     13217    14.06   0.983    39
screen                       60 |            45     20742    14.56   0.358 |       19     14417    14.05   0.800    43
screen                       70 |            57     21993    15.11   0.277 |       24     15499    14.77   0.695    45
screen                       80 |            78     23105    16.20   0.188 |       32     16426    15.54   0.492    46
screen                       90 |           133     25592    17.52   0.132 |       45     18091    16.48   0.328    46
screen                      100 |           256     29391    20.76   0.079 |      256     26628    22.90   0.090    64
  screen: BD-rate SSIM -26.4%, blurred -13.2%
screen-alpha                  5 |            28     19118    13.02   0.625 |       10     11495    12.56   2.180    40
screen-alpha                 10 |            31     19389    13.34   0.498 |       13     11442    12.78   1.586    37
screen-alpha                 20 |            34     19070    13.55   0.471 |       17     14694    13.30   1.139    40
screen-alpha                 30 |            37     19647    13.76   0.399 |       18     14189    13.81   0.975    42
screen-alpha                 40 |            40     19899    13.94   0.384 |       19     14112    14.13   0.916    39
screen-alpha                 50 |            47     20209    14.48   0.344 |       21     14738    14.16   0.830    59
screen-alpha                 60 |            55     20868    14.90   0.297 |       24     14998    14.52   0.704    41
screen-alpha                 70 |            66     21961    15.17   0.233 |       31     16307    15.12   0.526    43
screen-alpha                 80 |            86     23073    16.01   0.187 |       38     16942    15.25   0.435    43
screen-alpha                 90 |           146     25741    17.97   0.121 |       54     18202    16.84   0.311    44
screen-alpha                100 |           256     29497    20.59   0.073 |      256     26392    22.76   0.089    62
  screen-alpha: BD-rate SSIM -24.6%, blurred -8.3%
palette                       5 |             9      3714    10.24   0.868 |        7      9989     8.65   1.831    43
palette                      10 |            10      3879    20.57   0.617 |        8      5049     9.68   1.158    42
palette                      20 |            10      3879    20.57   0.617 |        8      5049     9.68   1.158    46
palette                      30 |            10      3879    20.57   0.617 |        9      3885    10.28   0.707    59
palette                      40 |            10      3879    20.57   0.617 |        9      3885    10.28   0.707    62
palette                      50 |            11      2128   100.00   0.000 |        9      3885    10.28   0.707    41
palette                      60 |            11      2128   100.00   0.000 |       10      4141    20.99   0.413    47
palette                      70 |            11      2128   100.00   0.000 |       10      4141    20.99   0.413    41
palette                      80 |            11      2128   100.00   0.000 |       10      4141    20.99   0.413    55
palette                      90 |            11      2128   100.00   0.000 |       11      2208   100.00   0.000    42
palette                     100 |            11      2128   100.00   0.000 |       11      2208   100.00   0.000     9
  palette: BD-rate SSIM NaN%, blurred NaN%
sticker                       5 |            32     62051    15.48   0.888 |       10     32380    14.15   1.430    64
sticker                      10 |            42     67935    17.05   0.757 |       13     37114    15.47   1.225    60
sticker                      20 |            60     78941    18.79   0.696 |       17     45664    16.86   1.026    69
sticker                      30 |            67     81775    19.03   0.683 |       20     46005    17.26   0.966    90
sticker                      40 |            88     86572    20.82   0.576 |       24     49065    17.95   0.842    69
sticker                      50 |           110     92062    21.57   0.532 |       29     56003    18.81   0.746    72
sticker                      60 |           130     95508    22.92   0.505 |       35     59465    19.72   0.690    74
sticker                      70 |           188    106223    24.69   0.432 |       44     61665    20.58   0.631    73
sticker                      80 |           256    114005    26.13   0.404 |       67     75488    21.28   0.448    82
sticker                      90 |           256    114025    26.14   0.402 |      117     88666    23.38   0.360    91
sticker                     100 |           256    114025    26.14   0.402 |      256    111068    26.74   0.245   110
  sticker: BD-rate SSIM -22.4%, blurred -24.5%
summary, dither on: BD-rate vs pngquant (own files) SSIM -15.4%, blurred -8.8%; bytes at the same quality number ×0.98; time ×0.42
```
