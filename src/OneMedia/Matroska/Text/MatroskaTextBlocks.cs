using System.Text;

namespace RecursiveAI.OneMedia.Matroska;

// Text subtitle blocks as Matroska stores them (RFC 9559 and the Matroska codec mappings, "Subtitles" page: S_TEXT/ASS, S_TEXT/SSA,
// S_TEXT/WEBVTT, and the WebM D_WEBVTT/* mapping). S_TEXT/UTF8 needs no type: its block is the cue text.

/// <summary>
/// One event of an S_TEXT/ASS or S_TEXT/SSA track as it sits in a Block: the fields of a script's <c>Dialogue:</c> line without the
/// start and end times (the Block's timestamp and duration carry those), led by a read-order number. The Matroska mapping fixes the
/// order: ReadOrder, Layer, Style, Name, MarginL, MarginR, MarginV, Effect, Text.
/// </summary>
/// <param name="ReadOrder">The position of the event in the original script, which lets a player restore the script's order.</param>
/// <param name="Layer">The layer as written (a number for ASS, empty or the SSA "Marked" value for SSA).</param>
/// <param name="Style">The name of the style the event uses.</param>
/// <param name="Name">The actor or speaker name.</param>
/// <param name="MarginL">Left margin as written (often <c>0000</c> or <c>0</c>).</param>
/// <param name="MarginR">Right margin as written.</param>
/// <param name="MarginV">Vertical margin as written.</param>
/// <param name="Effect">The effect field.</param>
/// <param name="Text">The text with its override codes; may contain commas.</param>
public sealed record MatroskaAssEvent(string ReadOrder, string Layer, string Style, string Name, string MarginL, string MarginR, string MarginV, string Effect, string Text)
{
    /// <summary>Reads a Block's content.</summary>
    /// <param name="block">The Block's frame bytes, UTF-8.</param>
    /// <param name="result">The event.</param>
    /// <returns>False when the block has fewer than the eight commas that separate the nine fields. The ninth field takes the rest, commas included.</returns>
    public static bool TryParse(ReadOnlySpan<byte> block, out MatroskaAssEvent? result)
    {
        result = null;
        string s = Encoding.UTF8.GetString(block);
        string[] f = new string[9];
        int at = 0;
        for (int i = 0; i < 8; i++)
        {
            int comma = s.IndexOf(',', at);
            if (comma < 0)
            {
                return false;
            }

            f[i] = s[at..comma];
            at = comma + 1;
        }

        f[8] = s[at..];
        result = new MatroskaAssEvent(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8]);
        return true;
    }

    /// <summary>Writes the Block's content.</summary>
    /// <returns>The nine fields joined by commas, as UTF-8.</returns>
    public byte[] ToBytes() => Encoding.UTF8.GetBytes(string.Join(',', ReadOrder, Layer, Style, Name, MarginL, MarginR, MarginV, Effect, Text));
}

/// <summary>
/// The extra information of an S_TEXT/WEBVTT Block, stored in the BlockAdditional with BlockAddID 1 (Matroska "Subtitles" page, WebVTT):
/// the cue settings line, the cue identifier line and the comment blocks that preceded the cue. The Block itself holds the cue text.
/// </summary>
/// <param name="Settings">The cue settings list; empty when the cue had none.</param>
/// <param name="Identifier">The cue identifier; empty when the cue had none.</param>
/// <param name="Comments">The NOTE blocks that preceded the cue, joined by line feeds; empty when none.</param>
public sealed record MatroskaWebVttAdditions(string Settings, string Identifier, string Comments)
{
    /// <summary>True when all three are empty, which is how a muxer omits the BlockAdditions element.</summary>
    public bool IsEmpty => Settings.Length == 0 && Identifier.Length == 0 && Comments.Length == 0;

    /// <summary>Reads a BlockAdditional.</summary>
    /// <param name="additional">The bytes, UTF-8; empty means no additions.</param>
    /// <returns>The three parts. Missing lines read as empty.</returns>
    public static MatroskaWebVttAdditions Parse(ReadOnlySpan<byte> additional)
    {
        string s = Encoding.UTF8.GetString(additional);
        int first = s.IndexOf('\n', StringComparison.Ordinal);
        if (first < 0)
        {
            return new MatroskaWebVttAdditions(s, string.Empty, string.Empty);
        }

        string settings = s[..first];
        int second = s.IndexOf('\n', first + 1);
        return second < 0
            ? new MatroskaWebVttAdditions(settings, s[(first + 1)..], string.Empty)
            : new MatroskaWebVttAdditions(settings, s[(first + 1)..second], s[(second + 1)..]);
    }

    /// <summary>Writes a BlockAdditional.</summary>
    /// <returns>Settings, a line feed, the identifier, and, when there are comments, a line feed and the comments; empty when nothing is set.</returns>
    public byte[] ToBytes()
    {
        if (IsEmpty)
        {
            return [];
        }

        string s = Comments.Length == 0 ? Settings + "\n" + Identifier : Settings + "\n" + Identifier + "\n" + Comments;
        return Encoding.UTF8.GetBytes(s);
    }
}

/// <summary>
/// The Block of a WebM <c>D_WEBVTT/*</c> track: the cue identifier, the cue settings and the cue text, as three parts separated by
/// line feeds, the way the WebM WebVTT proposal (and files written by common tools) lay it out. The identifier and settings lines may be empty.
/// </summary>
/// <param name="Identifier">The cue identifier; empty when none.</param>
/// <param name="Settings">The cue settings list; empty when none.</param>
/// <param name="Payload">The cue text.</param>
public sealed record WebmWebVttBlock(string Identifier, string Settings, string Payload)
{
    /// <summary>Reads a Block's content.</summary>
    /// <param name="block">The frame bytes, UTF-8.</param>
    /// <returns>The three parts. A block with fewer than two line feeds reads as all payload, so a track from a writer that leaves the lines out still shows its text.</returns>
    public static WebmWebVttBlock Parse(ReadOnlySpan<byte> block)
    {
        string s = Encoding.UTF8.GetString(block);
        int first = s.IndexOf('\n', StringComparison.Ordinal);
        int second = first < 0 ? -1 : s.IndexOf('\n', first + 1);
        return second < 0
            ? new WebmWebVttBlock(string.Empty, string.Empty, s)
            : new WebmWebVttBlock(s[..first], s[(first + 1)..second], s[(second + 1)..]);
    }

    /// <summary>Writes a Block's content.</summary>
    /// <returns>Identifier, line feed, settings, line feed, payload, as UTF-8.</returns>
    public byte[] ToBytes() => Encoding.UTF8.GetBytes(Identifier + "\n" + Settings + "\n" + Payload);
}
