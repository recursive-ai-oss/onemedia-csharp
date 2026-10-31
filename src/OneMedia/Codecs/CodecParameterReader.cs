using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Codecs.Ac3;
using RecursiveAI.OneMedia.Codecs.Amr;
using RecursiveAI.OneMedia.Codecs.Av1;
using RecursiveAI.OneMedia.Codecs.Flac;
using RecursiveAI.OneMedia.Codecs.H264;
using RecursiveAI.OneMedia.Codecs.Hevc;
using RecursiveAI.OneMedia.Codecs.Mpeg2Video;
using RecursiveAI.OneMedia.Codecs.MpegAudio;
using RecursiveAI.OneMedia.Codecs.Opus;
using RecursiveAI.OneMedia.Codecs.Vorbis;
using RecursiveAI.OneMedia.Codecs.Vpx;
using RecursiveAI.OneMedia.Codecs.Vvc;

namespace RecursiveAI.OneMedia.Codecs;

/// <summary>
/// Turns what a container stores for a track (its codec setup bytes, or a first sample) into a <see cref="CodecParameters"/>
/// summary. It is an explicit switch over the codecs this library knows; nothing is discovered by reflection.
/// </summary>
public static class CodecParameterReader
{
    /// <summary>Describes a track from its codec configuration.</summary>
    /// <param name="codec">The codec.</param>
    /// <param name="configuration">The setup bytes as the container stored them. <see cref="CodecConfiguration.Format"/> names the layout
    /// (<c>avcC</c>, <c>hvcC</c>, <c>vvcC</c>, <c>av1C</c>, <c>vpcC</c>, <c>esds</c>, <c>asc</c>, <c>dOps</c>, <c>dfLa</c>, <c>dac3</c>, <c>dec3</c>, <c>damr</c>,
    /// <c>OpusHead</c>, <c>CodecPrivate</c>); when it is null or <c>CodecPrivate</c> the layout is the codec's Matroska one.</param>
    /// <param name="parameters">The summary when true.</param>
    /// <returns>False when the codec has no configuration form here, the bytes are empty (except AMR, whose facts are fixed), or they do not parse. Never throws.</returns>
    public static bool TryDescribe(CodecId codec, CodecConfiguration configuration, out CodecParameters? parameters)
    {
        parameters = null;
        ReadOnlyMemory<byte> data = configuration.Data;
        if (data.IsEmpty)
        {
            // 3GPP TS 26.244 wants a damr box, but writers exist that leave it out (observed for AMR-WB in 3GP); the sample entry already
            // says which band, so the fixed AMR facts can still be given.
            if (codec.Name is "amr-nb" or "amr-wb")
            {
                parameters = DescribeAmr(codec.Name == "amr-wb");
                return true;
            }

            return false;
        }

        string format = configuration.Format ?? "CodecPrivate";
        switch (codec.Name)
        {
            case "h264":
                return AvcDecoderConfigurationRecord.TryParse(data, out AvcDecoderConfigurationRecord? avc) && avc!.TryDescribe(out parameters);
            case "hevc":
                return HevcDecoderConfigurationRecord.TryParse(data, out HevcDecoderConfigurationRecord? hevc) && hevc!.TryDescribe(out parameters);
            case "vvc":
                return VvcDecoderConfigurationRecord.TryParse(data, out VvcDecoderConfigurationRecord? vvc) && vvc!.TryDescribe(out parameters);
            case "av1":
                return Av1CodecConfigurationRecord.TryParse(data, out Av1CodecConfigurationRecord? av1) && av1!.TryDescribe(out parameters);
            case "vp9":
                if (string.Equals(format, "vpcC", StringComparison.OrdinalIgnoreCase))
                {
                    if (VpCodecConfigurationRecord.TryParse(data.Span, out VpCodecConfigurationRecord? vp))
                    {
                        parameters = vp!.Describe();
                        return true;
                    }

                    return false;
                }

                if (Vp9CodecPrivate.TryParse(data.Span, out int profile, out int level, out int depth, out int chroma))
                {
                    VpCodecConfigurationRecord rec = new() { Profile = Math.Max(profile, 0), Level = Math.Max(level, 0), BitDepth = Math.Max(depth, 0), ChromaSubsampling = Math.Max(chroma, 0) };
                    parameters = rec.Describe();
                    return true;
                }

                return false;
            case "aac":
                {
                    ReadOnlyMemory<byte> asc = data;
                    if (string.Equals(format, "esds", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!EsDescriptor.TryParse(data, out EsDescriptor? es) || es!.DecoderSpecificInfo.IsEmpty)
                        {
                            return false;
                        }

                        asc = es.DecoderSpecificInfo;
                    }

                    if (!AudioSpecificConfig.TryParse(asc.Span, out AudioSpecificConfig config))
                    {
                        return false;
                    }

                    parameters = config.Describe();
                    return true;
                }

            case "opus":
                {
                    OpusHead? head;
                    bool ok = string.Equals(format, "dOps", StringComparison.OrdinalIgnoreCase)
                        ? OpusHead.TryParseDOps(data, out head)
                        : OpusHead.TryParseOpusHead(data, out head);
                    parameters = ok ? head!.Describe() : null;
                    return ok;
                }

            case "vorbis":
                if (VorbisXiphLacing.TrySplit(data, out ReadOnlyMemory<byte> id, out _, out _) && VorbisIdentificationHeader.TryParse(id.Span, out VorbisIdentificationHeader vorbis))
                {
                    parameters = vorbis.Describe();
                    return true;
                }

                return false;
            case "flac":
                {
                    ReadOnlyMemory<byte> blocks;
                    bool ok = string.Equals(format, "dfLa", StringComparison.OrdinalIgnoreCase)
                        ? FlacConfig.TryGetBlocksFromDfLa(data, out blocks)
                        : FlacConfig.TryGetBlocksFromCodecPrivate(data, out blocks);
                    if (ok && FlacConfig.TryGetStreamInfo(blocks.Span, out FlacStreamInfo info))
                    {
                        parameters = info.Describe();
                        return true;
                    }

                    return false;
                }

            case "ac3":
                return Ac3Config.TryDescribeDac3(data.Span, out parameters);
            case "eac3":
                return Ac3Config.TryDescribeDec3(data.Span, out parameters);
            case "amr-nb":
            case "amr-wb":
                {
                    if (!AmrSpecificBox.TryParse(data.Span, out _))
                    {
                        return false;
                    }

                    parameters = DescribeAmr(codec.Name == "amr-wb");
                    return true;
                }

            default:
                return false;
        }
    }

    /// <summary>Describes a track from its configuration and, for codecs whose configuration lacks the picture size (VP9), its first sample.</summary>
    /// <param name="codec">The codec.</param>
    /// <param name="configuration">The setup bytes, as for the other overload.</param>
    /// <param name="firstSample">The first coded sample of the track.</param>
    /// <param name="parameters">The summary when true.</param>
    /// <returns>As the other overload; a sample that does not parse leaves the size at 0 rather than failing.</returns>
    public static bool TryDescribe(CodecId codec, CodecConfiguration configuration, ReadOnlySpan<byte> firstSample, out CodecParameters? parameters)
    {
        if (!TryDescribe(codec, configuration, out parameters))
        {
            return false;
        }

        if (parameters!.Width == 0 && codec == CodecId.Vp9 && Vp9FrameHeader.TryParse(firstSample, out Vp9FrameHeader h) && h.HasSize)
        {
            parameters = parameters with { Width = h.Width, Height = h.Height };
        }

        return true;
    }

    /// <summary>Describes a track from its first coded sample, for codecs whose parameters live in the stream: Annex B or length-prefixed
    /// video is not handled here (use the configuration record); AC-3, E-AC-3, MPEG audio, ADTS, VP8, VP9, AV1 low-overhead and MPEG-2 video are.</summary>
    /// <param name="codec">The codec.</param>
    /// <param name="sample">One coded frame or access unit, starting at its sync word or header.</param>
    /// <param name="parameters">The summary when true.</param>
    /// <returns>False when the codec is not handled or the sample does not parse.</returns>
    public static bool TryDescribeSample(CodecId codec, ReadOnlySpan<byte> sample, out CodecParameters? parameters)
    {
        parameters = null;
        switch (codec.Name)
        {
            case "ac3":
            case "eac3":
                if (Ac3SyncFrame.TryParse(sample, out Ac3SyncFrame ac3))
                {
                    parameters = ac3.Describe();
                    return true;
                }

                return false;
            case "mp3":
                if (MpegAudioFrameHeader.TryParse(sample, out MpegAudioFrameHeader mp))
                {
                    parameters = mp.Describe();
                    return true;
                }

                return false;
            case "aac":
                if (AdtsHeader.TryParse(sample, out AdtsHeader adts) && AudioSpecificConfig.TryParse(AdtsAsc(adts), out AudioSpecificConfig config))
                {
                    parameters = config.Describe();
                    return true;
                }

                return false;
            case "amr-nb":
            case "amr-wb":
                // RFC 4867 §5.1: a storage-format file starts with the "#!AMR" magic, which also says narrow or wide band.
                if (AmrFrame.TryReadMagic(sample, out bool wideband, out _))
                {
                    parameters = DescribeAmr(wideband);
                    return true;
                }

                return false;
            case "vp8":
                if (Vp8KeyFrameHeader.TryParse(sample, out Vp8KeyFrameHeader v8))
                {
                    parameters = new CodecParameters { Codec = CodecId.Vp8, Rfc6381 = "vp08.00.10.08", Profile = v8.Version, Width = v8.Width, Height = v8.Height, BitDepth = 8, Chroma = ChromaFormat.Yuv420 };
                    return true;
                }

                return false;
            case "vp9":
                if (Vp9FrameHeader.TryParse(sample, out Vp9FrameHeader v9) && v9.HasSize)
                {
                    VpCodecConfigurationRecord rec = VpCodecConfigurationRecord.FromFrameHeader(v9, Vp9Level.Estimate(v9.Width, v9.Height));
                    parameters = rec.Describe() with { Width = v9.Width, Height = v9.Height };
                    return true;
                }

                return false;
            case "av1":
                if (SequenceHeader.TryFind(sample, out SequenceHeader sh, out ReadOnlySpan<byte> obu))
                {
                    Av1CodecConfigurationRecord rec = Av1CodecConfigurationRecord.FromSequenceHeader(sh, obu.ToArray());
                    return rec.TryDescribe(out parameters);
                }

                return false;
            case "mpeg2video":
                if (Mpeg2SequenceHeader.TryFind(sample, out Mpeg2SequenceHeader m2))
                {
                    parameters = new CodecParameters
                    {
                        Codec = CodecId.Mpeg2Video,
                        Rfc6381 = "mp4v.61",
                        Profile = m2.Profile,
                        Level = m2.Level,
                        ProfileName = m2.ProfileName,
                        Width = m2.Width,
                        Height = m2.Height,
                        Chroma = m2.ChromaFormat switch { 1 => ChromaFormat.Yuv420, 2 => ChromaFormat.Yuv422, 3 => ChromaFormat.Yuv444, _ => ChromaFormat.Unknown },
                        BitDepth = 8,
                        IsInterlaced = m2.HasExtension && !m2.ProgressiveSequence,
                        FrameRateNumerator = m2.FrameRateNumerator,
                        FrameRateDenominator = m2.FrameRateDenominator,
                        ReorderFrames = m2.LowDelay ? 0 : -1,
                    };
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>Describes a NAL-based video stream from an Annex B byte stream (or its first access unit), using the parameter sets in it.</summary>
    /// <param name="codec">Which NAL codec the bytes are.</param>
    /// <param name="annexB">Annex B bytes that include at least the SPS.</param>
    /// <param name="parameters">The summary when true.</param>
    /// <returns>False when no valid SPS is found.</returns>
    public static bool TryDescribeAnnexB(NalCodec codec, ReadOnlyMemory<byte> annexB, out CodecParameters? parameters)
    {
        parameters = null;
        switch (codec)
        {
            case NalCodec.H264:
                return AvcDecoderConfigurationRecord.TryCreateFromAnnexB(annexB, out AvcDecoderConfigurationRecord? avc) && avc!.TryDescribe(out parameters);
            case NalCodec.Hevc:
                return HevcDecoderConfigurationRecord.TryCreateFromAnnexB(annexB, out HevcDecoderConfigurationRecord? hevc) && hevc!.TryDescribe(out parameters);
            default:
                {
                    AnnexBReader reader = new(annexB.Span);
                    while (reader.TryRead(out ReadOnlySpan<byte> nal, out int offset, out _))
                    {
                        if (NalUnit.GetType(NalCodec.Vvc, nal) == VvcNalType.Sps)
                        {
                            VvcDecoderConfigurationRecord rec = new() { Arrays = [new VvcNalArray(true, VvcNalType.Sps, [annexB.Slice(offset, nal.Length)])] };
                            return rec.TryDescribe(out parameters);
                        }
                    }

                    return false;
                }
        }
    }

    private static CodecParameters DescribeAmr(bool wide) => new()
    {
        Codec = wide ? CodecId.AmrWb : CodecId.AmrNb,
        Rfc6381 = wide ? "sawb" : "samr",
        SampleRate = wide ? 16000 : 8000,
        Channels = 1,
        SamplesPerFrame = AmrFrame.SamplesPerFrame(wide),
    };

    private static byte[] AdtsAsc(in AdtsHeader h)
    {
        byte[] asc = new byte[2];
        AudioSpecificConfig.TryWriteSimple(h.AudioObjectType, h.SamplingFrequencyIndex, h.ChannelConfiguration, asc);
        return asc;
    }
}

/// <summary>A rough VP9 level estimate from picture size (VP9 bitstream specification Annex A, Table A.1, by maximum luma picture size).</summary>
public static class Vp9Level
{
    /// <summary>Estimates the lowest level, times ten, whose maximum luma picture size admits the picture. Frame rate and bit rate limits are not considered.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>For example 10 for level 1, 31 for level 3.1; 62 for anything larger than level 6.1's limit.</returns>
    public static int Estimate(int width, int height)
    {
        long size = (long)width * height;
        return size switch
        {
            <= 36_864 => 10,
            <= 73_728 => 11,
            <= 122_880 => 20,
            <= 245_760 => 21,
            <= 552_960 => 30,
            <= 983_040 => 31,
            <= 2_228_224 => 40,
            <= 8_912_896 => 50,
            _ => 60,
        };
    }
}
