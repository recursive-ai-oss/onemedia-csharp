using System;
using System.IO;
using System.Linq;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Avi;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz target for the RIFF structure reader and the AVI demuxer. The first input byte picks the path (bit 0: read as a forward-only
/// stream; bit 1: seek around after reading; bit 2: open through the registry and compare with a direct open; bit 3: read the metadata);
/// the rest is the file. The chunk tree is walked on every run. Anything other than a <see cref="MediaException"/> escaping is a finding.
/// </summary>
internal static class RiffAviTarget
{
    public static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 13)
        {
            return;
        }

        bool forward = (input[0] & 1) != 0;
        bool seek = (input[0] & 2) != 0;
        byte[] file = input[1..].ToArray();
        MediaLimits limits = new() { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxPacketBytes = 1 << 20, MaxTracks = 64, MaxSampleTableEntries = 1_000_000 };
        MediaOpenOptions options = new() { Limits = limits };
        if ((input[0] & 4) != 0)
        {
            string? viaRegistry = Summarise(() => MediaFormats.Default.Open(new MemoryMediaSource(file), new MediaOpenOptions { Limits = limits }));
            if (viaRegistry is not null)
            {
                string? direct = Summarise(() => AviDemuxer.Open(new MemoryMediaSource(file), new MediaOpenOptions { Limits = limits }));
                if (direct != viaRegistry)
                {
                    throw new InvalidOperationException($"opening through the registry gave {viaRegistry} but a direct open gave {direct}");
                }
            }
        }

        try
        {
            using AviDemuxer d = forward
                ? AviDemuxer.Open(new StreamForwardSource(new MemoryStream(file)), options)
                : AviDemuxer.Open(new MemoryMediaSource(file), options);
            Drain(d, 20_000);
            if ((input[0] & 8) != 0 && !forward)
            {
                _ = d.ReadMetadata().Tags.Title;
            }

            if (seek && d.CanSeek)
            {
                foreach (long ms in new long[] { 0, 500, 1500, 60_000 })
                {
                    d.Seek(new MediaTime(ms, 1000));
                    Drain(d, 200);
                    d.Seek(new MediaTime(ms, 1000), SeekMode.NextKeyframe);
                    Drain(d, 200);
                }

                if (d.Tracks.Count > 0)
                {
                    d.SetTrackEnabled(0, false);
                }

                d.Seek(MediaTime.Zero);
                Drain(d, 200);
            }

            if (d.Structure is { } root)
            {
                foreach ((IStructureNode node, int _) in root.Descendants(limits).Take(5000))
                {
                    _ = ((RiffChunk)node).FourCc;
                }
            }
        }
        catch (MediaException)
        {
            // Typed rejection of bad data.
        }
    }

    private static void Drain(AviDemuxer d, int max)
    {
        for (int i = 0; i < max && d.TryReadPacket(out _); i++)
        {
        }
    }

    private static string? Summarise(Func<IDemuxer> open)
    {
        try
        {
            using IDemuxer d = open();
            if (d.Format != MediaFormatId.Avi)
            {
                return null;
            }

            long packets = 0, bytes = 0;
            while (packets < 20_000 && d.TryReadPacket(out PacketView p))
            {
                packets++;
                bytes += p.Data.Length;
            }

            return $"{d.Tracks.Count} tracks, {packets} packets, {bytes} bytes";
        }
        catch (MediaException)
        {
            return null;
        }
    }
}
