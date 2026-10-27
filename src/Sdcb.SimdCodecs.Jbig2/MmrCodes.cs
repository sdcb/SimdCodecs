using System;
using System.IO;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>
/// The Modified Huffman run-length code tables of ITU-T T.4 §4.1.2 (Tables 2, 3
/// and the shared extended makeup codes), plus the two-dimensional mode codes of
/// T.4 Table 4 that T.6 inherits.
/// <para>
/// The codes are written as literal bit strings rather than
/// <c>(length, value)</c> pairs so a reader can check them against the printed
/// tables column by column — the tables are the part of MMR most likely to hold
/// a silent transcription error, and the one place where a typo produces
/// plausible-looking output rather than a crash. They are expanded once, on
/// first use, into flat peek-indexed lookup tables.
/// </para>
/// </summary>
internal static class MmrCodes
{
    /// <summary>Longest white code, hence the peek width for the white lookup table.</summary>
    public const int WhiteBits = 12;

    /// <summary>Longest black code, hence the peek width for the black lookup table.</summary>
    public const int BlackBits = 13;

    /// <summary>Longest two-dimensional mode code that is not the all-zero EOL prefix.</summary>
    public const int ModeBits = 7;

    /// <summary>
    /// A decoded run-length lookup: how many bits the code occupies and the run
    /// it stands for. <see cref="Length"/> is 0 for a bit pattern that no code
    /// starts with.
    /// </summary>
    public readonly record struct Run(int Length, int Value);

    /// <summary>Two-dimensional coding modes, T.4 Table 4 / T.6 §2.2.</summary>
    public enum Mode
    {
        /// <summary>No code matched — the bits are an EOL prefix, an extension, or corruption.</summary>
        None = 0,

        /// <summary>Pass mode: the run on the reference line passes the current position entirely.</summary>
        Pass,

        /// <summary>Horizontal mode: two run lengths follow, coded with the Modified Huffman tables.</summary>
        Horizontal,

        /// <summary>Vertical mode: the changing element sits within three pixels of b1.</summary>
        Vertical,

        /// <summary>2D extension (<c>0000001</c>), which includes T.6's uncompressed mode.</summary>
        Extension,
    }

    /// <summary>A decoded mode code: the mode, its bit length, and the vertical offset for <see cref="Mode.Vertical"/>.</summary>
    public readonly record struct ModeCode(Mode Mode, int Length, int Delta);

    // ---- T.4 Table 2 and Table 3: white runs -------------------------------------

    private static readonly (string Bits, int Run)[] WhiteCodes =
    [
        // Terminating codes, runs 0-63.
        ("00110101", 0),   ("000111", 1),     ("0111", 2),       ("1000", 3),
        ("1011", 4),       ("1100", 5),       ("1110", 6),       ("1111", 7),
        ("10011", 8),      ("10100", 9),      ("00111", 10),     ("01000", 11),
        ("001000", 12),    ("000011", 13),    ("110100", 14),    ("110101", 15),
        ("101010", 16),    ("101011", 17),    ("0100111", 18),   ("0001100", 19),
        ("0001000", 20),   ("0010111", 21),   ("0000011", 22),   ("0000100", 23),
        ("0101000", 24),   ("0101011", 25),   ("0010011", 26),   ("0100100", 27),
        ("0011000", 28),   ("00000010", 29),  ("00000011", 30),  ("00011010", 31),
        ("00011011", 32),  ("00010010", 33),  ("00010011", 34),  ("00010100", 35),
        ("00010101", 36),  ("00010110", 37),  ("00010111", 38),  ("00101000", 39),
        ("00101001", 40),  ("00101010", 41),  ("00101011", 42),  ("00101100", 43),
        ("00101101", 44),  ("00000100", 45),  ("00000101", 46),  ("00001010", 47),
        ("00001011", 48),  ("01010010", 49),  ("01010011", 50),  ("01010100", 51),
        ("01010101", 52),  ("00100100", 53),  ("00100101", 54),  ("01011000", 55),
        ("01011001", 56),  ("01011010", 57),  ("01011011", 58),  ("01001010", 59),
        ("01001011", 60),  ("00110010", 61),  ("00110011", 62),  ("00110100", 63),

        // Makeup codes, runs 64-1728 in steps of 64.
        ("11011", 64),        ("10010", 128),       ("010111", 192),      ("0110111", 256),
        ("00110110", 320),    ("00110111", 384),    ("01100100", 448),    ("01100101", 512),
        ("01101000", 576),    ("01100111", 640),    ("011001100", 704),   ("011001101", 768),
        ("011010010", 832),   ("011010011", 896),   ("011010100", 960),   ("011010101", 1024),
        ("011010110", 1088),  ("011010111", 1152),  ("011011000", 1216),  ("011011001", 1280),
        ("011011010", 1344),  ("011011011", 1408),  ("010011000", 1472),  ("010011001", 1536),
        ("010011010", 1600),  ("011000", 1664),     ("010011011", 1728),
    ];

    // ---- T.4 Table 2 and Table 3: black runs -------------------------------------

    private static readonly (string Bits, int Run)[] BlackCodes =
    [
        // Terminating codes, runs 0-63.
        ("0000110111", 0),   ("010", 1),          ("11", 2),           ("10", 3),
        ("011", 4),          ("0011", 5),         ("0010", 6),         ("00011", 7),
        ("000101", 8),       ("000100", 9),       ("0000100", 10),     ("0000101", 11),
        ("0000111", 12),     ("00000100", 13),    ("00000111", 14),    ("000011000", 15),
        ("0000010111", 16),  ("0000011000", 17),  ("0000001000", 18),  ("00001100111", 19),
        ("00001101000", 20), ("00001101100", 21), ("00000110111", 22), ("00000101000", 23),
        ("00000010111", 24), ("00000011000", 25), ("000011001010", 26), ("000011001011", 27),
        ("000011001100", 28), ("000011001101", 29), ("000001101000", 30), ("000001101001", 31),
        ("000001101010", 32), ("000001101011", 33), ("000011010010", 34), ("000011010011", 35),
        ("000011010100", 36), ("000011010101", 37), ("000011010110", 38), ("000011010111", 39),
        ("000001101100", 40), ("000001101101", 41), ("000011011010", 42), ("000011011011", 43),
        ("000001010100", 44), ("000001010101", 45), ("000001010110", 46), ("000001010111", 47),
        ("000001100100", 48), ("000001100101", 49), ("000001010010", 50), ("000001010011", 51),
        ("000000100100", 52), ("000000110111", 53), ("000000111000", 54), ("000000100111", 55),
        ("000000101000", 56), ("000001011000", 57), ("000001011001", 58), ("000000101011", 59),
        ("000000101100", 60), ("000001011010", 61), ("000001100110", 62), ("000001100111", 63),

        // Makeup codes, runs 64-1728 in steps of 64.
        ("0000001111", 64),     ("000011001000", 128),  ("000011001001", 192),
        ("000001011011", 256),  ("000000110011", 320),  ("000000110100", 384),
        ("000000110101", 448),  ("0000001101100", 512), ("0000001101101", 576),
        ("0000001001010", 640), ("0000001001011", 704), ("0000001001100", 768),
        ("0000001001101", 832), ("0000001110010", 896), ("0000001110011", 960),
        ("0000001110100", 1024), ("0000001110101", 1088), ("0000001110110", 1152),
        ("0000001110111", 1216), ("0000001010010", 1280), ("0000001010011", 1344),
        ("0000001010100", 1408), ("0000001010101", 1472), ("0000001011010", 1536),
        ("0000001011011", 1600), ("0000001100100", 1664), ("0000001100101", 1728),
    ];

    /// <summary>
    /// The extended makeup codes of T.4 Table 3, runs 1792-2560. Unlike every
    /// other code here these are <b>shared</b> by both colours — the same bit
    /// pattern means the same run whether the current colour is white or black.
    /// </summary>
    private static readonly (string Bits, int Run)[] ExtendedMakeupCodes =
    [
        ("00000001000", 1792),  ("00000001100", 1856),  ("00000001101", 1920),
        ("000000010010", 1984), ("000000010011", 2048), ("000000010100", 2112),
        ("000000010101", 2176), ("000000010110", 2240), ("000000010111", 2304),
        ("000000011100", 2368), ("000000011101", 2432), ("000000011110", 2496),
        ("000000011111", 2560),
    ];

    /// <summary>The longest run a single code can express; anything longer is a chain of makeup codes.</summary>
    public const int MaxRun = 2560;

    /// <summary>A run of 64 or more is a makeup code and must be followed by a terminating code.</summary>
    public const int MakeupThreshold = 64;

    private static readonly Run[] WhiteLookup = BuildRunLookup(WhiteCodes, WhiteBits);
    private static readonly Run[] BlackLookup = BuildRunLookup(BlackCodes, BlackBits);
    private static readonly ModeCode[] ModeLookup = BuildModeLookup();

    /// <summary>
    /// Decodes one run-length code from the top <see cref="WhiteBits"/> /
    /// <see cref="BlackBits"/> bits of <paramref name="peek"/>, which must be
    /// left-aligned in that width. <see cref="Run.Length"/> is 0 when no code
    /// matches.
    /// </summary>
    public static Run LookupRun(int colour, int peek) =>
        colour == 0 ? WhiteLookup[peek] : BlackLookup[peek];

    /// <summary>Decodes one two-dimensional mode code from <see cref="ModeBits"/> peeked bits.</summary>
    public static ModeCode LookupMode(int peek) => ModeLookup[peek];

    /// <summary>The raw table entries, for the tests that check them against T.4 directly.</summary>
    internal static (string Bits, int Run)[] TableFor(int colour) => colour == 0 ? WhiteCodes : BlackCodes;

    /// <summary>The shared extended makeup entries, for the same reason.</summary>
    internal static (string Bits, int Run)[] ExtendedMakeupTable => ExtendedMakeupCodes;

    /// <summary>
    /// Expands a code list into a flat table indexed by the next
    /// <paramref name="bits"/> bits of the stream. Every index whose leading bits
    /// are a code gets that code's entry, so decoding is one peek and one array
    /// read instead of a bit-at-a-time walk.
    /// </summary>
    private static Run[] BuildRunLookup((string Bits, int Run)[] codes, int bits)
    {
        var table = new Run[1 << bits];

        // The extended makeup codes belong to both tables, so they are appended
        // to whichever colour is being built.
        foreach (var (pattern, run) in (ReadOnlySpan<(string Bits, int Run)>)[.. codes, .. ExtendedMakeupCodes])
        {
            var length = pattern.Length;
            if (length > bits)
                throw new InvalidOperationException($"MMR code '{pattern}' is longer than the {bits}-bit lookup.");

            var prefix = Convert.ToInt32(pattern, 2) << (bits - length);
            var span = 1 << (bits - length);
            for (var i = 0; i < span; i++)
            {
                if (table[prefix + i].Length != 0)
                    throw new InvalidOperationException($"MMR code '{pattern}' is not prefix-free.");

                table[prefix + i] = new Run(length, run);
            }
        }

        return table;
    }

    /// <summary>
    /// T.4 Table 4, the two-dimensional mode codes. <c>0000000</c> is deliberately
    /// left as <see cref="Mode.None"/>: it is the prefix of EOL / EOFB and of the
    /// fill bits before them, none of which is a mode.
    /// </summary>
    private static ModeCode[] BuildModeLookup()
    {
        (string Bits, Mode Mode, int Delta)[] modes =
        [
            ("1", Mode.Vertical, 0),
            ("011", Mode.Vertical, 1),
            ("010", Mode.Vertical, -1),
            ("000011", Mode.Vertical, 2),
            ("000010", Mode.Vertical, -2),
            ("0000011", Mode.Vertical, 3),
            ("0000010", Mode.Vertical, -3),
            ("001", Mode.Horizontal, 0),
            ("0001", Mode.Pass, 0),
            ("0000001", Mode.Extension, 0),
        ];

        var table = new ModeCode[1 << ModeBits];
        foreach (var (pattern, mode, delta) in modes)
        {
            var prefix = Convert.ToInt32(pattern, 2) << (ModeBits - pattern.Length);
            var span = 1 << (ModeBits - pattern.Length);
            for (var i = 0; i < span; i++)
            {
                if (table[prefix + i].Mode != Mode.None)
                    throw new InvalidOperationException($"MMR mode code '{pattern}' is not prefix-free.");

                table[prefix + i] = new ModeCode(mode, pattern.Length, delta);
            }
        }

        return table;
    }
}
