using RecursiveAI.OneMedia.Codecs.Pcm;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>Which member of the ISO base media family a muxer writes: it decides the brands and the conventions of the sample entries.</summary>
public enum IsoFileKind
{
    /// <summary>MP4 (ISO/IEC 14496-14): brand <c>isom</c>.</summary>
    Mp4 = 0,

    /// <summary>MPEG-4 audio file (<c>M4A </c>): the same structure as MP4, with the iTunes-style brand.</summary>
    M4a,

    /// <summary>MPEG-4 video file (<c>M4V </c>).</summary>
    M4v,

    /// <summary>QuickTime movie: brand <c>qt  </c>, QuickTime handler and sound-description conventions, and the QuickTime PCM sample entries.</summary>
    Mov,

    /// <summary>3GPP file (3GPP TS 26.244): brand <c>3gp4</c>, or <c>3gp5</c> when the file holds H.264.</summary>
    ThreeGp,

    /// <summary>3GPP2 file: brand <c>3g2a</c>.</summary>
    ThreeG2,
}

/// <summary>Where the movie box goes and whether the file is fragmented.</summary>
public enum IsoLayout
{
    /// <summary>
    /// <c>ftyp</c>, <c>moov</c>, <c>mdat</c> (what streaming players want, "faststart"). The movie box is only known when the last packet
    /// has been seen, so the payload waits in a spool (memory up to a budget, then a temporary file) and is copied behind the movie box
    /// at <see cref="IMuxer.Finish"/>. Works with a destination that cannot seek.
    /// </summary>
    MoovFirst = 0,

    /// <summary>
    /// <c>ftyp</c>, <c>mdat</c>, <c>moov</c>. With a seekable destination the payload goes straight to it and the media data box size is
    /// patched at the end. With one that cannot seek the payload is spooled as for <see cref="MoovFirst"/>.
    /// </summary>
    MoovLast,

    /// <summary>
    /// A movie box with no samples and <c>mvex</c>, then one <c>moof</c> + <c>mdat</c> pair per fragment. Only the packets of the
    /// current fragment are buffered, so this layout streams to a destination that cannot seek (live output).
    /// </summary>
    Fragmented,
}

/// <summary>Settings of an <see cref="IsoMuxer"/>.</summary>
public sealed record IsoMuxOptions
{
    /// <summary>Which flavour of the family to write. Default <see cref="IsoFileKind.Mp4"/>.</summary>
    public IsoFileKind Kind { get; init; } = IsoFileKind.Mp4;

    /// <summary>Where the movie box goes, or fragmented. Default <see cref="IsoLayout.MoovFirst"/>.</summary>
    public IsoLayout Layout { get; init; } = IsoLayout.MoovFirst;

    /// <summary>
    /// Fragmented layout only: write a CMAF header and fragments (ISO/IEC 23000-19): major brand <c>iso6</c> with <c>cmfc</c> and
    /// <c>iso6</c> among the compatible brands, exactly one track, 64-bit <c>tfdt</c>, a single <c>trun</c> per <c>moof</c> and
    /// default-base-is-moof addressing. Any other number of tracks is refused. Default false.
    /// </summary>
    public bool Cmaf { get; init; }

    /// <summary>The major brand to write instead of the one the flavour picks, four characters; null for the flavour's.</summary>
    public string? MajorBrand { get; init; }

    /// <summary>The compatible brands to write instead of the flavour's, each four characters; null for the flavour's. The major brand is always included.</summary>
    public IReadOnlyList<string>? CompatibleBrands { get; init; }

    /// <summary>Minor version of the file type box. Default 512 for MP4-family files, 0 for QuickTime.</summary>
    public uint? MinorVersion { get; init; }

    /// <summary>
    /// Ticks per second of the movie header. 0 (the default) picks the timescale of the first edit list segment the tracks bring (so a
    /// copy keeps its edit durations exact) and otherwise 1000.
    /// </summary>
    public long MovieTimescale { get; init; }

    /// <summary>Creation and modification time written to the movie, track and media headers; null (the default) writes zero, so the output is a pure function of the input.</summary>
    public DateTimeOffset? CreationTime { get; init; }

    /// <summary>
    /// Write sample sizes as <c>stz2</c> with 8 or 16-bit fields when every size fits (it saves 2 bytes per sample over <c>stsz</c>). QuickTime
    /// does not read <c>stz2</c>, so this is off by default and ignored for <see cref="IsoFileKind.Mov"/>.
    /// </summary>
    public bool CompactSampleSizes { get; init; }

    /// <summary>Limits applied to what the muxer accepts: tracks, sample-table entries and packet size. Default <see cref="MediaLimits.Default"/>.</summary>
    public MediaLimits Limits { get; init; } = MediaLimits.Default;

    /// <summary>True to dispose the destination stream when the muxer is disposed after <see cref="IMuxer.Finish"/>. Default false.</summary>
    public bool OwnsOutput { get; init; }

    /// <summary>
    /// Payload bytes kept in memory by the spool of <see cref="IsoLayout.MoovFirst"/> (and of <see cref="IsoLayout.MoovLast"/> on a destination
    /// that cannot seek) before it moves to a stream from <see cref="SpoolFactory"/>. Default 16 MiB.
    /// </summary>
    public long SpoolMemoryBudget { get; init; } = 16L << 20;

    /// <summary>
    /// Makes the stream the spool overflows into: read-write, seekable, empty. Disposed by the muxer. Null (the default) creates a temporary
    /// file that is deleted when closed.
    /// </summary>
    public Func<Stream>? SpoolFactory { get; init; }

    /// <summary>Fragmented layout: the length a fragment should reach before the next keyframe of the reference track starts a new one. Default two seconds.</summary>
    public MediaTime FragmentDuration { get; init; } = new(2, 1);

    /// <summary>
    /// Fragmented layout: payload bytes of one fragment at which a fragment ends at the next packet whether or not it is a keyframe, so a long
    /// group of pictures cannot grow the buffer without bound. Default 64 MiB.
    /// </summary>
    public long MaxFragmentBytes { get; init; } = 64L << 20;

    /// <summary>
    /// Fragmented layout: write a segment index (<c>sidx</c>) in front of the first fragment, one reference per fragment, for the reference
    /// track (the first video track, else the first track). The index needs every fragment's size, so the fragments are spooled and the
    /// output is written at <see cref="IMuxer.Finish"/>; use it for files, not for live output. Default false.
    /// </summary>
    public bool WriteSegmentIndex { get; init; }

    /// <summary>Fragmented layout: write a movie fragment random access box (<c>mfra</c>) at the end, with an entry for each fragment that starts at a random-access point. Default false.</summary>
    public bool WriteRandomAccess { get; init; }
}

/// <summary>Per-track settings of an <see cref="IsoMuxer"/>.</summary>
public sealed record IsoTrackOptions
{
    /// <summary>The track identifier to write; 0 (the default) uses <see cref="MediaTrack.Id"/> when it is positive, fits 32 bits and is not taken, else the next free one.</summary>
    public uint TrackId { get; init; }

    /// <summary>
    /// The layout of the samples of a PCM track, when <see cref="MediaTrack.CodecTag"/> and the audio parameters do not say it (a WAVE
    /// <c>fmt </c> chunk gives it as little-endian signed integers at the declared width, which is also the fallback).
    /// </summary>
    public PcmSampleFormat? Pcm { get; init; }

    /// <summary>The handler type to write (<c>vide</c>, <c>soun</c>, <c>text</c>, <c>sbtl</c>, <c>subt</c>, <c>meta</c>…) instead of the one the track kind and codec imply; four characters.</summary>
    public string? HandlerType { get; init; }

    /// <summary>
    /// Complete boxes to append inside the sample entry after the codec configuration box: <c>colr</c>, <c>clap</c>, <c>fiel</c>, <c>btrt</c>,
    /// <c>chnl</c> and the like that the caller carries over from a source file. Written as given.
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>>? SampleEntryBoxes { get; init; }

    /// <summary>Complete boxes to append inside the <c>trak</c> after the media box: <c>tref</c>, <c>udta</c> and other track-level boxes the caller carries over. Written as given.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>>? TrackBoxes { get; init; }
}
