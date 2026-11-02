using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Repair.Reconstruction;

/// <summary>How far a reconstructed track can be trusted.</summary>
public enum ReconstructionConfidence
{
    /// <summary>Nothing of the track was recovered.</summary>
    None = 0,

    /// <summary>Samples were found, but the codec setup or the timing was guessed (no reference file, no in-band parameter sets for
    /// the timing, a guessed AudioSpecificConfig, an assumed frame rate).</summary>
    Low,

    /// <summary>The samples were framed from their own syntax and the setup came from the stream or a reference file, but part of the timing was estimated.</summary>
    Medium,

    /// <summary>Framing was validated sample by sample, the setup came from a reference file or from parameter sets in the stream, and the timing comes from a reference file or the stream's own timing information.</summary>
    High,
}

/// <summary>Where a track's codec setup (sample entry) came from.</summary>
public enum ReconstructionSetupSource
{
    /// <summary>A reference file from the same device.</summary>
    Reference = 0,

    /// <summary>Parameter sets, sequence headers or frame headers found in the media data itself.</summary>
    InStream,

    /// <summary>Tried against the data until one fitted (for example an AudioSpecificConfig for AAC).</summary>
    Guessed,
}

/// <summary>Why a stretch of the media data was not turned into a sample.</summary>
public enum SkippedReason
{
    /// <summary>The bytes at that position are not the start of a sample of any known track, and the scan moved on to the next position that is.</summary>
    Unframed = 0,

    /// <summary>The input ends inside the last sample; the partial sample is dropped.</summary>
    PartialSample,

    /// <summary>Bytes after the media data box's declared end, or after the last whole sample, that could not be read as samples (for example a cut-off <c>moov</c>).</summary>
    AfterMediaData,
}

/// <summary>A stretch of the input that was not used.</summary>
/// <param name="Range">Where it is in the input.</param>
/// <param name="Reason">Why it was left out.</param>
/// <param name="TrackIndex">The output track the bytes probably belong to (a dropped partial sample), or -1.</param>
public readonly record struct SkippedRange(ByteRange Range, SkippedReason Reason, int TrackIndex = -1);

/// <summary>What was recovered for one output track.</summary>
/// <param name="Index">Output track index.</param>
/// <param name="Kind">Video or audio.</param>
/// <param name="Codec">The codec.</param>
/// <param name="Samples">Samples written.</param>
/// <param name="PayloadBytes">Bytes of media data used.</param>
/// <param name="Duration">Track duration as written, in the track's timescale; unknown when no sample was recovered.</param>
/// <param name="Confidence">How far to trust the track.</param>
/// <param name="Setup">Where the sample entry came from.</param>
/// <param name="Notes">Plain-words statements about what was guessed or estimated for this track.</param>
public sealed record ReconstructedTrack(
    int Index,
    TrackKind Kind,
    CodecId Codec,
    long Samples,
    long PayloadBytes,
    MediaTime Duration,
    ReconstructionConfidence Confidence,
    ReconstructionSetupSource Setup,
    IReadOnlyList<string> Notes);

/// <summary>
/// What a reconstruction did: the tracks it rebuilt with a confidence each, which byte ranges of the input became samples and which
/// were left out, and every guess it made.
/// </summary>
/// <param name="InputLength">Length of the input in bytes.</param>
/// <param name="MediaDataOffset">Offset of the <c>mdat</c> box header, or -1 when the input has no recognisable media data box and the scan began after the file type box.</param>
/// <param name="ScanStart">Offset of the first byte that was scanned for samples.</param>
/// <param name="ScanEnd">Offset after the last byte that was scanned (the end of the media data, or the end of the input).</param>
/// <param name="OutputLength">Length of the output file.</param>
/// <param name="Tracks">One entry per output track.</param>
/// <param name="UsedRanges">The input ranges that became samples, with neighbouring ranges merged.</param>
/// <param name="SkippedRanges">The input ranges inside the scanned span that did not, in order; at most <see cref="ReconstructionReport.MaxListedSkips"/> are listed.</param>
/// <param name="SkippedRangeCount">The total number of skipped ranges, which may exceed the listed ones.</param>
/// <param name="UsedBytes">Bytes of the scanned span that became samples.</param>
/// <param name="Notes">Statements about the input and the output that are not about one track.</param>
public sealed record ReconstructionReport(
    long InputLength,
    long MediaDataOffset,
    long ScanStart,
    long ScanEnd,
    long OutputLength,
    IReadOnlyList<ReconstructedTrack> Tracks,
    IReadOnlyList<ByteRange> UsedRanges,
    IReadOnlyList<SkippedRange> SkippedRanges,
    long SkippedRangeCount,
    long UsedBytes,
    IReadOnlyList<string> Notes)
{
    /// <summary>The most skipped ranges a report lists; more are counted in <see cref="SkippedRangeCount"/>.</summary>
    public const int MaxListedSkips = 1000;

    /// <summary>The share of the scanned span that became samples, from 0 to 1; 0 for an empty span.</summary>
    public double RecoveredFraction => ScanEnd > ScanStart ? (double)UsedBytes / (ScanEnd - ScanStart) : 0;

    /// <summary>The lowest confidence among tracks that recovered samples; <see cref="ReconstructionConfidence.None"/> when there are none.</summary>
    public ReconstructionConfidence Confidence
    {
        get
        {
            ReconstructionConfidence lowest = ReconstructionConfidence.High;
            bool any = false;
            foreach (ReconstructedTrack t in Tracks)
            {
                if (t.Samples > 0)
                {
                    any = true;
                    lowest = (ReconstructionConfidence)Math.Min((int)lowest, (int)t.Confidence);
                }
            }

            return any ? lowest : ReconstructionConfidence.None;
        }
    }
}

/// <summary>Why a reconstruction could not produce a file.</summary>
public enum ReconstructionFailure
{
    /// <summary>The input has no media data to scan: no <c>mdat</c> box and nothing after the file type box.</summary>
    NoMediaData = 1,

    /// <summary>No track could be set up: without a reference file the media data holds no parameter sets or recognisable audio, or the reference's tracks match nothing in the data.</summary>
    NoTracks,

    /// <summary>Tracks were set up but no sample could be framed.</summary>
    NoSamples,

    /// <summary>The output stream cannot be written the way the muxer needs.</summary>
    OutputNotSuitable,
}

/// <summary>A reconstruction could not produce a file; the message says what was missing and what a reference file would add.</summary>
public sealed class ReconstructionException : MediaException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="failure">Why it failed.</param>
    /// <param name="message">The explanation.</param>
    public ReconstructionException(ReconstructionFailure failure, string message) : base(message) => Failure = failure;

    /// <summary>Why the reconstruction failed.</summary>
    public ReconstructionFailure Failure { get; }
}

/// <summary>
/// Frames the samples of a codec whose syntax the core library does not parse (AAC raw data blocks need an audio decoder's syntax
/// tables, so <c>RecursiveAI.OneMedia.Audio</c> supplies the probe). Give instances to <see cref="ReconstructionOptions.AudioProbes"/>.
/// </summary>
public interface IAudioFrameProbe
{
    /// <summary>The codec this probe frames.</summary>
    CodecId Codec { get; }

    /// <summary>
    /// Tells whether <paramref name="config"/> is a setup this probe can frame (for AAC, an AudioSpecificConfig it can parse). Creates the framer for it.
    /// </summary>
    /// <param name="config">Codec setup bytes as a sample entry stores them.</param>
    /// <returns>A framer for the stream, or null when the setup is outside what the probe handles. Never throws.</returns>
    IAudioFramer? CreateFramer(ReadOnlySpan<byte> config);

    /// <summary>
    /// Candidate setups to try when no reference file or in-stream header gives one, most likely first (AAC: AudioSpecificConfigs for the
    /// usual sample rates and channel layouts). Empty when guessing is not possible.
    /// </summary>
    IEnumerable<GuessedAudioSetup> Guesses();
}

/// <summary>One setup an <see cref="IAudioFrameProbe"/> offers to try.</summary>
/// <param name="Config">The codec setup bytes (an AudioSpecificConfig).</param>
/// <param name="SampleRate">Sample rate of the stream in Hz.</param>
/// <param name="Channels">Channel count.</param>
public readonly record struct GuessedAudioSetup(ReadOnlyMemory<byte> Config, int SampleRate, int Channels);

/// <summary>Result of looking for one sample at the start of a buffer.</summary>
public enum AudioFrameResult
{
    /// <summary>The bytes do not start a sample of this stream.</summary>
    NotFrame = 0,

    /// <summary>The bytes start a sample whose end lies beyond the buffer: offer more bytes.</summary>
    Incomplete,

    /// <summary>A whole sample starts here; its length is reported.</summary>
    Frame,
}

/// <summary>Finds the end of one audio sample (one container-level packet) at the start of a buffer.</summary>
public interface IAudioFramer
{
    /// <summary>Samples (PCM frames per channel) one packet holds, for the packet's duration in the sample-rate timescale.</summary>
    int SamplesPerFrame { get; }

    /// <summary>Measures the sample at the start of <paramref name="data"/>.</summary>
    /// <param name="data">Bytes starting at a candidate sample boundary; they may extend past the sample.</param>
    /// <param name="length">The sample's length when the result is <see cref="AudioFrameResult.Frame"/>, else 0.</param>
    /// <returns>What was found.</returns>
    AudioFrameResult Probe(ReadOnlySpan<byte> data, out int length);
}

/// <summary>Settings of a reconstruction.</summary>
public sealed record ReconstructionOptions
{
    /// <summary>Limits applied to what the scan accepts (sample size, sample count per track). Default <see cref="MediaLimits.Default"/>.</summary>
    public MediaLimits Limits { get; init; } = MediaLimits.Default;

    /// <summary>
    /// Probes for codecs the core library cannot frame by itself (AAC, through <c>RecursiveAI.OneMedia.Audio</c>). Without one, an AAC track
    /// of the reference is reported as not recoverable and its bytes are skipped.
    /// </summary>
    public IReadOnlyList<IAudioFrameProbe> AudioProbes { get; init; } = [];

    /// <summary>
    /// The frame rate to use for video when neither a reference file, the stream's timing information nor audio to measure against
    /// gives one, as numerator and denominator. Default 30/1.
    /// </summary>
    public (int Numerator, int Denominator) AssumedFrameRate { get; init; } = (30, 1);

    /// <summary>
    /// Bytes of sound from which a guessed audio setup is judged (the guess must frame this many samples in a row). Default 8 samples.
    /// </summary>
    public int GuessConfirmSamples { get; init; } = 8;

    /// <summary>Settings for the muxer limits: tracks and sample-table entries come from <see cref="Limits"/>.</summary>
    public IsoFileKind? Kind { get; init; }
}
