using System;
using System.IO;
using RecursiveAI.OneMedia.Flac;
using RecursiveAI.OneMedia.Flv;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Ogg;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.VorbisComment;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the Ogg, native FLAC and FLV readers and the Vorbis comment parser. For the three readers the first input byte picks the path
/// (bit 0: forward-only source; bit 1: seek around after reading; bit 2: the asynchronous open) and the rest is the file; tags are read on every run.
/// For <c>vorbis-comment</c> bit 0 picks a PICTURE block instead of a comment header. Anything other than a <see cref="MediaException"/> escaping is a finding.
/// </summary>
internal static class XiphFlvTargets
{
    private static readonly MediaLimits Limits = new() { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxPacketBytes = 1 << 20, MaxTagBytes = 1 << 20, MaxAttachmentBytes = 1 << 20 };

    public static void Register(System.Collections.Generic.Dictionary<string, FuzzTarget> targets)
    {
        targets["ogg"] = i => Reader(i, 0);
        targets["flac"] = i => Reader(i, 1);
        targets["flv"] = i => Reader(i, 2);
        targets["vorbis-comment"] = VorbisComment;
    }

    private static void Reader(ReadOnlySpan<byte> input, int kind)
    {
        if (input.Length < 5)
        {
            return;
        }

        bool forward = (input[0] & 1) != 0;
        bool seek = (input[0] & 2) != 0;
        bool async = (input[0] & 4) != 0;
        byte[] file = input[1..].ToArray();
        MediaOpenOptions options = new() { Limits = Limits };
        try
        {
            if (async && !forward)
            {
                IMediaFormat format = kind == 0 ? new OggFormat() : kind == 1 ? new FlacFormat() : new FlvFormat();
                IAsyncDemuxer d = format.OpenAsync(new MemoryMediaSource(file), options).AsTask().GetAwaiter().GetResult();
                try
                {
                    for (int i = 0; i < 20_000 && d.ReadPacketAsync().AsTask().GetAwaiter().GetResult(); i++)
                    {
                    }

                    if (d is IAsyncMetadataProvider p)
                    {
                        _ = p.ReadMetadataAsync().AsTask().GetAwaiter().GetResult().NativeTags;
                    }
                }
                finally
                {
                    d.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                return;
            }

            using IDemuxer demuxer = Open(kind, file, forward, options);
            Drain(demuxer, 20_000);
            if (demuxer is IMetadataProvider mp)
            {
                MediaMetadata m = mp.ReadMetadata();
                foreach (NativeTag t in m.NativeTags)
                {
                    _ = t.Kind is TagValueKind.Binary ? t.ReadValue().Length : t.Text?.Length;
                }
            }

            if (seek && demuxer.CanSeek)
            {
                foreach (long ms in new long[] { 0, 500, 1500, 60_000 })
                {
                    demuxer.Seek(new MediaTime(ms, 1000));
                    Drain(demuxer, 200);
                    demuxer.Seek(new MediaTime(ms, 1000), SeekMode.NextKeyframe);
                    Drain(demuxer, 200);
                }

                if (demuxer.Tracks.Count > 0)
                {
                    demuxer.SetTrackEnabled(0, false);
                }

                demuxer.Seek(MediaTime.Zero);
                Drain(demuxer, 200);
            }
        }
        catch (MediaException)
        {
            // Typed rejection of bad data.
        }
    }

    private static IDemuxer Open(int kind, byte[] file, bool forward, MediaOpenOptions options) => kind switch
    {
        0 => forward ? OggDemuxer.Open(new StreamForwardSource(new MemoryStream(file)), options) : OggDemuxer.Open(new MemoryMediaSource(file), options),
        1 => forward ? FlacDemuxer.Open(new StreamForwardSource(new MemoryStream(file)), options) : FlacDemuxer.Open(new MemoryMediaSource(file), options),
        _ => forward ? FlvDemuxer.Open(new StreamForwardSource(new MemoryStream(file)), options) : FlvDemuxer.Open(new MemoryMediaSource(file), options),
    };

    private static void Drain(IDemuxer d, int max)
    {
        for (int i = 0; i < max && d.TryReadPacket(out _); i++)
        {
        }
    }

    private static void VorbisComment(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
        {
            return;
        }

        bool picture = (input[0] & 1) != 0;
        byte[] body = input[1..].ToArray();
        DefectLog defects = new(Limits);
        try
        {
            if (picture)
            {
                NativeTag? t = VorbisCommentReader.ReadPictureBlock(body, new MemoryMediaSource(body), 0, Limits, defects);
                if (t is not null)
                {
                    _ = t.ReadValue().Length;
                }
            }
            else
            {
                VorbisCommentBlock block = VorbisCommentReader.Read(body, 0, Limits, defects);
                foreach (NativeTag t in block.Tags)
                {
                    _ = t.Kind is TagValueKind.Binary ? t.ReadValue().Length : t.Text?.Length;
                }

                _ = new CommonTags(block.Tags).Title;
            }
        }
        catch (MediaException)
        {
        }
    }
}
