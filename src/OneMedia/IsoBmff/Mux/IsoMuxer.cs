using System.Buffers;
using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>
/// Writes MP4, MOV, 3GP/3G2 and M4A/M4V files and fragmented MP4 / CMAF from tracks and packets (ISO/IEC 14496-12, 14496-14, the
/// QuickTime File Format and 3GPP TS 26.244). Payload bytes are copied unchanged; sample tables, edit lists, sample entries and
/// brands are built from the declared <see cref="MediaTrack"/>s.
/// </summary>
/// <remarks>
/// <para><b>Memory.</b> The payload goes to the destination (or, for <see cref="IsoLayout.MoovFirst"/>, to a spool that spills to a
/// temporary file) as packets arrive; nothing else grows with the file except the sample tables. They cost, per sample: 4 bytes of
/// size, 4 bytes more when it is a keyframe, 8 bytes per run of equal durations and 8 per run of equal composition offsets (a
/// constant frame rate costs 8 bytes in total, a variable one at most 8 per sample), plus 8 bytes per chunk and 12 per change in
/// samples per chunk. A track that interleaves with another at every packet therefore costs about 24 bytes per sample. The
/// fragmented layout keeps only the packets of the fragment being collected.</para>
/// <para><b>Times.</b> Decode times are taken as given, shifted so each track's first decode time is zero (ISO sample tables start at
/// zero); the shift and any priming or start offset the track declares are written as an edit list so the presentation timeline is
/// unchanged. Durations come from the gaps between decode times, the last sample's from its declared duration. Where that replaces a
/// declared duration, or a time had to be rounded to the track's timescale, <see cref="MuxReport.Notes"/> says how often.</para>
/// <para><b>Not stored.</b> Packet flags other than keyframe and disposable, per-packet discard padding, and sample descriptions beyond
/// the one each track declares.</para>
/// </remarks>
public sealed partial class IsoMuxer : IMuxer
{
    private enum State
    {
        Declaring,
        Writing,
        Finished,
        Failed,
    }

    private readonly Stream _stream;
    private readonly IsoMuxOptions _options;
    private readonly OutputBuffer _out;
    private readonly List<MuxTrack> _tracks = [];
    private readonly List<MediaTrack> _declared = [];
    private readonly List<string> _notes = [];
    private SpoolBuffer? _spool;
    private State _state;
    private byte[]? _ftyp;
    private bool _payloadStarted;
    private bool _direct;
    private long _payloadPosition;
    private int _lastTrack = -1;
    private bool _disposed;

    /// <summary>Creates a muxer that writes to <paramref name="output"/>, which is written from its current position.</summary>
    /// <param name="output">The destination. It must be writable; it must be seekable for <see cref="IsoLayout.MoovLast"/> to avoid a spool, and for nothing else.</param>
    /// <param name="options">Settings; null for the defaults (MP4, movie box first).</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="output"/> is not writable, or a brand in the options is not four characters.</exception>
    public IsoMuxer(Stream output, IsoMuxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The destination stream is not writable.", nameof(output));
        }

        _options = options ?? new IsoMuxOptions();
        CheckBrand(_options.MajorBrand, nameof(options));
        if (_options.CompatibleBrands is { } brands)
        {
            foreach (string brand in brands)
            {
                CheckBrand(brand, nameof(options));
            }
        }

        if (_options.Layout != IsoLayout.Fragmented && _options.Cmaf)
        {
            throw new ArgumentException("CMAF output needs the fragmented layout.", nameof(options));
        }

        _stream = output;
        _out = new OutputBuffer(output);
    }

    private static void CheckBrand(string? brand, string parameter)
    {
        if (brand is not null && brand.Length != 4)
        {
            throw new ArgumentException("A brand is four characters.", parameter);
        }
    }

    /// <inheritdoc />
    public MediaFormatId Format => MediaFormatId.IsoBmff;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _declared;

    /// <inheritdoc />
    public int AddTrack(MediaTrack track) => AddTrack(track, null);

    /// <summary>Declares a track with ISO-specific settings; see <see cref="IMuxer.AddTrack"/>.</summary>
    /// <param name="track">The track.</param>
    /// <param name="options">Track identifier, PCM layout, handler type, extra sample entry and track boxes; null for none.</param>
    /// <returns>The track's output index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="track"/> is null.</exception>
    /// <exception cref="MuxRefusedException">The track cannot be written: <see cref="MuxRefusalReason.UnsupportedCodec"/> for a codec with no
    /// ISO sample entry (Vorbis, Matroska text, companded PCM in MP4 …) or an encrypted track, <see cref="MuxRefusalReason.MissingConfiguration"/>
    /// for a codec whose parameter sets or setup bytes are absent, <see cref="MuxRefusalReason.InvalidConfiguration"/> when they do not parse or are
    /// laid out for another container, <see cref="MuxRefusalReason.InvalidTrack"/> for a missing timescale, size, rate or channel count, a second track in CMAF
    /// output, <see cref="MuxRefusalReason.InvalidState"/> after the first packet, and <see cref="MuxRefusalReason.LimitExceeded"/> past the track limit.</exception>
    public int AddTrack(MediaTrack track, IsoTrackOptions? options)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (_state != State.Declaring)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "Tracks must be declared before the first packet is written.");
        }

        int index = _tracks.Count;
        if (index >= _options.Limits.MaxTracks)
        {
            throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"The track limit of {_options.Limits.MaxTracks} is reached.", index, track.Codec);
        }

        if (_options.Cmaf && index >= 1)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, "A CMAF track file holds exactly one track.", index, track.Codec);
        }

        if (track.Timescale is <= 0 or > uint.MaxValue)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, $"Track {index}: the timescale must be between 1 and {uint.MaxValue}.", index, track.Codec);
        }

        SampleEntryPlan plan = IsoSampleEntries.Plan(track, index, _options.Kind, options);
        uint id = PickTrackId(track, options);
        MediaTrack declared = track with { Index = index, Id = id };
        _tracks.Add(new MuxTrack(declared, options, plan, id));
        _declared.Add(declared);
        return index;
    }

    private uint PickTrackId(MediaTrack track, IsoTrackOptions? options)
    {
        uint wanted = options?.TrackId is > 0 ? options.TrackId : track.Id is > 0 and <= uint.MaxValue ? (uint)track.Id : 0;
        if (wanted != 0 && !_tracks.Exists(t => t.TrackId == wanted))
        {
            return wanted;
        }

        uint next = 1;
        while (_tracks.Exists(t => t.TrackId == next))
        {
            next++;
        }

        return next;
    }

    /// <inheritdoc />
    public void WritePacket(in PacketInfo info, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_state is State.Finished or State.Failed)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "The muxer is finished.");
        }

        if (_tracks.Count == 0)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "No track was declared before the first packet.");
        }

        if ((uint)info.TrackIndex >= (uint)_tracks.Count)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, $"Packet for track {info.TrackIndex}, which was not declared.", info.TrackIndex);
        }

        MuxTrack t = _tracks[info.TrackIndex];
        MediaLimits limits = _options.Limits;
        if (data.Length > limits.MaxPacketBytes)
        {
            throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"Track {info.TrackIndex}: a packet of {data.Length} bytes exceeds the packet limit of {limits.MaxPacketBytes}.", info.TrackIndex, t.Declared.Codec);
        }

        if (t.Samples + 1 > limits.MaxSampleTableEntries)
        {
            throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"Track {info.TrackIndex}: more than {limits.MaxSampleTableEntries} samples.", info.TrackIndex, t.Declared.Codec);
        }

        if (!info.Dts.IsKnown && !info.Pts.IsKnown)
        {
            throw new MuxRefusedException(MuxRefusalReason.MissingTimestamp, $"Track {info.TrackIndex}: a packet has neither a presentation nor a decode time.", info.TrackIndex, t.Declared.Codec);
        }

        bool fragmented = _options.Layout == IsoLayout.Fragmented;
        long rounded = 0;
        long dts = Ticks(info.Dts.IsKnown ? info.Dts : info.Pts, t, ref rounded);
        long pts = Ticks(info.Pts.IsKnown ? info.Pts : info.Dts, t, ref rounded);
        long declaredDuration = info.Duration.IsKnown ? Ticks(info.Duration, t, ref rounded) : -1;
        if (t.Started && dts < t.PreviousRawDts)
        {
            throw new MuxRefusedException(MuxRefusalReason.NonMonotonicDts,
                $"Track {info.TrackIndex}: decode time {dts} follows {t.PreviousRawDts}; packets must arrive in decode order.", info.TrackIndex, t.Declared.Codec);
        }

        // ISO sample tables start at time zero, so the first decode time becomes zero. In a fragmented file the time of each fragment is
        // explicit, so only a negative start (composition reordering) is shifted.
        long shift = t.Started ? t.Shift : fragmented ? Math.Min(dts, 0) : dts;
        long shifted = dts - shift;
        long composition = pts - dts;
        if (composition is < int.MinValue or > int.MaxValue || (t.Started && shifted - t.PreviousShiftedDts > int.MaxValue) || declaredDuration > int.MaxValue)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, $"Track {info.TrackIndex}: a time offset or duration does not fit 32 bits at the track's timescale of {t.Timescale}.", info.TrackIndex, t.Declared.Codec);
        }

        if (!t.Started && t.Plan.NeedsFirstPacket)
        {
            int n = Math.Min(data.Length, IsoSampleEntries.FirstPacketBytes);
            t.Head = data[..n].ToArray();
            t.HeadLength = n;
        }

        if (_state == State.Declaring)
        {
            _state = State.Writing;
        }

        t.RoundedTimes += rounded;
        if ((info.Flags & PacketAttributes.Discard) != 0)
        {
            t.IgnoredDiscards++;
        }

        if (fragmented)
        {
            AddFragmentPacket(info, t, data, shifted, declaredDuration, (int)composition, cancellationToken);
            t.Shift = shift;
        }
        else
        {
            AddProgressivePacket(info, t, data, shifted, declaredDuration, (int)composition);
            t.Shift = shift;
        }

        t.Started = true;
        t.PreviousRawDts = dts;
        t.PreviousShiftedDts = shifted;
    }

    // Rescales a time to the track's timescale: exact when it divides, otherwise to the nearest tick (counted).
    private static long Ticks(MediaTime time, MuxTrack t, ref long rounded)
    {
        long scale = t.Timescale;
        if (time.Timescale == scale)
        {
            return time.Ticks;
        }

        if (!time.TryRescale(scale, MediaRounding.NearestAwayFromZero, out MediaTime r))
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, $"A time of {time} does not fit 64 bits at the track's timescale of {scale}.", t.Declared.Index, t.Declared.Codec);
        }

        if (((Int128)time.Ticks * scale) % time.Timescale != 0)
        {
            rounded++;
        }

        return r.Ticks;
    }

    private void AddProgressivePacket(in PacketInfo info, MuxTrack t, ReadOnlySpan<byte> data, long shifted, long declaredDuration, int composition)
    {
        EnsureStarted();
        if (t.Samples > 0)
        {
            long delta = shifted - t.PreviousShiftedDts;
            if (t.PreviousDeclared >= 0 && t.PreviousDeclared != delta)
            {
                t.ReplacedDurations++;
            }

            t.CommitDuration(delta);
        }

        t.PreviousDeclared = declaredDuration;
        t.AddComposition(composition);
        uint size = (uint)data.Length;
        t.Sizes.Add(size);
        t.MinSize = Math.Min(t.MinSize, size);
        t.MaxSize = Math.Max(t.MaxSize, size);
        bool disposable = (info.Flags & PacketAttributes.Disposable) != 0;
        if (disposable && t.Dependencies is null)
        {
            // The first disposable sample: from now on the dependency byte of every sample is kept, the earlier ones worked out from the sync samples.
            t.StartDependencies();
        }

        t.Dependencies?.Add(MuxTrack.DependencyByte(info.IsKeyframe, disposable));
        if (info.IsKeyframe)
        {
            t.SyncSamples.Add((uint)(t.Samples + 1));
            t.KeyCount++;
        }

        // A chunk is a run of one track's samples that are adjacent in the payload; a packet of another track in between ends it.
        if (_lastTrack != info.TrackIndex || !t.ChunkOpen)
        {
            t.CloseChunk();
            t.ChunkOffsets.Add((ulong)_payloadPosition);
            t.ChunkOpen = true;
        }

        t.ChunkSamples++;
        _lastTrack = info.TrackIndex;
        t.Samples++;
        t.PayloadBytes += data.Length;
        if (_direct)
        {
            _out.Write(data);
        }
        else
        {
            _spool!.Write(data);
        }

        _payloadPosition += data.Length;
    }

    private void EnsureStarted()
    {
        if (_payloadStarted)
        {
            return;
        }

        _payloadStarted = true;
        _ftyp ??= BuildFtyp();
        _direct = _options.Layout == IsoLayout.MoovLast && _out.CanSeek;
        if (_direct)
        {
            _out.Write(_ftyp);
            // A media data box with a 64-bit size, patched at the end: when the payload turns out to fit 32 bits the 16 bytes become a
            // free box and an ordinary media data header, so small files carry no oversize header.
            Span<byte> header = stackalloc byte[16]; // fixed small buffer
            BinaryPrimitives.WriteUInt32BigEndian(header, 1);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], BoxTypes.Mdat);
            BinaryPrimitives.WriteUInt64BigEndian(header[8..], 16);
            _out.Write(header);
        }
        else
        {
            _spool = new SpoolBuffer(_options.SpoolMemoryBudget, _options.SpoolFactory ?? CreateTemporaryFile);
        }
    }

    private static FileStream CreateTemporaryFile()
    {
        try
        {
            return new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or PlatformNotSupportedException)
        {
            throw new MuxRefusedException(MuxRefusalReason.OutputNotSuitable, "The payload has to wait for the movie box and no temporary file could be made; pass a seekable destination with MoovLast, a SpoolFactory, or the fragmented layout.");
        }
    }

    /// <inheritdoc />
    public void Flush(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_state is State.Finished or State.Failed)
        {
            return;
        }

        if (_options.Layout == IsoLayout.Fragmented && _state == State.Writing)
        {
            FlushFragment(cancellationToken);
        }

        _out.Flush(true);
    }

    /// <inheritdoc />
    public MuxReport Finish(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_state is State.Finished or State.Failed)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "The muxer is already finished.");
        }

        if (_tracks.Count == 0)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "No track was declared.");
        }

        try
        {
            _ftyp ??= BuildFtyp();
            if (_options.Layout == IsoLayout.Fragmented)
            {
                FinishFragmented(cancellationToken);
            }
            else
            {
                FinishProgressive(cancellationToken);
            }

            _out.Flush(true);
        }
        catch
        {
            _state = State.Failed;
            throw;
        }

        _state = State.Finished;
        return BuildReport();
    }

    private MuxReport BuildReport()
    {
        List<TrackMuxReport> tracks = new(_tracks.Count);
        foreach (MuxTrack t in _tracks)
        {
            tracks.Add(new TrackMuxReport(t.Declared.Index, t.Samples, t.PayloadBytes, new MediaTime(t.DurationSum, t.Timescale)));
            if (t.RoundedTimes > 0)
            {
                _notes.Add($"Track {t.Declared.Index}: {t.RoundedTimes} times were rounded to the track timescale of {t.Timescale}.");
            }

            if (t.ReplacedDurations > 0)
            {
                _notes.Add($"Track {t.Declared.Index}: {t.ReplacedDurations} declared durations were replaced by the gap to the next decode time.");
            }

            if (t.ZeroDurations > 0)
            {
                _notes.Add($"Track {t.Declared.Index}: {t.ZeroDurations} samples share their decode time with the next one; the sample table then holds zero durations, which some readers refuse.");
            }

            if (t.IgnoredDiscards > 0 && t.Declared.Edit.Edits is null && t.Declared.Edit.Priming.IsZero)
            {
                _notes.Add($"Track {t.Declared.Index}: {t.IgnoredDiscards} packets carry the discard flag, which ISO files store only through the edit list.");
            }
        }

        return new MuxReport(_out.Position, tracks, _notes);
    }

    /// <summary>
    /// Releases the muxer. Disposing before <see cref="Finish"/> abandons the output: what was written stays incomplete. The destination
    /// stream is disposed only when <see cref="IsoMuxOptions.OwnsOutput"/> is set.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (MuxTrack t in _tracks)
        {
            t.Dispose();
        }

        _spool?.Dispose();
        _out.Dispose();
        if (_options.OwnsOutput)
        {
            _stream.Dispose();
        }
    }
}
