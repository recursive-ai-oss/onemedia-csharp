using System;
using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz target for the ISO BMFF reader. The first input byte picks the path (bit 0: read it as a forward-only stream; bit 1: seek around
/// after reading); the rest is the file. Anything other than a <see cref="MediaException"/> escaping is a finding.
/// </summary>
internal static class IsoBmffTarget
{
    public static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 9)
        {
            return;
        }

        bool forward = (input[0] & 1) != 0;
        bool seek = (input[0] & 2) != 0;
        byte[] file = input[1..].ToArray();
        // Small limits keep each run fast and exercise the limit paths.
        MediaOpenOptions options = new()
        {
            Limits = new MediaLimits { MaxAllocationBytes = 4 << 20, MaxSampleTableEntries = 2_000_000, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxPacketBytes = 1 << 20 },
        };
        try
        {
            using IsoBmffDemuxer d = forward
                ? IsoBmffDemuxer.Open(new StreamForwardSource(new MemoryStream(file)), options)
                : IsoBmffDemuxer.Open(new MemoryMediaSource(file), options);
            Drain(d, 20_000);
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
                    // A caller bug (an index past the track list) throws ArgumentOutOfRangeException by contract, so only valid indexes are tried.
                    d.SetTrackEnabled(0, false);
                }

                d.Seek(MediaTime.Zero);
                Drain(d, 200);
            }
        }
        catch (MediaException)
        {
            // Typed rejection of bad data.
        }

        try
        {
            BoxTree tree = new(new MemoryMediaSource(file), options.Limits);
            foreach (BoxNode top in tree.TopLevel)
            {
                int n = 0;
                foreach ((IStructureNode _, int _) in top.Descendants(options.Limits))
                {
                    if (++n > 5000)
                    {
                        break;
                    }
                }
            }
        }
        catch (MediaException)
        {
        }
    }

    private static void Drain(IsoBmffDemuxer d, int max)
    {
        for (int i = 0; i < max && d.TryReadPacket(out _); i++)
        {
        }
    }
}
