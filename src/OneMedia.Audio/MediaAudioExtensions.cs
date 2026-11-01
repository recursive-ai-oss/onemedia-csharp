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
        if (track.Codec == CodecId.Mp3)
        {
            // OneAudio reads MP3 from a byte stream, so the track's frames go over as one, led by the Xing/Info frame that carries the gapless data.
            global::OneAudio.IContainerFormat mp3 = registry.Detect(OneMediaContainerFormat.Mp3Probe) ?? throw new global::OneAudio.UnsupportedAudioFormatException("MP3 is not registered with the OneAudio registry.");
            if (demuxer.CanSeek)
            {
                demuxer.Seek(MediaTime.Zero);
            }

            ReadOnlyMemory<byte> infoFrame = track.Configuration.Format == "mpeg-audio-info-frame" ? track.Configuration.Data : default;
            return mp3.Open(new PacketConcatenationStream(demuxer, track.Index, null, infoFrame, ownsDemuxer: false), registry, limits ?? global::OneAudio.AudioLimits.Default, false, default);
        }

        MediaTrackPacketSource source = MediaTrackPacketSource.Create(demuxer, track);
        return registry.GetCodec(source.Codec).CreateDecoder(source, limits ?? global::OneAudio.AudioLimits.Default);
    }
}
