using System.Buffers;
using System.Linq;
using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Codecs.Captions;

/// <summary>What to do with caption SEI messages already present in the video.</summary>
public enum ExistingCaptionPolicy
{
    /// <summary>Access units that receive new caption data lose their own caption SEI NAL units; the rest are untouched (the default).</summary>
    Replace,

    /// <summary>Every access unit loses its caption SEI NAL units, including those that receive nothing.</summary>
    RemoveAll,

    /// <summary>Existing caption SEI NAL units stay; new ones are added beside them.</summary>
    Keep,
}

/// <summary>
/// A packet feed that copies an H.264 or HEVC track and inserts ATSC caption SEI NAL units (A/72 Part 1 §6.2.3, <c>GA94</c> in a
/// <c>user_data_registered_itu_t_t35</c> message) into the access units whose presentation time has caption data. This is the one place the
/// library changes media payload bytes, and it only ever adds or removes whole SEI NAL units:
/// <list type="bullet">
/// <item>In an access unit that gets captions, one SEI NAL unit is inserted immediately before its first slice NAL unit (after any
/// delimiter and parameter sets). With <see cref="ExistingCaptionPolicy.Replace"/> or <see cref="ExistingCaptionPolicy.RemoveAll"/>, SEI NAL
/// units holding only caption messages are dropped from the access unit.</item>
/// <item>Every other NAL unit, slice data included, is copied byte for byte; length prefixes or start codes of copied NAL units are kept.</item>
/// <item>Access units without caption data are passed through unchanged (unless <see cref="ExistingCaptionPolicy.RemoveAll"/>).</item>
/// </list>
/// Use it as a <see cref="TrackReplacement"/> in <see cref="RemuxOptions.Replacements"/>; open the input a second time for the feed, because the
/// remux reads the first instance.
/// </summary>
public sealed class CaptionSeiFeed : IPacketFeed
{
    private readonly IEnumerator<OwnedPacket> _packets;
    private readonly NalCodec _codec;
    private readonly int _lengthSize;
    private readonly ExistingCaptionPolicy _policy;
    private readonly SortedList<MediaTime, IReadOnlyList<CcTriplet>> _byPts = [];
    private byte[] _buffer = new byte[4096];
    private PacketInfo _info;
    private int _length;
    private bool _have;
    private bool _done;
    private long _inserted;
    private long _removed;

    /// <summary>Creates the feed.</summary>
    /// <param name="source">A second open instance of the input (synchronous source); the feed enumerates <paramref name="track"/>'s packets from it.</param>
    /// <param name="track">The H.264 or HEVC video track, as read from <paramref name="source"/>.</param>
    /// <param name="captions">Caption data keyed by the presentation time of the access unit that carries it (<see cref="CaptionFrame.Pts"/>, compared as exact rational times).</param>
    /// <param name="policy">Handling of caption SEI already in the stream.</param>
    /// <exception cref="ArgumentException">The track is not H.264 or HEVC.</exception>
    public CaptionSeiFeed(MediaFile source, MediaTrack track, IReadOnlyList<CaptionFrame> captions, ExistingCaptionPolicy policy = ExistingCaptionPolicy.Replace)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(captions);
        CaptionExtractor? ex = CaptionExtractor.ForTrack(track);
        if (ex is null || track.Codec == CodecId.Mpeg2Video)
        {
            throw new ArgumentException("Caption write-back needs an H.264 or HEVC track.", nameof(track));
        }

        Track = track;
        _codec = track.Codec == CodecId.H264 ? NalCodec.H264 : NalCodec.Hevc;
        ReadOnlySpan<byte> cfg = track.Configuration.Data.Span;
        _lengthSize = _codec == NalCodec.H264
            ? (cfg.Length >= 5 && cfg[0] == 1 ? (cfg[4] & 3) + 1 : 0)
            : (cfg.Length >= 23 && cfg[0] == 1 ? (cfg[21] & 3) + 1 : 0);
        _policy = policy;
        foreach (CaptionFrame f in captions)
        {
            if (f.Pts.IsKnown && f.Triplets.Count > 0)
            {
                _byPts[f.Pts] = f.Triplets;
            }
        }

        _packets = source.Packets(track).GetEnumerator();
    }

    /// <inheritdoc />
    public MediaTrack Track { get; }

    /// <summary>Caption SEI NAL units added so far.</summary>
    public long InsertedCount => _inserted;

    /// <summary>Caption SEI NAL units dropped so far.</summary>
    public long RemovedCount => _removed;

    /// <inheritdoc />
    public bool TryPeek(out MediaTime decodeTime)
    {
        if (!Fill())
        {
            decodeTime = MediaTime.Unknown;
            return false;
        }

        decodeTime = _info.Dts.IsKnown ? _info.Dts : _info.Pts;
        return true;
    }

    /// <inheritdoc />
    public bool TryRead(out PacketView packet)
    {
        if (!Fill())
        {
            packet = default;
            return false;
        }

        _have = false;
        packet = new PacketView(_info, _buffer.AsSpan(0, _length));
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => _packets.Dispose();

    private bool Fill()
    {
        if (_have)
        {
            return true;
        }

        if (_done || !_packets.MoveNext())
        {
            _done = true;
            return false;
        }

        using OwnedPacket p = _packets.Current;
        MediaTime pts = p.Info.Pts.IsKnown ? p.Info.Pts : p.Info.Dts;
        _byPts.TryGetValue(pts, out IReadOnlyList<CcTriplet>? triplets);
        byte[]? sei = triplets is null ? null : BuildSei(triplets);
        ReadOnlySpan<byte> src = p.Data.Span;
        bool drop = _policy == ExistingCaptionPolicy.RemoveAll || (_policy == ExistingCaptionPolicy.Replace && sei is not null);
        if (sei is null && !drop)
        {
            Ensure(src.Length);
            src.CopyTo(_buffer);
            _length = src.Length;
        }
        else
        {
            Rewrite(src, sei, drop);
        }

        _info = p.Info with { Size = _length, SourceRange = (sei is null && !drop) ? p.Info.SourceRange : ByteRange.None };
        _have = true;
        return true;
    }

    private void Ensure(int size)
    {
        if (_buffer.Length < size)
        {
            _buffer = new byte[Math.Max(size, _buffer.Length * 2)];
        }
    }

    // SEI NAL unit bytes without framing: header, then the escaped RBSP (message type 4, size, payload, trailing bits).
    private byte[] BuildSei(IReadOnlyList<CcTriplet> triplets)
    {
        var limited = new CcTriplet[Math.Min(31, triplets.Count)]; // cc_count is a 5-bit field
        for (int i = 0; i < limited.Length; i++)
        {
            limited[i] = triplets[i];
        }

        byte[] payload = CaptionData.BuildItuT35Payload(limited);
        var rbsp = new List<byte> { 4 };
        int size = payload.Length;
        while (size >= 255)
        {
            rbsp.Add(255);
            size -= 255;
        }

        rbsp.Add((byte)size);
        rbsp.AddRange(payload);
        rbsp.Add(0x80);
        var nal = new List<byte>();
        if (_codec == NalCodec.H264)
        {
            nal.Add(0x06); // nal_ref_idc 0, nal_unit_type 6 (SEI)
        }
        else
        {
            nal.Add(39 << 1); // prefix SEI, layer 0
            nal.Add(0x01); // temporal_id_plus1 = 1
        }

        // H.264 §7.4.1: insert 0x03 after two zero bytes when the next byte is 0..3 (emulation prevention).
        int zeros = 0;
        foreach (byte b in rbsp)
        {
            if (zeros >= 2 && b <= 3)
            {
                nal.Add(3);
                zeros = 0;
            }

            nal.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return [.. nal];
    }

    private void Rewrite(ReadOnlySpan<byte> src, byte[]? sei, bool drop)
    {
        var output = new ArrayBufferWriter<byte>(src.Length + 64);
        bool inserted = false;
        void Emit(ReadOnlySpan<byte> nal, bool annexB, int startCodeLen)
        {
            if (annexB)
            {
                if (startCodeLen == 3)
                {
                    output.Write(new byte[] { 0, 0, 1 });
                }
                else
                {
                    output.Write(new byte[] { 0, 0, 0, 1 });
                }

                output.Write(nal);
            }
            else
            {
                Span<byte> len = stackalloc byte[4];
                for (int i = 0; i < _lengthSize; i++)
                {
                    len[i] = (byte)(nal.Length >> (8 * (_lengthSize - 1 - i)));
                }

                output.Write(len[.._lengthSize]);
                output.Write(nal);
            }
        }

        void Visit(ReadOnlySpan<byte> nal, bool annexB, int startCodeLen)
        {
            if (nal.Length == 0)
            {
                return;
            }

            int type = NalUnit.GetType(_codec, nal);
            if (!inserted && sei is not null && NalUnit.IsSlice(_codec, type))
            {
                Emit(sei, annexB, 4);
                inserted = true;
                _inserted++;
            }

            if (drop && IsCaptionOnlySei(nal, type))
            {
                _removed++;
                return;
            }

            Emit(nal, annexB, startCodeLen);
        }

        if (_lengthSize == 0)
        {
            var reader = new AnnexBReader(src);
            while (reader.TryRead(out ReadOnlySpan<byte> nal, out _, out int sc))
            {
                Visit(nal, true, sc);
            }
        }
        else
        {
            int pos = 0;
            while (pos + _lengthSize <= src.Length)
            {
                long len = 0;
                for (int i = 0; i < _lengthSize; i++)
                {
                    len = (len << 8) | src[pos + i];
                }

                pos += _lengthSize;
                if (len > src.Length - pos)
                {
                    break;
                }

                Visit(src.Slice(pos, (int)len), false, 0);
                pos += (int)len;
            }
        }

        ReadOnlySpan<byte> written = output.WrittenSpan;
        Ensure(written.Length);
        written.CopyTo(_buffer);
        _length = written.Length;
    }

    private bool IsCaptionOnlySei(ReadOnlySpan<byte> nal, int type)
    {
        bool sei = _codec == NalCodec.H264 ? type == 6 : type == 39;
        if (!sei || nal.Length < 3)
        {
            return false;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(nal.Length);
        try
        {
            int n = EmulationPrevention.Remove(nal[NalUnit.HeaderSize(_codec)..], rented);
            var r = new SeiMessageReader(rented.AsSpan(0, n));
            bool any = false;
            while (r.TryRead(out int pt, out ReadOnlySpan<byte> payload))
            {
                any = true;
                if (pt != 4 || !CaptionData.TryGetFromItuT35(payload, out _))
                {
                    return false;
                }
            }

            return any;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

/// <summary>Assigns caption byte pairs to video frames.</summary>
public static class CaptionPlacement
{
    /// <summary>
    /// Places CEA-608 field 1 byte pairs on frames: each pair goes to the last frame at or before its time that has no pair yet (one pair per
    /// frame, as at 29.97 fps), spilling to the following frames when pairs bunch up.
    /// </summary>
    /// <param name="framePts">Presentation times of the video frames, in any order.</param>
    /// <param name="pairs">Pairs with their times (parity bits as they should be transmitted), in order.</param>
    /// <returns>Caption frames keyed by frame time, in presentation order, ready for <see cref="CaptionSeiFeed"/>. Pairs after the last frame are dropped.</returns>
    public static IReadOnlyList<CaptionFrame> Place(IReadOnlyList<MediaTime> framePts, IEnumerable<(MediaTime Time, byte B1, byte B2)> pairs)
    {
        ArgumentNullException.ThrowIfNull(framePts);
        ArgumentNullException.ThrowIfNull(pairs);
        var sorted = new List<MediaTime>(framePts);
        sorted.Sort();
        var slots = new Dictionary<int, CcTriplet>();
        int next = 0;
        foreach ((MediaTime time, byte b1, byte b2) in pairs)
        {
            int lo = 0;
            int hi = sorted.Count - 1;
            int at = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (sorted[mid] <= time)
                {
                    at = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            at = Math.Max(at, next);
            if (at >= sorted.Count)
            {
                break;
            }

            slots[at] = new CcTriplet(true, 0, b1, b2);
            next = at + 1;
        }

        var result = new List<CaptionFrame>(slots.Count);
        foreach (int i in slots.Keys.OrderBy(k => k))
        {
            result.Add(new CaptionFrame(sorted[i], MediaTime.Unknown, [slots[i]]));
        }

        return result;
    }
}
