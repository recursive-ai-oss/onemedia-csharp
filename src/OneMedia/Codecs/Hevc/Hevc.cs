using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMedia.Codecs.Hevc;

/// <summary>NAL unit types of ITU-T H.265 Table 7-1 that this layer handles.</summary>
public static class HevcNalType
{
    /// <summary>Video parameter set.</summary>
    public const int Vps = 32;

    /// <summary>Sequence parameter set.</summary>
    public const int Sps = 33;

    /// <summary>Picture parameter set.</summary>
    public const int Pps = 34;

    /// <summary>Access unit delimiter.</summary>
    public const int AccessUnitDelimiter = 35;

    /// <summary>Prefix SEI.</summary>
    public const int PrefixSei = 39;

    /// <summary>Suffix SEI.</summary>
    public const int SuffixSei = 40;
}

/// <summary>The general profile, tier and level of an H.265 stream (ITU-T H.265 §7.3.3, general_* fields).</summary>
/// <param name="ProfileSpace">general_profile_space.</param>
/// <param name="Tier">general_tier_flag: true for the High tier.</param>
/// <param name="ProfileIdc">general_profile_idc.</param>
/// <param name="CompatibilityFlags">general_profile_compatibility_flag[0..31]; flag j is bit (31 - j).</param>
/// <param name="ConstraintFlags">The 48 bits following the compatibility flags: progressive_source, interlaced_source, non_packed_constraint, frame_only_constraint and 44 further bits, most significant first.</param>
/// <param name="LevelIdc">general_level_idc: 30 × the level number.</param>
public readonly record struct HevcProfileTierLevel(int ProfileSpace, bool Tier, int ProfileIdc, uint CompatibilityFlags, ulong ConstraintFlags, int LevelIdc)
{
    /// <summary>The <c>hvc1.</c> style string of RFC 6381 / ISO/IEC 14496-15 Annex E, for example <c>hvc1.1.6.L93.B0</c>.</summary>
    /// <param name="prefix">The sample entry code: <c>hvc1</c> or <c>hev1</c>.</param>
    /// <returns>The string.</returns>
    public string ToRfc6381(string prefix = "hvc1")
    {
        // The compatibility flags are written with flag 0 as the least significant bit, i.e. bit-reversed.
        uint reversed = 0;
        for (int i = 0; i < 32; i++)
        {
            reversed |= ((CompatibilityFlags >> (31 - i)) & 1) << i;
        }

        StringBuilder sb = new(prefix);
        sb.Append('.');
        if (ProfileSpace > 0)
        {
            sb.Append((char)('A' + ProfileSpace - 1));
        }

        sb.Append(ProfileIdc.ToString(CultureInfo.InvariantCulture)).Append('.')
          .Append(reversed.ToString("X", CultureInfo.InvariantCulture)).Append('.')
          .Append(Tier ? 'H' : 'L').Append(LevelIdc.ToString(CultureInfo.InvariantCulture));

        // Constraint bytes, most significant first, trailing zero bytes dropped (a lone zero byte is kept as nothing).
        int last = -1;
        for (int i = 0; i < 6; i++)
        {
            if (((ConstraintFlags >> (40 - (8 * i))) & 0xff) != 0)
            {
                last = i;
            }
        }

        for (int i = 0; i <= last; i++)
        {
            sb.Append('.').Append(((ConstraintFlags >> (40 - (8 * i))) & 0xff).ToString("X", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>The profile as ffprobe prints it, for example <c>Main 10</c>; <c>Unknown</c> otherwise.</summary>
    public string ProfileName => ProfileIdc switch
    {
        1 => "Main",
        2 => "Main 10",
        3 => "Main Still Picture",
        4 => "Rext",
        5 => "High Throughput",
        6 => "Multiview Main",
        7 => "Scalable Main",
        8 => "3D Main",
        9 => "Screen Extended",
        _ => "Unknown",
    };
}

/// <summary>The fields of an H.265 sequence parameter set that tools need (ITU-T H.265 §7.3.2.2.1), with the VUI of Annex E.</summary>
public readonly record struct SequenceParameterSet
{
    /// <summary>sps_video_parameter_set_id.</summary>
    public int VpsId { get; init; }

    /// <summary>sps_max_sub_layers_minus1 + 1.</summary>
    public int MaxSubLayers { get; init; }

    /// <summary>sps_temporal_id_nesting_flag.</summary>
    public bool TemporalIdNesting { get; init; }

    /// <summary>The general profile, tier and level.</summary>
    public HevcProfileTierLevel Ptl { get; init; }

    /// <summary>sps_seq_parameter_set_id.</summary>
    public int SpsId { get; init; }

    /// <summary>chroma_format_idc.</summary>
    public int ChromaFormatIdc { get; init; }

    /// <summary>separate_colour_plane_flag.</summary>
    public bool SeparateColourPlane { get; init; }

    /// <summary>Width before the conformance window is applied.</summary>
    public int CodedWidth { get; init; }

    /// <summary>Height before the conformance window is applied.</summary>
    public int CodedHeight { get; init; }

    /// <summary>Width after the conformance window.</summary>
    public int Width { get; init; }

    /// <summary>Height after the conformance window.</summary>
    public int Height { get; init; }

    /// <summary>Luma bit depth.</summary>
    public int BitDepthLuma { get; init; }

    /// <summary>Chroma bit depth.</summary>
    public int BitDepthChroma { get; init; }

    /// <summary>sps_max_num_reorder_pics of the highest sub-layer.</summary>
    public int MaxNumReorderPics { get; init; }

    /// <summary>sps_max_dec_pic_buffering_minus1 + 1 of the highest sub-layer.</summary>
    public int MaxDecPicBuffering { get; init; }

    /// <summary>True when a VUI is present.</summary>
    public bool HasVui { get; init; }

    /// <summary>Sample aspect ratio width, 0 when absent.</summary>
    public int SarWidth { get; init; }

    /// <summary>Sample aspect ratio height, 0 when absent.</summary>
    public int SarHeight { get; init; }

    /// <summary>The VUI colour description.</summary>
    public ColorInfo Color { get; init; }

    /// <summary>field_seq_flag: the stream is coded as fields.</summary>
    public bool FieldSeq { get; init; }

    /// <summary>True when vui_timing_info_present_flag is set.</summary>
    public bool HasTimingInfo { get; init; }

    /// <summary>vui_num_units_in_tick.</summary>
    public uint NumUnitsInTick { get; init; }

    /// <summary>vui_time_scale.</summary>
    public uint TimeScale { get; init; }

    /// <summary>min_spatial_segmentation_idc of the VUI bitstream restriction, 0 when absent.</summary>
    public int MinSpatialSegmentationIdc { get; init; }

    /// <summary>Number of temporal sub-layers is <see cref="MaxSubLayers"/>.</summary>
    /// <param name="prefix">The sample entry code, <c>hvc1</c> or <c>hev1</c>.</param>
    /// <returns>The RFC 6381 string.</returns>
    public string ToRfc6381(string prefix = "hvc1") => Ptl.ToRfc6381(prefix);

    /// <summary>Parses a sequence parameter set.</summary>
    /// <param name="nal">The NAL unit including its two-byte header; emulation-prevention bytes are removed while reading.</param>
    /// <param name="sps">The result when true.</param>
    /// <returns>False when the unit is not an SPS, is truncated or out of range. Never throws, never allocates.</returns>
    public static bool TryParse(ReadOnlySpan<byte> nal, out SequenceParameterSet sps)
    {
        sps = default;
        if (nal.Length < 6 || ((nal[0] >> 1) & 0x3f) != HevcNalType.Sps)
        {
            return false;
        }

        BitReader r = new(nal[2..], removeEmulationPrevention: true);
        int vps = (int)r.ReadBits(4);
        int maxSub = (int)r.ReadBits(3);
        bool nesting = r.ReadFlag();
        if (maxSub > 6)
        {
            return false;
        }

        HevcProfileTierLevel ptl = ReadProfileTierLevel(ref r, maxSub);
        uint id = r.ReadUe();
        uint chroma = r.ReadUe();
        if (id > 15 || chroma > 3)
        {
            return false;
        }

        bool separate = false;
        if (chroma == 3)
        {
            separate = r.ReadFlag();
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

        uint bdl = r.ReadUe();
        uint bdc = r.ReadUe();
        uint log2PocLsbMinus4 = r.ReadUe();
        if (r.Overrun || w is 0 or > 16888 || h is 0 or > 16888 || bdl > 8 || bdc > 8 || log2PocLsbMinus4 > 12)
        {
            return false;
        }

        bool subLayerOrdering = r.ReadFlag();
        uint reorder = 0, buffering = 0;
        for (int i = subLayerOrdering ? 0 : maxSub; i <= maxSub; i++)
        {
            buffering = r.ReadUe() + 1;
            reorder = r.ReadUe();
            r.ReadUe(); // sps_max_latency_increase_plus1
        }

        r.ReadUe(); // log2_min_luma_coding_block_size_minus3
        r.ReadUe(); // log2_diff_max_min_luma_coding_block_size
        r.ReadUe(); // log2_min_luma_transform_block_size_minus2
        r.ReadUe(); // log2_diff_max_min_luma_transform_block_size
        r.ReadUe(); // max_transform_hierarchy_depth_inter
        r.ReadUe(); // max_transform_hierarchy_depth_intra
        if (r.ReadFlag() && r.ReadFlag())
        {
            SkipScalingListData(ref r);
        }

        r.ReadBit(); // amp_enabled_flag
        r.ReadBit(); // sample_adaptive_offset_enabled_flag
        if (r.ReadFlag())
        {
            r.SkipBits(8); // pcm sample bit depths
            r.ReadUe();
            r.ReadUe();
            r.ReadBit();
        }

        uint numRps = r.ReadUe();
        if (r.Overrun || numRps > 64)
        {
            return false;
        }

        // NumDeltaPocs of every earlier set, needed by inter-set prediction (§7.4.8).
        DeltaPocCounts counts = default;
        for (uint i = 0; i < numRps; i++)
        {
            if (!SkipShortTermRefPicSet(ref r, (int)i, (int)numRps, ref counts))
            {
                return false;
            }
        }

        if (r.ReadFlag())
        {
            uint numLt = r.ReadUe();
            if (numLt > 32)
            {
                return false;
            }

            for (uint i = 0; i < numLt; i++)
            {
                r.SkipBits((int)log2PocLsbMinus4 + 4);
                r.ReadBit();
            }
        }

        r.ReadBit(); // sps_temporal_mvp_enabled_flag
        r.ReadBit(); // strong_intra_smoothing_enabled_flag

        int chromaArrayType = separate ? 0 : (int)chroma;
        int subW = chromaArrayType is 1 or 2 ? 2 : 1;
        int subH = chromaArrayType == 1 ? 2 : 1;
        long width = w - ((long)subW * (cl + (long)cr));
        long height = h - ((long)subH * (ct + (long)cb));
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        SequenceParameterSet result = new()
        {
            VpsId = vps,
            MaxSubLayers = maxSub + 1,
            TemporalIdNesting = nesting,
            Ptl = ptl,
            SpsId = (int)id,
            ChromaFormatIdc = (int)chroma,
            SeparateColourPlane = separate,
            CodedWidth = (int)w,
            CodedHeight = (int)h,
            Width = (int)width,
            Height = (int)height,
            BitDepthLuma = (int)bdl + 8,
            BitDepthChroma = (int)bdc + 8,
            MaxNumReorderPics = (int)Math.Min(reorder, 64u),
            MaxDecPicBuffering = (int)Math.Min(buffering, 65u),
            Color = ColorInfo.Unspecified,
        };

        if (!r.Overrun && r.ReadFlag())
        {
            SequenceParameterSet withVui = ParseVui(ref r, result, maxSub);
            if (!r.Overrun)
            {
                result = withVui;
            }
        }

        sps = result;
        return true;
    }

    private static HevcProfileTierLevel ReadProfileTierLevel(ref BitReader r, int maxSub)
    {
        // ITU-T H.265 §7.3.3 with profilePresentFlag = 1
        int space = (int)r.ReadBits(2);
        bool tier = r.ReadFlag();
        int idc = (int)r.ReadBits(5);
        uint compat = r.ReadBits(32);
        ulong constraints = r.ReadBits64(48);
        int level = (int)r.ReadBits(8);
        uint profilePresent = 0, levelPresent = 0;
        for (int i = 0; i < maxSub; i++)
        {
            profilePresent |= (uint)r.ReadBit() << i;
            levelPresent |= (uint)r.ReadBit() << i;
        }

        if (maxSub > 0)
        {
            r.SkipBits(2 * (8 - maxSub)); // reserved_zero_2bits
        }

        for (int i = 0; i < maxSub; i++)
        {
            if (((profilePresent >> i) & 1) != 0)
            {
                r.SkipBits(88);
            }

            if (((levelPresent >> i) & 1) != 0)
            {
                r.SkipBits(8);
            }
        }

        return new HevcProfileTierLevel(space, tier, idc, compat, constraints, level);
    }

    private static void SkipScalingListData(ref BitReader r)
    {
        for (int sizeId = 0; sizeId < 4; sizeId++)
        {
            for (int matrixId = 0; matrixId < 6; matrixId += sizeId == 3 ? 3 : 1)
            {
                if (!r.ReadFlag())
                {
                    r.ReadUe(); // scaling_list_pred_matrix_id_delta
                }
                else
                {
                    int coefNum = Math.Min(64, 1 << (4 + (sizeId << 1)));
                    if (sizeId > 1)
                    {
                        r.ReadSe(); // scaling_list_dc_coef_minus8
                    }

                    for (int i = 0; i < coefNum; i++)
                    {
                        r.ReadSe();
                    }
                }

                if (r.Overrun)
                {
                    return;
                }
            }
        }
    }

    [System.Runtime.CompilerServices.InlineArray(65)]
    private struct DeltaPocCounts
    {
        private int _element;
    }

    // ITU-T H.265 §7.3.7 and §7.4.8: only the number of entries matters here, to keep later sets parseable.
    private static bool SkipShortTermRefPicSet(ref BitReader r, int idx, int numSets, ref DeltaPocCounts counts)
    {
        bool inter = idx != 0 && r.ReadFlag();
        if (inter)
        {
            if (idx == numSets)
            {
                return false; // only a slice header can predict from the immediately preceding set by index
            }

            int refIdx = idx - 1; // delta_idx_minus1 is absent in an SPS, so the reference is the previous set
            r.ReadBit();          // delta_rps_sign
            r.ReadUe();           // abs_delta_rps_minus1
            int refCount = counts[refIdx];
            int kept = 0;
            for (int j = 0; j <= refCount; j++)
            {
                bool used = r.ReadFlag();
                bool useDelta = true;
                if (!used)
                {
                    useDelta = r.ReadFlag();
                }

                if (used || useDelta)
                {
                    kept++;
                }
            }

            counts[idx] = kept;
        }
        else
        {
            uint neg = r.ReadUe();
            uint pos = r.ReadUe();
            if (neg > 16 || pos > 16)
            {
                return false;
            }

            for (uint i = 0; i < neg + pos; i++)
            {
                r.ReadUe();
                r.ReadBit();
            }

            counts[idx] = (int)(neg + pos);
        }

        return !r.Overrun;
    }

    // ITU-T H.265 §E.2.1
    private static SequenceParameterSet ParseVui(ref BitReader r, SequenceParameterSet s, int maxSub)
    {
        s = s with { HasVui = true };
        if (r.ReadFlag())
        {
            int idc = (int)r.ReadBits(8);
            if (idc == 255)
            {
                s = s with { SarWidth = (int)r.ReadBits(16), SarHeight = (int)r.ReadBits(16) };
            }
            else if (idc is >= 1 and <= 16)
            {
                s = s with { SarWidth = SarTable[(idc - 1) * 2], SarHeight = SarTable[((idc - 1) * 2) + 1] };
            }
        }

        if (r.ReadFlag())
        {
            r.ReadBit();
        }

        if (r.ReadFlag())
        {
            r.SkipBits(3);
            bool full = r.ReadFlag();
            ColorInfo color = new(false, 2, 2, 2, full);
            if (r.ReadFlag())
            {
                color = new ColorInfo(true, (int)r.ReadBits(8), (int)r.ReadBits(8), (int)r.ReadBits(8), full);
            }

            s = s with { Color = color };
        }

        if (r.ReadFlag())
        {
            r.ReadUe();
            r.ReadUe();
        }

        r.ReadBit(); // neutral_chroma_indication_flag
        s = s with { FieldSeq = r.ReadFlag() };
        r.ReadBit(); // frame_field_info_present_flag
        if (r.ReadFlag())
        {
            r.ReadUe();
            r.ReadUe();
            r.ReadUe();
            r.ReadUe();
        }

        if (r.ReadFlag())
        {
            uint units = r.ReadBits(32);
            uint scale = r.ReadBits(32);
            s = s with { HasTimingInfo = true, NumUnitsInTick = units, TimeScale = scale };
            if (r.ReadFlag())
            {
                r.ReadUe(); // vui_num_ticks_poc_diff_one_minus1
            }

            if (r.ReadFlag() && !SkipHrd(ref r, maxSub))
            {
                return s;
            }
        }

        if (r.ReadFlag())
        {
            r.SkipBits(3); // tiles_fixed_structure_flag, motion_vectors_over_pic_boundaries_flag, restricted_ref_pic_lists_flag
            uint seg = r.ReadUe();
            if (!r.Overrun && seg < 4096)
            {
                s = s with { MinSpatialSegmentationIdc = (int)seg };
            }
        }

        return s;
    }

    // ITU-T H.265 §E.2.2 with commonInfPresentFlag = 1
    private static bool SkipHrd(ref BitReader r, int maxSub)
    {
        bool nal = r.ReadFlag();
        bool vcl = r.ReadFlag();
        bool subPic = false;
        if (nal || vcl)
        {
            subPic = r.ReadFlag();
            if (subPic)
            {
                r.SkipBits(8 + 5 + 1 + 5);
            }

            r.SkipBits(8);
            if (subPic)
            {
                r.SkipBits(4);
            }

            r.SkipBits(15);
        }

        for (int i = 0; i <= maxSub; i++)
        {
            bool fixedGeneral = r.ReadFlag();
            bool fixedCvs = true;
            if (!fixedGeneral)
            {
                fixedCvs = r.ReadFlag();
            }

            bool lowDelay = false;
            if (fixedCvs)
            {
                r.ReadUe();
            }
            else
            {
                lowDelay = r.ReadFlag();
            }

            uint cpbCnt = 1;
            if (!lowDelay)
            {
                cpbCnt = r.ReadUe() + 1;
                if (cpbCnt > 32)
                {
                    return false;
                }
            }

            int sets = (nal ? 1 : 0) + (vcl ? 1 : 0);
            for (int k = 0; k < sets; k++)
            {
                for (uint j = 0; j < cpbCnt; j++)
                {
                    r.ReadUe();
                    r.ReadUe();
                    if (subPic)
                    {
                        r.ReadUe();
                        r.ReadUe();
                    }

                    r.ReadBit();
                }
            }

            if (r.Overrun)
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlySpan<byte> SarTable => [1, 1, 12, 11, 10, 11, 16, 11, 40, 33, 24, 11, 20, 11, 32, 11, 80, 33, 18, 11, 15, 11, 64, 33, 160, 99, 4, 3, 3, 2, 2, 1];
}

/// <summary>The essentials of an H.265 picture parameter set (ITU-T H.265 §7.3.2.3.1), as far as the decoder configuration record needs.</summary>
public readonly record struct PictureParameterSet
{
    /// <summary>pps_pic_parameter_set_id.</summary>
    public int PpsId { get; init; }

    /// <summary>pps_seq_parameter_set_id.</summary>
    public int SpsId { get; init; }

    /// <summary>tiles_enabled_flag.</summary>
    public bool TilesEnabled { get; init; }

    /// <summary>entropy_coding_sync_enabled_flag.</summary>
    public bool EntropyCodingSyncEnabled { get; init; }

    /// <summary>Parses a picture parameter set.</summary>
    /// <param name="nal">The NAL unit including its two-byte header.</param>
    /// <param name="pps">The result when true.</param>
    /// <returns>False when the unit is not a PPS or is truncated. Never throws.</returns>
    public static bool TryParse(ReadOnlySpan<byte> nal, out PictureParameterSet pps)
    {
        pps = default;
        if (nal.Length < 4 || ((nal[0] >> 1) & 0x3f) != HevcNalType.Pps)
        {
            return false;
        }

        BitReader r = new(nal[2..], removeEmulationPrevention: true);
        uint id = r.ReadUe();
        uint spsId = r.ReadUe();
        r.SkipBits(2 + 3 + 1 + 1); // dependent_slice_segments, output_flag_present, num_extra_slice_header_bits, sign_data_hiding, cabac_init_present
        r.ReadUe();
        r.ReadUe();
        r.ReadSe();
        r.SkipBits(2);
        if (r.ReadFlag())
        {
            r.ReadUe();
        }

        r.ReadSe();
        r.ReadSe();
        r.SkipBits(4); // slice_chroma_qp_offsets_present, weighted_pred, weighted_bipred, transquant_bypass
        bool tiles = r.ReadFlag();
        bool sync = r.ReadFlag();
        if (r.Overrun || id > 63 || spsId > 15)
        {
            return false;
        }

        pps = new PictureParameterSet { PpsId = (int)id, SpsId = (int)spsId, TilesEnabled = tiles, EntropyCodingSyncEnabled = sync };
        return true;
    }
}

/// <summary>One array of NAL units of a single type in a <see cref="HevcDecoderConfigurationRecord"/>.</summary>
/// <param name="Completeness">array_completeness: all units of this type are in the array.</param>
/// <param name="NalUnitType">NAL_unit_type of the units.</param>
/// <param name="Units">The NAL units, with headers and without start codes.</param>
public sealed record HevcNalArray(bool Completeness, int NalUnitType, IReadOnlyList<ReadOnlyMemory<byte>> Units);

/// <summary>
/// An HEVCDecoderConfigurationRecord, the content of an <c>hvcC</c> box (ISO/IEC 14496-15 §8.3.3.1): profile, tier, level,
/// chroma and bit depth summary, NAL length size and the stored parameter-set arrays.
/// </summary>
public sealed class HevcDecoderConfigurationRecord
{
    /// <summary>configurationVersion, 1.</summary>
    public int ConfigurationVersion { get; init; } = 1;

    /// <summary>The general profile, tier and level fields.</summary>
    public HevcProfileTierLevel Ptl { get; init; }

    /// <summary>min_spatial_segmentation_idc (12 bits).</summary>
    public int MinSpatialSegmentationIdc { get; init; }

    /// <summary>parallelismType: 0 unknown, 1 slice, 2 tile, 3 wavefront.</summary>
    public int ParallelismType { get; init; }

    /// <summary>chromaFormat (chroma_format_idc).</summary>
    public int ChromaFormat { get; init; }

    /// <summary>Luma bit depth (bitDepthLumaMinus8 + 8).</summary>
    public int BitDepthLuma { get; init; } = 8;

    /// <summary>Chroma bit depth.</summary>
    public int BitDepthChroma { get; init; } = 8;

    /// <summary>avgFrameRate in frames per 256 seconds, 0 when unspecified.</summary>
    public int AvgFrameRate { get; init; }

    /// <summary>constantFrameRate (2 bits).</summary>
    public int ConstantFrameRate { get; init; }

    /// <summary>numTemporalLayers (3 bits).</summary>
    public int NumTemporalLayers { get; init; } = 1;

    /// <summary>temporalIdNested.</summary>
    public bool TemporalIdNested { get; init; }

    /// <summary>lengthSizeMinusOne + 1.</summary>
    public int LengthSize { get; init; } = 4;

    /// <summary>The parameter-set (and SEI) arrays in stored order.</summary>
    public IReadOnlyList<HevcNalArray> Arrays { get; init; } = [];

    /// <summary>Parses the record.</summary>
    /// <param name="data">The box content after its header. The record keeps slices of this memory.</param>
    /// <param name="record">The result when true.</param>
    /// <param name="limits">Bounds the stored unit count; default limits when null.</param>
    /// <returns>False when truncated, or the version is not 1.</returns>
    public static bool TryParse(ReadOnlyMemory<byte> data, out HevcDecoderConfigurationRecord? record, MediaLimits? limits = null)
    {
        record = null;
        limits ??= MediaLimits.Default;
        ReadOnlySpan<byte> s = data.Span;
        if (s.Length < 23 || s[0] != 1)
        {
            return false;
        }

        ulong constraints = 0;
        for (int i = 0; i < 6; i++)
        {
            constraints = (constraints << 8) | s[6 + i];
        }

        HevcProfileTierLevel ptl = new(s[1] >> 6, (s[1] & 0x20) != 0, s[1] & 0x1f, BinaryPrimitives.ReadUInt32BigEndian(s[2..]), constraints, s[12]);
        int arrayCount = s[22];
        int pos = 23;
        List<HevcNalArray> arrays = new(arrayCount);
        long totalUnits = 0;
        for (int a = 0; a < arrayCount; a++)
        {
            if (pos + 3 > s.Length)
            {
                return false;
            }

            bool complete = (s[pos] & 0x80) != 0;
            int type = s[pos] & 0x3f;
            int count = BinaryPrimitives.ReadUInt16BigEndian(s[(pos + 1)..]);
            pos += 3;
            totalUnits += count;
            if (totalUnits > Math.Min(limits.MaxElementCount, s.Length))
            {
                return false;
            }

            List<ReadOnlyMemory<byte>> units = new(count);
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

            arrays.Add(new HevcNalArray(complete, type, units));
        }

        record = new HevcDecoderConfigurationRecord
        {
            Ptl = ptl,
            MinSpatialSegmentationIdc = BinaryPrimitives.ReadUInt16BigEndian(s[13..]) & 0x0fff,
            ParallelismType = s[15] & 3,
            ChromaFormat = s[16] & 3,
            BitDepthLuma = (s[17] & 7) + 8,
            BitDepthChroma = (s[18] & 7) + 8,
            AvgFrameRate = BinaryPrimitives.ReadUInt16BigEndian(s[19..]),
            ConstantFrameRate = s[21] >> 6,
            NumTemporalLayers = (s[21] >> 3) & 7,
            TemporalIdNested = (s[21] & 4) != 0,
            LengthSize = (s[21] & 3) + 1,
            Arrays = arrays,
        };
        return true;
    }

    /// <summary>The size of the record as <see cref="Write"/> produces it.</summary>
    /// <returns>Bytes.</returns>
    public int GetSize()
    {
        int n = 23;
        foreach (HevcNalArray a in Arrays)
        {
            n += 3;
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
        w[0] = (byte)ConfigurationVersion;
        w[1] = (byte)((Ptl.ProfileSpace << 6) | (Ptl.Tier ? 0x20 : 0) | (Ptl.ProfileIdc & 0x1f));
        BinaryPrimitives.WriteUInt32BigEndian(w[2..], Ptl.CompatibilityFlags);
        for (int i = 0; i < 6; i++)
        {
            w[6 + i] = (byte)(Ptl.ConstraintFlags >> (40 - (8 * i)));
        }

        w[12] = (byte)Ptl.LevelIdc;
        BinaryPrimitives.WriteUInt16BigEndian(w[13..], (ushort)(0xf000 | (MinSpatialSegmentationIdc & 0x0fff)));
        w[15] = (byte)(0xfc | (ParallelismType & 3));
        w[16] = (byte)(0xfc | (ChromaFormat & 3));
        w[17] = (byte)(0xf8 | ((BitDepthLuma - 8) & 7));
        w[18] = (byte)(0xf8 | ((BitDepthChroma - 8) & 7));
        BinaryPrimitives.WriteUInt16BigEndian(w[19..], (ushort)AvgFrameRate);
        w[21] = (byte)(((ConstantFrameRate & 3) << 6) | ((NumTemporalLayers & 7) << 3) | (TemporalIdNested ? 4 : 0) | ((LengthSize - 1) & 3));
        w[22] = (byte)Arrays.Count;
        int p = 23;
        foreach (HevcNalArray a in Arrays)
        {
            w[p++] = (byte)((a.Completeness ? 0x80 : 0) | (a.NalUnitType & 0x3f));
            BinaryPrimitives.WriteUInt16BigEndian(w[p..], (ushort)a.Units.Count);
            p += 2;
            foreach (ReadOnlyMemory<byte> u in a.Units)
            {
                BinaryPrimitives.WriteUInt16BigEndian(w[p..], (ushort)u.Length);
                u.Span.CopyTo(w[(p + 2)..]);
                p += 2 + u.Length;
            }
        }

        writer.Advance(p);
    }

    /// <summary>Builds the record from an Annex B stream or its first access unit, collecting VPS, SPS, PPS and the prefix and suffix SEI
    /// units that precede the first slice.</summary>
    /// <param name="annexB">Annex B bytes.</param>
    /// <param name="record">The result when true.</param>
    /// <param name="lengthSize">The NAL length size the samples will use.</param>
    /// <param name="arrayCompleteness">The array_completeness value to store. ISO/IEC 14496-15 asks for true when the arrays hold every unit of their type;
    /// some writers store false.</param>
    /// <returns>False when no valid SPS is found. Arrays are ordered VPS, SPS, PPS, then SEI units, with duplicates dropped.</returns>
    public static bool TryCreateFromAnnexB(ReadOnlyMemory<byte> annexB, out HevcDecoderConfigurationRecord? record, int lengthSize = 4, bool arrayCompleteness = true)
    {
        record = null;
        List<ReadOnlyMemory<byte>> vps = [], sps = [], pps = [], prefix = [], suffix = [];
        AnnexBReader reader = new(annexB.Span);
        while (reader.TryRead(out ReadOnlySpan<byte> nal, out int offset, out _))
        {
            if (nal.Length < 3)
            {
                continue;
            }

            int type = (nal[0] >> 1) & 0x3f;
            List<ReadOnlyMemory<byte>>? target = type switch
            {
                HevcNalType.Vps => vps,
                HevcNalType.Sps => sps,
                HevcNalType.Pps => pps,
                HevcNalType.PrefixSei => prefix,
                HevcNalType.SuffixSei => suffix,
                _ => null,
            };
            if (target is null)
            {
                if (type < 32 && sps.Count > 0)
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

        // parallelismType stays 0 (mixed or unknown): deriving it would need the slice and tile layout of every picture, and files
        // written by common tools store 0 as well, including for streams with wavefront parallel processing enabled.
        const int parallelism = 0;

        List<HevcNalArray> arrays = [];
        AddArray(arrays, arrayCompleteness, HevcNalType.Vps, vps);
        AddArray(arrays, arrayCompleteness, HevcNalType.Sps, sps);
        AddArray(arrays, arrayCompleteness, HevcNalType.Pps, pps);
        AddArray(arrays, arrayCompleteness, HevcNalType.PrefixSei, prefix);
        AddArray(arrays, arrayCompleteness, HevcNalType.SuffixSei, suffix);
        record = new HevcDecoderConfigurationRecord
        {
            Ptl = first.Ptl,
            MinSpatialSegmentationIdc = first.MinSpatialSegmentationIdc,
            ParallelismType = parallelism,
            ChromaFormat = first.ChromaFormatIdc,
            BitDepthLuma = first.BitDepthLuma,
            BitDepthChroma = first.BitDepthChroma,
            NumTemporalLayers = first.MaxSubLayers,
            TemporalIdNested = first.TemporalIdNesting,
            LengthSize = lengthSize,
            Arrays = arrays,
        };
        return true;
    }

    private static void AddArray(List<HevcNalArray> arrays, bool complete, int type, List<ReadOnlyMemory<byte>> units)
    {
        if (units.Count > 0)
        {
            arrays.Add(new HevcNalArray(complete, type, units));
        }
    }

    /// <summary>Describes the stream from the first stored SPS (or, without one, from the record's own fields).</summary>
    /// <param name="parameters">The summary when true.</param>
    /// <returns>False when no SPS can be parsed.</returns>
    public bool TryDescribe(out CodecParameters? parameters)
    {
        parameters = null;
        foreach (HevcNalArray a in Arrays)
        {
            if (a.NalUnitType == HevcNalType.Sps && a.Units.Count > 0 && SequenceParameterSet.TryParse(a.Units[0].Span, out SequenceParameterSet sps))
            {
                parameters = new CodecParameters
                {
                    Codec = CodecId.Hevc,
                    Rfc6381 = Ptl.ToRfc6381(),
                    Profile = Ptl.ProfileIdc,
                    Level = Ptl.LevelIdc,
                    ProfileName = Ptl.ProfileName,
                    Width = sps.Width,
                    Height = sps.Height,
                    BitDepth = sps.BitDepthLuma,
                    Chroma = sps.ChromaFormatIdc switch { 0 => Codecs.ChromaFormat.Monochrome, 1 => Codecs.ChromaFormat.Yuv420, 2 => Codecs.ChromaFormat.Yuv422, _ => Codecs.ChromaFormat.Yuv444 },
                    Color = sps.Color,
                    IsInterlaced = sps.FieldSeq,
                    SarWidth = sps.SarWidth,
                    SarHeight = sps.SarHeight,
                    FrameRateNumerator = sps.HasTimingInfo && sps.NumUnitsInTick != 0 ? (int)Math.Min(sps.TimeScale, int.MaxValue) : 0,
                    FrameRateDenominator = sps.HasTimingInfo && sps.NumUnitsInTick != 0 ? (int)Math.Min(sps.NumUnitsInTick, int.MaxValue) : 0,
                    ReorderFrames = sps.MaxNumReorderPics,
                    NalLengthSize = LengthSize,
                };
                return true;
            }
        }

        return false;
    }
}
