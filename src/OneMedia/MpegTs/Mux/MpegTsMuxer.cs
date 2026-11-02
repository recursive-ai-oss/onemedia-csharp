using System.Buffers;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Aac;

namespace RecursiveAI.OneMedia.MpegTs;

/// <summary>
/// Writes an MPEG transport stream (ITU-T H.222.0 / ISO/IEC 13818-1) with one program from packets in decode order, as 188-byte TS or
/// 192-byte M2TS. Nothing seeks: every byte goes to the output as soon as its packet is written, so the output may be a pipe.
/// </summary>
/// <remarks>
/// <para><b>What is written.</b> A PAT and a PMT (repeated every <see cref="MpegTsMuxOptions.PsiIntervalMilliseconds"/> and at every segment start),
/// then each packet as one PES packet (§2.4.3.6) or, for SCTE-35, as sections. PTS and DTS are 90 kHz, moved so the first decode time sits
/// <see cref="MpegTsMuxOptions.InitialDelay"/> after zero; the program clock reference rides in the adaptation field of the first transport
/// packet of every PES packet of the PCR track (the first video track, else the first track), with PCR-only packets filling any gap longer than
/// <see cref="MpegTsMuxOptions.PcrIntervalMilliseconds"/>. Continuity counters count per PID (§2.4.3.3) and carry on across segments.</para>
/// <para><b>Payloads.</b> Media bytes are copied. The framing the format requires is added or converted through the codec layer: H.264, HEVC
/// and VVC access units go out as Annex B (length-prefixed samples of an <c>avcC</c>/<c>hvcC</c>/<c>vvcC</c> track are converted; Annex B tracks are
/// copied), each with an access unit delimiter and, on a keyframe without them, the track's parameter sets in front; AAC goes out as ADTS
/// (raw access units are wrapped using the AudioSpecificConfig; ADTS frames are copied). Every addition is a whole NAL unit or header, and
/// removing them gives back the input bytes.</para>
/// <para><b>Tracks.</b> H.264, HEVC, VVC, MPEG-1/2 video, AAC, MP1/2/3, AC-3, E-AC-3, DVB subtitles and teletext (PES payloads as given),
/// timed ID3 and SCTE-35. A track of another codec is refused with <see cref="MuxRefusalReason.UnsupportedCodec"/>, one with a missing or
/// unparseable configuration with <see cref="MuxRefusalReason.MissingConfiguration"/> or <see cref="MuxRefusalReason.InvalidConfiguration"/>.</para>
/// <para><b>Segments.</b> <see cref="StartSegment"/> switches the output to a new stream between two packets, so the next packet starts the
/// new segment, with PAT and PMT in front of it and counters unbroken. <see cref="CanStartSegmentAt"/> says whether a packet is a good place.</para>
/// <para>Memory is a few packets' worth of buffers and does not depend on the length of the stream. Not thread-safe. The caller owns the
/// output streams.</para>
/// </remarks>
public sealed class MpegTsMuxer : IMuxer
{
    private const long Clock90 = 90_000;
    private const long Mask33 = (1L << 33) - 1;
    private const int MaxPcrFillPackets = 100;
    private const int SmallAudioBytes = 32;
    private const int MaxPendingBytes = 2048;

    private readonly MpegTsMuxOptions _o;
    private readonly MediaLimits _limits;
    private readonly int _packetSize;
    private readonly int _prefix;
    private readonly List<MediaTrack> _declared = [];
    private readonly List<TsTrack> _tracks = [];
    private readonly List<string> _notes = [];
    private Stream _out;
    private readonly byte[] _buffer;
    private int _buffered;
    private long _bytesWritten;
    private long _segmentBytes;
    private int _segment;
    private bool _started;
    private bool _finished;
    private bool _noteRounded;
    private bool _noteDtsFromPts;
    private bool _notePieces;

    // Clock.
    private bool _haveOrigin;
    private long _offset90;
    private readonly long _initialDelay90;
    private readonly long _lead90;
    private long _clock27;
    private long _lastPcr27 = -1;
    private long _lastPsi27 = long.MinValue;
    private long _step27;
    private bool _psiDue = true;
    private int _pcrTrack = -1;

    // PSI.
    private byte _patCc;
    private byte _pmtCc;
    private byte[]? _pat;
    private byte[]? _pmt;

    // Scratch.
    private byte[] _pes = new byte[1 << 16];
    private byte[] _au = new byte[1 << 16];
    private byte[] _prefixBytes = new byte[1 << 12];

    /// <summary>Creates a muxer over an output stream. Declare the tracks with <see cref="AddTrack"/> before the first packet.</summary>
    /// <param name="output">Where the stream goes; any writable stream (no seeking). It starts at the stream's current position. Not closed by the muxer.</param>
    /// <param name="options">Settings; defaults when null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    public MpegTsMuxer(Stream output, MpegTsMuxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _o = options ?? new MpegTsMuxOptions();
        _limits = _o.Limits ?? MediaLimits.Default;
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream is not writable.", nameof(output));
        }

        if (_o.Layout is not (TsPacketLayout.Ts188 or TsPacketLayout.M2ts192))
        {
            throw new ArgumentException("Unknown packet layout.", nameof(options));
        }

        if (_o.ProgramNumber is < 1 or > 0xFFFF || _o.TransportStreamId is < 0 or > 0xFFFF
            || _o.PmtPid is < 0x20 or > 0x1FFE || _o.FirstElementaryPid is < 0x20 or > 0x1FFE || _o.FirstElementaryPid == _o.PmtPid
            || _o.PcrIntervalMilliseconds is < 10 or > 100 || _o.PsiIntervalMilliseconds is < 10 or > 500)
        {
            throw new ArgumentException("A program number, transport stream id, PID or interval is out of range.", nameof(options));
        }

        if (!_o.InitialDelay.IsKnown || !_o.PcrLead.IsKnown || _o.InitialDelay < _o.PcrLead || _o.PcrLead < MediaTime.Zero)
        {
            throw new ArgumentException("InitialDelay must not be shorter than PcrLead, and neither may be negative.", nameof(options));
        }

        _out = output;
        _packetSize = (int)_o.Layout;
        _prefix = _packetSize - 188;
        _buffer = new byte[_packetSize * 256];
        _initialDelay90 = _o.InitialDelay.Rescale(Clock90, MediaRounding.NearestAwayFromZero).Ticks;
        _lead90 = _o.PcrLead.Rescale(Clock90, MediaRounding.NearestAwayFromZero).Ticks;
    }

    /// <inheritdoc/>
    public MediaFormatId Format => MediaFormatId.MpegTs;

    /// <inheritdoc/>
    public IReadOnlyList<MediaTrack> Tracks => _declared;

    /// <summary>The PID each track was given, in output order.</summary>
    public IReadOnlyList<int> Pids
    {
        get
        {
            int[] pids = new int[_tracks.Count];
            for (int i = 0; i < pids.Length; i++)
            {
                pids[i] = _tracks[i].Pid;
            }

            return pids;
        }
    }

    /// <summary>Index of the track whose keyframes are the natural segment boundaries (the PCR track: the first video track, else the first track); -1 before a track is added.</summary>
    public int SyncTrackIndex => _pcrTrack;

    /// <summary>Number of segments started so far, counting the first (0 before the first packet).</summary>
    public int SegmentIndex => _segment;

    /// <summary>Bytes written to the current segment's stream (buffered bytes included).</summary>
    public long SegmentBytes => _segmentBytes;

    /// <inheritdoc/>
    public int AddTrack(MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (_started || _finished)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "A track cannot be added after the first packet.");
        }

        int index = _tracks.Count;
        if (index >= _limits.MaxTracks || FirstPid(index) > 0x1FFE)
        {
            throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"Track {index}: too many tracks.", index);
        }

        int video = 0, audio = 0;
        foreach (TsTrack t in _tracks)
        {
            if (t.IsVideo)
            {
                video++;
            }
            else if (t.Kind is TsPayloadKind.AacAdts or TsPayloadKind.MpegAudio)
            {
                audio++;
            }
        }

        if (video >= 16 || audio >= 32)
        {
            throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"Track {index}: a PES stream_id range (16 video, 32 audio streams) is used up.", index);
        }

        TsTrack plan = TsTrackPlanner.Plan(track, index, FirstPid(index), video, audio, _notes);
        // The PMT section must fit one section (§2.4.4.8: 1021 bytes).
        int es = 5 + plan.Descriptors.Length;
        foreach (TsTrack t in _tracks)
        {
            es += 5 + t.Descriptors.Length;
        }

        if (16 + es > 1021)
        {
            throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"Track {index}: the program map table would not fit one section.", index, track.Codec);
        }

        _tracks.Add(plan);
        _declared.Add(track with { Index = index });
        if (_pcrTrack < 0 || (plan.IsVideo && !_tracks[_pcrTrack].IsVideo))
        {
            _pcrTrack = index;
        }

        _pmt = null;
        return index;
    }

    private int FirstPid(int index)
    {
        int pid = _o.FirstElementaryPid + index;
        // Skip the PMT PID.
        return _o.PmtPid >= _o.FirstElementaryPid && pid >= _o.PmtPid ? pid + 1 : pid;
    }

    /// <summary>True when starting a new segment right before <paramref name="info"/> gives a decodable segment: the packet is a keyframe of <see cref="SyncTrackIndex"/>.</summary>
    /// <param name="info">The packet about to be written (its output track index).</param>
    /// <returns>True for a keyframe of the sync track.</returns>
    public bool CanStartSegmentAt(in PacketInfo info) => info.TrackIndex == _pcrTrack && _pcrTrack >= 0 && info.IsKeyframe;

    /// <summary>
    /// Ends the current segment and makes <paramref name="next"/> the output: the packet written next starts a new segment, preceded by a PAT and
    /// a PMT, with a PCR on its first video packet, and with continuity counters carried on. Bytes already written stay in the old stream (it is
    /// flushed, not closed). Call it between two <see cref="WritePacket"/> calls, before the keyframe that should open the segment; see
    /// <see cref="CanStartSegmentAt"/>. Called before the first packet it only redirects the output.
    /// </summary>
    /// <param name="next">The new destination; writable, not closed by the muxer.</param>
    /// <param name="cancellationToken">Checked before the old stream is flushed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> is null.</exception>
    /// <exception cref="MuxRefusedException">After <see cref="Finish"/> (<see cref="MuxRefusalReason.InvalidState"/>), or the stream is not writable.</exception>
    public void StartSegment(Stream next, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        cancellationToken.ThrowIfCancellationRequested();
        if (_finished)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "The muxer is finished.");
        }

        if (!next.CanWrite)
        {
            throw new MuxRefusedException(MuxRefusalReason.OutputNotSuitable, "The segment stream is not writable.");
        }

        FlushAllPending();
        FlushBuffer();
        _out.Flush();
        _out = next;
        _segmentBytes = 0;
        _psiDue = true;
        if (_started)
        {
            _segment++;
        }
    }

    /// <inheritdoc/>
    public void WritePacket(in PacketInfo info, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_finished)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "The muxer is finished.");
        }

        if ((uint)info.TrackIndex >= (uint)_tracks.Count)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, $"Packet for track {info.TrackIndex}, which was not added.", info.TrackIndex);
        }

        TsTrack t = _tracks[info.TrackIndex];
        if (info.Size != data.Length || data.Length > _limits.MaxPacketBytes)
        {
            throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"Track {info.TrackIndex}: packet of {data.Length} bytes (declared {info.Size}) is over the packet limit or inconsistent.", info.TrackIndex, t.Declared.Codec);
        }

        MediaTime dts = info.Dts.IsKnown ? info.Dts : info.Pts;
        MediaTime pts = info.Pts.IsKnown ? info.Pts : info.Dts;
        if (!dts.IsKnown)
        {
            // A packet with no time at all (a transport stream picture without a PES time stamp) continues where the track's last packet ended.
            if (!t.LastDts.IsKnown)
            {
                throw new MuxRefusedException(MuxRefusalReason.MissingTimestamp, $"Track {info.TrackIndex}: the first packet has neither a presentation nor a decode time.", info.TrackIndex, t.Declared.Codec);
            }

            dts = t.LastDuration.IsKnown ? t.LastDts.Add(t.LastDuration, MediaRounding.NearestAwayFromZero) : t.LastDts;
            pts = dts;
            if (!t.NotedMissingDts)
            {
                t.NotedMissingDts = true;
                _notes.Add($"Track {info.TrackIndex}: packets without any time took the previous packet's decode time plus its duration.");
            }
        }

        if (!info.Dts.IsKnown && t.IsVideo && !_noteDtsFromPts)
        {
            _noteDtsFromPts = true;
            _notes.Add("A video packet had no decode time; its presentation time was used, which is right only without frame reordering.");
        }

        if (t.LastDts.IsKnown && dts < t.LastDts)
        {
            throw new MuxRefusedException(MuxRefusalReason.NonMonotonicDts, $"Track {info.TrackIndex}: decode time {dts} is before the previous packet's {t.LastDts}.", info.TrackIndex, t.Declared.Codec);
        }

        long dts90 = To90(dts, info.TrackIndex, t);
        long pts90 = pts == dts ? dts90 : To90(pts, info.TrackIndex, t);
        if (!_haveOrigin)
        {
            _offset90 = _initialDelay90 - dts90;
            _haveOrigin = true;
        }

        dts90 += _offset90;
        pts90 += _offset90;
        if (dts90 < 0 || pts90 < 0)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, $"Track {info.TrackIndex}: time {dts} is more than the initial delay before the first packet; the stream cannot start before zero.", info.TrackIndex, t.Declared.Codec);
        }

        // Build the payload first: a refusal for bad data must leave nothing half-written.
        if (t.Kind == TsPayloadKind.Sections)
        {
            if (data.Length is < 3 or > 4096)
            {
                throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, $"Track {info.TrackIndex}: a section of {data.Length} bytes cannot be carried (3 to 4096).", info.TrackIndex, t.Declared.Codec);
            }

            _started = true;
            Segment0();
            ClockAndPsi(dts90);
            long ats = _clock27;
            WritePayload(t.Pid, ref t.Cc, data, section: true, pcr27: -1, randomAccess: false, ats);
            Account(t, info, data.Length, dts);
            return;
        }

        _started = true;
        if (t.Kind is TsPayloadKind.MpegAudio or TsPayloadKind.Ac3 && data.Length < SmallAudioBytes)
        {
            // A piece too small to be a frame (QuickTime stores MPEG audio in chunks, one byte long in the worst case) is gathered with its
            // neighbours into one PES packet stamped with the first piece's time.
            if (t.PendingLength > 0 && t.PendingLength + data.Length > MaxPendingBytes)
            {
                FlushPending(t);
            }

            if (t.PendingLength == 0)
            {
                t.PendingPts = pts90;
                t.PendingDts = dts90;
                t.PendingKey = info.IsKeyframe;
                t.Pending ??= new byte[MaxPendingBytes];
            }

            data.CopyTo(t.Pending.AsSpan(t.PendingLength));
            t.PendingLength += data.Length;
            Account(t, info, data.Length, dts);
            if (!_notePieces)
            {
                _notePieces = true;
                _notes.Add("Audio packets under 32 bytes were gathered into PES packets of up to 2048 bytes.");
            }

            return;
        }

        int prefixLength = 0, splitAt = 0;
        ReadOnlySpan<byte> body = Build(t, info, data, ref prefixLength, ref splitAt);
        ReadOnlySpan<byte> prefixPart = _prefixBytes.AsSpan(0, prefixLength);
        ReadOnlySpan<byte> head = body[..splitAt];
        ReadOnlySpan<byte> tail = body[splitAt..];

        FlushPending(t);
        WritePes(t, info.TrackIndex, pts90, dts90, info.IsKeyframe, head, prefixPart, tail);
        Account(t, info, data.Length, dts);
    }

    // One PES packet (ISO/IEC 13818-1 section 2.4.3.6) holding head, prefix and tail in that order, sent as transport packets.
    private void WritePes(TsTrack t, int index, long pts90, long dts90, bool key, ReadOnlySpan<byte> head, ReadOnlySpan<byte> prefixPart, ReadOnlySpan<byte> tail)
    {
        int prefixLength = prefixPart.Length;
        int pesData = prefixLength + head.Length + tail.Length;
        bool video = t.IsVideo;
        bool pesLengthField = !video;
        // A video PES packet always carries its DTS, also when it equals the PTS: a reader that finds none may assume frame reordering and guess.
        int headerLength = video ? 10 : 5;
        long packetLength = 3L + headerLength + pesData;
        if (pesLengthField && packetLength > 0xFFFF)
        {
            throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"Track {index}: a {pesData}-byte packet does not fit one PES packet.", index, t.Declared.Codec);
        }

        int total = 6 + 3 + headerLength + pesData;
        if (_pes.Length < total)
        {
            Array.Resize(ref _pes, (int)Math.Min(Math.Max(_pes.Length * 2L, total), int.MaxValue));
        }

        Span<byte> p = _pes;
        // ISO/IEC 13818-1 §2.4.3.6: packet_start_code_prefix, stream_id, PES_packet_length (0 = unbounded, allowed for video only).
        p[0] = 0;
        p[1] = 0;
        p[2] = 1;
        p[3] = (byte)t.StreamId;
        int len = video ? 0 : (int)packetLength;
        p[4] = (byte)(len >> 8);
        p[5] = (byte)len;
        p[6] = 0x84; // '10', data_alignment_indicator: each PES packet starts with a complete access unit or frame
        bool both = headerLength == 10;
        p[7] = (byte)(both ? 0xC0 : 0x80);
        p[8] = (byte)headerLength;
        WriteTimestamp(p[9..], both ? 3 : 2, pts90);
        if (both)
        {
            WriteTimestamp(p[14..], 1, dts90);
        }

        int at = 9 + headerLength;
        head.CopyTo(p[at..]);
        at += head.Length;
        prefixPart.CopyTo(p[at..]);
        at += prefixLength;
        tail.CopyTo(p[at..]);

        _started = true;
        Segment0();
        ClockAndPsi(dts90);
        long pcr = -1;
        if (index == _pcrTrack)
        {
            pcr = _clock27;
            _lastPcr27 = pcr;
        }

        WritePayload(t.Pid, ref t.Cc, _pes.AsSpan(0, total), section: false, pcr, randomAccess: video && key, _clock27);
    }

    private void FlushPending(TsTrack t)
    {
        if (t.PendingLength > 0)
        {
            int n = t.PendingLength;
            t.PendingLength = 0;
            WritePes(t, t.Index, t.PendingPts, t.PendingDts, t.PendingKey, t.Pending.AsSpan(0, n), default, default);
        }
    }

    private void FlushAllPending()
    {
        foreach (TsTrack t in _tracks)
        {
            FlushPending(t);
        }
    }

    private void Segment0()
    {
        if (_segment == 0)
        {
            _segment = 1;
        }
    }

    private static void Account(TsTrack t, in PacketInfo info, int size, MediaTime dts)
    {
        t.LastDts = dts;
        t.LastDuration = info.Duration;
        t.Packets++;
        t.PayloadBytes += size;
        if (info.Duration.IsKnown)
        {
            t.Duration = t.Duration.IsZero ? info.Duration : t.Duration.Add(info.Duration, MediaRounding.NearestAwayFromZero);
        }
    }

    // Moves a time to 90 kHz; says once when that is not exact.
    private long To90(MediaTime time, int track, TsTrack t)
    {
        if (time.Timescale == Clock90)
        {
            return time.Ticks;
        }

        if (!time.TryRescale(Clock90, MediaRounding.NearestAwayFromZero, out MediaTime r))
        {
            throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"Track {track}: time {time} does not fit the 90 kHz clock.", track, t.Declared.Codec);
        }

        if (!_noteRounded && r.Ticks * time.Timescale != time.Ticks * Clock90)
        {
            _noteRounded = true;
            _notes.Add("Times were rounded to the nearest tick of the 90 kHz transport stream clock.");
        }

        return r.Ticks;
    }

    // ---- payload framing ----

    // Returns the bytes to send after the prefix; prefix bytes (access unit delimiter, parameter sets) are left in _prefixBytes. For video the
    // prefix goes after the first NAL unit when that is a delimiter, so the body is split at splitAt.
    private ReadOnlySpan<byte> Build(TsTrack t, in PacketInfo info, ReadOnlySpan<byte> data, ref int prefixLength, ref int splitAt)
    {
        switch (t.Kind)
        {
            case TsPayloadKind.NalVideo:
                return BuildNal(t, info, data, ref prefixLength, ref splitAt);
            case TsPayloadKind.AacAdts:
                return BuildAdts(t, info, data);
            default:
                splitAt = data.Length;
                return data;
        }
    }

    private ReadOnlySpan<byte> BuildAdts(TsTrack t, in PacketInfo info, ReadOnlySpan<byte> data)
    {
        // A packet that is one complete ADTS frame is copied; anything else is a raw access unit and gets a header from the AudioSpecificConfig.
        if (AdtsHeader.TryParse(data, out AdtsHeader h) && h.FrameLength <= data.Length && (!t.HasAsc || h.SamplingFrequencyIndex == t.Asc.SamplingFrequencyIndex))
        {
            return data;
        }

        if (!t.HasAsc)
        {
            throw new MuxRefusedException(MuxRefusalReason.MissingConfiguration, $"Track {info.TrackIndex}: AAC packets are not ADTS frames and the track has no AudioSpecificConfig to wrap them with.", info.TrackIndex, t.Declared.Codec);
        }

        if (_au.Length < data.Length + 7)
        {
            Array.Resize(ref _au, data.Length + 7);
        }

        AudioSpecificConfig asc = t.Asc;
        OperationStatus s = AdtsRepackager.WrapRaw(in asc, data, _au, out int written);
        if (s != OperationStatus.Done)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidConfiguration, $"Track {info.TrackIndex}: a {data.Length}-byte AAC access unit cannot be wrapped in an ADTS frame.", info.TrackIndex, t.Declared.Codec);
        }

        return _au.AsSpan(0, written);
    }

    private ReadOnlySpan<byte> BuildNal(TsTrack t, in PacketInfo info, ReadOnlySpan<byte> data, ref int prefixLength, ref int splitAt)
    {
        ReadOnlySpan<byte> au = data;
        if (t.LengthSize > 0)
        {
            // ISO/IEC 14496-15 length-prefixed samples to Annex B.
            OperationStatus s;
            int written;
            while (true)
            {
                s = NalRepackager.LengthPrefixedToAnnexB(t.Nal, data, t.LengthSize, _au, out written);
                if (s != OperationStatus.DestinationTooSmall)
                {
                    break;
                }

                if (written <= 0 || written > _limits.MaxPacketBytes * 4)
                {
                    throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"Track {info.TrackIndex}: an access unit of {data.Length} bytes grows past the packet limit as Annex B.", info.TrackIndex, t.Declared.Codec);
                }

                _au = new byte[written];
            }

            if (s != OperationStatus.Done)
            {
                throw new MuxRefusedException(MuxRefusalReason.InvalidConfiguration, $"Track {info.TrackIndex}: a packet is not made of {t.LengthSize}-byte length-prefixed NAL units.", info.TrackIndex, t.Declared.Codec);
            }

            au = _au.AsSpan(0, written);
        }
        else if (!StartsWithStartCode(data))
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidConfiguration, $"Track {info.TrackIndex}: a packet does not start with an Annex B start code and the track has no length-size record.", info.TrackIndex, t.Declared.Codec);
        }

        // Look at the NAL units up to the first slice: a delimiter, parameter sets.
        bool first = true;
        bool hasAud = false, hasSets = false;
        int audEnd = 0;
        int setsFrom = -1, setsTo = -1;
        AnnexBReader r = new(au);
        while (r.TryRead(out ReadOnlySpan<byte> nal, out int offset, out int sc))
        {
            if (nal.Length == 0)
            {
                continue;
            }

            int type = NalUnit.GetType(t.Nal, nal);
            if (first && type == AudType(t.Nal))
            {
                hasAud = true;
                audEnd = offset + nal.Length;
            }

            first = false;
            if (NalUnit.IsParameterSet(t.Nal, type))
            {
                hasSets = true;
                if (setsFrom < 0)
                {
                    setsFrom = offset - sc;
                }

                setsTo = offset + nal.Length;
            }
            else if (NalUnit.IsSlice(t.Nal, type))
            {
                break;
            }
        }

        if (hasSets && t.ParameterSets.Length == 0 && setsTo - setsFrom <= 1 << 16 && setsFrom >= 0)
        {
            // An Annex B track: remember the first parameter sets for keyframes that come without.
            t.ParameterSets = au[setsFrom..setsTo].ToArray();
        }

        int n = 0;
        bool needAud = _o.InsertAccessUnitDelimiters && !hasAud;
        bool needSets = _o.InsertParameterSets && !hasSets && info.IsKeyframe && t.ParameterSets.Length > 0;
        int audLength = needAud ? AudBytes(t.Nal, info.IsKeyframe) : 0;
        n = audLength;
        if (needAud)
        {
            // Delimiter first, then parameter sets, then the access unit.
            if (needSets)
            {
                EnsurePrefix(n + t.ParameterSets.Length);
                t.ParameterSets.CopyTo(_prefixBytes, n);
                n += t.ParameterSets.Length;
            }

            prefixLength = n;
            splitAt = 0;
            return au;
        }

        if (needSets)
        {
            EnsurePrefix(t.ParameterSets.Length);
            t.ParameterSets.CopyTo(_prefixBytes, 0);
            prefixLength = t.ParameterSets.Length;
            // After the delimiter that is already there, else in front.
            splitAt = hasAud ? audEnd : 0;
            return au;
        }

        splitAt = au.Length;
        return au;
    }

    private void EnsurePrefix(int needed)
    {
        if (_prefixBytes.Length < needed)
        {
            Array.Resize(ref _prefixBytes, needed + 64);
        }
    }

    private static bool StartsWithStartCode(ReadOnlySpan<byte> d) =>
        (d.Length >= 4 && d[0] == 0 && d[1] == 0 && ((d[2] == 0 && d[3] == 1) || d[2] == 1)) || (d.Length >= 3 && d[0] == 0 && d[1] == 0 && d[2] == 1);

    private static int AudType(NalCodec c) => c switch { NalCodec.H264 => 9, NalCodec.Hevc => 35, _ => 20 };

    // The access unit delimiter NAL unit with a four-byte start code. H.264 §7.3.2.4: primary_pic_type 7 (any slice type); H.265 §7.3.2.5: pic_type 2
    // (I, P or B); H.266 §7.3.2.10: aud_irap_or_gdr_flag, aud_pic_type 2.
    private int AudBytes(NalCodec c, bool key)
    {
        ReadOnlySpan<byte> bytes = c switch
        {
            NalCodec.H264 => [0, 0, 0, 1, 0x09, 0xF0],
            NalCodec.Hevc => [0, 0, 0, 1, 0x46, 0x01, 0x50],
            _ => key ? [0, 0, 0, 1, 0x00, 0xA1, 0xA8] : [0, 0, 0, 1, 0x00, 0xA1, 0x28],
        };
        EnsurePrefix(bytes.Length);
        bytes.CopyTo(_prefixBytes);
        return bytes.Length;
    }

    // ---- clock, PSI ----

    // Sets the stream clock for a packet about to be written, inserts PCR-only packets for a long gap and the PAT and PMT when due.
    private void ClockAndPsi(long dts90)
    {
        long c = Math.Max(0, dts90 - _lead90) * 300;
        if (c < _clock27)
        {
            c = _clock27;
        }

        long step = c - _clock27;
        _clock27 = c;
        if (step > 0)
        {
            _step27 = step;
        }

        // A segment (or the stream) starts with the tables, before any filler.
        if (_psiDue)
        {
            WritePsi();
            _psiDue = false;
            _lastPsi27 = Math.Max(_lastPcr27, _clock27 - step);
        }

        if (_lastPcr27 >= 0 && _pcrTrack >= 0)
        {
            long interval = _o.PcrIntervalMilliseconds * 27_000L;
            int fill = 0;
            while (c - _lastPcr27 > interval && fill++ < MaxPcrFillPackets)
            {
                _lastPcr27 += interval;
                if (_lastPcr27 - _lastPsi27 > _o.PsiIntervalMilliseconds * 27_000L)
                {
                    WritePsi();
                    _lastPsi27 = _lastPcr27;
                }

                WritePcrOnly(_tracks[_pcrTrack], _lastPcr27);
            }
        }

        // Repeat the tables early enough that the next packet, one step later, does not leave them older than the interval.
        if (c - _lastPsi27 + _step27 > _o.PsiIntervalMilliseconds * 27_000L)
        {
            WritePsi();
            _lastPsi27 = c;
        }
    }

    private void WritePsi()
    {
        _pat ??= BuildPat();
        _pmt ??= BuildPmt();
        WritePayload(0, ref _patCc, _pat, section: true, -1, false, _clock27);
        WritePayload(_o.PmtPid, ref _pmtCc, _pmt, section: true, -1, false, _clock27);
    }

    // ISO/IEC 13818-1 §2.4.4.3 program_association_section; the first byte is the pointer_field.
    private byte[] BuildPat()
    {
        byte[] s = new byte[1 + 8 + 4 + 4];
        int b = 1;
        s[b++] = 0x00; // table_id
        s[b++] = 0xB0; // section_syntax_indicator, '0', reserved
        s[b++] = (byte)(5 + 4 + 4); // section_length: 5 header bytes, one program, CRC
        s[b++] = (byte)(_o.TransportStreamId >> 8);
        s[b++] = (byte)_o.TransportStreamId;
        s[b++] = 0xC1; // reserved, version 0, current_next_indicator 1
        s[b++] = 0; // section_number
        s[b++] = 0; // last_section_number
        s[b++] = (byte)(_o.ProgramNumber >> 8);
        s[b++] = (byte)_o.ProgramNumber;
        s[b++] = (byte)(0xE0 | (_o.PmtPid >> 8));
        s[b++] = (byte)_o.PmtPid;
        Crc(s, b);
        return s;
    }

    // §2.4.4.8 TS_program_map_section.
    private byte[] BuildPmt()
    {
        int es = 0;
        foreach (TsTrack t in _tracks)
        {
            es += 5 + t.Descriptors.Length;
        }

        int sectionLength = 9 + es + 4;
        byte[] s = new byte[1 + 3 + sectionLength];
        int b = 1;
        s[b++] = 0x02;
        s[b++] = (byte)(0xB0 | (sectionLength >> 8));
        s[b++] = (byte)sectionLength;
        s[b++] = (byte)(_o.ProgramNumber >> 8);
        s[b++] = (byte)_o.ProgramNumber;
        s[b++] = 0xC1;
        s[b++] = 0;
        s[b++] = 0;
        int pcrPid = _pcrTrack >= 0 ? _tracks[_pcrTrack].Pid : 0x1FFF;
        s[b++] = (byte)(0xE0 | (pcrPid >> 8));
        s[b++] = (byte)pcrPid;
        s[b++] = 0xF0; // reserved, program_info_length = 0
        s[b++] = 0;
        foreach (TsTrack t in _tracks)
        {
            s[b++] = (byte)t.StreamType;
            s[b++] = (byte)(0xE0 | (t.Pid >> 8));
            s[b++] = (byte)t.Pid;
            s[b++] = (byte)(0xF0 | (t.Descriptors.Length >> 8));
            s[b++] = (byte)t.Descriptors.Length;
            t.Descriptors.CopyTo(s, b);
            b += t.Descriptors.Length;
        }

        Crc(s, b);
        return s;
    }

    // Appends the CRC-32 of the section that starts after the pointer_field byte (Annex A).
    private static void Crc(byte[] s, int end)
    {
        uint crc = Mpeg2Crc.Compute(s.AsSpan(1, end - 1));
        s[end] = (byte)(crc >> 24);
        s[end + 1] = (byte)(crc >> 16);
        s[end + 2] = (byte)(crc >> 8);
        s[end + 3] = (byte)crc;
    }

    // ---- transport packets ----

    // Writes data as the payload of transport packets of one PID. The first packet starts the unit (payload_unit_start_indicator). A PCR and
    // the random access flag go in the adaptation field of the first packet; a short last packet is padded with adaptation field stuffing,
    // except for sections, which are padded with 0xFF in the payload.
    private void WritePayload(int pid, ref byte cc, ReadOnlySpan<byte> data, bool section, long pcr27, bool randomAccess, long ats)
    {
        int pos = 0;
        bool first = true;
        // A section starts with its pointer_field (PSI built above already has it); SCTE-35 sections get theirs here.
        bool needPointer = section && pid != 0 && pid != _o.PmtPid;
        int pointerPending = needPointer ? 1 : 0;
        int total = data.Length + pointerPending;
        int done = 0;
        while (done < total)
        {
            Span<byte> p = NextPacket(ats);
            int afLen = first && (pcr27 >= 0 || randomAccess) ? 2 + (pcr27 >= 0 ? 6 : 0) : 0;
            int cap = 184 - afLen;
            int n = Math.Min(cap, total - done);
            int pad = cap - n;
            int totalAf = afLen;
            int payloadPad = 0;
            if (pad > 0)
            {
                if (section)
                {
                    payloadPad = pad;
                }
                else
                {
                    totalAf += pad;
                }
            }

            p[0] = 0x47;
            p[1] = (byte)((first ? 0x40 : 0) | (pid >> 8));
            p[2] = (byte)pid;
            p[3] = (byte)((totalAf > 0 ? 0x30 : 0x10) | (cc & 15));
            cc = (byte)((cc + 1) & 15);
            int w = 4;
            if (totalAf > 0)
            {
                p[w++] = (byte)(totalAf - 1);
                if (totalAf > 1)
                {
                    p[w++] = (byte)((first && randomAccess ? 0x40 : 0) | (first && pcr27 >= 0 ? 0x10 : 0));
                    if (first && pcr27 >= 0)
                    {
                        WritePcr(p.Slice(w, 6), pcr27);
                        w += 6;
                    }
                }

                p.Slice(w, 4 + totalAf - w).Fill(0xFF);
                w = 4 + totalAf;
            }

            int take = n;
            if (pointerPending > 0 && first)
            {
                p[w++] = 0; // pointer_field
                take--;
                done++;
                pointerPending = 0;
            }

            data.Slice(pos, take).CopyTo(p[w..]);
            pos += take;
            done += take;
            w += take;
            if (payloadPad > 0)
            {
                p[w..188].Fill(0xFF);
            }

            first = false;
        }
    }

    // A packet that carries only a PCR (§2.4.3.4: adaptation_field_control '10'; the continuity counter repeats the previous value).
    private void WritePcrOnly(TsTrack t, long pcr27)
    {
        Span<byte> p = NextPacket(_clock27);
        p[0] = 0x47;
        p[1] = (byte)(t.Pid >> 8);
        p[2] = (byte)t.Pid;
        p[3] = (byte)(0x20 | ((t.Cc - 1) & 15));
        p[4] = 183;
        p[5] = 0x10;
        WritePcr(p.Slice(6, 6), pcr27);
        p[12..188].Fill(0xFF);
    }

    // §2.4.3.5: program_clock_reference_base (33 bits, 90 kHz), 6 reserved bits, extension (9 bits, 27 MHz remainder).
    private static void WritePcr(Span<byte> d, long pcr27)
    {
        long baseValue = (pcr27 / 300) & Mask33;
        int ext = (int)(pcr27 % 300);
        d[0] = (byte)(baseValue >> 25);
        d[1] = (byte)(baseValue >> 17);
        d[2] = (byte)(baseValue >> 9);
        d[3] = (byte)(baseValue >> 1);
        d[4] = (byte)((int)((baseValue & 1) << 7) | 0x7E | (ext >> 8));
        d[5] = (byte)ext;
    }

    // §2.4.3.7: '0010' or '0011' or '0001' prefix, then the 33-bit value in three parts separated by marker bits.
    private static void WriteTimestamp(Span<byte> d, int prefix, long value)
    {
        value &= Mask33;
        d[0] = (byte)((prefix << 4) | (int)(((value >> 30) & 7) << 1) | 1);
        d[1] = (byte)(value >> 22);
        d[2] = (byte)((int)(((value >> 15) & 0x7F) << 1) | 1);
        d[3] = (byte)(value >> 7);
        d[4] = (byte)((int)((value & 0x7F) << 1) | 1);
    }

    // Returns the 188 bytes of the next packet in the output buffer (after the M2TS extra header, which is filled in).
    private Span<byte> NextPacket(long ats27)
    {
        if (_buffered + _packetSize > _buffer.Length)
        {
            FlushBuffer();
        }

        Span<byte> p = _buffer.AsSpan(_buffered, _packetSize);
        if (_prefix > 0)
        {
            // TP_extra_header: copy_permission_indicator 0 and a 30-bit arrival_time_stamp on the 27 MHz clock.
            uint ats = (uint)(ats27 & 0x3FFFFFFF);
            p[0] = (byte)(ats >> 24);
            p[1] = (byte)(ats >> 16);
            p[2] = (byte)(ats >> 8);
            p[3] = (byte)ats;
        }

        _buffered += _packetSize;
        _bytesWritten += _packetSize;
        _segmentBytes += _packetSize;
        return p[_prefix..];
    }

    private void FlushBuffer()
    {
        if (_buffered > 0)
        {
            _out.Write(_buffer, 0, _buffered);
            _buffered = 0;
        }
    }

    /// <inheritdoc/>
    public void Flush(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_finished)
        {
            return;
        }

        FlushAllPending();
        FlushBuffer();
        _out.Flush();
    }

    /// <inheritdoc/>
    public MuxReport Finish(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_finished)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "The muxer is already finished.");
        }

        if (_tracks.Count == 0)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, "There are no tracks to write.");
        }

        if (!_started)
        {
            // A stream without packets still announces its program.
            _started = true;
            Segment0();
            WritePsi();
        }

        FlushAllPending();
        FlushBuffer();
        _out.Flush();
        _finished = true;
        List<TrackMuxReport> tracks = new(_tracks.Count);
        for (int i = 0; i < _tracks.Count; i++)
        {
            TsTrack t = _tracks[i];
            tracks.Add(new TrackMuxReport(i, t.Packets, t.PayloadBytes, t.Duration));
        }

        List<string> notes = [.. _notes];
        notes.Add($"Times are 90 kHz: the first decode time was moved to {_initialDelay90} ticks, and the program clock runs {_lead90} ticks ahead of decode times.");
        if (_prefix > 0)
        {
            notes.Add("M2TS packets carry arrival time stamps derived from the program clock, not measured arrival times.");
        }

        return new MuxReport(_bytesWritten, tracks, notes);
    }

    /// <summary>Abandons the output. Bytes not yet handed to the stream are dropped; the stream is not closed.</summary>
    public void Dispose()
    {
        _finished = true;
    }
}
