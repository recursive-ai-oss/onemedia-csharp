using System.Collections.Generic;
using System;
using System.IO;
using System.Threading.Tasks;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Aiff;
using RecursiveAI.OneMedia.Caf;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the chunk readers and the WAVE (<c>riff-wav</c>), AIFF (<c>aiff</c>) and CAF (<c>caf</c>) demuxers with their tag layers. The first input
/// byte picks the path (bit 0: forward-only stream; bit 1: seek around after reading; bit 2: ranged asynchronous source; bit 3: read the metadata); the
/// rest is the file. Anything other than a <see cref="MediaException"/> escaping is a finding.
/// </summary>
internal static class PcmTargets
{
    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["riff-wav"] = input => Run(input, Kind.Wav);
        targets["aiff"] = input => Run(input, Kind.Aiff);
        targets["caf"] = input => Run(input, Kind.Caf);
    }

    private enum Kind
    {
        Wav,
        Aiff,
        Caf,
    }

    private static AudioFileDemuxer Open(Kind kind, byte[] file, bool forward, MediaOpenOptions options) => kind switch
    {
        Kind.Wav => forward ? WavDemuxer.Open(new StreamForwardSource(new MemoryStream(file)), options) : WavDemuxer.Open(new MemoryMediaSource(file), options),
        Kind.Aiff => forward ? AiffDemuxer.Open(new StreamForwardSource(new MemoryStream(file)), options) : AiffDemuxer.Open(new MemoryMediaSource(file), options),
        _ => forward ? CafDemuxer.Open(new StreamForwardSource(new MemoryStream(file)), options) : CafDemuxer.Open(new MemoryMediaSource(file), options),
    };

    private static IMediaFormat Format(Kind kind) => kind switch
    {
        Kind.Wav => new WavFileFormat(),
        Kind.Aiff => new AiffFileFormat(),
        _ => new CafFileFormat(),
    };

    private static void Run(ReadOnlySpan<byte> input, Kind kind)
    {
        if (input.Length < 9)
        {
            return;
        }

        bool forward = (input[0] & 1) != 0;
        bool seek = (input[0] & 2) != 0;
        bool ranged = (input[0] & 4) != 0;
        bool metadata = (input[0] & 8) != 0;
        byte[] file = input[1..].ToArray();
        // Small limits keep each run fast and exercise the limit paths.
        MediaLimits limits = new() { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxPacketBytes = 1 << 20, MaxTagBytes = 1 << 20 };
        MediaOpenOptions options = new() { Limits = limits };
        try
        {
            if (ranged)
            {
                RunRanged(kind, file, options, metadata);
                return;
            }

            using AudioFileDemuxer d = Open(kind, file, forward, options);
            Drain(d, 20_000);
            if (metadata)
            {
                _ = d.ReadMetadata();
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
            }
        }
        catch (MediaException)
        {
            // Typed rejection of bad data.
        }

        if (kind != Kind.Caf)
        {
            try
            {
                ChunkFile? chunks = ChunkFile.TryOpen(new MemoryMediaSource(file), limits, new DefectLog(limits));
                if (chunks is not null)
                {
                    int n = 0;
                    foreach (ChunkNode top in chunks.Chunks)
                    {
                        Walk(top, 0, ref n);
                    }
                }
            }
            catch (MediaException)
            {
            }
        }
    }

    // The ranged path must give what the plain path gives, packet for packet.
    private static void RunRanged(Kind kind, byte[] file, MediaOpenOptions options, bool metadata)
    {
        long expectedPackets = 0, expectedBytes = 0;
        bool plainOk = true;
        try
        {
            using AudioFileDemuxer plain = Open(kind, file, false, options with { Defects = null });
            while (expectedPackets < 20_000 && plain.TryReadPacket(out PacketView p))
            {
                expectedPackets++;
                expectedBytes += p.Data.Length;
            }
        }
        catch (MediaException)
        {
            plainOk = false;
        }

        try
        {
            long packets = 0, bytes = 0;
            Task.Run(async () =>
            {
                await using IAsyncDemuxer d = await Format(kind).OpenAsync(new MemoryMediaSource(file), options with { Defects = null });
                while (packets < 20_000 && await d.ReadPacketAsync())
                {
                    packets++;
                    bytes += d.Current.Data.Length;
                }

                if (metadata)
                {
                    _ = await ((IAsyncMetadataProvider)d).ReadMetadataAsync();
                }
            }).GetAwaiter().GetResult();
            if (!plainOk || packets != expectedPackets || bytes != expectedBytes)
            {
                throw new InvalidOperationException($"the ranged read gave {packets} packets / {bytes} bytes, the plain read {expectedPackets} / {expectedBytes} (plain opened: {plainOk})");
            }
        }
        catch (MediaException)
        {
            if (plainOk)
            {
                throw new InvalidOperationException("the plain read opened the file but the ranged read rejected it");
            }
        }
    }

    private static void Walk(ChunkNode node, int depth, ref int count)
    {
        if (++count > 5000 || depth > 24)
        {
            return;
        }

        if (node.IsContainer)
        {
            foreach (ChunkNode child in node.Chunks)
            {
                Walk(child, depth + 1, ref count);
            }
        }
    }

    private static void Drain(AudioFileDemuxer d, int max)
    {
        for (int i = 0; i < max && d.TryReadPacket(out _); i++)
        {
        }
    }
}
