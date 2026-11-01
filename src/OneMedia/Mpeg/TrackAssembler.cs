namespace RecursiveAI.OneMedia.Mpeg;

/// <summary>Timestamps and position that a PES header gives the bytes after it. Timestamps are unwrapped 90 kHz ticks, negative when absent.</summary>
internal readonly record struct PesStamp(long Pts, long Dts, long Pos, bool RandomAccess, bool Contiguous = false)
{
    public bool HasTime => Pts >= 0 || Dts >= 0;
}

/// <summary>
/// Completed packets waiting to be handed out. Their bytes live in one arena that is reused once the queue drains, so a
/// steady pull does not allocate. A view returned by <see cref="TryDequeue"/> stays valid until the next enqueue or dequeue.
/// </summary>
internal sealed class PacketQueue(MediaLimits limits)
{
    private readonly List<(PacketInfo Info, int Offset)> _entries = [];
    private byte[] _arena = new byte[64 * 1024];
    private int _used;
    private int _head;

    public bool IsEmpty => _head >= _entries.Count;

    public int Count => _entries.Count - _head;

    public void Enqueue(in PacketInfo info, ReadOnlySpan<byte> data)
    {
        if (_head >= _entries.Count)
        {
            _entries.Clear();
            _head = 0;
            _used = 0;
        }

        long at = info.SourceRange.IsNone ? -1 : info.SourceRange.Offset;
        limits.CheckPacket(data.Length, at);
        if (_used + data.Length > _arena.Length)
        {
            long wanted = Math.Max((long)_arena.Length * 2, (long)_used + data.Length);
            limits.CheckAllocation(wanted, at);
            Array.Resize(ref _arena, (int)wanted);
        }

        data.CopyTo(_arena.AsSpan(_used));
        _entries.Add((info with { Size = data.Length }, _used));
        _used += data.Length;
    }

    public bool TryDequeue(out PacketInfo info, out ReadOnlySpan<byte> data)
    {
        if (_head >= _entries.Count)
        {
            info = default;
            data = default;
            return false;
        }

        (PacketInfo i, int offset) = _entries[_head++];
        info = i;
        data = _arena.AsSpan(offset, i.Size);
        return true;
    }

    public void Clear()
    {
        _entries.Clear();
        _head = 0;
        _used = 0;
    }
}

/// <summary>
/// Cuts one elementary stream, delivered as PES payloads, into packets (access units or codec frames). Timing comes from the
/// PES headers: a stamp applies to the first unit that starts in the PES it heads (ISO/IEC 13818-1 §2.4.3.7).
/// </summary>
internal abstract class TrackAssembler
{
    /// <summary>The 90 kHz clock of PES timestamps.</summary>
    public const long Clock = 90000;

    private readonly PacketQueue _queue;
    private readonly List<Mark> _marks = [];
    private byte[] _buf = new byte[16 * 1024];
    private long _lossAbs = -1;
    private long _absTail; // stream offset of the byte after the last one appended

    protected TrackAssembler(int index, PacketQueue queue, MediaLimits limits, DefectLog defects)
    {
        Index = index;
        _queue = queue;
        Limits = limits;
        Defects = defects;
    }

    private struct Mark
    {
        public long Abs;
        public PesStamp Stamp;
        public bool TimeUsed;
        public bool PosUsed;
    }

    public int Index { get; }

    /// <summary>Ticks per second of the times this assembler writes: the 90 kHz PES clock unless a subclass derives its own.</summary>
    public virtual long Timescale => Clock;

    public MediaLimits Limits { get; }

    public DefectLog Defects { get; }

    /// <summary>What the first units reveal about the stream (size, rate, profile…), or null until seen.</summary>
    public virtual Codecs.CodecParameters? Parameters { get; protected set; }

    /// <summary>Codec setup bytes derived from the stream (for example an AAC AudioSpecificConfig), or empty.</summary>
    public virtual ReadOnlyMemory<byte> Configuration { get; protected set; }

    /// <summary>The codec the stream turned out to be, when its bytes tell more than the container did (MPEG audio layer, MPEG-1 or MPEG-2 video); unknown otherwise.</summary>
    public CodecId DetectedCodec { get; protected set; }

    /// <summary>Packets produced so far.</summary>
    public long Emitted { get; protected set; }

    /// <summary>True once the track needs nothing more from the stream to be described.</summary>
    public virtual bool IsDescribed => Parameters is not null;

    protected int Head { get; private set; }

    protected int Tail { get; private set; }

    protected ReadOnlySpan<byte> Buffered => _buf.AsSpan(Head, Tail - Head);

    /// <summary>Feeds the next payload bytes. <paramref name="startsPes"/> is true when <paramref name="data"/> begins a PES packet.</summary>
    public virtual void Push(ReadOnlySpan<byte> data, in PesStamp stamp, bool startsPes)
    {
        if (startsPes)
        {
            _marks.Add(new Mark { Abs = _absTail, Stamp = stamp });
        }

        Append(data);
        Scan(final: false);
    }

    /// <summary>Ends the stream: whatever is buffered becomes the last packet.</summary>
    public virtual void Flush()
    {
        Scan(final: true);
        FlushPending();
        Head = Tail = 0;
        _marks.Clear();
    }

    /// <summary>Forgets partial state, for a seek.</summary>
    public virtual void Reset()
    {
        Head = Tail = 0;
        _marks.Clear();
    }

    /// <summary>Looks for complete units in the buffer; with <paramref name="final"/> the buffer ends the stream.</summary>
    protected abstract void Scan(bool final);

    /// <summary>Hands over a pending packet that waits for its successor to learn its duration.</summary>
    protected virtual void FlushPending()
    {
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (Tail + data.Length > _buf.Length)
        {
            // Make room by dropping consumed bytes first, then grow.
            if (Head > 0)
            {
                Buffer.BlockCopy(_buf, Head, _buf, 0, Tail - Head);
                Tail -= Head;
                Head = 0;
            }

            if (Tail + data.Length > _buf.Length)
            {
                long wanted = Math.Max((long)_buf.Length * 2, (long)Tail + data.Length);
                Limits.CheckPacket(wanted, -1);
                Array.Resize(ref _buf, (int)wanted);
            }
        }

        data.CopyTo(_buf.AsSpan(Tail));
        Tail += data.Length;
        _absTail += data.Length;
    }

    /// <summary>
    /// Takes the timing for a unit that starts at buffer index <paramref name="start"/>: the earliest unused stamp among the PES
    /// packets that began at or before it, and the position of the latest of them (-1 when that packet already gave its position to an earlier unit).
    /// </summary>
    protected bool TakeStamp(int start, out PesStamp stamp, out long pos)
    {
        long abs = _absTail - (Tail - start);
        stamp = new PesStamp(-1, -1, -1, false);
        pos = -1;
        bool found = false;
        int last = -1;
        for (int i = 0; i < _marks.Count && _marks[i].Abs <= abs; i++)
        {
            last = i;
            Mark m = _marks[i];
            if (!found && m.Stamp.HasTime && !m.TimeUsed)
            {
                stamp = m.Stamp;
                m.TimeUsed = true;
                _marks[i] = m;
                found = true;
            }
        }

        if (last >= 0)
        {
            Mark m = _marks[last];
            if (m.Stamp.Contiguous)
            {
                // The bytes after this mark are consecutive in the file, so the unit starts exactly that far into them.
                pos = m.Stamp.Pos + (abs - m.Abs);
            }
            else if (!m.PosUsed)
            {
                pos = m.Stamp.Pos;
                m.PosUsed = true;
                _marks[last] = m;
            }

            long keep = m.Abs;
            // Older packets that gave up everything they had are not needed again.
            int w = 0;
            for (int i = 0; i < _marks.Count; i++)
            {
                Mark x = _marks[i];
                if (!(x.Abs < keep && (x.TimeUsed || !x.Stamp.HasTime)))
                {
                    _marks[w++] = x;
                }
            }

            _marks.RemoveRange(w, _marks.Count - w);
        }

        return found;
    }

    /// <summary>Records that transport packets were lost at this point of the stream, so the unit spanning it is flagged corrupt.</summary>
    public void NoteLoss() => _lossAbs = _absTail;

    /// <summary>True (once) when a loss fell inside the unit of <paramref name="length"/> bytes at buffer index <paramref name="start"/>.</summary>
    protected bool CoversLoss(int start, int length)
    {
        if (_lossAbs < 0)
        {
            return false;
        }

        long abs = _absTail - (Tail - start);
        if (_lossAbs >= abs && _lossAbs <= abs + length)
        {
            _lossAbs = -1;
            return true;
        }

        if (_lossAbs < abs)
        {
            _lossAbs = -1;
        }

        return false;
    }

    /// <summary>Removes the first <paramref name="count"/> buffered bytes.</summary>
    protected void Consume(int count)
    {
        Head += count;
        if (Head == Tail)
        {
            Head = Tail = 0;
        }
    }

    /// <summary>Queues one finished packet.</summary>
    protected void Emit(ReadOnlySpan<byte> data, long pos, PacketAttributes flags, long pts, long dts, long duration)
    {
        PacketInfo info = new(
            Index,
            pts >= 0 ? new MediaTime(pts, Timescale) : MediaTime.Unknown,
            dts >= 0 ? new MediaTime(dts, Timescale) : MediaTime.Unknown,
            duration > 0 ? new MediaTime(duration, Timescale) : MediaTime.Unknown,
            flags,
            pos >= 0 ? new ByteRange(pos, 0) : ByteRange.None,
            data.Length);
        _queue.Enqueue(info, data);
        Emitted++;
    }
}
