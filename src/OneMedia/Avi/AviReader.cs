using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia.Avi;

/// <summary>
/// The AVI engine: opens the header list, builds the tracks, and walks the <c>movi</c> lists chunk by chunk. Packets come out in file
/// order, as the chunks are stored. Key-frame flags come from the index (OpenDML standard indexes, else <c>idx1</c>) by looking up each
/// chunk's position; a file without an index, or a source that cannot seek, has none, and every packet is then reported as a key frame.
/// </summary>
internal sealed class AviReader : IDisposable
{
    private static readonly uint Idx1Id = RiffChunk.Make("idx1");
    private static readonly uint MoviType = RiffChunk.Make("movi");
    private static readonly uint RecType = RiffChunk.Make("rec ");

    private readonly AviInput _input;
    private readonly IMediaSource? _source;
    private readonly MediaLimits _limits;
    private readonly DefectLog _defects;
    private readonly CancellationToken _cancellation;
    private readonly List<TrackState> _state = [];
    private byte[] _packet = [];
    private bool _unknownStreamReported;

    // The current section: the data of one 'movi' list, from _pos to _sectionEnd.
    private long _pos;
    private long _sectionEnd;
    private bool _finished;
    private long _firstMoviOffset = -1;
    private long _firstMoviPayload;

    // Key-frame lookup.
    private Idx1Cursor? _idx1;
    private OdmlCursor?[] _odml = [];

    private AviReader(AviInput input, IMediaSource? source, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        _input = input;
        _source = source;
        _limits = limits;
        _defects = defects;
        _cancellation = cancellationToken;
    }

    public AviHeaderList Headers { get; private set; } = null!;

    public IReadOnlyList<MediaTrack> Tracks { get; private set; } = [];

    public MediaTime Duration { get; private set; } = MediaTime.Unknown;

    /// <summary>The root RIFF chunk of the first RIFF AVI, for the structure view and the metadata reader.</summary>
    public RiffChunk? Root { get; private set; }

    public ByteRange IndexRange { get; private set; } = ByteRange.None;

    public bool HasIndex => _idx1 is not null || Array.Exists(_odml, c => c is not null);

    public bool CanSeek => _source is not null;

    public PacketInfo CurrentInfo { get; private set; }

    public ReadOnlySpan<byte> CurrentData => _packet.AsSpan(0, CurrentInfo.Size);

    public static AviReader Open(IMediaSource source, ReadOnlySpan<byte> head, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        AviInput input = new(source, cancellationToken);
        AviReader r = new(input, source, limits, defects, cancellationToken);
        try
        {
            r.OpenSeekable(head);
            return r;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    public static AviReader Open(IForwardMediaSource source, MediaLimits limits, DefectLog defects, CancellationToken cancellationToken)
    {
        AviInput input = new(source, cancellationToken);
        AviReader r = new(input, null, limits, defects, cancellationToken);
        try
        {
            r.OpenForward();
            return r;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _input.Dispose();
        if (_packet.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_packet);
            _packet = [];
        }
    }

    public void SetTrackEnabled(int trackIndex, bool enabled)
    {
        if ((uint)trackIndex >= (uint)_state.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIndex));
        }

        _state[trackIndex].Enabled = enabled;
    }

    // ---- opening ----

    private void OpenSeekable(ReadOnlySpan<byte> head)
    {
        IMediaSource source = _source!;
        if (head.Length < 12 || !head[..4].SequenceEqual("RIFF"u8))
        {
            throw new MediaFormatException("The data does not start with a RIFF header.") { Offset = 0 };
        }

        RiffChunk? root = RiffChunk.Read(source, 0, source.Length, _limits, _defects, 1, _cancellation);
        if (root is null || root.ListType is not ("AVI " or "AVIX"))
        {
            throw new MediaFormatException("The RIFF form is not AVI.") { Offset = 0 };
        }

        Root = root;
        RiffChunk? hdrl = null;
        RiffChunk? movi = null;
        RiffChunk? idx1 = null;
        foreach (IStructureNode n in root.Children)
        {
            if (n is not RiffChunk c)
            {
                continue;
            }

            if (c.IsList && c.ListType == "hdrl" && hdrl is null)
            {
                hdrl = c;
            }
            else if (c.IsList && c.ListType == "movi")
            {
                movi ??= c;
            }
            else if (c.Id == Idx1Id && idx1 is null)
            {
                idx1 = c;
            }
        }

        if (hdrl is null)
        {
            throw new MediaFormatException("The AVI file has no hdrl list.") { Offset = 12 };
        }

        Headers = AviHeaderParser.Parse(hdrl, _limits, _defects, _cancellation);
        BuildTracks();
        if (movi is null)
        {
            _defects.Add(DefectKind.MissingRequired, DefectSeverity.Error, root.NextOffset, "The AVI file has no movi list; there are no packets.", "avi.missing-movi");
            _finished = true;
            return;
        }

        _firstMoviOffset = movi.Offset;
        _firstMoviPayload = movi.PayloadSize;
        _pos = movi.Offset + 12;
        _sectionEnd = _pos + movi.PayloadSize;
        if (idx1 is not null)
        {
            IndexRange = new ByteRange(idx1.Offset + 8, idx1.PayloadSize);
        }

        OpenIndexes(source, idx1, movi);
    }

    private void OpenIndexes(IMediaSource source, RiffChunk? idx1, RiffChunk movi)
    {
        _odml = new OdmlCursor?[Headers.Streams.Count];
        bool anyOdml = false;
        for (int i = 0; i < Headers.Streams.Count; i++)
        {
            AviStreamInfo s = Headers.Streams[i];
            if (s.SuperIndex is { Count: > 0 } super)
            {
                _odml[i] = new OdmlCursor(source, super, i, _defects);
                anyOdml = true;
            }
        }

        // idx1 serves the streams that have no OpenDML index. Files written with both keep idx1 for old players.
        if (idx1 is not null && (!anyOdml || Array.Exists(_odml, c => c is null)))
        {
            _idx1 = Idx1Cursor.Open(source, new ByteRange(idx1.Offset + 8, idx1.PayloadSize), movi.Offset, _defects, _cancellation);
        }
    }

    private void OpenForward()
    {
        // The header list and its neighbours come before the first movi data. They are collected into memory (with JUNK
        // padding left as zeros) so the same chunk reader as for a seekable source can parse them.
        Span<byte> head = stackalloc byte[12]; // RIFF header, fixed
        if (_input.Read(head) < 12 || !head[..4].SequenceEqual("RIFF"u8) || !(head[8..12].SequenceEqual("AVI "u8) || head[8..12].SequenceEqual("AVIX"u8)))
        {
            throw new MediaFormatException("The data is not an AVI file.") { Offset = 0 };
        }

        MemoryStream prefix = new();
        prefix.Write(head);
        Span<byte> hdr = stackalloc byte[12]; // chunk header plus list type, fixed
        long position = 12;
        long moviDataStart = -1;
        long moviSize = 0;
        while (moviDataStart < 0)
        {
            int got = _input.Read(hdr[..8]);
            if (got < 8)
            {
                throw new MediaFormatException("The file ends before the movi list.") { Offset = position };
            }

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(hdr);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..]);
            bool isList = id == RiffChunk.Ids.List && size >= 4;
            if (isList)
            {
                if (_input.Read(hdr[8..12]) < 4)
                {
                    throw new MediaFormatException("The file ends inside a list header.") { Offset = position };
                }

                if (BinaryPrimitives.ReadUInt32LittleEndian(hdr[8..]) == MoviType)
                {
                    prefix.Write(hdr);
                    moviDataStart = position + 12;
                    moviSize = size - 4;
                    break;
                }
            }

            _limits.CheckAllocation(prefix.Length + 12L + size, position);
            prefix.Write(hdr[..(isList ? 12 : 8)]);
            long remaining = size - (isList ? 4 : 0);
            long padded = remaining + (size & 1);
            if (id == RiffChunk.Ids.Junk)
            {
                prefix.SetLength(prefix.Length + padded);
                prefix.Seek(0, SeekOrigin.End);
                if (!_input.Skip(padded))
                {
                    throw new MediaFormatException("The file ends inside a JUNK chunk.") { Offset = position };
                }
            }
            else
            {
                byte[] data = new byte[padded];
                if (_input.Read(data) < padded)
                {
                    throw new MediaFormatException("The file ends inside a header chunk.") { Offset = position };
                }

                prefix.Write(data);
            }

            position += 8 + size + (size & 1);
        }

        byte[] bytes = prefix.ToArray();
        MemoryMediaSource mem = new(bytes);
        RiffChunk? root = RiffChunk.Read(mem, 0, long.MaxValue / 2, _limits, new DefectLog(_limits), 1, _cancellation);
        RiffChunk? hdrl = null;
        if (root is not null)
        {
            // The in-memory prefix ends inside the RIFF; read the children by hand.
            long p = 12;
            while (p < bytes.Length)
            {
                RiffChunk? c = RiffChunk.Read(mem, p, bytes.Length, _limits, _defects, 2, _cancellation);
                if (c is null)
                {
                    break;
                }

                if (c.IsList && c.ListType == "hdrl")
                {
                    hdrl = c;
                    break;
                }

                p = c.NextOffset;
            }
        }

        if (hdrl is null)
        {
            throw new MediaFormatException("The AVI file has no hdrl list.") { Offset = 12 };
        }

        Headers = AviHeaderParser.Parse(hdrl, _limits, _defects, _cancellation);
        BuildTracks();
        _pos = moviDataStart;
        _sectionEnd = moviDataStart + moviSize;
        _firstMoviOffset = moviDataStart - 12;
        _firstMoviPayload = moviSize;
    }

    // ---- tracks ----

    private void BuildTracks()
    {
        List<MediaTrack> tracks = [];
        MediaTime longest = MediaTime.Unknown;
        foreach (AviStreamInfo s in Headers.Streams)
        {
            TrackState st = new(s);
            _state.Add(st);
            MediaTrack t = AviTracks.Build(s, Headers);
            tracks.Add(t);
            if (t.Duration.IsKnown && (!longest.IsKnown || t.Duration > longest))
            {
                longest = t.Duration;
            }
        }

        Tracks = tracks;
        if (!longest.IsKnown && Headers.Main.TotalFrames > 0 && Headers.Main.MicroSecondsPerFrame > 0)
        {
            longest = new MediaTime(Headers.Main.TotalFrames * (long)Headers.Main.MicroSecondsPerFrame, 1_000_000);
        }

        Duration = longest;
    }

    // ---- packets ----

    public bool ReadPacket()
    {
        while (NextChunk(out ChunkHeader chunk))
        {
            TrackState st = _state[chunk.Stream];
            long ticksBefore = Account(st, chunk.Size);
            if (!st.Enabled)
            {
                _pos = chunk.Offset + 8 + chunk.Size + (chunk.Size & 1);
                continue;
            }

            ReadPayload(chunk, out int size, out bool complete);
            PacketAttributes flags = KeyFlag(chunk) ? PacketAttributes.Keyframe : PacketAttributes.None;
            if (!complete)
            {
                flags |= PacketAttributes.Corrupt;
            }

            MediaTime pts = st.TimeOf(ticksBefore);
            CurrentInfo = new PacketInfo(chunk.Stream, pts, pts, st.DurationOf(st.Ticks - ticksBefore), flags, new ByteRange(chunk.Offset + 8, size), size);
            return true;
        }

        return false;
    }

    // Advances the stream's clock past one chunk and returns the clock before it. A stream with a sample size counts bytes and
    // divides (so the clock is in samples, as a PCM chunk is a whole number of them); any other stream counts chunks.
    private static long Account(TrackState st, long size)
    {
        long before = st.Ticks;
        if (size == 0)
        {
            st.Ticks += st.SampleSize == 0 && !st.IsAudio ? 1 : 0;
        }
        else if (st.SampleSize > 0)
        {
            st.Bytes += size;
            st.Ticks = st.Bytes / st.SampleSize;
        }
        else
        {
            st.Ticks++;
        }

        return before;
    }

    private bool KeyFlag(ChunkHeader chunk)
    {
        if (_source is null)
        {
            return true;
        }

        AviIndexCursor? cursor = _odml[chunk.Stream] ?? (AviIndexCursor?)_idx1;
        if (cursor is null)
        {
            return true;
        }

        if (cursor.Seek(chunk.Offset, out AviIndexEntry e) && e.Offset == chunk.Offset)
        {
            return e.IsKeyframe;
        }

        return true;
    }

    private void ReadPayload(ChunkHeader chunk, out int size, out bool complete)
    {
        _limits.CheckPacket(chunk.Size, chunk.Offset);
        int n = (int)chunk.Size;
        if (_packet.Length < n)
        {
            if (_packet.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_packet);
            }

            _packet = ArrayPool<byte>.Shared.Rent(Math.Max(n, 4096));
        }

        _input.Seek(chunk.Offset + 8);
        int got = _input.Read(_packet.AsSpan(0, n));
        size = got;
        complete = got == n;
        _pos = chunk.Offset + 8 + chunk.Size + (chunk.Size & 1);
        if (!complete)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, chunk.Offset + 8 + got, $"The file ends {n - got} bytes into a {n}-byte chunk.", "avi.truncated-chunk");
            _finished = true;
        }
    }

    private readonly record struct ChunkHeader(long Offset, uint Size, int Stream, ushort Type);

    private bool NextChunk(out ChunkHeader chunk)
    {
        Span<byte> h = stackalloc byte[12]; // chunk header plus list type, fixed
        while (!_finished)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (_pos >= _sectionEnd && !EnterNextSection())
            {
                break;
            }

            _input.Seek(_pos);
            int got = _input.Read(h[..8]);
            if (got < 8)
            {
                if (got > 0)
                {
                    _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, _pos, "The file ends inside a chunk header.", "avi.truncated-header");
                }

                _finished = true;
                break;
            }

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(h);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            long headerAt = _pos;
            if (id == RiffChunk.Ids.List)
            {
                if (_input.Read(h[8..12]) < 4)
                {
                    _finished = true;
                    break;
                }

                if (BinaryPrimitives.ReadUInt32LittleEndian(h[8..]) == RecType)
                {
                    // A 'rec ' list groups the chunks of one interleave step; its chunks follow the 12-byte header.
                    _pos = headerAt + 12;
                    continue;
                }

                _pos = headerAt + 8 + size + (size & 1);
                continue;
            }

            if (AviChunkId.TryStreamNumber(id, out int stream, out ushort type) && stream < _state.Count && (type == AviChunkId.Dc || type == AviChunkId.Db || type == AviChunkId.Wb || type == AviChunkId.Tx))
            {
                if (size == 0)
                {
                    // An empty chunk is a dropped frame, not a packet. It still takes a video frame's place on the clock; an empty audio chunk does not
                    // (observed: the next audio packet continues the count without a gap).
                    Account(_state[stream], 0);
                    _pos = headerAt + 8;
                    continue;
                }

                chunk = new ChunkHeader(headerAt, size, stream, type);
                return true;
            }

            if (AviChunkId.TryStreamNumber(id, out int other, out ushort otype) && otype != AviChunkId.Pc && other >= _state.Count && !_unknownStreamReported)
            {
                _unknownStreamReported = true;
                _defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, headerAt, $"A chunk belongs to stream {other}, which the header list does not describe; such chunks are skipped.", "avi.unknown-stream");
            }

            // JUNK, index chunks (ix##), palette changes (##pc) and unknown chunks are skipped whole.
            _pos = headerAt + 8 + size + (size & 1);
        }

        chunk = default;
        return false;
    }

    // Moves to the next 'movi' list after the current one: past idx1 and other trailing chunks, into a following RIFF AVIX.
    private bool EnterNextSection()
    {
        Span<byte> h = stackalloc byte[12]; // chunk header plus form type, fixed
        long limit = _input.Length;
        while (!_finished)
        {
            _input.Seek(_pos);
            int got = _input.Read(h[..8]);
            if (got < 8)
            {
                _finished = true;
                return false;
            }

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(h);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            if (id == RiffChunk.Ids.Riff)
            {
                if (_input.Read(h[8..12]) < 4 || !h[8..12].SequenceEqual("AVIX"u8))
                {
                    _defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Info, _pos, "A RIFF chunk that is not AVIX follows the AVI data; reading stops.", "avi.trailing-riff");
                    _finished = true;
                    return false;
                }

                long riffEnd = size == 0xFFFFFFFF || size < 4 ? long.MaxValue : _pos + 8 + size;
                long p = _pos + 12;
                long riffPos = _pos;
                while (p < riffEnd && (limit < 0 || p < limit))
                {
                    _input.Seek(p);
                    if (_input.Read(h[..12]) < 8)
                    {
                        _finished = true;
                        return false;
                    }

                    uint cid = BinaryPrimitives.ReadUInt32LittleEndian(h);
                    uint csize = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
                    if (cid == RiffChunk.Ids.List && BinaryPrimitives.ReadUInt32LittleEndian(h[8..]) == MoviType)
                    {
                        _pos = p + 12;
                        _sectionEnd = p + 8 + csize;
                        if (limit >= 0 && _sectionEnd > limit)
                        {
                            _defects.Add(DefectKind.SizeExceedsFile, DefectSeverity.Warning, p, "The movi list of an AVIX chunk runs past the end of the file.", "avi.avix-truncated");
                            _sectionEnd = limit;
                        }

                        return true;
                    }

                    p += 8 + csize + (csize & 1);
                }

                _pos = riffEnd == long.MaxValue ? riffPos + 12 : riffEnd + (riffEnd & 1);
                if (riffEnd == long.MaxValue)
                {
                    _finished = true;
                    return false;
                }

                continue;
            }

            if (AviChunkId.TryStreamNumber(id, out _, out _))
            {
                // Chunks continue past the end the movi list declared: the size was wrong (common in files cut short by a crash).
                _defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Warning, _pos, "Stream chunks continue after the end the movi list declares; reading on.", "avi.movi-size-too-small");
                _sectionEnd = limit >= 0 ? limit : long.MaxValue;
                return true;
            }

            if (id is 0 or 0xFFFFFFFF)
            {
                _finished = true;
                return false;
            }

            // idx1, JUNK, INFO lists and the like.
            _pos += 8 + size + (size & 1);
            if (limit >= 0 && _pos >= limit)
            {
                _finished = true;
                return false;
            }
        }

        return false;
    }

    /// <summary>Finds the header of the first video-type chunk of a stream near the start of the first <c>movi</c> list (at most 256 chunks in).</summary>
    public bool TryFindFirstChunk(int stream, out long offset)
    {
        offset = -1;
        if (_source is null || _firstMoviOffset < 0)
        {
            return false;
        }

        long pos = _firstMoviOffset + 12;
        long end = pos + _firstMoviPayload;
        Span<byte> h = stackalloc byte[12]; // chunk header plus list type, fixed
        for (int i = 0; i < 256 && pos + 8 <= end; i++)
        {
            if (_source.Read(pos, h, _cancellation) < 12)
            {
                return false;
            }

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(h);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            if (id == RiffChunk.Ids.List && BinaryPrimitives.ReadUInt32LittleEndian(h[8..]) == RecType)
            {
                pos += 12;
                continue;
            }

            if (AviChunkId.TryStreamNumber(id, out int s, out ushort type) && s == stream && (type == AviChunkId.Dc || type == AviChunkId.Db))
            {
                offset = pos;
                return true;
            }

            pos += 8 + size + (size & 1);
        }

        return false;
    }

    // ---- seeking ----

    public MediaTime Seek(MediaTime target, SeekMode mode)
    {
        if (_source is null)
        {
            throw new NotSupportedException("The source is forward-only; seeking is not possible.");
        }

        int reference = 0;
        for (int i = 0; i < Tracks.Count; i++)
        {
            if (Tracks[i].Kind == TrackKind.Video)
            {
                reference = i;
                break;
            }
        }

        // The index states key frames per chunk but not the clocks of the other streams, so seeking walks the chunk headers from
        // the start (one small read each, no payloads) to find the best key frame of the reference stream, then walks again to
        // set every stream's clock at that chunk.
        Rewind();
        long bestPos = -1;
        MediaTime bestTime = MediaTime.Unknown;
        while (NextChunk(out ChunkHeader chunk))
        {
            TrackState st = _state[chunk.Stream];
            long before = Account(st, chunk.Size);
            _pos = chunk.Offset + 8 + chunk.Size + (chunk.Size & 1);
            if (chunk.Stream != reference || !KeyFlag(chunk))
            {
                continue;
            }

            MediaTime pts = st.TimeOf(before);
            if (mode == SeekMode.PreviousKeyframe)
            {
                if (pts > target && bestPos >= 0)
                {
                    break;
                }

                bestPos = chunk.Offset;
                bestTime = pts;
            }
            else if (pts >= target)
            {
                bestPos = chunk.Offset;
                bestTime = pts;
                break;
            }
        }

        Rewind();
        if (bestPos < 0)
        {
            return MediaTime.Unknown;
        }

        while (NextChunk(out ChunkHeader chunk))
        {
            if (chunk.Offset >= bestPos)
            {
                _pos = chunk.Offset;
                break;
            }

            Account(_state[chunk.Stream], chunk.Size);
            _pos = chunk.Offset + 8 + chunk.Size + (chunk.Size & 1);
        }

        return bestTime;
    }

    private void Rewind()
    {
        foreach (TrackState s in _state)
        {
            s.Reset();
        }

        _finished = false;
        _pos = _firstMoviOffset + 12;
        _sectionEnd = _pos + _firstMoviPayload;
        _idx1?.Reset();
        foreach (OdmlCursor? c in _odml)
        {
            c?.Reset();
        }
    }

    /// <summary>Per-stream clock and timing rule.</summary>
    private sealed class TrackState
    {
        private readonly long _scale;
        private readonly long _rate;
        private readonly long _start;

        public TrackState(AviStreamInfo s)
        {
            long scale = s.Header.Scale;
            long rate = s.Header.Rate;
            if (scale == 0 || rate == 0)
            {
                // A stream that states no rate gets one tick per millisecond so that times stay ordered.
                scale = 1;
                rate = 1000;
            }

            long g = (long)BigInteger.GreatestCommonDivisor(scale, rate);
            _scale = scale / g;
            _rate = rate / g;
            SampleSize = s.Header.SampleSize;
            IsAudio = s.Header.Type == AviStreamHeader.Auds;
            _start = s.Header.Start;
            Enabled = true;
        }

        public bool Enabled { get; set; }

        public long SampleSize { get; }

        public bool IsAudio { get; }

        public long Ticks { get; set; }

        public long Bytes { get; set; }

        public void Reset()
        {
            Ticks = 0;
            Bytes = 0;
        }

        // One tick lasts scale/rate seconds, so a time is ticks * scale in a timescale of rate.
        public MediaTime TimeOf(long ticks) => new((ticks + _start) * _scale, _rate);

        public MediaTime DurationOf(long ticks) => new(ticks * _scale, _rate);
    }
}
