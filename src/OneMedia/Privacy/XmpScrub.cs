using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RecursiveAI.OneMedia.Privacy;

/// <summary>
/// Removes location and device-identifier properties from an XMP packet (XMP specification part 1, serialisation as an <c>xpacket</c>-wrapped RDF/XML
/// document). The properties are taken out wherever they sit (attributes of <c>rdf:Description</c> or child elements, at any depth), the rest of the
/// document is kept, and the packet is padded with white space before its closing <c>xpacket</c> instruction to the length it had, so the box that
/// holds it keeps its size.
/// </summary>
internal static class XmpScrub
{
    /// <summary>One property that was removed.</summary>
    public readonly record struct Removed(StripCategories Category, string Key, int Bytes);

    /// <summary>Scrubs a packet.</summary>
    /// <param name="packet">The packet bytes exactly as stored.</param>
    /// <param name="cats">The categories to strip.</param>
    /// <param name="removed">The properties removed.</param>
    /// <param name="error">When the result is null and something should have changed: why it could not.</param>
    /// <returns>The new packet, the same length as the old; null when nothing needed removing or the packet could not be rewritten.</returns>
    public static byte[]? Scrub(byte[] packet, StripCategories cats, List<Removed> removed, out string? error)
    {
        error = null;
        string text = Encoding.UTF8.GetString(packet);
        // Cheap test before the parse: only the names the rules look at can lead to a removal.
        bool maybe = false;
        foreach (string w in new[] { "gps", "latitude", "longitude", "location", "serial", "uniqueid", "owner", "city", "state", "country", "udid", "imei", "deviceid", "cameraid", "camera_id", "geo", "coordinates" })
        {
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase))
            {
                maybe = true;
                break;
            }
        }

        if (!maybe)
        {
            return null;
        }

        XDocument doc;
        try
        {
            string body = text.TrimStart('\uFEFF');
            doc = XDocument.Parse(body, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException e)
        {
            error = "The XMP packet is not well-formed XML (" + e.Message + ").";
            return null;
        }

        if (doc.Root is null)
        {
            return null;
        }

        int before = removed.Count;
        Prune(doc.Root, cats, removed, 0);
        if (removed.Count == before)
        {
            return null;
        }

        string xml = doc.ToString(SaveOptions.DisableFormatting);
        int endPi = xml.LastIndexOf("<?xpacket end", StringComparison.Ordinal);
        byte[] bytes = Encoding.UTF8.GetBytes(xml);
        if (bytes.Length > packet.Length)
        {
            removed.RemoveRange(before, removed.Count - before);
            error = "The rewritten XMP packet is longer than the original.";
            return null;
        }

        byte[] output = new byte[packet.Length];
        int pad = packet.Length - bytes.Length;
        int cut = endPi >= 0 ? Encoding.UTF8.GetByteCount(xml.AsSpan(0, endPi)) : bytes.Length;
        Array.Copy(bytes, 0, output, 0, cut);
        Array.Fill(output, (byte)' ', cut, pad);
        Array.Copy(bytes, cut, output, cut + pad, bytes.Length - cut);
        if (packet.Length >= 3 && packet[0] == 0xEF && packet[1] == 0xBB && packet[2] == 0xBF)
        {
            // The BOM is not part of the document text; keep it where it was.
            byte[] shifted = new byte[packet.Length];
            shifted[0] = 0xEF;
            shifted[1] = 0xBB;
            shifted[2] = 0xBF;
            int usable = packet.Length - 3;
            Array.Copy(output, 0, shifted, 3, usable);
            if (output.AsSpan(usable).IndexOfAnyExcept((byte)' ') >= 0)
            {
                removed.RemoveRange(before, removed.Count - before);
                error = "The rewritten XMP packet does not fit beside its byte-order mark.";
                return null;
            }

            output = shifted;
        }

        return output;
    }

    private static void Prune(XElement element, StripCategories cats, List<Removed> removed, int depth)
    {
        if (depth > 64)
        {
            return;
        }

        foreach (XAttribute a in element.Attributes().ToList())
        {
            if (a.IsNamespaceDeclaration)
            {
                continue;
            }

            StripCategories c = StripRules.ClassifyXmp(a.Name.NamespaceName, a.Name.LocalName) & cats;
            if (c != 0)
            {
                removed.Add(new Removed(c, a.Name.NamespaceName + "#" + a.Name.LocalName, Encoding.UTF8.GetByteCount(a.ToString())));
                a.Remove();
            }
        }

        foreach (XElement child in element.Elements().ToList())
        {
            StripCategories c = StripRules.ClassifyXmp(child.Name.NamespaceName, child.Name.LocalName) & cats;
            if (c != 0 && child.Name.NamespaceName != "http://www.w3.org/1999/02/22-rdf-syntax-ns#")
            {
                removed.Add(new Removed(c, child.Name.NamespaceName + "#" + child.Name.LocalName, Encoding.UTF8.GetByteCount(child.ToString(SaveOptions.DisableFormatting))));
                child.Remove();
            }
            else
            {
                Prune(child, cats, removed, depth + 1);
            }
        }
    }
}
