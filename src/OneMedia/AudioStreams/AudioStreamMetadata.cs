using System.Globalization;
using System.Linq;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.Ape;
using RecursiveAI.OneMedia.Tags.Id3;
using RecursiveAI.OneMedia.Tags.Lyrics3;

namespace RecursiveAI.OneMedia.AudioStreams;

/// <summary>Collects the tag containers of an audio stream (ID3v2 in front, ID3v1, APE and Lyrics3 behind) and the facts they and the stream header give.</summary>
internal static class AudioStreamMetadata
{
    public static MediaMetadata Read(IMediaSource? media, List<ByteRange> headTags, List<byte[]> headData, List<(string Kind, ByteRange Range)> tail, MediaTrack? track, TrackSetup? setup,
        MediaLimits limits, DefectLog defects, CancellationToken ct)
    {
        List<ITagContainer> containers = [];
        List<Chapter> chapters = [];
        for (int i = 0; i < headTags.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            Id3Tag? tag = ReadId3(media, headTags[i], i < headData.Count ? headData[i] : [], limits, defects);
            AddId3(tag, containers, chapters);
        }

        // File order: the blocks were found from the end backwards.
        foreach ((string kind, ByteRange range) in tail.OrderBy(t => t.Range.Offset))
        {
            ct.ThrowIfCancellationRequested();
            if (media is null)
            {
                continue;
            }

            try
            {
                switch (kind)
                {
                    case "id3v1" when Id3v1Tag.TryRead(media, range.End - Id3v1Tag.Length, out Id3v1Tag? v1, limits, defects) && v1 is not null:
                        containers.Add(v1);
                        break;
                    case "ape" when ApeTag.TryReadFromFooter(media, range.End - ApeTag.BlockSize, out ApeTag? ape, limits, defects) && ape is not null:
                        containers.Add(ape);
                        break;
                    case "lyrics3" when Lyrics3Tag.TryReadEndingAt(media, range.End, out Lyrics3Tag? lyrics, limits, defects) && lyrics is not null:
                        containers.Add(lyrics);
                        break;
                    case "id3v2":
                        AddId3(ReadId3(media, range, [], limits, defects), containers, chapters);
                        break;
                }
            }
            catch (MediaLimitExceededException e)
            {
                defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "tags.limit");
            }
        }

        FactsBuilder facts = new(defects);
        foreach (ITagContainer c in containers)
        {
            foreach (NativeTag t in c.Tags)
            {
                facts.AddNative(t);
            }
        }

        AddRecordingDate(facts, containers);
        if (track is not null && track.Duration.IsKnown)
        {
            facts.Duration.Add(0, new FactCandidate<MediaTime>(track.Duration, "audio-stream", track.Duration.ToString(), setup is { Exact: true } ? "frame count of the stream's Xing or VBRI header, less the encoder's delay and padding" : "estimated from the audio size and the length of the first frames"));
        }

        if (!string.IsNullOrEmpty(setup?.Encoder))
        {
            facts.Software.Add(75, new FactCandidate<string>(setup.Encoder, "audio-stream:encoder", setup.Encoder, "encoder version string of the stream header"));
        }

        return new MediaMetadata(containers, chapters, facts.Build(), defects, true);
    }

    private static Id3Tag? ReadId3(IMediaSource? media, ByteRange range, byte[] data, MediaLimits limits, DefectLog defects)
    {
        try
        {
            if (media is not null)
            {
                return Id3Tag.Read(media, range.Offset, limits, defects);
            }

            return data.Length > 0 ? Id3Tag.Read(data, range.Offset, limits, defects) : null;
        }
        catch (MediaLimitExceededException e)
        {
            defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "tags.limit");
            return null;
        }
        catch (MediaFormatException e)
        {
            defects.Add(e.Kind, DefectSeverity.Error, e.Offset, e.Message, "id3.header");
            return null;
        }
    }

    private static void AddId3(Id3Tag? tag, List<ITagContainer> containers, List<Chapter> chapters)
    {
        if (tag is null)
        {
            return;
        }

        containers.Add(tag);
        foreach ((Id3Chapter c, string title) in tag.Chapters())
        {
            chapters.Add(new Chapter(new MediaTime(c.StartMilliseconds, 1000), title, tag.Frames.First(f => f.Chapter == c).Range));
        }
    }

    // ID3v2.3 splits the recording date over TYER (year), TDAT (day and month as DDMM) and TIME (hour and minute as HHMM); 2.4 has TDRC, which the generic facts reader handles.
    private static void AddRecordingDate(FactsBuilder facts, List<ITagContainer> containers)
    {
        foreach (ITagContainer c in containers)
        {
            if (c is not Id3Tag tag)
            {
                continue;
            }

            string? year = Text(tag, "TYER", "TYE");
            string? dayMonth = Text(tag, "TDAT", "TDA");
            if (year is not { Length: 4 } || dayMonth is not { Length: 4 } || !int.TryParse(dayMonth, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            string? time = Text(tag, "TIME", "TIM");
            string composed = $"{year}-{dayMonth[2..4]}-{dayMonth[..2]}" + (time is { Length: 4 } ? $"T{time[..2]}:{time[2..4]}" : string.Empty);
            if (MediaDateTime.TryParse(composed, out MediaDateTime dt, out bool dateOnly))
            {
                Id3Frame? f = tag.Frames.FirstOrDefault(x => x.Id is "TYER" or "TYE");
                facts.CaptureTime.Add(56, new FactCandidate<MediaDateTime>(dt, "id3v2:TYER+TDAT" + (time is null ? string.Empty : "+TIME"), composed, dateOnly ? "ID3v2.3 year, day and month; date only" : "ID3v2.3 year, day, month, hour and minute", dt.Certainty, f?.Range ?? default));
            }
        }
    }

    private static string? Text(Id3Tag tag, string v23, string v22)
    {
        foreach (Id3Frame f in tag.Frames)
        {
            if ((f.Id == v23 || f.Id == v22) && f.Values.Count > 0)
            {
                return f.Values[0];
            }
        }

        return null;
    }
}
