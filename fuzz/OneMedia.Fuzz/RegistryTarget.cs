using System;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Metadata;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz target for the whole registry: the bytes are sniffed, opened by whichever reader claims them, read to the end (or a packet cap), asked for
/// their metadata and seeked. It finds what the per-format targets cannot: two readers disagreeing about the same bytes, and a reader that is handed
/// a file it sniffed but cannot open. The first input byte picks the path (bit 0: seek around; bit 1: read the metadata);
/// the rest is the file. Anything other than a <see cref="MediaException"/> escaping is a finding.
/// </summary>
internal static class RegistryTarget
{
    public static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 17)
        {
            return;
        }

        bool seek = (input[0] & 1) != 0;
        bool metadata = (input[0] & 2) != 0;
        byte[] file = input[1..].ToArray();
        MediaLimits limits = new() { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxPacketBytes = 1 << 20, MaxTagBytes = 1 << 20 };
        MediaOpenOptions options = new() { Limits = limits };
        try
        {
            using IDemuxer d = MediaFormats.Default.Open(new MemoryMediaSource(file), options);
            Drain(d, 20_000);
            if (metadata && d is IMetadataProvider provider)
            {
                _ = provider.ReadMetadata();
            }

            if (seek && d.CanSeek)
            {
                foreach (long ms in new long[] { 0, 500, 1500, 60_000 })
                {
                    d.Seek(new MediaTime(ms, 1000));
                    Drain(d, 200);
                }
            }
        }
        catch (MediaException)
        {
            // Typed rejection of bad data.
        }
    }

    private static void Drain(IDemuxer d, int cap)
    {
        for (int i = 0; i < cap && d.TryReadPacket(out PacketView _); i++)
        {
        }
    }
}
