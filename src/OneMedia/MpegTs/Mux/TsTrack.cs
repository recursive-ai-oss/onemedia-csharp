using System.Buffers;
using System.Text;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Codecs.H264;
using RecursiveAI.OneMedia.Codecs.Hevc;
using RecursiveAI.OneMedia.Codecs.Vvc;

namespace RecursiveAI.OneMedia.MpegTs;

/// <summary>How the payload of a track is put into the stream.</summary>
internal enum TsPayloadKind
{
    /// <summary>H.264, HEVC or VVC access units, Annex B on the wire.</summary>
    NalVideo,

    /// <summary>MPEG-1/2 video, copied as it is.</summary>
    Mpeg2Video,

    /// <summary>AAC as ADTS frames.</summary>
    AacAdts,

    /// <summary>MPEG-1/2 audio (Layers I to III) frames, copied as they are.</summary>
    MpegAudio,

    /// <summary>AC-3 or E-AC-3 synchronization frames in private stream 1.</summary>
    Ac3,

    /// <summary>A PES payload of a private stream (DVB subtitles, teletext), copied as it is.</summary>
    PrivatePes,

    /// <summary>Timed ID3 in a metadata PES.</summary>
    TimedId3,

    /// <summary>SCTE-35 sections, carried straight in transport packets without PES framing.</summary>
    Sections,
}

/// <summary>What one declared track turns into on the wire: stream type, descriptors, PES stream id and the framing rules for its payload.</summary>
internal sealed class TsTrack
{
    public required MediaTrack Declared { get; init; }

    public required int Pid { get; init; }

    public required int StreamType { get; init; }

    public required byte[] Descriptors { get; init; }

    public required int StreamId { get; init; }

    public required TsPayloadKind Kind { get; init; }

    public NalCodec Nal { get; init; }

    /// <summary>Length-prefix size of the packets (1 to 4) when the track is given as <c>avcC</c>-style samples; 0 for Annex B packets.</summary>
    public int LengthSize { get; init; }

    /// <summary>Parameter sets in Annex B form; from the configuration record, or learnt from the first access unit that carried them.</summary>
    public byte[] ParameterSets { get; set; } = [];

    public AudioSpecificConfig Asc { get; init; }

    public bool HasAsc { get; init; }

    public bool IsVideo => Kind is TsPayloadKind.NalVideo or TsPayloadKind.Mpeg2Video;

    // Running state.
    public byte Cc;

    public bool Started { get; set; }

    public MediaTime LastDts { get; set; } = MediaTime.Unknown;

    public MediaTime LastDuration { get; set; } = MediaTime.Unknown;

    public int Index { get; set; }

    // Small audio pieces waiting to be sent as one PES packet.
    public byte[]? Pending { get; set; }

    public int PendingLength { get; set; }

    public long PendingPts { get; set; }

    public long PendingDts { get; set; }

    public bool PendingKey { get; set; }

    public long Packets { get; set; }

    public long PayloadBytes { get; set; }

    public MediaTime Duration { get; set; }

    public bool NotedMissingDts { get; set; }

    public bool NotedAudioWrap { get; set; }
}

/// <summary>Maps neutral tracks to stream types and descriptors (ISO/IEC 13818-1 Table 2-34; ETSI EN 300 468 §6.2; ETSI TS 102 366 Annex H; Apple's timed metadata in HLS).</summary>
internal static class TsTrackPlanner
{
    // ISO/IEC 13818-1 §2.6.8 registration_descriptor: tag 5, a four-character format_identifier.
    private static byte[] Registration(string id) => [0x05, 0x04, .. Encoding.ASCII.GetBytes(id)];

    public static TsTrack Plan(MediaTrack t, int outputIndex, int pid, int videoCount, int audioCount, List<string> notes)
    {
        string codec = t.Codec.Name;
        List<byte> d = [];
        TsPayloadKind kind;
        int streamType;
        int streamId;
        NalCodec nal = default;
        int lengthSize = 0;
        byte[] sets = [];
        AudioSpecificConfig asc = default;
        bool hasAsc = false;

        void Refuse(MuxRefusalReason reason, string message) => throw new MuxRefusedException(reason, $"Track {outputIndex}: {message}", outputIndex, t.Codec);

        switch (t.Kind, codec)
        {
            case (TrackKind.Video, "h264" or "hevc" or "vvc"):
                kind = TsPayloadKind.NalVideo;
                nal = codec == "h264" ? NalCodec.H264 : codec == "hevc" ? NalCodec.Hevc : NalCodec.Vvc;
                streamType = codec == "h264" ? 0x1B : codec == "hevc" ? 0x24 : 0x33;
                streamId = 0xE0 + videoCount;
                if (codec == "hevc")
                {
                    // ETSI TS 101 154 / ATSC A/72: HEVC streams carry the 'HEVC' registration descriptor.
                    d.AddRange(Registration("HEVC"));
                }

                if (!t.Configuration.IsEmpty)
                {
                    ReadParameterSets(t, nal, outputIndex, out sets, out lengthSize);
                }
                else if (t.Configuration.Format is "avcC" or "hvcC" or "vvcC")
                {
                    Refuse(MuxRefusalReason.MissingConfiguration, $"the {t.Configuration.Format} record is empty.");
                }

                break;
            case (TrackKind.Video, "mpeg2video" or "mpeg1video"):
                kind = TsPayloadKind.Mpeg2Video;
                streamType = codec == "mpeg1video" ? 0x01 : 0x02;
                streamId = 0xE0 + videoCount;
                break;
            case (TrackKind.Audio, "aac"):
                kind = TsPayloadKind.AacAdts;
                streamType = 0x0F;
                streamId = 0xC0 + audioCount;
                if (!t.Configuration.IsEmpty)
                {
                    ReadOnlyMemory<byte> ascBytes = t.Configuration.Data;
                    if (t.Configuration.Format == "esds")
                    {
                        // An mp4a sample entry stores an ES descriptor; the AudioSpecificConfig is its decoder-specific info.
                        ArrayBufferWriter<byte> w = new();
                        if (CodecConfigMapper.IsoToMatroska(t.Codec, t.Configuration.Data, w) != ConfigMapStatus.Done)
                        {
                            Refuse(MuxRefusalReason.InvalidConfiguration, "the esds descriptor holds no AudioSpecificConfig.");
                        }

                        ascBytes = w.WrittenMemory.ToArray();
                    }

                    if (!AudioSpecificConfig.TryParse(ascBytes.Span, out asc))
                    {
                        Refuse(MuxRefusalReason.InvalidConfiguration, "the AudioSpecificConfig does not parse.");
                    }

                    hasAsc = true;
                    Span<byte> probe = stackalloc byte[8]; // fixed 8-byte buffer: a header plus one byte, to ask whether ADTS can describe the config
                    if (AdtsRepackager.WrapRaw(in asc, [0], probe, out _) == OperationStatus.InvalidData)
                    {
                        Refuse(MuxRefusalReason.InvalidConfiguration, $"ADTS cannot describe this AAC configuration (object type {asc.AudioObjectType}, channel configuration {asc.ChannelConfiguration}, rate index {asc.SamplingFrequencyIndex}).");
                    }
                }

                break;
            case (TrackKind.Audio, "mp3" or "mp2" or "mp1"):
                kind = TsPayloadKind.MpegAudio;
                // ISO/IEC 13818-1 Table 2-34: 0x03 is ISO/IEC 11172-3 audio (MPEG-1), 0x04 is ISO/IEC 13818-3 (MPEG-2 lower sample rates).
                streamType = t.Audio is { SampleRate: > 0 and < 32000 } ? 0x04 : 0x03;
                streamId = 0xC0 + audioCount;
                break;
            case (TrackKind.Audio, "ac3" or "eac3"):
                kind = TsPayloadKind.Ac3;
                streamType = 0x06;
                streamId = 0xBD;
                // ETSI EN 300 468 §6.2.1 / D: AC-3_descriptor (0x6A) and enhanced_AC-3_descriptor (0x7A) with no optional fields.
                d.AddRange([codec == "ac3" ? (byte)0x6A : (byte)0x7A, 0x01, 0x00]);
                break;
            case (TrackKind.Subtitle, "dvb-subtitle"):
                kind = TsPayloadKind.PrivatePes;
                streamType = 0x06;
                streamId = 0xBD;
                // EN 300 468 §6.2.41 subtitling_descriptor: the reader does not hand over the stream's own, so the page ids are the common defaults.
                d.AddRange([0x59, 0x08, .. Language3(t), 0x10, 0x00, 0x01, 0x00, 0x01]);
                notes.Add($"Track {outputIndex}: the DVB subtitling descriptor was written with default composition and ancillary page ids 1 and subtitling type 0x10.");
                break;
            case (TrackKind.Subtitle, "dvb-teletext"):
                kind = TsPayloadKind.PrivatePes;
                streamType = 0x06;
                streamId = 0xBD;
                // EN 300 468 §6.2.43 teletext_descriptor: type 2 (subtitle page), magazine 1, page 0x88; the real pages are not known here.
                d.AddRange([0x56, 0x05, .. Language3(t), 0x11, 0x88]);
                notes.Add($"Track {outputIndex}: the teletext descriptor was written with the default subtitle page 188 (magazine 1, page 0x88).");
                break;
            case (TrackKind.Data, "timed-id3"):
                kind = TsPayloadKind.TimedId3;
                streamType = 0x15;
                streamId = 0xFC;
                // ITU-T H.222.0 metadata_descriptor (tag 0x26) with a registration descriptor, both naming 'ID3 ' as application format and format: the form HLS players
                // recognise. Checked by decoding the PMT with a transport stream analyser and by ffprobe, which reports the stream as timed ID3.
                d.AddRange(Registration("ID3 "));
                d.AddRange([0x26, 0x0D, 0xFF, 0xFF, (byte)'I', (byte)'D', (byte)'3', (byte)' ', 0xFF, (byte)'I', (byte)'D', (byte)'3', (byte)' ', 0x00, 0x1F]);
                break;
            case (TrackKind.Data, "scte-35"):
                kind = TsPayloadKind.Sections;
                streamType = 0x86;
                streamId = 0;
                d.AddRange(Registration("CUEI"));
                break;
            default:
                Refuse(MuxRefusalReason.UnsupportedCodec, codec == "opus"
                    ? "Opus in MPEG-TS is not written (the carriage is specified by a draft that was not used)."
                    : $"MPEG transport streams are written without {t.Kind} tracks of codec '{codec}'.");
                return null!;
        }

        if (t.Kind is TrackKind.Audio or TrackKind.Subtitle && kind is not TsPayloadKind.PrivatePes && Iso639(t.Language) is { } lang)
        {
            // ISO/IEC 13818-1 §2.6.18 ISO_639_language_descriptor: code and audio_type 0 (undefined).
            d.AddRange([0x0A, 0x04, .. Encoding.ASCII.GetBytes(lang), 0x00]);
        }

        if (d.Count > 255 - 2)
        {
            Refuse(MuxRefusalReason.InvalidTrack, "the descriptor loop is too long.");
        }

        return new TsTrack
        {
            Declared = t,
            Pid = pid,
            StreamType = streamType,
            Descriptors = [.. d],
            StreamId = streamId,
            Kind = kind,
            Nal = nal,
            LengthSize = lengthSize,
            ParameterSets = sets,
            Asc = asc,
            HasAsc = hasAsc,
            Index = outputIndex,
        };
    }

    private static string? Iso639(string? language)
    {
        if (language is { Length: 3 } l && l != "und" && l != "mis" && l != "zxx")
        {
            foreach (char c in l)
            {
                if (c is < 'a' or > 'z')
                {
                    return null;
                }
            }

            return l;
        }

        return null;
    }

    private static byte[] Language3(MediaTrack t) => Encoding.ASCII.GetBytes(Iso639(t.Language) ?? "und");

    // The configuration of an H.264/HEVC/VVC track given as a record: the length-prefix size and the parameter sets in Annex B form.
    private static void ReadParameterSets(MediaTrack t, NalCodec nal, int outputIndex, out byte[] sets, out int lengthSize)
    {
        sets = [];
        lengthSize = 0;
        string? format = t.Configuration.Format;
        ReadOnlyMemory<byte> data = t.Configuration.Data;
        string expected = nal == NalCodec.H264 ? "avcC" : nal == NalCodec.Hevc ? "hvcC" : "vvcC";
        if (format == "bitmapinfoheader")
        {
            // Matroska V_MS/VFW/FOURCC: the CodecPrivate is a BITMAPINFOHEADER; what follows its 40 bytes is a record or Annex B parameter sets.
            byte[] whole = data.ToArray();
            if (Avi.AviTracks.TryGetRecord(t.Codec, whole, out string form, out ReadOnlyMemory<byte> record))
            {
                format = form;
                data = record;
            }
            else
            {
                format = "annexb";
                data = whole.Length > Avi.BitmapInfoHeader.Size ? whole.AsMemory(Avi.BitmapInfoHeader.Size) : default;
            }
        }

        if (format is null || format == "annexb")
        {
            // Annex B parameter sets handed over as the configuration.
            sets = data.ToArray();
            return;
        }

        if (format != expected)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidConfiguration, $"Track {outputIndex}: a {t.Codec.Name} track's configuration is '{format}'; expected {expected} or Annex B parameter sets.", outputIndex, t.Codec);
        }

        List<ReadOnlyMemory<byte>> units = [];
        switch (nal)
        {
            case NalCodec.H264:
                if (!AvcDecoderConfigurationRecord.TryParse(data, out AvcDecoderConfigurationRecord? avc) || avc is null)
                {
                    throw Bad();
                }

                lengthSize = avc.LengthSize;
                units.AddRange(avc.SequenceParameterSets);
                units.AddRange(avc.PictureParameterSets);
                break;
            case NalCodec.Hevc:
                if (!HevcDecoderConfigurationRecord.TryParse(data, out HevcDecoderConfigurationRecord? hevc) || hevc is null)
                {
                    throw Bad();
                }

                lengthSize = hevc.LengthSize;
                foreach (HevcNalArray a in hevc.Arrays)
                {
                    if (a.NalUnitType is >= 32 and <= 34)
                    {
                        units.AddRange(a.Units);
                    }
                }

                break;
            default:
                if (!VvcDecoderConfigurationRecord.TryParse(data, out VvcDecoderConfigurationRecord? vvc) || vvc is null)
                {
                    throw Bad();
                }

                lengthSize = vvc.LengthSize;
                foreach (VvcNalArray a in vvc.Arrays)
                {
                    if (a.NalUnitType is >= 12 and <= 17)
                    {
                        units.AddRange(a.Units);
                    }
                }

                break;
        }

        if (lengthSize is < 1 or > 4)
        {
            throw Bad();
        }

        sets = NalRepackager.ParameterSetsToAnnexB(units);
        return;

        MuxRefusedException Bad() => new(MuxRefusalReason.InvalidConfiguration, $"Track {outputIndex}: the {expected} record does not parse.", outputIndex, t.Codec);
    }
}
