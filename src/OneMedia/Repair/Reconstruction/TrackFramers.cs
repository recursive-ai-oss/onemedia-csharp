using RecursiveAI.OneMedia.Codecs;

namespace RecursiveAI.OneMedia.Repair.Reconstruction;

/// <summary>Outcome of trying to read one sample at a position.</summary>
internal enum FrameStatus
{
    /// <summary>The bytes are not the start of a sample of this track.</summary>
    No,

    /// <summary>The bytes look like a sample that runs past what was offered, and the source has more: offer more.</summary>
    NeedMore,

    /// <summary>The bytes look like a sample that the end of the input cuts off.</summary>
    Partial,

    /// <summary>A whole sample starts here.</summary>
    Ok,
}

/// <summary>What a framer learned about the sample it accepted.</summary>
internal struct FrameInfo
{
    /// <summary>Length in bytes.</summary>
    public int Length;

    /// <summary>The sample is a random-access point.</summary>
    public bool Key;

    /// <summary>For audio, the PCM frames per channel the sample holds (0 for video).</summary>
    public int Samples;
}

/// <summary>
/// Reads one sample of one track at the start of a buffer. Framers only parse headers (NAL units and their slice headers, OBU headers,
/// audio frame headers); they never decode a sample. <see cref="TryFrame"/> has no side effects: it is called on candidate positions
/// that may turn out to be something else.
/// </summary>
internal abstract class TrackFramer
{
    /// <summary>Output track index, assigned when the track is declared.</summary>
    public int Track { get; set; } = -1;

    public abstract TrackKind Kind { get; }

    /// <summary>Largest sample the framer will accept.</summary>
    public int MaxSample { get; set; } = 64 << 20;

    /// <summary>Examines the bytes at a candidate position.</summary>
    /// <param name="data">Bytes from the candidate position on, as many as the walker could offer.</param>
    /// <param name="atEnd">True when <paramref name="data"/> reaches the end of the scanned span, so nothing more can be offered.</param>
    /// <param name="frame">The sample when the status is <see cref="FrameStatus.Ok"/>.</param>
    public abstract FrameStatus TryFrame(ReadOnlySpan<byte> data, bool atEnd, out FrameInfo frame);

    /// <summary>Called with each accepted sample, in file order: learns parameter sets that travel in the stream.</summary>
    public virtual void Accept(ReadOnlySpan<byte> sample)
    {
    }
}

/// <summary>Audio samples that are one whole frame of a self-delimiting codec: AMR, MPEG audio, AC-3, and AAC through a plug-in probe.</summary>
internal sealed class AudioFramer : TrackFramer
{
    private readonly Func<ReadOnlySpan<byte>, (AudioFrameResult Result, int Length)> _probe;

    public AudioFramer(int samplesPerFrame, Func<ReadOnlySpan<byte>, (AudioFrameResult, int)> probe)
    {
        SamplesPerFrame = samplesPerFrame;
        _probe = probe;
    }

    public int SamplesPerFrame { get; }

    public override TrackKind Kind => TrackKind.Audio;

    public override FrameStatus TryFrame(ReadOnlySpan<byte> data, bool atEnd, out FrameInfo frame)
    {
        frame = default;
        (AudioFrameResult result, int length) = _probe(data);
        switch (result)
        {
            case AudioFrameResult.Frame when length > 0 && length <= data.Length:
                frame = new FrameInfo { Length = length, Key = true, Samples = SamplesPerFrame };
                return FrameStatus.Ok;
            case AudioFrameResult.Incomplete:
                return atEnd ? FrameStatus.Partial : FrameStatus.NeedMore;
            default:
                return FrameStatus.No;
        }
    }
}

/// <summary>
/// Raw PCM has no framing to validate: any bytes are samples. It is recognised only when nothing else is, and only with a reference file
/// that says the track exists. It claims the bytes up to the next place another track's sample starts.
/// </summary>
internal sealed class PcmFramer(int blockAlign, int packetFrames, bool fixedPackets = false) : TrackFramer
{
    /// <summary>True when the reference file's PCM packets all have one size, so a run of PCM ends on a packet boundary.</summary>
    public bool FixedPackets { get; } = fixedPackets;

    public int BlockAlign { get; } = blockAlign;

    /// <summary>Frames per packet, from the reference's typical packet.</summary>
    public int PacketFrames { get; } = packetFrames;

    public override TrackKind Kind => TrackKind.Audio;

    public override FrameStatus TryFrame(ReadOnlySpan<byte> data, bool atEnd, out FrameInfo frame)
    {
        // Fallback only; the walker drives PCM runs itself.
        frame = default;
        return FrameStatus.No;
    }
}

/// <summary>
/// GoPro GPMF timed metadata (a <c>meta</c> track whose samples are one DEVC key-length-value item): self-delimiting by its own length
/// field, so a cut file can be framed without a reference index.
/// </summary>
internal sealed class GpmfFramer : TrackFramer
{
    public override TrackKind Kind => TrackKind.Data;

    public override FrameStatus TryFrame(ReadOnlySpan<byte> data, bool atEnd, out FrameInfo frame)
    {
        frame = default;
        // GPMF format description: key (4 characters), type (1 byte), structure size (1 byte), repeat (2 bytes, big-endian); the
        // payload is size * repeat bytes padded to a multiple of 4. A sample is one top-level DEVC item.
        if (data.Length < 8)
        {
            return atEnd ? FrameStatus.No : FrameStatus.NeedMore;
        }

        if (!data[..4].SequenceEqual("DEVC"u8) || data[4] != 0)
        {
            return FrameStatus.No;
        }

        long payload = data[5] * ((data[6] << 8) | data[7]);
        long total = 8 + ((payload + 3) & ~3L);
        if (total > MaxSample || payload == 0)
        {
            return FrameStatus.No;
        }

        if (total > data.Length)
        {
            return atEnd ? FrameStatus.Partial : FrameStatus.NeedMore;
        }

        frame = new FrameInfo { Length = (int)total, Key = true };
        return FrameStatus.Ok;
    }
}

internal static class AudioFramers
{
    /// <summary>AMR in 3GP/MP4 (RFC 4867 §5.1 table of contents byte): one frame per sample, with its ToC byte.</summary>
    public static AudioFramer Amr(bool wideband) => new(Codecs.Amr.AmrFrame.SamplesPerFrame(wideband), data =>
    {
        if (data.IsEmpty)
        {
            return (AudioFrameResult.Incomplete, 0);
        }

        // RFC 4867 §5.3: F bit must be 0 inside a single-frame packet, the padding bits are zero, the frame type is a defined mode, and Q is set for a good frame.
        byte toc = data[0];
        if ((toc & 0x80) != 0 || (toc & 0x03) != 0 || (toc & 0x04) == 0 || !Codecs.Amr.AmrFrame.TryGetFrameSize(toc, wideband, out int size))
        {
            return (AudioFrameResult.NotFrame, 0);
        }

        return data.Length >= size ? (AudioFrameResult.Frame, size) : (AudioFrameResult.Incomplete, 0);
    });

    /// <summary>MPEG-1/2/2.5 audio (layers I–III): a frame header with a fixed length.</summary>
    public static AudioFramer MpegAudio(int samplesPerFrame, int sampleRate, int layer) => new(samplesPerFrame, data =>
    {
        if (data.Length < 4)
        {
            return (AudioFrameResult.Incomplete, 0);
        }

        if (!Codecs.MpegAudio.MpegAudioFrameHeader.TryParse(data, out Codecs.MpegAudio.MpegAudioFrameHeader h) || h.FrameLength <= 0 || h.SampleRate != sampleRate || h.Layer != layer)
        {
            return (AudioFrameResult.NotFrame, 0);
        }

        return data.Length >= h.FrameLength ? (AudioFrameResult.Frame, h.FrameLength) : (AudioFrameResult.Incomplete, 0);
    });

    /// <summary>AC-3 and E-AC-3: a syncframe whose size the header states (ETSI TS 102 366 §5.3).</summary>
    public static AudioFramer Ac3(int sampleRate, int samplesPerFrame) => new(samplesPerFrame, data =>
    {
        if (data.Length < 8)
        {
            return (AudioFrameResult.Incomplete, 0);
        }

        if (!Codecs.Ac3.Ac3SyncFrame.TryParse(data, out Codecs.Ac3.Ac3SyncFrame f) || f.FrameSize <= 0 || f.SampleRate != sampleRate)
        {
            return (AudioFrameResult.NotFrame, 0);
        }

        return data.Length >= f.FrameSize ? (AudioFrameResult.Frame, f.FrameSize) : (AudioFrameResult.Incomplete, 0);
    });

    /// <summary>Wraps a plug-in framer.</summary>
    public static AudioFramer FromPlugin(IAudioFramer framer) => new(framer.SamplesPerFrame, data =>
    {
        AudioFrameResult r = framer.Probe(data, out int length);
        return (r, length);
    });
}
