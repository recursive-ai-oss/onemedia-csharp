namespace RecursiveAI.OneMedia.Avi;

/// <summary>
/// Maps the FourCC of a video stream (<c>biCompression</c>, or the <c>strh</c> handler when that is empty) and the <c>wFormatTag</c>
/// of an audio stream onto the neutral <see cref="CodecId"/>. Names follow the common codec names used by media tools so that
/// results can be compared; codecs without a neutral id get a lower-case name of their own.
/// </summary>
/// <remarks>
/// The video list covers the FourCC codes that are in common use; the audio list covers the registered <c>wFormatTag</c> values that
/// appear in AVI files. Every name was checked against what ffprobe reports for files carrying the code. Codes seen only in
/// broken files are not listed.
/// </remarks>
public static class AviCodecMap
{
    /// <summary>Returns the codec for a video FourCC; unknown codes give <see cref="CodecId.Unknown"/>.</summary>
    /// <param name="fourcc">The four characters; case is ignored for codes that are conventionally written either way.</param>
    public static CodecId Video(string fourcc)
    {
        if (fourcc.Length != 4)
        {
            return CodecId.Unknown;
        }

        string u = fourcc.ToUpperInvariant();
        switch (u)
        {
            case "FMP4" or "DIVX" or "DX50" or "XVID" or "MP4V" or "DVX1" or "3IV2" or "3IVX" or "RMP4" or "SEDG" or "BLZ0" or "MP4S" or "M4S2" or "XVIX" or "UMP4" or "WV1F" or "SMP4" or "HDX4" or "GEOX" or "FVFW" or "DX5O" or "DXGM" or "EM4A" or "EPHV" or "FMD4" or "GEOV" or "NDIG" or "DIGI" or "SIPP" or "DM4V" or "COL1":
                return u == "COL1" ? new CodecId("msmpeg4v3") : new CodecId("mpeg4");
            case "MPG4" or "MP41" or "DIV1":
                return new CodecId("msmpeg4v1");
            case "MP42" or "DIV2":
                return new CodecId("msmpeg4v2");
            case "MP43" or "DIV3" or "DIV4" or "DIV5" or "DIV6" or "AP41" or "COL0" or "MPG3" or "DVX3":
                return new CodecId("msmpeg4v3");
            case "MJPG" or "AVRN" or "AVDJ" or "JPEG" or "IJPG" or "AVI1" or "AVI2" or "DMB1" or "MJPA" or "MJPB" or "LJPG" or "JFIF" or "MJ2C":
                return u == "MJ2C" ? new CodecId("jpeg2000") : CodecId.Mjpeg;
            case "H264" or "X264" or "AVC1" or "DAVC" or "VSSH" or "Q264" or "V264" or "GAVC" or "PAVC" or "SVC1" or "SAVC" or "UMSV" or "AVC2":
                return CodecId.H264;
            case "HEVC" or "HEV1" or "HVC1" or "H265" or "X265":
                return CodecId.Hevc;
            case "DVSD" or "DVHD" or "DVSL" or "DV25" or "DV50" or "DVCP" or "DVCS" or "CDVC" or "DVH1" or "DVH2" or "DVH3" or "DVH4" or "DVH5" or "DVH6" or "DVHQ" or "DVP " or "DVPP" or "DVC " or "DV1N" or "DV1P" or "DVHP":
                return new CodecId("dvvideo");
            case "HFYU":
                return new CodecId("huffyuv");
            case "FFVH":
                return new CodecId("ffvhuff");
            case "FFV1":
                return new CodecId("ffv1");
            case "VP80":
                return CodecId.Vp8;
            case "VP90":
                return CodecId.Vp9;
            case "AV01":
                return CodecId.Av1;
            case "THEO":
                return new CodecId("theora");
            case "WMV1":
                return new CodecId("wmv1");
            case "WMV2":
                return new CodecId("wmv2");
            case "WMV3":
                return new CodecId("wmv3");
            case "WVC1" or "WMVA" or "VC-1":
                return new CodecId("vc1");
            case "MPEG" or "MPG1" or "MP1V" or "PIM1":
                return new CodecId("mpeg1video");
            case "MPG2" or "MP2V" or "PIM2" or "MPGV" or "M2V1" or "HDV1" or "HDV2" or "HDV3" or "HDV4" or "HDV5" or "HDV6" or "HDV7" or "HDV8" or "HDV9":
                return CodecId.Mpeg2Video;
            case "CVID":
                return new CodecId("cinepak");
            case "IV31" or "IV32":
                return new CodecId("indeo3");
            case "IV41" or "IV42":
                return new CodecId("indeo4");
            case "IV50":
                return new CodecId("indeo5");
            case "MSVC" or "CRAM" or "WHAM":
                return new CodecId("msvideo1");
            case "MRLE" or "RLE ":
                return new CodecId("msrle");
            case "TSCC":
                return new CodecId("tscc");
            case "ZLIB":
                return new CodecId("zlib");
            case "MSZH":
                return new CodecId("mszh");
            case "LAGS":
                return new CodecId("lagarith");
            case "ULRG" or "ULRA" or "ULY0" or "ULY2" or "ULH0" or "ULH2":
                return new CodecId("utvideo");
            case "FPS1":
                return new CodecId("fraps");
            case "SVQ1":
                return new CodecId("svq1");
            case "SVQ3":
                return new CodecId("svq3");
            case "FLV1":
                return new CodecId("flv1");
            case "H263" or "S263" or "U263" or "VX1K" or "I263":
                return new CodecId("h263");
            case "CYUV":
                return new CodecId("cyuv");
            case "YV12" or "I420" or "IYUV" or "YUY2" or "YUYV" or "UYVY" or "YVYU" or "Y41P" or "Y800" or "Y8  " or "NV12" or "NV21" or "YV16" or "YV24" or "P010" or "V210" or "2VUY" or "YUV2" or "444P" or "UYNV" or "HDYC" or "Y422" or "V422":
                return new CodecId("rawvideo");
            case "CFHD":
                return new CodecId("cfhd");
            case "APCH" or "APCN" or "APCS" or "APCO" or "AP4H":
                return new CodecId("prores");
            default:
                return CodecId.Unknown;
        }
    }

    /// <summary>
    /// Returns the codec for an uncompressed bitmap (<c>biCompression</c> of 0 or 3): raw video in the stated pixel layout.
    /// </summary>
    public static CodecId RawBitmap() => new("rawvideo");

    /// <summary>Returns the codec for an audio <c>wFormatTag</c>; unknown tags give <see cref="CodecId.Unknown"/>.</summary>
    /// <param name="formatTag">The format tag (or the sub-format code of the extensible form).</param>
    public static CodecId Audio(int formatTag) => formatTag switch
    {
        0x0001 or 0x0003 or 0x0006 or 0x0007 => CodecId.Pcm,
        0x0002 => new CodecId("adpcm_ms"),
        0x0011 => new CodecId("adpcm_ima_wav"),
        0x0016 => new CodecId("adpcm_g722"),
        0x0045 => new CodecId("adpcm_g726"),
        0x0031 or 0x0032 => new CodecId("gsm_ms"),
        0x0050 => new CodecId("mp2"),
        0x0055 => CodecId.Mp3,
        0x00FF or 0x1600 or 0x1601 or 0x1602 or 0x706D => CodecId.Aac,
        0x0160 => new CodecId("wmav1"),
        0x0161 => new CodecId("wmav2"),
        0x0162 => new CodecId("wmapro"),
        0x0163 => new CodecId("wmalossless"),
        0x2000 => CodecId.Ac3,
        0x2001 => new CodecId("dts"),
        0x566F or 0x674F or 0x6750 or 0x6751 or 0x676F or 0x6770 or 0x6771 => CodecId.Vorbis,
        0xF1AC => CodecId.Flac,
        0x0130 => new CodecId("sipr"),
        0x0401 => new CodecId("imc"),
        0x0402 => new CodecId("iac"),
        0x1100 => new CodecId("lpc"),
        0x2002 => new CodecId("ra_288"),
        0x6C61 => new CodecId("alac"),
        _ => CodecId.Unknown,
    };
}
