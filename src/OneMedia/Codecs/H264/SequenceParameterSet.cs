using System.Buffers;
using System.Globalization;

namespace RecursiveAI.OneMedia.Codecs.H264;

/// <summary>NAL unit types of ITU-T H.264 Table 7-1 that this layer handles.</summary>
public static class H264NalType
{
    /// <summary>Coded slice of a non-IDR picture.</summary>
    public const int Slice = 1;

    /// <summary>Coded slice of an IDR picture.</summary>
    public const int IdrSlice = 5;

    /// <summary>Supplemental enhancement information.</summary>
    public const int Sei = 6;

    /// <summary>Sequence parameter set.</summary>
    public const int Sps = 7;

    /// <summary>Picture parameter set.</summary>
    public const int Pps = 8;

    /// <summary>Access unit delimiter.</summary>
    public const int AccessUnitDelimiter = 9;

    /// <summary>Sequence parameter set extension.</summary>
    public const int SpsExtension = 13;
}

/// <summary>
/// The fields of an H.264 sequence parameter set that tools need to describe a stream (ITU-T H.264 §7.3.2.1.1), with the VUI of
/// Annex E. Scaling lists and picture order count details are skipped, not stored.
/// </summary>
public readonly record struct SequenceParameterSet
{
    /// <summary>profile_idc.</summary>
    public int ProfileIdc { get; init; }

    /// <summary>The eight bits holding constraint_set0_flag to constraint_set5_flag and two reserved bits, as in the byte after profile_idc.</summary>
    public int ConstraintFlags { get; init; }

    /// <summary>level_idc.</summary>
    public int LevelIdc { get; init; }

    /// <summary>seq_parameter_set_id.</summary>
    public int SpsId { get; init; }

    /// <summary>chroma_format_idc: 0 monochrome, 1 4:2:0, 2 4:2:2, 3 4:4:4. 1 when the profile does not signal it.</summary>
    public int ChromaFormatIdc { get; init; }

    /// <summary>separate_colour_plane_flag.</summary>
    public bool SeparateColourPlane { get; init; }

    /// <summary>Luma bit depth (bit_depth_luma_minus8 + 8).</summary>
    public int BitDepthLuma { get; init; }

    /// <summary>Chroma bit depth (bit_depth_chroma_minus8 + 8).</summary>
    public int BitDepthChroma { get; init; }

    /// <summary>max_num_ref_frames.</summary>
    public int MaxNumRefFrames { get; init; }

    /// <summary>log2_max_frame_num_minus4 + 4: the width in bits of frame_num in slice headers.</summary>
    public int Log2MaxFrameNum { get; init; }

    /// <summary>frame_mbs_only_flag: false for streams that may code fields.</summary>
    public bool FrameMbsOnly { get; init; }

    /// <summary>mb_adaptive_frame_field_flag.</summary>
    public bool MbAdaptiveFrameField { get; init; }

    /// <summary>Width in luma samples before cropping.</summary>
    public int CodedWidth { get; init; }

    /// <summary>Height in luma samples before cropping.</summary>
    public int CodedHeight { get; init; }

    /// <summary>Width in luma samples after the frame cropping rectangle is applied (§7.4.2.1.1).</summary>
    public int Width { get; init; }

    /// <summary>Height in luma samples after cropping.</summary>
    public int Height { get; init; }

    /// <summary>True when a VUI is present.</summary>
    public bool HasVui { get; init; }

    /// <summary>aspect_ratio_idc, 0 when absent.</summary>
    public int AspectRatioIdc { get; init; }

    /// <summary>Sample aspect ratio width (from the idc table or Extended_SAR), 0 when absent.</summary>
    public int SarWidth { get; init; }

    /// <summary>Sample aspect ratio height, 0 when absent.</summary>
    public int SarHeight { get; init; }

    /// <summary>The colour description of the VUI.</summary>
    public ColorInfo Color { get; init; }

    /// <summary>video_format of the VUI (5 = unspecified), or 5 when absent.</summary>
    public int VideoFormat { get; init; }

    /// <summary>True when timing_info_present_flag is set.</summary>
    public bool HasTimingInfo { get; init; }

    /// <summary>num_units_in_tick.</summary>
    public uint NumUnitsInTick { get; init; }

    /// <summary>time_scale.</summary>
    public uint TimeScale { get; init; }

    /// <summary>fixed_frame_rate_flag.</summary>
    public bool FixedFrameRate { get; init; }

    /// <summary>pic_struct_present_flag.</summary>
    public bool PicStructPresent { get; init; }

    /// <summary>max_num_reorder_frames from the VUI bitstream restriction, or -1 when not signalled.</summary>
    public int MaxNumReorderFrames { get; init; }

    /// <summary>max_dec_frame_buffering from the VUI bitstream restriction, or -1 when not signalled.</summary>
    public int MaxDecFrameBuffering { get; init; }

    /// <summary>The <c>avc1.PPCCLL</c> string of RFC 6381 built from profile, constraint flags and level (ISO/IEC 14496-15).</summary>
    public string Rfc6381 => string.Create(CultureInfo.InvariantCulture, $"avc1.{ProfileIdc:X2}{ConstraintFlags:X2}{LevelIdc:X2}");

    /// <summary>Frame rate numerator from timing info: time_scale (the tick is half a frame, §E.2.1), or 0 when absent.</summary>
    public long FrameRateNumerator => HasTimingInfo && NumUnitsInTick != 0 ? TimeScale : 0;

    /// <summary>Frame rate denominator: 2 × num_units_in_tick, or 0 when absent.</summary>
    public long FrameRateDenominator => HasTimingInfo && NumUnitsInTick != 0 ? 2L * NumUnitsInTick : 0;

    /// <summary>The profile name as players print it, for example <c>High</c> or <c>Constrained Baseline</c>.</summary>
    public string ProfileName => H264Profiles.Name(ProfileIdc, ConstraintFlags);

    /// <summary>Parses a sequence parameter set.</summary>
    /// <param name="nal">The NAL unit including its header byte; emulation-prevention bytes are removed while reading.</param>
    /// <param name="sps">The parsed set when the result is true.</param>
    /// <returns>False when the unit is not an SPS, is truncated, or holds values outside the syntax's ranges. Never throws and never allocates.</returns>
    public static bool TryParse(ReadOnlySpan<byte> nal, out SequenceParameterSet sps)
    {
        sps = default;
        if (nal.Length < 4 || (nal[0] & 0x1f) != H264NalType.Sps)
        {
            return false;
        }

        BitReader r = new(nal[1..], removeEmulationPrevention: true);
        int profile = (int)r.ReadBits(8);
        int constraints = (int)r.ReadBits(8);
        int level = (int)r.ReadBits(8);
        uint id = r.ReadUe();
        if (id > 31)
        {
            return false;
        }

        int chroma = 1;
        bool separate = false;
        int bdLuma = 8;
        int bdChroma = 8;
        if (HasChromaInfo(profile))
        {
            uint c = r.ReadUe();
            if (c > 3)
            {
                return false;
            }

            chroma = (int)c;
            if (chroma == 3)
            {
                separate = r.ReadFlag();
            }

            uint bl = r.ReadUe();
            uint bc = r.ReadUe();
            if (bl > 6 || bc > 6)
            {
                return false;
            }

            bdLuma = (int)bl + 8;
            bdChroma = (int)bc + 8;
            r.ReadBit(); // qpprime_y_zero_transform_bypass_flag
            if (r.ReadFlag())
            {
                int lists = chroma != 3 ? 8 : 12;
                for (int i = 0; i < lists; i++)
                {
                    if (r.ReadFlag())
                    {
                        SkipScalingList(ref r, i < 6 ? 16 : 64);
                    }
                }
            }
        }

        uint log2MaxFrameNum = r.ReadUe();
        uint pocType = r.ReadUe();
        if (log2MaxFrameNum > 12 || pocType > 2)
        {
            return false;
        }

        if (pocType == 0)
        {
            if (r.ReadUe() > 12)
            {
                return false;
            }
        }
        else if (pocType == 1)
        {
            r.ReadBit();
            r.ReadSe();
            r.ReadSe();
            uint cycle = r.ReadUe();
            if (cycle > 255)
            {
                return false;
            }

            for (uint i = 0; i < cycle; i++)
            {
                r.ReadSe();
            }
        }

        uint refFrames = r.ReadUe();
        r.ReadBit(); // gaps_in_frame_num_value_allowed_flag
        uint wMbs = r.ReadUe();
        uint hMapUnits = r.ReadUe();
        bool frameMbsOnly = r.ReadFlag();
        bool mbaff = false;
        if (!frameMbsOnly)
        {
            mbaff = r.ReadFlag();
        }

        r.ReadBit(); // direct_8x8_inference_flag
        uint cropL = 0, cropR = 0, cropT = 0, cropB = 0;
        if (r.ReadFlag())
        {
            cropL = r.ReadUe();
            cropR = r.ReadUe();
            cropT = r.ReadUe();
            cropB = r.ReadUe();
        }

        if (r.Overrun || wMbs > 8191 || hMapUnits > 8191 || refFrames > 16)
        {
            return false;
        }

        long codedW = (wMbs + 1L) * 16;
        long codedH = (hMapUnits + 1L) * 16 * (frameMbsOnly ? 1 : 2);
        // §7.4.2.1.1: the crop unit depends on ChromaArrayType and on whether the sequence can contain field pictures.
        int chromaArrayType = separate ? 0 : chroma;
        int unitX = chromaArrayType is 1 or 2 ? 2 : 1;
        int unitY = (chromaArrayType == 1 ? 2 : 1) * (frameMbsOnly ? 1 : 2);
        long w = codedW - (long)unitX * (cropL + (long)cropR);
        long h = codedH - (long)unitY * (cropT + (long)cropB);
        if (w <= 0 || h <= 0)
        {
            return false;
        }

        SequenceParameterSet result = new()
        {
            ProfileIdc = profile,
            ConstraintFlags = constraints,
            LevelIdc = level,
            SpsId = (int)id,
            ChromaFormatIdc = chroma,
            SeparateColourPlane = separate,
            BitDepthLuma = bdLuma,
            BitDepthChroma = bdChroma,
            MaxNumRefFrames = (int)refFrames,
            Log2MaxFrameNum = (int)log2MaxFrameNum + 4,
            FrameMbsOnly = frameMbsOnly,
            MbAdaptiveFrameField = mbaff,
            CodedWidth = (int)codedW,
            CodedHeight = (int)codedH,
            Width = (int)w,
            Height = (int)h,
            VideoFormat = 5,
            Color = ColorInfo.Unspecified,
            MaxNumReorderFrames = -1,
            MaxDecFrameBuffering = -1,
        };

        if (r.ReadFlag())
        {
            result = ParseVui(ref r, result);
        }

        // A truncated VUI is not fatal: the fields before it are valid and the VUI ones keep their defaults.
        sps = r.Overrun ? result with { HasVui = false } : result;
        return true;
    }

    // ITU-T H.264 §E.1.1
    private static SequenceParameterSet ParseVui(ref BitReader r, SequenceParameterSet s)
    {
        s = s with { HasVui = true };
        if (r.ReadFlag())
        {
            int idc = (int)r.ReadBits(8);
            s = s with { AspectRatioIdc = idc };
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
            r.ReadBit(); // overscan_appropriate_flag
        }

        if (r.ReadFlag())
        {
            int format = (int)r.ReadBits(3);
            bool full = r.ReadFlag();
            ColorInfo color = new(false, 2, 2, 2, full);
            if (r.ReadFlag())
            {
                color = new ColorInfo(true, (int)r.ReadBits(8), (int)r.ReadBits(8), (int)r.ReadBits(8), full);
            }

            s = s with { VideoFormat = format, Color = color };
        }

        if (r.ReadFlag())
        {
            r.ReadUe();
            r.ReadUe();
        }

        if (r.ReadFlag())
        {
            uint units = r.ReadBits(32);
            uint scale = r.ReadBits(32);
            bool fixedRate = r.ReadFlag();
            s = s with { HasTimingInfo = true, NumUnitsInTick = units, TimeScale = scale, FixedFrameRate = fixedRate };
        }

        bool nalHrd = r.ReadFlag();
        if (nalHrd && !SkipHrd(ref r))
        {
            return s;
        }

        bool vclHrd = r.ReadFlag();
        if (vclHrd && !SkipHrd(ref r))
        {
            return s;
        }

        if (nalHrd || vclHrd)
        {
            r.ReadBit(); // low_delay_hrd_flag
        }

        s = s with { PicStructPresent = r.ReadFlag() };
        if (r.ReadFlag())
        {
            r.ReadBit(); // motion_vectors_over_pic_boundaries_flag
            r.ReadUe();
            r.ReadUe();
            r.ReadUe();
            r.ReadUe();
            uint reorder = r.ReadUe();
            uint buffering = r.ReadUe();
            if (!r.Overrun)
            {
                s = s with { MaxNumReorderFrames = (int)Math.Min(reorder, 64u), MaxDecFrameBuffering = (int)Math.Min(buffering, 64u) };
            }
        }

        return s;
    }

    // ITU-T H.264 §E.1.2
    private static bool SkipHrd(ref BitReader r)
    {
        uint cpbCountMinus1 = r.ReadUe();
        if (cpbCountMinus1 > 31)
        {
            return false;
        }

        r.SkipBits(8); // bit_rate_scale, cpb_size_scale
        for (uint i = 0; i <= cpbCountMinus1; i++)
        {
            r.ReadUe();
            r.ReadUe();
            r.ReadBit();
        }

        r.SkipBits(20); // four 5-bit length fields
        return !r.Overrun;
    }

    // Table E-1
    private static ReadOnlySpan<byte> SarTable => [1, 1, 12, 11, 10, 11, 16, 11, 40, 33, 24, 11, 20, 11, 32, 11, 80, 33, 18, 11, 15, 11, 64, 33, 160, 99, 4, 3, 3, 2, 2, 1];

    private static bool HasChromaInfo(int profile) =>
        profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135;

    private static void SkipScalingList(ref BitReader r, int size)
    {
        int last = 8;
        int next = 8;
        for (int j = 0; j < size; j++)
        {
            if (next != 0)
            {
                int delta = r.ReadSe();
                next = (last + delta + 256) % 256;
            }

            if (next != 0)
            {
                last = next;
            }

            if (r.Overrun)
            {
                return;
            }
        }
    }
}

/// <summary>Profile naming for H.264.</summary>
public static class H264Profiles
{
    /// <summary>The name of a profile as ffprobe and players print it.</summary>
    /// <param name="profileIdc">profile_idc.</param>
    /// <param name="constraintFlags">The constraint byte (constraint_set0 is bit 7).</param>
    /// <returns>A name, or <c>Unknown</c>.</returns>
    public static string Name(int profileIdc, int constraintFlags)
    {
        bool set1 = (constraintFlags & 0x40) != 0;
        bool set3 = (constraintFlags & 0x10) != 0;
        return profileIdc switch
        {
            66 => set1 ? "Constrained Baseline" : "Baseline",
            77 => "Main",
            88 => "Extended",
            100 => (constraintFlags & 0x0c) == 0x0c ? "Constrained High" : (constraintFlags & 0x08) != 0 ? "Progressive High" : "High",
            110 => set3 ? "High 10 Intra" : (constraintFlags & 0x08) != 0 ? "Progressive High 10" : "High 10",
            122 => set3 ? "High 4:2:2 Intra" : "High 4:2:2",
            244 => set3 ? "High 4:4:4 Intra" : "High 4:4:4 Predictive",
            44 => "CAVLC 4:4:4 Intra",
            83 => "Scalable Baseline",
            86 => "Scalable High",
            118 => "Multiview High",
            128 => "Stereo High",
            _ => "Unknown",
        };
    }
}
