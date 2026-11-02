using System.Globalization;

namespace RecursiveAI.OneMedia.Streaming;

/// <summary>Reads HLS playlists (RFC 8216). Tolerant of unknown tags, strict about the ones it models.</summary>
internal static class HlsParser
{
    public static HlsPlaylist Parse(string text, MediaLimits limits)
    {
        limits.CheckAllocation(text.Length, 0);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        // RFC 8216 §4.1: lines end with LF or CRLF; the first line is #EXTM3U.
        string[] lines = text.Split('\n');
        limits.CheckElementCount(lines.Length, 0);
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != "#EXTM3U")
        {
            throw new ManifestFormatException("A playlist starts with #EXTM3U.", 1);
        }

        bool multivariant = false;
        for (int i = 1; i < lines.Length && !multivariant; i++)
        {
            multivariant = lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal) || lines[i].StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)
                || lines[i].StartsWith("#EXT-X-I-FRAME-STREAM-INF:", StringComparison.Ordinal);
        }

        return multivariant ? ParseMultivariant(lines) : ParseMedia(lines);
    }

    private static HlsAttributeList Attributes(string line, int number)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || !HlsAttributeList.TryParse(line.AsSpan(colon + 1), out HlsAttributeList list))
        {
            throw new ManifestFormatException("Malformed attribute list.", number);
        }

        return list;
    }

    private static string Value(string line) => line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..];

    private static long ParseLong(string s, int line)
    {
        if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out long v))
        {
            throw new ManifestFormatException($"'{s}' is not a decimal integer.", line);
        }

        return v;
    }

    private static bool Yes(string? v) => v == "YES";

    private static HlsByteRange ParseRange(string s, int line, long previousEnd)
    {
        int at = s.IndexOf('@', StringComparison.Ordinal);
        long length = ParseLong(at < 0 ? s : s[..at], line);
        long offset = at < 0 ? previousEnd : ParseLong(s[(at + 1)..], line);
        return new HlsByteRange(length, offset);
    }

    private static HlsMediaPlaylist ParseMedia(string[] lines)
    {
        int? version = null;
        int target = 0;
        long sequence = 0, discontinuitySequence = 0;
        HlsPlaylistType type = HlsPlaylistType.None;
        bool iframes = false, endList = false, independent = false, precise = false;
        MediaTime? start = null;
        List<string> headTags = [], trailing = [];
        List<HlsSegment> segments = [];
        List<string> pending = [];
        HlsMap? map = null;
        MediaTime duration = default;
        bool haveDuration = false;
        string? title = null;
        HlsByteRange? range = null;
        bool discontinuity = false, gap = false;
        DateTimeOffset? pdt = null;
        long previousEnd = 0;
        for (int n = 1; n < lines.Length; n++)
        {
            string line = lines[n].TrimEnd('\r');
            int number = n + 1;
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] != '#')
            {
                if (!haveDuration)
                {
                    throw new ManifestFormatException("A segment URI needs an EXTINF tag before it.", number);
                }

                segments.Add(new HlsSegment
                {
                    Uri = line,
                    Duration = duration,
                    Title = title,
                    ByteRange = range,
                    Discontinuity = discontinuity,
                    ProgramDateTime = pdt,
                    Map = map,
                    Gap = gap,
                    OtherTags = pending.Count == 0 ? [] : pending.ToArray(),
                });
                if (range is { } used)
                {
                    previousEnd = used.End;
                }

                pending = [];
                haveDuration = false;
                title = null;
                range = null;
                discontinuity = false;
                gap = false;
                pdt = null;
                continue;
            }

            if (!line.StartsWith("#EXT", StringComparison.Ordinal))
            {
                continue; // a comment
            }

            string name = line.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0 ? line[..colon] : line;
            switch (name)
            {
                case "#EXT-X-VERSION":
                    version = (int)ParseLong(Value(line), number);
                    break;
                case "#EXT-X-TARGETDURATION":
                    target = (int)Math.Min(ParseLong(Value(line), number), int.MaxValue);
                    break;
                case "#EXT-X-MEDIA-SEQUENCE":
                    sequence = ParseLong(Value(line), number);
                    break;
                case "#EXT-X-DISCONTINUITY-SEQUENCE":
                    discontinuitySequence = ParseLong(Value(line), number);
                    break;
                case "#EXT-X-PLAYLIST-TYPE":
                    type = Value(line) switch { "VOD" => HlsPlaylistType.Vod, "EVENT" => HlsPlaylistType.Event, _ => throw new ManifestFormatException("Unknown playlist type.", number) };
                    break;
                case "#EXT-X-I-FRAMES-ONLY":
                    iframes = true;
                    break;
                case "#EXT-X-ENDLIST":
                    endList = true;
                    break;
                case "#EXT-X-INDEPENDENT-SEGMENTS":
                    independent = true;
                    break;
                case "#EXT-X-START":
                    {
                        HlsAttributeList a = Attributes(line, number);
                        if (a.Get("TIME-OFFSET") is not { } offset || !ManifestText.TryParseSeconds(offset, out MediaTime t))
                        {
                            throw new ManifestFormatException("EXT-X-START needs TIME-OFFSET.", number);
                        }

                        start = t;
                        precise = Yes(a.Get("PRECISE"));
                        break;
                    }

                case "#EXT-X-MAP":
                    {
                        HlsAttributeList a = Attributes(line, number);
                        string uri = a.Get("URI") ?? throw new ManifestFormatException("EXT-X-MAP needs URI.", number);
                        HlsByteRange? mr = a.Get("BYTERANGE") is { } br ? ParseRange(br, number, 0) : null;
                        map = new HlsMap(uri, mr);
                        break;
                    }

                case "#EXTINF":
                    {
                        string v = Value(line);
                        int comma = v.IndexOf(',', StringComparison.Ordinal);
                        string d = comma < 0 ? v : v[..comma];
                        if (!ManifestText.TryParseSeconds(d, out duration))
                        {
                            throw new ManifestFormatException($"EXTINF duration '{d}' is not a decimal number.", number);
                        }

                        title = comma >= 0 && comma + 1 < v.Length ? v[(comma + 1)..] : null;
                        haveDuration = true;
                        break;
                    }

                case "#EXT-X-BYTERANGE":
                    {
                        // RFC 8216 §4.3.2.2: a range without an offset continues the previous range of the same resource.
                        range = ParseRange(Value(line), number, previousEnd);
                        break;
                    }

                case "#EXT-X-DISCONTINUITY":
                    discontinuity = true;
                    break;
                case "#EXT-X-PROGRAM-DATE-TIME":
                    if (!ManifestText.TryParseDateTime(Value(line), out DateTimeOffset p))
                    {
                        throw new ManifestFormatException("EXT-X-PROGRAM-DATE-TIME is not an ISO 8601 date and time.", number);
                    }

                    pdt = p;
                    break;
                case "#EXT-X-GAP":
                    gap = true;
                    break;
                default:
                    if (segments.Count == 0 && pending.Count == 0 && !haveDuration)
                    {
                        headTags.Add(line);
                    }
                    else
                    {
                        pending.Add(line);
                    }

                    break;
            }
        }

        // Unmodelled tags after the last segment URI belong after it.
        trailing.AddRange(pending);
        return new HlsMediaPlaylist
        {
            Version = version ?? 1,
            TargetDuration = target,
            MediaSequence = sequence,
            DiscontinuitySequence = discontinuitySequence,
            PlaylistType = type,
            IFramesOnly = iframes,
            EndList = endList,
            IndependentSegments = independent,
            StartTimeOffset = start,
            StartPrecise = precise,
            OtherTags = headTags,
            Segments = segments,
            TrailingTags = trailing,
        };
    }

    private static HlsMultivariantPlaylist ParseMultivariant(string[] lines)
    {
        int? version = null;
        bool independent = false, precise = false;
        MediaTime? start = null;
        List<string> other = [];
        List<HlsVariant> variants = [];
        List<HlsRendition> renditions = [];
        HlsAttributeList? pendingVariant = null;
        for (int n = 1; n < lines.Length; n++)
        {
            string line = lines[n].TrimEnd('\r');
            int number = n + 1;
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] != '#')
            {
                if (pendingVariant is null)
                {
                    throw new ManifestFormatException("A URI needs an EXT-X-STREAM-INF tag before it.", number);
                }

                variants.Add(Variant(pendingVariant, line, false, number));
                pendingVariant = null;
                continue;
            }

            if (!line.StartsWith("#EXT", StringComparison.Ordinal))
            {
                continue;
            }

            string name = line.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0 ? line[..colon] : line;
            switch (name)
            {
                case "#EXT-X-VERSION":
                    version = (int)ParseLong(Value(line), number);
                    break;
                case "#EXT-X-INDEPENDENT-SEGMENTS":
                    independent = true;
                    break;
                case "#EXT-X-START":
                    {
                        HlsAttributeList a = Attributes(line, number);
                        if (a.Get("TIME-OFFSET") is not { } offset || !ManifestText.TryParseSeconds(offset, out MediaTime t))
                        {
                            throw new ManifestFormatException("EXT-X-START needs TIME-OFFSET.", number);
                        }

                        start = t;
                        precise = Yes(a.Get("PRECISE"));
                        break;
                    }

                case "#EXT-X-STREAM-INF":
                    pendingVariant = Attributes(line, number);
                    break;
                case "#EXT-X-I-FRAME-STREAM-INF":
                    {
                        HlsAttributeList a = Attributes(line, number);
                        variants.Add(Variant(a, a.Get("URI") ?? throw new ManifestFormatException("EXT-X-I-FRAME-STREAM-INF needs URI.", number), true, number));
                        break;
                    }

                case "#EXT-X-MEDIA":
                    {
                        HlsAttributeList a = Attributes(line, number);
                        renditions.Add(new HlsRendition
                        {
                            Type = a.Get("TYPE") switch
                            {
                                "AUDIO" => HlsMediaType.Audio,
                                "VIDEO" => HlsMediaType.Video,
                                "SUBTITLES" => HlsMediaType.Subtitles,
                                "CLOSED-CAPTIONS" => HlsMediaType.ClosedCaptions,
                                _ => throw new ManifestFormatException("EXT-X-MEDIA has no valid TYPE.", number),
                            },
                            GroupId = a.Get("GROUP-ID") ?? throw new ManifestFormatException("EXT-X-MEDIA needs GROUP-ID.", number),
                            Name = a.Get("NAME") ?? throw new ManifestFormatException("EXT-X-MEDIA needs NAME.", number),
                            Uri = a.Get("URI"),
                            Language = a.Get("LANGUAGE"),
                            Default = Yes(a.Get("DEFAULT")),
                            AutoSelect = Yes(a.Get("AUTOSELECT")),
                            Forced = Yes(a.Get("FORCED")),
                            Channels = a.Get("CHANNELS"),
                            Characteristics = a.Get("CHARACTERISTICS"),
                            InstreamId = a.Get("INSTREAM-ID"),
                            OtherAttributes = a.Others("TYPE", "GROUP-ID", "NAME", "URI", "LANGUAGE", "DEFAULT", "AUTOSELECT", "FORCED", "CHANNELS", "CHARACTERISTICS", "INSTREAM-ID"),
                        });
                        break;
                    }

                default:
                    other.Add(line);
                    break;
            }
        }

        if (pendingVariant is not null)
        {
            throw new ManifestFormatException("EXT-X-STREAM-INF is not followed by a URI.", lines.Length);
        }

        return new HlsMultivariantPlaylist { Version = version ?? 1, IndependentSegments = independent, StartTimeOffset = start, StartPrecise = precise, OtherTags = other, Variants = variants, Renditions = renditions };
    }

    private static HlsVariant Variant(HlsAttributeList a, string uri, bool iframe, int number)
    {
        int width = 0, height = 0;
        if (a.Get("RESOLUTION") is { } res)
        {
            int x = res.IndexOf('x', StringComparison.Ordinal);
            if (x <= 0 || !int.TryParse(res.AsSpan(0, x), NumberStyles.None, CultureInfo.InvariantCulture, out width) || !int.TryParse(res.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out height))
            {
                throw new ManifestFormatException($"RESOLUTION '{res}' is not WIDTHxHEIGHT.", number);
            }
        }

        return new HlsVariant
        {
            Uri = uri,
            IFrameOnly = iframe,
            Bandwidth = a.Get("BANDWIDTH") is { } b ? ParseLong(b, number) : 0,
            AverageBandwidth = a.Get("AVERAGE-BANDWIDTH") is { } ab ? ParseLong(ab, number) : 0,
            Codecs = a.Get("CODECS"),
            Width = width,
            Height = height,
            FrameRate = a.Get("FRAME-RATE"),
            VideoRange = a.Get("VIDEO-RANGE"),
            AudioGroup = a.Get("AUDIO"),
            VideoGroup = a.Get("VIDEO"),
            SubtitleGroup = a.Get("SUBTITLES"),
            ClosedCaptionGroup = a.Get("CLOSED-CAPTIONS"),
            OtherAttributes = a.Others("BANDWIDTH", "AVERAGE-BANDWIDTH", "CODECS", "RESOLUTION", "FRAME-RATE", "VIDEO-RANGE", "AUDIO", "VIDEO", "SUBTITLES", "CLOSED-CAPTIONS", "URI"),
        };
    }
}
