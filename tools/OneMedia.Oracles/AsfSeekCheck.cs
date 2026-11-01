using System.Globalization;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>
/// Checks keyframe seeking in ASF files against a full read: for targets spread over the file, the packet read after <c>Seek</c> must be the
/// keyframe of the primary stream that a brute-force pass finds as the last one at or before the target (and the next one for NextKeyframe).
/// </summary>
internal static class AsfSeekCheck
{
    public static int Run(IEnumerable<string> files)
    {
        MediaFormatRegistry registry = Readers.Create();
        int bad = 0, total = 0;
        foreach (string file in files)
        {
            using FileMediaSource source = FileMediaSource.Open(file);
            IDemuxer? opened;
            try
            {
                opened = registry.Open(source, new MediaOpenOptions { LeaveOpen = true });
            }
            catch (MediaException)
            {
                Console.WriteLine($"skip\t{Path.GetFileName(file)}\trejected");
                continue;
            }

            using IDemuxer d = opened;
            if (!d.CanSeek || d.Tracks.Count == 0)
            {
                Console.WriteLine($"skip\t{Path.GetFileName(file)}\tcannot seek");
                continue;
            }

            int primary = d.Tracks.ToList().FindIndex(t => t.Kind == TrackKind.Video);
            if (primary < 0)
            {
                primary = 0;
            }

            List<long> keys = [];
            long last = 0;
            while (d.TryReadPacket(out PacketView v))
            {
                if (v.Info.TrackIndex == primary && v.Info.IsKeyframe && v.Info.Pts.IsKnown)
                {
                    keys.Add(v.Info.Pts.Ticks);
                }

                if (v.Info.Pts.IsKnown)
                {
                    last = Math.Max(last, v.Info.Pts.Ticks);
                }
            }

            int problems = 0, checks = 0;
            for (int i = 0; i <= 10; i++)
            {
                long target = last * i / 10;
                foreach (SeekMode mode in new[] { SeekMode.PreviousKeyframe, SeekMode.NextKeyframe })
                {
                    long expected = mode == SeekMode.PreviousKeyframe ? keys.LastOrDefault(k => k <= target, keys.Count > 0 ? keys[0] : 0) : keys.FirstOrDefault(k => k >= target, keys.Count > 0 ? keys[^1] : 0);
                    MediaTime landed = d.Seek(new MediaTime(target, 1000), mode);
                    long firstKey = long.MinValue;
                    int guard = 0;
                    while (d.TryReadPacket(out PacketView v) && guard++ < 100000)
                    {
                        if (v.Info.TrackIndex == primary && v.Info.Pts.IsKnown)
                        {
                            firstKey = v.Info.Pts.Ticks;
                            break;
                        }
                    }

                    checks++;
                    // A target after the last keyframe positions at the end of the data: nothing more to read.
                    bool pastEnd = mode == SeekMode.NextKeyframe && keys.Count > 0 && keys[^1] < target;
                    if (keys.Count > 0 && (landed.Ticks != expected || (!pastEnd && firstKey != expected)))
                    {
                        problems++;
                        if (problems <= 3)
                        {
                            Console.WriteLine($"  {mode} target {target}: expected {expected}, landed {landed.Ticks}, first packet {firstKey}");
                        }
                    }
                }
            }

            total++;
            bad += problems > 0 ? 1 : 0;
            Console.WriteLine($"{(problems == 0 ? "same" : "DIFF")}\t{Path.GetFileName(file)}\t{checks} seeks, {problems} wrong, {keys.Count} keyframes");
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"asfseek: {total} files, {bad} with wrong seeks"));
        return bad == 0 ? 0 : 1;
    }
}
