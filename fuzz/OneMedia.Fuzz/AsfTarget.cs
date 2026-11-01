using System;
using System.IO;
using System.Threading.Tasks;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Asf;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz target for the ASF header reader, the data packet parser, the demuxer and the tag layer. The first input byte picks the path
/// (bit 0: read forward-only; bit 1: seek around after reading; bit 2: the asynchronous reader); the rest is the file. The header
/// tree is walked and all tags are read on every run. Anything other than a <see cref="MediaException"/> escaping is a finding.
/// </summary>
internal static class AsfTarget
{
    public static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 31)
        {
            return;
        }

        bool forward = (input[0] & 1) != 0;
        bool seek = (input[0] & 2) != 0;
        bool async = (input[0] & 4) != 0;
        byte[] file = input[1..].ToArray();
        MediaLimits limits = new() { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxPacketBytes = 1 << 20 };
        MediaOpenOptions options = new() { Limits = limits };
        try
        {
            if (async)
            {
                RunAsync(file, forward, seek, options).AsTask().GetAwaiter().GetResult();
                return;
            }

            using AsfDemuxer d = forward
                ? AsfDemuxer.Open(new StreamForwardSource(new MemoryStream(file)), options)
                : AsfDemuxer.Open(new MemoryMediaSource(file), options);
            Walk(d.Header.Root, 0);
            Touch(d.ReadMetadata());
            for (int i = 0; i < 20_000 && d.TryReadPacket(out _); i++)
            {
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
        }
        catch (MediaException)
        {
            // Typed rejection of bad data.
        }
    }

    private static async ValueTask RunAsync(byte[] file, bool forward, bool seek, MediaOpenOptions options)
    {
        AsfAsyncDemuxer d = forward
            ? await AsfAsyncDemuxer.OpenAsync(new StreamForwardSource(new MemoryStream(file)), options)
            : await AsfAsyncDemuxer.OpenAsync(new MemoryMediaSource(file), options);
        try
        {
            Touch(await d.ReadMetadataAsync());
            for (int i = 0; i < 20_000 && await d.ReadPacketAsync(); i++)
            {
            }

            if (seek && d.CanSeek)
            {
                await d.SeekAsync(new MediaTime(500, 1000));
                for (int i = 0; i < 200 && await d.ReadPacketAsync(); i++)
                {
                }
            }
        }
        finally
        {
            await d.DisposeAsync();
        }
    }

    private static void Drain(AsfDemuxer d, int max)
    {
        for (int i = 0; i < max && d.TryReadPacket(out _); i++)
        {
        }
    }

    private static void Walk(IStructureNode node, int depth)
    {
        if (depth > 24)
        {
            return;
        }

        foreach (IStructureNode child in node.Children)
        {
            Walk(child, depth + 1);
        }
    }

    private static void Touch(MediaMetadata m)
    {
        foreach (NativeTag t in m.NativeTags)
        {
            _ = t.Text;
            if (t.Kind == TagValueKind.Binary || t.Kind == TagValueKind.Image)
            {
                try
                {
                    _ = t.ReadValue();
                }
                catch (MediaLimitExceededException)
                {
                    // A value over the limit is refused with a typed exception.
                }
            }
        }

        foreach (CommonField f in Enum.GetValues<CommonField>())
        {
            _ = m.Tags.Values(f);
        }

        foreach (TagPicture p in m.Tags.Pictures)
        {
            _ = p.Length;
        }

        _ = m.Facts.Duration.Best;
        _ = m.Facts.CaptureTime.Best;
        _ = m.Facts.FrameSize.Best;
        _ = m.Defects.Count;
    }
}
