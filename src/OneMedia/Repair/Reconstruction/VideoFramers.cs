using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Av1;
using H264 = RecursiveAI.OneMedia.Codecs.H264;
using Hevc = RecursiveAI.OneMedia.Codecs.Hevc;

namespace RecursiveAI.OneMedia.Repair.Reconstruction;

/// <summary>What a slice header says about where its picture sits in output order (header fields only).</summary>
internal readonly record struct PicOrder(
    bool Has,
    bool ResetsOrder,
    int Lsb,
    int LsbBits,
    bool IsReference,
    int TemporalId,
    bool FieldPicture,
    int OrderType);

/// <summary>
/// H.264 and H.265 samples in the length-prefixed form MP4 stores (ISO/IEC 14496-15 §5.3, §8.3): a run of NAL units, each preceded by its
/// length. One sample is one access unit. The framer follows ITU-T H.264 §7.4.1.2.3 and H.265 §7.4.2.4.4 for where an access unit
/// starts, and checks every NAL unit header and the first slice header for plausibility, so audio bytes or the middle of another
/// track's sample are not mistaken for video.
/// </summary>
internal sealed class NalVideoFramer : TrackFramer
{
    private readonly Dictionary<int, H264.SequenceParameterSet> _avcSps = [];
    private readonly Dictionary<int, H264.PictureParameterSet> _avcPps = [];
    private readonly Dictionary<int, Hevc.SequenceParameterSet> _hevcSps = [];
    private readonly Dictionary<int, Hevc.PictureParameterSet> _hevcPps = [];

    private readonly List<byte[]> _rawSets = [];
    private int _prevRefFrameNum = -1;   // frame_num of the last reference picture accepted (H.264 §7.4.3), -1 before the first

    public NalVideoFramer(NalCodec codec, int lengthSize)
    {
        Codec = codec;
        LengthSize = lengthSize;
    }

    public NalCodec Codec { get; }

    /// <summary>
    /// Check that each picture's frame_num follows the last accepted reference picture's. On by default; a trial walk over positions that were
    /// not accepted yet turns it off, because the state only advances over accepted samples.
    /// </summary>
    public bool CheckSequence { get; set; } = true;

    /// <summary>The part of the framer's state that accepting a sample changes (frame numbering), so a trial acceptance can be undone.</summary>
    public int SnapshotState() => _prevRefFrameNum;

    /// <summary>Puts back a state taken by <see cref="SnapshotState"/>.</summary>
    public void RestoreState(int state) => _prevRefFrameNum = state;

    public int LengthSize { get; }

    public override TrackKind Kind => TrackKind.Video;

    /// <summary>True once an SPS and a PPS are known, from the reference's configuration or from the stream.</summary>
    public bool HasParameterSets => Codec == NalCodec.H264 ? _avcSps.Count > 0 && _avcPps.Count > 0 : _hevcSps.Count > 0 && _hevcPps.Count > 0;

    public bool TryGetAvcSps(out H264.SequenceParameterSet sps)
    {
        foreach (H264.SequenceParameterSet s in _avcSps.Values)
        {
            sps = s;
            return true;
        }

        sps = default;
        return false;
    }

    public bool TryGetHevcSps(out Hevc.SequenceParameterSet sps)
    {
        foreach (Hevc.SequenceParameterSet s in _hevcSps.Values)
        {
            sps = s;
            return true;
        }

        sps = default;
        return false;
    }

    /// <summary>The parameter set NAL units learned so far, in the order they were seen (VPS, SPS, PPS).</summary>
    public IReadOnlyList<byte[]> ParameterSetNals => _rawSets;

    /// <summary>Learns a parameter set NAL unit (with its header).</summary>
    public void LearnParameterSet(ReadOnlySpan<byte> nal)
    {
        if (nal.IsEmpty)
        {
            return;
        }

        if (IsParameterSet(NalType(nal[0])))
        {
            bool known = false;
            foreach (byte[] r in _rawSets)
            {
                if (r.AsSpan().SequenceEqual(nal))
                {
                    known = true;
                    break;
                }
            }

            if (!known)
            {
                _rawSets.Add(nal.ToArray());
            }
        }

        if (Codec == NalCodec.H264)
        {
            int type = nal[0] & 0x1f;
            if (type == H264.H264NalType.Sps && H264.SequenceParameterSet.TryParse(nal, out H264.SequenceParameterSet s))
            {
                _avcSps[s.SpsId] = s;
            }
            else if (type == H264.H264NalType.Pps && H264.PictureParameterSet.TryParse(nal, out H264.PictureParameterSet p))
            {
                _avcPps[p.PpsId] = p;
            }
        }
        else if (Codec == NalCodec.Hevc && nal.Length > 2)
        {
            int type = (nal[0] >> 1) & 0x3f;
            if (type == Hevc.HevcNalType.Sps && Hevc.SequenceParameterSet.TryParse(nal, out Hevc.SequenceParameterSet s))
            {
                _hevcSps[s.SpsId] = s;
            }
            else if (type == Hevc.HevcNalType.Pps && Hevc.PictureParameterSet.TryParse(nal, out Hevc.PictureParameterSet p))
            {
                _hevcPps[p.PpsId] = p;
            }
        }
    }

    public override void Accept(ReadOnlySpan<byte> sample)
    {
        int pos = 0;
        while (pos + LengthSize < sample.Length)
        {
            int len = ReadLength(sample[pos..]);
            pos += LengthSize;
            if (len <= 0 || pos + len > sample.Length)
            {
                return;
            }

            int type = NalType(sample[pos]);
            if (IsParameterSet(type))
            {
                LearnParameterSet(sample.Slice(pos, len));
            }

            pos += len;
        }
    }

    private int ReadLength(ReadOnlySpan<byte> d)
    {
        int v = 0;
        for (int i = 0; i < LengthSize; i++)
        {
            v = (v << 8) | d[i];
        }

        return v;
    }

    private int NalType(byte first) => Codec == NalCodec.H264 ? first & 0x1f : (first >> 1) & 0x3f;

    private bool IsParameterSet(int type) => Codec == NalCodec.H264 ? type is 7 or 8 : type is 32 or 33 or 34;

    private bool IsVcl(int type) => Codec == NalCodec.H264 ? type is 1 or 5 : type <= 9 || type is >= 16 and <= 21;

    // H.264 Table 7-1 and H.265 Table 7-1: the NAL unit types an ordinary MP4 sample contains.
    private bool IsKnownType(int type) => Codec == NalCodec.H264
        ? type is 1 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12
        : type <= 9 || type is >= 16 and <= 21 or >= 32 and <= 40;

    // Non-VCL units that open an access unit when a picture was already seen (H.264 §7.4.1.2.3, H.265 §7.4.2.4.4).
    private bool OpensAccessUnit(int type) => Codec == NalCodec.H264 ? type is 6 or 7 or 8 or 9 : type is 32 or 33 or 34 or 35 or 39;

    // H.264 §7.4.1 and H.265 §7.4.2: inside a NAL unit the byte sequences 00 00 00, 00 00 01 and 00 00 02 never occur (the encoder inserts an
    // emulation prevention byte), so a stretch of audio or other data that has one is not a NAL unit. A cut-off NAL unit is checked as far as it goes.
    private static bool EmulationPreventionHolds(ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> z0 = [0, 0, 0];
        ReadOnlySpan<byte> z1 = [0, 0, 1];
        ReadOnlySpan<byte> z2 = [0, 0, 2];
        return payload.IndexOf(z0) < 0 && payload.IndexOf(z1) < 0 && payload.IndexOf(z2) < 0;
    }

    private bool HeaderPlausible(ReadOnlySpan<byte> nal)
    {
        if (Codec == NalCodec.H264)
        {
            // forbidden_zero_bit is 0; nal_ref_idc of an SPS, PPS or IDR slice is not 0 (H.264 §7.4.1).
            if ((nal[0] & 0x80) != 0)
            {
                return false;
            }

            int type = nal[0] & 0x1f;
            int refIdc = (nal[0] >> 5) & 3;
            return IsKnownType(type) && (type is not (5 or 7 or 8) || refIdc != 0) && (type is not (6 or 9 or 10 or 11 or 12) || refIdc == 0);
        }

        if (nal.Length < 2 || (nal[0] & 0x80) != 0)
        {
            return false;
        }

        // H.265 §7.3.1.2: nuh_layer_id 0 for a plain stream, nuh_temporal_id_plus1 at least 1.
        int layer = ((nal[0] & 1) << 5) | (nal[1] >> 3);
        return layer == 0 && (nal[1] & 7) != 0 && IsKnownType(NalType(nal[0]));
    }

    // first_mb_in_slice == 0 (H.264 §7.3.3) / first_slice_segment_in_pic_flag (H.265 §7.3.6.1) is the first bit after the NAL header.
    private bool FirstSliceOfPicture(ReadOnlySpan<byte> nal)
    {
        int header = Codec == NalCodec.H264 ? 1 : 2;
        return nal.Length > header && (nal[header] & 0x80) != 0;
    }

    public override FrameStatus TryFrame(ReadOnlySpan<byte> data, bool atEnd, out FrameInfo frame)
    {
        frame = default;
        int pos = 0;
        bool seenVcl = false;
        bool key = false;
        bool firstUnit = true;
        bool intraFirst = false;
        bool recovery = false;
        int firstVclType = -1;
        while (true)
        {
            if (pos + LengthSize + 1 > data.Length)
            {
                if (!atEnd)
                {
                    return FrameStatus.NeedMore;
                }

                if (!seenVcl)
                {
                    return pos == 0 ? FrameStatus.No : FrameStatus.Partial;
                }

                break;
            }

            int len = ReadLength(data[pos..]);
            if (len < 1 || len > MaxSample)
            {
                if (!seenVcl)
                {
                    return FrameStatus.No;
                }

                break;
            }

            int start = pos + LengthSize;
            int available = Math.Min(len, data.Length - start);
            ReadOnlySpan<byte> nal = data.Slice(start, available);
            if (!HeaderPlausible(nal))
            {
                if (!seenVcl)
                {
                    return FrameStatus.No;
                }

                break;
            }

            int type = NalType(nal[0]);
            bool vcl = IsVcl(type);
            bool opens = vcl ? seenVcl && FirstSliceOfPicture(nal) : seenVcl && OpensAccessUnit(type);
            if (opens)
            {
                // The next access unit begins here.
                break;
            }

            if (seenVcl && !ContinuesAccessUnit(nal, type, vcl, firstVclType))
            {
                // Whatever follows the picture is not another slice of it (audio, another track's sample, damage): the access unit ends here.
                break;
            }

            if (firstUnit)
            {
                // An access unit starts with a delimiter, a parameter set, a prefix SEI or the first slice of a picture.
                bool ok = vcl ? FirstSliceOfPicture(nal) : OpensAccessUnit(type);
                if (!ok)
                {
                    return FrameStatus.No;
                }

                firstUnit = false;
            }

            // The cheap header checks come before anything that reads the whole unit or asks for more bytes, so stray data is turned down fast.
            if (vcl && !seenVcl)
            {
                if (!SliceHeaderPlausible(nal, type, out intraFirst))
                {
                    return FrameStatus.No;
                }
            }

            if (!EmulationPreventionHolds(nal[Math.Min(nal.Length, Codec == NalCodec.H264 ? 1 : 2)..]))
            {
                if (!seenVcl)
                {
                    return FrameStatus.No;
                }

                break;
            }

            if (len > data.Length - start)
            {
                // This unit runs past what was offered.
                return atEnd ? FrameStatus.Partial : FrameStatus.NeedMore;
            }

            if (vcl)
            {
                if (!seenVcl)
                {
                    firstVclType = type;
                }

                seenVcl = true;
                key |= Codec == NalCodec.H264 ? type == 5 : type is >= 16 and <= 21;
            }
            else if (Codec == NalCodec.H264 && type == 6 && HasRecoveryPoint(nal[1..]))
            {
                recovery = true;
            }

            pos = start + len;
            if (pos > MaxSample)
            {
                return FrameStatus.No;
            }
        }

        if (!seenVcl)
        {
            return FrameStatus.No;
        }

        frame = new FrameInfo { Length = pos, Key = key || (recovery && intraFirst) };
        return FrameStatus.Ok;
    }

    // After a picture's first slice only more slices of the same picture, or the few trailing units an encoder may add, continue the access unit.
    private bool ContinuesAccessUnit(ReadOnlySpan<byte> nal, int type, bool vcl, int firstVclType)
    {
        if (vcl)
        {
            // A further slice: same kind of picture as the first, a known picture parameter set, and (H.264) a first macroblock inside the picture.
            if (Codec == NalCodec.H264)
            {
                if (type != firstVclType)
                {
                    return false;
                }

                BitReader r = new(nal[..Math.Min(nal.Length, 16)], removeEmulationPrevention: true);
                r.SkipBits(8);
                uint firstMb = r.ReadUe();
                uint sliceType = r.ReadUe();
                uint ppsId = r.ReadUe();
                if (r.Overrun || sliceType > 9 || ppsId > 255 || (_avcPps.Count > 0 && !_avcPps.TryGetValue((int)ppsId, out _)))
                {
                    return false;
                }

                if (_avcPps.TryGetValue((int)ppsId, out H264.PictureParameterSet pps) && _avcSps.TryGetValue(pps.SpsId, out H264.SequenceParameterSet sps))
                {
                    long mbs = (long)((sps.CodedWidth + 15) / 16) * ((sps.CodedHeight + 15) / 16);
                    return firstMb < mbs;
                }

                return firstMb < (1u << 16);
            }

            bool irap = type >= 16;
            BitReader h = new(nal[..Math.Min(nal.Length, 16)], removeEmulationPrevention: true);
            h.SkipBits(16);
            h.ReadBit();
            if (irap)
            {
                h.ReadBit();
            }

            uint hppsId = h.ReadUe();
            return !h.Overrun && irap == (firstVclType >= 16) && hppsId <= 63 && (_hevcPps.Count == 0 || _hevcPps.ContainsKey((int)hppsId));
        }

        // Trailing units that belong to the current access unit.
        if (Codec == NalCodec.H264)
        {
            return type switch
            {
                10 or 11 => nal.Length == 1,
                12 => nal.Length < 4 || nal[1] == 0xFF,
                _ => false,
            };
        }

        return type switch
        {
            36 or 37 => nal.Length == 2,
            38 => nal.Length < 4 || nal[2] == 0xFF,
            40 => nal.Length is > 3 and <= 4096,
            _ => false,
        };
    }

    // H.264 §7.3.3 slice_header, read as far as the fields have limited ranges: a stretch of audio or damaged data that happens to look like a
    // first slice fails one of them. Stops early (and accepts) at a pred_weight_table, whose layout needs more of the sequence parameter set.
    private static bool AvcSliceHeaderValid(ReadOnlySpan<byte> nal, int type, in H264.PictureParameterSet pps, in H264.SequenceParameterSet sps, out uint frameNum, out bool fieldPic)
    {
        frameNum = 0;
        fieldPic = false;
        BitReader r = new(nal[..Math.Min(nal.Length, 96)], removeEmulationPrevention: true);
        r.SkipBits(8);
        r.ReadUe();
        uint sliceType = r.ReadUe();
        r.ReadUe();
        uint st = sliceType % 5;
        bool idr = type == 5;
        int refIdc = (nal[0] >> 5) & 3;
        if (sps.SeparateColourPlane)
        {
            r.ReadBits(2);
        }

        frameNum = r.ReadBits(sps.Log2MaxFrameNum);
        if (!sps.FrameMbsOnly)
        {
            fieldPic = r.ReadFlag();
            if (fieldPic)
            {
                r.ReadBit();
            }
        }

        if (idr && (r.ReadUe() > 65535 || frameNum != 0))
        {
            return false;
        }

        if (sps.PicOrderCntType == 0)
        {
            r.ReadBits(sps.Log2MaxPicOrderCntLsb);
            if (pps.BottomFieldPicOrderInFramePresent && !fieldPic)
            {
                r.ReadSe();
            }
        }
        else if (sps.PicOrderCntType == 1)
        {
            return !r.Overrun; // delta_pic_order_cnt needs the sequence's delta_pic_order_always_zero_flag; accept
        }

        if (pps.RedundantPicCntPresent && r.ReadUe() > 127)
        {
            return false;
        }

        if (st == 1)
        {
            r.ReadBit(); // direct_spatial_mv_pred_flag
        }

        if (st is 0 or 1 or 3)
        {
            if (r.ReadFlag())
            {
                if (r.ReadUe() > 31 || (st == 1 && r.ReadUe() > 31))
                {
                    return false;
                }
            }
        }

        if (st is not (2 or 4))
        {
            // ref_pic_list_modification(): one list for P, two for B; modification_of_pic_nums_idc is 0..3 and 3 ends the list.
            for (int list = 0; list < (st == 1 ? 2 : 1); list++)
            {
                if (!r.ReadFlag())
                {
                    continue;
                }

                for (int guard = 0; ; guard++)
                {
                    uint idc = r.ReadUe();
                    if (idc == 3)
                    {
                        break;
                    }

                    if (idc > 5 || guard > 40 || r.Overrun)
                    {
                        return false;
                    }

                    r.ReadUe();
                }
            }
        }

        if ((pps.WeightedPred && st is 0 or 3) || (pps.WeightedBipredIdc == 1 && st == 1))
        {
            return !r.Overrun;
        }

        if (refIdc != 0)
        {
            // dec_ref_pic_marking()
            if (idr)
            {
                r.ReadBits(2);
            }
            else if (r.ReadFlag())
            {
                for (int guard = 0; ; guard++)
                {
                    uint op = r.ReadUe();
                    if (op == 0)
                    {
                        break;
                    }

                    if (op > 6 || guard > 66 || r.Overrun)
                    {
                        return false;
                    }

                    if (op is 1 or 3)
                    {
                        r.ReadUe();
                    }

                    if (op == 2)
                    {
                        r.ReadUe();
                    }

                    if (op == 3)
                    {
                        r.ReadUe();
                    }

                    if (op is 4 or 6)
                    {
                        r.ReadUe();
                    }
                }
            }
        }

        if (pps.EntropyCodingMode && st is not (2 or 4) && r.ReadUe() > 2)
        {
            return false; // cabac_init_idc
        }

        int qpDelta = r.ReadSe();
        if (st is 3 or 4)
        {
            return !r.Overrun;
        }

        if (pps.PicInitQp > 0)
        {
            int qp = pps.PicInitQp + qpDelta;
            int bdOffset = 6 * Math.Max(0, sps.BitDepthLuma - 8);
            if (qp < -bdOffset || qp > 51)
            {
                return false;
            }
        }

        if (pps.DeblockingFilterControlPresent)
        {
            uint idc = r.ReadUe();
            if (idc > 2)
            {
                return false;
            }

            if (idc != 1)
            {
                int alpha = r.ReadSe();
                int beta = r.ReadSe();
                if (alpha is < -6 or > 6 || beta is < -6 or > 6)
                {
                    return false;
                }
            }
        }

        return !r.Overrun;
    }

    private bool SliceHeaderPlausible(ReadOnlySpan<byte> nal, int type, out bool intra)
    {
        intra = false;
        BitReader r = new(nal[..Math.Min(nal.Length, 32)], removeEmulationPrevention: true);
        if (Codec == NalCodec.H264)
        {
            r.SkipBits(8);
            uint firstMb = r.ReadUe();
            uint sliceType = r.ReadUe();
            uint ppsId = r.ReadUe();
            if (r.Overrun || firstMb != 0 || sliceType > 9 || ppsId > 255)
            {
                return false;
            }

            // A stream that declares its picture parameter sets cannot use an undeclared one.
            if (_avcPps.Count > 0 && !_avcPps.ContainsKey((int)ppsId))
            {
                return false;
            }

            uint st = sliceType % 5;
            intra = st is 2 or 4;
            if (_avcPps.TryGetValue((int)ppsId, out H264.PictureParameterSet known) && _avcSps.TryGetValue(known.SpsId, out H264.SequenceParameterSet knownSps))
            {
                if (!AvcSliceHeaderValid(nal, type, known, knownSps, out uint frameNum, out bool fieldPic))
                {
                    return false;
                }

                if (CheckSequence && type != 5 && !fieldPic && _prevRefFrameNum >= 0 && frameNum != (_prevRefFrameNum + 1) % (1u << knownSps.Log2MaxFrameNum))
                {
                    // §7.4.3 with no gaps allowed: every picture after a reference picture has frame_num one above that picture's.
                    return false;
                }
            }

            // IDR pictures are I or SI slices (H.264 §7.4.3: slice_type of an IDR picture is 2, 4, 7 or 9).
            return type != 5 || intra;
        }

        r.SkipBits(16);
        bool first = r.ReadFlag();
        if (type is >= 16 and <= 23)
        {
            r.ReadBit(); // no_output_of_prior_pics_flag
        }

        uint hppsId = r.ReadUe();
        if (r.Overrun || !first || hppsId > 63)
        {
            return false;
        }

        if (_hevcPps.Count > 0 && !_hevcPps.ContainsKey((int)hppsId))
        {
            return false;
        }

        intra = type is >= 16 and <= 21;
        if (_hevcPps.TryGetValue((int)hppsId, out Hevc.PictureParameterSet hpps))
        {
            for (int i = 0; i < hpps.NumExtraSliceHeaderBits; i++)
            {
                r.ReadBit();
            }

            // H.265 §7.4.7.1: slice_type is 0 (B), 1 (P) or 2 (I); a random-access picture has only I slices.
            uint sliceType = r.ReadUe();
            if (r.Overrun || sliceType > 2 || (intra && sliceType != 2))
            {
                return false;
            }
        }

        return true;
    }

    // H.264 Annex D.2.8: an SEI message with payloadType 6 is a recovery point, a random-access point without an IDR picture.
    private static bool HasRecoveryPoint(ReadOnlySpan<byte> escaped)
    {
        int p = 0;
        for (int guard = 0; guard < 8 && p < escaped.Length; guard++)
        {
            int type = 0;
            while (p < escaped.Length && escaped[p] == 0xFF)
            {
                type += 255;
                p++;
            }

            if (p >= escaped.Length)
            {
                return false;
            }

            type += escaped[p++];
            int size = 0;
            while (p < escaped.Length && escaped[p] == 0xFF)
            {
                size += 255;
                p++;
            }

            if (p >= escaped.Length)
            {
                return false;
            }

            size += escaped[p++];
            if (type == 6)
            {
                return true;
            }

            p += size;
        }

        return false;
    }

    /// <summary>
    /// Reads the output-order fields of the first slice header of an accepted sample: the picture order count's least significant bits
    /// (H.264 §8.2.1, H.265 §8.3.1), whether the picture restarts the count, and whether later pictures may refer to it.
    /// </summary>
    public PicOrder ReadPicOrder(ReadOnlySpan<byte> sample)
    {
        int pos = 0;
        while (pos + LengthSize < sample.Length)
        {
            int len = ReadLength(sample[pos..]);
            pos += LengthSize;
            if (len < 1 || pos + len > sample.Length)
            {
                break;
            }

            ReadOnlySpan<byte> nal = sample.Slice(pos, len);
            int type = NalType(nal[0]);
            if (IsVcl(type))
            {
                return Codec == NalCodec.H264 ? ReadAvcOrder(nal, type) : ReadHevcOrder(nal, type);
            }

            pos += len;
        }

        return default;
    }

    private PicOrder ReadAvcOrder(ReadOnlySpan<byte> nal, int type)
    {
        BitReader r = new(nal[..Math.Min(nal.Length, 32)], removeEmulationPrevention: true);
        r.SkipBits(8);
        r.ReadUe();
        r.ReadUe();
        uint ppsId = r.ReadUe();
        if (!_avcPps.TryGetValue((int)ppsId, out H264.PictureParameterSet pps) || !_avcSps.TryGetValue(pps.SpsId, out H264.SequenceParameterSet sps))
        {
            return default;
        }

        if (sps.SeparateColourPlane)
        {
            r.ReadBits(2);
        }

        int frameNum = (int)r.ReadBits(sps.Log2MaxFrameNum);
        if (((nal[0] >> 5) & 3) != 0)
        {
            _prevRefFrameNum = type == 5 ? 0 : frameNum;
        }

        bool field = false;
        if (!sps.FrameMbsOnly)
        {
            field = r.ReadFlag();
            if (field)
            {
                r.ReadBit();
            }
        }

        bool idr = type == 5;
        if (idr)
        {
            r.ReadUe();
        }

        bool isRef = ((nal[0] >> 5) & 3) != 0;
        if (sps.PicOrderCntType == 0)
        {
            int lsb = (int)r.ReadBits(sps.Log2MaxPicOrderCntLsb);
            return r.Overrun ? default : new PicOrder(true, idr, lsb, sps.Log2MaxPicOrderCntLsb, isRef, 0, field, 0);
        }

        return new PicOrder(true, idr, 0, 0, isRef, 0, field, sps.PicOrderCntType);
    }

    private PicOrder ReadHevcOrder(ReadOnlySpan<byte> nal, int type)
    {
        BitReader r = new(nal[..Math.Min(nal.Length, 32)], removeEmulationPrevention: true);
        r.SkipBits(16);
        r.ReadBit(); // first_slice_segment_in_pic_flag
        if (type is >= 16 and <= 23)
        {
            r.ReadBit();
        }

        uint ppsId = r.ReadUe();
        if (!_hevcPps.TryGetValue((int)ppsId, out Hevc.PictureParameterSet pps) || !_hevcSps.TryGetValue(pps.SpsId, out Hevc.SequenceParameterSet sps))
        {
            return default;
        }

        for (int i = 0; i < pps.NumExtraSliceHeaderBits; i++)
        {
            r.ReadBit();
        }

        r.ReadUe(); // slice_type
        if (pps.OutputFlagPresent)
        {
            r.ReadBit();
        }

        if (sps.SeparateColourPlane)
        {
            r.ReadBits(2);
        }

        int tid = (nal[1] & 7) - 1;
        bool idr = type is 19 or 20;
        // H.265 §8.3.1: IDR and BLA pictures, and a CRA that starts the stream, reset the picture order count to its lsb of zero; only those
        // we can tell from the type. A CRA in mid-stream continues the count.
        bool resets = type is >= 16 and <= 20;
        // prevTid0Pic must be a reference picture of temporal id 0 that is not RASL, RADL or a sub-layer non-reference picture.
        bool reference = tid == 0 && type is not (6 or 7 or 8 or 9) && (type > 14 || (type & 1) == 1);
        if (idr)
        {
            return new PicOrder(true, true, 0, sps.Log2MaxPicOrderCntLsb, reference, tid, false, 0);
        }

        int lsb = (int)r.ReadBits(sps.Log2MaxPicOrderCntLsb);
        return r.Overrun ? default : new PicOrder(true, resets, lsb, sps.Log2MaxPicOrderCntLsb, reference, tid, false, 0);
    }
}

/// <summary>
/// AV1 samples in the low-overhead format MP4 stores (AV1 ISOBMFF binding §2.4, AV1 specification §5.2): a run of OBUs that all carry
/// <c>obu_size</c>. A sample is one temporal unit, which ends with its shown frame (a <c>show_frame</c> frame, or a
/// <c>show_existing_frame</c> header).
/// </summary>
internal sealed class Av1ObuFramer : TrackFramer
{
    public override TrackKind Kind => TrackKind.Video;

    /// <summary>The sequence header OBU found in the stream (with its header and size field), or empty.</summary>
    public ReadOnlyMemory<byte> SequenceHeaderObu { get; private set; }

    public override FrameStatus TryFrame(ReadOnlySpan<byte> data, bool atEnd, out FrameInfo frame)
    {
        frame = default;
        int pos = 0;
        bool key = false;
        bool shown = false;
        bool first = true;
        bool inFrame = false;
        while (true)
        {
            if (pos >= data.Length)
            {
                if (!atEnd)
                {
                    return FrameStatus.NeedMore;
                }

                if (!shown)
                {
                    return pos == 0 ? FrameStatus.No : FrameStatus.Partial;
                }

                break;
            }

            byte b = data[pos];
            // obu_forbidden_bit 0, obu_has_size_field 1, obu_reserved_1bit 0 (AV1 specification §5.3.1).
            if ((b & 0x81) != 0 || (b & 0x02) == 0)
            {
                if (!shown)
                {
                    return FrameStatus.No;
                }

                break;
            }

            int type = (b >> 3) & 15;
            if (type is 0 or (>= 9 and <= 14))
            {
                if (!shown)
                {
                    return FrameStatus.No;
                }

                break;
            }

            if (shown && type is not (4 or 5 or 15))
            {
                // The next temporal unit begins.
                break;
            }

            if (first && type is not (1 or 2 or 3 or 6))
            {
                return FrameStatus.No;
            }

            // obu_header (one byte, two with the extension) then obu_size as leb128 (AV1 specification §5.3.1, §4.10.5).
            int payloadOffset = (b & 0x04) != 0 ? 2 : 1;
            long obuSize = 0;
            int shift = 0;
            bool sizeDone = false;
            for (int i = payloadOffset; i < data.Length - pos && shift < 56; i++)
            {
                byte lb = data[pos + i];
                obuSize |= (long)(lb & 0x7f) << shift;
                shift += 7;
                if ((lb & 0x80) == 0)
                {
                    payloadOffset = i + 1;
                    sizeDone = true;
                    break;
                }
            }

            if (!sizeDone)
            {
                return shift >= 56 ? FrameStatus.No : atEnd ? FrameStatus.Partial : FrameStatus.NeedMore;
            }

            if (obuSize + payloadOffset > MaxSample)
            {
                return FrameStatus.No;
            }

            ObuHeader h = new(type, (b & 0x04) != 0, 0, 0, (b & 0x04) != 0 ? 2 : 1, payloadOffset, (int)obuSize);
            if (pos + h.TotalSize > data.Length)
            {
                return atEnd ? FrameStatus.Partial : FrameStatus.NeedMore;
            }

            first = false;
            ReadOnlySpan<byte> payload = data.Slice(pos + h.PayloadOffset, h.PayloadSize);
            switch (type)
            {
                case ObuType.Frame when payload.Length > 0:
                    // frame_header_obu: show_existing_frame f(1) is 0 in a frame OBU, then frame_type f(2) and show_frame f(1).
                    if ((payload[0] & 0x80) != 0)
                    {
                        return FrameStatus.No;
                    }

                    if ((payload[0] & 0x10) != 0)
                    {
                        shown = true;
                        key |= ((payload[0] >> 5) & 3) == 0;
                    }

                    break;
                case ObuType.FrameHeader when payload.Length > 0:
                    if ((payload[0] & 0x80) != 0)
                    {
                        shown = true; // show_existing_frame: the unit's picture is one already decoded
                    }
                    else
                    {
                        inFrame = true;
                        if ((payload[0] & 0x10) != 0)
                        {
                            key |= ((payload[0] >> 5) & 3) == 0;
                            shown = true;
                        }
                    }

                    break;
            }

            pos += h.TotalSize;
            _ = inFrame;
        }

        if (!shown)
        {
            return FrameStatus.No;
        }

        frame = new FrameInfo { Length = pos, Key = key };
        return FrameStatus.Ok;
    }

    public override void Accept(ReadOnlySpan<byte> sample)
    {
        if (SequenceHeaderObu.IsEmpty && SequenceHeader.TryFind(sample, out _, out ReadOnlySpan<byte> obu))
        {
            SequenceHeaderObu = obu.ToArray();
        }
    }
}
