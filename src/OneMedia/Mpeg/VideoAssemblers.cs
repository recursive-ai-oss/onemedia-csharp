using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Mpeg2Video;

namespace RecursiveAI.OneMedia.Mpeg;

/// <summary>Delivers each PES packet as one packet. Used for codecs without framing support here and for data and subtitle streams.</summary>
internal sealed class PesPassthroughAssembler : TrackAssembler
{
    private readonly bool _key;
    private readonly bool _dvdSubpicture;
    private readonly bool _mpeg4Visual;
    private byte[] _pes = new byte[2048];
    private int _length;
    private bool _open;
    private PesStamp _stamp;

    // dvdSubpicture joins PES packets until a DVD sub-picture unit holds the size its first two bytes declare;
    // mpeg4Visual marks packets whose first VOP is an intra picture as key frames.
    public PesPassthroughAssembler(int index, PacketQueue queue, MediaLimits limits, DefectLog defects, bool keyframes, bool dvdSubpicture = false, bool mpeg4Visual = false)
        : base(index, queue, limits, defects)
    {
        _key = keyframes;
        _dvdSubpicture = dvdSubpicture;
        _mpeg4Visual = mpeg4Visual;
    }

    /// <summary>These streams are described by their PMT entry, not by their bytes.</summary>
    public override bool IsDescribed => true;

    public override void Push(ReadOnlySpan<byte> data, in PesStamp stamp, bool startsPes)
    {
        // DVD-Video sub-picture units: the first two bytes give the unit's size, and a unit may span PES packets.
        bool continuation = startsPes && _dvdSubpicture && _open && _length >= 2 && ((_pes[0] << 8) | _pes[1]) > _length;
        if (startsPes && !continuation)
        {
            Finish();
            _open = true;
            _stamp = stamp;
        }

        if (!_open)
        {
            return;
        }

        if (_length + data.Length > _pes.Length)
        {
            long wanted = Math.Max((long)_pes.Length * 2, (long)_length + data.Length);
            Limits.CheckPacket(wanted, _stamp.Pos);
            Array.Resize(ref _pes, (int)wanted);
        }

        data.CopyTo(_pes.AsSpan(_length));
        _length += data.Length;
    }

    public override void Flush()
    {
        Finish();
    }

    public override void Reset()
    {
        _open = false;
        _length = 0;
    }

    protected override void Scan(bool final)
    {
    }

    // ISO/IEC 14496-2 §6.2.5: a VOP starts with 00 00 01 B6 and two bits of vop_coding_type; 0 is an intra picture.
    private static bool StartsWithIntraVop(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> code = [0, 0, 1, 0xB6];
        int at = data.IndexOf(code);
        return at >= 0 && at + 4 < data.Length && (data[at + 4] >> 6) == 0;
    }

    private void Finish()
    {
        if (_open && _length > 0)
        {
            long pts = _stamp.Pts >= 0 ? _stamp.Pts : _stamp.Dts;
            long dts = _stamp.Dts >= 0 ? _stamp.Dts : pts;
            bool key = _key || (_mpeg4Visual && StartsWithIntraVop(_pes.AsSpan(0, _length)));
            Emit(_pes.AsSpan(0, _length), _stamp.Pos, key ? PacketAttributes.Keyframe : PacketAttributes.None, pts, dts, 0);
        }

        _open = false;
        _length = 0;
    }
}

/// <summary>Common work for video: the timing of units that carry none, durations from the frame rate or the next unit, and parameter discovery.</summary>
internal abstract class VideoAssembler : TrackAssembler
{
    private byte[] _pending = new byte[4096];
    private int _pendingLength;
    private bool _hasPending;
    private PacketAttributes _pendingFlags;
    private long _pendingPts;
    private long _pendingDts;
    private long _pendingPos;
    private long _lastDts = -1;
    private long _lastDuration;
    private long _anchorPts = -1;
    private long _rateNumerator;
    private long _rateDenominator;
    private long _count;
    private int _anchorReference;
    private bool _pendingField;
    private int _pendingExtra;
    private int _openParity = -1;   // parity (0 top, 1 bottom) of a first field still waiting for its second field
    private long _openPts = -1;
    private long _openDts = -1;

    protected VideoAssembler(int index, PacketQueue queue, MediaLimits limits, DefectLog defects)
        : base(index, queue, limits, defects)
    {
    }

    /// <summary>
    /// For streams with no container timing (Annex B, MPEG video and AV1 files): decode times count the units at the stream's own
    /// frame rate (25 frames per second when it declares none), and the track timescale is that rate's numerator so every time is exact.
    /// </summary>
    public bool ElementaryMode { get; init; }

    /// <summary>In elementary mode, presentation time equals decode time (true for streams without reordering, such as AV1).</summary>
    public bool PtsFollowsDts { get; init; }

    /// <inheritdoc />
    public override long Timescale => ElementaryMode && _rateNumerator > 0 ? _rateNumerator : Clock;

    /// <summary>Duration of one frame in timescale ticks from the stream's declared frame rate, or 0.</summary>
    protected long NominalDuration
    {
        get
        {
            if (ElementaryMode)
            {
                return _rateDenominator;
            }

            if (Parameters is { FrameRateNumerator: > 0, FrameRateDenominator: > 0 } p)
            {
                return Clock * p.FrameRateDenominator / p.FrameRateNumerator; // rounded down, as the packet's whole ticks
            }

            return 0;
        }
    }

    /// <summary>
    /// Takes the first <paramref name="length"/> buffered bytes as one access unit. <paramref name="fieldParity"/> is 0 or 1 when the
    /// unit is a single field picture (top or bottom), -1 for a frame.
    /// </summary>
    protected void CutUnit(int length, bool key, int temporalReference = -1, int fieldParity = -1, int extraHalves = 0, bool weakKey = false)
    {
        TakeStamp(Head, out PesStamp stamp, out long pos);
        long pts, dts;
        if (ElementaryMode)
        {
            key |= weakKey;
            if (_rateNumerator == 0)
            {
                long n = Parameters is { FrameRateNumerator: > 0, FrameRateDenominator: > 0 } fp ? fp.FrameRateNumerator : 25;
                long d = Parameters is { FrameRateNumerator: > 0, FrameRateDenominator: > 0 } fq ? fq.FrameRateDenominator : 1;
                long g = Gcd(n, d);
                _rateNumerator = n / g;
                _rateDenominator = d / g;
            }

            dts = _count++ * _rateDenominator;
            pts = PtsFollowsDts ? dts : -1;
            PacketAttributes f = (key ? PacketAttributes.Keyframe : PacketAttributes.None) | (CoversLoss(Head, length) ? PacketAttributes.Corrupt : PacketAttributes.None);
            Queue(Buffered[..length], pos, f, pts, dts, false, 0);
            Consume(length);
            return;
        }

        if (stamp.HasTime)
        {
            pts = stamp.Pts >= 0 ? stamp.Pts : stamp.Dts;
            dts = stamp.Dts >= 0 ? stamp.Dts : pts;
        }
        else
        {
            // A unit that starts inside a PES without a timestamp: decode time continues from the previous unit; presentation time is unknown.
            pts = -1;
            dts = _lastDts >= 0 && _lastDuration > 0 ? _lastDts + _lastDuration : -1;
            // MPEG-1/2 video states the display order inside a group of pictures (ISO/IEC 13818-2 §6.3.9 temporal_reference):
            // a picture without a stamp is placed relative to the last stamped one of the same group.
            long frame = NominalDuration > 0 ? NominalDuration : _lastDuration;
            if (temporalReference >= 0 && _anchorPts >= 0 && frame > 0 && Math.Abs(temporalReference - _anchorReference) < 64)
            {
                pts = _anchorPts + ((temporalReference - _anchorReference) * frame);
            }
        }

        bool field = fieldParity >= 0;
        bool secondField = field && _openParity >= 0 && _openParity != fieldParity;
        if (weakKey && !secondField)
        {
            key = true; // an intra picture that opens with a parameter set, and is not the second field of a frame
        }

        if (field)
        {
            long half = (NominalDuration + 1) / 2;
            if (_openParity >= 0 && _openParity != fieldParity)
            {
                // The second field of a frame: a field without a stamp of its own is half a frame after the first.
                if (!stamp.HasTime && half > 0 && _openDts >= 0)
                {
                    dts = _openDts + half;
                    pts = _openPts >= 0 ? _openPts + half : -1;
                }

                _openParity = -1;
            }
            else
            {
                _openParity = fieldParity;
                _openPts = pts;
                _openDts = dts;
            }
        }
        else
        {
            _openParity = -1;
        }

        if (stamp.HasTime && temporalReference >= 0)
        {
            _anchorPts = pts;
            _anchorReference = temporalReference;
        }

        PacketAttributes flags = (key ? PacketAttributes.Keyframe : PacketAttributes.None) | (CoversLoss(Head, length) ? PacketAttributes.Corrupt : PacketAttributes.None);
        Queue(Buffered[..length], pos, flags, pts, dts, field, extraHalves);
        Consume(length);
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return Math.Max(1, a);
    }

    protected override void FlushPending()
    {
        Release(0);
    }

    private void Queue(ReadOnlySpan<byte> unit, long pos, PacketAttributes flags, long pts, long dts, bool field, int extraHalves)
    {
        // The duration of the previous unit is the distance to this one's decode time unless the stream declares a frame rate.
        long nominal = NominalDuration;
        long duration = nominal;
        if (_hasPending && _pendingField && nominal > 0)
        {
            duration = nominal / 2; // a field lasts half a frame, rounded down
        }
        else if (_hasPending && _pendingExtra > 0 && nominal > 0)
        {
            duration = nominal * (2 + _pendingExtra) / 2; // repeated fields lengthen the picture (ISO/IEC 13818-2 §6.3.10)
        }
        else if (_hasPending && _pendingDts >= 0 && dts >= 0 && dts > _pendingDts && nominal == 0)
        {
            duration = dts - _pendingDts;
        }

        Release(duration);
        if (unit.Length > _pending.Length)
        {
            Array.Resize(ref _pending, Math.Max(unit.Length, _pending.Length * 2));
        }

        unit.CopyTo(_pending);
        _pendingLength = unit.Length;
        _pendingFlags = flags;
        _pendingPts = pts;
        _pendingDts = dts;
        _pendingPos = pos;
        _pendingField = field;
        _pendingExtra = extraHalves;
        _hasPending = true;
        if (dts >= 0)
        {
            _lastDts = dts;
        }
        else if (_lastDts >= 0 && _lastDuration > 0)
        {
            _lastDts += _lastDuration;
        }

        _lastDuration = nominal > 0 ? nominal : _lastDuration;
    }

    private void Release(long duration)
    {
        if (!_hasPending)
        {
            return;
        }

        _hasPending = false;
        if (duration == 0)
        {
            duration = NominalDuration > 0 ? (_pendingField ? NominalDuration / 2 : NominalDuration * (2 + _pendingExtra) / 2) : _lastDuration;
        }

        if (duration > 0)
        {
            _lastDuration = duration;
        }

        Emit(_pending.AsSpan(0, _pendingLength), _pendingPos, _pendingFlags, _pendingPts, _pendingDts, duration);
    }

    public override void Reset()
    {
        base.Reset();
        _hasPending = false;
        _lastDts = -1;
        _anchorPts = -1;
        _openParity = -1;
        _count = 0;
    }
}

/// <summary>Receives an SEI NAL unit payload; returns true when it has what it wanted and needs no more.</summary>
internal delegate bool SeiHandlerDelegate(ReadOnlySpan<byte> sei);

/// <summary>H.264, H.265 and H.266 in Annex B form: access units are cut at the NAL units that open a new one (H.264 §7.4.1.2.3, H.265 §7.4.2.4.4, H.266 §7.4.2.4.3).</summary>
internal sealed class NalVideoAssembler : VideoAssembler
{
    private readonly NalCodec _codec;
    private int _scan;      // buffer-relative index from which start codes are still to be looked for
    private bool _open;     // the current unit has seen its first NAL unit
    private bool _seenVcl;
    private bool _key;
    private bool _paramSets;
    private int _seiUnits;
    private bool _unitHasSps;
    private bool _unitIntra;
    private int _unitParity = -1;
    private bool _frameMbsOnly = true;
    private int _log2FrameNum;
    private bool _separatePlanes;

    /// <summary>Called with the payload of each SEI NAL unit (after its header) until it returns true; AVCHD metadata is found this way.</summary>
    public SeiHandlerDelegate? SeiHandler { get; set; }

    public NalVideoAssembler(NalCodec codec, int index, PacketQueue queue, MediaLimits limits, DefectLog defects)
        : base(index, queue, limits, defects)
    {
        _codec = codec;
    }

    public override void Reset()
    {
        base.Reset();
        _scan = 0;
        _open = _seenVcl = _key = _paramSets = false;
        _unitHasSps = _unitIntra = false;
        _unitParity = -1;
    }

    protected override void Scan(bool final)
    {
        while (true)
        {
            ReadOnlySpan<byte> span = Buffered;
            int idx = _scan < span.Length ? span[_scan..].IndexOf(StartCode) : -1;
            if (idx < 0)
            {
                _scan = Math.Max(_scan, span.Length - 2);
                break;
            }

            int p = _scan + idx;
            int header = NalUnit.HeaderSize(_codec);
            if (span.Length < p + 3 + header + 1 && !final)
            {
                _scan = p; // wait for the bytes that classify the NAL unit
                break;
            }

            // A zero byte before the three-byte code makes a four-byte start code, which belongs to the new NAL unit.
            int nalStart = p > 0 && span[p - 1] == 0 ? p - 1 : p;
            ReadOnlySpan<byte> nal = span[(p + 3)..];
            int type = NalUnit.GetType(_codec, nal);
            bool startsUnit = StartsUnit(type, nal, header);
            if (!_open)
            {
                // Bytes before the first start code are the tail of a unit that began before this reader did.
                if (nalStart > 0)
                {
                    Consume(nalStart);
                    p -= nalStart;
                    nalStart = 0;
                    span = Buffered;
                }

                _open = true;
            }
            else if (startsUnit)
            {
                CompleteUnit(nalStart);
                p -= nalStart;
                span = Buffered;
            }

            Track(type);
            _scan = p + 3;
        }

        if (final && Buffered.Length > 0 && _open)
        {
            CompleteUnit(Buffered.Length);
            _open = false;
        }
    }

    private static ReadOnlySpan<byte> StartCode => [0, 0, 1];

    private void CompleteUnit(int length)
    {
        if (SeiHandler is { } handler)
        {
            // Camera data rides in the SEI of the first few access units; look there, then stop looking.
            AnnexBReader reader = new(Buffered[..length]);
            while (reader.TryRead(out ReadOnlySpan<byte> sei, out _, out _))
            {
                if (NalUnit.GetType(_codec, sei) == 6 && _codec == NalCodec.H264 && handler(sei[1..]))
                {
                    SeiHandler = null;
                    break;
                }
            }

            if (++_seiUnits >= 60)
            {
                SeiHandler = null;
            }
        }

        Classify(length);
        if (!Describe())
        {
            _paramSets = false;
        }

        // A unit that opens with a sequence parameter set and codes an intra picture is where a decoder can start, as encoders
        // that write no recovery point SEI intend (observed in AVCHD camera streams).
        CutUnit(length, _key, -1, _unitParity, 0, _unitHasSps && _unitIntra);
        _seenVcl = false;
        _key = false;
        _unitHasSps = _unitIntra = false;
        _unitParity = -1;
        _scan = 0;
    }

    private bool Describe()
    {
        if (Parameters is null && _paramSets)
        {
            if (CodecParameterReader.TryDescribeAnnexB(_codec, Buffered.Slice(0, Math.Min(Buffered.Length, 1 << 16)).ToArray(), out CodecParameters? p) && p is not null)
            {
                Parameters = p;
                return true;
            }
        }

        return Parameters is not null;
    }

    private bool StartsUnit(int type, ReadOnlySpan<byte> nal, int header)
    {
        switch (_codec)
        {
            case NalCodec.H264:
                if (type is 9 or 6 or 7 or 8 or (>= 14 and <= 18))
                {
                    return _seenVcl;
                }

                // H.264 §7.4.1.2.4: first_mb_in_slice equal to 0 is ue(v) '1', the top bit after the NAL header.
                return type is >= 1 and <= 5 && _seenVcl && nal.Length > header && (nal[header] & 0x80) != 0;
            case NalCodec.Hevc:
                if (type is 35 or (>= 32 and <= 34) or 39 or (>= 41 and <= 44) or (>= 48 and <= 55))
                {
                    return _seenVcl;
                }

                // H.265 §7.4.7.1: first_slice_segment_in_pic_flag is the first bit after the two-byte NAL header.
                return type <= 31 && _seenVcl && nal.Length > header && (nal[header] & 0x80) != 0;
            default:
                if (type is 20 or (>= 14 and <= 17) or 19 or 23)
                {
                    return _seenVcl;
                }

                // H.266 §7.3.7.1: a slice that carries its picture header (sh_picture_header_in_slice_header_flag) opens a picture.
                return type <= 11 && _seenVcl && nal.Length > header && (nal[header] & 0x80) != 0;
        }
    }

    private void Track(int type)
    {
        if (NalUnit.IsParameterSet(_codec, type))
        {
            _paramSets = true;
        }

        if (NalUnit.IsSlice(_codec, type))
        {
            _seenVcl = true;
            if (NalUnit.IsRandomAccess(_codec, type))
            {
                _key = true;
            }
        }
    }

    /// <summary>
    /// Reads what only the whole unit can tell (the NAL units may arrive in pieces): a recovery point SEI, the sequence parameter set's
    /// field coding and frame_num width, and the first slice's type and field parity.
    /// </summary>
    private void Classify(int length)
    {
        AnnexBReader reader = new(Buffered[..length]);
        bool firstSlice = true;
        while (reader.TryRead(out ReadOnlySpan<byte> nal, out _, out _))
        {
            int type = NalUnit.GetType(_codec, nal);
            if (_codec == NalCodec.H264 && type == 7 && Codecs.H264.SequenceParameterSet.TryParse(nal, out Codecs.H264.SequenceParameterSet sps))
            {
                _unitHasSps = true;
                _frameMbsOnly = sps.FrameMbsOnly;
                _log2FrameNum = sps.Log2MaxFrameNum;
                _separatePlanes = sps.SeparateColourPlane;
            }
            else if (((_codec == NalCodec.H264 && type == 6) || (_codec == NalCodec.Hevc && type == 39)) && HasRecoveryPoint(nal[Math.Min(NalUnit.HeaderSize(_codec), nal.Length)..]))
            {
                _key = true;
            }
            else if (_codec == NalCodec.H264 && firstSlice && type is >= 1 and <= 5)
            {
                firstSlice = false;
                ReadSliceHeader(nal[1..]);
            }
        }
    }

    // H.264 §7.3.3 slice_header: first_mb_in_slice, slice_type, pic_parameter_set_id, [colour_plane_id], frame_num, then
    // field_pic_flag and bottom_field_flag when the sequence may code fields.
    private void ReadSliceHeader(ReadOnlySpan<byte> rbsp)
    {
        BitReader r = new(rbsp[..Math.Min(rbsp.Length, 24)], removeEmulationPrevention: true);
        r.ReadUe();
        uint sliceType = r.ReadUe() % 5; // 0 P, 1 B, 2 I, 3 SP, 4 SI
        _unitIntra = sliceType is 2 or 4;
        r.ReadUe();
        if (_separatePlanes)
        {
            r.ReadBits(2);
        }

        if (_log2FrameNum > 0)
        {
            r.ReadBits(_log2FrameNum);
        }

        if (!_frameMbsOnly && !r.Overrun && r.ReadFlag())
        {
            _unitParity = r.ReadBit();
        }
    }

    // H.264 Annex D / H.265 Annex D: an SEI message with payloadType 6 is a recovery point, a random access point without an IDR.
    private static bool HasRecoveryPoint(ReadOnlySpan<byte> escaped)
    {
        // Payload sizes count bytes after emulation prevention, so unescape first (SEI units are small).
        Span<byte> clean = stackalloc byte[Math.Min(escaped.Length, 512)]; // bounded: at most 512 bytes of the unit are examined
        int n = 0;
        int zeros = 0;
        for (int i = 0; i < escaped.Length && n < clean.Length; i++)
        {
            if (zeros >= 2 && escaped[i] == 3)
            {
                zeros = 0;
                continue;
            }

            zeros = escaped[i] == 0 ? zeros + 1 : 0;
            clean[n++] = escaped[i];
        }

        ReadOnlySpan<byte> rbsp = clean[..n];
        int p = 0;
        while (p < rbsp.Length)
        {
            int type = 0;
            while (p < rbsp.Length && rbsp[p] == 0xFF)
            {
                type += 255;
                p++;
            }

            if (p >= rbsp.Length)
            {
                return false;
            }

            type += rbsp[p++];
            int size = 0;
            while (p < rbsp.Length && rbsp[p] == 0xFF)
            {
                size += 255;
                p++;
            }

            if (p >= rbsp.Length)
            {
                return false;
            }

            size += rbsp[p++];
            if (type == 6)
            {
                return true;
            }

            p += size;
            if (p < rbsp.Length && rbsp[p] == 0x80)
            {
                return false;
            }
        }

        return false;
    }
}

/// <summary>MPEG-1 and MPEG-2 video: one packet per picture, cut at picture, group-of-pictures and sequence headers (ISO/IEC 13818-2 §6.2.2).</summary>
internal sealed class Mpeg2VideoAssembler : VideoAssembler
{
    private int _scan;
    private bool _open;
    private bool _seenPicture;
    private bool _key;
    private int _reference = -1;
    private int _structure; // picture_structure of the access unit's first picture: 1 top field, 2 bottom field, 3 frame, 0 not seen
    private int _pictures;
    private int _pendingStart = -1; // a picture that may be the second field of the current unit, from this offset
    private bool _progressiveSequence;
    private int _extraHalves;       // half-frame periods the first picture adds by repeating fields

    public Mpeg2VideoAssembler(int index, PacketQueue queue, MediaLimits limits, DefectLog defects)
        : base(index, queue, limits, defects)
    {
    }

    public override void Reset()
    {
        base.Reset();
        _scan = 0;
        _open = _seenPicture = _key = false;
        _structure = _pictures = _extraHalves = 0;
        _pendingStart = -1;
    }

    protected override void Scan(bool final)
    {
        while (true)
        {
            ReadOnlySpan<byte> span = Buffered;
            int idx = _scan < span.Length ? span[_scan..].IndexOf((ReadOnlySpan<byte>)[0, 0, 1]) : -1;
            if (idx < 0)
            {
                _scan = Math.Max(_scan, span.Length - 2);
                break;
            }

            int p = _scan + idx;
            if (span.Length < p + 8 && !final)
            {
                _scan = p;
                break;
            }

            int code = p + 3 < span.Length ? span[p + 3] : 0;
            if (!_open)
            {
                if (p > 0)
                {
                    Consume(p);
                    p = 0;
                    span = Buffered;
                }

                if (code is not (Mpeg2StartCode.Picture or Mpeg2StartCode.SequenceHeader or Mpeg2StartCode.GroupOfPictures))
                {
                    // Slices, extensions or user data before the first picture belong to a picture whose start was not captured: skip them.
                    _scan = 3;
                    continue;
                }

                _open = true;
            }
            else if (_pendingStart >= 0 && code is Mpeg2StartCode.SequenceHeader or Mpeg2StartCode.GroupOfPictures)
            {
                // Two fields of one frame never have a group or sequence header between them: the pending picture began a new unit.
                CompleteAtPending();
                continue;
            }
            else if (_seenPicture && _pendingStart < 0 && code == Mpeg2StartCode.Picture && _pictures == 1 && _structure is 1 or 2)
            {
                // ISO/IEC 13818-2 §6.1.1.1: a frame may be coded as two field pictures. The opposite-parity field that follows belongs to the
                // same access unit, which is known from its picture_coding_extension; wait for it.
                _pendingStart = p;
            }
            else if (_seenPicture && _pendingStart < 0 && (code == Mpeg2StartCode.Picture || code == Mpeg2StartCode.SequenceHeader || code == Mpeg2StartCode.GroupOfPictures))
            {
                Complete(p);
                p = 0;
                span = Buffered;
            }
            else if (_seenPicture && _pendingStart >= 0 && code == Mpeg2StartCode.Picture)
            {
                // A third picture without an extension in between: the pending one stands alone.
                CompleteAtPending();
                continue;
            }

            if (code == Mpeg2StartCode.Picture && _pendingStart < 0)
            {
                if (!_seenPicture && p + 5 < span.Length)
                {
                    _reference = (span[p + 4] << 2) | (span[p + 5] >> 6);
                }

                _seenPicture = true;
                _pictures++;
                // §6.3.9: temporal_reference(10), picture_coding_type(3): type 1 is an intra picture.
                if (p + 5 < span.Length && ((span[p + 5] >> 3) & 7) == 1)
                {
                    _key = true;
                }
            }
            else if (code == Mpeg2StartCode.Extension && !_seenPicture && p + 5 < span.Length && (span[p + 4] >> 4) == 1)
            {
                _progressiveSequence = (span[p + 5] & 0x08) != 0; // §6.3.5 sequence_extension: progressive_sequence follows the 4-bit level
            }
            else if (code == Mpeg2StartCode.Extension && _seenPicture && p + 6 < span.Length && (span[p + 4] >> 4) == 8)
            {
                // §6.3.10: extension_start_code_identifier 8 is the picture coding extension; picture_structure is the last two bits of its third byte.
                int structure = span[p + 6] & 3;
                if (_pendingStart >= 0)
                {
                    // §6.3.9: both fields of a frame carry the same temporal_reference; a lone field followed by another frame's field does not pair.
                    int pendingReference = (span[_pendingStart + 4] << 2) | (span[_pendingStart + 5] >> 6);
                    if (structure == 3 - _structure && structure is 1 or 2 && pendingReference == _reference)
                    {
                        _pictures = 2;
                        DiscardStampsInside(_pendingStart);
                        _pendingStart = -1;
                    }
                    else
                    {
                        CompleteAtPending();
                        continue;
                    }
                }
                else if (_structure == 0)
                {
                    _structure = structure;
                    // §6.3.10: repeat_first_field and top_field_first are bits 1 and 7 of the fourth byte. In a progressive sequence the frame
                    // is shown two or three times (three when top_field_first is set), otherwise a frame picture shows its first field again.
                    if (p + 7 < span.Length && structure == 3 && (span[p + 7] & 0x02) != 0)
                    {
                        _extraHalves = _progressiveSequence ? ((span[p + 7] & 0x80) != 0 ? 4 : 2) : 1;
                    }
                }
            }

            _scan = p + 3;
        }

        if (final && _open && Buffered.Length > 0)
        {
            _pendingStart = -1;
            Complete(Buffered.Length);
            _open = false;
        }
    }

    // The picture held back as a possible second field turned out to start its own unit: cut before it and read it again as a first picture.
    private void CompleteAtPending()
    {
        int at = _pendingStart;
        _pendingStart = -1;
        Complete(at);
    }

    private void Complete(int length)
    {
        if (Parameters is null && CodecParameterReader.TryDescribeSample(CodecId.Mpeg2Video, Buffered[..length], out CodecParameters? p) && p is not null)
        {
            Parameters = p;
            // ISO/IEC 13818-2 §6.2.2.3: an MPEG-1 stream has no sequence extension.
            DetectedCodec = Mpeg2SequenceHeader.TryFind(Buffered[..length], out Mpeg2SequenceHeader sh) && !sh.HasExtension ? new CodecId("mpeg1video") : CodecId.Mpeg2Video;
        }

        CutUnit(length, _key, _reference, -1, _extraHalves);
        _seenPicture = false;
        _reference = -1;
        _key = false;
        _structure = _pictures = _extraHalves = 0;
        _pendingStart = -1;
        _scan = 0;
    }
}
