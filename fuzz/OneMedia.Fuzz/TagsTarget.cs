using System;
using System.IO;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz target for the tag layers and basic facts. The first input byte picks the path (bit 0: read the file as Matroska rather than ISO
/// BMFF; bit 1: use the asynchronous reader); the rest is the file. Every native tag's value, the common view, the pictures and the facts are
/// read, so lazily decoded values are exercised too. Anything other than a <see cref="MediaException"/> escaping is a finding.
/// </summary>
internal static class TagsTarget
{
    public static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 9)
        {
            return;
        }

        bool matroska = (input[0] & 1) != 0;
        bool async = (input[0] & 2) != 0;
        byte[] file = input[1..].ToArray();
        // Small limits keep each run fast and exercise the limit paths.
        MediaOpenOptions options = new()
        {
            Limits = new MediaLimits { MaxAllocationBytes = 4 << 20, MaxSampleTableEntries = 2_000_000, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxPacketBytes = 1 << 20, MaxTagBytes = 1 << 20, MaxAttachmentBytes = 1 << 20 },
        };
        try
        {
            MediaMetadata metadata;
            if (matroska && async)
            {
                MatroskaAsyncDemuxer d = MatroskaAsyncDemuxer.OpenAsync(new MemoryMediaSource(file), options).AsTask().GetAwaiter().GetResult();
                try
                {
                    metadata = d.ReadMetadataAsync().AsTask().GetAwaiter().GetResult();
                    Touch(metadata);
                }
                finally
                {
                    d.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                return;
            }

            if (matroska)
            {
                using MatroskaDemuxer d = MatroskaDemuxer.Open(new MemoryMediaSource(file), options);
                Touch(d.ReadMetadata());
                return;
            }

            if (async)
            {
                IsoBmffAsyncDemuxer d = IsoBmffAsyncDemuxer.OpenAsync(new MemoryMediaSource(file), options).AsTask().GetAwaiter().GetResult();
                try
                {
                    Touch(d.ReadMetadata());
                }
                finally
                {
                    d.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                return;
            }

            using IsoBmffDemuxer iso = IsoBmffDemuxer.Open(new MemoryMediaSource(file), options);
            Touch(iso.ReadMetadata());
        }
        catch (MediaException)
        {
            // Typed rejection of bad data.
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

        MediaFacts facts = m.Facts;
        _ = facts.Duration.Best;
        _ = facts.CaptureTime.Best;
        _ = facts.Location.Best;
        _ = facts.Make.Best;
        _ = facts.Model.Best;
        _ = facts.Software.Best;
        _ = facts.Orientation.Best;
        _ = facts.FrameSize.Best;
        _ = m.Chapters.Count;
        _ = m.Defects.Count;
    }
}
