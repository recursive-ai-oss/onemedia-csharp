using System.Globalization;
using RecursiveAI.OneMedia.Captions;

namespace RecursiveAI.OneMedia.Streaming;

/// <summary>The manifest formats a <see cref="Packager"/> writes.</summary>
[Flags]
public enum PackagingFormats
{
    /// <summary>No manifest: segments only.</summary>
    None = 0,

    /// <summary>HLS: <c>master.m3u8</c> and one media playlist per track.</summary>
    Hls = 1,

    /// <summary>DASH: <c>manifest.mpd</c>.</summary>
    Dash = 2,

    /// <summary>Both, sharing the same segments.</summary>
    Both = Hls | Dash,
}

/// <summary>The container of the media segments.</summary>
public enum SegmentFormat
{
    /// <summary>Fragmented MP4 in the CMAF profile (ISO/IEC 23000-19): one init segment and a segment series per track, for HLS and DASH.</summary>
    Cmaf = 0,

    /// <summary>MPEG transport stream segments (RFC 8216 §3.2): audio and video multiplexed into one stream per rendition. HLS only; DASH is left out with a note.</summary>
    MpegTs,
}

/// <summary>Settings of live packaging: segments are published as they complete and old ones leave the playlists.</summary>
public sealed record LiveOptions
{
    /// <summary>The time the presentation started, written as the DASH <c>availabilityStartTime</c> and the base of the HLS program date times. Required: the library never reads the clock.</summary>
    public required DateTimeOffset StartTime { get; init; }

    /// <summary>How many segments the playlists list. Default 6. The oldest leaves the playlist when a new one arrives (HLS <c>EXT-X-MEDIA-SEQUENCE</c> rises; DASH <c>timeShiftBufferDepth</c> is the window).</summary>
    public int WindowSegments { get; init; } = 6;

    /// <summary>Delete a segment's file once it has left the window and <see cref="ExpiredSegmentsKept"/> further segments have come. Default true.</summary>
    public bool DeleteExpired { get; init; } = true;

    /// <summary>How many segments past the window stay on disk before deletion, so a client that is a little behind can finish. Default 3 (RFC 8216 §6.2.2 asks servers to keep a removed segment available for at least three target durations).</summary>
    public int ExpiredSegmentsKept { get; init; } = 3;

    /// <summary>Write <c>EXT-X-PROGRAM-DATE-TIME</c> on every segment of the HLS playlists, counted from <see cref="StartTime"/>. Default true.</summary>
    public bool ProgramDateTime { get; init; } = true;
}

/// <summary>A subtitle track to add to the presentation: HLS gets WebVTT segments, DASH a WebVTT file.</summary>
/// <param name="Document">The cues.</param>
/// <param name="Language">BCP 47 language, or null.</param>
/// <param name="Name">A display name, or null.</param>
public sealed record PackagingSubtitle(SubtitleDocument Document, string? Language = null, string? Name = null)
{
    /// <summary>Mark as the default rendition. Default false.</summary>
    public bool Default { get; init; }

    /// <summary>Mark as forced narrative. Default false.</summary>
    public bool Forced { get; init; }
}

/// <summary>A segment the packager has written.</summary>
/// <param name="Path">The file, relative to the output directory, with <c>/</c> separators. In single-file mode every segment of a track names the one file.</param>
/// <param name="Track">The track.</param>
/// <param name="Segment">The segment's timing.</param>
/// <param name="Bytes">Its size in bytes.</param>
public sealed record PackagedSegment(string Path, SegmentedTrack Track, SegmentInfo Segment, long Bytes);

/// <summary>Settings of a <see cref="Packager"/>.</summary>
public sealed record PackagingOptions
{
    /// <summary>The manifests to write. Default <see cref="PackagingFormats.Both"/>.</summary>
    public PackagingFormats Formats { get; init; } = PackagingFormats.Both;

    /// <summary>The container of the media segments. Default <see cref="SegmentFormat.Cmaf"/>. <see cref="SegmentFormat.MpegTs"/> writes HLS only, one multiplexed stream per input (a bit-rate ladder gives each input its own audio), a <c>.ts</c> file per segment or, with <see cref="SingleFile"/>, one <c>media.ts</c> with byte ranges.</summary>
    public SegmentFormat SegmentFormat { get; init; }

    /// <summary>The target segment duration; see <see cref="SegmenterOptions.SegmentDuration"/>. Default six seconds.</summary>
    public MediaTime SegmentDuration { get; init; } = new(6, 1);

    /// <summary>
    /// Write one media file per track (init segment, a <c>sidx</c>, then the fragments) instead of a file per segment: HLS playlists use byte ranges, DASH uses the
    /// on-demand profile (<c>SegmentBase</c> with the index range). Video on demand only; the segments of a track are spooled in a temporary file until the track ends.
    /// Default false.
    /// </summary>
    public bool SingleFile { get; init; }

    /// <summary>Live mode, or null (the default) for video on demand. Live packaging takes one input, ends with <c>EXT-X-ENDLIST</c> / a static MPD when the input ends, and works on a forward-only stream.</summary>
    public LiveOptions? Live { get; init; }

    /// <summary>Subtitle tracks to add besides the ones the input's text tracks supply. Video on demand only.</summary>
    public IReadOnlyList<PackagingSubtitle> Subtitles { get; init; } = [];

    /// <summary>Also package the input's own text tracks (tx3g, WebVTT, SRT in Matroska, and the like) as subtitle renditions. Default true; a source that cannot be read twice skips them with a note.</summary>
    public bool IncludeContainerSubtitles { get; init; } = true;

    /// <summary>Timed events to announce: DASH gets an <c>EventStream</c> in the period, HLS an <c>EXT-X-DATERANGE</c> on the segment the event falls in (in every media playlist, as RFC 8216 §4.4.5.1 asks). Default none.</summary>
    public IReadOnlyList<PackagingEvent> Events { get; init; } = [];

    /// <summary>Also announce the <c>emsg</c> events found in the first input (video on demand only). Default true.</summary>
    public bool IncludeSourceEvents { get; init; } = true;

    /// <summary>Replace an existing manifest in the directory. Default false: a directory that already holds the manifest is refused.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Limits for the muxers and manifest builders. Default <see cref="MediaLimits.Default"/>.</summary>
    public MediaLimits Limits { get; init; } = MediaLimits.Default;

    /// <summary>The value of <c>minBufferTime</c> in the MPD. Default two seconds.</summary>
    public MediaTime MinBufferTime { get; init; } = new(2, 1);

    /// <summary>Called after each media segment is written (every segment, as it completes in live mode).</summary>
    public Action<PackagedSegment>? SegmentWritten { get; init; }

    /// <summary>Called after a manifest (a playlist or the MPD) has been written, with its path relative to the output directory. In live mode this follows every segment.</summary>
    public Action<string>? ManifestWritten { get; init; }
}

/// <summary>What the packager made for one track.</summary>
/// <param name="Name">The track's directory name (<c>video0</c>, <c>audio0</c>, <c>text0</c>).</param>
/// <param name="Kind">Video, audio or subtitles.</param>
/// <param name="Codecs">The RFC 6381 codec string, or null.</param>
/// <param name="Segments">Number of media segments written.</param>
/// <param name="Bytes">Bytes of media segments written.</param>
/// <param name="DurationSeconds">The track's length in seconds as the sum of its segment durations (for display).</param>
public sealed record PackagedTrack(string Name, TrackKind Kind, string? Codecs, long Segments, long Bytes, double DurationSeconds);

/// <summary>What a <see cref="Packager"/> run produced.</summary>
/// <param name="Directory">The output directory.</param>
/// <param name="Tracks">The tracks written.</param>
/// <param name="Manifests">The manifests written, relative to the directory.</param>
/// <param name="Losses">What did not carry over from the inputs.</param>
/// <param name="Notes">Things worth knowing: tracks left out and why, ladder renditions whose segment boundaries differ, subtitles that could not be read.</param>
public sealed record PackagingReport(string Directory, IReadOnlyList<PackagedTrack> Tracks, IReadOnlyList<string> Manifests, IReadOnlyList<Remuxing.RemuxLoss> Losses, IReadOnlyList<string> Notes);

/// <summary>
/// Packages media for adaptive streaming: cuts CMAF segments with a <see cref="CmafSegmenter"/> and writes HLS playlists (RFC 8216) and a DASH MPD
/// (ISO/IEC 23009-1, DASH-IF interoperability guidelines) beside them. Video on demand writes the whole presentation; live mode publishes each segment and
/// playlist as it completes and keeps memory constant.
/// </summary>
/// <remarks>
/// <para>Layout of the output directory: <c>master.m3u8</c> and <c>manifest.mpd</c> at the top; one directory per track (<c>video0</c>, <c>audio0</c>, <c>text0</c>) holding
/// <c>init.mp4</c>, <c>seg-00001.m4s</c>… and <c>index.m3u8</c> (or <c>media.mp4</c> in single-file mode; subtitles are <c>seg-00001.vtt</c> and, for DASH, <c>subs.vtt</c>).</para>
/// <para>Several inputs make a bit-rate ladder: the first input supplies every track, each further input its first video track as another rendition. Renditions
/// switch cleanly only when their keyframes coincide; the report says when segment boundaries differ.</para>
/// </remarks>
public static class Packager
{
    /// <summary>Packages one file.</summary>
    /// <param name="input">The input; recognised by content.</param>
    /// <param name="directory">The output directory; created if needed.</param>
    /// <param name="options">Settings; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the run; what was written stays.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="UnsupportedMediaException">The input is not recognised.</exception>
    /// <exception cref="MuxRefusedException">No track can be carried.</exception>
    /// <exception cref="IOException">The directory already holds a manifest and <see cref="PackagingOptions.Overwrite"/> is false, or a file cannot be written.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static PackagingReport Package(string input, string directory, PackagingOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Package([input], directory, options, cancellationToken);
    }

    /// <summary>Packages several files as a bit-rate ladder (see the type remarks).</summary>
    /// <param name="inputs">The inputs, the one with the audio first. Live mode takes exactly one.</param>
    /// <param name="directory">The output directory; created if needed.</param>
    /// <param name="options">Settings; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="ArgumentException">No input, or several inputs in live mode.</exception>
    /// <exception cref="UnsupportedMediaException">An input is not recognised.</exception>
    /// <exception cref="MuxRefusedException">No track can be carried.</exception>
    /// <exception cref="IOException">As for <see cref="Package(string, string, PackagingOptions?, CancellationToken)"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static PackagingReport Package(IReadOnlyList<string> inputs, string directory, PackagingOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            throw new ArgumentException("At least one input is needed.", nameof(inputs));
        }

        using PackagingRun run = new(directory, options ?? new PackagingOptions(), inputs.Count);
        foreach (string path in inputs)
        {
            using MediaFile file = Media.Open(path);
            run.AddInput(file, cancellationToken);
        }

        return run.Finish(cancellationToken);
    }

    /// <summary>Packages an open file. A file opened from a stream that cannot seek works in live mode.</summary>
    /// <param name="input">The opened file; it is read once and left open.</param>
    /// <param name="directory">The output directory; created if needed.</param>
    /// <param name="options">Settings; defaults when null.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="MuxRefusedException">No track can be carried.</exception>
    /// <exception cref="IOException">As for <see cref="Package(string, string, PackagingOptions?, CancellationToken)"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static PackagingReport Package(MediaFile input, string directory, PackagingOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        using PackagingRun run = new(directory, options ?? new PackagingOptions(), 1);
        run.AddInput(input, cancellationToken);
        return run.Finish(cancellationToken);
    }

    /// <summary>The WebVTT of one HLS subtitle segment: the cues that overlap the span, with the <c>X-TIMESTAMP-MAP</c> header RFC 8216 §3.5 asks for.</summary>
    /// <param name="document">All cues of the track.</param>
    /// <param name="start">The start of the segment on the presentation timeline.</param>
    /// <param name="end">The end of the segment; <see cref="MediaTime.Unknown"/> for the last segment, which takes every later cue.</param>
    /// <returns>The segment's text. Cue times are the cues' own (presentation times), mapped by <c>MPEGTS:0,LOCAL:00:00:00.000</c>.</returns>
    public static string WebVttSegment(SubtitleDocument document, MediaTime start, MediaTime end)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<SubtitleCue> cues = [];
        foreach (SubtitleCue cue in document.Cues)
        {
            // A cue belongs to every segment it overlaps (RFC 8216 §3.5), so a player that joins at a segment sees it.
            if (cue.End > start && (!end.IsKnown || cue.Start < end))
            {
                cues.Add(cue);
            }
        }

        string text = SubtitleFormats.WebVtt.Write(document with { Cues = new SubtitleList<SubtitleCue>(cues) }).Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        int firstLine = text.IndexOf('\n', StringComparison.Ordinal);
        string map = string.Create(CultureInfo.InvariantCulture, $"X-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000");
        return firstLine < 0 ? text + "\n" + map + "\n" : string.Concat(text.AsSpan(0, firstLine + 1), map, "\n", text.AsSpan(firstLine + 1));
    }
}
