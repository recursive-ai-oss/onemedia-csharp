namespace RecursiveAI.OneMedia.Remuxing;

/// <summary>What kind of thing a remux did not carry over.</summary>
public enum LossKind
{
    /// <summary>A whole track.</summary>
    Track = 1,

    /// <summary>Tags of one native tag system (the report says how many and which).</summary>
    Tag,

    /// <summary>Embedded pictures (cover art).</summary>
    Picture,

    /// <summary>Chapters.</summary>
    Chapter,

    /// <summary>Attachments (Matroska fonts and files).</summary>
    Attachment,

    /// <summary>A codec-specific field: a sample entry box the output does not write, an extra sample description.</summary>
    CodecField,

    /// <summary>A timing detail: an edit list, a start offset, padding.</summary>
    Timing,

    /// <summary>Track disposition flags the target has no field for.</summary>
    Disposition,

    /// <summary>Container structure that has no counterpart in the target (unknown boxes, movie-level metadata boxes).</summary>
    Structure,
}

/// <summary>Why something was not carried over.</summary>
public enum LossReason
{
    /// <summary>The caller chose to leave it out (<see cref="RemuxOptions.Select"/> or <see cref="TrackEdit.Drop"/>). Not a defect of the remux.</summary>
    Deselected = 1,

    /// <summary>The target cannot hold the track's codec.</summary>
    UnsupportedCodec,

    /// <summary>The codec needs setup bytes and neither the input nor its first packets provide them.</summary>
    MissingConfiguration,

    /// <summary>The setup bytes do not parse, or are laid out in a way the target cannot store.</summary>
    InvalidConfiguration,

    /// <summary>The track is encrypted; clear sample entries cannot describe its packets.</summary>
    Protected,

    /// <summary>The target holds fewer of these than the input has (a CMAF file holds one track).</summary>
    TargetLimit,

    /// <summary>The target has no field for it.</summary>
    NotRepresentable,

    /// <summary>The target can hold it, but this version does not write it yet.</summary>
    NotWritten,

    /// <summary>The input could not be inspected for it (a forward-only source, an unreadable tag block); what was lost is unknown.</summary>
    NotInspected,
}

/// <summary>One thing that did not carry over.</summary>
/// <param name="Kind">What kind of thing.</param>
/// <param name="Reason">Why.</param>
/// <param name="InputTrack">Input track index, or -1 when it is not about one track.</param>
/// <param name="Subject">Short name: a codec tag, a tag system, a box type.</param>
/// <param name="Detail">One sentence in plain words: counts, the refusal's message, the field names.</param>
public sealed record RemuxLoss(LossKind Kind, LossReason Reason, int InputTrack, string Subject, string Detail);

/// <summary>What happened to one input track.</summary>
/// <param name="InputTrack">Input track index.</param>
/// <param name="OutputTrack">Output track index, or -1 when the track was left out.</param>
/// <param name="Packets">Packets written.</param>
/// <param name="Repackaging">The change made to each packet's payload: <c>none</c>, <c>annexb-to-length-prefixed</c> or <c>adts-to-raw</c>.</param>
public sealed record RemuxTrackReport(int InputTrack, int OutputTrack, long Packets, string Repackaging);

/// <summary>The outcome of a remux.</summary>
/// <param name="Mux">What the muxer wrote (bytes, packets and payload bytes per output track, its own notes).</param>
/// <param name="Tracks">One entry per input track, in input order.</param>
/// <param name="Losses">Everything that did not carry over. Empty means the output holds everything the input held that the remux looks at.</param>
/// <param name="Changes">Changes the remux made to what it carried, in plain words: times moved, configuration derived from the stream, parameter sets moved out of the samples.</param>
/// <param name="InputDefects">Number of defects the reader recorded in the input.</param>
public sealed record RemuxReport(
    MuxReport Mux,
    IReadOnlyList<RemuxTrackReport> Tracks,
    IReadOnlyList<RemuxLoss> Losses,
    IReadOnlyList<string> Changes,
    int InputDefects)
{
    /// <summary>
    /// Input tracks the caller wanted that did not reach the output and are media the file is about: every video track, and the audio tracks when none
    /// of them was written. Empty when the output holds what was asked for. A track the caller deselected or dropped is not listed.
    /// </summary>
    public IReadOnlyList<int> LostMediaTracks { get; init; } = [];

    /// <summary>True when anything other than a caller's own choice (<see cref="LossReason.Deselected"/>) did not carry over.</summary>
    public bool HasLosses
    {
        get
        {
            foreach (RemuxLoss l in Losses)
            {
                if (l.Reason != LossReason.Deselected)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>What a faststart did.</summary>
/// <param name="Relocated">True when the movie box was moved to the front; false when the file already had it there (the output is then a byte-identical copy).</param>
/// <param name="MovieBoxBytes">Size of the movie box as written.</param>
/// <param name="BytesCopied">Bytes copied from the input without change (everything but the movie box).</param>
/// <param name="OffsetsShifted">Chunk offset entries (<c>stco</c>/<c>co64</c>) that were moved.</param>
/// <param name="PromotedTables">Chunk offset tables that had to become 64-bit.</param>
public sealed record FaststartReport(bool Relocated, long MovieBoxBytes, long BytesCopied, long OffsetsShifted, int PromotedTables);
