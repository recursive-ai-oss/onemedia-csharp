using RecursiveAI.OneMedia.ElementaryStreams;

namespace RecursiveAI.OneMedia.ElementaryStreams;

/// <summary>Recognises and opens one kind of raw video file. Registered once per <see cref="VideoStreamKind"/>.</summary>
public sealed class VideoStreamFormat : IMediaFormat
{
    private readonly VideoStreamKind _kind;

    /// <summary>Creates the format for one bitstream kind.</summary>
    /// <param name="kind">The bitstream.</param>
    public VideoStreamFormat(VideoStreamKind kind)
    {
        _kind = kind;
        (Id, DisplayName) = kind switch
        {
            VideoStreamKind.H264 => (new MediaFormatId("h264"), "H.264 Annex B stream"),
            VideoStreamKind.Hevc => (new MediaFormatId("hevc"), "H.265 Annex B stream"),
            VideoStreamKind.Vvc => (new MediaFormatId("vvc"), "H.266 Annex B stream"),
            VideoStreamKind.MpegVideo => (new MediaFormatId("mpegvideo"), "MPEG-1/2 video stream"),
            _ => (new MediaFormatId("obu"), "AV1 low-overhead stream"),
        };
    }

    /// <inheritdoc />
    public MediaFormatId Id { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) => _kind switch
    {
        VideoStreamKind.MpegVideo => SniffMpeg(head),
        VideoStreamKind.Av1Obu => SniffObu(head),
        _ => SniffAnnexB(head, _kind),
    };

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        VideoStreamDemuxer.Open(source, _kind, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        VideoStreamDemuxer.Open(source, _kind, options, cancellationToken);

    // ISO/IEC 13818-2 §6.2.2.1: a video sequence starts with sequence_header_code 0x000001B3 (a sequence may be preceded by zero bytes).
    private static SniffResult SniffMpeg(ReadOnlySpan<byte> head)
    {
        int i = 0;
        while (i < head.Length && head[i] == 0)
        {
            i++;
        }

        if (i < 2 || i + 5 >= head.Length || head[i] != 1 || head[i + 1] != 0xB3)
        {
            return SniffResult.None;
        }

        // horizontal_size_value and vertical_size_value (12 bits each) must be nonzero.
        int width = (head[i + 2] << 4) | (head[i + 3] >> 4);
        int height = ((head[i + 3] & 0xF) << 8) | head[i + 4];
        return width > 0 && height > 0 ? new SniffResult(SniffConfidence.Likely) : SniffResult.None;
    }

    private static SniffResult SniffObu(ReadOnlySpan<byte> head)
    {
        // AV1 specification §5.2: a low-overhead stream starts with a temporal delimiter OBU, 0x12 0x00.
        if (head.Length < 4 || head[0] != 0x12 || head[1] != 0x00)
        {
            return SniffResult.None;
        }

        // The delimiter is normally followed by the sequence header OBU (type 1, has_size_field set, forbidden bit clear).
        return (head[2] & 0x80) == 0 && ((head[2] >> 3) & 0xF) == 1 && (head[2] & 2) != 0 ? new SniffResult(SniffConfidence.Likely) : SniffResult.None;
    }

    // Annex B (H.264 §B.1, H.265 §B.2, H.266 §B.2): zero bytes, 0x000001, then NAL units. The three codecs share the framing, so the
    // NAL unit headers decide: parameter sets (and a few other types) that are valid for exactly one codec score for it.
    private static SniffResult SniffAnnexB(ReadOnlySpan<byte> head, VideoStreamKind kind)
    {
        int h264 = 0, hevc = 0, vvc = 0, nals = 0;
        int pos = 0;
        while (pos + 6 < head.Length && nals < 24)
        {
            int at = head[pos..].IndexOf((ReadOnlySpan<byte>)[0, 0, 1]);
            if (at < 0)
            {
                break;
            }

            pos += at + 3;
            if (nals == 0 && at > 3)
            {
                return SniffResult.None; // a stream starts at its first start code, preceded by zero bytes only
            }

            if (nals == 0 && !AllZero(head[..(pos - 3)]))
            {
                return SniffResult.None;
            }

            nals++;
            byte b0 = head[pos], b1 = head[pos + 1];
            if ((b0 & 0x80) == 0)
            {
                int t = b0 & 0x1F;
                h264 += t switch { 7 => 3, 8 => 2, 9 => 1, 6 => 1, 5 => 1, 1 => 1, _ => 0 };
                int ht = (b0 >> 1) & 0x3F;
                if ((b1 & 7) >= 1 && (b0 & 1) == 0 && (b1 >> 3) == 0)
                {
                    hevc += ht switch { 32 => 3, 33 => 3, 34 => 2, 35 => 1, 39 => 1, 19 or 20 or 1 => 1, _ => 0 };
                }

                if ((b0 & 0x40) == 0 && (b1 & 7) >= 1)
                {
                    int vt = b1 >> 3;
                    vvc += vt switch { 14 => 2, 15 => 3, 16 => 2, 20 => 1, 7 or 8 or 0 or 1 => 1, _ => 0 };
                }
            }
        }

        // A codec is named only when its header test passes more NAL units than the others'.
        int best = kind switch { VideoStreamKind.H264 => h264, VideoStreamKind.Hevc => hevc, _ => vvc };
        bool wins = kind switch
        {
            VideoStreamKind.H264 => h264 >= 3 && h264 > hevc && h264 > vvc,
            VideoStreamKind.Hevc => hevc >= 3 && hevc >= h264 && hevc > vvc && IsHevcLike(head),
            _ => vvc >= 3 && vvc > h264 && vvc > hevc,
        };
        return wins && best > 0 ? new SniffResult(SniffConfidence.Likely) : SniffResult.None;
    }

    // H.264 headers can read as plausible HEVC ones; an HEVC stream's first parameter set has nuh_layer_id 0 and temporal_id_plus1 1 in its second byte.
    private static bool IsHevcLike(ReadOnlySpan<byte> head)
    {
        int pos = 0;
        while (pos + 6 < head.Length)
        {
            int at = head[pos..].IndexOf((ReadOnlySpan<byte>)[0, 0, 1]);
            if (at < 0)
            {
                return false;
            }

            pos += at + 3;
            int t = (head[pos] >> 1) & 0x3F;
            if (t is 32 or 33 or 34 or 35)
            {
                return head[pos + 1] == 1 && (head[pos] & 0x81) == 0;
            }
        }

        return false;
    }

    private static bool AllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The IVF format: recognises the DKIF signature and opens the file.</summary>
public sealed class IvfFormat : IMediaFormat
{
    private readonly Sniffing.IvfSniffer _sniffer = new();

    /// <inheritdoc />
    public MediaFormatId Id => _sniffer.Id;

    /// <inheritdoc />
    public string DisplayName => _sniffer.DisplayName;

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) => _sniffer.Sniff(head);

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        IvfDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        IvfDemuxer.Open(source, options, cancellationToken);
}
