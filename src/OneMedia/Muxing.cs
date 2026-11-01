namespace RecursiveAI.OneMedia;

/// <summary>Why a muxer refused a track or a packet.</summary>
public enum MuxRefusalReason
{
    /// <summary>The output format cannot carry the track's codec. The exception names the track and the codec.</summary>
    UnsupportedCodec = 1,

    /// <summary>The codec needs setup bytes (a parameter-set record, an AudioSpecificConfig, a stream header) and the track has none,
    /// and they cannot be derived from the first packet.</summary>
    MissingConfiguration,

    /// <summary>The track's setup bytes are present but do not parse, or are laid out in a way the output format cannot store.</summary>
    InvalidConfiguration,

    /// <summary>A packet's decode time is earlier than the previous packet's of the same track. Muxers take packets in decode order.</summary>
    NonMonotonicDts,

    /// <summary>A packet has neither a presentation time nor a decode time.</summary>
    MissingTimestamp,

    /// <summary>The track kind or its declared parameters (timescale, sample rate, channel count, frame size) cannot be written.</summary>
    InvalidTrack,

    /// <summary>The call is not allowed in the muxer's current state: a track added after the first packet, a packet after <c>Finish</c>,
    /// a packet for a track that was never added.</summary>
    InvalidState,

    /// <summary>A <see cref="MediaLimits"/> value would be exceeded (tracks, sample-table entries, packet bytes).</summary>
    LimitExceeded,

    /// <summary>The output needs something the destination cannot do, for example a stream that cannot seek where the layout needs to go back.</summary>
    OutputNotSuitable,
}

/// <summary>
/// A muxer refused a track or a packet. The message names the track (by its output index) and what was wrong; the muxer is left in a
/// state where the call can be retried with corrected input unless <see cref="Reason"/> says otherwise, except after a failed
/// <see cref="IMuxer.Finish"/>, which leaves the output incomplete.
/// </summary>
public sealed class MuxRefusedException : MediaException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Why the call was refused.</param>
    /// <param name="message">What was wrong.</param>
    /// <param name="trackIndex">Output index of the track concerned, or -1.</param>
    /// <param name="codec">The codec concerned, or null.</param>
    public MuxRefusedException(MuxRefusalReason reason, string message, int trackIndex = -1, CodecId? codec = null) : base(message)
    {
        Reason = reason;
        TrackIndex = trackIndex;
        Codec = codec;
    }

    /// <summary>Why the call was refused.</summary>
    public MuxRefusalReason Reason { get; }

    /// <summary>Output index of the track concerned (the value <see cref="IMuxer.AddTrack"/> returned), or -1 when none applies.</summary>
    public int TrackIndex { get; }

    /// <summary>The codec concerned, when the refusal is about one.</summary>
    public CodecId? Codec { get; }
}

/// <summary>What one track of a finished output holds.</summary>
/// <param name="TrackIndex">Output index of the track.</param>
/// <param name="Packets">Packets written.</param>
/// <param name="PayloadBytes">Payload bytes written.</param>
/// <param name="Duration">Sum of the packet durations as stored (the media duration).</param>
public readonly record struct TrackMuxReport(int TrackIndex, long Packets, long PayloadBytes, MediaTime Duration);

/// <summary>What a muxer wrote, and what it had to change to write it.</summary>
/// <param name="BytesWritten">Total length of the output in bytes.</param>
/// <param name="Tracks">One entry per track, in output order.</param>
/// <param name="Notes">Every normalisation the muxer applied, in plain words (a timestamp rescaled with rounding, a duration replaced by
/// the decode-time delta, a default filled in). Empty when the output carries the input's values exactly.</param>
public sealed record MuxReport(long BytesWritten, IReadOnlyList<TrackMuxReport> Tracks, IReadOnlyList<string> Notes);

/// <summary>
/// Writes a container from tracks and packets. Declare every track with <see cref="AddTrack"/>, then give packets in decode order
/// with <see cref="WritePacket"/>, then call <see cref="Finish"/>. Payload bytes are copied to the output unchanged; a muxer
/// repackages only through the codec layer's repackagers (Annex B to length-prefixed, ADTS to raw) when the caller asks.
/// </summary>
/// <remarks>
/// <para>Packets of different tracks go in the order the output should store them (the order a demuxer delivers them in is right for a
/// copy). Within a track, decode times must not decrease; a decrease is refused with <see cref="MuxRefusalReason.NonMonotonicDts"/>.</para>
/// <para>Times are <see cref="MediaTime"/> in any timescale. They are rescaled exactly to the track's timescale; when that is not
/// exact the muxer rounds to the nearest tick and says so in <see cref="MuxReport.Notes"/>.</para>
/// <para>A muxer is not thread-safe. <see cref="IDisposable.Dispose"/> before <see cref="Finish"/> abandons the output: whatever was
/// written is left incomplete and the muxer writes nothing more.</para>
/// <para>Incremental formats (fragmented MP4, and later live segmenting) put data on the output while packets are still arriving; the
/// others write at <see cref="Finish"/> or as their layout allows. <see cref="Flush"/> asks an incremental muxer to end the current
/// fragment now; for the others it only pushes buffered bytes to the destination.</para>
/// </remarks>
public interface IMuxer : IDisposable
{
    /// <summary>The container the muxer writes.</summary>
    MediaFormatId Format { get; }

    /// <summary>The tracks added so far, as declared, in output order. <see cref="MediaTrack.Index"/> of each is its output index.</summary>
    IReadOnlyList<MediaTrack> Tracks { get; }

    /// <summary>
    /// Declares a track. Must be called for every track before the first packet.
    /// </summary>
    /// <param name="track">The track as a demuxer or a caller describes it: codec, configuration, timescale, language, disposition,
    /// audio or video parameters, edit information (priming, start time, edit list). Its own <see cref="MediaTrack.Index"/> is ignored.</param>
    /// <returns>The track's output index: the value to put in <see cref="PacketInfo.TrackIndex"/> of its packets.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="track"/> is null.</exception>
    /// <exception cref="MuxRefusedException">The format cannot carry the track (<see cref="MuxRefusalReason.UnsupportedCodec"/>,
    /// <see cref="MuxRefusalReason.MissingConfiguration"/>, <see cref="MuxRefusalReason.InvalidConfiguration"/>,
    /// <see cref="MuxRefusalReason.InvalidTrack"/>), a packet was already written, or the track limit is reached.</exception>
    int AddTrack(MediaTrack track);

    /// <summary>
    /// Writes one packet. The bytes are copied before the call returns, so the caller may reuse the buffer (a demuxer's pull-path view).
    /// </summary>
    /// <param name="info">The packet's description. <see cref="PacketInfo.TrackIndex"/> is the output index. <see cref="PacketInfo.Size"/>
    /// is ignored in favour of the data's length. <see cref="PacketInfo.Dts"/> falls back to the presentation time and the other way
    /// round when one is unknown. A duration that is unknown is taken from the next packet's decode time.</param>
    /// <param name="data">The payload.</param>
    /// <param name="cancellationToken">Checked before the packet is accepted.</param>
    /// <exception cref="MuxRefusedException"><see cref="MuxRefusalReason.NonMonotonicDts"/>, <see cref="MuxRefusalReason.MissingTimestamp"/>,
    /// <see cref="MuxRefusalReason.InvalidState"/> (unknown track, no track declared, after <see cref="Finish"/>) or
    /// <see cref="MuxRefusalReason.LimitExceeded"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    void WritePacket(in PacketInfo info, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the current fragment (incremental formats) or pushes buffered bytes to the destination (the others).
    /// </summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    void Flush(CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the output: indexes, headers and trailers are written and the destination is flushed. The muxer accepts nothing after
    /// this. The destination stream is not closed unless the muxer was told it owns it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write; the output is then incomplete.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="MuxRefusedException">No track was declared, a track has no packets and the format needs at least one, a track's
    /// configuration could not be completed, or a limit was exceeded.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    MuxReport Finish(CancellationToken cancellationToken = default);
}

/// <summary>Conveniences over <see cref="IMuxer"/>.</summary>
public static class MuxerExtensions
{
    /// <summary>Writes a packet delivered by a demuxer's pull path.</summary>
    /// <param name="muxer">The muxer.</param>
    /// <param name="packet">The packet; its <see cref="PacketInfo.TrackIndex"/> must already be the output index.</param>
    /// <param name="cancellationToken">Checked before the packet is accepted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="muxer"/> is null.</exception>
    /// <exception cref="MuxRefusedException">See <see cref="IMuxer.WritePacket"/>.</exception>
    public static void WritePacket(this IMuxer muxer, in PacketView packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(muxer);
        muxer.WritePacket(packet.Info, packet.Data, cancellationToken);
    }

    /// <summary>Writes a packet that owns its bytes.</summary>
    /// <param name="muxer">The muxer.</param>
    /// <param name="packet">The packet; its <see cref="PacketInfo.TrackIndex"/> must already be the output index.</param>
    /// <param name="cancellationToken">Checked before the packet is accepted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="muxer"/> is null.</exception>
    /// <exception cref="MuxRefusedException">See <see cref="IMuxer.WritePacket"/>.</exception>
    public static void WritePacket(this IMuxer muxer, OwnedPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(muxer);
        muxer.WritePacket(packet.Info, packet.Data.Span, cancellationToken);
    }
}
