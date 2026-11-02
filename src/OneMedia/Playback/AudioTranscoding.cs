using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Playback;

/// <summary>What <see cref="IAudioTranscoder"/> did to one audio track.</summary>
/// <param name="InputTrack">The input track (<see cref="MediaTrack.Index"/>).</param>
/// <param name="From">The codec the track had.</param>
/// <param name="To">The codec it was encoded to.</param>
/// <param name="BitRate">The target bit rate in bits per second (the encoder's setting; the stream varies around it).</param>
/// <param name="SampleRate">Samples per second of the encoded stream.</param>
/// <param name="Channels">Channels of the encoded stream.</param>
/// <param name="EncoderDelay">The samples the decoder must discard at the start (Opus pre-skip), as an exact time. The muxer states it as the first edit-list entry of an MP4 and as <c>CodecDelay</c> in Matroska.</param>
/// <param name="Encoder">A name for the encoder, for the report.</param>
public sealed record AudioTranscodeInfo(int InputTrack, CodecId From, CodecId To, long BitRate, int SampleRate, int Channels, MediaTime EncoderDelay, string Encoder);

/// <summary>An audio track being re-encoded: a packet feed (<see cref="IPacketFeed.Track"/> is the output track) plus a description for the report.</summary>
public interface IAudioTranscode : IPacketFeed
{
    /// <summary>What the transcode does; known before the first packet.</summary>
    AudioTranscodeInfo Info { get; }
}

/// <summary>
/// Re-encodes an audio track so a browser can play it. The core never decodes or encodes anything: it defines this seam, and
/// <c>RecursiveAI.OneMedia.Audio</c> supplies the implementation (<c>AudioTranscoder</c>), decoding with the family audio library and encoding to Opus.
/// </summary>
public interface IAudioTranscoder
{
    /// <summary>The codecs this transcoder can produce, most preferred first.</summary>
    IReadOnlyList<CodecId> Targets { get; }

    /// <summary>Whether a track can be transcoded to a target, without opening anything.</summary>
    /// <param name="source">The input audio track.</param>
    /// <param name="target">One of <see cref="Targets"/>.</param>
    /// <param name="refusalCode">When false, a stable lowercase code starting <c>transcode.</c> (for example <c>transcode.no-decoder</c>).</param>
    /// <param name="refusalMessage">When false, a sentence for a person.</param>
    /// <returns>True when <see cref="Begin"/> is expected to succeed.</returns>
    bool CanTranscode(MediaTrack source, CodecId target, out string refusalCode, out string refusalMessage);

    /// <summary>Starts transcoding a track. The transcoder opens its own reader on the file, so the remux reads the other tracks undisturbed.</summary>
    /// <param name="inputPath">The file the track belongs to.</param>
    /// <param name="source">The input audio track.</param>
    /// <param name="target">One of <see cref="Targets"/>.</param>
    /// <param name="bitRate">Bits per second wanted, or 0 for the transcoder's own default.</param>
    /// <param name="cancellationToken">Cancels reading and encoding.</param>
    /// <returns>The feed; the caller disposes it.</returns>
    /// <exception cref="MediaException">The track cannot be decoded or encoded after all.</exception>
    IAudioTranscode Begin(string inputPath, MediaTrack source, CodecId target, long bitRate, CancellationToken cancellationToken);
}
