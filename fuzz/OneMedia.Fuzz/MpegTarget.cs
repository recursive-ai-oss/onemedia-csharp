using System;
using System.IO;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Avchd;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.ElementaryStreams;
using RecursiveAI.OneMedia.MpegPs;
using RecursiveAI.OneMedia.MpegTs;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the transport stream, program stream, video elementary stream and MDPM readers. For the stream targets the first
/// input byte picks the path (bit 0: read as a forward-only stream; bit 1: seek around and read the metadata afterwards; for
/// <c>video-es</c> bits 2-4 pick the stream kind); the rest is the file. Anything other than a <see cref="MediaException"/> escaping is a
/// finding.
/// </summary>
internal static class MpegTarget
{
    public static void Register(System.Collections.Generic.Dictionary<string, FuzzTarget> targets)
    {
        targets["mpegts"] = i => Stream(i, (s, o) => MpegTsDemuxer.Open(s, o), (s, o) => MpegTsDemuxer.Open(s, o));
        targets["mpegps"] = i => Stream(i, (s, o) => MpegPsDemuxer.Open(s, o), (s, o) => MpegPsDemuxer.Open(s, o));
        targets["video-es"] = VideoEs;
        targets["mdpm"] = Mdpm;
    }

    private static MediaOpenOptions Options() => new()
    {
        // Small limits keep each run fast and exercise the limit paths.
        Limits = new MediaLimits { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxPacketBytes = 1 << 20 },
    };

    private static void Stream(ReadOnlySpan<byte> input, Func<IMediaSource, MediaOpenOptions, IDemuxer> open, Func<IForwardMediaSource, MediaOpenOptions, IDemuxer> openForward)
    {
        if (input.Length < 9)
        {
            return;
        }

        bool forward = (input[0] & 1) != 0;
        bool seek = (input[0] & 2) != 0;
        byte[] file = input[1..].ToArray();
        try
        {
            using IDemuxer d = forward
                ? openForward(new StreamForwardSource(new MemoryStream(file)), Options())
                : open(new MemoryMediaSource(file), Options());
            Exercise(d, seek);
        }
        catch (MediaException)
        {
            // Typed rejection of bad data.
        }
    }

    private static void VideoEs(ReadOnlySpan<byte> input)
    {
        if (input.Length < 9)
        {
            return;
        }

        bool forward = (input[0] & 1) != 0;
        bool seek = (input[0] & 2) != 0;
        VideoStreamKind kind = (VideoStreamKind)((input[0] >> 2) % 5);
        byte[] file = input[1..].ToArray();
        try
        {
            if ((input[0] & 0x80) != 0)
            {
                using IDemuxer ivf = forward
                    ? IvfDemuxer.Open(new StreamForwardSource(new MemoryStream(file)), Options())
                    : IvfDemuxer.Open(new MemoryMediaSource(file), Options());
                Exercise(ivf, seek);
                return;
            }

            using IDemuxer d = forward
                ? VideoStreamDemuxer.Open(new StreamForwardSource(new MemoryStream(file)), kind, Options())
                : VideoStreamDemuxer.Open(new MemoryMediaSource(file), kind, Options());
            Exercise(d, seek);
        }
        catch (MediaException)
        {
        }
    }

    private static void Exercise(IDemuxer d, bool seek)
    {
        for (int i = 0; i < 20_000 && d.TryReadPacket(out _); i++)
        {
        }

        if (d is IMetadataProvider mp)
        {
            _ = mp.ReadMetadata();
        }

        if (seek && d.CanSeek)
        {
            foreach (long ms in new long[] { 0, 500, 1500, 60_000 })
            {
                d.Seek(new MediaTime(ms, 1000));
                for (int i = 0; i < 200 && d.TryReadPacket(out _); i++)
                {
                }

                d.Seek(new MediaTime(ms, 1000), SeekMode.NextKeyframe);
                for (int i = 0; i < 200 && d.TryReadPacket(out _); i++)
                {
                }
            }
        }
    }

    private static void Mdpm(ReadOnlySpan<byte> input)
    {
        if (Avchd.Mdpm.TryParse(input, out MdpmBlock? block) && block is not null)
        {
            _ = block.RecordingTime;
            _ = block.Make;
            _ = block.MakerModelCodes;
            _ = block.RecordingTimeFlags;
        }
    }
}
