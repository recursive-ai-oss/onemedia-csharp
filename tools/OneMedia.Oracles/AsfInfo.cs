using System.Globalization;
using RecursiveAI.OneMedia.Asf;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>Prints what the ASF reader makes of a file's header: protection, streams, File Properties, objects, defects and packet counts per track.</summary>
internal static class AsfInfo
{
    public static int Run(IEnumerable<string> files)
    {
        foreach (string file in files)
        {
            Console.WriteLine("== " + Path.GetFileName(file));
            try
            {
                using FileMediaSource source = FileMediaSource.Open(file);
                DefectLog log = new();
                using AsfDemuxer d = AsfDemuxer.Open(source, new MediaOpenOptions { LeaveOpen = true, Defects = log });
                AsfFileProperties? fp = d.Header.FileProperties;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  protection={d.Protection} flags=0x{fp?.Flags:X} packet={fp?.MinPacketSize}/{fp?.MaxPacketSize} preroll={fp?.PrerollMs} packets(hdr)={d.Header.DataPacketCount} seek={d.CanSeek}"));
                foreach (AsfStreamEntry s in d.Header.Streams)
                {
                    Console.WriteLine($"  stream {s.StreamNumber} kind={s.Kind} encrypted={s.IsEncrypted} ext={s.HasExtendedProperties} at {s.Range.Offset}");
                }

                foreach (IStructureNode n in d.Header.Root.Children)
                {
                    Console.WriteLine($"  object {((AsfObjectNode)n).ObjectId} kind={((AsfObjectNode)n).Kind} at {n.Offset} size {n.PayloadSize + n.HeaderSize}");
                }

                long[] counts = new long[d.Tracks.Count];
                Queue<string> lastFew = new();
                while (d.TryReadPacket(out PacketView v))
                {
                    counts[v.Info.TrackIndex]++;
                    lastFew.Enqueue($"track {v.Info.TrackIndex} pts {v.Info.Pts} size {v.Data.Length} attrs {v.Info.Flags}");
                    if (lastFew.Count > 4)
                    {
                        lastFew.Dequeue();
                    }
                }

                foreach (string line in lastFew)
                {
                    Console.WriteLine("  last: " + line);
                }

                Console.WriteLine("  packets per track: " + string.Join(", ", counts));
                foreach (MediaDefect x in log.Take(12))
                {
                    Console.WriteLine($"  defect {x.Code} @{x.Offset}: {x.Message}");
                }
            }
            catch (MediaException e)
            {
                Console.WriteLine("  error: " + e.Message);
            }
        }

        return 0;
    }
}
