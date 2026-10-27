using System.Text;
using System.Xml;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// The XMP properties <see cref="JpegEncodeOptions.Xmp"/> writes: <c>xmp:CreatorTool</c>, <c>dc:description</c>,
/// and a caller-owned namespace (<see cref="CustomNamespaceUri"/> bound to <see cref="CustomPrefix"/>) holding
/// <see cref="CustomProperties"/>. Every property is written in RDF's attribute form on a single
/// <c>rdf:Description</c>, so the packet is an escaped string template: no <c>XmlSerializer</c>, no DTD, no
/// namespace-aware writer, nothing the trimmer or AOT compiler has to keep alive.
/// <para>
/// A <c>null</c> property is left out; an empty string is written as an empty attribute. Values may hold any text
/// XML 1.0 can carry (non-ASCII included, written as UTF-8): <c>&lt; &gt; &amp; " '</c> and tab / newline / carriage
/// return are escaped, so they read back exactly. Characters XML 1.0 cannot represent at all (most C0 controls,
/// U+FFFE / U+FFFF, an unpaired surrogate) are an <see cref="ArgumentException"/>, as is a custom key that is not an
/// XML NCName, a duplicate key, or a custom property with no namespace to live in.
/// </para>
/// <para>
/// There is no <c>xpacket</c> wrapper and no Extended XMP: the packet must fit one APP1 segment
/// (<see cref="JpegEncodeOptions.MaxXmpPacketBytes"/> bytes of UTF-8), and a larger one is refused rather than split.
/// This is the same shape as <c>GainMapXmp</c>'s packets (<c>Sdcb.SimdCodecs.Jpeg.GainMap</c>), which Chromium and libultrahdr read.
/// </para>
/// </summary>
public sealed record JpegXmp
{
    /// <summary>The <c>xmp:</c> namespace, <c>http://ns.adobe.com/xap/1.0/</c>.</summary>
    public const string XmpNamespaceUri = "http://ns.adobe.com/xap/1.0/";

    /// <summary>The <c>dc:</c> (Dublin Core) namespace.</summary>
    public const string DublinCoreNamespaceUri = "http://purl.org/dc/elements/1.1/";

    private const string RdfNamespaceUri = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string MetaNamespaceUri = "adobe:ns:meta/";

    /// <summary><c>xmp:CreatorTool</c>: the application that made the image.</summary>
    public string? CreatorTool { get; init; }

    /// <summary><c>dc:description</c>, written as a plain attribute (which XMP readers take as the <c>x-default</c> language).</summary>
    public string? Description { get; init; }

    /// <summary>Namespace URI for <see cref="CustomProperties"/>. Required when there are any; must not be one of the
    /// namespaces this packet already uses (xmp, dc, rdf, the <c>x:</c> wrapper's).</summary>
    public string? CustomNamespaceUri { get; init; }

    /// <summary>Prefix bound to <see cref="CustomNamespaceUri"/>: an XML NCName, not starting with <c>xml</c> in any
    /// case, and not <c>x</c>, <c>rdf</c>, <c>xmp</c> or <c>dc</c>, which the packet uses itself.</summary>
    public string? CustomPrefix { get; init; }

    /// <summary>Key/value pairs written as <c>prefix:key="value"</c> in order. Keys are XML NCNames
    /// (<see cref="XmlConvert.VerifyNCName"/>) and unique.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? CustomProperties { get; init; }

    /// <summary>
    /// Builds the packet's UTF-8 bytes (the part after the <c>http://ns.adobe.com/xap/1.0/\0</c> identifier).
    /// </summary>
    /// <exception cref="ArgumentException">A value, prefix, URI or key is invalid (see the type remarks), or the packet
    /// is over <see cref="JpegEncodeOptions.MaxXmpPacketBytes"/> bytes.</exception>
    public byte[] BuildPacket()
    {
        var sb = new StringBuilder(512);
        sb.Append("<x:xmpmeta xmlns:x=\"").Append(MetaNamespaceUri).Append("\" x:xmptk=\"Sdcb.SimdCodecs.Jpeg\">\n");
        sb.Append("  <rdf:RDF xmlns:rdf=\"").Append(RdfNamespaceUri).Append("\">\n");
        sb.Append("    <rdf:Description rdf:about=\"\"");

        var custom = CustomProperties;
        var hasCustom = custom is { Count: > 0 };
        if (hasCustom)
        {
            var prefix = CustomPrefix;
            var uri = CustomNamespaceUri;
            if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(uri))
                throw new ArgumentException("Custom XMP properties need both CustomPrefix and CustomNamespaceUri.");
            CheckPrefix(prefix);
            CheckUri(uri);
            sb.Append(" xmlns:").Append(prefix).Append("=\"");
            AppendEscaped(sb, uri, nameof(CustomNamespaceUri));
            sb.Append('"');
        }
        if (CreatorTool is not null)
            sb.Append(" xmlns:xmp=\"").Append(XmpNamespaceUri).Append('"');
        if (Description is not null)
            sb.Append(" xmlns:dc=\"").Append(DublinCoreNamespaceUri).Append('"');

        if (CreatorTool is { } tool)
        {
            sb.Append(" xmp:CreatorTool=\"");
            AppendEscaped(sb, tool, nameof(CreatorTool));
            sb.Append('"');
        }
        if (Description is { } description)
        {
            sb.Append(" dc:description=\"");
            AppendEscaped(sb, description, nameof(Description));
            sb.Append('"');
        }
        if (hasCustom)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, value) in custom!)
            {
                if (string.IsNullOrEmpty(key))
                    throw new ArgumentException("A custom XMP key is empty.");
                string ncName;
                try
                {
                    ncName = XmlConvert.VerifyNCName(key);
                }
                catch (XmlException ex)
                {
                    throw new ArgumentException($"Custom XMP key '{key}' is not an XML NCName: {ex.Message}", ex);
                }
                if (!seen.Add(ncName))
                    throw new ArgumentException($"Custom XMP key '{key}' appears twice.");
                if (value is null)
                    throw new ArgumentException($"Custom XMP key '{key}' has a null value.");
                sb.Append(' ').Append(CustomPrefix).Append(':').Append(ncName).Append("=\"");
                AppendEscaped(sb, value, $"the value of '{key}'");
                sb.Append('"');
            }
        }

        sb.Append("/>\n  </rdf:RDF>\n</x:xmpmeta>");

        // Count before allocating, so an absurd value is refused without a second copy of it.
        var packet = sb.ToString();
        var bytes = Encoding.UTF8.GetByteCount(packet);
        if (bytes > JpegEncodeOptions.MaxXmpPacketBytes)
            throw new ArgumentException($"XMP packet is {bytes} bytes; one APP1 segment holds at most {JpegEncodeOptions.MaxXmpPacketBytes} (Extended XMP is not written).");
        return Encoding.UTF8.GetBytes(packet);
    }

    private static void CheckPrefix(string prefix)
    {
        try
        {
            XmlConvert.VerifyNCName(prefix);
        }
        catch (XmlException ex)
        {
            throw new ArgumentException($"CustomPrefix '{prefix}' is not an XML NCName: {ex.Message}", ex);
        }
        if (prefix.StartsWith("xml", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"CustomPrefix '{prefix}' starts with 'xml', which XML reserves.");
        if (prefix is "x" or "rdf" or "xmp" or "dc")
            throw new ArgumentException($"CustomPrefix '{prefix}' is already used by the packet itself.");
    }

    private static void CheckUri(string uri)
    {
        if (uri is XmpNamespaceUri or DublinCoreNamespaceUri or RdfNamespaceUri or MetaNamespaceUri)
            throw new ArgumentException($"CustomNamespaceUri '{uri}' is a namespace the packet already uses.");
    }

    /// <summary>
    /// Appends <paramref name="value"/> escaped for a double-quoted attribute. Tab, LF and CR are written as character
    /// references because an XML parser normalises a literal one to a space, which would not read back.
    /// </summary>
    private static void AppendEscaped(StringBuilder sb, string value, string what)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                case '\t': sb.Append("&#9;"); break;
                case '\n': sb.Append("&#10;"); break;
                case '\r': sb.Append("&#13;"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        sb.Append(c).Append(value[++i]);
                    }
                    else if (c < 0x20 || char.IsSurrogate(c) || c is '￾' or '￿')
                    {
                        throw new ArgumentException($"{what} holds U+{(int)c:X4}, which XML 1.0 cannot carry.");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
    }
}
