using System.Buffers;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Codecs.Ac3;
using RecursiveAI.OneMedia.Codecs.Amr;
using RecursiveAI.OneMedia.Codecs.MpegAudio;

namespace RecursiveAI.OneMedia.AudioStreams;

/// <summary>
/// Reads an MPEG audio elementary stream: MP3 (Layer III) and MP2/MP1, MPEG-1, MPEG-2 and MPEG-2.5, constant, variable and free-format bit
/// rates. A Xing, Info or VBRI header in the first frame is not a packet; it supplies the frame count (so the duration costs no scan) and, for
/// the LAME extension, the encoder delay and padding (ISO/IEC 11172-3 §2.4; the tags as described publicly by their authors).
/// </summary>
public sealed class MpegAudioDemuxer : AudioStreamDemuxer
{
    // The delay of the reference MP3 decoder: 528 samples plus one, as LAME's documentation of gapless playback states it.
    private const int DecoderDelay = 529;
    private const int FreeFormatScan = 10 * 1024;
    private int _freeBase;

    private MpegAudioDemuxer(StreamBuffer input, IMediaSource? media, MediaOpenOptions options, DefectLog defects) : base(input, media, options, defects)
    {
    }

    /// <summary>Opens a seekable source. Reads the start of the audio (16 KiB) and a few bytes at the end to find trailing tags; no more.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Observed while reading.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">No MPEG audio frame is found.</exception>
    public static MpegAudioDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        StreamBuffer input = new(source, options.LeaveOpen ? null : source) { Cancellation = cancellationToken };
        return Create(new MpegAudioDemuxer(input, source, options, options.Defects ?? new DefectLog(options.Limits)));
    }

    /// <summary>Opens a forward-only source. The duration is known only when the first frame carries a Xing or VBRI header; trailing tags are skipped as they are met.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Observed while reading.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">No MPEG audio frame is found.</exception>
    public static MpegAudioDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        StreamBuffer input = new(source, options.LeaveOpen ? null : source) { Cancellation = cancellationToken };
        return Create(new MpegAudioDemuxer(input, null, options, options.Defects ?? new DefectLog(options.Limits)));
    }

    internal static MpegAudioDemuxer OpenBuffered(StreamBuffer input, IMediaSource? media, MediaOpenOptions options) =>
        Create(new MpegAudioDemuxer(input, media, options, options.Defects ?? new DefectLog(options.Limits)));

    private protected override MediaFormatId FormatId => MediaFormatId.MpegAudio;

    private protected override int MinHeaderBytes => 4;

    private protected override int NextCandidate(ReadOnlySpan<byte> data, int from) => from >= data.Length ? -1 : data[from..].IndexOf((byte)0xFF) is var i and >= 0 ? from + i : -1;

    private protected override ParseStatus Parse(ReadOnlySpan<byte> data, bool atEnd, out AudioUnit unit)
    {
        unit = default;
        if (data.Length < 4)
        {
            unit.NeedBytes = 4;
            return atEnd ? ParseStatus.Invalid : ParseStatus.NeedMore;
        }

        if (!MpegAudioFrameHeader.TryParse(data, out MpegAudioFrameHeader h))
        {
            return ParseStatus.Invalid;
        }

        int length = h.FrameLength;
        if (h.BitRate == 0)
        {
            length = FreeFormatLength(data, h, atEnd);
            if (length <= 0)
            {
                return ParseStatus.Invalid;
            }
        }

        unit = new AudioUnit { Length = length, Samples = h.SamplesPerFrame, Signature = (h.Version << 28) | (h.Layer << 24) | h.SampleRate };
        return ParseStatus.Ok;
    }

    // Free format (bit rate index 0, ISO/IEC 11172-3 §2.4.2.3): the frame length is not in the header, so it is the distance to the next header
    // with the same stable fields. The distance is found by looking ahead; a length that has been confirmed once is tried first on later frames.
    private int FreeFormatLength(ReadOnlySpan<byte> data, in MpegAudioFrameHeader h, bool atEnd)
    {
        int slot = h.Layer == 1 ? 4 : 1;
        int pad = h.Padding ? slot : 0;
        if (_freeBase > 0)
        {
            int expected = _freeBase + pad;
            if (expected + 4 <= data.Length && SameStream(data, data[expected..]))
            {
                return expected;
            }
        }

        int limit = Math.Min(data.Length - 4, FreeFormatScan);
        for (int j = 8; j <= limit; j++)
        {
            if (data[j] != 0xFF || !SameStream(data, data[j..]))
            {
                continue;
            }

            // Confirmed when the header one frame further on lines up too, or the data ends before it.
            int baseLength = j - pad;
            int next = j + baseLength + ((data[j + 2] & 2) != 0 ? slot : 0);
            if (next + 4 > data.Length ? atEnd : SameStream(data, data[next..]))
            {
                _freeBase = baseLength;
                return j;
            }
        }

        return atEnd ? data.Length : -1;
    }

    private static bool SameStream(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        b.Length >= 4 && b[0] == 0xFF && (b[1] & 0xFE) == (a[1] & 0xFE) && (b[2] & 0xFC) == (a[2] & 0xFC) && (b[3] & 0xC0) == (a[3] & 0xC0);

    private protected override TrackSetup Describe(ReadOnlySpan<byte> firstUnit, in AudioUnit unit, UnitStatistics statistics, long audioBytes)
    {
        if (!MpegAudioFrameHeader.TryParse(firstUnit, out MpegAudioFrameHeader h))
        {
            throw new MediaFormatException("The first frame header cannot be read again.");
        }

        CodecParameters p = h.Describe();
        long total = -1;
        bool exact = false;
        long priming = 0, padding = 0;
        long bitRate = h.BitRate;
        string? encoder = null;
        bool skip = false;
        CodecConfiguration config = default;
        if (MpegAudioVbrHeader.TryParse(firstUnit, out MpegAudioVbrHeader vbr))
        {
            skip = true;
            config = new CodecConfiguration(firstUnit.ToArray(), "mpeg-audio-info-frame");
            if (vbr.FrameCount >= 0)
            {
                total = vbr.FrameCount * h.SamplesPerFrame;
                exact = true;
                if (vbr.ByteCount > 0 && vbr.FrameCount > 0)
                {
                    bitRate = vbr.ByteCount * 8 * h.SampleRate / total;
                }
            }
            else if (audioBytes > 0 && statistics.Bytes > 0)
            {
                total = (audioBytes - unit.Length) * statistics.Samples / statistics.Bytes;
            }

            if (vbr.EncoderDelay >= 0)
            {
                // LAME documentation: the decoder adds 528 + 1 samples of its own to the encoder's delay at the start, and the padding the encoder
                // states includes those at the end.
                priming = vbr.EncoderDelay + DecoderDelay;
                padding = Math.Max(0, vbr.EncoderPadding - DecoderDelay);
                if (total >= 0 && priming + padding > total)
                {
                    priming = padding = 0;
                }
            }

            encoder = vbr.EncoderVersion.Length > 0 ? vbr.EncoderVersion : null;
        }
        else if (audioBytes > 0 && statistics.Bytes > 0)
        {
            total = audioBytes * statistics.Samples / statistics.Bytes;
        }

        return new TrackSetup
        {
            Codec = p.Codec,
            Configuration = config,
            SampleRate = h.SampleRate,
            Channels = h.Channels,
            BitRate = bitRate,
            TotalSamples = total,
            Exact = exact,
            PrimingSamples = priming,
            EndPaddingSamples = padding,
            SkipFirstUnit = skip,
            Encoder = encoder,
        };
    }
}

/// <summary>Reads AAC in ADTS framing (ISO/IEC 13818-7 §6.2, ISO/IEC 14496-3 §1.A.2). One packet per ADTS frame, header included, as the format stores it.</summary>
public sealed class AdtsDemuxer : AudioStreamDemuxer
{
    private AdtsDemuxer(StreamBuffer input, IMediaSource? media, MediaOpenOptions options, DefectLog defects) : base(input, media, options, defects)
    {
    }

    /// <summary>Opens a seekable source.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Observed while reading.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">No ADTS frame is found.</exception>
    public static AdtsDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        StreamBuffer input = new(source, options.LeaveOpen ? null : source) { Cancellation = cancellationToken };
        return Create(new AdtsDemuxer(input, source, options, options.Defects ?? new DefectLog(options.Limits)));
    }

    /// <summary>Opens a forward-only source. The duration is unknown (ADTS states none).</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Observed while reading.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">No ADTS frame is found.</exception>
    public static AdtsDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        StreamBuffer input = new(source, options.LeaveOpen ? null : source) { Cancellation = cancellationToken };
        return Create(new AdtsDemuxer(input, null, options, options.Defects ?? new DefectLog(options.Limits)));
    }

    internal static AdtsDemuxer OpenBuffered(StreamBuffer input, IMediaSource? media, MediaOpenOptions options) =>
        Create(new AdtsDemuxer(input, media, options, options.Defects ?? new DefectLog(options.Limits)));

    private protected override MediaFormatId FormatId => MediaFormatId.Adts;

    private protected override int MinHeaderBytes => 7;

    private protected override int NextCandidate(ReadOnlySpan<byte> data, int from) => from >= data.Length ? -1 : data[from..].IndexOf((byte)0xFF) is var i and >= 0 ? from + i : -1;

    private protected override ParseStatus Parse(ReadOnlySpan<byte> data, bool atEnd, out AudioUnit unit)
    {
        unit = default;
        if (data.Length < 7)
        {
            unit.NeedBytes = 7;
            return atEnd ? ParseStatus.Invalid : ParseStatus.NeedMore;
        }

        if (!AdtsHeader.TryParse(data, out AdtsHeader h))
        {
            return ParseStatus.Invalid;
        }

        unit = new AudioUnit { Length = h.FrameLength, Samples = 1024 * h.RawDataBlocks, Signature = (h.SamplingFrequencyIndex << 8) | h.ChannelConfiguration };
        return ParseStatus.Ok;
    }

    private protected override TrackSetup Describe(ReadOnlySpan<byte> firstUnit, in AudioUnit unit, UnitStatistics statistics, long audioBytes)
    {
        if (!AdtsHeader.TryParse(firstUnit, out AdtsHeader h))
        {
            throw new MediaFormatException("The first frame header cannot be read again.");
        }

        ArrayBufferWriter<byte> asc = new(8);
        int channels = h.ChannelConfiguration;
        CodecConfiguration config = default;
        if (firstUnit.Length >= h.FrameLength && AdtsRepackager.TryCreateConfig(firstUnit, asc))
        {
            config = new CodecConfiguration(asc.WrittenMemory.ToArray(), "asc");
            if (AudioSpecificConfig.TryParse(asc.WrittenSpan, out AudioSpecificConfig c) && c.Channels > 0)
            {
                channels = c.Channels;
            }
        }
        else if (channels == 7)
        {
            channels = 8; // channel_configuration 7 is 7.1 (ISO/IEC 14496-3 Table 1.19)
        }

        long total = audioBytes > 0 && statistics.Bytes > 0 ? audioBytes * statistics.Samples / statistics.Bytes : -1;
        long bitRate = statistics.Samples > 0 ? statistics.Bytes * 8 * h.SamplingFrequency / statistics.Samples : 0;
        return new TrackSetup
        {
            Codec = CodecId.Aac,
            Configuration = config,
            SampleRate = h.SamplingFrequency,
            Channels = channels,
            BitRate = bitRate,
            TotalSamples = total,
        };
    }
}

/// <summary>
/// Reads AC-3 and E-AC-3 elementary streams (ETSI TS 102 366 §4.3 and Annex E): one packet per syncframe, with an E-AC-3 independent
/// frame joined to the dependent frames and extra substreams that follow it, so a packet is one access unit.
/// </summary>
public sealed class Ac3Demuxer : AudioStreamDemuxer
{
    private Ac3Demuxer(StreamBuffer input, IMediaSource? media, MediaOpenOptions options, DefectLog defects) : base(input, media, options, defects)
    {
    }

    /// <summary>Opens a seekable source.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Observed while reading.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">No AC-3 or E-AC-3 frame is found.</exception>
    public static Ac3Demuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        StreamBuffer input = new(source, options.LeaveOpen ? null : source) { Cancellation = cancellationToken };
        return Create(new Ac3Demuxer(input, source, options, options.Defects ?? new DefectLog(options.Limits)));
    }

    /// <summary>Opens a forward-only source. The duration is unknown.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Observed while reading.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">No AC-3 or E-AC-3 frame is found.</exception>
    public static Ac3Demuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        StreamBuffer input = new(source, options.LeaveOpen ? null : source) { Cancellation = cancellationToken };
        return Create(new Ac3Demuxer(input, null, options, options.Defects ?? new DefectLog(options.Limits)));
    }

    internal static Ac3Demuxer OpenBuffered(StreamBuffer input, IMediaSource? media, MediaOpenOptions options) =>
        Create(new Ac3Demuxer(input, media, options, options.Defects ?? new DefectLog(options.Limits)));

    private protected override MediaFormatId FormatId => MediaFormatId.Ac3;

    private protected override int MinHeaderBytes => 8;

    private protected override int NextCandidate(ReadOnlySpan<byte> data, int from) => from >= data.Length ? -1 : data[from..].IndexOf((byte)0x0B) is var i and >= 0 ? from + i : -1;

    private protected override ParseStatus Parse(ReadOnlySpan<byte> data, bool atEnd, out AudioUnit unit)
    {
        unit = default;
        if (data.Length < 8)
        {
            unit.NeedBytes = 8;
            return atEnd ? ParseStatus.Invalid : ParseStatus.NeedMore;
        }

        if (!Ac3SyncFrame.TryParse(data, out Ac3SyncFrame f) || (f.IsEac3 && f.StreamType == 1))
        {
            return ParseStatus.Invalid;
        }

        int length = f.FrameSize;
        if (f.IsEac3)
        {
            // ETSI TS 102 366 Annex E: the dependent frames (strmtyp 1) and the frames of further substreams that follow an independent frame belong to its access unit.
            while (true)
            {
                if (data.Length < length + 8)
                {
                    if (atEnd)
                    {
                        break;
                    }

                    unit.NeedBytes = length + 8;
                    return ParseStatus.NeedMore;
                }

                if (!Ac3SyncFrame.TryParse(data[length..], out Ac3SyncFrame d) || !d.IsEac3 || (d.StreamType != 1 && d.SubstreamId == 0))
                {
                    break;
                }

                length += d.FrameSize;
            }
        }

        unit = new AudioUnit { Length = length, Samples = f.SamplesPerFrame, Signature = ((f.IsEac3 ? 1 : 0) << 24) | f.SampleRate };
        return ParseStatus.Ok;
    }

    private protected override TrackSetup Describe(ReadOnlySpan<byte> firstUnit, in AudioUnit unit, UnitStatistics statistics, long audioBytes)
    {
        if (!Ac3SyncFrame.TryParse(firstUnit, out Ac3SyncFrame f))
        {
            throw new MediaFormatException("The first frame header cannot be read again.");
        }

        ArrayBufferWriter<byte> config = new(8);
        string format;
        if (f.IsEac3)
        {
            f.TryWriteDec3(f.BitRate / 1000, config);
            format = "dec3";
        }
        else
        {
            f.TryWriteDac3(config);
            format = "dac3";
        }

        long total = audioBytes > 0 && statistics.Bytes > 0 ? audioBytes * statistics.Samples / statistics.Bytes : -1;
        return new TrackSetup
        {
            Codec = f.IsEac3 ? CodecId.Eac3 : CodecId.Ac3,
            CodecTag = f.IsEac3 ? "ec-3" : "ac-3",
            Configuration = new CodecConfiguration(config.WrittenMemory.ToArray(), format),
            SampleRate = f.SampleRate,
            Channels = f.Channels,
            BitRate = f.BitRate,
            TotalSamples = total,
        };
    }
}

/// <summary>
/// Reads AMR-NB and AMR-WB storage files (RFC 4867 §5): the <c>#!AMR\n</c> or <c>#!AMR-WB\n</c> magic (and the multi-channel magics with their
/// channel field), then frame-blocks, each frame headed by a table-of-contents byte. One packet per frame-block; for mono files that is one frame.
/// </summary>
public sealed class AmrDemuxer : AudioStreamDemuxer
{
    private bool _wideband;
    private int _channels = 1;

    private AmrDemuxer(StreamBuffer input, IMediaSource? media, MediaOpenOptions options, DefectLog defects) : base(input, media, options, defects)
    {
    }

    /// <summary>Opens a seekable source.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Observed while reading.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The AMR magic is missing or no frame follows it.</exception>
    public static AmrDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        StreamBuffer input = new(source, options.LeaveOpen ? null : source) { Cancellation = cancellationToken };
        return Create(new AmrDemuxer(input, source, options, options.Defects ?? new DefectLog(options.Limits)));
    }

    /// <summary>Opens a forward-only source. The duration is unknown.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Observed while reading.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The AMR magic is missing or no frame follows it.</exception>
    public static AmrDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        StreamBuffer input = new(source, options.LeaveOpen ? null : source) { Cancellation = cancellationToken };
        return Create(new AmrDemuxer(input, null, options, options.Defects ?? new DefectLog(options.Limits)));
    }

    internal static AmrDemuxer OpenBuffered(StreamBuffer input, IMediaSource? media, MediaOpenOptions options) =>
        Create(new AmrDemuxer(input, media, options, options.Defects ?? new DefectLog(options.Limits)));

    private protected override MediaFormatId FormatId => MediaFormatId.Amr;

    private protected override bool AllowsTags => false;

    private protected override int MinHeaderBytes => 1;

    private protected override int NextCandidate(ReadOnlySpan<byte> data, int from) => from >= data.Length ? -1 : from;

    private protected override bool ReadStreamHeader(StreamBuffer input)
    {
        ReadOnlySpan<byte> head = input.Peek(20);
        if (AmrFrame.TryReadMagic(head, out bool wb, out int length))
        {
            _wideband = wb;
            input.Consume(length);
            return true;
        }

        // RFC 4867 §5.2: the multi-channel magics are followed by a 32-bit field whose low four bits are the channel count.
        ReadOnlySpan<byte> narrow = "#!AMR_MC1.0\n"u8;
        ReadOnlySpan<byte> wide = "#!AMR-WB_MC1.0\n"u8;
        int magic = head.StartsWith(wide) ? wide.Length : head.StartsWith(narrow) ? narrow.Length : 0;
        if (magic == 0 || head.Length < magic + 4)
        {
            return false;
        }

        _wideband = magic == wide.Length;
        _channels = head[magic + 3] & 0xF;
        input.Consume(magic + 4);
        if (_channels == 0)
        {
            Defects.Add(DefectKind.InvalidValue, DefectSeverity.Error, input.Position - 4, "The multi-channel AMR header declares zero channels.", "amr.channels");
            return false;
        }

        return true;
    }

    private protected override ParseStatus Parse(ReadOnlySpan<byte> data, bool atEnd, out AudioUnit unit)
    {
        unit = default;
        int p = 0;
        for (int c = 0; c < _channels; c++)
        {
            if (p >= data.Length)
            {
                unit.NeedBytes = p + 1;
                if (!atEnd)
                {
                    return ParseStatus.NeedMore;
                }

                // The data ends inside a frame-block: what is there is delivered as a truncated packet.
                unit = c > 0 ? new AudioUnit { Length = p + 1, Samples = AmrFrame.SamplesPerFrame(_wideband) } : default;
                return c > 0 ? ParseStatus.Ok : ParseStatus.Invalid;
            }

            // RFC 4867 §5.3: P(1) FT(4) Q(1) P P; the padding bits are zero when written.
            byte toc = data[p];
            if ((toc & 0x83) != 0 || !AmrFrame.TryGetFrameSize(toc, _wideband, out int size))
            {
                return ParseStatus.Invalid;
            }

            p += size;
        }

        unit = new AudioUnit { Length = p, Samples = AmrFrame.SamplesPerFrame(_wideband), Signature = 0 };
        return ParseStatus.Ok;
    }

    private protected override TrackSetup Describe(ReadOnlySpan<byte> firstUnit, in AudioUnit unit, UnitStatistics statistics, long audioBytes)
    {
        int rate = _wideband ? 16000 : 8000;
        byte[] damr = new byte[AmrSpecificBox.Size];
        // Mode set: every mode the codec has (3GPP TS 26.244 §8.4.2): narrowband modes 0 to 7 and SID, wideband 0 to 8 and SID.
        new AmrSpecificBox(0x20202020, 0, _wideband ? 0x83FF : 0x81FF, 0, 1).TryWrite(damr);
        long total = audioBytes > 0 && statistics.Bytes > 0 ? audioBytes * statistics.Samples / statistics.Bytes : -1;
        long bitRate = statistics.Samples > 0 ? statistics.Bytes * 8 * rate / statistics.Samples : 0;
        return new TrackSetup
        {
            Codec = _wideband ? CodecId.AmrWb : CodecId.AmrNb,
            CodecTag = _wideband ? "sawb" : "samr",
            Configuration = new CodecConfiguration(damr, "damr"),
            SampleRate = rate,
            Channels = _channels,
            BitRate = bitRate,
            TotalSamples = total,
        };
    }
}
