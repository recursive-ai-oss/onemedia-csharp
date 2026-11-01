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
    private const int NeededBytes = 16;
    private readonly PacketQueue _queue;
    private readonly byte[] _head = new byte[NeededBytes];
    private int _headLength;
    private PesStamp _stamp;
    private bool _haveStamp;
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

        if (startsPes)
        {
            _stamp = stamp;
            _haveStamp = true;
            _headLength = 0;
        }

        if (!_haveStamp)
        {
            return;
        }

        int take = Math.Min(data.Length, NeededBytes - _headLength);
        data[..take].CopyTo(_head.AsSpan(_headLength));
        _headLength += take;
        Decide();
        _inner!.Push(_head.AsSpan(0, _headLength), _stamp, true);
        if (data.Length > take)
        {
            _inner.Push(data[take..], default, false);
        }
    }

    private void Decide()
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
        else if (Ac3SyncFrame.TryParse(h, out Ac3SyncFrame ac3))
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
        else
        {
            _inner = new PesPassthroughAssembler(Index, _queue, Limits, Defects, keyframes: true);
        }

        Resolved = (kind, codec);
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

    public override void Flush() => _inner?.Flush();

    public override void Reset() => _inner?.Reset();

    protected override void Scan(bool final)
    {
    }
}
