using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Subtitles;

/// <summary>The kinds of subtitle text track that OneMedia reads and writes inside MP4, QuickTime, 3GP and Matroska/WebM files.</summary>
public enum TextTrackFormat
{
    /// <summary>Not a text track this library maps to cues.</summary>
    Unknown = 0,

    /// <summary>3GPP timed text, sample entry <c>tx3g</c> (3GPP TS 26.245); also called <c>mov_text</c>.</summary>
    Tx3g,

    /// <summary>QuickTime text, sample entry <c>text</c> (Apple QuickTime File Format). Read only.</summary>
    QuickTimeText,

    /// <summary>WebVTT in ISO files, sample entry <c>wvtt</c>.</summary>
    WebVttIso,

    /// <summary>XML subtitles in ISO files, sample entry <c>stpp</c> (TTML and IMSC documents).</summary>
    XmlSubtitleIso,

    /// <summary>Matroska <c>S_TEXT/UTF8</c> (SubRip text).</summary>
    SubRip,

    /// <summary>Matroska <c>S_TEXT/ASS</c> and <c>S_TEXT/SSA</c> (SubStation Alpha events with the script header in CodecPrivate).</summary>
    Ass,

    /// <summary>Matroska <c>S_TEXT/WEBVTT</c> (cue text in the Block, settings and identifier in BlockAdditions).</summary>
    WebVtt,

    /// <summary>WebM <c>D_WEBVTT/SUBTITLES</c>, <c>/CAPTIONS</c>, <c>/DESCRIPTIONS</c> and <c>/METADATA</c> (identifier, settings and text in the Block).</summary>
    WebmWebVtt,
}

/// <summary>Something a text-track conversion or mapping could not carry.</summary>
/// <param name="Feature">Short name: <c>placement</c>, <c>speaker</c>, <c>ruby</c>, <c>overlap</c>…</param>
/// <param name="Detail">One sentence in plain words.</param>
/// <param name="Cues">Number of cues affected; 0 when it concerns the track as a whole.</param>
public sealed record TextTrackLoss(string Feature, string Detail, int Cues = 0);

/// <summary>Names the text-track formats and tells which one a track is.</summary>
public static class TextTracks
{
    /// <summary>The text-track format of a track, from its sample entry code or Matroska CodecID.</summary>
    /// <param name="track">A track from any container.</param>
    /// <returns><see cref="TextTrackFormat.Unknown"/> for tracks that are not subtitles, subtitles in other forms (bitmaps, transport stream subtitles) and codecs without a mapping.</returns>
    public static TextTrackFormat FormatOf(MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Kind != TrackKind.Subtitle)
        {
            return TextTrackFormat.Unknown;
        }

        string? tag = track.CodecTag;
        if (tag is not null)
        {
            switch (tag)
            {
                case "tx3g": return TextTrackFormat.Tx3g;
                case "text": return TextTrackFormat.QuickTimeText;
                case "wvtt": return TextTrackFormat.WebVttIso;
                case "stpp": return TextTrackFormat.XmlSubtitleIso;
                case "S_TEXT/UTF8":
                case "S_TEXT/ASCII": return TextTrackFormat.SubRip;
                case "S_TEXT/ASS":
                case "S_TEXT/SSA":
                case "S_ASS":
                case "S_SSA": return TextTrackFormat.Ass;
                case "S_TEXT/WEBVTT": return TextTrackFormat.WebVtt;
                default:
                    return tag.StartsWith("D_WEBVTT/", StringComparison.Ordinal) ? TextTrackFormat.WebmWebVtt : TextTrackFormat.Unknown;
            }
        }

        // A track described without a container tag (built by a caller): the codec name decides, in the form a sample entry or CodecID would give.
        if (track.Codec == CodecId.TimedText)
        {
            return TextTrackFormat.Tx3g;
        }

        if (track.Codec == CodecId.SubRip)
        {
            return TextTrackFormat.SubRip;
        }

        return track.Codec.Name switch
        {
            "ass" => TextTrackFormat.Ass,
            "webvtt" => TextTrackFormat.WebVtt,
            _ => TextTrackFormat.Unknown,
        };
    }

    /// <summary>True for the formats that live in ISO base media files (MP4, MOV, 3GP).</summary>
    /// <param name="format">A format.</param>
    public static bool IsIsoFormat(TextTrackFormat format) => format is TextTrackFormat.Tx3g or TextTrackFormat.QuickTimeText or TextTrackFormat.WebVttIso or TextTrackFormat.XmlSubtitleIso;

    /// <summary>True for the formats that live in Matroska and WebM.</summary>
    /// <param name="format">A format.</param>
    public static bool IsMatroskaFormat(TextTrackFormat format) => format is TextTrackFormat.SubRip or TextTrackFormat.Ass or TextTrackFormat.WebVtt or TextTrackFormat.WebmWebVtt;

    /// <summary>
    /// The format a text track is written as when a remux moves it into <paramref name="target"/>: the track's own format when the target holds it
    /// (a <c>tx3g</c> track stays <c>tx3g</c> in MP4, a SubRip track stays SubRip in Matroska), else the nearest one.
    /// </summary>
    /// <remarks>
    /// MP4, MOV and 3GP take <c>tx3g</c> (plain and styled text), <c>wvtt</c> and <c>stpp</c>; Matroska takes SubRip, ASS and WebVTT; WebM takes WebVTT only.
    /// Moving between the families chooses: <c>tx3g</c> to SubRip, <c>wvtt</c> to WebVTT, <c>stpp</c> to WebVTT, SubRip to <c>tx3g</c>, ASS to <c>tx3g</c>,
    /// WebVTT to <c>wvtt</c>. QuickTime <c>text</c> becomes <c>tx3g</c> in ISO targets and SubRip in Matroska.
    /// </remarks>
    /// <param name="source">The track's format.</param>
    /// <param name="target">The output container.</param>
    /// <returns>The format to write, or <see cref="TextTrackFormat.Unknown"/> when <paramref name="source"/> is unknown.</returns>
    public static TextTrackFormat TargetFor(TextTrackFormat source, RemuxFormat target)
    {
        bool iso = target is not (RemuxFormat.Matroska or RemuxFormat.WebM);
        if (source == TextTrackFormat.Unknown)
        {
            return TextTrackFormat.Unknown;
        }

        if (target == RemuxFormat.WebM)
        {
            return TextTrackFormat.WebmWebVtt;
        }

        if (iso)
        {
            return source switch
            {
                TextTrackFormat.WebVtt or TextTrackFormat.WebmWebVtt => TextTrackFormat.WebVttIso,
                TextTrackFormat.QuickTimeText or TextTrackFormat.SubRip or TextTrackFormat.Ass => TextTrackFormat.Tx3g,
                _ => source,
            };
        }

        return source switch
        {
            TextTrackFormat.Tx3g or TextTrackFormat.QuickTimeText => TextTrackFormat.SubRip,
            TextTrackFormat.WebVttIso or TextTrackFormat.XmlSubtitleIso => TextTrackFormat.WebVtt,
            TextTrackFormat.WebmWebVtt => TextTrackFormat.WebVtt,
            _ => source,
        };
    }
}
