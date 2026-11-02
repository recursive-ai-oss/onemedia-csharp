namespace RecursiveAI.OneMedia.IsoBmff;

/// <summary>One sample of a fragment that is being collected.</summary>
internal struct PendingSample
{
    public long Dts;
    public long DeclaredDuration;
    public int Size;
    public uint Flags;
    public int CompositionOffset;
}

/// <summary>
/// The state of one track inside an <see cref="IsoMuxer"/>: its sample entry, the sample tables that grow with the file, the timing
/// state that turns packet times into table entries, and (fragmented layout) the samples of the fragment being collected.
/// </summary>
internal sealed class MuxTrack : IDisposable
{
    public MuxTrack(MediaTrack declared, IsoTrackOptions? options, SampleEntryPlan plan, uint trackId)
    {
        Declared = declared;
        Options = options;
        Plan = plan;
        TrackId = trackId;
        Timescale = declared.Timescale;
    }

    public MediaTrack Declared { get; }

    public IsoTrackOptions? Options { get; }

    public SampleEntryPlan Plan { get; }

    public uint TrackId { get; }

    public long Timescale { get; }

    // Sample tables (progressive layouts). Sizes are 4 bytes per sample; the timing and composition tables are run-length coded, so
    // a constant frame rate costs eight bytes in total and a variable one at most eight bytes per sample; keyframes cost 4 bytes each;
    // each chunk costs 8 bytes plus a 12-byte sample-to-chunk entry whenever the samples per chunk change.
    public Paged<uint> Sizes { get; } = new();

    public Paged<uint> Durations { get; } = new();

    public Paged<uint> Compositions { get; } = new();

    public Paged<uint> SyncSamples { get; } = new();

    public Paged<uint> SampleToChunk { get; } = new();

    public Paged<ulong> ChunkOffsets { get; } = new();

    /// <summary>One byte per sample for the independent and disposable samples box; created when the first disposable sample arrives.</summary>
    public Paged<byte>? Dependencies { get; private set; }

    /// <summary>
    /// ISO/IEC 14496-12 §8.6.4 sample dependency byte: is_leading(2) sample_depends_on(2) sample_is_depended_on(2) sample_has_redundancy(2).
    /// A sync sample depends on no other (2), any other sample on some (1); a disposable one is depended on by none (2).
    /// </summary>
    public static byte DependencyByte(bool key, bool disposable) => (byte)((key ? 0x20 : 0x10) | (disposable ? 0x08 : 0));

    /// <summary>Creates <see cref="Dependencies"/> and fills in the samples written so far, none of which was disposable.</summary>
    public void StartDependencies()
    {
        Dependencies = new Paged<byte>();
        long next = 0;
        for (long s = 0; s < Samples; s++)
        {
            bool key = next < SyncSamples.Count && SyncSamples[next] == s + 1;
            if (key)
            {
                next++;
            }

            Dependencies.Add(DependencyByte(key, false));
        }
    }

    public long Samples { get; set; }

    public long PayloadBytes { get; set; }

    public long KeyCount { get; set; }

    public uint MinSize { get; set; } = uint.MaxValue;

    public uint MaxSize { get; set; }

    // Timing.
    public bool Started { get; set; }

    /// <summary>The decode time of the first packet, taken off every time so the media timeline starts at zero (ISO tables cannot start later).</summary>
    public long Shift { get; set; }

    public long PreviousRawDts { get; set; }

    public long PreviousShiftedDts { get; set; }

    public long PreviousDeclared { get; set; } = -1;

    public long LastDelta { get; set; } = -1;

    public long RunDelta { get; set; }

    public long RunCount { get; set; }

    public long DurationSum { get; set; }

    /// <summary>The composition offset (presentation minus decode time) of the sample last added.</summary>
    public int PreviousComposition { get; set; }

    /// <summary>
    /// The end of the latest-ending sample on the presentation timeline (its presentation time plus its duration), in the shifted media timeline.
    /// With composition reordering this runs past <see cref="DurationSum"/> by the reorder delay, and an edit that ends at <see cref="DurationSum"/>
    /// would cut off the last frames shown.
    /// </summary>
    public long PresentationEnd { get; set; }

    /// <summary>Notes that the sample whose decode time is <paramref name="shiftedDts"/> and composition offset <paramref name="composition"/> lasts <paramref name="duration"/>.</summary>
    public void NotePresentation(long shiftedDts, int composition, long duration) => PresentationEnd = Math.Max(PresentationEnd, shiftedDts + composition + duration);

    public int CompositionValue { get; set; }

    public long CompositionRun { get; set; }

    public bool AnyNegativeComposition { get; set; }

    public bool AnyComposition { get; set; }

    // Normalisations, reported at the end.
    public long RoundedTimes { get; set; }

    public long ReplacedDurations { get; set; }

    public long ZeroDurations { get; set; }

    public long IgnoredDiscards { get; set; }

    // Chunks.
    public bool ChunkOpen { get; set; }

    public uint ChunkSamples { get; set; }

    public uint LastSamplesPerChunk { get; set; }

    // First packet bytes, for codecs whose configuration comes from the stream.
    public byte[]? Head { get; set; }

    public int HeadLength { get; set; }

    // Fragmented layout.
    public PendingSample[] Fragment { get; set; } = [];

    public int FragmentCount { get; set; }

    public byte[] FragmentData { get; set; } = [];

    public int FragmentLength { get; set; }

    public bool ForceLargeOffsets { get; set; }

    /// <summary>Adds the duration of a finished sample to the run-length coded decoding-time table.</summary>
    public void CommitDuration(long delta)
    {
        DurationSum += delta;
        LastDelta = delta;
        if (delta == 0)
        {
            ZeroDurations++;
        }

        if (RunCount > 0 && delta == RunDelta)
        {
            RunCount++;
            return;
        }

        FlushDurationRun();
        RunDelta = delta;
        RunCount = 1;
    }

    public void FlushDurationRun()
    {
        if (RunCount > 0)
        {
            Durations.Add((uint)RunCount);
            Durations.Add((uint)RunDelta);
            RunCount = 0;
        }
    }

    public void AddComposition(int offset)
    {
        if (offset < 0)
        {
            AnyNegativeComposition = true;
        }

        if (offset != 0)
        {
            AnyComposition = true;
        }

        if (CompositionRun > 0 && offset == CompositionValue)
        {
            CompositionRun++;
            return;
        }

        FlushCompositionRun();
        CompositionValue = offset;
        CompositionRun = 1;
    }

    public void FlushCompositionRun()
    {
        if (CompositionRun > 0)
        {
            Compositions.Add((uint)CompositionRun);
            Compositions.Add(unchecked((uint)CompositionValue));
            CompositionRun = 0;
        }
    }

    /// <summary>Ends the open chunk, recording its sample count in the sample-to-chunk table when it differs from the chunk before.</summary>
    public void CloseChunk()
    {
        if (!ChunkOpen)
        {
            return;
        }

        if (ChunkOffsets.Count == 1 || ChunkSamples != LastSamplesPerChunk)
        {
            SampleToChunk.Add((uint)ChunkOffsets.Count);
            SampleToChunk.Add(ChunkSamples);
            SampleToChunk.Add(1);
            LastSamplesPerChunk = ChunkSamples;
        }

        ChunkOpen = false;
        ChunkSamples = 0;
    }

    public void Dispose()
    {
        Sizes.Dispose();
        Durations.Dispose();
        Compositions.Dispose();
        SyncSamples.Dispose();
        SampleToChunk.Dispose();
        ChunkOffsets.Dispose();
        Dependencies?.Dispose();
        if (FragmentData.Length > 0)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(FragmentData);
            FragmentData = [];
        }

        if (Fragment.Length > 0)
        {
            System.Buffers.ArrayPool<PendingSample>.Shared.Return(Fragment);
            Fragment = [];
        }
    }
}
