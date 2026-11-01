using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Asf;

/// <summary>
/// The ASF engine behind the sync, async and forward-only demuxers: opens the header, reads data packets one at a time, reassembles media
/// objects from their payloads and hands them out in the order they complete. Written once as an asynchronous reader; a source that can
/// answer synchronously makes every step complete without suspending, so the synchronous demuxer wraps it without allocating.
/// </summary>
internal sealed class AsfReader
{
    private readonly AsfInput _input;
    private readonly MediaLimits _limits;
    private readonly DefectLog _defects;
    private readonly int[] _trackOfStream = new int[128];
    private readonly bool[] _reportedStream = new bool[128];
    private AsfAudioSpread?[] _spread = [];
    private readonly List<AsfPayload> _payloads = [];
    private readonly Queue<Entry> _queue = new();
    private readonly Stack<byte[]> _pool = new();
    private AsfHeader _header = null!;
    private StreamState[] _streams = [];
    private bool[] _enabled = [];
    private byte[] _packetBuffer = [];
    private int _fixedSize;
    private bool _variable;
    private long _nextOffset;
    private long _packetNumber;
    private long _packetLimit = long.MaxValue;
    private long _dataEnd = long.MaxValue;
    private long _preroll;
    private int _skipTrack = -1;      // after a seek: the track whose objects before the keyframe's time are dropped
    private long _skipBefore;
    private byte[]? _currentArray;
    private int _currentStart;
    private int _currentLength;
    private byte[]? _currentOwned;

    public AsfReader(AsfInput input, MediaLimits limits, DefectLog defects)
    {
        _input = input;
        _limits = limits;
        _defects = defects;
    }

    public AsfHeader Header => _header;

    public IReadOnlyList<MediaTrack> Tracks { get; private set; } = [];

    public MediaTime Duration { get; private set; } = MediaTime.Unknown;

    public bool IsProtected { get; private set; }

    public bool CanSeek { get; private set; }

    public PacketInfo CurrentInfo { get; private set; }

    public ReadOnlySpan<byte> CurrentData => _currentArray is null ? default : _currentArray.AsSpan(_currentStart, _currentLength);

    public async ValueTask OpenAsync(CancellationToken ct)
    {
        _header = await AsfHeaderParser.ParseAsync(_input, _limits, _defects, ct).ConfigureAwait(false);
        AsfFileProperties? fp = _header.FileProperties;
        _preroll = fp?.PrerollMs ?? 0;
        IsProtected = _header.Protection != AsfProtection.None;
        if (fp is { PlayDuration100ns: > 0 } && !fp.IsBroadcast)
        {
            // The play duration includes the preroll; times the demuxer reports start after it.
            Duration = new MediaTime(Math.Max(0, fp.PlayDuration100ns / 10_000 - _preroll), 1000);
        }

        Array.Fill(_trackOfStream, -1);
        List<MediaTrack> tracks = [];
        foreach (AsfStreamEntry s in _header.Streams)
        {
            if (s.StreamNumber is >= 1 and <= 127 && _trackOfStream[s.StreamNumber] < 0)
            {
                _trackOfStream[s.StreamNumber] = tracks.Count;
                tracks.Add(AsfCodecMap.BuildTrack(tracks.Count, s, _header, Duration, IsProtected));
            }
        }

        Tracks = tracks;
        _streams = new StreamState[tracks.Count];
        _spread = new AsfAudioSpread?[tracks.Count];
        foreach (AsfStreamEntry e in _header.Streams)
        {
            if (e.StreamNumber is >= 1 and <= 127 && _trackOfStream[e.StreamNumber] >= 0 && _spread[_trackOfStream[e.StreamNumber]] is null)
            {
                _spread[_trackOfStream[e.StreamNumber]] = e.Spread;
            }
        }

        _enabled = new bool[tracks.Count];
        Array.Fill(_enabled, true);
        if (IsProtected)
        {
            _defects.Add(DefectKind.Unsupported, DefectSeverity.Info, 0, "The content is protected; the tracks and tags are listed, and no packets are delivered.", "asf.protected");
        }

        if (fp is null || _header.DataObjectOffset < 0)
        {
            return;
        }

        uint min = fp.MinPacketSize, max = fp.MaxPacketSize;
        if (max == 0 || min == 0 || min > max)
        {
            if (max == 0 && min == 0)
            {
                _defects.Add(DefectKind.InvalidValue, DefectSeverity.Error, fp.Range.Offset, "File Properties states a packet size of 0; no packets can be read.", "asf.packet-size");
                return;
            }

            _defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, fp.Range.Offset, $"File Properties gives minimum packet size {min} and maximum {max}.", "asf.packet-size");
            (min, max) = (Math.Max(min, max), Math.Max(min, max));
        }

        _limits.CheckAllocation(max, fp.Range.Offset);
        _fixedSize = (int)min;
        _variable = min != max;
        _packetBuffer = new byte[max];
        _nextOffset = _header.DataStart;
        long count = _header.DataPacketCount != 0 ? _header.DataPacketCount : fp.DataPacketCount;
        if (count > 0 && !fp.IsBroadcast)
        {
            _packetLimit = count;
        }

        long length = _input.Length;
        // A Data Object that states just its own header (50 bytes) is a placeholder, as in files captured while broadcast: the packets run to the end of the file.
        long statedEnd = _header.DataObjectSize > 50 ? _header.DataObjectOffset + _header.DataObjectSize : long.MaxValue;
        _dataEnd = length >= 0 ? Math.Min(statedEnd, length) : statedEnd;
        CanSeek = _input.CanSeek && min == max && !IsProtected;
    }

    public void SetTrackEnabled(int trackIndex, bool enabled)
    {
        if ((uint)trackIndex >= (uint)_enabled.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIndex));
        }

        _enabled[trackIndex] = enabled;
    }

    public async ValueTask<bool> ReadPacketAsync(CancellationToken ct)
    {
        if (_currentOwned is not null)
        {
            Recycle(_currentOwned);
            _currentOwned = null;
        }

        _currentArray = null;
        while (_queue.Count == 0)
        {
            if (!await ReadDataPacketAsync(ct).ConfigureAwait(false))
            {
                if (FlushPartialObjects())
                {
                    continue;
                }

                return false;
            }
        }

        Entry e = _queue.Dequeue();
        if (_skipTrack >= 0 && e.Track == _skipTrack)
        {
            // The keyframe shares a data packet with frames that precede it; a seek must start at the keyframe itself.
            if (e.TimeMs - _preroll < _skipBefore)
            {
                if (e.Owned is not null)
                {
                    Recycle(e.Owned);
                }

                return await ReadPacketAsync(ct).ConfigureAwait(false);
            }

            _skipTrack = -1;
        }

        _currentArray = e.Owned ?? _packetBuffer;
        _currentOwned = e.Owned;
        _currentStart = e.Owned is null ? e.Start : 0;
        _currentLength = e.Length;
        MediaTime pts = e.TimeMs >= 0 ? new MediaTime(e.TimeMs - _preroll, 1000) : MediaTime.Unknown;
        // The stream byte's key flag is set for video key frames only; every audio frame stands alone, as in the other containers' audio tracks.
        bool key = e.Key || Tracks[e.Track].Kind == TrackKind.Audio;
        CurrentInfo = new PacketInfo(e.Track, pts, pts, MediaTime.Unknown, (key ? PacketAttributes.Keyframe : PacketAttributes.None) | (e.Corrupt ? PacketAttributes.Corrupt : PacketAttributes.None),
            e.SourceOffset >= 0 ? new ByteRange(e.SourceOffset, e.Length) : ByteRange.None, e.Length);
        return true;
    }

    // When the file ends inside a payload, the part of the object that arrived is delivered, flagged corrupt: observed in ffprobe's output for files
    // cut at round sizes. Only the object whose payload the cut went through is delivered; other objects still waiting for their next fragment are not.
    private bool FlushPartialObjects()
    {
        bool any = false;
        for (int track = 0; track < _streams.Length; track++)
        {
            ref StreamState st = ref _streams[track];
            if (!st.Active)
            {
                continue;
            }

            st.Active = false;
            if (st.Have > 0 && st.Buffer is not null && st.Cut)
            {
                _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, _nextOffset, $"The data ends inside media object {st.ObjectNumber} of stream {Tracks[track].Id}; the {st.Have} of {st.Size} bytes that arrived are delivered.", "asf.truncated-object");
                _queue.Enqueue(new Entry(track, st.TimeMs, st.Key, 0, st.Have, st.Buffer, -1, true));
                st.Buffer = null;
                any = true;
            }
        }

        return any;
    }

    // Entries that point into the packet buffer stay valid because the next packet is not read until the queue is empty.

    private async ValueTask<bool> ReadDataPacketAsync(CancellationToken ct)
    {
        if (IsProtected || _header.DataObjectOffset < 0 || _packetBuffer.Length == 0)
        {
            return false;
        }

        while (true)
        {
            if (_packetNumber >= _packetLimit)
            {
                return false;
            }

            long offset = _nextOffset;
            if (offset >= _dataEnd)
            {
                return false;
            }

            int want = (int)Math.Min(_packetBuffer.Length, _dataEnd - offset);
            int got = await _input.ReadAsync(offset, _packetBuffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (got < 4)
            {
                if (got > 0)
                {
                    _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "The file ends inside a data packet.", "asf.truncated-packet");
                }
                else if (_packetLimit != long.MaxValue && _packetNumber < _packetLimit && _input.Length >= 0)
                {
                    _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, offset, $"The file has {_packetNumber} data packets; the headers state {_packetLimit}.", "asf.short-data");
                }

                return false;
            }

            if (got >= 16 && IsTopLevelObject(_packetBuffer.AsSpan(0, 16)))
            {
                return false; // an index object (or another object) where a packet should be: the packets have ended
            }

            string? problem = AsfPacketParser.Parse(_packetBuffer.AsSpan(0, got), _fixedSize, _payloads, out int length);
            if (length <= 0 || !_variable)
            {
                // With one packet size the next packet starts that far on, whatever the packet states (the rest is implied padding).
                length = _fixedSize;
            }

            if (got < length)
            {
                _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "The file ends inside a data packet.", "asf.truncated-packet");
            }

            if (problem is not null)
            {
                _defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, $"Data packet {_packetNumber} is damaged ({problem}); the payloads before the damage are kept.", "asf.packet-" + problem);
            }

            if (got < length && _payloads.Count > 0 && _payloads[^1].DataStart + _payloads[^1].DataLength >= got)
            {
                // The file ends inside the last payload (or right at its end, in a packet that should go on).
                AsfPayload last = _payloads[^1];
                last.Cut = true;
                _payloads[^1] = last;
            }

            _nextOffset = offset + length;
            _packetNumber++;
            Process(offset);
            if (_queue.Count > 0)
            {
                return true;
            }
        }
    }

    private static bool IsTopLevelObject(ReadOnlySpan<byte> first16)
    {
        Guid g = new(first16);
        return g == AsfGuids.SimpleIndex || g == AsfGuids.Index || g == AsfGuids.Header || g == AsfGuids.Data;
    }

    private void Process(long packetOffset)
    {
        foreach (AsfPayload p in _payloads)
        {
            int track = p.Stream < 128 ? _trackOfStream[p.Stream] : -1;
            if (track < 0)
            {
                if (p.Stream < 128 && !_reportedStream[p.Stream])
                {
                    _reportedStream[p.Stream] = true;
                    _defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, packetOffset + p.DataStart, $"Payloads name stream {p.Stream}, which no Stream Properties object describes; they are skipped.", "asf.unknown-stream");
                }

                continue;
            }

            if (!_enabled[track])
            {
                // A skipped stream's partly assembled object must not be completed by a later fragment after the stream is turned back on.
                _streams[track].Active = false;
                continue;
            }

            if (p.Compressed)
            {
                AddCompressed(track, p, packetOffset);
            }
            else
            {
                Assemble(track, p, packetOffset);
            }
        }
    }

    // A compressed payload holds whole small objects, each with a one-byte length in front, one time step apart.
    private void AddCompressed(int track, AsfPayload p, long packetOffset)
    {
        int at = p.DataStart, end = p.DataStart + p.DataLength;
        for (int n = 0; at < end; n++)
        {
            int length = _packetBuffer[at++];
            if (length > end - at)
            {
                _defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Warning, packetOffset + at - 1, "A sub-payload of a compressed payload is longer than the payload; it is cut.", "asf.compressed-payload");
                length = end - at;
            }

            if (length > 0)
            {
                _queue.Enqueue(new Entry(track, p.TimeMs + (long)n * p.TimeDelta, p.Key, at, length, null, packetOffset + at));
            }

            at += length;
        }
    }

    private void Assemble(int track, AsfPayload p, long packetOffset)
    {
        ref StreamState st = ref _streams[track];
        uint size = p.ObjectSize != 0 ? p.ObjectSize : (uint)p.DataLength;
        if (p.Offset == 0 && p.DataLength == size)
        {
            st.Active = false;
            if (p.DataLength > 0)
            {
                if (_spread[track] is { } spread && p.DataLength == spread.ObjectLength)
                {
                    byte[] plain = Rent(p.DataLength);
                    Unscramble(spread, _packetBuffer.AsSpan(p.DataStart, p.DataLength), plain);
                    _queue.Enqueue(new Entry(track, p.TimeMs, p.Key, 0, p.DataLength, plain, -1));
                }
                else
                {
                    _queue.Enqueue(new Entry(track, p.TimeMs, p.Key, p.DataStart, p.DataLength, null, packetOffset + p.DataStart));
                }
            }

            return;
        }

        if (p.Offset == 0)
        {
            if (st.Active)
            {
                _defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, packetOffset + p.DataStart, $"Media object {st.ObjectNumber} of stream {p.Stream} was not completed before object {p.ObjectNumber} began; it is dropped.", "asf.incomplete-object");
                Recycle(st.Buffer);
                st.Active = false;
            }

            if (size > _limits.MaxPacketBytes || p.DataLength > size)
            {
                _defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, packetOffset + p.DataStart, $"A media object of {size} bytes exceeds the packet limit or its first fragment; it is skipped.", "asf.object-size");
                return;
            }

            st.Active = true;
            st.ObjectNumber = p.ObjectNumber;
            st.Size = (int)size;
            st.Have = 0;
            st.TimeMs = p.TimeMs;
            st.Key = p.Key;
            st.Buffer = Rent(st.Size);
        }
        else if (!st.Active || st.ObjectNumber != p.ObjectNumber || st.Have != p.Offset || (long)st.Have + p.DataLength > st.Size)
        {
            _defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, packetOffset + p.DataStart, $"A fragment of media object {p.ObjectNumber} (stream {p.Stream}) at offset {p.Offset} does not continue the object being assembled; it is dropped.", "asf.orphan-fragment");
            return;
        }

        Buffer.BlockCopy(_packetBuffer, p.DataStart, st.Buffer!, st.Have, p.DataLength);
        st.Have += p.DataLength;
        st.Cut = p.Cut;
        if (st.Have == st.Size)
        {
            st.Active = false;
            byte[] done = st.Buffer!;
            if (_spread[track] is { } spread && st.Size == spread.ObjectLength)
            {
                byte[] plain = Rent(st.Size);
                Unscramble(spread, done.AsSpan(0, st.Size), plain);
                Recycle(done);
                done = plain;
            }

            _queue.Enqueue(new Entry(track, st.TimeMs, st.Key, 0, st.Size, done, -1));
            st.Buffer = null;
        }
    }

    // Audio spread: the object's chunks are stored one virtual packet after the other (row by row) and played column by column. Observed in a file
    // whose objects ffprobe returns de-scrambled: 20 packets of 256 bytes in chunks of 128 come out as chunks 0, 2, 4 ... 38, 1, 3 ... 39.
    private static void Unscramble(AsfAudioSpread spread, ReadOnlySpan<byte> stored, byte[] played)
    {
        int perPacket = spread.PacketLength / spread.ChunkLength;
        int chunk = spread.ChunkLength;
        for (int row = 0; row < spread.Span; row++)
        {
            for (int column = 0; column < perPacket; column++)
            {
                stored.Slice((row * perPacket + column) * chunk, chunk).CopyTo(played.AsSpan((column * spread.Span + row) * chunk, chunk));
            }
        }
    }

    private byte[] Rent(int size)
    {
        while (_pool.TryPop(out byte[]? b))
        {
            if (b.Length >= size)
            {
                return b;
            }
        }

        return new byte[Math.Max(size, 256)];
    }

    private void Recycle(byte[]? buffer)
    {
        if (buffer is { Length: > 0 } && _pool.Count < 16)
        {
            _pool.Push(buffer);
        }
    }

    private readonly record struct Entry(int Track, long TimeMs, bool Key, int Start, int Length, byte[]? Owned, long SourceOffset, bool Corrupt = false);

    private struct StreamState
    {
        public bool Active;
        public uint ObjectNumber;
        public int Size;
        public int Have;
        public long TimeMs;
        public bool Key;
        public bool Cut;
        public byte[]? Buffer;
    }

    /// <summary>
    /// Seeking works on the demuxer's own times (presentation time less the preroll, which is also how the Simple Index counts: observed in files
    /// whose first keyframe is indexed at 0 while its payloads carry the preroll as presentation time). The index only narrows the search: it
    /// is one entry per interval, naming the packet of the keyframe at or before that time, so the exact keyframe is found by reading packet
    /// headers forward from the entry.
    /// </summary>
    public async ValueTask<MediaTime> SeekAsync(MediaTime target, SeekMode mode, CancellationToken ct)
    {
        if (!CanSeek)
        {
            throw new NotSupportedException("This ASF source cannot be seeked: it is forward-only, has variable-size packets or is protected.");
        }

        ResetAssembly();
        _skipTrack = -1;
        int primary = 0;
        for (int i = 0; i < Tracks.Count; i++)
        {
            if (Tracks[i].Kind == TrackKind.Video)
            {
                primary = i;
                break;
            }
        }

        if (Tracks.Count == 0)
        {
            return MediaTime.Zero;
        }

        // Every audio object is a keyframe, as the packets report it.
        int stream = (int)Tracks[primary].Id;
        bool anyKey = Tracks[primary].Kind != TrackKind.Video;
        long wanted = Math.Max(0, target.Rescale(1000, mode == SeekMode.PreviousKeyframe ? MediaRounding.Floor : MediaRounding.Ceiling).Ticks);
        _index ??= await ReadIndexAsync(ct).ConfigureAwait(false);
        long entry = 0;
        if (_index is { Packets.Length: > 0 } index)
        {
            entry = Math.Min(wanted / Math.Max(1, index.IntervalMs), index.Packets.Length - 1);
        }

        while (true)
        {
            long startPacket = _index is { } ix && entry > 0 ? ix.Packets[entry] : 0;
            bool found = false, passed = false;
            long bestPacket = 0, bestTime = 0, lastTime = 0, lastPacket = -1;
            _nextOffset = _header.DataStart + startPacket * _fixedSize;
            _packetNumber = startPacket;
            while (!passed)
            {
                long number = _packetNumber;
                if (!await ReadDataPacketScanAsync(ct).ConfigureAwait(false))
                {
                    break;
                }

                foreach (AsfPayload p in _payloads)
                {
                    if (p.Stream != stream || !(p.Key || anyKey) || !(p.Compressed || p.Offset == 0) || p.TimeMs < 0)
                    {
                        continue;
                    }

                    long t = Math.Max(0, p.TimeMs - _preroll);
                    lastTime = t;
                    lastPacket = number;
                    if (mode == SeekMode.PreviousKeyframe)
                    {
                        if (t <= wanted)
                        {
                            (found, bestTime, bestPacket) = (true, t, number);
                        }
                        else
                        {
                            passed = true;
                            if (!found && entry == 0)
                            {
                                (found, bestTime, bestPacket) = (true, t, number); // before the first keyframe: the first one
                            }

                            break;
                        }
                    }
                    else if (t >= wanted)
                    {
                        (found, bestTime, bestPacket) = (true, t, number);
                        passed = true;
                        break;
                    }
                }
            }

            if (!found && mode == SeekMode.PreviousKeyframe && entry > 0)
            {
                entry--; // the entry's packet starts after the wanted time: look from the entry before
                continue;
            }

            if (!found)
            {
                // Past the last keyframe: position at the end.
                _nextOffset = _header.DataStart + (_packetNumber > 0 ? _packetNumber : 0) * _fixedSize;
                return lastPacket >= 0 ? new MediaTime(lastTime, 1000) : MediaTime.Zero;
            }

            _packetNumber = bestPacket;
            _nextOffset = _header.DataStart + bestPacket * _fixedSize;
            _skipTrack = primary;
            _skipBefore = bestTime;
            return new MediaTime(bestTime, 1000);
        }
    }

    private void ResetAssembly()
    {
        while (_queue.Count > 0)
        {
            Entry e = _queue.Dequeue();
            if (e.Owned is not null)
            {
                Recycle(e.Owned);
            }
        }

        for (int i = 0; i < _streams.Length; i++)
        {
            if (_streams[i].Active)
            {
                Recycle(_streams[i].Buffer);
            }

            _streams[i].Active = false;
        }

        _input.Invalidate();
    }

    // Reads one packet for the key scan: like ReadDataPacketAsync, but nothing is assembled and the payload list is left for the caller.
    private async ValueTask<bool> ReadDataPacketScanAsync(CancellationToken ct)
    {
        if (_packetNumber >= _packetLimit || _nextOffset >= _dataEnd)
        {
            return false;
        }

        int want = (int)Math.Min(_packetBuffer.Length, _dataEnd - _nextOffset);
        int got = await _input.ReadAsync(_nextOffset, _packetBuffer.AsMemory(0, want), ct).ConfigureAwait(false);
        if (got < 4 || (got >= 16 && IsTopLevelObject(_packetBuffer.AsSpan(0, 16))))
        {
            return false;
        }

        _ = AsfPacketParser.Parse(_packetBuffer.AsSpan(0, got), _fixedSize, _payloads, out int length);
        _nextOffset += length > 0 && _variable ? length : _fixedSize;
        _packetNumber++;
        return true;
    }

    private SimpleIndex? _index;

    private sealed record SimpleIndex(long IntervalMs, long[] Packets);

    // Looks for a Simple Index after the data. Its body: file ID (16), time interval (8, in 100 ns), maximum packet count (4), entry count (4),
    // then per entry a packet number (4) and a packet count (2). Null when the file has none.
    private async ValueTask<SimpleIndex?> ReadIndexAsync(CancellationToken ct)
    {
        long length = _input.Length;
        long at = _header.DataObjectSize > 50 ? _header.DataObjectOffset + _header.DataObjectSize : _header.DataStart + (_packetLimit == long.MaxValue ? 0 : _packetLimit * _fixedSize);
        byte[] head = new byte[24];
        for (int guard = 0; guard < 16 && at >= 0 && at + 24 <= length; guard++)
        {
            if (await _input.ReadAsync(at, head, ct).ConfigureAwait(false) < 24)
            {
                break;
            }

            Guid guid = new(head.AsSpan(0, 16));
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(16));
            if (size < 24 || size > (ulong)(length - at))
            {
                break;
            }

            if (guid == AsfGuids.SimpleIndex && size <= 64 << 20 && size >= 24 + 32)
            {
                byte[] body = new byte[size - 24];
                _limits.CheckAllocation(body.Length, at);
                if (await _input.ReadAsync(at + 24, body, ct).ConfigureAwait(false) == body.Length)
                {
                    long interval = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(16)), long.MaxValue) / 10_000;
                    uint count = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(28));
                    long usable = Math.Min(count, (body.Length - 32) / 6);
                    long[] packets = new long[usable];
                    for (int i = 0; i < usable; i++)
                    {
                        long packet = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(32 + i * 6));
                        packets[i] = packet >= _packetLimit || packet == 0xFFFFFFFF ? 0 : packet;
                    }

                    return interval > 0 && usable > 0 ? new SimpleIndex(interval, packets) : null;
                }
            }

            at += (long)size;
        }

        return null;
    }
}
