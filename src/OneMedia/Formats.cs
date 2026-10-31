using System.Buffers;

namespace RecursiveAI.OneMedia;

/// <summary>Names a container or elementary-stream format. Compare by <see cref="Name"/>.</summary>
/// <param name="Name">Short lowercase name, for example <c>isobmff</c> or <c>matroska</c>.</param>
public readonly record struct MediaFormatId(string Name)
{
    /// <summary>ISO base media file format family: MP4, MOV, 3GP/3G2, M4A, fragmented MP4, CMAF.</summary>
    public static MediaFormatId IsoBmff => new("isobmff");
    /// <summary>Matroska and WebM (EBML).</summary>
    public static MediaFormatId Matroska => new("matroska");
    /// <summary>RIFF WAVE, including BWF, RF64 and Wave64.</summary>
    public static MediaFormatId Wav => new("wav");
    /// <summary>AVI.</summary>
    public static MediaFormatId Avi => new("avi");
    /// <summary>ASF, WMV and WMA.</summary>
    public static MediaFormatId Asf => new("asf");
    /// <summary>MPEG transport stream, including 192-byte M2TS.</summary>
    public static MediaFormatId MpegTs => new("mpegts");
    /// <summary>MPEG program stream and VOB.</summary>
    public static MediaFormatId MpegPs => new("mpegps");
    /// <summary>Flash Video.</summary>
    public static MediaFormatId Flv => new("flv");
    /// <summary>Ogg.</summary>
    public static MediaFormatId Ogg => new("ogg");
    /// <summary>Native FLAC.</summary>
    public static MediaFormatId Flac => new("flac");
    /// <summary>AIFF and AIFF-C.</summary>
    public static MediaFormatId Aiff => new("aiff");
    /// <summary>Apple Core Audio Format.</summary>
    public static MediaFormatId Caf => new("caf");
    /// <summary>An ID3v2 tag at the start of a file; the audio format behind it is found by the reader.</summary>
    public static MediaFormatId Id3 => new("id3");
    /// <summary>MPEG audio (MP3, MP2) elementary stream.</summary>
    public static MediaFormatId MpegAudio => new("mpegaudio");
    /// <summary>AAC in ADTS framing.</summary>
    public static MediaFormatId Adts => new("adts");
    /// <summary>AMR and AMR-WB storage files.</summary>
    public static MediaFormatId Amr => new("amr");
    /// <summary>IVF (VP8/VP9/AV1 frames).</summary>
    public static MediaFormatId Ivf => new("ivf");

    /// <inheritdoc />
    public override string ToString() => Name ?? string.Empty;
}

/// <summary>How sure a sniffer is that the data is its format.</summary>
public enum SniffConfidence
{
    /// <summary>Not this format.</summary>
    None = 0,

    /// <summary>The few bytes available fit, but many other things would fit too (a bare frame-sync pattern).</summary>
    Possible,

    /// <summary>The structure checks out as far as the head allows, but the format's signature is not unique.</summary>
    Likely,

    /// <summary>A signature that belongs to this format alone, and the fields after it are consistent.</summary>
    Certain,
}

/// <summary>The outcome of one sniffer looking at the start of some data.</summary>
/// <param name="Confidence">How sure the sniffer is.</param>
/// <param name="Detail">A sub-type found in the head, such as an ISO major brand (<c>isom</c>, <c>qt  </c>) or an EBML DocType (<c>webm</c>); null if none.</param>
public readonly record struct SniffResult(SniffConfidence Confidence, string? Detail = null)
{
    /// <summary>Not this format.</summary>
    public static SniffResult None => default;
}

/// <summary>
/// Recognises one format from the first bytes of some data. A sniffer must look at nothing but the head it is given, which is
/// at most <see cref="MediaFormatRegistry.SniffBytes"/> long (shorter for a short file), and must never throw on any input.
/// </summary>
public interface IMediaFormatSniffer
{
    /// <summary>The format this sniffer recognises.</summary>
    MediaFormatId Id { get; }

    /// <summary>Human-readable format name, for diagnostics.</summary>
    string DisplayName { get; }

    /// <summary>Checks the head of the data.</summary>
    /// <param name="head">The first bytes of the data.</param>
    /// <returns>How sure the sniffer is; <see cref="SniffResult.None"/> if the head is not this format.</returns>
    SniffResult Sniff(ReadOnlySpan<byte> head);
}

/// <summary>Options for opening a container.</summary>
public sealed record MediaOpenOptions
{
    /// <summary>The default options.</summary>
    public static MediaOpenOptions Default { get; } = new();

    /// <summary>Resource ceilings to enforce. <see cref="MediaLimits.Default"/> unless set.</summary>
    public MediaLimits Limits { get; init; } = MediaLimits.Default;

    /// <summary>Where deviations are collected. When null the opened container creates its own log, available as <see cref="IMediaContainer.Defects"/>.</summary>
    public DefectLog? Defects { get; init; }

    /// <summary>Leave the source open when the demuxer is disposed. Default false: the demuxer owns the source.</summary>
    public bool LeaveOpen { get; init; }
}

/// <summary>A format that can be both recognised and opened. Registered explicitly so formats that are not used trim away.</summary>
public interface IMediaFormat : IMediaFormatSniffer
{
    /// <summary>Opens a seekable source as a demuxer, positioned at the first packet.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the headers and indexes are read.</param>
    /// <returns>The demuxer; the caller disposes it.</returns>
    /// <exception cref="MediaFormatException">The data is not valid for this format.</exception>
    /// <exception cref="MediaLimitExceededException">The data asks for more than the limits allow.</exception>
    IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default);

    /// <summary>Opens a source that can only be read once, front to back. Formats that cannot be read that way throw.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the headers are read.</param>
    /// <returns>The demuxer; the caller disposes it.</returns>
    /// <exception cref="NotSupportedException">The format cannot be read without seeking.</exception>
    IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{DisplayName} cannot be read from a forward-only source.");

    /// <summary>Opens an asynchronous (ranged or remote) source.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The demuxer; the caller disposes it.</returns>
    /// <exception cref="NotSupportedException">The format has no asynchronous reader.</exception>
    ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{DisplayName} has no asynchronous reader.");
}

/// <summary>A sniffer's verdict on some data.</summary>
/// <param name="Format">The sniffer that matched; also an <see cref="IMediaFormat"/> when the format can be opened.</param>
/// <param name="Result">What it found.</param>
public readonly record struct FormatMatch(IMediaFormatSniffer Format, SniffResult Result);

/// <summary>
/// The formats a program has chosen to support. There is no global registry and nothing is found by reflection: the program adds
/// the formats it wants, so the ones it does not mention are trimmed away by NativeAOT and the trimmer. Build it once at start-up;
/// adding formats while other threads read is not supported, reading from several threads is.
/// </summary>
public sealed class MediaFormatRegistry
{
    /// <summary>How many bytes from the start of the data sniffers are shown. Enough for several 204-byte transport packets and for ISO boxes that follow a small <c>ftyp</c>.</summary>
    public const int SniffBytes = 4096;

    private readonly List<IMediaFormatSniffer> _formats = [];

    /// <summary>The registered formats, in registration order.</summary>
    public IReadOnlyList<IMediaFormatSniffer> Formats => _formats;

    /// <summary>Registers a format. Where two formats are equally sure, the one registered first wins.</summary>
    /// <param name="format">A sniffer, or a full <see cref="IMediaFormat"/> that can also open what it recognises.</param>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is null.</exception>
    public void Add(IMediaFormatSniffer format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _formats.Add(format);
    }

    /// <summary>Finds the most confident format for the head of some data.</summary>
    /// <param name="head">The first bytes; only the first <see cref="SniffBytes"/> are used.</param>
    /// <param name="match">The best match, when there is one.</param>
    /// <returns>False when no registered format recognises the data.</returns>
    public bool TrySniff(ReadOnlySpan<byte> head, out FormatMatch match)
    {
        if (head.Length > SniffBytes)
        {
            head = head[..SniffBytes];
        }

        match = default;
        SniffConfidence best = SniffConfidence.None;
        foreach (IMediaFormatSniffer sniffer in _formats)
        {
            SniffResult result = sniffer.Sniff(head);
            if (result.Confidence > best)
            {
                best = result.Confidence;
                match = new FormatMatch(sniffer, result);
            }
        }

        return best != SniffConfidence.None;
    }

    /// <summary>Reads the head of a source (one read of up to <see cref="SniffBytes"/>) and sniffs it.</summary>
    /// <param name="source">The source.</param>
    /// <param name="match">The best match, when there is one.</param>
    /// <param name="cancellationToken">Observed before the read.</param>
    /// <returns>False when no registered format recognises the data.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public bool TrySniff(IMediaSource source, out FormatMatch match, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(SniffBytes);
        try
        {
            int n = source.Read(0, buffer.AsSpan(0, SniffBytes), cancellationToken);
            return TrySniff(buffer.AsSpan(0, n), out match);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads the head of an asynchronous source (one range request of up to <see cref="SniffBytes"/>) and sniffs it.</summary>
    /// <param name="source">The source.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The best match, or null when no registered format recognises the data.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public async ValueTask<FormatMatch?> SniffAsync(IAsyncMediaSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(SniffBytes);
        try
        {
            int n = await source.ReadAsync(0, buffer.AsMemory(0, SniffBytes), cancellationToken).ConfigureAwait(false);
            return TrySniff(buffer.AsSpan(0, n), out FormatMatch match) ? match : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Sniffs a seekable source and opens it with the format that matched.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="options">Options; <see cref="MediaOpenOptions.Default"/> if null.</param>
    /// <param name="cancellationToken">Observed while sniffing and opening.</param>
    /// <returns>The demuxer; the caller disposes it.</returns>
    /// <exception cref="UnsupportedMediaException">No registered format recognises the data, or the match cannot be opened.</exception>
    /// <exception cref="MediaFormatException">The data is not valid for the format it was recognised as.</exception>
    public IDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!TrySniff(source, out FormatMatch match, cancellationToken))
        {
            throw new UnsupportedMediaException("The data is not in any registered format.");
        }

        if (match.Format is not IMediaFormat format)
        {
            throw new UnsupportedMediaException($"{match.Format.DisplayName} is recognised but this program has no reader for it.");
        }

        return format.Open(source, options ?? MediaOpenOptions.Default, cancellationToken);
    }
}
