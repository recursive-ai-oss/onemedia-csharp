using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Codecs.Ac3;
using RecursiveAI.OneMedia.Codecs.MpegAudio;

namespace RecursiveAI.OneMedia.Mpeg;

/// <summary>
/// For private streams that name no codec: looks at the first bytes of the first PES packet and picks an audio framer when they are
/// ADTS, AC-3 or MPEG audio, otherwise delivers whole PES packets. Muxers that put AAC in private streams without a descriptor rely on this.
/// </summary>
internal sealed class ProbingAssembler : TrackAssembler
{
    // The decision may need to look past the first PES packet: a stream that starts mid-frame shows its first sync word later.
    private const int ProbeLimit = 8192;
    private readonly PacketQueue _queue;
    private readonly byte[] _head = new byte[ProbeLimit];
    private int _headLength;
    private readonly List<(int Start, int Length, PesStamp Stamp, bool StartsPes)> _chunks = [];
    private TrackAssembler? _inner;

    public ProbingAssembler(int index, PacketQueue queue, MediaLimits limits, DefectLog defects)
        : base(index, queue, limits, defects)
    {
        _queue = queue;
    }

    /// <summary>The kind and codec found, once decided; null before.</summary>
    public (TrackKind Kind, CodecId Codec)? Resolved { get; private set; }

    public override CodecParameters? Parameters => _inner?.Parameters;

    public override ReadOnlyMemory<byte> Configuration => _inner?.Configuration ?? default;

    public override bool IsDescribed => _inner?.IsDescribed ?? false;

    /// <summary>The inner framer, for its detected codec.</summary>
    public TrackAssembler? Inner => _inner;

    public override void Push(ReadOnlySpan<byte> data, in PesStamp stamp, bool startsPes)
    {
        if (_inner is not null)
        {
            _inner.Push(data, stamp, startsPes);
            return;
        }

        if (_chunks.Count == 0 && !startsPes)
        {
            return;
        }

        int take = Math.Min(data.Length, ProbeLimit - _headLength);
        data[..take].CopyTo(_head.AsSpan(_headLength));
        _chunks.Add((_headLength, take, stamp, startsPes));
        _headLength += take;
        if (Decide(final: _headLength >= ProbeLimit))
        {
            Replay();
            if (data.Length > take)
            {
                _inner!.Push(data[take..], default, false);
            }
        }
    }

    public override void Flush()
    {
        if (_inner is null && _chunks.Count > 0 && Decide(final: true))
        {
            Replay();
        }

        _inner?.Flush();
    }

    public override void Reset()
    {
        _inner?.Reset();
        if (_inner is null)
        {
            _headLength = 0;
            _chunks.Clear();
        }
    }

    // Hands everything buffered to the framer that was chosen, PES packet by PES packet so each keeps its timestamps.
    private void Replay()
    {
        foreach ((int start, int length, PesStamp stamp, bool starts) in _chunks)
        {
            _inner!.Push(_head.AsSpan(start, length), stamp, starts);
        }

        _chunks.Clear();
        _headLength = 0;
    }

    private bool Decide(bool final)
    {
        ReadOnlySpan<byte> h = _head.AsSpan(0, _headLength);
        CodecId codec = CodecId.Unknown;
        TrackKind kind = TrackKind.Data;
        if (AdtsHeader.TryParse(h, out _))
        {
            codec = CodecId.Aac;
            kind = TrackKind.Audio;
            _inner = new AdtsAssembler(Index, _queue, Limits, Defects);
        }
        else if (LooksLikeMpegVideo(h))
        {
            codec = CodecId.Mpeg2Video;
            kind = TrackKind.Video;
            _inner = new Mpeg2VideoAssembler(Index, _queue, Limits, Defects);
        }
        else if (AnnexBNal(h, out int first))
        {
            kind = TrackKind.Video;
            bool hevc = first is 0x40 or 0x42 or 0x44 or 0x46 or 0x4E && h.Length > 1;
            codec = hevc ? CodecId.Hevc : CodecId.H264;
            _inner = new NalVideoAssembler(hevc ? NalCodec.Hevc : NalCodec.H264, Index, _queue, Limits, Defects);
        }
        else if (FindAc3(h, out Ac3SyncFrame ac3))
        {
            codec = ac3.IsEac3 ? CodecId.Eac3 : CodecId.Ac3;
            kind = TrackKind.Audio;
            _inner = new Ac3Assembler(Index, _queue, Limits, Defects);
        }
        else if (MpegAudioFrameHeader.TryParse(h, out MpegAudioFrameHeader mp) && mp.FrameLength > 4)
        {
            codec = mp.Layer == 3 ? CodecId.Mp3 : new CodecId(mp.Layer == 2 ? "mp2" : "mp1");
            kind = TrackKind.Audio;
            _inner = new MpegAudioAssembler(Index, _queue, Limits, Defects);
        }
        else if (final)
        {
            _inner = new PesPassthroughAssembler(Index, _queue, Limits, Defects, keyframes: true);
        }
        else
        {
            return false; // nothing recognised yet: wait for more of the stream
        }

        Resolved = (kind, codec);
        return true;
    }

    // ETSI TS 102 366 §4.3.1: a frame starts with the sync word 0x0B77. A stream that begins mid-frame shows it later, so the first
    // few thousand bytes are searched; a match counts when the next frame, if buffered, starts with a sync word too.
    private static bool FindAc3(ReadOnlySpan<byte> h, out Ac3SyncFrame frame)
    {
        frame = default;
        for (int i = 0; i + 1 < h.Length; i++)
        {
            if (h[i] != 0x0B || h[i + 1] != 0x77 || !Ac3SyncFrame.TryParse(h[i..], out frame))
            {
                continue;
            }

            int next = i + frame.FrameSize;
            if (i == 0 || (next + 1 < h.Length && h[next] == 0x0B && h[next + 1] == 0x77))
            {
                return true;
            }
        }

        return false;
    }

    // ISO/IEC 13818-2 §6.2.2: a stream starts at a sequence header (0xB3), group of pictures (0xB8) or picture (0x00) start code.
    private static bool LooksLikeMpegVideo(ReadOnlySpan<byte> h)
    {
        int i = 0;
        while (i < h.Length && h[i] == 0)
        {
            i++;
        }

        return i >= 2 && i + 1 < h.Length && h[i] == 1 && h[i + 1] is 0xB3 or 0xB8 or 0x00;
    }

    // H.264 §B.1 / H.265 §B.2: an Annex B stream starts with zero bytes, 0x01, then a NAL unit header with forbidden_zero_bit 0.
    private static bool AnnexBNal(ReadOnlySpan<byte> h, out int first)
    {
        first = 0;
        int i = 0;
        while (i < h.Length && h[i] == 0)
        {
            i++;
        }

        if (i < 2 || i + 1 >= h.Length || h[i] != 1 || (h[i + 1] & 0x80) != 0)
        {
            return false;
        }

        first = h[i + 1];
        int type = first & 0x1F;
        return type is 1 or 5 or 6 or 7 or 8 or 9 || first is 0x40 or 0x42 or 0x44 or 0x46;
    }

    protected override void Scan(bool final)
    {
    }
}
