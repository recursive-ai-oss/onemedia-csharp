using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Amr;
using RecursiveAI.OneMedia.Codecs.Pcm;

namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>Which media information header a track needs (ISO/IEC 14496-12 §8.4.5).</summary>
internal enum MediaHeaderKind
{
    Video,
    Sound,
    Null,
    Subtitle,
    QuickTimeGeneric,
}

/// <summary>What the muxer needs to know about a track's sample description: the handler, the media header and the entry bytes.</summary>
internal sealed class SampleEntryPlan
{
    public required string Handler { get; init; }

    public required MediaHeaderKind Header { get; init; }

    /// <summary>The complete sample entry box, or null while it still needs the first packet (<see cref="NeedsFirstPacket"/>).</summary>
    public byte[]? Entry { get; set; }

    public bool NeedsFirstPacket { get; init; }
}

/// <summary>
/// Builds sample entries from a track's codec and configuration. The configuration box is copied from the track when it already is
/// the ISO box (the track came from an ISO file or Matroska, where the bytes are the same), mapped through
/// <see cref="CodecConfigMapper"/> when it is a Matroska-style record, and derived from the first packet for the codecs that store
/// none in their source container.
/// </summary>
internal static class IsoSampleEntries
{
    private const int HeadBytes = 4096;

    /// <summary>How many bytes of the first packet of a track the muxer keeps for codecs that derive their configuration from it.</summary>
    public static int FirstPacketBytes => HeadBytes;

    public static SampleEntryPlan Plan(MediaTrack t, int index, IsoFileKind kind, IsoTrackOptions? options)
    {
        string handler = options?.HandlerType ?? DefaultHandler(t, kind);
        if (handler.Length != 4)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, $"Track {index}: the handler type must be four characters.", index, t.Codec);
        }

        MediaHeaderKind header = t.Kind switch
        {
            TrackKind.Video => MediaHeaderKind.Video,
            TrackKind.Audio => MediaHeaderKind.Sound,
            TrackKind.Subtitle => handler == "subt" ? MediaHeaderKind.Subtitle : MediaHeaderKind.Null,
            _ => handler == "tmcd" ? MediaHeaderKind.QuickTimeGeneric : MediaHeaderKind.Null,
        };
        if (handler == "vide")
        {
            header = MediaHeaderKind.Video;
        }
        else if (handler == "soun")
        {
            header = MediaHeaderKind.Sound;
        }

        bool deferred = NeedsFirstPacket(t);
        SampleEntryPlan plan = new() { Handler = handler, Header = header, NeedsFirstPacket = deferred };
        if (!deferred)
        {
            plan.Entry = BuildEntry(t, index, kind, options, []);
        }
        else
        {
            // Validate what can be validated before the first packet exists, so an unsupported codec is refused when it is declared.
            _ = Resolve(t, index, kind, options, [], probe: true);
        }

        return plan;
    }

    /// <summary>Completes a plan whose entry waited for the track's first packet.</summary>
    public static void Complete(SampleEntryPlan plan, MediaTrack t, int index, IsoFileKind kind, IsoTrackOptions? options, byte[] firstPacket)
    {
        plan.Entry = BuildEntry(t, index, kind, options, firstPacket);
    }

    private static bool NeedsFirstPacket(MediaTrack t)
    {
        string? format = t.Configuration.Format;
        if (t.Codec == CodecId.Ac3)
        {
            return format != "dac3";
        }

        if (t.Codec == CodecId.Eac3)
        {
            return format != "dec3";
        }

        if (t.Codec == CodecId.Mp3)
        {
            return format != "esds";
        }

        return t.Codec == CodecId.Vp9 && format != "vpcC" && t.Configuration.IsEmpty;
    }

    private static string DefaultHandler(MediaTrack t, IsoFileKind kind) => t.Kind switch
    {
        TrackKind.Video => "vide",
        TrackKind.Audio => "soun",
        TrackKind.Subtitle => t.Codec.Name switch
        {
            "tx3g" => kind is IsoFileKind.ThreeGp or IsoFileKind.ThreeG2 or IsoFileKind.Mov ? "text" : "sbtl",
            "webvtt" => "text",
            "ttml" => "subt",
            _ => t.CodecTag == "clcp" ? "clcp" : "sbtl",
        },
        _ => t.CodecTag == "tmcd" ? "tmcd" : "meta",
    };

    private static byte[] BuildEntry(MediaTrack t, int index, IsoFileKind kind, IsoTrackOptions? options, byte[] firstPacket)
    {
        (string Fourcc, string? ConfigBox, byte[]? ConfigPayload) r = Resolve(t, index, kind, options, firstPacket, probe: false);
        BoxStack b = new();
        b.Begin(r.Fourcc);
        b.Zeros(6);
        b.U16(1); // data_reference_index
        switch (t.Kind)
        {
            case TrackKind.Video:
                WriteVisual(b, t, index);
                break;
            case TrackKind.Audio:
                WriteAudio(b, t, index, kind, r.Fourcc, ResolvePcmOrNull(t, options));
                break;
            default:
                // Subtitle and data entries are carried as the source stored them: the bytes after the sample entry header are the configuration.
                if (r.ConfigPayload is { Length: > 0 } passthrough && r.ConfigBox is null)
                {
                    b.Bytes(passthrough);
                }

                break;
        }

        if (r.ConfigBox is not null && r.ConfigPayload is not null)
        {
            b.Begin(r.ConfigBox);
            b.Bytes(r.ConfigPayload);
            b.End();
        }

        if (t.Kind == TrackKind.Audio && r.Fourcc is "ipcm" or "fpcm")
        {
            PcmSampleFormat pcm = ResolvePcm(t, options);
            b.BeginFull("pcmC", 0, 0);
            b.U8(pcm.BigEndian ? 0 : 1); // ISO/IEC 23003-5: bit 0 of format_flags set means little endian
            b.U8(pcm.BitsPerSample);
            b.End();
        }

        if (t.Kind == TrackKind.Video && t.Video is { } v && v.PixelAspectNumerator > 0 && v.PixelAspectDenominator > 0 && v.PixelAspectNumerator != v.PixelAspectDenominator)
        {
            b.Begin("pasp");
            b.U32((uint)v.PixelAspectNumerator);
            b.U32((uint)v.PixelAspectDenominator);
            b.End();
        }

        if (options?.SampleEntryBoxes is { } extra)
        {
            foreach (ReadOnlyMemory<byte> box in extra)
            {
                if (!BoxBuilder.IsSingleBox(box.Span, out _))
                {
                    throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, $"Track {index}: a sample entry box given in the track options is not one complete box.", index, t.Codec);
                }

                b.Bytes(box.Span);
            }
        }

        b.End();
        return b.ToArray();
    }

    private static void WriteVisual(BoxStack b, MediaTrack t, int index)
    {
        int width = t.Video?.Width ?? 0;
        int height = t.Video?.Height ?? 0;
        if ((width <= 0 || height <= 0) && t.DescribeCodec() is { Width: > 0, Height: > 0 } coded)
        {
            width = coded.Width;
            height = coded.Height;
        }

        if (width is <= 0 or > ushort.MaxValue || height is <= 0 or > ushort.MaxValue)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, $"Track {index}: a video track needs a width and height between 1 and 65535.", index, t.Codec);
        }

        b.U16(0); // pre_defined
        b.U16(0);
        b.Zeros(12);
        b.U16(width);
        b.U16(height);
        b.U32(0x00480000); // 72 dpi
        b.U32(0x00480000);
        b.U32(0);
        b.U16(1); // frame_count
        b.Zeros(32); // compressorname: empty
        b.U16(0x0018);
        b.U16(0xFFFF);
    }

    private static void WriteAudio(BoxStack b, MediaTrack t, int index, IsoFileKind kind, string fourcc, PcmSampleFormat? pcm)
    {
        AudioTrackInfo a = t.Audio ?? default;
        int channels = a.Channels;
        int rate = a.SampleRate;
        if (channels is <= 0 or > ushort.MaxValue)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, $"Track {index}: an audio track needs a channel count between 1 and 65535.", index, t.Codec);
        }

        if (rate <= 0)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, $"Track {index}: an audio track needs a sample rate.", index, t.Codec);
        }

        int bits = pcm?.BitsPerSample ?? (a.BitsPerSample > 0 ? a.BitsPerSample : 16);
        if (fourcc == "Opus")
        {
            rate = 48000; // the Opus encapsulation fixes the entry's rate; the real input rate is in the configuration box
        }
        else if (fourcc == "fLaC")
        {
            // The FLAC encapsulation: the greatest regular division of a rate that does not fit the 16.16 field (the real rate is in STREAMINFO).
            while (rate > ushort.MaxValue && rate % 2 == 0)
            {
                rate /= 2;
            }
        }

        bool quickTimeV1 = kind == IsoFileKind.Mov && fourcc is "in24" or "in32" or "fl32" or "fl64";
        if (fourcc is "samr" or "sawb")
        {
            // 3GPP TS 26.244 §8.3: files state two channels and 16-bit samples; the real values are fixed by the codec.
            channels = 2;
            bits = 16;
            rate = fourcc == "samr" ? 8000 : 16000;
        }
        else if (fourcc is not ("ipcm" or "fpcm" or "sowt" or "twos" or "in24" or "in32" or "fl32" or "fl64" or "raw " or "fLaC" or "alac"))
        {
            bits = 16;
        }

        b.U16(quickTimeV1 ? 1 : 0); // sound version
        b.U16(0);
        b.U32(0);
        b.U16(channels);
        b.U16(bits);
        b.U16(0); // compression id
        b.U16(0); // packet size
        b.U32(rate <= ushort.MaxValue ? (uint)rate << 16 : 0);
        if (quickTimeV1)
        {
            // QuickTime File Format, sound sample description version 1: samples per packet, bytes per packet, bytes per frame, bytes per sample.
            int bytes = bits / 8;
            b.U32(1);
            b.U32((uint)bytes);
            b.U32((uint)(bytes * channels));
            b.U32((uint)bytes);
        }
    }

    private static PcmSampleFormat? ResolvePcmOrNull(MediaTrack t, IsoTrackOptions? o) => t.Codec == CodecId.Pcm ? ResolvePcm(t, o) : null;

    /// <summary>The sample layout of a PCM track: the track options, else the sample entry code it came from, else little-endian signed integers at the declared width.</summary>
    public static PcmSampleFormat ResolvePcm(MediaTrack t, IsoTrackOptions? o)
    {
        if (o?.Pcm is { } given)
        {
            return given;
        }

        switch (t.CodecTag)
        {
            case "sowt": return new PcmSampleFormat(PcmEncoding.SignedInteger, 16, 16, false);
            case "twos": return new PcmSampleFormat(PcmEncoding.SignedInteger, 16, 16, true);
            case "in24": return new PcmSampleFormat(PcmEncoding.SignedInteger, 24, 24, true);
            case "in32": return new PcmSampleFormat(PcmEncoding.SignedInteger, 32, 32, true);
            case "fl32": return new PcmSampleFormat(PcmEncoding.FloatingPoint, 32, 32, true);
            case "fl64": return new PcmSampleFormat(PcmEncoding.FloatingPoint, 64, 64, true);
            case "raw ": return new PcmSampleFormat(PcmEncoding.UnsignedInteger, 8, 8, false);
            case "ipcm" or "fpcm" when t.Configuration.Format == "pcmC" && t.Configuration.Data.Length >= 6:
                {
                    ReadOnlySpan<byte> c = t.Configuration.Data.Span;
                    int size = c[5];
                    return new PcmSampleFormat(t.CodecTag == "fpcm" ? PcmEncoding.FloatingPoint : PcmEncoding.SignedInteger, size, size, (c[4] & 1) == 0);
                }
        }

        int bits = t.Audio?.BitsPerSample ?? 0;
        if (bits <= 0)
        {
            bits = 16;
        }

        return bits == 8
            ? new PcmSampleFormat(PcmEncoding.UnsignedInteger, 8, 8, false)
            : new PcmSampleFormat(PcmEncoding.SignedInteger, bits, bits, false);
    }

    /// <summary>
    /// Decides the sample entry code and the configuration box. Throws the refusal when the codec cannot be carried or its configuration
    /// is absent or unusable.
    /// </summary>
    private static (string Fourcc, string? ConfigBox, byte[]? ConfigPayload) Resolve(MediaTrack t, int index, IsoFileKind kind, IsoTrackOptions? options, byte[] firstPacket, bool probe)
    {
        CodecId codec = t.Codec;
        string? format = t.Configuration.Format;
        ReadOnlyMemory<byte> data = t.Configuration.Data;
        MuxRefusedException Unsupported(string why) => new(MuxRefusalReason.UnsupportedCodec, $"Track {index}: {why}", index, codec);
        MuxRefusedException Missing(string what) => new(MuxRefusalReason.MissingConfiguration, $"Track {index}: {what}", index, codec);

        if (t.IsProtected)
        {
            throw Unsupported("the track is encrypted; its packets cannot be written as a clear sample entry.");
        }

        if (t.Kind is TrackKind.Subtitle or TrackKind.Data or TrackKind.Unknown)
        {
            return ResolveOther(t, index, Unsupported, Missing);
        }

        string? tag = ValidTag(t);
        switch (codec.Name)
        {
            case "h264": return Record(tag is "avc1" or "avc2" or "avc3" or "avc4" ? tag : "avc1", "avcC");
            case "hevc": return Record(tag is "hvc1" or "hev1" ? tag : "hvc1", "hvcC");
            case "vvc": return Record(tag is "vvc1" or "vvi1" ? tag : "vvc1", "vvcC");
            case "av1": return Record("av01", "av1C");
            case "vp8": return (kind == IsoFileKind.Mov ? throw Unsupported("VP8 has no QuickTime sample entry.") : "vp08", null, null);
            case "vp9":
                {
                    if (format == "vpcC" && !data.IsEmpty)
                    {
                        return ("vp09", "vpcC", data.ToArray());
                    }

                    ArrayBufferWriter<byte> w = new();
                    ConfigMapStatus s = CodecConfigMapper.MatroskaToIso(codec, data, firstPacket, w, out _);
                    if (s == ConfigMapStatus.Done)
                    {
                        return ("vp09", "vpcC", w.WrittenSpan.ToArray());
                    }

                    if (probe)
                    {
                        return ("vp09", "vpcC", null);
                    }

                    throw s == ConfigMapStatus.InvalidData ? new MuxRefusedException(MuxRefusalReason.InvalidConfiguration, $"Track {index}: the VP9 configuration does not parse.", index, codec) : Missing("a VP9 track needs a configuration record or a key frame to derive one from.");
                }

            case "aac": return Mapped("mp4a");
            case "mp3": return Mp3(probe);
            case "opus": return Mapped("Opus");
            case "flac": return Mapped("fLaC");
            case "ac3": return Dolby("ac-3", "dac3");
            case "eac3": return Dolby("ec-3", "dec3");
            case "amr-nb":
            case "amr-wb":
                {
                    string fourcc = codec == CodecId.AmrNb ? "samr" : "sawb";
                    if (format == "damr" && !data.IsEmpty)
                    {
                        return (fourcc, "damr", data.ToArray());
                    }

                    // 3GPP TS 26.244 §8.4.2: vendor, decoder version, mode set (all modes), mode change period, one frame per sample.
                    byte[] damr = new byte[AmrSpecificBox.Size];
                    new AmrSpecificBox(0x20202020, 0, codec == CodecId.AmrWb ? 0x83FF : 0x81FF, 0, 1).TryWrite(damr);
                    return (fourcc, "damr", damr);
                }

            case "alac":
                if (data.IsEmpty)
                {
                    throw Missing("an ALAC track needs its magic cookie.");
                }

                return ("alac", "alac", data.ToArray());
            case "pcm":
                {
                    PcmSampleFormat p = ResolvePcm(t, options);
                    if (p.Encoding is PcmEncoding.ALaw or PcmEncoding.MuLaw)
                    {
                        throw Unsupported("companded PCM has no sample entry here.");
                    }

                    if (kind == IsoFileKind.Mov)
                    {
                        string? qt = (p.Encoding, p.BitsPerSample, p.BigEndian) switch
                        {
                            (PcmEncoding.UnsignedInteger, 8, _) => "raw ",
                            (PcmEncoding.SignedInteger, 16, false) => "sowt",
                            (PcmEncoding.SignedInteger, 16, true) => "twos",
                            (PcmEncoding.SignedInteger, 24, true) => "in24",
                            (PcmEncoding.SignedInteger, 32, true) => "in32",
                            (PcmEncoding.FloatingPoint, 32, true) => "fl32",
                            (PcmEncoding.FloatingPoint, 64, true) => "fl64",
                            _ => null,
                        };
                        return qt is null ? throw Unsupported($"QuickTime has no sample entry for {p.Name} PCM.") : (qt, null, null);
                    }

                    if (p.Encoding == PcmEncoding.UnsignedInteger || p.BitsPerSample is not (16 or 24 or 32 or 64) || (p.Encoding == PcmEncoding.FloatingPoint && p.BitsPerSample is not (32 or 64)))
                    {
                        throw Unsupported($"{p.Name} PCM has no ISO sample entry (ISO/IEC 23003-5 stores signed integers of 16, 24 or 32 bits and floats of 32 or 64 bits).");
                    }

                    return (p.Encoding == PcmEncoding.FloatingPoint ? "fpcm" : "ipcm", null, null);
                }

            default:
                // A codec the layers above do not know by name is carried when it came from an ISO file: its sample entry code and
                // configuration box are the source's own.
                if (tag is not null && IsoCodecMap.Map(tag, t.Configuration) == codec && !codec.IsUnknown && (data.IsEmpty || format is { Length: 4 }))
                {
                    return (tag, data.IsEmpty ? null : format, data.IsEmpty ? null : data.ToArray());
                }

                throw Unsupported($"{(codec.IsUnknown ? "an unknown codec" : codec.Name)} has no ISO base media sample entry in OneMedia.");
        }

        (string, string?, byte[]?) Record(string fourcc, string boxType)
        {
            if (data.IsEmpty)
            {
                throw Missing($"a {codec.Name} track needs its {boxType} configuration record (parameter sets).");
            }

            if (format == boxType || format == "CodecPrivate")
            {
                return (fourcc, boxType, data.ToArray());
            }

            throw new MuxRefusedException(MuxRefusalReason.InvalidConfiguration,
                $"Track {index}: the {codec.Name} configuration is laid out as '{format}', not as {boxType}; repackage it first.", index, codec);
        }

        (string, string?, byte[]?) Mapped(string fourcc)
        {
            string boxType = codec.Name switch { "opus" => "dOps", "flac" => "dfLa", _ => "esds" };
            if (format == boxType && !data.IsEmpty)
            {
                return (fourcc, boxType, data.ToArray());
            }

            if (data.IsEmpty)
            {
                throw Missing($"a {codec.Name} track needs its codec setup bytes.");
            }

            ArrayBufferWriter<byte> w = new();
            // Formats that hold the Matroska-style record: CodecPrivate, OpusHead, AudioSpecificConfig (asc), MP4 'dac' etc.
            ConfigMapStatus s = CodecConfigMapper.MatroskaToIso(codec, data, firstPacket, w, out string? mapped, (uint)Math.Clamp(t.BitRate, 0, uint.MaxValue));
            if (s != ConfigMapStatus.Done)
            {
                throw new MuxRefusedException(MuxRefusalReason.InvalidConfiguration, $"Track {index}: the {codec.Name} setup bytes ('{format}') do not parse.", index, codec);
            }

            return (fourcc, mapped, w.WrittenSpan.ToArray());
        }

        (string, string?, byte[]?) Dolby(string fourcc, string boxType)
        {
            if (format == boxType && !data.IsEmpty)
            {
                return (fourcc, boxType, data.ToArray());
            }

            if (firstPacket.Length == 0)
            {
                if (probe)
                {
                    return (fourcc, boxType, null);
                }

                throw Missing($"{codec.Name} setup is derived from the first frame, and the track has none.");
            }

            ArrayBufferWriter<byte> w = new();
            ConfigMapStatus s = CodecConfigMapper.MatroskaToIso(codec, data, firstPacket, w, out string? mapped, (uint)Math.Clamp(t.BitRate, 0, uint.MaxValue));
            return s == ConfigMapStatus.Done
                ? (fourcc, mapped, w.WrittenSpan.ToArray())
                : throw new MuxRefusedException(MuxRefusalReason.InvalidConfiguration, $"Track {index}: the first {codec.Name} frame does not parse.", index, codec);
        }

        (string, string?, byte[]?) Mp3(bool probing)
        {
            if (format == "esds" && !data.IsEmpty)
            {
                return ("mp4a", "esds", data.ToArray());
            }

            if (probing)
            {
                return ("mp4a", "esds", null);
            }

            ArrayBufferWriter<byte> w = new();
            _ = CodecConfigMapper.MatroskaToIso(codec, default, firstPacket, w, out string? mapped, (uint)Math.Clamp(t.BitRate, 0, uint.MaxValue));
            return ("mp4a", mapped, w.WrittenSpan.ToArray());
        }
    }

    private static (string, string?, byte[]?) ResolveOther(MediaTrack t, int index, Func<string, MuxRefusedException> unsupported, Func<string, MuxRefusedException> missing)
    {
        string? tag = ValidTag(t);
        if (tag is null)
        {
            throw unsupported($"{(t.Codec.IsUnknown ? "the track" : t.Codec.Name)} has no ISO sample entry code; only tracks that came from an ISO file or carry one are written.");
        }

        if (t.Configuration.IsEmpty)
        {
            throw missing($"the '{tag}' track has no sample entry content to carry.");
        }

        _ = index;
        return (tag, null, t.Configuration.Data.ToArray());
    }

    // A sample entry code the track carries: four printable Latin-1 characters that the codec map reads back as the same codec.
    private static string? ValidTag(MediaTrack t)
    {
        string? tag = t.CodecTag;
        if (tag is not { Length: 4 })
        {
            return null;
        }

        foreach (char c in tag)
        {
            if (c is < ' ' or > '~' and not '\u00A9')
            {
                return null;
            }
        }

        return tag;
    }

    /// <summary>The 4-bit-aligned test used by callers that need to know the sample entry code of a plan.</summary>
    public static string EntryCode(byte[] entry) => BoxTypes.ToText(BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(4)));
}
