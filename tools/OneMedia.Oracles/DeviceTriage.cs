using System.Globalization;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>
/// Sorts the packet differences on real device files into causes, track by track, so each can be checked against an explanation instead of read by eye.
/// A track is "same" when every field matches; otherwise the first rule that holds names the cause:
/// <list type="bullet">
/// <item><c>dts-shift</c>: counts, positions, sizes, flags and presentation times equal, decode times differ by one constant (ffprobe moves DTS earlier by the largest dts - pts).</item>
/// <item><c>key-superset</c>: only the keyframe flag differs and every key frame of ours is one of theirs (ffprobe also marks IDR pictures the sync table omits).</item>
/// <item><c>regroup</c>: both sides list the same bytes in the same order, cut into packets differently (PCM packet length).</item>
/// <item><c>edit-repeat</c>: every packet of ours is also in ffprobe's list and ffprobe lists more (an edit list that plays media twice).</item>
/// <item><c>edit-drop</c>: ffprobe lists a subset of ours (samples outside the edit list).</item>
/// <item><c>other</c>: none of these; the line states the first difference.</item>
/// </list>
/// </summary>
internal static class DeviceTriage
{
    public static int Run(IEnumerable<string> files)
    {
        MediaFormatRegistry registry = Readers.Create();
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            List<ProbeStream> streams;
            List<PacketRecord> theirs;
            try
            {
                streams = FfprobeReader.ReadStreams(file);
                theirs = [.. FfprobeReader.ReadPackets(file, streams)];
            }
            catch (Exception e)
            {
                Console.WriteLine($"{name}\t-\t-\tffprobe-failed\t{e.GetType().Name}");
                continue;
            }

            OurResult ours = OurReader.Read(registry, file);
            if (ours.Status != "ok")
            {
                Console.WriteLine($"{name}\t-\t-\tours-{ours.Status}\t{ours.Message}");
                continue;
            }

            foreach (MediaTrack t in ours.Tracks)
            {
                List<PacketRecord> a = [.. ours.Packets.Where(p => p.Track == t.Index)];
                List<PacketRecord> b = [.. theirs.Where(p => p.Track == t.Index)];
                (string cls, string detail) = Classify(a, b);
                Console.WriteLine($"{name}\t{t.Index}\t{t.Codec}\t{cls}\tours {a.Count} ffprobe {b.Count}; {detail}");
            }

            foreach (int extra in theirs.Select(p => p.Track).Distinct().Where(i => ours.Tracks.All(t => t.Index != i)))
            {
                Console.WriteLine($"{name}\t{extra}\t-\textra-ffprobe-track\tffprobe lists {theirs.Count(p => p.Track == extra)} packets for a stream we have no track for");
            }
        }

        return 0;
    }

    private static (string, string) Classify(List<PacketRecord> a, List<PacketRecord> b)
    {
        if (a.Count == b.Count)
        {
            // Per packet, which fields differ. The last packet's duration is excused: ffprobe replaces it (docs/isobmff.md).
            HashSet<string> fields = [];
            int lastIndex = a.Count - 1, firstDiff = -1, lastDiff = -1;
            bool excusedLast = false, constant = true;
            int startDiffs = 0;
            MediaTime? shift = null, shiftMax = null;
            for (int i = 0; i < a.Count; i++)
            {
                PacketRecord p = a[i], q = b[i];
                List<string> d = [];
                if (p.Size != q.Size || p.Hash != q.Hash) d.Add("bytes");
                else if (p.Pos != q.Pos) d.Add("pos");
                if (p.Pts != q.Pts) d.Add("pts");
                if (p.Dts != q.Dts) d.Add("dts");
                if (p.Duration != q.Duration) { if (i == lastIndex) excusedLast = true; else d.Add("duration"); }
                if (p.Key != q.Key) d.Add("key");
                if (d.Count == 0) continue;
                // The first packets may sit across the edit list's start, where ffprobe moves their times (docs/isobmff.md).
                if (i <= 3 && !d.Contains("bytes") && !d.Contains("key"))
                {
                    startDiffs++;
                    d.Remove("pts");
                    d.Remove("duration");
                    if (d.Count == 0) continue;
                }

                if (firstDiff < 0) firstDiff = i;
                lastDiff = i;
                foreach (string f in d) fields.Add(f);
                if (d.Count == 1 && d[0] == "dts" && p.Dts.IsKnown && q.Dts.IsKnown)
                {
                    MediaTime delta = q.Dts - p.Dts.Rescale(q.Dts.Timescale, MediaRounding.NearestAwayFromZero);
                    if (shift is null) { shift = delta; shiftMax = delta; } else { if (shift != delta) constant = false; if (delta < shift) shift = delta; if (delta > shiftMax) shiftMax = delta; }
                }
            }

            string note = (excusedLast ? " (last duration differs)" : "") + (startDiffs > 0 ? $" (pts/duration of the first {startDiffs} packets differ: edit start)" : "");
            if (fields.Count == 0) return (startDiffs > 0 ? "edit-start" : excusedLast ? "last-duration" : "same", note.Trim());
            if (fields.SetEquals(["dts"]) && constant) return ("dts-shift", $"{shift!.Value.Ticks}/{shift.Value.Timescale} on every differing packet{note}");
            if (fields.SetEquals(["dts"]) && shift is not null) return ("dts-shift-varying", $"dts differ by {shift.Value.Ticks}..{shiftMax!.Value.Ticks} ({shift.Value.Timescale}){note}");
            if (fields.IsSubsetOf(["pos", "key"]) && fields.Count > 0)
            {
                // Position: ffprobe gives every access unit of a shared PES packet its position, ours the first unit only (docs/mpeg.md).
                // Keys: ours marks an intra picture that opens with a parameter set; ffprobe wants a recovery point SEI as well (docs/mpeg.md).
                bool oursMore = true;
                for (int i = 0; i < a.Count; i++) if (b[i].Key && !a[i].Key) oursMore = false;
                if (oursMore) return ("pos-key-documented", $"fields {string.Join("+", fields)}; keys ffprobe {b.Count(p => p.Key)} ours {a.Count(p => p.Key)}{note}");
            }

            if (fields.SetEquals(["key"]))
            {
                bool superset = true;
                for (int i = 0; i < a.Count; i++) if (a[i].Key && !b[i].Key) superset = false;
                if (superset) return ("key-superset", $"ffprobe marks {b.Count(p => p.Key)} key frames, we mark {a.Count(p => p.Key)}{note}");
            }

            if (fields.Count > 0 && !(fields.Contains("bytes") || fields.Contains("key")))
            {
                return ("timing-other", $"fields {string.Join("+", fields)}; first packet {firstDiff}: ffprobe pts {b[firstDiff].Pts} dts {b[firstDiff].Dts} dur {b[firstDiff].Duration}, ours pts {a[firstDiff].Pts} dts {a[firstDiff].Dts} dur {a[firstDiff].Duration}; {startDiffs} edit-start packets{note}");
            }

            if (!fields.Contains("bytes") && !fields.Contains("key") && lastDiff <= 2)
            {
                return ("edit-start", $"only packets 0..{lastDiff} differ in {string.Join("+", fields)}; first: ffprobe pts {b[firstDiff].Pts} dts {b[firstDiff].Dts} ours pts {a[firstDiff].Pts} dts {a[firstDiff].Dts}{note}");
            }
        }

        // Compare what bytes each side lists, ignoring cuts.
        if (Coverage(a) == Coverage(b) && a.Count != b.Count && Same(Merge(a), Merge(b)))
        {
            return ("regroup", $"same {Total(a)} bytes, packet sizes ours {a.FirstOrDefault().Size} ffprobe {b.FirstOrDefault().Size}");
        }

        HashSet<(long, long)> mine = [.. a.Select(p => (p.Pos, p.Size))], others = [.. b.Select(p => (p.Pos, p.Size))];
        int ourOnly = mine.Count(k => !others.Contains(k)), theirOnly = others.Count(k => !mine.Contains(k));
        if (theirOnly == 0 && b.Count < a.Count)
        {
            List<int> gone = [.. Enumerable.Range(0, a.Count).Where(i => !others.Contains((a[i].Pos, a[i].Size)))];
            return ("edit-drop", $"ffprobe leaves out {gone.Count} of ours: indices {string.Join(",", gone.Take(4))}{(gone.Count > 4 ? ",.." : "")} of 0..{a.Count - 1}");
        }

        if (ourOnly == 0 && b.Count > a.Count) return ("edit-repeat", $"ffprobe lists {b.Count - a.Count} packets again; {theirOnly} not in ours");
        string first = "";
        for (int i = 0; i < Math.Min(a.Count, b.Count) && first.Length == 0; i++)
        {
            PacketRecord p = a[i], q = b[i];
            if (p.Size != q.Size) first = $"packet {i} size {q.Size} vs {p.Size}";
            else if (p.Pos != q.Pos) first = $"packet {i} pos {q.Pos} vs {p.Pos}";
            else if (p.Pts != q.Pts) first = $"packet {i} pts {q.Pts} vs {p.Pts}";
            else if (p.Dts != q.Dts) first = $"packet {i} dts {q.Dts} vs {p.Dts}";
            else if (p.Duration != q.Duration) first = $"packet {i} duration {q.Duration} vs {p.Duration}";
            else if (p.Key != q.Key) first = $"packet {i} key {q.Key} vs {p.Key}";
        }

        return ("other", $"only ours {ourOnly}, only ffprobe {theirOnly}; {first}");
    }

    private static long Total(List<PacketRecord> l) => l.Sum(p => p.Size);

    private static long Coverage(List<PacketRecord> l) => l.Sum(p => p.Size);

    private static List<(long Start, long End)> Merge(List<PacketRecord> l)
    {
        List<(long, long)> r = [];
        foreach (PacketRecord p in l.Where(p => p.Pos >= 0).OrderBy(p => p.Pos))
        {
            if (r.Count > 0 && r[^1].Item2 == p.Pos) r[^1] = (r[^1].Item1, p.Pos + p.Size);
            else r.Add((p.Pos, p.Pos + p.Size));
        }

        return r;
    }

    private static bool Same(List<(long Start, long End)> x, List<(long Start, long End)> y) => x.SequenceEqual(y);
}
