using System.Linq;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Writing;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>
/// Plans tag edits in an ISO BMFF / QuickTime file: the iTunes item list (<c>meta</c>/<c>ilst</c> with the <c>mdir</c> handler, including cover art
/// and freeform items), QuickTime key-table metadata (<c>mdta</c>), the QuickTime © atoms of <c>udta</c> and the 3GPP text boxes. Systems
/// present in the file are edited; when none is, an iTunes item list is created in <c>moov/udta/meta</c>. Items that are not edited are copied byte for byte.
/// </summary>
internal static class IsoTagWriter
{
    private static readonly uint Hdlr = BoxTypes.Hdlr;
    private static readonly uint Keys = BoxTypes.Make("keys");
    private static readonly uint Data = BoxTypes.Make("data");
    private static readonly uint Covr = BoxTypes.Make("covr");
    private static readonly uint Gnre = BoxTypes.Make("gnre");
    private static readonly uint Mdir = BoxTypes.Make("mdir");
    private static readonly uint Mdta = BoxTypes.Make("mdta");

    private sealed class Item(string key, uint type, byte[] raw)
    {
        public string Key { get; } = key;

        public uint Type { get; } = type;

        public byte[] Raw { get; set; } = raw;

        public bool Dirty { get; set; }

        public bool Deleted { get; set; }
    }

    public static TagWritePlan Plan(IMediaSource src, TagEdits edits, TagWriteOptions options, MediaLimits limits, CancellationToken ct)
    {
        DefectLog defects = new();
        BoxTree tree = new(src, limits, defects);
        BoxNode moov = tree.TopLevel.FirstOrDefault(b => b.Type == BoxTypes.Moov) ?? throw new UnsupportedMediaException("The file has no movie box; its tags cannot be edited.");
        BoxEditor editor = new(tree, src, limits) { GrowthPadding = options.PaddingBytes };
        List<SystemWriteReport> reports = [];
        List<string> notes = [];

        BoxNode? udta = moov.Children.FirstOrDefault(c => c.Type == BoxTypes.Udta);
        List<BoxNode> metas = [];
        if (udta is not null)
        {
            metas.AddRange(udta.Children.Where(c => c.Type == BoxTypes.Meta));
        }

        metas.AddRange(moov.Children.Where(c => c.Type == BoxTypes.Meta));

        bool didIlst = false;
        bool didMdta = false;
        foreach (BoxNode meta in metas)
        {
            uint handler = HandlerOf(meta, src);
            BoxNode? ilst = meta.Children.FirstOrDefault(c => c.Type == BoxTypes.Ilst);
            if (ilst is null)
            {
                continue;
            }

            if (handler == Mdta)
            {
                if (didMdta || !options.Wants(TagSystems.QuickTimeKeys))
                {
                    continue;
                }

                didMdta = true;
                reports.Add(EditKeyed(src, meta, ilst, edits, editor, limits));
            }
            else if (options.Wants(TagSystems.ITunes) && !didIlst)
            {
                didIlst = true;
                reports.Add(EditItunes(src, meta, ilst, edits, editor, limits));
            }
        }

        bool anyUdtaText = false;
        if (udta is not null && options.Wants(TagSystems.QuickTimeUserData))
        {
            SystemWriteReport? r = EditUserData(src, udta, edits, editor, limits, options.Systems is not null);
            if (r is not null)
            {
                reports.Add(r);
                anyUdtaText = true;
            }
        }

        bool wroteText = anyUdtaText || didMdta;
        // A capture-time write sets header fields and named native keys only; it creates no item list of its own.
        bool touchesItems = edits.TouchedFields.Count > 0 || edits.PictureEdits.Any() || edits.NativeFor(TagSystems.ITunes).Any();
        if (!didIlst && touchesItems && options.Wants(TagSystems.ITunes) && (!wroteText || edits.PictureEdits.Any(e => e.Kind == TagEditKind.SetPicture)))
        {
            // Pictures cannot live in udta or in key-table metadata: with only those in the file, an item list is created for them alone.
            TagEdits forList = wroteText ? PicturesOnly(edits) : edits;
            SystemWriteReport created = CreateItunes(src, moov, udta, forList, editor, limits);
            reports.Add(created);
        }

        if (options.Wants(TagSystems.Xmp))
        {
            SystemWriteReport? xmp = EditXmp(src, tree, moov, edits, editor, limits, options.Systems is not null);
            if (xmp is not null)
            {
                reports.Add(xmp);
            }
        }

        if (edits.HeaderDate is { } headerDate)
        {
            CaptureDatePatches.Iso(src, moov, headerDate, editor, limits);
        }

        EditPlan plan = editor.Build(ct);
        return new TagWritePlan(plan, new TagWriteReport(reports, notes));
    }

    // XMP lives in a uuid box (BE7ACFCB-97A9-42E8-9C71-999491E3AFAC) beside the movie box or inside it. An existing packet is edited in place of
    // its box; one is created at the end of the file only when the caller names XMP, because no other system asks for it.
    private static SystemWriteReport? EditXmp(IMediaSource src, BoxTree tree, BoxNode moov, TagEdits edits, BoxEditor editor, MediaLimits limits, bool named)
    {
        BoxNode? box = tree.TopLevel.Concat(moov.Children).FirstOrDefault(b => b.Type == BoxTypes.Uuid && b.Header.UserType == Xmp.XmpNamespaces.BoxUserType);
        if (box is null && !named)
        {
            return null;
        }

        byte[]? existing = null;
        if (box is not null)
        {
            limits.CheckTag(box.PayloadSize, box.Offset);
            existing = new byte[box.PayloadSize];
            src.ReadExactly(box.Header.PayloadOffset, existing);
        }

        (byte[] packet, SystemWriteReport report) = XmpEditor.Edit(existing, edits, limits);
        long total = BoxBuilder.HeaderSize(BoxTypes.Uuid, packet.Length + 32L > uint.MaxValue) + packet.Length;
        byte[] bytes = new byte[total];
        int h = BoxBuilder.WriteHeader(bytes, BoxTypes.Uuid, total, Xmp.XmpNamespaces.BoxUserType);
        packet.CopyTo(bytes, h);
        if (box is null)
        {
            editor.AppendChild(null, bytes);
        }
        else
        {
            editor.Replace(box, bytes);
        }

        return report;
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

    private static uint HandlerOf(BoxNode meta, IMediaSource src)
    {
        BoxNode? hdlr = meta.Children.FirstOrDefault(c => c.Type == Hdlr);
        if (hdlr is null || hdlr.PayloadSize < 12)
        {
            return Mdir;
        }

        Span<byte> b = stackalloc byte[12]; // bounded: a fixed 12-byte header read
        return src.TryReadExactly(hdlr.Offset + hdlr.HeaderSize, b) ? BinaryPrimitives.ReadUInt32BigEndian(b[8..]) : Mdir;
    }

    private static byte[] ReadBox(IMediaSource src, BoxNode n, MediaLimits limits)
    {
        long size = n.HeaderSize + n.PayloadSize;
        limits.CheckAllocation(size, n.Offset);
        byte[] b = new byte[size];
        src.ReadExactly(n.Offset, b);
        return b;
    }

    // ---- the iTunes item list ----

    private static string KeyOf(BoxNode item, IMediaSource src, MediaLimits limits)
    {
        if (BoxTypes.ToText(item.Type) != "----")
        {
            return BoxTypes.ToText(item.Type);
        }

        string mean = "";
        string name = "";
        foreach (BoxNode c in item.Children)
        {
            string t = BoxTypes.ToText(c.Type);
            if ((t == "mean" || t == "name") && c.PayloadSize >= 4 && c.PayloadSize < 4096)
            {
                byte[] b = new byte[c.PayloadSize - 4];
                src.ReadExactly(c.Offset + c.HeaderSize + 4, b);
                if (t == "mean")
                {
                    mean = Encoding.UTF8.GetString(b);
                }
                else
                {
                    name = Encoding.UTF8.GetString(b);
                }
            }
        }

        return $"----:{mean}:{name}";
    }

    private static byte[] DataBox(uint wellKnownType, ReadOnlySpan<byte> value)
    {
        byte[] p = new byte[8 + value.Length];
        BinaryPrimitives.WriteUInt32BigEndian(p, wellKnownType);
        value.CopyTo(p.AsSpan(8));
        return BoxBuilder.Create(Data, p);
    }

    private static byte[] FullText(string type, string text)
    {
        byte[] t = Encoding.UTF8.GetBytes(text);
        byte[] p = new byte[4 + t.Length];
        t.CopyTo(p, 4);
        return BoxBuilder.Create(BoxTypes.Make(type), p);
    }

    private static byte[] BuildItem(string key, IReadOnlyList<string> values, uint? indexType)
    {
        byte[] data = [];
        if (key is "trkn" or "disk")
        {
            (int n, int t) = ParsePair(values[0]);
            byte[] v = key == "trkn" ? new byte[8] : new byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(v.AsSpan(2), (ushort)n);
            BinaryPrimitives.WriteUInt16BigEndian(v.AsSpan(4), (ushort)t);
            data = DataBox(0, v);
        }
        else
        {
            foreach (string s in values)
            {
                data = [.. data, .. DataBox(1, Encoding.UTF8.GetBytes(s))];
            }
        }

        if (key.StartsWith("----:", StringComparison.Ordinal))
        {
            string[] parts = key.Split(':', 3);
            return BoxBuilder.Create(BoxTypes.Make("----"), [.. FullText("mean", parts[1]), .. FullText("name", parts[2]), .. data]);
        }

        return BoxBuilder.Create(indexType ?? BoxTypes.Make(key), data);
    }

    private static (int N, int T) ParsePair(string s)
    {
        int slash = s.IndexOf('/', StringComparison.Ordinal);
        _ = int.TryParse(slash < 0 ? s : s[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out int n);
        int t = 0;
        if (slash >= 0)
        {
            _ = int.TryParse(s[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out t);
        }

        return (Math.Min(n, 65535), Math.Min(t, 65535));
    }

    private static string? PairText(byte[] raw)
    {
        // item header 8 + data header 8 + data flags/locale 8 = payload at 24; trkn: 2 reserved, number, total
        if (raw.Length < 24 + 6)
        {
            return null;
        }

        int n = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(26));
        int t = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(28));
        return t > 0 ? $"{n}/{t}" : n.ToString(CultureInfo.InvariantCulture);
    }

    private static string? FirstText(byte[] raw)
    {
        if (raw.Length < 24 || BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(12)) != Data)
        {
            return null;
        }

        int len = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(8)), (uint)(raw.Length - 8)) - 16;
        return len < 0 ? null : Encoding.UTF8.GetString(raw.AsSpan(24, Math.Min(len, raw.Length - 24)));
    }

    private static uint ImageType(string mime) => mime.ToLowerInvariant() switch
    {
        "image/png" => 14,
        "image/bmp" => 27,
        "image/gif" => 12,
        _ => 13,
    };

    private static SystemWriteReport EditItunes(IMediaSource src, BoxNode meta, BoxNode ilst, TagEdits edits, BoxEditor editor, MediaLimits limits)
    {
        List<Item> items = [];
        foreach (BoxNode n in ilst.Children)
        {
            items.Add(new Item(KeyOf(n, src, limits), n.Type, ReadBox(src, n, limits)));
        }

        string? Existing(string key) => items.FirstOrDefault(i => i.Key == key && !i.Deleted) is { } it ? (key is "trkn" or "disk" ? PairText(it.Raw) : FirstText(it.Raw)) : null;
        List<KeyOp> ops = [];
        SystemWriteReport rep = KeyValueOps.Build(TagSystems.ITunes, edits, Existing, f => f is CommonField.TrackNumber or CommonField.DiscNumber ? PairStyle.Combined : PairStyle.None, null, ops);

        foreach (KeyOp op in ops)
        {
            ApplyOp(items, op, null);
        }

        if (edits.FieldChange(CommonField.Genre) is { } g && g.Count > 0)
        {
            foreach (Item it in items.Where(i => i.Type == Gnre))
            {
                it.Deleted = true;
                it.Dirty = true;
            }
        }

        List<string> conv = [.. rep.Conversions];
        ApplyPictures(items, edits, conv, out bool picturesChanged);
        List<(CommonField, string)> not = [.. rep.NotRepresentable];
        foreach (CommonField f in edits.TouchedFields.Where(f => edits.FieldChange(f) is not null && KeyValueOps.PrimaryKey(f, TagSystems.ITunes) is null && f is not (CommonField.TrackTotal or CommonField.DiscTotal or CommonField.TrackNumber or CommonField.DiscNumber) && !not.Any(x => x.Item1 == f)))
        {
            not.Add((f, $"an iTunes item list has no standard item for {f}"));
        }

        if (items.Any(i => i.Dirty) || picturesChanged)
        {
            byte[] newIlst = BoxBuilder.Create(BoxTypes.Ilst, items.Where(i => !i.Deleted).SelectMany(i => i.Raw).ToArray());
            editor.Replace(ilst, newIlst);
        }

        return rep with { NotRepresentable = not, Conversions = conv };
    }

    private static void ApplyOp(List<Item> items, KeyOp op, uint? indexType)
    {
        if (op.Values is null)
        {
            foreach (Item it in items.Where(i => !i.Deleted && (i.Key == op.Key || op.Aliases.Contains(i.Key))))
            {
                it.Deleted = true;
                it.Dirty = true;
            }

            return;
        }

        byte[] raw = BuildItem(op.Key, op.Values, indexType);
        Item? first = items.FirstOrDefault(i => i.Key == op.Key && !i.Deleted);
        if (first is null)
        {
            items.Add(new Item(op.Key, indexType ?? BoxTypes.Make(op.Key.StartsWith("----:", StringComparison.Ordinal) ? "----" : op.Key), raw) { Dirty = true });
            return;
        }

        if (!first.Raw.AsSpan().SequenceEqual(raw))
        {
            first.Raw = raw;
            first.Dirty = true;
        }

        foreach (Item dup in items.Where(i => i != first && i.Key == op.Key && !i.Deleted))
        {
            dup.Deleted = true;
            dup.Dirty = true;
        }
    }

    // MP4 has one item for pictures, covr, with one data box per picture; the picture's role is not stored.
    private static void ApplyPictures(List<Item> items, TagEdits edits, List<string> conv, out bool changed)
    {
        changed = false;
        bool replacedOnce = false;
        foreach (TagEdit e in edits.PictureEdits)
        {
            Item? covr = items.FirstOrDefault(i => i.Type == Covr && !i.Deleted);
            if (e.Kind == TagEditKind.RemovePictures)
            {
                if (covr is not null)
                {
                    covr.Deleted = true;
                    changed = true;
                }

                conv.Add("pictures: MP4 does not store picture roles; all cover pictures were removed");
                continue;
            }

            byte[] box = DataBox(ImageType(e.MimeType!), e.PictureData.Span);
            if (covr is null)
            {
                items.Add(new Item("covr", Covr, BoxBuilder.Create(Covr, box)) { Dirty = true });
            }
            else if (!replacedOnce)
            {
                covr.Raw = BoxBuilder.Create(Covr, box);
            }
            else
            {
                covr.Raw = BoxBuilder.Create(Covr, [.. covr.Raw.AsSpan(8).ToArray(), .. box]);
            }

            replacedOnce = true;
            changed = true;
            if (e.PictureRole is not (null or PictureType.FrontCover))
            {
                conv.Add($"pictures: role {e.PictureRole} is not stored in MP4 and is read back as a front cover");
            }
        }
    }

    // ---- QuickTime metadata with a key table ----

    private static SystemWriteReport EditKeyed(IMediaSource src, BoxNode meta, BoxNode ilst, TagEdits edits, BoxEditor editor, MediaLimits limits)
    {
        BoxNode? keysBox = meta.Children.FirstOrDefault(c => c.Type == Keys);
        List<string> keyNames = [];
        if (keysBox is not null)
        {
            byte[] kb = ReadBox(src, keysBox, limits);
            int count = kb.Length >= 16 ? (int)BinaryPrimitives.ReadUInt32BigEndian(kb.AsSpan(12)) : 0;
            int pos = 16;
            for (int i = 0; i < count && pos + 8 <= kb.Length; i++)
            {
                int sz = (int)BinaryPrimitives.ReadUInt32BigEndian(kb.AsSpan(pos));
                if (sz < 8 || pos + sz > kb.Length)
                {
                    break;
                }

                keyNames.Add(Encoding.UTF8.GetString(kb.AsSpan(pos + 8, sz - 8)));
                pos += sz;
            }
        }

        List<Item> items = [];
        foreach (BoxNode n in ilst.Children)
        {
            int index = (int)n.Type;
            string key = index >= 1 && index <= keyNames.Count ? keyNames[index - 1] : "#" + index;
            items.Add(new Item(key, n.Type, ReadBox(src, n, limits)));
        }

        string? Existing(string key) => items.FirstOrDefault(i => i.Key == key && !i.Deleted) is { } it ? FirstText(it.Raw) : null;
        List<KeyOp> ops = [];
        SystemWriteReport rep = KeyValueOps.Build(TagSystems.QuickTimeKeys, edits, Existing, _ => PairStyle.None, null, ops);
        bool keysChanged = false;
        foreach (KeyOp op in ops)
        {
            uint? idx = null;
            if (op.Values is not null)
            {
                int at = keyNames.IndexOf(op.Key);
                if (at < 0)
                {
                    keyNames.Add(op.Key);
                    at = keyNames.Count - 1;
                    keysChanged = true;
                }

                idx = (uint)(at + 1);
            }

            ApplyOp(items, op, idx);
        }

        if (items.Any(i => i.Dirty))
        {
            editor.Replace(ilst, BoxBuilder.Create(BoxTypes.Ilst, items.Where(i => !i.Deleted).SelectMany(i => i.Raw).ToArray()));
        }

        if (keysChanged && keysBox is not null)
        {
            using MemoryStream ms = new();
            Span<byte> hdr = stackalloc byte[8]; // fixed 8-byte entry header
            foreach (string k in keyNames)
            {
                byte[] kn = Encoding.UTF8.GetBytes(k);
                BinaryPrimitives.WriteUInt32BigEndian(hdr, (uint)(8 + kn.Length));
                BinaryPrimitives.WriteUInt32BigEndian(hdr[4..], Mdta);
                ms.Write(hdr);
                ms.Write(kn);
            }

            byte[] count = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(count, (uint)keyNames.Count);
            editor.Replace(keysBox, BoxBuilder.CreateFull(Keys, 0, 0, [.. count, .. ms.ToArray()]));
        }

        return rep;
    }

    // ---- udta: QuickTime © text atoms and 3GPP text boxes ----

    private static readonly (CommonField Field, string Atom)[] UserDataAtoms =
    [
        (CommonField.Title, "\u00a9nam"), (CommonField.Artist, "\u00a9ART"), (CommonField.Album, "\u00a9alb"), (CommonField.Composer, "\u00a9wrt"),
        (CommonField.Genre, "\u00a9gen"), (CommonField.Comment, "\u00a9cmt"), (CommonField.Date, "\u00a9day"), (CommonField.Copyright, "\u00a9cpy"),
        (CommonField.Encoder, "\u00a9too"), (CommonField.EncodedBy, "\u00a9enc"), (CommonField.Description, "\u00a9inf"), (CommonField.Director, "\u00a9dir"),
        (CommonField.Author, "\u00a9aut"), (CommonField.Keywords, "\u00a9key"),
    ];

    private static byte[] QuickTimeText(string atom, string text)
    {
        byte[] t = Encoding.UTF8.GetBytes(text);
        byte[] p = new byte[4 + t.Length];
        BinaryPrimitives.WriteUInt16BigEndian(p, (ushort)t.Length);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), 0x15C7); // packed ISO 639-2 "eng"
        t.CopyTo(p, 4);
        return BoxBuilder.Create(BoxTypes.Make(atom), p);
    }

    // Edits the © atoms present under udta; returns null when udta has none and nothing asked for one that exists.
    private static SystemWriteReport? EditUserData(IMediaSource src, BoxNode udta, TagEdits edits, BoxEditor editor, MediaLimits limits, bool explicitSystem)
    {
        List<BoxNode> atoms = [.. udta.Children.Where(c => (c.Type >> 24) == 0xA9)];
        if (atoms.Count == 0 && !explicitSystem)
        {
            return null;
        }

        List<CommonField> written = [];
        List<CommonField> removed = [];
        List<(CommonField, string)> not = [];
        foreach (CommonField f in edits.TouchedFields)
        {
            IReadOnlyList<string>? change = edits.FieldChange(f);
            string? atom = UserDataAtoms.FirstOrDefault(a => a.Field == f).Atom;
            if (change is null)
            {
                continue;
            }

            if (atom is null)
            {
                not.Add((f, $"QuickTime user data has no © atom for {f}"));
                continue;
            }

            uint type = BoxTypes.Make(atom);
            BoxNode? existing = atoms.FirstOrDefault(a => a.Type == type);
            if (change.Count == 0)
            {
                if (existing is not null)
                {
                    editor.Remove(existing);
                    removed.Add(f);
                }
            }
            else
            {
                byte[] box = change.Count == 1 ? QuickTimeText(atom, change[0]) : BoxBuilder.Create(type, change.SelectMany(v => QuickTimeText(atom, v).AsSpan(8).ToArray()).ToArray());
                if (existing is not null)
                {
                    editor.Replace(existing, box);
                }
                else
                {
                    editor.AppendChild(udta, box);
                }

                written.Add(f);
            }
        }

        // Native edits name one © atom by its four-character code (a capture-time write sets ©day this way).
        foreach (TagEdit n in edits.NativeFor(TagSystems.QuickTimeUserData))
        {
            if (n.Key is not { Length: 4 } code || code[0] != '\u00a9')
            {
                continue;
            }

            uint nativeType = BoxTypes.Make(code);
            BoxNode? current = atoms.FirstOrDefault(a => a.Type == nativeType);
            if (n.Kind == TagEditKind.RemoveNative)
            {
                if (current is not null)
                {
                    editor.Remove(current);
                }

                continue;
            }

            byte[] atomBox = QuickTimeText(code, n.Values[0]);
            if (current is not null)
            {
                editor.Replace(current, atomBox);
            }
            else
            {
                editor.AppendChild(udta, atomBox);
            }
        }

        return new SystemWriteReport(TagSystems.QuickTimeUserData, written, removed, not, [], false);
    }

    // ---- creating an item list ----

    private static SystemWriteReport CreateItunes(IMediaSource src, BoxNode moov, BoxNode? udta, TagEdits edits, BoxEditor editor, MediaLimits limits)
    {
        List<Item> items = [];
        List<KeyOp> ops = [];
        SystemWriteReport rep = KeyValueOps.Build(TagSystems.ITunes, edits, _ => null, f => f is CommonField.TrackNumber or CommonField.DiscNumber ? PairStyle.Combined : PairStyle.None, null, ops);
        foreach (KeyOp op in ops)
        {
            ApplyOp(items, op, null);
        }

        List<string> conv = [.. rep.Conversions];
        ApplyPictures(items, edits, conv, out _);
        byte[] ilst = BoxBuilder.Create(BoxTypes.Ilst, items.Where(i => !i.Deleted).SelectMany(i => i.Raw).ToArray());
        byte[] hdlr = BoxBuilder.CreateFull(Hdlr, 0, 0, [.. new byte[4], .. "mdir"u8.ToArray(), .. "appl"u8.ToArray(), .. new byte[9]]);
        byte[] meta = BoxBuilder.CreateFull(BoxTypes.Meta, 0, 0, [.. hdlr, .. ilst]);
        if (udta is not null)
        {
            editor.AppendChild(udta, meta);
        }
        else
        {
            editor.AppendChild(moov, BoxBuilder.Create(BoxTypes.Udta, meta));
        }

        List<(CommonField, string)> not = [.. rep.NotRepresentable];
        foreach (CommonField f in edits.TouchedFields.Where(f => edits.FieldChange(f) is not null && KeyValueOps.PrimaryKey(f, TagSystems.ITunes) is null && f is not (CommonField.TrackTotal or CommonField.DiscTotal or CommonField.TrackNumber or CommonField.DiscNumber) && !not.Any(x => x.Item1 == f)))
        {
            not.Add((f, $"an iTunes item list has no standard item for {f}"));
        }

        return rep with { NotRepresentable = not, Conversions = conv, Created = true };
    }
}
