using System;
using System.IO;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Codecs.Dv;
using RecursiveAI.OneMedia.Dv;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz target for the DV frame parser and the raw DV demuxer. The first input byte picks the path (bit 0: forward-only; bit 1: seek around;
/// bit 2: read the metadata); the rest is the file. Anything other than a <see cref="MediaException"/> escaping is a finding.
/// </summary>
internal static class DvTarget
{
    public static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 9)
        {
            return;
        }

        bool forward = (input[0] & 1) != 0;
        bool seek = (input[0] & 2) != 0;
        ReadOnlySpan<byte> data = input[1..];
        if (DvDif.TryParseFrame(data, out DvFrameInfo? info))
        {
            _ = info.FrameSize + info.Packs.Count;
        }

        byte[] file = data.ToArray();
        MediaOpenOptions options = new() { Limits = new MediaLimits { MaxAllocationBytes = 4 << 20, MaxPacketBytes = 1 << 20 } };
        try
        {
            using DvDemuxer d = forward
                ? DvDemuxer.Open(new StreamForwardSource(new MemoryStream(file)), options)
                : DvDemuxer.Open(new MemoryMediaSource(file), options);
            for (int i = 0; i < 50 && d.TryReadPacket(out _); i++)
            {
            }

            if ((input[0] & 4) != 0)
            {
                _ = d.ReadMetadata().Facts.CaptureTime.Candidates.Count;
            }

            if (seek && d.CanSeek)
            {
                foreach (long ms in new long[] { 0, 500, 100_000 })
                {
                    d.Seek(new MediaTime(ms, 1000));
                    _ = d.TryReadPacket(out _);
                    d.Seek(new MediaTime(ms, 1000), SeekMode.NextKeyframe);
                    _ = d.TryReadPacket(out _);
                }
            }
        }
        catch (MediaException)
        {
        }
    }
}
