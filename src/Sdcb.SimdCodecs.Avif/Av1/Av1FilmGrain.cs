namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// Parsed AV1 film grain parameters (§5.9.30).
/// </summary>
internal sealed class Av1FilmGrainParams
{
    internal bool ApplyGrain;
    internal ushort GrainSeed;
    internal bool UpdateGrain;
    internal int NumYPoints;
    internal readonly byte[] PointYValue = new byte[14];
    internal readonly byte[] PointYScaling = new byte[14];
    internal bool ChromaScalingFromLuma;
    internal int NumCbPoints;
    internal readonly byte[] PointCbValue = new byte[10];
    internal readonly byte[] PointCbScaling = new byte[10];
    internal int NumCrPoints;
    internal readonly byte[] PointCrValue = new byte[10];
    internal readonly byte[] PointCrScaling = new byte[10];
    internal int GrainScalingMinus8;
    internal int ArCoeffLag;
    internal readonly byte[] ArCoeffsYPlus128 = new byte[24];
    internal readonly byte[] ArCoeffsCbPlus128 = new byte[25];
    internal readonly byte[] ArCoeffsCrPlus128 = new byte[25];
    internal int ArCoeffShiftMinus6;
    internal int GrainScaleShift;
    internal int CbMult;
    internal int CbLumaMult;
    internal int CbOffset;
    internal int CrMult;
    internal int CrLumaMult;
    internal int CrOffset;
    internal bool OverlapFlag;
    internal bool ClipToRestrictedRange;

    internal Av1FilmGrainParams Clone()
    {
        var c = new Av1FilmGrainParams
        {
            ApplyGrain = ApplyGrain,
            GrainSeed = GrainSeed,
            UpdateGrain = UpdateGrain,
            NumYPoints = NumYPoints,
            ChromaScalingFromLuma = ChromaScalingFromLuma,
            NumCbPoints = NumCbPoints,
            NumCrPoints = NumCrPoints,
            GrainScalingMinus8 = GrainScalingMinus8,
            ArCoeffLag = ArCoeffLag,
            ArCoeffShiftMinus6 = ArCoeffShiftMinus6,
            GrainScaleShift = GrainScaleShift,
            CbMult = CbMult,
            CbLumaMult = CbLumaMult,
            CbOffset = CbOffset,
            CrMult = CrMult,
            CrLumaMult = CrLumaMult,
            CrOffset = CrOffset,
            OverlapFlag = OverlapFlag,
            ClipToRestrictedRange = ClipToRestrictedRange,
        };
        PointYValue.CopyTo(c.PointYValue, 0);
        PointYScaling.CopyTo(c.PointYScaling, 0);
        PointCbValue.CopyTo(c.PointCbValue, 0);
        PointCbScaling.CopyTo(c.PointCbScaling, 0);
        PointCrValue.CopyTo(c.PointCrValue, 0);
        PointCrScaling.CopyTo(c.PointCrScaling, 0);
        ArCoeffsYPlus128.CopyTo(c.ArCoeffsYPlus128, 0);
        ArCoeffsCbPlus128.CopyTo(c.ArCoeffsCbPlus128, 0);
        ArCoeffsCrPlus128.CopyTo(c.ArCoeffsCrPlus128, 0);
        return c;
    }
}

/// <summary>
/// AV1 deterministic film grain synthesis process (§7.18.3).
/// </summary>
internal static class Av1FilmGrain
{
    // Gaussian_Sequence[2048] (§9.3)
    private static readonly short[] GaussianSequence =
    [
           56,   568,  -180,   172,   124,   -84,   172,   -64,  -900,    24,   820,   224,  1248,   996,   272,    -8,
         -916,  -388,  -732,  -104,  -188,   800,   112,  -652,  -320,  -376,   140,  -252,   492,  -168,    44,  -788,
          588,  -584,   500,  -228,    12,   680,   272,  -476,   972,  -100,   652,   368,   432,  -196,  -720,  -192,
         1000,  -332,   652,  -136,  -552,  -604,    -4,   192,  -220,  -136,  1000,   -52,   372,   -96,  -624,   124,
          -24,   396,   540,   -12,  -104,   640,   464,   244,  -208,   -84,   368,  -528,  -740,   248,  -968,  -848,
          608,   376,   -60,  -292,   -40,  -156,   252,  -292,   248,   224,  -280,   400,  -244,   244,   -60,    76,
          -80,   212,   532,   340,   128,   -36,   824,  -352,   -60,  -264,   -96,  -612,   416,  -704,   220,  -204,
          640,  -160,  1220,  -408,   900,   336,    20,  -336,   -96,  -792,   304,    48,   -28, -1232, -1172,  -448,
          104,  -292,  -520,   244,    60,  -948,     0,  -708,   268,   108,   356,  -548,   488,  -344,  -136,   488,
         -196,  -224,   656,  -236, -1128,    60,     4,   140,   276,  -676,  -376,   168,  -108,   464,     8,   564,
           64,   240,   308,  -300,  -400,  -456,  -136,    56,   120,  -408,  -116,   436,   504,  -232,   328,   844,
         -164,   -84,   784,  -168,   232,  -224,   348,  -376,   128,   568,    96, -1244,  -288,   276,   848,   832,
         -360,   656,   464,  -384,  -332,  -356,   728,  -388,   160,  -192,   468,   296,   224,   140,  -776,  -100,
          280,     4,   196,    44,   -36,  -648,   932,    16,  1428,    28,   528,   808,   772,    20,   268,    88,
         -332,  -284,   124,  -384,  -448,   208,  -228, -1044,  -328,   660,   380,  -148,  -300,   588,   240,   540,
           28,   136,   -88,  -436,   256,   296, -1000,  1400,     0,   -48,  1056,  -136,   264,  -528, -1108,   632,
         -484,  -592,  -344,   796,   124,  -668,  -768,   388,  1296,  -232,  -188,  -200,  -288,    -4,   308,   100,
         -168,   256,  -500,   204,  -508,   648,  -136,   372,  -272,  -120, -1004,  -552,  -548,  -384,   548,  -296,
          428,  -108,    -8,  -912,  -324,  -224,   -88,  -112,  -220,  -100,   996,  -796,   548,   360,  -216,   180,
          428,  -200,  -212,   148,    96,   148,   284,   216,  -412,  -320,   120,  -300,  -384,  -604,  -572,  -332,
           -8,  -180,  -176,   696,   116,   -88,   628,    76,    44,  -516,   240,  -208,   -40,   100,  -592,   344,
         -308,  -452,  -228,    20,   916, -1752,  -136,  -340,  -804,   140,    40,   512,   340,   248,   184,  -492,
          896,  -156,   932,  -628,   328,  -688,  -448,  -616,  -752,  -100,   560, -1020,   180,  -800,   -64,    76,
          576,  1068,   396,   660,   552,  -108,   -28,   320,  -628,   312,   -92,   -92,  -472,   268,    16,   560,
          516,  -672,   -52,   492,  -100,   260,   384,   284,   292,   304,  -148,    88,  -152,  1012,  1064,  -228,
          164,  -376,  -684,   592,  -392,   156,   196,  -524,   -64,  -884,   160,  -176,   636,   648,   404,  -396,
         -436,   864,   424,  -728,   988,  -604,   904,  -592,   296,  -224,   536,  -176,  -920,   436,   -48,  1176,
         -884,   416,  -776,  -824,  -884,   524,  -548,  -564,   -68,  -164,   -96,   692,   364,  -692, -1012,   -68,
          260,  -480,   876, -1116,   452,  -332,  -352,   892, -1088,  1220,  -676,    12,  -292,   244,   496,   372,
          -32,   280,   200,   112,  -440,   -96,    24,  -644,  -184,    56,  -432,   224,  -980,   272,  -260,   144,
         -436,   420,   356,   364,  -528,    76,   172,  -744,  -368,   404,  -752,  -416,   684,  -688,    72,   540,
          416,    92,   444,   480,   -72, -1416,   164, -1172,   -68,    24,   424,   264,  1040,   128,  -912,  -524,
         -356,    64,   876,   -12,     4,   -88,   532,   272,  -524,   320,   276,  -508,   940,    24,  -400,  -120,
          756,    60,   236,  -412,   100,   376,  -484,   400,  -100,  -740,  -108,  -260,   328,  -268,   224,  -200,
         -416,   184,  -604,  -564,   -20,   296,    60,   892,  -888,    60,   164,    68,  -760,   216,  -296,   904,
         -336,   -28,   404,  -356,  -568,  -208, -1480,  -512,   296,   328,  -360,  -164, -1560,  -776,  1156,  -428,
          164,  -504,  -112,   120,  -216,  -148,  -264,   308,    32,    64,   -72,    72,   116,   176,   -64,  -272,
          460,  -536,  -784,  -280,   348,   108,  -752,  -132,   524,  -540,  -776,   116,  -296, -1196,  -288,  -560,
         1040,  -472,   116,  -848, -1116,   116,   636,   696,   284,  -176,  1016,   204,  -864,  -648,  -248,   356,
          972,  -584,  -204,   264,   880,   528,   -24,  -184,   116,   448,  -144,   828,   524,   212,  -212,    52,
           12,   200,   268,  -488,  -404,  -880,   824,  -672,   -40,   908,  -248,   500,   716,  -576,   492,  -576,
           16,   720,  -108,   384,   124,   344,   280,   576,  -500,   252,   104,  -308,   196,  -188,    -8,  1268,
          296,  1032, -1196,   436,   316,   372,  -432,  -200,  -660,   704,  -224,   596,  -132,   268,    32,  -452,
          884,   104, -1008,   424, -1348,  -280,     4, -1168,   368,   476,   696,   300,    -8,    24,   180,  -592,
         -196,   388,   304,   500,   724,  -160,   244,   -84,   272,  -256,  -420,   320,   208,  -144,  -156,   156,
          364,   452,    28,   540,   316,   220,  -644,  -248,   464,    72,   360,    32,  -388,   496,  -680,   -48,
          208,  -116,  -408,    60,  -604,  -392,   548,  -840,   784,  -460,   656,  -544,  -388,  -264,   908,  -800,
         -628,  -612,  -568,   572,  -220,   164,   288,   -16,  -308,   308,  -112,  -636,  -760,   280,  -668,   432,
          364,   240,  -196,   604,   340,   384,   196,   592,   -44,  -500,   432,  -580,  -132,   636,   -76,   392,
            4,  -412,   540,   508,   328,  -356,   -36,    16,  -220,   -64,  -248,   -60,    24,  -192,   368,  1040,
           92,   -24, -1044,   -32,    40,   104,   148,   192,  -136,  -520,    56,  -816,  -224,   732,   392,   356,
          212,   -80,  -424, -1008,  -324,   588, -1496,   576,   460,  -816,  -848,    56,  -580,   -92, -1372,  -112,
         -496,   200,   364,    52,  -140,    48,   -48,   -60,    84,    72,    40,   132,  -356,  -268,  -104,  -284,
         -404,   732,  -520,   164,  -304,  -540,   120,   328,   -76,  -460,   756,   388,   588,   236,  -436,   -72,
         -176,  -404,  -316,  -148,   716,  -604,   404,   -72,   -88,  -888,   -68,   944,    88,  -220,  -344,   960,
          472,   460,  -232,   704,   120,   832,  -228,   692,  -508,   132,  -476,   844,  -748,  -364,   -44,  1116,
        -1104, -1056,    76,   428,   552,  -692,    60,   356,    96,  -384,  -188,  -612,  -576,   736,   508,   892,
          352, -1132,   504,   -24,  -352,   324,   332,  -600,  -312,   292,   508,  -144,    -8,   484,    48,   284,
         -260,  -240,   256,  -100,  -292,  -204,   -44,   472,  -204,   908,  -188, -1000,  -256,    92,  1164,  -392,
          564,   356,   652,   -28,  -884,   256,   484,  -192,   760,  -176,   376,  -524,  -452,  -436,   860,  -736,
          212,   124,   504,  -476,   468,    76,  -472,   552,  -692,  -944,  -620,   740,  -240,   400,   132,    20,
          192,  -196,   264,  -668, -1012,   -60,   296,  -316,  -828,    76,  -156,   284,  -768,  -448,  -832,   148,
          248,   652,   616,  1236,   288,  -328,  -400,  -124,   588,   220,   520,  -696,  1032,   768,  -740,   -92,
         -272,   296,   448,  -464,   412,  -200,   392,   440,  -200,   264,  -152,  -260,   320,  1032,   216,   320,
           -8,   -64,   156, -1016,  1084,  1172,   536,   484,  -432,   132,   372,   -52,  -256,    84,   116,  -352,
           48,   116,   304,  -384,   412,   924,  -300,   528,   628,   180,   648,    44,  -980,  -220,  1320,    48,
          332,   748,   524,  -268,  -720,   540,  -276,   564,  -344,  -208,  -196,   436,   896,    88,  -392,   132,
           80,  -964,  -288,   568,    56,   -48,  -456,   888,     8,   552,  -156,  -292,   948,   288,   128,  -716,
         -292,  1192,  -152,   876,   352,  -600,  -260,  -812,  -468,   -28,  -120,   -32,   -44,  1284,   496,   192,
          464,   312,   -76,  -516,  -380,  -456, -1012,   -48,   308,  -156,    36,   492,  -156,  -808,   188,  1652,
           68,  -120,  -116,   316,   160,  -140,   352,   808,  -416,   592,   316,  -480,    56,   528,  -204,  -568,
          372,  -232,   752,  -344,   744,    -4,   324,  -416,  -600,   768,   268,  -248,   -88,  -132,  -420,  -432,
           80,  -288,   404,  -316, -1216,  -588,   520,  -108,    92,  -320,   368,  -480,  -216,   -92,  1688,  -300,
          180,  1020,  -176,   820,   -68,  -228,  -260,   436,  -904,    20,    40,  -508,   440,  -736,   312,   332,
          204,   760,  -372,   728,    96,   -20,  -632,  -520,  -560,   336,  1076,   -64,  -532,   776,   584,   192,
          396,  -728,  -520,   276,  -188,    80,   -52,  -612,  -252,   -48,   648,   212,  -688,   228,   -52,  -260,
          428,  -412,  -272,  -404,   180,   816,  -796,    48,   152,   484,   -88,  -216,   988,   696,   188,  -528,
          648,  -116,  -180,   316,   476,    12,  -564,    96,   476,  -252,  -364,  -376,  -392,   556,  -256,  -576,
          260,  -352,   120,   -16,  -136,  -260,  -492,    72,   556,   660,   580,   616,   772,   436,   424,   -32,
         -324, -1268,   416,  -324,   -80,   920,   160,   228,   724,    32,  -516,    64,   384,    68,  -128,   136,
          240,   248,  -204,   -68,   252,  -932,  -120,  -480,  -628,   -84,   192,   852,  -404,  -288,  -132,   204,
          100,   168,   -68,  -196,  -868,   460,  1080,   380,   -80,   244,     0,   484,  -888,    64,   184,   352,
          600,   460,   164,   604,  -196,   320,   -64,   588,  -184,   228,    12,   372,    48,  -848,  -344,   224,
          208,  -200,   484,   128,   -20,   272,  -468,  -840,   384,   256,  -720,  -520,  -464,  -580,   112,  -120,
          644,  -356,  -208,  -608,  -528,   704,   560,  -424,   392,   828,    40,    84,   200,  -152,     0,  -144,
          584,   280,  -120,    80,  -556,  -972,  -196,  -472,   724,    80,   168,   -32,    88,   160,  -688,     0,
          160,   356,   372,  -776,   740,  -128,   676,  -248,  -480,     4,  -364,    96,   544,   232, -1032,   956,
          236,   356,    20,   -40,   300,    24,  -676,  -596,   132,  1120,  -104,   532, -1096,   568,   648,   444,
          508,   380,   188,  -376,  -604,  1488,   424,    24,   756,  -220,  -192,   716,   120,   920,   688,   168,
           44,  -460,   568,   284,  1144,  1160,   600,   424,   888,   656,  -356,  -320,   220,   316,  -176,  -724,
         -188,  -816,  -628,  -348,  -228,  -380,  1012,  -452,  -660,   736,   928,   404,  -696,   -72,  -268,  -892,
          128,   184,  -344,  -780,   360,   336,   400,   344,   428,   548,  -112,   136,  -228,  -216,  -820,  -516,
          340,    92,  -136,   116,  -300,   376,  -244,   100,  -316,  -520,  -284,   -12,   824,   164,  -548,  -180,
         -128,   116,  -924,  -828,   268,  -368,  -580,   620,   192,   160,     0, -1676,  1068,   424,   -56,  -360,
          468,  -156,   720,   288,  -528,   556,  -364,   548,  -148,   504,   316,   152,  -648,  -620,  -684,   -24,
         -376,  -384,  -108,  -920, -1032,   768,   180,  -264,  -508, -1268,  -260,   -60,   300,  -240,   988,   724,
         -376,  -576,  -212,  -736,   556,   192,  1092,  -620,  -880,   376,   -56,    -4,  -216,   -32,   836,   268,
          396,  1332,   864,  -600,   100,    56,  -412,   -92,   356,   180,   884,  -468,  -436,   292,  -388,  -804,
         -704,  -840,   368,  -348,   140,  -724,  1536,   940,   372,   112,  -372,   436,  -480,  1136,   296,   -32,
         -228,   132,   -48,  -220,   868, -1016,   -60, -1044,  -464,   328,   916,   244,    12,  -736,  -296,   360,
          468,  -376,  -108,   -92,   788,   368,   -56,   544,   400,  -672,  -420,   728,    16,   320,    44,  -284,
         -380,  -796,   488,   132,   204,  -596,  -372,    88,  -152,  -908,  -636,  -572,  -624,  -116,  -692,  -200,
          -56,   276,   -88,   484,  -324,   948,   864,  1000,  -456,  -184,  -276,   292,  -296,   156,   676,   320,
          160,   908,   -84, -1236,  -288,  -116,   260,  -372,  -644,   732,  -756,   -96,    84,   344,  -520,   348,
         -688,   240,   -84,   216, -1044,  -136,  -676,  -396, -1500,   960,   -40,   176,   168,  1516,   420,  -504,
         -344,  -364,  -360,  1216,  -940,  -380,  -212,   252,  -660,  -708,   484,  -444,  -152,   928,  -120,  1112,
          476,  -260,   560,  -148,  -344,   108,  -196,   228,  -288,   504,   560,  -328,   -88,   288, -1008,   460,
         -228,   468,  -836,  -196,    76,   388,   232,   412, -1168,  -716,  -644,   756,  -172,  -356,  -504,   116,
          432,   528,    48,   476,  -168,  -608,   448,   160,  -532,  -272,    28,  -676,   -12,   828,   980,   456,
          520,   104,  -104,   256,  -344,    -4,   -28,  -368,   -52,  -524,  -572,  -556,  -200,   768,  1124,  -208,
         -512,   176,   232,   248,  -148,  -888,   604,  -600,  -304,   804,  -156,  -212,   488,  -192,  -804,  -256,
          368,  -360,  -916,  -328,   228,  -240,  -448,  -472,   856,  -556,  -364,   572,   -12,  -156,  -368,  -340,
          432,   252,  -752,  -152,   288,   268,  -580,  -848,  -592,   108,   -76,   244,   312,  -716,   592,   -80,
          436,   360,     4,  -248,   160,   516,   584,   732,    44,  -468,  -280,  -292,  -156,  -588,    28,   308,
          912,    24,   124,   156,   180,  -252,   944,  -924,  -772,  -520,  -428,  -624,   300,  -212, -1144,    32,
         -724,   800, -1128,  -212, -1288,  -848,   180,  -416,   440,   192,  -576,  -792,   -76, -1080,    80,  -532,
         -352,  -132,   380,  -820,   148,  1112,   128,   164,   456,   700,  -924,   144,  -668,  -384,   648,  -832,
          508,   552,   -52,  -100,  -656,   208,  -568,   748,   -88,   680,   232,   300,   192,  -408, -1012,  -152,
         -252,  -268,   272,  -876,  -664,  -648,  -332,  -136,    16,    12,  1152,   -28,   332,  -536,   320,  -672,
         -460,  -316,   532,  -260,   228,   -40,  1052,  -816,   180,    88,  -496,  -556,  -672,  -368,   428,    92,
          356,   404,  -408,   252,   196,  -176,  -556,   792,   268,    32,   372,    40,    96,  -332,   328,   120,
          372,  -900,   -40,   472,  -264,  -592,   952,   128,   656,   112,   664,  -232,   420,     4,  -344,  -464,
          556,   244,  -416,   -32,   252,     0,  -412,   188,  -696,   508,  -476,   324, -1096,   656,  -312,   560,
          264,  -136,   304,   160,   -64,  -580,   248,   336,  -720,   560,  -348,  -288,  -276,  -196,  -500,   852,
         -544,  -236, -1128,  -992,  -776,   116,    56,    52,   860,   884,   212,   -12,   168,  1020,   512,  -552,
          924,  -148,   716,   188,   164,  -340,  -520,  -184,   880,  -152,  -680,  -208, -1156,  -300,  -528,  -472,
          364,   100,  -744, -1056,   -32,   540,   280,   144,  -676,   -32,  -232,  -280,  -224,    96,   568,   -76,
          172,   148,   148,   104,    32,  -296,   -32,   788,   -80,    32,   -16,   280,   288,   944,   428,  -484,
    ];

    /// <summary>
    /// Applies film grain synthesis in-place onto <paramref name="pic"/> according to §7.18.3.
    /// </summary>
    internal static void Apply(Av1SequenceHeader seq, Av1FilmGrainParams fg, Av1Picture pic)
    {
        if (!fg.ApplyGrain)
            return;

        int bitDepth = seq.BitDepth;
        int w = pic.Width;
        int h = pic.Height;
        int subX = seq.SubsamplingX;
        int subY = seq.SubsamplingY;
        int numPlanes = seq.NumPlanes;

        int grainCenter = 128 << (bitDepth - 8);
        int grainMin = -grainCenter;
        int grainMax = (256 << (bitDepth - 8)) - 1 - grainCenter;

        // 1. Generate AR grain templates (§7.18.3.3)
        var lumaGrain = new int[73 * 82];
        int scaleShift = 12 - bitDepth + fg.GrainScaleShift;
        int rng = fg.GrainSeed;
        for (int y = 0; y < 73; y++)
        {
            for (int x = 0; x < 82; x++)
            {
                int g = fg.NumYPoints > 0 ? GaussianSequence[GetRandomNumber(ref rng, 11)] : 0;
                lumaGrain[y * 82 + x] = Av1Constants.Round2(g, scaleShift);
            }
        }

        int arShift = fg.ArCoeffShiftMinus6 + 6;
        int lag = fg.ArCoeffLag;
        for (int y = 3; y < 73; y++)
        {
            for (int x = 3; x < 82 - 3; x++)
            {
                int s = 0;
                int pos = 0;
                for (int dr = -lag; dr <= 0; dr++)
                {
                    for (int dc = -lag; dc <= lag; dc++)
                    {
                        if (dr == 0 && dc == 0)
                            break;
                        int c = fg.ArCoeffsYPlus128[pos] - 128;
                        s += lumaGrain[(y + dr) * 82 + (x + dc)] * c;
                        pos++;
                    }
                }
                lumaGrain[y * 82 + x] = Av1Constants.Clip3(grainMin, grainMax, lumaGrain[y * 82 + x] + Av1Constants.Round2(s, arShift));
            }
        }

        int chromaW = subX != 0 ? 44 : 82;
        int chromaH = subY != 0 ? 38 : 73;
        int[]? cbGrain = null;
        int[]? crGrain = null;
        if (!seq.Monochrome)
        {
            cbGrain = new int[chromaH * chromaW];
            crGrain = new int[chromaH * chromaW];

            rng = fg.GrainSeed ^ 0xB524;
            for (int y = 0; y < chromaH; y++)
            {
                for (int x = 0; x < chromaW; x++)
                {
                    int g = (fg.NumCbPoints > 0 || fg.ChromaScalingFromLuma)
                        ? GaussianSequence[GetRandomNumber(ref rng, 11)] : 0;
                    cbGrain[y * chromaW + x] = Av1Constants.Round2(g, scaleShift);
                }
            }

            rng = fg.GrainSeed ^ 0x49D8;
            for (int y = 0; y < chromaH; y++)
            {
                for (int x = 0; x < chromaW; x++)
                {
                    int g = (fg.NumCrPoints > 0 || fg.ChromaScalingFromLuma)
                        ? GaussianSequence[GetRandomNumber(ref rng, 11)] : 0;
                    crGrain[y * chromaW + x] = Av1Constants.Round2(g, scaleShift);
                }
            }

            for (int y = 3; y < chromaH; y++)
            {
                for (int x = 3; x < chromaW - 3; x++)
                {
                    int s0 = 0, s1 = 0;
                    int pos = 0;
                    for (int dr = -lag; dr <= 0; dr++)
                    {
                        for (int dc = -lag; dc <= lag; dc++)
                        {
                            int c0 = fg.ArCoeffsCbPlus128[pos] - 128;
                            int c1 = fg.ArCoeffsCrPlus128[pos] - 128;
                            if (dr == 0 && dc == 0)
                            {
                                if (fg.NumYPoints > 0)
                                {
                                    int luma = 0;
                                    int lumaX = ((x - 3) << subX) + 3;
                                    int lumaY = ((y - 3) << subY) + 3;
                                    for (int i = 0; i <= subY; i++)
                                        for (int j = 0; j <= subX; j++)
                                            luma += lumaGrain[(lumaY + i) * 82 + (lumaX + j)];
                                    luma = Av1Constants.Round2(luma, subX + subY);
                                    s0 += luma * c0;
                                    s1 += luma * c1;
                                }
                                break;
                            }
                            s0 += cbGrain[(y + dr) * chromaW + (x + dc)] * c0;
                            s1 += crGrain[(y + dr) * chromaW + (x + dc)] * c1;
                            pos++;
                        }
                    }
                    cbGrain[y * chromaW + x] = Av1Constants.Clip3(grainMin, grainMax, cbGrain[y * chromaW + x] + Av1Constants.Round2(s0, arShift));
                    crGrain[y * chromaW + x] = Av1Constants.Clip3(grainMin, grainMax, crGrain[y * chromaW + x] + Av1Constants.Round2(s1, arShift));
                }
            }
        }

        // 2. Scaling lookup initialization (§7.18.3.4)
        var scalingLut = new int[3, 256];
        for (int plane = 0; plane < numPlanes; plane++)
        {
            int numPoints;
            byte[] px, py;
            if (plane == 0 || fg.ChromaScalingFromLuma)
            {
                numPoints = fg.NumYPoints;
                px = fg.PointYValue;
                py = fg.PointYScaling;
            }
            else if (plane == 1)
            {
                numPoints = fg.NumCbPoints;
                px = fg.PointCbValue;
                py = fg.PointCbScaling;
            }
            else
            {
                numPoints = fg.NumCrPoints;
                px = fg.PointCrValue;
                py = fg.PointCrScaling;
            }

            if (numPoints == 0) continue;

            for (int x = 0; x < px[0]; x++)
                scalingLut[plane, x] = py[0];

            for (int i = 0; i < numPoints - 1; i++)
            {
                int deltaY = py[i + 1] - py[i];
                int deltaX = px[i + 1] - px[i];
                if (deltaX <= 0)
                    throw new InvalidDataException("AV1 film grain scaling points are not strictly increasing.");
                int delta = deltaY * ((65536 + (deltaX >> 1)) / deltaX);
                for (int x = 0; x < deltaX; x++)
                    scalingLut[plane, px[i] + x] = py[i] + ((x * delta + 32768) >> 16);
            }

            for (int x = px[numPoints - 1]; x < 256; x++)
                scalingLut[plane, x] = py[numPoints - 1];
        }

        // 3. Add noise synthesis process (§7.18.3.5) — streaming stripe-by-stripe (only 2 stripes live at once)
        int minValue, maxLuma, maxChroma;
        if (fg.ClipToRestrictedRange)
        {
            minValue = 16 << (bitDepth - 8);
            maxLuma = 235 << (bitDepth - 8);
            maxChroma = seq.MatrixCoefficients == 0 ? maxLuma : (240 << (bitDepth - 8));
        }
        else
        {
            minValue = 0;
            maxLuma = (256 << (bitDepth - 8)) - 1;
            maxChroma = maxLuma;
        }

        int scalingShift = fg.GrainScalingMinus8 + 8;
        int wChroma = (w + subX) >> subX;
        int hChroma = (h + subY) >> subY;
        int lumaStripeW = ((w + 31) & ~31) + 34;
        int chromaStripeW = ((wChroma + 31) & ~31) + 34;
        int cStripeH = 34 >> subY;

        var curStripeY = new int[34 * lumaStripeW];
        var prevStripeY = new int[34 * lumaStripeW];
        int[]? curStripeU = numPlanes > 1 ? new int[cStripeH * chromaStripeW] : null;
        int[]? prevStripeU = numPlanes > 1 ? new int[cStripeH * chromaStripeW] : null;
        int[]? curStripeV = numPlanes > 1 ? new int[cStripeH * chromaStripeW] : null;
        int[]? prevStripeV = numPlanes > 1 ? new int[cStripeH * chromaStripeW] : null;

        ushort[] outY = pic.Planes[0];
        int strideY = pic.PlaneStrides[0];
        ushort[]? outU = numPlanes > 1 ? pic.Planes[1] : null;
        ushort[]? outV = numPlanes > 1 ? pic.Planes[2] : null;
        int strideU = numPlanes > 1 ? pic.PlaneStrides[1] : 0;
        int strideV = numPlanes > 1 ? pic.PlaneStrides[2] : 0;

        int numStripes = (((h + 1) / 2) + 15) / 16;
        int halfW = (w + 1) / 2;

        for (int lumaNum = 0; lumaNum < numStripes; lumaNum++)
        {
            // Build horizontal stripe lumaNum
            rng = fg.GrainSeed;
            rng ^= ((lumaNum * 37 + 178) & 255) << 8;
            rng ^= (lumaNum * 173 + 105) & 255;

            for (int x = 0; x < halfW; x += 16)
            {
                int rand = GetRandomNumber(ref rng, 8);
                int offsetX = rand >> 4;
                int offsetY = rand & 15;

                // Plane 0 (luma)
                int pOffX = 9 + offsetX * 2;
                int pOffY = 9 + offsetY * 2;
                for (int i = 0; i < 34; i++)
                {
                    int dstRow = i * lumaStripeW + x * 2;
                    int srcRow = (pOffY + i) * 82 + pOffX;
                    for (int j = 0; j < 34; j++)
                    {
                        int g = lumaGrain[srcRow + j];
                        if (j < 2 && fg.OverlapFlag && x > 0)
                        {
                            int old = curStripeY[dstRow + j];
                            g = j == 0 ? (old * 27 + g * 17) : (old * 17 + g * 27);
                            g = Av1Constants.Clip3(grainMin, grainMax, Av1Constants.Round2(g, 5));
                        }
                        curStripeY[dstRow + j] = g;
                    }
                }

                // Planes 1 & 2 (chroma)
                if (numPlanes > 1)
                {
                    int cOffX = subX != 0 ? (6 + offsetX) : (9 + offsetX * 2);
                    int cOffY = subY != 0 ? (6 + offsetY) : (9 + offsetY * 2);
                    int cW = 34 >> subX;
                    int cDstX = subX != 0 ? x : (x * 2);
                    for (int i = 0; i < cStripeH; i++)
                    {
                        int dstRow = i * chromaStripeW + cDstX;
                        int srcRow = (cOffY + i) * chromaW + cOffX;
                        for (int j = 0; j < cW; j++)
                        {
                            int gU = cbGrain![srcRow + j];
                            int gV = crGrain![srcRow + j];
                            if (fg.OverlapFlag && x > 0)
                            {
                                if (subX == 0 && j < 2)
                                {
                                    int oldU = curStripeU![dstRow + j];
                                    int oldV = curStripeV![dstRow + j];
                                    gU = j == 0 ? (oldU * 27 + gU * 17) : (oldU * 17 + gU * 27);
                                    gV = j == 0 ? (oldV * 27 + gV * 17) : (oldV * 17 + gV * 27);
                                    gU = Av1Constants.Clip3(grainMin, grainMax, Av1Constants.Round2(gU, 5));
                                    gV = Av1Constants.Clip3(grainMin, grainMax, Av1Constants.Round2(gV, 5));
                                }
                                else if (subX != 0 && j == 0)
                                {
                                    gU = Av1Constants.Clip3(grainMin, grainMax, Av1Constants.Round2(curStripeU![dstRow] * 23 + gU * 22, 5));
                                    gV = Av1Constants.Clip3(grainMin, grainMax, Av1Constants.Round2(curStripeV![dstRow] * 23 + gV * 22, 5));
                                }
                            }
                            curStripeU![dstRow + j] = gU;
                            curStripeV![dstRow + j] = gV;
                        }
                    }
                }
            }

            // First blend chroma rows in this stripe (reads original unmodified OutY!)
            if (numPlanes > 1 && (fg.NumCbPoints > 0 || fg.NumCrPoints > 0 || fg.ChromaScalingFromLuma))
            {
                int cYStart = lumaNum << (5 - subY);
                int cYEnd = Math.Min(hChroma, cYStart + (32 >> subY));
                for (int y = cYStart; y < cYEnd; y++)
                {
                    int i = y - cYStart;
                    int lumaY = y << subY;
                    int yRowOff = lumaY * strideY;
                    int uRowOff = y * strideU;
                    int vRowOff = y * strideV;
                    int curRowOff = i * chromaStripeW;
                    int prevRowOff = (i + (32 >> subY)) * chromaStripeW;
                    bool vOverlap = fg.OverlapFlag && lumaNum > 0 && (subY == 0 ? i < 2 : i < 1);

                    for (int x = 0; x < wChroma; x++)
                    {
                        int lumaX = x << subX;
                        int averageLuma = subX != 0
                            ? Av1Constants.Round2(outY[yRowOff + lumaX] + outY[yRowOff + Math.Min(lumaX + 1, w - 1)], 1)
                            : outY[yRowOff + lumaX];

                        if (fg.NumCbPoints > 0 || fg.ChromaScalingFromLuma)
                        {
                            int orig = outU![uRowOff + x];
                            int merged;
                            if (fg.ChromaScalingFromLuma)
                            {
                                merged = averageLuma;
                            }
                            else
                            {
                                int combined = averageLuma * (fg.CbLumaMult - 128) + orig * (fg.CbMult - 128);
                                merged = Av1Constants.Clip1((combined >> 6) + ((fg.CbOffset - 256) << (bitDepth - 8)), bitDepth);
                            }
                            int noise = curStripeU![curRowOff + x];
                            if (vOverlap)
                            {
                                int old = prevStripeU![prevRowOff + x];
                                noise = subY == 0
                                    ? (i == 0 ? old * 27 + noise * 17 : old * 17 + noise * 27)
                                    : (old * 23 + noise * 22);
                                noise = Av1Constants.Clip3(grainMin, grainMax, Av1Constants.Round2(noise, 5));
                            }
                            noise = Av1Constants.Round2(ScaleLut(scalingLut, 1, merged, bitDepth) * noise, scalingShift);
                            outU[uRowOff + x] = (ushort)Av1Constants.Clip3(minValue, maxChroma, orig + noise);
                        }

                        if (fg.NumCrPoints > 0 || fg.ChromaScalingFromLuma)
                        {
                            int orig = outV![vRowOff + x];
                            int merged;
                            if (fg.ChromaScalingFromLuma)
                            {
                                merged = averageLuma;
                            }
                            else
                            {
                                int combined = averageLuma * (fg.CrLumaMult - 128) + orig * (fg.CrMult - 128);
                                merged = Av1Constants.Clip1((combined >> 6) + ((fg.CrOffset - 256) << (bitDepth - 8)), bitDepth);
                            }
                            int noise = curStripeV![curRowOff + x];
                            if (vOverlap)
                            {
                                int old = prevStripeV![prevRowOff + x];
                                noise = subY == 0
                                    ? (i == 0 ? old * 27 + noise * 17 : old * 17 + noise * 27)
                                    : (old * 23 + noise * 22);
                                noise = Av1Constants.Clip3(grainMin, grainMax, Av1Constants.Round2(noise, 5));
                            }
                            noise = Av1Constants.Round2(ScaleLut(scalingLut, 2, merged, bitDepth) * noise, scalingShift);
                            outV[vRowOff + x] = (ushort)Av1Constants.Clip3(minValue, maxChroma, orig + noise);
                        }
                    }
                }
            }

            // Now blend luma rows in this stripe
            if (fg.NumYPoints > 0)
            {
                int yStart = lumaNum << 5;
                int yEnd = Math.Min(h, yStart + 32);
                for (int y = yStart; y < yEnd; y++)
                {
                    int i = y - yStart;
                    int yRowOff = y * strideY;
                    int curRowOff = i * lumaStripeW;
                    int prevRowOff = (i + 32) * lumaStripeW;
                    bool vOverlap = fg.OverlapFlag && lumaNum > 0 && i < 2;

                    for (int x = 0; x < w; x++)
                    {
                        int orig = outY[yRowOff + x];
                        int noise = curStripeY[curRowOff + x];
                        if (vOverlap)
                        {
                            int old = prevStripeY[prevRowOff + x];
                            noise = i == 0 ? (old * 27 + noise * 17) : (old * 17 + noise * 27);
                            noise = Av1Constants.Clip3(grainMin, grainMax, Av1Constants.Round2(noise, 5));
                        }
                        noise = Av1Constants.Round2(ScaleLut(scalingLut, 0, orig, bitDepth) * noise, scalingShift);
                        outY[yRowOff + x] = (ushort)Av1Constants.Clip3(minValue, maxLuma, orig + noise);
                    }
                }
            }

            // Swap stripe buffers
            (prevStripeY, curStripeY) = (curStripeY, prevStripeY);
            if (numPlanes > 1)
            {
                (prevStripeU, curStripeU) = (curStripeU, prevStripeU);
                (prevStripeV, curStripeV) = (curStripeV, prevStripeV);
            }
        }
    }

    private static int GetRandomNumber(ref int randomRegister, int bits)
    {
        int r = randomRegister;
        int bit = ((r >> 0) ^ (r >> 1) ^ (r >> 3) ^ (r >> 12)) & 1;
        r = (r >> 1) | (bit << 15);
        int result = (r >> (16 - bits)) & ((1 << bits) - 1);
        randomRegister = r;
        return result;
    }

    private static int ScaleLut(int[,] scalingLut, int plane, int index, int bitDepth)
    {
        int shift = bitDepth - 8;
        int x = index >> shift;
        int rem = index - (x << shift);
        if (bitDepth == 8 || x == 255)
            return scalingLut[plane, x];
        int start = scalingLut[plane, x];
        int end = scalingLut[plane, x + 1];
        return start + Av1Constants.Round2((end - start) * rem, shift);
    }
}
