namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// One textual entry of a PNG: a <paramref name="Keyword"/> and its <paramref name="Text"/>, as the PNG
/// spec's text chunks carry them (§11.3.4). <see cref="PngWriter"/> writes each as an <c>iTXt</c> chunk, so
/// the text is UTF-8 and any length; <see cref="PngReader"/> reads <c>tEXt</c>, <c>zTXt</c> and <c>iTXt</c>
/// alike into this one shape, so a file written by another tool reads the same way.
/// </summary>
/// <param name="Keyword">1 to 79 Latin-1 printable characters (codes 32 to 126 and 161 to 255), with no
/// leading, trailing or doubled space, as the spec requires. The registered keywords include <c>Title</c>,
/// <c>Author</c>, <c>Description</c>, <c>Software</c>, <c>Source</c> and <c>Comment</c>; an application's own
/// keyword is any other.</param>
/// <param name="Text">The text, any Unicode; written uncompressed when short and deflated when long.</param>
public sealed record PngText(string Keyword, string Text)
{
    /// <summary>The longest keyword the spec allows, in bytes.</summary>
    public const int MaxKeywordLength = 79;

    /// <summary>Whether <paramref name="keyword"/> is one the spec allows (see <see cref="Keyword"/>).</summary>
    public static bool IsValidKeyword(string keyword)
    {
        if (keyword.Length is 0 or > MaxKeywordLength || keyword[0] == ' ' || keyword[^1] == ' ' || keyword.Contains("  "))
        {
            return false;
        }
        foreach (var c in keyword)
        {
            if (c is not ((>= ' ' and <= '~') or (>= '¡' and <= 'ÿ')))
            {
                return false;
            }
        }
        return true;
    }
}
