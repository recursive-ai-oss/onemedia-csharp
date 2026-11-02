namespace RecursiveAI.OneMedia.Streaming.Sources;

/// <summary>
/// Turns an HLS playlist, read by the shared manifest model (<see cref="HlsPlaylist"/>), into the plan a presentation plays: variants, the renditions
/// of each, and the segments with their resolved locations, byte ranges, initialization maps, keys and discontinuities.
/// </summary>
internal static class HlsReader
{
    /// <summary>True when the text starts with the <c>#EXTM3U</c> tag (RFC 8216 §4.3.1.1), after an optional byte order mark and blank space.</summary>
    public static bool IsPlaylist(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("\uFEFF"u8))
        {
            head = head[3..];
        }

        while (!head.IsEmpty && head[0] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            head = head[1..];
        }

        return head.StartsWith("#EXTM3U"u8);
    }

    /// <summary>True when the playlist is a multivariant playlist (it lists <c>EXT-X-STREAM-INF</c> entries).</summary>
    public static bool IsMultivariant(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);

    /// <summary>Reads a playlist of either kind into a plan. A media playlist is filled in; a multivariant playlist leaves its renditions' segments empty, to be loaded from <see cref="PlannedRendition.PlaylistLocation"/>.</summary>
    /// <exception cref="ManifestFormatException">The playlist is malformed.</exception>
    public static PresentationPlan Read(string text, Uri location, MediaLimits limits, DefectLog defects)
    {
        PresentationPlan plan = new();
        HlsPlaylist playlist = Parse(text, limits, defects);
        if (playlist is HlsMediaPlaylist media)
        {
            PlannedRendition r = ToRendition(media, location, limits, defects);
            plan.Variants.Add(new PresentationVariant(0, 0, null, 0, 0, null));
            plan.VariantRenditions.Add([r]);
            plan.Duration = r.Ended ? SumDurations(r) : MediaTime.Unknown;
            return plan;
        }

        HlsMultivariantPlaylist master = (HlsMultivariantPlaylist)playlist;
        plan.IsMaster = true;
        foreach (HlsVariant v in master.Variants)
        {
            if (v.IFrameOnly || !TryResolve(location, v.Uri, out Uri? uri))
            {
                continue;
            }

            long bandwidth = v.AverageBandwidth > 0 ? v.AverageBandwidth : v.Bandwidth;
            plan.Variants.Add(new PresentationVariant(plan.Variants.Count, bandwidth, v.Codecs, v.Width, v.Height, null));
            List<PlannedRendition> parts = [new PlannedRendition { PlaylistLocation = uri, Bandwidth = bandwidth, Codecs = v.Codecs, Width = v.Width, Height = v.Height }];

            // A separate audio rendition: the variant's AUDIO group entry that has its own playlist, the default one first. A group entry
            // without a URI means the audio is inside the variant's own segments.
            HlsRendition? chosen = null;
            foreach (HlsRendition m in master.Renditions)
            {
                if (m.Type == HlsMediaType.Audio && v.AudioGroup is not null && m.GroupId == v.AudioGroup && m.Uri is not null && (chosen is null || (m.Default && !chosen.Default)))
                {
                    chosen = m;
                }
            }

            if (chosen is not null && TryResolve(location, chosen.Uri!, out Uri? au))
            {
                parts.Add(new PlannedRendition { PlaylistLocation = au, KindHint = TrackKind.Audio, Language = chosen.Language, Name = chosen.Name, IsDefault = chosen.Default });
            }

            plan.VariantRenditions.Add(parts);
        }

        return plan;
    }

    /// <summary>Reads a media playlist (RFC 8216 §4.3.2, §4.3.3).</summary>
    /// <exception cref="ManifestFormatException">The playlist is malformed or is a multivariant playlist.</exception>
    public static PlannedRendition ReadMedia(string text, Uri location, MediaLimits limits, DefectLog defects) =>
        Parse(text, limits, defects) is HlsMediaPlaylist m ? ToRendition(m, location, limits, defects) : throw new ManifestFormatException("A variant's URI names a multivariant playlist, not a media playlist.");

    private static PlannedRendition ToRendition(HlsMediaPlaylist playlist, Uri location, MediaLimits limits, DefectLog defects)
    {
        PlannedRendition r = new() { PlaylistLocation = location, Ended = playlist.EndList || playlist.PlaylistType == HlsPlaylistType.Vod, FirstSequence = playlist.MediaSequence };
        if (playlist.IFramesOnly)
        {
            defects.Add(DefectKind.Unsupported, DefectSeverity.Warning, 0, "An I-frames-only playlist lists byte ranges of single pictures; it is read as an ordinary playlist.", "hls.iframes-only");
        }

        bool encrypted;
        long sequence = playlist.MediaSequence;
        foreach (HlsSegment s in playlist.Segments)
        {
            // RFC 8216 §4.3.2.4: the model lists the keys in effect for each segment; none means not encrypted.
            encrypted = false;
            foreach (HlsKey k in s.Keys)
            {
                encrypted |= k.Method != HlsKeyMethod.None;
            }

            r.Protected |= encrypted;
            if (!s.Gap)
            {
                limits.CheckSegments(r.Segments.Count + 1, -1);
                if (!TryResolve(location, s.Uri, out Uri? uri))
                {
                    defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, 0, "A segment URI cannot be resolved.", "hls.bad-uri");
                }
                else
                {
                    PlannedMap? map = null;
                    if (s.Map is { } m && TryResolve(location, m.Uri, out Uri? mu))
                    {
                        map = new PlannedMap(mu!, m.ByteRange?.Offset ?? 0, m.ByteRange?.Length ?? -1);
                    }

                    r.Segments.Add(new PlannedSegment
                    {
                        Location = uri!,
                        Offset = s.ByteRange?.Offset ?? 0,
                        Length = s.ByteRange?.Length ?? -1,
                        Duration = s.Duration,
                        Discontinuity = s.Discontinuity,
                        Init = map,
                        Encrypted = encrypted,
                        Sequence = sequence,
                    });
                }
            }

            sequence++;
        }

        return r;
    }

    /// <summary>Reads a playlist leniently, as servers write them, and records what the standard forbids as defects in the log.</summary>
    private static HlsPlaylist Parse(string text, MediaLimits limits, DefectLog defects)
    {
        HlsParseResult result = HlsPlaylist.ParseLenient(text, limits);
        foreach (ManifestDefect d in result.Defects)
        {
            defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, d.Offset, $"Playlist line {d.Line}: {d.Message}", "hls." + d.Kind switch
            {
                ManifestDefectKind.MissingPlaylistHeader => "missing-header",
                ManifestDefectKind.SegmentWithoutDuration => "segment-without-duration",
                ManifestDefectKind.UriWithoutStreamInf => "uri-without-stream-inf",
                _ => "malformed-tag",
            });
        }

        return result.Playlist;
    }

    /// <summary>
    /// The total of the segment durations, or unknown when one of them states none. EXTINF is a decimal number (RFC 8216 §4.3.2.1) and is read
    /// exactly, so the total keeps every digit: five segments of 10.4 s are 52 s and not the 50 s that rounding each one gives.
    /// </summary>
    public static MediaTime SumDurations(PlannedRendition r)
    {
        MediaTime total = MediaTime.Zero;
        foreach (PlannedSegment s in r.Segments)
        {
            if (!s.Duration.IsKnown)
            {
                return MediaTime.Unknown;
            }

            total = ManifestText.ExactSum(total, s.Duration);
        }

        return total;
    }

    private static bool TryResolve(Uri baseUri, string reference, out Uri? result)
    {
        result = null;
        try
        {
            return Uri.TryCreate(baseUri, reference, out result) && result is not null;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
