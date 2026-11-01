using RecursiveAI.OneMedia.Codecs.Ac3;
using RecursiveAI.OneMedia.Codecs.MpegAudio;
using RecursiveAI.OneMedia.Sniffing;
using RecursiveAI.OneMedia.Tags.Id3;

namespace RecursiveAI.OneMedia.AudioStreams;

/// <summary>Recognises MPEG audio by a frame header, and a second header where the first frame ends when the head shows it; opens it with <see cref="MpegAudioDemuxer"/>.</summary>
public sealed class MpegAudioFormat : IMediaFormat
{
    private static readonly MpegAudioSniffer s_basic = new();

    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.MpegAudio;

    /// <inheritdoc />
    public string DisplayName => "MPEG audio";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        SniffResult basic = s_basic.Sniff(head);
        if (basic.Confidence == SniffConfidence.None || !MpegAudioFrameHeader.TryParse(head, out MpegAudioFrameHeader h))
        {
            return basic;
        }

        // A second header of the same stream right after the first frame makes a false match very unlikely.
        int length = h.FrameLength;
        if (length > 0 && head.Length >= length + 4 && MpegAudioFrameHeader.TryParse(head[length..], out MpegAudioFrameHeader next) && next.Version == h.Version && next.Layer == h.Layer && next.SampleRate == h.SampleRate)
        {
            return new SniffResult(SniffConfidence.Likely, basic.Detail);
        }

        if (h.BitRate == 0)
        {
            // Free format (bit-rate index 0): no length in the header, so a second header with the same stable fields a plausible distance on is the evidence.
            for (int j = 24; j + 4 <= head.Length; j++)
            {
                if (!SameStream(head, j))
                {
                    continue;
                }

                // The frame after that is one frame further on, a byte either way for the padding bit.
                int second = j * 2;
                if (second + 5 > head.Length || SameStream(head, second - 1) || SameStream(head, second) || SameStream(head, second + 1))
                {
                    return new SniffResult(SniffConfidence.Likely, basic.Detail);
                }
            }

            return basic;
        }

        return length > 0 && head.Length >= length + 4 ? SniffResult.None : basic;
    }

    private static bool SameStream(ReadOnlySpan<byte> head, int at) =>
        at + 4 <= head.Length && head[at] == 0xFF && (head[at + 1] & 0xFE) == (head[1] & 0xFE) && (head[at + 2] & 0xFC) == (head[2] & 0xFC) && (head[at + 3] & 0xC0) == (head[3] & 0xC0);

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => MpegAudioDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => MpegAudioDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        await AudioStreamAsyncDemuxer.OpenAsync(source, options, (s, o, ct) => MpegAudioDemuxer.Open(s, o, ct), cancellationToken).ConfigureAwait(false);
}

/// <summary>Recognises AAC in ADTS framing and opens it with <see cref="AdtsDemuxer"/>.</summary>
public sealed class AdtsFormat : IMediaFormat
{
    private static readonly AdtsSniffer s_basic = new();

    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Adts;

    /// <inheritdoc />
    public string DisplayName => "AAC (ADTS)";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) => s_basic.Sniff(head);

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => AdtsDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => AdtsDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        await AudioStreamAsyncDemuxer.OpenAsync(source, options, (s, o, ct) => AdtsDemuxer.Open(s, o, ct), cancellationToken).ConfigureAwait(false);
}

/// <summary>Recognises AC-3 and E-AC-3 by the 0x0B77 syncword and a valid syncframe header, and opens them with <see cref="Ac3Demuxer"/>.</summary>
public sealed class Ac3Format : IMediaFormat
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Ac3;

    /// <inheritdoc />
    public string DisplayName => "AC-3 / E-AC-3";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        // ETSI TS 102 366 §4.3.1: every syncframe starts with the syncword 0x0B77.
        if (head.Length < 8 || head[0] != 0x0B || head[1] != 0x77 || !Ac3SyncFrame.TryParse(head, out Ac3SyncFrame f))
        {
            return SniffResult.None;
        }

        string detail = f.IsEac3 ? "eac3" : "ac3";
        if (head.Length >= f.FrameSize + 2)
        {
            return head[f.FrameSize] == 0x0B && head[f.FrameSize + 1] == 0x77 ? new SniffResult(SniffConfidence.Likely, detail) : SniffResult.None;
        }

        return new SniffResult(SniffConfidence.Possible, detail);
    }

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => Ac3Demuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => Ac3Demuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        await AudioStreamAsyncDemuxer.OpenAsync(source, options, (s, o, ct) => Ac3Demuxer.Open(s, o, ct), cancellationToken).ConfigureAwait(false);
}

/// <summary>Recognises AMR and AMR-WB storage files, single and multi-channel, and opens them with <see cref="AmrDemuxer"/>.</summary>
public sealed class AmrFormat : IMediaFormat
{
    private static readonly AmrSniffer s_basic = new();

    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Amr;

    /// <inheritdoc />
    public string DisplayName => "AMR";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        SniffResult basic = s_basic.Sniff(head);
        if (basic.Confidence != SniffConfidence.None)
        {
            return basic;
        }

        // RFC 4867 §5.2.
        if (head.StartsWith("#!AMR-WB_MC1.0\n"u8))
        {
            return new SniffResult(SniffConfidence.Certain, "wb-mc");
        }

        return head.StartsWith("#!AMR_MC1.0\n"u8) ? new SniffResult(SniffConfidence.Certain, "nb-mc") : SniffResult.None;
    }

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => AmrDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => AmrDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        await AudioStreamAsyncDemuxer.OpenAsync(source, options, (s, o, ct) => AmrDemuxer.Open(s, o, ct), cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// Opens a file that starts with an ID3v2 tag. The tag can be longer than the bytes a sniffer sees, so this format looks behind the tag itself
/// and hands the file to the reader for what it finds there: MPEG audio, ADTS, AC-3, and, on a seekable source, FLAC or Ogg when the tag was too long
/// for their own sniffers to see past. Other audio behind a tag is reported as unsupported here.
/// </summary>
public sealed class Id3AudioFormat : IMediaFormat
{
    private static readonly Id3Sniffer s_basic = new();

    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Id3;

    /// <inheritdoc />
    public string DisplayName => "ID3v2-tagged audio";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        SniffResult basic = s_basic.Sniff(head);
        if (basic.Confidence == SniffConfidence.None)
        {
            return basic;
        }

        // Tags in front of FLAC, WAV or Ogg are those formats' business: when the bytes behind the tags are visible and are not ours, step aside.
        int at = 0;
        while (at + Id3Header.HeaderSize <= head.Length && Id3Header.TryParse(head[at..], out Id3Header h) && at + h.TotalLength < int.MaxValue / 2)
        {
            at += (int)h.TotalLength;
        }

        if (at < head.Length && Behind(head[at..]) is "flac" or "wav" or "ogg")
        {
            return SniffResult.None;
        }

        return basic;
    }

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        long position = 0;
        Span<byte> buffer = stackalloc byte[Id3Header.HeaderSize]; // one tag header at a time
        Span<byte> after = stackalloc byte[16]; // enough to recognise the audio behind the tags
        while (source.Read(position, buffer, cancellationToken) == buffer.Length && Id3Header.TryParse(buffer, out Id3Header h))
        {
            position += h.TotalLength;
        }

        int got = source.Read(position, after, cancellationToken);
        return Behind(after[..got]) switch
        {
            "mpeg" => MpegAudioDemuxer.Open(source, options, cancellationToken),
            "adts" => AdtsDemuxer.Open(source, options, cancellationToken),
            "ac3" => Ac3Demuxer.Open(source, options, cancellationToken),
            // A tag longer than the sniffed head hides these from their own sniffers, so the file arrives here; their readers skip the tag themselves.
            "flac" => Flac.FlacDemuxer.Open(source, options, cancellationToken),
            "ogg" => Ogg.OggDemuxer.Open(source, options, cancellationToken),
            var other => throw new UnsupportedMediaException($"The audio behind the ID3v2 tag ({other}) is not a format this reader opens."),
        };
    }

    /// <inheritdoc />
    public async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        WindowedSource window = new(source);
        await window.FetchAsync(0, WindowedSource.WindowBytes, cancellationToken).ConfigureAwait(false);
        long position = 0;
        while (true)
        {
            byte[] header = await AudioStreamAsyncDemuxer.PeekAsync(window, position, Id3Header.HeaderSize, cancellationToken).ConfigureAwait(false);
            if (header.Length != Id3Header.HeaderSize || !Id3Header.TryParse(header, out Id3Header h))
            {
                break;
            }

            position += h.TotalLength;
        }

        byte[] after = await AudioStreamAsyncDemuxer.PeekAsync(window, position, 16, cancellationToken).ConfigureAwait(false);
        int got = after.Length;
        Func<IMediaSource, MediaOpenOptions, CancellationToken, AudioStreamDemuxer> open = Behind(after.AsSpan(0, got)) switch
        {
            "mpeg" => (s, o, ct) => MpegAudioDemuxer.Open(s, o, ct),
            "adts" => (s, o, ct) => AdtsDemuxer.Open(s, o, ct),
            "ac3" => (s, o, ct) => Ac3Demuxer.Open(s, o, ct),
            var other => throw new UnsupportedMediaException($"The audio behind the ID3v2 tag ({other}) is not a format this reader opens."),
        };
        return await AudioStreamAsyncDemuxer.OpenAsync(source, options, open, cancellationToken, window).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        StreamBuffer input = new(source, options.LeaveOpen ? null : source) { Cancellation = cancellationToken };
        try
        {
            // The tags are looked past in the buffer; a tag larger than the buffer cannot be looked past on a source that cannot seek.
            int at = 0;
            while (true)
            {
                ReadOnlySpan<byte> head = input.Peek(at + Id3Header.HeaderSize);
                if (head.Length < at + Id3Header.HeaderSize || !Id3Header.TryParse(head[at..], out Id3Header h))
                {
                    break;
                }

                at += (int)Math.Min(h.TotalLength, int.MaxValue / 2);
                if (at > 200 * 1024)
                {
                    throw new UnsupportedMediaException("The ID3v2 tag is too large to read past on a source that cannot seek.");
                }
            }

            ReadOnlySpan<byte> peek = input.Peek(at + 16);
            string kind = at <= peek.Length ? Behind(peek[at..]) : "unknown";
            return kind switch
            {
                "mpeg" => MpegAudioDemuxer.OpenBuffered(input, null, options),
                "adts" => AdtsDemuxer.OpenBuffered(input, null, options),
                "ac3" => Ac3Demuxer.OpenBuffered(input, null, options),
                _ => throw new UnsupportedMediaException($"The audio behind the ID3v2 tag ({kind}) is not a format this reader opens."),
            };
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    private static string Behind(ReadOnlySpan<byte> audio)
    {
        if (audio.Length >= 4 && audio.StartsWith("fLaC"u8))
        {
            return "flac";
        }

        if (new AdtsSniffer().Sniff(audio).Confidence != SniffConfidence.None)
        {
            return "adts";
        }

        if (audio.Length >= 8 && audio[0] == 0x0B && audio[1] == 0x77 && Ac3SyncFrame.TryParse(audio, out _))
        {
            return "ac3";
        }

        if (new MpegAudioSniffer().Sniff(audio).Confidence != SniffConfidence.None)
        {
            return "mpeg";
        }

        return audio.Length >= 4 && audio.StartsWith("RIFF"u8) ? "wav" : audio.Length >= 4 && audio.StartsWith("OggS"u8) ? "ogg" : "unknown";
    }
}
