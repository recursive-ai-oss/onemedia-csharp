using System.Buffers;
using RecursiveAI.OneMedia.IsoBmff;
using System.Text;
using RecursiveAI.OneMedia.Metadata;

namespace RecursiveAI.OneMedia.Streaming.Sources;

/// <summary>
/// An HLS or DASH presentation read as one media file: the tracks are those of the chosen variant, the packets are the packets of its segments in
/// order, with times continuous across segments. Each segment is fetched through the caller's <see cref="IPresentationResolver"/> as one ranged
/// read (with its initialization segment in front for fragmented MP4), demultiplexed by the container reader that recognises it, and released when
/// the next one starts, so memory depends on the largest segment, not on the length of the presentation.
/// </summary>
/// <remarks>
/// <para><b>Time.</b> Packet times are those the segments state (an MPEG-TS PTS, a fragment's <c>baseMediaDecodeTime</c>), in each track's own timescale.
/// Across an HLS <c>EXT-X-DISCONTINUITY</c> the times of the following segments are shifted so they continue where the previous ones ended
/// (end of the last packet before, less the start of the first after), and the first packet of each track after the break carries
/// <see cref="PacketAttributes.Discontinuity"/>. DASH segments are placed on the manifest's timeline: <c>Period@start</c> less <c>presentationTimeOffset</c>
/// is added to the times the segment states, which is zero for a single period that starts at 0.</para>
/// <para><b>Several renditions.</b> An HLS variant with a separate audio group lists the audio rendition's tracks after the variant's; DASH lists one
/// stream per adaptation set. Packets of the renditions are interleaved by decode time.</para>
/// <para><b>Protected content.</b> A presentation with <c>EXT-X-KEY</c> (method other than NONE) or <c>ContentProtection</c> reports
/// <see cref="IsProtected"/>, delivers no packets, and lists the tracks it can read without decrypting.</para>
/// <para><b>Seeking.</b> <see cref="Seek"/> lands at the start of the segment that contains the target, so the first packet may be earlier than the target
/// by up to a segment. Segment-relative defect offsets from the container readers are copied into <see cref="Defects"/> with the segment number in the message.</para>
/// <para>Not thread-safe. It implements both the synchronous and the asynchronous demuxer: use the asynchronous members with a ranged resolver; the
/// synchronous ones wait on it, which is only suitable for resolvers that complete at once (local files).</para>
/// </remarks>
public sealed class PresentationDemuxer : IDemuxer, IAsyncDemuxer, IMetadataProvider, IAsyncMetadataProvider
{
    private const int CachedSources = 2;

    private readonly IPresentationResolver _resolver;
    private readonly PresentationOptions _options;
    private readonly MediaLimits _limits;
    private readonly DefectLog _defects;
    private readonly MediaFormatRegistry _segmentFormats;
    private readonly List<Rendition> _renditions = [];
    private readonly List<MediaTrack> _tracks = [];
    private readonly List<(Uri Uri, IAsyncMediaSource Source)> _sources = [];
    private readonly List<bool> _enabled = [];
    private Rendition? _lastReturned;
    private PacketInfo _currentInfo;
    private Rendition? _current;
    private MediaFormatId _format;
    private List<PresentationVariant> _variants = [];
    private int _variantIndex;
    private MediaTime _duration = MediaTime.Unknown;
    private bool _protected;
    private bool _disposed;

    private PresentationDemuxer(IPresentationResolver resolver, PresentationOptions options, MediaOpenOptions open)
    {
        _resolver = resolver;
        _options = options;
        _limits = open.Limits;
        _defects = open.Defects ?? new DefectLog(_limits);
        _segmentFormats = options.SegmentFormats ?? MediaFormats.SegmentFormats;
    }

    /// <summary>Every variant the playlist or manifest lists, in its own order.</summary>
    public IReadOnlyList<PresentationVariant> Variants => _variants;

    /// <summary>The variant being read (an index into <see cref="Variants"/>).</summary>
    public int VariantIndex => _variantIndex;

    /// <summary>True when the presentation is live: a playlist without <c>EXT-X-ENDLIST</c> that may still grow.</summary>
    public bool IsLive
    {
        get
        {
            foreach (Rendition r in _renditions)
            {
                if (!r.Plan.Ended)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <inheritdoc />
    public MediaFormatId Format => _format;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _tracks;

    /// <inheritdoc />
    public MediaTime Duration => _duration;

    /// <inheritdoc />
    public DefectLog Defects => _defects;

    /// <inheritdoc />
    public bool CanSeek => true;

    /// <inheritdoc />
    public bool IsProtected => _protected;

    /// <inheritdoc />
    public PacketView Current => _current is { HeldValid: true } r ? new PacketView(_currentInfo, r.Held.AsSpan(0, r.HeldLength)) : default;

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(trackIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(trackIndex, _tracks.Count);
        _enabled[trackIndex] = enabled;
        foreach (Rendition r in _renditions)
        {
            r.ApplyEnabled(_enabled);
        }
    }

    // ---- opening ----------------------------------------------------------------------------------------------------------------------

    internal static async ValueTask<PresentationDemuxer> CreateAsync(Uri location, IPresentationResolver resolver, byte[]? playlist, PresentationOptions options, MediaOpenOptions open, CancellationToken cancellationToken)
    {
        PresentationDemuxer d = new(resolver, options, open);
        try
        {
            await d.InitializeAsync(location, playlist, cancellationToken).ConfigureAwait(false);
            return d;
        }
        catch
        {
            await d.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask InitializeAsync(Uri location, byte[]? playlist, CancellationToken ct)
    {
        byte[] bytes = playlist ?? await ReadWholeAsync(location, _limits.MaxTagBytes, ct).ConfigureAwait(false);
        PresentationPlan plan;
        if (HlsReader.IsPlaylist(bytes))
        {
            _format = MediaFormatId.Hls;
            plan = HlsReader.Read(DecodeText(bytes), location, _limits, _defects);
        }
        else if (DashReader.IsManifest(bytes.AsSpan(0, Math.Min(bytes.Length, MediaFormatRegistry.SniffBytes))))
        {
            _format = MediaFormatId.Dash;
            plan = DashReader.Read(bytes, location, _options.VariantIndex, _limits, _defects);
        }
        else
        {
            throw new UnsupportedMediaException("The resource is neither an HLS playlist nor a DASH manifest.");
        }

        _variants = plan.Variants;
        _variantIndex = Math.Clamp(_options.VariantIndex, 0, plan.Variants.Count - 1);
        List<PlannedRendition> parts = plan.VariantRenditions[_variantIndex];
        foreach (PlannedRendition part in parts)
        {
            if (_format == MediaFormatId.Hls && plan.IsMaster)
            {
                byte[] media = await ReadWholeAsync(part.PlaylistLocation!, _limits.MaxTagBytes, ct).ConfigureAwait(false);
                if (!HlsReader.IsPlaylist(media) || HlsReader.IsMultivariant(DecodeText(media)))
                {
                    throw new MediaFormatException(new MediaDefect(DefectKind.InvalidValue, DefectSeverity.Error, 0, "A variant's URI does not name a media playlist.", "hls.not-media-playlist"));
                }

                PlannedRendition parsed = HlsReader.ReadMedia(DecodeText(media), part.PlaylistLocation!, _limits, _defects);
                part.Segments.AddRange(parsed.Segments);
                part.Ended = parsed.Ended;
                part.FirstSequence = parsed.FirstSequence;
                part.Protected |= parsed.Protected;
            }

            if (part.IndexRange is { } range)
            {
                byte[] box = await ReadRangeAsync(part.IndexedResource!, range.Offset, range.Length, _limits.MaxTagBytes, ct).ConfigureAwait(false);
                if (TryParseIndexBox(box, range.Offset, out SegmentIndexBox? index))
                {
                    DashReader.ExpandIndex(part, index!, _limits);
                }
                else
                {
                    _defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, range.Offset, "The index range of a representation does not hold a segment index box.", "dash.bad-sidx");
                }
            }
        }

        _duration = plan.Duration;
        MediaTime longest = MediaTime.Zero;
        bool allKnown = true;
        foreach (PlannedRendition part in parts)
        {
            _protected |= part.Protected;
            foreach (PlannedSegment s in part.Segments)
            {
                _protected |= s.Encrypted;
            }

            MediaTime sum = HlsReader.SumDurations(part);
            if (!part.Ended || !sum.IsKnown)
            {
                allKnown = false;
            }
            else if (sum > longest)
            {
                longest = sum;
            }
        }

        if (!_duration.IsKnown && allKnown)
        {
            _duration = longest;
        }

        int index2 = 0;
        foreach (PlannedRendition part in parts)
        {
            Rendition r = new(part, index2++);
            _renditions.Add(r);
            if (part.Segments.Count == 0)
            {
                continue;
            }

            try
            {
                // The first segment decides the tracks, so a segment that cannot be read is an error here, where later ones are skipped with a defect.
                await OpenNextAsync(r, true, ct).ConfigureAwait(false);
            }
            catch (MediaException) when (_protected)
            {
                _defects.Add(DefectKind.Unsupported, DefectSeverity.Warning, 0, "The segments are encrypted; the tracks cannot be listed.", "presentation.protected");
                r.Inner = null;
                r.NextSegment = part.Segments.Count;
            }

            if (r.Inner is not null)
            {
                foreach (MediaTrack t in r.Inner.Tracks)
                {
                    _tracks.Add(t with { Index = _tracks.Count, Language = t.Language ?? part.Language, Name = t.Name ?? part.Name, DurationIsPartial = t.DurationIsPartial || !part.Ended });
                    _enabled.Add(true);
                }

                r.BuildMap(_tracks, _tracks.Count - r.Inner.Tracks.Count);
                r.ApplyEnabled(_enabled);
            }
        }

        if (_tracks.Count == 0 && !_protected)
        {
            throw new MediaFormatException(new MediaDefect(DefectKind.MissingRequired, DefectSeverity.Error, 0, "The presentation lists no segments to read.", "presentation.no-segments"));
        }
    }

    private static bool TryParseIndexBox(byte[] box, long offset, out SegmentIndexBox? index)
    {
        index = null;
        if (box.Length < 12 || box[4] != 's' || box[5] != 'i' || box[6] != 'd' || box[7] != 'x')
        {
            return false;
        }

        long size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(box);
        int header = 8;
        if (size == 1 && box.Length >= 16)
        {
            size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(box.AsSpan(8));
            header = 16;
        }
        else if (size == 0)
        {
            size = box.Length;
        }

        return SegmentIndexBox.TryParse(box.AsSpan(header), offset, offset + size, out index);
    }

    private static string DecodeText(byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        if (span.StartsWith("\uFEFF"u8))
        {
            span = span[3..];
        }

        return Encoding.UTF8.GetString(span);
    }

    // ---- reading ----------------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public bool TryReadPacket(out PacketView packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValueTask<bool> pending = AdvanceAsync(CancellationToken.None);
        bool ok = pending.IsCompletedSuccessfully ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
        packet = ok ? Current : default;
        return ok;
    }

    /// <inheritdoc />
    public ValueTask<bool> ReadPacketAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return AdvanceAsync(cancellationToken);
    }

    private async ValueTask<bool> AdvanceAsync(CancellationToken ct)
    {
        if (_lastReturned is not null)
        {
            _lastReturned.HeldValid = false;
            _lastReturned = null;
        }

        _current = null;
        if (_protected)
        {
            return false;
        }

        Rendition? best = null;
        MediaTime bestKey = default;
        for (int i = 0; i < _renditions.Count; i++)
        {
            Rendition r = _renditions[i];
            if (!r.HeldValid && !r.Ended)
            {
                await FillAsync(r, ct).ConfigureAwait(false);
            }

            if (r.HeldValid)
            {
                MediaTime key = r.HeldInfo.Dts.IsKnown ? r.HeldInfo.Dts : r.HeldInfo.Pts;
                if (best is null || (key.IsKnown && (!bestKey.IsKnown || key < bestKey)))
                {
                    best = r;
                    bestKey = key;
                }
            }
        }

        if (best is null)
        {
            return false;
        }

        _lastReturned = best;
        _current = best;
        _currentInfo = best.HeldInfo;
        return true;
    }

    // Reads the next packet of a rendition into its holding buffer, opening the next segment when the current one runs out.
    private async ValueTask FillAsync(Rendition r, CancellationToken ct)
    {
        while (!r.HeldValid && !r.Ended)
        {
            ct.ThrowIfCancellationRequested();
            if (r.Inner is null && !await OpenNextAsync(r, false, ct).ConfigureAwait(false))
            {
                r.Ended = true;
                return;
            }

            try
            {
                if (TryReadInner(r))
                {
                    return;
                }
            }
            catch (MediaLimitExceededException)
            {
                throw;
            }
            catch (MediaException e)
            {
                _defects.Add(DefectKind.Inconsistent, DefectSeverity.Error, e.Offset, $"Segment {r.SegmentNumber}: {e.Message}", "presentation.bad-segment");
            }

            CloseSegment(r);
        }
    }

    // One packet from the open segment into the holding buffer, with its track and times mapped. Returns false at the end of the segment.
    private bool TryReadInner(Rendition r)
    {
        while (r.Inner!.TryReadPacket(out PacketView view))
        {
            PacketInfo i = view.Info;
            if ((uint)i.TrackIndex >= (uint)r.Map.Length || r.Map[i.TrackIndex] < 0)
            {
                continue;
            }

            int outer = r.Map[i.TrackIndex];
            _limits.CheckPacket(view.Data.Length, -1);
            if (r.Held.Length < view.Data.Length)
            {
                ArrayPool<byte>.Shared.Return(r.Held);
                r.Held = ArrayPool<byte>.Shared.Rent(Math.Max(view.Data.Length, 4096));
            }

            view.Data.CopyTo(r.Held);
            r.HeldLength = view.Data.Length;
            long scale = _tracks[outer].Timescale;
            if (r.Resync)
            {
                // A discontinuity without a manifest timeline: continue from where the packets before it ended.
                MediaTime first = i.Dts.IsKnown ? i.Dts : i.Pts;
                r.Offset = first.IsKnown && r.LastEnd.IsKnown ? r.LastEnd.Subtract(first, MediaRounding.NearestAwayFromZero) : MediaTime.Zero;
                r.Resync = false;
            }

            MediaTime pts = Shift(i.Pts, r.Offset, scale);
            MediaTime dts = Shift(i.Dts, r.Offset, scale);
            MediaTime dur = i.Duration.IsKnown && i.Duration.Timescale != scale ? i.Duration.Rescale(scale, MediaRounding.NearestAwayFromZero) : i.Duration;
            PacketAttributes flags = i.Flags;
            int local = outer - r.FirstTrack;
            if (r.FlagPending[local])
            {
                flags |= PacketAttributes.Discontinuity;
                r.FlagPending[local] = false;
            }

            MediaTime start = dts.IsKnown ? dts : pts;
            if (start.IsKnown)
            {
                MediaTime end = dur.IsKnown ? start.Add(dur, MediaRounding.NearestAwayFromZero) : start;
                if (!r.LastEnd.IsKnown || end > r.LastEnd)
                {
                    r.LastEnd = end;
                }
            }

            r.HeldInfo = new PacketInfo(outer, pts, dts, dur, flags, ByteRange.None, view.Data.Length, i.DiscardPadding);
            r.HeldValid = true;
            return true;
        }

        return false;
    }

    private static MediaTime Shift(MediaTime t, MediaTime offset, long scale)
    {
        if (!t.IsKnown)
        {
            return t;
        }

        if (!offset.IsZero)
        {
            t = t.Add(offset, MediaRounding.NearestAwayFromZero);
        }

        return t.Timescale == scale ? t : t.Rescale(scale, MediaRounding.NearestAwayFromZero);
    }

    // ---- segments ---------------------------------------------------------------------------------------------------------------------

    // Opens the rendition's next segment. Returns false when there are no more. A segment that cannot be fetched or recognised is skipped with a
    // defect, except the very first, which decides the tracks.
    private async ValueTask<bool> OpenNextAsync(Rendition r, bool first, CancellationToken ct)
    {
        while (true)
        {
            if (r.NextSegment >= r.Plan.Segments.Count && !await ExtendLiveAsync(r, ct).ConfigureAwait(false))
            {
                return false;
            }

            PlannedSegment seg = r.Plan.Segments[r.NextSegment];
            r.SegmentNumber = r.NextSegment + (int)Math.Min(r.Dropped, int.MaxValue);
            r.NextSegment++;
            Fetched fetched = default;
            try
            {
                fetched = await FetchAsync(r, seg, ct).ConfigureAwait(false);
                IMediaSource source = new MemoryMediaSource(fetched.Buffer.AsMemory(0, fetched.Length));
                DefectLog own = new(_limits);
                IDemuxer inner = _segmentFormats.Open(source, new MediaOpenOptions { Limits = _limits, Defects = own, LeaveOpen = false }, ct);
                r.Inner = inner;
                r.Buffer = fetched.Buffer;
                r.InnerDefects = own;
                r.InnerDefectsCopied = 0;
            }
            catch (MediaLimitExceededException)
            {
                Return(fetched.Buffer);
                throw;
            }
            catch (MediaException e) when (!first)
            {
                Return(fetched.Buffer);
                _defects.Add(DefectKind.Inconsistent, DefectSeverity.Error, e.Offset, $"Segment {r.SegmentNumber} was skipped: {e.Message}", "presentation.bad-segment");
                continue;
            }
            catch
            {
                Return(fetched.Buffer);
                throw;
            }

            if (seg.TimelineOffset is { } fixedOffset)
            {
                r.Offset = fixedOffset;
                r.Resync = false;
            }
            else if (seg.Discontinuity && r.LastEnd.IsKnown)
            {
                r.Resync = true;
            }

            if (seg.Discontinuity && r.Emitted)
            {
                Array.Fill(r.FlagPending, true);
            }

            if (r.Map.Length > 0 || r.TrackCount > 0)
            {
                r.RemapFor(r.Inner!, _tracks);
                r.ApplyEnabled(_enabled);
            }

            return true;
        }
    }

    private void CloseSegment(Rendition r)
    {
        if (r.Inner is null)
        {
            return;
        }

        if (r.InnerDefects is { } own)
        {
            for (int i = r.InnerDefectsCopied; i < own.Count; i++)
            {
                MediaDefect d = own[i];
                _defects.Add(d.Kind, d.Severity, d.Offset, $"Segment {r.SegmentNumber}: {d.Message}", d.Code);
            }
        }

        r.Inner.Dispose();
        r.Inner = null;
        Return(r.Buffer);
        r.Buffer = null;
        r.Emitted = true;
        if (!r.Plan.Ended)
        {
            // A live presentation drops the segments it has read, so memory does not grow with the broadcast.
            if (r.NextSegment >= 64)
            {
                r.Plan.Segments.RemoveRange(0, r.NextSegment);
                r.Dropped += r.NextSegment;
                r.NextSegment = 0;
            }
        }
    }

    private static void Return(byte[]? buffer)
    {
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // A live HLS playlist is loaded again under the live policy; the segments it adds after the last one read are queued.
    private async ValueTask<bool> ExtendLiveAsync(Rendition r, CancellationToken ct)
    {
        PlannedRendition plan = r.Plan;
        if (plan.Ended || _options.LivePolicy is not { } policy || plan.PlaylistLocation is null || _format != MediaFormatId.Hls)
        {
            return false;
        }

        long lastSequence = plan.Segments.Count > 0 ? plan.Segments[^1].Sequence : plan.FirstSequence - 1 + r.Dropped;
        GrowthPolicy.Episode wait = policy.Begin();
        while (true)
        {
            byte[] bytes = await ReadWholeAsync(plan.PlaylistLocation, _limits.MaxTagBytes, ct).ConfigureAwait(false);
            if (HlsReader.IsPlaylist(bytes))
            {
                PlannedRendition fresh = HlsReader.ReadMedia(DecodeText(bytes), plan.PlaylistLocation, _limits, _defects);
                int before = plan.Segments.Count;
                foreach (PlannedSegment s in fresh.Segments)
                {
                    if (s.Sequence > lastSequence)
                    {
                        plan.Segments.Add(s);
                    }
                }

                plan.Protected |= fresh.Protected;
                if (fresh.Protected)
                {
                    _protected = true;
                }

                if (fresh.Ended)
                {
                    plan.Ended = true;
                }

                if (plan.Segments.Count > before)
                {
                    return true;
                }

                if (plan.Ended)
                {
                    return false;
                }
            }

            if (!await wait.WaitAsync(ct).ConfigureAwait(false))
            {
                plan.Ended = true;
                return false;
            }
        }
    }

    private async ValueTask<Fetched> FetchAsync(Rendition r, PlannedSegment seg, CancellationToken ct)
    {
        byte[]? init = null;
        if (seg.Init is { } map)
        {
            if (r.InitMap != map || r.InitBytes is null)
            {
                r.InitBytes = await ReadRangeAsync(map.Location, map.Offset, map.Length, _limits.MaxSegmentBytes, ct).ConfigureAwait(false);
                r.InitMap = map;
            }

            init = r.InitBytes;
        }

        int initLength = init?.Length ?? 0;
        IAsyncMediaSource src = await SourceFor(seg.Location, ct).ConfigureAwait(false);
        long length = seg.Length;
        if (length < 0)
        {
            long total = src.Length;
            length = total >= 0 ? Math.Max(0, total - seg.Offset) : -1;
        }

        byte[] buffer;
        int got;
        if (length >= 0)
        {
            _limits.CheckSegmentBytes(initLength + length, seg.Offset);
            buffer = ArrayPool<byte>.Shared.Rent((int)(initLength + length));
            try
            {
                got = await ReadFullyAsync(src, seg.Offset, buffer.AsMemory(initLength, (int)length), ct).ConfigureAwait(false);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }

            if (got < length)
            {
                _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, seg.Offset + got, $"Segment {r.SegmentNumber + 1} ends {length - got} bytes before the length the playlist gives.", "presentation.short-segment");
            }
        }
        else
        {
            // The source cannot say how long it is and the playlist gives no range: read to the end, growing the buffer.
            buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initLength + 65536, 65536));
            got = 0;
            try
            {
                while (true)
                {
                    if (initLength + got == buffer.Length)
                    {
                        _limits.CheckSegmentBytes(buffer.Length + 1L, seg.Offset + got);
                        byte[] bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min(int.MaxValue / 2, buffer.Length * 2L));
                        Buffer.BlockCopy(buffer, 0, bigger, 0, initLength + got);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = bigger;
                    }

                    int n = await src.ReadAsync(seg.Offset + got, buffer.AsMemory(initLength + got), ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break;
                    }

                    got += n;
                }
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
        }

        if (init is not null)
        {
            init.CopyTo(buffer, 0);
        }

        return new Fetched(buffer, initLength + got);
    }

    private readonly record struct Fetched(byte[] Buffer, int Length);

    private static async ValueTask<int> ReadFullyAsync(IAsyncMediaSource source, long offset, Memory<byte> destination, CancellationToken ct)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = await source.ReadAsync(offset + total, destination[total..], ct).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    // A source for a location, kept for the next fetch: a presentation made of byte ranges of one file reads that file again and again.
    private async ValueTask<IAsyncMediaSource> SourceFor(Uri uri, CancellationToken ct)
    {
        for (int i = 0; i < _sources.Count; i++)
        {
            if (_sources[i].Uri == uri)
            {
                (Uri, IAsyncMediaSource) hit = _sources[i];
                _sources.RemoveAt(i);
                _sources.Add(hit);
                return hit.Item2;
            }
        }

        IAsyncMediaSource source = await _resolver.OpenAsync(uri, ct).ConfigureAwait(false);
        _sources.Add((uri, source));
        while (_sources.Count > CachedSources)
        {
            IAsyncMediaSource old = _sources[0].Source;
            _sources.RemoveAt(0);
            await old.DisposeAsync().ConfigureAwait(false);
        }

        return source;
    }

    private async ValueTask<byte[]> ReadWholeAsync(Uri location, long maxBytes, CancellationToken ct)
    {
        IAsyncMediaSource src = await _resolver.OpenAsync(location, ct).ConfigureAwait(false);
        try
        {
            return await ReadRangeFromAsync(src, 0, -1, maxBytes, ct).ConfigureAwait(false);
        }
        finally
        {
            await src.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<byte[]> ReadRangeAsync(Uri location, long offset, long length, long maxBytes, CancellationToken ct)
    {
        IAsyncMediaSource src = await SourceFor(location, ct).ConfigureAwait(false);
        return await ReadRangeFromAsync(src, offset, length, maxBytes, ct).ConfigureAwait(false);
    }

    private static async ValueTask<byte[]> ReadRangeFromAsync(IAsyncMediaSource src, long offset, long length, long maxBytes, CancellationToken ct)
    {
        if (length < 0 && src.Length >= 0)
        {
            length = Math.Max(0, src.Length - offset);
        }

        if (length >= 0)
        {
            if (length > maxBytes)
            {
                throw new MediaLimitExceededException("MaxSegmentBytes", maxBytes, length, offset);
            }

            byte[] exact = new byte[length];
            int n = await ReadFullyAsync(src, offset, exact, ct).ConfigureAwait(false);
            return n == length ? exact : exact.AsSpan(0, n).ToArray();
        }

        byte[] buffer = new byte[16384];
        int total = 0;
        while (true)
        {
            if (total == buffer.Length)
            {
                if (buffer.Length >= maxBytes)
                {
                    throw new MediaLimitExceededException("MaxSegmentBytes", maxBytes, total + 1L, offset + total);
                }

                Array.Resize(ref buffer, (int)Math.Min(maxBytes, buffer.Length * 2L));
            }

            int n = await src.ReadAsync(offset + total, buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return buffer.AsSpan(0, total).ToArray();
    }

    // ---- seeking ----------------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe) =>
        SeekAsync(target, mode, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask<MediaTime> SeekAsync(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lastReturned = null;
        _current = null;
        foreach (Rendition r in _renditions)
        {
            CloseSegment(r);
            r.HeldValid = false;
            r.Ended = false;
            r.Emitted = false;
            r.Offset = MediaTime.Zero;
            r.LastEnd = MediaTime.Unknown;
            r.Resync = false;
            Array.Clear(r.FlagPending);
            r.NextSegment = SegmentFor(r, target, mode);
        }

        MediaTime earliest = MediaTime.Unknown;
        if (_protected)
        {
            return earliest;
        }

        foreach (Rendition r in _renditions)
        {
            await FillAsync(r, cancellationToken).ConfigureAwait(false);
            if (r.HeldValid)
            {
                MediaTime t = r.HeldInfo.Dts.IsKnown ? r.HeldInfo.Dts : r.HeldInfo.Pts;
                if (t.IsKnown && (!earliest.IsKnown || t < earliest))
                {
                    earliest = t;
                }
            }
        }

        return earliest;
    }

    // The segment whose stated duration covers the target; segment 0 when the target is before the start or the durations are not stated.
    private static int SegmentFor(Rendition r, MediaTime target, SeekMode mode)
    {
        MediaTime start = MediaTime.Zero;
        for (int i = 0; i < r.Plan.Segments.Count; i++)
        {
            MediaTime d = r.Plan.Segments[i].Duration;
            if (!d.IsKnown)
            {
                return 0;
            }

            MediaTime end = start.Add(d, MediaRounding.NearestAwayFromZero);
            if (target < end)
            {
                return mode == SeekMode.NextKeyframe && target > start && i + 1 < r.Plan.Segments.Count ? i + 1 : i;
            }

            start = end;
        }

        return Math.Max(0, r.Plan.Segments.Count - 1);
    }

    // ---- metadata ---------------------------------------------------------------------------------------------------------------------

    /// <summary>The presentation's metadata: its duration. Playlists carry no tags.</summary>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>The metadata.</returns>
    public MediaMetadata ReadMetadata(CancellationToken cancellationToken = default)
    {
        FactsBuilder facts = new(_defects);
        if (_duration.IsKnown)
        {
            facts.Duration.Add(0, new FactCandidate<MediaTime>(_duration, "playlist", _duration.ToString(), "sum of the segment durations the playlist states"));
        }

        return new MediaMetadata([], [], facts.Build(), _defects);
    }

    /// <inheritdoc />
    public ValueTask<MediaMetadata> ReadMetadataAsync(CancellationToken cancellationToken = default) => new(ReadMetadata(cancellationToken));

    // ---- disposal ---------------------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Rendition r in _renditions)
        {
            CloseSegment(r);
            ArrayPool<byte>.Shared.Return(r.Held);
            r.Held = [];
        }

        foreach ((Uri _, IAsyncMediaSource source) in _sources)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }

        _sources.Clear();
    }

    // ---- per-rendition state ----------------------------------------------------------------------------------------------------------

    private sealed class Rendition(PlannedRendition plan, int index)
    {
        public readonly PlannedRendition Plan = plan;
        public readonly int Index = index;
        public int FirstTrack;
        public int TrackCount;
        public int[] Map = [];
        public bool[] FlagPending = [];
        public int NextSegment;
        public int SegmentNumber;
        public long Dropped;
        public IDemuxer? Inner;
        public byte[]? Buffer;
        public DefectLog? InnerDefects;
        public int InnerDefectsCopied;
        public MediaTime Offset = MediaTime.Zero;
        public MediaTime LastEnd = MediaTime.Unknown;
        public bool Resync;
        public bool Emitted;
        public bool Ended;
        public byte[] Held = ArrayPool<byte>.Shared.Rent(4096);
        public int HeldLength;
        public PacketInfo HeldInfo;
        public bool HeldValid;
        public PlannedMap? InitMap;
        public byte[]? InitBytes;

        // The first segment's tracks become the presentation's tracks for this rendition.
        public void BuildMap(List<MediaTrack> all, int firstTrack)
        {
            FirstTrack = firstTrack;
            TrackCount = Inner!.Tracks.Count;
            FlagPending = new bool[TrackCount];
            Map = new int[TrackCount];
            for (int i = 0; i < TrackCount; i++)
            {
                Map[i] = firstTrack + i;
            }
        }

        // A later segment's tracks are matched to the presentation's by kind and the container's track id, then by kind and order.
        public void RemapFor(IDemuxer inner, List<MediaTrack> all)
        {
            if (TrackCount == 0)
            {
                Map = new int[inner.Tracks.Count];
                Array.Fill(Map, -1);
                return;
            }

            int[] map = new int[inner.Tracks.Count];
            Array.Fill(map, -1);
            bool[] taken = new bool[TrackCount];
            for (int i = 0; i < map.Length; i++)
            {
                MediaTrack t = inner.Tracks[i];
                for (int k = 0; k < TrackCount; k++)
                {
                    MediaTrack o = all[FirstTrack + k];
                    if (!taken[k] && o.Kind == t.Kind && o.Id == t.Id)
                    {
                        map[i] = FirstTrack + k;
                        taken[k] = true;
                        break;
                    }
                }
            }

            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] >= 0)
                {
                    continue;
                }

                for (int k = 0; k < TrackCount; k++)
                {
                    if (!taken[k] && all[FirstTrack + k].Kind == inner.Tracks[i].Kind)
                    {
                        map[i] = FirstTrack + k;
                        taken[k] = true;
                        break;
                    }
                }
            }

            Map = map;
        }

        public void ApplyEnabled(List<bool> enabled)
        {
            if (Inner is null)
            {
                return;
            }

            for (int i = 0; i < Map.Length; i++)
            {
                if (Map[i] >= 0 && Map[i] < enabled.Count)
                {
                    Inner.SetTrackEnabled(i, enabled[Map[i]]);
                }
            }
        }
    }
}
