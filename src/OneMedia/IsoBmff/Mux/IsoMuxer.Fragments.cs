using System.Buffers;
using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.IsoBmff;

public sealed partial class IsoMuxer
{
    private readonly record struct SegmentReference(uint Size, uint Duration, bool StartsAtRandomAccess);

    private readonly record struct RandomAccessEntry(long Time, long FragmentOffset, int TrafNumber);

    // Fragments are written to the destination as they end, except when a segment index is asked for: it goes in front of the
    // fragments and needs all their sizes, so they wait in the spool.
    private readonly BoxStack _moof = new(2048);
    private readonly List<SegmentReference> _references = [];
    private List<RandomAccessEntry>[] _randomAccess = [];
    private int _referenceTrack = -1;
    private long _referenceTargetTicks;
    private long _referenceFirstDts;
    private bool _referenceHasSamples;
    private long _earliestPresentation = long.MaxValue;
    private bool _fragmentsStarted;
    private bool _initWritten;
    private uint _sequence;
    private long _fragmentBytes;
    private int _fragmentSamples;
    private long _fragmentArea;
    private long _fragmentsBase;
    private bool _spoolFragments;
    private int _fragmentsCut;

    private void EnsureFragmentsStarted()
    {
        if (_fragmentsStarted)
        {
            return;
        }

        _fragmentsStarted = true;
        _ftyp ??= BuildFtyp();
        _referenceTrack = 0;
        for (int i = 0; i < _tracks.Count; i++)
        {
            if (_tracks[i].Declared.Kind == TrackKind.Video)
            {
                _referenceTrack = i;
                break;
            }
        }

        MuxTrack reference = _tracks[_referenceTrack];
        _referenceTargetTicks = _options.FragmentDuration.TryRescale(reference.Timescale, MediaRounding.NearestAwayFromZero, out MediaTime target) ? Math.Max(target.Ticks, 1) : 1;
        _randomAccess = new List<RandomAccessEntry>[_tracks.Count];
        for (int i = 0; i < _randomAccess.Length; i++)
        {
            _randomAccess[i] = [];
        }

        _spoolFragments = _options.WriteSegmentIndex;
        if (_spoolFragments)
        {
            _spool = new SpoolBuffer(_options.SpoolMemoryBudget, _options.SpoolFactory ?? CreateTemporaryFile);
        }
    }

    private void AddFragmentPacket(in PacketInfo info, MuxTrack t, ReadOnlySpan<byte> data, long shifted, long declaredDuration, int composition, CancellationToken ct)
    {
        EnsureFragmentsStarted();
        if (_fragmentSamples > 0)
        {
            bool cut = info.TrackIndex == _referenceTrack && info.IsKeyframe && _referenceHasSamples && shifted - _referenceFirstDts >= _referenceTargetTicks;
            if (cut || _fragmentBytes + data.Length > _options.MaxFragmentBytes)
            {
                FlushFragment(ct);
            }
        }

        if (t.FragmentCount == t.Fragment.Length)
        {
            PendingSample[] bigger = ArrayPool<PendingSample>.Shared.Rent(Math.Max(256, t.Fragment.Length * 2));
            Array.Copy(t.Fragment, bigger, t.FragmentCount);
            if (t.Fragment.Length > 0)
            {
                ArrayPool<PendingSample>.Shared.Return(t.Fragment);
            }

            t.Fragment = bigger;
        }

        if (t.FragmentLength + data.Length > t.FragmentData.Length)
        {
            byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(1 << 16, Math.Max(t.FragmentData.Length * 2, t.FragmentLength + data.Length)));
            t.FragmentData.AsSpan(0, t.FragmentLength).CopyTo(bigger);
            if (t.FragmentData.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(t.FragmentData);
            }

            t.FragmentData = bigger;
        }

        // §8.8.8.1 sample flags: is_leading(2) depends_on(2) is_depended_on(2) has_redundancy(2) padding(3) is_non_sync(1) degradation(16).
        // A sync sample depends on no other (depends_on 2); a non-sync sample does (1) and sets is_non_sync.
        uint flags = info.IsKeyframe ? 0x02000000u : 0x01010000u;
        if ((info.Flags & PacketAttributes.Disposable) != 0)
        {
            flags |= 0x00800000; // is_depended_on = 2: nothing depends on it
        }

        t.Fragment[t.FragmentCount++] = new PendingSample { Dts = shifted, DeclaredDuration = declaredDuration, Size = data.Length, Flags = flags, CompositionOffset = composition };
        data.CopyTo(t.FragmentData.AsSpan(t.FragmentLength));
        t.FragmentLength += data.Length;
        _fragmentBytes += data.Length;
        _fragmentSamples++;
        t.Samples++;
        t.PayloadBytes += data.Length;
        if (info.TrackIndex == _referenceTrack && !_referenceHasSamples)
        {
            _referenceHasSamples = true;
            _referenceFirstDts = shifted;
        }
    }

    private void Emit(ReadOnlySpan<byte> bytes)
    {
        if (_spoolFragments)
        {
            _spool!.Write(bytes);
        }
        else
        {
            _out.Write(bytes);
        }

        _fragmentArea += bytes.Length;
    }

    private void WriteInit(CancellationToken ct)
    {
        foreach (MuxTrack t in _tracks)
        {
            CompleteEntry(t);
        }

        Summarize(fragmented: true);
        _out.Write(_ftyp!);
        BuildMoov(0, fragmented: true).WriteTo(_out, ct);
        _fragmentsBase = _out.Position;
        _initWritten = true;
    }

    private void FlushFragment(CancellationToken ct)
    {
        if (_fragmentSamples == 0)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        if (!_spoolFragments && !_initWritten)
        {
            WriteInit(ct);
        }

        BoxStack b = _moof;
        b.Clear();
        long payload = 0;
        Span<int> dataOffsetAt = _tracks.Count <= 16 ? stackalloc int[16] : new int[_tracks.Count]; // fixed small buffer
        b.Begin("moof");
        b.BeginFull("mfhd", 0, 0);
        b.U32(++_sequence);
        b.End();
        long referenceDuration = 0;
        long fragmentEarliest = long.MaxValue;
        bool referenceStartsWithKey = false;
        for (int i = 0; i < _tracks.Count; i++)
        {
            MuxTrack t = _tracks[i];
            if (t.FragmentCount == 0)
            {
                continue;
            }

            PendingSample[] samples = t.Fragment;
            int count = t.FragmentCount;
            // The sample's duration is the gap to the next decode time; the last one's is its declared duration, else the gap before it.
            long total = 0;
            long previousGap = 0;
            bool constant = true;
            long firstDuration = -1;
            bool anyComposition = false;
            bool anyNegative = false;
            for (int s = 0; s < count; s++)
            {
                long duration = s + 1 < count ? samples[s + 1].Dts - samples[s].Dts : samples[s].DeclaredDuration >= 0 ? samples[s].DeclaredDuration : count > 1 ? previousGap : 0;
                previousGap = duration;
                samples[s].DeclaredDuration = duration;
                total += duration;
                if (firstDuration < 0)
                {
                    firstDuration = duration;
                }
                else if (duration != firstDuration)
                {
                    constant = false;
                }

                anyComposition |= samples[s].CompositionOffset != 0;
                anyNegative |= samples[s].CompositionOffset < 0;
            }

            t.DurationSum += total;
            b.Begin("traf");
            // tfhd flags: 0x020000 default-base-is-moof (data offsets count from the start of the moof), 0x08 default-sample-duration.
            b.BeginFull("tfhd", 0, constant ? 0x020008u : 0x020000u);
            b.U32(t.TrackId);
            if (constant)
            {
                b.U32((uint)firstDuration);
            }

            b.End();
            b.BeginFull("tfdt", 1, 0);
            b.U64((ulong)samples[0].Dts);
            b.End();
            // trun flags: 0x1 data-offset, 0x100 duration, 0x200 size, 0x400 flags, 0x800 composition offset.
            b.BeginFull("trun", anyNegative ? 1 : 0, 0x000601u | (constant ? 0u : 0x100u) | (anyComposition ? 0x800u : 0u));
            b.U32((uint)count);
            dataOffsetAt[i] = b.Mark;
            b.U32(0);
            for (int s = 0; s < count; s++)
            {
                if (!constant)
                {
                    b.U32((uint)samples[s].DeclaredDuration);
                }

                b.U32((uint)samples[s].Size);
                b.U32(samples[s].Flags);
                if (anyComposition)
                {
                    b.I32(samples[s].CompositionOffset);
                }
            }

            b.End();
            if (t.Declared.Codec == CodecId.Opus)
            {
                b.BeginFull("sbgp", 0, 0);
                b.Code("roll");
                b.U32(1);
                b.U32((uint)count);
                b.U32(1);
                b.End();
            }

            b.End();
            payload += t.FragmentLength;
            if (i == _referenceTrack)
            {
                referenceDuration = total;
                referenceStartsWithKey = (samples[0].Flags & 0x00010000) == 0;
                for (int s = 0; s < count; s++)
                {
                    fragmentEarliest = Math.Min(fragmentEarliest, samples[s].Dts + samples[s].CompositionOffset);
                }
            }
        }

        b.End();
        int moofSize = (int)b.Length;
        long running = moofSize + 8;
        for (int i = 0; i < _tracks.Count; i++)
        {
            if (_tracks[i].FragmentCount > 0)
            {
                b.PatchU32(dataOffsetAt[i], (uint)running);
                running += _tracks[i].FragmentLength;
            }
        }

        long moofOffset = _fragmentArea;
        for (int i = 0; i < _tracks.Count; i++)
        {
            MuxTrack t = _tracks[i];
            if (_options.WriteRandomAccess && t.FragmentCount > 0 && (t.Fragment[0].Flags & 0x00010000) == 0)
            {
                _randomAccess[i].Add(new RandomAccessEntry(t.Fragment[0].Dts + t.Fragment[0].CompositionOffset, moofOffset, TrafNumber(i)));
            }
        }

        Emit(b.AsSpan());
        Span<byte> mdat = stackalloc byte[8]; // fixed small buffer
        BinaryPrimitives.WriteUInt32BigEndian(mdat, (uint)(payload + 8));
        BinaryPrimitives.WriteUInt32BigEndian(mdat[4..], BoxTypes.Mdat);
        Emit(mdat);
        for (int i = 0; i < _tracks.Count; i++)
        {
            MuxTrack t = _tracks[i];
            if (t.FragmentCount > 0)
            {
                Emit(t.FragmentData.AsSpan(0, t.FragmentLength));
                t.FragmentCount = 0;
                t.FragmentLength = 0;
            }
        }

        if (fragmentEarliest != long.MaxValue)
        {
            _earliestPresentation = Math.Min(_earliestPresentation, _fragmentsCut == 0 ? fragmentEarliest : long.MaxValue);
            _fragmentsCut++;
            // Per-fragment state is kept only for the indexes that need it, so a plain fragmented file holds none.
            if (_spoolFragments)
            {
                _references.Add(new SegmentReference((uint)(moofSize + 8 + payload), (uint)Math.Min(referenceDuration, uint.MaxValue), referenceStartsWithKey));
            }
        }

        _fragmentBytes = 0;
        _fragmentSamples = 0;
        _referenceHasSamples = false;
    }

    // The position of a track's traf among the trafs of its moof (1-based): only tracks with samples in the fragment have one.
    private int TrafNumber(int trackIndex)
    {
        int n = 0;
        for (int i = 0; i <= trackIndex; i++)
        {
            if (_tracks[i].FragmentCount > 0)
            {
                n++;
            }
        }

        return n;
    }

    private void FinishFragmented(CancellationToken ct)
    {
        EnsureFragmentsStarted();
        FlushFragment(ct);
        foreach (MuxTrack t in _tracks)
        {
            CompleteEntry(t);
        }

        long baseOffset;
        if (_spoolFragments)
        {
            Summarize(fragmented: true);
            if (_references.Count > ushort.MaxValue)
            {
                throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"A segment index holds at most 65535 references; this file has {_references.Count} fragments. Use longer fragments or no segment index.");
            }

            BoxStack moov = BuildMoov(0, fragmented: true);
            BoxStack sidx = BuildSegmentIndex();
            _out.Write(_ftyp!);
            moov.WriteTo(_out, ct);
            sidx.WriteTo(_out, ct);
            baseOffset = _ftyp!.Length + moov.Length + sidx.Length;
            _spool!.CopyTo(_out, ct);
        }
        else
        {
            if (!_initWritten)
            {
                WriteInit(ct);
            }

            baseOffset = _fragmentsBase;
        }

        if (_options.WriteRandomAccess)
        {
            WriteRandomAccessBox(baseOffset, ct);
        }
    }

    // ISO/IEC 14496-12 §8.16.3, version 1: one reference per fragment, to the reference track's timeline; every reference is a movie fragment (type 0).
    private BoxStack BuildSegmentIndex()
    {
        MuxTrack reference = _tracks[_referenceTrack];
        BoxStack b = new(64 + (_references.Count * 12));
        b.BeginFull("sidx", 1, 0);
        b.U32(reference.TrackId);
        b.U32((uint)reference.Timescale);
        b.U64(_earliestPresentation == long.MaxValue ? 0 : (ulong)Math.Max(0, _earliestPresentation));
        b.U64(0); // first_offset: the first fragment follows the index
        b.U16(0);
        b.U16(_references.Count);
        foreach (SegmentReference r in _references)
        {
            b.U32(r.Size);
            b.U32(r.Duration);
            // starts_with_SAP(1) SAP_type(3) SAP_delta_time(28): a fragment that starts at a sync sample is SAP type 1 at delta 0.
            b.U32(r.StartsAtRandomAccess ? 0x90000000u : 0u);
        }

        b.End();
        return b;
    }

    // §8.8.9 and §8.8.11: one track fragment random access box per track that has entries, closed by the offset box that lets a reader find the start from the end of the file.
    private void WriteRandomAccessBox(long baseOffset, CancellationToken ct)
    {
        BoxStack b = new(256);
        b.Begin("mfra");
        for (int i = 0; i < _tracks.Count; i++)
        {
            List<RandomAccessEntry> entries = _randomAccess[i];
            if (entries.Count == 0)
            {
                continue;
            }

            b.BeginFull("tfra", 1, 0);
            b.U32(_tracks[i].TrackId);
            b.U32(0); // one byte each for traf_number, trun_number and sample_number
            b.U32((uint)entries.Count);
            foreach (RandomAccessEntry e in entries)
            {
                b.U64((ulong)Math.Max(0, e.Time));
                b.U64((ulong)(e.FragmentOffset + baseOffset));
                b.U8(e.TrafNumber);
                b.U8(1);
                b.U8(1);
            }

            b.End();
        }

        b.BeginFull("mfro", 0, 0);
        int sizeAt = b.Mark;
        b.U32(0);
        b.End();
        b.End();
        b.PatchU32(sizeAt, (uint)b.Length);
        b.WriteTo(_out, ct);
    }
}
