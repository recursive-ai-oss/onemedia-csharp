using System.Linq;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RecursiveAI.OneMedia.Tags.Ape;
using RecursiveAI.OneMedia.Tags.Id3;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>One replacement of a byte range of the source by new bytes (or, with a zero length, an insertion).</summary>
/// <param name="Offset">Where the replaced range starts.</param>
/// <param name="Length">Length of the replaced range.</param>
/// <param name="Data">The new bytes.</param>
/// <param name="Description">What is replaced, for the report.</param>
/// <param name="ZeroPad">Zero bytes that follow <paramref name="Data"/> (tag padding); generated when written, never held.</param>
internal readonly record struct Replacement(long Offset, long Length, byte[] Data, string Description, long ZeroPad = 0)
{
    /// <summary>Length of the new content: the bytes plus the padding.</summary>
    public long NewLength => Data.Length + ZeroPad;
}

internal static class ReplacementPlan
{
    /// <summary>Builds a plan that copies everything outside the replacements. Replacements must not overlap.</summary>
    public static EditPlan Build(long sourceLength, IEnumerable<Replacement> replacements, long paddingAvailable)
    {
        PieceList list = new();
        long pos = 0;
        long required = 0;
        foreach (Replacement r in replacements.OrderBy(x => x.Offset))
        {
            list.AddCopy(pos, r.Offset - pos);
            long start = list.Length;
            list.AddData(r.Data);
            list.AddZeros(r.ZeroPad);
            list.NoteChange(r.Description, new ByteRange(r.Offset, r.Length), start);
            // Growth padding (ZeroPad) is optional: only the bytes the edit itself needs count as a shortfall.
            required += Math.Max(0, r.Data.Length - r.Length);
            pos = r.Offset + r.Length;
        }

        list.AddCopy(pos, sourceLength - pos);
        return list.ToPlan(sourceLength, 0, paddingAvailable, required);
    }
}

/// <summary>
/// Plans tag edits in an MP3 (or other file that carries tags at its ends): the ID3v2 tag at the start, an APEv2 tag and an ID3v1 tag at the end.
/// Frames and items that are not edited are copied byte for byte; the ID3v2 tag keeps its size (and so the file its layout) when the result fits
/// in the old tag, padding included.
/// </summary>
internal static class Mp3TagWriter
{
    public static TagWritePlan Plan(IMediaSource src, TagEdits edits, TagWriteOptions options, MediaLimits limits, CancellationToken ct)
    {
        List<Replacement> reps = [];
        List<SystemWriteReport> reports = [];
        List<string> notes = [];
        long length = src.Length;
        long audioStart = 0;
        long padAvail = 0;

        // ---- ID3v2 ----
        // Some files carry several ID3v2 tags back to back; each is edited so that every reader sees the same values.
        List<Id3Tag> tags = [];
        Span<byte> head = stackalloc byte[Id3Header.HeaderSize]; // the fixed tag header
        while (src.Read(audioStart, head, ct) == head.Length && Id3Header.TryParse(head, out _) && Id3Tag.TryRead(src, audioStart, out Id3Tag? next, limits) && next is not null)
        {
            tags.Add(next);
            audioStart += next.Range.Length;
        }

        Id3Tag? tag = tags.Count > 0 ? tags[0] : null;
        padAvail = tag is null || tag.Padding.IsNone ? 0 : tag.Padding.Length;
        bool wantV2 = options.Wants(TagSystems.Id3v2) && (tag is not null || options.Systems is not null || !HasEnd(src, length, out _, out _));
        if (wantV2)
        {
            if (tags.Count == 0)
            {
                (byte[] fresh, long freshPad, SystemWriteReport created, List<string> n0) = BuildId3v2(src, null, edits, options, limits, 0);
                notes.AddRange(n0);
                reports.Add(created);
                reps.Add(new Replacement(0, 0, fresh, "ID3v2 tag", freshPad));
            }

            for (int i = 0; i < tags.Count; i++)
            {
                (byte[] newTag, long newPad, SystemWriteReport r, List<string> n) = BuildId3v2(src, tags[i], edits, options, limits, tags[i].Range.Length);
                notes.AddRange(n);
                if (i == 0)
                {
                    reports.Add(r);
                }
                else
                {
                    notes.Add($"A further ID3v2 tag follows the first (at {tags[i].Range.Offset}); it received the same edits.");
                }

                reps.Add(new Replacement(tags[i].Range.Offset, tags[i].Range.Length, newTag, "ID3v2 tag", newPad));
            }
        }

        // ---- APEv2 and ID3v1 at the end ----
        HasEnd(src, length, out ApeTag? ape, out bool hasV1);
        long v1Start = hasV1 ? length - Id3v1Tag.Length : length;
        if (hasV1 && v1Start < audioStart)
        {
            // The ID3v2 tag claims bytes that the ID3v1 tag also occupies; moving either would cut into the other.
            throw new UnsupportedMediaException("The ID3v2 tag overlaps the ID3v1 tag at the end of the file; the file is not edited.") { Offset = v1Start };
        }
        if (ape is not null && options.Wants(TagSystems.Ape) && ape.Range.Offset >= audioStart)
        {
            (byte[] apeBytes, SystemWriteReport r) = BuildApe(ape, src, edits, limits);
            reports.Add(r);
            if (!apeBytes.AsSpan().SequenceEqual(ReadRange(src, ape.Range)))
            {
                reps.Add(new Replacement(ape.Range.Offset, ape.Range.Length, apeBytes, "APEv2 tag"));
            }
        }
        else if (ape is null && options.Systems?.Contains(TagSystems.Ape) == true)
        {
            (byte[] apeBytes, SystemWriteReport r) = BuildApe(null, src, edits, limits);
            reports.Add(r with { Created = true });
            reps.Add(new Replacement(v1Start, 0, apeBytes, "APEv2 tag"));
        }

        if (options.Wants(TagSystems.Id3v1) && (hasV1 || options.Systems?.Contains(TagSystems.Id3v1) == true))
        {
            byte[] old = hasV1 ? ReadRange(src, new ByteRange(v1Start, Id3v1Tag.Length)) : new byte[Id3v1Tag.Length];
            (byte[] v1, SystemWriteReport r) = BuildId3v1(old, hasV1, edits);
            reports.Add(r with { Created = !hasV1 });
            if (!hasV1 || !v1.AsSpan().SequenceEqual(old))
            {
                reps.Add(new Replacement(v1Start, hasV1 ? Id3v1Tag.Length : 0, v1, "ID3v1 tag"));
            }
        }

        EditPlan plan = ReplacementPlan.Build(length, reps.Where(x => !Unchanged(src, x)), padAvail);
        return new TagWritePlan(plan, new TagWriteReport(reports, notes));
    }

    // A replacement that would write the bytes already there is dropped, so that an edit which changes nothing leaves the tag as it is. A tag's
    // padding is not compared (it would mean reading all of it): when the new head equals the old head and the length is the same, the old padding stays.
    private static bool Unchanged(IMediaSource src, Replacement x)
    {
        if (x.Length != x.NewLength || x.Data.Length > x.Length)
        {
            return false;
        }

        byte[] old = new byte[x.Data.Length];
        src.ReadExactly(x.Offset, old);
        return old.AsSpan().SequenceEqual(x.Data);
    }

    private static byte[] ReadRange(IMediaSource src, ByteRange r)
    {
        byte[] b = new byte[r.Length];
        src.ReadExactly(r.Offset, b);
        return b;
    }

    // Finds an APEv2 tag and an ID3v1 tag at the end of the file.
    private static bool HasEnd(IMediaSource src, long length, out ApeTag? ape, out bool v1)
    {
        ape = null;
        v1 = false;
        long end = length;
        Span<byte> t = stackalloc byte[3]; // the "TAG" marker of ID3v1
        if (length >= Id3v1Tag.Length && src.Read(length - Id3v1Tag.Length, t) == 3 && t.SequenceEqual("TAG"u8))
        {
            v1 = true;
            end -= Id3v1Tag.Length;
        }

        DefectLog defects = new();
        if (end >= ApeTag.BlockSize && ApeTag.TryReadFromFooter(src, end - ApeTag.BlockSize, out ApeTag? a, null, defects) && !defects.HasErrors)
        {
            // A tag whose items do not parse (a key that is no key, an item past the tag's end) may be audio or other data that merely ends in an APE-looking footer; it is left alone.
            ape = a;
        }

        return v1 || ape is not null;
    }

    // ---- ID3v1 ----

    private static (byte[] Tag, SystemWriteReport Report) BuildId3v1(byte[] old, bool existed, TagEdits edits)
    {
        byte[] b = existed ? (byte[])old.Clone() : NewV1();
        List<CommonField> written = [];
        List<CommonField> removed = [];
        List<(CommonField, string)> not = [];
        List<string> conv = [];
        foreach (CommonField f in edits.TouchedFields)
        {
            IReadOnlyList<string>? ch = edits.FieldChange(f);
            if (ch is null)
            {
                continue;
            }

            string v = ch.Count > 0 ? ch[0] : "";
            switch (f)
            {
                case CommonField.Title: Put(b, 3, 30, v, conv, f); break;
                case CommonField.Artist: Put(b, 33, 30, v, conv, f); break;
                case CommonField.Album: Put(b, 63, 30, v, conv, f); break;
                case CommonField.Date:
                    Put(b, 93, 4, v.Length >= 4 ? v[..4] : v, conv, f);
                    if (v.Length > 4)
                    {
                        conv.Add("Date: ID3v1 holds only the year");
                    }

                    break;
                case CommonField.Comment:
                    bool v11 = b[125] == 0 && b[126] != 0 || existed && b[125] == 0;
                    Put(b, 97, v11 ? 28 : 30, v, conv, f);
                    break;
                case CommonField.TrackNumber:
                    b[125] = 0;
                    b[126] = (byte)(ch.Count > 0 && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? Math.Min(n, 255) : 0);
                    break;
                case CommonField.Genre:
                    b[127] = ch.Count == 0 ? (byte)255 : (byte)Id3Genres.Number(v);
                    if (ch.Count > 0 && b[127] == 255)
                    {
                        conv.Add($"Genre: '{v}' is not in the ID3v1 list and is written as 'unknown'");
                    }

                    break;
                default:
                    not.Add((f, $"ID3v1 has no field for {f}"));
                    continue;
            }

            (ch.Count > 0 ? written : removed).Add(f);
        }

        return (b, new SystemWriteReport(TagSystems.Id3v1, written, removed, not, conv, false));
    }

    private static byte[] NewV1()
    {
        byte[] b = new byte[Id3v1Tag.Length];
        "TAG"u8.CopyTo(b);
        b[127] = 255;
        return b;
    }

    private static void Put(byte[] b, int at, int max, string text, List<string> conv, CommonField f)
    {
        byte[] enc = Encoding.Latin1.GetBytes(text);
        if (enc.Length > max)
        {
            conv.Add($"{f}: cut to {max} bytes");
        }

        Array.Clear(b, at, max);
        Array.Copy(enc, 0, b, at, Math.Min(enc.Length, max));
        if (text.Any(c => c > 0xFF))
        {
            conv.Add($"{f}: characters outside Latin-1 replaced by '?'");
        }
    }

    // ---- APEv2 ----

    private static (byte[] Bytes, SystemWriteReport Report) BuildApe(ApeTag? existing, IMediaSource src, TagEdits edits, MediaLimits limits)
    {
        List<(string Key, byte[] Raw)> items = [];
        if (existing is not null)
        {
            foreach (ApeItem it in existing.Items)
            {
                items.Add((it.Key, ReadRange(src, it.Range)));
            }
        }

        string? Existing(string key) => existing?.Items.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase))?.Native.Text;
        List<KeyOp> ops = [];
        SystemWriteReport rep = KeyValueOps.Build(TagSystems.Ape, edits, Existing, f => f is CommonField.TrackNumber or CommonField.DiscNumber ? PairStyle.Combined : PairStyle.None, null, ops);
        foreach (KeyOp op in ops)
        {
            if (op.Values is null)
            {
                items.RemoveAll(i => string.Equals(i.Key, op.Key, StringComparison.OrdinalIgnoreCase) || op.Aliases.Contains(i.Key, StringComparer.OrdinalIgnoreCase));
                continue;
            }

            byte[] value = Encoding.UTF8.GetBytes(string.Join('\0', op.Values));
            byte[] raw = ApeItemBytes(op.Key, 0, value);
            int at = items.FindIndex(i => string.Equals(i.Key, op.Key, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                items[at] = (items[at].Key, raw);
                items.RemoveAll(i => !ReferenceEquals(i.Raw, raw) && string.Equals(i.Key, op.Key, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                items.Add((op.Key, raw));
            }
        }

        List<string> conv = [.. rep.Conversions];
        foreach (TagEdit e in edits.PictureEdits)
        {
            if (e.Kind == TagEditKind.RemovePictures)
            {
                items.RemoveAll(i => i.Key.StartsWith("Cover Art", StringComparison.OrdinalIgnoreCase));
                continue;
            }

            string role = e.PictureRole is null or PictureType.FrontCover ? "Front" : e.PictureRole == PictureType.BackCover ? "Back" : "Other";
            string key = $"Cover Art ({role})";
            string ext = e.MimeType!.EndsWith("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
            byte[] value = [.. Encoding.UTF8.GetBytes($"cover.{ext}"), 0, .. e.PictureData.Span.ToArray()];
            limits.CheckAttachment(value.Length, 0);
            items.RemoveAll(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
            items.Add((key, ApeItemBytes(key, 1, value)));
            if (e.PictureRole is not (null or PictureType.FrontCover or PictureType.BackCover))
            {
                conv.Add($"pictures: role {e.PictureRole} is written as 'Cover Art (Other)'");
            }
        }

        bool header = existing is null || (existing.Flags & ApeTagAttributes.HasHeader) != 0;
        int body = items.Sum(i => i.Raw.Length);
        int size = body + ApeTag.BlockSize;
        byte[] result = new byte[(header ? ApeTag.BlockSize : 0) + size];
        uint flagsBase = (header ? 0x80000000u : 0u);
        int pos = 0;
        if (header)
        {
            WriteApeBlock(result.AsSpan(0, 32), size, items.Count, flagsBase | 0x20000000u);
            pos = 32;
        }

        foreach ((string _, byte[] raw) in items)
        {
            raw.CopyTo(result, pos);
            pos += raw.Length;
        }

        WriteApeBlock(result.AsSpan(pos, 32), size, items.Count, flagsBase);
        return (result, rep with { Conversions = conv });
    }

    private static void WriteApeBlock(Span<byte> b, int size, int count, uint flags)
    {
        "APETAGEX"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], 2000);
        BinaryPrimitives.WriteUInt32LittleEndian(b[12..], (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(b[16..], (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(b[20..], flags);
    }

    private static byte[] ApeItemBytes(string key, uint type, byte[] value)
    {
        byte[] k = Encoding.ASCII.GetBytes(key);
        byte[] b = new byte[8 + k.Length + 1 + value.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)value.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), type << 1);
        k.CopyTo(b, 8);
        value.CopyTo(b, 9 + k.Length);
        return b;
    }

    // ---- ID3v2 ----

    private sealed class FrameOut(string id, byte[] bytes, string? key, string? description)
    {
        public string Id { get; } = id;

        public byte[] Bytes { get; set; } = bytes;

        public string? Key { get; } = key;

        public string? Description { get; } = description;

        public bool Deleted { get; set; }
    }

    // For tags inside a chunk of a RIFF file: the chunk builder needs the whole payload, so the padding is materialised here.
    internal static (byte[] Tag, SystemWriteReport Report, List<string> Notes, long Padding) BuildId3v2Public(IMediaSource src, Id3Tag? tag, TagEdits edits, TagWriteOptions options, MediaLimits limits, long oldLength)
    {
        (byte[] head, long pad, SystemWriteReport report, List<string> notes) = BuildId3v2(src, tag, edits, options, limits, oldLength);
        byte[] whole = new byte[head.Length + pad];
        head.CopyTo(whole, 0);
        return (whole, report, notes, pad);
    }

    // Returns the tag header and frames, and the number of zero padding bytes that follow them.
    private static (byte[] Tag, long ZeroPad, SystemWriteReport Report, List<string> Notes) BuildId3v2(IMediaSource src, Id3Tag? tag, TagEdits edits, TagWriteOptions options, MediaLimits limits, long oldLength)
    {
        List<string> notes = [];
        int version = options.Id3v2Version == 3 ? 3 : 4;
        List<FrameOut> frames = [];
        bool created = tag is null;
        long padAvail = 0;
        if (tag is not null)
        {
            if ((tag.Header.Flags & Id3TagAttributes.Unsynchronisation) != 0)
            {
                throw new UnsupportedMediaException("The ID3v2 tag uses tag-wide unsynchronisation; it is not edited (rewrite it with another tool first).") { Offset = 0 };
            }

            if (tag.Header.MajorVersion is 3 or 4)
            {
                version = tag.Header.MajorVersion;
            }
            else
            {
                notes.Add($"The ID3v2.{tag.Header.MajorVersion} tag was upgraded to ID3v2.{version}; frames without a counterpart were dropped.");
            }

            if (!tag.ExtendedHeader.IsNone)
            {
                notes.Add("The extended header of the ID3v2 tag was dropped.");
            }

            padAvail = tag.Padding.IsNone ? 0 : tag.Padding.Length;
            foreach (Id3Frame f in tag.Frames)
            {
                byte[] raw = ReadRange(src, f.Range);
                string id = f.Id;
                if (tag.Header.MajorVersion == 2)
                {
                    byte[]? up = UpgradeFrame22(f, raw, version, out string? newId);
                    if (up is null)
                    {
                        notes.Add($"ID3v2.2 frame {f.Id} has no counterpart in ID3v2.{version} and was dropped.");
                        continue;
                    }

                    raw = up;
                    id = newId!;
                }

                // The key a frame is known by follows its upgraded identifier (TT2 becomes TIT2, TXX:desc becomes TXXX:desc).
                string? key = f.Native?.Key;
                if (tag.Header.MajorVersion == 2 && key is not null && key.StartsWith(f.Id, StringComparison.Ordinal))
                {
                    key = id + key[f.Id.Length..];
                }

                frames.Add(new FrameOut(id, raw, key, f.Native?.Description));
            }
        }

        string? Existing(string key)
        {
            FrameOut? fo = frames.FirstOrDefault(x => !x.Deleted && string.Equals(x.Key ?? x.Id, key, StringComparison.OrdinalIgnoreCase));
            return fo is null ? null : TextOfFrame(fo.Bytes);
        }

        List<KeyOp> ops = [];
        SystemWriteReport rep = KeyValueOps.Build(TagSystems.Id3v2, edits, Existing, f => f is CommonField.TrackNumber or CommonField.DiscNumber ? PairStyle.Combined : PairStyle.None, null, ops);
        List<string> conv = [.. rep.Conversions];
        foreach (KeyOp op in ops)
        {
            string key = op.Key;
            if (version == 3 && key == "TDRC")
            {
                key = "TYER";
            }

            if (op.Values is null)
            {
                foreach (FrameOut fo in frames.Where(x => !x.Deleted && Matches(x, key, op.Aliases)))
                {
                    fo.Deleted = true;
                }

                if (version == 3 && key == "TYER")
                {
                    foreach (FrameOut fo in frames.Where(x => x.Id is "TDAT" or "TIME" or "TDRC"))
                    {
                        fo.Deleted = true;
                    }
                }

                continue;
            }

            AddFrames(frames, key, op.Values, version, conv);
        }

        foreach (TagEdit e in edits.PictureEdits)
        {
            if (e.Kind == TagEditKind.RemovePictures)
            {
                foreach (FrameOut fo in frames.Where(x => x.Id == "APIC" && !x.Deleted && (e.PictureRole is null || PictureTypeOf(x.Bytes) == (int)e.PictureRole)))
                {
                    fo.Deleted = true;
                }

                continue;
            }

            limits.CheckAttachment(e.PictureData.Length, 0);
            foreach (FrameOut fo in frames.Where(x => x.Id == "APIC" && !x.Deleted && PictureTypeOf(x.Bytes) == (int)(e.PictureRole ?? PictureType.FrontCover)))
            {
                fo.Deleted = true;
            }

            frames.Add(new FrameOut("APIC", ApicFrame(version, e), null, null));
        }

        // The tag: header, frames, padding. It stays as long as the old tag when it fits, so the audio does not move.
        int framesLength = frames.Where(x => !x.Deleted).Sum(x => x.Bytes.Length);
        int bodyLength;
        if (tag is not null && framesLength + 10 <= oldLength && tag.Header.MajorVersion == version)
        {
            bodyLength = (int)oldLength - 10;
        }
        else
        {
            bodyLength = framesLength + options.PaddingBytes;
        }

        byte[] result = new byte[10 + framesLength];
        "ID3"u8.CopyTo(result);
        result[3] = (byte)version;
        result[4] = 0;
        result[5] = 0;
        WriteSyncsafe(result.AsSpan(6), bodyLength);
        int pos = 10;
        foreach (FrameOut fo in frames.Where(x => !x.Deleted))
        {
            fo.Bytes.CopyTo(result, pos);
            pos += fo.Bytes.Length;
        }

        _ = padAvail;
        return (result, bodyLength - framesLength, rep with { Conversions = conv, Created = created }, notes);
    }

    private static bool Matches(FrameOut f, string key, IReadOnlyList<string> aliases)
    {
        string k = f.Key ?? f.Id;
        if (key is "COMM" or "USLT")
        {
            return f.Id == key && string.IsNullOrEmpty(f.Description);
        }

        return string.Equals(k, key, StringComparison.OrdinalIgnoreCase) || aliases.Contains(k, StringComparer.OrdinalIgnoreCase) || (key.StartsWith("TXXX:", StringComparison.Ordinal) && string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddFrames(List<FrameOut> frames, string key, IReadOnlyList<string> values, int version, List<string> conv)
    {
        string id = key;
        string? desc = null;
        if (key.StartsWith("TXXX:", StringComparison.Ordinal))
        {
            id = "TXXX";
            desc = key[5..];
        }

        string joined;
        if (id[0] == 'T' && values.Count > 1)
        {
            joined = version == 4 ? string.Join('\0', values) : string.Join('/', values);
            if (version == 3)
            {
                conv.Add($"{key}: several values joined with '/' (ID3v2.3 has no multi-value text)");
            }
        }
        else
        {
            joined = values[0];
        }

        byte[] data;
        if (id is "COMM" or "USLT")
        {
            (byte enc, byte[] text) = EncodeText(joined, version);
            byte[] term = Terminator(enc);
            data = [enc, .. "eng"u8.ToArray(), .. term, .. text];
        }
        else if (id == "TXXX")
        {
            (byte enc, byte[] text) = EncodeText(joined, version);
            (_, byte[] d) = EncodeText(desc!, version, enc);
            data = [enc, .. d, .. Terminator(enc), .. text];
        }
        else
        {
            (byte enc, byte[] text) = EncodeText(joined, version);
            data = [enc, .. text];
        }

        byte[] frame = FrameBytes(id, data, version);
        FrameOut? existing = frames.FirstOrDefault(x => !x.Deleted && Matches(x, key, []));
        if (existing is not null)
        {
            existing.Bytes = frame;
            foreach (FrameOut dup in frames.Where(x => x != existing && !x.Deleted && Matches(x, key, [])))
            {
                dup.Deleted = true;
            }
        }
        else
        {
            frames.Add(new FrameOut(id, frame, key, desc ?? (id is "COMM" or "USLT" ? "" : null)));
        }

        if (version == 3 && id == "TYER" && joined.Length > 4)
        {
            conv.Add("Date: ID3v2.3 stores the year in TYER; the day and month go to TDAT");
            frames.RemoveAll(x => x.Id == "TDAT");
            if (joined.Length >= 10 && joined[4] == '-' && joined[7] == '-')
            {
                frames[frames.IndexOf(frames.First(x => x.Id == "TYER"))] = new FrameOut("TYER", FrameBytes("TYER", [0, .. Encoding.Latin1.GetBytes(joined[..4])], 3), "TYER", null);
                frames.Add(new FrameOut("TDAT", FrameBytes("TDAT", [0, .. Encoding.Latin1.GetBytes(string.Concat(joined.AsSpan(8, 2), joined.AsSpan(5, 2)))], 3), "TDAT", null));
            }
            else
            {
                frames[frames.IndexOf(frames.First(x => x.Id == "TYER"))] = new FrameOut("TYER", FrameBytes("TYER", [0, .. Encoding.Latin1.GetBytes(joined[..4])], 3), "TYER", null);
            }
        }
    }

    private static int PictureTypeOf(byte[] frame)
    {
        // header 10, encoding 1, mime to NUL, then the picture type byte
        int i = 11;
        while (i < frame.Length && frame[i] != 0)
        {
            i++;
        }

        return i + 1 < frame.Length ? frame[i + 1] : -1;
    }

    private static byte[] ApicFrame(int version, TagEdit e)
    {
        (byte enc, byte[] desc) = EncodeText(e.Description, version);
        byte[] data = [enc, .. Encoding.Latin1.GetBytes(e.MimeType!), 0, (byte)(e.PictureRole ?? PictureType.FrontCover), .. desc, .. Terminator(enc), .. e.PictureData.Span.ToArray()];
        return FrameBytes("APIC", data, version);
    }

    private static string? TextOfFrame(byte[] frame)
    {
        if (frame.Length < 12 || frame[0] != (byte)'T')
        {
            return null;
        }

        byte enc = frame[10];
        ReadOnlySpan<byte> body = frame.AsSpan(11);
        string s = enc switch
        {
            1 or 2 => body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE ? Encoding.Unicode.GetString(body[2..]) : body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(body[2..]) : Encoding.BigEndianUnicode.GetString(body),
            3 => Encoding.UTF8.GetString(body),
            _ => Encoding.Latin1.GetString(body),
        };
        return s.TrimEnd('\0');
    }

    private static (byte Encoding, byte[] Text) EncodeText(string s, int version, int? forced = null)
    {
        bool latin = s.All(c => c <= 0xFF);
        if (forced is { } f)
        {
            return ((byte)f, f switch { 0 => Encoding.Latin1.GetBytes(s), 1 => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(s)], _ => Encoding.UTF8.GetBytes(s) });
        }

        if (latin)
        {
            return (0, Encoding.Latin1.GetBytes(s));
        }

        return version == 4 ? ((byte)3, Encoding.UTF8.GetBytes(s)) : ((byte)1, [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(s)]);
    }

    private static byte[] Terminator(byte enc) => enc is 1 or 2 ? [0, 0] : [0];

    private static byte[] FrameBytes(string id, byte[] data, int version)
    {
        byte[] b = new byte[10 + data.Length];
        Encoding.ASCII.GetBytes(id, b);
        if (version == 4)
        {
            WriteSyncsafe(b.AsSpan(4), data.Length);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)data.Length);
        }

        data.CopyTo(b, 10);
        return b;
    }

    private static void WriteSyncsafe(Span<byte> d, int v)
    {
        d[0] = (byte)((v >> 21) & 0x7F);
        d[1] = (byte)((v >> 14) & 0x7F);
        d[2] = (byte)((v >> 7) & 0x7F);
        d[3] = (byte)(v & 0x7F);
    }

    private static readonly Dictionary<string, string> Upgrade22 = new()
    {
        ["TT2"] = "TIT2", ["TP1"] = "TPE1", ["TP2"] = "TPE2", ["TAL"] = "TALB", ["TRK"] = "TRCK", ["TPA"] = "TPOS", ["TCO"] = "TCON", ["TCM"] = "TCOM",
        ["TCR"] = "TCOP", ["TEN"] = "TENC", ["TSS"] = "TSSE", ["TT1"] = "TIT1", ["TT3"] = "TIT3", ["TYE"] = "TYER", ["TDA"] = "TDAT", ["TIM"] = "TIME",
        ["TXX"] = "TXXX", ["COM"] = "COMM", ["ULT"] = "USLT", ["TLE"] = "TLEN", ["TBP"] = "TBPM", ["TKE"] = "TKEY", ["TLA"] = "TLAN", ["TPB"] = "TPUB",
        ["TP3"] = "TPE3", ["TP4"] = "TPE4", ["TOA"] = "TOPE", ["TOT"] = "TOAL", ["TXT"] = "TEXT", ["TRC"] = "TSRC", ["WXX"] = "WXXX",
    };

    // Frames of version 2.2 have 6-byte headers; the body of every frame kept here has the same layout in 2.3 and 2.4.
    private static byte[]? UpgradeFrame22(Id3Frame f, byte[] raw, int version, out string? newId)
    {
        newId = null;
        byte[] body = raw.AsSpan(6).ToArray();
        if (f.Id == "PIC")
        {
            if (body.Length < 6)
            {
                return null;
            }

            string fmt = Encoding.Latin1.GetString(body, 1, 3).ToUpperInvariant();
            string mime = fmt == "PNG" ? "image/png" : fmt is "JPG" ? "image/jpeg" : fmt;
            byte[] rest = body[5..];
            newId = "APIC";
            return FrameBytes("APIC", [body[0], .. Encoding.Latin1.GetBytes(mime), 0, body[4], .. rest], version);
        }

        if (!Upgrade22.TryGetValue(f.Id, out string? id))
        {
            return null;
        }

        if (version == 4 && id == "TYER")
        {
            id = "TDRC";
        }
        else if (version == 4 && id is "TDAT" or "TIME")
        {
            return null;
        }

        newId = id;
        return FrameBytes(id, body, version);
    }
}
