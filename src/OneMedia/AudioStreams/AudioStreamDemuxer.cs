using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.Id3;

namespace RecursiveAI.OneMedia.AudioStreams;

/// <summary>What <c>Parse</c> found at the start of some bytes.</summary>
internal enum ParseStatus
{
    /// <summary>A valid unit starts here.</summary>
    Ok,

    /// <summary>The bytes are valid so far but too few to decide; <see cref="AudioUnit.NeedBytes"/> says how many are wanted.</summary>
    NeedMore,

    /// <summary>These bytes do not start a unit.</summary>
    Invalid,
}

/// <summary>One packet's worth of an audio stream, as found by a header parse.</summary>
internal struct AudioUnit
{
    /// <summary>Bytes in the packet.</summary>
    public int Length;

    /// <summary>Samples per channel the packet holds.</summary>
    public int Samples;

    /// <summary>A value that is equal for every unit of one stream (version, layer, rate…); a candidate with another one is not a unit of this stream.</summary>
    public int Signature;

    /// <summary>For <see cref="ParseStatus.NeedMore"/>, how many bytes are needed.</summary>
    public int NeedBytes;
}

/// <summary>What a format learned from its first unit: the track's description and how to treat the unit itself.</summary>
internal sealed class TrackSetup
{
    public CodecId Codec { get; init; }

    public string? CodecTag { get; init; }

    public CodecConfiguration Configuration { get; init; }

    public int SampleRate { get; init; }

    public int Channels { get; init; }

    public long BitRate { get; init; }

    /// <summary>Total samples per channel, or -1 when the stream does not say.</summary>
    public long TotalSamples { get; init; } = -1;

    /// <summary>True when <see cref="TotalSamples"/> comes from the stream's own count, false when it is an estimate from sizes.</summary>
    public bool Exact { get; init; }

    /// <summary>Samples at the start of the decoded output that the encoder added.</summary>
    public long PrimingSamples { get; init; }

    /// <summary>Samples at the end of the decoded output that the encoder added.</summary>
    public long EndPaddingSamples { get; init; }

    /// <summary>True when the first unit carries a header of the stream (Xing, Info or VBRI) and no audio: it is not delivered as a packet.</summary>
    public bool SkipFirstUnit { get; init; }

    /// <summary>The encoder as the stream names it, or null.</summary>
    public string? Encoder { get; init; }
}

/// <summary>Running totals over the units seen in the first buffered bytes, from which a duration is estimated when the stream does not state one.</summary>
internal readonly record struct UnitStatistics(long Units, long Bytes, long Samples);

/// <summary>
/// Reads an audio elementary stream that is a plain run of frames: MPEG audio, ADTS AAC, AC-3 and E-AC-3, AMR. A leading ID3v2 tag is skipped;
/// trailing ID3v1, APE and Lyrics3 tags are located and kept out of the audio. Bytes that are not a frame are skipped and reported as a defect,
/// and reading resumes at the next frame that is followed by another one. Each packet is one frame (an E-AC-3 access unit is its independent
/// frame with the dependent frames that follow it). Works over seekable and forward-only sources.
/// </summary>
public abstract class AudioStreamDemuxer : IDemuxer, IMetadataProvider
{
    private const int MaxPeek = 12 * 1024;
    private const int CheckpointEvery = 256;
    private const int MaxStatisticsUnits = 128;

    private readonly StreamBuffer _input;
    private readonly MediaLimits _limits;
    private readonly IMediaSource? _media;
    private readonly List<long> _checkpointOffsets = new(1024);
    private readonly List<long> _checkpointSamples = new(1024);
    private readonly List<ByteRange> _headTags = [];
    private readonly List<byte[]> _headTagData = [];
    private readonly List<(string Kind, ByteRange Range)> _tailBlocks = [];
    private MediaTrack? _track;
    private TrackSetup? _setup;
    private long _samples;
    private long _unit;
    private int _signature;
    private long _firstUnitOffset;
    private long _lastUnitOffset = -1;
    private long _lastUnitSamples;
    private long _audioStart;
    private bool _enabled = true;
    private bool _truncatedReported;

    internal AudioStreamDemuxer(StreamBuffer input, IMediaSource? media, MediaOpenOptions options, DefectLog defects)
    {
        _input = input;
        _media = media;
        _limits = options.Limits;
        Defects = defects;
        Cancellation = CancellationToken.None;
    }

    private CancellationToken Cancellation { get; set; }

    /// <inheritdoc />
    public MediaFormatId Format => FormatId;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _track is null ? [] : [_track];

    /// <inheritdoc />
    public MediaTime Duration => _track?.Duration ?? MediaTime.Unknown;

    /// <inheritdoc />
    public DefectLog Defects { get; }

    /// <inheritdoc />
    public bool CanSeek => _input.CanSeek;

    /// <summary>
    /// True when <see cref="Duration"/> was estimated from the size of the audio and the length of the frames at the start (a stream with no frame
    /// count of its own), false when the stream states it. An estimate is exact for constant-bit-rate streams without padding variation and
    /// approximate otherwise; it costs no reads beyond the ones opening already made.
    /// </summary>
    public bool IsDurationEstimated => _setup is { TotalSamples: >= 0, Exact: false };

    /// <summary>Where the first byte after the leading tags is (the start of the audio), in the source.</summary>
    public long AudioStart => _audioStart;

    /// <summary>The offset of the first frame delivered as a packet.</summary>
    public long FirstFrameOffset => _firstUnitOffset;

    /// <summary>The offset where the audio ends (the start of trailing tags or the end of the data); -1 for a forward-only source.</summary>
    public long AudioEnd => _input.Limit == long.MaxValue ? -1 : _input.Limit;

    /// <summary>The encoder named by the stream (the LAME version string of an MP3, for instance), or null.</summary>
    public string? Encoder => _setup?.Encoder;

    private protected abstract MediaFormatId FormatId { get; }

    private protected virtual bool AllowsTags => true;

    private protected abstract int MinHeaderBytes { get; }

    private protected abstract int NextCandidate(ReadOnlySpan<byte> data, int from);

    private protected abstract ParseStatus Parse(ReadOnlySpan<byte> data, bool atEnd, out AudioUnit unit);

    private protected abstract TrackSetup Describe(ReadOnlySpan<byte> firstUnit, in AudioUnit unit, UnitStatistics statistics, long audioBytes);

    /// <summary>Reads a file header that precedes the first frame (the AMR magic); false when it is wrong.</summary>
    private protected virtual bool ReadStreamHeader(StreamBuffer input) => true;

    private protected virtual void Reset()
    {
    }

    internal static T Create<T>(T demuxer) where T : AudioStreamDemuxer
    {
        try
        {
            demuxer.Initialize();
            return demuxer;
        }
        catch
        {
            demuxer.Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        if (AllowsTags)
        {
            SkipLeadingTags();
        }

        _audioStart = _input.Position;
        if (!ReadStreamHeader(_input))
        {
            throw new MediaFormatException($"The data does not start with a valid {FormatId} header.") { Offset = _audioStart, Kind = DefectKind.InvalidValue };
        }

        long dataStart = _input.Position;
        if (_input.CanSeek && AllowsTags)
        {
            PeelTail(dataStart);
        }

        if (!Resync(out AudioUnit first, firstFrame: true))
        {
            throw new MediaFormatException("No audio frame was found.") { Offset = dataStart, Kind = DefectKind.MissingRequired };
        }

        _signature = first.Signature;
        ReadOnlySpan<byte> head = _input.Peek(MaxPeek);
        bool atEnd = head.Length < MaxPeek;
        UnitStatistics stats = Gather(head, atEnd);
        long audioBytes = _input.Limit != long.MaxValue ? _input.Limit - _input.Position : _input.Length >= 0 ? _input.Length - _input.Position : -1;
        _firstUnitOffset = _input.Position;
        _setup = Describe(head[..Math.Min(first.Length, head.Length)], first, stats, audioBytes);
        if (_setup.SkipFirstUnit)
        {
            _input.Consume(Math.Min(first.Length, head.Length));
            if (!Resync(out AudioUnit next, firstFrame: false) || next.Signature != _signature)
            {
                // A stream of only the header frame: no packets.
                _firstUnitOffset = _input.Position;
            }
            else
            {
                _firstUnitOffset = _input.Position;
            }
        }

        _track = BuildTrack(_setup);
    }

    private static MediaTrack BuildTrack(TrackSetup s)
    {
        long rate = Math.Max(s.SampleRate, 1);
        MediaTime duration = s.TotalSamples >= 0 ? new MediaTime(Math.Max(0, s.TotalSamples - s.PrimingSamples - s.EndPaddingSamples), rate) : MediaTime.Unknown;
        return new MediaTrack
        {
            Index = 0,
            Id = 1,
            Kind = TrackKind.Audio,
            Codec = s.Codec,
            CodecTag = s.CodecTag,
            Configuration = s.Configuration,
            Timescale = rate,
            Duration = duration,
            Edit = new TrackEditInfo(
                Priming: s.PrimingSamples > 0 ? new MediaTime(s.PrimingSamples, rate) : default,
                EndPadding: s.EndPaddingSamples > 0 ? new MediaTime(s.EndPaddingSamples, rate) : default),
            Audio = new AudioTrackInfo(s.SampleRate, s.Channels),
            BitRate = s.BitRate,
            Disposition = TrackDisposition.Default,
        };
    }

    // The ID3v2 tags (usually one) in front of the audio; a tag from a forward-only source is kept in memory so its fields can still be read.
    private void SkipLeadingTags()
    {
        while (true)
        {
            ReadOnlySpan<byte> head = _input.Peek(Id3Header.HeaderSize);
            if (!Id3Header.TryParse(head, out Id3Header h))
            {
                return;
            }

            long offset = _input.Position;
            long total = h.TotalLength;
            if (_input.Length >= 0 && offset + total > _input.Length)
            {
                Defects.Add(DefectKind.SizeExceedsFile, DefectSeverity.Error, offset, "The ID3v2 tag at the start is larger than the file.", "id3.size-exceeds-file");
                total = Math.Max(0, _input.Length - offset);
            }

            _headTags.Add(new ByteRange(offset, total));
            if (!_input.CanSeek && total <= _limits.MaxTagBytes)
            {
                byte[] bytes = new byte[total];
                int got = _input.TryTake(bytes) ? bytes.Length : 0;
                _headTagData.Add(got == bytes.Length ? bytes : []);
            }
            else
            {
                _input.Skip(total);
                _headTagData.Add([]);
            }
        }
    }

    // Finds the tags at the end of a seekable source, one from the end at a time: ID3v1, then an APE tag, then Lyrics3 (the usual order is the reverse of that in the file).
    private void PeelTail(long dataStart)
    {
        IMediaSource media = _media!;
        long end = media.Length;
        for (int round = 0; round < 6 && end > dataStart; round++)
        {
            if (Tags.Id3.Id3v1Tag.TryRead(media, end - Id3v1Tag.Length, out Id3v1Tag? v1, _limits, Defects) && v1 is not null && end - Id3v1Tag.Length >= dataStart)
            {
                long start = v1.Extended.IsNone ? v1.Range.Offset : v1.Extended.Offset;
                _tailBlocks.Add(("id3v1", new ByteRange(start, end - start)));
                end = start;
                continue;
            }

            if (end - Tags.Ape.ApeTag.BlockSize >= dataStart && Tags.Ape.ApeTag.TryReadFromFooter(media, end - Tags.Ape.ApeTag.BlockSize, out Tags.Ape.ApeTag? ape, _limits, Defects) && ape is not null && ape.Range.Offset >= dataStart)
            {
                _tailBlocks.Add(("ape", ape.Range));
                end = ape.Range.Offset;
                continue;
            }

            if (Tags.Lyrics3.Lyrics3Tag.TryReadEndingAt(media, end, out Tags.Lyrics3.Lyrics3Tag? lyrics, _limits, Defects) && lyrics is not null && lyrics.Range.Offset >= dataStart)
            {
                _tailBlocks.Add(("lyrics3", lyrics.Range));
                end = lyrics.Range.Offset;
                continue;
            }

            Span<byte> footer = stackalloc byte[Id3Header.HeaderSize]; // an ID3v2.4 footer ("3DI") ends an appended tag
            if (end - footer.Length >= dataStart && media.Read(end - footer.Length, footer, Cancellation) == footer.Length && footer.StartsWith("3DI"u8))
            {
                footer[..3].Clear();
                footer[0] = (byte)'I';
                footer[1] = (byte)'D';
                footer[2] = (byte)'3';
                if (Id3Header.TryParse(footer, out Id3Header fh))
                {
                    long start = end - Id3Header.HeaderSize - fh.BodySize - Id3Header.HeaderSize;
                    if (start >= dataStart)
                    {
                        _tailBlocks.Add(("id3v2", new ByteRange(start, end - start)));
                        end = start;
                        continue;
                    }
                }
            }

            break;
        }

        _input.Limit = end;
    }

    private UnitStatistics Gather(ReadOnlySpan<byte> head, bool atEnd)
    {
        long units = 0, bytes = 0, samples = 0;
        int p = 0;
        while (p < head.Length && units < MaxStatisticsUnits)
        {
            if (Parse(head[p..], atEnd, out AudioUnit u) != ParseStatus.Ok || u.Signature != _signature || p + u.Length > head.Length)
            {
                break;
            }

            units++;
            bytes += u.Length;
            samples += u.Samples;
            p += u.Length;
        }

        return new UnitStatistics(units, bytes, samples);
    }

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(trackIndex, 0);
        _enabled = enabled;
    }

    /// <inheritdoc />
    public bool TryReadPacket(out PacketView packet)
    {
        packet = default;
        if (!_enabled || _track is null)
        {
            return false;
        }

        long rate = _track.Timescale;
        while (true)
        {
            ReadOnlySpan<byte> head = _input.Peek(MaxPeek);
            if (head.IsEmpty)
            {
                return false;
            }

            bool atEnd = head.Length < MaxPeek;
            if (AllowsTags && TrySkipSpecial(head, atEnd))
            {
                continue;
            }

            if (Parse(head, atEnd, out AudioUnit u) == ParseStatus.Ok && u.Signature == _signature)
            {
                long offset = _input.Position;
                bool truncated = u.Length > head.Length;
                int size = truncated ? head.Length : u.Length;
                PacketAttributes flags = PacketAttributes.Keyframe;
                if (truncated)
                {
                    flags |= PacketAttributes.Corrupt;
                    if (!_truncatedReported)
                    {
                        _truncatedReported = true;
                        Defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, $"The data ends {u.Length - size} bytes before the end of the last frame; the part that exists is delivered and marked corrupt.", "audio.truncated-frame");
                    }
                }

                MediaTime pts = new(_samples, rate);
                PacketInfo info = new(0, pts, pts, new MediaTime(u.Samples, rate), flags, new ByteRange(offset, size), size);
                Advance(offset, u.Samples);
                packet = new PacketView(info, head[..size]);
                _input.Consume(size);
                return true;
            }

            if (!Resync(out _, firstFrame: false))
            {
                return false;
            }
        }
    }

    private void Advance(long offset, int samples)
    {
        if (_unit % CheckpointEvery == 0 && _unit / CheckpointEvery == _checkpointOffsets.Count)
        {
            _checkpointOffsets.Add(offset);
            _checkpointSamples.Add(_samples);
        }

        _lastUnitOffset = offset;
        _lastUnitSamples = _samples;
        _samples += samples;
        _unit++;
    }

    // Moves to the next place where a unit starts and the unit after it starts too (or the data ends); reports what it skipped.
    private bool Resync(out AudioUnit unit, bool firstFrame)
    {
        unit = default;
        long runStart = _input.Position;
        long skipped = 0;
        while (true)
        {
            ReadOnlySpan<byte> head = _input.Peek(MaxPeek);
            if (head.IsEmpty)
            {
                Report(runStart, skipped);
                return false;
            }

            bool atEnd = head.Length < MaxPeek;
            int i = 0;
            while (true)
            {
                i = NextCandidate(head, i);
                if (i < 0 || i >= head.Length)
                {
                    break;
                }

                ReadOnlySpan<byte> at = head[i..];
                if (Parse(at, atEnd, out AudioUnit u) == ParseStatus.Ok && (firstFrame || u.Signature == _signature) && Confirmed(head, i, u, atEnd))
                {
                    skipped += i;

                    _input.Consume(i);
                    Report(runStart, skipped);
                    unit = u;
                    return true;
                }

                i++;
            }

            // No unit starts in this window. Keep the last few bytes: a header may straddle the window's end.
            int keep = atEnd ? 0 : Math.Min(head.Length, MinHeaderBytes + 1);
            int drop = head.Length - keep;
            if (drop <= 0)
            {
                // The window holds nothing more to look at: the data ends here.
                skipped += head.Length;
                _input.Consume(head.Length);
                Report(runStart, skipped);
                return false;
            }

            skipped += drop;
            _input.Consume(drop);
        }
    }

    // A candidate is accepted when another unit of the same stream follows it, or the data ends right after it (or inside what would be the next header).
    private bool Confirmed(ReadOnlySpan<byte> head, int at, in AudioUnit u, bool atEnd)
    {
        if (u.Length > MaxPeek - MinHeaderBytes - 16)
        {
            return false;
        }

        int next = at + u.Length;
        if (next + MinHeaderBytes > head.Length)
        {
            return atEnd;
        }

        ParseStatus s = Parse(head[next..], atEnd, out AudioUnit n);
        return s == ParseStatus.Ok ? n.Signature == u.Signature : (atEnd && s == ParseStatus.NeedMore) || (next >= head.Length && atEnd);
    }

    private void Report(long start, long skipped)
    {
        if (skipped > 0)
        {
            Defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Warning, start, $"{skipped} bytes that do not form a frame were skipped.", "audio.junk");
        }
    }

    // Tags inside the stream: another ID3v2 tag (a spliced file), and, in a source that cannot say where the audio ends, the trailing tags.
    private bool TrySkipSpecial(ReadOnlySpan<byte> head, bool atEnd)
    {
        switch (head[0])
        {
            case (byte)'I' when Id3Header.TryParse(head, out Id3Header h):
                _headTags.Add(new ByteRange(_input.Position, h.TotalLength));
                _headTagData.Add([]);
                Defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Info, _input.Position, "An ID3v2 tag sits between frames and is skipped.", "audio.inner-id3");
                _input.Skip(h.TotalLength);
                return true;
            case (byte)'T' when !_input.CanSeek && atEnd && head.Length == Id3v1Tag.Length && head.StartsWith("TAG"u8):
                _tailBlocks.Add(("id3v1", new ByteRange(_input.Position, head.Length)));
                _input.Consume(head.Length);
                return true;
            case (byte)'A' when !_input.CanSeek && Tags.Ape.ApeTag.TryParseBlock(head, out _, out long size, out _, out Tags.Ape.ApeTagAttributes flags) && (flags & Tags.Ape.ApeTagAttributes.IsHeader) != 0:
                _tailBlocks.Add(("ape", new ByteRange(_input.Position, Tags.Ape.ApeTag.BlockSize + size)));
                _input.Skip(Tags.Ape.ApeTag.BlockSize + size);
                return true;
            case (byte)'L' when !_input.CanSeek && head.StartsWith("LYRICSBEGIN"u8):
                _tailBlocks.Add(("lyrics3", new ByteRange(_input.Position, 0)));
                _input.Skip(long.MaxValue / 2);
                return true;
        }

        return false;
    }

    /// <summary>
    /// Moves to the frame that holds <paramref name="target"/> (previous) or the first frame that starts at or after it (next). The stream has no
    /// index, so the frame headers are walked from the nearest earlier frame the reader has already passed (every 256th is remembered) or from
    /// the start; no frame is decoded.
    /// </summary>
    /// <param name="target">The wanted time.</param>
    /// <param name="mode">Whether to land before or after the target.</param>
    /// <returns>The time of the frame reached; <see cref="MediaTime.Unknown"/> when nothing follows the target.</returns>
    /// <exception cref="NotSupportedException">The source is forward-only.</exception>
    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe)
    {
        if (!_input.CanSeek || _track is null)
        {
            throw new NotSupportedException("The source cannot seek.");
        }

        long rate = _track.Timescale;
        long goal = target.IsKnown ? Math.Max(0, target.Rescale(rate, MediaRounding.Floor).Ticks) : 0;
        int cp = _checkpointSamples.Count - 1;
        while (cp >= 0 && _checkpointSamples[cp] > goal)
        {
            cp--;
        }

        long offset = cp >= 0 ? _checkpointOffsets[cp] : _firstUnitOffset;
        long samples = cp >= 0 ? _checkpointSamples[cp] : 0;
        long unit = cp >= 0 ? (long)cp * CheckpointEvery : 0;
        _input.Seek(offset);
        _samples = samples;
        _unit = unit;
        long lastOffset = -1, lastSamples = 0;
        while (true)
        {
            ReadOnlySpan<byte> head = _input.Peek(MaxPeek);
            if (head.IsEmpty)
            {
                break;
            }

            bool atEnd = head.Length < MaxPeek;
            if (Parse(head, atEnd, out AudioUnit u) == ParseStatus.Ok && u.Signature == _signature)
            {
                long here = _input.Position;
                bool lands = mode == SeekMode.PreviousKeyframe ? _samples + u.Samples > goal : _samples >= goal;
                if (lands)
                {
                    _input.Seek(here);
                    return new MediaTime(_samples, rate);
                }

                lastOffset = here;
                lastSamples = _samples;
                Advance(here, u.Samples);
                _input.Consume(Math.Min(u.Length, head.Length));
                continue;
            }

            if (AllowsTags && TrySkipSpecial(head, atEnd))
            {
                continue;
            }

            if (!Resync(out _, firstFrame: false))
            {
                break;
            }
        }

        if (mode == SeekMode.PreviousKeyframe && lastOffset >= 0)
        {
            _input.Seek(lastOffset);
            _samples = lastSamples;
            return new MediaTime(lastSamples, rate);
        }

        return MediaTime.Unknown;
    }

    /// <summary>
    /// Reads the tags: ID3v2 at the start, and ID3v1, APE and Lyrics3 at the end (found when the stream was opened), the basic facts and the
    /// chapters of ID3v2 <c>CHAP</c> frames. A forward-only source gives the ID3v2 tag it passed at the start and no trailing tags.
    /// </summary>
    /// <param name="cancellationToken">Observed between tags.</param>
    /// <returns>The metadata; it reads large values from the source, so it is valid while the stream is open.</returns>
    public MediaMetadata ReadMetadata(CancellationToken cancellationToken = default) =>
        AudioStreamMetadata.Read(_media, _headTags, _headTagData, _tailBlocks, _track, _setup, _limits, Defects, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        _input.Dispose();
        GC.SuppressFinalize(this);
    }

    internal void SetCancellation(CancellationToken token)
    {
        Cancellation = token;
        _input.Cancellation = token;
    }
}
