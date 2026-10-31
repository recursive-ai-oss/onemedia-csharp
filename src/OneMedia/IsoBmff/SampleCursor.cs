namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>
/// A small read window over a run of fixed-size big-endian fields in a source. Sequential access refills one buffer; nothing is
/// allocated after construction.
/// </summary>
internal sealed class TableWindow
{
    private const int WindowBytes = 4096;
    private readonly IMediaSource _source;
    private readonly long _start;
    private readonly long _length;
    private readonly byte[] _buffer = new byte[WindowBytes];
    private long _bufferStart = -1;
    private int _bufferLength;

    public TableWindow(IMediaSource source, long start, long length)
    {
        _source = source;
        _start = start;
        _length = Math.Max(0, length);
    }

    /// <summary>Makes bytes [relative, relative + count) available, returning false past the end of the table or the source.</summary>
    private bool Fill(long relative, int count)
    {
        if (relative < 0 || relative + count > _length)
        {
            return false;
        }

        if (relative >= _bufferStart && _bufferStart >= 0 && relative + count <= _bufferStart + _bufferLength)
        {
            return true;
        }

        int want = (int)Math.Min(WindowBytes, _length - relative);
        int n = _source.Read(_start + relative, _buffer.AsSpan(0, want));
        _bufferStart = relative;
        _bufferLength = n;
        return n >= count;
    }

    public bool TryU8(long relative, out byte value)
    {
        if (Fill(relative, 1))
        {
            value = _buffer[relative - _bufferStart];
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryU16(long relative, out uint value)
    {
        if (Fill(relative, 2))
        {
            value = Be.U16(_buffer, (int)(relative - _bufferStart));
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryU32(long relative, out uint value)
    {
        if (Fill(relative, 4))
        {
            value = Be.U32(_buffer, (int)(relative - _bufferStart));
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryU64(long relative, out ulong value)
    {
        if (Fill(relative, 8))
        {
            value = Be.U64(_buffer, (int)(relative - _bufferStart));
            return true;
        }

        value = 0;
        return false;
    }
}

/// <summary>One sample as the sample table describes it.</summary>
internal struct TableSample
{
    public long Index;
    public long Offset;
    public int Size;
    public long Dts;
    public long CompositionOffset;
    public long Duration;
    public bool IsSync;
    public bool IsDisposable;
    public int DescriptionIndex;
}

/// <summary>
/// Walks one track's sample table in sample order using fixed-size read windows. Memory is constant whatever the table length: the
/// decoding-time, composition-time, sync, size and chunk tables are read as the cursor passes them, never loaded.
/// </summary>
internal sealed class SampleCursor
{
    private readonly SampleTableLayout _t;
    private readonly DefectLog _defects;
    private readonly long _fileLength;
    private readonly TableWindow _stts, _ctts, _stss, _stsc, _chunks, _sizes, _sdtp;
    private readonly long _sttsRuns, _cttsRuns, _stssCount, _stscCount, _chunkCount;
    private readonly bool _hasStss;
    private readonly long _boxOffset;

    // decoding time
    private long _sttsRun, _sttsLeft;
    private uint _delta;
    // composition offset
    private long _cttsRun, _cttsLeft;
    private int _cto;
    // chunk walk
    private long _stscEntry, _chunk, _chunkLeft, _chunkNextFirst;
    private uint _spc, _desc;
    private long _chunkOffset;
    // sync samples
    private long _syncIdx;
    private uint _nextSync;

    private TableSample _current;
    private bool _valid;
    private bool _reportedTiming;

    public SampleCursor(SampleTableLayout layout, IMediaSource source, DefectLog defects, long fileLength, long trackOffset)
    {
        _t = layout;
        _defects = defects;
        _fileLength = fileLength;
        _boxOffset = trackOffset;
        _stts = new TableWindow(source, layout.Stts.Offset, layout.Stts.Count * 8);
        _sttsRuns = layout.Stts.Count;
        _ctts = new TableWindow(source, layout.Ctts.Offset, layout.Ctts.Count * 8);
        _cttsRuns = layout.Ctts.IsPresent ? layout.Ctts.Count : 0;
        _hasStss = layout.Stss.IsPresent;
        _stssCount = _hasStss ? layout.Stss.Count : 0;
        _stss = new TableWindow(source, layout.Stss.Offset, _stssCount * 4);
        _stscCount = layout.Stsc.Count;
        _stsc = new TableWindow(source, layout.Stsc.Offset, _stscCount * 12);
        _chunkCount = layout.ChunkOffsets.Count;
        _chunks = new TableWindow(source, layout.ChunkOffsets.Offset, _chunkCount * (layout.ChunkOffsets64 ? 8 : 4));
        _sizes = new TableWindow(source, layout.SizeTable.Offset, layout.SizeTable.IsPresent ? ((layout.SizeTable.Count * layout.SizeFieldBits) + 7) / 8 : 0);
        _sdtp = new TableWindow(source, layout.Sdtp.Offset, layout.Sdtp.IsPresent ? layout.Sdtp.Count : 0);
        SampleCount = layout.SampleCount;
        SeekToSample(0);
    }

    public long SampleCount { get; }

    public bool IsValid => _valid;

    public ref readonly TableSample Current => ref _current;

    public long SyncSampleCount => _hasStss ? _stssCount : SampleCount;

    /// <summary>Moves to the next sample; false at the end of the table or where it stops making sense.</summary>
    public bool MoveNext()
    {
        if (!_valid)
        {
            return false;
        }

        long next = _current.Index + 1;
        if (next >= SampleCount)
        {
            _valid = false;
            return false;
        }

        // decoding time
        _current.Dts += _delta;
        if (--_sttsLeft <= 0 && !LoadTimingRun(_sttsRun + 1))
        {
            Stop("The time-to-sample table ends before the sample size table does.", "isobmff.stts-short");
            return false;
        }

        // composition offset (a short table leaves the offset at its last value; ctts is optional)
        if (_cttsRuns > 0 && --_cttsLeft <= 0)
        {
            LoadCompositionRun(_cttsRun + 1);
        }

        // chunk position
        _chunkOffset += _current.Size;
        if (--_chunkLeft <= 0 && !EnterChunk(_chunk + 1))
        {
            _valid = false;
            return false;
        }

        _current.Index = next;
        _current.Offset = _chunkOffset;
        _current.DescriptionIndex = (int)_desc;
        _current.Duration = _delta;
        _current.CompositionOffset = _cto;
        _current.Size = SizeOf(next);
        if (_hasStss)
        {
            _current.IsSync = _nextSync == next + 1;
            if (_current.IsSync)
            {
                LoadSync(_syncIdx + 1);
            }
        }
        else
        {
            _current.IsSync = true;
        }

        _current.IsDisposable = IsDisposable(next);
        return true;
    }

    /// <summary>Positions on a sample by number (0-based). Returns false if the sample is not described by the table.</summary>
    public bool SeekToSample(long index)
    {
        _valid = false;
        if (index < 0 || index >= SampleCount || !_t.Stts.IsPresent || !_t.Stsc.IsPresent)
        {
            return false;
        }

        // decoding time: walk the runs to the one holding the sample.
        long run = 0, firstOfRun = 0;
        long dts = 0;
        uint count = 0, delta = 0;
        while (true)
        {
            if (run >= _sttsRuns || !_stts.TryU32(run * 8, out count) || !_stts.TryU32((run * 8) + 4, out delta))
            {
                Stop("The time-to-sample table ends before the sample size table does.", "isobmff.stts-short");
                return false;
            }

            if (index < firstOfRun + count)
            {
                break;
            }

            dts += (long)count * delta;
            firstOfRun += count;
            run++;
        }

        _sttsRun = run;
        _sttsLeft = firstOfRun + count - index;
        _delta = delta;
        dts += (index - firstOfRun) * delta;

        // composition offsets
        _cto = 0;
        if (_cttsRuns > 0)
        {
            long cr = 0, first = 0;
            _cttsLeft = 0;
            while (cr < _cttsRuns && _ctts.TryU32(cr * 8, out uint cc) && _ctts.TryU32((cr * 8) + 4, out uint co))
            {
                if (index < first + cc)
                {
                    _cttsRun = cr;
                    _cttsLeft = first + cc - index;
                    _cto = unchecked((int)co);
                    break;
                }

                first += cc;
                cr++;
            }

            if (_cttsLeft == 0)
            {
                _cttsRun = _cttsRuns;
            }
        }

        // chunk containing the sample
        long cumulative = 0;
        long entry = 0;
        bool found = false;
        uint firstChunk = 0, spc = 0, desc = 0;
        while (entry < _stscCount)
        {
            if (!_stsc.TryU32(entry * 12, out firstChunk) || !_stsc.TryU32((entry * 12) + 4, out spc) || !_stsc.TryU32((entry * 12) + 8, out desc))
            {
                break;
            }

            long nextFirst = _chunkCount + 1;
            if (entry + 1 < _stscCount && _stsc.TryU32((entry + 1) * 12, out uint nf))
            {
                nextFirst = nf;
            }

            if (firstChunk == 0 || nextFirst < firstChunk)
            {
                Stop("The sample-to-chunk table is not in order.", "isobmff.stsc-order");
                return false;
            }

            long samplesHere = (nextFirst - firstChunk) * spc;
            if (spc > 0 && index < cumulative + samplesHere)
            {
                long into = index - cumulative;
                _stscEntry = entry;
                _chunk = firstChunk + (into / spc);
                _chunkLeft = spc - (into % spc);
                _spc = spc;
                _desc = desc;
                _chunkNextFirst = nextFirst;
                found = true;
                break;
            }

            cumulative += samplesHere;
            entry++;
        }

        if (!found)
        {
            Stop("The sample-to-chunk table does not cover every sample.", "isobmff.stsc-short");
            return false;
        }

        if (!ChunkStart(_chunk, out long chunkStart))
        {
            Stop("A chunk offset lies outside the chunk offset table.", "isobmff.chunk-missing");
            return false;
        }

        // offset of the sample within its chunk: the sizes of the samples before it in the chunk
        long firstInChunk = index - (_spc - _chunkLeft);
        long inChunk = 0;
        for (long s = firstInChunk; s < index; s++)
        {
            inChunk += SizeOf(s);
        }

        _chunkOffset = chunkStart + inChunk;

        // sync samples: the first listed sync sample number at or after this one
        if (_hasStss)
        {
            long lo = 0, hi = _stssCount;
            while (lo < hi)
            {
                long mid = (lo + hi) / 2;
                _stss.TryU32(mid * 4, out uint v);
                if (v < index + 1)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            LoadSync(lo);
        }

        _current = new TableSample
        {
            Index = index,
            Offset = _chunkOffset,
            Size = SizeOf(index),
            Dts = dts,
            CompositionOffset = _cto,
            Duration = delta,
            IsSync = !_hasStss || _nextSync == index + 1,
            DescriptionIndex = (int)desc,
        };
        if (_hasStss && _current.IsSync)
        {
            LoadSync(_syncIdx + 1);
        }

        _current.IsDisposable = IsDisposable(index);
        _valid = true;
        return true;
    }

    /// <summary>Finds the last sample whose decoding time is at or before <paramref name="dts"/> (0 when none is).</summary>
    public long FindSampleByDecodeTime(long dts)
    {
        long run = 0, firstOfRun = 0, time = 0;
        while (run < _sttsRuns && _stts.TryU32(run * 8, out uint count) && _stts.TryU32((run * 8) + 4, out uint delta))
        {
            long span = (long)count * delta;
            if (delta > 0 && dts < time + span)
            {
                return Math.Min(firstOfRun + ((dts - time) / delta), SampleCount - 1);
            }

            if (delta == 0 && count > 0 && dts < time)
            {
                return Math.Max(0, firstOfRun - 1);
            }

            time += span;
            firstOfRun += count;
            run++;
        }

        return Math.Max(0, Math.Min(firstOfRun, SampleCount) - 1);
    }

    /// <summary>The 0-based number of the last sync sample at or before <paramref name="index"/>, or the first sync sample after it when none precedes.</summary>
    public long SyncAtOrBefore(long index)
    {
        if (!_hasStss)
        {
            return index;
        }

        long lo = 0, hi = _stssCount;
        while (lo < hi)
        {
            long mid = (lo + hi) / 2;
            _stss.TryU32(mid * 4, out uint v);
            if (v <= index + 1)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        // lo is the count of sync samples numbered <= index + 1
        if (lo == 0)
        {
            return _stssCount > 0 && _stss.TryU32(0, out uint first) ? Math.Max(0, first - 1L) : 0;
        }

        _stss.TryU32((lo - 1) * 4, out uint prev);
        return prev - 1L;
    }

    /// <summary>The 0-based number of the first sync sample at or after <paramref name="index"/>, or -1 when none follows.</summary>
    public long SyncAtOrAfter(long index)
    {
        if (!_hasStss)
        {
            return index < SampleCount ? index : -1;
        }

        long lo = 0, hi = _stssCount;
        while (lo < hi)
        {
            long mid = (lo + hi) / 2;
            _stss.TryU32(mid * 4, out uint v);
            if (v < index + 1)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo < _stssCount && _stss.TryU32(lo * 4, out uint s) ? s - 1L : -1;
    }

    private int SizeOf(long index)
    {
        if (_t.ConstantSize != 0)
        {
            return (int)Math.Min(_t.ConstantSize, int.MaxValue);
        }

        uint v = 0;
        bool ok = _t.SizeFieldBits switch
        {
            32 => _sizes.TryU32(index * 4, out v),
            16 => _sizes.TryU16(index * 2, out v),
            8 => _sizes.TryU8(index, out byte b) && Set(b, out v),
            _ => _sizes.TryU8(index / 2, out byte n) && Set((index & 1) == 0 ? (uint)(n >> 4) : (uint)(n & 0xF), out v),
        };
        return ok ? (int)Math.Min(v, int.MaxValue) : 0;

        static bool Set(uint value, out uint v)
        {
            v = value;
            return true;
        }
    }

    private bool IsDisposable(long index)
    {
        // §8.6.4: is_depended_on is bits 3-2 of each sdtp byte; 2 means no other sample depends on this one.
        return _t.Sdtp.IsPresent && index < _t.Sdtp.Count && _sdtp.TryU8(index, out byte b) && ((b >> 2) & 3) == 2;
    }

    private bool LoadTimingRun(long run)
    {
        while (run < _sttsRuns)
        {
            if (!_stts.TryU32(run * 8, out uint count) || !_stts.TryU32((run * 8) + 4, out uint delta))
            {
                return false;
            }

            if (count > 0)
            {
                _sttsRun = run;
                _sttsLeft = count;
                _delta = delta;
                return true;
            }

            run++;
        }

        return false;
    }

    private void LoadCompositionRun(long run)
    {
        while (run < _cttsRuns && _ctts.TryU32(run * 8, out uint count) && _ctts.TryU32((run * 8) + 4, out uint offset))
        {
            if (count > 0)
            {
                _cttsRun = run;
                _cttsLeft = count;
                _cto = unchecked((int)offset);
                return;
            }

            run++;
        }

        _cttsRun = _cttsRuns;
        _cttsLeft = long.MaxValue;
    }

    private void LoadSync(long idx)
    {
        _syncIdx = idx;
        _nextSync = idx < _stssCount && _stss.TryU32(idx * 4, out uint v) ? v : uint.MaxValue;
    }

    private bool ChunkStart(long chunk, out long offset)
    {
        offset = 0;
        long i = chunk - 1;
        if (i < 0 || i >= _chunkCount)
        {
            return false;
        }

        if (_t.ChunkOffsets64)
        {
            if (!_chunks.TryU64(i * 8, out ulong v) || v > long.MaxValue)
            {
                return false;
            }

            offset = (long)v;
            return true;
        }

        if (!_chunks.TryU32(i * 4, out uint o))
        {
            return false;
        }

        offset = o;
        return true;
    }

    private bool EnterChunk(long chunk)
    {
        if (chunk >= _chunkNextFirst)
        {
            // Move to the next sample-to-chunk entry.
            long entry = _stscEntry + 1;
            if (entry >= _stscCount || !_stsc.TryU32(entry * 12, out uint firstChunk) || !_stsc.TryU32((entry * 12) + 4, out uint spc) || !_stsc.TryU32((entry * 12) + 8, out uint desc) || spc == 0)
            {
                Stop("The sample-to-chunk table does not cover every sample.", "isobmff.stsc-short");
                return false;
            }

            _stscEntry = entry;
            _spc = spc;
            _desc = desc;
            _chunkNextFirst = entry + 1 < _stscCount && _stsc.TryU32((entry + 1) * 12, out uint nf) ? nf : _chunkCount + 1;
            if (firstChunk != chunk)
            {
                chunk = firstChunk;
            }
        }

        if (!ChunkStart(chunk, out long start))
        {
            Stop("A chunk offset lies outside the chunk offset table.", "isobmff.chunk-missing");
            return false;
        }

        _chunk = chunk;
        _chunkLeft = _spc;
        _chunkOffset = start;
        return true;
    }

    private void Stop(string message, string code)
    {
        _valid = false;
        if (!_reportedTiming)
        {
            _reportedTiming = true;
            _defects.Add(DefectKind.Inconsistent, DefectSeverity.Error, _boxOffset, message, code);
        }
    }
}
