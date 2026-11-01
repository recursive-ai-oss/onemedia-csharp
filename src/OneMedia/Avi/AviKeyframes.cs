namespace RecursiveAI.OneMedia.Avi;

/// <summary>
/// Key-frame detection for chunks that no index describes (index-less or damaged files). Only the picture header at the start of the
/// chunk is read; no sample is decoded. Codecs whose frames are all intra, and codecs not listed, report every chunk as a key frame.
/// </summary>
internal enum KeyframeGuess
{
    /// <summary>Every chunk is a key frame (intra-only codecs and codecs this code does not inspect).</summary>
    All,

    /// <summary>MPEG-4 Part 2: the vop_coding_type of the first VOP (ISO/IEC 14496-2 §6.3.5).</summary>
    Mpeg4Part2,

    /// <summary>H.264: an IDR slice NAL unit (ITU-T H.264 §7.4.1, nal_unit_type 5) is present.</summary>
    H264,

    /// <summary>MPEG-1/2 video: picture_coding_type 1 (ISO/IEC 13818-2 §6.3.9).</summary>
    Mpeg12,
}

internal static class AviKeyframes
{
    /// <summary>How many leading bytes of a chunk are examined.</summary>
    public const int HeadBytes = 16 * 1024;

    public static KeyframeGuess For(CodecId codec) => codec.Name switch
    {
        "mpeg4" => KeyframeGuess.Mpeg4Part2,
        "h264" => KeyframeGuess.H264,
        "mpeg1video" or "mpeg2video" => KeyframeGuess.Mpeg12,
        _ => KeyframeGuess.All,
    };

    /// <summary>Whether the chunk starting with <paramref name="head"/> holds a key frame.</summary>
    public static bool IsKey(KeyframeGuess guess, ReadOnlySpan<byte> head)
    {
        switch (guess)
        {
            case KeyframeGuess.Mpeg4Part2:
                {
                    // VOP start code 00 00 01 B6, then vop_coding_type in the top two bits: 0 = I, 1 = P, 2 = B, 3 = S. A chunk with no VOP start
                    // code cannot be judged and counts as a key frame, like every chunk of a codec that is not inspected.
                    int at = IndexOfStartCode(head, 0xB6, 0);
                    return at < 0 || at + 4 >= head.Length || (head[at + 4] >> 6) == 0;
                }

            case KeyframeGuess.H264:
                {
                    bool sawSlice = false;
                    int from = 0;
                    while (true)
                    {
                        int at = IndexOfStartCode(head, -1, from);
                        if (at < 0 || at + 3 >= head.Length)
                        {
                            break;
                        }

                        int type = head[at + 3] & 0x1F;
                        if (type == 5)
                        {
                            return true;
                        }

                        sawSlice |= type is >= 1 and <= 4;
                        from = at + 3;
                    }

                    // Data that is not Annex B (length-prefixed NAL units) cannot be judged here and counts as a key frame.
                    return !sawSlice && IndexOfStartCode(head, -1, 0) < 0;
                }

            case KeyframeGuess.Mpeg12:
                {
                    // Picture start code 00 00 01 00, 10-bit temporal_reference, 3-bit picture_coding_type: 1 = I.
                    int at = IndexOfStartCode(head, 0x00, 0);
                    return at < 0 || at + 5 >= head.Length || ((head[at + 5] >> 3) & 7) == 1;
                }

            default:
                return true;
        }
    }

    // Finds 00 00 01 <code> (any code when code is -1) at or after from; returns the index of the first 00.
    private static int IndexOfStartCode(ReadOnlySpan<byte> data, int code, int from)
    {
        for (int i = from; i + 3 < data.Length; i++)
        {
            if (data[i + 2] > 1)
            {
                i += 2;
                continue;
            }

            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && (code < 0 || data[i + 3] == code))
            {
                return i;
            }
        }

        return -1;
    }
}
