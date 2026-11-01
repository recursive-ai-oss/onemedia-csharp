using System.Buffers;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Codecs.Ac3;
using RecursiveAI.OneMedia.Codecs.MpegAudio;

namespace RecursiveAI.OneMedia.Mpeg;

/// <summary>Audio carried as a run of self-delimiting frames (ADTS, AC-3/E-AC-3, MPEG audio): one packet per frame.</summary>
internal abstract class AudioFrameAssembler : TrackAssembler
{
    private long _anchor = -1;
    private long _samplesSinceAnchor;
    private bool _reportedSync;

    protected AudioFrameAssembler(int index, PacketQueue queue, MediaLimits limits, DefectLog defects)
        : base(index, queue, limits, defects)
    {
    }

    /// <summary>
    /// Reads the frame header at the start of <paramref name="data"/>. Returns the frame length, 0 when more bytes are needed,
    /// or -1 when the bytes are not a frame.
    /// </summary>
    protected abstract int TryFrame(ReadOnlySpan<byte> data, out int samples, out int rate);

    /// <summary>Called with the first complete frame, to describe the stream.</summary>
    protected abstract void DescribeFrame(ReadOnlySpan<byte> frame);

    public override void Reset()
    {
        base.Reset();
        _anchor = -1;
        _samplesSinceAnchor = 0;
    }

    protected override void Scan(bool final)
    {
        while (true)
        {
            ReadOnlySpan<byte> span = Buffered;
            if (span.IsEmpty)
            {
                return;
            }

            int length = TryFrame(span, out int samples, out int rate);
            if (length == 0)
            {
                if (final)
                {
                    Consume(span.Length);
                }

                return;
            }

            if (length < 0)
            {
                // Not at a frame: skip to the next byte that could start one (the sync word is the only way back in).
                if (!_reportedSync)
                {
                    _reportedSync = true;
                    Defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, -1, "Audio frame sync lost; skipping bytes until the next frame header.", "mpeg.audio-sync");
                }

                Consume(1);
                continue;
            }

            if (span.Length < length)
            {
                if (final)
                {
                    Consume(span.Length);
                }

                return;
            }

            if (Parameters is null)
            {
                DescribeFrame(span[..length]);
            }

            TakeStamp(Head, out PesStamp stamp, out long pos);
            long pts;
            if (stamp.HasTime)
            {
                _anchor = stamp.Pts >= 0 ? stamp.Pts : stamp.Dts;
                _samplesSinceAnchor = 0;
                pts = _anchor;
            }
            else if (_anchor >= 0 && rate > 0)
            {
                // Frames after the first in a PES: the stamp plus the samples so far, converted to ticks with a single rounding.
                pts = _anchor + (((_samplesSinceAnchor * Clock) + (rate / 2)) / rate);
            }
            else
            {
                pts = -1;
            }

            long duration = rate > 0 ? samples * Clock / rate : 0;
            Emit(span[..length], pos, PacketAttributes.Keyframe, pts, pts, duration);
            _samplesSinceAnchor += samples;
            Consume(length);
        }
    }
}

/// <summary>AAC in ADTS framing (ISO/IEC 14496-3 §1.A.2).</summary>
internal sealed class AdtsAssembler : AudioFrameAssembler
{
    public AdtsAssembler(int index, PacketQueue queue, MediaLimits limits, DefectLog defects)
        : base(index, queue, limits, defects)
    {
    }

    protected override int TryFrame(ReadOnlySpan<byte> data, out int samples, out int rate)
    {
        samples = 0;
        rate = 0;
        if (data.Length < 7)
        {
            return data.Length >= 1 && data[0] != 0xFF ? -1 : 0;
        }

        if (!AdtsHeader.TryParse(data, out AdtsHeader h))
        {
            return -1;
        }

        samples = 1024 * h.RawDataBlocks;
        rate = h.SamplingFrequency;
        return h.FrameLength;
    }

    protected override void DescribeFrame(ReadOnlySpan<byte> frame)
    {
        if (CodecParameterReader.TryDescribeSample(CodecId.Aac, frame, out CodecParameters? p) && p is not null)
        {
            Parameters = p;
            ArrayBufferWriter<byte> writer = new(8);
            if (AdtsRepackager.TryCreateConfig(frame, writer))
            {
                Configuration = writer.WrittenMemory;
            }
        }
    }
}

/// <summary>AC-3 and E-AC-3 synchronisation frames (ETSI TS 102 366 §4.3, Annex E).</summary>
internal sealed class Ac3Assembler : AudioFrameAssembler
{
    public Ac3Assembler(int index, PacketQueue queue, MediaLimits limits, DefectLog defects)
        : base(index, queue, limits, defects)
    {
    }

    protected override int TryFrame(ReadOnlySpan<byte> data, out int samples, out int rate)
    {
        samples = 0;
        rate = 0;
        if (data.Length < 8)
        {
            return data.Length >= 1 && data[0] != 0x0B ? -1 : 0;
        }

        if (!Ac3SyncFrame.TryParse(data, out Ac3SyncFrame f))
        {
            return -1;
        }

        samples = f.SamplesPerFrame;
        rate = f.SampleRate;
        return f.FrameSize;
    }

    protected override void DescribeFrame(ReadOnlySpan<byte> frame)
    {
        if (Ac3SyncFrame.TryParse(frame, out Ac3SyncFrame f))
        {
            DetectedCodec = f.IsEac3 ? CodecId.Eac3 : CodecId.Ac3;
            if (CodecParameterReader.TryDescribeSample(DetectedCodec, frame, out CodecParameters? p) && p is not null)
            {
                Parameters = p;
            }
        }
    }
}

/// <summary>MPEG-1/2 audio frames, layers I to III (ISO/IEC 11172-3 §2.4.1).</summary>
internal sealed class MpegAudioAssembler : AudioFrameAssembler
{
    public MpegAudioAssembler(int index, PacketQueue queue, MediaLimits limits, DefectLog defects)
        : base(index, queue, limits, defects)
    {
    }

    protected override int TryFrame(ReadOnlySpan<byte> data, out int samples, out int rate)
    {
        samples = 0;
        rate = 0;
        if (data.Length < 4)
        {
            return data.Length >= 1 && data[0] != 0xFF ? -1 : 0;
        }

        if (!MpegAudioFrameHeader.TryParse(data, out MpegAudioFrameHeader h) || h.FrameLength <= 4)
        {
            return -1;
        }

        samples = h.SamplesPerFrame;
        rate = h.SampleRate;
        return h.FrameLength;
    }

    protected override void DescribeFrame(ReadOnlySpan<byte> frame)
    {
        if (MpegAudioFrameHeader.TryParse(frame, out MpegAudioFrameHeader h))
        {
            DetectedCodec = h.Layer switch { 3 => CodecId.Mp3, 2 => new CodecId("mp2"), _ => new CodecId("mp1") };
            if (CodecParameterReader.TryDescribeSample(CodecId.Mp3, frame, out CodecParameters? p) && p is not null)
            {
                Parameters = p with { Codec = DetectedCodec };
            }
        }
    }
}
