using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Repair;

/// <summary>
/// A progressive file whose media data is cut off but whose movie box survived: the sample tables are cut back to the samples that lie
/// wholly inside the file (ISO/IEC 14496-12 §8.6 sample tables, §8.7 sample table box). The new movie box is never longer than the old one,
/// so it is padded with a <c>free</c> box to the old size and every chunk offset in the file stays where it is.
/// </summary>
internal static partial class IsoRepair
{
    private const uint Ctts = 0x63747473, Stss = 0x73747373, Stps = 0x73747073, Sdtp = 0x73647470;

    private sealed class SampleCut
    {
        public long Samples;       // samples kept
        public int Chunks;         // chunks kept, the last one possibly with fewer samples than the table says
        public long PartialSamples; // samples kept of the last chunk when it is not whole, else 0
        public long Duration;      // media duration of the kept samples
        public bool Changed;
        public long FirstLostOffset = -1;
        public long FirstLostSize;
        public int ElstEntries;
        public long ElstSum;
        public bool HasElst;
    }

    // Returns the replacement movie box, padded to the old size, or null when the tables cannot be cut here (a warning says why).
    private static byte[]? ShrinkMovie(byte[] m, BoxHeader moov, List<TrackInfo> tracks, long[][] offsets, long fileLength, long movieTimescale,
        long mvhdDurationAt, int mvhdWidth, long mvhdDuration, RepairReportBuilder report, out SampleCut[] cuts)
    {
        cuts = new SampleCut[tracks.Count];
        bool any = false;
        for (int i = 0; i < tracks.Count; i++)
        {
            cuts[i] = CutTrack(m, tracks[i], offsets[i], fileLength);
            any |= cuts[i].Changed;
        }

        if (!any)
        {
            return null;
        }

        // the edit lists decide the track durations, so they are cut first
        for (int i = 0; i < tracks.Count; i++)
        {
            if (cuts[i].Changed && cuts[i].Samples > 0)
            {
                _ = EditsFor(m, tracks[i], cuts[i], movieTimescale);
            }
        }

        // movie header duration: the longest track
        long movieDuration = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (cuts[i].Changed && cuts[i].Samples == 0)
            {
                continue;
            }

            movieDuration = Math.Max(movieDuration, cuts[i].Changed ? TrackDuration(tracks[i], cuts[i], movieTimescale) : tracks[i].TkhdDuration);
        }

        SampleCut[] local = cuts;
        byte[] rebuilt = RewriteContainer(m, 0, moov.HeaderSize, m.Length, c =>
        {
            if (c.Type == Mvhd && mvhdDurationAt >= 0 && mvhdDuration != movieDuration && movieDuration > 0)
            {
                return Patched(m, c, (int)mvhdDurationAt, mvhdWidth, movieDuration);
            }

            if (c.Type != Trak)
            {
                return null;
            }

            int ti = tracks.FindIndex(t => t.TrakAt == c.At);
            if (ti < 0 || !local[ti].Changed)
            {
                return null;
            }

            return local[ti].Samples == 0 ? [] : RebuildTrak(m, moov.Offset, tracks[ti], local[ti], offsets[ti], movieTimescale);
        });

        int pad = m.Length - rebuilt.Length;
        if (pad < 0 || (pad is > 0 and < 8))
        {
            report.Warn("The cut sample tables leave a gap of " + pad + " bytes in the movie box that a free box cannot fill; the tables were left as they were.");
            return null;
        }

        if (pad == 0)
        {
            return rebuilt;
        }

        byte[] result = new byte[m.Length];
        rebuilt.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(rebuilt.Length), (uint)pad);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(rebuilt.Length + 4), Free);
        return result;
    }

    private static SampleCut CutTrack(byte[] m, TrackInfo t, long[] offsets, long fileLength)
    {
        var cut = new SampleCut { Samples = t.Samples, Chunks = offsets.Length };
        long constant = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(t.StszAt + 4));
        long Size(long i) => constant != 0 ? constant : BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(t.StszAt + 12 + (int)(i * 4)));
        for (int c = 0; c < offsets.Length; c++)
        {
            long first = t.ChunkFirstSample[c];
            long n = t.ChunkSamples[c];
            long pos = offsets[c];
            long fit = 0;
            while (fit < n && pos >= 0 && pos + Size(first + fit) <= fileLength)
            {
                pos += Size(first + fit);
                fit++;
            }

            if (fit == n)
            {
                continue;
            }

            cut.Changed = true;
            cut.Samples = first + fit;
            cut.Chunks = fit > 0 ? c + 1 : c;
            cut.PartialSamples = fit > 0 ? fit : 0;
            cut.FirstLostOffset = pos;
            cut.FirstLostSize = Size(first + fit);
            break;
        }

        if (cut.Changed)
        {
            cut.Duration = SumDurations(m, t, cut.Samples);
        }

        return cut;
    }

    private static long SumDurations(byte[] m, TrackInfo t, long samples)
    {
        long total = 0;
        long left = samples;
        (int At, int Hdr, int Size)? stts = FindIn(m, t, Stts);
        if (stts is null)
        {
            return 0;
        }

        int body = stts.Value.At + stts.Value.Hdr + 4;
        uint n = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(body));
        for (uint k = 0; k < n && left > 0 && body + 4 + ((k + 1) * 8L) <= stts.Value.At + stts.Value.Size; k++)
        {
            long count = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(body + 4 + (int)(k * 8)));
            long delta = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(body + 8 + (int)(k * 8)));
            long take = Math.Min(count, left);
            total += take * delta;
            left -= take;
        }

        return total;
    }

    // The sample table box of a track by type, or null.
    private static (int At, int Hdr, int Size)? FindIn(byte[] m, TrackInfo t, uint type)
    {
        foreach ((uint ct, int ca, int ch, int cs) in Children(m, t.TrakAt + t.TrakHdr, t.TrakAt + t.TrakSize))
        {
            if (ct != Mdia)
            {
                continue;
            }

            foreach ((uint mt, int ma, int mh, int ms) in Children(m, ca + ch, ca + cs))
            {
                if (mt != Minf)
                {
                    continue;
                }

                foreach ((uint nt, int na, int nh, int ns) in Children(m, ma + mh, ma + ms))
                {
                    if (nt != Stbl)
                    {
                        continue;
                    }

                    foreach ((uint st, int sa, int sh, int ss) in Children(m, na + nh, na + ns))
                    {
                        if (st == type)
                        {
                            return (sa, sh, ss);
                        }
                    }
                }
            }
        }

        return null;
    }

    private static long TrackDuration(TrackInfo t, SampleCut cut, long movieTimescale)
    {
        return cut.HasElst ? cut.ElstSum : RescaleRounded(cut.Duration, movieTimescale, Math.Max(1, t.Timescale));
    }

    private static byte[] RebuildTrak(byte[] m, long moovOffset, TrackInfo t, SampleCut cut, long[] offsets, long movieTimescale)
    {
        byte[]? newEdts = EditsFor(m, t, cut, movieTimescale);
        long trackDuration = TrackDuration(t, cut, movieTimescale);
        return RewriteContainer(m, t.TrakAt, t.TrakHdr, t.TrakSize, c =>
        {
            switch (c.Type)
            {
                case Tkhd:
                    return t.TkhdDurationAt >= 0 && trackDuration > 0 ? Patched(m, c, (int)(t.TkhdDurationAt - moovOffset), t.TkhdDurationWidth, trackDuration) : null;
                case Edts:
                    return newEdts;
                case Mdia:
                    return RewriteContainer(m, c.At, c.Hdr, c.Size, d =>
                    {
                        if (d.Type == Mdhd)
                        {
                            return t.MdhdDurationAt >= 0 ? Patched(m, d, (int)(t.MdhdDurationAt - moovOffset), t.MdhdDurationWidth, cut.Duration) : null;
                        }

                        if (d.Type != Minf)
                        {
                            return null;
                        }

                        return RewriteContainer(m, d.At, d.Hdr, d.Size, e => e.Type != Stbl ? null : RewriteContainer(m, e.At, e.Hdr, e.Size, f => RebuildTable(m, f, t, cut, offsets)));
                    });
                default:
                    return null;
            }
        });
    }

    // The track's edit list box cut to the media that is left (null when it has none), also noting the new sum in `cut`.
    private static byte[]? EditsFor(byte[] m, TrackInfo t, SampleCut cut, long movieTimescale)
    {
        foreach ((uint ct, int ca, int ch, int cs) in Children(m, t.TrakAt + t.TrakHdr, t.TrakAt + t.TrakSize))
        {
            if (ct == Edts)
            {
                return RebuildEdts(m, (ca, ch, cs), t, cut, movieTimescale);
            }
        }

        return null;
    }

    // ISO/IEC 14496-12 §8.6.6: segment_duration is in movie time, media_time in media time, -1 marks an empty edit.
    private static byte[]? RebuildEdts(byte[] m, (int At, int Hdr, int Size) edts, TrackInfo t, SampleCut cut, long movieTimescale)
    {
        foreach ((uint et, int ea, int eh, int es) in Children(m, edts.At + edts.Hdr, edts.At + edts.Size))
        {
            if (et != Elst)
            {
                continue;
            }

            int v = m[ea + eh];
            int entry = v == 1 ? 20 : 12;
            uint n = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(ea + eh + 4));
            var body = new List<byte>();
            int kept = 0;
            long sum = 0;
            for (uint k = 0; k < n && ea + eh + 8 + ((k + 1) * entry) <= ea + es; k++)
            {
                int p = ea + eh + 8 + (int)(k * entry);
                long dur = v == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(p)) : BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(p));
                long mediaTime = v == 1 ? BinaryPrimitives.ReadInt64BigEndian(m.AsSpan(p + 8)) : BinaryPrimitives.ReadInt32BigEndian(m.AsSpan(p + 4));
                if (mediaTime >= 0)
                {
                    long availMedia = cut.Duration - mediaTime;
                    long availMovie = availMedia <= 0 ? 0 : (long)((Int128)availMedia * movieTimescale / Math.Max(1, t.Timescale));
                    if (availMovie <= 0)
                    {
                        continue;
                    }

                    dur = Math.Min(dur, availMovie);
                }

                kept++;
                sum += dur;
                int rate = v == 1 ? p + 16 : p + 8;
                body.AddRange(Number(dur, v == 1 ? 8 : 4));
                body.AddRange(m.AsSpan(p + (v == 1 ? 8 : 4), v == 1 ? 8 : 4).ToArray());
                body.AddRange(m.AsSpan(rate, 4).ToArray());
            }

            cut.HasElst = kept > 0;
            cut.ElstSum = sum;
            cut.ElstEntries = kept;
            if (kept == 0)
            {
                return [];
            }

            byte[] head = new byte[8];
            head[0] = (byte)v;
            m.AsSpan(ea + eh + 1, 3).CopyTo(head.AsSpan(1));
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), (uint)kept);
            return Box(Edts, Box(Elst, [.. head, .. body]));
        }

        return null;
    }

    private static byte[]? RebuildTable(byte[] m, (uint Type, int At, int Hdr, int Size) f, TrackInfo t, SampleCut cut, long[] offsets)
    {
        int body = f.At + f.Hdr;
        int end = f.At + f.Size;
        ReadOnlySpan<byte> versionFlags = m.AsSpan(body, 4);
        var o = new List<byte>();
        o.AddRange(versionFlags.ToArray());
        switch (f.Type)
        {
            case Stts:
            case Ctts:
                {
                    uint n = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(body + 4));
                    long left = cut.Samples;
                    var entries = new List<byte>();
                    uint written = 0;
                    for (uint k = 0; k < n && left > 0 && body + 8 + ((k + 1) * 8L) <= end; k++)
                    {
                        int p = body + 8 + (int)(k * 8);
                        long count = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(p));
                        long take = Math.Min(count, left);
                        entries.AddRange(Number(take, 4));
                        entries.AddRange(m.AsSpan(p + 4, 4).ToArray());
                        left -= take;
                        written++;
                    }

                    o.AddRange(Number(written, 4));
                    o.AddRange(entries);
                    return Box(f.Type, [.. o]);
                }

            case Stsz:
                {
                    uint constant = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(body + 4));
                    o.AddRange(Number(constant, 4));
                    o.AddRange(Number(cut.Samples, 4));
                    if (constant == 0)
                    {
                        o.AddRange(m.AsSpan(body + 12, (int)(cut.Samples * 4)).ToArray());
                    }

                    return Box(Stsz, [.. o]);
                }

            case Stsc:
                {
                    uint n = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(body + 4));
                    var kept = new List<(uint First, uint Per, uint Sdi)>();
                    for (uint k = 0; k < n && body + 8 + ((k + 1) * 12L) <= end; k++)
                    {
                        int p = body + 8 + (int)(k * 12);
                        uint first = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(p));
                        if (first <= cut.Chunks)
                        {
                            kept.Add((first, BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(p + 4)), BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(p + 8))));
                        }
                    }

                    if (cut.PartialSamples > 0 && kept.Count > 0)
                    {
                        (uint first, _, uint sdi) = kept[^1];
                        if (first == cut.Chunks)
                        {
                            kept[^1] = (first, (uint)cut.PartialSamples, sdi);
                        }
                        else
                        {
                            kept.Add(((uint)cut.Chunks, (uint)cut.PartialSamples, sdi));
                        }
                    }

                    o.AddRange(Number(kept.Count, 4));
                    foreach ((uint first, uint per, uint sdi) in kept)
                    {
                        o.AddRange(Number(first, 4));
                        o.AddRange(Number(per, 4));
                        o.AddRange(Number(sdi, 4));
                    }

                    return Box(Stsc, [.. o]);
                }

            case Stco:
            case Co64:
                {
                    int w = f.Type == Co64 ? 8 : 4;
                    o.AddRange(Number(cut.Chunks, 4));
                    for (int c = 0; c < cut.Chunks; c++)
                    {
                        if (w == 4 && offsets[c] > uint.MaxValue)
                        {
                            throw new MediaFormatException("A chunk offset does not fit the 32-bit chunk offset box.");
                        }

                        o.AddRange(Number(offsets[c], w));
                    }

                    return Box(f.Type, [.. o]);
                }

            case Stss:
            case Stps:
                {
                    uint n = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(body + 4));
                    var entries = new List<byte>();
                    uint written = 0;
                    for (uint k = 0; k < n && body + 8 + ((k + 1) * 4L) <= end; k++)
                    {
                        uint sample = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(body + 8 + (int)(k * 4)));
                        if (sample <= cut.Samples)
                        {
                            entries.AddRange(Number(sample, 4));
                            written++;
                        }
                    }

                    o.AddRange(Number(written, 4));
                    o.AddRange(entries);
                    return Box(f.Type, [.. o]);
                }

            case Sdtp:
                o.AddRange(m.AsSpan(body + 4, (int)Math.Min(cut.Samples, end - body - 4)).ToArray());
                return Box(Sdtp, [.. o]);
            default:
                return null;
        }
    }

    // A copy of a box with a duration field overwritten; `at` is the field's offset within the movie box bytes.
    private static byte[] Patched(byte[] m, (uint Type, int At, int Hdr, int Size) c, int at, int width, long value)
    {
        byte[] copy = m.AsSpan(c.At, c.Size).ToArray();
        Number(value, width).CopyTo(copy, at - c.At);
        return copy;
    }

    private static byte[] Box(uint type, ReadOnlySpan<byte> payload)
    {
        byte[] b = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), type);
        payload.CopyTo(b.AsSpan(8));
        return b;
    }

    // Rebuilds a container box: each child is copied unless the hook returns a replacement (an empty array drops it); bytes after the last
    // child are kept.
    private static byte[] RewriteContainer(byte[] m, int at, int hdr, int size, Func<(uint Type, int At, int Hdr, int Size), byte[]?> hook)
    {
        var body = new List<byte>(size);
        int p = at + hdr;
        foreach ((uint type, int ca, int ch, int cs) in Children(m, at + hdr, at + size))
        {
            byte[]? r = hook((type, ca, ch, cs));
            if (r is null)
            {
                body.AddRange(m.AsSpan(ca, cs).ToArray());
            }
            else
            {
                body.AddRange(r);
            }

            p = ca + cs;
        }

        body.AddRange(m.AsSpan(p, at + size - p).ToArray());
        return Box(BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(at + 4)), [.. body]);
    }
}
