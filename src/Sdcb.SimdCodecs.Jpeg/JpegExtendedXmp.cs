using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Joins Extended XMP (XMP Specification Part 3, §1.1.3.1) to the standard packet. A writer whose packet outgrows one
/// APP1 segment keeps the important properties in the standard packet, which names the rest with
/// <c>xmpNote:HasExtendedXMP</c> = a GUID, and writes the rest as a separate serialization (no packet wrapper) split
/// over APP1 segments <c>http://ns.adobe.com/xmp/extension/\0</c>, GUID (32 ASCII hex digits, the MD5 of the whole
/// serialization), full length and chunk offset (both 32-bit big-endian), chunk bytes.
/// <para>
/// The join is byte-level, so a consumer gets one packet it can parse: the content of the extended serialization's
/// <c>rdf:RDF</c> element is inserted just before the standard packet's <c>&lt;/rdf:RDF&gt;</c>; every other byte of the
/// standard packet is kept, <c>xmpNote:HasExtendedXMP</c> and the <c>xpacket</c> trailer included (the spec has a
/// reader drop the property from its data model; a raw block keeps it, which also lets a consumer see the packet was
/// joined). The namespace bindings the inserted content relied on from its own ancestors (<c>rdf:RDF</c>,
/// <c>x:xmpmeta</c>) are declared again, from their original bytes, on each element directly inside <c>rdf:RDF</c>
/// wherever the standard packet's scope there does not already bind the prefix to the same URI, so neither an unbound
/// prefix nor one the standard packet binds differently changes the meaning of the inserted content.
/// </para>
/// <para>
/// Prefixes are resolved by namespace, not taken literally: <c>HasExtendedXMP</c> is recognised under whatever prefix
/// binds <c>http://ns.adobe.com/xmp/note/</c>, as an attribute or as a simple-valued element. Anything else, from a
/// chunk that is missing, overlapping, out of bounds or of a disagreeing length, to a digest mismatch, a GUID named
/// twice with different values, or XML this reader does not accept (a DOCTYPE, unbalanced tags, more than one
/// <c>rdf:RDF</c>), leaves the standard packet alone. Chunks may come in any order.
/// </para>
/// </summary>
internal static class JpegExtendedXmp
{
    /// <summary>The largest extended serialization joined. Its bytes are in the file, so this bounds nothing the input
    /// does not already hold; it keeps a hostile file from making one metadata read the size of the file.</summary>
    public const int MaxExtendedLength = 16 << 20;

    private const byte App1 = 0xE1;
    private const int GuidLength = 32;
    private const int ChunkHeaderLength = GuidLength + 4 + 4;

    private const string XmpNoteNamespace = "http://ns.adobe.com/xmp/note/";
    private const string RdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    /// <summary>The identifier that opens an Extended XMP APP1 payload, terminating NUL included.</summary>
    public static ReadOnlySpan<byte> ExtensionId => "http://ns.adobe.com/xmp/extension/\0"u8;

    /// <summary>True when <paramref name="segment"/> is an APP1 segment carrying an Extended XMP chunk.</summary>
    public static bool IsExtensionSegment(ReadOnlySpan<byte> jpeg, in JpegSegment segment) =>
        segment.Marker == App1 && segment.Payload(jpeg).StartsWith(ExtensionId);

    /// <summary>
    /// The standard packet with the Extended XMP it names joined in; false (and nothing allocated beyond the scan) when
    /// there is nothing valid to join, in which case the caller keeps the standard packet as it is.
    /// </summary>
    public static bool TryJoin(ReadOnlySpan<byte> jpeg, List<JpegSegment> segments, ReadOnlySpan<byte> standard,
        [NotNullWhen(true)] out byte[]? joined)
    {
        joined = null;
        if (!TryReadStandard(standard, out var guid, out var insertAt, out var standardScope))
            return false;
        if (!TryAssemble(jpeg, segments, guid, out var extended))
            return false;
        if (!TryReadExtended(extended, out var contentStart, out var contentEnd, out var extendedScope, out var children))
            return false;

        // The bindings the inserted content needs that the standard packet's rdf:RDF scope does not give it.
        var redeclare = new List<(string Prefix, byte[] Text)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = extendedScope.Count - 1; i >= 0; i--)
        {
            var binding = extendedScope[i];
            if (!seen.Add(binding.Prefix) || binding.Prefix is "xml")
                continue;
            if (Lookup(standardScope, binding.Prefix) == binding.Uri)
                continue;
            var text = new byte[1 + binding.RawEnd - binding.RawStart];
            text[0] = (byte)' ';
            extended.AsSpan(binding.RawStart, binding.RawEnd - binding.RawStart).CopyTo(text.AsSpan(1));
            redeclare.Add((binding.Prefix, text));
        }

        redeclare.Reverse(); // document order

        // An unprefixed element in the inserted content is in no namespace unless an ancestor said otherwise; a
        // default namespace on the standard packet's ancestors would capture it, so undeclare it.
        if (!seen.Contains("") && Lookup(standardScope, "") is { Length: > 0 })
            redeclare.Add(("", " xmlns=\"\""u8.ToArray()));

        // Sized before anything is written: re-declarations repeat per child, so a hostile packet (many ancestor
        // declarations, many children) could otherwise multiply its size. The joined packet may add at most as much
        // again as the extended serialization holds.
        long total = (long)standard.Length + (contentEnd - contentStart);
        foreach (var child in children)
        {
            foreach (var (prefix, bytes) in redeclare)
            {
                if (!child.Declared.Contains(prefix))
                    total += bytes.Length;
            }
        }

        if (total > (long)standard.Length + 2L * extended.Length)
            return false;

        var result = new byte[total];
        var o = 0;
        standard[..insertAt].CopyTo(result);
        o += insertAt;
        var from = contentStart;
        foreach (var child in children)
        {
            var at = child.InsertAt;
            extended.AsSpan(from, at - from).CopyTo(result.AsSpan(o));
            o += at - from;
            foreach (var (prefix, bytes) in redeclare)
            {
                if (child.Declared.Contains(prefix))
                    continue;
                bytes.CopyTo(result, o);
                o += bytes.Length;
            }

            from = at;
        }

        extended.AsSpan(from, contentEnd - from).CopyTo(result.AsSpan(o));
        o += contentEnd - from;
        standard[insertAt..].CopyTo(result.AsSpan(o));
        joined = result;
        return true;
    }

    /// <summary>Gathers the chunks whose GUID is <paramref name="guid"/> into the whole serialization and checks it
    /// against its MD5.</summary>
    private static bool TryAssemble(ReadOnlySpan<byte> jpeg, List<JpegSegment> segments, byte[] guid,
        [NotNullWhen(true)] out byte[]? extended)
    {
        extended = null;
        long fullLength = -1;
        var chunks = new List<(uint Offset, int Start, int Length)>();
        foreach (var segment in segments)
        {
            if (!IsExtensionSegment(jpeg, segment))
                continue;
            var body = segment.Payload(jpeg)[ExtensionId.Length..];
            if (body.Length < GuidLength || !body[..GuidLength].SequenceEqual(guid))
                continue; // another packet's chunk (or too short to name one): not ours to read
            if (body.Length <= ChunkHeaderLength)
                return false; // ours, but its header is cut short or it carries no bytes
            var length = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(GuidLength, 4));
            var offset = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(GuidLength + 4, 4));
            if (fullLength < 0)
                fullLength = length;
            else if (fullLength != length)
                return false;
            chunks.Add((offset, segment.PayloadOffset + ExtensionId.Length + ChunkHeaderLength, body.Length - ChunkHeaderLength));
        }

        if (chunks.Count == 0 || fullLength <= 0 || fullLength > MaxExtendedLength)
            return false;

        // Sorted by offset, the chunks must tile [0, fullLength) exactly: no gap, no overlap, nothing past the end.
        chunks.Sort((x, y) => x.Offset.CompareTo(y.Offset));
        long next = 0;
        foreach (var chunk in chunks)
        {
            if (chunk.Offset != next)
                return false;
            next += chunk.Length;
        }

        if (next != fullLength)
            return false;

        var bytes = new byte[fullLength];
        foreach (var chunk in chunks)
            jpeg.Slice(chunk.Start, chunk.Length).CopyTo(bytes.AsSpan((int)chunk.Offset));

        Span<byte> digest = stackalloc byte[16];
        JpegMd5.HashData(bytes, digest);
        for (var i = 0; i < 16; i++)
        {
            if (HexValue(guid[2 * i]) != digest[i] >> 4 || HexValue(guid[2 * i + 1]) != (digest[i] & 15))
                return false;
        }

        extended = bytes;
        return true;
    }

    /// <summary>The standard packet's <c>HasExtendedXMP</c> GUID, where its <c>&lt;/rdf:RDF&gt;</c> starts, and the
    /// namespace bindings in scope inside its <c>rdf:RDF</c> element.</summary>
    private static bool TryReadStandard(ReadOnlySpan<byte> xml, [NotNullWhen(true)] out byte[]? guid, out int insertAt,
        [NotNullWhen(true)] out List<Binding>? rdfScope)
    {
        guid = null;
        insertAt = -1;
        rdfScope = null;
        string? value = null;
        var scope = new List<Binding>();
        var open = new List<(int Start, int End)>();
        int rdfDepth = -1, rdfCount = 0, noteDepth = -1, noteContent = 0;
        var tokens = new XmlTokens(xml);
        while (true)
        {
            var kind = tokens.Next();
            if (kind == TokenKind.End)
                break;
            if (kind == TokenKind.Error)
                return false;
            if (kind == TokenKind.Other)
                continue;

            if (kind == TokenKind.StartTag)
            {
                var depth = open.Count + 1;
                if (!PushDeclarations(xml, tokens, depth, scope, out _))
                    return false;
                if (IsElement(xml, tokens, scope, RdfNamespace, "RDF"))
                {
                    if (++rdfCount > 1 || tokens.SelfClosing)
                        return false;
                    rdfDepth = depth;
                    rdfScope = [.. scope];
                }

                var attributes = new AttributeReader(xml, tokens.NameEnd, tokens.AttributesEnd);
                while (attributes.Next(out var a))
                {
                    var name = xml[a.NameStart..a.NameEnd];
                    if (IsDeclaration(name))
                        continue;
                    if (Resolve(name, scope, attribute: true, out var local) == XmpNoteNamespace && local.SequenceEqual("HasExtendedXMP"u8)
                        && !Agree(ref value, Decode(xml[a.ValueStart..a.ValueEnd])))
                        return false;
                }

                if (IsElement(xml, tokens, scope, XmpNoteNamespace, "HasExtendedXMP") && noteDepth < 0)
                {
                    if (tokens.SelfClosing)
                        return false;
                    noteDepth = depth;
                    noteContent = tokens.End;
                }

                if (tokens.SelfClosing)
                    Pop(scope, depth);
                else
                    open.Add((tokens.NameStart, tokens.NameEnd));
            }
            else
            {
                var depth = open.Count;
                if (depth == 0 || !xml[open[^1].Start..open[^1].End].SequenceEqual(xml[tokens.NameStart..tokens.NameEnd]))
                    return false;
                if (depth == noteDepth)
                {
                    // A simple value: text only (Decode refuses markup), white space around it ignored.
                    if (!Agree(ref value, Decode(xml[noteContent..tokens.Start])))
                        return false;
                    noteDepth = -1;
                }

                if (depth == rdfDepth)
                {
                    insertAt = tokens.Start;
                    rdfDepth = -1;
                }

                Pop(scope, depth);
                open.RemoveAt(open.Count - 1);
            }
        }

        if (open.Count != 0 || rdfCount != 1 || insertAt < 0 || rdfScope is null || value is null)
            return false;
        guid = Encoding.ASCII.GetBytes(value);
        return true;
    }

    /// <summary>Where the content of the extended serialization's <c>rdf:RDF</c> element lies, the bindings in scope
    /// there, and the elements directly inside it with the prefixes each declares itself.</summary>
    private static bool TryReadExtended(byte[] bytes, out int contentStart, out int contentEnd,
        [NotNullWhen(true)] out List<Binding>? rdfScope, [NotNullWhen(true)] out List<Child>? children)
    {
        ReadOnlySpan<byte> xml = bytes;
        contentStart = contentEnd = -1;
        rdfScope = null;
        children = null;
        var found = new List<Child>();
        var scope = new List<Binding>();
        var open = new List<(int Start, int End)>();
        int rdfDepth = -1, rdfCount = 0;
        var tokens = new XmlTokens(xml);
        while (true)
        {
            var kind = tokens.Next();
            if (kind == TokenKind.End)
                break;
            if (kind == TokenKind.Error)
                return false;
            if (kind == TokenKind.Other)
                continue;

            if (kind == TokenKind.StartTag)
            {
                var depth = open.Count + 1;
                if (!PushDeclarations(xml, tokens, depth, scope, out var declared))
                    return false;
                if (rdfDepth > 0 && depth == rdfDepth + 1)
                    found.Add(new Child(tokens.NameEnd, declared));
                if (IsElement(xml, tokens, scope, RdfNamespace, "RDF"))
                {
                    if (++rdfCount > 1)
                        return false;
                    rdfScope = [.. scope];
                    contentStart = tokens.End;
                    if (tokens.SelfClosing)
                        contentEnd = tokens.End;
                    else
                        rdfDepth = depth;
                }

                if (tokens.SelfClosing)
                    Pop(scope, depth);
                else
                    open.Add((tokens.NameStart, tokens.NameEnd));
            }
            else
            {
                var depth = open.Count;
                if (depth == 0 || !xml[open[^1].Start..open[^1].End].SequenceEqual(xml[tokens.NameStart..tokens.NameEnd]))
                    return false;
                if (depth == rdfDepth)
                {
                    contentEnd = tokens.Start;
                    rdfDepth = -1;
                }

                Pop(scope, depth);
                open.RemoveAt(open.Count - 1);
            }
        }

        if (open.Count != 0 || rdfCount != 1 || contentEnd < 0 || rdfScope is null)
            return false;
        children = found;
        return true;
    }

    /// <summary>Records a value of <c>HasExtendedXMP</c>: 32 hex digits, and the same each time it is given.</summary>
    private static bool Agree(ref string? value, string? candidate)
    {
        candidate = candidate?.Trim(' ', '\t', '\r', '\n');
        if (candidate is not { Length: GuidLength })
            return false;
        foreach (var ch in candidate)
        {
            if (ch > 0x7F || HexValue((byte)ch) < 0)
                return false;
        }

        if (value is not null && value != candidate)
            return false;
        value = candidate;
        return true;
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        _ => -1,
    };

    // ---- namespaces -------------------------------------------------------------------------------------------

    /// <summary>One <c>xmlns</c> / <c>xmlns:p</c> declaration: the prefix (empty for the default namespace), the
    /// decoded URI, the element depth it was declared at, and where the attribute's bytes lie.</summary>
    private readonly record struct Binding(string Prefix, string Uri, int Depth, int RawStart, int RawEnd);

    /// <summary>An element directly inside the extended <c>rdf:RDF</c>: where re-declared namespaces go (after its
    /// name) and the prefixes it declares itself.</summary>
    private sealed record Child(int InsertAt, HashSet<string> Declared);

    private static bool IsDeclaration(ReadOnlySpan<byte> name) =>
        name.SequenceEqual("xmlns"u8) || name.StartsWith("xmlns:"u8);

    private static bool PushDeclarations(ReadOnlySpan<byte> xml, in XmlTokens tokens, int depth, List<Binding> scope,
        out HashSet<string> declared)
    {
        declared = new HashSet<string>(StringComparer.Ordinal);
        var attributes = new AttributeReader(xml, tokens.NameEnd, tokens.AttributesEnd);
        while (attributes.Next(out var a))
        {
            var name = xml[a.NameStart..a.NameEnd];
            if (!IsDeclaration(name))
                continue;
            var prefix = name.Length == 5 ? "" : Encoding.UTF8.GetString(name[6..]);
            var uri = Decode(xml[a.ValueStart..a.ValueEnd]);
            if (uri is null || !declared.Add(prefix))
                return false;
            scope.Add(new Binding(prefix, uri, depth, a.NameStart, a.ValueEnd + 1));
        }

        return true;
    }

    private static void Pop(List<Binding> scope, int depth)
    {
        while (scope.Count > 0 && scope[^1].Depth >= depth)
            scope.RemoveAt(scope.Count - 1);
    }

    /// <summary>The URI <paramref name="prefix"/> is bound to (<c>""</c> for an unbound default namespace), or null.</summary>
    private static string? Lookup(List<Binding> scope, string prefix)
    {
        if (prefix == "xml")
            return XmlNamespace;
        for (var i = scope.Count - 1; i >= 0; i--)
        {
            if (scope[i].Prefix == prefix)
                return scope[i].Uri;
        }

        return prefix.Length == 0 ? "" : null;
    }

    /// <summary>The namespace of a qualified name and its local part; an unprefixed attribute is in no namespace.</summary>
    private static string? Resolve(ReadOnlySpan<byte> name, List<Binding> scope, bool attribute, out ReadOnlySpan<byte> local)
    {
        var colon = name.IndexOf((byte)':');
        if (colon < 0)
        {
            local = name;
            return attribute ? "" : Lookup(scope, "");
        }

        local = name[(colon + 1)..];
        return Lookup(scope, Encoding.UTF8.GetString(name[..colon]));
    }

    private static bool IsElement(ReadOnlySpan<byte> xml, in XmlTokens tokens, List<Binding> scope, string ns, string local) =>
        Resolve(xml[tokens.NameStart..tokens.NameEnd], scope, attribute: false, out var name) == ns
        && Ascii.Equals(name, local);

    /// <summary>Character data with the predefined entities and character references replaced; null when it holds
    /// markup or an entity reference this reader does not know.</summary>
    private static string? Decode(ReadOnlySpan<byte> raw)
    {
        if (raw.IndexOf((byte)'<') >= 0)
            return null;
        var text = Encoding.UTF8.GetString(raw);
        var amp = text.IndexOf('&');
        if (amp < 0)
            return text;

        var sb = new StringBuilder(text.Length);
        sb.Append(text, 0, amp);
        for (var i = amp; i < text.Length; i++)
        {
            if (text[i] != '&')
            {
                sb.Append(text[i]);
                continue;
            }

            var semi = text.IndexOf(';', i + 1);
            if (semi < 0)
                return null;
            var entity = text.AsSpan(i + 1, semi - i - 1);
            switch (entity)
            {
                case "lt": sb.Append('<'); break;
                case "gt": sb.Append('>'); break;
                case "amp": sb.Append('&'); break;
                case "quot": sb.Append('"'); break;
                case "apos": sb.Append('\''); break;
                default:
                    if (entity.Length < 2 || entity[0] != '#')
                        return null;
                    var hex = entity[1] == 'x';
                    var digits = entity[(hex ? 2 : 1)..];
                    if (!int.TryParse(digits, hex ? System.Globalization.NumberStyles.AllowHexSpecifier : System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var code)
                        || code is <= 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
                        return null;
                    sb.Append(char.ConvertFromUtf32(code));
                    break;
            }

            i = semi;
        }

        return sb.ToString();
    }

    // ---- tokens -----------------------------------------------------------------------------------------------

    private enum TokenKind { End, Error, StartTag, EndTag, Other }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool IsNameByte(byte b) => b > (byte)' ' && b is not ((byte)'<' or (byte)'>' or (byte)'/' or (byte)'='
        or (byte)'"' or (byte)'\'' or (byte)'&' or (byte)'?' or (byte)'!');

    private static int SkipName(ReadOnlySpan<byte> s, int p)
    {
        while (p < s.Length && IsNameByte(s[p]))
            p++;
        return p;
    }

    private static int SkipSpace(ReadOnlySpan<byte> s, int p)
    {
        while (p < s.Length && IsSpace(s[p]))
            p++;
        return p;
    }

    private readonly record struct Attribute(int NameStart, int NameEnd, int ValueStart, int ValueEnd);

    /// <summary>Reads <c>name = "value"</c> (or single-quoted) at <paramref name="p"/>; a value may not hold <c>&lt;</c>.</summary>
    private static bool TryAttribute(ReadOnlySpan<byte> s, ref int p, out Attribute attribute)
    {
        attribute = default;
        var nameStart = p;
        var nameEnd = SkipName(s, p);
        if (nameEnd == nameStart)
            return false;
        p = SkipSpace(s, nameEnd);
        if (p >= s.Length || s[p] != '=')
            return false;
        p = SkipSpace(s, p + 1);
        if (p >= s.Length || s[p] is not ((byte)'"' or (byte)'\''))
            return false;
        var valueStart = p + 1;
        var close = s[valueStart..].IndexOf(s[p]);
        if (close < 0 || s.Slice(valueStart, close).IndexOf((byte)'<') >= 0)
            return false;
        p = valueStart + close + 1;
        attribute = new Attribute(nameStart, nameEnd, valueStart, valueStart + close);
        return true;
    }

    /// <summary>Re-reads the attributes of a start tag <see cref="XmlTokens"/> has already validated.</summary>
    private ref struct AttributeReader(ReadOnlySpan<byte> xml, int start, int end)
    {
        private readonly ReadOnlySpan<byte> _s = xml[..end];
        private int _p = start;

        public bool Next(out Attribute attribute)
        {
            _p = SkipSpace(_s, _p);
            attribute = default;
            return _p < _s.Length && TryAttribute(_s, ref _p, out attribute);
        }
    }

    /// <summary>
    /// A forward-only tokenizer over UTF-8 XML, enough for an XMP packet: start tags (with their attributes checked),
    /// end tags, and everything else (text, comments, CDATA, processing instructions) as <see cref="TokenKind.Other"/>.
    /// A DOCTYPE or any other markup declaration is an error (XMP has none). Every step moves forward, and every search
    /// starts where the last one ended, so a packet is read in time linear in its length.
    /// </summary>
    private ref struct XmlTokens(ReadOnlySpan<byte> xml)
    {
        private readonly ReadOnlySpan<byte> _s = xml;
        private int _p;

        /// <summary>The current token's first byte and the byte after its last.</summary>
        public int Start, End;

        /// <summary>A tag's name; for a start tag, where its attributes end (at <c>&gt;</c> or <c>/&gt;</c>).</summary>
        public int NameStart, NameEnd, AttributesEnd;

        public bool SelfClosing;

        public TokenKind Next()
        {
            var s = _s;
            var p = _p;
            if (p >= s.Length)
                return TokenKind.End;
            Start = p;
            if (s[p] != '<')
            {
                var lt = s[p..].IndexOf((byte)'<');
                return Advance(lt < 0 ? s.Length : p + lt, TokenKind.Other);
            }

            var rest = s[p..];
            if (rest.StartsWith("<?"u8))
                return SkipPast(p + 2, "?>"u8);
            if (rest.StartsWith("<!--"u8))
                return SkipPast(p + 4, "-->"u8);
            if (rest.StartsWith("<![CDATA["u8))
                return SkipPast(p + 9, "]]>"u8);
            if (rest.StartsWith("<!"u8))
                return Fail();

            if (rest.StartsWith("</"u8))
            {
                NameStart = p + 2;
                NameEnd = SkipName(s, NameStart);
                if (NameEnd == NameStart)
                    return Fail();
                var q = SkipSpace(s, NameEnd);
                return q < s.Length && s[q] == '>' ? Advance(q + 1, TokenKind.EndTag) : Fail();
            }

            NameStart = p + 1;
            NameEnd = SkipName(s, NameStart);
            if (NameEnd == NameStart)
                return Fail();
            var r = NameEnd;
            while (true)
            {
                var before = r;
                r = SkipSpace(s, r);
                if (r >= s.Length)
                    return Fail();
                if (s[r] == '>')
                {
                    AttributesEnd = r;
                    SelfClosing = false;
                    return Advance(r + 1, TokenKind.StartTag);
                }

                if (s[r] == '/')
                {
                    if (r + 1 >= s.Length || s[r + 1] != '>')
                        return Fail();
                    AttributesEnd = r;
                    SelfClosing = true;
                    return Advance(r + 2, TokenKind.StartTag);
                }

                if (r == before || !TryAttribute(s, ref r, out _))
                    return Fail(); // attributes are separated from the name and each other by white space
            }
        }

        private TokenKind SkipPast(int from, ReadOnlySpan<byte> terminator)
        {
            var i = _s[from..].IndexOf(terminator);
            return i < 0 ? Fail() : Advance(from + i + terminator.Length, TokenKind.Other);
        }

        private TokenKind Advance(int end, TokenKind kind)
        {
            End = _p = end;
            return kind;
        }

        private TokenKind Fail()
        {
            _p = _s.Length;
            return TokenKind.Error;
        }
    }
}
