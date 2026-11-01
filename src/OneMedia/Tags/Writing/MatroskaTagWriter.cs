using System.Linq;
using System.Text;
using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>
/// Plans tag edits in a Matroska or WebM file: <c>SimpleTag</c>s of the file-level <c>Tag</c>s (those whose <c>Targets</c> name no track, edition,
/// chapter or attachment), the <c>Title</c> in Segment Info, and cover art as <c>AttachedFile</c>s named <c>cover.*</c>. A tag that is replaced keeps its language, default flag and nested tags.
/// </summary>
internal static class MatroskaTagWriter
{
    private const uint Info = 0x1549A966;
    private const uint InfoTitle = 0x7BA9;
    private const uint Tags = 0x1254C367;
    private const uint Tag = 0x7373;
    private const uint Targets = 0x63C0;
    private const uint TargetTypeValue = 0x68CA;
    private const uint SimpleTag = 0x67C8;
    private const uint TagName = 0x45A3;
    private const uint TagString = 0x4487;
    private const uint TagBinary = 0x4485;
    private const uint Attachments = 0x1941A469;
    private const uint AttachedFile = 0x61A7;
    private const uint FileDescription = 0x467E;
    private const uint FileName = 0x466E;
    private const uint FileMediaType = 0x4660;
    private const uint FileData = 0x465C;
    private const uint FileUid = 0x46AE;

    public static TagWritePlan Plan(IMediaSource src, TagEdits edits, TagWriteOptions options, MediaLimits limits, CancellationToken ct)
    {
        EbmlDocument doc = EbmlDocument.Open(src, limits, null, ct);
        EbmlElement seg = doc.Segment ?? throw new UnsupportedMediaException("The file has no Segment; its tags cannot be edited.");
        EbmlEditor editor = new(doc, src, limits);
        List<SystemWriteReport> reports = [];
        IReadOnlyList<EbmlElement> children = seg.ChildElements;

        // Bytes that no element accounts for (the reader found its way past them by searching) or a SeekHead whose entries were cut back leave the reader's picture of the
        // file resting on luck; moving anything could change what it finds. Such a file is repaired first, not edited.
        if (doc.Defects.Any(d => d.Kind == DefectKind.UnaccountedBytes && d.Offset >= seg.Offset) || children.Any(c => c.Id == 0x114D9B74 && HasCutBackSize(c)))
        {
            throw new UnsupportedMediaException("The Segment has bytes that no element accounts for, or a damaged SeekHead; repair the file before editing its tags.");
        }

        List<EbmlElement> fileTags = [];
        foreach (EbmlElement tagsEl in children.Where(c => c.Id == Tags))
        {
            foreach (EbmlElement tag in tagsEl.ChildElements.Where(c => c.Id == Tag))
            {
                if (IsFileLevel(tag))
                {
                    fileTags.Add(tag);
                }
            }
        }

        List<(EbmlElement Tag, EbmlElement Simple, string Name)> simples = [];
        foreach (EbmlElement tag in fileTags)
        {
            foreach (EbmlElement s in tag.ChildElements.Where(c => c.Id == SimpleTag))
            {
                simples.Add((tag, s, NameOf(s)));
            }
        }

        string? Existing(string key) => simples.FirstOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase)) is { Simple: not null } m ? StringOf(m.Simple) : null;
        bool hasTrack = simples.Any(x => string.Equals(x.Name, "TRACK", StringComparison.OrdinalIgnoreCase));
        bool hasPart = simples.Any(x => string.Equals(x.Name, "PART_NUMBER", StringComparison.OrdinalIgnoreCase));
        PairStyle Style(CommonField f) => f == CommonField.TrackNumber ? (hasTrack && !hasPart ? PairStyle.Combined : PairStyle.Separate) : PairStyle.Combined;
        string? Override(CommonField f) => f == CommonField.TrackNumber && hasTrack && !hasPart ? "TRACK" : f == CommonField.DiscNumber ? "DISC" : null;

        List<KeyOp> ops = [];
        SystemWriteReport rep = KeyValueOps.Build(TagSystems.Matroska, edits, Existing, Style, Override, ops);
        bool wantsTags = options.Wants(TagSystems.Matroska);
        if (!wantsTags)
        {
            // Naming other systems leaves the Tags element and the attachments alone.
            ops.Clear();
            rep = new SystemWriteReport(TagSystems.Matroska, [], [], [], [], false);
        }

        // The tag new items are appended to: a file-level Tag at the default level, else any file-level Tag, else a new one.
        EbmlElement? home = fileTags.FirstOrDefault(t => LevelOf(t) == 50) ?? fileTags.FirstOrDefault();
        List<byte[]> newSimples = [];
        List<byte[]> inserts = [];
        HashSet<long> consumed = [];
        HashSet<long> blocked = [];
        bool created = false;
        foreach (KeyOp op in ops)
        {
            var matches = simples.Where(x => !consumed.Contains(x.Simple.Offset) && (string.Equals(x.Name, op.Key, StringComparison.OrdinalIgnoreCase) || op.Aliases.Contains(x.Name, StringComparer.OrdinalIgnoreCase))).ToList();
            if (op.Values is null)
            {
                foreach (var m in matches)
                {
                    editor.Remove(m.Simple);
                    blocked.Add(TopOf(m.Simple, seg));
                    consumed.Add(m.Simple.Offset);
                }

                continue;
            }

            // The match the common view would pick: levels 30 and above first, lowest first.
            var primary = matches.Where(m => string.Equals(m.Name, op.Key, StringComparison.OrdinalIgnoreCase)).OrderBy(m => LevelOf(m.Tag) is var l && l < 30 ? 1000 + l : l).FirstOrDefault();
            int next = 0;
            if (primary.Simple is not null)
            {
                editor.Replace(primary.Simple, BuildSimple(primary.Simple, op.Key, op.Values[next++], src));
                blocked.Add(TopOf(primary.Simple, seg));
                consumed.Add(primary.Simple.Offset);
            }

            for (; next < op.Values.Count; next++)
            {
                newSimples.Add(BuildSimple(null, op.Key, op.Values[next], src));
            }
        }

        // Title also lives in Segment Info.
        List<CommonField> written = [.. rep.Written];
        List<CommonField> removed = [.. rep.Removed];
        EbmlElement? info = children.FirstOrDefault(c => c.Id == Info);
        if (edits.FieldChange(CommonField.Title) is { } title && info is not null && options.Wants(TagSystems.MatroskaInfo))
        {
            EbmlElement? old = info.ChildElements.FirstOrDefault(c => c.Id == InfoTitle);
            if (title.Count == 0)
            {
                if (old is not null)
                {
                    editor.Remove(old);
                    blocked.Add(info.Offset);
                }
            }
            else if (old is not null)
            {
                editor.Replace(old, EbmlWriter.Element(InfoTitle, Encoding.UTF8.GetBytes(title[0])));
                blocked.Add(info.Offset);
            }
            else
            {
                editor.AppendChild(info, EbmlWriter.Element(InfoTitle, Encoding.UTF8.GetBytes(title[0])));
                blocked.Add(info.Offset);
            }

            reports.Add(new SystemWriteReport(TagSystems.MatroskaInfo, title.Count > 0 ? [CommonField.Title] : [], title.Count == 0 ? [CommonField.Title] : [], [], [], false));
        }

        if (newSimples.Count > 0)
        {
            if (home is not null)
            {
                foreach (byte[] s in newSimples)
                {
                    editor.AppendChild(home, s);
                    blocked.Add(TopOf(home, seg));
                }
            }
            else if (children.FirstOrDefault(c => c.Id == Tags) is { } tagsEl)
            {
                editor.AppendChild(tagsEl, Wrap(newSimples));
                blocked.Add(tagsEl.Offset);
            }
            else
            {
                inserts.Add(EbmlWriter.Element(Tags, Wrap(newSimples)));
                created = true;
            }
        }

        // Pictures.
        List<string> conv = [.. rep.Conversions];
        if (removed.Contains(CommonField.Encoder))
        {
            conv.Add("Encoder: the ENCODER tag was removed; the Segment's MuxingApp and WritingApp belong to the muxer and are kept");
        }

        if (wantsTags)
        {
            PlanPictures(src, seg, children, edits, editor, conv, limits, inserts, blocked);
        }

        if (inserts.Count > 0)
        {
            InsertBeforeClusters(src, seg, children, inserts, editor, limits, blocked);
        }

        if (wantsTags)
        {
            reports.Insert(0, rep with { Written = written, Removed = removed, Conversions = conv, Created = created });
        }

        EditPlan plan = editor.Build(ct);
        return new TagWritePlan(plan, new TagWriteReport(reports, []));
    }

    // New top-level elements go in front of the first Cluster, where readers look for them without scanning: beside the last element before it, which
    // is copied (it is small) and followed by the new elements. A Void element after it absorbs the growth. When that is not possible they go at the end.
    private static void InsertBeforeClusters(IMediaSource src, EbmlElement seg, IReadOnlyList<EbmlElement> children, List<byte[]> inserts, EbmlEditor editor, MediaLimits limits, HashSet<long> blocked)
    {
        List<EbmlElement> candidates = [];
        foreach (EbmlElement c in children)
        {
            if (c.Id == 0x1F43B675)
            {
                break;
            }

            // The SeekHead and Cues hold positions that the editor moves when something is inserted; an element that is replaced is skipped by that
            // pass, so neither can be the anchor. A Void is no anchor either.
            if (c.Id is not (0xEC or 0x114D9B74 or 0x1C53BB6B) && !blocked.Contains(c.Offset))
            {
                candidates.Add(c);
            }
        }

        byte[] extra = inserts.SelectMany(x => x).ToArray();
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            EbmlElement anchor = candidates[i];
            if (anchor.Range.Length > 1 << 20)
            {
                continue;
            }

            byte[] raw = new byte[anchor.Range.Length];
            src.ReadExactly(anchor.Offset, raw);
            try
            {
                editor.ReplaceWithRun(anchor, raw.Concat(extra).ToArray());
                return;
            }
            catch (ArgumentException)
            {
                // This element is being edited already: try the one before it.
            }
        }

        foreach (byte[] x in inserts)
        {
            editor.AppendChild(seg, x);
        }
    }

    private static bool HasCutBackSize(EbmlElement e) => e.IsSizeInferred || e.ChildElements.Any(HasCutBackSize);

    private static long TopOf(EbmlElement e, EbmlElement seg)
    {
        EbmlElement t = e;
        while (t.Parent is { } p && p.Offset != seg.Offset)
        {
            t = p;
        }

        return t.Offset;
    }

    private static byte[] Wrap(List<byte[]> simples) => EbmlWriter.Element(Tag, [.. EbmlWriter.Element(Targets, []), .. simples.SelectMany(s => s)]);

    private static bool IsFileLevel(EbmlElement tag)
    {
        EbmlElement? targets = tag.ChildElements.FirstOrDefault(c => c.Id == Targets);
        // Track, edition, chapter and attachment UIDs aim a tag at one thing (RFC 9559 §5.1.8.1).
        return targets is null || !targets.ChildElements.Any(c => c.Id is 0x63C5 or 0x63C9 or 0x63C4 or 0x63C6);
    }

    private static long LevelOf(EbmlElement tag)
    {
        EbmlElement? t = tag.ChildElements.FirstOrDefault(c => c.Id == Targets)?.ChildElements.FirstOrDefault(c => c.Id == TargetTypeValue);
        Span<byte> b = stackalloc byte[8]; // an unsigned integer element is at most 8 bytes
        if (t is not null && t.PayloadSize is > 0 and <= 8 && t.ReadPayload(b[..(int)t.PayloadSize]) == t.PayloadSize && Ebml.TryReadUnsigned(b[..(int)t.PayloadSize], out ulong v))
        {
            return (long)v;
        }

        return 50;
    }

    private static string NameOf(EbmlElement simple) => TextChild(simple, TagName) ?? "";

    private static string? StringOf(EbmlElement simple) => TextChild(simple, TagString);

    private static string? TextChild(EbmlElement parent, uint id)
    {
        EbmlElement? c = parent.ChildElements.FirstOrDefault(x => x.Id == id);
        if (c is null || c.PayloadSize > 1 << 20)
        {
            return null;
        }

        byte[] b = new byte[c.PayloadSize];
        return c.ReadPayload(b) == b.Length ? Encoding.UTF8.GetString(b).TrimEnd('\0') : null;
    }

    private static byte[] BuildSimple(EbmlElement? old, string name, string value, IMediaSource src)
    {
        List<byte> payload = [];
        payload.AddRange(EbmlWriter.Element(TagName, Encoding.UTF8.GetBytes(name)));
        payload.AddRange(EbmlWriter.Element(TagString, Encoding.UTF8.GetBytes(value)));
        if (old is not null)
        {
            foreach (EbmlElement c in old.ChildElements)
            {
                if (c.Id is TagName or TagString or TagBinary)
                {
                    continue;
                }

                byte[] raw = new byte[c.Range.Length];
                src.ReadExactly(c.Offset, raw);
                payload.AddRange(raw);
            }
        }

        return EbmlWriter.Element(SimpleTag, [.. payload]);
    }

    // The DocType element (ID 0x4282) of the EBML header holds "webm" for WebM files; the header is the first few bytes of the file.
    private static bool IsWebM(IMediaSource src)
    {
        Span<byte> head = stackalloc byte[64]; // the EBML header is shorter than this in every file seen
        int n = src.Read(0, head);
        ReadOnlySpan<byte> pattern = [0x42, 0x82, 0x84, (byte)'w', (byte)'e', (byte)'b', (byte)'m'];
        return head[..n].IndexOf(pattern) >= 0;
    }

    private static void PlanPictures(IMediaSource src, EbmlElement seg, IReadOnlyList<EbmlElement> children, TagEdits edits, EbmlEditor editor, List<string> conv, MediaLimits limits, List<byte[]> inserts, HashSet<long> blocked)
    {
        if (!edits.PictureEdits.Any())
        {
            return;
        }

        if (IsWebM(src) && edits.PictureEdits.Any(e => e.Kind == TagEditKind.SetPicture))
        {
            // The WebM profile has no Attachments element (mkvalidator rejects an AttachedFile in a webm file), so a cover has no place in it.
            conv.Add("pictures: not written; the WebM profile has no attachments");
            return;
        }

        EbmlElement? attachments = children.FirstOrDefault(c => c.Id == Attachments);
        List<EbmlElement> covers = [];
        if (attachments is not null)
        {
            foreach (EbmlElement f in attachments.ChildElements.Where(c => c.Id == AttachedFile))
            {
                string name = TextChild(f, FileName) ?? "";
                if (name.StartsWith("cover", StringComparison.OrdinalIgnoreCase) || name.StartsWith("small_cover", StringComparison.OrdinalIgnoreCase) || IsRoleName(name))
                {
                    covers.Add(f);
                }
            }
        }

        HashSet<long> touched = [];
        List<byte[]> additions = [];
        foreach (TagEdit e in edits.PictureEdits)
        {
            if (e.Kind == TagEditKind.RemovePictures)
            {
                foreach (EbmlElement c in covers.Where(c => touched.Add(c.Offset)))
                {
                    editor.Remove(c);
                    blocked.Add(attachments!.Offset);
                }

                continue;
            }

            string ext = e.MimeType!.ToLowerInvariant() switch { "image/png" => "png", "image/gif" => "gif", "image/bmp" => "bmp", "image/webp" => "webp", _ => "jpg" };
            string stem = e.PictureRole is null or PictureType.FrontCover ? "cover" : e.PictureRole.Value.ToString().ToLowerInvariant();
            if (stem != "cover")
            {
                conv.Add($"pictures: role {e.PictureRole} is carried in the attachment name '{stem}.{ext}' and is read back as a non-cover attachment");
            }

            string fileName = stem + "." + ext;
            ulong uid = 0x4F4D00000000UL | EbmlCrc32.Compute(e.PictureData.Span);
            limits.CheckAttachment(e.PictureData.Length, 0);
            byte[] file = EbmlWriter.Element(AttachedFile, [
                .. EbmlWriter.Element(FileDescription, Encoding.UTF8.GetBytes(e.Description)),
                .. EbmlWriter.Element(FileName, Encoding.UTF8.GetBytes(fileName)),
                .. EbmlWriter.Element(FileMediaType, Encoding.ASCII.GetBytes(e.MimeType!)),
                .. EbmlWriter.Element(FileData, e.PictureData.Span),
                .. EbmlWriter.UnsignedElement(FileUid, uid),
            ]);
            EbmlElement? same = covers.FirstOrDefault(c => !touched.Contains(c.Offset) && string.Equals(TextChild(c, FileName)?.Split('.')[0], stem, StringComparison.OrdinalIgnoreCase));
            if (same is not null)
            {
                editor.Replace(same, file);
                blocked.Add(attachments!.Offset);
                touched.Add(same.Offset);
            }
            else
            {
                additions.Add(file);
            }
        }

        if (additions.Count > 0)
        {
            if (attachments is not null)
            {
                foreach (byte[] a in additions)
                {
                    editor.AppendChild(attachments, a);
                    blocked.Add(attachments.Offset);
                }
            }
            else
            {
                inserts.Add(EbmlWriter.Element(Attachments, additions.SelectMany(a => a).ToArray()));
            }
        }
    }

    private static bool IsRoleName(string name)
    {
        string stem = name.Split('.')[0];
        return Enum.GetNames<PictureType>().Any(n => string.Equals(n, stem, StringComparison.OrdinalIgnoreCase));
    }
}
