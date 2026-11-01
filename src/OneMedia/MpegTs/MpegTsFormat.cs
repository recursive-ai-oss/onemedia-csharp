using RecursiveAI.OneMedia.Sniffing;
namespace RecursiveAI.OneMedia.MpegTs;

/// <summary>The MPEG transport stream format: recognises 188-, 192- and 204-byte packet streams and opens them.</summary>
public sealed class MpegTsFormat : IMediaFormat
{
    private readonly MpegTsSniffer _sniffer = new();

    /// <inheritdoc />
    public MediaFormatId Id => _sniffer.Id;

    /// <inheritdoc />
    public string DisplayName => _sniffer.DisplayName;

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) => _sniffer.Sniff(head);

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        MpegTsDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        MpegTsDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        Mpeg.AsyncDemuxer.OpenAsync(source, s => MpegTsDemuxer.Open(s, options, cancellationToken), cancellationToken);
}
