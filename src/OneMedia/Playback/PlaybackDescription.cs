using System.Linq;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Playback;

/// <summary>What a browser needs to know about one track to decide whether it plays.</summary>
/// <param name="TrackIndex">The track's <see cref="MediaTrack.Index"/>.</param>
/// <param name="Kind">Video or audio. Other kinds are not described: browsers ignore subtitle and data tracks inside a file.</param>
/// <param name="Codec">The codec.</param>
/// <param name="CodecTag">The container's name for the codec, such as <c>hvc1</c>; null when none.</param>
/// <param name="CodecString">The RFC 6381 <c>codecs</c> value, as <c>MediaSource.isTypeSupported</c> takes it; null when none can be built.</param>
/// <param name="Profile">The codec profile number (see <see cref="CodecRule.Profiles"/>), or -1 when the configuration does not say.</param>
/// <param name="Level">The codec level (see <see cref="CodecRule.MaxLevel"/>), or -1.</param>
/// <param name="BitDepth">The luma bit depth, or 0 when unknown.</param>
/// <param name="Chroma">The chroma format, or <see cref="ChromaFormat.Unknown"/>.</param>
/// <param name="IsProtected">True when the samples are encrypted.</param>
public sealed record PlaybackTrack(
    int TrackIndex, TrackKind Kind, CodecId Codec, string? CodecTag, string? CodecString, int Profile, int Level, int BitDepth, ChromaFormat Chroma, bool IsProtected);

/// <summary>The facts about a file that decide playability, separated from any profile so they can be shown, logged and tested.</summary>
/// <param name="Container">The container key (see <see cref="ContainerSupport"/>), or <c>unknown</c>.</param>
/// <param name="ContentType">The MIME type a server should send, without parameters, for example <c>video/mp4</c>; <c>audio/…</c> when the file has no video.</param>
/// <param name="MimeType">The content type with a <c>codecs</c> parameter for every described track, as passed to <c>canPlayType</c> and
/// <c>MediaSource.isTypeSupported</c>; the plain content type when some track has no codec string.</param>
/// <param name="IsSegmented">True for fragmented MP4 and for WebM, the two forms that can be appended to a MediaSource.</param>
/// <param name="IsFastStart">True when the index can be read before the media data (the movie box precedes it); true for containers that have no such distinction.</param>
/// <param name="Tracks">The video and audio tracks, in container order. Cover pictures stored as video tracks are left out.</param>
public sealed record PlaybackDescription(
    string Container, string ContentType, string MimeType, bool IsSegmented, bool IsFastStart, IReadOnlyList<PlaybackTrack> Tracks)
{
    /// <summary>True when the file has a video track.</summary>
    public bool HasVideo => Tracks.Any(t => t.Kind == TrackKind.Video);

    /// <summary>Describes an open file. Reads nothing beyond what opening it already read.</summary>
    /// <param name="file">The file.</param>
    /// <returns>The description. Never throws for a file that opened.</returns>
    public static PlaybackDescription Of(MediaFile file)
    {
        (string container, bool segmented, bool fastStart) = Classify(file);
        List<PlaybackTrack> tracks = [];
        foreach (MediaTrack t in file.Tracks)
        {
            if (t.Kind is not (TrackKind.Video or TrackKind.Audio) || (t.Disposition & TrackDisposition.AttachedPicture) != 0)
            {
                continue;
            }

            CodecParameters? p = t.DescribeCodec();
            int profile = p?.Profile ?? -1;
            if (t.Kind == TrackKind.Audio && t.Codec == CodecId.Aac && p is not null)
            {
                // HE-AAC announces itself by its SBR/PS tool, not by the object type in the configuration (ISO/IEC 14496-3 §1.6.5).
                profile = p.PsPresent ? 29 : p.SbrPresent || p.ContainerDeclaresDoubleRate ? 5 : p.Profile;
            }

            tracks.Add(new PlaybackTrack(t.Index, t.Kind, t.Codec, t.CodecTag, t.Rfc6381Codec() ?? FallbackCodecString(t.Codec, container), profile, p?.Level ?? -1, p?.BitDepth ?? 0, p?.Chroma ?? ChromaFormat.Unknown, t.IsProtected));
        }

        bool video = tracks.Any(t => t.Kind == TrackKind.Video);
        string contentType = ContentTypeOf(container, video);
        string[] strings = tracks.Select(t => t.CodecString ?? string.Empty).ToArray();
        string mime = tracks.Count > 0 && strings.All(s => s.Length > 0) ? $"{contentType}; codecs=\"{string.Join(',', strings)}\"" : contentType;
        return new PlaybackDescription(container, contentType, mime, segmented, fastStart, tracks);
    }

    // Codecs whose configuration gives no RFC 6381 string but which browsers name in a codecs parameter: MP3 is mp4a.6B inside ISO files
    // (the object type indication for MPEG-1 audio, ISO/IEC 14496-1 Table 5) and plain mp3 elsewhere; ALAC is named alac.
    private static string? FallbackCodecString(CodecId codec, string container) => codec.Name switch
    {
        "mp3" => container is "mp4" or "mov" or "3gp" ? "mp4a.6B" : "mp3",
        "alac" => "alac",
        _ => null,
    };

    private static (string Container, bool Segmented, bool FastStart) Classify(MediaFile file)
    {
        object? demuxer = (object?)file.Demuxer ?? file.AsyncDemuxer;
        switch (demuxer)
        {
            case IsoBmffDemuxer iso:
                return (IsoKey(iso.FileType), iso.Movie.IsFragmented, !iso.MediaDataPrecedesMovie);
            case IsoBmffAsyncDemuxer isoAsync:
                return (IsoKey(isoAsync.FileType), isoAsync.Movie.IsFragmented, !isoAsync.MediaDataPrecedesMovie);
            case MatroskaDemuxer mkv:
                return (mkv.Header.IsWebM ? "webm" : "matroska", mkv.Header.IsWebM, true);
            case MatroskaAsyncDemuxer mkvAsync:
                return (mkvAsync.Header.IsWebM ? "webm" : "matroska", mkvAsync.Header.IsWebM, true);
        }

        string key = file.Format.Name switch
        {
            "wav" => "wav",
            "avi" => "avi",
            "asf" => "asf",
            "mpegts" => "mpegts",
            "flv" => "flv",
            "ogg" => "ogg",
            "flac" => "flac",
            "aiff" => "aiff",
            "caf" => "caf",
            "id3" or "mpegaudio" => "mp3",
            "adts" => "adts",
            "amr" => "amr",
            "ac3" => "ac3",
            _ => "unknown",
        };
        return (key, false, true);
    }

    // ISO/IEC 14496-12 §4.3: the major brand names the file's specification. QuickTime movies use 'qt  ' (Apple QTFF), 3GPP files
    // use 3gp*/3g2* (3GPP TS 26.244 §5.3); everything else is treated as MP4.
    private static string IsoKey(FileTypeBox? ftyp) => ftyp switch
    {
        null => "mov",
        { IsQuickTime: true } => "mov",
        { MajorBrand: var b } when b.StartsWith("3g", StringComparison.Ordinal) => "3gp",
        _ => "mp4",
    };

    private static string ContentTypeOf(string container, bool video) => container switch
    {
        "mp4" => video ? "video/mp4" : "audio/mp4",
        "mov" => video ? "video/quicktime" : "audio/x-m4a",
        "3gp" => video ? "video/3gpp" : "audio/3gpp",
        "webm" => video ? "video/webm" : "audio/webm",
        "matroska" => video ? "video/x-matroska" : "audio/x-matroska",
        "ogg" => video ? "video/ogg" : "audio/ogg",
        "mp3" => "audio/mpeg",
        "adts" => "audio/aac",
        "flac" => "audio/flac",
        "wav" => "audio/wav",
        "aiff" => "audio/aiff",
        "caf" => "audio/x-caf",
        "avi" => "video/x-msvideo",
        "flv" => "video/x-flv",
        "mpegts" => video ? "video/mp2t" : "audio/mp2t",
        "asf" => video ? "video/x-ms-asf" : "audio/x-ms-asf",
        "ac3" => "audio/ac3",
        "amr" => "audio/amr",
        _ => "application/octet-stream",
    };
}
