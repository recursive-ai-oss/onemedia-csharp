using RecursiveAI.OneMedia.Repair;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>Repairs a cut copy of each supported container under NativeAOT and in the browser WebAssembly runtime, and checks the result opens and reads packets.</summary>
internal static class RepairSmoke
{
    public static void Run(Action<string, bool> check)
    {
        foreach (string name in new[] { "repair-tiny.mkv", "repair-tiny.avi", "repair-tiny.mp4", "repair-tiny-frag.mp4", "repair-tiny.ts" })
        {
            using Stream s = typeof(RepairSmoke).Assembly.GetManifestResourceStream(name)!;
            using MemoryStream m = new();
            s.CopyTo(m);
            byte[] healthy = m.ToArray();

            using MemoryStream same = new();
            RepairReport none = Repairer.Repair(new MemoryMediaSource(healthy), same);
            check(name + " healthy needs nothing", none.Status == RepairStatus.NothingToRepair && same.ToArray().AsSpan().SequenceEqual(healthy));

            byte[] cut = healthy.AsSpan(0, (int)(healthy.Length * 0.6) + 33).ToArray();
            using MemoryStream fixedUp = new();
            RepairReport r = Repairer.Repair(new MemoryMediaSource(cut), fixedUp);
            bool reads = false;
            if (r.OutputWritten)
            {
                using MediaFile f = Media.Open(fixedUp.ToArray());
                int packets = 0;
                while (f.Demuxer!.TryReadPacket(out PacketView _))
                {
                    packets++;
                }

                reads = packets > 0;
            }

            check(name + " cut file repaired", r.Status is RepairStatus.Repaired or RepairStatus.PartiallyRepaired && reads);
        }
    }
}
