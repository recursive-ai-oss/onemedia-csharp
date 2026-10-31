using System.Buffers;
using System.Globalization;

namespace RecursiveAI.OneMedia.Codecs.Av1;

/// <summary>OBU types of the AV1 bitstream specification §6.2.2.</summary>
public static class ObuType
{
    /// <summary>OBU_SEQUENCE_HEADER.</summary>
    public const int SequenceHeader = 1;

    /// <summary>OBU_TEMPORAL_DELIMITER.</summary>
    public const int TemporalDelimiter = 2;

    /// <summary>OBU_FRAME_HEADER.</summary>
    public const int FrameHeader = 3;

    /// <summary>OBU_TILE_GROUP.</summary>
    public const int TileGroup = 4;

    /// <summary>OBU_METADATA.</summary>
    public const int Metadata = 5;

    /// <summary>OBU_FRAME.</summary>
    public const int Frame = 6;

    /// <summary>OBU_REDUNDANT_FRAME_HEADER.</summary>
    public const int RedundantFrameHeader = 7;

    /// <summary>OBU_TILE_LIST.</summary>
    public const int TileList = 8;

    /// <summary>OBU_PADDING.</summary>
    public const int Padding = 15;
}

/// <summary>An OBU header (AV1 bitstream specification §5.3.2) and where its payload lies.</summary>
/// <param name="Type">obu_type.</param>
/// <param name="HasExtension">obu_extension_flag.</param>
/// <param name="TemporalId">temporal_id, 0 without an extension.</param>
/// <param name="SpatialId">spatial_id, 0 without an extension.</param>
/// <param name="HeaderSize">Bytes of header (1 or 2), not counting the size field.</param>
/// <param name="PayloadOffset">Offset of the payload from the OBU's first byte.</param>
/// <param name="PayloadSize">Payload size in bytes.</param>
public readonly record struct ObuHeader(int Type, bool HasExtension, int TemporalId, int SpatialId, int HeaderSize, int PayloadOffset, int PayloadSize)
{
    /// <summary>Total OBU length, header and size field and payload.</summary>
    public int TotalSize => PayloadOffset + PayloadSize;

    /// <summary>Parses one OBU header from the start of <paramref name="data"/>.</summary>
    /// <param name="data">Bytes starting at an OBU.</param>
    /// <param name="header">The result when true.</param>
    /// <param name="sizeIfNoField">When the OBU has no size field its payload runs to the end of the data (low-overhead format
    /// requires the field; Annex B supplies the length otherwise). Pass the OBU's length in that case, or -1 to reject such OBUs.</param>
    /// <returns>False when the header is truncated, the forbidden bit is set, or the size runs past the data.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out ObuHeader header, int sizeIfNoField = -1)
    {
        header = default;
        if (data.Length < 1 || (data[0] & 0x80) != 0)
        {
            return false;
        }

        int type = (data[0] >> 3) & 0xf;
        bool ext = (data[0] & 4) != 0;
        bool hasSize = (data[0] & 2) != 0;
        int pos = 1;
        int tid = 0, sid = 0;
        if (ext)
        {
            if (data.Length < 2)
            {
                return false;
            }

            tid = data[1] >> 5;
            sid = (data[1] >> 3) & 3;
            pos = 2;
        }

        long size;
        if (hasSize)
        {
            size = 0;
            int i = 0;
            while (true)
            {
                if (pos >= data.Length || i >= 8)
                {
                    return false;
                }

                byte b = data[pos++];
                size |= (long)(b & 0x7f) << (i * 7);
                i++;
                if ((b & 0x80) == 0)
                {
                    break;
                }
            }
        }
        else
        {
            if (sizeIfNoField < 0)
            {
                return false;
            }

            size = sizeIfNoField - pos;
            if (size < 0)
            {
                return false;
            }
        }

        if (size > data.Length - pos)
        {
            return false;
        }

        header = new ObuHeader(type, ext, tid, sid, ext ? 2 : 1, pos, (int)size);
        return true;
    }
}

/// <summary>How a run of AV1 data is framed.</summary>
public enum Av1Framing
{
    /// <summary>Not recognisable as AV1 data.</summary>
    Unknown = 0,

    /// <summary>Low-overhead bitstream format (§5.2): OBUs with size fields, as in <c>.obu</c> files and MP4/Matroska samples.</summary>
    LowOverhead,

    /// <summary>Annex B length-delimited format: temporal_unit_size, frame_unit_size and obu_length fields.</summary>
    AnnexB,
}

/// <summary>Detection of AV1 framing.</summary>
public static class Av1Detection
{
    /// <summary>Decides whether a run of bytes is low-overhead or Annex B framed, by walking the structure of the first temporal unit.</summary>
    /// <param name="data">The start of a stream or a sample.</param>
    /// <returns>The framing, or <see cref="Av1Framing.Unknown"/>. Low-overhead is preferred when both walk cleanly, since a stream that starts with a temporal delimiter OBU (0x12 0x00) is almost never also valid Annex B.</returns>
    public static Av1Framing DetectFraming(ReadOnlySpan<byte> data)
    {
        if (LooksLowOverhead(data))
        {
            return Av1Framing.LowOverhead;
        }

        return LooksAnnexB(data) ? Av1Framing.AnnexB : Av1Framing.Unknown;
    }

    private static bool LooksLowOverhead(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        int count = 0;
        while (pos < data.Length && count < 8)
        {
            if (!ObuHeader.TryParse(data[pos..], out ObuHeader h) || (data[pos] & 2) == 0 || h.Type is 0 or (> 8 and < 15))
            {
                return false;
            }

            if (count == 0 && h.Type != ObuType.TemporalDelimiter && h.Type != ObuType.SequenceHeader)
            {
                return false;
            }

            pos += h.TotalSize;
            count++;
        }

        return count > 0;
    }

    private static bool LooksAnnexB(ReadOnlySpan<byte> data)
    {
        BitReader r = new(data);
        ulong tuSize = r.ReadLeb128();
        if (r.Overrun || tuSize == 0 || tuSize > (ulong)(data.Length - (r.BitsConsumed / 8)))
        {
            return false;
        }

        long tuStart = r.BitsConsumed / 8;
        ulong fuSize = r.ReadLeb128();
        long fuStart = r.BitsConsumed / 8;
        if (r.Overrun || fuSize == 0 || fuSize > tuSize - (ulong)(fuStart - tuStart))
        {
            return false;
        }

        ulong obuLen = r.ReadLeb128();
        long obuStart = r.BitsConsumed / 8;
        if (r.Overrun || obuLen == 0 || obuLen > fuSize - (ulong)(obuStart - fuStart))
        {
            return false;
        }

        // The first OBU of a temporal unit is a temporal delimiter (§7.5).
        return ObuHeader.TryParse(data.Slice((int)obuStart, (int)obuLen), out ObuHeader h, (int)obuLen) && h.Type == ObuType.TemporalDelimiter;
    }
}

/// <summary>The fields of an AV1 sequence header OBU that tools need (AV1 bitstream specification §5.5).</summary>
public readonly record struct SequenceHeader
{
    /// <summary>seq_profile: 0 main, 1 high, 2 professional.</summary>
    public int Profile { get; init; }

    /// <summary>still_picture.</summary>
    public bool StillPicture { get; init; }

    /// <summary>reduced_still_picture_header.</summary>
    public bool ReducedStillPictureHeader { get; init; }

    /// <summary>seq_level_idx of operating point 0.</summary>
    public int LevelIdx { get; init; }

    /// <summary>seq_tier of operating point 0: true for the High tier.</summary>
    public bool Tier { get; init; }

    /// <summary>BitDepth: 8, 10 or 12.</summary>
    public int BitDepth { get; init; }

    /// <summary>mono_chrome.</summary>
    public bool MonoChrome { get; init; }

    /// <summary>subsampling_x.</summary>
    public bool SubsamplingX { get; init; }

    /// <summary>subsampling_y.</summary>
    public bool SubsamplingY { get; init; }

    /// <summary>chroma_sample_position: 0 unknown, 1 vertical, 2 colocated.</summary>
    public int ChromaSamplePosition { get; init; }

    /// <summary>The colour description (H.273 code points; the stream's own when colour_description_present_flag, otherwise unspecified).</summary>
    public ColorInfo Color { get; init; }

    /// <summary>max_frame_width_minus_1 + 1.</summary>
    public int MaxFrameWidth { get; init; }

    /// <summary>max_frame_height_minus_1 + 1.</summary>
    public int MaxFrameHeight { get; init; }

    /// <summary>True when timing_info_present_flag is set.</summary>
    public bool HasTimingInfo { get; init; }

    /// <summary>num_units_in_display_tick.</summary>
    public uint NumUnitsInDisplayTick { get; init; }

    /// <summary>time_scale.</summary>
    public uint TimeScale { get; init; }

    /// <summary>equal_picture_interval.</summary>
    public bool EqualPictureInterval { get; init; }

    /// <summary>num_ticks_per_picture_minus_1 + 1 when equal_picture_interval, else 0.</summary>
    public uint NumTicksPerPicture { get; init; }

    /// <summary>The chroma format implied by the subsampling flags.</summary>
    public ChromaFormat Chroma => MonoChrome ? ChromaFormat.Monochrome : !SubsamplingX && !SubsamplingY ? ChromaFormat.Yuv444 : SubsamplingX && !SubsamplingY ? ChromaFormat.Yuv422 : ChromaFormat.Yuv420;

    /// <summary>The <c>av01.P.LLT.DD</c> string of the AV1 ISOBMFF binding (§5), the short form without the optional colour fields.</summary>
    public string Rfc6381 => string.Create(CultureInfo.InvariantCulture, $"av01.{Profile}.{LevelIdx:D2}{(Tier ? 'H' : 'M')}.{BitDepth:D2}");

    /// <summary>Parses a sequence header OBU payload.</summary>
    /// <param name="payload">The OBU payload (after the header and size field).</param>
    /// <param name="header">The result when true.</param>
    /// <returns>False when truncated or out of range. Never throws, never allocates.</returns>
    public static bool TryParse(ReadOnlySpan<byte> payload, out SequenceHeader header)
    {
        header = default;
        BitReader r = new(payload);
        int profile = (int)r.ReadBits(3);
        bool still = r.ReadFlag();
        bool reduced = r.ReadFlag();
        bool timing = false;
        uint units = 0, scale = 0, ticks = 0;
        bool equal = false;
        int level = 0;
        bool tier = false;
        if (profile > 2)
        {
            return false;
        }

        if (reduced)
        {
            level = (int)r.ReadBits(5);
        }
        else
        {
            timing = r.ReadFlag();
            bool decoderModel = false;
            int bufferDelayLength = 0;
            if (timing)
            {
                units = r.ReadBits(32);
                scale = r.ReadBits(32);
                equal = r.ReadFlag();
                if (equal)
                {
                    ticks = r.ReadUvlc() + 1;
                }

                decoderModel = r.ReadFlag();
                if (decoderModel)
                {
                    bufferDelayLength = (int)r.ReadBits(5) + 1;
                    r.SkipBits(32 + 5 + 5);
                }
            }

            bool initialDisplayDelay = r.ReadFlag();
            int opCount = (int)r.ReadBits(5) + 1;
            for (int i = 0; i < opCount; i++)
            {
                r.SkipBits(12); // operating_point_idc
                int lvl = (int)r.ReadBits(5);
                bool t = lvl > 7 && r.ReadFlag();
                if (i == 0)
                {
                    level = lvl;
                    tier = t;
                }

                if (decoderModel && r.ReadFlag())
                {
                    r.SkipBits((2L * bufferDelayLength) + 1);
                }

                if (initialDisplayDelay && r.ReadFlag())
                {
                    r.SkipBits(4);
                }
            }
        }

        int wBits = (int)r.ReadBits(4) + 1;
        int hBits = (int)r.ReadBits(4) + 1;
        int maxW = (int)r.ReadBits(wBits) + 1;
        int maxH = (int)r.ReadBits(hBits) + 1;
        if (!reduced && r.ReadFlag())
        {
            r.SkipBits(4 + 3); // delta_frame_id_length_minus_2, additional_frame_id_length_minus_1
        }

        r.SkipBits(3); // use_128x128_superblock, enable_filter_intra, enable_intra_edge_filter
        if (!reduced)
        {
            r.SkipBits(4); // interintra_compound, masked_compound, warped_motion, dual_filter
            bool orderHint = r.ReadFlag();
            if (orderHint)
            {
                r.SkipBits(2); // enable_jnt_comp, enable_ref_frame_mvs
            }

            int forceScreenContent = 2;
            if (!r.ReadFlag())
            {
                forceScreenContent = r.ReadBit();
            }

            if (forceScreenContent > 0 && !r.ReadFlag())
            {
                r.ReadBit(); // seq_force_integer_mv
            }

            if (orderHint)
            {
                r.SkipBits(3);
            }
        }

        r.SkipBits(3); // enable_superres, enable_cdef, enable_restoration

        // color_config() §5.5.2
        bool high = r.ReadFlag();
        int bitDepth = 8;
        if (profile == 2 && high)
        {
            bitDepth = r.ReadFlag() ? 12 : 10;
        }
        else if (high)
        {
            bitDepth = 10;
        }

        bool mono = profile != 1 && r.ReadFlag();
        ColorInfo color = ColorInfo.Unspecified;
        int primaries = 2, transfer = 2, matrix = 2;
        bool described = r.ReadFlag();
        if (described)
        {
            primaries = (int)r.ReadBits(8);
            transfer = (int)r.ReadBits(8);
            matrix = (int)r.ReadBits(8);
        }

        bool subX, subY, fullRange;
        int csp = 0;
        if (mono)
        {
            fullRange = r.ReadFlag();
            subX = subY = true;
        }
        else if (primaries == 1 && transfer == 13 && matrix == 0)
        {
            // BT.709 primaries, sRGB transfer, identity matrix: RGB signalled as 4:4:4 full range
            fullRange = true;
            subX = subY = false;
        }
        else
        {
            fullRange = r.ReadFlag();
            if (profile == 0)
            {
                subX = subY = true;
            }
            else if (profile == 1)
            {
                subX = subY = false;
            }
            else if (bitDepth == 12)
            {
                subX = r.ReadFlag();
                subY = subX && r.ReadFlag();
            }
            else
            {
                subX = true;
                subY = false;
            }

            if (subX && subY)
            {
                csp = (int)r.ReadBits(2);
            }
        }

        if (!mono)
        {
            r.ReadBit(); // separate_uv_delta_q
        }

        if (r.Overrun)
        {
            return false;
        }

        color = new ColorInfo(described, primaries, transfer, matrix, fullRange);
        header = new SequenceHeader
        {
            Profile = profile,
            StillPicture = still,
            ReducedStillPictureHeader = reduced,
            LevelIdx = level,
            Tier = tier,
            BitDepth = bitDepth,
            MonoChrome = mono,
            SubsamplingX = subX,
            SubsamplingY = subY,
            ChromaSamplePosition = csp,
            Color = color,
            MaxFrameWidth = maxW,
            MaxFrameHeight = maxH,
            HasTimingInfo = timing,
            NumUnitsInDisplayTick = units,
            TimeScale = scale,
            EqualPictureInterval = equal,
            NumTicksPerPicture = ticks,
        };
        return true;
    }

    /// <summary>Finds the sequence header OBU in low-overhead data and parses it.</summary>
    /// <param name="obus">Low-overhead OBUs, for example a first sample or an <c>av1C</c> configOBUs field.</param>
    /// <param name="header">The result when true.</param>
    /// <param name="obu">The whole OBU (header, size field and payload) that held it, when found.</param>
    /// <returns>False when no sequence header is found or it does not parse.</returns>
    public static bool TryFind(ReadOnlySpan<byte> obus, out SequenceHeader header, out ReadOnlySpan<byte> obu)
    {
        header = default;
        obu = default;
        int pos = 0;
        while (pos < obus.Length)
        {
            if (!ObuHeader.TryParse(obus[pos..], out ObuHeader h))
            {
                return false;
            }

            if (h.Type == ObuType.SequenceHeader)
            {
                obu = obus.Slice(pos, h.TotalSize);
                return TryParse(obus.Slice(pos + h.PayloadOffset, h.PayloadSize), out header);
            }

            pos += h.TotalSize;
        }

        return false;
    }
}

/// <summary>
/// An AV1CodecConfigurationRecord, the content of an <c>av1C</c> box (AV1 Codec ISO Media File Format Binding §2.3).
/// </summary>
public sealed class Av1CodecConfigurationRecord
{
    /// <summary>seq_profile.</summary>
    public int SeqProfile { get; init; }

    /// <summary>seq_level_idx_0.</summary>
    public int SeqLevelIdx0 { get; init; }

    /// <summary>seq_tier_0: true for High.</summary>
    public bool SeqTier0 { get; init; }

    /// <summary>high_bitdepth.</summary>
    public bool HighBitDepth { get; init; }

    /// <summary>twelve_bit.</summary>
    public bool TwelveBit { get; init; }

    /// <summary>monochrome.</summary>
    public bool Monochrome { get; init; }

    /// <summary>chroma_subsampling_x.</summary>
    public bool ChromaSubsamplingX { get; init; }

    /// <summary>chroma_subsampling_y.</summary>
    public bool ChromaSubsamplingY { get; init; }

    /// <summary>chroma_sample_position.</summary>
    public int ChromaSamplePosition { get; init; }

    /// <summary>initial_presentation_delay_minus_one when present, otherwise -1.</summary>
    public int InitialPresentationDelayMinusOne { get; init; } = -1;

    /// <summary>The configOBUs: low-overhead OBUs, normally the sequence header.</summary>
    public ReadOnlyMemory<byte> ConfigObus { get; init; }

    /// <summary>Bit depth implied by high_bitdepth and twelve_bit.</summary>
    public int BitDepth => TwelveBit ? 12 : HighBitDepth ? 10 : 8;

    /// <summary>Parses the record.</summary>
    /// <param name="data">The box content after its header.</param>
    /// <param name="record">The result when true.</param>
    /// <returns>False when shorter than four bytes, or the marker or version is not 1.</returns>
    public static bool TryParse(ReadOnlyMemory<byte> data, out Av1CodecConfigurationRecord? record)
    {
        record = null;
        ReadOnlySpan<byte> s = data.Span;
        if (s.Length < 4 || s[0] != 0x81)
        {
            return false;
        }

        record = new Av1CodecConfigurationRecord
        {
            SeqProfile = s[1] >> 5,
            SeqLevelIdx0 = s[1] & 0x1f,
            SeqTier0 = (s[2] & 0x80) != 0,
            HighBitDepth = (s[2] & 0x40) != 0,
            TwelveBit = (s[2] & 0x20) != 0,
            Monochrome = (s[2] & 0x10) != 0,
            ChromaSubsamplingX = (s[2] & 8) != 0,
            ChromaSubsamplingY = (s[2] & 4) != 0,
            ChromaSamplePosition = s[2] & 3,
            InitialPresentationDelayMinusOne = (s[3] & 0x10) != 0 ? s[3] & 0xf : -1,
            ConfigObus = data[4..],
        };
        return true;
    }

    /// <summary>Builds the record from a parsed sequence header and its OBU.</summary>
    /// <param name="header">The sequence header.</param>
    /// <param name="sequenceHeaderObu">The complete sequence header OBU (with size field), stored as configOBUs.</param>
    /// <returns>The record.</returns>
    public static Av1CodecConfigurationRecord FromSequenceHeader(in SequenceHeader header, ReadOnlyMemory<byte> sequenceHeaderObu) => new()
    {
        SeqProfile = header.Profile,
        SeqLevelIdx0 = header.LevelIdx,
        SeqTier0 = header.Tier,
        HighBitDepth = header.BitDepth > 8,
        TwelveBit = header.BitDepth == 12,
        Monochrome = header.MonoChrome,
        ChromaSubsamplingX = header.SubsamplingX,
        ChromaSubsamplingY = header.SubsamplingY,
        ChromaSamplePosition = header.ChromaSamplePosition,
        ConfigObus = sequenceHeaderObu,
    };

    /// <summary>The size of the record as <see cref="Write"/> produces it.</summary>
    /// <returns>Bytes.</returns>
    public int GetSize() => 4 + ConfigObus.Length;

    /// <summary>Writes the record.</summary>
    /// <param name="writer">Receives <see cref="GetSize"/> bytes.</param>
    public void Write(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> w = writer.GetSpan(GetSize());
        w[0] = 0x81;
        w[1] = (byte)((SeqProfile << 5) | (SeqLevelIdx0 & 0x1f));
        w[2] = (byte)((SeqTier0 ? 0x80 : 0) | (HighBitDepth ? 0x40 : 0) | (TwelveBit ? 0x20 : 0) | (Monochrome ? 0x10 : 0)
            | (ChromaSubsamplingX ? 8 : 0) | (ChromaSubsamplingY ? 4 : 0) | (ChromaSamplePosition & 3));
        w[3] = (byte)(InitialPresentationDelayMinusOne >= 0 ? 0x10 | (InitialPresentationDelayMinusOne & 0xf) : 0);
        ConfigObus.Span.CopyTo(w[4..]);
        writer.Advance(GetSize());
    }

    /// <summary>Describes the stream from the stored sequence header, falling back to the record's own fields.</summary>
    /// <param name="parameters">The summary when true.</param>
    /// <returns>Always true for a parsed record.</returns>
    public bool TryDescribe(out CodecParameters? parameters)
    {
        SequenceHeader.TryFind(ConfigObus.Span, out SequenceHeader h, out _);
        bool found = h.MaxFrameWidth != 0;
        ChromaFormat chroma = Monochrome ? ChromaFormat.Monochrome : !ChromaSubsamplingX && !ChromaSubsamplingY ? ChromaFormat.Yuv444 : ChromaSubsamplingX && !ChromaSubsamplingY ? ChromaFormat.Yuv422 : ChromaFormat.Yuv420;
        parameters = new CodecParameters
        {
            Codec = CodecId.Av1,
            Rfc6381 = string.Create(CultureInfo.InvariantCulture, $"av01.{SeqProfile}.{SeqLevelIdx0:D2}{(SeqTier0 ? 'H' : 'M')}.{BitDepth:D2}"),
            Profile = SeqProfile,
            Level = SeqLevelIdx0,
            ProfileName = SeqProfile switch { 0 => "Main", 1 => "High", 2 => "Professional", _ => null },
            Width = found ? h.MaxFrameWidth : 0,
            Height = found ? h.MaxFrameHeight : 0,
            BitDepth = BitDepth,
            Chroma = chroma,
            Color = found ? h.Color : ColorInfo.Unspecified,
            FrameRateNumerator = found && h.EqualPictureInterval && h.NumUnitsInDisplayTick != 0 ? (int)Math.Min(h.TimeScale, int.MaxValue) : 0,
            FrameRateDenominator = found && h.EqualPictureInterval && h.NumUnitsInDisplayTick != 0 ? (int)Math.Min((long)h.NumUnitsInDisplayTick * h.NumTicksPerPicture, int.MaxValue) : 0,
        };
        return true;
    }
}
