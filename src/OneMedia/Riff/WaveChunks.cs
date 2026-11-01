using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Riff;

/// <summary>A cue point of a WAVE file: a position in the sample data with an optional label and note from the associated-data list.</summary>
/// <param name="Id">The cue point's identifier, which labels refer to.</param>
/// <param name="Position">The position as a play-order value; for files without a playlist it equals the sample offset.</param>
/// <param name="DataChunkId">The chunk the offsets count in (<c>data</c>, or <c>slnt</c> in a wave list).</param>
/// <param name="ChunkStart">Byte offset of the chunk inside a wave list (0 for a plain file).</param>
/// <param name="BlockStart">Byte offset of the block that holds the position, from the start of the chunk's data.</param>
/// <param name="SampleOffset">Frame offset of the position inside that block; with a plain file, the frame position in the stream.</param>
/// <param name="Label">The text of a <c>labl</c> sub-chunk for this id, or <c>null</c>.</param>
/// <param name="Note">The text of a <c>note</c> sub-chunk for this id, or <c>null</c>.</param>
public sealed record WaveCuePoint(uint Id, uint Position, string DataChunkId, uint ChunkStart, uint BlockStart, uint SampleOffset, string? Label, string? Note);

/// <summary>One loop of a sampler chunk.</summary>
/// <param name="CueId">The cue point that names the loop.</param>
/// <param name="Type">0 forward, 1 ping-pong (alternating), 2 backward; other values are manufacturer specific.</param>
/// <param name="Start">First frame of the loop.</param>
/// <param name="End">Last frame of the loop.</param>
/// <param name="Fraction">Fraction of a frame to add to the end, in units of 1/2^32.</param>
/// <param name="PlayCount">Number of repeats; 0 means forever.</param>
public sealed record SamplerLoop(uint CueId, uint Type, uint Start, uint End, uint Fraction, uint PlayCount);

/// <summary>The <c>smpl</c> chunk of a WAVE file (Microsoft's RIFF extensions): how a sampler plays the recording.</summary>
/// <param name="Manufacturer">MMA manufacturer code, 0 for none.</param>
/// <param name="Product">The manufacturer's product code.</param>
/// <param name="SamplePeriodNanoseconds">Duration of one sample in nanoseconds.</param>
/// <param name="MidiUnityNote">The MIDI note at which the recording plays unchanged.</param>
/// <param name="MidiPitchFraction">Fraction of a semitone above the unity note, in units of 1/2^32 semitone.</param>
/// <param name="SmpteFormat">SMPTE frame rate code: 0, 24, 25, 29 or 30.</param>
/// <param name="SmpteOffset">The SMPTE offset packed as hour, minute, second and frame bytes.</param>
/// <param name="Loops">The loops.</param>
/// <param name="SamplerDataBytes">Length of the sampler-specific data that follows the loops.</param>
public sealed record WaveSampler(uint Manufacturer, uint Product, uint SamplePeriodNanoseconds, uint MidiUnityNote, uint MidiPitchFraction, uint SmpteFormat, uint SmpteOffset, IReadOnlyList<SamplerLoop> Loops, uint SamplerDataBytes);

/// <summary>The <c>inst</c> chunk of a WAVE file: the MIDI mapping of a sampled instrument.</summary>
/// <param name="UnshiftedNote">MIDI note at which the sample plays unchanged (0 to 127).</param>
/// <param name="FineTuneCents">Detune in cents (-50 to 50).</param>
/// <param name="GainDecibels">Gain in dB.</param>
/// <param name="LowNote">Lowest note the sample is played for.</param>
/// <param name="HighNote">Highest note.</param>
/// <param name="LowVelocity">Lowest velocity.</param>
/// <param name="HighVelocity">Highest velocity.</param>
public sealed record WaveInstrument(int UnshiftedNote, int FineTuneCents, int GainDecibels, int LowNote, int HighNote, int LowVelocity, int HighVelocity);

/// <summary>Reads the small sampler-related chunks of WAVE files.</summary>
public static class WaveChunkReader
{
    /// <summary>
    /// Reads a <c>cue </c> chunk and attaches the label and note of each point from the <c>LIST adtl</c> chunks given.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="cue">The <c>cue </c> chunk.</param>
    /// <param name="adtlLists">The <c>LIST</c> chunks of type <c>adtl</c>; may be empty.</param>
    /// <param name="limits">Ceilings for allocations and table sizes.</param>
    /// <param name="defects">Receives defects.</param>
    /// <returns>The cue points in file order.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="MediaLimitExceededException">The chunk declares more points than the table limit.</exception>
    public static IReadOnlyList<WaveCuePoint> ReadCues(IMediaSource source, ChunkNode cue, IEnumerable<ChunkNode> adtlLists, MediaLimits limits, DefectLog defects)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cue);
        ArgumentNullException.ThrowIfNull(adtlLists);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        Dictionary<uint, string> labels = [], notes = [];
        foreach (ChunkNode list in adtlLists)
        {
            foreach (ChunkNode c in list.Chunks)
            {
                if (c.Id is not ("labl" or "note") || c.PayloadSize < 4)
                {
                    continue;
                }

                limits.CheckTag(c.PayloadSize, c.Offset);
                byte[] b = new byte[c.PayloadSize];
                if (source.Read(c.Payload.Offset, b) < b.Length)
                {
                    continue;
                }

                uint id = BinaryPrimitives.ReadUInt32LittleEndian(b);
                string text = Tags.Riff.RiffTagReader.Decode(b.AsSpan(4), c.Payload.Offset + 4, defects, nulTerminated: true);
                (c.Id == "labl" ? labels : notes)[id] = text;
            }
        }

        if (cue.PayloadSize < 4)
        {
            return [];
        }

        Span<byte> countBytes = stackalloc byte[4]; // the cue-point count
        if (source.Read(cue.Payload.Offset, countBytes) < 4)
        {
            return [];
        }

        // Each cue point is 24 bytes; never trust a count beyond the chunk.
        long count = BinaryPrimitives.ReadUInt32LittleEndian(countBytes);
        long fit = (cue.PayloadSize - 4) / 24;
        if (count > fit)
        {
            defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, cue.Offset, $"The cue chunk declares {count} points but holds room for {fit}.", "riff.cue-count");
            count = fit;
        }

        limits.CheckSampleTable(count, cue.Offset);
        List<WaveCuePoint> points = [];
        byte[] entry = new byte[24];
        for (long i = 0; i < count; i++)
        {
            if (source.Read(cue.Payload.Offset + 4 + (i * 24), entry) < 24)
            {
                break;
            }

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            points.Add(new WaveCuePoint(id, BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(4)), ChunkNode.FourCc(BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(8))),
                BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(12)), BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(16)), BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(20)),
                labels.GetValueOrDefault(id), notes.GetValueOrDefault(id)));
        }

        return points;
    }

    /// <summary>Reads a <c>smpl</c> chunk.</summary>
    /// <param name="source">The source.</param>
    /// <param name="chunk">The chunk.</param>
    /// <param name="limits">Ceilings.</param>
    /// <param name="defects">Receives defects.</param>
    /// <returns>The sampler description, or <c>null</c> when the chunk is shorter than its fixed part.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static WaveSampler? ReadSampler(IMediaSource source, ChunkNode chunk, MediaLimits limits, DefectLog defects)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(defects);
        if (chunk.PayloadSize < 36)
        {
            defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, chunk.Offset, "The smpl chunk is shorter than its 36 fixed bytes.", "riff.smpl-short");
            return null;
        }

        limits.CheckTag(chunk.PayloadSize, chunk.Offset);
        byte[] b = new byte[chunk.PayloadSize];
        if (source.Read(chunk.Payload.Offset, b) < b.Length)
        {
            return null;
        }

        uint loops = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(28));
        long fit = (b.Length - 36L) / 24;
        if (loops > fit)
        {
            defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, chunk.Offset, $"The smpl chunk declares {loops} loops but holds room for {fit}.", "riff.smpl-loops");
            loops = (uint)fit;
        }

        List<SamplerLoop> list = [];
        for (int i = 0; i < loops; i++)
        {
            ReadOnlySpan<byte> l = b.AsSpan(36 + (i * 24), 24);
            list.Add(new SamplerLoop(BinaryPrimitives.ReadUInt32LittleEndian(l), BinaryPrimitives.ReadUInt32LittleEndian(l[4..]), BinaryPrimitives.ReadUInt32LittleEndian(l[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(l[12..]), BinaryPrimitives.ReadUInt32LittleEndian(l[16..]), BinaryPrimitives.ReadUInt32LittleEndian(l[20..])));
        }

        return new WaveSampler(BinaryPrimitives.ReadUInt32LittleEndian(b), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12)), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16)), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(20)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(24)), list, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(32)));
    }

    /// <summary>Reads an <c>inst</c> chunk.</summary>
    /// <param name="source">The source.</param>
    /// <param name="chunk">The chunk.</param>
    /// <returns>The instrument, or <c>null</c> when the chunk is shorter than its 7 bytes.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static WaveInstrument? ReadInstrument(IMediaSource source, ChunkNode chunk)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(chunk);
        Span<byte> b = stackalloc byte[7]; // the seven one-byte fields of the instrument chunk
        if (chunk.PayloadSize < 7 || source.Read(chunk.Payload.Offset, b) < 7)
        {
            return null;
        }

        return new WaveInstrument(b[0], (sbyte)b[1], (sbyte)b[2], b[3], b[4], b[5], b[6]);
    }
}
