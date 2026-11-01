using System.Linq;
using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>Vorbis comment editing shared by FLAC and Ogg: the comment list is parsed to raw entries, edited by key, and written back.</summary>
internal static class VorbisCommentEditor
{
    public const string PictureKey = "METADATA_BLOCK_PICTURE";

    public sealed class Result(byte[] block, SystemWriteReport report)
    {
        public byte[] Block { get; } = block;

        public SystemWriteReport Report { get; } = report;
    }

    /// <param name="existing">The comment body (vendor, count, entries; no framing byte), or empty for a new block.</param>
    /// <param name="edits">The edits.</param>
    /// <param name="picturesInComments">True for Ogg (pictures as METADATA_BLOCK_PICTURE entries); false for FLAC (picture blocks are the writer's).</param>
    /// <param name="limits">Resource limits.</param>
    /// <param name="created">True when the file had no comment block.</param>
    public static Result Edit(ReadOnlySpan<byte> existing, TagEdits edits, bool picturesInComments, MediaLimits limits, bool created)
    {
        string vendor = "OneMedia";
        byte[] vendorBytes = Encoding.UTF8.GetBytes(vendor);
        List<byte[]> entries = [];
        if (existing.Length >= 8)
        {
            int vl = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(existing), (uint)(existing.Length - 4));
            vendorBytes = existing.Slice(4, vl).ToArray();
            int pos = 4 + vl;
            if (existing.Length >= pos + 4)
            {
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(existing[pos..]);
                pos += 4;
                for (uint i = 0; i < count && pos + 4 <= existing.Length; i++)
                {
                    int l = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(existing[pos..]), (uint)(existing.Length - pos - 4));
                    entries.Add(existing.Slice(pos + 4, l).ToArray());
                    pos += 4 + l;
                }
            }
        }

        string? Existing(string key)
        {
            foreach (byte[] e in entries)
            {
                int eq = Array.IndexOf(e, (byte)'=');
                if (eq > 0 && string.Equals(Encoding.ASCII.GetString(e, 0, eq), key, StringComparison.OrdinalIgnoreCase))
                {
                    return Encoding.UTF8.GetString(e, eq + 1, e.Length - eq - 1);
                }
            }

            return null;
        }

        List<KeyOp> ops = [];
        SystemWriteReport rep = KeyValueOps.Build(TagSystems.VorbisComment, edits, Existing, f => f is CommonField.TrackNumber or CommonField.DiscNumber ? PairStyle.Separate : PairStyle.None, null, ops);
        foreach (KeyOp op in ops)
        {
            bool Is(byte[] e, string k)
            {
                int eq = Array.IndexOf(e, (byte)'=');
                if (eq <= 0)
                {
                    return false;
                }

                string name = Encoding.ASCII.GetString(e, 0, eq);
                return string.Equals(name, k, StringComparison.OrdinalIgnoreCase);
            }

            if (op.Values is null)
            {
                entries.RemoveAll(e => Is(e, op.Key) || op.Aliases.Any(a => Is(e, a)));
                continue;
            }

            int at = entries.FindIndex(e => Is(e, op.Key));
            entries.RemoveAll(e => Is(e, op.Key));
            List<byte[]> fresh = [.. op.Values.Select(v => Encoding.UTF8.GetBytes(op.Key + "=" + v))];
            entries.InsertRange(at < 0 ? entries.Count : at, fresh);
        }

        List<string> conv = [.. rep.Conversions];
        if (picturesInComments)
        {
            foreach (TagEdit e in edits.PictureEdits)
            {
                if (e.Kind == TagEditKind.RemovePictures)
                {
                    entries.RemoveAll(x => IsPicture(x) && (e.PictureRole is null || PictureRoleOf(x) == (int)e.PictureRole));
                    continue;
                }

                limits.CheckAttachment(e.PictureData.Length, 0);
                entries.RemoveAll(x => IsPicture(x) && PictureRoleOf(x) == (int)(e.PictureRole ?? PictureType.FrontCover));
                entries.Add(Encoding.ASCII.GetBytes(PictureKey + "=" + Convert.ToBase64String(FlacPicture(e))));
            }
        }

        using MemoryStream ms = new();
        Span<byte> w = stackalloc byte[4]; // one 32-bit length at a time
        BinaryPrimitives.WriteUInt32LittleEndian(w, (uint)vendorBytes.Length);
        ms.Write(w);
        ms.Write(vendorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(w, (uint)entries.Count);
        ms.Write(w);
        foreach (byte[] e in entries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(w, (uint)e.Length);
            ms.Write(w);
            ms.Write(e);
        }

        return new Result(ms.ToArray(), rep with { Conversions = conv, Created = created });
    }

    private static bool IsPicture(byte[] e) => e.Length > PictureKey.Length && e.AsSpan(0, PictureKey.Length + 1).SequenceEqual(Encoding.ASCII.GetBytes(PictureKey + "="));

    private static int PictureRoleOf(byte[] e)
    {
        try
        {
            string b64 = Encoding.ASCII.GetString(e, PictureKey.Length + 1, Math.Min(e.Length - PictureKey.Length - 1, 8));
            b64 = b64[..(b64.Length / 4 * 4)];
            byte[] head = Convert.FromBase64String(b64);
            return head.Length >= 4 ? (int)BinaryPrimitives.ReadUInt32BigEndian(head) : -1;
        }
        catch (FormatException)
        {
            return -1;
        }
    }

    /// <summary>The FLAC PICTURE block body (RFC 9639 §8.8). Width, height and depth are written as 0 (unknown) because reading them needs the image header.</summary>
    public static byte[] FlacPicture(TagEdit e)
    {
        byte[] mime = Encoding.ASCII.GetBytes(e.MimeType!);
        byte[] desc = Encoding.UTF8.GetBytes(e.Description);
        byte[] b = new byte[32 + mime.Length + desc.Length + e.PictureData.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)(e.PictureRole ?? PictureType.FrontCover));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)mime.Length);
        mime.CopyTo(b, 8);
        int p = 8 + mime.Length;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), (uint)desc.Length);
        desc.CopyTo(b, p + 4);
        p += 4 + desc.Length + 16; // width, height, depth, colours: 0
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), (uint)e.PictureData.Length);
        e.PictureData.Span.CopyTo(b.AsSpan(p + 4));
        return b;
    }
}

/// <summary>
/// Plans tag edits in a native FLAC file (RFC 9639 §8): the Vorbis comment block and PICTURE blocks. Other blocks are copied untouched; the
/// padding blocks are merged into one at the end, sized so that the file keeps its layout when the new blocks fit the old metadata.
/// </summary>
internal static class FlacTagWriter
{
    public static TagWritePlan Plan(IMediaSource src, long start, TagEdits edits, TagWriteOptions options, MediaLimits limits, CancellationToken ct)
    {
        Span<byte> mark = stackalloc byte[4]; // the "fLaC" marker
        src.ReadExactly(start, mark, ct);
        long pos = start + 4;
        List<(byte Type, ByteRange Range, ByteRange Body)> blocks = [];
        bool last;
        do
        {
            ct.ThrowIfCancellationRequested();
            byte[] h = new byte[4]; // a block header
            if (!src.TryReadExactly(pos, h, ct))
            {
                throw new MediaFormatException("The FLAC metadata ends inside a block header.") { Offset = pos };
            }

            last = (h[0] & 0x80) != 0;
            int len = (h[1] << 16) | (h[2] << 8) | h[3];
            if (pos + 4 + len > src.Length)
            {
                throw new MediaFormatException("A FLAC metadata block runs past the end of the file.") { Offset = pos };
            }

            blocks.Add(((byte)(h[0] & 0x7F), new ByteRange(pos, 4 + len), new ByteRange(pos + 4, len)));
            pos += 4 + len;
        }
        while (!last);

        long metaEnd = pos;
        long oldMeta = metaEnd - (start + 4);
        long oldPadding = blocks.Where(b => b.Type == 1).Sum(b => b.Range.Length);

        (byte Type, ByteRange Range, ByteRange Body) vc = blocks.FirstOrDefault(b => b.Type == 4);
        bool hasVc = vc.Range.Length != 0;
        byte[] existingBody = hasVc ? new byte[vc.Body.Length] : [];
        if (hasVc)
        {
            limits.CheckTag(vc.Body.Length, vc.Body.Offset);
            src.ReadExactly(vc.Body.Offset, existingBody, ct);
        }

        VorbisCommentEditor.Result cr = VorbisCommentEditor.Edit(existingBody, edits, false, limits, !hasVc);
        byte[] newVc = Block(4, cr.Block, false);

        // Picture edits: drop replaced blocks, append new ones.
        HashSet<long> dropped = [];
        List<byte[]> newPictures = [];
        List<string> conv = [.. cr.Report.Conversions];
        foreach (TagEdit e in edits.PictureEdits)
        {
            foreach (var b in blocks.Where(b => b.Type == 6 && !dropped.Contains(b.Range.Offset)))
            {
                byte[] t = new byte[4]; // the picture type word
                src.ReadExactly(b.Body.Offset, t, ct);
                int role = (int)BinaryPrimitives.ReadUInt32BigEndian(t);
                if (e.Kind == TagEditKind.RemovePictures ? e.PictureRole is null || role == (int)e.PictureRole : role == (int)(e.PictureRole ?? PictureType.FrontCover))
                {
                    dropped.Add(b.Range.Offset);
                }
            }

            if (e.Kind == TagEditKind.SetPicture)
            {
                limits.CheckAttachment(e.PictureData.Length, 0);
                newPictures.Add(Block(6, VorbisCommentEditor.FlacPicture(e), false));
            }
        }

        // Assemble: pieces in order, with the padding last.
        List<object> parts = []; // ByteRange (copy) or byte[] (data)
        foreach (var b in blocks)
        {
            if (b.Type == 1 || dropped.Contains(b.Range.Offset))
            {
                continue;
            }

            if (b.Type == 4)
            {
                parts.Add(newVc);
            }
            else
            {
                parts.Add(b.Range);
            }
        }

        if (!hasVc)
        {
            parts.Insert(Math.Min(1, parts.Count), newVc);
        }

        parts.AddRange(newPictures);
        long content = parts.Sum(p => p is ByteRange r ? r.Length : ((byte[])p).Length);
        long padding;
        if (content + 4 <= oldMeta && !(oldMeta - content < 4))
        {
            padding = oldMeta - content;
        }
        else if (content == oldMeta)
        {
            padding = 0;
        }
        else
        {
            padding = options.PaddingBytes + 4;
        }

        bool fits = content + padding == oldMeta;
        long cur = start + 4;
        PieceList list = new();
        list.AddCopy(0, start + 4);
        long outStart = list.Length;
        for (int i = 0; i < parts.Count; i++)
        {
            bool isLast = i == parts.Count - 1 && padding == 0;
            if (parts[i] is ByteRange r)
            {
                // Block headers carry the "last block" bit: rewrite the header byte when it differs from the source's.
                byte[] hb = new byte[1]; // one header byte
                src.ReadExactly(r.Offset, hb, ct);
                byte want = (byte)((hb[0] & 0x7F) | (isLast ? 0x80 : 0));
                if (fits && r.Offset != cur)
                {
                    // The block moved inside metadata that keeps its size: write it as data so the plan can be applied in place.
                    limits.CheckAttachment(r.Length, r.Offset);
                    byte[] moved = new byte[r.Length];
                    src.ReadExactly(r.Offset, moved, ct);
                    moved[0] = want;
                    list.AddData(moved);
                }
                else if (want != hb[0])
                {
                    list.AddData(new[] { want });
                    list.AddCopy(r.Offset + 1, r.Length - 1);
                }
                else
                {
                    list.AddCopy(r.Offset, r.Length);
                }
            }
            else
            {
                byte[] d = (byte[])((byte[])parts[i]).Clone();
                d[0] = (byte)((d[0] & 0x7F) | (isLast ? 0x80 : 0));
                list.AddData(d);
            }

            cur += parts[i] is ByteRange rr ? rr.Length : ((byte[])parts[i]).Length;
        }

        if (padding > 0)
        {
            byte[] pad = new byte[padding];
            pad[0] = 0x81;
            pad[1] = (byte)((padding - 4) >> 16);
            pad[2] = (byte)((padding - 4) >> 8);
            pad[3] = (byte)(padding - 4);
            list.AddData(pad);
        }

        list.NoteChange("FLAC metadata blocks", new ByteRange(start + 4, oldMeta), outStart);
        list.AddCopy(metaEnd, src.Length - metaEnd);
        long required = Math.Max(0, content - (oldMeta - oldPadding));
        EditPlan plan = list.ToPlan(src.Length, Math.Min(oldPadding, Math.Max(0, content - (oldMeta - oldPadding))), oldPadding, required);
        List<SystemWriteReport> reports = [cr.Report with { Conversions = conv }];
        if (edits.PictureEdits.Any())
        {
            reports[0] = reports[0] with { Written = reports[0].Written };
        }

        // An ID3v2 tag in front of the stream is copied as it is, so readers that prefer it keep seeing the old values.
        List<string> notes = start > 0 ? ["An ID3v2 tag in front of the FLAC stream was not edited; it still holds the old values."] : [];
        return new TagWritePlan(plan, new TagWriteReport(reports, notes));
    }

    private static byte[] Block(byte type, byte[] body, bool last)
    {
        byte[] b = new byte[4 + body.Length];
        b[0] = (byte)(type | (last ? 0x80 : 0));
        b[1] = (byte)(body.Length >> 16);
        b[2] = (byte)(body.Length >> 8);
        b[3] = (byte)body.Length;
        body.CopyTo(b, 4);
        return b;
    }
}
