using OneAudio;
using OneAudio.Aac;
using OneAudio.Flac;
using RecursiveAI.OneMedia.Codecs.Pcm;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Playback;
using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Audio;

// Opening the decoded samples of one input track, shared by every transcode target.
internal static class TranscodeInput
{
    // The first packet's time anchors the audio on the timeline, so it is read before the decoder starts its own pass.
    public static ISampleSource Open(MediaFile file, MediaTrack track, out MediaTrack input, out long firstPts)
    {
        IDemuxer demuxer = file.Demuxer ?? throw new MediaReadStateException(MediaReadStateReason.AsynchronousSource, "Transcoding needs a file opened from a seekable source.");
        MediaTrack t = file.Tracks[track.Index];
        foreach (MediaTrack x in demuxer.Tracks)
        {
            demuxer.SetTrackEnabled(x.Index, x.Index == t.Index);
        }

        firstPts = 0;
        _ = file.BeginRead();
        if (demuxer.TryReadPacket(out PacketView p))
        {
            MediaTime pts = p.Info.Pts.IsKnown ? p.Info.Pts : p.Info.Dts;
            firstPts = pts.IsKnown ? pts.Rescale(t.Timescale, MediaRounding.NearestAwayFromZero).Ticks : 0;
        }

        foreach (MediaTrack x in demuxer.Tracks)
        {
            demuxer.SetTrackEnabled(x.Index, true);
        }

        input = t;
        if (t.Codec == CodecId.Pcm)
        {
            PcmSampleFormat format = IsoSampleEntries.ResolvePcm(t, Remuxer.PcmOptionsFor(demuxer, t));
            return new PcmSamples(file, t, format);
        }

        return new DecoderSamples(file.Decode(t));
    }

    // The presentation time of the first decoded sample that is kept, in `rate` ticks: the first packet's time moved by the edit information,
    // plus the priming the decoder drops.
    public static long StartTicks(MediaTrack input, long firstPts, int rate)
    {
        MediaTime priming = input.Edit.Priming.IsKnown ? input.Edit.Priming : MediaTime.Zero;
        MediaTime first = new(firstPts, input.Timescale);
        MediaTime t0 = input.PresentationTime(first).Add(priming, MediaRounding.NearestAwayFromZero);
        return t0.Rescale(rate, MediaRounding.NearestAwayFromZero).Ticks;
    }
}

// Decoded audio in, finished access units out, one at a time, so memory stays flat. A derived class owns the encoder: it turns the floats it is
// given into units (Enqueue) and, at the end of the input, flushes the rest. The last unit is held back until the end is known because the
// AAC encoder only then knows how much of it is padding.
internal abstract class EncodedTranscode : IAudioTranscode
{
    private const int ReadFrames = 4096;

    private readonly MediaFile _file;
    private readonly ISampleSource _source;
    private readonly IResampler? _resampler;
    private readonly float[] _read;
    private readonly float[] _resampled;
    private readonly Queue<(byte[] Data, int Length, long Start, int Duration)> _ready = new();
    private readonly Stack<byte[]> _free = new();
    private readonly CancellationToken _cancellation;
    private byte[] _current = [];
    private bool _eof;
    private bool _finished;
    private bool _drained;

    protected EncodedTranscode(MediaFile file, ISampleSource source, int outputRate, long start, CancellationToken cancellationToken)
    {
        _file = file;
        _source = source;
        _cancellation = cancellationToken;
        Channels = source.Channels;
        Start = start;
        _resampler = source.SampleRate == outputRate ? null : AudioResampler.Create(source.SampleRate, outputRate, Channels, ResamplerQuality.Polyphase);
        _read = new float[ReadFrames * Channels];
        _resampled = new float[(_resampler?.MaxOutputFrames(ReadFrames) ?? 0) * Channels];
    }

    public MediaTrack Track { get; protected set; } = null!;

    public AudioTranscodeInfo Info { get; protected set; } = null!;

    protected int Channels { get; }

    /// <summary>Presentation time of the first kept sample, in output-rate ticks; the first unit starts here.</summary>
    protected long Start { get; }

    /// <summary>Samples at the end of the last unit that are padding: its duration is shortened by this much, which ends the edit list there.</summary>
    protected int EndPadding { get; set; }

    /// <summary>Feeds interleaved frames at the output rate to the encoder.</summary>
    protected abstract void Feed(ReadOnlySpan<float> frames);

    /// <summary>Flushes the encoder at the end of the input.</summary>
    protected abstract void Finish();

    /// <summary>Queues one finished unit that starts <paramref name="start"/> ticks after <see cref="Start"/>.</summary>
    protected void Enqueue(ReadOnlySpan<byte> data, long start, int duration)
    {
        // At most three units are ever waiting (two held back, one being finished), so their buffers are reused rather than made per unit.
        byte[]? buffer = null;
        while (buffer is null && _free.Count > 0)
        {
            byte[] candidate = _free.Pop();
            buffer = candidate.Length >= data.Length ? candidate : null;
        }

        buffer ??= new byte[Math.Max(data.Length, 1024)];
        data.CopyTo(buffer);
        _ready.Enqueue((buffer, data.Length, start, duration));
    }

    public bool TryPeek(out MediaTime decodeTime)
    {
        if (!Fill())
        {
            decodeTime = MediaTime.Unknown;
            return false;
        }

        decodeTime = new MediaTime(Start + _ready.Peek().Start, Track.Timescale);
        return true;
    }

    public bool TryRead(out PacketView packet)
    {
        if (!Fill())
        {
            packet = default;
            return false;
        }

        if (_current.Length > 0)
        {
            _free.Push(_current);
        }

        (byte[] data, int length, long start, int duration) = _ready.Dequeue();
        if (_finished && _ready.Count == 0 && EndPadding is > 0 and < int.MaxValue && EndPadding < duration)
        {
            duration -= EndPadding;
        }

        _current = data;
        MediaTime pts = new(Start + start, Track.Timescale);
        PacketInfo info = new(Track.Index, pts, pts, new MediaTime(duration, Track.Timescale), PacketAttributes.Keyframe, ByteRange.None, length);
        packet = new PacketView(info, _current.AsSpan(0, length));
        return true;
    }

    // Makes units until two are waiting (so the last one is never handed out before the end is known) or the input is over.
    private bool Fill()
    {
        while (_ready.Count < 2 && !_drained)
        {
            _cancellation.ThrowIfCancellationRequested();
            try
            {
                if (!_eof)
                {
                    Pump();
                }
                else
                {
                    Finish();
                    _finished = true;
                    _drained = true;
                }
            }
            catch (OneAudioException e)
            {
                // The decoder met bytes it cannot decode part-way through; the caller sees a media error, not a OneAudio one.
                throw new MediaFormatException($"Audio track {Track.Index} cannot be decoded: {e.Message}");
            }
        }

        return _ready.Count > 0;
    }

    private void Pump()
    {
        int got = _source.Read(_read);
        if (got <= 0)
        {
            _eof = true;
            if (_resampler is not null)
            {
                int tail = _resampler.Flush(_resampled);
                if (tail > 0)
                {
                    Feed(_resampled.AsSpan(0, tail * Channels));
                }
            }

            return;
        }

        if (_resampler is null)
        {
            Feed(_read.AsSpan(0, got * Channels));
            return;
        }

        int done = 0;
        while (done < got)
        {
            _resampler.Process(_read.AsSpan(done * Channels, (got - done) * Channels), _resampled, out int used, out int written);
            if (written > 0)
            {
                Feed(_resampled.AsSpan(0, written * Channels));
            }

            if (used == 0 && written == 0)
            {
                break;
            }

            done += used;
        }
    }

    public virtual void Dispose()
    {
        _source.Dispose();
        _file.Dispose();
    }
}

// AAC-LC (ISO/IEC 14496-3) from OneAudio's encoder: mono or stereo at 8 to 48 kHz. The first 1,024 output samples are the transform's overlap
// with silence (priming), declared as the track's priming so the muxer writes an edit list that starts after them (ISO/IEC 14496-12 §8.6.6);
// the last unit's duration stops where the padding starts, so the edit list also ends on the last real sample.
internal sealed class AacTranscode : EncodedTranscode
{
    private static readonly int[] s_rates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000];

    private readonly AacEncoder _encoder;

    private AacTranscode(MediaFile file, ISampleSource source, MediaTrack input, long firstPts, long bitRate, int rate, AacEncoder encoder, CancellationToken ct)
        : base(file, source, rate, TranscodeInput.StartTicks(input, firstPts, rate), ct)
    {
        _encoder = encoder;
        MediaTime priming = new(AacEncoder.PrimingFrames, rate);
        Track = new MediaTrack
        {
            Index = input.Index,
            Id = input.Id,
            Kind = TrackKind.Audio,
            Codec = CodecId.Aac,
            CodecTag = "mp4a",
            Configuration = new CodecConfiguration(encoder.AudioSpecificConfig.ToArray(), "asc"),
            Timescale = rate,
            Language = input.Language,
            Name = input.Name,
            Disposition = input.Disposition,
            Audio = new AudioTrackInfo(rate, source.Channels),
            BitRate = bitRate,
            Edit = new TrackEditInfo(Priming: priming),
        };
        Info = new AudioTranscodeInfo(input.Index, input.Codec, CodecId.Aac, bitRate, rate, source.Channels, priming, "OneAudio AAC-LC encoder");
    }

    /// <summary>The rate AAC is written at: the source's when AAC-LC has it, otherwise the nearest one it has (ties go up).</summary>
    public static int OutputRate(int sourceRate)
    {
        int best = s_rates[0];
        foreach (int r in s_rates)
        {
            int d = Math.Abs(r - sourceRate);
            int bd = Math.Abs(best - sourceRate);
            if (d < bd || (d == bd && r > best))
            {
                best = r;
            }
        }

        return best;
    }

    // 64 kbit/s per channel by default; a requested rate is kept inside what the format allows at the rate (8 kbit/s per channel up to 90 % of
    // 6,144 bits per channel per block, ISO/IEC 14496-3 §4.5.3.1).
    public static int ChooseBitRate(long requested, int rate, int channels)
    {
        long wanted = requested > 0 ? requested : 64000L * channels;
        long max = (long)(0.9 * 6144.0 * channels * rate / AacEncoder.FrameLength);
        return (int)Math.Clamp(wanted, 8000L * channels, Math.Min(max, int.MaxValue));
    }

    public static AacTranscode Create(MediaFile file, MediaTrack track, long bitRate, CancellationToken cancellationToken)
    {
        ISampleSource source = TranscodeInput.Open(file, track, out MediaTrack t, out long firstPts);
        try
        {
            if (source.Channels is < 1 or > 2)
            {
                throw new UnsupportedMediaException($"The audio decodes to {source.Channels} channels; the AAC encoder takes one or two.");
            }

            int rate = OutputRate(source.SampleRate);
            int bits = ChooseBitRate(bitRate, rate, source.Channels);
            AacEncoder encoder = new(rate, source.Channels, new AacEncoderSettings { Bitrate = bits });
            return new AacTranscode(file, source, t, firstPts, bits, rate, encoder, cancellationToken);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    protected override void Feed(ReadOnlySpan<float> frames)
    {
        while (!frames.IsEmpty)
        {
            int taken = _encoder.Write(frames);
            frames = frames[(taken * Channels)..];
            if (Drain() == 0 && taken == 0)
            {
                throw new UnsupportedMediaException("The AAC encoder took no input and produced no unit.");
            }
        }
    }

    protected override void Finish()
    {
        _encoder.Finish();
        Drain();
        EndPadding = _encoder.EndPaddingFrames;
    }

    private int Drain()
    {
        int n = 0;
        while (_encoder.TryReadPacket(out AacPacket p))
        {
            Enqueue(p.Data.Span, p.Timestamp, p.Duration);
            n++;
        }

        return n;
    }
}

// FLAC frames from OneAudio's packet encoder, for the profiles that play FLAC in MP4 (`fLaC` with a `dfLa` box) and nothing the lossy targets
// give. Lossless: the sample rate is kept and no sample is dropped, so there is no priming and no padding. The `dfLa` STREAMINFO is written
// before the first frame, so it carries no total length and no MD5, which RFC 9639 §8.2 allows ("unknown").
internal sealed class FlacTranscode : EncodedTranscode
{
    private readonly FlacPacketEncoder _encoder;

    private FlacTranscode(MediaFile file, ISampleSource source, MediaTrack input, long firstPts, FlacPacketEncoder encoder, int bits, CancellationToken ct)
        : base(file, source, source.SampleRate, TranscodeInput.StartTicks(input, firstPts, source.SampleRate), ct)
    {
        _encoder = encoder;
        int rate = source.SampleRate;
        byte[] dfLa = new byte[4 + 38];
        encoder.GetStreamInfoBlock().CopyTo(dfLa, 4);
        Track = new MediaTrack
        {
            Index = input.Index,
            Id = input.Id,
            Kind = TrackKind.Audio,
            Codec = CodecId.Flac,
            CodecTag = "fLaC",
            Configuration = new CodecConfiguration(dfLa, "dfLa"),
            Timescale = rate,
            Language = input.Language,
            Name = input.Name,
            Disposition = input.Disposition,
            Audio = new AudioTrackInfo(rate, source.Channels),
            Edit = default,
        };
        Info = new AudioTranscodeInfo(input.Index, input.Codec, CodecId.Flac, 0, rate, source.Channels, MediaTime.Zero, $"OneAudio FLAC encoder ({bits}-bit, lossless)");
    }

    public static FlacTranscode Create(MediaFile file, MediaTrack track, CancellationToken cancellationToken)
    {
        ISampleSource source = TranscodeInput.Open(file, track, out MediaTrack t, out long firstPts);
        FlacPacketEncoder? encoder = null;
        try
        {
            if (source.Channels is < 1 or > 2)
            {
                throw new UnsupportedMediaException($"The audio decodes to {source.Channels} channels; the FLAC transcode takes one or two.");
            }

            // Sources wider than 16 bits keep 24; everything else, including decoded lossy audio, is written at 16.
            int bits = source.BitsPerSample > 16 ? 24 : 16;
            encoder = new FlacPacketEncoder(new AudioFormat(source.SampleRate, source.Channels), new FlacEncoderSettings { BitsPerSample = bits });
            return new FlacTranscode(file, source, t, firstPts, encoder, bits, cancellationToken);
        }
        catch
        {
            encoder?.Dispose();
            source.Dispose();
            throw;
        }
    }

    protected override void Feed(ReadOnlySpan<float> frames)
    {
        while (!frames.IsEmpty)
        {
            int taken = _encoder.Write(frames);
            frames = frames[(taken * Channels)..];
            if (Drain() == 0 && taken == 0)
            {
                throw new UnsupportedMediaException("The FLAC encoder took no input and produced no frame.");
            }
        }
    }

    protected override void Finish()
    {
        _encoder.Finish();
        Drain();
    }

    private int Drain()
    {
        int n = 0;
        while (_encoder.TryReadPacket(out FlacPacket p))
        {
            Enqueue(p.Data.Span, p.FirstSample, p.Samples);
            n++;
        }

        return n;
    }

    public override void Dispose()
    {
        _encoder.Dispose();
        base.Dispose();
    }
}
