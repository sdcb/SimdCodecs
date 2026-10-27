namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// The inverse multiple component transforms of T.800 Annex G, which COD's MCT flag applies to
/// components 0, 1 and 2 together.
/// <para>
/// They run after the inverse DWT and before the DC level shift (G.1.2), on the signed values the
/// wavelet reconstructs. That order is not a convention: the transforms were applied to level-shifted
/// samples at the encoder, so undoing the shift first would feed them numbers offset by half the
/// range.
/// </para>
/// <para>
/// What the three components MEAN afterwards (RGB, or something a JP2 <c>colr</c> box names) is not
/// this transform's business. MCT is part of how the codestream was coded, so the decoder undoes it;
/// the colour interpretation belongs to whoever holds the container or the PDF image dictionary.
/// </para>
/// </summary>
internal static class ComponentTransform
{
    /// <summary>
    /// G.2.2: the reversible component transform, inverted. Integer and exact, which is what keeps a
    /// lossless colour codestream lossless.
    /// </summary>
    /// <param name="y0">Component 0 (Y0), rewritten in place as the first output component.</param>
    /// <param name="y1">Component 1 (Y1), rewritten in place as the second.</param>
    /// <param name="y2">Component 2 (Y2), rewritten in place as the third.</param>
    /// <remarks>
    /// The floor in G.2.2's first equation is an arithmetic shift, not a division: a division rounds
    /// toward zero, which is wrong for the negative sums half of all samples produce.
    /// </remarks>
    public static void InverseRct(int[] y0, int[] y1, int[] y2)
    {
        for (var i = 0; i < y0.Length; i++)
        {
            var g = y0[i] - ((y1[i] + y2[i]) >> 2);
            var r = y2[i] + g;
            var b = y1[i] + g;

            y0[i] = r;
            y1[i] = g;
            y2[i] = b;
        }
    }

    /// <summary>
    /// G.3.2: the irreversible component transform, inverted. This is the YCbCr to RGB of JPEG
    /// with T.800's own rounded coefficients, applied to the 9/7 path's unrounded samples; the
    /// rounding to integers waits for the level shift, so it happens once.
    /// </summary>
    /// <param name="y0">Component 0 (Y), rewritten in place as the first output component.</param>
    /// <param name="y1">Component 1 (Cb), rewritten in place as the second.</param>
    /// <param name="y2">Component 2 (Cr), rewritten in place as the third.</param>
    public static void InverseIct(float[] y0, float[] y1, float[] y2)
    {
        for (var i = 0; i < y0.Length; i++)
        {
            var y = y0[i];
            var cb = y1[i];
            var cr = y2[i];

            y0[i] = y + 1.402f * cr;
            y1[i] = y - 0.34413f * cb - 0.71414f * cr;
            y2[i] = y + 1.772f * cb;
        }
    }
}
