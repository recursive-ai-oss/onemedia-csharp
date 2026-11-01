using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneMedia;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>Reads every packet of each file through the public happy path and reports packets, bytes, allocation per packet on the pull path and the peak working set.</summary>
internal static class DrainCheck
{
    public static int Run(IReadOnlyList<string> files)
    {
        foreach (string file in files)
        {
            using MediaFile media = Media.Open(file);
            IDemuxer d = media.Demuxer ?? throw new InvalidOperationException("not a synchronous reader");
            long packets = 0, bytes = 0;
            Stopwatch sw = Stopwatch.StartNew();
            // The first packets warm the buffers; allocation is counted from the 1,000th on.
            long baseline = -1, baselinePackets = 0;
            while (d.TryReadPacket(out PacketView p))
            {
                packets++;
                bytes += p.Data.Length;
                if (packets == 1000)
                {
                    baseline = GC.GetAllocatedBytesForCurrentThread();
                    baselinePackets = packets;
                }
            }

            long allocated = baseline < 0 ? -1 : GC.GetAllocatedBytesForCurrentThread() - baseline;
            long counted = packets - baselinePackets;
            using Process self = Process.GetCurrentProcess();
            Console.WriteLine(string.Join('\t', file, packets.ToString(CultureInfo.InvariantCulture), bytes.ToString(CultureInfo.InvariantCulture),
                "allocated-after-warmup=" + allocated.ToString(CultureInfo.InvariantCulture) + " over " + counted.ToString(CultureInfo.InvariantCulture) + " packets",
                "peak-working-set-MB=" + (self.PeakWorkingSet64 >> 20).ToString(CultureInfo.InvariantCulture),
                "seconds=" + sw.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)));
        }

        return 0;
    }
}
