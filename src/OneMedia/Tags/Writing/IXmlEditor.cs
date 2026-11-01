using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>
/// Edits the <c>iXML</c> chunk of a Broadcast Wave file (the iXML specification: a <c>BWFXML</c> document). The common view has no iXML fields, so
/// only native edits apply: a key is the path of an element below the root, for example <c>PROJECT</c> or <c>BEXT/BWF_ORIGINATOR</c>. Every element the edit
/// does not name is kept as it was; a document that gets smaller is padded with whitespace to its old length so the chunk keeps its size.
/// </summary>
internal static class IXmlEditor
{
    /// <summary>Applies the native iXML edits to a document, or to a new one when <paramref name="existing"/> is null.</summary>
    /// <param name="existing">The chunk payload, or null.</param>
    /// <param name="edits">The edits; only the ones for <see cref="TagSystems.IXml"/> are used.</param>
    /// <param name="limits">Limits.</param>
    /// <returns>The new payload and the report (iXML has no common fields, so the report lists conversions only).</returns>
    /// <exception cref="UnsupportedMediaException">The existing document is not well-formed; it is not rewritten.</exception>
    public static (byte[] Payload, SystemWriteReport Report) Edit(byte[]? existing, TagEdits edits, MediaLimits limits)
    {
        XElement root;
        List<string> conv = [];
        if (existing is null)
        {
            root = new XElement("BWFXML", new XElement("IXML_VERSION", "1.5"));
        }
        else
        {
            root = Parse(existing, limits, conv);
        }

        foreach (TagEdit e in edits.NativeFor(TagSystems.IXml))
        {
            string[] path = e.Key!.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.Length == 0 || path.Any(p => !IsName(p)))
            {
                throw new ArgumentException($"'{e.Key}' is not an element path for iXML (names separated by '/').");
            }

            if (e.Kind == TagEditKind.RemoveNative)
            {
                XElement? at = Find(root, path, create: false);
                at?.Remove();
                continue;
            }

            XElement target = Find(root, path, create: true)!;
            if (e.Values.Count > 1)
            {
                conv.Add($"{e.Key}: {e.Values.Count} values joined with '; ' (an iXML element holds one text)");
            }

            target.RemoveNodes();
            target.Value = string.Join("; ", e.Values);
        }

        using MemoryStream ms = new();
        XmlWriterSettings settings = new() { Encoding = new UTF8Encoding(false), Indent = false, OmitXmlDeclaration = false };
        using (XmlWriter w = XmlWriter.Create(ms, settings))
        {
            new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(w);
        }

        byte[] bytes = ms.ToArray();
        if (existing is not null && bytes.Length < existing.Length)
        {
            byte[] padded = new byte[existing.Length];
            bytes.CopyTo(padded, 0);
            padded.AsSpan(bytes.Length).Fill((byte)' ');
            bytes = padded;
        }

        return (bytes, new SystemWriteReport(TagSystems.IXml, [], [], [], conv, existing is null));
    }

    private static bool IsName(string name)
    {
        try
        {
            _ = XmlConvert.VerifyNCName(name);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static XElement? Find(XElement root, string[] path, bool create)
    {
        XElement at = root;
        foreach (string name in path)
        {
            XElement? next = at.Element(name);
            if (next is null)
            {
                if (!create)
                {
                    return null;
                }

                next = new XElement(name);
                at.Add(next);
            }

            at = next;
        }

        return at;
    }

    private static XElement Parse(byte[] bytes, MediaLimits limits, List<string> conv)
    {
        int end = bytes.Length;
        while (end > 0 && bytes[end - 1] is 0 or (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t')
        {
            end--;
        }

        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = limits.MaxTagBytes, IgnoreComments = false };
        try
        {
            using MemoryStream stream = new(bytes, 0, end, writable: false);
            using XmlReader reader = XmlReader.Create(stream, settings);
            XDocument doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            string? declared = doc.Declaration?.Encoding;
            if (declared is not null && !declared.Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
            {
                conv.Add($"the document declared {declared}; it is written as UTF-8");
            }

            return doc.Root ?? throw new UnsupportedMediaException("The iXML chunk has no root element; it is not rewritten.");
        }
        catch (XmlException e)
        {
            throw new UnsupportedMediaException("The iXML chunk is not well-formed XML and is not rewritten: " + e.Message, e);
        }
    }
}
