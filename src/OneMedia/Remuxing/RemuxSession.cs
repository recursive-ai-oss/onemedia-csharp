using System.Buffers;
using System.Linq;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Codecs.H264;
using RecursiveAI.OneMedia.Codecs.Hevc;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Remuxing;

/// <summary>
/// Writes the packets of an already-open container into a muxer: picks tracks, maps each codec configuration to the form the target stores,
/// repackages payloads where the framing differs (Annex B to length-prefixed, ADTS to raw), carries priming and edit information, and keeps
/// the list of everything that did not carry over. <see cref="Remuxer"/> drives one from a <see cref="MediaFile"/>; callers that filter or
/// retime packets (trim, split, concatenation) drive one themselves.
/// </summary>
/// <remarks>
/// <para>Give every packet of every wanted track to <see cref="WritePacket"/> in the order the output should store them, then call
/// <see cref="Finish"/>. Packets are copied before the call returns. Memory does not depend on the length of the input: the only packets held
/// back are those that arrive before every track's configuration is known, up to <see cref="RemuxOptions.ConfigurationProbeBytes"/>.</para>
/// <para>A session is not thread-safe. Disposing it before <see cref="Finish"/> abandons the output.</para>
/// </remarks>
public sealed class RemuxSession : IDisposable
{
    private enum Repack
    {
        None,
        AnnexB,
        Adts,
    }

    private sealed class TrackState(MediaTrack input)
    {
        public MediaTrack Input { get; } = input;

        public bool Wanted { get; set; }

        public bool Dropped { get; set; }

        public int Out { get; set; } = -1;

        public Repack Mode { get; set; }

        /// <summary>False while the configuration still has to be derived from the stream (or the first packet decides the framing).</summary>
        public bool Resolved { get; set; }

        public bool FramingDecided { get; set; }

        public CodecConfiguration Derived { get; set; }

        public NalCodec Nal { get; set; }

        public ArrayBufferWriter<byte>? ParameterSetBytes { get; set; }

        public List<byte[]> KnownSets { get; } = [];

        public MediaTime Shift { get; set; }

        public long Packets { get; set; }

        public bool ReportedAnnexB { get; set; }

        public MediaTime NextPts { get; set; } = MediaTime.Unknown;

        public List<RemuxSession.Pending> Queue { get; } = [];

        public List<long> Sorted { get; } = [];

        public long QueueBytes { get; set; }

        public long TimeScale { get; set; }

        public long Lag { get; set; }

        public bool LagKnown { get; set; }

        public bool ReportedDerivedDts { get; set; }

        public bool ReportedRetime { get; set; }
    }

    private readonly struct Held(PacketInfo info, byte[] data)
    {
        public PacketInfo Info { get; } = info;

        public byte[] Data { get; } = data;
    }

    private const int ParameterSetCap = 1 << 16;

    private readonly IReadOnlyList<MediaTrack> _inputTracks;
    private readonly Stream _output;
    private readonly RemuxFormat _format;
    private readonly RemuxOptions _options;
    private readonly List<TrackState> _tracks = [];
    private readonly List<RemuxLoss> _losses = [];
    private readonly List<string> _changes = [];
    private readonly HashSet<int> _unlisted = [];
    private readonly List<Held> _held = [];
    private long _heldBytes;
    private IMuxer? _muxer;
    private bool _started;
    private bool _finished;
    private byte[] _scratch = new byte[1 << 16];

    /// <summary>
    /// Creates a session. Nothing is written until the first packet or <see cref="Finish"/>.
    /// </summary>
    /// <param name="inputTracks">The tracks of the input, with their own <see cref="MediaTrack.Index"/> values; packets refer to these.</param>
    /// <param name="output">The destination. Seekable for the progressive MP4 layouts without a spool; otherwise any writable stream. Not closed.</param>
    /// <param name="format">The container to write.</param>
    /// <param name="options">Settings; defaults when null.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="UnsupportedMediaException"><paramref name="format"/> has no muxer yet.</exception>
    public RemuxSession(IReadOnlyList<MediaTrack> inputTracks, Stream output, RemuxFormat format, RemuxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputTracks);
        ArgumentNullException.ThrowIfNull(output);
        _inputTracks = inputTracks;
        _output = output;
        _format = format;
        _options = options ?? new RemuxOptions();
        if (_options.Edits is not null)
        {
            foreach (KeyValuePair<int, TrackEdit> e in _options.Edits)
            {
                if (e.Value.RotationDegrees is { } r and not (0 or 90 or 180 or 270))
                {
                    throw new ArgumentOutOfRangeException(nameof(options), $"Rotation must be 0, 90, 180 or 270, not {r}.");
                }
            }
        }

        _muxer = RemuxMuxers.Create(format, output, _options);
        foreach (MediaTrack t in inputTracks)
        {
            TrackState s = new(t);
            _tracks.Add(s);
            Select(s);
        }
    }

    /// <summary>
    /// Per-track ISO settings for the ISO targets: extra sample entry boxes, PCM layout, track id. Asked once per written input track when the output
    /// tracks are declared. <see cref="Remuxer"/> sets it from the input (see <see cref="Remuxer.IsoTrackOptionsFor"/>); callers that drive a session
    /// themselves may set it before the first packet. Ignored by the Matroska targets, which report the entry boxes they cannot write.
    /// </summary>
    public Func<MediaTrack, IsoTrackOptions?>? IsoOptions { get; set; }

    /// <summary>Whether the packets of each input track are wanted, by input index. Turn the others off in the demuxer to skip reading them.</summary>
    public IReadOnlyList<bool> Wanted
    {
        get
        {
            bool[] w = new bool[_tracks.Count];
            for (int i = 0; i < w.Length; i++)
            {
                w[i] = _tracks[i].Wanted && !_tracks[i].Dropped;
            }

            return w;
        }
    }

    /// <summary>Adds an entry to the loss list (used by callers that know about things the session cannot see, such as tags).</summary>
    /// <param name="loss">The entry.</param>
    public void AddLoss(RemuxLoss loss)
    {
        ArgumentNullException.ThrowIfNull(loss);
        _losses.Add(loss);
    }

    /// <summary>Adds a plain-words entry to the list of changes.</summary>
    /// <param name="change">The sentence.</param>
    public void AddChange(string change)
    {
        ArgumentNullException.ThrowIfNull(change);
        _changes.Add(change);
    }

    private void Select(TrackState s)
    {
        MediaTrack t = s.Input;
        TrackEdit? edit = Edit(t.Index);
        bool wanted = edit is not { Drop: true } && (_options.Select?.Invoke(t) ?? true);
        s.Wanted = wanted;
        if (!wanted)
        {
            _losses.Add(new RemuxLoss(LossKind.Track, LossReason.Deselected, t.Index, Describe(t), "The track was left out on request."));
            return;
        }

        if (t.Kind is TrackKind.Video && t.Codec.Name is "h264" or "hevc")
        {
            bool stored = t.Configuration.Format is "avcC" or "hvcC" && !t.Configuration.IsEmpty;
            s.Nal = t.Codec == CodecId.H264 ? NalCodec.H264 : NalCodec.Hevc;
            if (stored)
            {
                s.Resolved = true;
                s.FramingDecided = true;
            }
            else
            {
                s.Mode = Repack.AnnexB;
                s.ParameterSetBytes = new ArrayBufferWriter<byte>();
            }

            return;
        }

        if (t.Codec == CodecId.Aac && t.Kind == TrackKind.Audio)
        {
            // Whether the packets carry ADTS headers is known only from the first packet.
            return;
        }

        s.Resolved = true;
        s.FramingDecided = true;
    }

    // The ISO 'alac' box holds a version/flags word and the 24-byte ALACSpecificConfig. CAF and QuickTime movies store the cookie as atoms
    // (frma 'alac', then an 'alac' atom of that same content, then a terminator), and bare 24-byte configs also occur.
    private static byte[]? AlacBoxPayload(ReadOnlySpan<byte> cookie)
    {
        if (cookie.Length == 24)
        {
            byte[] r = new byte[28];
            cookie.CopyTo(r.AsSpan(4));
            return r;
        }

        int at = 0;
        while (at + 8 <= cookie.Length)
        {
            long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(cookie[at..]);
            if (size < 8 || at + size > cookie.Length)
            {
                break;
            }

            if (cookie.Slice(at + 4, 4).SequenceEqual("alac"u8) && size == 36)
            {
                return cookie.Slice(at + 8, 28).ToArray();
            }

            at += (int)size;
        }

        return null;
    }

    private TrackEdit? Edit(int index) => _options.Edits is { } e && e.TryGetValue(index, out TrackEdit? edit) ? edit : null;

    private static string Describe(MediaTrack t) => t.CodecTag is { Length: > 0 } tag ? $"{t.Kind} {t.Codec.Name} [{tag}]" : $"{t.Kind} {t.Codec.Name}";

    /// <summary>Writes one packet.</summary>
    /// <param name="info">The packet; <see cref="PacketInfo.TrackIndex"/> is the <b>input</b> track index.</param>
    /// <param name="data">The payload as the reader delivered it. Copied before the call returns.</param>
    /// <param name="cancellationToken">Checked before the packet is accepted.</param>
    /// <exception cref="InvalidOperationException">After <see cref="Finish"/>, or a negative track index. Packets of a track number past the list are skipped and reported once.</exception>
    /// <exception cref="MuxRefusedException">The muxer refused the packet (<see cref="MuxRefusalReason.NonMonotonicDts"/>, a missing time).</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public void WritePacket(in PacketInfo info, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_finished)
        {
            throw new InvalidOperationException("The session is finished.");
        }

        if (info.TrackIndex < 0)
        {
            throw new InvalidOperationException($"Packet for track {info.TrackIndex}, which is not an input track.");
        }

        if (info.TrackIndex >= _tracks.Count)
        {
            // A reader met a stream after it had listed its tracks (a logical stream that begins late in an Ogg file, say): nothing declares it,
            // so its packets cannot be written. Say so once.
            if (_unlisted.Add(info.TrackIndex))
            {
                _losses.Add(new RemuxLoss(LossKind.Track, LossReason.NotInspected, info.TrackIndex, $"stream {info.TrackIndex}", "The input has packets of a stream that its track list does not declare; they were skipped."));
            }

            return;
        }

        TrackState s = _tracks[info.TrackIndex];
        if (!s.Wanted || s.Dropped)
        {
            return;
        }

        if (!_started)
        {
            Hold(s, info, data);
            if (AllResolved() || _heldBytes > _options.ConfigurationProbeBytes)
            {
                Start(cancellationToken);
            }

            return;
        }

        Emit(s, info, data, cancellationToken);
    }

    private void Hold(TrackState s, in PacketInfo info, ReadOnlySpan<byte> data)
    {
        _held.Add(new Held(info, data.ToArray()));
        _heldBytes += data.Length;
        if (!s.Resolved)
        {
            Probe(s, data);
        }
    }

    private bool _wantedChanged;

    /// <summary>True once after the set of wanted tracks changed because a track could not be written; the caller can then stop reading it.</summary>
    internal bool TakeWantedChange()
    {
        bool changed = _wantedChanged;
        _wantedChanged = false;
        return changed;
    }

    private bool AllResolved()
    {
        foreach (TrackState s in _tracks)
        {
            if (s.Wanted && !s.Dropped && !s.Resolved)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Looks at a packet of a track whose configuration or framing is not yet known.</summary>
    private void Probe(TrackState s, ReadOnlySpan<byte> data)
    {
        if (s.Mode == Repack.AnnexB)
        {
            ProbeAnnexB(s, data);
            return;
        }

        // AAC: the first packet decides the framing.
        if (!s.FramingDecided)
        {
            s.FramingDecided = true;
            bool adts = AdtsHeader.TryParse(data, out AdtsHeader h) && h.FrameLength <= data.Length;
            s.Mode = adts ? Repack.Adts : Repack.None;
            if (adts && s.Input.Configuration.IsEmpty)
            {
                ArrayBufferWriter<byte> w = new();
                if (AdtsRepackager.TryCreateConfig(data, w))
                {
                    s.Derived = new CodecConfiguration(w.WrittenMemory.ToArray(), "asc");
                    _changes.Add($"Track {s.Input.Index}: the AudioSpecificConfig was derived from the first ADTS header.");
                }
            }

            if (adts)
            {
                _changes.Add($"Track {s.Input.Index}: ADTS headers were removed from the AAC packets; the configuration is stored once.");
            }

            s.Resolved = true;
        }
    }

    private void ProbeAnnexB(TrackState s, ReadOnlySpan<byte> data)
    {
        ArrayBufferWriter<byte> ps = s.ParameterSetBytes!;
        bool added = false;
        AnnexBReader r = new(data);
        while (r.TryRead(out ReadOnlySpan<byte> nal, out _, out _))
        {
            if (nal.Length == 0)
            {
                continue;
            }

            int type = NalUnit.GetType(s.Nal, nal);
            if (!NalUnit.IsParameterSet(s.Nal, type) || ps.WrittenCount + 4 + nal.Length > ParameterSetCap)
            {
                continue;
            }

            ReadOnlySpan<byte> start = [0, 0, 0, 1];
            start.CopyTo(ps.GetSpan(4));
            ps.Advance(4);
            nal.CopyTo(ps.GetSpan(nal.Length));
            ps.Advance(nal.Length);
            added = true;
        }

        if (!added)
        {
            return;
        }

        ArrayBufferWriter<byte> record = new();
        if (s.Nal == NalCodec.H264)
        {
            if (AvcDecoderConfigurationRecord.TryCreateFromAnnexB(ps.WrittenMemory, out AvcDecoderConfigurationRecord? avc) && avc is { PictureParameterSets.Count: > 0 })
            {
                avc.Write(record);
                foreach (ReadOnlyMemory<byte> u in avc.SequenceParameterSets.Concat(avc.PictureParameterSets).Concat(avc.SequenceParameterSetExtensions))
                {
                    s.KnownSets.Add(u.ToArray());
                }

                s.Derived = new CodecConfiguration(record.WrittenMemory.ToArray(), "avcC");
            }
        }
        else if (HevcDecoderConfigurationRecord.TryCreateFromAnnexB(ps.WrittenMemory, out HevcDecoderConfigurationRecord? hevc) && hevc is not null
            && hevc.Arrays.Any(a => a.NalUnitType == HevcNalType.Pps))
        {
            hevc.Write(record);
            foreach (HevcNalArray a in hevc.Arrays)
            {
                if (a.NalUnitType is HevcNalType.Vps or HevcNalType.Sps or HevcNalType.Pps)
                {
                    foreach (ReadOnlyMemory<byte> u in a.Units)
                    {
                        s.KnownSets.Add(u.ToArray());
                    }
                }
            }

            s.Derived = new CodecConfiguration(record.WrittenMemory.ToArray(), "hvcC");
        }

        if (!s.Derived.IsEmpty)
        {
            s.Resolved = true;
            _changes.Add($"Track {s.Input.Index}: the {(s.Nal == NalCodec.H264 ? "avcC" : "hvcC")} record was built from the parameter sets in the stream, and NAL units were re-framed from Annex B to 4-byte lengths; parameter sets equal to the record's were moved out of the samples.");
        }
    }

    private void Start(CancellationToken cancellationToken)
    {
        _started = true;
        _wantedChanged = true;
        IMuxer muxer = _muxer!;
        foreach (TrackState s in _tracks)
        {
            if (!s.Wanted)
            {
                continue;
            }

            if (!s.Resolved)
            {
                s.Dropped = true;
                _losses.Add(new RemuxLoss(LossKind.Track, LossReason.MissingConfiguration, s.Input.Index, Describe(s.Input),
                    s.Mode == Repack.AnnexB
                        ? "The stream has no parameter sets within the first packets, so the sample entry cannot be built."
                        : "No packet arrived to show how the track is framed."));
                if (!_options.AllowDrop)
                {
                    throw new MuxRefusedException(MuxRefusalReason.MissingConfiguration, $"Track {s.Input.Index}: no codec configuration could be derived.", s.Input.Index, s.Input.Codec);
                }

                continue;
            }

            MediaTrack declared = Declare(s);
            try
            {
                if (muxer is IsoMuxer iso)
                {
                    s.Out = iso.AddTrack(declared, IsoOptions?.Invoke(s.Input));
                }
                else
                {
                    s.Out = muxer.AddTrack(declared);
                    ReportUnwrittenEntryBoxes(s);
                }
            }
            catch (MuxRefusedException e) when (_options.AllowDrop)
            {
                s.Dropped = true;
                bool cmafFull = _format == RemuxFormat.Cmaf && _tracks.Exists(x => x.Out >= 0);
                _losses.Add(new RemuxLoss(LossKind.Track, cmafFull ? LossReason.TargetLimit : ReasonOf(e.Reason, s.Input), s.Input.Index, Describe(s.Input), e.Message));
            }
        }

        if (!_tracks.Exists(t => t.Out >= 0))
        {
            return;
        }

        foreach (Held h in _held)
        {
            TrackState s = _tracks[h.Info.TrackIndex];
            if (!s.Dropped)
            {
                Emit(s, h.Info, h.Data, cancellationToken);
            }
        }

        _held.Clear();
        _heldBytes = 0;
    }

    // Matroska has no place for the colour, field-order, clean-aperture or HDR boxes of an ISO sample entry; the ISO path carries them.
    private void ReportUnwrittenEntryBoxes(TrackState s)
    {
        if (IsoOptions?.Invoke(s.Input)?.SampleEntryBoxes is { Count: > 0 } boxes)
        {
            string types = string.Join(' ', boxes.Select(b => System.Text.Encoding.ASCII.GetString(b.Span.Slice(4, 4))));
            _losses.Add(new RemuxLoss(LossKind.CodecField, LossReason.NotRepresentable, s.Input.Index, types, "The muxer does not write these sample entry boxes (colour, field order, clean aperture, HDR metadata)."));
        }
    }

    private static LossReason ReasonOf(MuxRefusalReason reason, MediaTrack t) => reason switch
    {
        MuxRefusalReason.UnsupportedCodec => t.IsProtected ? LossReason.Protected : LossReason.UnsupportedCodec,
        MuxRefusalReason.MissingConfiguration => LossReason.MissingConfiguration,
        MuxRefusalReason.InvalidConfiguration => LossReason.InvalidConfiguration,
        MuxRefusalReason.LimitExceeded => LossReason.TargetLimit,
        _ => LossReason.NotRepresentable,
    };

    private MediaTrack Declare(TrackState s)
    {
        MediaTrack t = s.Input;
        TrackEdit? edit = Edit(t.Index);
        CodecConfiguration config = s.Derived.IsEmpty ? t.Configuration : s.Derived;
        if (t.Codec == CodecId.Alac && s.Derived.IsEmpty && AlacBoxPayload(config.Data.Span) is { } alac && !alac.AsSpan().SequenceEqual(config.Data.Span))
        {
            config = new CodecConfiguration(alac, "alac");
            _changes.Add($"Track {t.Index}: the ALAC magic cookie was unwrapped to the configuration the ISO 'alac' box holds.");
        }

        MediaTrack declared = t with { Configuration = config };
        if (edit?.Language is { } lang)
        {
            declared = declared with { Language = lang };
        }

        if (edit?.Name is { } name)
        {
            declared = declared with { Name = name };
        }

        if (edit?.Disposition is { } disposition)
        {
            declared = declared with { Disposition = disposition };
        }

        if (edit?.RotationDegrees is { } rotation && declared.Video is { } v)
        {
            declared = declared with { Video = v with { RotationDegrees = rotation } };
        }

        // Packet times that already have the priming taken off start at minus the priming (Ogg Opus). Muxers expect raw media times, with
        // the priming in the edit information, so every time moves forward by it.
        if (t.Edit.PacketTimesIncludePriming && t.Edit.Priming.IsKnown && !t.Edit.Priming.IsZero)
        {
            s.Shift = t.Edit.Priming;
            declared = declared with { Edit = t.Edit with { PacketTimesIncludePriming = false } };
            _changes.Add($"Track {t.Index}: packet times were moved forward by the priming ({t.Edit.Priming.Ticks}/{t.Edit.Priming.Timescale} s) so the first packet starts at zero; the priming is kept in the edit information.");
        }

        if (_format is RemuxFormat.Mp4 or RemuxFormat.M4a or RemuxFormat.M4v or RemuxFormat.Mov or RemuxFormat.ThreeGp or RemuxFormat.ThreeG2 or RemuxFormat.FragmentedMp4 or RemuxFormat.Cmaf)
        {
            TrackDisposition kept = declared.Disposition & (TrackDisposition.Disabled | TrackDisposition.Default);
            TrackDisposition lost = declared.Disposition & ~kept;
            if (lost != TrackDisposition.None)
            {
                _losses.Add(new RemuxLoss(LossKind.Disposition, LossReason.NotRepresentable, t.Index, lost.ToString(), "MP4 and QuickTime have no field for these disposition flags."));
            }
        }

        if (_format is RemuxFormat.Matroska or RemuxFormat.WebM)
        {
            if (declared.Video is { RotationDegrees: not 0 } rot)
            {
                _losses.Add(new RemuxLoss(LossKind.CodecField, LossReason.NotWritten, t.Index, "rotation", $"The Matroska muxer does not write the display rotation of {rot.RotationDegrees} degrees."));
            }

            if (t.Codec == CodecId.Aac && ((declared.Edit.Priming.IsKnown && !declared.Edit.Priming.IsZero) || declared.Edit.Edits is { Count: > 0 }))
            {
                _losses.Add(new RemuxLoss(LossKind.Timing, LossReason.NotWritten, t.Index, "aac-priming", "AAC priming and edit lists are not written to Matroska; players that do not drop the encoder delay start the track late."));
            }

            if (_format == RemuxFormat.WebM && t.Kind == TrackKind.Video)
            {
                _losses.Add(new RemuxLoss(LossKind.CodecField, LossReason.NotWritten, t.Index, "colour", "WebM output omits the Colour element and the SegmentUID (the validator used for the writer rejects them there)."));
            }

            _changes.Add($"Track {t.Index}: BlockAdditions, interlacing and stereo mode are not written to Matroska.");
        }

        return declared;
    }

    // Matroska stores presentation times only, so its video packets arrive without decode times. The muxers need them, and with reordered
    // frames they are not the presentation times. A decode time is taken from the packets' presentation order: the k-th packet in decode
    // order gets the k-th smallest presentation time, moved back by one constant lag so that no packet decodes after it is due. Both are
    // judged over a short window of packets, which is held back; the lag is fixed from the first window.
    private const int ReorderWindow = 24;
    private const long ReorderByteCap = 32L << 20;

    internal sealed class Pending(PacketInfo info, byte[] data, int length)
    {
        public PacketInfo Info { get; } = info;

        public byte[] Data { get; } = data;

        public int Length { get; } = length;
    }

    private void Emit(TrackState s, in PacketInfo info, ReadOnlySpan<byte> data, CancellationToken cancellationToken)
    {
        if (s.Input.Kind != TrackKind.Video || info.Dts.IsKnown || !info.Pts.IsKnown)
        {
            EmitNow(s, info, data, cancellationToken);
            return;
        }

        byte[] copy = ArrayPool<byte>.Shared.Rent(Math.Max(data.Length, 1));
        data.CopyTo(copy);
        s.Queue.Add(new Pending(info, copy, data.Length));
        s.QueueBytes += data.Length;
        long pts = info.Pts.Ticks;
        if (s.TimeScale == 0)
        {
            s.TimeScale = info.Pts.Timescale;
        }

        if (info.Pts.Timescale != s.TimeScale)
        {
            pts = info.Pts.Rescale(s.TimeScale, MediaRounding.NearestAwayFromZero).Ticks;
        }

        int at = s.Sorted.BinarySearch(pts);
        s.Sorted.Insert(at < 0 ? ~at : at, pts);
        if (!s.LagKnown && (s.Queue.Count >= ReorderWindow || s.QueueBytes > ReorderByteCap))
        {
            FixLag(s);
        }

        while (s.LagKnown && (s.Queue.Count >= ReorderWindow || s.QueueBytes > ReorderByteCap) && s.Queue.Count > 0)
        {
            PopReordered(s, cancellationToken);
        }
    }

    private static void FixLag(TrackState s)
    {
        long lag = 0;
        for (int k = 0; k < s.Queue.Count; k++)
        {
            long pts = s.Queue[k].Info.Pts.Timescale == s.TimeScale ? s.Queue[k].Info.Pts.Ticks : s.Queue[k].Info.Pts.Rescale(s.TimeScale, MediaRounding.NearestAwayFromZero).Ticks;
            lag = Math.Max(lag, s.Sorted[k] - pts);
        }

        s.Lag = lag;
        s.LagKnown = true;
    }

    private void PopReordered(TrackState s, CancellationToken cancellationToken)
    {
        Pending head = s.Queue[0];
        s.Queue.RemoveAt(0);
        long dts = s.Sorted[0] - s.Lag;
        s.Sorted.RemoveAt(0);
        s.QueueBytes -= head.Length;
        if (!s.ReportedDerivedDts)
        {
            s.ReportedDerivedDts = true;
            _changes.Add($"Track {s.Input.Index}: decode times were derived from the order of the presentation times, because the input stores none; presentation lags decoding by {s.Lag} of {s.TimeScale} s at most.");
        }

        try
        {
            EmitNow(s, head.Info with { Dts = new MediaTime(dts, s.TimeScale) }, head.Data.AsSpan(0, head.Length), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(head.Data);
        }
    }

    private void DrainReordered(CancellationToken cancellationToken)
    {
        foreach (TrackState s in _tracks)
        {
            if (s.Queue.Count > 0 && !s.LagKnown)
            {
                FixLag(s);
            }

            while (s.Queue.Count > 0)
            {
                PopReordered(s, cancellationToken);
            }
        }
    }

    private void EmitNow(TrackState s, in PacketInfo info, ReadOnlySpan<byte> data, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> payload = data;
        switch (s.Mode)
        {
            case Repack.AnnexB:
                payload = ConvertAnnexB(s, data);
                break;
            case Repack.Adts:
                if (AdtsRepackager.TryGetRawData(data, out ReadOnlySpan<byte> raw))
                {
                    payload = raw;
                }
                else
                {
                    throw new MediaFormatException($"Track {s.Input.Index}: a packet does not start with a complete ADTS frame.") { Offset = info.SourceRange.IsNone ? -1 : info.SourceRange.Offset };
                }

                break;
            default:
                break;
        }

        PacketInfo p = info with { TrackIndex = s.Out, Size = payload.Length };
        if (s.Shift.IsKnown && !s.Shift.IsZero)
        {
            if (p.Pts.IsKnown)
            {
                p = p with { Pts = p.Pts.Add(s.Shift, MediaRounding.NearestAwayFromZero) };
            }

            if (p.Dts.IsKnown)
            {
                p = p with { Dts = p.Dts.Add(s.Shift, MediaRounding.NearestAwayFromZero) };
            }
        }

        if (p.Pts.IsKnown && s.Input.Kind == TrackKind.Audio && s.Input.Codec.Name is "opus" or "ac3" or "aac" or "mp3")
        {
            p = RetimeLaced(s, p, data);
        }

        _options.Observer?.Invoke(p, payload);
        _muxer!.WritePacket(p, payload, cancellationToken);
        s.Packets++;
    }

    // Matroska stores one timestamp per block, and a laced block of audio frames without a DefaultDuration leaves its frames with the
    // block's time and no duration. The length of each frame is fixed by the codec (the Opus TOC byte, RFC 6716 §3.1; 1536 samples for
    // AC-3; 1024 for AAC; 1152 or 576 for MPEG audio), so a frame that would start before the previous one ended is placed where it ended.
    private PacketInfo RetimeLaced(TrackState s, PacketInfo p, ReadOnlySpan<byte> data)
    {
        if (!p.Duration.IsKnown && FrameLength(s.Input, data) is { } length)
        {
            p = p with { Duration = length };
            if (s.NextPts.IsKnown && p.Pts < s.NextPts)
            {
                MediaTime at = s.NextPts;
                p = p with { Pts = at, Dts = p.Dts.IsKnown ? at : p.Dts };
                if (!s.ReportedRetime)
                {
                    s.ReportedRetime = true;
                    _changes.Add($"Track {s.Input.Index}: packets of laced blocks without a duration were spaced by their frame lengths.");
                }
            }
        }

        s.NextPts = p.Duration.IsKnown ? p.Pts.Add(p.Duration, MediaRounding.NearestAwayFromZero) : MediaTime.Unknown;
        return p;
    }

    private static MediaTime? FrameLength(MediaTrack t, ReadOnlySpan<byte> data)
    {
        int rate = t.Audio?.SampleRate ?? 0;
        switch (t.Codec.Name)
        {
            case "opus":
                return Codecs.Opus.OpusToc.TryGetSamples(data, out int samples) ? new MediaTime(samples, 48000) : null;
            case "ac3" when rate > 0 && data.Length > 2 && data[0] == 0x0B && data[1] == 0x77:
                return new MediaTime(1536, rate);
            case "aac" when rate > 0:
                return new MediaTime(1024, rate);
            case "mp3" when rate > 0:
                return new MediaTime(rate >= 32000 ? 1152 : 576, rate);
            default:
                return null;
        }
    }

    /// <summary>Annex B access unit to 4-byte length-prefixed NAL units, leaving out parameter sets that the sample entry already holds.</summary>
    private ReadOnlySpan<byte> ConvertAnnexB(TrackState s, ReadOnlySpan<byte> data)
    {
        // A packet made only of parameter sets keeps them, so that no sample is empty.
        bool anyKept = false;
        AnnexBReader scan = new(data);
        while (!anyKept && scan.TryRead(out ReadOnlySpan<byte> unit, out _, out _))
        {
            anyKept = unit.Length > 0 && !IsKnownSet(s, unit);
        }

        int at = 0;
        AnnexBReader r = new(data);
        while (r.TryRead(out ReadOnlySpan<byte> nal, out _, out _))
        {
            if (nal.Length == 0 || (anyKept && IsKnownSet(s, nal)))
            {
                continue;
            }

            if (at + 4L + nal.Length > _scratch.Length)
            {
                Array.Resize(ref _scratch, (int)Math.Min(Math.Max(_scratch.Length * 2L, at + 4L + nal.Length), int.MaxValue));
            }

            _scratch[at] = (byte)(nal.Length >> 24);
            _scratch[at + 1] = (byte)(nal.Length >> 16);
            _scratch[at + 2] = (byte)(nal.Length >> 8);
            _scratch[at + 3] = (byte)nal.Length;
            nal.CopyTo(_scratch.AsSpan(at + 4));
            at += 4 + nal.Length;
        }

        return _scratch.AsSpan(0, at);
    }

    private static bool IsKnownSet(TrackState s, ReadOnlySpan<byte> nal)
    {
        if (s.KnownSets.Count == 0 || !NalUnit.IsParameterSet(s.Nal, NalUnit.GetType(s.Nal, nal)))
        {
            return false;
        }

        foreach (byte[] k in s.KnownSets)
        {
            if (k.AsSpan().SequenceEqual(nal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Completes the output and returns the report.</summary>
    /// <param name="cancellationToken">Cancels the write; the output is then incomplete.</param>
    /// <returns>What was written and what did not carry over.</returns>
    /// <exception cref="MuxRefusedException">Every track was left out, or the muxer could not complete the file.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public RemuxReport Finish(CancellationToken cancellationToken = default)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The session is already finished.");
        }

        if (!_started)
        {
            Start(cancellationToken);
        }

        DrainReordered(cancellationToken);
        _finished = true;
        if (!_tracks.Exists(t => t.Out >= 0))
        {
            string why = _losses.Count > 0 ? " " + string.Join("; ", _losses.Where(l => l.Kind == LossKind.Track).Select(l => $"track {l.InputTrack}: {l.Reason}")) : string.Empty;
            throw new MuxRefusedException(MuxRefusalReason.UnsupportedCodec, "No track of the input can be written to this container." + why);
        }

        MuxReport mux = _muxer!.Finish(cancellationToken);
        List<RemuxTrackReport> tracks = [];
        foreach (TrackState s in _tracks)
        {
            tracks.Add(new RemuxTrackReport(s.Input.Index, s.Out, s.Packets, s.Mode switch { Repack.AnnexB => "annexb-to-length-prefixed", Repack.Adts => "adts-to-raw", _ => "none" }));
        }

        return new RemuxReport(mux, tracks, _losses, _changes, 0);
    }

    /// <summary>Releases the muxer. Without <see cref="Finish"/> the output stays incomplete.</summary>
    public void Dispose()
    {
        _muxer?.Dispose();
        _muxer = null;
    }

    /// <summary>The input tracks this session was made for.</summary>
    public IReadOnlyList<MediaTrack> InputTracks => _inputTracks;

    /// <summary>The destination stream.</summary>
    public Stream Output => _output;
}
