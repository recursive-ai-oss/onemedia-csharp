

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>Counts the bytes allocated while the pull path reads a file's packets, after a warm-up that lets the reader size its buffers.</summary>
internal static class AllocCheck
{
    public static int Run(IReadOnlyList<string> files)
    {
        int bad = 0;
        foreach (string file in files)
        {
            MediaFormatRegistry registry = Readers.Create();
            using IDemuxer d = registry.Open(FileMediaSource.Open(file));
            long total = 0;
            int warm = 0;
            // A first pass sizes every buffer for the largest packet; the measured pass then starts again from the beginning.
            while (d.TryReadPacket(out _))
            {
                warm++;
            }

            if (d.CanSeek)
            {
                d.Seek(MediaTime.Zero);
            }

            long n = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            long last = before;
            int shown = 0;
            while (d.TryReadPacket(out PacketView p))
            {
                n++;
                long now = GC.GetAllocatedBytesForCurrentThread();
                if (now != last && shown++ < 3)
                {
                    Console.WriteLine($"  {now - last} bytes at packet {n + warm}: track {p.Info.TrackIndex} size {p.Data.Length}");
                }

                last = now;
            }

            total = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine($"{(total == 0 ? "ok  " : "FAIL")} {Path.GetFileName(file)}: {n} packets after a first pass of {warm}, {total} bytes allocated");
            bad += total == 0 ? 0 : 1;
        }

        return bad == 0 ? 0 : 1;
    }

    /// <summary>Opens each file and prints its tracks, first packets and every defect (for investigating a difference).</summary>
    public static int Defects(IReadOnlyList<string> files)
    {
        foreach (string file in files)
        {
            using IDemuxer d = Readers.Create().Open(FileMediaSource.Open(file));
            Console.WriteLine($"{Path.GetFileName(file)}: {d.Format} duration {d.Duration}");
            foreach (MediaTrack t in d.Tracks)
            {
                Console.WriteLine($"  track {t.Index} {t.Kind} {t.Codec.Name} timescale {t.Timescale} duration {t.Duration}");
            }

            int n = 0;
            while (d.TryReadPacket(out PacketView p))
            {
                if (n++ < 5)
                {
                    Console.WriteLine($"  packet {p.Info.TrackIndex} pts {p.Info.Pts} dts {p.Info.Dts} dur {p.Info.Duration} size {p.Info.Size} {p.Info.Flags}");
                }
            }

            Console.WriteLine($"  {n} packets");
            foreach (MediaDefect x in d.Defects)
            {
                Console.WriteLine($"  defect {x} [{x.Code}]");
            }
        }

        return 0;
    }
}
