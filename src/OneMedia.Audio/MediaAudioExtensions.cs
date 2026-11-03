using OneAudio;
using RecursiveAI.OneMedia.Codecs;

namespace RecursiveAI.OneMedia.Audio;

/// <summary>Decoding the audio of an opened <see cref="MediaFile"/> through OneAudio.</summary>
public static class MediaAudioExtensions
{
    /// <summary>
    /// A OneAudio registry holding every codec this adapter can present (AAC, Opus, Vorbis, FLAC, AMR-NB, AMR-WB) and OneMedia's
    /// containers. Using it links all of those codecs into a trimmed application; build your own registry to keep fewer.
    /// </summary>
    /// <returns>A new registry.</returns>
    public static global::OneAudio.AudioCodecRegistry CreateRegistry() =>
        new global::OneAudio.AudioCodecRegistry().AddOneMedia().AddAac().AddOpus().AddVorbis().AddFlac().AddAmrNb().AddAmrWb().AddMp3();

    /// <summary>
    /// Decodes an audio track of the file. The decoder reads through the file's own demuxer, so the file must stay open while the decoder
    /// is used. Each decode starts from the beginning of the file, also after <c>Packets</c> or another decode; a newer read of the file
    /// (packets, another track's decode) takes over the demuxer, and the older decoder then throws <see cref="MediaReadStateException"/> instead of
    /// returning wrong or no audio. Disposing the decoder does not close the file.
    /// </summary>
    /// <remarks>
    /// HE-AAC (SBR, and parametric stereo where present) decodes at the full output rate; <c>decoder.Format</c> is the truth for rate and
    /// channels. OneAudio still refuses a few configurations (dual-mono SBR, SBR output at 64-96 kHz with bs_start_freq other than 9,
    /// multichannel AAC, 960-sample frames, ER/scalable object types) with its typed unsupported error; unrestricted parametric stereo is decoded in the baseline profile. Use the overload with an <see cref="AudioDecodeReport"/> for the signalling.
    /// </remarks>
    /// <param name="media">The file, opened from a seekable source.</param>
    /// <param name="track">The audio track; <see cref="MediaFile.Audio"/> when null.</param>
    /// <param name="registry">Codecs to use; <see cref="CreateRegistry"/> when null.</param>
    /// <param name="limits">Limits for untrusted input; <see cref="global::OneAudio.AudioLimits.Default"/> when null.</param>
    /// <returns>A OneAudio decoder at frame 0 that yields PCM floats.</returns>
    /// <exception cref="global::OneAudio.UnsupportedAudioFormatException">There is no audio track, its codec is not registered or cannot be presented, or its AAC configuration is one OneAudio refuses (dual-mono SBR, SBR output at 64-96 kHz with bs_start_freq other than 9, multichannel AAC, 960-sample frames, or ER/scalable object types); its message says why. Unrestricted HE-AAC v2 parametric stereo is decoded in the baseline profile.</exception>
    /// <exception cref="MediaReadStateException">The file was read before and its source cannot seek, so the decode cannot start from the beginning (<see cref="MediaReadStateReason.CannotRewind"/>). Once the decoder exists, reading it after a later read of the file fails with <see cref="MediaReadStateReason.Superseded"/>.</exception>
    /// <exception cref="MediaReadStateException">The file was opened from an asynchronous source (<see cref="MediaReadStateReason.AsynchronousSource"/>).</exception>
    public static global::OneAudio.IAudioDecoder Decode(this MediaFile media, MediaTrack? track = null, global::OneAudio.AudioCodecRegistry? registry = null, global::OneAudio.AudioLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        IDemuxer demuxer = media.Demuxer ?? throw new MediaReadStateException(MediaReadStateReason.AsynchronousSource, "Decoding needs a file opened from a seekable source.");
        track ??= media.Audio ?? throw new global::OneAudio.UnsupportedAudioFormatException("The file has no audio track.");
        registry ??= CreateRegistry();
        if (track.Codec == CodecId.Mp3)
        {
            return OpenMp3(media, demuxer, track, registry, limits);
        }

        MediaTrackPacketSource source = BeginSource(media, demuxer, track);
        return CreateDecoder(source, registry, limits);
    }

    /// <summary>Decodes this audio track; the same as <see cref="Decode(MediaFile, MediaTrack?, global::OneAudio.AudioCodecRegistry?, global::OneAudio.AudioLimits?)"/> on the file that handed the track out, with the default registry and limits.</summary>
    /// <param name="track">A track obtained from <see cref="MediaFile.Audio"/> or <see cref="MediaFile.Tracks"/>.</param>
    /// <returns>A OneAudio decoder at frame 0 that yields PCM floats.</returns>
    /// <exception cref="ArgumentException">The track did not come from an open <see cref="MediaFile"/>.</exception>
    /// <exception cref="global::OneAudio.UnsupportedAudioFormatException">As for the file-level overload.</exception>
    /// <exception cref="MediaReadStateException">As for the file-level overload.</exception>
    public static global::OneAudio.IAudioDecoder Decode(this MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        MediaFile file = MediaFile.OwnerOf(track) ?? throw new ArgumentException("The track was not obtained from a MediaFile.", nameof(track));
        return file.Decode(track);
    }

    /// <summary>Decodes this audio track and says what the decode will not do; the same as <see cref="Decode(MediaFile, out AudioDecodeReport, MediaTrack?, global::OneAudio.AudioCodecRegistry?, global::OneAudio.AudioLimits?)"/> for this track, with the default registry and limits.</summary>
    /// <param name="track">A track obtained from <see cref="MediaFile.Audio"/> or <see cref="MediaFile.Tracks"/>.</param>
    /// <param name="report">What the decoder runs against what the file signals (HE-AAC rate and channels, SBR and PS).</param>
    /// <returns>A OneAudio decoder at frame 0 that yields PCM floats.</returns>
    /// <exception cref="ArgumentException">The track did not come from an open <see cref="MediaFile"/>.</exception>
    /// <exception cref="global::OneAudio.UnsupportedAudioFormatException">As for the file-level overload.</exception>
    /// <exception cref="MediaReadStateException">As for the file-level overload.</exception>
    public static global::OneAudio.IAudioDecoder Decode(this MediaTrack track, out AudioDecodeReport report)
    {
        ArgumentNullException.ThrowIfNull(track);
        MediaFile file = MediaFile.OwnerOf(track) ?? throw new ArgumentException("The track was not obtained from a MediaFile.", nameof(track));
        return file.Decode(out report, track);
    }

    /// <summary>
    /// True when a decoder returned by <c>Decode</c> has skipped spectral-band-replication data: SBR first appeared after the decoder's first 256
    /// access units, so it was opened as plain AAC-LC, and the PCM is the core coder's audio at the core rate. Unlike <see cref="AudioDecodeReport"/> this cannot be known
    /// before the data is reached, so ask again after reading; a false answer before the end of the stream proves nothing.
    /// </summary>
    /// <param name="decoder">A decoder from <c>Decode</c>.</param>
    /// <returns>True when OneAudio's AAC decoder reports that it ignored SBR data; false for other decoders.</returns>
    public static bool SbrDataIgnored(this global::OneAudio.IAudioDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        return decoder is global::OneAudio.Aac.AacDecoder aac && aac.SbrDataIgnored;
    }

    /// <summary>
    /// Says what decoding an audio track will not do, and how the file signals SBR, in the same terms as <c>Decode(out report)</c>.
    /// </summary>
    /// <remarks>
    /// The rate and channels are the decoder's own, which only OneAudio can settle (a container hint of SBR or parametric stereo that the stream
    /// does not bear out decodes at the core rate), so this opens the decoder as <see cref="Decode(MediaFile, out AudioDecodeReport, MediaTrack?, global::OneAudio.AudioCodecRegistry?, global::OneAudio.AudioLimits?)"/>
    /// does, reading the first access units, and disposes it. It therefore rewinds the file like a decode does and supersedes an earlier decoder.
    /// A configuration OneAudio refuses does not throw here: the report then holds what the file declares, and <c>Decode</c> throws.
    /// </remarks>
    /// <param name="media">The file, opened from a seekable source.</param>
    /// <param name="track">The audio track; <see cref="MediaFile.Audio"/> when null.</param>
    /// <returns>The report, equal to the one <c>Decode</c> returns for the same track.</returns>
    /// <exception cref="global::OneAudio.UnsupportedAudioFormatException">There is no audio track, or its codec cannot be presented.</exception>
    /// <exception cref="MediaReadStateException">The file was opened from an asynchronous source (<see cref="MediaReadStateReason.AsynchronousSource"/>).</exception>
    public static AudioDecodeReport DescribeDecode(this MediaFile media, MediaTrack? track = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        IDemuxer demuxer = media.Demuxer ?? throw new MediaReadStateException(MediaReadStateReason.AsynchronousSource, "Describing a decode needs a file opened from a seekable source.");
        track ??= media.Audio ?? throw new global::OneAudio.UnsupportedAudioFormatException("The file has no audio track.");
        try
        {
            using global::OneAudio.IAudioDecoder decoder = Decode(media, out AudioDecodeReport report, track);
            return report;
        }
        catch (global::OneAudio.UnsupportedAudioFormatException) when (track.Codec.Name == "aac")
        {
            // A refused AAC configuration still has a declared rate and channel count to report.
            return ReportOf(MediaTrackPacketSource.Create(demuxer, track));
        }
    }

    /// <summary>
    /// Decodes an audio track like <see cref="Decode(MediaFile, MediaTrack?, global::OneAudio.AudioCodecRegistry?, global::OneAudio.AudioLimits?)"/> and says what the decode will not do.
    /// </summary>
    /// <remarks>
    /// HE-AAC (SBR, and parametric stereo where present) is decoded at the full output rate. The report's rate and channels are the decoder's own
    /// (a container hint of SBR that the stream does not bear out decodes at the core rate, and the file's declared rate is in
    /// <see cref="AudioDecodeReport.SignalledSampleRate"/>). Call <see cref="AudioDecodeReport.AfterReading"/> after reading to learn about SBR that
    /// first appeared late in the stream (<see cref="AudioDecodeReport.IsCoreOnly"/>).
    /// </remarks>
    /// <param name="media">The file, opened from a seekable source.</param>
    /// <param name="report">What the decode will not do.</param>
    /// <param name="track">The audio track; <see cref="MediaFile.Audio"/> when null.</param>
    /// <param name="registry">Codecs to use; <see cref="CreateRegistry"/> when null.</param>
    /// <param name="limits">Limits for untrusted input; <see cref="global::OneAudio.AudioLimits.Default"/> when null.</param>
    /// <returns>A OneAudio decoder at frame 0 that yields PCM floats.</returns>
    /// <exception cref="global::OneAudio.UnsupportedAudioFormatException">There is no audio track, its codec is not registered or cannot be presented, or its AAC configuration is one OneAudio refuses (dual-mono SBR, SBR output at 64-96 kHz with bs_start_freq other than 9, multichannel AAC, 960-sample frames, or ER/scalable object types); its message says why. Unrestricted HE-AAC v2 parametric stereo is decoded in the baseline profile.</exception>
    /// <exception cref="MediaReadStateException">The file was read before and its source cannot seek, so the decode cannot start from the beginning (<see cref="MediaReadStateReason.CannotRewind"/>). Once the decoder exists, reading it after a later read of the file fails with <see cref="MediaReadStateReason.Superseded"/>.</exception>
    /// <exception cref="MediaReadStateException">The file was opened from an asynchronous source (<see cref="MediaReadStateReason.AsynchronousSource"/>).</exception>
    public static global::OneAudio.IAudioDecoder Decode(this MediaFile media, out AudioDecodeReport report, MediaTrack? track = null, global::OneAudio.AudioCodecRegistry? registry = null, global::OneAudio.AudioLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        IDemuxer demuxer = media.Demuxer ?? throw new MediaReadStateException(MediaReadStateReason.AsynchronousSource, "Decoding needs a file opened from a seekable source.");
        track ??= media.Audio ?? throw new global::OneAudio.UnsupportedAudioFormatException("The file has no audio track.");
        registry ??= CreateRegistry();
        if (track.Codec == CodecId.Mp3)
        {
            global::OneAudio.IAudioDecoder mp3Decoder = OpenMp3(media, demuxer, track, registry, limits);
            report = new AudioDecodeReport(AudioSeamLimitations.None, mp3Decoder.Format.SampleRate, 0, mp3Decoder.Format.Channels, 0);
            return mp3Decoder;
        }

        MediaTrackPacketSource source = BeginSource(media, demuxer, track);
        global::OneAudio.IAudioDecoder decoder = CreateDecoder(source, registry, limits);
        // OneAudio has looked at the stream by now, so rate and channels are the decoder's own, not the file's declaration.
        report = ReportOf(source).AfterReading(decoder);
        return decoder;
    }

    // OneAudio reads MP3 from a byte stream, so the track's frames go over as one, led by the Xing/Info frame that carries the gapless data.
    // Like the packet-source path, the decode starts from the beginning of the file and is superseded by a later read of it.
    private static global::OneAudio.IAudioDecoder OpenMp3(MediaFile media, IDemuxer demuxer, MediaTrack track, global::OneAudio.AudioCodecRegistry registry, global::OneAudio.AudioLimits? limits)
    {
        global::OneAudio.IContainerFormat mp3 = registry.Detect(OneMediaContainerFormat.Mp3Probe) ?? throw new global::OneAudio.UnsupportedAudioFormatException("MP3 is not registered with the OneAudio registry.");
        int generation = media.BeginRead();
        ReadOnlyMemory<byte> infoFrame = track.Configuration.Format == "mpeg-audio-info-frame" ? track.Configuration.Data : default;
        return mp3.Open(new PacketConcatenationStream(demuxer, track.Index, null, infoFrame, ownsDemuxer: false) { Owner = media, Generation = generation }, registry, limits ?? global::OneAudio.AudioLimits.Default, false, default);
    }

    private static AudioDecodeReport ReportOf(MediaTrackPacketSource source) =>
        new(source.Limitations, source.SampleRate, source.SignalledSampleRate, source.Channels, source.SignalledChannels) { Signalling = source.SbrSignalling };

    // The decoder reads the file's demuxer from the start, whatever was read before; a later read of the file supersedes this decoder.
    private static MediaTrackPacketSource BeginSource(MediaFile media, IDemuxer demuxer, MediaTrack track)
    {
        MediaTrackPacketSource source = MediaTrackPacketSource.Create(demuxer, track);
        source.Owner = media;
        source.Generation = media.BeginRead();
        return source;
    }

    // OneAudio refuses a configuration that signals SBR or PS with UnsupportedAudioFormatException itself (since 1.5.1, also for the
    // MP4Box form that lacks the trailing extension flag); the track's own signalling is reported by AudioDecodeReport.Signalling.
    private static global::OneAudio.IAudioDecoder CreateDecoder(MediaTrackPacketSource source, global::OneAudio.AudioCodecRegistry registry, global::OneAudio.AudioLimits? limits) =>
        registry.GetCodec(source.Codec).CreateDecoder(source, limits ?? global::OneAudio.AudioLimits.Default);
}
