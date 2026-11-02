namespace RecursiveAI.OneMedia.Remuxing;

/// <summary>The container a remux writes.</summary>
public enum RemuxFormat
{
    /// <summary>MP4 (<c>isom</c> brands), one <c>moov</c> and one <c>mdat</c>.</summary>
    Mp4 = 0,

    /// <summary>MP4 audio (<c>M4A </c>).</summary>
    M4a,

    /// <summary>MP4 video (<c>M4V </c>).</summary>
    M4v,

    /// <summary>QuickTime movie (<c>qt  </c>).</summary>
    Mov,

    /// <summary>3GPP (<c>3gp4</c>, or <c>3gp5</c> with H.264).</summary>
    ThreeGp,

    /// <summary>3GPP2 (<c>3g2a</c>).</summary>
    ThreeG2,

    /// <summary>Fragmented MP4: <c>moov</c> with <c>mvex</c>, then <c>moof</c> + <c>mdat</c> pairs.</summary>
    FragmentedMp4,

    /// <summary>A CMAF track file: fragmented MP4 with exactly one track.</summary>
    Cmaf,

    /// <summary>Matroska (<c>.mkv</c>, <c>.mka</c>).</summary>
    Matroska,

    /// <summary>WebM, the Matroska subset for VP8, VP9, AV1, Vorbis and Opus.</summary>
    WebM,
}

/// <summary>Receives each packet exactly as it is handed to the muxer: output track index, times after any shift, and the payload after repackaging.</summary>
/// <param name="packet">The packet; <see cref="PacketInfo.TrackIndex"/> is the output index.</param>
/// <param name="payload">The bytes written. Valid only during the call.</param>
public delegate void RemuxPacketObserver(in PacketInfo packet, ReadOnlySpan<byte> payload);

/// <summary>Changes to one input track in a remux. A null member leaves the input's value.</summary>
public sealed record TrackEdit
{
    /// <summary>True to leave the track out. It is reported as <see cref="LossReason.Deselected"/>.</summary>
    public bool Drop { get; init; }

    /// <summary>Language code (ISO 639-2/T, three lowercase letters, for MP4/MOV/3GP).</summary>
    public string? Language { get; init; }

    /// <summary>Track name (the handler name in ISO files, the track name in Matroska).</summary>
    public string? Name { get; init; }

    /// <summary>Disposition flags to write instead of the input's.</summary>
    public TrackDisposition? Disposition { get; init; }

    /// <summary>Clockwise display rotation for a video track: 0, 90, 180 or 270. The packets are not touched; only the flag changes.</summary>
    public int? RotationDegrees { get; init; }
}

/// <summary>Settings for a remux. Everything has a default; <c>new RemuxOptions()</c> copies every track the target can hold.</summary>
public sealed record RemuxOptions
{
    /// <summary>
    /// For the progressive MP4 family: true (default) writes <c>moov</c> before <c>mdat</c> ("faststart"), spooling the payload through memory and a
    /// temporary file; false writes <c>mdat</c> first and needs no spool on a seekable output. Ignored by fragmented and Matroska output.
    /// </summary>
    public bool Faststart { get; init; } = true;

    /// <summary>Chooses tracks. Called once per input track; false leaves the track out (<see cref="LossReason.Deselected"/>). Null keeps all.</summary>
    public Func<MediaTrack, bool>? Select { get; init; }

    /// <summary>Edits by input track index.</summary>
    public IReadOnlyDictionary<int, TrackEdit>? Edits { get; init; }

    /// <summary>
    /// True (default): a track the target cannot carry is left out and reported in <see cref="RemuxReport.Losses"/>. False: the first such track
    /// throws the muxer's <see cref="MuxRefusedException"/> and nothing is written.
    /// </summary>
    public bool AllowDrop { get; init; } = true;

    /// <summary>Limits passed to the muxer.</summary>
    public MediaLimits Limits { get; init; } = MediaLimits.Default;

    /// <summary>
    /// Most bytes of packets held back while a track's codec configuration is derived from the stream (Annex B video without a stored
    /// parameter-set record, ADTS audio without a stored AudioSpecificConfig). A track that has no configuration by then is left out as
    /// <see cref="LossReason.MissingConfiguration"/>. Default 32 MiB.
    /// </summary>
    public long ConfigurationProbeBytes { get; init; } = 32L << 20;

    /// <summary>Creation and modification time to write; null writes zero, so the output is a pure function of the input.</summary>
    public DateTimeOffset? CreationTime { get; init; }

    /// <summary>Called for every packet just before it is written; for progress, hashing or checks. Must not retain the payload span. Null for none.</summary>
    public RemuxPacketObserver? Observer { get; init; }

    /// <summary>For the path overloads: replace an existing output file (default false: refuse).</summary>
    public bool Overwrite { get; init; }

    /// <summary>The timescale of the movie header for the ISO targets, in ticks per second; 0 (the default) lets the muxer choose. Edit list durations are stated in it.</summary>
    public long MovieTimescale { get; init; }

    /// <summary>
    /// For the path overloads: after the remux, write the input's tags and pictures into the output through the common view (default true). The stream overloads
    /// never do; their report lists every tag as not written. Chapters are not carried.
    /// </summary>
    public bool CarryTags { get; init; } = true;
}
