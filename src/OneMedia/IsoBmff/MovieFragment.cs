namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>One track run box (§8.8.8) located inside a movie fragment buffer.</summary>
internal struct TrunRun
{
    public int EntriesAt;
    public uint Flags;
    public uint SampleCount;
    public uint FirstSampleFlags;
    public long DataStart;

    public readonly int EntrySize => ((Flags & 0x100) != 0 ? 4 : 0) + ((Flags & 0x200) != 0 ? 4 : 0) + ((Flags & 0x400) != 0 ? 4 : 0) + ((Flags & 0x800) != 0 ? 4 : 0);
}

/// <summary>One track fragment (§8.8.6) of the movie fragment being read.</summary>
internal sealed class TrafInfo
{
    public int TrackIndex;
    public uint DefaultDuration, DefaultSize, DefaultFlags;
    public int DescriptionIndex;
    public bool HasBaseDts;
    public long BaseDts;
    public readonly List<TrunRun> Runs = [];

    // iteration state
    public int Run;
    public uint InRun;
    public int Pos;
    public long DataOffset;
    public long Dts;
    public bool Active;
}

/// <summary>One sample of a movie fragment, with the track fragment it came from.</summary>
internal struct FragmentSample
{
    public int TrackIndex;
    public long Offset;
    public int Size;
    public long Dts;
    public long CompositionOffset;
    public long Duration;
    public uint Flags;
    public int DescriptionIndex;
}

/// <summary>
/// A movie fragment held in memory (the <c>moof</c> box bytes), with its track fragments located. Samples are produced one at a time
/// from the buffer. ISO/IEC 14496-12 §8.8.
/// </summary>
internal sealed class MovieFragment
{
    private byte[] _buffer = new byte[4096];
    private int _length;
    private TrafInfo[] _trafs = [];
    private int _trafCount;

    public long MoofOffset { get; private set; }

    public int TrafCount => _trafCount;

    /// <summary>Forgets the loaded fragment, so no samples are produced until the next one is parsed.</summary>
    public void Clear() => _trafCount = 0;

    public byte[] RentBuffer(int length)
    {
        if (_buffer.Length < length)
        {
            _buffer = new byte[Math.Max(length, _buffer.Length * 2)];
        }

        _length = length;
        return _buffer;
    }

    /// <summary>Parses the box now in the buffer (filled through <see cref="RentBuffer"/>) as a <c>moof</c>.</summary>
    public void Parse(long moofOffset, IReadOnlyList<IsoTrack> tracks, TrackState[] states, DefectLog defects, MediaLimits limits)
    {
        MoofOffset = moofOffset;
        _trafCount = 0;
        ReadOnlySpan<byte> buf = _buffer.AsSpan(0, _length);
        int pos = 8; // the moof header; a 64-bit-size moof is handled by the caller passing it as 8 + 8, see Skip below
        if (_length >= 16 && Be.U32(buf, 0) == 1)
        {
            pos = 16;
        }

        while (pos + 8 <= _length)
        {
            uint size = Be.U32(buf, pos);
            uint type = Be.U32(buf, pos + 4);
            long boxSize = size;
            int hdr = 8;
            if (size == 1)
            {
                ulong big = Be.U64(buf, pos + 8);
                boxSize = big > int.MaxValue ? int.MaxValue : (long)big;
                hdr = 16;
            }
            else if (size == 0)
            {
                boxSize = _length - pos;
            }

            if (boxSize < hdr || pos + boxSize > _length)
            {
                defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Error, moofOffset + pos, "A box inside the movie fragment overruns it.", "isobmff.size-exceeds-parent");
                break;
            }

            if (type == BoxTypes.Traf)
            {
                limits.CheckElementCount(_trafCount + 1, moofOffset + pos);
                ParseTraf(pos + hdr, pos + (int)boxSize, moofOffset, tracks, states, defects, limits);
            }

            pos += (int)boxSize;
        }
    }

    private void ParseTraf(int start, int end, long moofOffset, IReadOnlyList<IsoTrack> tracks, TrackState[] states, DefectLog defects, MediaLimits limits)
    {
        ReadOnlySpan<byte> buf = _buffer.AsSpan(0, _length);
        if (_trafs.Length <= _trafCount)
        {
            Array.Resize(ref _trafs, Math.Max(4, _trafs.Length * 2));
        }

        TrafInfo traf = _trafs[_trafCount] ??= new TrafInfo();
        traf.Runs.Clear();
        traf.HasBaseDts = false;
        traf.Active = false;
        bool haveHeader = false;
        long baseOffset = moofOffset;
        bool explicitBase = false;
        long previousEnd = _trafCount > 0 ? _trafs[_trafCount - 1].DataOffset : moofOffset;
        long runEnd = -1;
        int pos = start;
        while (pos + 8 <= end)
        {
            uint size = Be.U32(buf, pos);
            uint type = Be.U32(buf, pos + 4);
            long boxSize = size == 0 ? end - pos : size;
            if (boxSize < 8 || pos + boxSize > end)
            {
                defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Error, moofOffset + pos, "A box inside the track fragment overruns it.", "isobmff.size-exceeds-parent");
                break;
            }

            int p = pos + 8;
            int boxEnd = pos + (int)boxSize;
            if (type == BoxTypes.Tfhd && boxEnd - p >= 8)
            {
                uint flags = Be.U32(buf, p) & 0xFFFFFF;
                uint trackId = Be.U32(buf, p + 4);
                int q = p + 8;
                int trackIndex = -1;
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (tracks[i].Header.TrackId == trackId)
                    {
                        trackIndex = i;
                        break;
                    }
                }

                if (trackIndex < 0)
                {
                    defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, moofOffset + pos, $"A track fragment names track {trackId}, which the movie does not declare.", "isobmff.traf-unknown-track");
                    return;
                }

                traf.TrackIndex = trackIndex;
                TrackExtends? trex = tracks[trackIndex].Extends;
                traf.DescriptionIndex = (int)(trex?.DefaultSampleDescriptionIndex ?? 1);
                traf.DefaultDuration = trex?.DefaultSampleDuration ?? 0;
                traf.DefaultSize = trex?.DefaultSampleSize ?? 0;
                traf.DefaultFlags = trex?.DefaultSampleFlags ?? 0;
                if ((flags & 0x1) != 0)
                {
                    ulong b = Be.U64(buf, q);
                    baseOffset = b > long.MaxValue ? 0 : (long)b;
                    explicitBase = true;
                    q += 8;
                }

                if ((flags & 0x2) != 0)
                {
                    traf.DescriptionIndex = (int)Math.Min(Be.U32(buf, q), int.MaxValue);
                    q += 4;
                }

                if ((flags & 0x8) != 0)
                {
                    traf.DefaultDuration = Be.U32(buf, q);
                    q += 4;
                }

                if ((flags & 0x10) != 0)
                {
                    traf.DefaultSize = Be.U32(buf, q);
                    q += 4;
                }

                if ((flags & 0x20) != 0)
                {
                    traf.DefaultFlags = Be.U32(buf, q);
                }

                if (!explicitBase && (flags & 0x20000) == 0 && _trafCount > 0)
                {
                    // §8.8.7.1: without a base offset or default-base-is-moof, a later track fragment's data begins where the previous one's ended.
                    baseOffset = previousEnd;
                }

                haveHeader = true;
                runEnd = baseOffset;
            }
            else if (type == BoxTypes.Tfdt && boxEnd - p >= 8)
            {
                traf.HasBaseDts = true;
                traf.BaseDts = buf[p] == 1 ? unchecked((long)Be.U64(buf, p + 4)) : Be.U32(buf, p + 4);
            }
            else if (type == BoxTypes.Trun && haveHeader && boxEnd - p >= 8)
            {
                uint flags = Be.U32(buf, p) & 0xFFFFFF;
                uint count = Be.U32(buf, p + 4);
                int q = p + 8;
                long dataStart = runEnd;
                if ((flags & 0x1) != 0)
                {
                    dataStart = baseOffset + Be.I32(buf, q);
                    q += 4;
                }

                uint firstFlags = traf.DefaultFlags;
                if ((flags & 0x4) != 0)
                {
                    firstFlags = Be.U32(buf, q);
                    q += 4;
                }

                TrunRun run = new() { EntriesAt = q, Flags = flags | ((flags & 0x4) != 0 ? 0x1000000u : 0), SampleCount = count, FirstSampleFlags = firstFlags, DataStart = dataStart };
                long fit = run.EntrySize == 0 ? count : (boxEnd - q) / run.EntrySize;
                if (count > fit)
                {
                    defects.Add(DefectKind.Truncated, DefectSeverity.Error, moofOffset + pos, $"A track run declares {count} samples but holds {fit}.", "isobmff.trun-count");
                    run.SampleCount = (uint)Math.Max(0, fit);
                }

                limits.CheckSampleTable(run.SampleCount, moofOffset + pos);
                traf.Runs.Add(run);
                runEnd = dataStart + RunDataBytes(run, traf, buf);
            }

            pos = boxEnd;
        }

        if (!haveHeader)
        {
            defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, moofOffset + start, "A track fragment has no header box.", "isobmff.traf-no-tfhd");
            return;
        }

        traf.DataOffset = runEnd; // reused as the end of this fragment's data until iteration starts
        _trafCount++;
    }

    private static long RunDataBytes(TrunRun run, TrafInfo traf, ReadOnlySpan<byte> buf)
    {
        if ((run.Flags & 0x200) == 0)
        {
            return (long)run.SampleCount * traf.DefaultSize;
        }

        long total = 0;
        int size = run.EntrySize;
        int sizeAt = ((run.Flags & 0x100) != 0 ? 4 : 0);
        for (uint i = 0; i < run.SampleCount; i++)
        {
            total += Be.U32(buf, run.EntriesAt + (int)(i * size) + sizeAt);
        }

        return total;
    }

    /// <summary>Resets iteration to the first sample of every track fragment.</summary>
    public void Rewind(TrackState[] states)
    {
        for (int i = 0; i < _trafCount; i++)
        {
            TrafInfo t = _trafs[i];
            t.Run = 0;
            t.InRun = 0;
            t.Pos = 0;
            t.Active = t.Runs.Count > 0;
            if (t.Active)
            {
                t.DataOffset = t.Runs[0].DataStart;
                t.Pos = t.Runs[0].EntriesAt;
            }

            t.Dts = t.HasBaseDts ? t.BaseDts : states[t.TrackIndex].NextFragmentDts;
            Skip(t);
        }
    }

    // Moves past empty runs so Active means "has a sample".
    private static void Skip(TrafInfo t)
    {
        while (t.Active && t.InRun >= t.Runs[t.Run].SampleCount)
        {
            t.Run++;
            t.InRun = 0;
            if (t.Run >= t.Runs.Count)
            {
                t.Active = false;
                return;
            }

            t.DataOffset = t.Runs[t.Run].DataStart;
            t.Pos = t.Runs[t.Run].EntriesAt;
        }
    }

    /// <summary>Peeks the next sample (lowest file offset across enabled track fragments) without consuming it.</summary>
    public bool Peek(TrackState[] states, out FragmentSample sample, out int trafIndex)
    {
        sample = default;
        trafIndex = -1;
        long best = long.MaxValue;
        for (int i = 0; i < _trafCount; i++)
        {
            TrafInfo t = _trafs[i];
            if (!t.Active || !states[t.TrackIndex].Enabled || t.DataOffset >= best)
            {
                continue;
            }

            best = t.DataOffset;
            trafIndex = i;
        }

        if (trafIndex < 0)
        {
            return false;
        }

        sample = Read(_trafs[trafIndex]);
        return true;
    }

    /// <summary>Consumes the sample last peeked from track fragment <paramref name="trafIndex"/>.</summary>
    public void Advance(int trafIndex, in FragmentSample sample, TrackState[] states)
    {
        TrafInfo t = _trafs[trafIndex];
        t.DataOffset += sample.Size;
        t.Dts += sample.Duration;
        t.Pos += t.Runs[t.Run].EntrySize;
        t.InRun++;
        states[t.TrackIndex].NextFragmentDts = t.Dts;
        Skip(t);
    }

    /// <summary>Consumes the samples that start before <paramref name="offset"/> (used to land a seek inside a fragment).</summary>
    public void SkipBefore(TrackState[] states, long offset)
    {
        while (Peek(states, out FragmentSample s, out int traf) && s.Offset < offset)
        {
            Advance(traf, s, states);
        }
    }

    /// <summary>Consumes samples of disabled tracks so their decode times still advance for the next fragment.</summary>
    public void DrainDisabled(TrackState[] states)
    {
        for (int i = 0; i < _trafCount; i++)
        {
            TrafInfo t = _trafs[i];
            while (t.Active && !states[t.TrackIndex].Enabled)
            {
                FragmentSample s = Read(t);
                Advance(i, s, states);
            }
        }
    }

    private FragmentSample Read(TrafInfo t)
    {
        ReadOnlySpan<byte> buf = _buffer.AsSpan(0, _length);
        TrunRun run = t.Runs[t.Run];
        int p = t.Pos;
        uint duration = t.DefaultDuration, size = t.DefaultSize, flags = t.DefaultFlags;
        int cto = 0;
        if ((run.Flags & 0x100) != 0)
        {
            duration = Be.U32(buf, p);
            p += 4;
        }

        if ((run.Flags & 0x200) != 0)
        {
            size = Be.U32(buf, p);
            p += 4;
        }

        if ((run.Flags & 0x400) != 0)
        {
            flags = Be.U32(buf, p);
            p += 4;
        }
        else if (t.InRun == 0 && (run.Flags & 0x1000000) != 0)
        {
            flags = run.FirstSampleFlags;
        }

        if ((run.Flags & 0x800) != 0)
        {
            // §8.8.8: version 0 offsets are unsigned and version 1 signed; real files hold small values, so both are read as signed.
            cto = Be.I32(buf, p);
        }

        return new FragmentSample
        {
            TrackIndex = t.TrackIndex,
            Offset = t.DataOffset,
            Size = (int)Math.Min(size, int.MaxValue),
            Dts = t.Dts,
            CompositionOffset = cto,
            Duration = duration,
            Flags = flags,
            DescriptionIndex = t.DescriptionIndex,
        };
    }
}
