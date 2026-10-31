namespace RecursiveAI.OneMedia.Matroska;

/// <summary>The EBML header of a file (RFC 8794 §11.2.1): the EBML version fields and the document type.</summary>
public sealed class MatroskaHeader
{
    /// <summary>EBMLVersion; 1 when absent.</summary>
    public long EbmlVersion { get; init; } = 1;

    /// <summary>EBMLReadVersion; 1 when absent. A reader must refuse files that need a newer EBML.</summary>
    public long EbmlReadVersion { get; init; } = 1;

    /// <summary>EBMLMaxIDLength; 4 when absent.</summary>
    public long MaxIdLength { get; init; } = 4;

    /// <summary>EBMLMaxSizeLength; 8 when absent.</summary>
    public long MaxSizeLength { get; init; } = 8;

    /// <summary>DocType, usually <c>matroska</c> or <c>webm</c>; empty when absent.</summary>
    public string DocType { get; init; } = string.Empty;

    /// <summary>DocTypeVersion: the highest document version used; 1 when absent.</summary>
    public long DocTypeVersion { get; init; } = 1;

    /// <summary>DocTypeReadVersion: the lowest document version a reader needs; 1 when absent.</summary>
    public long DocTypeReadVersion { get; init; } = 1;

    /// <summary>True when <see cref="DocType"/> is <c>webm</c>.</summary>
    public bool IsWebM => DocType == "webm";
}

/// <summary>The Segment's Info element (RFC 9559 §5.1.2).</summary>
public sealed class MatroskaInfo
{
    /// <summary>TimestampScale: nanoseconds per timestamp tick; 1,000,000 when absent.</summary>
    public long TimestampScale { get; init; } = 1_000_000;

    /// <summary>The Segment duration, or <see cref="MediaTime.Unknown"/> when Info has none (live recordings).</summary>
    public MediaTime Duration { get; init; } = MediaTime.Unknown;

    /// <summary>DateUTC, when present and in range.</summary>
    public DateTimeOffset? DateUtc { get; init; }

    /// <summary>Title, or null.</summary>
    public string? Title { get; init; }

    /// <summary>MuxingApp, or null.</summary>
    public string? MuxingApp { get; init; }

    /// <summary>WritingApp, or null.</summary>
    public string? WritingApp { get; init; }

    /// <summary>SegmentUUID bytes, empty when absent.</summary>
    public ReadOnlyMemory<byte> SegmentUuid { get; init; }
}

/// <summary>A Video element of a track entry (RFC 9559 §5.1.4.1.28).</summary>
/// <param name="PixelWidth">PixelWidth.</param>
/// <param name="PixelHeight">PixelHeight.</param>
/// <param name="DisplayWidth">DisplayWidth, or 0 when absent.</param>
/// <param name="DisplayHeight">DisplayHeight, or 0 when absent.</param>
/// <param name="DisplayUnit">DisplayUnit: 0 pixels, 1 centimetres, 2 inches, 3 display aspect ratio, 4 unknown.</param>
/// <param name="Interlaced">FlagInterlaced: 0 undetermined, 1 interlaced, 2 progressive.</param>
/// <param name="StereoMode">StereoMode, 0 when absent.</param>
public readonly record struct MatroskaVideo(long PixelWidth, long PixelHeight, long DisplayWidth, long DisplayHeight, long DisplayUnit, long Interlaced, long StereoMode);

/// <summary>An Audio element of a track entry (RFC 9559 §5.1.4.1.29).</summary>
/// <param name="SamplingFrequency">SamplingFrequency in hertz, rounded to an integer; 8000 when absent.</param>
/// <param name="OutputSamplingFrequency">OutputSamplingFrequency in hertz, rounded; equals <paramref name="SamplingFrequency"/> when absent.</param>
/// <param name="Channels">Channels; 1 when absent.</param>
/// <param name="BitDepth">BitDepth; 0 when absent.</param>
public readonly record struct MatroskaAudio(long SamplingFrequency, long OutputSamplingFrequency, long Channels, long BitDepth);

/// <summary>One ContentEncoding of a track (RFC 9559 §5.1.4.1.31).</summary>
/// <param name="Order">ContentEncodingOrder.</param>
/// <param name="Scope">ContentEncodingScope bit set: 1 frame contents, 2 CodecPrivate, 4 next encoding.</param>
/// <param name="Type">ContentEncodingType: 0 compression, 1 encryption.</param>
/// <param name="CompressionAlgorithm">ContentCompAlgo (0 zlib, 1 bzlib, 2 lzo1x, 3 header stripping) when <paramref name="Type"/> is 0; -1 otherwise.</param>
/// <param name="CompressionSettings">ContentCompSettings: for header stripping, the bytes to put back in front of every frame.</param>
public readonly record struct MatroskaContentEncoding(long Order, long Scope, long Type, long CompressionAlgorithm, ReadOnlyMemory<byte> CompressionSettings);

/// <summary>One TrackEntry (RFC 9559 §5.1.4.1), as stored; the neutral <see cref="MediaTrack"/> is derived from it.</summary>
public sealed class MatroskaTrackEntry
{
    /// <summary>Byte offset of the TrackEntry element.</summary>
    public long Offset { get; init; }

    /// <summary>TrackNumber, the number Blocks use to name the track.</summary>
    public long TrackNumber { get; init; }

    /// <summary>TrackUID, the random 64-bit identifier reinterpreted as a signed number.</summary>
    public long TrackUid { get; init; }

    /// <summary>TrackType: 1 video, 2 audio, 3 complex, 16 logo, 17 subtitle, 18 buttons, 32 control, 33 metadata.</summary>
    public long TrackType { get; init; }

    /// <summary>FlagEnabled; true when absent.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>FlagDefault; true when absent.</summary>
    public bool Default { get; init; } = true;

    /// <summary>FlagForced; false when absent.</summary>
    public bool Forced { get; init; }

    /// <summary>FlagHearingImpaired.</summary>
    public bool HearingImpaired { get; init; }

    /// <summary>FlagVisualImpaired.</summary>
    public bool VisualImpaired { get; init; }

    /// <summary>FlagOriginal.</summary>
    public bool Original { get; init; }

    /// <summary>FlagCommentary.</summary>
    public bool Commentary { get; init; }

    /// <summary>FlagLacing; true when absent.</summary>
    public bool Lacing { get; init; } = true;

    /// <summary>DefaultDuration in nanoseconds, 0 when absent.</summary>
    public long DefaultDurationNs { get; init; }

    /// <summary>The Matroska CodecID, such as <c>V_MPEG4/ISO/AVC</c>; empty when absent.</summary>
    public string CodecId { get; init; } = string.Empty;

    /// <summary>CodecPrivate bytes, empty when absent.</summary>
    public ReadOnlyMemory<byte> CodecPrivate { get; init; }

    /// <summary>CodecName, or null.</summary>
    public string? CodecName { get; init; }

    /// <summary>The track's Name, or null.</summary>
    public string? Name { get; init; }

    /// <summary>Language, an ISO 639-2 code; <c>eng</c> when absent.</summary>
    public string Language { get; init; } = "eng";

    /// <summary>LanguageBCP47, or null when absent.</summary>
    public string? LanguageBcp47 { get; init; }

    /// <summary>CodecDelay in nanoseconds: decoder output to drop at the start (Opus pre-skip and similar); 0 when absent.</summary>
    public long CodecDelayNs { get; init; }

    /// <summary>SeekPreRoll in nanoseconds: how long before a seek point decoding has to start; 0 when absent.</summary>
    public long SeekPreRollNs { get; init; }

    /// <summary>The Video element, or null.</summary>
    public MatroskaVideo? Video { get; init; }

    /// <summary>The Audio element, or null.</summary>
    public MatroskaAudio? Audio { get; init; }

    /// <summary>The ContentEncodings in file order.</summary>
    public IReadOnlyList<MatroskaContentEncoding> ContentEncodings { get; init; } = [];

    /// <summary>
    /// The bytes that header stripping (ContentCompAlgo 3) removed from the front of every frame, which the demuxer puts back;
    /// empty when the track does not use it.
    /// </summary>
    public ReadOnlyMemory<byte> HeaderStripPrefix { get; init; }

    /// <summary>True when a frame-level compression other than header stripping is declared (zlib, bzlib, lzo1x); the frames are then delivered as stored.</summary>
    public bool HasUnsupportedFrameCompression { get; init; }

    /// <summary>True when a ContentEncryption element is present.</summary>
    public bool IsEncrypted { get; init; }
}

/// <summary>One entry of a SeekHead (RFC 9559 §5.1.1.1).</summary>
/// <param name="Id">SeekID: the ID of the element it points at.</param>
/// <param name="Position">SeekPosition: the offset from the first byte of the Segment's data.</param>
public readonly record struct MatroskaSeekEntry(uint Id, long Position);

/// <summary>One CuePoint's position for one track (RFC 9559 §5.1.5).</summary>
/// <param name="Time">CueTime in timestamp ticks.</param>
/// <param name="Track">CueTrack, a TrackNumber.</param>
/// <param name="ClusterPosition">CueClusterPosition: the Cluster's offset from the first byte of the Segment's data.</param>
/// <param name="RelativePosition">CueRelativePosition: the Block's offset inside the Cluster's data, or -1 when absent.</param>
public readonly record struct MatroskaCuePoint(long Time, long Track, long ClusterPosition, long RelativePosition);
