namespace RecursiveAI.OneMedia.Matroska;

/// <summary>Maps Matroska CodecIDs (RFC 9559 §27 and the Matroska codec mapping document) onto the neutral <see cref="CodecId"/>.</summary>
internal static class MatroskaCodecs
{
    /// <summary>
    /// Describes a CodecID. <paramref name="configFormat"/> names how the track's CodecPrivate is laid out, using the same words
    /// <see cref="CodecConfiguration.Format"/> documents: <c>avcC</c>, <c>hvcC</c>, <c>vvcC</c>, <c>av1C</c>, <c>asc</c>, <c>OpusHead</c>,
    /// <c>xiph-lacing</c> (Vorbis and Theora headers), <c>flac-header</c>, <c>bitmapinfoheader</c> and <c>waveformatex</c>.
    /// </summary>
    public static void Map(string matroskaId, out CodecId codec, out string? configFormat, out string? tag)
    {
        configFormat = null;
        tag = matroskaId.Length == 0 ? null : matroskaId;
        codec = CodecId.Unknown;
        switch (matroskaId)
        {
            case "V_MPEG4/ISO/AVC": codec = CodecId.H264; configFormat = "avcC"; return;
            case "V_MPEGH/ISO/HEVC": codec = CodecId.Hevc; configFormat = "hvcC"; return;
            case "V_MPEGI/ISO/VVC": codec = CodecId.Vvc; configFormat = "vvcC"; return;
            case "V_AV1": codec = CodecId.Av1; configFormat = "av1C"; return;
            case "V_VP8": codec = CodecId.Vp8; return;
            case "V_VP9": codec = CodecId.Vp9; return;
            case "V_MPEG2": codec = CodecId.Mpeg2Video; return;
            case "V_MPEG1": codec = new CodecId("mpeg1video"); return;
            case "V_MJPEG": codec = CodecId.Mjpeg; return;
            case "V_THEORA": codec = new CodecId("theora"); configFormat = "xiph-lacing"; return;
            case "V_PRORES": codec = new CodecId("prores"); return;
            case "V_MPEG4/ISO/SP":
            case "V_MPEG4/ISO/ASP":
            case "V_MPEG4/ISO/AP": codec = new CodecId("mpeg4"); return;
            case "V_MPEG4/MS/V3": codec = new CodecId("msmpeg4v3"); return;
            case "V_MS/VFW/FOURCC": configFormat = "bitmapinfoheader"; return;
            case "A_OPUS": codec = CodecId.Opus; configFormat = "OpusHead"; return;
            case "A_VORBIS": codec = CodecId.Vorbis; configFormat = "xiph-lacing"; return;
            case "A_FLAC": codec = CodecId.Flac; configFormat = "flac-header"; return;
            case "A_AC3":
            case "A_AC3/BSID9":
            case "A_AC3/BSID10": codec = CodecId.Ac3; return;
            case "A_EAC3": codec = CodecId.Eac3; return;
            case "A_MPEG/L3": codec = CodecId.Mp3; return;
            case "A_MPEG/L2": codec = new CodecId("mp2"); return;
            case "A_MPEG/L1": codec = new CodecId("mp1"); return;
            case "A_TRUEHD": codec = new CodecId("truehd"); return;
            case "A_ALAC": codec = new CodecId("alac"); return;
            case "A_MS/ACM": configFormat = "waveformatex"; return;
            case "S_TEXT/UTF8":
            case "S_TEXT/ASCII": codec = CodecId.SubRip; return;
            case "S_TEXT/SSA":
            case "S_TEXT/ASS":
            case "S_SSA":
            case "S_ASS": codec = new CodecId("ass"); return;
            case "S_TEXT/WEBVTT": codec = CodecId.WebVtt; return;
            case "S_TEXT/USF": codec = new CodecId("usf"); return;
            case "S_VOBSUB": codec = new CodecId("vobsub"); return;
            case "S_HDMV/PGS": codec = new CodecId("pgs"); return;
            case "S_HDMV/TEXTST": codec = new CodecId("hdmv-textst"); return;
            case "S_DVBSUB": codec = new CodecId("dvbsub"); return;
            case "S_KATE": codec = new CodecId("kate"); return;
            default: break;
        }

        // Families with a variable tail.
        if (matroskaId.StartsWith("A_AAC", StringComparison.Ordinal))
        {
            // A_AAC, A_AAC/MPEG2/LC and A_AAC/MPEG4/LC (RFC 9559 codec mapping): CodecPrivate is the AudioSpecificConfig.
            codec = CodecId.Aac;
            configFormat = "asc";
        }
        else if (matroskaId.StartsWith("A_PCM/", StringComparison.Ordinal))
        {
            codec = CodecId.Pcm;
        }
        else if (matroskaId.StartsWith("A_DTS", StringComparison.Ordinal))
        {
            codec = new CodecId("dts");
        }
        else if (matroskaId.StartsWith("D_WEBVTT/", StringComparison.Ordinal))
        {
            codec = CodecId.WebVtt;
        }
        else if (matroskaId.StartsWith("V_MPEG4/ISO/", StringComparison.Ordinal))
        {
            codec = new CodecId("mpeg4");
        }
    }

    public static TrackKind KindOf(long trackType) => trackType switch
    {
        1 => TrackKind.Video,
        2 => TrackKind.Audio,
        17 => TrackKind.Subtitle,
        _ => TrackKind.Data,
    };
}

/// <summary>Helpers for the Xiph-laced CodecPrivate that Vorbis and Theora tracks carry (the Matroska codec mapping document).</summary>
public static class MatroskaCodecPrivate
{
    /// <summary>
    /// Splits a Xiph-laced CodecPrivate into its header packets: one byte holding the packet count minus one, then the sizes of all but
    /// the last packet in Ogg lacing (runs of 255 plus a remainder), then the packets. For Vorbis the three packets are the
    /// identification, comment and setup headers.
    /// </summary>
    /// <param name="codecPrivate">The CodecPrivate bytes.</param>
    /// <param name="packets">Slices of <paramref name="codecPrivate"/>, one per packet, in order.</param>
    /// <returns>False when the lacing runs past the data or the sizes do not fit it.</returns>
    public static bool TrySplitXiphLacing(ReadOnlyMemory<byte> codecPrivate, out ReadOnlyMemory<byte>[] packets)
    {
        packets = [];
        ReadOnlySpan<byte> data = codecPrivate.Span;
        if (data.IsEmpty)
        {
            return false;
        }

        int count = data[0] + 1;
        int pos = 1;
        int[] sizes = new int[count];
        long used = 0;
        for (int i = 0; i < count - 1; i++)
        {
            int size = 0;
            while (true)
            {
                if (pos >= data.Length)
                {
                    return false;
                }

                byte b = data[pos++];
                size += b;
                if (b != 255)
                {
                    break;
                }

                if (size > data.Length)
                {
                    return false;
                }
            }

            sizes[i] = size;
            used += size;
        }

        long remaining = data.Length - pos - used;
        if (remaining < 0)
        {
            return false;
        }

        sizes[count - 1] = (int)remaining;
        ReadOnlyMemory<byte>[] result = new ReadOnlyMemory<byte>[count];
        int at = pos;
        for (int i = 0; i < count; i++)
        {
            result[i] = codecPrivate.Slice(at, sizes[i]);
            at += sizes[i];
        }

        packets = result;
        return true;
    }
}
