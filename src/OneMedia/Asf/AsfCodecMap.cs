using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Asf;

/// <summary>Maps the codec identifiers ASF streams carry (WAVEFORMATEX format tags, BITMAPINFOHEADER compression codes) to neutral codec ids, and builds tracks.</summary>
internal static class AsfCodecMap
{
    /// <summary>The neutral codec for an audio format tag, or <see cref="CodecId.Unknown"/>.</summary>
    public static CodecId AudioCodec(int formatTag) => formatTag switch
    {
        0x0001 or 0x0003 => CodecId.Pcm,
        0x0160 => CodecId.WmaV1,
        0x0161 => CodecId.WmaV2,
        0x0162 => CodecId.WmaPro,
        0x0163 => CodecId.WmaLossless,
        0x000A or 0x000B => CodecId.WmaVoice,
        0x0050 => CodecId.Mp2,
        0x0055 => CodecId.Mp3,
        0x2000 => CodecId.Ac3,
        0x00FF or 0x1600 or 0xA106 => CodecId.Aac,
        0x7A21 => CodecId.AmrNb,
        0x7A22 => CodecId.AmrWb,
        _ => CodecId.Unknown,
    };

    /// <summary>The neutral codec for a video compression code (a FOURCC, compared without regard to case), or <see cref="CodecId.Unknown"/>.</summary>
    public static CodecId VideoCodec(string fourcc) => fourcc.ToUpperInvariant() switch
    {
        "WMV1" => CodecId.Wmv1,
        "WMV2" => CodecId.Wmv2,
        "WMV3" => CodecId.Wmv3,
        "WVC1" or "WMVA" => CodecId.Vc1,
        "MPG4" => new CodecId("msmpeg4v1"),
        "MP42" or "DIV2" => new CodecId("msmpeg4v2"),
        "MP43" or "DIV3" or "DIV4" or "DIV5" or "DIV6" or "AP41" or "COL1" => new CodecId("msmpeg4v3"),
        "MP4S" or "M4S2" or "MP4V" or "FMP4" or "XVID" or "DIVX" or "DX50" => CodecId.Mpeg4,
        "H264" or "AVC1" or "X264" => CodecId.H264,
        "MJPG" => CodecId.Mjpeg,
        "MPG2" or "MPEG" => CodecId.Mpeg2Video,
        _ => CodecId.Unknown,
    };

    /// <summary>Builds the track for a stream, or null for a stream kind that carries no packets this library delivers.</summary>
    public static MediaTrack BuildTrack(int index, AsfStreamEntry s, AsfHeader header, MediaTime duration, bool protectedFile)
    {
        ReadOnlySpan<byte> d = s.TypeSpecificData.Span;
        string? language = s.LanguageIndex < header.Languages.Count ? header.Languages[s.LanguageIndex] : null;
        string? name = s.Names.Count > 0 ? s.Names[0] : null;
        uint bitrate = s.DataBitrate != 0 ? s.DataBitrate : s.AverageBitrate;
        switch (s.Kind)
        {
            case AsfStreamKind.Audio:
            {
                // WAVEFORMATEX: format tag, channels, samples per second, average bytes per second, block align, bits per sample, extra-data size, extra data.
                int tag = d.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(d) : 0;
                int channels = d.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(d[2..]) : 0;
                int rate = d.Length >= 8 ? (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(d[4..]), int.MaxValue) : 0;
                int bits = d.Length >= 16 ? BinaryPrimitives.ReadUInt16LittleEndian(d[14..]) : 0;
                int extraLength = d.Length >= 18 ? BinaryPrimitives.ReadUInt16LittleEndian(d[16..]) : 0;
                ReadOnlyMemory<byte> extra = d.Length > 18 ? s.TypeSpecificData.Slice(18, Math.Min(extraLength, d.Length - 18)) : default;
                CodecId codec = AudioCodec(tag);
                long rateBits = d.Length >= 12 ? (long)BinaryPrimitives.ReadUInt32LittleEndian(d[8..]) * 8 : 0;
                return new MediaTrack
                {
                    Index = index,
                    Id = s.StreamNumber,
                    Kind = TrackKind.Audio,
                    Codec = codec,
                    CodecTag = "0x" + tag.ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
                    Configuration = new CodecConfiguration(extra, codec == CodecId.Aac ? "asc" : "waveformatex-extra"),
                    Timescale = 1000,
                    Duration = duration,
                    Language = language,
                    Name = name,
                    Audio = new AudioTrackInfo(rate, channels, bits),
                    BitRate = bitrate != 0 ? bitrate : rateBits,
                    IsProtected = protectedFile,
                };
            }

            case AsfStreamKind.Video:
            {
                // Video type-specific data: width, height (4 bytes each), a reserved byte, the size of the format data (2 bytes), then the BITMAPINFOHEADER.
                int width = 0, height = 0;
                string fourcc = string.Empty;
                ReadOnlyMemory<byte> extra = default;
                if (d.Length >= 11 + 40)
                {
                    ReadOnlySpan<byte> bmi = d[11..];
                    width = BinaryPrimitives.ReadInt32LittleEndian(bmi[4..]);
                    int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bmi[8..]);
                    height = signedHeight == int.MinValue ? 0 : Math.Abs(signedHeight); // a negative height means a top-down bitmap
                    fourcc = System.Text.Encoding.Latin1.GetString(bmi.Slice(16, 4)).TrimEnd('\0');
                    int headerSize = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bmi), (uint)bmi.Length);
                    if (headerSize >= 40 && headerSize < bmi.Length)
                    {
                        extra = s.TypeSpecificData.Slice(11 + headerSize, bmi.Length - headerSize);
                    }
                }
                else if (d.Length >= 8)
                {
                    width = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(d), int.MaxValue);
                    height = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(d[4..]), int.MaxValue);
                }

                int frameNum = 0, frameDen = 0;
                if (s.AverageTimePerFrame100ns > 0)
                {
                    long g = Gcd(10_000_000, s.AverageTimePerFrame100ns);
                    frameNum = (int)Math.Min(10_000_000 / g, int.MaxValue);
                    frameDen = (int)Math.Min(s.AverageTimePerFrame100ns / g, int.MaxValue);
                }

                return new MediaTrack
                {
                    Index = index,
                    Id = s.StreamNumber,
                    Kind = TrackKind.Video,
                    Codec = VideoCodec(fourcc),
                    CodecTag = fourcc.Length > 0 ? fourcc : null,
                    Configuration = new CodecConfiguration(extra, "bitmapinfo-extra"),
                    Timescale = 1000,
                    Duration = duration,
                    Language = language,
                    Name = name,
                    Video = new VideoTrackInfo(width, height, FrameRateNumerator: frameNum, FrameRateDenominator: frameDen),
                    BitRate = bitrate,
                    IsProtected = protectedFile,
                };
            }

            default:
                return new MediaTrack
                {
                    Index = index,
                    Id = s.StreamNumber,
                    Kind = s.Kind is AsfStreamKind.Command or AsfStreamKind.Binary or AsfStreamKind.FileTransfer ? TrackKind.Data : TrackKind.Unknown,
                    Timescale = 1000,
                    Duration = duration,
                    Language = language,
                    Name = name,
                    BitRate = bitrate,
                    IsProtected = protectedFile,
                };
        }
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
