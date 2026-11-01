using System.Buffers;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Codecs.Flac;
using RecursiveAI.OneMedia.Codecs.Opus;

namespace RecursiveAI.OneMedia.Matroska;

/// <summary>What the muxer decided about one track before writing it: its CodecID, its CodecPrivate and the timing facts the Tracks element carries.</summary>
internal sealed class MatroskaTrackPlan
{
    public required int Index { get; init; }

    public required MediaTrack Track { get; init; }

    public required string MatroskaCodecId { get; init; }

    public byte[] CodecPrivate { get; init; } = [];

    /// <summary>CodecDelay in nanoseconds (RFC 9559 §5.1.4.1.26), 0 for none.</summary>
    public long CodecDelayNs { get; init; }

    public long SeekPreRollNs { get; init; }

    /// <summary>True when BitDepth is written whenever the track states one: tracks kept from a Matroska file, and codecs whose depth is part of what they are.</summary>
    public bool CarriesBitDepth { get; init; }

    /// <summary>A DefaultDuration known from the codec or the declared frame rate, in nanoseconds, or 0.</summary>
    public long HintDefaultDurationNs { get; init; }

    public bool IsVideo => Track.Kind == TrackKind.Video;
}

/// <summary>
/// Chooses the Matroska CodecID and CodecPrivate for a neutral track and writes its TrackEntry (RFC 9559 §5.1.4). Setup bytes are
/// repackaged through <see cref="CodecConfigMapper"/>; a codec the target cannot name or configure is refused with a typed exception.
/// </summary>
internal static class MatroskaTrackWriter
{
    private static readonly string[] WebMIds = ["V_VP8", "V_VP9", "V_AV1", "A_VORBIS", "A_OPUS"];

    public static MatroskaTrackPlan Plan(MediaTrack t, int index, MatroskaProfile profile)
    {
        string name = $"track {index} ({(t.Codec.IsUnknown ? t.CodecTag ?? "unknown codec" : t.Codec.Name)})";
        if (t.Kind is not (TrackKind.Video or TrackKind.Audio or TrackKind.Subtitle))
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, $"{name}: Matroska carries video, audio and subtitle tracks; a {t.Kind} track is not written.", index, t.Codec);
        }

        // A track that came from Matroska keeps its own CodecID and CodecPrivate exactly (the CodecID variants, VFW and ACM wrappers
        // and codecs this library has no name for all survive a copy).
        if (t.CodecTag is { Length: > 2 } tag && tag[1] == '_' && tag[0] is 'V' or 'A' or 'S' && IsTagKind(tag[0], t.Kind))
        {
            CheckProfile(profile, tag, name, index, t.Codec);
            return new MatroskaTrackPlan
            {
                Index = index,
                Track = t,
                MatroskaCodecId = tag,
                CodecPrivate = t.Configuration.Data.ToArray(),
                // The reader took CodecDelay off the packet times (PacketTimesIncludePriming); stating it again keeps the Block timestamps as they were.
                CodecDelayNs = tag == "A_OPUS" ? OpusDelay(t, t.Configuration.Data.Span) : t.Edit.PacketTimesIncludePriming && t.Edit.Priming.IsKnown && t.Edit.Priming.Ticks > 0 ? t.Edit.Priming.Rescale(1_000_000_000, MediaRounding.NearestAwayFromZero).Ticks : 0,
                SeekPreRollNs = tag == "A_OPUS" ? 80_000_000 : 0,
                CarriesBitDepth = true,
                HintDefaultDurationNs = Hint(t),
            };
        }

        // Codecs identified by a BITMAPINFOHEADER or WAVEFORMATEX (from Matroska's VFW and ACM CodecIDs, or from AVI) keep that wrapper, which
        // is the only way Matroska names them (Matroska codec mapping: V_MS/VFW/FOURCC and A_MS/ACM, CodecPrivate being the structure).
        string? wrapped = Is(t.Configuration, "bitmapinfoheader") && t.Kind == TrackKind.Video ? "V_MS/VFW/FOURCC"
            : Is(t.Configuration, "waveformatex") && t.Kind == TrackKind.Audio ? "A_MS/ACM" : null;
        if (wrapped is not null)
        {
            CheckProfile(profile, wrapped, name, index, t.Codec);
            return new MatroskaTrackPlan { Index = index, Track = t, MatroskaCodecId = wrapped, CodecPrivate = t.Configuration.Data.ToArray(), CarriesBitDepth = true, HintDefaultDurationNs = Hint(t) };
        }

        string id = IdOf(t.Codec, name, index);
        CheckProfile(profile, id, name, index, t.Codec);
        byte[] priv = Private(t, name, index);
        long delay = 0;
        long preRoll = 0;
        if (t.Codec == CodecId.Opus)
        {
            delay = OpusDelay(t, priv);
            preRoll = 80_000_000; // RFC 7845 §4.2 / Matroska codec mapping: Opus needs 80 ms of pre-roll after a seek
        }

        return new MatroskaTrackPlan { Index = index, Track = t, MatroskaCodecId = id, CodecPrivate = priv, CodecDelayNs = delay, SeekPreRollNs = preRoll, CarriesBitDepth = t.Codec == CodecId.Flac, HintDefaultDurationNs = Hint(t) };
    }

    private static bool IsTagKind(char c, TrackKind kind) => (c, kind) is ('V', TrackKind.Video) or ('A', TrackKind.Audio) or ('S', TrackKind.Subtitle);

    private static void CheckProfile(MatroskaProfile profile, string id, string name, int index, CodecId codec)
    {
        if (profile == MatroskaProfile.WebM && Array.IndexOf(WebMIds, id) < 0)
        {
            throw new MuxRefusedException(MuxRefusalReason.UnsupportedCodec, $"{name}: WebM carries only VP8, VP9, AV1, Vorbis and Opus; {id} is not allowed.", index, codec);
        }
    }

    private static string IdOf(CodecId codec, string name, int index) => codec.Name switch
    {
        "h264" => "V_MPEG4/ISO/AVC",
        "hevc" => "V_MPEGH/ISO/HEVC",
        "vvc" => "V_MPEGI/ISO/VVC",
        "av1" => "V_AV1",
        "vp8" => "V_VP8",
        "vp9" => "V_VP9",
        "mpeg2video" => "V_MPEG2",
        "mpeg1video" => "V_MPEG1",
        "mjpeg" => "V_MJPEG",
        "mpeg4" => "V_MPEG4/ISO/ASP",
        "theora" => "V_THEORA",
        "aac" => "A_AAC",
        "mp3" => "A_MPEG/L3",
        "mp2" => "A_MPEG/L2",
        "ac3" => "A_AC3",
        "eac3" => "A_EAC3",
        "opus" => "A_OPUS",
        "vorbis" => "A_VORBIS",
        "flac" => "A_FLAC",
        "subrip" => "S_TEXT/UTF8",
        "ass" => "S_TEXT/ASS",
        _ => throw new MuxRefusedException(MuxRefusalReason.UnsupportedCodec, $"{name}: this muxer has no Matroska CodecID for the codec.", index, codec),
    };

    private static bool Is(CodecConfiguration c, string format) => string.Equals(c.Format, format, StringComparison.OrdinalIgnoreCase);

    private static bool IsAny(CodecConfiguration c, params string[] formats)
    {
        foreach (string f in formats)
        {
            if (Is(c, f))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Private(MediaTrack t, string name, int index)
    {
        CodecConfiguration c = t.Configuration;
        ReadOnlyMemory<byte> data = c.Data;
        MuxRefusedException Missing(string what) => new(data.IsEmpty ? MuxRefusalReason.MissingConfiguration : MuxRefusalReason.InvalidConfiguration, $"{name}: {(data.IsEmpty ? "no CodecPrivate" : "unusable CodecPrivate")}; {what}", index, t.Codec);
        switch (t.Codec.Name)
        {
            case "h264":
                return data.IsEmpty || !Is(c, "avcC") ? throw Missing("H.264 in Matroska needs the avcC record (ISO/IEC 14496-15) as CodecPrivate.") : data.ToArray();
            case "hevc":
                return data.IsEmpty || !Is(c, "hvcC") ? throw Missing("HEVC in Matroska needs the hvcC record as CodecPrivate.") : data.ToArray();
            case "vvc":
                return data.IsEmpty || !Is(c, "vvcC") ? throw Missing("VVC in Matroska needs the vvcC record as CodecPrivate.") : data.ToArray();
            case "av1":
                return data.IsEmpty || !Is(c, "av1C") ? throw Missing("AV1 in Matroska needs the av1C record as CodecPrivate.") : data.ToArray();
            case "vp9":
                if (data.IsEmpty)
                {
                    return [];
                }

                return Is(c, "vpcC") ? Convert(t.Codec, data, name, index) : throw Missing("the VP9 setup bytes are not a vpcC record.");
            case "aac":
                if (data.IsEmpty)
                {
                    throw Missing("AAC in Matroska needs the AudioSpecificConfig as CodecPrivate.");
                }

                if (Is(c, "esds"))
                {
                    return Convert(t.Codec, data, name, index);
                }

                return IsAny(c, "asc", "AudioSpecificConfig") && AudioSpecificConfig.TryParse(data.Span, out _) ? data.ToArray() : throw Missing("the AAC setup bytes are not an AudioSpecificConfig.");
            case "opus":
                if (data.IsEmpty)
                {
                    throw Missing("Opus in Matroska needs OpusHead as CodecPrivate.");
                }

                if (Is(c, "dOps"))
                {
                    return Convert(t.Codec, data, name, index);
                }

                return Is(c, "OpusHead") && OpusHead.TryParseOpusHead(data, out _) ? data.ToArray() : throw Missing("the Opus setup bytes are not an OpusHead.");
            case "vorbis":
                return !data.IsEmpty && IsAny(c, "CodecPrivate", "xiph-lacing") && MatroskaCodecPrivate.TrySplitXiphLacing(data, out ReadOnlyMemory<byte>[] headers) && headers.Length == 3
                    ? data.ToArray()
                    : throw Missing("Vorbis in Matroska needs the three headers in Xiph lacing as CodecPrivate.");
            case "flac":
                if (data.IsEmpty)
                {
                    throw Missing("FLAC in Matroska needs 'fLaC' and the metadata blocks as CodecPrivate.");
                }

                if (Is(c, "dfLa"))
                {
                    return Convert(t.Codec, data, name, index);
                }

                return IsAny(c, "CodecPrivate", "flac-header") && FlacConfig.TryGetBlocksFromCodecPrivate(data, out ReadOnlyMemory<byte> blocks) && FlacConfig.TryGetStreamInfo(blocks.Span, out _)
                    ? data.ToArray()
                    : throw Missing("the FLAC setup bytes are not fLaC plus metadata blocks.");
            case "mpeg4":
                if (data.IsEmpty)
                {
                    return [];
                }

                if (Is(c, "esds"))
                {
                    return EsDescriptor.TryParse(data, out EsDescriptor? es) ? es!.DecoderSpecificInfo.ToArray() : throw Missing("the MPEG-4 esds cannot be read.");
                }

                return data.ToArray();
            case "ass":
                return data.ToArray();
            default:
                // VP8, MPEG-1/2 video, MJPEG, MP2, MP3, AC-3, E-AC-3, SubRip: Matroska keeps no setup bytes for them.
                return [];
        }
    }

    private static byte[] Convert(CodecId codec, ReadOnlyMemory<byte> boxPayload, string name, int index)
    {
        ArrayBufferWriter<byte> w = new();
        ConfigMapStatus s = CodecConfigMapper.IsoToMatroska(codec, boxPayload, w);
        return s == ConfigMapStatus.Done
            ? w.WrittenMemory.ToArray()
            : throw new MuxRefusedException(MuxRefusalReason.InvalidConfiguration, $"{name}: the sample entry's setup bytes cannot be repackaged as CodecPrivate ({s}).", index, codec);
    }

    private static long OpusDelay(MediaTrack t, ReadOnlySpan<byte> opusHead)
    {
        if (t.Edit.Priming.IsKnown && t.Edit.Priming.Ticks > 0)
        {
            return t.Edit.Priming.Rescale(1_000_000_000, MediaRounding.NearestAwayFromZero).Ticks;
        }

        // RFC 7845 §5.1: pre-skip counts 48 kHz samples.
        return OpusHead.TryParseOpusHead(opusHead.ToArray(), out OpusHead? head) && head!.PreSkip > 0 ? (long)head.PreSkip * 1_000_000_000L / 48_000 : 0;
    }

    private static long Hint(MediaTrack t)
    {
        if (t.Video is { FrameRateNumerator: > 0, FrameRateDenominator: > 0 } v)
        {
            return ((long)v.FrameRateDenominator * 1_000_000_000L + v.FrameRateNumerator / 2) / v.FrameRateNumerator;
        }

        if (t.Kind == TrackKind.Audio && t.Audio is { SampleRate: > 0 } a && t.DescribeCodec() is { SamplesPerFrame: > 0 } p && t.Codec != CodecId.Opus)
        {
            long rate = p.SbrPresent && p.CoreSampleRate > 0 ? p.CoreSampleRate : a.SampleRate;
            return ((long)p.SamplesPerFrame * 1_000_000_000L + rate / 2) / rate;
        }

        return 0;
    }

    /// <summary>Writes one TrackEntry (RFC 9559 §5.1.4.1) into <paramref name="b"/>.</summary>
    public static void WriteEntry(EbmlBuffer b, MatroskaTrackPlan p, long trackNumber, ulong trackUid, long defaultDurationNs, MatroskaProfile profile)
    {
        MediaTrack t = p.Track;
        int entry = b.Begin(MatroskaIds.TrackEntry);
        b.UInt(MatroskaIds.TrackNumber, (ulong)trackNumber);
        b.UInt(MatroskaIds.TrackUid, trackUid);
        b.UInt(MatroskaIds.TrackType, t.Kind switch { TrackKind.Video => 1UL, TrackKind.Audio => 2UL, _ => 17UL });
        if ((t.Disposition & TrackDisposition.Disabled) != 0)
        {
            b.UInt(MatroskaIds.FlagEnabled, 0);
        }

        // FlagDefault is 1 when absent (RFC 9559 §5.1.4.1.4), so only "not default" is spelled out.
        if ((t.Disposition & TrackDisposition.Default) == 0)
        {
            b.UInt(MatroskaIds.FlagDefault, 0);
        }

        Flag(b, MatroskaIds.FlagForced, t.Disposition, TrackDisposition.Forced);
        Flag(b, MatroskaIds.FlagHearingImpaired, t.Disposition, TrackDisposition.HearingImpaired);
        Flag(b, MatroskaIds.FlagVisualImpaired, t.Disposition, TrackDisposition.VisualImpaired);
        Flag(b, MatroskaIds.FlagOriginal, t.Disposition, TrackDisposition.Original);
        Flag(b, MatroskaIds.FlagCommentary, t.Disposition, TrackDisposition.Commentary);
        if (t.Kind == TrackKind.Video)
        {
            // Video is never laced here; audio may be (packets that share a timestamp go back into one lace), so its default of 1 stands.
            b.UInt(MatroskaIds.FlagLacing, 0);
        }
        if (defaultDurationNs > 0)
        {
            b.UInt(MatroskaIds.DefaultDuration, (ulong)defaultDurationNs);
        }

        if (!string.IsNullOrEmpty(t.Name))
        {
            b.String(MatroskaIds.Name, t.Name);
        }

        WriteLanguage(b, t.Language);
        b.String(MatroskaIds.CodecIdElement, p.MatroskaCodecId);
        if (p.CodecPrivate.Length > 0)
        {
            b.Binary(MatroskaIds.CodecPrivate, p.CodecPrivate);
        }

        if (p.CodecDelayNs > 0)
        {
            b.UInt(MatroskaIds.CodecDelay, (ulong)p.CodecDelayNs);
        }

        if (p.SeekPreRollNs > 0)
        {
            b.UInt(MatroskaIds.SeekPreRoll, (ulong)p.SeekPreRollNs);
        }

        if (t.Kind == TrackKind.Video)
        {
            WriteVideo(b, t, profile);
        }
        else if (t.Kind == TrackKind.Audio)
        {
            WriteAudio(b, p);
        }

        b.End(entry);
    }

    private static void Flag(EbmlBuffer b, uint id, TrackDisposition d, TrackDisposition bit)
    {
        if ((d & bit) != 0)
        {
            b.UInt(id, 1);
        }
    }

    private static void WriteLanguage(EbmlBuffer b, string? language)
    {
        // Language is an ISO 639-2 code and defaults to "eng" when absent, so a track without a language says "und" (RFC 9559 §5.1.4.1.20).
        if (string.IsNullOrEmpty(language))
        {
            b.String(MatroskaIds.Language, "und");
            return;
        }

        bool plain = language.Length == 3;
        foreach (char ch in language)
        {
            plain &= ch is >= 'a' and <= 'z';
        }

        if (plain)
        {
            b.String(MatroskaIds.Language, language);
        }
        else
        {
            b.String(MatroskaIds.Language, "und");
            b.String(MatroskaIds.LanguageBcp47, language);
        }
    }

    private static void WriteVideo(EbmlBuffer b, MediaTrack t, MatroskaProfile profile)
    {
        VideoTrackInfo v = t.Video ?? default;
        int video = b.Begin(MatroskaIds.Video);
        b.UInt(MatroskaIds.PixelWidth, (ulong)Math.Max(v.Width, 0));
        b.UInt(MatroskaIds.PixelHeight, (ulong)Math.Max(v.Height, 0));
        if (v.PixelAspectNumerator > 0 && v.PixelAspectDenominator > 0 && v.PixelAspectNumerator != v.PixelAspectDenominator && v.Width > 0 && v.Height > 0)
        {
            // RFC 9559 §5.1.4.1.28.14: DisplayWidth/DisplayHeight (in pixels, DisplayUnit 0) carry the pixel aspect ratio.
            long displayWidth = ((long)v.Width * v.PixelAspectNumerator + v.PixelAspectDenominator / 2) / v.PixelAspectDenominator;
            b.UInt(MatroskaIds.DisplayWidth, (ulong)displayWidth);
            b.UInt(MatroskaIds.DisplayHeight, (ulong)v.Height);
        }

        // mkvalidator 0.6.0 rejects the whole Colour element in its webm profile (the WebM guidelines list it), so WebM output leaves it out;
        // VP9 and AV1 carry the same facts inside the bitstream.
        if (profile != MatroskaProfile.WebM && t.DescribeCodec() is { Color.IsSignalled: true } p)
        {
            int colour = b.Begin(MatroskaIds.Colour);
            b.UInt(MatroskaIds.MatrixCoefficients, (ulong)p.Color.Matrix);
            b.UInt(MatroskaIds.TransferCharacteristics, (ulong)p.Color.Transfer);
            b.UInt(MatroskaIds.ColourPrimaries, (ulong)p.Color.Primaries);
            b.UInt(MatroskaIds.Range, p.Color.FullRange ? 2UL : 1UL); // RFC 9559 §5.1.4.1.28.27: 1 broadcast, 2 full
            b.End(colour);
        }

        b.End(video);
    }

    private static void WriteAudio(EbmlBuffer b, MatroskaTrackPlan p)
    {
        MediaTrack t = p.Track;
        AudioTrackInfo a = t.Audio ?? default;
        long output = a.SampleRate;
        long sampling = output;
        if (t.Codec == CodecId.Aac && t.DescribeCodec() is { CoreSampleRate: > 0 } d && d.CoreSampleRate != output && (d.SbrPresent || d.ContainerDeclaresDoubleRate))
        {
            sampling = d.CoreSampleRate; // RFC 9559 §5.1.4.1.29.2: SamplingFrequency is the core rate, OutputSamplingFrequency what SBR doubles it to
        }

        if (p.MatroskaCodecId.EndsWith("/SBR", StringComparison.Ordinal) && output % 2 == 0 && sampling == output)
        {
            sampling = output / 2; // the legacy CodecID A_AAC/MPEG4/LC/SBR says the core runs at half the output rate
        }

        int audio = b.Begin(MatroskaIds.Audio);
        b.Float64(MatroskaIds.SamplingFrequency, sampling);
        if (output != sampling)
        {
            b.Float64(MatroskaIds.OutputSamplingFrequency, output);
        }

        b.UInt(MatroskaIds.Channels, (ulong)Math.Max(a.Channels, 1));
        if (a.BitsPerSample > 0 && p.CarriesBitDepth)
        {
            b.UInt(MatroskaIds.BitDepth, (ulong)a.BitsPerSample);
        }

        b.End(audio);
    }
}
