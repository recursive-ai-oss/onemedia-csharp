using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;

namespace RecursiveAI.OneMedia.Codecs.Vvc;

/// <summary>NAL unit types of ITU-T H.266 Table 5 that this layer handles.</summary>
public static class VvcNalType
{
    /// <summary>Operating point information.</summary>
    public const int Opi = 12;

    /// <summary>Decoding capability information.</summary>
    public const int Dci = 13;

    /// <summary>Video parameter set.</summary>
    public const int Vps = 14;

    /// <summary>Sequence parameter set.</summary>
    public const int Sps = 15;

    /// <summary>Picture parameter set.</summary>
    public const int Pps = 16;

    /// <summary>Prefix SEI.</summary>
    public const int PrefixSei = 23;

    /// <summary>Suffix SEI.</summary>
    public const int SuffixSei = 24;
}

/// <summary>
/// The fields of an H.266 sequence parameter set that tools need to describe a stream (ITU-T H.266 §7.3.2.3). Parsed from the
/// specification text; no VVC encoder was available to produce reference streams for this layer.
/// </summary>
public readonly record struct SequenceParameterSet
{
    /// <summary>sps_seq_parameter_set_id.</summary>
    public int SpsId { get; init; }

    /// <summary>sps_max_sub_layers_minus1 + 1.</summary>
    public int MaxSubLayers { get; init; }

    /// <summary>sps_chroma_format_idc.</summary>
    public int ChromaFormatIdc { get; init; }

    /// <summary>True when profile_tier_level is present in the SPS.</summary>
    public bool HasProfileTierLevel { get; init; }

    /// <summary>general_profile_idc.</summary>
    public int ProfileIdc { get; init; }

    /// <summary>general_tier_flag: true for the High tier.</summary>
    public bool Tier { get; init; }

    /// <summary>general_level_idc.</summary>
    public int LevelIdc { get; init; }

    /// <summary>Maximum picture width before the conformance window.</summary>
    public int CodedWidth { get; init; }

    /// <summary>Maximum picture height before the conformance window.</summary>
    public int CodedHeight { get; init; }

    /// <summary>Width after the conformance window.</summary>
    public int Width { get; init; }

    /// <summary>Height after the conformance window.</summary>
    public int Height { get; init; }

    /// <summary>Bit depth (sps_bitdepth_minus8 + 8), or 0 when the SPS has subpicture information that this layer does not walk.</summary>
    public int BitDepth { get; init; }

    /// <summary>The <c>vvc1.</c> style string: profile, then the tier letter and level.</summary>
    /// <param name="prefix">The sample entry code, <c>vvc1</c> or <c>vvi1</c>.</param>
    /// <returns>For example <c>vvc1.1.L51</c>.</returns>
    public string ToRfc6381(string prefix = "vvc1") =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}.{ProfileIdc}.{(Tier ? 'H' : 'L')}{LevelIdc}");

    /// <summary>Parses a sequence parameter set.</summary>
    /// <param name="nal">The NAL unit including its two-byte header.</param>
    /// <param name="sps">The result when true.</param>
    /// <returns>False when the unit is not an SPS or is truncated. Never throws, never allocates.</returns>
    public static bool TryParse(ReadOnlySpan<byte> nal, out SequenceParameterSet sps)
    {
        sps = default;
        if (nal.Length < 6 || NalUnit.GetType(NalCodec.Vvc, nal) != VvcNalType.Sps)
        {
            return false;
        }

        BitReader r = new(nal[2..], removeEmulationPrevention: true);
        int id = (int)r.ReadBits(4);
        r.SkipBits(4); // sps_video_parameter_set_id
        int maxSub = (int)r.ReadBits(3);
        int chroma = (int)r.ReadBits(2);
        r.SkipBits(2); // sps_log2_ctu_size_minus5
        bool ptlPresent = r.ReadFlag();
        int profile = 0, level = 0;
        bool tier = false;
        if (ptlPresent)
        {
            if (maxSub > 6)
            {
                return false;
            }

            // ITU-T H.266 §7.3.3.1 with profileTierPresentFlag = 1
            profile = (int)r.ReadBits(7);
            tier = r.ReadFlag();
            level = (int)r.ReadBits(8);
            r.SkipBits(2); // ptl_frame_only_constraint_flag, ptl_multilayer_enabled_flag
            if (r.ReadFlag())
            {
                // general_constraints_info(): gci_present_flag then 71 constraint bits, an 8-bit reserved count and the reserved bits.
                r.SkipBits(71);
                r.SkipBits(r.ReadBits(8));
            }

            r.ByteAlign();
            uint sublayerPresent = 0;
            for (int i = maxSub - 1; i >= 0; i--)
            {
                sublayerPresent |= (uint)r.ReadBit() << i;
            }

            r.ByteAlign();
            for (int i = maxSub - 1; i >= 0; i--)
            {
                if (((sublayerPresent >> i) & 1) != 0)
                {
                    r.SkipBits(8);
                }
            }

            uint subProfiles = r.ReadBits(8);
            r.SkipBits(32L * subProfiles);
        }

        r.ReadBit(); // sps_gdr_enabled_flag
        if (r.ReadFlag())
        {
            r.ReadBit(); // sps_res_change_in_clvs_allowed_flag
        }

        uint w = r.ReadUe();
        uint h = r.ReadUe();
        uint cl = 0, cr = 0, ct = 0, cb = 0;
        if (r.ReadFlag())
        {
            cl = r.ReadUe();
            cr = r.ReadUe();
            ct = r.ReadUe();
            cb = r.ReadUe();
        }

        bool subpics = r.ReadFlag();
        int bitDepth = 0;
        if (!subpics)
        {
            uint bd = r.ReadUe();
            if (bd <= 8)
            {
                bitDepth = (int)bd + 8;
            }
        }

        if (r.Overrun || w is 0 or > 65536 || h is 0 or > 65536)
        {
            return false;
        }

        int subW = chroma is 1 or 2 ? 2 : 1;
        int subH = chroma == 1 ? 2 : 1;
        long width = w - ((long)subW * (cl + (long)cr));
        long height = h - ((long)subH * (ct + (long)cb));
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        sps = new SequenceParameterSet
        {
            SpsId = id,
            MaxSubLayers = maxSub + 1,
            ChromaFormatIdc = chroma,
            HasProfileTierLevel = ptlPresent,
            ProfileIdc = profile,
            Tier = tier,
            LevelIdc = level,
            CodedWidth = (int)w,
            CodedHeight = (int)h,
            Width = (int)width,
            Height = (int)height,
            BitDepth = bitDepth,
        };
        return true;
    }
}

/// <summary>One array of NAL units of a single type in a <see cref="VvcDecoderConfigurationRecord"/>.</summary>
/// <param name="Completeness">array_completeness.</param>
/// <param name="NalUnitType">NAL_unit_type.</param>
/// <param name="Units">The units without start codes.</param>
public sealed record VvcNalArray(bool Completeness, int NalUnitType, IReadOnlyList<ReadOnlyMemory<byte>> Units);

/// <summary>
/// A VvcDecoderConfigurationRecord, the content of a <c>vvcC</c> box (ISO/IEC 14496-15 §11.2.4.2). The layout follows public
/// descriptions of the record and could not be checked against a reference writer; treat it as provisional. The profile, tier and
/// level record (<c>VvcPTLRecord</c>) is kept as the bytes stored.
/// </summary>
public sealed class VvcDecoderConfigurationRecord
{
    /// <summary>lengthSizeMinusOne + 1.</summary>
    public int LengthSize { get; init; } = 4;

    /// <summary>True when ptl_present_flag is set; the fields up to <see cref="PtlRecord"/> are then meaningful.</summary>
    public bool PtlPresent { get; init; }

    /// <summary>ols_idx (9 bits).</summary>
    public int OlsIdx { get; init; }

    /// <summary>num_sublayers (3 bits).</summary>
    public int NumSublayers { get; init; }

    /// <summary>constant_frame_rate (2 bits).</summary>
    public int ConstantFrameRate { get; init; }

    /// <summary>chroma_format_idc.</summary>
    public int ChromaFormatIdc { get; init; }

    /// <summary>bit_depth_minus8 + 8.</summary>
    public int BitDepth { get; init; } = 8;

    /// <summary>The VvcPTLRecord bytes as stored, empty when absent.</summary>
    public ReadOnlyMemory<byte> PtlRecord { get; init; }

    /// <summary>general_profile_idc from the PTL record, or 0.</summary>
    public int ProfileIdc { get; init; }

    /// <summary>general_tier_flag from the PTL record.</summary>
    public bool Tier { get; init; }

    /// <summary>general_level_idc from the PTL record, or 0.</summary>
    public int LevelIdc { get; init; }

    /// <summary>max_picture_width.</summary>
    public int MaxPictureWidth { get; init; }

    /// <summary>max_picture_height.</summary>
    public int MaxPictureHeight { get; init; }

    /// <summary>avg_frame_rate in units of frames per 256 seconds.</summary>
    public int AvgFrameRate { get; init; }

    /// <summary>The NAL unit arrays.</summary>
    public IReadOnlyList<VvcNalArray> Arrays { get; init; } = [];

    /// <summary>Parses the record.</summary>
    /// <param name="data">The box content after its header.</param>
    /// <param name="record">The result when true.</param>
    /// <returns>False when truncated.</returns>
    public static bool TryParse(ReadOnlyMemory<byte> data, out VvcDecoderConfigurationRecord? record)
    {
        record = null;
        ReadOnlySpan<byte> s = data.Span;
        if (s.Length < 1)
        {
            return false;
        }

        int lengthSize = ((s[0] >> 1) & 3) + 1;
        bool ptlPresent = (s[0] & 1) != 0;
        int pos = 1;
        int ols = 0, sub = 0, cfr = 0, chroma = 0, bitDepth = 8;
        ReadOnlyMemory<byte> ptl = default;
        int profile = 0, level = 0;
        bool tier = false;
        int maxW = 0, maxH = 0, avg = 0;
        if (ptlPresent)
        {
            if (s.Length < 4)
            {
                return false;
            }

            // ols_idx(9) num_sublayers(3) constant_frame_rate(2) chroma_format_idc(2), then bit_depth_minus8(3) and 5 reserved bits.
            ols = (s[1] << 1) | (s[2] >> 7);
            sub = (s[2] >> 4) & 7;
            cfr = (s[2] >> 2) & 3;
            chroma = s[2] & 3;
            pos = 4;
            if (pos >= s.Length)
            {
                return false;
            }

            bitDepth = (s[3] >> 5) + 8;
            if (pos >= s.Length)
            {
                return false;
            }

            // VvcPTLRecord(num_sublayers)
            int ptlStart = pos;
            int numBytesConstraint = s[pos] & 0x3f;
            pos += 1;
            if (pos + 2 + numBytesConstraint > s.Length)
            {
                return false;
            }

            profile = s[pos] >> 1;
            tier = (s[pos] & 1) != 0;
            level = s[pos + 1];
            pos += 2 + Math.Max(numBytesConstraint, 1);
            if (sub > 1)
            {
                // one flag per sub-layer below the highest, padded to a byte boundary, then one level byte per flagged layer
                if (pos >= s.Length)
                {
                    return false;
                }

                int flags = s[pos++];
                int levels = 0;
                for (int i = 0; i < sub - 1; i++)
                {
                    levels += (flags >> (7 - i)) & 1;
                }

                pos += levels;
            }

            if (pos >= s.Length)
            {
                return false;
            }

            int numSubProfiles = s[pos++];
            pos += 4 * numSubProfiles;
            if (pos + 6 + 1 > s.Length)
            {
                return false;
            }

            ptl = data[ptlStart..pos];
            maxW = BinaryPrimitives.ReadUInt16BigEndian(s[pos..]);
            maxH = BinaryPrimitives.ReadUInt16BigEndian(s[(pos + 2)..]);
            avg = BinaryPrimitives.ReadUInt16BigEndian(s[(pos + 4)..]);
            pos += 6;
        }

        if (pos >= s.Length)
        {
            return false;
        }

        int arrayCount = s[pos++];
        List<VvcNalArray> arrays = new(arrayCount);
        for (int a = 0; a < arrayCount; a++)
        {
            if (pos >= s.Length)
            {
                return false;
            }

            bool complete = (s[pos] & 0x80) != 0;
            int type = s[pos] & 0x1f;
            pos++;
            int count = 1;
            if (type is not (VvcNalType.Dci or VvcNalType.Opi))
            {
                if (pos + 2 > s.Length)
                {
                    return false;
                }

                count = BinaryPrimitives.ReadUInt16BigEndian(s[pos..]);
                pos += 2;
            }

            List<ReadOnlyMemory<byte>> units = new(Math.Min(count, 64));
            for (int u = 0; u < count; u++)
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

            arrays.Add(new VvcNalArray(complete, type, units));
        }

        record = new VvcDecoderConfigurationRecord
        {
            LengthSize = lengthSize,
            PtlPresent = ptlPresent,
            OlsIdx = ols,
            NumSublayers = sub,
            ConstantFrameRate = cfr,
            ChromaFormatIdc = chroma,
            BitDepth = bitDepth,
            PtlRecord = ptl,
            ProfileIdc = profile,
            Tier = tier,
            LevelIdc = level,
            MaxPictureWidth = maxW,
            MaxPictureHeight = maxH,
            AvgFrameRate = avg,
            Arrays = arrays,
        };
        return true;
    }

    /// <summary>The size of the record as <see cref="Write"/> produces it.</summary>
    /// <returns>Bytes.</returns>
    public int GetSize()
    {
        int n = 1;
        if (PtlPresent)
        {
            n += 3 + 1 + PtlRecord.Length + 6;
        }

        n += 1;
        foreach (VvcNalArray a in Arrays)
        {
            n += 1 + (a.NalUnitType is VvcNalType.Dci or VvcNalType.Opi ? 0 : 2);
            foreach (ReadOnlyMemory<byte> u in a.Units)
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
        // 5 reserved one bits, lengthSizeMinusOne, ptl_present_flag
        w[p++] = (byte)(0xf8 | (((LengthSize - 1) & 3) << 1) | (PtlPresent ? 1 : 0));
        if (PtlPresent)
        {
            // ols_idx(9) num_sublayers(3) constant_frame_rate(2) chroma_format_idc(2) = 16 bits
            int bits = ((OlsIdx & 0x1ff) << 7) | ((NumSublayers & 7) << 4) | ((ConstantFrameRate & 3) << 2) | (ChromaFormatIdc & 3);
            BinaryPrimitives.WriteUInt16BigEndian(w[p..], (ushort)bits);
            p += 2;
            w[p++] = (byte)(((BitDepth - 8) << 5) | 0x1f);
            PtlRecord.Span.CopyTo(w[p..]);
            p += PtlRecord.Length;
            BinaryPrimitives.WriteUInt16BigEndian(w[p..], (ushort)MaxPictureWidth);
            BinaryPrimitives.WriteUInt16BigEndian(w[(p + 2)..], (ushort)MaxPictureHeight);
            BinaryPrimitives.WriteUInt16BigEndian(w[(p + 4)..], (ushort)AvgFrameRate);
            p += 6;
        }

        w[p++] = (byte)Arrays.Count;
        foreach (VvcNalArray a in Arrays)
        {
            w[p++] = (byte)((a.Completeness ? 0x80 : 0) | 0x60 | (a.NalUnitType & 0x1f));
            if (a.NalUnitType is not (VvcNalType.Dci or VvcNalType.Opi))
            {
                BinaryPrimitives.WriteUInt16BigEndian(w[p..], (ushort)a.Units.Count);
                p += 2;
            }

            foreach (ReadOnlyMemory<byte> u in a.Units)
            {
                BinaryPrimitives.WriteUInt16BigEndian(w[p..], (ushort)u.Length);
                u.Span.CopyTo(w[(p + 2)..]);
                p += 2 + u.Length;
            }
        }

        writer.Advance(p);
    }

    /// <summary>Describes the stream from the record's fields and the first stored SPS.</summary>
    /// <param name="parameters">The summary when true.</param>
    /// <returns>False when the record holds neither a PTL nor a parsable SPS.</returns>
    public bool TryDescribe(out CodecParameters? parameters)
    {
        parameters = null;
        int width = MaxPictureWidth, height = MaxPictureHeight, profile = ProfileIdc, level = LevelIdc, bitDepth = PtlPresent ? BitDepth : 0;
        bool tier = Tier;
        int chroma = ChromaFormatIdc;
        foreach (VvcNalArray a in Arrays)
        {
            if (a.NalUnitType == VvcNalType.Sps && a.Units.Count > 0 && SequenceParameterSet.TryParse(a.Units[0].Span, out SequenceParameterSet sps))
            {
                width = sps.Width;
                height = sps.Height;
                if (!PtlPresent && sps.HasProfileTierLevel)
                {
                    profile = sps.ProfileIdc;
                    level = sps.LevelIdc;
                    tier = sps.Tier;
                }

                chroma = sps.ChromaFormatIdc;
                bitDepth = sps.BitDepth != 0 ? sps.BitDepth : bitDepth;
                break;
            }
        }

        if (width == 0 && profile == 0)
        {
            return false;
        }

        parameters = new CodecParameters
        {
            Codec = CodecId.Vvc,
            Rfc6381 = string.Create(CultureInfo.InvariantCulture, $"vvc1.{profile}.{(tier ? 'H' : 'L')}{level}"),
            Profile = profile,
            Level = level,
            Width = width,
            Height = height,
            BitDepth = bitDepth,
            Chroma = chroma switch { 0 => Codecs.ChromaFormat.Monochrome, 1 => Codecs.ChromaFormat.Yuv420, 2 => Codecs.ChromaFormat.Yuv422, _ => Codecs.ChromaFormat.Yuv444 },
            NalLengthSize = LengthSize,
        };
        return true;
    }
}
