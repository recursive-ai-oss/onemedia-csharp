using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using RecursiveAI.OneMedia.Tags.Xmp;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>
/// Edits an XMP packet (XMP specification part 1 and 3: the <c>xpacket</c> wrapper, <c>x:xmpmeta</c>, RDF/XML in the serialised subset). The
/// common fields that XMP carries are written as properties of the first <c>rdf:Description</c>; every other property, namespace and
/// attribute of the packet is kept as it was. The packet keeps its length where the edit fits, filling the rest with the whitespace padding
/// the specification reserves for in-place growth, so that the box around it keeps its size.
/// </summary>
internal static class XmpEditor
{
    private static readonly XNamespace Rdf = XmpNamespaces.Rdf;
    private static readonly XNamespace Dc = XmpNamespaces.DublinCore;
    private static readonly XNamespace XmpBasic = XmpNamespaces.Xmp;
    private static readonly XNamespace XmlNs = "http://www.w3.org/XML/1998/namespace";
    private static readonly XNamespace Meta = "adobe:ns:meta/";
    private const string PacketId = "W5M0MpCehiHzreSzNTczkc9d";
    private const int NewPadding = 2048;

    private static readonly (CommonField Field, XNamespace Ns, string Name, string Kind)[] Fields =
    [
        (CommonField.Title, Dc, "title", "Alt"),
        (CommonField.Artist, Dc, "creator", "Seq"),
        (CommonField.Description, Dc, "description", "Alt"),
        (CommonField.Copyright, Dc, "rights", "Alt"),
        (CommonField.Keywords, Dc, "subject", "Bag"),
        (CommonField.Date, XmpBasic, "CreateDate", ""),
    ];

    /// <summary>Edits a packet, or builds a new one when <paramref name="existing"/> is null.</summary>
    /// <param name="existing">The packet bytes, or null.</param>
    /// <param name="edits">The edits.</param>
    /// <param name="limits">Limits for parsing.</param>
    /// <returns>The new packet bytes (same length as the old one when the edit fits) and the report.</returns>
    /// <exception cref="UnsupportedMediaException">The existing packet is not well-formed XML; it is not rewritten.</exception>
    public static (byte[] Packet, SystemWriteReport Report) Edit(byte[]? existing, TagEdits edits, MediaLimits limits)
    {
        XElement root;
        if (existing is null)
        {
            root = new XElement(Meta + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", Meta.NamespaceName), new XAttribute(Meta + "xmptk", "OneMedia"),
                new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName), new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""))));
        }
        else
        {
            root = Parse(existing, limits);
        }

        XElement rdf = root.Descendants(Rdf + "RDF").FirstOrDefault() ?? throw new UnsupportedMediaException("The XMP packet has no rdf:RDF element; it is not rewritten.");
        XElement desc = rdf.Elements(Rdf + "Description").FirstOrDefault() ?? AddDescription(rdf);
        List<CommonField> written = [];
        List<CommonField> removed = [];
        List<(CommonField, string)> not = [];
        List<string> conv = [];
        foreach (CommonField f in edits.TouchedFields)
        {
            IReadOnlyList<string>? change = edits.FieldChange(f);
            if (change is null)
            {
                continue;
            }

            int row = Array.FindIndex(Fields, x => x.Field == f);
            if (row < 0)
            {
                not.Add((f, "XMP's Dublin Core and basic properties have no field for " + f));
                continue;
            }

            (_, XNamespace ns, string name, string kind) = Fields[row];
            foreach (XElement d in rdf.Elements(Rdf + "Description"))
            {
                d.Attribute(ns + name)?.Remove();
                d.Elements(ns + name).Remove();
            }

            if (change.Count == 0)
            {
                removed.Add(f);
                continue;
            }

            written.Add(f);
            EnsurePrefix(desc, ns, ns == Dc ? "dc" : "xmp");
            if (kind.Length == 0)
            {
                desc.Add(new XElement(ns + name, change[0]));
                continue;
            }

            if (kind == "Alt")
            {
                if (change.Count > 1)
                {
                    conv.Add($"{f}: only the first of {change.Count} values is written (a language alternative holds one default text)");
                }

                desc.Add(new XElement(ns + name, new XElement(Rdf + "Alt", new XElement(Rdf + "li", new XAttribute(XmlNs + "lang", "x-default"), change[0]))));
                continue;
            }

            desc.Add(new XElement(ns + name, new XElement(Rdf + kind, change.Select(v => new XElement(Rdf + "li", v)))));
        }

        // Native edits name a property as prefix:Name (xmp:CreateDate, photoshop:DateCreated, exif:DateTimeOriginal); the property is set as a simple text value.
        foreach (TagEdit n in edits.NativeFor(TagSystems.Xmp))
        {
            int colon = n.Key!.IndexOf(':', StringComparison.Ordinal);
            XNamespace? nns = colon <= 0 ? null : n.Key[..colon] switch
            {
                "xmp" => XmpBasic,
                "photoshop" => (XNamespace)XmpNamespaces.Photoshop,
                "exif" => (XNamespace)XmpNamespaces.Exif,
                _ => null,
            };
            if (nns is null)
            {
                conv.Add($"{n.Key}: the XMP prefix is not one of xmp, photoshop or exif; not written");
                continue;
            }

            string local = n.Key[(colon + 1)..];
            foreach (XElement d in rdf.Elements(Rdf + "Description"))
            {
                d.Attribute(nns + local)?.Remove();
                d.Elements(nns + local).Remove();
            }

            if (n.Kind == TagEditKind.SetNative)
            {
                EnsurePrefix(desc, nns, n.Key[..colon]);
                desc.Add(new XElement(nns + local, n.Values[0]));
                conv.Add($"{n.Key}: set");
            }
        }

        byte[] body = Serialise(root);
        byte[] begin = Encoding.UTF8.GetBytes("<?xpacket begin=\"\uFEFF\" id=\"" + PacketId + "\"?>\n");
        byte[] end = Encoding.UTF8.GetBytes("\n<?xpacket end=\"w\"?>");
        long need = begin.Length + body.Length + end.Length;
        long total = existing is not null && need <= existing.Length ? existing.Length : need + NewPadding;
        byte[] packet = new byte[total];
        begin.CopyTo(packet, 0);
        body.CopyTo(packet, begin.Length);
        int pad = (int)(total - need);
        // The padding sits between the metadata and the end marker: spaces, with a newline every 100 bytes as the specification's examples do.
        for (int i = 0; i < pad; i++)
        {
            packet[begin.Length + body.Length + i] = (byte)(i % 101 == 100 ? '\n' : ' ');
        }

        end.CopyTo(packet, begin.Length + body.Length + pad);
        return (packet, new SystemWriteReport(TagSystems.Xmp, written, removed, not, conv, existing is null));
    }

    private static XElement AddDescription(XElement rdf)
    {
        XElement d = new(Rdf + "Description", new XAttribute(Rdf + "about", ""));
        rdf.Add(d);
        return d;
    }

    private static void EnsurePrefix(XElement element, XNamespace ns, string prefix)
    {
        if (element.GetPrefixOfNamespace(ns) is null)
        {
            element.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
        }
    }

    private static XElement Parse(byte[] bytes, MediaLimits limits)
    {
        limits.CheckTag(bytes.Length, 0);
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = limits.MaxTagBytes,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
        };
        int end = bytes.Length;
        while (end > 0 && bytes[end - 1] is 0 or (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t')
        {
            end--;
        }

        try
        {
            using MemoryStream stream = new(bytes, 0, end, writable: false);
            using XmlReader reader = XmlReader.Create(stream, settings);
            return XElement.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException e)
        {
            throw new UnsupportedMediaException("The XMP packet is not well-formed XML and is not rewritten: " + e.Message, e);
        }
    }

    private static byte[] Serialise(XElement root)
    {
        using MemoryStream ms = new();
        XmlWriterSettings settings = new() { OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false), Indent = false, ConformanceLevel = ConformanceLevel.Fragment };
        using (XmlWriter w = XmlWriter.Create(ms, settings))
        {
            root.WriteTo(w);
        }

        return ms.ToArray();
    }
}
