namespace RecursiveAI.OneMedia.Repair;

/// <summary>The overall outcome of a repair.</summary>
public enum RepairStatus
{
    /// <summary>The input is healthy as far as the repairer can tell. The output is a byte-identical copy of the input.</summary>
    NothingToRepair = 1,

    /// <summary>Everything that was damaged was rebuilt or dropped; the output is a complete file.</summary>
    Repaired,

    /// <summary>Some structures or tracks could not be rebuilt. The output holds what could be; the report lists the rest.</summary>
    PartiallyRepaired,

    /// <summary>Nothing usable could be recovered. No output file is left behind.</summary>
    Unrepairable,
}

/// <summary>How far a repaired structure or track can be trusted to match what the recorder wrote.</summary>
public enum RepairConfidence
{
    /// <summary>Not applicable: nothing was recovered.</summary>
    None = 0,

    /// <summary>Guessed: the result is plausible but nothing in the file confirms it.</summary>
    Low,

    /// <summary>Derived from the file's own data, with at least one check that could not be made (for example a count that disagreed).</summary>
    Medium,

    /// <summary>Derived from the file's own data and every available cross-check agreed.</summary>
    High,
}

/// <summary>What the repair did to one structure.</summary>
public enum RepairAction
{
    /// <summary>The structure was missing or unusable and was built again from the media data.</summary>
    Rebuilt = 1,

    /// <summary>A value or field was corrected in place (a duration, a count, an offset).</summary>
    Fixed,

    /// <summary>Bytes were left out of the output: a cut-off block, a broken packet, a stray fragment.</summary>
    Dropped,

    /// <summary>The structure was checked and carried over unchanged.</summary>
    Kept,

    /// <summary>A value was filled in without evidence in the file (for example an unknown size resolved to the end of the file).</summary>
    Guessed,
}

/// <summary>One thing the repair did.</summary>
/// <param name="Action">What was done.</param>
/// <param name="Structure">The structure concerned, in the format's own words: <c>Cues</c>, <c>SeekHead</c>, <c>idx1</c>, <c>stco</c>, <c>mfra</c>, <c>PES</c>.</param>
/// <param name="Track">Track index (container order), or -1 when it concerns the file.</param>
/// <param name="Range">Input bytes concerned (for <see cref="RepairAction.Dropped"/>, the bytes left out); a zero-length range at the offset when only a position is known.</param>
/// <param name="Detail">One sentence in plain words: counts, old and new values.</param>
/// <param name="Confidence">How far the result can be trusted.</param>
public sealed record RepairItem(RepairAction Action, string Structure, int Track, ByteRange Range, string Detail, RepairConfidence Confidence);

/// <summary>What the repair made of one track.</summary>
/// <param name="Track">Track index (container order).</param>
/// <param name="Codec">Short codec name, or empty when unknown.</param>
/// <param name="Packets">Packets in the output.</param>
/// <param name="DroppedPackets">Packets left out because they were cut off or broken.</param>
/// <param name="Confidence">How far the track's rebuilt structures can be trusted.</param>
/// <param name="Ranges">Input byte ranges the track's recovered packets were read from, merged where adjacent. May be empty when the repair does not track them.</param>
public sealed record RepairTrackReport(int Track, string Codec, long Packets, long DroppedPackets, RepairConfidence Confidence, IReadOnlyList<ByteRange> Ranges);

/// <summary>The outcome of a repair: what was rebuilt, dropped and kept, per track and per structure.</summary>
/// <param name="Status">The overall outcome.</param>
/// <param name="Format">The container that was repaired, as <see cref="MediaFormatId.Name"/>.</param>
/// <param name="InputLength">Input size in bytes.</param>
/// <param name="OutputLength">Output size in bytes; 0 when no file was written.</param>
/// <param name="Items">Every action, in file order.</param>
/// <param name="Tracks">One entry per track the repair looked at. Empty for formats without tracks to report.</param>
/// <param name="Confidence">The lowest confidence of any rebuilt structure; <see cref="RepairConfidence.High"/> when nothing needed rebuilding.</param>
/// <param name="Warnings">Plain-language notes: things the repair could not do and what would be needed.</param>
public sealed record RepairReport(
    RepairStatus Status,
    string Format,
    long InputLength,
    long OutputLength,
    IReadOnlyList<RepairItem> Items,
    IReadOnlyList<RepairTrackReport> Tracks,
    RepairConfidence Confidence,
    IReadOnlyList<string> Warnings)
{
    /// <summary>True when an output file was written (every status but <see cref="RepairStatus.Unrepairable"/>).</summary>
    public bool OutputWritten => Status != RepairStatus.Unrepairable;

    /// <summary>The total number of input bytes the repair left out of the output.</summary>
    public long DroppedBytes
    {
        get
        {
            long total = 0;
            foreach (RepairItem i in Items)
            {
                if (i.Action == RepairAction.Dropped)
                {
                    total += i.Range.Length;
                }
            }

            return total;
        }
    }
}
