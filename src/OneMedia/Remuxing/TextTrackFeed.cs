using RecursiveAI.OneMedia.Subtitles;

namespace RecursiveAI.OneMedia.Remuxing;

/// <summary>
/// Gives the packets of a text track that was converted to another format (see <see cref="TextTrackEncoder"/>) to a remux in place of the
/// input track's own, merged into the output by time like any <see cref="IPacketFeed"/>.
/// </summary>
public sealed class TextTrackFeed : IPacketFeed
{
    private readonly TextTrackEncoding _encoding;
    private int _next;
    private int _delivered = -1;

    /// <summary>Creates a feed that stands in for an input track.</summary>
    /// <param name="inputTrack">The input track being replaced; the track and its packets carry this index.</param>
    /// <param name="inputTrackId">The container identifier of that track, kept as the converted track's <see cref="MediaTrack.Id"/>.</param>
    /// <param name="encoding">The converted track.</param>
    /// <exception cref="ArgumentNullException"><paramref name="encoding"/> is null.</exception>
    public TextTrackFeed(int inputTrack, long inputTrackId, TextTrackEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        _encoding = encoding;
        Track = encoding.Track with { Index = inputTrack, Id = inputTrackId };
    }

    /// <inheritdoc />
    public MediaTrack Track { get; }

    /// <summary>What the conversion could not carry.</summary>
    public IReadOnlyList<TextTrackLoss> Losses => _encoding.Losses;

    /// <summary>Normalisations the conversion made.</summary>
    public IReadOnlyList<string> Notes => _encoding.Notes;

    /// <inheritdoc />
    public ReadOnlySpan<byte> LastAdditional => _delivered >= 0 && _encoding.Packets[_delivered].Additional is { } a ? a : default;

    /// <inheritdoc />
    public bool TryPeek(out MediaTime decodeTime)
    {
        if (_next >= _encoding.Packets.Count)
        {
            decodeTime = MediaTime.Unknown;
            return false;
        }

        PacketInfo info = _encoding.Packets[_next].Info;
        decodeTime = info.Dts.IsKnown ? info.Dts : info.Pts;
        return true;
    }

    /// <inheritdoc />
    public bool TryRead(out PacketView packet)
    {
        if (_next >= _encoding.Packets.Count)
        {
            packet = default;
            return false;
        }

        TextTrackPacket p = _encoding.Packets[_next];
        _delivered = _next++;
        packet = new PacketView(p.Info with { TrackIndex = Track.Index }, p.Data);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>
/// Plans the text tracks of a remux: tracks the target container holds as they are pass through, the others are read to cues, written in the
/// format the target holds (see <see cref="TextTracks.TargetFor"/>) and handed to the remux as feeds. The report gets one loss entry per
/// thing the new format could not carry.
/// </summary>
internal sealed class TextTrackRemux : IDisposable
{
    private readonly List<TextTrackFeed> _feeds = [];
    private readonly List<RemuxLoss> _losses = [];
    private readonly List<string> _changes = [];

    private TextTrackRemux()
    {
    }

    public RemuxOptions? Options { get; private set; }

    public static TextTrackRemux Plan(MediaFile input, RemuxFormat target, RemuxOptions? options)
    {
        TextTrackRemux plan = new() { Options = options };
        List<(MediaTrack Track, TextTrackFormat Format, TextTrackFormat Wanted)> work = Select(input, target, options);
        foreach ((MediaTrack track, TextTrackFormat format, TextTrackFormat wanted) in work)
        {
            try
            {
                SubtitleDocument doc = TextTrackDecoder.Read(input, track, options?.Limits);
                plan.Add(track, format, wanted, doc, options);
            }
            catch (MediaReadStateException e)
            {
                plan._losses.Add(new RemuxLoss(LossKind.Track, LossReason.NotInspected, track.Index, Describe(track), $"The text track could not be converted because the source cannot be read twice: {e.Message}"));
            }
        }

        return plan;
    }

    public static async ValueTask<TextTrackRemux> PlanAsync(MediaFile input, RemuxFormat target, RemuxOptions? options, CancellationToken cancellationToken)
    {
        TextTrackRemux plan = new() { Options = options };
        List<(MediaTrack Track, TextTrackFormat Format, TextTrackFormat Wanted)> work = Select(input, target, options);
        foreach ((MediaTrack track, TextTrackFormat format, TextTrackFormat wanted) in work)
        {
            try
            {
                SubtitleDocument doc = await TextTrackDecoder.ReadAsync(input, track, options?.Limits, cancellationToken).ConfigureAwait(false);
                plan.Add(track, format, wanted, doc, options);
            }
            catch (MediaReadStateException e)
            {
                plan._losses.Add(new RemuxLoss(LossKind.Track, LossReason.NotInspected, track.Index, Describe(track), $"The text track could not be converted because the source cannot be read twice: {e.Message}"));
            }
        }

        return plan;
    }

    // The tracks the caller wants whose format the target does not hold as it is.
    private static List<(MediaTrack, TextTrackFormat, TextTrackFormat)> Select(MediaFile input, RemuxFormat target, RemuxOptions? options)
    {
        List<(MediaTrack, TextTrackFormat, TextTrackFormat)> work = [];
        foreach (MediaTrack t in input.Tracks)
        {
            TextTrackFormat format = TextTracks.FormatOf(t);
            if (format == TextTrackFormat.Unknown || Replaced(options, t.Index))
            {
                continue;
            }

            TrackEdit? edit = options?.Edits is { } e && e.TryGetValue(t.Index, out TrackEdit? found) ? found : null;
            if (edit is { Drop: true } || !(options?.Select?.Invoke(t) ?? true))
            {
                continue;
            }

            TextTrackFormat wanted = TextTracks.TargetFor(format, target);
            if (wanted != format && wanted != TextTrackFormat.Unknown)
            {
                work.Add((t, format, wanted));
            }
        }

        return work;
    }

    private static bool Replaced(RemuxOptions? options, int index)
    {
        if (options?.Replacements is { } r)
        {
            foreach (TrackReplacement x in r)
            {
                if (x.InputTrack == index)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void Add(MediaTrack track, TextTrackFormat from, TextTrackFormat to, SubtitleDocument doc, RemuxOptions? options)
    {
        TextTrackOptions encode = new()
        {
            Language = track.Language,
            Name = track.Name,
            Disposition = track.Disposition,
            Limits = options?.Limits,
        };
        TextTrackEncoding encoding = TextTrackEncoder.Encode(doc, to, encode);
        TextTrackFeed feed = new(track.Index, track.Id, encoding);
        _feeds.Add(feed);
        _changes.Add($"Input track {track.Index}: the {Name(from)} text track was converted to {Name(to)} through the cue model ({doc.Cues.Count} cues in, {encoding.Packets.Count} packets out).");
        foreach (string note in encoding.Notes)
        {
            _changes.Add($"Input track {track.Index}: {note}");
        }

        foreach (TextTrackLoss l in encoding.Losses)
        {
            string count = l.Cues > 0 ? $" ({l.Cues} cues)" : string.Empty;
            _losses.Add(new RemuxLoss(LossKind.CodecField, LossReason.NotRepresentable, track.Index, l.Feature, l.Detail + count));
        }

        List<TrackReplacement> all = options?.Replacements is { } existing ? [.. existing] : [];
        all.Add(new TrackReplacement(feed));
        Options = (options ?? new RemuxOptions()) with { Replacements = all };
    }

    public void Report(RemuxSession session)
    {
        foreach (RemuxLoss l in _losses)
        {
            session.AddLoss(l);
        }

        foreach (string c in _changes)
        {
            session.AddChange(c);
        }
    }

    public void Dispose()
    {
        foreach (TextTrackFeed f in _feeds)
        {
            f.Dispose();
        }
    }

    private static string Describe(MediaTrack t) => t.CodecTag is { Length: > 0 } tag ? $"{t.Kind} {t.Codec.Name} [{tag}]" : $"{t.Kind} {t.Codec.Name}";

    private static string Name(TextTrackFormat f) => f switch
    {
        TextTrackFormat.Tx3g => "tx3g (3GPP timed text)",
        TextTrackFormat.QuickTimeText => "QuickTime text",
        TextTrackFormat.WebVttIso => "wvtt (WebVTT in ISO)",
        TextTrackFormat.XmlSubtitleIso => "stpp (TTML in ISO)",
        TextTrackFormat.SubRip => "S_TEXT/UTF8 (SubRip)",
        TextTrackFormat.Ass => "S_TEXT/ASS",
        TextTrackFormat.WebVtt => "S_TEXT/WEBVTT",
        TextTrackFormat.WebmWebVtt => "D_WEBVTT",
        _ => f.ToString(),
    };
}
