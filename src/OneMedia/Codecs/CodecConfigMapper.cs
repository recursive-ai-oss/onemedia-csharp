using System.Buffers;
using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Codecs.Ac3;
using RecursiveAI.OneMedia.Codecs.Av1;
using RecursiveAI.OneMedia.Codecs.Flac;
using RecursiveAI.OneMedia.Codecs.MpegAudio;
using RecursiveAI.OneMedia.Codecs.Opus;
using RecursiveAI.OneMedia.Codecs.Vpx;

namespace RecursiveAI.OneMedia.Codecs;

/// <summary>The outcome of mapping codec setup bytes between Matroska and ISO base media.</summary>
public enum ConfigMapStatus
{
    /// <summary>The mapping was written.</summary>
    Done = 0,

    /// <summary>The input does not parse as the codec's configuration.</summary>
    InvalidData,

    /// <summary>The codec has no configuration in the target container (for example Vorbis in ISO base media has no registered sample entry).</summary>
    NoMapping,

    /// <summary>The target container stores no configuration for this codec (AC-3 in Matroska, MPEG audio in ISO): nothing was written.</summary>
    NoConfiguration,

    /// <summary>The mapping needs the first coded frame (AC-3, E-AC-3) and none was given.</summary>
    NeedsFirstFrame,
}

/// <summary>
/// Maps a track's codec setup bytes between Matroska CodecPrivate and the configuration box of the ISO base media sample entry.
/// For AVC, HEVC, VVC and AV1 the two are the same bytes; for the others the layouts differ (AAC, Opus, FLAC, VP9) or the Matroska
/// side is empty and the ISO box comes from the first frame (AC-3, E-AC-3).
/// </summary>
public static class CodecConfigMapper
{
    /// <summary>Maps Matroska CodecPrivate to the content of the ISO configuration box (the bytes after the box header).</summary>
    /// <param name="codec">The codec.</param>
    /// <param name="codecPrivate">The CodecPrivate bytes; may be empty for codecs that have none.</param>
    /// <param name="firstFrame">The first coded frame: needed for AC-3 and E-AC-3, and for VP9 and MPEG audio when the CodecPrivate is empty.</param>
    /// <param name="writer">Receives the box content.</param>
    /// <param name="boxType">The four-character code of the box: <c>avcC</c>, <c>hvcC</c>, <c>vvcC</c>, <c>av1C</c>, <c>vpcC</c>, <c>esds</c>, <c>dOps</c>, <c>dfLa</c>, <c>dac3</c> or <c>dec3</c>.</param>
    /// <param name="bitrate">Average bit rate in bits per second for the <c>esds</c> descriptor; 0 when unknown.</param>
    /// <returns>The status.</returns>
    public static ConfigMapStatus MatroskaToIso(CodecId codec, ReadOnlyMemory<byte> codecPrivate, ReadOnlySpan<byte> firstFrame, IBufferWriter<byte> writer, out string? boxType, uint bitrate = 0)
    {
        ArgumentNullException.ThrowIfNull(writer);
        boxType = null;
        ReadOnlySpan<byte> s = codecPrivate.Span;
        switch (codec.Name)
        {
            case "h264": boxType = "avcC"; return Copy(s, writer);
            case "hevc": boxType = "hvcC"; return Copy(s, writer);
            case "vvc": boxType = "vvcC"; return Copy(s, writer);
            case "av1": boxType = "av1C"; return Copy(s, writer);
            case "vp9":
                {
                    boxType = "vpcC";
                    if (Vp9CodecPrivate.TryParse(s, out int profile, out int level, out int depth, out int chroma) && profile >= 0 && depth >= 0)
                    {
                        new VpCodecConfigurationRecord { Profile = profile, Level = Math.Max(level, 0), BitDepth = depth, ChromaSubsampling = Math.Max(chroma, 0) }.Write(writer);
                        return ConfigMapStatus.Done;
                    }

                    // Matroska writers commonly omit the VP9 CodecPrivate; the first key frame then supplies the same facts.
                    if (s.IsEmpty && Vp9FrameHeader.TryParse(firstFrame, out Vp9FrameHeader frameHeader) && frameHeader.HasSize)
                    {
                        VpCodecConfigurationRecord.FromFrameHeader(frameHeader, Vp9Level.Estimate(frameHeader.Width, frameHeader.Height)).Write(writer);
                        return ConfigMapStatus.Done;
                    }

                    return ConfigMapStatus.InvalidData;
                }

            case "aac":
                {
                    boxType = "esds";
                    if (!AudioSpecificConfig.TryParse(s, out _))
                    {
                        return ConfigMapStatus.InvalidData;
                    }

                    new EsDescriptor { ObjectTypeIndication = 0x40, StreamType = 5, AvgBitrate = bitrate, MaxBitrate = bitrate, DecoderSpecificInfo = codecPrivate }.WriteBoxPayload(writer);
                    return ConfigMapStatus.Done;
                }

            case "opus":
                {
                    boxType = "dOps";
                    if (!OpusHead.TryParseOpusHead(codecPrivate, out OpusHead? head))
                    {
                        return ConfigMapStatus.InvalidData;
                    }

                    head!.WriteDOps(writer);
                    return ConfigMapStatus.Done;
                }

            case "flac":
                {
                    boxType = "dfLa";
                    if (!FlacConfig.TryGetBlocksFromCodecPrivate(codecPrivate, out ReadOnlyMemory<byte> blocks) || !FlacConfig.TryGetStreamInfo(blocks.Span, out _))
                    {
                        return ConfigMapStatus.InvalidData;
                    }

                    FlacConfig.WriteDfLa(blocks.Span, writer);
                    return ConfigMapStatus.Done;
                }

            case "ac3":
            case "eac3":
                {
                    boxType = codec.Name == "ac3" ? "dac3" : "dec3";
                    if (firstFrame.IsEmpty)
                    {
                        return ConfigMapStatus.NeedsFirstFrame;
                    }

                    if (!Ac3SyncFrame.TryParse(firstFrame, out Ac3SyncFrame frame))
                    {
                        return ConfigMapStatus.InvalidData;
                    }

                    bool ok = frame.IsEac3 ? frame.TryWriteDec3((frame.BitRate + 999) / 1000, writer) : frame.TryWriteDac3(writer);
                    return ok && frame.IsEac3 == (codec.Name == "eac3") ? ConfigMapStatus.Done : ConfigMapStatus.InvalidData;
                }

            case "vorbis":
                return ConfigMapStatus.NoMapping;
            case "mp3":
                {
                    // ISO base media stores MPEG audio as an esds with object type 0x6B (MPEG-1) or 0x69 (MPEG-2 and 2.5) and no decoder-specific info.
                    boxType = "esds";
                    bool mpeg1 = firstFrame.IsEmpty || !MpegAudioFrameHeader.TryParse(firstFrame, out MpegAudioFrameHeader mp) || mp.Version == 3;
                    new EsDescriptor { ObjectTypeIndication = mpeg1 ? 0x6B : 0x69, StreamType = 5, AvgBitrate = bitrate, MaxBitrate = bitrate }.WriteBoxPayload(writer);
                    return ConfigMapStatus.Done;
                }

            default:
                return ConfigMapStatus.NoMapping;
        }
    }

    /// <summary>Maps the content of an ISO configuration box to Matroska CodecPrivate.</summary>
    /// <param name="codec">The codec.</param>
    /// <param name="boxPayload">The box content (the bytes after the box header).</param>
    /// <param name="writer">Receives the CodecPrivate bytes. Nothing is written for AC-3, E-AC-3 and MPEG audio, which Matroska stores without one.</param>
    /// <returns>The status.</returns>
    public static ConfigMapStatus IsoToMatroska(CodecId codec, ReadOnlyMemory<byte> boxPayload, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ReadOnlySpan<byte> s = boxPayload.Span;
        switch (codec.Name)
        {
            case "h264":
            case "hevc":
            case "vvc":
            case "av1":
                return Copy(s, writer);
            case "vp9":
                {
                    if (!VpCodecConfigurationRecord.TryParse(s, out VpCodecConfigurationRecord? vp))
                    {
                        return ConfigMapStatus.InvalidData;
                    }

                    Vp9CodecPrivate.Write(writer, vp!.Profile, vp.Level, vp.BitDepth, vp.ChromaSubsampling);
                    return ConfigMapStatus.Done;
                }

            case "aac":
                {
                    if (!EsDescriptor.TryParse(boxPayload, out EsDescriptor? es) || es!.DecoderSpecificInfo.IsEmpty)
                    {
                        return ConfigMapStatus.InvalidData;
                    }

                    return Copy(es.DecoderSpecificInfo.Span, writer);
                }

            case "opus":
                {
                    if (!OpusHead.TryParseDOps(boxPayload, out OpusHead? head))
                    {
                        return ConfigMapStatus.InvalidData;
                    }

                    head!.WriteOpusHead(writer);
                    return ConfigMapStatus.Done;
                }

            case "flac":
                {
                    if (!FlacConfig.TryGetBlocksFromDfLa(boxPayload, out ReadOnlyMemory<byte> blocks) || !FlacConfig.TryGetStreamInfo(blocks.Span, out _))
                    {
                        return ConfigMapStatus.InvalidData;
                    }

                    FlacConfig.WriteCodecPrivate(blocks.Span, writer);
                    return ConfigMapStatus.Done;
                }

            case "ac3":
            case "eac3":
            case "mp3":
                return ConfigMapStatus.NoConfiguration;
            default:
                return ConfigMapStatus.NoMapping;
        }
    }

    private static ConfigMapStatus Copy(ReadOnlySpan<byte> source, IBufferWriter<byte> writer)
    {
        if (source.IsEmpty)
        {
            return ConfigMapStatus.InvalidData;
        }

        source.CopyTo(writer.GetSpan(source.Length));
        writer.Advance(source.Length);
        return ConfigMapStatus.Done;
    }
}
