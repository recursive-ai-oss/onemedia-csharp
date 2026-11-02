using System.Linq;
using System.Buffers.Binary;
using RecursiveAI.OneMedia.Ogg;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>
/// Plans tag edits in an Ogg file (RFC 3533) whose first logical stream is Vorbis, Opus or FLAC-in-Ogg: the comment header packet is rewritten, the header
/// packets are laid out in new pages, and, when the number of header pages changes, the later pages of that stream get new sequence numbers and checksums
/// as they are copied, one page at a time.
/// </summary>
internal static class OggTagWriter
{
    private sealed record Page(long Offset, int Length, byte Flags, ulong Granule, uint Serial, uint Sequence, byte[] Lacing, int BodyOffset);

    public static TagWritePlan Plan(IMediaSource src, TagEdits edits, TagWriteOptions options, MediaLimits limits, CancellationToken ct)
    {
        Page first = ReadPage(src, 0)?.Page ?? throw new MediaFormatException("The file does not start with an Ogg page.");
        byte[] firstBody = ReadPage(src, 0)!.Value.Body;
        int headersNeeded;
        string kind;
        if (firstBody.AsSpan().StartsWith("\u0001vorbis"u8))
        {
            (headersNeeded, kind) = (2, "vorbis");
        }
        else if (firstBody.AsSpan().StartsWith("OpusHead"u8))
        {
            (headersNeeded, kind) = (1, "opus");
        }
        else if (firstBody.Length >= 13 && firstBody[0] == 0x7F && firstBody.AsSpan(1, 4).SequenceEqual("FLAC"u8))
        {
            int n = BinaryPrimitives.ReadUInt16BigEndian(firstBody.AsSpan(7));
            if (n == 0)
            {
                throw new UnsupportedMediaException("The Ogg FLAC stream does not state its header count; its tags are not edited.");
            }

            (headersNeeded, kind) = (n, "flac");
        }
        else
        {
            throw new UnsupportedMediaException("Tags are written for Vorbis, Opus and FLAC streams in Ogg; this stream is another codec.");
        }

        // Gather the header packets, which end exactly at a page boundary.
        List<byte[]> packets = [];
        List<byte> current = [];
        long pos = first.Length;
        int pageCount = 0;
        uint lastSeq = first.Sequence;
        while (packets.Count < headersNeeded)
        {
            ct.ThrowIfCancellationRequested();
            (Page Page, byte[] Body)? r = ReadPage(src, pos);
            if (r is null || r.Value.Page.Serial != first.Serial)
            {
                throw new UnsupportedMediaException("The header pages of the first Ogg stream are interleaved with another stream or cut off; its tags are not edited.") { Offset = pos };
            }

            Page page = r.Value.Page;
            int at = 0;
            foreach (byte lace in page.Lacing)
            {
                current.AddRange(r.Value.Body.AsSpan(at, lace).ToArray());
                at += lace;
                limits.CheckAttachment(current.Count, pos);
                if (lace < 255)
                {
                    packets.Add([.. current]);
                    current.Clear();
                }
            }

            pos += page.Length;
            pageCount++;
            lastSeq = page.Sequence;
            if (packets.Count >= headersNeeded && current.Count != 0)
            {
                throw new UnsupportedMediaException("Audio data starts inside the last header page; its tags are not edited.") { Offset = pos };
            }
        }

        // Vorbis has three header packets (identification, comment, setup); the first was on page 0.
        int commentIndex = kind switch
        {
            "vorbis" => 0,
            "opus" => 0,
            _ => packets.FindIndex(p => p.Length > 0 && (p[0] & 0x7F) == 4),
        };
        if (commentIndex < 0)
        {
            throw new UnsupportedMediaException("The Ogg FLAC stream has no Vorbis comment block.");
        }

        byte[] old = packets[commentIndex];
        ReadOnlySpan<byte> body = kind switch
        {
            "vorbis" => old.Length >= 7 ? old.AsSpan(7, Math.Max(0, old.Length - 8)) : default,
            "opus" => old.Length >= 8 ? old.AsSpan(8) : default,
            _ => old.Length >= 4 ? old.AsSpan(4) : default,
        };
        VorbisCommentEditor.Result cr = VorbisCommentEditor.Edit(body, edits, true, limits, false);
        packets[commentIndex] = kind switch
        {
            "vorbis" => [3, .. "vorbis"u8.ToArray(), .. cr.Block, 1],
            "opus" => OpusTagsPacket(cr.Block, old.Length, options.PaddingBytes),
            _ => [(byte)(old[0] & 0x7F | (old[0] & 0x80)), (byte)(cr.Block.Length >> 16), (byte)(cr.Block.Length >> 8), (byte)cr.Block.Length, .. cr.Block],
        };

        // Lay the packets out in pages.
        List<byte[]> pages = Repage(packets, first.Serial, first.Sequence + 1);
        int delta = pages.Count - pageCount;
        PieceList list = new();
        list.AddCopy(0, first.Length);
        long outStart = list.Length;
        foreach (byte[] p in pages)
        {
            list.AddData(p);
        }

        list.NoteChange("Ogg header pages", new ByteRange(first.Length, pos - first.Length), outStart);
        long restLength = src.Length - pos;
        if (delta == 0 || restLength == 0)
        {
            list.AddCopy(pos, restLength);
        }
        else
        {
            list.AddGenerated(restLength, new Renumber(pos, first.Serial, delta));
        }

        EditPlan plan = list.ToPlan(src.Length, 0, 0, Math.Max(0, pages.Sum(p => p.Length) - (pos - first.Length)));
        return new TagWritePlan(plan, new TagWriteReport([cr.Report], delta != 0 ? [$"The header pages changed from {pageCount} to {pages.Count}; later pages of the stream were renumbered and re-checksummed."] : []));
    }

    // RFC 7845 §5.2: the comment header may end in zero padding after the user comment list. A packet that fits the old length keeps it, so the
    // pages stay as they were and the edit can be saved in place; a packet that grows leaves some padding for the next edit.
    private static byte[] OpusTagsPacket(byte[] comments, int oldLength, int growthPadding)
    {
        int used = 8 + comments.Length;
        int total = used <= oldLength ? oldLength : used + Math.Max(0, growthPadding);
        byte[] packet = new byte[total];
        "OpusTags"u8.CopyTo(packet);
        comments.CopyTo(packet, 8);
        return packet;
    }

    private static (Page Page, byte[] Body)? ReadPage(IMediaSource src, long offset)
    {
        Span<byte> h = stackalloc byte[27]; // the fixed page header
        if (!src.TryReadExactly(offset, h, CancellationToken.None) || !h[..4].SequenceEqual("OggS"u8))
        {
            return null;
        }

        int segs = h[26];
        byte[] lacing = new byte[segs];
        if (!src.TryReadExactly(offset + 27, lacing, CancellationToken.None))
        {
            return null;
        }

        int bodyLen = 0;
        foreach (byte l in lacing)
        {
            bodyLen += l;
        }

        byte[] body = new byte[bodyLen];
        if (!src.TryReadExactly(offset + 27 + segs, body, CancellationToken.None))
        {
            return null;
        }

        return (new Page(offset, 27 + segs + bodyLen, h[5], BinaryPrimitives.ReadUInt64LittleEndian(h[6..]), BinaryPrimitives.ReadUInt32LittleEndian(h[14..]), BinaryPrimitives.ReadUInt32LittleEndian(h[18..]), lacing, 27 + segs), body);
    }

    // RFC 3533 §6: a packet's lacing is runs of 255 and a final value below 255; a page holds at most 255 lacing values.
    private static List<byte[]> Repage(List<byte[]> packets, uint serial, uint firstSequence)
    {
        List<byte[]> pages = [];
        List<byte> lacing = [];
        List<byte> body = [];
        bool continued = false;
        uint seq = firstSequence;
        bool completedOnPage = false;

        void Flush(bool nextContinued)
        {
            byte[] page = new byte[27 + lacing.Count + body.Count];
            "OggS"u8.CopyTo(page);
            page[5] = (byte)(continued ? 1 : 0);
            BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(6), completedOnPage ? 0UL : ulong.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), seq++);
            page[26] = (byte)lacing.Count;
            lacing.CopyTo(page, 27);
            body.CopyTo(page, 27 + lacing.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), OggPageReader.Crc(page));
            pages.Add(page);
            lacing.Clear();
            body.Clear();
            continued = nextContinued;
            completedOnPage = false;
        }

        foreach (byte[] packet in packets)
        {
            int offset = 0;
            while (true)
            {
                if (lacing.Count == 255)
                {
                    Flush(false);
                }

                int remaining = packet.Length - offset;
                int segs = remaining / 255 + 1; // the last segment is below 255 (zero when the length is a multiple of 255)
                int room = 255 - lacing.Count;
                int take = Math.Min(segs, room);
                bool finishes = take == segs;
                for (int i = 0; i < take; i++)
                {
                    int n = finishes && i == take - 1 ? remaining % 255 : 255;
                    lacing.Add((byte)n);
                    body.AddRange(packet.AsSpan(offset, n).ToArray());
                    offset += n;
                }

                if (finishes)
                {
                    completedOnPage = true;
                    break;
                }

                Flush(true);
            }
        }

        if (lacing.Count > 0)
        {
            Flush(false);
        }

        return pages;
    }

    private sealed class Renumber(long start, uint serial, int delta) : IPieceGenerator
    {
        public void Write(Stream output, IMediaSource source, CancellationToken cancellationToken)
        {
            long pos = start;
            byte[] buffer = new byte[OggPageReader.MaxPageSize];
            while (pos < source.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Span<byte> h = buffer.AsSpan(0, 27);
                if (!source.TryReadExactly(pos, h, cancellationToken) || !h[..4].SequenceEqual("OggS"u8))
                {
                    // Not a page boundary any more (trailing junk): copy the rest unchanged.
                    int n;
                    while ((n = source.Read(pos, buffer, cancellationToken)) > 0)
                    {
                        output.Write(buffer, 0, n);
                        pos += n;
                    }

                    return;
                }

                int segs = buffer[26];
                source.ReadExactly(pos + 27, buffer.AsSpan(27, segs), cancellationToken);
                int body = 0;
                for (int i = 0; i < segs; i++)
                {
                    body += buffer[27 + i];
                }

                int total = 27 + segs + body;
                source.ReadExactly(pos + 27 + segs, buffer.AsSpan(27 + segs, body), cancellationToken);
                if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(14)) == serial)
                {
                    uint seq = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(18));
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(18), (uint)(seq + delta));
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(22), OggPageReader.Crc(buffer.AsSpan(0, total)));
                }

                output.Write(buffer, 0, total);
                pos += total;
            }
        }
    }
}
