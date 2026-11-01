using System.Linq;
using System.Buffers;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Codecs.Flac;
using RecursiveAI.OneMedia.Codecs.Opus;
using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Audio;

/// <summary>What OneAudio's packet interface cannot say about a track, collected while its packets are read.</summary>
[Flags]
public enum AudioSeamLimitations
{
    /// <summary>Nothing was lost.</summary>
    None = 0,

    /// <summary>A block asked to cut audio from its start (negative DiscardPadding); OneAudio has no way to receive that and the audio is kept.</summary>
    LeadingDiscardPadding = 1 << 0,

    /// <summary>Never set since DiscardPadding on any block is applied through OneAudio's per-packet trim. Kept so code written against 0.1.0-preview.2 still compiles.</summary>
    [Obsolete("DiscardPadding on any block is now applied; this flag is never set.")]
    MidStreamDiscardPadding = 1 << 1,

    /// <summary>Never set since end padding is applied for every codec through OneAudio's per-packet trim. Kept so code written against 0.1.0-preview.2 still compiles.</summary>
    [Obsolete("End DiscardPadding is now applied; this flag is never set.")]
    EndPaddingNotApplied = 1 << 2,

    /// <summary>
    /// The AAC configuration signals spectral band replication (HE-AAC), or only the container declares twice the core rate (a hint, see
    /// <see cref="MediaTrackPacketSource.SbrSignalling"/>), and OneAudio's AAC decoder does not apply SBR. It decodes the core coder
    /// only, so the PCM comes out at the core sampling rate (typically half the signalled output rate) and without the high band.
    /// <see cref="MediaTrackPacketSource.SampleRate"/> is that core rate; <see cref="MediaTrackPacketSource.SignalledSampleRate"/> is the rate the file asks for.
    /// </summary>
    SbrNotApplied = 1 << 3,

    /// <summary>
    /// The AAC configuration signals parametric stereo (HE-AACv2) and it is not applied: the decoded audio has the core's channel count
    /// (one) where the file signals two.
    /// </summary>
    PsNotApplied = 1 << 4,
}

/// <summary>What a decode of an audio track will and will not do, known before any audio is read.</summary>
/// <param name="Limitations">What OneAudio could not be told or did not apply that is already known from the track's configuration (the HE-AAC flags). Discard-padding flags are only known after reading, on <see cref="MediaTrackPacketSource.Limitations"/>.</param>
/// <param name="DecodedSampleRate">The sampling rate of the PCM the decoder returns, in Hz (0 when the container does not say).</param>
/// <param name="SignalledSampleRate">The rate the file asks for when it differs from <paramref name="DecodedSampleRate"/> (HE-AAC output rate); 0 when the same.</param>
/// <param name="DecodedChannels">The channel count of the returned PCM (0 when unknown).</param>
/// <param name="SignalledChannels">The channel count the file asks for when it differs from <paramref name="DecodedChannels"/> (HE-AACv2); 0 when the same.</param>
public readonly record struct AudioDecodeReport(AudioSeamLimitations Limitations, int DecodedSampleRate, int SignalledSampleRate, int DecodedChannels, int SignalledChannels)
{
    /// <summary>How the file signals SBR: <see cref="SbrSignalling.Explicit"/> or <see cref="SbrSignalling.BackwardCompatible"/> in the configuration, <see cref="SbrSignalling.ContainerRate"/> when only the container's doubled rate suggests it (a hint, which may be wrong), else <see cref="SbrSignalling.None"/>.</summary>
    public SbrSignalling Signalling { get; init; }

    /// <summary>True when the file signals SBR or PS, or its container declares twice the core rate, and the decoder returns the core coder's audio only.</summary>
    public bool IsCoreOnly => (Limitations & (AudioSeamLimitations.SbrNotApplied | AudioSeamLimitations.PsNotApplied)) != 0;
}

/// <summary>
/// One audio track of an opened container, presented as a OneAudio <see cref="global::OneAudio.IPacketSource"/>: codec id, container-declared
/// rate and channels, codec configuration, header packets for the codecs OneAudio reads from an Ogg-style packet stream, priming and
/// length, and seeking by frame. The packets are the container's bytes, unmodified.
/// </summary>
/// <remarks>
/// <para>Codec setup is delivered the way OneAudio's decoders expect it: AAC and AMR through <c>CodecConfig</c>; Opus, Vorbis and FLAC as
/// header packets (<c>OpusHead</c> + <c>OpusTags</c>, the three Vorbis headers, the Ogg FLAC identification packet), built from
/// Matroska CodecPrivate or the ISO <c>dOps</c>/<c>dfLa</c> boxes.</para>
/// <para>Priming for AAC and AMR comes from the container (edit list, iTunes gapless data, Matroska CodecDelay) and is counted in frames at
/// the track's rate. Opus drops its own pre-skip from <c>OpusHead</c>, so no priming is reported for it.</para>
/// </remarks>
public sealed class MediaTrackPacketSource : global::OneAudio.IPacketSource
{
    private readonly IDemuxer _demuxer;
    private readonly MediaTrack _track;
    private readonly bool _ownsDemuxer;
    private readonly bool _primingInTimestamps;
    private readonly byte[][] _headers;
    private int _nextHeader;
    private readonly int _rate;
    private int _signalledRate;
    private int _signalledChannels;
    private readonly int _priming;
    private readonly long _length;
    private readonly int _timeScale;
    private readonly int _discardRate; // output rate DiscardPadding is converted at; 0 when the codec's rate is not known here
    private long _opusEnd = -1;
    private long _opusGranule;
    private bool _disposed;

    private MediaTrackPacketSource(IDemuxer demuxer, MediaTrack track, bool ownsDemuxer, global::OneAudio.CodecId codec, byte[][] headers, ReadOnlyMemory<byte> config, int rate, int channels, int priming, long length)
    {
        _demuxer = demuxer;
        _track = track;
        _ownsDemuxer = ownsDemuxer;
        _primingInTimestamps = demuxer.Format == MediaFormatId.IsoBmff;
        Codec = codec;
        _headers = headers;
        CodecConfig = config;
        _rate = rate;
        Channels = channels;
        _priming = priming;
        _length = length;
        _timeScale = track.Timescale is > 0 and <= int.MaxValue ? (int)track.Timescale : rate;
        // DiscardPadding is nanoseconds of decoded output. Opus always decodes at 48 kHz (RFC 7845 section 5.1); the others use the container's rate.
        _discardRate = codec.Name == "opus" ? 48000 : rate > 0 ? rate : track.Audio?.SampleRate ?? 0;
        for (int i = 0; i < demuxer.Tracks.Count; i++)
        {
            demuxer.SetTrackEnabled(i, i == track.Index);
        }
    }

    /// <summary>The OneMedia codec ids this adapter can present to OneAudio's packet-based decoders.</summary>
    /// <param name="codec">The track's codec.</param>
    /// <returns>True for AAC, Opus, Vorbis, FLAC, AMR-NB and AMR-WB.</returns>
    public static bool IsSupported(CodecId codec) => codec.Name is "aac" or "opus" or "vorbis" or "flac" or "amr-nb" or "amr-wb";

    /// <summary>Presents one audio track of an open demuxer as a packet source. All other tracks are switched off on the demuxer.</summary>
    /// <param name="demuxer">The demuxer, positioned where reading should start (normally the beginning).</param>
    /// <param name="track">An audio track of <paramref name="demuxer"/>.</param>
    /// <param name="ownsDemuxer">True when disposing the source should dispose the demuxer.</param>
    /// <returns>The packet source.</returns>
    /// <exception cref="global::OneAudio.UnsupportedAudioFormatException">The track is not audio, its codec cannot be presented (see <see cref="IsSupported"/>), or its setup bytes are missing or malformed.</exception>
    public static MediaTrackPacketSource Create(IDemuxer demuxer, MediaTrack track, bool ownsDemuxer = false)
    {
        ArgumentNullException.ThrowIfNull(demuxer);
        ArgumentNullException.ThrowIfNull(track);
        if (track.Kind != TrackKind.Audio)
        {
            throw new global::OneAudio.UnsupportedAudioFormatException("The track is not an audio track.");
        }

        if (track.IsProtected)
        {
            throw new global::OneAudio.UnsupportedAudioFormatException("The track is protected; its samples cannot be decoded.");
        }

        if (!IsSupported(track.Codec))
        {
            throw new global::OneAudio.UnsupportedAudioFormatException(track.Codec.IsUnknown
                ? $"The audio codec '{track.CodecTag}' is not recognised."
                : $"OneAudio has no decoder for '{track.Codec}' (track {track.Index}).");
        }

        bool iso = demuxer.Format == MediaFormatId.IsoBmff;
        int rate = track.Audio?.SampleRate ?? 0;
        int channels = track.Audio?.Channels ?? 0;
        ReadOnlyMemory<byte> config = default;
        byte[][] headers = [];
        long rawFrames = track.Duration.IsKnown && rate > 0 ? track.Duration.Rescale(rate, MediaRounding.Floor).Ticks : -1;
        // A Matroska duration is a rounded segment figure, not a count of this track's frames, so only ISO tables give an exact length.
        if (!iso)
        {
            rawFrames = -1;
        }

        int priming = 0;
        long length = -1;
        long opusEnd = -1;
        int signalledRate = 0;
        int signalledChannels = 0;
        SbrSignalling signalling = SbrSignalling.None;
        AudioSeamLimitations limitations = AudioSeamLimitations.None;

        switch (track.Codec.Name)
        {
            case "aac":
                if (iso)
                {
                    if (!EsDescriptor.TryParse(track.Configuration.Data, out EsDescriptor? es) || es!.DecoderSpecificInfo.IsEmpty)
                    {
                        throw new global::OneAudio.AudioFormatException("An AAC track has no decoder-specific configuration.");
                    }

                    config = es.DecoderSpecificInfo;
                }
                else
                {
                    config = track.Configuration.Data.Length >= 2 ? track.Configuration.Data : SynthesizeAsc(track);
                }

                // ISO priming truncates, as OneAudio's own MP4 reader does. Matroska CodecDelay is in nanoseconds and 1024 frames at 48 kHz
                // is 21333333 ns, which truncates to 1023, so it rounds to the nearest frame.
                // ISO/IEC 14496-3 §1.6.2.1: with SBR or PS signalled, the file's sample entry and the configuration's extension rate describe the
                // output of a full HE-AAC decoder. OneAudio decodes the core coder only, so frames, priming and channels are counted at the core
                // rate and the difference is reported, never hidden.
                if (track.DescribeCodec() is { } heaac && (heaac.SbrPresent || heaac.ContainerDeclaresDoubleRate) && heaac.CoreSampleRate > 0)
                {
                    signalledRate = heaac.SampleRate;
                    signalling = heaac.SbrSignalling;
                    limitations = AudioSeamLimitations.SbrNotApplied | (heaac.PsPresent ? AudioSeamLimitations.PsNotApplied : 0);
                    rate = heaac.CoreSampleRate;
                    if (heaac.Channels > 0)
                    {
                        channels = heaac.PsPresent && heaac.Channels == 2 ? 1 : heaac.Channels;
                        signalledChannels = heaac.Channels != channels ? heaac.Channels : 0;
                    }

                    rawFrames = iso && track.Duration.IsKnown ? track.Duration.Rescale(rate, MediaRounding.Floor).Ticks : -1;
                }

                (priming, length) = TrimOf(track, rate, rawFrames, iso ? MediaRounding.Floor : MediaRounding.NearestAwayFromZero);
                break;

            case "amr-nb":
            case "amr-wb":
                config = track.Configuration.Data;
                rate = track.Codec.Name == "amr-wb" ? 16000 : 8000;
                channels = 1;
                rawFrames = iso && track.Duration.IsKnown ? track.Duration.Rescale(rate, MediaRounding.Floor).Ticks : -1;
                (priming, length) = TrimOf(track, rate, rawFrames);
                break;

            case "opus":
                headers = OpusHeaders(track, iso, out int preSkip, out int opusChannels);
                if (iso)
                {
                    // MP4 states Opus's real end in the edit list: media_time (the pre-skip) plus the segment duration.
                    (_, long editLength) = TrimOf(track, 48000, track.Duration.IsKnown ? track.Duration.Rescale(48000, MediaRounding.Floor).Ticks : -1);
                    if (editLength >= 0 && (track.Edit.Edits?.Any(e => !e.IsEmpty) ?? false))
                    {
                        opusEnd = preSkip + editLength;
                    }
                }
                rate = 0;
                channels = opusChannels;
                // OneAudio counts Opus length from the raw granule total (it subtracts the pre-skip itself).
                length = iso && track.Duration.IsKnown ? track.Duration.Rescale(48000, MediaRounding.NearestAwayFromZero).Ticks : -1;
                break;

            case "vorbis":
                if (!MatroskaCodecPrivate.TrySplitXiphLacing(track.Configuration.Data, out ReadOnlyMemory<byte>[] packets) || packets.Length != 3)
                {
                    throw new global::OneAudio.UnsupportedAudioFormatException("The Vorbis track does not carry its three headers in the form this adapter reads (Matroska CodecPrivate).");
                }

                headers = [packets[0].ToArray(), packets[1].ToArray(), packets[2].ToArray()];
                rate = 0;
                channels = 0;
                break;

            default: // flac
                headers = FlacHeaders(track, iso);
                rate = 0;
                channels = 0;
                break;
        }

        return new MediaTrackPacketSource(demuxer, track, ownsDemuxer, AudioCodecMap.ToOneAudio(track.Codec), headers, config, rate, channels, priming, length) { _opusEnd = opusEnd, _signalledRate = signalledRate, _signalledChannels = signalledChannels, SbrSignalling = signalling, Limitations = limitations };
    }

    // Priming and length in frames at the track's rate, from what the container states. With an edit list the first non-empty edit gives both
    // (media_time is the priming, segment_duration the length; ISO/IEC 14496-12 §8.6.6); when the iTunes gapless tag replaced the priming, or
    // there is no edit list, the length is the media's frames less priming and end padding. Frame counts truncate, as OneAudio's MP4 reader does.
    private static (int Priming, long Length) TrimOf(MediaTrack track, int rate, long rawFrames, MediaRounding primingRounding = MediaRounding.Floor)
    {
        if (rate <= 0)
        {
            return (0, -1);
        }

        long priming = track.Edit.Priming.IsKnown ? track.Edit.Priming.Rescale(rate, primingRounding).Ticks : 0;
        long padding = track.Edit.EndPadding.IsKnown ? track.Edit.EndPadding.Rescale(rate, MediaRounding.Floor).Ticks : 0;
        if (rawFrames < 0)
        {
            return ((int)Math.Min(priming, int.MaxValue), -1);
        }

        foreach (EditSegment e in track.Edit.Edits ?? [])
        {
            if (e.IsEmpty)
            {
                continue;
            }

            long editStart = e.MediaStart.Rescale(rate, MediaRounding.Floor).Ticks;
            if (editStart == priming)
            {
                priming = Math.Clamp(priming, 0, rawFrames);
                long room = rawFrames - priming;
                long segment = e.Duration.Ticks > 0 ? e.Duration.Rescale(rate, MediaRounding.Floor).Ticks : room;
                return ((int)Math.Min(priming, int.MaxValue), Math.Clamp(segment, 0, room));
            }

            break;
        }

        priming = Math.Clamp(priming, 0, rawFrames);
        return ((int)Math.Min(priming, int.MaxValue), Math.Max(0, rawFrames - priming - padding));
    }

    // Old Matroska files (A_AAC/MPEG4/LC and the like) can omit CodecPrivate; the AudioSpecificConfig is then rebuilt from the track's
    // object type (named by the CodecID), rate and channels (ISO/IEC 14496-3 §1.6.2.1: 5 bits object type, 4 bits rate index, 4 bits channels).
    private static byte[] SynthesizeAsc(MediaTrack track)
    {
        int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        int index = Array.IndexOf(rates, track.Audio?.SampleRate ?? 0);
        int channels = track.Audio?.Channels ?? 0;
        string tag = track.CodecTag ?? "";
        int objectType = tag.Contains("MAIN", StringComparison.Ordinal) ? 1 : tag.Contains("SSR", StringComparison.Ordinal) ? 3 : tag.Contains("LTP", StringComparison.Ordinal) ? 4 : 2;
        if (index < 0 || channels is < 1 or > 7)
        {
            throw new global::OneAudio.AudioFormatException("The AAC track has no CodecPrivate and its rate or channel count does not allow rebuilding one.");
        }

        int bits = (objectType << 11) | (index << 7) | (channels << 3);
        return [(byte)(bits >> 8), (byte)bits];
    }

    private static byte[][] OpusHeaders(MediaTrack track, bool iso, out int preSkip, out int channels)
    {
        OpusHead? head;
        bool ok = iso
            ? OpusHead.TryParseDOps(track.Configuration.Data, out head)
            : OpusHead.TryParseOpusHead(track.Configuration.Data, out head);
        if (!ok || head is null)
        {
            throw new global::OneAudio.AudioFormatException("The Opus track's setup bytes (OpusHead or dOps) are missing or malformed.");
        }

        preSkip = head.PreSkip;
        channels = head.ChannelCount;
        ArrayBufferWriter<byte> w = new();
        head.WriteOpusHead(w);
        // RFC 7845 §5.2: OpusTags with an empty vendor string and no comments; containers other than Ogg do not carry the packet.
        byte[] tags = [(byte)'O', (byte)'p', (byte)'u', (byte)'s', (byte)'T', (byte)'a', (byte)'g', (byte)'s', 0, 0, 0, 0, 0, 0, 0, 0];
        return [w.WrittenSpan.ToArray(), tags];
    }

    private static byte[][] FlacHeaders(MediaTrack track, bool iso)
    {
        ReadOnlyMemory<byte> blocks;
        bool ok = iso
            ? FlacConfig.TryGetBlocksFromDfLa(track.Configuration.Data, out blocks)
            : FlacConfig.TryGetBlocksFromCodecPrivate(track.Configuration.Data, out blocks);
        if (!ok || blocks.Length < 4 + FlacStreamInfo.BodyLength)
        {
            throw new global::OneAudio.AudioFormatException("The FLAC track's setup bytes (STREAMINFO) are missing or malformed.");
        }

        // Ogg FLAC mapping: 0x7F "FLAC", version 1.0, header packet count (0 here: only STREAMINFO is passed), "fLaC", then STREAMINFO's block.
        byte[] id = new byte[13 + 4 + FlacStreamInfo.BodyLength];
        id[0] = 0x7F;
        "FLAC"u8.CopyTo(id.AsSpan(1));
        id[5] = 1;
        "fLaC"u8.CopyTo(id.AsSpan(9));
        blocks.Span[..(4 + FlacStreamInfo.BodyLength)].CopyTo(id.AsSpan(13));
        id[13] &= 0x7F;
        return [id];
    }

    /// <inheritdoc />
    public global::OneAudio.CodecId Codec { get; }

    /// <inheritdoc />
    public long LengthFrames => _length;

    /// <inheritdoc />
    public bool CanSeek => _demuxer.CanSeek;

    /// <inheritdoc />
    public ReadOnlyMemory<byte> CodecConfig { get; }

    /// <inheritdoc />
    public int SampleRate => _rate;

    /// <inheritdoc />
    public int Channels { get; }

    /// <inheritdoc />
    public int TimeScale => _timeScale;

    /// <inheritdoc />
    public int PrimingFrames => _priming;

    /// <summary>An object to dispose after the demuxer when this source is disposed (the stream a container format was opened over).</summary>
    internal IDisposable? AlsoDispose { get; set; }

    // Set when the source reads a MediaFile's demuxer: a later read of the file supersedes this source, which then fails loudly
    // instead of returning packets from a moved position.
    internal MediaFile? Owner { get; set; }

    internal int Generation { get; set; }

    /// <summary>The track this source reads.</summary>
    public MediaTrack Track => _track;

    /// <summary>
    /// The sampling rate the file asks for when it differs from the rate OneAudio decodes at: the output rate of an HE-AAC configuration.
    /// Zero when the two are the same. <see cref="SampleRate"/> is always the rate of the PCM the decoder returns.
    /// </summary>
    public int SignalledSampleRate => _signalledRate;

    /// <summary>How the file signals SBR for an HE-AAC track: explicitly, by the backward-compatible extension, or only by a container rate twice the core's (a hint). <see cref="SbrSignalling.None"/> for every other track.</summary>
    public SbrSignalling SbrSignalling { get; private init; }

    /// <summary>The channel count the file asks for when it differs from <see cref="Channels"/> (HE-AACv2 parametric stereo); zero when the same.</summary>
    public int SignalledChannels => _signalledChannels;

    /// <summary>
    /// What OneAudio could not be told about the track, or did not apply. The HE-AAC flags are set as soon as the source is created; the
    /// discard-padding flags are collected while packets are read.
    /// </summary>
    public AudioSeamLimitations Limitations { get; private set; }

    /// <inheritdoc />
    public bool TryReadPacket(out global::OneAudio.PacketView packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Owner?.EnsureCurrentRead(Generation);
        if (_nextHeader < _headers.Length)
        {
            packet = new global::OneAudio.PacketView(_headers[_nextHeader++], -1, true);
            return true;
        }

        while (_demuxer.TryReadPacket(out PacketView view))
        {
            PacketInfo info = view.Info;
            if (info.TrackIndex != _track.Index)
            {
                continue;
            }

            MediaTime stamp = info.Dts.IsKnown ? info.Dts : info.Pts;
            long ts = stamp.IsKnown ? stamp.Rescale(_timeScale, MediaRounding.NearestAwayFromZero).Ticks : 0;
            long dur = info.Duration.IsKnown ? info.Duration.Rescale(_timeScale, MediaRounding.NearestAwayFromZero).Ticks : 0;
            long end = EndFrameOf(info, view.Data);
            ReadOnlySpan<byte> payload = view.Data;
            if (_demuxer.Format == MediaFormatId.Adts && payload.Length > 7)
            {
                // The ADTS demuxer delivers frames as stored; the decoder is configured by the AudioSpecificConfig, so it gets the raw data block
                // (ISO/IEC 13818-7 §6.2: 7-byte header, 9 with the CRC when protection_absent is 0).
                payload = payload[((payload[1] & 1) == 0 ? 9 : 7)..];
            }

            packet = new global::OneAudio.PacketView(payload, end, false, ts, dur, DiscardEndFramesOf(info));
            return true;
        }

        packet = default;
        return false;
    }

    // Matroska DiscardPadding (RFC 9559 section 5.1.3.5.7) is the nanoseconds of decoded audio to drop from the end of the block, on any block of
    // the track, not only the last. OneAudio takes it per packet as DiscardEndFrames, which trims the decoded output of that packet and leaves
    // the codec's state and overlap alone. Nanoseconds become frames at the output rate, rounded to the nearest frame (halves away from zero).
    // A negative value asks to cut the start of the block, which the packet interface cannot carry: the audio is kept and the limitation noted.
    private int DiscardEndFramesOf(PacketInfo info)
    {
        if (!info.DiscardPadding.IsKnown || info.DiscardPadding.Ticks == 0)
        {
            return 0;
        }

        if (info.DiscardPadding.Ticks < 0)
        {
            Limitations |= AudioSeamLimitations.LeadingDiscardPadding;
            return 0;
        }

        if (_discardRate <= 0)
        {
            return 0;
        }

        return (int)Math.Min(info.DiscardPadding.Rescale(_discardRate, MediaRounding.NearestAwayFromZero).Ticks, int.MaxValue);
    }

    // The end position of the stream where the container states one for the packet: Opus in MP4 from the edit list, and AAC/AMR in MP4 from
    // the movie timeline. Other packets return -1.
    private long EndFrameOf(PacketInfo info, ReadOnlySpan<byte> data)
    {
        if (Codec.Name == "opus" && _opusEnd >= 0 && _opusGranule >= 0)
        {
            if (!OpusToc.TryGetSamples(data, out int samples))
            {
                _opusGranule = -1;
                return -1;
            }

            _opusGranule += samples;
            return _opusGranule > _opusEnd ? _opusEnd : -1;
        }

        if (_length >= 0 && _rate > 0 && _primingInTimestamps && Codec.Name is "aac" or "amr-nb" or "amr-wb")
        {
            MediaTime endTime = info.Pts.Add(info.Duration, MediaRounding.NearestAwayFromZero);
            if (endTime.IsKnown)
            {
                long frames = endTime.Rescale(_rate, MediaRounding.NearestAwayFromZero).Ticks - _priming;
                return Math.Clamp(frames, 0, _length);
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public void SeekToFrame(long frame, out long startFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Owner?.EnsureCurrentRead(Generation);
        if (!_demuxer.CanSeek)
        {
            throw new NotSupportedException("The container cannot seek.");
        }

        int rate = _rate > 0 ? _rate : 48000;
        _nextHeader = _headers.Length;
        _opusGranule = -1; // the running position is lost; the end limit is not applied after a seek
        if (_length >= 0 && frame >= _length)
        {
            _demuxer.Seek(new MediaTime(long.MaxValue / 4, 1), SeekMode.PreviousKeyframe);
            startFrame = _length;
            return;
        }

        long offset = _primingInTimestamps ? _priming : 0;
        MediaTime target = new(frame + offset, rate);
        MediaTime landed = _demuxer.Seek(target, SeekMode.PreviousKeyframe);
        startFrame = landed.IsKnown ? landed.Rescale(rate, MediaRounding.Floor).Ticks - offset : _length;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsDemuxer)
        {
            _demuxer.Dispose();
        }

        AlsoDispose?.Dispose();
    }
}
