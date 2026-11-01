

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
}
