using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// The two XMP packets of an Ultra HDR v1 / Adobe gain-map JPEG, as APP1
/// segments: the <em>primary</em> image's <c>GContainer</c> directory (locates
/// the gain-map JPEG appended after the base) and the <em>gain-map</em> image's
/// <c>hdrgm</c> parameters. Writers emit byte layouts matching libultrahdr —
/// the reference the Chromium/Skia parser is tested against; the parsers are a
/// targeted read of just these two vocabularies (attribute and element form),
/// not a general XMP engine.
/// </summary>
public static class GainMapXmp
{
    /// <summary>The APP1 XMP identifier: the namespace URI plus NUL terminator (29 bytes).</summary>
    public static ReadOnlySpan<byte> App1Identifier => "http://ns.adobe.com/xap/1.0/\0"u8;

    internal static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    internal static readonly XNamespace Hdrgm = "http://ns.adobe.com/hdr-gain-map/1.0/";
    internal static readonly XNamespace Container = "http://ns.google.com/photos/1.0/container/";
    internal static readonly XNamespace Item = "http://ns.google.com/photos/1.0/container/item/";

    /// <summary>
    /// Builds the primary image's complete APP1 segment: the GContainer directory
    /// declaring a Primary item plus a GainMap item of <paramref name="gainMapLength"/>
    /// bytes (the gain-map JPEG's total size <em>including</em> its own XMP segment —
    /// readers locate the gain map as the trailing <c>Item:Length</c> bytes of the file).
    /// </summary>
    public static byte[] WritePrimaryApp1(int gainMapLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gainMapLength);
        var packet = $"""
            <x:xmpmeta xmlns:x="adobe:ns:meta/" x:xmptk="Adobe XMP Core 5.1.2">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about="" xmlns:Container="http://ns.google.com/photos/1.0/container/" xmlns:Item="http://ns.google.com/photos/1.0/container/item/" xmlns:hdrgm="http://ns.adobe.com/hdr-gain-map/1.0/" hdrgm:Version="1.0">
                  <Container:Directory>
                    <rdf:Seq>
                      <rdf:li rdf:parseType="Resource">
                        <Container:Item Item:Semantic="Primary" Item:Mime="image/jpeg"/>
                      </rdf:li>
                      <rdf:li rdf:parseType="Resource">
                        <Container:Item Item:Semantic="GainMap" Item:Mime="image/jpeg" Item:Length="{gainMapLength.ToString(CultureInfo.InvariantCulture)}"/>
                      </rdf:li>
                    </rdf:Seq>
                  </Container:Directory>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            """;
        return WrapApp1(packet);
    }

    /// <summary>
    /// Builds the gain-map image's complete APP1 segment carrying the hdrgm
    /// parameters. Boost and capacity ratios are converted to log2 for the wire
    /// (the hdrgm convention); gamma and offsets are written linear.
    /// </summary>
    public static byte[] WriteGainMapApp1(GainMapMetadata metadata)
    {
        metadata.Validate();
        var packet = $"""
            <x:xmpmeta xmlns:x="adobe:ns:meta/" x:xmptk="Adobe XMP Core 5.1.2">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about="" xmlns:hdrgm="http://ns.adobe.com/hdr-gain-map/1.0/" hdrgm:Version="1.0" hdrgm:GainMapMin="{Log2Str(metadata.GainMapMin)}" hdrgm:GainMapMax="{Log2Str(metadata.GainMapMax)}" hdrgm:Gamma="{Str(metadata.Gamma)}" hdrgm:OffsetSDR="{Str(metadata.OffsetSdr)}" hdrgm:OffsetHDR="{Str(metadata.OffsetHdr)}" hdrgm:HDRCapacityMin="{Log2Str(metadata.HdrCapacityMin)}" hdrgm:HDRCapacityMax="{Log2Str(metadata.HdrCapacityMax)}" hdrgm:BaseRenditionIsHDR="{(metadata.BaseRenditionIsHdr ? "True" : "False")}"/>
              </rdf:RDF>
            </x:xmpmeta>
            """;
        return WrapApp1(packet);
    }

    /// <summary>
    /// Extracts <c>hdrgm</c> gain-map parameters from an XMP packet (the payload
    /// after <see cref="App1Identifier"/>). Follows libultrahdr's contract:
    /// <c>Version</c>, <c>GainMapMax</c> and <c>HDRCapacityMax</c> are required;
    /// everything else takes the spec default. Handles both the attribute form
    /// (what ACR/libultrahdr write) and the expanded element form.
    /// </summary>
    public static bool TryParseGainMapMetadata(ReadOnlySpan<byte> xmpPacket, [NotNullWhen(true)] out GainMapMetadata? metadata)
    {
        metadata = null;
        if (!TryLoadPacket(xmpPacket, out var doc))
            return false;

        foreach (var description in doc!.Descendants(Rdf + "Description"))
        {
            if (GetValue(description, Hdrgm, "Version") is null)
                continue;
            var gainMapMax = GetValue(description, Hdrgm, "GainMapMax");
            var capacityMax = GetValue(description, Hdrgm, "HDRCapacityMax");
            if (gainMapMax is null || capacityMax is null)
                continue;
            if (!TryLog2Value(gainMapMax, out var max) || !TryLog2Value(capacityMax, out var capMax))
                return false;

            double gainMapMin = 1.0, gamma = 1.0, offsetSdr = 1.0 / 64, offsetHdr = 1.0 / 64, capMin = 1.0;
            if (GetValue(description, Hdrgm, "GainMapMin") is { } minText && !TryLog2Value(minText, out gainMapMin))
                return false;
            if (GetValue(description, Hdrgm, "HDRCapacityMin") is { } capMinText && !TryLog2Value(capMinText, out capMin))
                return false;
            if (GetValue(description, Hdrgm, "Gamma") is { } gammaText && !TryLinearValue(gammaText, out gamma))
                return false;
            if (GetValue(description, Hdrgm, "OffsetSDR") is { } offSdrText && !TryLinearValue(offSdrText, out offsetSdr))
                return false;
            if (GetValue(description, Hdrgm, "OffsetHDR") is { } offHdrText && !TryLinearValue(offHdrText, out offsetHdr))
                return false;

            metadata = new GainMapMetadata
            {
                GainMapMin = gainMapMin,
                GainMapMax = max,
                Gamma = gamma,
                OffsetSdr = offsetSdr,
                OffsetHdr = offsetHdr,
                HdrCapacityMin = capMin,
                HdrCapacityMax = capMax,
                BaseRenditionIsHdr = string.Equals(GetValue(description, Hdrgm, "BaseRenditionIsHDR"), "True", StringComparison.OrdinalIgnoreCase),
            };
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the GContainer directory of a primary image's XMP packet and returns
    /// the declared <c>Item:Length</c> of the GainMap item — the byte count of the
    /// gain-map JPEG appended after the base image.
    /// </summary>
    public static bool TryParseContainerGainMapLength(ReadOnlySpan<byte> xmpPacket, out int gainMapLength)
    {
        gainMapLength = 0;
        if (!TryLoadPacket(xmpPacket, out var doc))
            return false;

        foreach (var item in doc!.Descendants(Container + "Item"))
        {
            var semantic = item.Attribute(Item + "Semantic")?.Value ?? item.Attribute("Semantic")?.Value;
            if (!string.Equals(semantic, "GainMap", StringComparison.OrdinalIgnoreCase))
                continue;
            var length = item.Attribute(Item + "Length")?.Value ?? item.Attribute("Length")?.Value;
            return length is not null
                && int.TryParse(length, NumberStyles.Integer, CultureInfo.InvariantCulture, out gainMapLength)
                && gainMapLength > 0;
        }

        return false;
    }

    private static byte[] WrapApp1(string packet)
    {
        var packetBytes = Encoding.UTF8.GetByteCount(packet);
        var segmentLength = 2 + App1Identifier.Length + packetBytes;
        if (segmentLength > 0xFFFF)
            throw new InvalidOperationException($"XMP packet too large for a single APP1 segment ({segmentLength} > 65535 bytes).");

        var segment = new byte[2 + segmentLength];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)segmentLength);
        App1Identifier.CopyTo(segment.AsSpan(4));
        Encoding.UTF8.GetBytes(packet, segment.AsSpan(4 + App1Identifier.Length));
        return segment;
    }

    private static bool TryLoadPacket(ReadOnlySpan<byte> xmpPacket, out XDocument? doc)
    {
        doc = null;
        try
        {
            doc = XDocument.Parse(Encoding.UTF8.GetString(xmpPacket));
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>Reads a property in attribute form, element form, or (per-channel
    /// element form) the first <c>rdf:li</c> of its <c>rdf:Seq</c> — we apply one
    /// scalar to all channels, the common case for every known writer.</summary>
    private static string? GetValue(XElement description, XNamespace ns, string name)
    {
        if (description.Attribute(ns + name) is { } attribute)
            return attribute.Value;
        if (description.Element(ns + name) is { } element)
        {
            if (element.Element(Rdf + "Seq")?.Element(Rdf + "li") is { } li)
                return li.Value.Trim();
            return element.Value.Trim();
        }
        return null;
    }

    private static string Str(double linear) => linear.ToString("R", CultureInfo.InvariantCulture);

    private static string Log2Str(double linear) => Str(Math.Log2(linear));

    private static bool TryLinearValue(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryLog2Value(string text, out double linear)
    {
        if (!TryLinearValue(text, out var log2))
        {
            linear = 0;
            return false;
        }
        linear = Math.Pow(2, log2);
        return true;
    }
}
