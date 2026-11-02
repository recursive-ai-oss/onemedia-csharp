using System.Buffers;

namespace RecursiveAI.OneMedia.Captions;

/// <summary>What a format-specific text codec does with one packet; internal seam between the public decoder and the per-format mappings.</summary>
internal interface ITextTrackDecoder
{
    /// <summary>Takes one packet. <paramref name="start"/> is the presentation time on the file's timeline, <paramref name="duration"/> unknown or zero when the container gave none.</summary>
    void Add(in PacketInfo info, MediaTime start, MediaTime duration, ReadOnlySpan<byte> data, ReadOnlySpan<byte> additional);

    /// <summary>Builds the document from everything added.</summary>
    SubtitleDocument Finish();
}

/// <summary>
/// Turns the packets of one container text track into a <see cref="SubtitleDocument"/>: feed it the packets with <see cref="Add"/> in storage
/// order, then take the document with <see cref="Finish"/>. Cue times are exact <see cref="MediaTime"/> values on the file's presentation timeline
/// (the track's edit list start offset is applied). Gaps (empty samples, WebVTT's empty-cue boxes) produce no cue.
/// </summary>
/// <remarks>
/// A decoder holds the cues of the track in memory; a text track is small next to the media it accompanies, and the document is the product.
/// Packets that do not parse are skipped with a defect in the log; reading never throws for bad content. The number of cues and the total text
/// are bounded by <see cref="MediaLimits.MaxElementCount"/> and <see cref="MediaLimits.MaxAllocationBytes"/>.
/// </remarks>
public sealed class TextTrackDecoder
{
    private readonly ITextTrackDecoder _codec;
    private readonly MediaTrack _track;
    private readonly MediaLimits _limits;
    private long _count;
    private long _bytes;

    /// <summary>Creates a decoder for a track.</summary>
    /// <param name="track">A text track, as <see cref="TextTracks.FormatOf"/> recognises it.</param>
    /// <param name="defects">Where to report packets that do not parse; null keeps them private.</param>
    /// <param name="limits">Resource limits; <see cref="MediaLimits.Default"/> when null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="track"/> is null.</exception>
    /// <exception cref="ArgumentException">The track is not a text track with a mapping to cues.</exception>
    public TextTrackDecoder(MediaTrack track, DefectLog? defects = null, MediaLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        _track = track;
        _limits = limits ?? MediaLimits.Default;
        Format = TextTracks.FormatOf(track);
        defects ??= new DefectLog(_limits);
        _codec = Format switch
        {
            TextTrackFormat.Tx3g => new Tx3gDecoder(track, defects),
            TextTrackFormat.QuickTimeText => new QuickTimeTextDecoder(track, defects),
            TextTrackFormat.WebVttIso => new WebVttIsoDecoder(track, defects),
            TextTrackFormat.XmlSubtitleIso => new XmlSubtitleIsoDecoder(track, defects, _limits),
            TextTrackFormat.SubRip => new SubRipDecoder(track, defects, _limits),
            TextTrackFormat.Ass => new AssDecoder(track, defects, _limits),
            TextTrackFormat.WebVtt => new MatroskaWebVttDecoder(track, defects, _limits),
            TextTrackFormat.WebmWebVtt => new WebmWebVttDecoder(track, defects, _limits),
            _ => throw new ArgumentException($"Track {track.Index} ({track.CodecTag ?? track.Codec.Name}) is not a text track OneMedia maps to cues.", nameof(track)),
        };
    }

    /// <summary>The track's format.</summary>
    public TextTrackFormat Format { get; }

    /// <summary>Takes the next packet of the track.</summary>
    /// <param name="info">The packet's description (its times are in the track's timescale).</param>
    /// <param name="data">The packet's bytes.</param>
    /// <param name="additional">For Matroska <c>S_TEXT/WEBVTT</c>: the packet's BlockAdditional (BlockAddID 1), or empty.</param>
    /// <exception cref="MediaLimitExceededException">The track holds more packets or more text than the limits allow.</exception>
    public void Add(in PacketInfo info, ReadOnlySpan<byte> data, ReadOnlySpan<byte> additional = default)
    {
        _limits.CheckElementCount(++_count, info.SourceRange.Offset);
        _bytes += data.Length + additional.Length;
        _limits.CheckAllocation(_bytes, info.SourceRange.Offset);
        MediaTime start = info.Pts.IsKnown ? _track.PresentationTime(info) : MediaTime.Unknown;
        if (!start.IsKnown)
        {
            return;
        }

        _codec.Add(in info, start, info.Duration, data, additional);
    }

    /// <summary>Builds the document.</summary>
    /// <returns>The track's cues in time order (cues that start together keep their storage order), the document's language taken from the track, and the header information the format carries.</returns>
    public SubtitleDocument Finish() => _codec.Finish();

    /// <summary>Reads every packet of a track from an open file into a document.</summary>
    /// <param name="file">The open file; it is read from its start (a source that cannot rewind can read once).</param>
    /// <param name="track">One of the file's text tracks.</param>
    /// <param name="limits">Resource limits; <see cref="MediaLimits.Default"/> when null.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentException">The track is not a text track with a mapping to cues.</exception>
    /// <exception cref="MediaReadStateException">The file is open from an asynchronous source, or was read before and cannot seek.</exception>
    public static SubtitleDocument Read(MediaFile file, MediaTrack track, MediaLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(track);
        TextTrackDecoder decoder = new(track, file.Defects, limits);
        foreach (MediaTrack t in file.Tracks)
        {
            file.Demuxer?.SetTrackEnabled(t.Index, t.Index == track.Index);
        }

        IDemuxer demuxer = file.Demuxer ?? throw new MediaReadStateException(MediaReadStateReason.AsynchronousSource, "This file was opened from an asynchronous source; use ReadAsync.");
        int generation = file.BeginRead();
        try
        {
            while (true)
            {
                file.EnsureCurrentRead(generation);
                if (!demuxer.TryReadPacket(out PacketView view))
                {
                    break;
                }

                decoder.Add(view.Info, view.Data, AdditionOf(demuxer));
            }
        }
        finally
        {
            if (generation == file.CurrentReadGeneration)
            {
                foreach (MediaTrack t in file.Tracks)
                {
                    demuxer.SetTrackEnabled(t.Index, true);
                }
            }
        }

        return decoder.Finish();
    }

    /// <summary>Reads every packet of a track from an open file of either kind of source into a document.</summary>
    /// <param name="file">The open file.</param>
    /// <param name="track">One of the file's text tracks.</param>
    /// <param name="limits">Resource limits; <see cref="MediaLimits.Default"/> when null.</param>
    /// <param name="cancellationToken">Checked before each packet.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentException">The track is not a text track with a mapping to cues.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static async ValueTask<SubtitleDocument> ReadAsync(MediaFile file, MediaTrack track, MediaLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(track);
        if (file.AsyncDemuxer is not { } demuxer)
        {
            return Read(file, track, limits);
        }

        TextTrackDecoder decoder = new(track, file.Defects, limits);
        foreach (MediaTrack t in file.Tracks)
        {
            demuxer.SetTrackEnabled(t.Index, t.Index == track.Index);
        }

        int generation = await file.BeginReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (await demuxer.ReadPacketAsync(cancellationToken).ConfigureAwait(false))
            {
                file.EnsureCurrentRead(generation);
                PacketView view = demuxer.Current;
                decoder.Add(view.Info, view.Data, demuxer is Matroska.MatroskaAsyncDemuxer m ? m.LastBlockAdditional : default);
            }
        }
        finally
        {
            if (generation == file.CurrentReadGeneration)
            {
                foreach (MediaTrack t in file.Tracks)
                {
                    demuxer.SetTrackEnabled(t.Index, true);
                }
            }
        }

        return decoder.Finish();
    }

    private static ReadOnlySpan<byte> AdditionOf(IDemuxer demuxer) => demuxer is Matroska.MatroskaDemuxer m ? m.LastBlockAdditional : default;
}

/// <summary>One packet of an encoded text track.</summary>
/// <param name="Info">The packet's description; <see cref="PacketInfo.TrackIndex"/> is 0 until the packet is given to a muxer.</param>
/// <param name="Data">The payload.</param>
/// <param name="Additional">For Matroska <c>S_TEXT/WEBVTT</c>: the BlockAdditional (BlockAddID 1), else null.</param>
public sealed record TextTrackPacket(PacketInfo Info, byte[] Data, byte[]? Additional = null);

/// <summary>How a text track is written.</summary>
public sealed record TextTrackOptions
{
    /// <summary>The track's language (ISO 639-2/T for MP4, as <see cref="MediaTrack.Language"/>), or null to use the document's.</summary>
    public string? Language { get; init; }

    /// <summary>The track's name, or null.</summary>
    public string? Name { get; init; }

    /// <summary>Disposition flags: default, forced, hearing impaired.</summary>
    public TrackDisposition Disposition { get; init; }

    /// <summary>The track's timescale in ticks per second. Default 1000, which is what the Matroska timestamp scale gives and what common muxers choose for MP4 text.</summary>
    public long Timescale { get; init; } = 1000;

    /// <summary>The container track id to declare (<see cref="MediaTrack.Id"/>); 0 lets the muxer choose.</summary>
    public long TrackId { get; init; }

    /// <summary>Resource limits; <see cref="MediaLimits.Default"/> when null.</summary>
    public MediaLimits? Limits { get; init; }
}

/// <summary>A cue document written as a text track: the track to declare, its packets in storage order, and what the format could not carry.</summary>
public sealed class TextTrackEncoding
{
    internal TextTrackEncoding(MediaTrack track, IReadOnlyList<TextTrackPacket> packets, IReadOnlyList<TextTrackLoss> losses, IReadOnlyList<string> notes)
    {
        Track = track;
        Packets = packets;
        Losses = losses;
        Notes = notes;
    }

    /// <summary>The track as a muxer should declare it: codec, sample entry or CodecPrivate, timescale, language, disposition. <see cref="MediaTrack.Index"/> is 0.</summary>
    public MediaTrack Track { get; }

    /// <summary>The packets in decode order.</summary>
    public IReadOnlyList<TextTrackPacket> Packets { get; }

    /// <summary>What the format cannot carry, per feature, with the number of cues affected.</summary>
    public IReadOnlyList<TextTrackLoss> Losses { get; }

    /// <summary>Normalisations that lose nothing visible: gap samples added, times rounded to the timescale, overlapping cues split.</summary>
    public IReadOnlyList<string> Notes { get; }
}

/// <summary>Writes a <see cref="SubtitleDocument"/> as the packets and track description of a container text track.</summary>
public static class TextTrackEncoder
{
    /// <summary>Encodes a document.</summary>
    /// <param name="document">The cues. Cues with an end at or before their start, or without times, are left out and reported.</param>
    /// <param name="format">The track format to write.</param>
    /// <param name="options">Language, name, disposition and timescale; defaults when null.</param>
    /// <returns>The track, its packets and the loss report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="NotSupportedException"><paramref name="format"/> cannot be written: <see cref="TextTrackFormat.QuickTimeText"/> and <see cref="TextTrackFormat.Unknown"/>.</exception>
    /// <exception cref="MediaLimitExceededException">The document holds more cues or text than the limits allow.</exception>
    public static TextTrackEncoding Encode(SubtitleDocument document, TextTrackFormat format, TextTrackOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new TextTrackOptions();
        MediaLimits limits = options.Limits ?? MediaLimits.Default;
        limits.CheckElementCount(document.Cues.Count, 0);
        return format switch
        {
            TextTrackFormat.Tx3g => Tx3gEncoder.Encode(document, options),
            TextTrackFormat.WebVttIso => WebVttIsoEncoder.Encode(document, options),
            TextTrackFormat.XmlSubtitleIso => XmlSubtitleIsoEncoder.Encode(document, options),
            TextTrackFormat.SubRip => SubRipEncoder.Encode(document, options),
            TextTrackFormat.Ass => AssEncoder.Encode(document, options),
            TextTrackFormat.WebVtt => MatroskaWebVttEncoder.Encode(document, options, webm: false),
            TextTrackFormat.WebmWebVtt => MatroskaWebVttEncoder.Encode(document, options, webm: true),
            _ => throw new NotSupportedException($"A {format} text track cannot be written."),
        };
    }
}

internal static class TextTrackTimes
{
    /// <summary>Whole ticks of <paramref name="t"/> in <paramref name="timescale"/>, rounded to nearest; <paramref name="inexact"/> is set when rounding moved it.</summary>
    public static long ToTicks(MediaTime t, long timescale, ref bool inexact)
    {
        MediaTime r = t.Rescale(timescale, MediaRounding.NearestAwayFromZero);
        if (!inexact && r != t)
        {
            inexact = true;
        }

        return r.Ticks;
    }

    /// <summary>Cues sorted by start time, keeping storage order among equals; cues without usable times are dropped and counted.</summary>
    public static List<SubtitleCue> Playable(SubtitleDocument doc, out int dropped)
    {
        List<(SubtitleCue Cue, int Index)> list = [];
        dropped = 0;
        int i = 0;
        foreach (SubtitleCue c in doc.Cues)
        {
            if (c.Start.IsKnown && c.End.IsKnown && c.End > c.Start && c.Start.Ticks >= 0)
            {
                list.Add((c, i));
            }
            else
            {
                dropped++;
            }

            i++;
        }

        list.Sort(static (a, b) =>
        {
            int c = a.Cue.Start.CompareTo(b.Cue.Start);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });
        List<SubtitleCue> result = new(list.Count);
        foreach ((SubtitleCue cue, int _) in list)
        {
            result.Add(cue);
        }

        return result;
    }
}
