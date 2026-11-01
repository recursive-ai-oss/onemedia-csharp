using RecursiveAI.OneMedia.Codecs;

namespace RecursiveAI.OneMedia;

/// <summary>What a track carries.</summary>
public enum TrackKind
{
    /// <summary>The container does not say, or the track type is not recognised.</summary>
    Unknown = 0,

    /// <summary>Moving pictures.</summary>
    Video,

    /// <summary>Sound.</summary>
    Audio,

    /// <summary>Text or bitmap subtitles and captions.</summary>
    Subtitle,

    /// <summary>Timed data that is neither of the above: timecode, telemetry, timed metadata, chapters.</summary>
    Data,
}

/// <summary>Flags describing how a player should treat a track.</summary>
[Flags]
public enum TrackDisposition
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>Preferred track of its kind when the viewer has not chosen.</summary>
    Default = 1 << 0,

    /// <summary>Shown even when subtitles are off (foreign-language parts only).</summary>
    Forced = 1 << 1,

    /// <summary>In the original language of the work.</summary>
    Original = 1 << 2,

    /// <summary>Commentary track.</summary>
    Commentary = 1 << 3,

    /// <summary>Made for the hearing impaired (captions, with sound descriptions).</summary>
    HearingImpaired = 1 << 4,

    /// <summary>Made for the visually impaired (audio description).</summary>
    VisualImpaired = 1 << 5,

    /// <summary>A dub in another language.</summary>
    Dub = 1 << 6,

    /// <summary>Lyrics.</summary>
    Lyrics = 1 << 7,

    /// <summary>Karaoke.</summary>
    Karaoke = 1 << 8,

    /// <summary>A still picture (cover art) stored as a video track, not part of the timeline.</summary>
    AttachedPicture = 1 << 9,

    /// <summary>The container marks the track as disabled.</summary>
    Disabled = 1 << 10,
}

/// <summary>
/// Names a compression format, independently of the container that carries it. Compare by <see cref="Name"/>. Audio names are
/// plain lowercase strings (<c>aac</c>, <c>opus</c>, <c>amr-nb</c>…) so that adapters to audio libraries can map them one to one.
/// </summary>
/// <param name="Name">Short lowercase name, for example <c>h264</c>. Empty means unknown.</param>
public readonly record struct CodecId(string Name)
{
    /// <summary>The codec is not known or not recognised.</summary>
    public static CodecId Unknown => new("");

    /// <summary>H.264 / AVC.</summary>
    public static CodecId H264 => new("h264");
    /// <summary>H.265 / HEVC.</summary>
    public static CodecId Hevc => new("hevc");
    /// <summary>H.266 / VVC.</summary>
    public static CodecId Vvc => new("vvc");
    /// <summary>AV1.</summary>
    public static CodecId Av1 => new("av1");
    /// <summary>VP8.</summary>
    public static CodecId Vp8 => new("vp8");
    /// <summary>VP9.</summary>
    public static CodecId Vp9 => new("vp9");
    /// <summary>MPEG-2 video (H.262).</summary>
    public static CodecId Mpeg2Video => new("mpeg2video");
    /// <summary>Motion JPEG.</summary>
    public static CodecId Mjpeg => new("mjpeg");

    /// <summary>AAC.</summary>
    public static CodecId Aac => new("aac");
    /// <summary>MPEG audio Layer III.</summary>
    public static CodecId Mp3 => new("mp3");
    /// <summary>Opus.</summary>
    public static CodecId Opus => new("opus");
    /// <summary>Vorbis.</summary>
    public static CodecId Vorbis => new("vorbis");
    /// <summary>FLAC.</summary>
    public static CodecId Flac => new("flac");
    /// <summary>Dolby Digital (AC-3).</summary>
    public static CodecId Ac3 => new("ac3");
    /// <summary>Dolby Digital Plus (E-AC-3).</summary>
    public static CodecId Eac3 => new("eac3");
    /// <summary>AMR narrowband.</summary>
    public static CodecId AmrNb => new("amr-nb");
    /// <summary>AMR wideband.</summary>
    public static CodecId AmrWb => new("amr-wb");
    /// <summary>Uncompressed PCM; the sample layout is in the track's audio info and configuration.</summary>
    public static CodecId Pcm => new("pcm");

    /// <summary>Apple Lossless (ALAC).</summary>
    public static CodecId Alac => new("alac");
    /// <summary>MPEG audio Layer II.</summary>
    public static CodecId Mp2 => new("mp2");
    /// <summary>Microsoft ADPCM.</summary>
    public static CodecId AdpcmMs => new("adpcm-ms");
    /// <summary>IMA ADPCM as stored in WAVE (format tag 0x0011).</summary>
    public static CodecId AdpcmImaWav => new("adpcm-ima-wav");
    /// <summary>IMA ADPCM as stored in QuickTime, AIFF-C and CAF (<c>ima4</c>).</summary>
    public static CodecId AdpcmImaQt => new("adpcm-ima-qt");

    /// <summary>3GPP timed text (tx3g).</summary>
    public static CodecId TimedText => new("tx3g");
    /// <summary>WebVTT.</summary>
    public static CodecId WebVtt => new("webvtt");
    /// <summary>SubRip text.</summary>
    public static CodecId SubRip => new("subrip");

    /// <summary>True for <see cref="Unknown"/>.</summary>
    public bool IsUnknown => string.IsNullOrEmpty(Name);

    /// <inheritdoc />
    public override string ToString() => Name ?? string.Empty;
}

/// <summary>
/// The codec setup bytes a container stores for a track, kept exactly as stored. This layer does not interpret them; the codec
/// header layer parses them.
/// </summary>
/// <param name="Data">The bytes. For AAC this is the AudioSpecificConfig (ISO/IEC 14496-3 §1.6.2.1) as stored. Empty when the
/// container stores none.</param>
/// <param name="Format">How <paramref name="Data"/> is laid out, so a reader of the bytes knows what it holds: for example
/// <c>avcC</c>, <c>hvcC</c>, <c>av1C</c>, <c>asc</c>, <c>dOps</c>, <c>CodecPrivate</c>. Lowercase or as the container spells it;
/// null when empty or unspecified.</param>
public readonly record struct CodecConfiguration(ReadOnlyMemory<byte> Data, string? Format = null)
{
    /// <summary>True when there are no bytes.</summary>
    public bool IsEmpty => Data.IsEmpty;
}

/// <summary>Audio parameters as the container declares them. Zero means the container does not say.</summary>
/// <param name="SampleRate">Samples per second per channel, in Hz.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="BitsPerSample">Bits per sample for PCM and lossless codecs.</param>
public readonly record struct AudioTrackInfo(int SampleRate, int Channels, int BitsPerSample = 0);

/// <summary>Video parameters as the container declares them. Zero means the container does not say.</summary>
/// <param name="Width">Coded width in pixels (before the pixel aspect ratio and rotation are applied).</param>
/// <param name="Height">Coded height in pixels.</param>
/// <param name="RotationDegrees">Clockwise rotation to apply for display: 0, 90, 180 or 270.</param>
/// <param name="PixelAspectNumerator">Pixel aspect ratio numerator (1 for square pixels).</param>
/// <param name="PixelAspectDenominator">Pixel aspect ratio denominator (1 for square pixels).</param>
/// <param name="FrameRateNumerator">Nominal frame rate numerator, 0 if unknown or variable.</param>
/// <param name="FrameRateDenominator">Nominal frame rate denominator, 0 if unknown or variable.</param>
public readonly record struct VideoTrackInfo(
    int Width,
    int Height,
    int RotationDegrees = 0,
    int PixelAspectNumerator = 1,
    int PixelAspectDenominator = 1,
    int FrameRateNumerator = 0,
    int FrameRateDenominator = 0);

/// <summary>
/// One segment of an edit list: a stretch of the track's media placed on the presentation timeline.
/// </summary>
/// <param name="MediaStart">Where in the track's media the segment starts, or <see cref="MediaTime.Unknown"/> for an empty edit
/// (a gap; ISO/IEC 14496-12 §8.6.6 marks it with a media_time of -1).</param>
/// <param name="Duration">How long the segment lasts on the presentation timeline.</param>
public readonly record struct EditSegment(MediaTime MediaStart, MediaTime Duration)
{
    /// <summary>True when the segment is a gap with no media.</summary>
    public bool IsEmpty => !MediaStart.IsKnown;
}

/// <summary>
/// How a track's raw media timeline maps onto what a player should present: decoder delay to drop at the start, padding to drop at
/// the end, and where the first presented sample lands. All times are zero when the container says nothing.
/// </summary>
/// <param name="Priming">Media time at the start of the decoded output to discard: encoder delay or pre-skip, from an edit list's
/// first media_time, a Matroska CodecDelay or an Opus pre-skip.</param>
/// <param name="EndPadding">Media time at the end of the decoded output to discard, from a Matroska DiscardPadding or an edit
/// list that stops short of the media's end.</param>
/// <param name="StartTime">Where the first presented sample sits on the file's timeline: positive when an initial empty edit or a
/// start offset delays the track.</param>
/// <param name="Edits">The edit list as stored, for readers that need the full mapping; null when the container has none.</param>
public readonly record struct TrackEditInfo(
    MediaTime Priming = default,
    MediaTime EndPadding = default,
    MediaTime StartTime = default,
    IReadOnlyList<EditSegment>? Edits = null);

/// <summary>
/// One track (stream) of a container as the neutral model sees it: immutable, built by the format reader when the file is opened.
/// </summary>
public sealed record MediaTrack
{
    /// <summary>Position of the track in the container's own order; the value <see cref="PacketInfo.TrackIndex"/> refers to.</summary>
    public required int Index { get; init; }

    /// <summary>The container's identifier for the track: an ISO track_ID, a Matroska TrackNumber, a transport-stream PID, an AVI stream number. Not necessarily unique across containers.</summary>
    public long Id { get; init; }

    /// <summary>What the track carries.</summary>
    public required TrackKind Kind { get; init; }

    /// <summary>The compression format, or <see cref="CodecId.Unknown"/>.</summary>
    public CodecId Codec { get; init; }

    /// <summary>The container's own name for the codec, for diagnostics and round-tripping: a sample-entry FourCC such as <c>avc1</c>, a Matroska CodecID such as <c>V_MPEG4/ISO/AVC</c>.</summary>
    public string? CodecTag { get; init; }

    /// <summary>The codec setup bytes the container stores.</summary>
    public CodecConfiguration Configuration { get; init; }

    /// <summary>Ticks per second of the track's own clock (the media timescale in ISO, 10^9 / TimestampScale in Matroska). Packet times of this track are expressed in it.</summary>
    public required long Timescale { get; init; }

    /// <summary>Track duration in presentation time, or <see cref="MediaTime.Unknown"/>.</summary>
    public MediaTime Duration { get; init; } = MediaTime.Unknown;

    /// <summary>Priming, end padding and start placement.</summary>
    public TrackEditInfo Edit { get; init; }

    /// <summary>ISO 639-2/T or BCP 47 language code as stored (for example <c>eng</c>, <c>und</c>), or null.</summary>
    public string? Language { get; init; }

    /// <summary>The track's title or handler name, or null.</summary>
    public string? Name { get; init; }

    /// <summary>Playback hints.</summary>
    public TrackDisposition Disposition { get; init; }

    /// <summary>Audio parameters; null for other kinds or when the container gives none.</summary>
    public AudioTrackInfo? Audio { get; init; }

    /// <summary>Video parameters; null for other kinds or when the container gives none.</summary>
    public VideoTrackInfo? Video { get; init; }

    /// <summary>Average bit rate in bits per second as declared, or 0.</summary>
    public long BitRate { get; init; }

    /// <summary>
    /// Describes the track's codec from its configuration (see <see cref="CodecParameterReader.TryDescribe(CodecId, CodecConfiguration, out CodecParameters?)"/>)
    /// and, for AAC, adds what only the container's declared rate can show: when the configuration is plain AAC-LC and the container declares
    /// twice its rate, <see cref="CodecParameters.SbrImplied"/> is set and <see cref="CodecParameters.SampleRate"/> is the declared rate.
    /// </summary>
    /// <returns>The description, or null when the codec has no configuration form here or the bytes do not parse. Never throws.</returns>
    public CodecParameters? DescribeCodec()
    {
        if (!CodecParameterReader.TryDescribe(Codec, Configuration, out CodecParameters? p) || p is null)
        {
            return null;
        }

        if (Codec == CodecId.Aac && !p.SbrPresent && p.CoreSampleRate > 0 && Audio is { } a && a.SampleRate == 2 * p.CoreSampleRate)
        {
            return p with { SbrImplied = true, SampleRate = a.SampleRate };
        }

        return p;
    }

    /// <summary>Number of packets (samples, frames) when the container's index says, else -1.</summary>
    public long PacketCount { get; init; } = -1;

    /// <summary>True when the track's samples are encrypted or protected by DRM. Metadata remains readable; packets are delivered as stored.</summary>
    public bool IsProtected { get; init; }
}
