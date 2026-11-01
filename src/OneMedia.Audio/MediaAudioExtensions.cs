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
    /// HE-AAC (SBR, and parametric stereo where present) decodes as the core coder only, at the core sampling rate and without the high band;
    /// this overload says nothing about it. Use the overload with an <see cref="AudioDecodeReport"/> to find out.
    /// </remarks>
    /// <param name="media">The file, opened from a seekable source.</param>
    /// <param name="track">The audio track; <see cref="MediaFile.Audio"/> when null.</param>
    /// <param name="registry">Codecs to use; <see cref="CreateRegistry"/> when null.</param>
    /// <param name="limits">Limits for untrusted input; <see cref="global::OneAudio.AudioLimits.Default"/> when null.</param>
    /// <returns>A OneAudio decoder at frame 0 that yields PCM floats.</returns>
    /// <exception cref="global::OneAudio.UnsupportedAudioFormatException">There is no audio track, its codec is not registered or cannot be presented, or its AAC configuration signals SBR or PS (the message names the signalling).</exception>
    /// <exception cref="MediaReadStateException">The file was read before and its source cannot seek, so the decode cannot start from the beginning (<see cref="MediaReadStateReason.CannotRewind"/>). Once the decoder exists, reading it after a later read of the file fails with <see cref="MediaReadStateReason.Superseded"/>.</exception>
    /// <exception cref="InvalidOperationException">The file was opened from an asynchronous source.</exception>
    public static global::OneAudio.IAudioDecoder Decode(this MediaFile media, MediaTrack? track = null, global::OneAudio.AudioCodecRegistry? registry = null, global::OneAudio.AudioLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        IDemuxer demuxer = media.Demuxer ?? throw new InvalidOperationException("Decoding needs a file opened from a seekable source.");
        track ??= media.Audio ?? throw new global::OneAudio.UnsupportedAudioFormatException("The file has no audio track.");
        registry ??= CreateRegistry();
        MediaTrackPacketSource source = BeginSource(media, demuxer, track);
        return CreateDecoder(source, registry, limits);
    }

    /// <summary>
    /// True when a decoder returned by <c>Decode</c> has skipped spectral-band-replication data: the stream carries SBR (HE-AAC signalled only
    /// inside the stream) and the PCM is the core coder's audio at the core rate. Unlike <see cref="AudioDecodeReport"/> this cannot be known
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
    /// Says what decoding an audio track will not do, without decoding. For HE-AAC this works even when OneAudio refuses to build a decoder
    /// for the track (explicit or backward-compatible SBR/PS signalling), so the reason can be shown.
    /// </summary>
    /// <param name="media">The file, opened from a seekable source.</param>
    /// <param name="track">The audio track; <see cref="MediaFile.Audio"/> when null.</param>
    /// <returns>The report.</returns>
    /// <exception cref="global::OneAudio.UnsupportedAudioFormatException">There is no audio track, or its codec cannot be presented.</exception>
    /// <exception cref="InvalidOperationException">The file was opened from an asynchronous source.</exception>
    public static AudioDecodeReport DescribeDecode(this MediaFile media, MediaTrack? track = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        IDemuxer demuxer = media.Demuxer ?? throw new InvalidOperationException("Describing a decode needs a file opened from a seekable source.");
        track ??= media.Audio ?? throw new global::OneAudio.UnsupportedAudioFormatException("The file has no audio track.");
        MediaTrackPacketSource source = MediaTrackPacketSource.Create(demuxer, track);
        return ReportOf(source);
    }

    /// <summary>
    /// Decodes an audio track like <see cref="Decode(MediaFile, MediaTrack?, global::OneAudio.AudioCodecRegistry?, global::OneAudio.AudioLimits?)"/> and says what the decode will not do.
    /// </summary>
    /// <remarks>
    /// HE-AAC (SBR, and parametric stereo where present) is returned as the core coder's audio only: OneAudio has no SBR or PS decoder, so a
    /// 44.1 kHz HE-AAC track comes out as 22.05 kHz PCM without the high band. Check <see cref="AudioDecodeReport.IsCoreOnly"/> (SBR/PS signalled by the file or implied by its declared rate), <see cref="SbrDataIgnored"/> (SBR data met in the stream, known only after reading), and read the
    /// true PCM rate from the decoder or <see cref="AudioDecodeReport.DecodedSampleRate"/>, not from <see cref="AudioTrackInfo.SampleRate"/>.
    /// </remarks>
    /// <param name="media">The file, opened from a seekable source.</param>
    /// <param name="report">What the decode will not do.</param>
    /// <param name="track">The audio track; <see cref="MediaFile.Audio"/> when null.</param>
    /// <param name="registry">Codecs to use; <see cref="CreateRegistry"/> when null.</param>
    /// <param name="limits">Limits for untrusted input; <see cref="global::OneAudio.AudioLimits.Default"/> when null.</param>
    /// <returns>A OneAudio decoder at frame 0 that yields PCM floats.</returns>
    /// <exception cref="global::OneAudio.UnsupportedAudioFormatException">There is no audio track, its codec is not registered or cannot be presented, or its AAC configuration signals SBR or PS (the message names the signalling).</exception>
    /// <exception cref="MediaReadStateException">The file was read before and its source cannot seek, so the decode cannot start from the beginning (<see cref="MediaReadStateReason.CannotRewind"/>). Once the decoder exists, reading it after a later read of the file fails with <see cref="MediaReadStateReason.Superseded"/>.</exception>
    /// <exception cref="InvalidOperationException">The file was opened from an asynchronous source.</exception>
    public static global::OneAudio.IAudioDecoder Decode(this MediaFile media, out AudioDecodeReport report, MediaTrack? track = null, global::OneAudio.AudioCodecRegistry? registry = null, global::OneAudio.AudioLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        IDemuxer demuxer = media.Demuxer ?? throw new InvalidOperationException("Decoding needs a file opened from a seekable source.");
        track ??= media.Audio ?? throw new global::OneAudio.UnsupportedAudioFormatException("The file has no audio track.");
        registry ??= CreateRegistry();
        MediaTrackPacketSource source = BeginSource(media, demuxer, track);
        report = ReportOf(source);
        return CreateDecoder(source, registry, limits);
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

    // A configuration that signals SBR or PS is refused with UnsupportedAudioFormatException whatever OneAudio threw: its parser reports some
    // of them (MP4Box writes explicit signalling one bit short) as invalid, and words its own refusal as 'explicit' also for the
    // backward-compatible form, so neither says what the track signals. A OneAudio that decodes them in a later version is not blocked,
    // since the refusal only follows a failure.
    private static global::OneAudio.IAudioDecoder CreateDecoder(MediaTrackPacketSource source, global::OneAudio.AudioCodecRegistry registry, global::OneAudio.AudioLimits? limits)
    {
        try
        {
            return registry.GetCodec(source.Codec).CreateDecoder(source, limits ?? global::OneAudio.AudioLimits.Default);
        }
        catch (global::OneAudio.AudioFormatException) when (source.SbrSignalling is SbrSignalling.Explicit or SbrSignalling.BackwardCompatible)
        {
            bool ps = (source.Limitations & AudioSeamLimitations.PsNotApplied) != 0;
            string how = source.SbrSignalling == SbrSignalling.Explicit
                ? "explicitly (the AudioSpecificConfig starts with audio object type 5 or 29)"
                : "by the backward-compatible extension after the core configuration";
            throw new global::OneAudio.UnsupportedAudioFormatException(
                $"The AAC track signals {(ps ? "SBR and parametric stereo (HE-AACv2)" : "SBR (HE-AAC)")} {how}. OneAudio has no SBR or PS decoder, so it refuses the track instead of returning the core coder's audio.");
        }
    }
}
