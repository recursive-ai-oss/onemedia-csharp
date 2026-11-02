using System.Linq;
using System.Text;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>
/// Plans tag edits in RIFF and IFF files: the <c>LIST INFO</c> chunk and the Broadcast Wave <c>bext</c> chunk of WAVE and RF64, an <c>id3 </c>/<c>ID3 </c> chunk
/// in WAVE and AIFF, and the AIFF text chunks (<c>NAME</c>, <c>AUTH</c>, <c>(c) </c>, <c>ANNO</c>). A system that is not in the file is created only when named in
/// <see cref="TagWriteOptions.Systems"/>, except that a WAVE file with no tags gets <c>LIST INFO</c> and an AIFF file gets its text chunks.
/// </summary>
internal static class RiffTagWriter
{
    private static readonly string[] BextKeys = ["Description", "Originator", "OriginatorReference", "OriginationDate", "OriginationTime"];
    private static readonly (int Offset, int Length)[] BextSpans = [(0, 256), (256, 32), (288, 32), (320, 10), (330, 8)];

    public static TagWritePlan Plan(IMediaSource src, TagEdits edits, TagWriteOptions options, MediaLimits limits, CancellationToken ct)
    {
        ChunkFile cf = ChunkFile.TryOpen(src, limits, new DefectLog(limits)) ?? throw new MediaFormatException("Not a RIFF or IFF file.");
        ChunkEditor editor = new(cf, src, limits) { GrowthPadding = options.PaddingBytes };
        List<SystemWriteReport> reports = [];
        List<string> notes = [];
        ChunkLayout layout = cf.Layout;
        if (layout == ChunkLayout.Wave64)
        {
            throw new UnsupportedMediaException("Tag writing in Wave64 files is not supported.");
        }

        if (cf.FormType == "AVI ")
        {
            throw new UnsupportedMediaException("AVI is not an edit target: its indexes hold absolute offsets.");
        }

        bool any = false;
        if (layout == ChunkLayout.Iff)
        {
            if (options.Wants(TagSystems.Aiff))
            {
                reports.Add(EditAiff(cf, src, edits, editor, limits));
                any = true;
            }
        }
        else
        {
            ChunkNode? list = cf.Chunks.FirstOrDefault(c => c.Id == "LIST" && c.FormType == "INFO");
            if (options.Wants(TagSystems.RiffInfo) && (list is not null || options.Systems is not null || cf.Chunks.All(c => c.Id is not ("id3 " or "ID3 "))))
            {
                reports.Add(EditInfo(cf, list, src, edits, editor, limits));
                RemoveFromOtherInfoLists(cf, list, src, edits, editor, limits);
                any = true;
            }

            ChunkNode? bext = cf.Chunks.FirstOrDefault(c => c.Id == "bext");
            if (bext is not null && options.Wants(TagSystems.Bext))
            {
                SystemWriteReport? r = EditBext(bext, src, edits, editor, limits);
                if (r is not null)
                {
                    reports.Add(r);
                }
            }
        }

        ChunkNode? id3 = cf.Chunks.FirstOrDefault(c => c.Id is "id3 " or "ID3 ");
        if (id3 is not null && options.Wants(TagSystems.Id3Chunk) && Id3Tag_TryRead(src, id3, limits, out Id3.Id3Tag? tag))
        {
            (byte[] bytes, SystemWriteReport r, List<string> n) = Mp3TagWriter.BuildId3v2Public(src, tag, edits, options, limits, tag!.Range.Length);
            notes.AddRange(n);
            reports.Add(r with { System = TagSystems.Id3Chunk });
            byte[] chunk = ChunkBuilder.Create(layout, id3.Id, bytes);
            editor.Replace(id3, chunk);
            any = true;
        }
        else if (id3 is null && (options.Systems?.Contains(TagSystems.Id3Chunk) == true || (options.Systems is null && edits.PictureEdits.Any(e => e.Kind == TagEditKind.SetPicture))))
        {
            // Pictures live only in ID3: a chunk is created for them (with every field when asked for by name).
            TagEdits forId3 = options.Systems is null ? PicturesOnly(edits) : edits;
            (byte[] bytes, SystemWriteReport r, List<string> n) = Mp3TagWriter.BuildId3v2Public(src, null, forId3, options, limits, 0);
            notes.AddRange(n);
            reports.Add(r with { System = TagSystems.Id3Chunk, Created = true });
            editor.AppendChild(null, ChunkBuilder.Create(layout, layout == ChunkLayout.Iff ? "ID3 " : "id3 ", bytes));
            any = true;
        }

        if (layout != ChunkLayout.Iff && edits.NativeFor(TagSystems.IXml).Any() && options.Wants(TagSystems.IXml))
        {
            ChunkNode? ixml = cf.Chunks.FirstOrDefault(c => c.Id is "iXML" or "IXML");
            (byte[] doc, SystemWriteReport r) = IXmlEditor.Edit(ixml is null ? null : ReadPayload(src, ixml, limits), edits, limits);
            byte[] chunk = ChunkBuilder.Create(layout, ixml?.Id ?? "iXML", doc);
            if (ixml is null)
            {
                editor.AppendChild(null, chunk);
            }
            else
            {
                editor.Replace(ixml, chunk);
            }

            reports.Add(r);
            any = true;
        }

        if (!any && reports.Count == 0)
        {
            notes.Add("The file has no tag system this writer can edit.");
        }

        return new TagWritePlan(editor.Build(ct), new TagWriteReport(reports, notes));
    }

    private static byte[] ReadPayload(IMediaSource src, ChunkNode chunk, MediaLimits limits)
    {
        limits.CheckTag(chunk.PayloadSize, chunk.Offset);
        byte[] b = new byte[chunk.PayloadSize];
        src.ReadExactly(chunk.Payload.Offset, b);
        return b;
    }

    private static TagEdits PicturesOnly(TagEdits edits)
    {
        TagEdits only = new();
        foreach (TagEdit e in edits.PictureEdits)
        {
            if (e.Kind == TagEditKind.SetPicture)
            {
                only.SetPicture(e.PictureData, e.MimeType!, e.PictureRole ?? PictureType.FrontCover, e.Description);
            }
            else
            {
                only.RemovePictures(e.PictureRole);
            }
        }

        return only;
    }

    private static bool Id3Tag_TryRead(IMediaSource src, ChunkNode chunk, MediaLimits limits, out Id3.Id3Tag? tag) => Id3.Id3Tag.TryRead(src, chunk.Payload.Offset, out tag, limits);

    private static byte[] TextPayload(string s, bool nul) => nul ? [.. Encoding.UTF8.GetBytes(s), 0] : Encoding.UTF8.GetBytes(s);

    private static string TextOf(byte[] payload) => Encoding.UTF8.GetString(payload).TrimEnd('\0');

    // A file can hold more than one LIST INFO chunk; the edit above works on the first. A removal that must reach every copy (the privacy strip) also
    // takes the key out of the others.
    private static void RemoveFromOtherInfoLists(ChunkFile cf, ChunkNode? first, IMediaSource src, TagEdits edits, ChunkEditor editor, MediaLimits limits)
    {
        HashSet<string> keys = [.. edits.NativeFor(TagSystems.RiffInfo).Where(e => e.Kind == TagEditKind.RemoveNative && e.Everywhere && e.Key is { Length: 4 }).Select(e => e.Key!)];
        if (keys.Count == 0)
        {
            return;
        }

        foreach (ChunkNode other in cf.Chunks.Where(c => c.Id == "LIST" && c.FormType == "INFO" && !ReferenceEquals(c, first)))
        {
            List<(string Id, byte[] Payload)> items = [];
            bool changed = false;
            foreach (ChunkNode c in other.Chunks)
            {
                if (keys.Contains(c.Id))
                {
                    changed = true;
                    continue;
                }

                limits.CheckTag(c.PayloadSize, c.Offset);
                byte[] p = new byte[c.PayloadSize];
                src.ReadExactly(c.Payload.Offset, p);
                items.Add((c.Id, p));
            }

            if (!changed)
            {
                continue;
            }

            if (items.Count == 0)
            {
                editor.Remove(other);
            }
            else
            {
                editor.Replace(other, ChunkBuilder.CreateList(cf.Layout, "INFO", items.SelectMany(i => ChunkBuilder.Create(cf.Layout, i.Id, i.Payload)).ToArray()));
            }
        }
    }

    private static SystemWriteReport EditInfo(ChunkFile cf, ChunkNode? list, IMediaSource src, TagEdits edits, ChunkEditor editor, MediaLimits limits)
    {
        List<(string Id, byte[] Payload)> items = [];
        if (list is not null)
        {
            foreach (ChunkNode c in list.Chunks)
            {
                limits.CheckTag(c.PayloadSize, c.Offset);
                byte[] p = new byte[c.PayloadSize];
                src.ReadExactly(c.Payload.Offset, p);
                items.Add((c.Id, p));
            }
        }

        string? Existing(string key) => items.FirstOrDefault(i => i.Id == key) is { Payload: not null } m && m.Id == key ? TextOf(m.Payload) : null;
        List<KeyOp> ops = [];
        SystemWriteReport rep = KeyValueOps.Build(TagSystems.RiffInfo, edits, Existing, f => f == CommonField.TrackNumber ? PairStyle.Combined : PairStyle.None, null, ops);
        foreach (KeyOp op in ops)
        {
            if (op.Key.Length != 4)
            {
                continue;
            }

            if (op.Values is null)
            {
                items.RemoveAll(i => i.Id == op.Key || op.Aliases.Contains(i.Id));
                continue;
            }

            int at = items.FindIndex(i => i.Id == op.Key);
            byte[] payload = TextPayload(string.Join("; ", op.Values), true);
            if (at >= 0)
            {
                items[at] = (op.Key, payload);
            }
            else
            {
                items.Add((op.Key, payload));
            }
        }

        List<string> conv = [.. rep.Conversions];
        if (edits.FieldChange(CommonField.Keywords) is { Count: > 1 })
        {
            conv.Add("Keywords: several values joined with '; '");
        }

        byte[] children = items.SelectMany(i => ChunkBuilder.Create(cf.Layout, i.Id, i.Payload)).ToArray();
        if (list is null && items.Count == 0)
        {
            return rep with { Conversions = conv, Created = false };
        }

        byte[] newList = ChunkBuilder.CreateList(cf.Layout, "INFO", children);
        if (list is not null)
        {
            if (items.Count == 0)
            {
                editor.Remove(list);
            }
            else
            {
                editor.Replace(list, newList);
            }
        }
        else
        {
            editor.AppendChild(null, newList);
        }

        return rep with { Conversions = conv, Created = list is null };
    }

    private static SystemWriteReport EditAiff(ChunkFile cf, IMediaSource src, TagEdits edits, ChunkEditor editor, MediaLimits limits)
    {
        Dictionary<string, ChunkNode> existing = [];
        foreach (ChunkNode c in cf.Chunks)
        {
            if (c.Id is "NAME" or "AUTH" or "(c) " or "ANNO")
            {
                existing.TryAdd(c.Id, c);
            }
        }

        string? Existing(string key)
        {
            if (!existing.TryGetValue(key, out ChunkNode? c))
            {
                return null;
            }

            byte[] p = new byte[c.PayloadSize];
            src.ReadExactly(c.Payload.Offset, p);
            return TextOf(p);
        }

        List<KeyOp> ops = [];
        SystemWriteReport rep = KeyValueOps.Build(TagSystems.Aiff, edits, Existing, _ => PairStyle.None, null, ops);
        foreach (KeyOp op in ops)
        {
            if (op.Key.Length != 4)
            {
                continue;
            }

            existing.TryGetValue(op.Key, out ChunkNode? node);
            if (op.Values is null)
            {
                if (node is not null)
                {
                    editor.Remove(node);
                }

                continue;
            }

            byte[] chunk = ChunkBuilder.Create(cf.Layout, op.Key, Encoding.UTF8.GetBytes(string.Join("; ", op.Values)));
            if (node is not null)
            {
                editor.Replace(node, chunk);
            }
            else
            {
                editor.AppendChild(null, chunk);
            }
        }

        return rep;
    }

    private static SystemWriteReport? EditBext(ChunkNode bext, IMediaSource src, TagEdits edits, ChunkEditor editor, MediaLimits limits)
    {
        if (bext.PayloadSize < 338)
        {
            return null;
        }

        limits.CheckTag(bext.PayloadSize, bext.Offset);
        byte[] payload = new byte[bext.PayloadSize];
        src.ReadExactly(bext.Payload.Offset, payload);
        byte[] before = (byte[])payload.Clone();
        List<CommonField> written = [];
        List<CommonField> removed = [];
        List<(CommonField, string)> not = [];
        List<string> conv = [];

        void Put(string key, string? value, CommonField? field)
        {
            int i = Array.IndexOf(BextKeys, key);
            (int off, int len) = BextSpans[i];
            byte[] b = value is null ? [] : Encoding.ASCII.GetBytes(value);
            if (b.Length > len)
            {
                conv.Add($"{key}: cut to {len} bytes");
            }

            Array.Clear(payload, off, len);
            Array.Copy(b, 0, payload, off, Math.Min(b.Length, len));
            if (field is { } f)
            {
                (value is null ? removed : written).Add(f);
            }
        }

        foreach (CommonField f in edits.TouchedFields)
        {
            IReadOnlyList<string>? ch = edits.FieldChange(f);
            if (ch is null)
            {
                continue;
            }

            if (f == CommonField.Description)
            {
                Put("Description", ch.Count > 0 ? ch[0] : null, f);
            }
            else if (f == CommonField.Date)
            {
                string? d = ch.Count > 0 ? ch[0] : null;
                if (d is not null && d.Length >= 10 && DateOnly.TryParseExact(d[..10], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _))
                {
                    Put("OriginationDate", d[..10], f);
                    if (d.Length > 10)
                    {
                        conv.Add("Date: only the date part is stored in OriginationDate");
                    }
                }
                else if (d is null)
                {
                    Put("OriginationDate", null, f);
                }
                else
                {
                    not.Add((f, "bext stores the origination date as yyyy-mm-dd; the value is not a full date"));
                }
            }
        }

        foreach (TagEdit n in edits.NativeFor(TagSystems.Bext))
        {
            if (BextKeys.Contains(n.Key))
            {
                Put(n.Key!, n.Kind == TagEditKind.SetNative ? n.Values[0] : null, null);
            }
        }

        if (!payload.AsSpan().SequenceEqual(before))
        {
            editor.Replace(bext, ChunkBuilder.Create(ChunkLayout.Riff, "bext", payload));
        }

        return new SystemWriteReport(TagSystems.Bext, written, removed, not, conv, false);
    }
}
