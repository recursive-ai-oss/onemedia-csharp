using OneAudio;

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
    /// is used and its packets cannot be read at the same time; disposing the decoder does not close the file.
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
    /// <exception cref="global::OneAudio.UnsupportedAudioFormatException">There is no audio track, or its codec is not registered or cannot be presented.</exception>
    /// <exception cref="InvalidOperationException">The file was opened from an asynchronous source.</exception>
    public static global::OneAudio.IAudioDecoder Decode(this MediaFile media, MediaTrack? track = null, global::OneAudio.AudioCodecRegistry? registry = null, global::OneAudio.AudioLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        IDemuxer demuxer = media.Demuxer ?? throw new InvalidOperationException("Decoding needs a file opened from a seekable source.");
        track ??= media.Audio ?? throw new global::OneAudio.UnsupportedAudioFormatException("The file has no audio track.");
        registry ??= CreateRegistry();
        MediaTrackPacketSource source = MediaTrackPacketSource.Create(demuxer, track);
        return registry.GetCodec(source.Codec).CreateDecoder(source, limits ?? global::OneAudio.AudioLimits.Default);
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
        return new AudioDecodeReport(source.Limitations, source.SampleRate, source.SignalledSampleRate, source.Channels, source.SignalledChannels);
    }

    /// <summary>
    /// Decodes an audio track like <see cref="Decode(MediaFile, MediaTrack?, global::OneAudio.AudioCodecRegistry?, global::OneAudio.AudioLimits?)"/> and says what the decode will not do.
    /// </summary>
    /// <remarks>
    /// HE-AAC (SBR, and parametric stereo where present) is returned as the core coder's audio only: OneAudio has no SBR or PS decoder, so a
    /// 44.1 kHz HE-AAC track comes out as 22.05 kHz PCM without the high band. Check <see cref="AudioDecodeReport.IsCoreOnly"/>, and read the
    /// true PCM rate from the decoder or <see cref="AudioDecodeReport.DecodedSampleRate"/>, not from <see cref="AudioTrackInfo.SampleRate"/>.
    /// </remarks>
    /// <param name="media">The file, opened from a seekable source.</param>
    /// <param name="report">What the decode will not do.</param>
    /// <param name="track">The audio track; <see cref="MediaFile.Audio"/> when null.</param>
    /// <param name="registry">Codecs to use; <see cref="CreateRegistry"/> when null.</param>
    /// <param name="limits">Limits for untrusted input; <see cref="global::OneAudio.AudioLimits.Default"/> when null.</param>
    /// <returns>A OneAudio decoder at frame 0 that yields PCM floats.</returns>
    /// <exception cref="global::OneAudio.UnsupportedAudioFormatException">There is no audio track, or its codec is not registered or cannot be presented.</exception>
    /// <exception cref="InvalidOperationException">The file was opened from an asynchronous source.</exception>
    public static global::OneAudio.IAudioDecoder Decode(this MediaFile media, out AudioDecodeReport report, MediaTrack? track = null, global::OneAudio.AudioCodecRegistry? registry = null, global::OneAudio.AudioLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        IDemuxer demuxer = media.Demuxer ?? throw new InvalidOperationException("Decoding needs a file opened from a seekable source.");
        track ??= media.Audio ?? throw new global::OneAudio.UnsupportedAudioFormatException("The file has no audio track.");
        registry ??= CreateRegistry();
        MediaTrackPacketSource source = MediaTrackPacketSource.Create(demuxer, track);
        report = new AudioDecodeReport(source.Limitations, source.SampleRate, source.SignalledSampleRate, source.Channels, source.SignalledChannels);
        return registry.GetCodec(source.Codec).CreateDecoder(source, limits ?? global::OneAudio.AudioLimits.Default);
    }
}
