namespace RecursiveAI.OneMedia.Matroska;

/// <summary>Which Matroska document type a <see cref="MatroskaMuxer"/> writes.</summary>
public enum MatroskaProfile
{
    /// <summary>General Matroska (<c>DocType</c> <c>matroska</c>): any codec the muxer can name and configure.</summary>
    Matroska = 0,

    /// <summary>
    /// WebM (<c>DocType</c> <c>webm</c>): only VP8, VP9, AV1, Vorbis and Opus. A track with any other codec is refused with
    /// <see cref="MuxRefusalReason.UnsupportedCodec"/>.
    /// </summary>
    WebM,
}

/// <summary>How a <see cref="MatroskaMuxer"/> lays the file out. Every property has a default that is safe for general use.</summary>
public sealed record MatroskaMuxOptions
{
    /// <summary>Matroska or WebM. Default <see cref="MatroskaProfile.Matroska"/>.</summary>
    public MatroskaProfile Profile { get; init; } = MatroskaProfile.Matroska;

    /// <summary>
    /// Live (forward-only) mode, default false. The output may be a stream that cannot seek: the Segment and every Cluster get an
    /// unknown size (RFC 8794 §6.2), blocks are written the moment they arrive, and no Cues, Duration or later patch of earlier bytes
    /// is written. Without it the output must be seekable, clusters are sized, and Cues, Duration and the SeekHead are written when
    /// <see cref="MatroskaMuxer.Finish(CancellationToken)"/> runs.
    /// </summary>
    public bool Live { get; init; }

    /// <summary>
    /// Nanoseconds per timestamp unit (RFC 9559 §5.1.2.1), which must divide 1,000,000,000. Packet times are rounded to the nearest
    /// unit; a finer scale keeps more of the source's precision but shortens how far a Cluster can reach (block offsets are 16-bit),
    /// so clusters get shorter. Null (the default) picks one millisecond, the value players and the WebM guidelines expect, unless
    /// every track's time base is the same whole number of ticks per second, finer than a millisecond but not finer than 100 µs (as in
    /// a Matroska file with such a TimestampScale), in which case that time base is used and the packet times survive exactly.
    /// </summary>
    public long? TimestampScale { get; init; }

    /// <summary>
    /// A Cluster is closed at the next video key frame (or, with no video, the next packet of the first track) once it is at least
    /// this long. Default 2 s. A longer group of pictures makes a longer Cluster, so each Cluster starts on a key frame; the Cluster is
    /// cut inside a group of pictures only when block offsets would overflow their 16-bit range (about 32 s at one-millisecond units)
    /// or the byte bound below is passed fourfold.
    /// </summary>
    public MediaTime MinClusterDuration { get; init; } = new(2, 1);

    /// <summary>
    /// A Cluster is closed at the next video key frame once its payload has reached this many bytes, default 4 MiB, and inside a group
    /// of pictures at four times this. In seekable mode that bounds the memory the muxer holds (plus one packet).
    /// </summary>
    public int MaxClusterBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Write Cues for video key frames (or Cluster starts when there is no video), default true. Ignored in live mode.</summary>
    public bool WriteCues { get; init; } = true;

    /// <summary>
    /// Put a CRC-32 element first in the SeekHead, Info, Tracks, Cues and every Cluster (RFC 8794 §11.3.1), default false. Not used in
    /// live mode, where a Cluster's size is not known before its content is written.
    /// </summary>
    public bool Crc32 { get; init; }

    /// <summary>
    /// Bytes of <c>Void</c> (RFC 8794 §11.2.6) left after the Tracks, default 2048, so tags can later be added in place without moving
    /// the media. Zero leaves none.
    /// </summary>
    public int ReserveBytes { get; init; } = 2048;

    /// <summary>
    /// The Segment's duration when the packets do not show it (a track whose packets carry no duration ends one packet short). The
    /// file states the larger of this and the end of the last packet. Null (default) states the end of the last packet.
    /// </summary>
    public MediaTime? Duration { get; init; }

    /// <summary>The 16-byte SegmentUUID, not all zero (RFC 9559 §5.1.2.2). Null (default) picks a random one. Set it for reproducible output.</summary>
    public ReadOnlyMemory<byte>? SegmentUuid { get; init; }

    /// <summary>The Segment's <c>DateUTC</c> in nanoseconds since 2001-01-01T00:00:00 UTC, or null (default) to write none.</summary>
    public long? DateUtcNanoseconds { get; init; }

    /// <summary>The Segment's <c>Title</c>, or null for none.</summary>
    public string? Title { get; init; }

    /// <summary>The MuxingApp and WritingApp string. Default <c>RecursiveAI.OneMedia</c>.</summary>
    public string WritingApp { get; init; } = "RecursiveAI.OneMedia";
}
