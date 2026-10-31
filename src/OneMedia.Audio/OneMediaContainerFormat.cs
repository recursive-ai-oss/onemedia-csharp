using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Audio;

/// <summary>
/// A OneAudio container format backed by a OneMedia reader: it opens the file with OneMedia, picks the first audio track whose
/// codec OneAudio has registered, and hands that track to the codec as a <see cref="MediaTrackPacketSource"/>. Register one with
/// <see cref="OneMediaRegistryExtensions.AddOneMedia"/>; where OneAudio also has a reader of its own for the same bytes, the one registered first wins.
/// </summary>
public abstract class OneMediaContainerFormat : global::OneAudio.IContainerFormat
{
    private readonly IMediaFormat _format;

    private protected OneMediaContainerFormat(IMediaFormat format, string name, string[] extensions)
    {
        _format = format;
        Name = name;
        Extensions = extensions;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; }

    /// <inheritdoc />
    public bool Matches(ReadOnlySpan<byte> head) => _format.Sniff(head).Confidence >= SniffConfidence.Likely;

    /// <inheritdoc />
    /// <exception cref="global::OneAudio.UnsupportedAudioFormatException">The stream is not seekable, the container has no audio track, or none has a codec that is registered and presentable.</exception>
    /// <exception cref="global::OneAudio.AudioFormatException">The container is malformed.</exception>
    public global::OneAudio.IAudioDecoder Open(Stream stream, global::OneAudio.AudioCodecRegistry registry, global::OneAudio.AudioLimits limits, bool leaveOpen, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(limits);
        if (!stream.CanSeek)
        {
            throw new global::OneAudio.UnsupportedAudioFormatException($"{Name} audio needs a seekable stream.");
        }

        // The stream stays the caller's if opening fails, so the source never closes it; the decoder closes it when asked to.
        IDemuxer demuxer;
        try
        {
            demuxer = _format.Open(new StreamMediaSource(stream, leaveOpen: true), MediaOpenOptions.Default, cancellationToken);
        }
        catch (UnsupportedMediaException e)
        {
            throw new global::OneAudio.UnsupportedAudioFormatException(e.Message);
        }
        catch (MediaException e)
        {
            throw new global::OneAudio.AudioFormatException(e.Message);
        }

        try
        {
            MediaTrack track = PickTrack(demuxer, registry);
            MediaTrackPacketSource source = MediaTrackPacketSource.Create(demuxer, track, ownsDemuxer: true);
            global::OneAudio.IAudioDecoder decoder = registry.GetCodec(source.Codec).CreateDecoder(source, limits);
            if (!leaveOpen)
            {
                source.AlsoDispose = stream;
            }

            return decoder;
        }
        catch
        {
            demuxer.Dispose();
            throw;
        }
    }

    private static MediaTrack PickTrack(IDemuxer demuxer, global::OneAudio.AudioCodecRegistry registry)
    {
        MediaTrack? best = null;
        string? why = null;
        foreach (bool defaults in new[] { true, false })
        {
            foreach (MediaTrack t in demuxer.Tracks)
            {
                if (t.Kind != TrackKind.Audio || ((t.Disposition & TrackDisposition.Default) != 0) != defaults)
                {
                    continue;
                }

                if (!MediaTrackPacketSource.IsSupported(t.Codec))
                {
                    why ??= t.Codec.IsUnknown ? $"audio codec '{t.CodecTag}' is not recognised" : $"OneAudio has no decoder for '{t.Codec}'";
                    continue;
                }

                try
                {
                    registry.GetCodec(AudioCodecMap.ToOneAudio(t.Codec));
                    best = t;
                    break;
                }
                catch (global::OneAudio.UnsupportedAudioFormatException e)
                {
                    why ??= e.Message;
                }
            }

            if (best is not null)
            {
                break;
            }
        }

        return best ?? throw new global::OneAudio.UnsupportedAudioFormatException(why ?? "The container has no audio track.");
    }
}

/// <summary>MP4, M4A, 3GP and QuickTime movies read by OneMedia's ISO BMFF reader.</summary>
public sealed class IsoBmffAudioFormat : OneMediaContainerFormat
{
    /// <summary>The shared instance.</summary>
    public static IsoBmffAudioFormat Instance { get; } = new();

    private IsoBmffAudioFormat()
        : base(new IsoBmffFormat(), "MP4 (OneMedia)", [".m4a", ".m4b", ".mp4", ".3gp", ".3g2", ".3gpp", ".mov", ".m4v"])
    {
    }
}

/// <summary>Matroska and WebM (MKA, MKV, WEBM) read by OneMedia's Matroska reader.</summary>
public sealed class MatroskaAudioFormat : OneMediaContainerFormat
{
    /// <summary>The shared instance.</summary>
    public static MatroskaAudioFormat Instance { get; } = new();

    private MatroskaAudioFormat()
        : base(new MatroskaFormat(), "Matroska (OneMedia)", [".mka", ".mkv", ".webm", ".weba"])
    {
    }
}

/// <summary>Registers OneMedia's containers with a OneAudio registry.</summary>
public static class OneMediaRegistryExtensions
{
    /// <summary>
    /// Registers the MP4/MOV/3GP and Matroska/WebM readers of OneMedia. Codecs (AAC, Opus, Vorbis, FLAC, AMR) are registered separately
    /// as usual. Register these before OneAudio's own MP4 reader if OneMedia's should open MP4 files.
    /// </summary>
    /// <param name="registry">The registry to add to.</param>
    /// <returns><paramref name="registry"/>, for chaining.</returns>
    public static global::OneAudio.AudioCodecRegistry AddOneMedia(this global::OneAudio.AudioCodecRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Add(IsoBmffAudioFormat.Instance).Add(MatroskaAudioFormat.Instance);
    }
}
