using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.IsoBmff;

// WebVTT in ISO base media files: the 'wvtt' sample entry and the cue boxes of its samples. ISO/IEC 14496-30 defines them and is not
// available to this project; the layout here is what files written by MP4Box and read by other tools show (box types vttC, vlab, vttc,
// vtte, vtta, iden, sttg, payl, ctim) with every payload a UTF-8 string without terminator, and it is documented in docs/text-tracks.md
// together with what could not be settled.

/// <summary>The sample entry of a WebVTT track, <c>wvtt</c>: the file header text (<c>vttC</c>) and an optional source label (<c>vlab</c>).</summary>
public sealed class WebVttSampleEntry
{
    /// <summary>The sample entry code.</summary>
    public const string Code = "wvtt";

    /// <summary>The WebVTT file body up to the first cue: the <c>WEBVTT</c> line and any STYLE or REGION blocks, as text. Empty when the entry has no <c>vttC</c>.</summary>
    public string HeaderText { get; init; } = "WEBVTT";

    /// <summary>The <c>vlab</c> source label, or null when the entry has none.</summary>
    public string? SourceLabel { get; init; }

    /// <summary>The entry's other child boxes (<c>btrt</c>, unknown), in file order, as <c>size, type, payload</c> bytes.</summary>
    public ReadOnlyMemory<byte> OtherBoxes { get; init; }

    /// <summary>Reads the entry from the bytes after the 8-byte sample entry header.</summary>
    /// <param name="configuration">The entry's child boxes.</param>
    /// <param name="entry">The entry.</param>
    /// <returns>Always true: an entry without a <c>vttC</c> box reads as the header <c>WEBVTT</c>. A box that does not fit ends the list, and the rest is kept in <see cref="OtherBoxes"/>.</returns>
    public static bool TryParse(ReadOnlySpan<byte> configuration, out WebVttSampleEntry? entry)
    {
        string header = "WEBVTT";
        string? label = null;
        bool haveHeader = false;
        using MemoryStream others = new();
        int at = 0;
        while (at < configuration.Length)
        {
            if (!TimedTextSampleEntry.TryReadBox(configuration, at, out string type, out ReadOnlySpan<byte> payload, out int total))
            {
                others.Write(configuration[at..]);
                break;
            }

            if (type == "vttC" && !haveHeader)
            {
                header = Encoding.UTF8.GetString(payload);
                haveHeader = true;
            }
            else if (type == "vlab" && label is null)
            {
                label = Encoding.UTF8.GetString(payload);
            }
            else
            {
                others.Write(configuration.Slice(at, total));
            }

            at += total;
        }

        entry = new WebVttSampleEntry { HeaderText = header, SourceLabel = label, OtherBoxes = others.ToArray() };
        return true;
    }

    /// <summary>Writes the entry's child boxes: <c>vttC</c>, then <c>vlab</c> when there is a label, then <see cref="OtherBoxes"/>.</summary>
    /// <returns>The bytes after the 8-byte sample entry header.</returns>
    public byte[] ToConfiguration()
    {
        using MemoryStream m = new();
        WebVttBoxes.WriteString(m, "vttC", HeaderText);
        if (SourceLabel is not null)
        {
            WebVttBoxes.WriteString(m, "vlab", SourceLabel);
        }

        m.Write(OtherBoxes.Span);
        return m.ToArray();
    }
}

/// <summary>An item of a WebVTT sample: a cue, an empty-cue marker, additional text or an unknown box.</summary>
public abstract record WebVttSampleItem;

/// <summary>A <c>vttc</c> box: one cue active during the sample.</summary>
/// <param name="Payload">The cue text (<c>payl</c>), with WebVTT markup and line breaks as <c>\n</c>.</param>
public sealed record WebVttCueBox(string Payload) : WebVttSampleItem
{
    /// <summary>The cue settings (<c>sttg</c>), for example <c>line:10% align:center</c>; null when absent.</summary>
    public string? Settings { get; init; }

    /// <summary>The cue identifier (<c>iden</c>); null when absent.</summary>
    public string? Identifier { get; init; }

    /// <summary>The cue's original start time as WebVTT text (<c>ctim</c>), present when a cue continues from an earlier sample; null when absent.</summary>
    public string? CueTime { get; init; }

    /// <summary>The cue source identifier (<c>vsid</c>), or null when absent.</summary>
    public uint? SourceId { get; init; }

    /// <summary>Other child boxes of the cue box as <c>size, type, payload</c> bytes, kept for writing back.</summary>
    public ReadOnlyMemory<byte> OtherBoxes { get; init; }
}

/// <summary>A <c>vtte</c> box: the sample is a gap in which no cue is active.</summary>
public sealed record WebVttEmptyCue : WebVttSampleItem;

/// <summary>A <c>vtta</c> box: text that is not a cue, such as a NOTE block that precedes the next cue.</summary>
/// <param name="Text">The text.</param>
public sealed record WebVttAdditionalText(string Text) : WebVttSampleItem;

/// <summary>A box of a type this layer does not define, kept as stored.</summary>
/// <param name="Type">The four-character type.</param>
/// <param name="Payload">The content after the 8-byte header.</param>
public sealed record WebVttUnknownBox(string Type, ReadOnlyMemory<byte> Payload) : WebVttSampleItem;

/// <summary>One sample of a WebVTT track: the boxes in it, in order.</summary>
public sealed class WebVttSample
{
    /// <summary>Creates a sample.</summary>
    /// <param name="items">The items in order.</param>
    public WebVttSample(IReadOnlyList<WebVttSampleItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items;
    }

    /// <summary>The boxes in file order.</summary>
    public IReadOnlyList<WebVttSampleItem> Items { get; }

    /// <summary>Bytes after the last well-formed box (a box that did not fit), kept as stored.</summary>
    public ReadOnlyMemory<byte> Trailing { get; init; }

    /// <summary>True when the sample holds no cue (it holds <c>vtte</c> or nothing).</summary>
    public bool HasNoCues
    {
        get
        {
            foreach (WebVttSampleItem i in Items)
            {
                if (i is WebVttCueBox)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Reads a sample.</summary>
    /// <param name="sample">The sample's bytes.</param>
    /// <param name="result">The sample.</param>
    /// <returns>Always true: a sample that is empty or starts with a bad box reads as no items with the bytes in <see cref="Trailing"/>.</returns>
    public static bool TryParse(ReadOnlySpan<byte> sample, out WebVttSample? result)
    {
        List<WebVttSampleItem> items = [];
        int at = 0;
        ReadOnlyMemory<byte> trailing = default;
        while (at < sample.Length)
        {
            if (!TimedTextSampleEntry.TryReadBox(sample, at, out string type, out ReadOnlySpan<byte> payload, out int total))
            {
                trailing = sample[at..].ToArray();
                break;
            }

            items.Add(type switch
            {
                "vttc" => ReadCue(payload),
                "vtte" => new WebVttEmptyCue(),
                "vtta" => new WebVttAdditionalText(Encoding.UTF8.GetString(payload)),
                _ => new WebVttUnknownBox(type, payload.ToArray()),
            });
            at += total;
        }

        result = new WebVttSample(items) { Trailing = trailing };
        return true;
    }

    /// <summary>Writes the sample.</summary>
    /// <returns>The boxes in order, then <see cref="Trailing"/>. A cue box writes <c>vsid</c>, <c>ctim</c>, <c>iden</c>, <c>sttg</c>, <c>payl</c> (those present), then its other boxes.</returns>
    public byte[] ToArray()
    {
        using MemoryStream m = new();
        foreach (WebVttSampleItem item in Items)
        {
            switch (item)
            {
                case WebVttCueBox cue:
                    {
                        using MemoryStream inner = new();
                        if (cue.SourceId is { } vsid)
                        {
                            Span<byte> id = stackalloc byte[4]; // fixed 4-byte field
                            BinaryPrimitives.WriteUInt32BigEndian(id, vsid);
                            WebVttBoxes.WriteBox(inner, "vsid", id);
                        }

                        if (cue.CueTime is not null)
                        {
                            WebVttBoxes.WriteString(inner, "ctim", cue.CueTime);
                        }

                        if (cue.Identifier is not null)
                        {
                            WebVttBoxes.WriteString(inner, "iden", cue.Identifier);
                        }

                        if (cue.Settings is not null)
                        {
                            WebVttBoxes.WriteString(inner, "sttg", cue.Settings);
                        }

                        WebVttBoxes.WriteString(inner, "payl", cue.Payload);
                        inner.Write(cue.OtherBoxes.Span);
                        WebVttBoxes.WriteBox(m, "vttc", inner.GetBuffer().AsSpan(0, (int)inner.Length));
                        break;
                    }

                case WebVttEmptyCue:
                    WebVttBoxes.WriteBox(m, "vtte", []);
                    break;
                case WebVttAdditionalText extra:
                    WebVttBoxes.WriteString(m, "vtta", extra.Text);
                    break;
                case WebVttUnknownBox unknown:
                    WebVttBoxes.WriteBox(m, unknown.Type, unknown.Payload.Span);
                    break;
                default:
                    break;
            }
        }

        m.Write(Trailing.Span);
        return m.ToArray();
    }

    private static WebVttCueBox ReadCue(ReadOnlySpan<byte> content)
    {
        string payload = string.Empty;
        bool havePayload = false;
        string? settings = null, identifier = null, cueTime = null;
        uint? sourceId = null;
        using MemoryStream others = new();
        int at = 0;
        while (at < content.Length)
        {
            if (!TimedTextSampleEntry.TryReadBox(content, at, out string type, out ReadOnlySpan<byte> p, out int total))
            {
                others.Write(content[at..]);
                break;
            }

            if (type == "payl" && !havePayload)
            {
                payload = Encoding.UTF8.GetString(p);
                havePayload = true;
            }
            else if (type == "sttg" && settings is null)
            {
                settings = Encoding.UTF8.GetString(p);
            }
            else if (type == "iden" && identifier is null)
            {
                identifier = Encoding.UTF8.GetString(p);
            }
            else if (type == "ctim" && cueTime is null)
            {
                cueTime = Encoding.UTF8.GetString(p);
            }
            else if (type == "vsid" && sourceId is null && p.Length >= 4)
            {
                sourceId = BinaryPrimitives.ReadUInt32BigEndian(p);
            }
            else
            {
                others.Write(content.Slice(at, total));
            }

            at += total;
        }

        return new WebVttCueBox(payload) { Settings = settings, Identifier = identifier, CueTime = cueTime, SourceId = sourceId, OtherBoxes = others.ToArray() };
    }
}

internal static class WebVttBoxes
{
    public static void WriteBox(MemoryStream m, string type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8]; // box header
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(8 + payload.Length));
        Encoding.Latin1.GetBytes(type, header[4..]);
        m.Write(header);
        m.Write(payload);
    }

    public static void WriteString(MemoryStream m, string type, string text) => WriteBox(m, type, Encoding.UTF8.GetBytes(text));
}

/// <summary>
/// The sample entry of an XML subtitle track, <c>stpp</c> (ISO/IEC 14496-12 §12.6 XMLSubtitleSampleEntry): the XML namespace, an
/// optional schema location and the MIME types of auxiliary files (images) the documents refer to, as null-terminated strings.
/// </summary>
public sealed class XmlSubtitleSampleEntry
{
    /// <summary>The sample entry code.</summary>
    public const string Code = "stpp";

    /// <summary>The namespace of the documents, for TTML <c>http://www.w3.org/ns/ttml</c>. Default that value.</summary>
    public string Namespace { get; init; } = "http://www.w3.org/ns/ttml";

    /// <summary>Schema locations; empty for none.</summary>
    public string SchemaLocation { get; init; } = string.Empty;

    /// <summary>MIME types of auxiliary files, such as <c>image/png</c>; empty for none.</summary>
    public string AuxiliaryMimeTypes { get; init; } = string.Empty;

    /// <summary>The entry's child boxes (<c>btrt</c> and others) as <c>size, type, payload</c> bytes.</summary>
    public ReadOnlyMemory<byte> OtherBoxes { get; init; }

    /// <summary>Reads the entry from the bytes after the 8-byte sample entry header.</summary>
    /// <param name="configuration">The entry's strings and child boxes.</param>
    /// <param name="entry">The entry.</param>
    /// <returns>False when the namespace string has no terminator. A missing schema location or MIME list reads as empty.</returns>
    public static bool TryParse(ReadOnlySpan<byte> configuration, out XmlSubtitleSampleEntry? entry)
    {
        entry = null;
        int at = 0;
        if (!TryReadString(configuration, ref at, out string ns))
        {
            return false;
        }

        _ = TryReadString(configuration, ref at, out string schema);
        _ = TryReadString(configuration, ref at, out string mimes);
        entry = new XmlSubtitleSampleEntry { Namespace = ns, SchemaLocation = schema, AuxiliaryMimeTypes = mimes, OtherBoxes = configuration[Math.Min(at, configuration.Length)..].ToArray() };
        return true;
    }

    /// <summary>Writes the entry (without the 8-byte sample entry header): three null-terminated strings, then <see cref="OtherBoxes"/>.</summary>
    /// <returns>The bytes.</returns>
    public byte[] ToConfiguration()
    {
        using MemoryStream m = new();
        foreach (string s in new[] { Namespace, SchemaLocation, AuxiliaryMimeTypes })
        {
            m.Write(Encoding.UTF8.GetBytes(s));
            m.WriteByte(0);
        }

        m.Write(OtherBoxes.Span);
        return m.ToArray();
    }

    private static bool TryReadString(ReadOnlySpan<byte> data, ref int at, out string value)
    {
        value = string.Empty;
        if (at >= data.Length)
        {
            return false;
        }

        int end = data[at..].IndexOf((byte)0);
        if (end < 0)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(data.Slice(at, end));
        at += end + 1;
        return true;
    }
}
