using RecursiveAI.OneMedia.Avchd;

namespace RecursiveAI.OneMedia.Privacy;

/// <summary>
/// The strip for MPEG transport streams (including 192-byte M2TS packets): AVCHD camcorders repeat their camera data (MDPM) in an H.264 SEI message
/// at the start of every group of pictures, and that data holds the position. The scan reads the file once in blocks, follows each H.264 video PID, and
/// for every access unit looks in the first bytes of its PES packet for SEI messages of type 5 with the MDPM marker. The position entries
/// (tags 0xB0 to 0xC8 and 0xCA to 0xCC) are overwritten with 0xFF, tag byte included, in place in the transport packets. Nothing else changes:
/// the file keeps its length, and every other byte, including the other bytes of the video NAL units, is copied.
/// </summary>
/// <remarks>
/// 0xFF is used because zeros could join with neighbouring zeros into a start code or need an emulation prevention byte; 0xFF never can
/// (ITU-T H.264 §7.4.1). Zero bytes that are followed by an emulation prevention byte stay as they are, so the escape stays valid. An entry with
/// tag 0xFF is not defined by any reader this library knows, so decoders skip it. The video track's payload hash changes by necessity, because the
/// camera data lives inside the video stream.
/// </remarks>
internal static class TsStrip
{
    private const int Window = 8192;
    private const int Block = 1 << 20;
    private static ReadOnlySpan<byte> Uuid => [0x17, 0xEE, 0x8C, 0x60, 0xF8, 0x4D, 0x11, 0xD9, 0x8C, 0xD6, 0x08, 0x00, 0x20, 0x0C, 0x9A, 0x66];

    private sealed class Pes
    {
        public readonly byte[] Bytes = new byte[Window];
        public readonly long[] Offsets = new long[Window];
        public int Length;
        public bool Full;
    }

    public static StripPlan Plan(IMediaSource source, MediaFile file, StripOptions options, CancellationToken ct)
    {
        StripCategories cats = options.Categories;
        PatchSet patches = new();
        StripSink sink = new(patches);
        Dictionary<int, Pes> pids = [];
        foreach (MediaTrack t in file.Tracks)
        {
            if (t.Kind == TrackKind.Video && t.Codec == CodecId.H264 && t.Id is >= 0 and < 0x2000)
            {
                pids[(int)t.Id] = new Pes();
            }
        }

        if ((cats & StripCategories.Location) != 0 && pids.Count > 0 && TryStride(source, out int stride, out int prefix))
        {
            Scan(source, stride, prefix, pids, sink, ct);
        }
        else if ((cats & StripCategories.Location) != 0 && pids.Count > 0)
        {
            sink.Leftover(StripCategories.Location, "mdpm", "camera data", 0, ByteRange.None, "The packet size of the transport stream could not be determined, so the video was not searched.");
        }

        List<StripLeftover> found = [];
        StripInventory.DataTracks(file, cats, found);
        foreach (StripLeftover l in found)
        {
            sink.Leftover(l.Category, l.System, l.Name, l.TrackId, l.Range, l.Reason);
        }

        sink.AddNote("MPEG transport streams have no tag system; the only position data looked for is the AVCHD camera data (MDPM) in the H.264 video.");
        sink.AddNote("The video payload changes, by necessity: MDPM sits inside the H.264 SEI. Only the position entries' bytes differ; the decoded pictures are identical.");
        sink.AddNote("No serial number field of MDPM is documented in the public tag descriptions; the maker and model entries (0xE0 and the Sony model entries) stay.");
        sink.AddNote("Layout is unchanged: every changed range keeps its length and no other byte moved.");
        return new StripPlan(patches.ToPlan(source.Length), new StripReport(cats, sink.Entries(), sink.Leftovers, sink.Notes));
    }

    private static bool TryStride(IMediaSource source, out int stride, out int prefix)
    {
        byte[] head = new byte[5 * 204 + 8];
        int n = source.Read(0, head);
        foreach ((int s, int p) in new[] { (188, 0), (192, 4), (204, 0) })
        {
            bool ok = n >= p + (4 * s) + 1;
            for (int k = 0; ok && k < 5; k++)
            {
                ok = p + (k * s) < n && head[p + (k * s)] == 0x47;
            }

            if (ok)
            {
                stride = s;
                prefix = p;
                return true;
            }
        }

        stride = prefix = 0;
        return false;
    }

    private static void Scan(IMediaSource source, int stride, int prefix, Dictionary<int, Pes> pids, StripSink sink, CancellationToken ct)
    {
        byte[] block = new byte[Block - (Block % stride) + stride];
        long length = source.Length;
        long pos = 0;
        while (pos + stride <= length)
        {
            ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(block.Length - (block.Length % stride), length - pos);
            int got = source.Read(pos, block.AsSpan(0, want), ct);
            if (got < stride)
            {
                break;
            }

            int at = 0;
            while (at + stride <= got)
            {
                int p = at + prefix;
                if (block[p] != 0x47)
                {
                    // A lost sync byte (bytes dropped or inserted in the recording): look for the next place where sync bytes recur one packet apart and carry
                    // on from there, so the packets after the damage are still searched. The bytes in between belong to no packet.
                    int j = at + 1;
                    while (j + stride <= got && !(block[j + prefix] == 0x47 && (j + prefix + stride >= got || block[j + prefix + stride] == 0x47)))
                    {
                        j++;
                    }

                    at = j + stride <= got ? j : Math.Max(at + 1, got - stride + 1);
                    continue;
                }

                int pid = ((block[p + 1] & 0x1F) << 8) | block[p + 2];
                if (!pids.TryGetValue(pid, out Pes? pes))
                {
                    at += stride;
                    continue;
                }

                // H.222.0 §2.4.3.2: payload_unit_start_indicator marks a packet that begins a PES packet.
                if ((block[p + 1] & 0x40) != 0)
                {
                    Flush(pid, pes, sink);
                    pes.Length = 0;
                    pes.Full = false;
                }

                int afc = (block[p + 3] >> 4) & 3;
                int payload = 4;
                if ((afc & 2) != 0)
                {
                    payload += 1 + block[p + 4]; // adaptation_field_length counts the bytes after it
                }

                if ((afc & 1) == 0 || payload >= 188)
                {
                    at += stride;
                    continue;
                }

                int take = Math.Min(188 - payload, Window - pes.Length);
                for (int i = 0; i < take; i++)
                {
                    pes.Bytes[pes.Length + i] = block[p + payload + i];
                    pes.Offsets[pes.Length + i] = pos + p + payload + i;
                }

                pes.Length += take;
                pes.Full |= take < 188 - payload;
                at += stride;
            }

            pos += at;
        }

        foreach ((int pid, Pes pes) in pids)
        {
            Flush(pid, pes, sink);
        }
    }

    private static void Flush(int pid, Pes pes, StripSink sink)
    {
        byte[] b = pes.Bytes;
        int n = pes.Length;
        if (n < 12 || b[0] != 0 || b[1] != 0 || b[2] != 1 || b[3] < 0xE0 || b[3] > 0xEF)
        {
            return;
        }

        int i = 9 + b[8];
        for (; i + 4 <= n; i++)
        {
            if (b[i] != 0 || b[i + 1] != 0 || b[i + 2] != 1)
            {
                continue;
            }

            int type = b[i + 3] & 0x1F;
            if (type != 6)
            {
                continue;
            }

            int from = i + 4, to = from;
            while (to + 3 <= n && !(b[to] == 0 && b[to + 1] == 0 && b[to + 2] <= 1))
            {
                to++;
            }

            if (to + 3 > n)
            {
                if (pes.Full)
                {
                    sink.Leftover(StripCategories.Location, "mdpm", "camera data", pid, new ByteRange(pes.Offsets[from], n - from), "An SEI message runs past the first " + Window + " bytes of its video packet and was not searched.");
                }

                to = n;
            }

            Sei(pid, pes, from, to, sink);
            i = to - 1;
        }
    }

    // ITU-T H.264 §7.4.1: a 00 00 03 inside a NAL unit has the 03 inserted by the encoder; the zeros before it are kept as they are.
    private static void Sei(int pid, Pes pes, int from, int to, StripSink sink)
    {
        int max = Math.Min(to - from, 4096);
        byte[] un = new byte[max];
        int[] map = new int[max];
        bool[] keep = new bool[max];
        int len = 0, zeros = 0;
        for (int i = from; i < to && len < max; i++)
        {
            byte v = pes.Bytes[i];
            if (zeros >= 2 && v == 3)
            {
                keep[len - 1] = keep[len - 2] = true;
                zeros = 0;
                continue;
            }

            zeros = v == 0 ? zeros + 1 : 0;
            un[len] = v;
            map[len++] = i;
        }

        int pos = 0;
        while (pos + 2 <= len)
        {
            int type = 0, size = 0;
            while (pos < len && un[pos] == 0xFF)
            {
                type += 255;
                pos++;
            }

            if (pos >= len)
            {
                return;
            }

            type += un[pos++];
            while (pos < len && un[pos] == 0xFF)
            {
                size += 255;
                pos++;
            }

            if (pos >= len)
            {
                return;
            }

            size += un[pos++];
            if (size > len - pos)
            {
                return;
            }

            if (type == 5 && size >= 21 && un.AsSpan(pos, 16).SequenceEqual(Uuid) && un.AsSpan(pos + 16, 4).SequenceEqual("MDPM"u8))
            {
                int count = Math.Min(un[pos + 20], (size - 21) / 5);
                for (int e = 0; e < count; e++)
                {
                    int at = pos + 21 + (5 * e);
                    byte tag = un[at];
                    if (tag is >= 0xB0 and <= 0xC8 or >= 0xCA and <= 0xCC)
                    {
                        Neutralise(pid, pes, map, keep, at, 5, tag, sink);
                    }
                }
            }

            pos += size;
        }
    }

    private static void Neutralise(int pid, Pes pes, int[] map, bool[] keep, int at, int count, byte tag, StripSink sink)
    {
        int run = -1;
        for (int k = 0; k <= count; k++)
        {
            bool writable = k < count && !keep[at + k];
            if (writable && run < 0)
            {
                run = k;
            }
            else if (!writable && run >= 0)
            {
                // Bytes that are contiguous in the unescaped data are contiguous in the file unless an escape byte lies between them.
                int a = run;
                for (int m = run; m < k; m++)
                {
                    bool last = m == k - 1 || pes.Offsets[map[at + m + 1]] != pes.Offsets[map[at + m]] + 1;
                    if (last)
                    {
                        sink.Fill(StripCategories.Location, "mdpm", "MDPM 0x" + tag.ToString("X2", System.Globalization.CultureInfo.InvariantCulture), pid, pes.Offsets[map[at + a]], m - a + 1, 0xFF);
                        a = m + 1;
                    }
                }

                run = -1;
            }
        }
    }
}
