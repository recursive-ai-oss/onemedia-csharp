using System.Xml;

namespace RecursiveAI.OneMedia.Tags.Xmp;

/// <summary>One simple XMP property: the namespace, the name and its value, or its items for an array.</summary>
/// <param name="Namespace">The namespace URI (for example <c>http://ns.adobe.com/xap/1.0/</c>).</param>
/// <param name="Name">The local name (for example <c>CreateDate</c>).</param>
/// <param name="Values">One value for a simple property; the items for <c>rdf:Seq</c>, <c>rdf:Bag</c> and <c>rdf:Alt</c> (a language alternative lists its <c>x-default</c> item first).</param>
public sealed record XmpProperty(string Namespace, string Name, IReadOnlyList<string> Values)
{
    /// <summary>The first value.</summary>
    public string Value => Values.Count > 0 ? Values[0] : string.Empty;

    /// <summary>The property as <c>namespace#name</c>, the key used by <see cref="NativeTag.Key"/>.</summary>
    public string Key => Namespace + "#" + Name;
}

/// <summary>Namespace URIs of the XMP schemas the basic facts read.</summary>
public static class XmpNamespaces
{
    /// <summary>The XMP basic schema (<c>xmp:</c>).</summary>
    public const string Xmp = "http://ns.adobe.com/xap/1.0/";

    /// <summary>The Exif schema (<c>exif:</c>).</summary>
    public const string Exif = "http://ns.adobe.com/exif/1.0/";

    /// <summary>The TIFF schema (<c>tiff:</c>).</summary>
    public const string Tiff = "http://ns.adobe.com/tiff/1.0/";

    /// <summary>The Photoshop schema (<c>photoshop:</c>).</summary>
    public const string Photoshop = "http://ns.adobe.com/photoshop/1.0/";

    /// <summary>Dublin Core (<c>dc:</c>).</summary>
    public const string DublinCore = "http://purl.org/dc/elements/1.1/";

    /// <summary>The RDF syntax namespace.</summary>
    public const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    /// <summary>The user-type GUID of the <c>uuid</c> box that carries an XMP packet.</summary>
    public static readonly Guid BoxUserType = new("BE7ACFCB-97A9-42E8-9C71-999491E3AFAC");
}

/// <summary>
/// An XMP packet found in the file, in a <c>uuid</c> box with the XMP user type or an <c>XMP_</c> box. The packet is exposed as its
/// raw byte range; the simple properties (attributes and text elements of <c>rdf:Description</c>, and array items) are read for the basic
/// facts. Structured values are not modelled and are skipped. The XML is read with DTDs prohibited and no external resolution.
/// </summary>
public sealed class XmpPacket : ITagContainer
{
    private XmpPacket(ByteRange range, ByteRange packet, long trackId, IReadOnlyList<XmpProperty> properties, IReadOnlyList<NativeTag> tags)
    {
        Range = range;
        PacketRange = packet;
        TrackId = trackId;
        Properties = properties;
        Tags = tags;
    }

    /// <inheritdoc />
    public string System => TagSystems.Xmp;

    /// <inheritdoc />
    public long TrackId { get; }

    /// <summary>The box that holds the packet, header included.</summary>
    public ByteRange Range { get; }

    /// <summary>The XML bytes alone, including any <c>xpacket</c> wrapper and padding. Read them with <see cref="ReadPacket"/>.</summary>
    public ByteRange PacketRange { get; }

    /// <summary>The simple properties in document order.</summary>
    public IReadOnlyList<XmpProperty> Properties { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }

    /// <summary>Finds a property.</summary>
    /// <param name="ns">The namespace URI.</param>
    /// <param name="name">The local name.</param>
    /// <returns>The first property with that name, or <c>null</c>.</returns>
    public XmpProperty? Find(string ns, string name)
    {
        foreach (XmpProperty p in Properties)
        {
            if (p.Namespace == ns && p.Name == name)
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>Reads the packet bytes.</summary>
    /// <param name="source">The source the packet is in.</param>
    /// <returns>The bytes.</returns>
    public byte[] ReadPacket(IMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] bytes = new byte[PacketRange.Length];
        int n = source.Read(PacketRange.Offset, bytes);
        return n == bytes.Length ? bytes : bytes.AsSpan(0, n).ToArray();
    }

    /// <summary>
    /// Reads and parses a packet. Malformed XML (<c>tags.xmp-malformed</c>), a packet over the tag limit (<c>tags.value-too-large</c>) or too many
    /// properties (<c>tags.xmp-limit</c>) are defects; the properties read so far are kept.
    /// </summary>
    /// <param name="source">The source the packet is in.</param>
    /// <param name="boxRange">The box that holds the packet, header included.</param>
    /// <param name="packet">The XML bytes inside the box.</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="defects">Receives defects.</param>
    /// <param name="trackId">The track the box belongs to (0 for the movie or file).</param>
    /// <returns>The packet.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static XmpPacket Read(IMediaSource source, ByteRange boxRange, ByteRange packet, MediaLimits limits, DefectLog defects, long trackId = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        TagIo io = new(source, limits, defects);
        List<XmpProperty> props = [];
        if (packet.Length > limits.MaxTagBytes)
        {
            defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, packet.Offset, "The XMP packet is larger than the tag limit and is not parsed.", "tags.value-too-large");
        }
        else
        {
            byte[] bytes = io.Read(packet);
            Parse(bytes, packet.Offset, limits, defects, props);
        }

        List<NativeTag> tags = [];
        foreach (XmpProperty p in props)
        {
            NativeTag tag = new(io, TagSystems.Xmp, p.Key, trackId, TagValueKind.Text, 1, 0, null, boxRange, packet);
            tag.SetText(p.Value);
            tags.Add(tag);
        }

        return new XmpPacket(boxRange, packet, trackId, props, tags);
    }

    private static void Parse(byte[] bytes, long offset, MediaLimits limits, DefectLog defects, List<XmpProperty> into)
    {
        int end = bytes.Length;
        while (end > 0 && (bytes[end - 1] is 0 or (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t'))
        {
            end--;
        }

        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = limits.MaxTagBytes,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
        };
        const int MaxProperties = 20_000;
        try
        {
            using MemoryStream stream = new(bytes, 0, end, writable: false);
            using XmlReader reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == XmpNamespaces.Rdf && reader.LocalName == "Description")
                {
                    ReadDescription(reader, into, MaxProperties);
                    if (into.Count >= MaxProperties)
                    {
                        defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, offset, "The XMP packet has too many properties; the rest are skipped.", "tags.xmp-limit");
                        return;
                    }
                }
            }
        }
        catch (Exception e) when (e is XmlException or ArgumentException)
        {
            // XmlReader raises ArgumentException (not XmlException) from its encoding detection on some malformed declarations; found by fuzzing.
            defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, "The XMP packet is not well-formed XML: " + e.Message, "tags.xmp-malformed");
        }
    }

    // XMP specification part 1, RDF/XML subset: properties are attributes of rdf:Description or its child elements.
    private static void ReadDescription(XmlReader reader, List<XmpProperty> into, int max)
    {
        bool empty = reader.IsEmptyElement;
        int depth = reader.Depth;
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                string ns = reader.NamespaceURI;
                if (ns.Length == 0 || ns == XmpNamespaces.Rdf || ns == "http://www.w3.org/2000/xmlns/" || ns == "http://www.w3.org/XML/1998/namespace")
                {
                    continue;
                }

                into.Add(new XmpProperty(ns, reader.LocalName, [reader.Value]));
            }
            while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }

        if (empty)
        {
            return;
        }

        reader.Read();
        while (!(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) && !reader.EOF && into.Count < max)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                ReadProperty(reader, into);
            }
            else
            {
                reader.Read();
            }
        }
    }

    private static void ReadProperty(XmlReader reader, List<XmpProperty> into)
    {
        string ns = reader.NamespaceURI;
        string name = reader.LocalName;
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return;
        }

        int depth = reader.Depth;
        System.Text.StringBuilder text = new();
        List<string> items = [];
        bool structured = false;
        reader.Read();
        while (!(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) && !reader.EOF)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Text or XmlNodeType.CDATA:
                    text.Append(reader.Value);
                    reader.Read();
                    break;
                case XmlNodeType.Element when reader.NamespaceURI == XmpNamespaces.Rdf && reader.LocalName is "Seq" or "Bag" or "Alt":
                    ReadItems(reader, items);
                    break;
                case XmlNodeType.Element:
                    structured = true;
                    reader.Skip();
                    break;
                default:
                    reader.Read();
                    break;
            }
        }

        reader.Read();
        if (items.Count > 0)
        {
            into.Add(new XmpProperty(ns, name, items));
        }
        else if (!structured)
        {
            into.Add(new XmpProperty(ns, name, [text.ToString()]));
        }
    }

    private static void ReadItems(XmlReader reader, List<string> items)
    {
        bool empty = reader.IsEmptyElement;
        int depth = reader.Depth;
        string? defaultItem = null;
        List<string> others = [];
        reader.Read();
        if (empty)
        {
            return;
        }

        while ( !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) && !reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == XmpNamespaces.Rdf && reader.LocalName == "li")
            {
                bool isDefault = reader.GetAttribute("lang", "http://www.w3.org/XML/1998/namespace") == "x-default";
                bool emptyLi = reader.IsEmptyElement;
                int liDepth = reader.Depth;
                System.Text.StringBuilder sb = new();
                reader.Read();
                while (!emptyLi && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == liDepth) && !reader.EOF)
                {
                    if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                    {
                        sb.Append(reader.Value);
                        reader.Read();
                    }
                    else if (reader.NodeType == XmlNodeType.Element)
                    {
                        reader.Skip();
                    }
                    else
                    {
                        reader.Read();
                    }
                }

                if (!emptyLi)
                {
                    reader.Read();
                }

                if (isDefault)
                {
                    defaultItem = sb.ToString();
                }
                else
                {
                    others.Add(sb.ToString());
                }
            }
            else
            {
                reader.Skip();
            }
        }

        reader.Read();
        if (defaultItem is not null)
        {
            items.Add(defaultItem);
        }

        items.AddRange(others);
    }
}
