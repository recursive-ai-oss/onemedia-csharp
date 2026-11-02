using System.Buffers;
using System.Buffers.Binary;
using OneAudio;
using OneAudio.Opus;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Opus;
using RecursiveAI.OneMedia.Codecs.Pcm;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Playback;
using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Audio;

/// <summary>
/// Re-encodes an audio track so a browser can play it: the track is decoded with OneAudio (or read directly when it is PCM) and encoded with
/// OneAudio's Opus, AAC-LC or FLAC encoder, whichever the browser profile prefers (Opus needs 48 kHz, AAC resamples only rates it lacks). Plug it into <see cref="MakePlayableOptions.AudioTranscoder"/>, or call
/// <see cref="MakePlayable"/>.
/// </summary>
/// <remarks>
/// Mono and stereo only (the encoders here are single-stream). Sources: every codec OneAudio decodes (AAC-LC, MP3, Vorbis, FLAC, Opus, AMR) and integer or float PCM.
/// AC-3, E-AC-3, ALAC and WMA have no decoder here, so such a track stays a blocker.
/// </remarks>
public sealed class AudioTranscoder : IAudioTranscoder
{
    private static readonly CodecId[] s_targets = [CodecId.Opus, CodecId.Aac, CodecId.Flac];

    private static readonly HashSet<string> s_decodable = ["aac", "mp3", "opus", "vorbis", "flac", "amr-nb", "amr-wb", "pcm"];

    /// <summary>A transcoder with the default settings.</summary>
    public static AudioTranscoder Default { get; } = new();

    /// <summary>Opus (48 kHz whatever the source rate), AAC-LC and FLAC (the source's rate, or the nearest AAC has). A browser profile's own order decides which is used (<see cref="BrowserProfile.AudioTargets"/>).</summary>
    public IReadOnlyList<CodecId> Targets => s_targets;

    /// <summary>Makes a file play in a browser profile, re-encoding audio to Opus when that is what blocks it.</summary>
    /// <param name="input">The input file.</param>
    /// <param name="output">The new file; refused if it exists.</param>
    /// <param name="profile">The browser profile.</param>
    /// <returns>As <see cref="Media.MakePlayable(string, string, BrowserProfile)"/>, with audio transcoding available.</returns>
    public static MakePlayableReport MakePlayable(string input, string output, BrowserProfile profile) =>
        PlayableMaker.MakePlayable(input, output, profile, new MakePlayableOptions { AudioTranscoder = Default });

    /// <inheritdoc />
    public bool CanTranscode(MediaTrack source, CodecId target, out string refusalCode, out string refusalMessage)
    {
        ArgumentNullException.ThrowIfNull(source);
        refusalCode = string.Empty;
        refusalMessage = string.Empty;
        if (source.Kind != TrackKind.Audio)
        {
            refusalCode = "transcode.not-audio";
            refusalMessage = "Only audio tracks are re-encoded.";
            return false;
        }

        if (target != CodecId.Opus && target != CodecId.Aac && target != CodecId.Flac)
        {
            refusalCode = "transcode.no-encoder";
            refusalMessage = $"There is no {target.Name} encoder; only Opus, AAC and FLAC.";
            return false;
        }

        if (!s_decodable.Contains(source.Codec.Name))
        {
            refusalCode = "transcode.no-decoder";
            refusalMessage = $"There is no {(source.Codec.Name.Length == 0 ? "decoder for this codec" : source.Codec.Name + " decoder")}, so the audio cannot be re-encoded.";
            return false;
        }

        if (source.Audio is { Channels: > 2 })
        {
            refusalCode = "transcode.channels";
            refusalMessage = $"The audio has {source.Audio.Value.Channels} channels; the {target.Name} encoder takes one or two.";
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public IAudioTranscode Begin(string inputPath, MediaTrack source, CodecId target, long bitRate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(source);
        if (!CanTranscode(source, target, out _, out string message))
        {
            throw new UnsupportedMediaException(message);
        }

        MediaFile file = Media.Open(inputPath);
        try
        {
            return target == CodecId.Aac ? AacTranscode.Create(file, source, bitRate, cancellationToken)
                : target == CodecId.Flac ? FlacTranscode.Create(file, source, cancellationToken)
                : OpusTranscode.Create(file, source, bitRate, cancellationToken);
        }
        catch (OneAudioException e)
        {
            file.Dispose();
            throw new UnsupportedMediaException($"Audio track {source.Index} cannot be decoded: {e.Message}");
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }
}

// Decoded audio as interleaved floats.
internal interface ISampleSource : IDisposable
{
    int SampleRate { get; }

    int Channels { get; }

    /// <summary>Bits per sample of the source when it is integer PCM, FLAC or another lossless stream that says; 0 for lossy sources.</summary>
    int BitsPerSample { get; }

    /// <summary>Reads whole frames; 0 at the end.</summary>
    int Read(Span<float> destination);
}

internal sealed class DecoderSamples(IAudioDecoder decoder) : ISampleSource
{
    public int SampleRate => decoder.Format.SampleRate;

    public int Channels => decoder.Format.Channels;

    public int BitsPerSample => decoder.Format.BitsPerSample;

    public int Read(Span<float> destination) => decoder.Read(destination);

    public void Dispose() => decoder.Dispose();
}

// Integer or float PCM packets of one track, converted to floats. The layout comes from the container reader or the sample entry code.
internal sealed class PcmSamples : ISampleSource
{
    private readonly IDemuxer _demuxer;
    private readonly int _track;
    private readonly PcmSampleFormat _format;
    private byte[] _carry = [];
    private int _carryLength;
    private bool _end;

    public PcmSamples(MediaFile file, MediaTrack track, PcmSampleFormat format)
    {
        _demuxer = file.Demuxer ?? throw new MediaReadStateException(MediaReadStateReason.AsynchronousSource, "Transcoding needs a file opened from a seekable source.");
        _track = track.Index;
        _format = format;
        SampleRate = track.Audio?.SampleRate ?? 0;
        Channels = track.Audio?.Channels ?? 0;
        if (SampleRate <= 0 || Channels <= 0 || format.BytesPerSample is < 1 or > 8)
        {
            throw new UnsupportedMediaException("The PCM track does not state a sample rate and channel count.");
        }

        foreach (MediaTrack t in _demuxer.Tracks)
        {
            _demuxer.SetTrackEnabled(t.Index, t.Index == _track);
        }

        _ = file.BeginRead();
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public int BitsPerSample => _format.Encoding == PcmEncoding.FloatingPoint ? 24 : _format.BytesPerSample * 8;

    public int Read(Span<float> destination)
    {
        int frameBytes = Channels * _format.BytesPerSample;
        int wantFrames = destination.Length / Channels;
        if (wantFrames == 0)
        {
            return 0;
        }

        // Fill the carry buffer with at least one frame, from the next packet(s) of the track.
        while (_carryLength < frameBytes && !_end)
        {
            if (!_demuxer.TryReadPacket(out PacketView p))
            {
                _end = true;
                break;
            }

            if (p.Info.TrackIndex != _track)
            {
                continue;
            }

            if (_carry.Length < _carryLength + p.Data.Length)
            {
                Array.Resize(ref _carry, _carryLength + p.Data.Length);
            }

            p.Data.CopyTo(_carry.AsSpan(_carryLength));
            _carryLength += p.Data.Length;
        }

        int frames = Math.Min(wantFrames, _carryLength / frameBytes);
        int samples = frames * Channels;
        for (int i = 0; i < samples; i++)
        {
            destination[i] = Sample(_carry.AsSpan(i * _format.BytesPerSample, _format.BytesPerSample));
        }

        int used = frames * frameBytes;
        _carry.AsSpan(used, _carryLength - used).CopyTo(_carry);
        _carryLength -= used;
        return frames;
    }

    // One sample as a float in [-1, 1): signed integers by their top bits, unsigned 8-bit around 128, IEEE floats as they are.
    private float Sample(ReadOnlySpan<byte> b)
    {
        bool big = _format.BigEndian;
        switch (_format.Encoding)
        {
            case PcmEncoding.FloatingPoint:
                return b.Length == 4
                    ? BitConverter.Int32BitsToSingle(big ? BinaryPrimitives.ReadInt32BigEndian(b) : BinaryPrimitives.ReadInt32LittleEndian(b))
                    : (float)BitConverter.Int64BitsToDouble(big ? BinaryPrimitives.ReadInt64BigEndian(b) : BinaryPrimitives.ReadInt64LittleEndian(b));
            case PcmEncoding.UnsignedInteger:
                return (b[0] - 128) / 128f;
            default:
                {
                    // Left-align the value in 32 bits, then scale by 2^31.
                    int v = 0;
                    for (int k = 0; k < b.Length; k++)
                    {
                        int byteValue = big ? b[k] : b[b.Length - 1 - k];
                        v = (v << 8) | byteValue;
                    }

                    v <<= 32 - (8 * b.Length);
                    return v / 2147483648f;
                }
        }
    }

    public void Dispose()
    {
    }
}

// Reads decoded audio, resamples to 48 kHz and cuts it into 20 ms Opus packets; packets are made as they are asked for, so memory stays flat.
internal sealed class OpusTranscode : IAudioTranscode
{
    private const int Rate = 48000;
    private const int Frame = 960; // 20 ms at 48 kHz, RFC 6716 §2.1.4
    private const int ReadFrames = 4096;

    private readonly MediaFile _file;
    private readonly ISampleSource _source;
    private readonly IResampler? _resampler;
    private readonly OpusEncoder _encoder;
    private readonly int _channels;
    private readonly int _lookahead;
    private readonly float[] _read;
    private readonly float[] _resampled;
    private readonly byte[] _packet = new byte[4000];
    private readonly CancellationToken _cancellation;
    private float[] _queue;
    private int _queued;
    private long _realFrames;
    private long _index;
    private bool _eof;
    private bool _have;
    private PacketInfo _info;
    private int _length;

    private OpusTranscode(MediaFile file, ISampleSource source, MediaTrack input, long firstPts, long bitRate, CancellationToken cancellationToken)
    {
        _file = file;
        _source = source;
        _channels = source.Channels;
        _cancellation = cancellationToken;
        if (_channels is < 1 or > 2)
        {
            throw new UnsupportedMediaException($"The audio decodes to {_channels} channels; the Opus encoder takes one or two.");
        }

        int bits = checked((int)(bitRate > 0 ? bitRate : _channels == 1 ? 64000 : 96000));
        _encoder = new OpusEncoder(new OpusEncoderSettings { Channels = _channels, BitrateBps = bits, Complexity = 7 });
        _lookahead = _encoder.LookaheadSamples;
        _resampler = source.SampleRate == Rate ? null : AudioResampler.Create(source.SampleRate, Rate, _channels, ResamplerQuality.Polyphase);
        _read = new float[ReadFrames * _channels];
        _resampled = new float[(_resampler?.MaxOutputFrames(ReadFrames) ?? 0) * _channels];
        _queue = new float[(Frame + (_resampled.Length / _channels) + ReadFrames) * _channels];

        MediaTime priming = input.Edit.Priming.IsKnown ? input.Edit.Priming : MediaTime.Zero;
        MediaTime first = new(firstPts, input.Timescale);
        // The first decoded sample sits `priming` after the first packet's media time; its presentation time is the packet's, moved by the edit
        // information (MediaTrack.PresentationTime), plus that priming, which the decoder discards.
        MediaTime t0 = input.PresentationTime(first).Add(priming, MediaRounding.NearestAwayFromZero);
        _start = t0.Rescale(Rate, MediaRounding.NearestAwayFromZero).Ticks;

        OpusHead head = new() { ChannelCount = _channels, PreSkip = _lookahead, InputSampleRate = source.SampleRate };
        ArrayBufferWriter<byte> w = new();
        head.WriteOpusHead(w);
        MediaTime preSkip = new(_lookahead, Rate);
        Track = new MediaTrack
        {
            Index = input.Index,
            Id = input.Id,
            Kind = TrackKind.Audio,
            Codec = CodecId.Opus,
            CodecTag = "Opus",
            Configuration = new CodecConfiguration(w.WrittenMemory.ToArray(), "OpusHead"),
            Timescale = Rate,
            Language = input.Language,
            Name = input.Name,
            Disposition = input.Disposition,
            Audio = new AudioTrackInfo(Rate, _channels),
            BitRate = bits,
            Edit = new TrackEditInfo(Priming: preSkip),
        };
        Info = new AudioTranscodeInfo(input.Index, input.Codec, CodecId.Opus, bits, Rate, _channels, preSkip, "OneAudio Opus encoder");
    }

    private readonly long _start;

    public MediaTrack Track { get; }

    public AudioTranscodeInfo Info { get; }

    public static OpusTranscode Create(MediaFile file, MediaTrack track, long bitRate, CancellationToken cancellationToken)
    {
        ISampleSource source = TranscodeInput.Open(file, track, out MediaTrack t, out long firstPts);
        try
        {
            return new OpusTranscode(file, source, t, firstPts, bitRate, cancellationToken);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public bool TryPeek(out MediaTime decodeTime)
    {
        if (!Fill())
        {
            decodeTime = MediaTime.Unknown;
            return false;
        }

        decodeTime = _info.Dts;
        return true;
    }

    public bool TryRead(out PacketView packet)
    {
        if (!Fill())
        {
            packet = default;
            return false;
        }

        _have = false;
        packet = new PacketView(_info, _packet.AsSpan(0, _length));
        return true;
    }

    // Makes the next packet if there is none waiting. The stream ends when the packets cover every real sample plus the encoder's look-ahead.
    private bool Fill()
    {
        if (_have)
        {
            return true;
        }

        _cancellation.ThrowIfCancellationRequested();
        while (_queued < Frame && !_eof)
        {
            try
            {
                Pull();
            }
            catch (OneAudioException e)
            {
                // The decoder met bytes it cannot decode part-way through; the caller sees a media error, not a OneAudio one.
                throw new MediaFormatException($"Audio track {Track.Index} cannot be decoded: {e.Message}");
            }
        }

        if (_queued < Frame)
        {
            if (_index * Frame >= _realFrames + _lookahead)
            {
                return false;
            }

            Array.Clear(_queue, _queued * _channels, (Frame - _queued) * _channels);
            _queued = Frame;
        }

        _length = _encoder.Encode(_queue.AsSpan(0, Frame * _channels), Frame, _packet);
        if (_length <= 0)
        {
            throw new UnsupportedMediaException("The Opus encoder produced no packet.");
        }

        _queue.AsSpan(Frame * _channels, (_queued - Frame) * _channels).CopyTo(_queue);
        _queued -= Frame;
        MediaTime pts = new(_start + (_index * Frame), Rate);
        _info = new PacketInfo(Track.Index, pts, pts, new MediaTime(Frame, Rate), PacketAttributes.Keyframe, ByteRange.None, _length);
        _index++;
        _have = true;
        return true;
    }

    private void Pull()
    {
        int got = _source.Read(_read);
        if (got <= 0)
        {
            _eof = true;
            if (_resampler is not null)
            {
                int tail = _resampler.Flush(_resampled);
                Append(_resampled, tail);
            }

            return;
        }

        if (_resampler is null)
        {
            Append(_read, got);
            return;
        }

        int done = 0;
        while (done < got)
        {
            _resampler.Process(_read.AsSpan(done * _channels, (got - done) * _channels), _resampled, out int used, out int written);
            Append(_resampled, written);
            if (used == 0 && written == 0)
            {
                break;
            }

            done += used;
        }
    }

    private void Append(float[] frames, int count)
    {
        if (count <= 0)
        {
            return;
        }

        int need = (_queued + count) * _channels;
        if (need > _queue.Length)
        {
            Array.Resize(ref _queue, need + (Frame * _channels));
        }

        frames.AsSpan(0, count * _channels).CopyTo(_queue.AsSpan(_queued * _channels));
        _queued += count;
        _realFrames += count;
    }

    public void Dispose()
    {
        _source.Dispose();
        _file.Dispose();
    }
}
