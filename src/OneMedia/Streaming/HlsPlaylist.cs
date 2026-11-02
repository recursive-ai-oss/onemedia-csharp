using System.Globalization;
using System.Linq;
using System.Text;

namespace RecursiveAI.OneMedia.Streaming;

/// <summary>One <c>NAME=value</c> pair of an HLS attribute list (RFC 8216 §4.2).</summary>
/// <param name="Name">The attribute name, upper case letters, digits and hyphen.</param>
/// <param name="Value">The value without its quotes.</param>
/// <param name="Quoted">True when the value was a quoted string.</param>
public readonly record struct HlsAttributePair(string Name, string Value, bool Quoted)
{
    /// <summary>The attribute as written in a tag.</summary>
    public override string ToString() => Quoted ? $"{Name}=\"{Value}\"" : $"{Name}={Value}";
}

/// <summary>A parsed HLS attribute list (RFC 8216 §4.2). Order is kept; names are matched exactly.</summary>
public sealed class HlsAttributeList
{
    private readonly List<HlsAttributePair> _items;

    internal HlsAttributeList(List<HlsAttributePair> items) => _items = items;

    /// <summary>Every attribute in the order it was written.</summary>
    public IReadOnlyList<HlsAttributePair> Items => _items;

    /// <summary>Parses the text after the colon of a tag.</summary>
    /// <param name="text">For example <c>BANDWIDTH=800000,CODECS="avc1.64001f,mp4a.40.2"</c>.</param>
    /// <param name="attributes">The attributes.</param>
    /// <returns>False when the list is malformed (a name without a value, an unterminated quoted string).</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out HlsAttributeList attributes)
    {
        List<HlsAttributePair> items = [];
        attributes = new HlsAttributeList(items);
        int i = 0;
        while (i < text.Length)
        {
            int eq = text[i..].IndexOf('=');
            if (eq <= 0)
            {
                return false;
            }

            string name = text.Slice(i, eq).Trim().ToString();
            i += eq + 1;
            if (i < text.Length && text[i] == '"')
            {
                int end = text[(i + 1)..].IndexOf('"');
                if (end < 0)
                {
                    return false;
                }

                items.Add(new HlsAttributePair(name, text.Slice(i + 1, end).ToString(), true));
                i += end + 2;
            }
            else
            {
                int comma = text[i..].IndexOf(',');
                int stop = comma < 0 ? text.Length : i + comma;
                items.Add(new HlsAttributePair(name, text[i..stop].Trim().ToString(), false));
                i = stop;
            }

            if (i < text.Length)
            {
                if (text[i] != ',')
                {
                    return false;
                }

                i++;
            }
        }

        return true;
    }

    /// <summary>The value of an attribute, or null when absent.</summary>
    /// <param name="name">The attribute name.</param>
    public string? Get(string name)
    {
        foreach (HlsAttributePair a in _items)
        {
            if (a.Name == name)
            {
                return a.Value;
            }
        }

        return null;
    }

    /// <summary>The attributes whose names are not in <paramref name="known"/>, as <c>NAME=value</c> text, in order.</summary>
    /// <param name="known">The names the caller models.</param>
    internal List<string> Others(params string[] known)
    {
        List<string> other = [];
        foreach (HlsAttributePair a in _items)
        {
            if (Array.IndexOf(known, a.Name) < 0)
            {
                other.Add(a.ToString());
            }
        }

        return other;
    }
}

/// <summary>A byte range of a resource (RFC 8216 §4.3.2.2): <see cref="Length"/> bytes starting at <see cref="Offset"/>.</summary>
/// <param name="Length">Number of bytes.</param>
/// <param name="Offset">Offset of the first byte. A playlist that gives none continues after the previous range of the same resource; the parser resolves it.</param>
public readonly record struct HlsByteRange(long Length, long Offset)
{
    /// <summary>The offset one past the last byte.</summary>
    public long End => Offset + Length;
}

/// <summary>How the media is encrypted (<c>METHOD</c> of <c>EXT-X-KEY</c>, RFC 8216 §4.3.2.4).</summary>
public enum HlsKeyMethod
{
    /// <summary>Not encrypted (<c>NONE</c>).</summary>
    None = 0,

    /// <summary>Whole-segment AES-128 CBC (<c>AES-128</c>).</summary>
    Aes128,

    /// <summary>Sample-level encryption (<c>SAMPLE-AES</c>).</summary>
    SampleAes,

    /// <summary>Sample-level AES-CTR (<c>SAMPLE-AES-CTR</c>).</summary>
    SampleAesCtr,

    /// <summary>Another method name; see <see cref="HlsKey.MethodName"/>.</summary>
    Other,
}

/// <summary>An <c>EXT-X-KEY</c>: how to obtain the key that decrypts the segments after it. The library reads and writes it; decrypting is out of scope.</summary>
public sealed record HlsKey
{
    /// <summary>The method.</summary>
    public HlsKeyMethod Method { get; init; }

    /// <summary>The <c>METHOD</c> as written.</summary>
    public string MethodName { get; init; } = "NONE";

    /// <summary>The key's <c>URI</c>, or null (absent for <c>NONE</c>).</summary>
    public string? Uri { get; init; }

    /// <summary>The <c>IV</c> as written (<c>0x</c> and 32 hex digits), or null: the IV is then the segment's media sequence number (RFC 8216 §5.2).</summary>
    public string? Iv { get; init; }

    /// <summary>The <c>KEYFORMAT</c>, or null for <c>identity</c>.</summary>
    public string? KeyFormat { get; init; }

    /// <summary>The <c>KEYFORMATVERSIONS</c>, or null for version 1.</summary>
    public string? KeyFormatVersions { get; init; }

    /// <summary>Attributes the model does not name, as <c>NAME=value</c> text.</summary>
    public IReadOnlyList<string> OtherAttributes { get; init; } = [];

    /// <summary>The tag line as written to a playlist.</summary>
    public override string ToString()
    {
        StringBuilder sb = new("#EXT-X-KEY:METHOD=");
        sb.Append(Method == HlsKeyMethod.Other ? MethodName : Method switch { HlsKeyMethod.None => "NONE", HlsKeyMethod.Aes128 => "AES-128", HlsKeyMethod.SampleAes => "SAMPLE-AES", _ => "SAMPLE-AES-CTR" });
        if (Uri is not null)
        {
            sb.Append(",URI=\"").Append(Uri).Append('"');
        }

        if (Iv is not null)
        {
            sb.Append(",IV=").Append(Iv);
        }

        if (KeyFormat is not null)
        {
            sb.Append(",KEYFORMAT=\"").Append(KeyFormat).Append('"');
        }

        if (KeyFormatVersions is not null)
        {
            sb.Append(",KEYFORMATVERSIONS=\"").Append(KeyFormatVersions).Append('"');
        }

        foreach (string o in OtherAttributes)
        {
            sb.Append(',').Append(o);
        }

        return sb.ToString();
    }
}

/// <summary>An <c>EXT-X-MAP</c>: the initialisation section that media segments after it need (RFC 8216 §4.3.2.5).</summary>
/// <param name="Uri">Where the initialisation section is.</param>
/// <param name="ByteRange">The part of the resource, or null for the whole of it.</param>
public sealed record HlsMap(string Uri, HlsByteRange? ByteRange = null);

/// <summary>One media segment of a media playlist with the tags that apply to it.</summary>
public sealed record HlsSegment
{
    /// <summary>The segment's URI as written, absolute or relative to the playlist.</summary>
    public required string Uri { get; init; }

    /// <summary>The <c>EXTINF</c> duration, exact (a decimal number of seconds is read as a fraction with a power-of-ten timescale).</summary>
    public MediaTime Duration { get; init; }

    /// <summary>The optional title after the comma of <c>EXTINF</c>.</summary>
    public string? Title { get; init; }

    /// <summary>The <c>EXT-X-BYTERANGE</c> of the segment, with its offset resolved; null when the segment is the whole resource.</summary>
    public HlsByteRange? ByteRange { get; init; }

    /// <summary>True when an <c>EXT-X-DISCONTINUITY</c> precedes the segment (RFC 8216 §4.3.4.3).</summary>
    public bool Discontinuity { get; init; }

    /// <summary>The <c>EXT-X-PROGRAM-DATE-TIME</c> given for the segment, or null.</summary>
    public DateTimeOffset? ProgramDateTime { get; init; }

    /// <summary>The initialisation section in effect for the segment (the last <c>EXT-X-MAP</c> before it), or null.</summary>
    public HlsMap? Map { get; init; }

    /// <summary>True when the segment is marked <c>EXT-X-GAP</c>: it is not available and clients should not request it.</summary>
    public bool Gap { get; init; }

    /// <summary>The keys in effect for the segment (RFC 8216 §4.3.2.4): the last <c>EXT-X-KEY</c> of each <see cref="HlsKey.KeyFormat"/> before it. Empty when the segment is not encrypted. Several keys apply when a playlist carries one per key system.</summary>
    public IReadOnlyList<HlsKey> Keys { get; init; } = [];

    /// <summary>True when <c>EXT-X-KEY</c> tags stand directly before this segment, so that <see cref="Keys"/> is written here (the first segment of a playlist that is encrypted from its start has this set). An empty <see cref="Keys"/> with this set is <c>METHOD=NONE</c>.</summary>
    public bool KeysChanged { get; init; }

    /// <summary>Tags that precede the segment and are not modelled (<c>EXT-X-DATERANGE</c>, <c>EXT-X-BITRATE</c>, vendor tags), whole lines, kept so a rewrite loses nothing.</summary>
    public IReadOnlyList<string> OtherTags { get; init; } = [];
}

/// <summary>The kind of a media playlist (<c>EXT-X-PLAYLIST-TYPE</c>, RFC 8216 §4.3.3.5).</summary>
public enum HlsPlaylistType
{
    /// <summary>No type: a live playlist that may drop segments from its front.</summary>
    None = 0,

    /// <summary>Segments are only ever added.</summary>
    Event,

    /// <summary>The playlist never changes.</summary>
    Vod,
}

/// <summary>An HLS playlist: a media playlist or a multivariant ("master") playlist. Read with <see cref="Parse(string, MediaLimits?)"/>, written with <see cref="ToString"/>.</summary>
public abstract class HlsPlaylist
{
    /// <summary>The <c>EXT-X-VERSION</c> to write; null writes the lowest version the content needs (RFC 8216 §7). A parsed playlist carries the version it declared (1 when none).</summary>
    public int? Version { get; init; }

    /// <summary>True for <c>EXT-X-INDEPENDENT-SEGMENTS</c>.</summary>
    public bool IndependentSegments { get; init; }

    /// <summary>The <c>EXT-X-START</c> time offset in seconds, or null.</summary>
    public MediaTime? StartTimeOffset { get; init; }

    /// <summary>True for <c>PRECISE=YES</c> on <c>EXT-X-START</c>.</summary>
    public bool StartPrecise { get; init; }

    /// <summary>Whole-line tags before the first segment or variant that are not modelled, kept in order.</summary>
    public IReadOnlyList<string> OtherTags { get; init; } = [];

    /// <summary>The version the content needs, per RFC 8216 §7.</summary>
    public abstract int MinimumVersion { get; }

    /// <summary>The version that will be written: <see cref="Version"/> when set (never below <see cref="MinimumVersion"/>), else the minimum.</summary>
    public int EffectiveVersion => Math.Max(Version ?? 1, MinimumVersion);

    /// <summary>Writes the playlist as UTF-8 text with LF line ends, as RFC 8216 §4.1 allows.</summary>
    /// <param name="writer">The destination.</param>
    public abstract void WriteTo(TextWriter writer);

    /// <summary>The playlist as text.</summary>
    public override string ToString()
    {
        StringWriter w = new(CultureInfo.InvariantCulture) { NewLine = "\n" };
        WriteTo(w);
        return w.ToString();
    }

    /// <summary>The playlist as UTF-8 bytes without a byte order mark.</summary>
    public byte[] ToUtf8() => new UTF8Encoding(false).GetBytes(ToString());

    /// <summary>Parses a playlist.</summary>
    /// <param name="text">The playlist text.</param>
    /// <param name="limits">Limits on size and line count; <see cref="MediaLimits.Default"/> when null. The line count is checked against <see cref="MediaLimits.MaxElementCount"/>.</param>
    /// <returns>An <see cref="HlsMediaPlaylist"/> or an <see cref="HlsMultivariantPlaylist"/>, decided by the tags it holds.</returns>
    /// <exception cref="ManifestFormatException">The text is not an HLS playlist (it does not start with <c>#EXTM3U</c>) or a tag is malformed.</exception>
    /// <exception cref="MediaLimitExceededException">A limit was exceeded.</exception>
    public static HlsPlaylist Parse(string text, MediaLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HlsParser.Parse(text, limits ?? MediaLimits.Default);
    }

    /// <summary>Parses a playlist the way real-world servers write them: what the standard forbids is reported as a defect and skipped instead of thrown.</summary>
    /// <remarks>Recovered cases: no <c>#EXTM3U</c> first line, a segment URI without <c>EXTINF</c> (kept, duration zero), a URI without <c>EXT-X-STREAM-INF</c> (dropped), and any modelled tag whose value is malformed (the tag is ignored). Nothing else changes against <see cref="Parse(string, MediaLimits?)"/>.</remarks>
    /// <param name="text">The playlist text.</param>
    /// <param name="limits">Limits; defaults when null.</param>
    /// <returns>The playlist and the defects found, in line order.</returns>
    /// <exception cref="MediaLimitExceededException">A limit was exceeded.</exception>
    public static HlsParseResult ParseLenient(string text, MediaLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<ManifestDefect> defects = [];
        HlsPlaylist playlist = HlsParser.Parse(text, limits ?? MediaLimits.Default, defects);
        return new HlsParseResult(playlist, defects);
    }

    /// <summary>Like <see cref="ParseLenient(string, MediaLimits?)"/> without throwing.</summary>
    /// <param name="text">The playlist text.</param>
    /// <param name="result">The playlist and defects, or null.</param>
    /// <param name="limits">Limits; defaults when null.</param>
    /// <returns>False when the text is null or a limit was exceeded.</returns>
    public static bool TryParseLenient(string? text, out HlsParseResult? result, MediaLimits? limits = null)
    {
        result = null;
        if (text is null)
        {
            return false;
        }

        try
        {
            result = ParseLenient(text, limits);
            return true;
        }
        catch (MediaLimitExceededException)
        {
            return false;
        }
    }

    /// <summary>Parses a playlist from UTF-8 bytes.</summary>
    /// <param name="utf8">The playlist bytes; a byte order mark is ignored.</param>
    /// <param name="limits">Limits; defaults when null.</param>
    /// <returns>The playlist.</returns>
    /// <exception cref="ManifestFormatException">As for <see cref="Parse(string, MediaLimits?)"/>.</exception>
    /// <exception cref="MediaLimitExceededException">A limit was exceeded.</exception>
    public static HlsPlaylist Parse(ReadOnlySpan<byte> utf8, MediaLimits? limits = null)
    {
        limits ??= MediaLimits.Default;
        limits.CheckAllocation(utf8.Length, 0);
        return HlsParser.Parse(Encoding.UTF8.GetString(utf8), limits);
    }

    /// <summary>Parses a playlist without throwing.</summary>
    /// <param name="text">The playlist text.</param>
    /// <param name="playlist">The playlist, or null.</param>
    /// <param name="limits">Limits; defaults when null.</param>
    /// <returns>False when <see cref="Parse(string, MediaLimits?)"/> would have thrown.</returns>
    public static bool TryParse(string? text, out HlsPlaylist? playlist, MediaLimits? limits = null)
    {
        playlist = null;
        if (text is null)
        {
            return false;
        }

        try
        {
            playlist = Parse(text, limits);
            return true;
        }
        catch (Exception e) when (e is ManifestFormatException or MediaLimitExceededException)
        {
            return false;
        }
    }

    private protected void WriteHeader(TextWriter w)
    {
        w.Write("#EXTM3U\n");
        w.Write(string.Create(CultureInfo.InvariantCulture, $"#EXT-X-VERSION:{EffectiveVersion}\n"));
    }

    private protected void WriteCommon(TextWriter w)
    {
        if (IndependentSegments)
        {
            w.Write("#EXT-X-INDEPENDENT-SEGMENTS\n");
        }

        if (StartTimeOffset is { } start)
        {
            w.Write($"#EXT-X-START:TIME-OFFSET={ManifestText.Seconds(start)}{(StartPrecise ? ",PRECISE=YES" : "")}\n");
        }

        foreach (string t in OtherTags)
        {
            w.Write(t);
            w.Write('\n');
        }
    }
}

/// <summary>A media playlist (RFC 8216 §4.3.3, §4.3.2): a list of media segments.</summary>
public sealed class HlsMediaPlaylist : HlsPlaylist
{
    /// <summary>The <c>EXT-X-TARGETDURATION</c>: an integer number of seconds no shorter than any segment's duration rounded to the nearest second. 0 writes the value computed from the segments.</summary>
    public int TargetDuration { get; init; }

    /// <summary>The <c>EXT-X-MEDIA-SEQUENCE</c>: the sequence number of the first segment (RFC 8216 §4.3.3.2). Live playlists raise it as segments leave the window.</summary>
    public long MediaSequence { get; init; }

    /// <summary>The <c>EXT-X-DISCONTINUITY-SEQUENCE</c>: how many discontinuities came before the first segment (RFC 8216 §4.3.3.3).</summary>
    public long DiscontinuitySequence { get; init; }

    /// <summary>The playlist type.</summary>
    public HlsPlaylistType PlaylistType { get; init; }

    /// <summary>True for <c>EXT-X-I-FRAMES-ONLY</c>.</summary>
    public bool IFramesOnly { get; init; }

    /// <summary>True when <c>EXT-X-ENDLIST</c> is present: no more segments will be added.</summary>
    public bool EndList { get; init; }

    /// <summary>The segments in playback order.</summary>
    public IReadOnlyList<HlsSegment> Segments { get; init; } = [];

    /// <summary>Whole-line tags after the last segment that are not modelled (a preload hint, say), kept in order.</summary>
    public IReadOnlyList<string> TrailingTags { get; init; } = [];

    /// <summary>The sum of the segment durations.</summary>
    public MediaTime TotalDuration
    {
        get
        {
            // Summed in nanoseconds: the segments may be in different timescales (a decimal fraction each), which exact addition would refuse.
            Int128 nanos = 0;
            foreach (HlsSegment s in Segments)
            {
                nanos += (Int128)s.Duration.Ticks * 1_000_000_000 / s.Duration.Timescale;
            }

            return new MediaTime((long)Int128.Clamp(nanos, 0, long.MaxValue), 1_000_000_000);
        }
    }

    /// <summary>The target duration that will be written: <see cref="TargetDuration"/>, or the longest segment rounded up when that is 0 (RFC 8216 §4.3.3.1 asks for the duration rounded to the nearest integer not to exceed it; rounding up satisfies it).</summary>
    public int EffectiveTargetDuration
    {
        get
        {
            if (TargetDuration > 0)
            {
                return TargetDuration;
            }

            long longest = 1;
            foreach (HlsSegment s in Segments)
            {
                long whole = s.Duration.Rescale(1, MediaRounding.Ceiling).Ticks;
                longest = Math.Max(longest, whole);
            }

            return (int)Math.Min(longest, int.MaxValue);
        }
    }

    /// <inheritdoc/>
    public override int MinimumVersion
    {
        get
        {
            int v = 1;
            foreach (HlsSegment s in Segments)
            {
                if (s.Duration.IsKnown && s.Duration.Timescale != 1 && (s.Duration.Ticks % s.Duration.Timescale) != 0)
                {
                    v = Math.Max(v, 3);
                }

                if (s.ByteRange is not null)
                {
                    v = Math.Max(v, 4);
                }

                if (s.Map is not null)
                {
                    v = Math.Max(v, IFramesOnly ? 5 : 6);
                }

                // RFC 8216 §7: IV needs version 2, KEYFORMAT and KEYFORMATVERSIONS version 5.
                foreach (HlsKey k in s.Keys)
                {
                    if (k.Iv is not null)
                    {
                        v = Math.Max(v, 2);
                    }

                    if (k.KeyFormat is not null || k.KeyFormatVersions is not null)
                    {
                        v = Math.Max(v, 5);
                    }
                }
            }

            return IFramesOnly ? Math.Max(v, 4) : v;
        }
    }

    /// <inheritdoc/>
    public override void WriteTo(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WriteHeader(writer);
        writer.Write(string.Create(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{EffectiveTargetDuration}\n"));
        writer.Write(string.Create(CultureInfo.InvariantCulture, $"#EXT-X-MEDIA-SEQUENCE:{MediaSequence}\n"));
        if (DiscontinuitySequence > 0)
        {
            writer.Write(string.Create(CultureInfo.InvariantCulture, $"#EXT-X-DISCONTINUITY-SEQUENCE:{DiscontinuitySequence}\n"));
        }

        if (PlaylistType != HlsPlaylistType.None)
        {
            writer.Write(PlaylistType == HlsPlaylistType.Vod ? "#EXT-X-PLAYLIST-TYPE:VOD\n" : "#EXT-X-PLAYLIST-TYPE:EVENT\n");
        }

        if (IFramesOnly)
        {
            writer.Write("#EXT-X-I-FRAMES-ONLY\n");
        }

        WriteCommon(writer);
        HlsMap? previousMap = null;
        HlsByteRange? previousRange = null;
        string? previousUri = null;
        int version = EffectiveVersion;
        IReadOnlyList<HlsKey> previousKeys = [];
        foreach (HlsSegment s in Segments)
        {
            foreach (string t in s.OtherTags)
            {
                writer.Write(t);
                writer.Write('\n');
            }

            if (s.KeysChanged)
            {
                // RFC 8216 §4.3.2.4: METHOD=NONE ends every key. Write it first when a key of an earlier segment (by KEYFORMAT) is no longer in effect,
                // so that reading the output back gives the same keys in effect.
                if (s.Keys.Count == 0 || previousKeys.Any(p => !s.Keys.Any(k => string.Equals(k.KeyFormat ?? "identity", p.KeyFormat ?? "identity", StringComparison.Ordinal))))
                {
                    writer.Write("#EXT-X-KEY:METHOD=NONE\n");
                }

                foreach (HlsKey k in s.Keys)
                {
                    writer.Write(k.ToString());
                    writer.Write('\n');
                }
            }

            if (s.Discontinuity)
            {
                writer.Write("#EXT-X-DISCONTINUITY\n");
            }

            if (s.Map is { } map && map != previousMap)
            {
                writer.Write($"#EXT-X-MAP:URI=\"{map.Uri}\"");
                if (map.ByteRange is { } mr)
                {
                    writer.Write(string.Create(CultureInfo.InvariantCulture, $",BYTERANGE=\"{mr.Length}@{mr.Offset}\""));
                }

                writer.Write('\n');
            }

            previousMap = s.Map ?? previousMap;
            if (s.ProgramDateTime is { } pdt)
            {
                writer.Write($"#EXT-X-PROGRAM-DATE-TIME:{ManifestText.DateTime(pdt)}\n");
            }

            if (s.Gap)
            {
                writer.Write("#EXT-X-GAP\n");
            }

            if (s.ByteRange is { } r)
            {
                // RFC 8216 §4.3.2.2: without an offset the range starts after the previous range of the same resource.
                bool contiguous = previousRange is { } p && previousUri == s.Uri && p.End == r.Offset;
                writer.Write(contiguous
                    ? string.Create(CultureInfo.InvariantCulture, $"#EXT-X-BYTERANGE:{r.Length}\n")
                    : string.Create(CultureInfo.InvariantCulture, $"#EXT-X-BYTERANGE:{r.Length}@{r.Offset}\n"));
                previousRange = r;
            }
            else
            {
                previousRange = null;
            }

            previousUri = s.Uri;
            previousKeys = s.Keys;
            string duration = version >= 3 ? ManifestText.Seconds(s.Duration) : s.Duration.Rescale(1, MediaRounding.NearestAwayFromZero).Ticks.ToString(CultureInfo.InvariantCulture);
            writer.Write($"#EXTINF:{duration},{s.Title}\n");
            writer.Write(s.Uri);
            writer.Write('\n');
        }

        foreach (string t in TrailingTags)
        {
            writer.Write(t);
            writer.Write('\n');
        }

        if (EndList)
        {
            writer.Write("#EXT-X-ENDLIST\n");
        }
    }
}

/// <summary>A variant stream of a multivariant playlist: one <c>EXT-X-STREAM-INF</c> with its URI (RFC 8216 §4.3.4.2).</summary>
public sealed record HlsVariant
{
    /// <summary>The URI of the variant's media playlist.</summary>
    public required string Uri { get; init; }

    /// <summary>Peak bits per second (<c>BANDWIDTH</c>).</summary>
    public long Bandwidth { get; init; }

    /// <summary>Average bits per second (<c>AVERAGE-BANDWIDTH</c>), 0 when absent.</summary>
    public long AverageBandwidth { get; init; }

    /// <summary>The <c>CODECS</c> list of RFC 6381 strings, or null.</summary>
    public string? Codecs { get; init; }

    /// <summary>Video width in pixels from <c>RESOLUTION</c>, 0 when absent.</summary>
    public int Width { get; init; }

    /// <summary>Video height in pixels from <c>RESOLUTION</c>, 0 when absent.</summary>
    public int Height { get; init; }

    /// <summary>The <c>FRAME-RATE</c> as written (a decimal number), or null.</summary>
    public string? FrameRate { get; init; }

    /// <summary>The <c>VIDEO-RANGE</c> (<c>SDR</c>, <c>PQ</c>, <c>HLG</c>), or null.</summary>
    public string? VideoRange { get; init; }

    /// <summary>The group id of the audio renditions (<c>AUDIO</c>), or null.</summary>
    public string? AudioGroup { get; init; }

    /// <summary>The group id of the video renditions (<c>VIDEO</c>), or null.</summary>
    public string? VideoGroup { get; init; }

    /// <summary>The group id of the subtitle renditions (<c>SUBTITLES</c>), or null.</summary>
    public string? SubtitleGroup { get; init; }

    /// <summary>The group id of the closed-caption renditions (<c>CLOSED-CAPTIONS</c>); <c>NONE</c> is kept as the string "NONE"; null when absent.</summary>
    public string? ClosedCaptionGroup { get; init; }

    /// <summary>True for an <c>EXT-X-I-FRAME-STREAM-INF</c> (trick-play) variant, whose <see cref="Uri"/> is an attribute rather than a following line.</summary>
    public bool IFrameOnly { get; init; }

    /// <summary>Attributes not modelled, as <c>NAME=value</c> text, kept in order.</summary>
    public IReadOnlyList<string> OtherAttributes { get; init; } = [];
}

/// <summary>The type of an <c>EXT-X-MEDIA</c> rendition.</summary>
public enum HlsMediaType
{
    /// <summary>Audio.</summary>
    Audio = 0,

    /// <summary>Video.</summary>
    Video,

    /// <summary>Subtitles.</summary>
    Subtitles,

    /// <summary>Closed captions carried in the video.</summary>
    ClosedCaptions,
}

/// <summary>An alternative rendition of the content: one <c>EXT-X-MEDIA</c> (RFC 8216 §4.3.4.1).</summary>
public sealed record HlsRendition
{
    /// <summary>The rendition type.</summary>
    public HlsMediaType Type { get; init; }

    /// <summary>The group the rendition belongs to (<c>GROUP-ID</c>).</summary>
    public required string GroupId { get; init; }

    /// <summary>The human-readable name.</summary>
    public required string Name { get; init; }

    /// <summary>The URI of the rendition's media playlist; null when the rendition is carried in the variants' own stream (closed captions, muxed audio).</summary>
    public string? Uri { get; init; }

    /// <summary>The <c>LANGUAGE</c> tag (RFC 5646), or null.</summary>
    public string? Language { get; init; }

    /// <summary>True for <c>DEFAULT=YES</c>.</summary>
    public bool Default { get; init; }

    /// <summary>True for <c>AUTOSELECT=YES</c>.</summary>
    public bool AutoSelect { get; init; }

    /// <summary>True for <c>FORCED=YES</c>.</summary>
    public bool Forced { get; init; }

    /// <summary>The <c>CHANNELS</c> string (for audio, the channel count and optional codec-specific parameters), or null.</summary>
    public string? Channels { get; init; }

    /// <summary>The <c>CHARACTERISTICS</c> string, or null.</summary>
    public string? Characteristics { get; init; }

    /// <summary>The <c>INSTREAM-ID</c> of a closed-caption rendition (<c>CC1</c>…<c>CC4</c>, <c>SERVICE1</c>…), or null.</summary>
    public string? InstreamId { get; init; }

    /// <summary>Attributes not modelled, as <c>NAME=value</c> text.</summary>
    public IReadOnlyList<string> OtherAttributes { get; init; } = [];
}

/// <summary>A multivariant playlist (RFC 8216 §4.3.4): variant streams and alternative renditions.</summary>
public sealed class HlsMultivariantPlaylist : HlsPlaylist
{
    /// <summary>The variant streams, in the order written.</summary>
    public IReadOnlyList<HlsVariant> Variants { get; init; } = [];

    /// <summary>The alternative renditions.</summary>
    public IReadOnlyList<HlsRendition> Renditions { get; init; } = [];

    /// <inheritdoc/>
    public override int MinimumVersion
    {
        get
        {
            foreach (HlsRendition r in Renditions)
            {
                if (r.InstreamId is { } id && id.StartsWith("SERVICE", StringComparison.Ordinal))
                {
                    return 7;
                }
            }

            foreach (HlsVariant v in Variants)
            {
                if (v.IFrameOnly)
                {
                    return 4;
                }
            }

            return 1;
        }
    }

    /// <inheritdoc/>
    public override void WriteTo(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WriteHeader(writer);
        WriteCommon(writer);
        foreach (HlsRendition r in Renditions)
        {
            StringBuilder sb = new("#EXT-X-MEDIA:TYPE=");
            sb.Append(r.Type switch { HlsMediaType.Video => "VIDEO", HlsMediaType.Subtitles => "SUBTITLES", HlsMediaType.ClosedCaptions => "CLOSED-CAPTIONS", _ => "AUDIO" });
            sb.Append(",GROUP-ID=\"").Append(r.GroupId).Append('"');
            sb.Append(",NAME=\"").Append(r.Name).Append('"');
            if (r.Language is not null)
            {
                sb.Append(",LANGUAGE=\"").Append(r.Language).Append('"');
            }

            sb.Append(",DEFAULT=").Append(r.Default ? "YES" : "NO");
            sb.Append(",AUTOSELECT=").Append(r.AutoSelect ? "YES" : "NO");
            if (r.Forced)
            {
                sb.Append(",FORCED=YES");
            }

            if (r.Channels is not null)
            {
                sb.Append(",CHANNELS=\"").Append(r.Channels).Append('"');
            }

            if (r.Characteristics is not null)
            {
                sb.Append(",CHARACTERISTICS=\"").Append(r.Characteristics).Append('"');
            }

            if (r.InstreamId is not null)
            {
                sb.Append(",INSTREAM-ID=\"").Append(r.InstreamId).Append('"');
            }

            if (r.Uri is not null)
            {
                sb.Append(",URI=\"").Append(r.Uri).Append('"');
            }

            foreach (string o in r.OtherAttributes)
            {
                sb.Append(',').Append(o);
            }

            writer.Write(sb.ToString());
            writer.Write('\n');
        }

        foreach (HlsVariant v in Variants)
        {
            StringBuilder sb = new(v.IFrameOnly ? "#EXT-X-I-FRAME-STREAM-INF:" : "#EXT-X-STREAM-INF:");
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"BANDWIDTH={v.Bandwidth}"));
            if (v.AverageBandwidth > 0)
            {
                sb.Append(string.Create(CultureInfo.InvariantCulture, $",AVERAGE-BANDWIDTH={v.AverageBandwidth}"));
            }

            if (v.Codecs is not null)
            {
                sb.Append(",CODECS=\"").Append(v.Codecs).Append('"');
            }

            if (v.Width > 0 && v.Height > 0)
            {
                sb.Append(string.Create(CultureInfo.InvariantCulture, $",RESOLUTION={v.Width}x{v.Height}"));
            }

            if (v.FrameRate is not null)
            {
                sb.Append(",FRAME-RATE=").Append(v.FrameRate);
            }

            if (v.VideoRange is not null)
            {
                sb.Append(",VIDEO-RANGE=").Append(v.VideoRange);
            }

            if (v.VideoGroup is not null)
            {
                sb.Append(",VIDEO=\"").Append(v.VideoGroup).Append('"');
            }

            if (v.AudioGroup is not null)
            {
                sb.Append(",AUDIO=\"").Append(v.AudioGroup).Append('"');
            }

            if (v.SubtitleGroup is not null)
            {
                sb.Append(",SUBTITLES=\"").Append(v.SubtitleGroup).Append('"');
            }

            if (v.ClosedCaptionGroup is not null)
            {
                // CLOSED-CAPTIONS=NONE is an enumerated string, not a quoted one (RFC 8216 §4.3.4.2).
                if (v.ClosedCaptionGroup == "NONE")
                {
                    sb.Append(",CLOSED-CAPTIONS=NONE");
                }
                else
                {
                    sb.Append(",CLOSED-CAPTIONS=\"").Append(v.ClosedCaptionGroup).Append('"');
                }
            }

            foreach (string o in v.OtherAttributes)
            {
                sb.Append(',').Append(o);
            }

            if (v.IFrameOnly)
            {
                sb.Append(",URI=\"").Append(v.Uri).Append('"');
                writer.Write(sb.ToString());
                writer.Write('\n');
            }
            else
            {
                writer.Write(sb.ToString());
                writer.Write('\n');
                writer.Write(v.Uri);
                writer.Write('\n');
            }
        }
    }
}
