using System.Globalization;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>
/// Compares which stored samples ffprobe and our reader list, by file position and size, ignoring all timing. It separates the cases that
/// timing comparison blurs together: samples ffprobe drops (outside the edit list), samples it lists more than once (an edit list that
/// repeats media), samples only it knows, and samples only we list.
/// </summary>
internal static class PacketSets
{
    public static int Run(IEnumerable<string> files)
    {
        MediaFormatRegistry registry = Readers.Create();
        foreach (string file in files)
        {
            Console.WriteLine("== " + file);
            List<ProbeStream> streams = FfprobeReader.ReadStreams(file);
            List<PacketRecord> theirs = [.. FfprobeReader.ReadPackets(file, streams)];
            OurResult ours = OurReader.Read(registry, file);
            if (ours.Status != "ok")
            {
                Console.WriteLine($"  ours: {ours.Status} {ours.Message}");
                continue;
            }

            foreach (MediaTrack t in ours.Tracks)
            {
                List<PacketRecord> a = ours.Packets.Where(p => p.Track == t.Index).ToList();
                List<PacketRecord> b = theirs.Where(p => p.Track == t.Index).ToList();
                HashSet<(long, long)> mine = [.. a.Select(p => (p.Pos, p.Size))];
                HashSet<(long, long)> others = [.. b.Select(p => (p.Pos, p.Size))];
                int repeats = b.Count - others.Count;
                int dropped = mine.Count(k => !others.Contains(k));
                int onlyTheirs = others.Count(k => !mine.Contains(k));
                string edits = string.Join(" ", t.Edit.Edits?.Select(e => string.Create(CultureInfo.InvariantCulture, $"[{(e.IsEmpty ? "empty" : e.MediaStart.Ticks.ToString(CultureInfo.InvariantCulture))}+{e.Duration.Ticks}@{e.Duration.Timescale}]")) ?? []);
                Console.WriteLine($"  track {t.Index} {t.Codec}: ours {a.Count}, ffprobe {b.Count}; ours not in ffprobe {dropped}; ffprobe not in ours {onlyTheirs}; ffprobe repeats {repeats}; edits {edits}");
                if (dropped > 0 && dropped < a.Count)
                {
                    int first = a.FindIndex(p => !others.Contains((p.Pos, p.Size)));
                    int last = a.FindLastIndex(p => !others.Contains((p.Pos, p.Size)));
                    Console.WriteLine($"    dropped by ffprobe: sample indices {first}..{last}, pts {a[first].Pts} .. {a[last].Pts}; media timescale {t.Timescale}");
                }
            }
        }

        return 0;
    }
}
