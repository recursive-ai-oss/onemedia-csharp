using System.Buffers;
using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Codecs.H264;

/// <summary>The essentials of an H.264 picture parameter set (ITU-T H.264 §7.3.2.2).</summary>
public readonly record struct PictureParameterSet
{
    /// <summary>pic_parameter_set_id.</summary>
    public int PpsId { get; init; }

    /// <summary>seq_parameter_set_id the set refers to.</summary>
    public int SpsId { get; init; }

    /// <summary>entropy_coding_mode_flag: true for CABAC, false for CAVLC.</summary>
    public bool EntropyCodingMode { get; init; }

    /// <summary>num_slice_groups_minus1 + 1. Fields after it are left at defaults when this is above 1.</summary>
    public int SliceGroupCount { get; init; }

    /// <summary>weighted_pred_flag.</summary>
    public bool WeightedPred { get; init; }

    /// <summary>weighted_bipred_idc.</summary>
    public int WeightedBipredIdc { get; init; }

    /// <summary>Parses a picture parameter set.</summary>
    /// <param name="nal">The NAL unit including its header byte.</param>
    /// <param name="pps">The result when true.</param>
    /// <returns>False when the unit is not a PPS or is malformed. Never throws.</returns>
    public static bool TryParse(ReadOnlySpan<byte> nal, out PictureParameterSet pps)
    {
        pps = default;
        if (nal.Length < 2 || (nal[0] & 0x1f) != H264NalType.Pps)
        {
            return false;
        }

        BitReader r = new(nal[1..], removeEmulationPrevention: true);
        uint id = r.ReadUe();
        uint spsId = r.ReadUe();
        bool cabac = r.ReadFlag();
        r.ReadBit(); // bottom_field_pic_order_in_frame_present_flag
        uint groupsMinus1 = r.ReadUe();
        if (r.Overrun || id > 255 || spsId > 31 || groupsMinus1 > 7)
        {
            return false;
        }

        bool weighted = false;
        int bipred = 0;
        if (groupsMinus1 == 0)
        {
            r.ReadUe(); // num_ref_idx_l0_default_active_minus1
            r.ReadUe(); // num_ref_idx_l1_default_active_minus1
            weighted = r.ReadFlag();
            bipred = (int)r.ReadBits(2);
            if (r.Overrun)
            {
                weighted = false;
                bipred = 0;
            }
        }

        pps = new PictureParameterSet
        {
            PpsId = (int)id,
            SpsId = (int)spsId,
            EntropyCodingMode = cabac,
            SliceGroupCount = (int)groupsMinus1 + 1,
            WeightedPred = weighted,
            WeightedBipredIdc = bipred,
        };
        return true;
    }
}

/// <summary>
/// An AVCDecoderConfigurationRecord, the content of an <c>avcC</c> box (ISO/IEC 14496-15 §5.3.3.1, as written by common tools):
/// profile, level, NAL length size and the stored parameter sets.
/// </summary>
public sealed class AvcDecoderConfigurationRecord
{
    /// <summary>configurationVersion, 1.</summary>
    public int ConfigurationVersion { get; init; } = 1;

    /// <summary>AVCProfileIndication: the SPS profile_idc.</summary>
    public int ProfileIndication { get; init; }

    /// <summary>profile_compatibility: the SPS constraint flag byte.</summary>
    public int ProfileCompatibility { get; init; }

    /// <summary>AVCLevelIndication: the SPS level_idc.</summary>
    public int LevelIndication { get; init; }

    /// <summary>lengthSizeMinusOne + 1: 1, 2 or 4 (3 is reserved).</summary>
    public int LengthSize { get; init; } = 4;

    /// <summary>The sequence parameter sets (NAL units with headers, no start codes).</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> SequenceParameterSets { get; init; } = [];

    /// <summary>The picture parameter sets.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> PictureParameterSets { get; init; } = [];

    /// <summary>The SPS extension units, present only in the high-profile tail.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> SequenceParameterSetExtensions { get; init; } = [];

    /// <summary>The chroma_format of the high-profile tail, or -1 when the tail is absent.</summary>
    public int ChromaFormat { get; init; } = -1;

    /// <summary>The luma bit depth of the high-profile tail, or -1 when absent.</summary>
    public int BitDepthLuma { get; init; } = -1;

    /// <summary>The chroma bit depth of the high-profile tail, or -1 when absent.</summary>
    public int BitDepthChroma { get; init; } = -1;

    /// <summary>True for profiles whose record carries the chroma and bit depth tail: the profiles whose SPS has chroma_format_idc
    /// (100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135). Files written by common tools carry the tail for all of them;
    /// the older 144 is kept for records that still use it.</summary>
    /// <param name="profileIdc">AVCProfileIndication.</param>
    /// <returns>True when the tail is expected.</returns>
    public static bool HasHighProfileTail(int profileIdc) => profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135 or 144;

    /// <summary>Parses the record.</summary>
    /// <param name="data">The box content (after the box header). The record keeps slices of this memory, not copies.</param>
    /// <param name="record">The result when true.</param>
    /// <param name="limits">Bounds the number of stored units; the default limits when null.</param>
    /// <returns>False when the record is truncated or its version is not 1.</returns>
    public static bool TryParse(ReadOnlyMemory<byte> data, out AvcDecoderConfigurationRecord? record, MediaLimits? limits = null)
    {
        record = null;
        ReadOnlySpan<byte> s = data.Span;
        if (s.Length < 7 || s[0] != 1)
        {
            return false;
        }

        int lengthSize = (s[4] & 3) + 1;
        int spsCount = s[5] & 0x1f;
        int pos = 6;
        if (!ReadUnits(data, ref pos, spsCount, out List<ReadOnlyMemory<byte>>? sps) || pos >= s.Length)
        {
            return false;
        }

        int ppsCount = s[pos++];
        if (!ReadUnits(data, ref pos, ppsCount, out List<ReadOnlyMemory<byte>>? pps))
        {
            return false;
        }

        List<ReadOnlyMemory<byte>>? ext = null;
        int chroma = -1, bdl = -1, bdc = -1;
        if (HasHighProfileTail(s[1]) && pos + 4 <= s.Length)
        {
            chroma = s[pos] & 3;
            bdl = (s[pos + 1] & 7) + 8;
            bdc = (s[pos + 2] & 7) + 8;
            int extCount = s[pos + 3];
            pos += 4;
            if (!ReadUnits(data, ref pos, extCount, out ext))
            {
                return false;
            }
        }

        record = new AvcDecoderConfigurationRecord
        {
            ConfigurationVersion = 1,
            ProfileIndication = s[1],
            ProfileCompatibility = s[2],
            LevelIndication = s[3],
            LengthSize = lengthSize,
            SequenceParameterSets = sps!,
            PictureParameterSets = pps!,
            SequenceParameterSetExtensions = ext ?? [],
            ChromaFormat = chroma,
            BitDepthLuma = bdl,
            BitDepthChroma = bdc,
        };
        return true;
    }

    private static bool ReadUnits(ReadOnlyMemory<byte> data, ref int pos, int count, out List<ReadOnlyMemory<byte>>? units)
    {
        units = new List<ReadOnlyMemory<byte>>(count);
        ReadOnlySpan<byte> s = data.Span;
        for (int i = 0; i < count; i++)
        {
            if (pos + 2 > s.Length)
            {
                return false;
            }

            int len = BinaryPrimitives.ReadUInt16BigEndian(s[pos..]);
            pos += 2;
            if (len > s.Length - pos)
            {
                return false;
            }

            units.Add(data.Slice(pos, len));
            pos += len;
        }

        return true;
    }

    /// <summary>The size of the record as <see cref="Write"/> produces it.</summary>
    /// <returns>Bytes.</returns>
    public int GetSize()
    {
        int n = 7;
        foreach (ReadOnlyMemory<byte> u in SequenceParameterSets)
        {
            n += 2 + u.Length;
        }

        foreach (ReadOnlyMemory<byte> u in PictureParameterSets)
        {
            n += 2 + u.Length;
        }

        if (HasHighProfileTail(ProfileIndication) && ChromaFormat >= 0)
        {
            n += 4;
            foreach (ReadOnlyMemory<byte> u in SequenceParameterSetExtensions)
            {
                n += 2 + u.Length;
            }
        }

        return n;
    }

    /// <summary>Writes the record.</summary>
    /// <param name="writer">Receives <see cref="GetSize"/> bytes.</param>
    public void Write(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> w = writer.GetSpan(GetSize());
        int p = 0;
        w[p++] = (byte)ConfigurationVersion;
        w[p++] = (byte)ProfileIndication;
        w[p++] = (byte)ProfileCompatibility;
        w[p++] = (byte)LevelIndication;
        w[p++] = (byte)(0xfc | ((LengthSize - 1) & 3));
        w[p++] = (byte)(0xe0 | (SequenceParameterSets.Count & 0x1f));
        foreach (ReadOnlyMemory<byte> u in SequenceParameterSets)
        {
            p = PutUnit(w, p, u.Span);
        }

        w[p++] = (byte)PictureParameterSets.Count;
        foreach (ReadOnlyMemory<byte> u in PictureParameterSets)
        {
            p = PutUnit(w, p, u.Span);
        }

        if (HasHighProfileTail(ProfileIndication) && ChromaFormat >= 0)
        {
            w[p++] = (byte)(0xfc | (ChromaFormat & 3));
            w[p++] = (byte)(0xf8 | ((BitDepthLuma - 8) & 7));
            w[p++] = (byte)(0xf8 | ((BitDepthChroma - 8) & 7));
            w[p++] = (byte)SequenceParameterSetExtensions.Count;
            foreach (ReadOnlyMemory<byte> u in SequenceParameterSetExtensions)
            {
                p = PutUnit(w, p, u.Span);
            }
        }

        writer.Advance(p);
    }

    private static int PutUnit(Span<byte> w, int p, ReadOnlySpan<byte> unit)
    {
        BinaryPrimitives.WriteUInt16BigEndian(w[p..], (ushort)unit.Length);
        unit.CopyTo(w[(p + 2)..]);
        return p + 2 + unit.Length;
    }

    /// <summary>Builds the record from an Annex B stream (or its first access unit), collecting the parameter sets it contains.</summary>
    /// <param name="annexB">Annex B bytes holding at least one SPS and one PPS.</param>
    /// <param name="record">The result when true.</param>
    /// <param name="lengthSize">The NAL length size the samples will use.</param>
    /// <returns>False when no valid SPS is found. Duplicate units are kept once, in order of appearance; the profile fields come from the first SPS.</returns>
    public static bool TryCreateFromAnnexB(ReadOnlyMemory<byte> annexB, out AvcDecoderConfigurationRecord? record, int lengthSize = 4)
    {
        record = null;
        List<ReadOnlyMemory<byte>> sps = [];
        List<ReadOnlyMemory<byte>> pps = [];
        List<ReadOnlyMemory<byte>> ext = [];
        AnnexBReader reader = new(annexB.Span);
        while (reader.TryRead(out ReadOnlySpan<byte> nal, out int offset, out _))
        {
            if (nal.Length == 0)
            {
                continue;
            }

            int type = nal[0] & 0x1f;
            List<ReadOnlyMemory<byte>>? target = type switch { H264NalType.Sps => sps, H264NalType.Pps => pps, H264NalType.SpsExtension => ext, _ => null };
            if (target is null)
            {
                if (type is >= 1 and <= 5 && sps.Count > 0)
                {
                    break;
                }

                continue;
            }

            ReadOnlyMemory<byte> unit = annexB.Slice(offset, nal.Length);
            bool dup = false;
            foreach (ReadOnlyMemory<byte> have in target)
            {
                dup |= have.Span.SequenceEqual(unit.Span);
            }

            if (!dup)
            {
                target.Add(unit);
            }
        }

        if (sps.Count == 0 || !SequenceParameterSet.TryParse(sps[0].Span, out SequenceParameterSet first))
        {
            return false;
        }

        bool tail = HasHighProfileTail(first.ProfileIdc);
        record = new AvcDecoderConfigurationRecord
        {
            ProfileIndication = first.ProfileIdc,
            ProfileCompatibility = first.ConstraintFlags,
            LevelIndication = first.LevelIdc,
            LengthSize = lengthSize,
            SequenceParameterSets = sps,
            PictureParameterSets = pps,
            SequenceParameterSetExtensions = ext,
            ChromaFormat = tail ? first.ChromaFormatIdc : -1,
            BitDepthLuma = tail ? first.BitDepthLuma : -1,
            BitDepthChroma = tail ? first.BitDepthChroma : -1,
        };
        return true;
    }

    /// <summary>Describes the stream from the first stored SPS.</summary>
    /// <param name="parameters">The summary when true.</param>
    /// <returns>False when there is no parsable SPS.</returns>
    public bool TryDescribe(out CodecParameters? parameters)
    {
        parameters = null;
        if (SequenceParameterSets.Count == 0 || !SequenceParameterSet.TryParse(SequenceParameterSets[0].Span, out SequenceParameterSet sps))
        {
            return false;
        }

        // The VUI counts half-frame ticks, so 30/2 is 15/1: the rate is given in lowest terms.
        (int rateN, int rateD) = Mpeg.FrameRates.Reduce((int)Math.Min(sps.FrameRateNumerator, int.MaxValue), (int)Math.Min(sps.FrameRateDenominator, int.MaxValue));
        parameters = new CodecParameters
        {
            Codec = CodecId.H264,
            Rfc6381 = sps.Rfc6381,
            Profile = sps.ProfileIdc,
            Level = sps.LevelIdc,
            ProfileName = sps.ProfileName,
            Width = sps.Width,
            Height = sps.Height,
            BitDepth = sps.BitDepthLuma,
            Chroma = sps.ChromaFormatIdc switch { 0 => Codecs.ChromaFormat.Monochrome, 1 => Codecs.ChromaFormat.Yuv420, 2 => Codecs.ChromaFormat.Yuv422, _ => Codecs.ChromaFormat.Yuv444 },
            Color = sps.Color,
            IsInterlaced = !sps.FrameMbsOnly,
            SarWidth = sps.SarWidth,
            SarHeight = sps.SarHeight,
            FrameRateNumerator = rateN,
            FrameRateDenominator = rateD,
            ReorderFrames = sps.MaxNumReorderFrames,
            NalLengthSize = LengthSize,
        };
        return true;
    }
}
