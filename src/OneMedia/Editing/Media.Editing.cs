using System.Globalization;
using RecursiveAI.OneMedia.Editing;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia;

public static partial class Media
{
    /// <summary>
    /// Trims an MP4, MOV or 3GP file to a time range without re-encoding. Frame-accurate by default: the first frame shown is the first one at or after
    /// <paramref name="from"/> and the clip ends before <paramref name="to"/>, with the frames a decoder needs before it hidden by an edit list.
    /// See <see cref="IsoCutter.Trim"/> for the rules.
    /// </summary>
    /// <param name="input">The source file.</param>
    /// <param name="output">The new file; written next to its final name and renamed into place, so a failure leaves nothing behind.</param>
    /// <param name="from">Start on the source's presentation timeline.</param>
    /// <param name="to">End, exclusive.</param>
    /// <param name="mode">Frame-accurate (default) or keyframe-only.</param>
    /// <param name="cancellationToken">Cancels the trim.</param>
    /// <returns>Where the clip starts, its length, and what was rounded or dropped.</returns>
    /// <exception cref="EditRefusedException">The range is empty, the file is not an ISO base media file, or it cannot be cut.</exception>
    public static ClipReport Trim(string input, string output, TimeSpan from, TimeSpan to, TrimMode mode = TrimMode.FrameAccurate, CancellationToken cancellationToken = default) =>
        EditFiles.Trim(input, output, from, to, mode, cancellationToken);

    /// <summary>
    /// Splits an MP4, MOV or 3GP file into parts that start on keyframes at or before the given times. The parts hold the source's packets exactly once, so
    /// <see cref="Concat(string, string[])"/> puts the file back together.
    /// </summary>
    /// <param name="input">The source file.</param>
    /// <param name="outputPattern">A path for the parts; <c>{0}</c> stands for the part number from 1 (<c>clip-{0:000}.mp4</c>). Without it, <c>-001</c> is added before the extension.</param>
    /// <param name="at">Cut times, ascending and greater than zero.</param>
    /// <param name="cancellationToken">Cancels the split.</param>
    /// <returns>One report per part.</returns>
    /// <exception cref="EditRefusedException">The cut times are unusable or the file cannot be cut.</exception>
    public static IReadOnlyList<ClipReport> Split(string input, string outputPattern, IReadOnlyList<TimeSpan> at, CancellationToken cancellationToken = default) =>
        EditFiles.Split(input, outputPattern, at, cancellationToken);

    /// <summary>
    /// Splits an MP4, MOV or 3GP file into parts of about <paramref name="maxPartBytes"/> of payload, cutting on the first keyframe after the budget is reached.
    /// </summary>
    /// <param name="input">The source file.</param>
    /// <param name="outputPattern">The part path pattern, as for <see cref="Split"/>.</param>
    /// <param name="maxPartBytes">Payload budget per part, in bytes.</param>
    /// <param name="cancellationToken">Cancels the split.</param>
    /// <returns>One report per part.</returns>
    /// <exception cref="EditRefusedException">The file has no video track or cannot be cut.</exception>
    public static IReadOnlyList<ClipReport> SplitBySize(string input, string outputPattern, long maxPartBytes, CancellationToken cancellationToken = default) =>
        EditFiles.SplitBySize(input, outputPattern, maxPartBytes, cancellationToken);

    /// <summary>
    /// Joins MP4, MOV or 3GP files end to end without re-encoding. Inputs must match track for track; a mismatch is refused with the track named.
    /// </summary>
    /// <param name="output">The new file.</param>
    /// <param name="inputs">The files, in order.</param>
    /// <returns>The length of the result and what was dropped.</returns>
    /// <exception cref="EditRefusedException">The inputs differ in codec, configuration, timescale or frame size (<see cref="EditRefusalReason.NeedsReencode"/>).</exception>
    public static ClipReport Concat(string output, params string[] inputs) => EditFiles.Join(inputs, output, CancellationToken.None);

    /// <summary>
    /// Joins MP4, MOV or 3GP files end to end without re-encoding, like <see cref="Concat(string, string[])"/>, and can be cancelled.
    /// </summary>
    /// <param name="output">The new file.</param>
    /// <param name="inputs">The files, in order.</param>
    /// <param name="cancellationToken">Cancels the join; nothing is left behind.</param>
    /// <returns>The length of the result and what was dropped.</returns>
    /// <exception cref="EditRefusedException">The inputs differ in codec, configuration, timescale or frame size (<see cref="EditRefusalReason.NeedsReencode"/>).</exception>
    public static ClipReport Concat(string output, IReadOnlyList<string> inputs, CancellationToken cancellationToken = default) => EditFiles.Join(inputs, output, cancellationToken);
}
