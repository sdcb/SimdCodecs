using System.Runtime.CompilerServices;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// Bit costs for the VP8 encoder's decisions, in 1/256 bit: a bool coded 0 at probability p/256 costs
/// −log2(p/256), coded 1 costs −log2(1 − p/256). Coefficient tokens (RFC 6386 Section 13.2) are
/// classed as ZERO, ONE, TWO, THREE, FOUR and DCT_CAT1..6; a class's cost depends on the plane type,
/// band and context through the node probabilities, its extra bits and sign only on the value.
/// </summary>
internal static class Vp8EncoderCosts
{
    public const int Types = 4, Bands = 8, Contexts = 3, Nodes = 11;
    public const int ProbabilityCount = Types * Bands * Contexts * Nodes;
    public const int TokenClasses = 11;
    public const int MaxLevel = 2048;

    /// <summary>Cost of a 0 at probability p (index p, 0..255).</summary>
    public static readonly ushort[] Zero = new ushort[256];

    /// <summary>Cost of a 1 at probability p.</summary>
    public static readonly ushort[] One = new ushort[256];

    /// <summary>The extra bits and the sign of a level 1..2048 (index = level), independent of context.</summary>
    public static readonly ushort[] ExtraAndSign = new ushort[MaxLevel + 1];

    /// <summary>The token class of a level 0..2048.</summary>
    public static readonly byte[] ClassOf = new byte[MaxLevel + 1];

    // First level of each class, and the extra-bit probabilities of DCT_CAT1..6 (RFC 6386 Section 13.2).
    public static ReadOnlySpan<short> ClassBase => [0, 1, 2, 3, 4, 5, 7, 11, 19, 35, 67];

    public static readonly byte[][] CategoryProbabilities =
        [Vp8Tables.Pcat1, Vp8Tables.Pcat2, Vp8Tables.Pcat3, Vp8Tables.Pcat4, Vp8Tables.Pcat5, Vp8Tables.Pcat6];

    static Vp8EncoderCosts()
    {
        for (int p = 1; p < 256; p++)
        {
            Zero[p] = (ushort)Math.Round(-Math.Log2(p / 256.0) * 256);
            One[p] = (ushort)Math.Round(-Math.Log2(1 - p / 256.0) * 256);
        }

        Zero[0] = One[0] = 256 * 12; // p = 0 is never coded; keep it expensive.
        for (int level = 0; level <= MaxLevel; level++)
        {
            int cls = level <= 4 ? level : level <= 6 ? 5 : level <= 10 ? 6 : level <= 18 ? 7 : level <= 34 ? 8 : level <= 66 ? 9 : 10;
            ClassOf[level] = (byte)cls;
            if (level == 0)
            {
                continue;
            }

            int cost = 256; // sign
            if (cls >= 5)
            {
                byte[] probabilities = CategoryProbabilities[cls - 5];
                int extra = level - ClassBase[cls];
                int bits = probabilities.Length - 1;
                for (int i = 0; i < bits; i++)
                {
                    int bit = (extra >> (bits - 1 - i)) & 1;
                    cost += bit != 0 ? One[probabilities[i]] : Zero[probabilities[i]];
                }
            }

            ExtraAndSign[level] = (ushort)cost;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Bit(int bit, int probability) => bit != 0 ? One[probability] : Zero[probability];

    /// <summary>
    /// The path from <paramref name="start"/> to the leaf <paramref name="value"/> of a tree
    /// (Section 8.1: <c>tree[node + bit]</c> is a child index, or −leaf when ≤ 0), as (node &lt;&lt; 1 | bit)
    /// entries. Returns the depth.
    /// </summary>
    public static int TreePath(ReadOnlySpan<sbyte> tree, int value, int start, Span<int> path, out bool found)
    {
        found = Search(tree, value, start, path, 0, out int depth);
        if (!found)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Not a leaf of the tree.");
        }

        return depth;
    }

    private static bool Search(ReadOnlySpan<sbyte> tree, int value, int node, Span<int> path, int depth, out int end)
    {
        for (int bit = 0; bit < 2; bit++)
        {
            path[depth] = node << 1 | bit;
            int child = tree[node + bit];
            if (child <= 0)
            {
                if (-child == value)
                {
                    end = depth + 1;
                    return true;
                }
            }
            else if (Search(tree, value, child, path, depth + 1, out end))
            {
                return true;
            }
        }

        end = 0;
        return false;
    }

    /// <summary>The cost of coding leaf <paramref name="value"/> of a tree with these node probabilities.</summary>
    public static int TreeCost(ReadOnlySpan<sbyte> tree, ReadOnlySpan<byte> probabilities, int value)
    {
        Span<int> path = stackalloc int[16];
        int depth = TreePath(tree, value, 0, path, out _);
        int cost = 0;
        for (int i = 0; i < depth; i++)
        {
            int node = path[i] >> 1;
            cost += Bit(path[i] & 1, probabilities[node >> 1]);
        }

        return cost;
    }
}

/// <summary>
/// Per-frame token costs under one set of coefficient probabilities: for every (type, band, context)
/// the cost of EOB and of each token class, the latter both after a non-zero token (the EOB branch
/// is coded) and after ZERO (it is not). Extra bits and sign come from
/// <see cref="Vp8EncoderCosts.ExtraAndSign"/>.
/// </summary>
internal sealed class Vp8TokenCosts
{
    // [type][band][context] rows of 11 class costs + EOB at index 11; NoEob: class costs without the EOB branch.
    public readonly int[] WithEob = new int[Vp8EncoderCosts.Types * Vp8EncoderCosts.Bands * Vp8EncoderCosts.Contexts * 12];
    public readonly int[] NoEob = new int[Vp8EncoderCosts.Types * Vp8EncoderCosts.Bands * Vp8EncoderCosts.Contexts * 12];

    public Vp8TokenCosts(ReadOnlySpan<byte> probabilities)
    {
        Update(probabilities);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Row(int type, int band, int context) => ((type * 8 + band) * 3 + context) * 12;

    public void Update(ReadOnlySpan<byte> probabilities)
    {
        for (int row = 0; row < 4 * 8 * 3; row++)
        {
            ReadOnlySpan<byte> p = probabilities.Slice(row * 11, 11);
            int notEob = Vp8EncoderCosts.One[p[0]];
            for (int cls = 0; cls < 11; cls++)
            {
                int c = ClassCost(p, cls);
                NoEob[row * 12 + cls] = c;
                WithEob[row * 12 + cls] = c + notEob;
            }

            WithEob[row * 12 + 11] = Vp8EncoderCosts.Zero[p[0]];
            NoEob[row * 12 + 11] = 0;
        }
    }

    /// <summary>The cost of class <paramref name="cls"/> from node p1 on (the EOB node excluded).</summary>
    private static int ClassCost(ReadOnlySpan<byte> p, int cls)
    {
        static int B(int bit, byte probability) => Vp8EncoderCosts.Bit(bit, probability);
        if (cls == 0)
        {
            return B(0, p[1]);
        }

        int c = B(1, p[1]);
        if (cls == 1)
        {
            return c + B(0, p[2]);
        }

        c += B(1, p[2]);
        if (cls <= 4)
        {
            c += B(0, p[3]);
            return cls == 2 ? c + B(0, p[4]) : c + B(1, p[4]) + B(cls == 3 ? 0 : 1, p[5]);
        }

        c += B(1, p[3]);
        if (cls <= 6)
        {
            return c + B(0, p[6]) + B(cls == 5 ? 0 : 1, p[7]);
        }

        c += B(1, p[6]);
        return cls <= 8
            ? c + B(0, p[8]) + B(cls == 7 ? 0 : 1, p[9])
            : c + B(1, p[8]) + B(cls == 9 ? 0 : 1, p[10]);
    }
}
