# M1: perceptual RGBA quantizer, 2026-11-06

The rewritten `src/Shared/PaletteQuantizer.cs` (`ours`: OKLab premultiplied with alpha, exact-colour histogram, bucketed variance-minimising box split, 16 Lloyd iterations of which 14 on ≤ 2¹⁵ merged colours, edge-aware serpentine Floyd–Steinberg at 12/16 strength fading to none at a luma step of 32) against the previous one compiled from 91bf9e0 (`baseline-wu`), pngquant and ImageSharp, in one run.

- Machine: AMD Ryzen 7 5800X (8 cores / 16 threads), Windows 11 10.0.26200, .NET 10.0.11, x64, Vector256 accelerated, Vector512 not.
- pngquant 2.17.0 (September 2021), `--speed 4`; ImageSharp 3.1.12.
- Command: `rd --ablate --ablate-dither --contestants baseline-wu,ours,imagesharp-wu,imagesharp-octree,ours-no-kmeans,ours-rgb-space,ours-full-fs,ours-no-edge` (photos at 1024 px, colours 256/128/64/32/16).

## Findings

- Without dithering, `ours` needs 10% fewer bytes than pngquant at equal SSIM (12.5% at equal OKLab error, 10.2% at equal blurred error), and is ahead on every image; the old quantizer was 2.8% behind on SSIM.
- With dithering, 16% fewer bytes at equal SSIM and 11% at equal blurred error (what dithering is for); the old quantizer's plain Floyd–Steinberg was 40% behind on SSIM.
- Ablation: k-means is worth about 2 points without dithering; OKLab instead of premultiplied RGB is worth 8 points on SSIM; plain full-strength Floyd–Steinberg (`ours-full-fs`) costs 33 points against the edge-aware one, and turning off only the edge term (`ours-no-edge`, still at 12/16 strength) costs 7 points on SSIM but almost nothing on blurred error.
- Time: about a quarter of pngquant's process time (which includes PNG I/O and start-up), but 3–4× the old quantizer's (`baseline-wu` 0.07×). Not yet vectorised or parallel.

```
Summary against pngquant-s4: BD-rate (bytes at equal quality; negative = smaller), mean over images; time = geometric mean ratio.
contestant          dith  BD(SSIM)  BD(OKLab)  BD(blur)   time×   worst-image BD(SSIM)
baseline-wu            -     +2.8%      -2.7%     -3.2%    0.07   photo-night-alexanderplatz +11.7%
ours                   -    -10.1%     -12.5%    -10.2%    0.25   photo-night-brussels -0.6%
imagesharp-wu          -     +5.2%      +3.9%     +1.1%    0.14   photo-alpha +22.7%
imagesharp-octree      -    +29.1%     +14.2%    +12.0%    0.09   sticker +118.9%
ours-no-kmeans         -     -8.2%      -8.8%     -5.4%    0.17   photo-alpha -0.2%
ours-rgb-space         -     -2.2%      -7.9%     -7.8%    0.23   photo-alpha +9.4%
ours-full-fs           -    -10.1%     -12.5%    -10.2%    0.24   photo-night-brussels -0.6%
ours-no-edge           -    -10.1%     -12.5%    -10.2%    0.24   photo-night-brussels -0.6%
baseline-wu           fs    +40.2%     +30.6%     -0.5%    0.10   photo-night-alexanderplatz +63.4%
ours                  fs    -15.8%     -18.1%    -10.8%    0.30   photo-night-brussels -0.3%
imagesharp-wu         fs    +55.3%     +45.1%    +11.7%    0.21   photo-night-alexanderplatz +126.3%
imagesharp-octree     fs    +71.5%     +60.3%    +22.5%    0.13   sticker +137.5%
ours-no-kmeans        fs    -14.2%     -15.6%     -6.7%    0.24   photo-night-brussels -0.3%
ours-rgb-space        fs     -9.1%     -12.9%     -7.4%    0.29   photo-texture-ground +4.1%
ours-full-fs          fs    +17.1%     +16.7%     +5.0%    0.29   photo-architecture-teatro +40.5%
ours-no-edge          fs     -8.6%     -10.3%    -10.5%    0.26   photo-landscape-almsee +3.6%
```

Full per-point output:

```

image                  px     contestant          dith colors  used  common-B  native-B  SSIM-dB  OKLab-RMS  blurred      ms
photo-architecture-teatro  0.67M  pngquant-s4            -    256   256    334665    329608    21.34      1.119    0.684   406.6
photo-architecture-teatro  0.67M  pngquant-s4            -    128   128    266913    262122    19.47      1.427    0.927   382.6
photo-architecture-teatro  0.67M  pngquant-s4            -     64    64    198776    195396    17.23      1.792    1.144   323.7
photo-architecture-teatro  0.67M  pngquant-s4            -     32    32    151942    148623    14.77      2.310    1.486   310.0
photo-architecture-teatro  0.67M  pngquant-s4            -     16    16    109835    108362    12.52      3.047    1.985   312.2
photo-architecture-teatro  0.67M  baseline-wu            -    256   256    315631              20.19      1.166    0.665    36.8
photo-architecture-teatro  0.67M  baseline-wu            -    128   128    251948              18.93      1.444    0.854    23.7
photo-architecture-teatro  0.67M  baseline-wu            -     64    64    197549              16.78      1.831    1.112    18.8
photo-architecture-teatro  0.67M  baseline-wu            -     32    32    149612              14.40      2.395    1.469    15.4
photo-architecture-teatro  0.67M  baseline-wu            -     16    16    112729              12.89      3.189    2.119    36.3
photo-architecture-teatro  0.67M  ours                   -    256   256    322482              22.00      0.963    0.539   177.7
photo-architecture-teatro  0.67M  ours                   -    128   128    260868              20.60      1.221    0.760   141.2
photo-architecture-teatro  0.67M  ours                   -     64    64    206722              18.36      1.617    1.050   113.6
photo-architecture-teatro  0.67M  ours                   -     32    32    161752              16.02      2.081    1.399    97.8
photo-architecture-teatro  0.67M  ours                   -     16    16    119914              13.91      2.825    1.979    86.3
photo-architecture-teatro  0.67M  imagesharp-wu          -    256   256    308761              19.45      1.277    0.713    53.9
photo-architecture-teatro  0.67M  imagesharp-wu          -    128   128    249823              17.98      1.542    0.874    32.0
photo-architecture-teatro  0.67M  imagesharp-wu          -     64    64    197118              16.07      1.955    1.157    32.5
photo-architecture-teatro  0.67M  imagesharp-wu          -     32    32    153828              13.70      2.560    1.532    30.4
photo-architecture-teatro  0.67M  imagesharp-wu          -     16    16    114558              12.52      3.310    2.163    31.0
photo-architecture-teatro  0.67M  imagesharp-octree      -    256   252    188239              14.53      2.087    1.126    62.4
photo-architecture-teatro  0.67M  imagesharp-octree      -    128   123    172380              13.99      2.339    1.286    28.2
photo-architecture-teatro  0.67M  imagesharp-octree      -     64    58    117025              10.81      3.678    2.263    20.8
photo-architecture-teatro  0.67M  imagesharp-octree      -     32    25    106756              10.54      3.911    2.475    16.9
photo-architecture-teatro  0.67M  imagesharp-octree      -     16    15     52995               5.79      7.177    5.513    14.4
photo-architecture-teatro  0.67M  ours-no-kmeans         -    256   256    321715              21.75      1.001    0.563   107.8
photo-architecture-teatro  0.67M  ours-no-kmeans         -    128   128    263324              20.59      1.265    0.806    91.9
photo-architecture-teatro  0.67M  ours-no-kmeans         -     64    64    203443              17.97      1.663    1.090    80.1
photo-architecture-teatro  0.67M  ours-no-kmeans         -     32    32    159976              15.98      2.126    1.467    71.4
photo-architecture-teatro  0.67M  ours-no-kmeans         -     16    16    125359              14.09      2.961    2.168    63.6
photo-architecture-teatro  0.67M  ours-rgb-space         -    256   256    313765              20.88      1.096    0.667   156.8
photo-architecture-teatro  0.67M  ours-rgb-space         -    128   128    247133              19.04      1.375    0.857   130.4
photo-architecture-teatro  0.67M  ours-rgb-space         -     64    64    197632              17.44      1.796    1.203   106.6
photo-architecture-teatro  0.67M  ours-rgb-space         -     32    32    150785              14.98      2.268    1.467    89.8
photo-architecture-teatro  0.67M  ours-rgb-space         -     16    16    103515              12.44      3.108    2.008    78.2
photo-architecture-teatro  0.67M  ours-full-fs           -    256   256    322482              22.00      0.963    0.539   157.6
photo-architecture-teatro  0.67M  ours-full-fs           -    128   128    260868              20.60      1.221    0.760   129.4
photo-architecture-teatro  0.67M  ours-full-fs           -     64    64    206722              18.36      1.617    1.050   108.8
photo-architecture-teatro  0.67M  ours-full-fs           -     32    32    161752              16.02      2.081    1.399    94.0
photo-architecture-teatro  0.67M  ours-full-fs           -     16    16    119914              13.91      2.825    1.979    83.7
photo-architecture-teatro  0.67M  ours-no-edge           -    256   256    322482              22.00      0.963    0.539   158.2
photo-architecture-teatro  0.67M  ours-no-edge           -    128   128    260868              20.60      1.221    0.760   128.3
photo-architecture-teatro  0.67M  ours-no-edge           -     64    64    206722              18.36      1.617    1.050   108.8
photo-architecture-teatro  0.67M  ours-no-edge           -     32    32    161752              16.02      2.081    1.399    94.3
photo-architecture-teatro  0.67M  ours-no-edge           -     16    16    119914              13.91      2.825    1.979    83.6
photo-architecture-teatro  0.67M  pngquant-s4           fs    256   256    365824    360583    19.73      1.151    0.637   473.7
photo-architecture-teatro  0.67M  pngquant-s4           fs    128   128    307062    302937    17.12      1.486    0.841   455.8
photo-architecture-teatro  0.67M  pngquant-s4           fs     64    64    247900    244766    14.23      1.902    1.029   435.5
photo-architecture-teatro  0.67M  pngquant-s4           fs     32    32    197838    194350    12.24      2.465    1.323   459.1
photo-architecture-teatro  0.67M  pngquant-s4           fs     16    16    147244    146246     9.86      3.341    1.663   381.9
photo-architecture-teatro  0.67M  baseline-wu           fs    256   256    377836              13.51      1.715    0.444    51.9
photo-architecture-teatro  0.67M  baseline-wu           fs    128   128    307931              12.36      2.166    0.588    40.8
photo-architecture-teatro  0.67M  baseline-wu           fs     64    64    252266              10.89      2.635    0.854    32.5
photo-architecture-teatro  0.67M  baseline-wu           fs     32    32    202052               7.53      3.724    1.125    27.1
photo-architecture-teatro  0.67M  baseline-wu           fs     16    16    153687               5.53      4.858    1.702    26.6
photo-architecture-teatro  0.67M  ours                  fs    256   256    354835              19.54      1.002    0.483   190.6
photo-architecture-teatro  0.67M  ours                  fs    128   128    289846              18.48      1.265    0.697   160.5
photo-architecture-teatro  0.67M  ours                  fs     64    64    233051              15.71      1.704    0.936   138.9
photo-architecture-teatro  0.67M  ours                  fs     32    32    184115              13.29      2.186    1.270   125.6
photo-architecture-teatro  0.67M  ours                  fs     16    16    142689              11.86      2.960    1.796   113.4
photo-architecture-teatro  0.67M  imagesharp-wu         fs    256   256    391230              12.45      1.865    0.457    74.9
photo-architecture-teatro  0.67M  imagesharp-wu         fs    128   128    318352              12.13      2.272    0.598    60.7
photo-architecture-teatro  0.67M  imagesharp-wu         fs     64    64    258591              10.95      2.699    0.862    57.4
photo-architecture-teatro  0.67M  imagesharp-wu         fs     32    32    205319               7.63      3.741    1.129    56.7
photo-architecture-teatro  0.67M  imagesharp-wu         fs     16    16    155199               5.55      4.877    1.707    55.1
photo-architecture-teatro  0.67M  imagesharp-octree     fs    256   252    310135              10.90      2.435    0.454    71.4
photo-architecture-teatro  0.67M  imagesharp-octree     fs    128   123    280906              10.43      2.740    0.548    48.5
photo-architecture-teatro  0.67M  imagesharp-octree     fs     64    58    215082               8.45      3.954    1.321    40.1
photo-architecture-teatro  0.67M  imagesharp-octree     fs     32    25    179223               6.95      4.327    1.769    36.6
photo-architecture-teatro  0.67M  imagesharp-octree     fs     16    15    111117               4.94      6.885    4.337    34.7
photo-architecture-teatro  0.67M  ours-no-kmeans        fs    256   256    353767              19.83      1.043    0.498   134.4
photo-architecture-teatro  0.67M  ours-no-kmeans        fs    128   128    293726              18.70      1.315    0.734   117.5
photo-architecture-teatro  0.67M  ours-no-kmeans        fs     64    64    231254              15.63      1.753    0.970   107.5
photo-architecture-teatro  0.67M  ours-no-kmeans        fs     32    32    183832              13.14      2.237    1.328    99.4
photo-architecture-teatro  0.67M  ours-no-kmeans        fs     16    16    147019              11.72      3.079    2.007    90.9
photo-architecture-teatro  0.67M  ours-rgb-space        fs    256   256    348461              18.78      1.130    0.615   187.4
photo-architecture-teatro  0.67M  ours-rgb-space        fs    128   128    281051              16.59      1.421    0.789   162.3
photo-architecture-teatro  0.67M  ours-rgb-space        fs     64    64    228381              15.42      1.846    1.139   135.7
photo-architecture-teatro  0.67M  ours-rgb-space        fs     32    32    179191              12.56      2.355    1.339   121.2
photo-architecture-teatro  0.67M  ours-rgb-space        fs     16    16    120727              11.08      3.267    1.790   105.4
photo-architecture-teatro  0.67M  ours-full-fs          fs    256   256    386025              16.20      1.623    0.378   207.2
photo-architecture-teatro  0.67M  ours-full-fs          fs    128   128    320434              13.70      2.154    0.653   180.1
photo-architecture-teatro  0.67M  ours-full-fs          fs     64    64    261725              11.57      2.673    0.923   146.2
photo-architecture-teatro  0.67M  ours-full-fs          fs     32    32    207519               8.55      3.265    1.412   123.7
photo-architecture-teatro  0.67M  ours-full-fs          fs     16    16    159763               7.75      3.996    2.006   101.7
photo-architecture-teatro  0.67M  ours-no-edge          fs    256   256    371566              18.87      1.116    0.396   179.8
photo-architecture-teatro  0.67M  ours-no-edge          fs    128   128    306378              17.83      1.411    0.599   152.1
photo-architecture-teatro  0.67M  ours-no-edge          fs     64    64    248544              15.07      1.890    0.811   128.7
photo-architecture-teatro  0.67M  ours-no-edge          fs     32    32    196793              12.68      2.371    1.190   112.7
photo-architecture-teatro  0.67M  ours-no-edge          fs     16    16    154303              11.07      3.223    1.686    99.6
photo-foliage-mangrove  0.79M  pngquant-s4            -    256   256    325918    325437    19.69      0.662    0.419   302.7
photo-foliage-mangrove  0.79M  pngquant-s4            -    128   128    267160    265165    17.61      0.838    0.557   337.4
photo-foliage-mangrove  0.79M  pngquant-s4            -     64    64    208606    203743    15.72      1.078    0.764   420.9
photo-foliage-mangrove  0.79M  pngquant-s4            -     32    32    154708    147481    13.10      1.530    1.181   504.9
photo-foliage-mangrove  0.79M  pngquant-s4            -     16    16     97240     93607    10.26      2.150    1.632   457.5
photo-foliage-mangrove  0.79M  baseline-wu            -    256   256    309731              18.74      0.724    0.420    26.0
photo-foliage-mangrove  0.79M  baseline-wu            -    128   128    260852              17.16      0.892    0.565    18.9
photo-foliage-mangrove  0.79M  baseline-wu            -     64    64    202706              15.27      1.151    0.796    14.8
photo-foliage-mangrove  0.79M  baseline-wu            -     32    32    151783              12.93      1.579    1.195    12.9
photo-foliage-mangrove  0.79M  baseline-wu            -     16    16     96357              10.38      2.265    1.786    12.9
photo-foliage-mangrove  0.79M  ours                   -    256   256    337348              21.15      0.578    0.403    74.1
photo-foliage-mangrove  0.79M  ours                   -    128   128    272500              18.97      0.748    0.538    60.0
photo-foliage-mangrove  0.79M  ours                   -     64    64    209447              16.27      0.991    0.727    49.3
photo-foliage-mangrove  0.79M  ours                   -     32    32    153707              13.36      1.379    0.994    44.7
photo-foliage-mangrove  0.79M  ours                   -     16    16    112283              11.34      1.965    1.567    40.0
photo-foliage-mangrove  0.79M  imagesharp-wu          -    256   256    297029              17.57      0.797    0.471    32.4
photo-foliage-mangrove  0.79M  imagesharp-wu          -    128   128    257183              16.19      0.970    0.602    32.4
photo-foliage-mangrove  0.79M  imagesharp-wu          -     64    64    199595              14.26      1.233    0.820    31.1
photo-foliage-mangrove  0.79M  imagesharp-wu          -     32    32    150598              12.01      1.731    1.269    31.4
photo-foliage-mangrove  0.79M  imagesharp-wu          -     16    16     94330               9.77      2.432    1.903    31.6
photo-foliage-mangrove  0.79M  imagesharp-octree      -    256   254    197717              13.60      1.298    0.883    33.9
photo-foliage-mangrove  0.79M  imagesharp-octree      -    128   127    148352              11.40      1.786    1.203    26.3
photo-foliage-mangrove  0.79M  imagesharp-octree      -     64    63    103231               8.73      3.127    2.575    22.2
photo-foliage-mangrove  0.79M  imagesharp-octree      -     32    25     75179               6.61      4.374    3.571    19.0
photo-foliage-mangrove  0.79M  imagesharp-octree      -     16    15     43682               4.19      9.678    8.968    16.3
photo-foliage-mangrove  0.79M  ours-no-kmeans         -    256   256    338465              21.01      0.593    0.418    42.7
photo-foliage-mangrove  0.79M  ours-no-kmeans         -    128   128    271797              18.75      0.766    0.558    37.6
photo-foliage-mangrove  0.79M  ours-no-kmeans         -     64    64    211110              16.35      1.013    0.762    33.7
photo-foliage-mangrove  0.79M  ours-no-kmeans         -     32    32    155303              13.36      1.428    1.057    31.8
photo-foliage-mangrove  0.79M  ours-no-kmeans         -     16    16    110531              11.17      2.075    1.688    29.8
photo-foliage-mangrove  0.79M  ours-rgb-space         -    256   256    318635              19.62      0.653    0.416    73.9
photo-foliage-mangrove  0.79M  ours-rgb-space         -    128   128    256832              17.54      0.840    0.547    59.2
photo-foliage-mangrove  0.79M  ours-rgb-space         -     64    64    199755              15.34      1.100    0.789    49.8
photo-foliage-mangrove  0.79M  ours-rgb-space         -     32    32    144934              12.67      1.501    1.075    42.5
photo-foliage-mangrove  0.79M  ours-rgb-space         -     16    16     91714              10.10      2.197    1.655    36.7
photo-foliage-mangrove  0.79M  ours-full-fs           -    256   256    337348              21.15      0.578    0.403    73.0
photo-foliage-mangrove  0.79M  ours-full-fs           -    128   128    272500              18.97      0.748    0.538    59.5
photo-foliage-mangrove  0.79M  ours-full-fs           -     64    64    209447              16.27      0.991    0.727    50.4
photo-foliage-mangrove  0.79M  ours-full-fs           -     32    32    153707              13.36      1.379    0.994    44.0
photo-foliage-mangrove  0.79M  ours-full-fs           -     16    16    112283              11.34      1.965    1.567    39.9
photo-foliage-mangrove  0.79M  ours-no-edge           -    256   256    337348              21.15      0.578    0.403    74.6
photo-foliage-mangrove  0.79M  ours-no-edge           -    128   128    272500              18.97      0.748    0.538    60.2
photo-foliage-mangrove  0.79M  ours-no-edge           -     64    64    209447              16.27      0.991    0.727    50.0
photo-foliage-mangrove  0.79M  ours-no-edge           -     32    32    153707              13.36      1.379    0.994    44.4
photo-foliage-mangrove  0.79M  ours-no-edge           -     16    16    112283              11.34      1.965    1.567    41.2
photo-foliage-mangrove  0.79M  pngquant-s4           fs    256   256    367916    369072    17.53      0.710    0.371   368.9
photo-foliage-mangrove  0.79M  pngquant-s4           fs    128   128    312590    312758    14.62      0.940    0.474   394.9
photo-foliage-mangrove  0.79M  pngquant-s4           fs     64    64    254378    250856    12.04      1.242    0.646   485.3
photo-foliage-mangrove  0.79M  pngquant-s4           fs     32    32    199359    193753     9.48      1.743    0.989   636.3
photo-foliage-mangrove  0.79M  pngquant-s4           fs     16    16    141304    139024     6.50      2.543    1.252   562.4
photo-foliage-mangrove  0.79M  baseline-wu           fs    256   256    381628              12.89      1.126    0.322    58.1
photo-foliage-mangrove  0.79M  baseline-wu           fs    128   128    306543              10.18      1.537    0.496    51.1
photo-foliage-mangrove  0.79M  baseline-wu           fs     64    64    238384               8.27      2.000    0.711    40.5
photo-foliage-mangrove  0.79M  baseline-wu           fs     32    32    191928               6.34      2.719    1.074    32.3
photo-foliage-mangrove  0.79M  baseline-wu           fs     16    16    136684               4.58      3.666    1.607    29.8
photo-foliage-mangrove  0.79M  ours                  fs    256   256    380639              19.20      0.629    0.347   116.8
photo-foliage-mangrove  0.79M  ours                  fs    128   128    311349              16.70      0.815    0.460   103.2
photo-foliage-mangrove  0.79M  ours                  fs     64    64    243426              13.66      1.082    0.611    93.1
photo-foliage-mangrove  0.79M  ours                  fs     32    32    185035              10.55      1.515    0.817    86.6
photo-foliage-mangrove  0.79M  ours                  fs     16    16    139140               8.39      2.123    1.368    81.1
photo-foliage-mangrove  0.79M  imagesharp-wu         fs    256   246    385954              11.27      1.343    0.339    65.3
photo-foliage-mangrove  0.79M  imagesharp-wu         fs    128   127    321644               9.32      1.682    0.507    65.0
photo-foliage-mangrove  0.79M  imagesharp-wu         fs     64    64    247025               7.82      2.092    0.719    62.9
photo-foliage-mangrove  0.79M  imagesharp-wu         fs     32    32    196957               6.24      2.730    1.084    61.0
photo-foliage-mangrove  0.79M  imagesharp-wu         fs     16    16    139622               4.54      3.687    1.617    61.1
photo-foliage-mangrove  0.79M  imagesharp-octree     fs    256   254    343351               9.74      1.696    0.491    57.1
photo-foliage-mangrove  0.79M  imagesharp-octree     fs    128   127    281837               8.48      2.123    0.591    46.8
photo-foliage-mangrove  0.79M  imagesharp-octree     fs     64    63    214279               6.06      3.152    1.811    43.9
photo-foliage-mangrove  0.79M  imagesharp-octree     fs     32    25    164670               4.72      3.925    2.135    41.1
photo-foliage-mangrove  0.79M  imagesharp-octree     fs     16    15    100641               3.31      7.694    6.401    40.0
photo-foliage-mangrove  0.79M  ours-no-kmeans        fs    256   256    382187              19.14      0.645    0.360    84.0
photo-foliage-mangrove  0.79M  ours-no-kmeans        fs    128   128    309577              16.56      0.830    0.481    81.8
photo-foliage-mangrove  0.79M  ours-no-kmeans        fs     64    64    245725              14.02      1.098    0.661    76.6
photo-foliage-mangrove  0.79M  ours-no-kmeans        fs     32    32    185537              10.50      1.563    0.884    73.9
photo-foliage-mangrove  0.79M  ours-no-kmeans        fs     16    16    138396               8.10      2.243    1.468    71.1
photo-foliage-mangrove  0.79M  ours-rgb-space        fs    256   256    361215              17.67      0.707    0.358   115.3
photo-foliage-mangrove  0.79M  ours-rgb-space        fs    128   128    296821              15.24      0.912    0.461   100.0
photo-foliage-mangrove  0.79M  ours-rgb-space        fs     64    64    236628              12.56      1.208    0.664    91.1
photo-foliage-mangrove  0.79M  ours-rgb-space        fs     32    32    180256              10.00      1.636    0.896    81.4
photo-foliage-mangrove  0.79M  ours-rgb-space        fs     16    16    126167               7.42      2.405    1.373    73.3
photo-foliage-mangrove  0.79M  ours-full-fs          fs    256   256    382692              12.84      1.168    0.389   168.1
photo-foliage-mangrove  0.79M  ours-full-fs          fs    128   128    312968              11.08      1.428    0.569   141.8
photo-foliage-mangrove  0.79M  ours-full-fs          fs     64    64    247134               9.67      1.725    0.790   112.7
photo-foliage-mangrove  0.79M  ours-full-fs          fs     32    32    195547               7.58      2.245    0.978    90.9
photo-foliage-mangrove  0.79M  ours-full-fs          fs     16    16    141352               6.60      2.836    1.618    70.7
photo-foliage-mangrove  0.79M  ours-no-edge          fs    256   256    387857              18.71      0.673    0.321   104.5
photo-foliage-mangrove  0.79M  ours-no-edge          fs    128   128    317752              16.26      0.864    0.436    91.2
photo-foliage-mangrove  0.79M  ours-no-edge          fs     64    64    249003              13.24      1.139    0.584    79.3
photo-foliage-mangrove  0.79M  ours-no-edge          fs     32    32    190790              10.09      1.596    0.778    72.5
photo-foliage-mangrove  0.79M  ours-no-edge          fs     16    16    142121               8.00      2.212    1.340    66.0
photo-landscape-almsee  0.79M  pngquant-s4            -    256   256    403673    405588    19.95      1.028    0.538   400.5
photo-landscape-almsee  0.79M  pngquant-s4            -    128   128    320342    320324    17.45      1.293    0.724   384.0
photo-landscape-almsee  0.79M  pngquant-s4            -     64    64    249307    248106    14.79      1.649    0.974   374.0
photo-landscape-almsee  0.79M  pngquant-s4            -     32    32    192585    189702    12.71      2.227    1.510   409.4
photo-landscape-almsee  0.79M  pngquant-s4            -     16    16    141177    141343    11.20      2.954    2.184   392.3
photo-landscape-almsee  0.79M  baseline-wu            -    256   256    388941              18.64      1.079    0.542    39.4
photo-landscape-almsee  0.79M  baseline-wu            -    128   128    311749              16.32      1.343    0.735    27.4
photo-landscape-almsee  0.79M  baseline-wu            -     64    64    245602              14.11      1.760    1.074    20.5
photo-landscape-almsee  0.79M  baseline-wu            -     32    32    194855              12.67      2.303    1.573    17.5
photo-landscape-almsee  0.79M  baseline-wu            -     16    16    129168              10.00      3.186    2.232    17.5
photo-landscape-almsee  0.79M  ours                   -    256   256    403249              19.91      0.868    0.467   137.8
photo-landscape-almsee  0.79M  ours                   -    128   128    324911              17.25      1.108    0.639   112.8
photo-landscape-almsee  0.79M  ours                   -     64    64    255702              15.35      1.453    0.887    94.2
photo-landscape-almsee  0.79M  ours                   -     32    32    202995              13.24      1.987    1.297    82.9
photo-landscape-almsee  0.79M  ours                   -     16    16    143773              11.54      2.786    2.076    74.3
photo-landscape-almsee  0.79M  imagesharp-wu          -    256   256    381229              17.51      1.174    0.590    31.7
photo-landscape-almsee  0.79M  imagesharp-wu          -    128   128    310142              15.53      1.447    0.776    33.1
photo-landscape-almsee  0.79M  imagesharp-wu          -     64    64    245694              13.32      1.916    1.129    31.1
photo-landscape-almsee  0.79M  imagesharp-wu          -     32    32    193624              11.88      2.563    1.724    31.2
photo-landscape-almsee  0.79M  imagesharp-wu          -     16    16    127301               9.42      3.447    2.368    30.2
photo-landscape-almsee  0.79M  imagesharp-octree      -    256   249    256981              13.09      1.933    1.197    49.6
photo-landscape-almsee  0.79M  imagesharp-octree      -    128   127    191171              10.46      2.612    1.682    30.2
photo-landscape-almsee  0.79M  imagesharp-octree      -     64    61    174750              10.21      3.061    2.145    23.6
photo-landscape-almsee  0.79M  imagesharp-octree      -     32    30     97994               6.86      4.760    3.565    19.0
photo-landscape-almsee  0.79M  imagesharp-octree      -     16    15     80725               6.20      6.547    5.197    16.5
photo-landscape-almsee  0.79M  ours-no-kmeans         -    256   256    402950              19.77      0.898    0.497    92.0
photo-landscape-almsee  0.79M  ours-no-kmeans         -    128   128    324391              17.16      1.150    0.696    80.7
photo-landscape-almsee  0.79M  ours-no-kmeans         -     64    64    255314              15.21      1.495    0.939    70.3
photo-landscape-almsee  0.79M  ours-no-kmeans         -     32    32    207649              13.43      2.042    1.427    62.6
photo-landscape-almsee  0.79M  ours-no-kmeans         -     16    16    141618              11.45      2.820    2.111    56.2
photo-landscape-almsee  0.79M  ours-rgb-space         -    256   256    390205              19.32      1.020    0.543   136.9
photo-landscape-almsee  0.79M  ours-rgb-space         -    128   128    309103              16.98      1.275    0.718   109.7
photo-landscape-almsee  0.79M  ours-rgb-space         -     64    64    243565              14.51      1.621    0.984    91.2
photo-landscape-almsee  0.79M  ours-rgb-space         -     32    32    186370              12.49      2.193    1.515    78.1
photo-landscape-almsee  0.79M  ours-rgb-space         -     16    16    130448              10.69      2.965    2.133    66.3
photo-landscape-almsee  0.79M  ours-full-fs           -    256   256    403249              19.91      0.868    0.467   138.4
photo-landscape-almsee  0.79M  ours-full-fs           -    128   128    324911              17.25      1.108    0.639   112.9
photo-landscape-almsee  0.79M  ours-full-fs           -     64    64    255702              15.35      1.453    0.887    93.6
photo-landscape-almsee  0.79M  ours-full-fs           -     32    32    202995              13.24      1.987    1.297    82.4
photo-landscape-almsee  0.79M  ours-full-fs           -     16    16    143773              11.54      2.786    2.076    73.9
photo-landscape-almsee  0.79M  ours-no-edge           -    256   256    403249              19.91      0.868    0.467   136.3
photo-landscape-almsee  0.79M  ours-no-edge           -    128   128    324911              17.25      1.108    0.639   112.3
photo-landscape-almsee  0.79M  ours-no-edge           -     64    64    255702              15.35      1.453    0.887    96.2
photo-landscape-almsee  0.79M  ours-no-edge           -     32    32    202995              13.24      1.987    1.297    83.0
photo-landscape-almsee  0.79M  ours-no-edge           -     16    16    143773              11.54      2.786    2.076    74.1
photo-landscape-almsee  0.79M  pngquant-s4           fs    256   256    425089    427218    19.01      1.044    0.511   475.7
photo-landscape-almsee  0.79M  pngquant-s4           fs    128   128    349428    350304    16.05      1.331    0.668   467.2
photo-landscape-almsee  0.79M  pngquant-s4           fs     64    64    283929    283207    12.74      1.730    0.867   486.5
photo-landscape-almsee  0.79M  pngquant-s4           fs     32    32    227886    224628     9.46      2.393    1.307   546.3
photo-landscape-almsee  0.79M  pngquant-s4           fs     16    16    171702    172189     7.66      3.234    1.833   468.9
photo-landscape-almsee  0.79M  baseline-wu           fs    256   256    434299              13.44      1.462    0.418    63.1
photo-landscape-almsee  0.79M  baseline-wu           fs    128   128    349971              11.95      1.816    0.607    50.4
photo-landscape-almsee  0.79M  baseline-wu           fs     64    64    277733               9.06      2.388    0.916    39.6
photo-landscape-almsee  0.79M  baseline-wu           fs     32    32    221705               7.18      3.282    1.388    33.1
photo-landscape-almsee  0.79M  baseline-wu           fs     16    16    162078               5.04      4.696    2.051    28.1
photo-landscape-almsee  0.79M  ours                  fs    256   256    429994              18.64      0.904    0.421   178.4
photo-landscape-almsee  0.79M  ours                  fs    128   128    353410              15.34      1.162    0.560   154.1
photo-landscape-almsee  0.79M  ours                  fs     64    64    284283              13.22      1.523    0.779   133.1
photo-landscape-almsee  0.79M  ours                  fs     32    32    227766              10.52      2.093    1.124   122.8
photo-landscape-almsee  0.79M  ours                  fs     16    16    165524               8.45      2.969    1.780   114.2
photo-landscape-almsee  0.79M  imagesharp-wu         fs    256   256    440055              12.05      1.631    0.435    67.2
photo-landscape-almsee  0.79M  imagesharp-wu         fs    128   128    357443              10.98      1.973    0.617    64.9
photo-landscape-almsee  0.79M  imagesharp-wu         fs     64    64    282101               8.84      2.468    0.931    63.3
photo-landscape-almsee  0.79M  imagesharp-wu         fs     32    32    225238               7.04      3.344    1.396    60.3
photo-landscape-almsee  0.79M  imagesharp-wu         fs     16    16    163399               5.00      4.712    2.067    60.4
photo-landscape-almsee  0.79M  imagesharp-octree     fs    256   249    383083               9.66      2.231    0.541    70.6
photo-landscape-almsee  0.79M  imagesharp-octree     fs    128   127    304557               7.98      2.820    0.916    52.2
photo-landscape-almsee  0.79M  imagesharp-octree     fs     64    61    261654               7.18      3.319    1.391    46.2
photo-landscape-almsee  0.79M  imagesharp-octree     fs     32    30    180795               5.09      4.788    2.579    41.8
photo-landscape-almsee  0.79M  imagesharp-octree     fs     16    15    145388               4.43      6.259    3.620    40.1
photo-landscape-almsee  0.79M  ours-no-kmeans        fs    256   256    430898              18.43      0.936    0.449   132.0
photo-landscape-almsee  0.79M  ours-no-kmeans        fs    128   128    354276              15.15      1.206    0.613   120.6
photo-landscape-almsee  0.79M  ours-no-kmeans        fs     64    64    283834              13.11      1.566    0.831   110.1
photo-landscape-almsee  0.79M  ours-no-kmeans        fs     32    32    231895              10.55      2.148    1.260   103.4
photo-landscape-almsee  0.79M  ours-no-kmeans        fs     16    16    163004               8.32      3.010    1.792    96.9
photo-landscape-almsee  0.79M  ours-rgb-space        fs    256   256    416692              18.00      1.050    0.499   174.8
photo-landscape-almsee  0.79M  ours-rgb-space        fs    128   128    337702              15.60      1.315    0.658   150.7
photo-landscape-almsee  0.79M  ours-rgb-space        fs     64    64    270464              12.17      1.690    0.871   131.8
photo-landscape-almsee  0.79M  ours-rgb-space        fs     32    32    212792               9.91      2.298    1.350   117.9
photo-landscape-almsee  0.79M  ours-rgb-space        fs     16    16    153433               8.22      3.103    1.907   102.8
photo-landscape-almsee  0.79M  ours-full-fs          fs    256   256    452563              16.02      1.293    0.409   185.0
photo-landscape-almsee  0.79M  ours-full-fs          fs    128   128    369209              12.97      1.665    0.594   155.7
photo-landscape-almsee  0.79M  ours-full-fs          fs     64    64    293096              11.14      2.054    0.902   132.6
photo-landscape-almsee  0.79M  ours-full-fs          fs     32    32    228541               7.63      2.967    1.294   112.4
photo-landscape-almsee  0.79M  ours-full-fs          fs     16    16    169169               6.06      4.064    2.242    91.5
photo-landscape-almsee  0.79M  ours-no-edge          fs    256   256    439650              18.18      0.983    0.366   167.7
photo-landscape-almsee  0.79M  ours-no-edge          fs    128   128    362693              14.96      1.254    0.500   141.5
photo-landscape-almsee  0.79M  ours-no-edge          fs     64    64    291802              12.80      1.638    0.708   121.5
photo-landscape-almsee  0.79M  ours-no-edge          fs     32    32    233666              10.05      2.233    1.045   110.2
photo-landscape-almsee  0.79M  ours-no-edge          fs     16    16    171489               7.99      3.125    1.725    96.1
photo-night-alexanderplatz  0.70M  pngquant-s4            -    256   256    411628    412756    23.51      0.850    0.533   353.6
photo-night-alexanderplatz  0.70M  pngquant-s4            -    128   128    337183    336473    20.71      0.997    0.698   377.5
photo-night-alexanderplatz  0.70M  pngquant-s4            -     64    64    262577    259838    19.63      1.196    0.846   615.7
photo-night-alexanderplatz  0.70M  pngquant-s4            -     32    32    184868    181386    16.87      1.456    0.989   383.9
photo-night-alexanderplatz  0.70M  pngquant-s4            -     16    16    114004    113516    13.18      1.954    1.275   278.6
photo-night-alexanderplatz  0.70M  baseline-wu            -    256   256    295595              18.74      0.990    0.691    26.4
photo-night-alexanderplatz  0.70M  baseline-wu            -    128   128    249558              18.07      1.109    0.770    18.7
photo-night-alexanderplatz  0.70M  baseline-wu            -     64    64    202770              16.68      1.278    0.867    14.6
photo-night-alexanderplatz  0.70M  baseline-wu            -     32    32    151024              14.61      1.563    1.071    12.1
photo-night-alexanderplatz  0.70M  baseline-wu            -     16    16    117227              13.30      1.894    1.296    12.1
photo-night-alexanderplatz  0.70M  ours                   -    256   256    382487              25.61      0.596    0.357    71.6
photo-night-alexanderplatz  0.70M  ours                   -    128   128    316632              23.83      0.763    0.490    59.1
photo-night-alexanderplatz  0.70M  ours                   -     64    64    255334              20.65      0.973    0.643    49.0
photo-night-alexanderplatz  0.70M  ours                   -     32    32    214997              18.58      1.267    0.923    41.6
photo-night-alexanderplatz  0.70M  ours                   -     16    16    140985              14.75      1.690    1.182    37.0
photo-night-alexanderplatz  0.70M  imagesharp-wu          -    256   256    267524              17.41      1.079    0.756    30.1
photo-night-alexanderplatz  0.70M  imagesharp-wu          -    128   128    234916              16.85      1.203    0.819    30.2
photo-night-alexanderplatz  0.70M  imagesharp-wu          -     64    64    194300              15.81      1.376    0.897    30.3
photo-night-alexanderplatz  0.70M  imagesharp-wu          -     32    32    146455              14.10      1.657    1.096    29.3
photo-night-alexanderplatz  0.70M  imagesharp-wu          -     16    16    114575              12.74      2.020    1.355    29.6
photo-night-alexanderplatz  0.70M  imagesharp-octree      -    256   255    113954               9.65      2.645    1.885    31.7
photo-night-alexanderplatz  0.70M  imagesharp-octree      -    128   124    100967               9.16      2.820    1.975    23.3
photo-night-alexanderplatz  0.70M  imagesharp-octree      -     64    60     88335               8.94      2.960    2.042    19.5
photo-night-alexanderplatz  0.70M  imagesharp-octree      -     32    29     71679               5.46      5.272    4.337    16.7
photo-night-alexanderplatz  0.70M  imagesharp-octree      -     16    15     65548               5.42      5.409    4.411    15.1
photo-night-alexanderplatz  0.70M  ours-no-kmeans         -    256   256    386321              25.27      0.640    0.400    37.9
photo-night-alexanderplatz  0.70M  ours-no-kmeans         -    128   128    326567              23.69      0.816    0.540    33.0
photo-night-alexanderplatz  0.70M  ours-no-kmeans         -     64    64    258667              20.62      1.030    0.717    29.7
photo-night-alexanderplatz  0.70M  ours-no-kmeans         -     32    32    214172              18.26      1.300    0.948    28.2
photo-night-alexanderplatz  0.70M  ours-no-kmeans         -     16    16    146811              14.58      1.744    1.202    25.3
photo-night-alexanderplatz  0.70M  ours-rgb-space         -    256   256    350706              22.14      0.790    0.525    73.3
photo-night-alexanderplatz  0.70M  ours-rgb-space         -    128   128    285520              20.52      0.957    0.673    60.9
photo-night-alexanderplatz  0.70M  ours-rgb-space         -     64    64    230295              18.86      1.148    0.799    48.4
photo-night-alexanderplatz  0.70M  ours-rgb-space         -     32    32    165322              15.69      1.475    1.034    41.0
photo-night-alexanderplatz  0.70M  ours-rgb-space         -     16    16    103474              12.52      1.915    1.259    34.2
photo-night-alexanderplatz  0.70M  ours-full-fs           -    256   256    382487              25.61      0.596    0.357    72.3
photo-night-alexanderplatz  0.70M  ours-full-fs           -    128   128    316632              23.83      0.763    0.490    58.6
photo-night-alexanderplatz  0.70M  ours-full-fs           -     64    64    255334              20.65      0.973    0.643    49.3
photo-night-alexanderplatz  0.70M  ours-full-fs           -     32    32    214997              18.58      1.267    0.923    42.0
photo-night-alexanderplatz  0.70M  ours-full-fs           -     16    16    140985              14.75      1.690    1.182    36.5
photo-night-alexanderplatz  0.70M  ours-no-edge           -    256   256    382487              25.61      0.596    0.357    71.9
photo-night-alexanderplatz  0.70M  ours-no-edge           -    128   128    316632              23.83      0.763    0.490    59.0
photo-night-alexanderplatz  0.70M  ours-no-edge           -     64    64    255334              20.65      0.973    0.643    50.7
photo-night-alexanderplatz  0.70M  ours-no-edge           -     32    32    214997              18.58      1.267    0.923    41.9
photo-night-alexanderplatz  0.70M  ours-no-edge           -     16    16    140985              14.75      1.690    1.182    37.5
photo-night-alexanderplatz  0.70M  pngquant-s4           fs    256   256    413897    415007    23.50      0.804    0.548   408.7
photo-night-alexanderplatz  0.70M  pngquant-s4           fs    128   128    339632    339198    21.47      0.974    0.620   447.0
photo-night-alexanderplatz  0.70M  pngquant-s4           fs     64    64    264577    261821    19.37      1.186    0.787   601.1
photo-night-alexanderplatz  0.70M  pngquant-s4           fs     32    32    196193    192804    16.32      1.493    0.946   429.2
photo-night-alexanderplatz  0.70M  pngquant-s4           fs     16    16    129055    129107    11.68      2.065    1.079   330.0
photo-night-alexanderplatz  0.70M  baseline-wu           fs    256   256    328329              16.31      1.192    0.523    42.7
photo-night-alexanderplatz  0.70M  baseline-wu           fs    128   128    269559              14.40      1.493    0.585    37.0
photo-night-alexanderplatz  0.70M  baseline-wu           fs     64    64    216996              12.14      1.929    0.695    34.0
photo-night-alexanderplatz  0.70M  baseline-wu           fs     32    32    172143              10.52      2.339    1.049    27.8
photo-night-alexanderplatz  0.70M  baseline-wu           fs     16    16    132541               8.79      2.798    1.377    25.7
photo-night-alexanderplatz  0.70M  ours                  fs    256   256    392916              24.80      0.619    0.337   106.0
photo-night-alexanderplatz  0.70M  ours                  fs    128   128    329767              23.00      0.794    0.457    94.0
photo-night-alexanderplatz  0.70M  ours                  fs     64    64    265468              19.73      1.010    0.605    83.6
photo-night-alexanderplatz  0.70M  ours                  fs     32    32    221998              17.84      1.298    0.901    76.1
photo-night-alexanderplatz  0.70M  ours                  fs     16    16    144426              14.17      1.729    1.143    70.8
photo-night-alexanderplatz  0.70M  imagesharp-wu         fs    256   249    294854              11.75      1.913    1.506    58.1
photo-night-alexanderplatz  0.70M  imagesharp-wu         fs    128   128    255674              11.09      2.098    1.526    56.4
photo-night-alexanderplatz  0.70M  imagesharp-wu         fs     64    64    210505               9.83      2.420    1.531    57.4
photo-night-alexanderplatz  0.70M  imagesharp-wu         fs     32    32    176168              10.07      2.414    1.070    54.8
photo-night-alexanderplatz  0.70M  imagesharp-wu         fs     16    16    134622               8.48      2.868    1.401    55.1
photo-night-alexanderplatz  0.70M  imagesharp-octree     fs    256   253    190265               9.25      2.491    1.107    49.3
photo-night-alexanderplatz  0.70M  imagesharp-octree     fs    128   124    166589               8.40      2.753    1.124    42.4
photo-night-alexanderplatz  0.70M  imagesharp-octree     fs     64    60    148460               7.79      3.041    1.165    38.5
photo-night-alexanderplatz  0.70M  imagesharp-octree     fs     32    29    126533               5.16      4.765    2.816    36.5
photo-night-alexanderplatz  0.70M  imagesharp-octree     fs     16    15     98596               4.71      4.990    2.929    36.5
photo-night-alexanderplatz  0.70M  ours-no-kmeans        fs    256   256    396529              24.55      0.663    0.374    72.6
photo-night-alexanderplatz  0.70M  ours-no-kmeans        fs    128   128    336232              22.87      0.843    0.509    65.9
photo-night-alexanderplatz  0.70M  ours-no-kmeans        fs     64    64    266192              19.85      1.061    0.693    63.4
photo-night-alexanderplatz  0.70M  ours-no-kmeans        fs     32    32    222019              17.47      1.335    0.926    59.6
photo-night-alexanderplatz  0.70M  ours-no-kmeans        fs     16    16    153683              13.88      1.792    1.163    58.3
photo-night-alexanderplatz  0.70M  ours-rgb-space        fs    256   256    360987              21.54      0.811    0.497   106.4
photo-night-alexanderplatz  0.70M  ours-rgb-space        fs    128   128    291133              19.89      0.981    0.655    91.6
photo-night-alexanderplatz  0.70M  ours-rgb-space        fs     64    64    237637              18.33      1.177    0.769    79.5
photo-night-alexanderplatz  0.70M  ours-rgb-space        fs     32    32    176921              15.20      1.506    0.955    71.8
photo-night-alexanderplatz  0.70M  ours-rgb-space        fs     16    16    111900              11.87      1.985    1.181    64.4
photo-night-alexanderplatz  0.70M  ours-full-fs          fs    256   256    404566              20.80      0.958    0.288   108.9
photo-night-alexanderplatz  0.70M  ours-full-fs          fs    128   128    332107              17.96      1.302    0.397    99.8
photo-night-alexanderplatz  0.70M  ours-full-fs          fs     64    64    271473              15.67      1.651    0.615    85.1
photo-night-alexanderplatz  0.70M  ours-full-fs          fs     32    32    222897              13.90      1.847    1.109    71.6
photo-night-alexanderplatz  0.70M  ours-full-fs          fs     16    16    141747              10.47      2.671    1.694    60.2
photo-night-alexanderplatz  0.70M  ours-no-edge          fs    256   256    399313              24.01      0.682    0.295    93.8
photo-night-alexanderplatz  0.70M  ours-no-edge          fs    128   128    335113              22.11      0.872    0.404    81.9
photo-night-alexanderplatz  0.70M  ours-no-edge          fs     64    64    270199              18.87      1.108    0.551    72.4
photo-night-alexanderplatz  0.70M  ours-no-edge          fs     32    32    226057              17.07      1.409    0.892    63.0
photo-night-alexanderplatz  0.70M  ours-no-edge          fs     16    16    146140              13.62      1.844    1.142    56.4
photo-night-brussels  0.44M  pngquant-s4            -    256   224    225401    256090   100.00      0.000    0.000    86.9
photo-night-brussels  0.44M  pngquant-s4            -    128    93    225536    253518    42.10      0.358    0.058    86.5
photo-night-brussels  0.44M  pngquant-s4            -     64    64    225919    244029    33.49      0.235    0.128    86.3
photo-night-brussels  0.44M  pngquant-s4            -     32    32    222602    190419    23.34      0.474    0.320   132.0
photo-night-brussels  0.44M  pngquant-s4            -     16    16    127989    130317    18.54      0.715    0.326   165.4
photo-night-brussels  0.44M  baseline-wu            -    256    30    111196              13.64      1.153    0.460     6.1
photo-night-brussels  0.44M  baseline-wu            -    128    30    111196              13.64      1.153    0.460     6.2
photo-night-brussels  0.44M  baseline-wu            -     64    30    111196              13.64      1.153    0.460     6.4
photo-night-brussels  0.44M  baseline-wu            -     32    30    111196              13.64      1.153    0.460     6.3
photo-night-brussels  0.44M  baseline-wu            -     16    16     81419              13.63      1.160    0.461     5.0
photo-night-brussels  0.44M  ours                   -    256   224    225401             100.00      0.000    0.000     4.7
photo-night-brussels  0.44M  ours                   -    128   128    225424              59.27      0.017    0.011     5.0
photo-night-brussels  0.44M  ours                   -     64    64    225535              34.92      0.120    0.096     4.7
photo-night-brussels  0.44M  ours                   -     32    32    222767              25.61      0.320    0.179     4.6
photo-night-brussels  0.44M  ours                   -     16    16    139171              19.23      0.684    0.350     4.6
photo-night-brussels  0.44M  imagesharp-wu          -    256    30    106308              13.62      1.106    0.461    25.3
photo-night-brussels  0.44M  imagesharp-wu          -    128    30    106308              13.62      1.106    0.461    26.0
photo-night-brussels  0.44M  imagesharp-wu          -     64    30    106308              13.62      1.106    0.461    24.9
photo-night-brussels  0.44M  imagesharp-wu          -     32    30    106308              13.62      1.106    0.461    25.9
photo-night-brussels  0.44M  imagesharp-wu          -     16    16     79865              13.61      1.114    0.462    25.8
photo-night-brussels  0.44M  imagesharp-octree      -    256   224    225401             100.00      0.000    0.000    19.7
photo-night-brussels  0.44M  imagesharp-octree      -    128   115    212435              23.27      0.565    0.405    17.9
photo-night-brussels  0.44M  imagesharp-octree      -     64    60    166442              18.26      0.690    0.370    15.8
photo-night-brussels  0.44M  imagesharp-octree      -     32    30    113426              13.87      1.126    0.549    13.8
photo-night-brussels  0.44M  imagesharp-octree      -     16    15     35372               9.50      1.990    1.248    11.9
photo-night-brussels  0.44M  ours-no-kmeans         -    256   224    225401             100.00      0.000    0.000     4.8
photo-night-brussels  0.44M  ours-no-kmeans         -    128   128    225429              59.14      0.017    0.011     4.7
photo-night-brussels  0.44M  ours-no-kmeans         -     64    64    225576              34.91      0.121    0.096     5.7
photo-night-brussels  0.44M  ours-no-kmeans         -     32    32    222795              25.60      0.321    0.180     4.7
photo-night-brussels  0.44M  ours-no-kmeans         -     16    16    139130              19.24      0.686    0.350     5.7
photo-night-brussels  0.44M  ours-rgb-space         -    256   224    225401             100.00      0.000    0.000     5.0
photo-night-brussels  0.44M  ours-rgb-space         -    128   128    225410              55.70      0.066    0.015     4.9
photo-night-brussels  0.44M  ours-rgb-space         -     64    64    225269              33.42      0.170    0.088     4.7
photo-night-brussels  0.44M  ours-rgb-space         -     32    32    221255              22.99      0.427    0.247     4.6
photo-night-brussels  0.44M  ours-rgb-space         -     16    16    131119              18.97      0.684    0.298     5.6
photo-night-brussels  0.44M  ours-full-fs           -    256   224    225401             100.00      0.000    0.000     4.6
photo-night-brussels  0.44M  ours-full-fs           -    128   128    225424              59.27      0.017    0.011     4.8
photo-night-brussels  0.44M  ours-full-fs           -     64    64    225535              34.92      0.120    0.096     4.8
photo-night-brussels  0.44M  ours-full-fs           -     32    32    222767              25.61      0.320    0.179     5.4
photo-night-brussels  0.44M  ours-full-fs           -     16    16    139171              19.23      0.684    0.350     4.7
photo-night-brussels  0.44M  ours-no-edge           -    256   224    225401             100.00      0.000    0.000     5.7
photo-night-brussels  0.44M  ours-no-edge           -    128   128    225424              59.27      0.017    0.011     4.8
photo-night-brussels  0.44M  ours-no-edge           -     64    64    225535              34.92      0.120    0.096     4.7
photo-night-brussels  0.44M  ours-no-edge           -     32    32    222767              25.61      0.320    0.179     4.7
photo-night-brussels  0.44M  ours-no-edge           -     16    16    139171              19.23      0.684    0.350     5.4
photo-night-brussels  0.44M  pngquant-s4           fs    256   224    225401    256090   100.00      0.000    0.000    99.4
photo-night-brussels  0.44M  pngquant-s4           fs    128    93    225546    253530    42.10      0.358    0.058    99.3
photo-night-brussels  0.44M  pngquant-s4           fs     64    64    225932    244036    33.48      0.235    0.127    99.8
photo-night-brussels  0.44M  pngquant-s4           fs     32    32    222855    190630    23.26      0.477    0.318   152.2
photo-night-brussels  0.44M  pngquant-s4           fs     16    16    130954    133625    18.04      0.737    0.292   178.6
photo-night-brussels  0.44M  baseline-wu           fs    256    30    138951              12.34      1.273    0.316     9.9
photo-night-brussels  0.44M  baseline-wu           fs    128    30    138951              12.34      1.273    0.316     9.9
photo-night-brussels  0.44M  baseline-wu           fs     64    30    138951              12.34      1.273    0.316     9.9
photo-night-brussels  0.44M  baseline-wu           fs     32    30    138951              12.34      1.273    0.316     9.8
photo-night-brussels  0.44M  baseline-wu           fs     16    16     88648              12.33      1.282    0.316     8.8
photo-night-brussels  0.44M  ours                  fs    256   224    225401             100.00      0.000    0.000    18.9
photo-night-brussels  0.44M  ours                  fs    128   128    225424              59.27      0.017    0.011    19.2
photo-night-brussels  0.44M  ours                  fs     64    64    225639              34.36      0.121    0.091    19.2
photo-night-brussels  0.44M  ours                  fs     32    32    223724              25.13      0.329    0.165    19.8
photo-night-brussels  0.44M  ours                  fs     16    16    140777              18.68      0.711    0.302    21.4
photo-night-brussels  0.44M  imagesharp-wu         fs    256    29    127584               7.47      2.341    0.379    40.1
photo-night-brussels  0.44M  imagesharp-wu         fs    128    29    127584               7.47      2.341    0.379    39.5
photo-night-brussels  0.44M  imagesharp-wu         fs     64    29    127584               7.47      2.341    0.379    40.1
photo-night-brussels  0.44M  imagesharp-wu         fs     32    29    127584               7.47      2.341    0.379    40.6
photo-night-brussels  0.44M  imagesharp-wu         fs     16    15     76331               7.47      2.345    0.379    39.5
photo-night-brussels  0.44M  imagesharp-octree     fs    256    31    152248              11.52      1.371    0.301    22.5
photo-night-brussels  0.44M  imagesharp-octree     fs    128    31    144116              11.77      1.326    0.300    21.8
photo-night-brussels  0.44M  imagesharp-octree     fs     64    30    125202              12.70      1.188    0.326    21.0
photo-night-brussels  0.44M  imagesharp-octree     fs     32    29    132439              12.67      1.209    0.324    20.4
photo-night-brussels  0.44M  imagesharp-octree     fs     16    15     51424               7.89      2.227    0.952    20.7
photo-night-brussels  0.44M  ours-no-kmeans        fs    256   224    225401             100.00      0.000    0.000    18.9
photo-night-brussels  0.44M  ours-no-kmeans        fs    128   128    225429              59.14      0.017    0.011    19.1
photo-night-brussels  0.44M  ours-no-kmeans        fs     64    64    225664              34.34      0.122    0.091    19.2
photo-night-brussels  0.44M  ours-no-kmeans        fs     32    32    223674              25.11      0.330    0.165    19.8
photo-night-brussels  0.44M  ours-no-kmeans        fs     16    16    140741              18.66      0.714    0.299    20.3
photo-night-brussels  0.44M  ours-rgb-space        fs    256   224    225401             100.00      0.000    0.000    18.9
photo-night-brussels  0.44M  ours-rgb-space        fs    128   128    225410              55.70      0.066    0.015    19.7
photo-night-brussels  0.44M  ours-rgb-space        fs     64    64    225335              33.16      0.172    0.085    19.7
photo-night-brussels  0.44M  ours-rgb-space        fs     32    32    222152              22.68      0.439    0.222    20.0
photo-night-brussels  0.44M  ours-rgb-space        fs     16    16    132822              18.37      0.715    0.254    20.3
photo-night-brussels  0.44M  ours-full-fs          fs    256   224    225401             100.00      0.000    0.000     9.6
photo-night-brussels  0.44M  ours-full-fs          fs    128   128    225426              57.77      0.020    0.009     9.8
photo-night-brussels  0.44M  ours-full-fs          fs     64    64    225987              32.71      0.138    0.055     9.9
photo-night-brussels  0.44M  ours-full-fs          fs     32    32    225884              23.83      0.378    0.123    10.4
photo-night-brussels  0.44M  ours-full-fs          fs     16    16    142750              17.65      0.806    0.227    10.6
photo-night-brussels  0.44M  ours-no-edge          fs    256   224    225401             100.00      0.000    0.000     9.7
photo-night-brussels  0.44M  ours-no-edge          fs    128   128    225428              58.76      0.018    0.009     9.8
photo-night-brussels  0.44M  ours-no-edge          fs     64    64    225896              33.58      0.128    0.075     9.9
photo-night-brussels  0.44M  ours-no-edge          fs     32    32    224749              24.61      0.349    0.144    11.2
photo-night-brussels  0.44M  ours-no-edge          fs     16    16    141747              18.29      0.750    0.260    11.4
photo-panorama-lakeview  0.36M  pngquant-s4            -    256   256    207169    208552    20.64      0.908    0.484   199.9
photo-panorama-lakeview  0.36M  pngquant-s4            -    128   128    166084    166734    18.46      1.127    0.608   195.4
photo-panorama-lakeview  0.36M  pngquant-s4            -     64    64    131209    131282    15.58      1.512    0.780   176.0
photo-panorama-lakeview  0.36M  pngquant-s4            -     32    32    102752    100613    13.17      2.527    1.847   179.5
photo-panorama-lakeview  0.36M  pngquant-s4            -     16    16     72804     72923     9.61      3.693    2.855   167.1
photo-panorama-lakeview  0.36M  baseline-wu            -    256   256    204731              19.76      0.917    0.478    25.2
photo-panorama-lakeview  0.36M  baseline-wu            -    128   128    166435              17.78      1.171    0.614    16.6
photo-panorama-lakeview  0.36M  baseline-wu            -     64    64    133509              15.13      1.513    0.818    12.3
photo-panorama-lakeview  0.36M  baseline-wu            -     32    32    104445              13.14      1.988    1.140     9.6
photo-panorama-lakeview  0.36M  baseline-wu            -     16    16     71005               9.53      3.666    2.695     9.8
photo-panorama-lakeview  0.36M  ours                   -    256   256    208653              21.38      0.755    0.439    84.4
photo-panorama-lakeview  0.36M  ours                   -    128   128    170420              19.47      0.961    0.563    68.6
photo-panorama-lakeview  0.36M  ours                   -     64    64    134859              16.47      1.286    0.762    57.9
photo-panorama-lakeview  0.36M  ours                   -     32    32    107713              13.91      1.778    1.137    51.4
photo-panorama-lakeview  0.36M  ours                   -     16    16     71931              10.66      2.611    1.634    44.9
photo-panorama-lakeview  0.36M  imagesharp-wu          -    256   256    200634              18.37      1.032    0.510    24.9
photo-panorama-lakeview  0.36M  imagesharp-wu          -    128   128    165948              16.71      1.342    0.671    25.2
photo-panorama-lakeview  0.36M  imagesharp-wu          -     64    64    133163              14.52      1.657    0.869    23.9
photo-panorama-lakeview  0.36M  imagesharp-wu          -     32    32    103318              12.48      2.091    1.159    24.2
photo-panorama-lakeview  0.36M  imagesharp-wu          -     16    16     69772               9.20      3.764    2.721    24.6
photo-panorama-lakeview  0.36M  imagesharp-octree      -    256   251    146060              13.14      2.711    2.043    22.3
photo-panorama-lakeview  0.36M  imagesharp-octree      -    128   125    108914              11.09      3.084    2.184    14.2
photo-panorama-lakeview  0.36M  imagesharp-octree      -     64    58     77129               8.02      6.170    4.888    10.4
photo-panorama-lakeview  0.36M  imagesharp-octree      -     32    31     67509               7.34      6.338    5.005     8.5
photo-panorama-lakeview  0.36M  imagesharp-octree      -     16    10     33464               4.25     12.350   10.873     6.8
photo-panorama-lakeview  0.36M  ours-no-kmeans         -    256   256    208828              21.21      0.776    0.449    55.7
photo-panorama-lakeview  0.36M  ours-no-kmeans         -    128   128    170829              19.39      0.979    0.586    47.3
photo-panorama-lakeview  0.36M  ours-no-kmeans         -     64    64    134088              16.26      1.314    0.783    43.1
photo-panorama-lakeview  0.36M  ours-no-kmeans         -     32    32    108433              13.82      1.816    1.205    39.7
photo-panorama-lakeview  0.36M  ours-no-kmeans         -     16    16     71711              10.60      2.654    1.651    35.9
photo-panorama-lakeview  0.36M  ours-rgb-space         -    256   256    203926              20.14      0.886    0.495    83.9
photo-panorama-lakeview  0.36M  ours-rgb-space         -    128   128    167167              17.96      1.112    0.610    67.4
photo-panorama-lakeview  0.36M  ours-rgb-space         -     64    64    131842              15.26      1.469    0.790    56.8
photo-panorama-lakeview  0.36M  ours-rgb-space         -     32    32    102695              12.46      2.536    1.856    48.0
photo-panorama-lakeview  0.36M  ours-rgb-space         -     16    16     71421               9.77      3.406    2.537    41.4
photo-panorama-lakeview  0.36M  ours-full-fs           -    256   256    208653              21.38      0.755    0.439    83.8
photo-panorama-lakeview  0.36M  ours-full-fs           -    128   128    170420              19.47      0.961    0.563    69.1
photo-panorama-lakeview  0.36M  ours-full-fs           -     64    64    134859              16.47      1.286    0.762    59.0
photo-panorama-lakeview  0.36M  ours-full-fs           -     32    32    107713              13.91      1.778    1.137    51.0
photo-panorama-lakeview  0.36M  ours-full-fs           -     16    16     71931              10.66      2.611    1.634    45.1
photo-panorama-lakeview  0.36M  ours-no-edge           -    256   256    208653              21.38      0.755    0.439    83.4
photo-panorama-lakeview  0.36M  ours-no-edge           -    128   128    170420              19.47      0.961    0.563    69.0
photo-panorama-lakeview  0.36M  ours-no-edge           -     64    64    134859              16.47      1.286    0.762    58.3
photo-panorama-lakeview  0.36M  ours-no-edge           -     32    32    107713              13.91      1.778    1.137    50.8
photo-panorama-lakeview  0.36M  ours-no-edge           -     16    16     71931              10.66      2.611    1.634    46.4
photo-panorama-lakeview  0.36M  pngquant-s4           fs    256   256    219391    221013    19.10      0.933    0.461   231.4
photo-panorama-lakeview  0.36M  pngquant-s4           fs    128   128    180550    181218    16.33      1.186    0.563   217.6
photo-panorama-lakeview  0.36M  pngquant-s4           fs     64    64    145206    146546    13.28      1.613    0.679   202.6
photo-panorama-lakeview  0.36M  pngquant-s4           fs     32    32    116144    115086    10.91      2.687    1.765   217.3
photo-panorama-lakeview  0.36M  pngquant-s4           fs     16    16     84677     84988     7.86      3.892    2.685   193.5
photo-panorama-lakeview  0.36M  baseline-wu           fs    256   256    220965              14.33      1.544    0.608    40.3
photo-panorama-lakeview  0.36M  baseline-wu           fs    128   128    183319              11.99      2.020    0.699    29.4
photo-panorama-lakeview  0.36M  baseline-wu           fs     64    64    148363              10.16      2.488    0.855    22.6
photo-panorama-lakeview  0.36M  baseline-wu           fs     32    32    120248               9.10      3.088    1.147    17.8
photo-panorama-lakeview  0.36M  baseline-wu           fs     16    16     87302               6.73      4.608    2.453    16.2
photo-panorama-lakeview  0.36M  ours                  fs    256   256    220262              20.14      0.781    0.409   103.5
photo-panorama-lakeview  0.36M  ours                  fs    128   128    179687              17.99      0.993    0.525    87.2
photo-panorama-lakeview  0.36M  ours                  fs     64    64    143673              14.92      1.333    0.706    78.3
photo-panorama-lakeview  0.36M  ours                  fs     32    32    116741              12.21      1.852    1.041    69.7
photo-panorama-lakeview  0.36M  ours                  fs     16    16     80236               8.97      2.748    1.420    62.2
photo-panorama-lakeview  0.36M  imagesharp-wu         fs    256   254    223586              13.18      1.739    0.629    45.0
photo-panorama-lakeview  0.36M  imagesharp-wu         fs    128   128    187461              11.22      2.186    0.759    42.7
photo-panorama-lakeview  0.36M  imagesharp-wu         fs     64    64    150875               9.83      2.598    0.887    40.3
photo-panorama-lakeview  0.36M  imagesharp-wu         fs     32    32    122440               8.89      3.159    1.170    39.5
photo-panorama-lakeview  0.36M  imagesharp-wu         fs     16    16     88157               6.67      4.635    2.456    39.1
photo-panorama-lakeview  0.36M  imagesharp-octree     fs    256   251    204145              11.53      2.657    1.885    34.6
photo-panorama-lakeview  0.36M  imagesharp-octree     fs    128   125    165187               9.87      2.985    1.909    26.3
photo-panorama-lakeview  0.36M  imagesharp-octree     fs     64    58    130088               7.54      5.662    4.455    22.1
photo-panorama-lakeview  0.36M  imagesharp-octree     fs     32    31    112980               6.40      5.968    4.515    20.0
photo-panorama-lakeview  0.36M  imagesharp-octree     fs     16    10     58474               4.07     11.813   10.119    18.7
photo-panorama-lakeview  0.36M  ours-no-kmeans        fs    256   256    220373              19.95      0.803    0.418    74.0
photo-panorama-lakeview  0.36M  ours-no-kmeans        fs    128   128    180589              17.87      1.013    0.547    67.4
photo-panorama-lakeview  0.36M  ours-no-kmeans        fs     64    64    143365              14.74      1.364    0.724    62.6
photo-panorama-lakeview  0.36M  ours-no-kmeans        fs     32    32    117287              12.15      1.890    1.108    56.7
photo-panorama-lakeview  0.36M  ours-no-kmeans        fs     16    16     80296               8.91      2.795    1.431    52.9
photo-panorama-lakeview  0.36M  ours-rgb-space        fs    256   256    216886              18.74      0.912    0.469   103.0
photo-panorama-lakeview  0.36M  ours-rgb-space        fs    128   128    177744              16.11      1.154    0.562    87.5
photo-panorama-lakeview  0.36M  ours-rgb-space        fs     64    64    141866              13.34      1.536    0.698    76.2
photo-panorama-lakeview  0.36M  ours-rgb-space        fs     32    32    112385              10.62      2.624    1.778    65.8
photo-panorama-lakeview  0.36M  ours-rgb-space        fs     16    16     79288               8.31      3.531    2.371    58.3
photo-panorama-lakeview  0.36M  ours-full-fs          fs    256   256    224255              14.87      1.788    0.997   122.0
photo-panorama-lakeview  0.36M  ours-full-fs          fs    128   128    187177              12.96      2.152    1.115   101.5
photo-panorama-lakeview  0.36M  ours-full-fs          fs     64    64    150220              11.71      2.479    1.159    82.1
photo-panorama-lakeview  0.36M  ours-full-fs          fs     32    32    122950              10.07      3.068    1.303    68.7
photo-panorama-lakeview  0.36M  ours-full-fs          fs     16    16     87688               7.56      3.815    1.446    58.7
photo-panorama-lakeview  0.36M  ours-no-edge          fs    256   256    223429              19.59      0.973    0.435   101.0
photo-panorama-lakeview  0.36M  ours-no-edge          fs    128   128    183096              17.44      1.161    0.525    82.9
photo-panorama-lakeview  0.36M  ours-no-edge          fs     64    64    146511              14.47      1.489    0.672    72.4
photo-panorama-lakeview  0.36M  ours-no-edge          fs     32    32    119393              11.73      2.053    0.927    63.2
photo-panorama-lakeview  0.36M  ours-no-edge          fs     16    16     83676               8.56      3.002    1.244    55.9
photo-portrait-carter  0.89M  pngquant-s4            -    256   256    431987    430238    19.63      0.817    0.519   352.7
photo-portrait-carter  0.89M  pngquant-s4            -    128   128    336258    334910    17.68      1.045    0.725   373.8
photo-portrait-carter  0.89M  pngquant-s4            -     64    64    247550    244943    15.59      1.313    0.905   484.5
photo-portrait-carter  0.89M  pngquant-s4            -     32    32    181609    176919    13.20      1.728    1.202   495.9
photo-portrait-carter  0.89M  pngquant-s4            -     16    16    115843    113242    10.17      2.416    1.678   469.3
photo-portrait-carter  0.89M  baseline-wu            -    256   256    404351              18.79      0.864    0.512    37.7
photo-portrait-carter  0.89M  baseline-wu            -    128   128    316117              16.90      1.086    0.723    26.4
photo-portrait-carter  0.89M  baseline-wu            -     64    64    239592              15.25      1.358    0.933    20.6
photo-portrait-carter  0.89M  baseline-wu            -     32    32    172247              12.80      1.746    1.211    17.3
photo-portrait-carter  0.89M  baseline-wu            -     16    16    105889               9.48      2.513    1.709    18.4
photo-portrait-carter  0.89M  ours                   -    256   256    408024              20.69      0.719    0.436   102.7
photo-portrait-carter  0.89M  ours                   -    128   128    316676              18.45      0.943    0.629    84.9
photo-portrait-carter  0.89M  ours                   -     64    64    253208              16.44      1.221    0.847    73.5
photo-portrait-carter  0.89M  ours                   -     32    32    184435              13.86      1.633    1.172    66.2
photo-portrait-carter  0.89M  ours                   -     16    16    123165              10.79      2.351    1.735    59.0
photo-portrait-carter  0.89M  imagesharp-wu          -    256   256    375458              17.50      0.937    0.564    33.9
photo-portrait-carter  0.89M  imagesharp-wu          -    128   128    306927              15.76      1.167    0.752    33.9
photo-portrait-carter  0.89M  imagesharp-wu          -     64    64    235659              14.26      1.440    0.951    32.7
photo-portrait-carter  0.89M  imagesharp-wu          -     32    32    172503              12.12      1.858    1.254    33.2
photo-portrait-carter  0.89M  imagesharp-wu          -     16    16    108036               9.23      2.668    1.863    32.4
photo-portrait-carter  0.89M  imagesharp-octree      -    256   250    250222              13.36      1.502    1.120    49.8
photo-portrait-carter  0.89M  imagesharp-octree      -    128   121    181230              11.26      1.971    1.418    33.5
photo-portrait-carter  0.89M  imagesharp-octree      -     64    63    134989               8.76      3.989    3.507    25.6
photo-portrait-carter  0.89M  imagesharp-octree      -     32    28     90140               6.74      4.908    4.123    21.9
photo-portrait-carter  0.89M  imagesharp-octree      -     16    15     47071               4.61     10.322    9.647    17.6
photo-portrait-carter  0.89M  ours-no-kmeans         -    256   256    412229              20.63      0.747    0.465    72.6
photo-portrait-carter  0.89M  ours-no-kmeans         -    128   128    318653              18.33      0.969    0.672    63.7
photo-portrait-carter  0.89M  ours-no-kmeans         -     64    64    252825              16.45      1.253    0.898    56.5
photo-portrait-carter  0.89M  ours-no-kmeans         -     32    32    185478              13.84      1.658    1.200    51.6
photo-portrait-carter  0.89M  ours-no-kmeans         -     16    16    130989              11.24      2.426    1.942    48.5
photo-portrait-carter  0.89M  ours-rgb-space         -    256   256    418067              19.64      0.812    0.536    99.4
photo-portrait-carter  0.89M  ours-rgb-space         -    128   128    322284              17.63      1.030    0.714    82.3
photo-portrait-carter  0.89M  ours-rgb-space         -     64    64    229845              15.06      1.314    0.904    69.6
photo-portrait-carter  0.89M  ours-rgb-space         -     32    32    173063              12.72      1.743    1.224    59.5
photo-portrait-carter  0.89M  ours-rgb-space         -     16    16    111034              10.21      2.373    1.652    52.2
photo-portrait-carter  0.89M  ours-full-fs           -    256   256    408024              20.69      0.719    0.436   103.8
photo-portrait-carter  0.89M  ours-full-fs           -    128   128    316676              18.45      0.943    0.629    85.3
photo-portrait-carter  0.89M  ours-full-fs           -     64    64    253208              16.44      1.221    0.847    73.1
photo-portrait-carter  0.89M  ours-full-fs           -     32    32    184435              13.86      1.633    1.172    64.5
photo-portrait-carter  0.89M  ours-full-fs           -     16    16    123165              10.79      2.351    1.735    59.6
photo-portrait-carter  0.89M  ours-no-edge           -    256   256    408024              20.69      0.719    0.436   103.0
photo-portrait-carter  0.89M  ours-no-edge           -    128   128    316676              18.45      0.943    0.629    86.5
photo-portrait-carter  0.89M  ours-no-edge           -     64    64    253208              16.44      1.221    0.847    73.6
photo-portrait-carter  0.89M  ours-no-edge           -     32    32    184435              13.86      1.633    1.172    64.8
photo-portrait-carter  0.89M  ours-no-edge           -     16    16    123165              10.79      2.351    1.735    58.7
photo-portrait-carter  0.89M  pngquant-s4           fs    256   256    458100    455655    18.77      0.851    0.489   427.0
photo-portrait-carter  0.89M  pngquant-s4           fs    128   128    374177    373926    16.39      1.110    0.681   431.7
photo-portrait-carter  0.89M  pngquant-s4           fs     64    64    294500    293566    13.70      1.442    0.818   539.7
photo-portrait-carter  0.89M  pngquant-s4           fs     32    32    236016    233242    10.55      1.990    1.008   645.9
photo-portrait-carter  0.89M  pngquant-s4           fs     16    16    176516    175902     7.33      2.895    1.243   545.5
photo-portrait-carter  0.89M  baseline-wu           fs    256   256    467379              13.26      1.349    0.354    77.4
photo-portrait-carter  0.89M  baseline-wu           fs    128   128    352530               9.93      1.911    0.601    63.3
photo-portrait-carter  0.89M  baseline-wu           fs     64    64    286267               8.57      2.345    0.865    47.0
photo-portrait-carter  0.89M  baseline-wu           fs     32    32    239144               7.34      2.836    1.144    37.4
photo-portrait-carter  0.89M  baseline-wu           fs     16    16    180261               5.62      3.638    1.454    33.5
photo-portrait-carter  0.89M  ours                  fs    256   256    455491              19.40      0.774    0.375   152.7
photo-portrait-carter  0.89M  ours                  fs    128   128    363535              17.04      1.006    0.562   135.8
photo-portrait-carter  0.89M  ours                  fs     64    64    290719              14.79      1.303    0.765   123.9
photo-portrait-carter  0.89M  ours                  fs     32    32    224258              12.22      1.747    1.046   113.9
photo-portrait-carter  0.89M  ours                  fs     16    16    164695               8.74      2.559    1.553   109.2
photo-portrait-carter  0.89M  imagesharp-wu         fs    256   252    469979              11.29      1.623    0.410    73.9
photo-portrait-carter  0.89M  imagesharp-wu         fs    128   128    366110               9.45      2.008    0.614    69.7
photo-portrait-carter  0.89M  imagesharp-wu         fs     64    64    293659               8.26      2.421    0.872    68.5
photo-portrait-carter  0.89M  imagesharp-wu         fs     32    32    245579               7.11      2.907    1.157    65.6
photo-portrait-carter  0.89M  imagesharp-wu         fs     16    16    183400               5.58      3.659    1.460    65.1
photo-portrait-carter  0.89M  imagesharp-octree     fs    256   250    393646              10.03      1.825    0.771    75.3
photo-portrait-carter  0.89M  imagesharp-octree     fs    128   121    307731               7.98      2.561    0.865    58.9
photo-portrait-carter  0.89M  imagesharp-octree     fs     64    63    226343               6.16      3.945    2.262    52.4
photo-portrait-carter  0.89M  imagesharp-octree     fs     32    28    190853               5.28      4.629    2.419    48.3
photo-portrait-carter  0.89M  imagesharp-octree     fs     16    15    127203               3.33      9.727    6.723    45.3
photo-portrait-carter  0.89M  ours-no-kmeans        fs    256   256    460554              19.46      0.799    0.406   125.0
photo-portrait-carter  0.89M  ours-no-kmeans        fs    128   128    364038              16.95      1.028    0.613   117.0
photo-portrait-carter  0.89M  ours-no-kmeans        fs     64    64    292252              14.97      1.328    0.826   108.0
photo-portrait-carter  0.89M  ours-no-kmeans        fs     32    32    224903              12.16      1.774    1.076   103.3
photo-portrait-carter  0.89M  ours-no-kmeans        fs     16    16    169730               8.98      2.614    1.798    96.6
photo-portrait-carter  0.89M  ours-rgb-space        fs    256   256    458863              18.30      0.856    0.483   149.2
photo-portrait-carter  0.89M  ours-rgb-space        fs    128   128    362979              16.20      1.089    0.660   132.7
photo-portrait-carter  0.89M  ours-rgb-space        fs     64    64    271659              13.35      1.411    0.819   115.4
photo-portrait-carter  0.89M  ours-rgb-space        fs     32    32    217398              10.72      1.892    1.063   106.2
photo-portrait-carter  0.89M  ours-rgb-space        fs     16    16    155547               8.13      2.623    1.333    96.3
photo-portrait-carter  0.89M  ours-full-fs          fs    256   256    473114              14.80      1.226    0.357   178.7
photo-portrait-carter  0.89M  ours-full-fs          fs    128   128    371824              12.17      1.626    0.622   155.8
photo-portrait-carter  0.89M  ours-full-fs          fs     64    64    298750              10.02      2.075    0.874   126.3
photo-portrait-carter  0.89M  ours-full-fs          fs     32    32    247123               9.60      2.343    1.176   100.3
photo-portrait-carter  0.89M  ours-full-fs          fs     16    16    181003               7.18      3.253    1.928    85.5
photo-portrait-carter  0.89M  ours-no-edge          fs    256   256    471618              18.86      0.832    0.338   140.9
photo-portrait-carter  0.89M  ours-no-edge          fs    128   128    375480              16.52      1.076    0.521   121.2
photo-portrait-carter  0.89M  ours-no-edge          fs     64    64    301507              14.22      1.389    0.723   107.0
photo-portrait-carter  0.89M  ours-no-edge          fs     32    32    236919              11.76      1.866    0.986    96.2
photo-portrait-carter  0.89M  ours-no-edge          fs     16    16    173222               8.32      2.713    1.559    86.9
photo-sea-ibiza       0.59M  pngquant-s4            -    256   256    205895    203515    22.72      0.570    0.319   259.7
photo-sea-ibiza       0.59M  pngquant-s4            -    128   128    147059    143067    20.24      0.729    0.408   289.6
photo-sea-ibiza       0.59M  pngquant-s4            -     64    64    100391     96001    17.69      0.970    0.549   277.0
photo-sea-ibiza       0.59M  pngquant-s4            -     32    32     69502     65339    15.56      1.296    0.776   239.8
photo-sea-ibiza       0.59M  pngquant-s4            -     16    16     47556     44857    13.34      1.734    1.137   189.5
photo-sea-ibiza       0.59M  baseline-wu            -    256   256    130735              19.23      0.589    0.363    19.4
photo-sea-ibiza       0.59M  baseline-wu            -    128   128    108513              18.45      0.698    0.417    14.3
photo-sea-ibiza       0.59M  baseline-wu            -     64    64     86270              17.01      0.885    0.545    10.7
photo-sea-ibiza       0.59M  baseline-wu            -     32    32     58933              14.86      1.174    0.771     9.3
photo-sea-ibiza       0.59M  baseline-wu            -     16    16     39694              12.16      1.729    1.246     8.9
photo-sea-ibiza       0.59M  ours                   -    256   256    167685              23.13      0.454    0.267    60.5
photo-sea-ibiza       0.59M  ours                   -    128   128    122230              20.59      0.589    0.356    49.3
photo-sea-ibiza       0.59M  ours                   -     64    64     87031              17.76      0.778    0.476    39.5
photo-sea-ibiza       0.59M  ours                   -     32    32     65497              15.30      1.077    0.696    34.4
photo-sea-ibiza       0.59M  ours                   -     16    16     43904              13.38      1.546    1.078    30.7
photo-sea-ibiza       0.59M  imagesharp-wu          -    256   256    122641              17.92      0.661    0.422    28.6
photo-sea-ibiza       0.59M  imagesharp-wu          -    128   128    107902              17.32      0.773    0.464    28.1
photo-sea-ibiza       0.59M  imagesharp-wu          -     64    64     88550              16.29      0.962    0.588    28.1
photo-sea-ibiza       0.59M  imagesharp-wu          -     32    32     62856              14.17      1.255    0.815    28.7
photo-sea-ibiza       0.59M  imagesharp-wu          -     16    16     42491              11.88      1.795    1.266    27.1
photo-sea-ibiza       0.59M  imagesharp-octree      -    256   251     78969              14.16      1.140    0.912    24.7
photo-sea-ibiza       0.59M  imagesharp-octree      -    128   125     47616              12.08      1.989    1.736    19.1
photo-sea-ibiza       0.59M  imagesharp-octree      -     64    62     43643              11.94      2.068    1.764    17.0
photo-sea-ibiza       0.59M  imagesharp-octree      -     32    31     25446              10.06      2.853    2.387    13.6
photo-sea-ibiza       0.59M  imagesharp-octree      -     16    15     21969               9.94      3.059    2.502    12.6
photo-sea-ibiza       0.59M  ours-no-kmeans         -    256   256    170279              22.93      0.470    0.277    29.0
photo-sea-ibiza       0.59M  ours-no-kmeans         -    128   128    122667              20.36      0.608    0.374    25.6
photo-sea-ibiza       0.59M  ours-no-kmeans         -     64    64     87105              17.61      0.811    0.495    22.9
photo-sea-ibiza       0.59M  ours-no-kmeans         -     32    32     64690              14.88      1.118    0.730    20.7
photo-sea-ibiza       0.59M  ours-no-kmeans         -     16    16     43955              13.36      1.585    1.121    19.2
photo-sea-ibiza       0.59M  ours-rgb-space         -    256   256    161184              21.85      0.497    0.295    60.7
photo-sea-ibiza       0.59M  ours-rgb-space         -    128   128    119787              19.66      0.639    0.378    48.1
photo-sea-ibiza       0.59M  ours-rgb-space         -     64    64     82920              16.79      0.841    0.495    38.5
photo-sea-ibiza       0.59M  ours-rgb-space         -     32    32     59263              15.00      1.130    0.722    33.7
photo-sea-ibiza       0.59M  ours-rgb-space         -     16    16     41365              12.96      1.595    1.047    27.9
photo-sea-ibiza       0.59M  ours-full-fs           -    256   256    167685              23.13      0.454    0.267    61.6
photo-sea-ibiza       0.59M  ours-full-fs           -    128   128    122230              20.59      0.589    0.356    48.8
photo-sea-ibiza       0.59M  ours-full-fs           -     64    64     87031              17.76      0.778    0.476    39.6
photo-sea-ibiza       0.59M  ours-full-fs           -     32    32     65497              15.30      1.077    0.696    33.4
photo-sea-ibiza       0.59M  ours-full-fs           -     16    16     43904              13.38      1.546    1.078    29.2
photo-sea-ibiza       0.59M  ours-no-edge           -    256   256    167685              23.13      0.454    0.267    61.8
photo-sea-ibiza       0.59M  ours-no-edge           -    128   128    122230              20.59      0.589    0.356    49.0
photo-sea-ibiza       0.59M  ours-no-edge           -     64    64     87031              17.76      0.778    0.476    39.4
photo-sea-ibiza       0.59M  ours-no-edge           -     32    32     65497              15.30      1.077    0.696    33.6
photo-sea-ibiza       0.59M  ours-no-edge           -     16    16     43904              13.38      1.546    1.078    29.0
photo-sea-ibiza       0.59M  pngquant-s4           fs    256   256    216048    213545    22.12      0.584    0.304   313.2
photo-sea-ibiza       0.59M  pngquant-s4           fs    128   128    176340    173028    18.57      0.769    0.371   337.8
photo-sea-ibiza       0.59M  pngquant-s4           fs     64    64    145902    142450    15.18      1.056    0.468   397.7
photo-sea-ibiza       0.59M  pngquant-s4           fs     32    32    124281    119981    11.73      1.447    0.633   450.7
photo-sea-ibiza       0.59M  pngquant-s4           fs     16    16     94305     91792     9.05      1.976    0.943   348.1
photo-sea-ibiza       0.59M  baseline-wu           fs    256   256    203408              13.46      0.908    0.234    31.7
photo-sea-ibiza       0.59M  baseline-wu           fs    128   128    178215              11.64      1.141    0.289    28.4
photo-sea-ibiza       0.59M  baseline-wu           fs     64    64    145998               9.97      1.421    0.439    27.7
photo-sea-ibiza       0.59M  baseline-wu           fs     32    32    120354               8.34      1.855    0.660    21.6
photo-sea-ibiza       0.59M  baseline-wu           fs     16    16     98992               5.82      2.647    0.995    18.7
photo-sea-ibiza       0.59M  ours                  fs    256   256    198057              21.49      0.477    0.243    82.7
photo-sea-ibiza       0.59M  ours                  fs    128   128    155972              18.60      0.623    0.319    71.0
photo-sea-ibiza       0.59M  ours                  fs     64    64    119508              15.65      0.828    0.418    61.3
photo-sea-ibiza       0.59M  ours                  fs     32    32     96930              12.83      1.144    0.602    54.6
photo-sea-ibiza       0.59M  ours                  fs     16    16     74300               9.48      1.694    0.876    50.0
photo-sea-ibiza       0.59M  imagesharp-wu         fs    256   251    203896              10.47      1.159    0.252    52.5
photo-sea-ibiza       0.59M  imagesharp-wu         fs    128   126    181382               9.99      1.287    0.303    51.7
photo-sea-ibiza       0.59M  imagesharp-wu         fs     64    64    155227               9.35      1.510    0.464    49.6
photo-sea-ibiza       0.59M  imagesharp-wu         fs     32    32    128087               8.12      1.888    0.639    50.0
photo-sea-ibiza       0.59M  imagesharp-wu         fs     16    16    105405               5.77      2.657    0.991    50.4
photo-sea-ibiza       0.59M  imagesharp-octree     fs    256   251    177259               8.68      1.557    0.372    40.1
photo-sea-ibiza       0.59M  imagesharp-octree     fs    128   125    148439               5.51      2.422    0.711    35.1
photo-sea-ibiza       0.59M  imagesharp-octree     fs     64    62    139536               5.31      2.532    0.741    32.4
photo-sea-ibiza       0.59M  imagesharp-octree     fs     32    31    101473               2.98      3.670    1.074    31.1
photo-sea-ibiza       0.59M  imagesharp-octree     fs     16    15     88636               3.06      3.872    1.189    29.4
photo-sea-ibiza       0.59M  ours-no-kmeans        fs    256   256    201154              21.45      0.493    0.251    50.7
photo-sea-ibiza       0.59M  ours-no-kmeans        fs    128   128    154004              18.46      0.643    0.335    48.7
photo-sea-ibiza       0.59M  ours-no-kmeans        fs     64    64    120253              15.51      0.862    0.430    45.1
photo-sea-ibiza       0.59M  ours-no-kmeans        fs     32    32     96485              12.19      1.195    0.615    43.3
photo-sea-ibiza       0.59M  ours-no-kmeans        fs     16    16     74883              10.23      1.700    0.981    39.9
photo-sea-ibiza       0.59M  ours-rgb-space        fs    256   256    196236              20.24      0.520    0.269    83.3
photo-sea-ibiza       0.59M  ours-rgb-space        fs    128   128    156673              17.74      0.673    0.337    72.0
photo-sea-ibiza       0.59M  ours-rgb-space        fs     64    64    121939              14.40      0.902    0.421    60.8
photo-sea-ibiza       0.59M  ours-rgb-space        fs     32    32     93503              12.24      1.213    0.620    53.4
photo-sea-ibiza       0.59M  ours-rgb-space        fs     16    16     72192               9.76      1.717    0.876    48.9
photo-sea-ibiza       0.59M  ours-full-fs          fs    256   256    215065              15.39      0.873    0.232   103.5
photo-sea-ibiza       0.59M  ours-full-fs          fs    128   128    173284              12.73      1.096    0.316    89.6
photo-sea-ibiza       0.59M  ours-full-fs          fs     64    64    145938              10.87      1.420    0.444    70.9
photo-sea-ibiza       0.59M  ours-full-fs          fs     32    32    125539               9.95      1.745    0.613    56.4
photo-sea-ibiza       0.59M  ours-full-fs          fs     16    16     96969               7.39      2.376    0.821    48.9
photo-sea-ibiza       0.59M  ours-no-edge          fs    256   256    203088              21.08      0.513    0.220    73.3
photo-sea-ibiza       0.59M  ours-no-edge          fs    128   128    161123              18.15      0.669    0.295    61.7
photo-sea-ibiza       0.59M  ours-no-edge          fs     64    64    125305              15.26      0.889    0.385    53.6
photo-sea-ibiza       0.59M  ours-no-edge          fs     32    32    101578              12.51      1.235    0.547    46.2
photo-sea-ibiza       0.59M  ours-no-edge          fs     16    16     77794               9.08      1.824    0.793    41.3
photo-sunrise-fuji    0.59M  pngquant-s4            -    256   256    181964    179151    15.21      1.080    0.694   318.6
photo-sunrise-fuji    0.59M  pngquant-s4            -    128   128    139781    136707    13.06      1.371    0.899   297.2
photo-sunrise-fuji    0.59M  pngquant-s4            -     64    64    107952    104158    11.14      1.792    1.231   287.4
photo-sunrise-fuji    0.59M  pngquant-s4            -     32    32     82389     78557     9.72      2.429    1.795   283.0
photo-sunrise-fuji    0.59M  pngquant-s4            -     16    16     54948     53169     8.07      3.542    2.776   249.1
photo-sunrise-fuji    0.59M  baseline-wu            -    256   256    185055              14.90      1.119    0.726    30.6
photo-sunrise-fuji    0.59M  baseline-wu            -    128   128    144392              13.08      1.419    0.972    20.8
photo-sunrise-fuji    0.59M  baseline-wu            -     64    64    110180              11.11      1.854    1.305    15.6
photo-sunrise-fuji    0.59M  baseline-wu            -     32    32     82884               9.60      2.549    1.925    13.4
photo-sunrise-fuji    0.59M  baseline-wu            -     16    16     57100               8.36      3.561    2.846    15.0
photo-sunrise-fuji    0.59M  ours                   -    256   256    185598              16.03      0.948    0.590   134.5
photo-sunrise-fuji    0.59M  ours                   -    128   128    148328              14.08      1.235    0.826   111.1
photo-sunrise-fuji    0.59M  ours                   -     64    64    115464              12.21      1.635    1.158    93.4
photo-sunrise-fuji    0.59M  ours                   -     32    32     88837              10.61      2.243    1.684    82.3
photo-sunrise-fuji    0.59M  ours                   -     16    16     62795               9.12      3.143    2.532    73.8
photo-sunrise-fuji    0.59M  imagesharp-wu          -    256   256    185960              14.19      1.222    0.796    28.8
photo-sunrise-fuji    0.59M  imagesharp-wu          -    128   128    145727              12.59      1.530    1.042    28.2
photo-sunrise-fuji    0.59M  imagesharp-wu          -     64    64    112431              10.99      1.944    1.371    28.2
photo-sunrise-fuji    0.59M  imagesharp-wu          -     32    32     85010               9.52      2.662    2.019    29.7
photo-sunrise-fuji    0.59M  imagesharp-wu          -     16    16     57841               8.21      3.682    2.939    27.3
photo-sunrise-fuji    0.59M  imagesharp-octree      -    256   253    126571              11.26      1.912    1.333    39.5
photo-sunrise-fuji    0.59M  imagesharp-octree      -    128   125    109640              10.42      2.116    1.541    22.4
photo-sunrise-fuji    0.59M  imagesharp-octree      -     64    62     73070               8.60      3.399    2.631    16.9
photo-sunrise-fuji    0.59M  imagesharp-octree      -     32    30     62206               8.02      3.896    3.174    14.2
photo-sunrise-fuji    0.59M  imagesharp-octree      -     16    11     29677               6.03      7.731    6.803    11.6
photo-sunrise-fuji    0.59M  ours-no-kmeans         -    256   256    188054              15.79      0.980    0.622    89.6
photo-sunrise-fuji    0.59M  ours-no-kmeans         -    128   128    149826              13.91      1.284    0.885    78.9
photo-sunrise-fuji    0.59M  ours-no-kmeans         -     64    64    116420              12.23      1.689    1.232    67.8
photo-sunrise-fuji    0.59M  ours-no-kmeans         -     32    32     90746              10.79      2.312    1.802    61.8
photo-sunrise-fuji    0.59M  ours-no-kmeans         -     16    16     63383               9.05      3.240    2.630    55.8
photo-sunrise-fuji    0.59M  ours-rgb-space         -    256   256    184242              15.27      1.086    0.715   132.3
photo-sunrise-fuji    0.59M  ours-rgb-space         -    128   128    143336              13.12      1.367    0.921   107.1
photo-sunrise-fuji    0.59M  ours-rgb-space         -     64    64    108104              11.15      1.779    1.234    90.9
photo-sunrise-fuji    0.59M  ours-rgb-space         -     32    32     82426               9.76      2.406    1.789    77.5
photo-sunrise-fuji    0.59M  ours-rgb-space         -     16    16     53141               8.29      3.452    2.681    66.8
photo-sunrise-fuji    0.59M  ours-full-fs           -    256   256    185598              16.03      0.948    0.590   133.9
photo-sunrise-fuji    0.59M  ours-full-fs           -    128   128    148328              14.08      1.235    0.826   109.7
photo-sunrise-fuji    0.59M  ours-full-fs           -     64    64    115464              12.21      1.635    1.158    94.2
photo-sunrise-fuji    0.59M  ours-full-fs           -     32    32     88837              10.61      2.243    1.684    83.5
photo-sunrise-fuji    0.59M  ours-full-fs           -     16    16     62795               9.12      3.143    2.532    74.5
photo-sunrise-fuji    0.59M  ours-no-edge           -    256   256    185598              16.03      0.948    0.590   135.5
photo-sunrise-fuji    0.59M  ours-no-edge           -    128   128    148328              14.08      1.235    0.826   112.3
photo-sunrise-fuji    0.59M  ours-no-edge           -     64    64    115464              12.21      1.635    1.158    94.4
photo-sunrise-fuji    0.59M  ours-no-edge           -     32    32     88837              10.61      2.243    1.684    83.4
photo-sunrise-fuji    0.59M  ours-no-edge           -     16    16     62795               9.12      3.143    2.532    73.6
photo-sunrise-fuji    0.59M  pngquant-s4           fs    256   256    252050    250976    12.44      1.203    0.580   379.9
photo-sunrise-fuji    0.59M  pngquant-s4           fs    128   128    209123    208724     9.87      1.582    0.678   370.3
photo-sunrise-fuji    0.59M  pngquant-s4           fs     64    64    175472    174537     7.48      2.146    0.847   365.9
photo-sunrise-fuji    0.59M  pngquant-s4           fs     32    32    146742    144747     5.57      2.970    1.169   392.7
photo-sunrise-fuji    0.59M  pngquant-s4           fs     16    16    112575    110549     4.13      4.223    1.780   312.1
photo-sunrise-fuji    0.59M  baseline-wu           fs    256   256    260516               8.68      1.839    0.517    60.5
photo-sunrise-fuji    0.59M  baseline-wu           fs    128   128    220387               7.50      2.225    0.712    44.0
photo-sunrise-fuji    0.59M  baseline-wu           fs     64    64    185186               6.21      2.725    0.889    34.3
photo-sunrise-fuji    0.59M  baseline-wu           fs     32    32    153809               4.43      3.879    1.328    26.0
photo-sunrise-fuji    0.59M  baseline-wu           fs     16    16    123110               3.24      5.424    1.735    23.9
photo-sunrise-fuji    0.59M  ours                  fs    256   256    242905              13.79      1.044    0.462   177.7
photo-sunrise-fuji    0.59M  ours                  fs    128   128    202508              11.55      1.375    0.631   153.3
photo-sunrise-fuji    0.59M  ours                  fs     64    64    166281               9.23      1.839    0.851   131.3
photo-sunrise-fuji    0.59M  ours                  fs     32    32    134422               7.36      2.537    1.234   121.5
photo-sunrise-fuji    0.59M  ours                  fs     16    16    103797               5.41      3.589    1.834   109.6
photo-sunrise-fuji    0.59M  imagesharp-wu         fs    256   256    277384               8.30      1.930    0.520    62.2
photo-sunrise-fuji    0.59M  imagesharp-wu         fs    128   128    233556               7.27      2.295    0.718    57.9
photo-sunrise-fuji    0.59M  imagesharp-wu         fs     64    64    193465               6.04      2.781    0.902    56.0
photo-sunrise-fuji    0.59M  imagesharp-wu         fs     32    32    159606               4.38      3.886    1.335    56.6
photo-sunrise-fuji    0.59M  imagesharp-wu         fs     16    16    125305               3.17      5.454    1.720    52.4
photo-sunrise-fuji    0.59M  imagesharp-octree     fs    256   253    258904               7.25      2.355    0.435    59.6
photo-sunrise-fuji    0.59M  imagesharp-octree     fs    128   125    230272               5.85      2.702    0.542    42.7
photo-sunrise-fuji    0.59M  imagesharp-octree     fs     64    62    182037               4.34      4.068    1.212    36.8
photo-sunrise-fuji    0.59M  imagesharp-octree     fs     32    30    156230               3.36      4.764    1.661    33.4
photo-sunrise-fuji    0.59M  imagesharp-octree     fs     16    11     94516               2.55      7.853    5.358    31.5
photo-sunrise-fuji    0.59M  ours-no-kmeans        fs    256   256    246451              13.46      1.085    0.479   131.4
photo-sunrise-fuji    0.59M  ours-no-kmeans        fs    128   128    204946              11.24      1.433    0.670   122.3
photo-sunrise-fuji    0.59M  ours-no-kmeans        fs     64    64    168329               9.23      1.901    0.902   106.3
photo-sunrise-fuji    0.59M  ours-no-kmeans        fs     32    32    135682               7.59      2.591    1.394   100.2
photo-sunrise-fuji    0.59M  ours-no-kmeans        fs     16    16    104453               5.37      3.671    1.952    90.0
photo-sunrise-fuji    0.59M  ours-rgb-space        fs    256   256    240561              13.10      1.169    0.612   174.7
photo-sunrise-fuji    0.59M  ours-rgb-space        fs    128   128    197830              10.50      1.509    0.724   148.1
photo-sunrise-fuji    0.59M  ours-rgb-space        fs     64    64    162118               8.11      2.014    0.870   125.3
photo-sunrise-fuji    0.59M  ours-rgb-space        fs     32    32    132063               6.37      2.748    1.249   112.7
photo-sunrise-fuji    0.59M  ours-rgb-space        fs     16    16    101896               4.65      3.965    1.831    98.8
photo-sunrise-fuji    0.59M  ours-full-fs          fs    256   256    268735               9.91      1.732    0.471   212.0
photo-sunrise-fuji    0.59M  ours-full-fs          fs    128   128    225112               8.68      2.087    0.729   169.6
photo-sunrise-fuji    0.59M  ours-full-fs          fs     64    64    185773               7.26      2.509    1.049   133.6
photo-sunrise-fuji    0.59M  ours-full-fs          fs     32    32    153360               5.86      3.257    1.441   109.7
photo-sunrise-fuji    0.59M  ours-full-fs          fs     16    16    120281               3.92      4.528    2.093    91.3
photo-sunrise-fuji    0.59M  ours-no-edge          fs    256   256    250510              13.47      1.105    0.423   167.1
photo-sunrise-fuji    0.59M  ours-no-edge          fs    128   128    208897              11.23      1.441    0.594   139.3
photo-sunrise-fuji    0.59M  ours-no-edge          fs     64    64    171879               8.92      1.920    0.811   118.6
photo-sunrise-fuji    0.59M  ours-no-edge          fs     32    32    140911               7.00      2.661    1.170   104.8
photo-sunrise-fuji    0.59M  ours-no-edge          fs     16    16    110467               5.06      3.760    1.756    93.3
photo-texture-ground  0.63M  pngquant-s4            -    256   256    616819    631813    21.75      0.743    0.326   309.2
photo-texture-ground  0.63M  pngquant-s4            -    128   128    531962    538971    19.76      0.917    0.417   279.7
photo-texture-ground  0.63M  pngquant-s4            -     64    64    439554    446973    17.78      1.144    0.555   247.5
photo-texture-ground  0.63M  pngquant-s4            -     32    32    358427    367394    15.84      1.420    0.741   212.5
photo-texture-ground  0.63M  pngquant-s4            -     16    16    274748    285907    13.39      1.813    0.989   203.3
photo-texture-ground  0.63M  baseline-wu            -    256   256    566878              20.66      0.810    0.300    77.4
photo-texture-ground  0.63M  baseline-wu            -    128   128    512455              19.08      0.967    0.407    52.0
photo-texture-ground  0.63M  baseline-wu            -     64    64    432544              17.09      1.207    0.571    35.7
photo-texture-ground  0.63M  baseline-wu            -     32    32    356760              15.19      1.523    0.827    26.5
photo-texture-ground  0.63M  baseline-wu            -     16    16    270362              13.10      1.868    1.006    22.1
photo-texture-ground  0.63M  ours                   -    256   256    608389              23.40      0.623    0.278   157.9
photo-texture-ground  0.63M  ours                   -    128   128    519557              21.66      0.774    0.366   127.8
photo-texture-ground  0.63M  ours                   -     64    64    432445              19.73      0.965    0.478   107.5
photo-texture-ground  0.63M  ours                   -     32    32    348448              17.54      1.213    0.628    85.9
photo-texture-ground  0.63M  ours                   -     16    16    265470              15.12      1.561    0.834    74.9
photo-texture-ground  0.63M  imagesharp-wu          -    256   256    538620              19.03      0.888    0.351    32.1
photo-texture-ground  0.63M  imagesharp-wu          -    128   128    494965              17.62      1.054    0.446    31.7
photo-texture-ground  0.63M  imagesharp-wu          -     64    64    429150              15.65      1.320    0.620    30.9
photo-texture-ground  0.63M  imagesharp-wu          -     32    32    354854              13.63      1.635    0.840    29.9
photo-texture-ground  0.63M  imagesharp-wu          -     16    16    267838              12.11      1.937    1.016    30.7
photo-texture-ground  0.63M  imagesharp-octree      -    256   255    365685              13.97      1.470    0.629    46.9
photo-texture-ground  0.63M  imagesharp-octree      -    128   122    352551              13.74      1.532    0.668    28.9
photo-texture-ground  0.63M  imagesharp-octree      -     64    63    220696               8.61      2.670    1.512    20.5
photo-texture-ground  0.63M  imagesharp-octree      -     32    31    216735               8.57      2.711    1.541    18.1
photo-texture-ground  0.63M  imagesharp-octree      -     16    15    111109               5.31      3.976    2.827    14.6
photo-texture-ground  0.63M  ours-no-kmeans         -    256   256    608064              23.03      0.644    0.294    99.8
photo-texture-ground  0.63M  ours-no-kmeans         -    128   128    519804              21.52      0.808    0.382    83.5
photo-texture-ground  0.63M  ours-no-kmeans         -     64    64    431577              19.33      0.996    0.493    72.6
photo-texture-ground  0.63M  ours-no-kmeans         -     32    32    345980              17.03      1.241    0.649    62.7
photo-texture-ground  0.63M  ours-no-kmeans         -     16    16    262419              15.16      1.600    0.873    54.6
photo-texture-ground  0.63M  ours-rgb-space         -    256   256    603962              21.37      0.750    0.326   182.5
photo-texture-ground  0.63M  ours-rgb-space         -    128   128    518631              19.50      0.931    0.426   142.5
photo-texture-ground  0.63M  ours-rgb-space         -     64    64    429075              17.47      1.159    0.564   114.8
photo-texture-ground  0.63M  ours-rgb-space         -     32    32    352589              15.53      1.455    0.765    89.5
photo-texture-ground  0.63M  ours-rgb-space         -     16    16    268508              13.43      1.787    0.973    71.0
photo-texture-ground  0.63M  ours-full-fs           -    256   256    608389              23.40      0.623    0.278   157.5
photo-texture-ground  0.63M  ours-full-fs           -    128   128    519557              21.66      0.774    0.366   128.7
photo-texture-ground  0.63M  ours-full-fs           -     64    64    432445              19.73      0.965    0.478   106.2
photo-texture-ground  0.63M  ours-full-fs           -     32    32    348448              17.54      1.213    0.628    84.5
photo-texture-ground  0.63M  ours-full-fs           -     16    16    265470              15.12      1.561    0.834    70.6
photo-texture-ground  0.63M  ours-no-edge           -    256   256    608389              23.40      0.623    0.278   158.7
photo-texture-ground  0.63M  ours-no-edge           -    128   128    519557              21.66      0.774    0.366   129.0
photo-texture-ground  0.63M  ours-no-edge           -     64    64    432445              19.73      0.965    0.478   106.3
photo-texture-ground  0.63M  ours-no-edge           -     32    32    348448              17.54      1.213    0.628    84.2
photo-texture-ground  0.63M  ours-no-edge           -     16    16    265470              15.12      1.561    0.834    73.3
photo-texture-ground  0.63M  pngquant-s4           fs    256   256    616690    631789    21.68      0.743    0.320   382.8
photo-texture-ground  0.63M  pngquant-s4           fs    128   128    532381    539409    19.68      0.921    0.409   340.7
photo-texture-ground  0.63M  pngquant-s4           fs     64    64    440351    447820    17.68      1.152    0.541   297.3
photo-texture-ground  0.63M  pngquant-s4           fs     32    32    359533    368681    15.71      1.427    0.715   257.5
photo-texture-ground  0.63M  pngquant-s4           fs     16    16    276475    287803    13.16      1.831    0.952   238.3
photo-texture-ground  0.63M  baseline-wu           fs    256   256    579016              18.67      0.978    0.208    88.2
photo-texture-ground  0.63M  baseline-wu           fs    128   128    521540              16.55      1.217    0.302    65.2
photo-texture-ground  0.63M  baseline-wu           fs     64    64    433359              13.76      1.610    0.473    52.1
photo-texture-ground  0.63M  baseline-wu           fs     32    32    351353              11.21      2.101    0.789    45.1
photo-texture-ground  0.63M  baseline-wu           fs     16    16    267813               9.70      2.423    1.004    33.1
photo-texture-ground  0.63M  ours                  fs    256   256    610418              22.91      0.644    0.252   190.9
photo-texture-ground  0.63M  ours                  fs    128   128    522243              21.09      0.800    0.331   160.7
photo-texture-ground  0.63M  ours                  fs     64    64    435329              19.22      0.992    0.444   139.8
photo-texture-ground  0.63M  ours                  fs     32    32    351346              17.13      1.241    0.599   117.6
photo-texture-ground  0.63M  ours                  fs     16    16    268646              14.75      1.595    0.793   104.2
photo-texture-ground  0.63M  imagesharp-wu         fs    256   237    546233              14.95      1.354    0.241    56.0
photo-texture-ground  0.63M  imagesharp-wu         fs    128   125    508608              13.96      1.533    0.331    55.3
photo-texture-ground  0.63M  imagesharp-wu         fs     64    64    433625              12.37      1.816    0.492    55.2
photo-texture-ground  0.63M  imagesharp-wu         fs     32    32    353853              10.40      2.248    0.805    53.8
photo-texture-ground  0.63M  imagesharp-wu         fs     16    16    269996               9.22      2.521    1.018    54.3
photo-texture-ground  0.63M  imagesharp-octree     fs    256   255    448601              12.64      1.849    0.271    60.7
photo-texture-ground  0.63M  imagesharp-octree     fs    128   122    423097              12.57      1.843    0.303    43.2
photo-texture-ground  0.63M  imagesharp-octree     fs     64    63    324956               9.43      2.602    0.389    36.8
photo-texture-ground  0.63M  imagesharp-octree     fs     32    31    300582               8.77      2.732    0.547    34.5
photo-texture-ground  0.63M  imagesharp-octree     fs     16    15    195616               5.18      4.497    0.976    32.6
photo-texture-ground  0.63M  ours-no-kmeans        fs    256   256    610019              22.58      0.665    0.268   134.3
photo-texture-ground  0.63M  ours-no-kmeans        fs    128   128    521918              21.03      0.833    0.351   120.6
photo-texture-ground  0.63M  ours-no-kmeans        fs     64    64    434087              18.85      1.024    0.460   107.8
photo-texture-ground  0.63M  ours-no-kmeans        fs     32    32    348891              16.66      1.270    0.618    98.9
photo-texture-ground  0.63M  ours-no-kmeans        fs     16    16    265370              14.80      1.633    0.835    93.2
photo-texture-ground  0.63M  ours-rgb-space        fs    256   256    606695              20.84      0.773    0.287   220.8
photo-texture-ground  0.63M  ours-rgb-space        fs    128   128    521884              18.97      0.959    0.379   177.7
photo-texture-ground  0.63M  ours-rgb-space        fs     64    64    433558              16.99      1.191    0.511   148.7
photo-texture-ground  0.63M  ours-rgb-space        fs     32    32    356128              15.13      1.487    0.715   125.7
photo-texture-ground  0.63M  ours-rgb-space        fs     16    16    271706              13.13      1.814    0.923   107.5
photo-texture-ground  0.63M  ours-full-fs          fs    256   256    612079              20.92      0.805    0.238   186.9
photo-texture-ground  0.63M  ours-full-fs          fs    128   128    520566              18.71      1.050    0.357   157.6
photo-texture-ground  0.63M  ours-full-fs          fs     64    64    426229              16.05      1.402    0.610   145.0
photo-texture-ground  0.63M  ours-full-fs          fs     32    32    344555              14.14      1.812    0.967   127.8
photo-texture-ground  0.63M  ours-full-fs          fs     16    16    255004              11.47      2.261    1.263   105.2
photo-texture-ground  0.63M  ours-no-edge          fs    256   256    612216              22.10      0.696    0.215   178.5
photo-texture-ground  0.63M  ours-no-edge          fs    128   128    523851              20.25      0.864    0.291   151.5
photo-texture-ground  0.63M  ours-no-edge          fs     64    64    436598              18.39      1.063    0.409   124.8
photo-texture-ground  0.63M  ours-no-edge          fs     32    32    352787              16.46      1.310    0.573   105.3
photo-texture-ground  0.63M  ours-no-edge          fs     16    16    270009              14.09      1.678    0.758    90.1
photo-alpha           0.67M  pngquant-s4            -    256   256    195096    189762    14.01      2.516    1.530   620.3
photo-alpha           0.67M  pngquant-s4            -    128   128    151871    147864    12.16      3.216    2.075   548.7
photo-alpha           0.67M  pngquant-s4            -     64    64    114404    110908    10.29      4.210    2.871   484.5
photo-alpha           0.67M  pngquant-s4            -     32    32     84543     80976     8.60      5.684    4.353   466.8
photo-alpha           0.67M  pngquant-s4            -     16    16     58418     56435     6.46      8.184    6.708   409.0
photo-alpha           0.67M  baseline-wu            -    256   256    200693               4.83     36.058   35.610    27.0
photo-alpha           0.67M  baseline-wu            -    128   128    164442               4.80     36.056   35.604    18.4
photo-alpha           0.67M  baseline-wu            -     64    64    130361               4.76     36.062   35.605    13.4
photo-alpha           0.67M  baseline-wu            -     32    32     97406               4.67     36.063   35.595    10.7
photo-alpha           0.67M  baseline-wu            -     16    16     71164               4.52     36.097   35.609    11.0
photo-alpha           0.67M  ours                   -    256   256    182289              13.71      1.884    1.234   196.8
photo-alpha           0.67M  ours                   -    128   128    142713              11.82      2.441    1.668   163.3
photo-alpha           0.67M  ours                   -     64    64    104516               9.81      3.268    2.359   134.6
photo-alpha           0.67M  ours                   -     32    32     75804               7.86      4.439    3.350   111.4
photo-alpha           0.67M  ours                   -     16    16     53694               6.28      6.395    5.176    96.9
photo-alpha           0.67M  imagesharp-wu          -    256   256    199638              12.17      3.434    2.618    33.0
photo-alpha           0.67M  imagesharp-wu          -    128   128    156423              10.75      4.388    3.496    33.4
photo-alpha           0.67M  imagesharp-wu          -     64    64    126793               9.71      5.659    4.672    32.0
photo-alpha           0.67M  imagesharp-wu          -     32    32     99049               8.11      7.807    6.905    32.8
photo-alpha           0.67M  imagesharp-wu          -     16    16     59383               6.52      9.678    8.589    32.9
photo-alpha           0.67M  imagesharp-octree      -    256   252    188239               5.50     62.143   62.043    52.1
photo-alpha           0.67M  imagesharp-octree      -    128   123    172380               5.43     62.137   62.032    29.1
photo-alpha           0.67M  imagesharp-octree      -     64    58    117025               5.11     62.173   62.040    20.7
photo-alpha           0.67M  imagesharp-octree      -     32    25    106756               5.05     62.198   62.060    16.8
photo-alpha           0.67M  imagesharp-octree      -     16    15     52995               4.17     62.240   62.071    14.5
photo-alpha           0.67M  ours-no-kmeans         -    256   256    181954              13.48      1.927    1.323   123.7
photo-alpha           0.67M  ours-no-kmeans         -    128   128    144293              11.90      2.533    1.867   108.5
photo-alpha           0.67M  ours-no-kmeans         -     64    64    105252               9.82      3.370    2.542    91.8
photo-alpha           0.67M  ours-no-kmeans         -     32    32     77766               8.02      4.654    3.685    80.8
photo-alpha           0.67M  ours-no-kmeans         -     16    16     52283               5.98      6.662    5.571    74.4
photo-alpha           0.67M  ours-rgb-space         -    256   256    190860              13.11      2.219    1.367   222.7
photo-alpha           0.67M  ours-rgb-space         -    128   128    148164              11.39      2.839    1.875   173.6
photo-alpha           0.67M  ours-rgb-space         -     64    64    113916               9.54      3.769    2.657   138.8
photo-alpha           0.67M  ours-rgb-space         -     32    32     84954               8.00      5.190    3.902   110.1
photo-alpha           0.67M  ours-rgb-space         -     16    16     56865               6.49      6.889    5.699    87.6
photo-alpha           0.67M  ours-full-fs           -    256   256    182289              13.71      1.884    1.234   198.2
photo-alpha           0.67M  ours-full-fs           -    128   128    142713              11.82      2.441    1.668   163.5
photo-alpha           0.67M  ours-full-fs           -     64    64    104516               9.81      3.268    2.359   133.3
photo-alpha           0.67M  ours-full-fs           -     32    32     75804               7.86      4.439    3.350   112.2
photo-alpha           0.67M  ours-full-fs           -     16    16     53694               6.28      6.395    5.176    94.4
photo-alpha           0.67M  ours-no-edge           -    256   256    182289              13.71      1.884    1.234   198.0
photo-alpha           0.67M  ours-no-edge           -    128   128    142713              11.82      2.441    1.668   163.5
photo-alpha           0.67M  ours-no-edge           -     64    64    104516               9.81      3.268    2.359   129.8
photo-alpha           0.67M  ours-no-edge           -     32    32     75804               7.86      4.439    3.350   108.3
photo-alpha           0.67M  ours-no-edge           -     16    16     53694               6.28      6.395    5.176    95.1
photo-alpha           0.67M  pngquant-s4           fs    256   256    277098    272133    11.54      2.875    1.051   703.4
photo-alpha           0.67M  pngquant-s4           fs    128   128    227399    223982     9.49      3.726    1.350   638.7
photo-alpha           0.67M  pngquant-s4           fs     64    64    185828    183537     7.47      4.930    1.801   609.9
photo-alpha           0.67M  pngquant-s4           fs     32    32    153099    150344     5.80      6.726    2.594   576.8
photo-alpha           0.67M  pngquant-s4           fs     16    16    116321    114519     4.02      9.539    3.942   470.7
photo-alpha           0.67M  baseline-wu           fs    256   256    230764               4.62     36.081   35.617    40.2
photo-alpha           0.67M  baseline-wu           fs    128   128    194326               4.56     36.089   35.616    29.1
photo-alpha           0.67M  baseline-wu           fs     64    64    157171               4.46     36.103   35.608    23.0
photo-alpha           0.67M  baseline-wu           fs     32    32    124580               4.14     36.136   35.612    18.2
photo-alpha           0.67M  baseline-wu           fs     16    16     91561               3.61     36.234   35.635    16.2
photo-alpha           0.67M  ours                  fs    256   256    233035              12.60      2.002    1.059   238.8
photo-alpha           0.67M  ours                  fs    128   128    190607              10.56      2.604    1.392   202.9
photo-alpha           0.67M  ours                  fs     64    64    150722               8.41      3.520    1.882   170.4
photo-alpha           0.67M  ours                  fs     32    32    121650               6.34      4.821    2.578   146.1
photo-alpha           0.67M  ours                  fs     16    16     96302               4.60      6.976    4.058   128.2
photo-alpha           0.67M  imagesharp-wu         fs    256   256    324484               7.13      5.527    1.892   199.2
photo-alpha           0.67M  imagesharp-wu         fs    128   128    270748               5.73      6.955    1.962   143.6
photo-alpha           0.67M  imagesharp-wu         fs     64    64    226949               4.68      8.500    2.530   105.3
photo-alpha           0.67M  imagesharp-wu         fs     32    32    184886               3.84     10.615    4.472    87.2
photo-alpha           0.67M  imagesharp-wu         fs     16    16    145817               2.82     13.045    4.956    71.2
photo-alpha           0.67M  imagesharp-octree     fs    256   244    302266               2.19     63.047   49.214   163.6
photo-alpha           0.67M  imagesharp-octree     fs    128   123    261781               2.16     63.024   49.253   105.9
photo-alpha           0.67M  imagesharp-octree     fs     64    59    224155               2.10     63.063   49.237    73.2
photo-alpha           0.67M  imagesharp-octree     fs     32    26    182138               1.78     62.985   49.234    60.8
photo-alpha           0.67M  imagesharp-octree     fs     16    16    147819               1.70     63.065   49.230    48.6
photo-alpha           0.67M  ours-no-kmeans        fs    256   256    235402              12.35      2.043    1.152   173.5
photo-alpha           0.67M  ours-no-kmeans        fs    128   128    193514              10.49      2.705    1.575   147.9
photo-alpha           0.67M  ours-no-kmeans        fs     64    64    153006               8.25      3.638    2.030   133.2
photo-alpha           0.67M  ours-no-kmeans        fs     32    32    124114               6.11      5.059    2.872   116.0
photo-alpha           0.67M  ours-no-kmeans        fs     16    16     94581               4.25      7.250    4.462   107.6
photo-alpha           0.67M  ours-rgb-space        fs    256   256    242984              11.83      2.384    1.102   264.1
photo-alpha           0.67M  ours-rgb-space        fs    128   128    196280               9.90      3.067    1.452   216.3
photo-alpha           0.67M  ours-rgb-space        fs     64    64    159632               7.90      4.089    1.969   179.6
photo-alpha           0.67M  ours-rgb-space        fs     32    32    128428               6.12      5.690    2.776   146.2
photo-alpha           0.67M  ours-rgb-space        fs     16    16     98321               4.59      7.606    4.161   124.8
photo-alpha           0.67M  ours-full-fs          fs    256   256    283560               8.55      2.834    1.046   274.6
photo-alpha           0.67M  ours-full-fs          fs    128   128    231917               7.34      3.561    1.547   212.8
photo-alpha           0.67M  ours-full-fs          fs     64    64    189956               6.14      4.716    2.245   161.3
photo-alpha           0.67M  ours-full-fs          fs     32    32    157379               4.91      6.058    2.522   130.3
photo-alpha           0.67M  ours-full-fs          fs     16    16    126865               3.40      8.417    3.839   104.1
photo-alpha           0.67M  ours-no-edge          fs    256   256    262740              11.70      2.203    0.912   232.4
photo-alpha           0.67M  ours-no-edge          fs    128   128    215408               9.67      2.853    1.230   193.4
photo-alpha           0.67M  ours-no-edge          fs     64    64    176170               7.62      3.852    1.642   158.8
photo-alpha           0.67M  ours-no-edge          fs     32    32    143951               5.75      5.258    2.217   135.4
photo-alpha           0.67M  ours-no-edge          fs     16    16    115822               4.06      7.599    3.541   111.8
screen                0.66M  pngquant-s4            -    256   256     12291     10648    23.10      0.309    0.205    83.9
screen                0.66M  pngquant-s4            -    128   128      9733      8424    20.71      0.487    0.377    82.4
screen                0.66M  pngquant-s4            -     64    64      8961      7616    19.70      0.695    0.571    79.9
screen                0.66M  pngquant-s4            -     32    32      6860      5575    18.29      1.118    0.965    74.9
screen                0.66M  pngquant-s4            -     16    16      4829      4168    18.48      1.857    1.725    69.6
screen                0.66M  baseline-wu            -    256   256     10170              22.23      0.373    0.262    17.8
screen                0.66M  baseline-wu            -    128   128      8883              20.80      0.516    0.404    13.6
screen                0.66M  baseline-wu            -     64    64      7994              19.87      0.750    0.634    10.3
screen                0.66M  baseline-wu            -     32    32      6571              18.98      1.121    0.994     9.0
screen                0.66M  baseline-wu            -     16    16      5259              18.02      2.009    1.862     8.5
screen                0.66M  ours                   -    256   256     14627              24.78      0.276    0.177    42.5
screen                0.66M  ours                   -    128   128     11148              22.59      0.402    0.295    34.5
screen                0.66M  ours                   -     64    64      9097              20.88      0.609    0.488    28.1
screen                0.66M  ours                   -     32    32      7448              19.82      0.956    0.832    23.6
screen                0.66M  ours                   -     16    16      5054              19.47      1.558    1.437    21.8
screen                0.66M  imagesharp-wu          -    256   256      7314              22.40      0.395    0.288    32.2
screen                0.66M  imagesharp-wu          -    128   128      6299              21.07      0.535    0.424    31.8
screen                0.66M  imagesharp-wu          -     64    64      5764              20.06      0.762    0.646    30.4
screen                0.66M  imagesharp-wu          -     32    32      5417              19.55      1.163    1.038    31.2
screen                0.66M  imagesharp-wu          -     16    16      3576              19.14      2.098    1.958    31.4
screen                0.66M  imagesharp-octree      -    256   255      7234              22.07      0.433    0.317    17.4
screen                0.66M  imagesharp-octree      -    128   127      6168              20.80      0.632    0.520    14.3
screen                0.66M  imagesharp-octree      -     64    62      5751              19.39      1.617    1.536    11.9
screen                0.66M  imagesharp-octree      -     32    31      5110              17.73      2.213    2.088    10.8
screen                0.66M  imagesharp-octree      -     16    11      3352              18.28      2.773    2.647     9.5
screen                0.66M  ours-no-kmeans         -    256   256     15507              24.59      0.285    0.188    20.6
screen                0.66M  ours-no-kmeans         -    128   128     12069              22.37      0.421    0.318    19.2
screen                0.66M  ours-no-kmeans         -     64    64      9404              20.83      0.636    0.518    15.3
screen                0.66M  ours-no-kmeans         -     32    32      7610              19.90      1.009    0.890    14.1
screen                0.66M  ours-no-kmeans         -     16    16      5088              19.45      1.608    1.485    13.5
screen                0.66M  ours-rgb-space         -    256   256     11149              22.63      0.331    0.226    40.3
screen                0.66M  ours-rgb-space         -    128   128     10625              21.00      0.464    0.351    31.5
screen                0.66M  ours-rgb-space         -     64    64      8727              19.70      0.725    0.603    26.0
screen                0.66M  ours-rgb-space         -     32    32      6674              19.27      1.105    0.979    21.9
screen                0.66M  ours-rgb-space         -     16    16      4759              18.41      1.719    1.585    20.1
screen                0.66M  ours-full-fs           -    256   256     14627              24.78      0.276    0.177    43.0
screen                0.66M  ours-full-fs           -    128   128     11148              22.59      0.402    0.295    34.3
screen                0.66M  ours-full-fs           -     64    64      9097              20.88      0.609    0.488    28.3
screen                0.66M  ours-full-fs           -     32    32      7448              19.82      0.956    0.832    24.7
screen                0.66M  ours-full-fs           -     16    16      5054              19.47      1.558    1.437    21.8
screen                0.66M  ours-no-edge           -    256   256     14627              24.78      0.276    0.177    42.8
screen                0.66M  ours-no-edge           -    128   128     11148              22.59      0.402    0.295    33.7
screen                0.66M  ours-no-edge           -     64    64      9097              20.88      0.609    0.488    27.9
screen                0.66M  ours-no-edge           -     32    32      7448              19.82      0.956    0.832    23.4
screen                0.66M  ours-no-edge           -     16    16      5054              19.47      1.558    1.437    22.2
screen                0.66M  pngquant-s4           fs    256   256     33889     29391    20.76      0.390    0.079   108.4
screen                0.66M  pngquant-s4           fs    128   128     27758     25416    17.33      0.622    0.138   107.3
screen                0.66M  pngquant-s4           fs     64    64     24380     22491    15.46      0.896    0.239   107.2
screen                0.66M  pngquant-s4           fs     32    32     21266     19345    13.66      1.392    0.488   102.4
screen                0.66M  pngquant-s4           fs     16    16     16288     15154    12.61      2.272    1.018    95.4
screen                0.66M  baseline-wu           fs    256   256     39180              18.39      0.601    0.162    26.1
screen                0.66M  baseline-wu           fs    128   128     31535              16.72      0.750    0.189    20.2
screen                0.66M  baseline-wu           fs     64    64     27177              14.88      1.023    0.263    16.8
screen                0.66M  baseline-wu           fs     32    32     23428              13.32      1.543    0.436    15.6
screen                0.66M  baseline-wu           fs     16    16     26674               8.73      3.071    1.183    16.8
screen                0.66M  ours                  fs    256   256     29260              22.90      0.332    0.090    56.4
screen                0.66M  ours                  fs    128   128     24187              20.12      0.484    0.143    48.7
screen                0.66M  ours                  fs     64    64     21179              17.62      0.731    0.249    43.4
screen                0.66M  ours                  fs     32    32     17735              15.54      1.122    0.492    38.1
screen                0.66M  ours                  fs     16    16     14374              14.06      1.832    0.983    36.8
screen                0.66M  imagesharp-wu         fs    256   256     39709              18.79      0.554    0.135    45.2
screen                0.66M  imagesharp-wu         fs    128   128     32160              16.68      0.741    0.176    45.0
screen                0.66M  imagesharp-wu         fs     64    64     27196              15.00      0.998    0.264    44.6
screen                0.66M  imagesharp-wu         fs     32    32     24649              13.28      1.553    0.434    42.2
screen                0.66M  imagesharp-wu         fs     16    16     23883              10.86      2.808    1.135    54.7
screen                0.66M  imagesharp-octree     fs    256   255     40299              17.07      0.693    0.106    18.8
screen                0.66M  imagesharp-octree     fs    128   127     32960              15.67      0.857    0.204    16.9
screen                0.66M  imagesharp-octree     fs     64    62     43881               7.07      2.468    0.920    20.3
screen                0.66M  imagesharp-octree     fs     32    31     39401               5.98      4.008    1.178    20.9
screen                0.66M  imagesharp-octree     fs     16    11     31390               5.88      4.708    1.788    22.7
screen                0.66M  ours-no-kmeans        fs    256   256     29792              22.59      0.345    0.094    34.3
screen                0.66M  ours-no-kmeans        fs    128   128     25130              19.65      0.509    0.158    30.9
screen                0.66M  ours-no-kmeans        fs     64    64     21375              17.31      0.765    0.274    33.6
screen                0.66M  ours-no-kmeans        fs     32    32     17907              15.32      1.203    0.528    29.3
screen                0.66M  ours-no-kmeans        fs     16    16     14927              13.76      1.906    1.006    28.4
screen                0.66M  ours-rgb-space        fs    256   256     29991              20.51      0.400    0.113    53.0
screen                0.66M  ours-rgb-space        fs    128   128     24878              18.32      0.560    0.154    46.5
screen                0.66M  ours-rgb-space        fs     64    64     21510              15.96      0.877    0.297    40.7
screen                0.66M  ours-rgb-space        fs     32    32     17746              14.44      1.306    0.565    36.2
screen                0.66M  ours-rgb-space        fs     16    16     14291              12.98      2.017    0.979    35.4
screen                0.66M  ours-full-fs          fs    256   256     33953              22.27      0.366    0.082    56.5
screen                0.66M  ours-full-fs          fs    128   128     27456              19.56      0.530    0.145    46.3
screen                0.66M  ours-full-fs          fs     64    64     24376              16.99      0.810    0.258    40.7
screen                0.66M  ours-full-fs          fs     32    32     21520              14.57      1.324    0.502    35.8
screen                0.66M  ours-full-fs          fs     16    16     21224              12.09      2.343    0.977    33.3
screen                0.66M  ours-no-edge          fs    256   256     30521              22.76      0.338    0.085    54.5
screen                0.66M  ours-no-edge          fs    128   128     25386              19.97      0.492    0.135    45.4
screen                0.66M  ours-no-edge          fs     64    64     22218              17.47      0.743    0.236    38.4
screen                0.66M  ours-no-edge          fs     32    32     18447              15.33      1.147    0.480    34.9
screen                0.66M  ours-no-edge          fs     16    16     15600              13.67      1.891    0.949    32.7
screen-alpha          0.66M  pngquant-s4            -    256   256     13840     11891    22.89      0.309    0.206    84.3
screen-alpha          0.66M  pngquant-s4            -    128   128     10678      9111    20.70      0.480    0.372    80.5
screen-alpha          0.66M  pngquant-s4            -     64    64      8932      7299    19.52      0.757    0.607    77.6
screen-alpha          0.66M  pngquant-s4            -     32    32      7758      6447    18.11      1.245    1.063    75.0
screen-alpha          0.66M  pngquant-s4            -     16    16      4590      3888    17.25      2.426    2.193    67.6
screen-alpha          0.66M  baseline-wu            -    256   256     11105              15.56      3.780    3.359    14.3
screen-alpha          0.66M  baseline-wu            -    128   128      7840              15.21      3.798    3.375     9.8
screen-alpha          0.66M  baseline-wu            -     64    64      7242              15.01      3.825    3.400     7.3
screen-alpha          0.66M  baseline-wu            -     32    32      6790              14.85      3.940    3.516     6.2
screen-alpha          0.66M  baseline-wu            -     16    16      4580              14.55      4.173    3.747     5.6
screen-alpha          0.66M  ours                   -    256   256     14532              24.68      0.277    0.177    45.5
screen-alpha          0.66M  ours                   -    128   128     11386              22.42      0.406    0.304    35.9
screen-alpha          0.66M  ours                   -     64    64      9044              20.75      0.631    0.526    28.3
screen-alpha          0.66M  ours                   -     32    32      7162              19.55      1.103    0.916    27.2
screen-alpha          0.66M  ours                   -     16    16      4906              18.69      1.939    1.686    20.9
screen-alpha          0.66M  imagesharp-wu          -    256   256      7378              22.49      0.379    0.290    30.9
screen-alpha          0.66M  imagesharp-wu          -    128   128      6318              20.95      0.549    0.448    31.0
screen-alpha          0.66M  imagesharp-wu          -     64    64      5747              20.07      0.833    0.651    31.7
screen-alpha          0.66M  imagesharp-wu          -     32    32      5419              19.23      1.475    1.164    29.6
screen-alpha          0.66M  imagesharp-wu          -     16    16      3550              17.57      2.630    2.275    30.7
screen-alpha          0.66M  imagesharp-octree      -    256   255      7184               4.44     72.897   72.695    17.3
screen-alpha          0.66M  imagesharp-octree      -    128   127      6139               4.41     72.898   72.696    14.2
screen-alpha          0.66M  imagesharp-octree      -     64    62      5725               4.38     72.913   72.710    11.9
screen-alpha          0.66M  imagesharp-octree      -     32    31      5132               4.31     72.929   72.724    10.8
screen-alpha          0.66M  imagesharp-octree      -     16    11      3352               4.34     72.958   72.747     9.5
screen-alpha          0.66M  ours-no-kmeans         -    256   256     15544              24.28      0.289    0.192    21.6
screen-alpha          0.66M  ours-no-kmeans         -    128   128     11720              22.34      0.423    0.323    18.3
screen-alpha          0.66M  ours-no-kmeans         -     64    64      9366              20.61      0.668    0.564    16.7
screen-alpha          0.66M  ours-no-kmeans         -     32    32      7714              19.72      1.174    0.998    14.5
screen-alpha          0.66M  ours-no-kmeans         -     16    16      5086              18.64      2.094    1.850    13.1
screen-alpha          0.66M  ours-rgb-space         -    256   256     11488              22.70      0.323    0.221    38.9
screen-alpha          0.66M  ours-rgb-space         -    128   128      9313              20.66      0.501    0.396    31.4
screen-alpha          0.66M  ours-rgb-space         -     64    64      8162              19.52      0.767    0.593    26.8
screen-alpha          0.66M  ours-rgb-space         -     32    32      6959              18.61      1.384    1.079    23.1
screen-alpha          0.66M  ours-rgb-space         -     16    16      4600              17.54      2.276    1.886    16.3
screen-alpha          0.66M  ours-full-fs           -    256   256     14532              24.68      0.277    0.177    44.3
screen-alpha          0.66M  ours-full-fs           -    128   128     11386              22.42      0.406    0.304    34.3
screen-alpha          0.66M  ours-full-fs           -     64    64      9044              20.75      0.631    0.526    27.2
screen-alpha          0.66M  ours-full-fs           -     32    32      7162              19.55      1.103    0.916    24.7
screen-alpha          0.66M  ours-full-fs           -     16    16      4906              18.69      1.939    1.686    21.2
screen-alpha          0.66M  ours-no-edge           -    256   256     14532              24.68      0.277    0.177    45.2
screen-alpha          0.66M  ours-no-edge           -    128   128     11386              22.42      0.406    0.304    35.7
screen-alpha          0.66M  ours-no-edge           -     64    64      9044              20.75      0.631    0.526    28.0
screen-alpha          0.66M  ours-no-edge           -     32    32      7162              19.55      1.103    0.916    24.2
screen-alpha          0.66M  ours-no-edge           -     16    16      4906              18.69      1.939    1.686    21.1
screen-alpha          0.66M  pngquant-s4           fs    256   256     33697     29497    20.59      0.391    0.073   107.8
screen-alpha          0.66M  pngquant-s4           fs    128   128     27782     24981    17.45      0.615    0.129   105.1
screen-alpha          0.66M  pngquant-s4           fs     64    64     23498     21670    15.18      0.964    0.255   103.9
screen-alpha          0.66M  pngquant-s4           fs     32    32     21073     19088    13.42      1.562    0.490   102.4
screen-alpha          0.66M  pngquant-s4           fs     16    16     15898     14225    12.78      2.716    1.577    92.7
screen-alpha          0.66M  baseline-wu           fs    256   256     38148              14.40      3.807    3.353    19.7
screen-alpha          0.66M  baseline-wu           fs    128   128     31432              13.52      3.838    3.354    14.7
screen-alpha          0.66M  baseline-wu           fs     64    64     26268              12.94      3.888    3.365    11.7
screen-alpha          0.66M  baseline-wu           fs     32    32     23556              11.48      4.107    3.401    10.6
screen-alpha          0.66M  baseline-wu           fs     16    16     21769              10.34      4.601    3.507     9.6
screen-alpha          0.66M  ours                  fs    256   256     28868              22.76      0.333    0.089    53.6
screen-alpha          0.66M  ours                  fs    128   128     24358              19.92      0.491    0.145    43.2
screen-alpha          0.66M  ours                  fs     64    64     20182              17.25      0.760    0.263    37.3
screen-alpha          0.66M  ours                  fs     32    32     17745              15.15      1.293    0.526    34.6
screen-alpha          0.66M  ours                  fs     16    16     13802              13.28      2.242    1.188    30.3
screen-alpha          0.66M  imagesharp-wu         fs    256   256     38041              18.91      0.523    0.139    45.0
screen-alpha          0.66M  imagesharp-wu         fs    128   128     30925              16.53      0.739    0.171    43.2
screen-alpha          0.66M  imagesharp-wu         fs     64    64     26170              15.29      1.035    0.327    44.5
screen-alpha          0.66M  imagesharp-wu         fs     32    32     23996              12.79      1.912    0.665    44.0
screen-alpha          0.66M  imagesharp-wu         fs     16    16     21188              10.84      3.867    1.550    45.4
screen-alpha          0.66M  imagesharp-octree     fs    256   256     71509               3.23     56.534   39.657    34.4
screen-alpha          0.66M  imagesharp-octree     fs    128   128     64468               3.15     56.544   39.671    31.5
screen-alpha          0.66M  imagesharp-octree     fs     64    63     71863               1.86     56.590   39.630    30.6
screen-alpha          0.66M  imagesharp-octree     fs     32    32     58266               1.52     56.590   39.650    30.6
screen-alpha          0.66M  imagesharp-octree     fs     16    12     56314               1.48     56.854   39.925    29.5
screen-alpha          0.66M  ours-no-kmeans        fs    256   256     29900              22.15      0.351    0.093    30.1
screen-alpha          0.66M  ours-no-kmeans        fs    128   128     24762              19.56      0.515    0.151    27.3
screen-alpha          0.66M  ours-no-kmeans        fs     64    64     20783              16.70      0.815    0.267    26.9
screen-alpha          0.66M  ours-no-kmeans        fs     32    32     17954              15.15      1.379    0.608    24.6
screen-alpha          0.66M  ours-no-kmeans        fs     16    16     14896              13.05      2.366    1.411    22.4
screen-alpha          0.66M  ours-rgb-space        fs    256   256     28806              20.63      0.393    0.101    49.0
screen-alpha          0.66M  ours-rgb-space        fs    128   128     23037              17.65      0.608    0.185    40.6
screen-alpha          0.66M  ours-rgb-space        fs     64    64     21083              15.85      0.912    0.281    35.4
screen-alpha          0.66M  ours-rgb-space        fs     32    32     18423              14.04      1.611    0.637    32.3
screen-alpha          0.66M  ours-rgb-space        fs     16    16     14675              12.85      2.609    1.266    25.7
screen-alpha          0.66M  ours-full-fs          fs    256   256     33498              22.16      0.363    0.081    53.5
screen-alpha          0.66M  ours-full-fs          fs    128   128     27182              19.33      0.539    0.141    44.1
screen-alpha          0.66M  ours-full-fs          fs     64    64     23778              16.49      0.882    0.298    37.0
screen-alpha          0.66M  ours-full-fs          fs     32    32     21773              13.91      1.550    0.542    31.8
screen-alpha          0.66M  ours-full-fs          fs     16    16     20467              11.22      2.843    1.172    29.4
screen-alpha          0.66M  ours-no-edge          fs    256   256     30084              22.63      0.339    0.084    50.9
screen-alpha          0.66M  ours-no-edge          fs    128   128     25350              19.77      0.500    0.137    41.4
screen-alpha          0.66M  ours-no-edge          fs     64    64     21040              17.08      0.777    0.250    35.7
screen-alpha          0.66M  ours-no-edge          fs     32    32     18228              14.96      1.311    0.510    31.0
screen-alpha          0.66M  ours-no-edge          fs     16    16     15588              12.83      2.319    1.147    28.4
palette               0.66M  pngquant-s4            -    256    11      3533      2128   100.00      0.000    0.000    41.4
palette               0.66M  pngquant-s4            -    128    11      3533      2128   100.00      0.000    0.000    43.7
palette               0.66M  pngquant-s4            -     64    11      3533      2128   100.00      0.000    0.000    45.0
palette               0.66M  pngquant-s4            -     32    11      3533      2128   100.00      0.000    0.000    44.7
palette               0.66M  pngquant-s4            -     16    11      3533      2128   100.00      0.000    0.000    45.2
palette               0.66M  baseline-wu            -    256    11      3533             100.00      0.000    0.000     7.7
palette               0.66M  baseline-wu            -    128    11      3533             100.00      0.000    0.000     7.4
palette               0.66M  baseline-wu            -     64    11      3533             100.00      0.000    0.000     7.3
palette               0.66M  baseline-wu            -     32    11      3533             100.00      0.000    0.000     7.3
palette               0.66M  baseline-wu            -     16    11      3533             100.00      0.000    0.000     7.5
palette               0.66M  ours                   -    256    11      3533             100.00      0.000    0.000    23.7
palette               0.66M  ours                   -    128    11      3533             100.00      0.000    0.000    23.4
palette               0.66M  ours                   -     64    11      3533             100.00      0.000    0.000    23.7
palette               0.66M  ours                   -     32    11      3533             100.00      0.000    0.000    24.3
palette               0.66M  ours                   -     16    11      3533             100.00      0.000    0.000    23.5
palette               0.66M  imagesharp-wu          -    256    11      3533             100.00      0.000    0.000    30.8
palette               0.66M  imagesharp-wu          -    128    11      3533             100.00      0.000    0.000    31.8
palette               0.66M  imagesharp-wu          -     64    11      3533             100.00      0.000    0.000    31.3
palette               0.66M  imagesharp-wu          -     32    11      3533             100.00      0.000    0.000    29.1
palette               0.66M  imagesharp-wu          -     16    11      3533             100.00      0.000    0.000    31.1
palette               0.66M  imagesharp-octree      -    256    11      3533             100.00      0.000    0.000    20.9
palette               0.66M  imagesharp-octree      -    128    11      3533             100.00      0.000    0.000    19.0
palette               0.66M  imagesharp-octree      -     64    11      3533             100.00      0.000    0.000    16.9
palette               0.66M  imagesharp-octree      -     32    11      3533             100.00      0.000    0.000    14.9
palette               0.66M  imagesharp-octree      -     16    11      3533             100.00      0.000    0.000    13.1
palette               0.66M  ours-no-kmeans         -    256    11      3533             100.00      0.000    0.000    23.5
palette               0.66M  ours-no-kmeans         -    128    11      3533             100.00      0.000    0.000    23.2
palette               0.66M  ours-no-kmeans         -     64    11      3533             100.00      0.000    0.000    24.3
palette               0.66M  ours-no-kmeans         -     32    11      3533             100.00      0.000    0.000    23.2
palette               0.66M  ours-no-kmeans         -     16    11      3533             100.00      0.000    0.000    24.1
palette               0.66M  ours-rgb-space         -    256    11      3533             100.00      0.000    0.000    13.8
palette               0.66M  ours-rgb-space         -    128    11      3533             100.00      0.000    0.000    13.9
palette               0.66M  ours-rgb-space         -     64    11      3533             100.00      0.000    0.000    14.5
palette               0.66M  ours-rgb-space         -     32    11      3533             100.00      0.000    0.000    14.1
palette               0.66M  ours-rgb-space         -     16    11      3533             100.00      0.000    0.000    14.4
palette               0.66M  ours-full-fs           -    256    11      3533             100.00      0.000    0.000    23.2
palette               0.66M  ours-full-fs           -    128    11      3533             100.00      0.000    0.000    23.8
palette               0.66M  ours-full-fs           -     64    11      3533             100.00      0.000    0.000    23.4
palette               0.66M  ours-full-fs           -     32    11      3533             100.00      0.000    0.000    23.3
palette               0.66M  ours-full-fs           -     16    11      3533             100.00      0.000    0.000    23.8
palette               0.66M  ours-no-edge           -    256    11      3533             100.00      0.000    0.000    23.9
palette               0.66M  ours-no-edge           -    128    11      3533             100.00      0.000    0.000    23.5
palette               0.66M  ours-no-edge           -     64    11      3533             100.00      0.000    0.000    23.3
palette               0.66M  ours-no-edge           -     32    11      3533             100.00      0.000    0.000    23.7
palette               0.66M  ours-no-edge           -     16    11      3533             100.00      0.000    0.000    23.4
palette               0.66M  pngquant-s4           fs    256    11      3533      2128   100.00      0.000    0.000    60.8
palette               0.66M  pngquant-s4           fs    128    11      3533      2128   100.00      0.000    0.000    64.5
palette               0.66M  pngquant-s4           fs     64    11      3533      2128   100.00      0.000    0.000    63.4
palette               0.66M  pngquant-s4           fs     32    11      3533      2128   100.00      0.000    0.000    68.1
palette               0.66M  pngquant-s4           fs     16    11      3533      2128   100.00      0.000    0.000    64.0
palette               0.66M  baseline-wu           fs    256    11      3533             100.00      0.000    0.000    12.3
palette               0.66M  baseline-wu           fs    128    11      3533             100.00      0.000    0.000    12.9
palette               0.66M  baseline-wu           fs     64    11      3533             100.00      0.000    0.000    12.2
palette               0.66M  baseline-wu           fs     32    11      3533             100.00      0.000    0.000    12.4
palette               0.66M  baseline-wu           fs     16    11      3533             100.00      0.000    0.000    12.2
palette               0.66M  ours                  fs    256    11      3533             100.00      0.000    0.000    35.6
palette               0.66M  ours                  fs    128    11      3533             100.00      0.000    0.000    35.8
palette               0.66M  ours                  fs     64    11      3533             100.00      0.000    0.000    36.3
palette               0.66M  ours                  fs     32    11      3533             100.00      0.000    0.000    36.1
palette               0.66M  ours                  fs     16    11      3533             100.00      0.000    0.000    35.8
palette               0.66M  imagesharp-wu         fs    256    11      3533             100.00      0.000    0.000    34.0
palette               0.66M  imagesharp-wu         fs    128    11      3533             100.00      0.000    0.000    35.4
palette               0.66M  imagesharp-wu         fs     64    11      3533             100.00      0.000    0.000    36.3
palette               0.66M  imagesharp-wu         fs     32    11      3533             100.00      0.000    0.000    33.5
palette               0.66M  imagesharp-wu         fs     16    11      3533             100.00      0.000    0.000    35.4
palette               0.66M  imagesharp-octree     fs    256    11      3533             100.00      0.000    0.000     7.2
palette               0.66M  imagesharp-octree     fs    128    11      3533             100.00      0.000    0.000     7.3
palette               0.66M  imagesharp-octree     fs     64    11      3533             100.00      0.000    0.000     7.2
palette               0.66M  imagesharp-octree     fs     32    11      3533             100.00      0.000    0.000     7.1
palette               0.66M  imagesharp-octree     fs     16    11      3533             100.00      0.000    0.000     7.3
palette               0.66M  ours-no-kmeans        fs    256    11      3533             100.00      0.000    0.000    35.9
palette               0.66M  ours-no-kmeans        fs    128    11      3533             100.00      0.000    0.000    35.5
palette               0.66M  ours-no-kmeans        fs     64    11      3533             100.00      0.000    0.000    35.6
palette               0.66M  ours-no-kmeans        fs     32    11      3533             100.00      0.000    0.000    35.6
palette               0.66M  ours-no-kmeans        fs     16    11      3533             100.00      0.000    0.000    36.0
palette               0.66M  ours-rgb-space        fs    256    11      3533             100.00      0.000    0.000    25.6
palette               0.66M  ours-rgb-space        fs    128    11      3533             100.00      0.000    0.000    25.8
palette               0.66M  ours-rgb-space        fs     64    11      3533             100.00      0.000    0.000    25.6
palette               0.66M  ours-rgb-space        fs     32    11      3533             100.00      0.000    0.000    26.0
palette               0.66M  ours-rgb-space        fs     16    11      3533             100.00      0.000    0.000    26.9
palette               0.66M  ours-full-fs          fs    256    11      3533             100.00      0.000    0.000    31.9
palette               0.66M  ours-full-fs          fs    128    11      3533             100.00      0.000    0.000    31.8
palette               0.66M  ours-full-fs          fs     64    11      3533             100.00      0.000    0.000    31.9
palette               0.66M  ours-full-fs          fs     32    11      3533             100.00      0.000    0.000    31.8
palette               0.66M  ours-full-fs          fs     16    11      3533             100.00      0.000    0.000    31.7
palette               0.66M  ours-no-edge          fs    256    11      3533             100.00      0.000    0.000    32.1
palette               0.66M  ours-no-edge          fs    128    11      3533             100.00      0.000    0.000    31.7
palette               0.66M  ours-no-edge          fs     64    11      3533             100.00      0.000    0.000    32.7
palette               0.66M  ours-no-edge          fs     32    11      3533             100.00      0.000    0.000    31.9
palette               0.66M  ours-no-edge          fs     16    11      3533             100.00      0.000    0.000    32.1
sticker               0.67M  pngquant-s4            -    256   256    114052    108723    26.62      0.745    0.439   190.4
sticker               0.67M  pngquant-s4            -    128   128     90243     87048    23.85      0.962    0.545   175.4
sticker               0.67M  pngquant-s4            -     64    64     71113     68938    19.77      1.344    0.764   161.6
sticker               0.67M  pngquant-s4            -     32    32     51600     49501    16.55      2.088    1.210   140.9
sticker               0.67M  pngquant-s4            -     16    16     35366     34336    15.14      2.487    1.447   130.0
sticker               0.67M  baseline-wu            -    256   256    108315              18.80      5.180    4.474    21.0
sticker               0.67M  baseline-wu            -    128   128     88512              18.48      5.195    4.478    13.3
sticker               0.67M  baseline-wu            -     64    64     69369              18.04      5.220    4.486     9.5
sticker               0.67M  baseline-wu            -     32    32     52738              17.29      5.267    4.503     7.3
sticker               0.67M  baseline-wu            -     16    16     38688              16.01      5.378    4.552     6.9
sticker               0.67M  ours                   -    256   256    107346              27.87      0.527    0.262    81.4
sticker               0.67M  ours                   -    128   128     86255              25.50      0.710    0.368    65.6
sticker               0.67M  ours                   -     64    64     67236              22.75      0.966    0.516    52.9
sticker               0.67M  ours                   -     32    32     53131              20.19      1.370    0.780    45.8
sticker               0.67M  ours                   -     16    16     38035              17.59      1.988    1.232    41.1
sticker               0.67M  imagesharp-wu          -    256   256    103063              24.03      0.958    0.399    31.4
sticker               0.67M  imagesharp-wu          -    128   128     82596              22.04      1.259    0.512    32.0
sticker               0.67M  imagesharp-wu          -     64    64     63633              19.27      1.657    0.764    31.9
sticker               0.67M  imagesharp-wu          -     32    32     50465              17.74      2.471    1.232    31.7
sticker               0.67M  imagesharp-wu          -     16    16     38027              15.41      3.798    2.305    31.2
sticker               0.67M  imagesharp-octree      -    256   255     77437              15.14     13.820   13.121    17.1
sticker               0.67M  imagesharp-octree      -    128   125     60510              14.51     13.885   13.132    12.5
sticker               0.67M  imagesharp-octree      -     64    64     51054              13.94     13.920   13.144    10.5
sticker               0.67M  imagesharp-octree      -     32    31     38554              12.24     14.284   13.307     9.0
sticker               0.67M  imagesharp-octree      -     16    15     30745              12.00     14.415   13.411     8.3
sticker               0.67M  ours-no-kmeans         -    256   256    107865              27.61      0.554    0.280    42.1
sticker               0.67M  ours-no-kmeans         -    128   128     86176              25.09      0.749    0.394    38.8
sticker               0.67M  ours-no-kmeans         -     64    64     66750              22.18      1.022    0.541    32.8
sticker               0.67M  ours-no-kmeans         -     32    32     53835              19.77      1.451    0.870    30.9
sticker               0.67M  ours-no-kmeans         -     16    16     38884              17.51      2.071    1.314    28.0
sticker               0.67M  ours-rgb-space         -    256   256    105282              26.35      0.613    0.314    85.1
sticker               0.67M  ours-rgb-space         -    128   128     83994              24.00      0.819    0.413    66.9
sticker               0.67M  ours-rgb-space         -     64    64     65645              21.09      1.138    0.609    54.8
sticker               0.67M  ours-rgb-space         -     32    32     49530              18.53      1.585    0.867    45.0
sticker               0.67M  ours-rgb-space         -     16    16     35632              15.50      2.397    1.376    38.8
sticker               0.67M  ours-full-fs           -    256   256    107346              27.87      0.527    0.262    82.9
sticker               0.67M  ours-full-fs           -    128   128     86255              25.50      0.710    0.368    65.7
sticker               0.67M  ours-full-fs           -     64    64     67236              22.75      0.966    0.516    53.0
sticker               0.67M  ours-full-fs           -     32    32     53131              20.19      1.370    0.780    45.6
sticker               0.67M  ours-full-fs           -     16    16     38035              17.59      1.988    1.232    39.8
sticker               0.67M  ours-no-edge           -    256   256    107346              27.87      0.527    0.262    82.9
sticker               0.67M  ours-no-edge           -    128   128     86255              25.50      0.710    0.368    65.0
sticker               0.67M  ours-no-edge           -     64    64     67236              22.75      0.966    0.516    53.1
sticker               0.67M  ours-no-edge           -     32    32     53131              20.19      1.370    0.780    46.0
sticker               0.67M  ours-no-edge           -     16    16     38035              17.59      1.988    1.232    40.3
sticker               0.67M  pngquant-s4           fs    256   256    119272    114025    26.14      0.738    0.402   221.1
sticker               0.67M  pngquant-s4           fs    128   128     98877     95457    22.77      0.982    0.519   209.5
sticker               0.67M  pngquant-s4           fs     64    64     83407     81121    18.90      1.382    0.689   195.3
sticker               0.67M  pngquant-s4           fs     32    32     64114     62107    15.48      2.253    0.890   183.2
sticker               0.67M  pngquant-s4           fs     16    16     44181     43167    14.45      2.659    1.096   150.5
sticker               0.67M  baseline-wu           fs    256   256    119013              17.57      5.206    4.470    27.7
sticker               0.67M  baseline-wu           fs    128   128    101984              16.79      5.246    4.473    19.6
sticker               0.67M  baseline-wu           fs     64    64     81678              15.31      5.289    4.479    15.2
sticker               0.67M  baseline-wu           fs     32    32     66591              13.30      5.395    4.491    11.0
sticker               0.67M  baseline-wu           fs     16    16     48783              12.78      5.662    4.537    10.1
sticker               0.67M  ours                  fs    256   256    115103              26.74      0.540    0.245    92.3
sticker               0.67M  ours                  fs    128   128     93498              23.69      0.730    0.343    76.4
sticker               0.67M  ours                  fs     64    64     74723              21.30      1.006    0.477    62.9
sticker               0.67M  ours                  fs     32    32     58220              19.17      1.442    0.715    55.2
sticker               0.67M  ours                  fs     16    16     42320              16.90      2.075    1.140    51.1
sticker               0.67M  imagesharp-wu         fs    256   256    120421              19.77      1.104    0.298    54.6
sticker               0.67M  imagesharp-wu         fs    128   128    102756              17.95      1.476    0.381    52.2
sticker               0.67M  imagesharp-wu         fs     64    64     80928              15.54      1.918    0.550    48.7
sticker               0.67M  imagesharp-wu         fs     32    32     65264              13.60      2.689    0.713    48.4
sticker               0.67M  imagesharp-wu         fs     16    16     50190              12.24      3.836    1.346    48.8
sticker               0.67M  imagesharp-octree     fs    256   255    111171              14.60      7.892    1.890    51.2
sticker               0.67M  imagesharp-octree     fs    128   125     90640              14.08      7.955    1.898    35.4
sticker               0.67M  imagesharp-octree     fs     64    64     79241              13.48      8.041    1.842    30.7
sticker               0.67M  imagesharp-octree     fs     32    31     64030              11.59      8.324    1.888    26.6
sticker               0.67M  imagesharp-octree     fs     16    15     49522               9.75      8.580    2.370    23.2
sticker               0.67M  ours-no-kmeans        fs    256   256    115905              26.54      0.570    0.260    52.9
sticker               0.67M  ours-no-kmeans        fs    128   128     93707              23.38      0.772    0.366    47.9
sticker               0.67M  ours-no-kmeans        fs     64    64     74689              20.82      1.062    0.501    42.8
sticker               0.67M  ours-no-kmeans        fs     32    32     58815              18.87      1.518    0.804    40.7
sticker               0.67M  ours-no-kmeans        fs     16    16     43347              16.78      2.161    1.215    36.6
sticker               0.67M  ours-rgb-space        fs    256   256    114132              25.44      0.625    0.299    94.4
sticker               0.67M  ours-rgb-space        fs    128   128     92594              22.30      0.847    0.391    79.1
sticker               0.67M  ours-rgb-space        fs     64    64     74160              19.79      1.194    0.559    66.0
sticker               0.67M  ours-rgb-space        fs     32    32     55887              17.64      1.680    0.743    56.6
sticker               0.67M  ours-rgb-space        fs     16    16     41119              14.81      2.550    1.145    48.8
sticker               0.67M  ours-full-fs          fs    256   256    121914              22.39      0.849    0.246   101.8
sticker               0.67M  ours-full-fs          fs    128   128    100979              18.64      1.167    0.354    83.4
sticker               0.67M  ours-full-fs          fs     64    64     82451              18.19      1.456    0.508    66.5
sticker               0.67M  ours-full-fs          fs     32    32     64037              14.64      2.123    0.924    56.6
sticker               0.67M  ours-full-fs          fs     16    16     46941              13.72      2.954    1.325    50.4
sticker               0.67M  ours-no-edge          fs    256   256    118533              25.98      0.599    0.219    91.0
sticker               0.67M  ours-no-edge          fs    128   128     96895              22.89      0.805    0.309    75.1
sticker               0.67M  ours-no-edge          fs     64    64     78147              20.66      1.093    0.428    61.8
sticker               0.67M  ours-no-edge          fs     32    32     61059              18.44      1.555    0.663    55.2
sticker               0.67M  ours-no-edge          fs     16    16     45311              16.45      2.241    1.047    46.8

```
