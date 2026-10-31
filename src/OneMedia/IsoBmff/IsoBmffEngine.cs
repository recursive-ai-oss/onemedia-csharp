using System.Globalization;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>Per-track reading state shared by the demuxers.</summary>
internal sealed class TrackState
{
    public required IsoTrack Track { get; init; }

    public required MediaTrack Info { get; init; }

    public SampleCursor? Cursor { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Decode time where the next fragment's samples for this track continue when it has no <c>tfdt</c>.</summary>
    public long NextFragmentDts { get; set; }

    /// <summary>The media time of the first edit's start, in media units: packets that end at or before it are flagged as discardable.</summary>
    public long EditStartTicks { get; init; }

    /// <summary>Decode time just past the samples the movie box itself describes: where fragments continue when they carry no <c>tfdt</c> (hybrid files).</summary>
    public long FragmentStartDts { get; set; }

    /// <summary>True for PCM audio, whose table samples are single sample frames that are joined into packets (see <see cref="IsoBmffEngine.TryNextTableSample"/>).</summary>
    public bool GroupFrames { get; init; }

    public bool ReportedRange { get; set; }
}

/// <summary>
/// The packet logic shared by the synchronous, asynchronous and forward-only demuxers: tracks from the movie model, packet descriptions
/// from the sample tables or from the movie fragment in memory, and seeking. It performs no payload reads.
/// </summary>
internal sealed class IsoBmffEngine
{
    private const int FramesPerPacket = 1024;
    private const int MaxFrameBytes = 64;
    private readonly DefectLog _defects;
    private readonly MediaLimits _limits;
    private readonly long _fileLength;
    private readonly bool _fragmentedFileAllowed;

    public IsoBmffEngine(IsoMovie movie, FileTypeBox? fileType, IMediaSource tableSource, long fileLength, MediaLimits limits, DefectLog defects, MediaTime sidxDuration)
    {
        Movie = movie;
        _defects = defects;
        _limits = limits;
        _fileLength = fileLength;
        _fragmentedFileAllowed = movie.IsFragmented;
        bool qt = fileType?.IsQuickTime ?? false;
        States = new TrackState[movie.Tracks.Count];
        MediaTrack[] infos = new MediaTrack[movie.Tracks.Count];
        bool gaplessUsed = false;
        for (int i = 0; i < States.Length; i++)
        {
            IsoTrack t = movie.Tracks[i];
            MediaTrack info = BuildTrack(t, movie, qt, sidxDuration, ref gaplessUsed, out long editStart);
            infos[i] = info;
            SampleCursor? cursor = t.HasSampleTable && t.SampleCount > 0 ? new SampleCursor(t.Layout, tableSource, defects, fileLength, movie.Range.Offset) : null;
            States[i] = new TrackState { Track = t, Info = info, Cursor = cursor, EditStartTicks = editStart, GroupFrames = info.Kind == TrackKind.Audio && info.Codec == CodecId.Pcm };
            if (cursor is not null && !cursor.IsValid)
            {
                States[i].Cursor = null;
            }
        }

        Tracks = infos;
        Duration = ComputeDuration(movie, infos, sidxDuration);
        FindFragmentStarts();
        ResetFragmentTimes();
    }

    public IsoMovie Movie { get; }

    /// <summary>
    /// Sets every track's running fragment decode time to the end of the samples in the movie box (zero for a file with none).
    /// A fragment without <c>tfdt</c> continues from there (§8.8.12: the decode time of a track fragment without one follows the previous sample).
    /// </summary>
    public void ResetFragmentTimes()
    {
        foreach (TrackState s in States)
        {
            s.NextFragmentDts = s.FragmentStartDts;
        }
    }

    private void FindFragmentStarts()
    {
        if (!_fragmentedFileAllowed)
        {
            return;
        }

        foreach (TrackState s in States)
        {
            if (s.Cursor is { IsValid: true } c && c.SampleCount > 0 && c.SeekToSample(c.SampleCount - 1))
            {
                s.FragmentStartDts = c.Current.Dts + c.Current.Duration;
                c.SeekToSample(0);
            }
        }
    }

    /// <summary>The track a seek is measured on: the first enabled video track, else the first enabled track.</summary>
    public TrackState? PrimaryTrack()
    {
        TrackState? primary = null;
        foreach (TrackState s in States)
        {
            if (!s.Enabled)
            {
                continue;
            }

            if (s.Info.Kind == TrackKind.Video)
            {
                return s;
            }

            primary ??= s;
        }

        return primary;
    }

    public TrackState[] States { get; }

    public IReadOnlyList<MediaTrack> Tracks { get; }

    public MediaTime Duration { get; }

    public MovieFragment Fragment { get; } = new();

    public bool IsFragmented => _fragmentedFileAllowed;

    /// <summary>Description index (1-based) of the sample last returned, for files with several sample descriptions per track.</summary>
    public int LastDescriptionIndex { get; private set; }

    private static MediaTime ComputeDuration(IsoMovie movie, MediaTrack[] tracks, MediaTime sidx)
    {
        uint ts = movie.Header.Timescale;
        if (ts != 0 && movie.Header.Duration is > 0 and < long.MaxValue)
        {
            return new MediaTime((long)movie.Header.Duration, ts);
        }

        if (ts != 0 && movie.FragmentDuration > 0)
        {
            return new MediaTime(movie.FragmentDuration, ts);
        }

        if (sidx.IsKnown)
        {
            return sidx;
        }

        MediaTime best = MediaTime.Unknown;
        foreach (MediaTrack t in tracks)
        {
            if (t.Duration.IsKnown && (!best.IsKnown || t.Duration > best))
            {
                best = t.Duration;
            }
        }

        return best;
    }

    private static MediaTrack BuildTrack(IsoTrack t, IsoMovie movie, bool quickTime, MediaTime sidxDuration, ref bool gaplessUsed, out long editStart)
    {
        TrackKind kind = t.HandlerType switch
        {
            "vide" => TrackKind.Video,
            "soun" => TrackKind.Audio,
            "text" or "sbtl" or "subt" or "clcp" or "subp" => TrackKind.Subtitle,
            "meta" or "tmcd" or "hint" or "data" or "gpmd" => TrackKind.Data,
            _ => TrackKind.Unknown,
        };
        SampleEntry? entry = t.SampleEntries.Count > 0 ? t.SampleEntries[0] : null;
        long timescale = t.Media.Timescale == 0 ? 1 : t.Media.Timescale;
        CodecId codec = entry is null ? CodecId.Unknown : IsoCodecMap.Map(entry.OriginalFormat, entry.Configuration);
        if (kind == TrackKind.Unknown && entry is not null)
        {
            kind = codec.IsUnknown ? TrackKind.Unknown : TrackKind.Data;
        }

        MediaTime duration = t.Media.Duration is 0 or ulong.MaxValue || t.Media.Duration > long.MaxValue ? MediaTime.Unknown : new MediaTime((long)t.Media.Duration, timescale);
        if (!duration.IsKnown && t.Media.Duration == 0 && !movie.IsFragmented)
        {
            duration = MediaTime.Zero;
        }

        TrackEditInfo edit = BuildEditInfo(t, movie, timescale, kind, codec, entry, duration, ref gaplessUsed, out editStart);
        AudioTrackInfo? audio = null;
        VideoTrackInfo? video = null;
        if (kind == TrackKind.Audio && entry is not null)
        {
            int rate = entry.SampleRate > 0 ? entry.SampleRate : (int)Math.Min(timescale, int.MaxValue);
            audio = new AudioTrackInfo(rate, entry.ChannelCount, entry.SampleSizeBits);
        }
        else if (kind == TrackKind.Video)
        {
            int w = entry?.Width > 0 ? entry.Width : (int)(t.Header.Width >> 16);
            int h = entry?.Height > 0 ? entry.Height : (int)(t.Header.Height >> 16);
            int pn = 1, pd = 1;
            ExtensionBox? pasp = entry?.FindExtension(BoxTypes.Pasp);
            if (pasp is not null && pasp.Payload.Length >= 8)
            {
                uint hs = Be.U32(pasp.Payload.Span, 0), vs = Be.U32(pasp.Payload.Span, 4);
                if (hs is > 0 and <= int.MaxValue && vs is > 0 and <= int.MaxValue)
                {
                    pn = (int)hs;
                    pd = (int)vs;
                }
            }

            (int fn, int fd) = FrameRate(t, timescale, duration);
            video = new VideoTrackInfo(w, h, t.Header.RotationDegrees, pn, pd, fn, fd);
        }

        // Default: the first enabled track of its kind (within its alternate group when it has one); disabled tracks are marked as such.
        TrackDisposition disposition = TrackDisposition.None;
        if (!t.Header.IsEnabled)
        {
            disposition |= TrackDisposition.Disabled;
        }
        else if (IsFirstEnabledOfKind(t, movie, kind))
        {
            disposition |= TrackDisposition.Default;
        }

        return new MediaTrack
        {
            Index = t.Index,
            Id = t.Header.TrackId,
            Kind = kind,
            Codec = codec,
            CodecTag = entry?.OriginalFormat,
            Configuration = entry?.Configuration ?? default,
            Timescale = timescale,
            Duration = duration,
            Edit = edit,
            Language = t.Media.Language,
            Name = string.IsNullOrEmpty(t.HandlerName) ? null : t.HandlerName,
            Disposition = disposition,
            Audio = audio,
            Video = video,
            PacketCount = movie.IsFragmented || (kind == TrackKind.Audio && codec == CodecId.Pcm) ? -1 : t.SampleCount,
            IsProtected = entry?.IsProtected ?? false,
        };
    }

    // §8.3.1: tracks of one alternate group are alternatives of each other (only one plays); QuickTime players pick the first enabled.
    private static bool IsFirstEnabledOfKind(IsoTrack t, IsoMovie movie, TrackKind kind)
    {
        int group = t.Header.AlternateGroup;
        foreach (IsoTrack other in movie.Tracks)
        {
            if (other.Index >= t.Index)
            {
                break;
            }

            if (other.Header.IsEnabled && KindOf(other.HandlerType) == kind && (group == 0 || other.Header.AlternateGroup == group))
            {
                return false;
            }
        }

        return true;
    }

    private static TrackKind KindOf(string handler) => handler switch
    {
        "vide" => TrackKind.Video,
        "soun" => TrackKind.Audio,
        "text" or "sbtl" or "subt" or "clcp" or "subp" => TrackKind.Subtitle,
        _ => TrackKind.Unknown,
    };

    private static (int Numerator, int Denominator) FrameRate(IsoTrack t, long timescale, MediaTime duration)
    {
        long samples = t.SampleCount;
        if (samples <= 1 || !duration.IsKnown || duration.Ticks <= 0)
        {
            return (0, 0);
        }

        // Average rate: samples per media duration, reduced.
        System.Numerics.BigInteger num = (System.Numerics.BigInteger)samples * timescale;
        System.Numerics.BigInteger den = duration.Ticks;
        System.Numerics.BigInteger g = System.Numerics.BigInteger.GreatestCommonDivisor(num, den);
        num /= g;
        den /= g;
        return num > int.MaxValue || den > int.MaxValue ? (0, 0) : ((int)num, (int)den);
    }

    private static TrackEditInfo BuildEditInfo(IsoTrack t, IsoMovie movie, long timescale, TrackKind kind, CodecId codec, SampleEntry? entry, MediaTime mediaDuration,
        ref bool gaplessUsed, out long editStartTicks)
    {
        editStartTicks = 0;
        long movieScale = movie.Header.Timescale == 0 ? 1 : movie.Header.Timescale;
        List<EditSegment> segments = [];
        long startTicks = 0; // leading empty edits, in movie timescale units
        MediaTime priming = MediaTime.Zero;
        MediaTime endPadding = MediaTime.Zero;
        bool leading = true;
        bool havePriming = false;
        int nonEmpty = 0;
        EditListEntry lastNonEmpty = default;
        foreach (EditListEntry e in t.Edits)
        {
            long dur = e.SegmentDuration > long.MaxValue ? long.MaxValue : (long)e.SegmentDuration;
            segments.Add(new EditSegment(e.IsEmpty ? MediaTime.Unknown : new MediaTime(e.MediaTime, timescale), new MediaTime(dur, movieScale)));
            if (e.IsEmpty)
            {
                if (leading)
                {
                    startTicks = long.MaxValue - startTicks < dur ? long.MaxValue : startTicks + dur;
                }

                continue;
            }

            leading = false;
            nonEmpty++;
            lastNonEmpty = e;
            if (!havePriming)
            {
                havePriming = true;
                priming = new MediaTime(Math.Max(0, e.MediaTime), timescale);
                editStartTicks = Math.Max(0, e.MediaTime);
            }
        }

        if (nonEmpty == 1 && lastNonEmpty.SegmentDuration > 0 && mediaDuration.IsKnown && mediaDuration.Ticks > 0)
        {
            // The edit covers [media_time, media_time + segment) of the media; what lies past it is padding to drop.
            long segment = Rescaled(new MediaTime((long)Math.Min(lastNonEmpty.SegmentDuration, long.MaxValue / 2), movieScale), timescale);
            long padTicks = mediaDuration.Ticks - lastNonEmpty.MediaTime - segment;
            if (padTicks > 0 && segment >= 0)
            {
                endPadding = new MediaTime(padTicks, timescale);
            }
        }

        // The iTunes gapless tag describes the first audio track's encoder delay and padding exactly; it wins over the edit list.
        if (kind == TrackKind.Audio && !gaplessUsed && movie.GaplessInfo is { } smpb && entry is not null && TryParseGapless(smpb, out long delay, out long padding, out long frames))
        {
            int rate = entry.SampleRate > 0 ? entry.SampleRate : (int)Math.Min(timescale, int.MaxValue);
            if (rate > 0 && (delay + padding + frames) > 0)
            {
                gaplessUsed = true;
                priming = new MediaTime(Rescaled(new MediaTime(delay, rate), timescale), timescale);
                endPadding = new MediaTime(Rescaled(new MediaTime(padding, rate), timescale), timescale);
            }
        }

        _ = codec;
        return new TrackEditInfo(priming, endPadding, new MediaTime(startTicks, movieScale), segments);
    }

    // iTunSMPB: " 00000000 <delay> <padding> <valid frames, 64-bit> ..." with every field in hexadecimal (observed in files written by iTunes and ffmpeg).
    internal static bool TryParseGapless(string text, out long delay, out long padding, out long frames)
    {
        delay = padding = frames = 0;
        string[] f = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return f.Length >= 4
            && long.TryParse(f[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out delay)
            && long.TryParse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out padding)
            && long.TryParse(f[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out frames)
            && delay >= 0 && padding >= 0 && frames >= 0;
    }

    // Rescaling that saturates instead of throwing, for values read from files.
    private static long Rescaled(MediaTime value, long timescale) =>
        value.TryRescale(timescale, MediaRounding.NearestAwayFromZero, out MediaTime r) ? r.Ticks : long.MaxValue / 4;

    public void SetTrackEnabled(int index, bool enabled)
    {
        if ((uint)index >= (uint)States.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        States[index].Enabled = enabled;
    }

    /// <summary>Takes the next sample (lowest file offset) from the sample tables of the enabled tracks.</summary>
    public bool TryNextTableSample(out PacketInfo info)
    {
        while (true)
        {
            TrackState? best = null;
            long bestOffset = long.MaxValue;
            foreach (TrackState s in States)
            {
                if (s.Enabled && s.Cursor is { IsValid: true } c && c.Current.Offset < bestOffset)
                {
                    best = s;
                    bestOffset = c.Current.Offset;
                }
            }

            if (best is null)
            {
                info = default;
                return false;
            }

            SampleCursor cursor = best.Cursor!;
            TableSample sample = cursor.Current;
            cursor.MoveNext();
            long duration = sample.Duration;
            int size = sample.Size;
            if (best.GroupFrames && size is > 0 and <= MaxFrameBytes)
            {
                // PCM is stored one sample frame per table sample (QuickTime sound sample descriptions); a packet of one frame is useless, so the frames
                // that follow each other in the file are joined, up to 1024 frames per packet, the size most players and tools use.
                int frames = 1;
                while (frames < FramesPerPacket && cursor.IsValid && cursor.Current.Offset == sample.Offset + size && cursor.Current.DescriptionIndex == sample.DescriptionIndex
                    && cursor.Current.Size is > 0 and <= MaxFrameBytes)
                {
                    size += cursor.Current.Size;
                    duration += cursor.Current.Duration;
                    frames++;
                    cursor.MoveNext();
                }
            }

            LastDescriptionIndex = sample.DescriptionIndex;
            if (Describe(best, sample.Offset, size, sample.Dts, sample.CompositionOffset, duration, sample.IsSync, sample.IsDisposable, out info))
            {
                return true;
            }
        }
    }

    /// <summary>Takes the next sample from the movie fragment in memory.</summary>
    public bool TryNextFragmentSample(out PacketInfo info)
    {
        while (Fragment.Peek(States, out FragmentSample f, out int traf))
        {
            Fragment.Advance(traf, f, States);
            LastDescriptionIndex = f.DescriptionIndex;
            // §8.8.3.1 / §8.8.8: bit 16 of the sample flags is sample_is_non_sync_sample; is_depended_on is bits 23-22.
            bool sync = (f.Flags & 0x10000) == 0;
            bool disposable = ((f.Flags >> 22) & 3) == 2;
            if (Describe(States[f.TrackIndex], f.Offset, f.Size, f.Dts, f.CompositionOffset, f.Duration, sync, disposable, out info))
            {
                return true;
            }
        }

        Fragment.DrainDisabled(States);
        info = default;
        return false;
    }

    private bool Describe(TrackState s, long offset, int size, long dts, long cto, long duration, bool sync, bool disposable, out PacketInfo info)
    {
        info = default;
        if (size < 0 || offset < 0 || size > _limits.MaxPacketBytes)
        {
            Report(s, offset, "A sample is larger than the packet size limit.", DefectKind.LimitReached);
            return false;
        }

        if (offset + size > _fileLength)
        {
            // The data this sample points at was cut off; later samples of the track are usually cut off too, so say so once.
            Report(s, offset, "A sample lies beyond the end of the file.", DefectKind.Truncated);
            return false;
        }

        long timescale = s.Info.Timescale;
        long pts = dts + cto;
        PacketAttributes flags = PacketAttributes.None;
        if (sync)
        {
            flags |= PacketAttributes.Keyframe;
        }

        if (disposable)
        {
            flags |= PacketAttributes.Disposable;
        }

        if (s.EditStartTicks > 0 && pts + duration <= s.EditStartTicks)
        {
            // The first edit starts later in the media than this sample ends (encoder priming): it is decoded but not presented.
            flags |= PacketAttributes.Discard;
        }

        info = new PacketInfo(s.Info.Index, new MediaTime(pts, timescale), new MediaTime(dts, timescale), new MediaTime(duration, timescale), flags, new ByteRange(offset, size), size);
        return true;
    }

    private void Report(TrackState s, long offset, string message, DefectKind kind)
    {
        if (!s.ReportedRange)
        {
            s.ReportedRange = true;
            _defects.Add(kind, DefectSeverity.Error, offset, $"Track {s.Info.Index}: {message}", kind == DefectKind.Truncated ? "isobmff.sample-past-eof" : "isobmff.sample-limit");
        }
    }

    /// <summary>Loads a movie fragment from the bytes in <see cref="Fragment"/> and starts iterating it.</summary>
    public void BeginFragment(long moofOffset)
    {
        Fragment.Parse(moofOffset, Movie.Tracks, States, _defects, _limits);
        Fragment.Rewind(States);
    }

    /// <summary>
    /// Positions the table-based tracks for a seek. The primary track (the first enabled video track, else the first enabled track) lands on a
    /// sync sample at or before (or after) the target; the others land on their last sync sample not after the primary's landing time.
    /// </summary>
    /// <returns>The presentation time the primary track lands on, or <see cref="MediaTime.Unknown"/> when no track has a table.</returns>
    public MediaTime SeekTables(MediaTime target, SeekMode mode)
    {
        TrackState? primary = null;
        foreach (TrackState s in States)
        {
            if (!s.Enabled || s.Cursor is null)
            {
                continue;
            }

            if (s.Info.Kind == TrackKind.Video)
            {
                primary = s;
                break;
            }

            primary ??= s;
        }

        if (primary is null)
        {
            return MediaTime.Unknown;
        }

        SampleCursor pc = primary.Cursor!;
        long ts = primary.Info.Timescale;
        long targetTicks = target.TryRescale(ts, MediaRounding.Floor, out MediaTime scaled) ? scaled.Ticks : (target.Ticks < 0 ? 0 : long.MaxValue / 4);
        long found = pc.FindSampleByDecodeTime(targetTicks);
        long sync = mode == SeekMode.PreviousKeyframe ? pc.SyncAtOrBefore(found) : pc.SyncAtOrAfter(found);
        if (mode == SeekMode.PreviousKeyframe)
        {
            // A sync sample's presentation time can lie after its decode time; step back to one that does not pass the target.
            while (sync > 0 && pc.SeekToSample(sync) && pc.Current.Dts + pc.Current.CompositionOffset > targetTicks)
            {
                long prev = pc.SyncAtOrBefore(sync - 1);
                if (prev >= sync)
                {
                    break;
                }

                sync = prev;
            }
        }
        else if (sync >= 0 && pc.SeekToSample(sync) && pc.Current.Dts + pc.Current.CompositionOffset < targetTicks)
        {
            long next = pc.SyncAtOrAfter(sync + 1);
            sync = next;
        }

        if (sync < 0 || !pc.SeekToSample(sync))
        {
            // Nothing at or after the target: leave every table cursor at its end.
            foreach (TrackState s in States)
            {
                s.Cursor?.SeekToSample(long.MaxValue);
            }

            return MediaTime.Unknown;
        }

        MediaTime landing = new(pc.Current.Dts + pc.Current.CompositionOffset, ts);
        MediaTime landingDts = new(pc.Current.Dts, ts);
        foreach (TrackState s in States)
        {
            if (s == primary || s.Cursor is null || !s.Enabled)
            {
                continue;
            }

            long t = landingDts.TryRescale(s.Info.Timescale, MediaRounding.Floor, out MediaTime other) ? other.Ticks : long.MaxValue / 4;
            long sample = s.Cursor.FindSampleByDecodeTime(t);
            s.Cursor.SeekToSample(s.Cursor.SyncAtOrBefore(sample));
        }

        return landing;
    }
}
