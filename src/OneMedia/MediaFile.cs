using System.Buffers;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia;

/// <summary>
/// An opened media file: what the container says about itself (format, tracks, duration, defects) and its packets. Created by
/// <see cref="Media.Open(string)"/> and its overloads. The tracks are immutable neutral descriptions; the packets come from the
/// demuxer, which is reachable as <see cref="Demuxer"/> or <see cref="AsyncDemuxer"/> for everything the happy path does not offer.
/// Not thread-safe.
/// </summary>
public sealed class MediaFile : IDisposable, IAsyncDisposable
{
    private readonly IDemuxer? _sync;
    private readonly IAsyncDemuxer? _async;

    private MediaFile(IDemuxer? sync, IAsyncDemuxer? async)
    {
        _sync = sync;
        _async = async;
    }

    /// <summary>Opens a seekable source with the registered formats. The file owns the source and disposes it, also when opening fails.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="registry">The formats to try; <see cref="MediaFormats.Default"/> when null.</param>
    /// <param name="options">Limits and defect log; <see cref="MediaOpenOptions.Default"/> when null. <see cref="MediaOpenOptions.LeaveOpen"/> is honoured.</param>
    /// <param name="cancellationToken">Observed while the headers are read.</param>
    /// <returns>The opened file; the caller disposes it.</returns>
    /// <exception cref="UnsupportedMediaException">No registered format recognises the data, or the format has no reader.</exception>
    /// <exception cref="MediaFormatException">The data is not valid for the format it was recognised as.</exception>
    /// <exception cref="MediaLimitExceededException">The data asks for more than the limits allow.</exception>
    public static MediaFile Open(IMediaSource source, MediaFormatRegistry? registry = null, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            return new MediaFile((registry ?? MediaFormats.Default).Open(source, options, cancellationToken), null);
        }
        catch
        {
            if (options?.LeaveOpen != true)
            {
                source.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Opens a source that can only be read once, front to back (a pipe, a socket, a non-seekable stream), with the registered formats that
    /// can be read that way: Matroska/WebM, MP4 whose layout allows it (fragmented, or <c>moov</c> before <c>mdat</c>), ASF and the
    /// elementary-stream formats. The file owns the source and disposes it, also when opening fails.
    /// </summary>
    /// <remarks>
    /// Such a file can be read once. A second <see cref="Packets"/> pass, or reading another track after the first, raises
    /// <see cref="MediaReadStateException"/> with <see cref="MediaReadStateReason.CannotRewind"/>; open the data again to read it twice.
    /// </remarks>
    /// <param name="source">The source, from its first byte.</param>
    /// <param name="registry">The formats to try; <see cref="MediaFormats.Default"/> when null.</param>
    /// <param name="options">Limits and defect log; <see cref="MediaOpenOptions.LeaveOpen"/> is honoured.</param>
    /// <param name="cancellationToken">Observed while the headers are read.</param>
    /// <returns>The opened file; the caller disposes it.</returns>
    /// <exception cref="UnsupportedMediaException">The data is not recognised, or its format needs a seekable source.</exception>
    /// <exception cref="MediaFormatException">The data is not valid for its format.</exception>
    public static MediaFile Open(IForwardMediaSource source, MediaFormatRegistry? registry = null, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            return new MediaFile((registry ?? MediaFormats.Default).Open(source, options, cancellationToken), null);
        }
        catch
        {
            if (options?.LeaveOpen != true)
            {
                source.Dispose();
            }

            throw;
        }
    }

    /// <summary>Opens an asynchronous (ranged or remote) source with the registered formats.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="registry">The formats to try; <see cref="MediaFormats.Default"/> when null.</param>
    /// <param name="options">Limits and defect log.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The opened file; the caller disposes it. Packets are read with <see cref="PacketsAsync"/>.</returns>
    /// <exception cref="UnsupportedMediaException">No registered format recognises the data, or the format has no asynchronous reader.</exception>
    public static async ValueTask<MediaFile> OpenAsync(IAsyncMediaSource source, MediaFormatRegistry? registry = null, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        registry ??= MediaFormats.Default;
        try
        {
            FormatMatch? match = await registry.SniffAsync(source, cancellationToken).ConfigureAwait(false);
            if (match is null)
            {
                throw new UnsupportedMediaException("The data is not in any registered format.");
            }

            if (match.Value.Format is not IMediaFormat format)
            {
                throw new UnsupportedMediaException($"{match.Value.Format.DisplayName} is recognised but this program has no reader for it.");
            }

            return new MediaFile(null, await format.OpenAsync(source, options ?? MediaOpenOptions.Default, cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            if (options?.LeaveOpen != true)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private MediaMetadata? _metadata;

    /// <summary>
    /// The file's metadata: native tags, the common view and the basic facts. Read on first use from the demuxer's structure (no media data
    /// is read) and kept. A file opened from an asynchronous source must call <see cref="ReadMetadataAsync"/> first.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file came from an asynchronous source and <see cref="ReadMetadataAsync"/> has not run, or its format has no metadata reader.</exception>
    public MediaMetadata Metadata => _metadata ??= (_sync as IMetadataProvider ?? throw NotYetRead()).ReadMetadata();

    /// <summary>Reads the metadata from either kind of source and keeps it for <see cref="Metadata"/> and the properties built on it.</summary>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The metadata.</returns>
    /// <exception cref="InvalidOperationException">The format has no metadata reader.</exception>
    public async ValueTask<MediaMetadata> ReadMetadataAsync(CancellationToken cancellationToken = default) =>
        _metadata ??= _async is IAsyncMetadataProvider a ? await a.ReadMetadataAsync(cancellationToken).ConfigureAwait(false)
            : _sync is IMetadataProvider p ? p.ReadMetadata(cancellationToken) : throw NotYetRead();

    /// <summary>The common tag view over the movie-level tags: title, artist, comment, pictures and the rest. Read-only.</summary>
    public CommonTags Tags => Metadata.Tags;

    /// <summary>When the recording was made, with every candidate the file offers (<see cref="Fact{T}.Candidates"/>), best first.</summary>
    public Fact<MediaDateTime> CaptureTime => Metadata.Facts.CaptureTime;

    /// <summary>Where the recording was made, with its candidates.</summary>
    public Fact<GeoLocation> Location => Metadata.Facts.Location;

    /// <summary>The recording device: make, model and software, each with its candidates.</summary>
    public DeviceFacts Device => Metadata.Facts.Device;

    /// <summary>The display rotation of the first video track, with its candidates.</summary>
    public Fact<VideoOrientation> Rotation => Metadata.Facts.Orientation;

    /// <summary>Raw access to the native tags: <c>media.Native["udta", "©day"]</c>, and <see cref="MediaNative.Reader"/> for the demuxer itself.</summary>
    public MediaNative Native => new(this);

    private InvalidOperationException NotYetRead() => new(_sync is null ? "Call ReadMetadataAsync first for a file opened from an asynchronous source." : "This format has no metadata reader.");

    private IMediaContainer Container => (IMediaContainer?)_sync ?? _async!;

    /// <summary>The container format the data was read as.</summary>
    public MediaFormatId Format => Container.Format;

    /// <summary>The tracks in container order.</summary>
    public IReadOnlyList<MediaTrack> Tracks => Container.Tracks;

    /// <summary>The duration the container states about itself (movie header, fragment header or Segment information), or <see cref="MediaTime.Unknown"/>. It can disagree with the tracks.</summary>
    public MediaTime ContainerDuration => Container.Duration;

    /// <summary>
    /// The longest track duration as presented: the sum of the edit list's segments (ISO/IEC 14496-12 §8.6.6) when the track has one, else the media
    /// header's duration; <see cref="MediaTime.Unknown"/> when no track states one (Matroska, fragmented files).
    /// </summary>
    public MediaTime TracksDuration
    {
        get
        {
            MediaTime longest = MediaTime.Unknown;
            foreach (MediaTrack t in Container.Tracks)
            {
                MediaTime d = PresentedDuration(t);
                if (d.IsKnown && (!longest.IsKnown || d > longest))
                {
                    longest = d;
                }
            }

            return longest;
        }
    }

    private static MediaTime PresentedDuration(MediaTrack t)
    {
        if (t.Edit.Edits is { Count: > 0 } edits)
        {
            MediaTime total = default;
            foreach (EditSegment e in edits)
            {
                if (!e.Duration.IsKnown)
                {
                    return t.Duration;
                }

                total = total.Add(e.Duration, MediaRounding.NearestAwayFromZero);
            }

            if (total.Ticks > 0)
            {
                return total;
            }
        }

        return t.Duration;
    }

    /// <summary>
    /// The duration to present: the container's own figure, unless a track without an edit list is longer than it (a movie header shorter than its
    /// tracks), in which case that track wins, as in other tools; with no container figure, <see cref="TracksDuration"/>. A track with an edit list never
    /// overrides the container: its list may repeat or trim media, and the movie header already accounts for it. Both figures stay available as
    /// <see cref="ContainerDuration"/> and <see cref="TracksDuration"/>.
    /// </summary>
    public MediaTime ExactDuration
    {
        get
        {
            if (!ContainerDuration.IsKnown)
            {
                return TracksDuration;
            }

            MediaTime best = ContainerDuration;
            foreach (MediaTrack t in Container.Tracks)
            {
                if (t.Edit.Edits is not { Count: > 0 } && t.Duration.IsKnown && t.Duration > best)
                {
                    best = t.Duration;
                }
            }

            return best;
        }
    }

    /// <summary>The <see cref="ExactDuration"/> as a <see cref="TimeSpan"/> (rounded to the nearest tick), or null when nothing states one.</summary>
    public TimeSpan? Duration => ExactDuration.IsKnown ? ExactDuration.ToTimeSpan() : null;

    /// <summary>Deviations found so far; grows as packets are read.</summary>
    public DefectLog Defects => Container.Defects;

    /// <summary>True when the file's content is protected (encrypted or under digital rights management). Its tracks and metadata are readable; no payloads are delivered.</summary>
    public bool IsProtected => Container.IsProtected;

    /// <summary>The first video track marked default, else the first video track; null when there is none.</summary>
    public MediaTrack? Video => FirstOf(TrackKind.Video);

    /// <summary>The first audio track marked default, else the first audio track; null when there is none.</summary>
    public MediaTrack? Audio => FirstOf(TrackKind.Audio);

    /// <summary>The synchronous demuxer, or null when the file was opened from an asynchronous source.</summary>
    public IDemuxer? Demuxer => _sync;

    /// <summary>The asynchronous demuxer, or null when the file was opened from a seekable source.</summary>
    public IAsyncDemuxer? AsyncDemuxer => _async;

    private MediaTrack? FirstOf(TrackKind kind)
    {
        MediaTrack? first = null;
        foreach (MediaTrack t in Container.Tracks)
        {
            if (t.Kind != kind)
            {
                continue;
            }

            if ((t.Disposition & TrackDisposition.Default) != 0)
            {
                return t;
            }

            first ??= t;
        }

        return first;
    }

    // A file has one read position. The first read starts from where Open left it; every later read first returns to the start, when the
    // source can seek, and otherwise fails. Each read takes a new generation number, and an older reader that is still alive fails on its next
    // call instead of returning packets from a position someone else moved. Silent empty output is never the result.
    private bool _readStarted;
    private int _generation;

    /// <summary>Starts a read: returns the file to its beginning if it was read before, and returns the read's generation. Other tracks must already be switched off.</summary>
    internal int BeginRead()
    {
        if (_readStarted)
        {
            if (_sync is null || !_sync.CanSeek)
            {
                throw new MediaReadStateException(MediaReadStateReason.CannotRewind, "The file was already read and its source cannot seek, so it cannot be read again. Open it again to read it twice.");
            }

            _sync.Seek(StartOfFile, SeekMode.PreviousKeyframe);
        }

        _readStarted = true;
        return ++_generation;
    }

    private async ValueTask<int> BeginReadAsync(CancellationToken cancellationToken)
    {
        if (_readStarted)
        {
            if (!Container.CanSeek)
            {
                throw new MediaReadStateException(MediaReadStateReason.CannotRewind, "The file was already read and its source cannot seek, so it cannot be read again. Open it again to read it twice.");
            }

            if (_async is { } a)
            {
                await a.SeekAsync(StartOfFile, SeekMode.PreviousKeyframe, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _sync!.Seek(StartOfFile, SeekMode.PreviousKeyframe);
            }
        }

        _readStarted = true;
        return ++_generation;
    }

    // Earlier than any packet can be: about 34 years before zero, small enough that every container timescale rescales it without overflow.
    private static MediaTime StartOfFile => new(-(1L << 30), 1);

    /// <summary>Throws when a newer read has taken over the file since the read with this generation began.</summary>
    internal void EnsureCurrentRead(int generation)
    {
        if (generation != _generation)
        {
            throw new MediaReadStateException(MediaReadStateReason.Superseded, "This reader was superseded: the file was read again (another track, another decoder) after it began. Read one track at a time, and finish or dispose a reader before starting the next.");
        }
    }

    /// <summary>
    /// Reads the packets of one track as owned packets. Other tracks are switched off while the enumeration runs, so their payloads are not
    /// read, and switched on again when it ends. The consumer disposes each packet.
    /// </summary>
    /// <remarks>
    /// The first read of a file starts at its beginning. Every later read of the same file (another <c>Packets</c> pass, another track, or
    /// <c>Decode</c>) starts again from the beginning, which needs a source that can seek. Nothing is checked until the enumeration starts.
    /// </remarks>
    /// <param name="track">The track.</param>
    /// <returns>The packets in storage order.</returns>
    /// <exception cref="MediaReadStateException">When enumeration starts: the file was opened from an asynchronous source, so use <see cref="PacketsAsync"/> (<see cref="MediaReadStateReason.AsynchronousSource"/>); the file was read before and its source cannot seek (<see cref="MediaReadStateReason.CannotRewind"/>); or while enumerating: another read of the file began (<see cref="MediaReadStateReason.Superseded"/>).</exception>
    public IEnumerable<OwnedPacket> Packets(MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        IDemuxer demuxer = _sync ?? throw new MediaReadStateException(MediaReadStateReason.AsynchronousSource, "This file was opened from an asynchronous source; use PacketsAsync.");
        foreach (MediaTrack t in demuxer.Tracks)
        {
            demuxer.SetTrackEnabled(t.Index, t.Index == track.Index);
        }

        int generation = BeginRead();
        try
        {
            while (true)
            {
                EnsureCurrentRead(generation);
                if (!demuxer.TryReadPacket(out PacketView view))
                {
                    break;
                }

                IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(Math.Max(view.Data.Length, 1));
                view.Data.CopyTo(owner.Memory.Span);
                yield return new OwnedPacket(view.Info, owner);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                foreach (MediaTrack t in demuxer.Tracks)
                {
                    demuxer.SetTrackEnabled(t.Index, true);
                }
            }
        }
    }

    /// <summary>Reads the packets of one track as owned packets, from either kind of source. Later reads start again from the beginning, as for <see cref="Packets"/>.</summary>
    /// <param name="track">The track.</param>
    /// <param name="cancellationToken">Checked before each packet.</param>
    /// <returns>The packets in storage order. The consumer disposes each one.</returns>
    /// <exception cref="MediaReadStateException">As for <see cref="Packets"/>.</exception>
    public async IAsyncEnumerable<OwnedPacket> PacketsAsync(MediaTrack track, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        IMediaContainer container = Container;
        foreach (MediaTrack t in container.Tracks)
        {
            container.SetTrackEnabled(t.Index, t.Index == track.Index);
        }

        int generation = await BeginReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_async is { } asyncDemuxer)
            {
                await foreach (OwnedPacket p in asyncDemuxer.ReadPacketsAsync(null, cancellationToken).ConfigureAwait(false))
                {
                    EnsureCurrentRead(generation);
                    yield return p;
                }
            }
            else
            {
                await foreach (OwnedPacket p in _sync!.ReadPacketsAsync(null, cancellationToken).ConfigureAwait(false))
                {
                    EnsureCurrentRead(generation);
                    yield return p;
                }
            }
        }
        finally
        {
            if (generation == _generation)
            {
                foreach (MediaTrack t in container.Tracks)
                {
                    container.SetTrackEnabled(t.Index, true);
                }
            }
        }
    }

    /// <summary>Closes the demuxer and, unless it was opened to leave it open, the source.</summary>
    public void Dispose()
    {
        _sync?.Dispose();
        if (_async is not null)
        {
            _async.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Closes the demuxer and, unless it was opened to leave it open, the source.</summary>
    /// <returns>A task that completes when the source is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        _sync?.Dispose();
        if (_async is not null)
        {
            await _async.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Raw lookups over a file's native tags and the format reader behind it; see <see cref="MediaFile.Native"/>.</summary>
public readonly struct MediaNative
{
    private readonly MediaFile _file;

    internal MediaNative(MediaFile file) => _file = file;

    /// <summary>The first movie-level native tag with the system and key, or null.</summary>
    /// <param name="system">One of <see cref="TagSystems"/>.</param>
    /// <param name="key">The key as the system spells it.</param>
    public NativeTag? this[string system, string key] => _file.Metadata[system, key];

    /// <summary>Every native tag, movie or track level, with the system and key.</summary>
    /// <param name="system">One of <see cref="TagSystems"/>.</param>
    /// <param name="key">The key.</param>
    public IEnumerable<NativeTag> Find(string system, string key) => _file.Metadata.Find(system, key);

    /// <summary>
    /// The format reader itself (<c>IsoBmffDemuxer</c> or <c>MatroskaDemuxer</c> and their asynchronous twins), which exposes the
    /// format's own model: the movie box tree for ISO, track entries and info for Matroska.
    /// </summary>
    public object Reader => (object?)_file.Demuxer ?? _file.AsyncDemuxer!;
}
