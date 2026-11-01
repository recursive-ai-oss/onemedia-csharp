using System;
using System.Collections.Generic;
using RecursiveAI.OneMedia.AudioStreams;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.Ape;
using RecursiveAI.OneMedia.Tags.Id3;
using RecursiveAI.OneMedia.Tags.Lyrics3;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Targets for the audio tag layers and the audio elementary-stream readers. Anything other than a <see cref="MediaException"/> escaping is a finding.
/// <list type="bullet">
/// <item><c>id3</c>: first byte bit 0 reads the rest as an ID3v2 tag at offset 0, else as an ID3v1/Lyrics3 tail; also feeds the bytes through the in-memory entry point.</item>
/// <item><c>ape</c>: first byte bit 0 reads an APEv2 tag from its footer at the end, else from a header at the start.</item>
/// <item><c>mpa-es</c>: first byte bit 0 forward-only, bit 1 the asynchronous reader, bit 2 seeks; the rest is an MPEG audio stream.</item>
/// <item><c>adts-ac3-amr-es</c>: as <c>mpa-es</c>, and bits 4-5 pick ADTS, AC-3/E-AC-3 or AMR.</item>
/// </list>
/// </summary>
internal static class AudioStreamFuzz
{
    private static readonly MediaLimits Small = new() { MaxAllocationBytes = 4 << 20, MaxSampleTableEntries = 2_000_000, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxPacketBytes = 1 << 20, MaxTagBytes = 1 << 20, MaxAttachmentBytes = 1 << 20 };

    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["id3"] = Id3;
        targets["ape"] = Ape;
        targets["mpa-es"] = span => Stream(span, 0);
        targets["adts-ac3-amr-es"] = span => Stream(span, 1 + (span.Length > 0 ? (span[0] >> 4) % 3 : 0));
    }

    private static void Id3(ReadOnlySpan<byte> input)
    {
        if (input.Length < 11)
        {
            return;
        }

        bool v2 = (input[0] & 1) != 0;
        byte[] data = input[1..].ToArray();
        DefectLog log = new(Small);
        try
        {
            MemoryMediaSource source = new(data);
            if (v2)
            {
                Id3Tag tag = Id3Tag.Read(source, 0, Small, log);
                Touch(tag);
                Touch(Id3Tag.Read(data, 7, Small, new DefectLog(Small)));
                _ = tag.Chapters();
            }
            else
            {
                if (Id3v1Tag.TryRead(source, Math.Max(0, data.Length - Id3v1Tag.Length), out Id3v1Tag? v1, Small, log))
                {
                    Touch(v1!);
                }

                if (Lyrics3Tag.TryReadEndingAt(source, data.Length, out Lyrics3Tag? l3, Small, log))
                {
                    Touch(l3!);
                }
            }
        }
        catch (MediaException)
        {
            // Typed rejection of bad data.
        }
    }

    private static void Ape(ReadOnlySpan<byte> input)
    {
        if (input.Length < 33)
        {
            return;
        }

        bool footer = (input[0] & 1) != 0;
        byte[] data = input[1..].ToArray();
        DefectLog log = new(Small);
        try
        {
            MemoryMediaSource source = new(data);
            ApeTag? tag;
            bool found = footer ? ApeTag.TryReadFromFooter(source, data.Length - ApeTag.BlockSize, out tag, Small, log) : ApeTag.TryReadFromHeader(source, 0, out tag, Small, log);
            if (found)
            {
                Touch(tag!);
                foreach (ApeItem item in tag!.Items)
                {
                    _ = item.Key;
                    _ = item.Type;
                }
            }
        }
        catch (MediaException)
        {
            // Typed rejection of bad data.
        }
    }

    private static void Touch(ITagContainer container)
    {
        foreach (NativeTag t in container.Tags)
        {
            _ = t.Text;
            if (t.Kind is TagValueKind.Binary or TagValueKind.Image)
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

        CommonTags common = new(container.Tags);
        foreach (CommonField f in Enum.GetValues<CommonField>())
        {
            _ = common.Values(f);
        }

        foreach (TagPicture p in common.Pictures)
        {
            _ = p.Length;
        }
    }

    private static void Stream(ReadOnlySpan<byte> input, int kind)
    {
        if (input.Length < 8)
        {
            return;
        }

        byte flags = input[0];
        bool forward = (flags & 1) != 0;
        bool async = (flags & 2) != 0;
        bool seek = (flags & 4) != 0;
        byte[] data = input[1..].ToArray();
        MediaOpenOptions options = new() { Limits = Small };
        try
        {
            if (async)
            {
                AudioStreamAsyncDemuxer d = OpenAsync(data, kind, options);
                try
                {
                    int n = 0;
                    while (n++ < 20_000 && d.ReadPacketAsync().AsTask().GetAwaiter().GetResult())
                    {
                        _ = d.Current.Data.Length;
                    }

                    if (seek)
                    {
                        _ = d.SeekAsync(new MediaTime(data.Length % 1000, 8000)).AsTask().GetAwaiter().GetResult();
                    }

                    Touch(d.ReadMetadataAsync().AsTask().GetAwaiter().GetResult());
                }
                finally
                {
                    d.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                return;
            }

            using AudioStreamDemuxer demuxer = forward ? OpenForward(data, kind, options) : OpenSync(data, kind, options);
            int count = 0;
            while (count++ < 20_000 && demuxer.TryReadPacket(out PacketView p))
            {
                _ = p.Data.Length;
            }

            if (seek && demuxer.CanSeek)
            {
                _ = demuxer.Seek(new MediaTime(data.Length % 1000, 8000));
                _ = demuxer.TryReadPacket(out _);
            }

            if (!forward)
            {
                Touch(demuxer.ReadMetadata());
            }
        }
        catch (MediaException)
        {
            // Typed rejection of bad data.
        }
    }

    private static AudioStreamDemuxer OpenSync(byte[] data, int kind, MediaOpenOptions options)
    {
        MemoryMediaSource source = new(data);
        return kind switch { 0 => MpegAudioDemuxer.Open(source, options), 1 => AdtsDemuxer.Open(source, options), 2 => Ac3Demuxer.Open(source, options), _ => AmrDemuxer.Open(source, options) };
    }

    private static AudioStreamDemuxer OpenForward(byte[] data, int kind, MediaOpenOptions options)
    {
        StreamForwardSource source = new(new System.IO.MemoryStream(data));
        return kind switch { 0 => MpegAudioDemuxer.Open(source, options), 1 => AdtsDemuxer.Open(source, options), 2 => Ac3Demuxer.Open(source, options), _ => AmrDemuxer.Open(source, options) };
    }

    private static AudioStreamAsyncDemuxer OpenAsync(byte[] data, int kind, MediaOpenOptions options)
    {
        IMediaFormat format = kind switch { 0 => new MpegAudioFormat(), 1 => new AdtsFormat(), 2 => new Ac3Format(), _ => new AmrFormat() };
        return (AudioStreamAsyncDemuxer)format.OpenAsync(new MemoryMediaSource(data), options).AsTask().GetAwaiter().GetResult();
    }

    private static void Touch(MediaMetadata m)
    {
        foreach (NativeTag t in m.NativeTags)
        {
            _ = t.Text;
            if (t.Kind is TagValueKind.Binary or TagValueKind.Image)
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

        _ = m.Facts.Duration.Best;
        _ = m.Facts.CaptureTime.Best;
        _ = m.Chapters.Count;
    }
}
