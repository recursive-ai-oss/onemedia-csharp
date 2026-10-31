namespace RecursiveAI.OneMedia.Codecs.Mpeg2Video;

/// <summary>Start code values of ITU-T H.262 Table 6-1.</summary>
public static class Mpeg2StartCode
{
    /// <summary>picture_start_code.</summary>
    public const int Picture = 0x00;

    /// <summary>user_data_start_code.</summary>
    public const int UserData = 0xb2;

    /// <summary>sequence_header_code.</summary>
    public const int SequenceHeader = 0xb3;

    /// <summary>extension_start_code.</summary>
    public const int Extension = 0xb5;

    /// <summary>sequence_end_code.</summary>
    public const int SequenceEnd = 0xb7;

    /// <summary>group_start_code.</summary>
    public const int GroupOfPictures = 0xb8;
}

/// <summary>Walks the start codes of an MPEG-2 video elementary stream or sample (ITU-T H.262 §6.2).</summary>
public ref struct Mpeg2StartCodeReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    /// <summary>Creates a reader.</summary>
    /// <param name="data">The stream bytes.</param>
    public Mpeg2StartCodeReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    /// <summary>Finds the next start code.</summary>
    /// <param name="code">The byte after 00 00 01.</param>
    /// <param name="body">The bytes after the start code up to the next start code (or the end).</param>
    /// <param name="offset">The offset of the 00 00 01 prefix within the data.</param>
    /// <returns>False when no further start code exists.</returns>
    public bool TryRead(out int code, out ReadOnlySpan<byte> body, out int offset)
    {
        ReadOnlySpan<byte> pattern = [0, 0, 1];
        int at = _data[_pos..].IndexOf(pattern);
        if (at < 0 || _pos + at + 3 >= _data.Length)
        {
            code = 0;
            body = default;
            offset = 0;
            return false;
        }

        int start = _pos + at;
        code = _data[start + 3];
        int bodyStart = start + 4;
        int next = _data[bodyStart..].IndexOf(pattern);
        int end = next < 0 ? _data.Length : bodyStart + next;
        body = _data[bodyStart..end];
        offset = start;
        _pos = end;
        return true;
    }
}

/// <summary>A sequence header with its extension, as far as stream description needs (ITU-T H.262 §6.2.2.1, §6.2.2.3).</summary>
public readonly record struct Mpeg2SequenceHeader
{
    /// <summary>Width in luma samples, with horizontal_size_extension applied when the extension is present.</summary>
    public int Width { get; init; }

    /// <summary>Height in luma samples.</summary>
    public int Height { get; init; }

    /// <summary>aspect_ratio_information: 1 square samples, 2 4:3, 3 16:9, 4 2.21:1.</summary>
    public int AspectRatioInformation { get; init; }

    /// <summary>frame_rate_code (Table 6-4), 1 to 8.</summary>
    public int FrameRateCode { get; init; }

    /// <summary>Frame rate numerator after frame_rate_extension, 0 for reserved codes.</summary>
    public int FrameRateNumerator { get; init; }

    /// <summary>Frame rate denominator.</summary>
    public int FrameRateDenominator { get; init; }

    /// <summary>bit_rate in units of 400 bits per second, extension applied; 0x3FFFF means variable.</summary>
    public int BitRateValue { get; init; }

    /// <summary>vbv_buffer_size in units of 16 kbit, extension applied.</summary>
    public int VbvBufferSize { get; init; }

    /// <summary>True when a sequence extension followed the header (MPEG-2 rather than MPEG-1 video).</summary>
    public bool HasExtension { get; init; }

    /// <summary>profile_and_level_indication from the extension, 0 without one.</summary>
    public int ProfileAndLevel { get; init; }

    /// <summary>Profile number (1 High, 2 Spatial, 3 SNR, 4 Main, 5 Simple), 0 for MPEG-1 or an escape value.</summary>
    public int Profile => HasExtension ? (ProfileAndLevel >> 4) & 7 : 0;

    /// <summary>Level code (4 High, 6 High 1440, 8 Main, 10 Low), 0 for MPEG-1.</summary>
    public int Level => HasExtension ? ProfileAndLevel & 0xf : 0;

    /// <summary>progressive_sequence.</summary>
    public bool ProgressiveSequence { get; init; }

    /// <summary>chroma_format: 1 4:2:0, 2 4:2:2, 3 4:4:4; 1 without an extension.</summary>
    public int ChromaFormat { get; init; }

    /// <summary>low_delay: no B-pictures, so no reordering.</summary>
    public bool LowDelay { get; init; }

    /// <summary>The profile as ffprobe prints it, for example <c>Main</c>; <c>Unknown</c> otherwise.</summary>
    public string ProfileName => Profile switch { 1 => "High", 2 => "Spatially Scalable", 3 => "SNR Scalable", 4 => "Main", 5 => "Simple", _ => ProfileAndLevel == 0x82 ? "4:2:2" : "Unknown" };

    /// <summary>Parses the body of a sequence_header (after its start code), and the sequence extension that follows when given.</summary>
    /// <param name="header">The bytes after 00 00 01 B3 up to the next start code.</param>
    /// <param name="extension">The bytes after 00 00 01 B5 of the sequence extension, or empty when none follows.</param>
    /// <param name="result">The result when true.</param>
    /// <returns>False when the header is truncated, has a zero dimension or a missing marker bit.</returns>
    public static bool TryParse(ReadOnlySpan<byte> header, ReadOnlySpan<byte> extension, out Mpeg2SequenceHeader result)
    {
        result = default;
        BitReader r = new(header);
        int w = (int)r.ReadBits(12);
        int h = (int)r.ReadBits(12);
        int aspect = (int)r.ReadBits(4);
        int rate = (int)r.ReadBits(4);
        int bitRate = (int)r.ReadBits(18);
        bool marker = r.ReadFlag();
        int vbv = (int)r.ReadBits(10);
        if (r.Overrun || !marker || w == 0 || h == 0)
        {
            return false;
        }

        int num = 0, den = 0;
        if (rate is >= 1 and <= 8)
        {
            (num, den) = rate switch
            {
                1 => (24000, 1001),
                2 => (24, 1),
                3 => (25, 1),
                4 => (30000, 1001),
                5 => (30, 1),
                6 => (50, 1),
                7 => (60000, 1001),
                _ => (60, 1),
            };
        }

        bool hasExt = false;
        int pl = 0, chroma = 1;
        bool progressive = false, lowDelay = false;
        if (extension.Length >= 6)
        {
            BitReader e = new(extension);
            if (e.ReadBits(4) == 1)
            {
                pl = (int)e.ReadBits(8);
                progressive = e.ReadFlag();
                chroma = (int)e.ReadBits(2);
                int wExt = (int)e.ReadBits(2);
                int hExt = (int)e.ReadBits(2);
                int brExt = (int)e.ReadBits(12);
                e.ReadBit();
                int vbvExt = (int)e.ReadBits(8);
                lowDelay = e.ReadFlag();
                int fn = (int)e.ReadBits(2);
                int fd = (int)e.ReadBits(5);
                if (!e.Overrun)
                {
                    hasExt = true;
                    w |= wExt << 12;
                    h |= hExt << 12;
                    bitRate |= brExt << 18;
                    vbv |= vbvExt << 10;
                    if (num != 0)
                    {
                        // §6.3.3: the real rate is frame_rate_value * (n + 1) / (d + 1)
                        num *= fn + 1;
                        den *= fd + 1;
                    }
                }
            }
        }

        result = new Mpeg2SequenceHeader
        {
            Width = w,
            Height = h,
            AspectRatioInformation = aspect,
            FrameRateCode = rate,
            FrameRateNumerator = num,
            FrameRateDenominator = den,
            BitRateValue = bitRate,
            VbvBufferSize = vbv,
            HasExtension = hasExt,
            ProfileAndLevel = pl,
            ProgressiveSequence = progressive,
            ChromaFormat = chroma,
            LowDelay = lowDelay,
        };
        return true;
    }

    /// <summary>Finds the first sequence header (and its extension) in a stream or sample and parses it.</summary>
    /// <param name="data">Elementary stream bytes.</param>
    /// <param name="result">The result when true.</param>
    /// <returns>False when no valid sequence header is found.</returns>
    public static bool TryFind(ReadOnlySpan<byte> data, out Mpeg2SequenceHeader result)
    {
        result = default;
        Mpeg2StartCodeReader reader = new(data);
        while (reader.TryRead(out int code, out ReadOnlySpan<byte> body, out _))
        {
            if (code != Mpeg2StartCode.SequenceHeader)
            {
                continue;
            }

            // Skip optional quantiser matrices to reach the extension start code, which the reader returns as the next unit.
            ReadOnlySpan<byte> ext = default;
            Mpeg2StartCodeReader after = reader;
            if (after.TryRead(out int next, out ReadOnlySpan<byte> nextBody, out _) && next == Mpeg2StartCode.Extension)
            {
                ext = nextBody;
            }

            // The body contains the header and any quantiser matrices; the fixed part is the first 8 bytes.
            return TryParse(body, ext, out result);
        }

        return false;
    }

    /// <summary>The coded display aspect ratio as numerator and denominator, from aspect_ratio_information and the dimensions.</summary>
    /// <param name="num">Numerator.</param>
    /// <param name="den">Denominator.</param>
    /// <returns>False for reserved codes.</returns>
    public bool TryGetDisplayAspectRatio(out int num, out int den)
    {
        switch (AspectRatioInformation)
        {
            case 1: num = Width; den = Height; return true;
            case 2: num = 4; den = 3; return true;
            case 3: num = 16; den = 9; return true;
            case 4: num = 221; den = 100; return true;
            default: num = den = 0; return false;
        }
    }
}

/// <summary>A picture header (ITU-T H.262 §6.2.3).</summary>
/// <param name="TemporalReference">temporal_reference (10 bits).</param>
/// <param name="PictureCodingType">picture_coding_type: 1 I, 2 P, 3 B, 4 D (MPEG-1 only).</param>
/// <param name="VbvDelay">vbv_delay (16 bits).</param>
public readonly record struct Mpeg2PictureHeader(int TemporalReference, int PictureCodingType, int VbvDelay)
{
    /// <summary>Parses the body of a picture_header (after its start code).</summary>
    /// <param name="body">The bytes after 00 00 01 00.</param>
    /// <param name="header">The result when true.</param>
    /// <returns>False when truncated or the coding type is 0 or reserved.</returns>
    public static bool TryParse(ReadOnlySpan<byte> body, out Mpeg2PictureHeader header)
    {
        header = default;
        BitReader r = new(body);
        int tr = (int)r.ReadBits(10);
        int type = (int)r.ReadBits(3);
        int vbv = (int)r.ReadBits(16);
        if (r.Overrun || type is 0 or > 4)
        {
            return false;
        }

        header = new Mpeg2PictureHeader(tr, type, vbv);
        return true;
    }
}

/// <summary>A group_of_pictures_header (ITU-T H.262 §6.2.2.6).</summary>
/// <param name="DropFrame">drop_frame_flag.</param>
/// <param name="Hours">Time code hours.</param>
/// <param name="Minutes">Time code minutes.</param>
/// <param name="Seconds">Time code seconds.</param>
/// <param name="Pictures">Time code picture count within the second.</param>
/// <param name="ClosedGop">closed_gop.</param>
/// <param name="BrokenLink">broken_link.</param>
public readonly record struct Mpeg2GroupOfPicturesHeader(bool DropFrame, int Hours, int Minutes, int Seconds, int Pictures, bool ClosedGop, bool BrokenLink)
{
    /// <summary>Parses the body of a group header (after its start code).</summary>
    /// <param name="body">The bytes after 00 00 01 B8.</param>
    /// <param name="header">The result when true.</param>
    /// <returns>False when truncated.</returns>
    public static bool TryParse(ReadOnlySpan<byte> body, out Mpeg2GroupOfPicturesHeader header)
    {
        header = default;
        BitReader r = new(body);
        bool drop = r.ReadFlag();
        int hh = (int)r.ReadBits(5);
        int mm = (int)r.ReadBits(6);
        r.ReadBit();
        int ss = (int)r.ReadBits(6);
        int pp = (int)r.ReadBits(6);
        bool closed = r.ReadFlag();
        bool broken = r.ReadFlag();
        if (r.Overrun)
        {
            return false;
        }

        header = new Mpeg2GroupOfPicturesHeader(drop, hh, mm, ss, pp, closed, broken);
        return true;
    }
}
