using System.Globalization;
using System.Linq;
using static RecursiveAI.OneMedia.Streaming.PackagingRun;

namespace RecursiveAI.OneMedia.Streaming;

/// <summary>Builds the HLS playlists and the MPD for what a <see cref="PackagingRun"/> has written.</summary>
internal static class PackagingManifests
{
    private static string FileName(string relative) => relative[(relative.LastIndexOf('/') + 1)..];

    private static string? Language(MediaTrack t) => t.Language is { Length: > 0 } l && l != "und" ? l : null;

    private static string FrameRate(VideoTrackInfo v)
    {
        if (v.FrameRateNumerator <= 0 || v.FrameRateDenominator <= 0)
        {
            return string.Empty;
        }

        long milli = (((long)v.FrameRateNumerator * 1000) + (v.FrameRateDenominator / 2)) / v.FrameRateDenominator;
        return string.Create(CultureInfo.InvariantCulture, $"{milli / 1000}.{milli % 1000:D3}");
    }

    private static DateTimeOffset At(DateTimeOffset start, long ticks, long timescale) =>
        start.AddTicks((long)((Int128)ticks * TimeSpan.TicksPerSecond / Math.Max(timescale, 1)));

    // ---- HLS --------------------------------------------------------------------------------------------------

    public static HlsMediaPlaylist HlsMedia(TrackOut t, PackagingOptions options, bool ended)
    {
        bool single = options.SingleFile;
        long ts = Math.Max(t.Track.Timescale, 1);
        HlsMap map = new(single ? "media.mp4" : "init.mp4", single ? new HlsByteRange(t.InitLength, 0) : null);
        List<HlsSegment> segments = new(t.Segments.Count);
        foreach (SegmentRecord r in t.Segments)
        {
            segments.Add(new HlsSegment
            {
                Uri = single ? "media.mp4" : FileName(r.File),
                Duration = new MediaTime(r.Info.DurationTicks, ts),
                ByteRange = single ? new HlsByteRange(r.Bytes, r.Offset) : null,
                Map = map,
                ProgramDateTime = options.Live is { ProgramDateTime: true } live ? At(live.StartTime, r.Info.StartTicks, ts) : null,
            });
        }

        return new HlsMediaPlaylist
        {
            // RFC 8216 §7 puts EXT-X-MAP at version 6; Apple's fragmented-MP4 profile asks for 7.
            Version = 7,
            IndependentSegments = true,
            MediaSequence = options.Live is null ? 0 : t.FirstNumber - 1,
            PlaylistType = options.Live is null ? HlsPlaylistType.Vod : HlsPlaylistType.None,
            EndList = ended,
            Segments = segments,
        };
    }

    public static HlsMediaPlaylist HlsText(TextOut x, TrackOut reference)
    {
        long ts = Math.Max(reference.Track.Timescale, 1);
        List<HlsSegment> segments = [];
        for (int i = 0; i < reference.Segments.Count; i++)
        {
            segments.Add(new HlsSegment { Uri = $"seg-{(i + 1).ToString("D5", CultureInfo.InvariantCulture)}.vtt", Duration = new MediaTime(reference.Segments[i].Info.DurationTicks, ts) });
        }

        return new HlsMediaPlaylist { Version = 3, PlaylistType = HlsPlaylistType.Vod, EndList = true, Segments = segments };
    }

    // Handler names such as "SoundHandler" describe the muxer that wrote the file, not the track.
    private static string NameOf(MediaTrack t, string fallback) =>
        !string.IsNullOrWhiteSpace(t.Name) && !t.Name.EndsWith("Handler", StringComparison.OrdinalIgnoreCase) && !t.Name.StartsWith("Core Media", StringComparison.Ordinal) ? t.Name : Language(t) is { } l ? l : fallback;

    public static HlsMultivariantPlaylist HlsMaster(IReadOnlyList<TrackOut> tracks, IReadOnlyList<TextOut> text, PackagingOptions options)
    {
        List<TrackOut> video = tracks.Where(t => t.Track.Kind == TrackKind.Video).ToList();
        List<TrackOut> audio = tracks.Where(t => t.Track.Kind == TrackKind.Audio).ToList();
        List<HlsRendition> renditions = [];
        List<HlsVariant> variants = [];
        string? subsGroup = text.Count > 0 ? "subs" : null;
        for (int i = 0; i < text.Count; i++)
        {
            PackagingSubtitle s = text[i].Subtitle;
            renditions.Add(new HlsRendition
            {
                Type = HlsMediaType.Subtitles,
                GroupId = "subs",
                Name = s.Name ?? s.Language ?? $"Subtitles {i + 1}",
                Language = s.Language,
                Uri = $"{text[i].Name}/index.m3u8",
                Default = s.Default,
                AutoSelect = s.Default || s.Forced,
                Forced = s.Forced,
            });
        }

        if (video.Count > 0)
        {
            string? audioGroup = audio.Count > 0 ? "audio" : null;
            for (int i = 0; i < audio.Count; i++)
            {
                TrackOut a = audio[i];
                MediaTrack d = a.Track.Declared ?? a.Track.Input;
                renditions.Add(new HlsRendition
                {
                    Type = HlsMediaType.Audio,
                    GroupId = "audio",
                    Name = NameOf(d, $"Audio {i + 1}"),
                    Language = Language(d),
                    Uri = $"{a.Name}/index.m3u8",
                    Default = i == 0,
                    AutoSelect = true,
                    Channels = d.Audio is { Channels: > 0 } ai ? ai.Channels.ToString(CultureInfo.InvariantCulture) : null,
                });
            }

            TrackOut? defaultAudio = audio.Count > 0 ? audio[0] : null;
            foreach (TrackOut v in video)
            {
                MediaTrack d = v.Track.Declared ?? v.Track.Input;
                string codecs = string.Join(',', new[] { v.Track.Codecs, defaultAudio?.Track.Codecs }.Where(c => !string.IsNullOrEmpty(c)));
                variants.Add(new HlsVariant
                {
                    Uri = $"{v.Name}/index.m3u8",
                    Bandwidth = v.Track.PeakBitsPerSecond + (defaultAudio?.Track.PeakBitsPerSecond ?? 0),
                    AverageBandwidth = v.Track.AverageBitsPerSecond + (defaultAudio?.Track.AverageBitsPerSecond ?? 0),
                    Codecs = codecs.Length > 0 ? codecs : null,
                    Width = d.Video?.Width ?? 0,
                    Height = d.Video?.Height ?? 0,
                    FrameRate = d.Video is { } vi && FrameRate(vi) is { Length: > 0 } fr ? fr : null,
                    AudioGroup = audioGroup,
                    SubtitleGroup = subsGroup,
                });
            }
        }
        else
        {
            foreach (TrackOut a in audio)
            {
                variants.Add(new HlsVariant
                {
                    Uri = $"{a.Name}/index.m3u8",
                    Bandwidth = a.Track.PeakBitsPerSecond,
                    AverageBandwidth = a.Track.AverageBitsPerSecond,
                    Codecs = a.Track.Codecs,
                    SubtitleGroup = subsGroup,
                });
            }
        }

        return new HlsMultivariantPlaylist { Version = 7, IndependentSegments = true, Renditions = renditions, Variants = variants };
    }

    // ---- DASH -------------------------------------------------------------------------------------------------

    private static List<DashTimelineEntry> Timeline(TrackOut t)
    {
        List<DashTimelineEntry> entries = [];
        long expected = -1;
        foreach (SegmentRecord r in t.Segments)
        {
            long start = r.Info.StartTicks, duration = r.Info.DurationTicks;
            if (entries.Count > 0 && start == expected && entries[^1].Duration == duration)
            {
                DashTimelineEntry last = entries[^1];
                entries[^1] = last with { Repeat = last.Repeat + 1 };
            }
            else
            {
                entries.Add(new DashTimelineEntry(entries.Count == 0 || start != expected ? start : null, duration));
            }

            expected = start + duration;
        }

        return entries;
    }

    private static DashRepresentation Representation(TrackOut t, PackagingOptions options)
    {
        MediaTrack d = t.Track.Declared ?? t.Track.Input;
        DashSegmentTemplate? template = null;
        DashSegmentBase? segmentBase = null;
        List<string> baseUrls = [];
        long ts = Math.Max(t.Track.Timescale, 1);
        if (options.SingleFile)
        {
            baseUrls.Add($"{t.Name}/media.mp4");
            segmentBase = new DashSegmentBase
            {
                Timescale = ts,
                IndexRange = string.Create(CultureInfo.InvariantCulture, $"{t.SidxOffset}-{t.SidxOffset + t.SidxLength - 1}"),
                Initialization = new DashUrlRange(null, string.Create(CultureInfo.InvariantCulture, $"0-{t.InitLength - 1}")),
            };
        }
        else
        {
            template = new DashSegmentTemplate
            {
                Timescale = ts,
                StartNumber = options.Live is null ? 1 : t.FirstNumber,
                Media = $"{t.Name}/seg-$Number%05d$.m4s",
                Initialization = $"{t.Name}/init.mp4",
                Timeline = Timeline(t),
            };
        }

        List<DashDescriptor> descriptors = [];
        if (t.Track.Kind == TrackKind.Audio && d.Audio is { Channels: > 0 } audio)
        {
            descriptors.Add(new DashDescriptor("AudioChannelConfiguration", "urn:mpeg:dash:23003:3:audio_channel_configuration:2011", audio.Channels.ToString(CultureInfo.InvariantCulture)));
        }

        string? sar = d.Video is { } v && (v.PixelAspectNumerator != v.PixelAspectDenominator) && v.PixelAspectDenominator > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{v.PixelAspectNumerator}:{v.PixelAspectDenominator}")
            : null;
        string? fps = d.Video is { FrameRateNumerator: > 0, FrameRateDenominator: > 0 } fv
            ? fv.FrameRateDenominator == 1 ? fv.FrameRateNumerator.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{fv.FrameRateNumerator}/{fv.FrameRateDenominator}")
            : null;
        return new DashRepresentation
        {
            Id = t.Name,
            Bandwidth = Math.Max(t.Track.PeakBitsPerSecond, 1),
            Codecs = t.Track.Codecs,
            MimeType = t.Track.Kind == TrackKind.Video ? "video/mp4" : "audio/mp4",
            Width = d.Video?.Width ?? 0,
            Height = d.Video?.Height ?? 0,
            FrameRate = fps,
            SampleAspectRatio = sar,
            AudioSamplingRate = d.Audio?.SampleRate ?? 0,
            StartWithSap = 1,
            BaseUrls = baseUrls,
            Descriptors = descriptors,
            SegmentTemplate = template,
            SegmentBase = segmentBase,
        };
    }

    public static DashMpd Mpd(IReadOnlyList<TrackOut> tracks, IReadOnlyList<TextOut> text, PackagingOptions options, bool ended, List<string> notes)
    {
        bool live = options.Live is not null && !ended;
        List<DashAdaptationSet> sets = [];
        int id = 0;
        foreach (IGrouping<string, TrackOut> group in tracks.GroupBy(t => $"{(int)t.Track.Kind}:{(t.Track.Codecs ?? "?").Split('.')[0]}:{(t.Track.Kind == TrackKind.Audio ? Language(t.Track.Declared ?? t.Track.Input) : null)}"))
        {
            List<TrackOut> members = group.ToList();
            TrackOut first = members[0];
            MediaTrack d = first.Track.Declared ?? first.Track.Input;
            bool aligned = members.All(m => SameBoundaries(first, m));
            bool video = first.Track.Kind == TrackKind.Video;
            sets.Add(new DashAdaptationSet
            {
                Id = id++.ToString(CultureInfo.InvariantCulture),
                ContentType = video ? "video" : "audio",
                MimeType = video ? "video/mp4" : "audio/mp4",
                Language = video ? null : Language(d),
                SegmentAlignment = aligned,
                SubsegmentAlignment = options.SingleFile && aligned,
                StartWithSap = 1,
                Descriptors = [new DashDescriptor("Role", "urn:mpeg:dash:role:2011", "main")],
                Representations = members.Select(m => Representation(m, options)).ToList(),
            });
        }

        for (int i = 0; i < text.Count; i++)
        {
            PackagingSubtitle s = text[i].Subtitle;
            sets.Add(new DashAdaptationSet
            {
                Id = id++.ToString(CultureInfo.InvariantCulture),
                ContentType = "text",
                MimeType = "text/vtt",
                Language = s.Language,
                Descriptors = [new DashDescriptor("Role", "urn:mpeg:dash:role:2011", s.Forced ? "forced-subtitle" : "subtitle")],
                // A text/vtt file is one segment: the representation is the file (DASH-IF guidelines, sidecar subtitles).
                Representations = [new DashRepresentation { Id = text[i].Name, Bandwidth = 256, MimeType = "text/vtt", BaseUrls = [$"{text[i].Name}/subs.vtt"] }],
            });
        }

        MediaTime end = MediaTime.Zero;
        MediaTime longest = MediaTime.Zero;
        foreach (TrackOut t in tracks)
        {
            long ts = Math.Max(t.Track.Timescale, 1);
            end = MediaTime.Max(end, new MediaTime(t.EndTicks, ts));
            foreach (SegmentRecord r in t.Segments)
            {
                longest = MediaTime.Max(longest, new MediaTime(r.Info.DurationTicks, ts));
            }
        }

        TrackOut reference = tracks.FirstOrDefault(t => t.Track.Kind == TrackKind.Video) ?? tracks[0];
        long windowTicks = 0;
        foreach (SegmentRecord r in reference.Segments)
        {
            windowTicks += r.Info.DurationTicks;
        }

        MediaTime window = new(windowTicks, Math.Max(reference.Track.Timescale, 1));

        DashMpd mpd = new()
        {
            Type = live ? DashPresentationType.Dynamic : DashPresentationType.Static,
            Profiles = options.SingleFile ? "urn:mpeg:dash:profile:isoff-on-demand:2011" : "urn:mpeg:dash:profile:isoff-live:2011",
            MinBufferTime = options.MinBufferTime,
            MediaPresentationDuration = live ? null : end,
            MaxSegmentDuration = longest,
            Periods = [new DashPeriod { Id = "0", Start = MediaTime.Zero, AdaptationSets = sets }],
        };
        if (live && options.Live is { } l)
        {
            mpd = mpd with
            {
                AvailabilityStartTime = l.StartTime,
                PublishTime = At(l.StartTime, reference.EndTicks, Math.Max(reference.Track.Timescale, 1)),
                MinimumUpdatePeriod = options.SegmentDuration,
                TimeShiftBufferDepth = window,
                SuggestedPresentationDelay = new MediaTime(options.SegmentDuration.Ticks * 2, options.SegmentDuration.Timescale),
            };
        }

        return mpd;
    }
}
