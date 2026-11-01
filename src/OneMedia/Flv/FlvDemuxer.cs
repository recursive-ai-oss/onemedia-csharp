using System.Buffers.Binary;
using System.Globalization;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Codecs.MpegAudio;
using RecursiveAI.OneMedia.Input;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Flv;

/// <summary>The script data properties of a file as tags of system <see cref="TagSystems.Flv"/>.</summary>
public sealed class FlvScriptTags : ITagContainer
{
    internal FlvScriptTags(ByteRange range, IReadOnlyList<NativeTag> tags, string name)
    {
        Range = range;
        Tags = tags;
        Name = name;
    }

    /// <inheritdoc />
    public string System => TagSystems.Flv;

    /// <inheritdoc />
    public long TrackId => 0;

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }

    /// <summary>The script tag's name, usually <c>onMetaData</c>.</summary>
    public string Name { get; }
}

/// <summary>
/// Reads Flash Video (Adobe Flash Video File Format Specification 10.1) and Enhanced RTMP FourCC video and audio, one packet per audio or video tag.
/// The reader works on forward-only sources.
/// </summary>
/// <remarks>
/// Tracks are found by reading the first tags (at most 256 tags or 8 MiB, ending early once every stream the header announces is configured);
/// with a seekable source the reader then goes back to the first tag, with a forward-only one the tags already read are delivered first.
/// A tag's timestamp is the decode time in milliseconds; AVC and HEVC composition time offsets give the presentation time.
/// Packets carry the bytes after the tag's own headers (for H.264 the length-prefixed NAL units, as in an MP4 sample); sequence headers become
/// the track's configuration and are not packets. The script data of the leading tags are read as tags of system <see cref="TagSystems.Flv"/>.
/// The duration is the <c>duration</c> property of <c>onMetaData</c> when it is positive; otherwise a seekable source gives the timestamp of the last tag, read from the file's last bytes.
/// Seeking uses the <c>keyframes</c> index of <c>onMetaData</c>; without one the tags are walked (headers only) once to build it.
/// </remarks>
public sealed class FlvDemuxer : IDemuxer, IMetadataProvider
{
    private const int DiscoveryTags = 256;
    private const int DiscoveryBytes = 8 * 1024 * 1024;
    private const int TagHeaderSize = 11;

    private sealed class StreamState
    {
        public int Track = -1;
        public int Format;       // the SoundFormat or CodecID (or an Enhanced FourCC, below)
        public uint FourCc;
        public bool ConfigSeen;
        public bool Ready;
    }

    private struct Pending
    {
        public PacketInfo Info;
        public byte[] Data;
    }

    private readonly SequentialInput _input;
    private readonly IMediaSource? _random;
    private readonly IDisposable? _owned;
    private readonly MediaLimits _limits;
    private readonly List<MediaTrack> _tracks = [];
    private readonly List<AmfProperty> _meta = [];
    private readonly List<(long Offset, int Length)> _scriptTags = [];
    private readonly List<(long Offset, byte[] Data)> _scriptCopies = [];
    private readonly List<Pending> _pending = [];
    private readonly StreamState _video = new();
    private readonly StreamState _audio = new();
    private readonly List<(long Time, long Offset)> _keyframes = [];
    private byte[] _payload = new byte[4096];
    private int _pendingHead;
    private long _firstTag;
    private bool _prevSizeReported;
    private bool _ended;
    private bool _disposed;
    private bool _indexBuilt;
    private MediaTime _duration = MediaTime.Unknown;
    private long _currentTagEnd;
    private bool _hasAudioFlag;
    private bool _hasVideoFlag;

    private FlvDemuxer(SequentialInput input, IMediaSource? random, IDisposable? owned, MediaOpenOptions options, DefectLog defects)
    {
        _input = input;
        _random = random;
        _owned = owned;
        _limits = options.Limits;
        Defects = defects;
    }

    /// <summary>Opens a seekable source.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the leading tags are read.</param>
    /// <returns>The demuxer, positioned at the first tag.</returns>
    /// <exception cref="MediaFormatException">The data does not start with an FLV header.</exception>
    public static FlvDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        return Create(new SequentialInput(source), source, options.LeaveOpen ? null : source, options, cancellationToken);
    }

    /// <summary>Opens a source that can only be read once, front to back.</summary>
    /// <param name="source">The source, at the start of the FLV data.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the leading tags are read.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The data does not start with an FLV header.</exception>
    public static FlvDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        return Create(new SequentialInput(source), null, options.LeaveOpen ? null : source, options, cancellationToken);
    }

    internal static FlvDemuxer Open(IMediaSource source, ReadOnlySpan<byte> head, MediaOpenOptions options, CancellationToken cancellationToken)
    {
        SequentialInput input = new(source);
        input.Seed(head);
        return Create(input, source, options.LeaveOpen ? null : source, options, cancellationToken);
    }

    private static FlvDemuxer Create(SequentialInput input, IMediaSource? random, IDisposable? owned, MediaOpenOptions options, CancellationToken ct)
    {
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        FlvDemuxer d = new(input, random, owned, options, defects);
        try
        {
            d.ReadHeader(ct);
            d.Discover(ct);
            d.ReadTailDuration(ct);
        }
        catch
        {
            d.Dispose();
            throw;
        }

        return d;
    }

    /// <inheritdoc />
    public MediaFormatId Format => MediaFormatId.Flv;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _tracks;

    /// <summary>The length from <c>onMetaData</c>, or from the last tag of a seekable source; unknown otherwise.</summary>
    public MediaTime Duration => _duration;

    /// <inheritdoc />
    public DefectLog Defects { get; }

    /// <inheritdoc />
    public bool CanSeek => _random is not null;

    /// <summary>The FLV version from the header (1 for every file in use).</summary>
    public int Version { get; private set; }

    /// <summary>Whether the header announces audio tags.</summary>
    public bool HeaderHasAudio => _hasAudioFlag;

    /// <summary>Whether the header announces video tags.</summary>
    public bool HeaderHasVideo => _hasVideoFlag;

    /// <summary>Offset of the first tag's previous-tag-size field, as the header's DataOffset says.</summary>
    public long DataOffset { get; private set; }

    /// <summary>The flattened properties of the first <c>onMetaData</c> tag read at open, in file order.</summary>
    public IReadOnlyList<AmfProperty> MetaData => _meta;

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled)
    {
        // Tracks are skipped by the caller ignoring their packets; FLV carries at most one audio and one video track and is read tag by tag anyway.
        if (trackIndex >= 0 && trackIndex < _tracks.Count)
        {
            _disabled[trackIndex] = !enabled;
        }
    }

    private readonly bool[] _disabled = new bool[2];

    // ---- header and discovery ------------------------------------------------------------------------------------

    // FLV spec 10.1, "The FLV header": "FLV", version, type flags (bit 2 audio, bit 0 video), DataOffset (32 bits), then PreviousTagSize0.
    private void ReadHeader(CancellationToken ct)
    {
        Span<byte> h = stackalloc byte[9];
        if (_input.Read(h, ct) < 9 || h[0] != 'F' || h[1] != 'L' || h[2] != 'V')
        {
            throw new MediaFormatException("The data does not start with an FLV header.") { Kind = DefectKind.MissingRequired, Offset = 0 };
        }

        Version = h[3];
        _hasAudioFlag = (h[4] & 4) != 0;
        _hasVideoFlag = (h[4] & 1) != 0;
        uint dataOffset = BinaryPrimitives.ReadUInt32BigEndian(h[5..]);
        if (dataOffset < 9)
        {
            Defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, 5, $"The header's DataOffset {dataOffset} is below 9; 9 is used.", "flv.data-offset");
            dataOffset = 9;
        }

        if ((h[4] & 0xFA) != 0)
        {
            Defects.Add(DefectKind.ReservedValue, DefectSeverity.Info, 4, "Reserved bits of the FLV header flags are set.", "flv.reserved-flags");
        }

        if (_random is not null && dataOffset > _random.Length)
        {
            throw new MediaFormatException("The FLV header's DataOffset is past the end of the file.") { Kind = DefectKind.SizeExceedsFile, Offset = 5 };
        }

        DataOffset = dataOffset;
        _input.Skip(dataOffset - 9, ct);
        Span<byte> prev = stackalloc byte[4];
        if (_input.Read(prev, ct) < 4)
        {
            Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, dataOffset, "The file ends right after the header.", "flv.truncated");
            _ended = true;
        }
        else if (BinaryPrimitives.ReadUInt32BigEndian(prev) != 0)
        {
            Defects.Add(DefectKind.InvalidValue, DefectSeverity.Info, dataOffset, "PreviousTagSize0 is not 0.", "flv.prev-size-0");
        }

        _firstTag = _input.Position;
    }

    private struct TagHeader
    {
        public int Type;
        public int Size;
        public long Timestamp;   // milliseconds, signed 32 bits
        public bool Filtered;
        public long Offset;      // of the tag header
    }

    // Reads one tag header; false at the end of the data (an incomplete header counts as the end, with a defect).
    private bool ReadTagHeader(out TagHeader t, CancellationToken ct)
    {
        t = default;
        Span<byte> h = stackalloc byte[TagHeaderSize];
        long at = _input.Position;
        int got = _input.Read(h, ct);
        if (got < TagHeaderSize)
        {
            if (got > 0)
            {
                Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, at, "The file ends inside a tag header.", "flv.truncated");
            }

            return false;
        }

        // Spec 10.1 "FLV tags": reserved(2) filter(1) type(5), DataSize(24), Timestamp(24), TimestampExtended(8, the high byte), StreamID(24, always 0).
        t.Type = h[0] & 0x1F;
        t.Filtered = (h[0] & 0x20) != 0;
        t.Size = (h[1] << 16) | (h[2] << 8) | h[3];
        t.Timestamp = (int)(((uint)h[7] << 24) | ((uint)h[4] << 16) | ((uint)h[5] << 8) | h[6]);
        t.Offset = at;
        return true;
    }

    private bool EnsurePayload(int size)
    {
        if (size > _payload.Length)
        {
            _limits.CheckPacket(size, _input.Position);
            _payload = new byte[Math.Max(size, _payload.Length * 2)];
        }

        return true;
    }

    // Reads the payload of the tag just read into _payload and the PreviousTagSize after it. False when the data ends inside the tag.
    private bool ReadTagBody(in TagHeader t, CancellationToken ct)
    {
        EnsurePayload(t.Size);
        if (_input.Read(_payload.AsSpan(0, t.Size), ct) < t.Size)
        {
            Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, t.Offset, "The file ends inside a tag; the tag is dropped.", "flv.truncated");
            return false;
        }

        return ReadPreviousTagSize(t, ct);
    }

    private bool ReadPreviousTagSize(in TagHeader t, CancellationToken ct)
    {
        Span<byte> p = stackalloc byte[4];
        int got = _input.Read(p, ct);
        _currentTagEnd = _input.Position;
        if (got == 4 && BinaryPrimitives.ReadUInt32BigEndian(p) != (uint)(TagHeaderSize + t.Size) && !_prevSizeReported)
        {
            _prevSizeReported = true;
            Defects.Add(DefectKind.Inconsistent, DefectSeverity.Info, t.Offset, "A PreviousTagSize field does not match the tag before it (reported once).", "flv.prev-size");
        }

        return true;
    }

    private bool SkipTagBody(in TagHeader t, CancellationToken ct)
    {
        long skipped = _input.Skip((long)t.Size + 4, ct);
        _currentTagEnd = _input.Position;
        if (skipped < (long)t.Size)
        {
            Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, t.Offset, "The file ends inside a tag.", "flv.truncated");
            return false;
        }

        return true;
    }

    private void Discover(CancellationToken ct)
    {
        long bytes = 0;
        int tags = 0;
        while (!_ended && tags < DiscoveryTags && bytes < DiscoveryBytes && !DiscoveryDone())
        {
            ct.ThrowIfCancellationRequested();
            if (!ReadTagHeader(out TagHeader t, ct))
            {
                _ended = true;
                break;
            }

            tags++;
            bytes += TagHeaderSize + t.Size;
            if (!ReadTagBody(t, ct))
            {
                _ended = true;
                break;
            }

            if (t.Type == 18)
            {
                HandleScript(t);
            }
            else if (t.Type is 8 or 9)
            {
                if (Interpret(t, discovery: true, out PacketInfo info, out int skip) && _random is null)
                {
                    // A forward-only source cannot go back, so the packet is kept for delivery.
                    int length = t.Size - skip;
                    byte[] copy = new byte[length];
                    _payload.AsSpan(skip, length).CopyTo(copy);
                    _pending.Add(new Pending { Info = info, Data = copy });
                }
            }
        }

        if (_tracks.Count == 0 && _hasAudioFlag | _hasVideoFlag)
        {
            Defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, _firstTag, "No audio or video tag was found among the leading tags.", "flv.no-media");
        }

        if (_random is not null)
        {
            _input.Seek(_firstTag);
        }

        // Metadata with a keyframe index gives the length too.
        ApplyMetadata();
    }

    private bool DiscoveryDone()
    {
        bool videoOk = !_hasVideoFlag || _video.Ready;
        bool audioOk = !_hasAudioFlag || _audio.Ready;
        return _tracks.Count > 0 && videoOk && audioOk;
    }

    private void HandleScript(in TagHeader t)
    {
        if (t.Filtered || t.Size == 0)
        {
            return;
        }

        long bodyOffset = t.Offset + TagHeaderSize;
        List<AmfProperty> props = AmfReader.ReadScriptData(_payload.AsSpan(0, t.Size), bodyOffset, _limits, Defects, out string? name);
        if (name == "onMetaData" && _meta.Count == 0)
        {
            _meta.AddRange(props);
            _scriptTags.Add((bodyOffset, t.Size));
            if (_random is null)
            {
                _limits.CheckTag(t.Size, bodyOffset);
                _scriptCopies.Add((bodyOffset, _payload.AsSpan(0, t.Size).ToArray()));
            }
        }
    }

    private void ApplyMetadata()
    {
        foreach (AmfProperty p in _meta)
        {
            if (p.Key == "duration" && p.Kind == AmfValueKind.Number && p.Number > 0 && p.Number < 1e9)
            {
                // The script's duration is in seconds as a double; millisecond ticks are what the tags use.
                _duration = new MediaTime((long)Math.Round(p.Number * 1000), 1000);
            }
        }

        // keyframes/filepositions/N and keyframes/times/N, in seconds and bytes.
        Dictionary<int, double> positions = [];
        Dictionary<int, double> times = [];
        foreach (AmfProperty p in _meta)
        {
            if (p.Kind != AmfValueKind.Number)
            {
                continue;
            }

            if (p.Key.StartsWith("keyframes/filepositions/", StringComparison.Ordinal) && int.TryParse(p.Key.AsSpan("keyframes/filepositions/".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int i))
            {
                positions[i] = p.Number;
            }
            else if (p.Key.StartsWith("keyframes/times/", StringComparison.Ordinal) && int.TryParse(p.Key.AsSpan("keyframes/times/".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int j))
            {
                times[j] = p.Number;
            }
        }

        // Only a short listing is kept in the property list (see AmfReader), so a long index is used only through the table it can build itself.
        _ = positions;
        _ = times;
    }

    // ---- tags to packets ---------------------------------------------------------------------------------------

    // Understands an audio or video tag whose payload is in _payload. Returns true when it is a media packet and then sets `info` (with a
    // size) and `skip`, the number of leading payload bytes that are headers rather than codec data. Builds the track on first sight.
    private bool Interpret(in TagHeader t, bool discovery, out PacketInfo info, out int skip)
    {
        info = default;
        skip = 0;
        if (t.Filtered)
        {
            Defects.Add(DefectKind.Unsupported, DefectSeverity.Info, t.Offset, "A tag has the filter bit set (encrypted); it is skipped.", "flv.filtered");
            return false;
        }

        if (t.Size < 1)
        {
            return false;
        }

        return t.Type == 9 ? InterpretVideo(t, out info, out skip) : InterpretAudio(t, out info, out skip);
    }

    private static readonly uint FourccAvc1 = Four("avc1"), FourccHvc1 = Four("hvc1"), FourccAv01 = Four("av01"), FourccVp09 = Four("vp09"), FourccVvc1 = Four("vvc1"), FourccVp08 = Four("vp08");
    private static readonly uint FourccOpus = Four("Opus"), FourccFlac = Four("fLaC"), FourccAc3 = Four("ac-3"), FourccEc3 = Four("ec-3"), FourccMp3 = Four(".mp3"), FourccMp4a = Four("mp4a");

    private static uint Four(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    // Video tag (spec 10.1 "VIDEODATA"): FrameType(4) CodecID(4); AVC adds AVCPacketType(8) and CompositionTime(24).
    // Enhanced RTMP: when bit 7 is set, FrameType is 3 bits, PacketType 4 bits, followed by a FourCC.
    private bool InterpretVideo(in TagHeader t, out PacketInfo info, out int skip)
    {
        info = default;
        skip = 0;
        byte b0 = _payload[0];
        bool ex = (b0 & 0x80) != 0;
        int frameType = ex ? (b0 >> 4) & 7 : b0 >> 4;
        if (frameType == 5)
        {
            // Video info / command frame (0 = start of a seek range, 1 = its end): carries no picture.
            return false;
        }

        CodecId codec;
        string? tag;
        int packetType;
        uint fourcc = 0;
        int header;
        long cts = 0;
        if (ex)
        {
            if (t.Size < 5)
            {
                return false;
            }

            packetType = b0 & 0xF;
            fourcc = BinaryPrimitives.ReadUInt32BigEndian(_payload.AsSpan(1));
            header = 5;
            (codec, tag) = fourcc == FourccAvc1 ? (CodecId.H264, "avc1") : fourcc == FourccHvc1 ? (CodecId.Hevc, "hvc1") : fourcc == FourccAv01 ? (CodecId.Av1, "av01")
                : fourcc == FourccVp09 ? (CodecId.Vp9, "vp09") : fourcc == FourccVp08 ? (CodecId.Vp8, "vp08") : fourcc == FourccVvc1 ? (CodecId.Vvc, "vvc1") : (CodecId.Unknown, System.Text.Encoding.ASCII.GetString(_payload, 1, 4));
            switch (packetType)
            {
                case 0: // sequence start: the configuration record
                    SetVideoConfig(t, codec, tag, fourcc, 0, header);
                    return false;
                case 1: // coded frames, with a 24-bit composition time for AVC, HEVC and VVC
                    if (fourcc == FourccAvc1 || fourcc == FourccHvc1 || fourcc == FourccVvc1)
                    {
                        if (t.Size < 8)
                        {
                            return false;
                        }

                        cts = SignExtend24((_payload[5] << 16) | (_payload[6] << 8) | _payload[7]);
                        header = 8;
                    }

                    break;
                case 3: // coded frames without a composition time
                    break;
                default: // 2 sequence end, 4 metadata, 5 MPEG-2 TS sequence start
                    return false;
            }
        }
        else
        {
            int codecId = b0 & 0xF;
            packetType = 1;
            header = 1;
            (codec, tag) = codecId switch
            {
                2 => (new CodecId("flv1"), "flv1"),
                3 => (new CodecId("flashsv"), "flashsv"),
                4 => (new CodecId("vp6f"), "vp6f"),
                5 => (new CodecId("vp6a"), "vp6a"),
                6 => (new CodecId("flashsv2"), "flashsv2"),
                7 => (CodecId.H264, "avc1"),
                12 => (CodecId.Hevc, "hvc1"),
                _ => (CodecId.Unknown, "flv" + codecId.ToString(CultureInfo.InvariantCulture)),
            };
            if (codecId is 7 or 12)
            {
                if (t.Size < 5)
                {
                    return false;
                }

                int avcType = _payload[1];
                header = 5;
                cts = SignExtend24((_payload[2] << 16) | (_payload[3] << 8) | _payload[4]);
                if (avcType == 0)
                {
                    SetVideoConfig(t, codec, tag, 0, codecId, header);
                    return false;
                }

                if (avcType != 1)
                {
                    return false; // 2 = end of sequence
                }
            }
            else if (codecId is 4 or 5)
            {
                // VP6: the first byte holds the horizontal and vertical adjustment of the picture size; VP6A has a 3-byte alpha offset after it.
                header = codecId == 5 ? 5 : 1;
                header = Math.Min(header, t.Size);
            }

            fourcc = (uint)codecId;
        }

        if (!_video.Ready)
        {
            BuildVideoTrack(t, codec, tag, ex ? fourcc : 0, ex ? 0 : (int)fourcc, header, null);
        }

        if (_video.Track < 0 || header > t.Size)
        {
            return false;
        }

        long dts = t.Timestamp;
        long pts = dts + cts;
        // Frame types (spec): 1 key frame, 2 inter frame, 3 disposable inter frame (H.263 only), 4 generated key frame, 5 info/command.
        PacketAttributes flags = frameType == 1 ? PacketAttributes.Keyframe : frameType == 3 ? PacketAttributes.Disposable : PacketAttributes.None;
        skip = header;
        info = new PacketInfo(_video.Track, new MediaTime(pts, 1000), new MediaTime(dts, 1000), MediaTime.Unknown, flags, new ByteRange(t.Offset + TagHeaderSize + header, t.Size - header), t.Size - header);
        return true;
    }

    private static long SignExtend24(int v) => (v & 0x800000) != 0 ? v - 0x1000000 : v;

    private void SetVideoConfig(in TagHeader t, CodecId codec, string? tag, uint fourcc, int codecId, int header)
    {
        if (codec.IsUnknown || t.Size <= header)
        {
            return;
        }

        byte[] config = _payload.AsSpan(header, t.Size - header).ToArray();
        if (_video.Ready && _video.ConfigSeen)
        {
            // A repeated or changed sequence header in the middle of the stream: a new configuration starts here.
            MediaTrack old = _tracks[_video.Track];
            if (!old.Configuration.Data.Span.SequenceEqual(config))
            {
                Defects.Add(DefectKind.Inconsistent, DefectSeverity.Info, t.Offset, "The video sequence header changes; the track keeps its first configuration.", "flv.config-change");
            }

            return;
        }

        BuildVideoTrack(t, codec, tag, fourcc, codecId, header, config);
    }

    private void BuildVideoTrack(in TagHeader t, CodecId codec, string? tag, uint fourcc, int codecId, int header, byte[]? config)
    {
        if (_video.Ready && config is null)
        {
            return;
        }

        VideoTrackInfo? video = null;
        string? format = codec == CodecId.H264 ? "avcC" : codec == CodecId.Hevc ? "hvcC" : codec == CodecId.Av1 ? "av1C" : codec == CodecId.Vp9 ? "vpcC" : codec == CodecId.Vvc ? "vvcC" : null;
        CodecConfiguration cfg = config is null ? default : new CodecConfiguration(config, format);
        if (config is not null && CodecParameterReader.TryDescribe(codec, cfg, out CodecParameters? p) && p is not null && p.Width > 0)
        {
            video = new VideoTrackInfo(p.Width, p.Height, 0, p.SarWidth > 0 ? p.SarWidth : 1, p.SarHeight > 0 ? p.SarHeight : 1, p.FrameRateNumerator, p.FrameRateDenominator);
        }
        else if (config is null)
        {
            video = PictureSize(codec, _payload.AsSpan(header, t.Size - header));
        }

        bool needsConfig = codec == CodecId.H264 || codec == CodecId.Hevc || codec == CodecId.Av1 || codec == CodecId.Vvc || codec == CodecId.Vp9 && fourcc != 0;
        _video.Format = codecId;
        _video.FourCc = fourcc;
        _video.ConfigSeen = config is not null;
        _video.Ready = config is not null || !needsConfig;
        if (_video.Track < 0)
        {
            _video.Track = _tracks.Count;
            _tracks.Add(new MediaTrack { Index = _video.Track, Id = 9, Kind = TrackKind.Video, Codec = codec, CodecTag = tag, Timescale = 1000, Video = video, Configuration = cfg });
        }
        else
        {
            _tracks[_video.Track] = _tracks[_video.Track] with { Configuration = cfg, Video = video ?? _tracks[_video.Track].Video };
        }
    }

    // Picture size from the first key frame for codecs whose configuration is in-band.
    private static VideoTrackInfo? PictureSize(CodecId codec, ReadOnlySpan<byte> frame)
    {
        if (codec.Name == "flv1" && frame.Length >= 9)
        {
            // Sorenson H.263 (FLV spec 10.1, "H.263 VIDEOPACKET"): a 17-bit start code of value 1, 5-bit version, 8-bit temporal reference,
            // 3-bit picture size format, then for formats 0 and 1 the width and height as 8 or 16 bits each.
            if (ReadBits(frame, 0, 17) != 1)
            {
                return null;
            }

            int w;
            int h;
            switch (ReadBits(frame, 30, 3))
            {
                case 0: (w, h) = (ReadBits(frame, 33, 8), ReadBits(frame, 41, 8)); break;
                case 1: (w, h) = (ReadBits(frame, 33, 16), ReadBits(frame, 49, 16)); break;
                case 2: (w, h) = (352, 288); break;
                case 3: (w, h) = (176, 144); break;
                case 4: (w, h) = (128, 96); break;
                case 5: (w, h) = (320, 240); break;
                case 6: (w, h) = (160, 120); break;
                default: return null;
            }

            return w > 0 && h > 0 ? new VideoTrackInfo(w, h) : null;
        }

        if (codec.Name == "flashsv" && frame.Length >= 4)
        {
            // Screen video (spec "SCREENVIDEOPACKET"): 4 bits block width, 12 bits image width, 4 bits block height, 12 bits image height.
            int w = ((frame[0] & 0x0F) << 8) | frame[1];
            int h = ((frame[2] & 0x0F) << 8) | frame[3];
            return w > 0 && h > 0 ? new VideoTrackInfo(w, h) : null;
        }

        return null;
    }

    private static int ReadBits(ReadOnlySpan<byte> data, int bitStart, int count)
    {
        int v = 0;
        for (int i = 0; i < count; i++)
        {
            int bit = bitStart + i;
            int b = bit >> 3 < data.Length ? (data[bit >> 3] >> (7 - (bit & 7))) & 1 : 0;
            v = (v << 1) | b;
        }

        return v;
    }

    private static readonly int[] FlvRates = [5512, 11025, 22050, 44100];

    // Audio tag (spec 10.1 "AUDIODATA"): SoundFormat(4) SoundRate(2) SoundSize(1) SoundType(1); AAC adds AACPacketType(8).
    // Enhanced RTMP audio uses SoundFormat 9: AudioPacketType(4) then a FourCC.
    private bool InterpretAudio(in TagHeader t, out PacketInfo info, out int skip)
    {
        info = default;
        skip = 0;
        byte b0 = _payload[0];
        int fmt = b0 >> 4;
        int rate = FlvRates[(b0 >> 2) & 3];
        bool sixteen = (b0 & 2) != 0;
        int channels = (b0 & 1) != 0 ? 2 : 1;
        int header = 1;
        CodecId codec;
        string? tag;
        string? format = null;
        if (fmt == 9)
        {
            if (t.Size < 5)
            {
                return false;
            }

            int packetType = b0 & 0xF;
            uint fourcc = BinaryPrimitives.ReadUInt32BigEndian(_payload.AsSpan(1));
            header = 5;
            (codec, tag) = fourcc == FourccOpus ? (CodecId.Opus, "Opus") : fourcc == FourccFlac ? (CodecId.Flac, "fLaC") : fourcc == FourccAc3 ? (CodecId.Ac3, "ac-3")
                : fourcc == FourccEc3 ? (CodecId.Eac3, "ec-3") : fourcc == FourccMp3 ? (CodecId.Mp3, ".mp3") : fourcc == FourccMp4a ? (CodecId.Aac, "mp4a") : (CodecId.Unknown, System.Text.Encoding.ASCII.GetString(_payload, 1, 4));
            if (packetType == 0)
            {
                if (t.Size > header && !_audio.ConfigSeen)
                {
                    BuildAudioTrack(codec, tag, "CodecPrivate", _payload.AsSpan(header, t.Size - header).ToArray(), 0, 0, 0, fmt);
                }

                return false;
            }

            if (packetType != 1)
            {
                return false;
            }

            if (!_audio.Ready)
            {
                BuildAudioTrack(codec, tag, null, null, 0, 0, 0, fmt);
            }
        }
        else
        {
            int aacType = 1;
            (codec, tag) = fmt switch
            {
                0 => (CodecId.Pcm, sixteen ? "pcm_s16be" : "pcm_u8"),
                1 => (new CodecId("adpcm_swf"), "adpcm_swf"),
                2 or 14 => (CodecId.Mp3, ".mp3"),
                3 => (CodecId.Pcm, sixteen ? "pcm_s16le" : "pcm_u8"),
                4 or 5 or 6 => (new CodecId("nellymoser"), "nellymoser"),
                7 => (CodecId.Pcm, "pcm_alaw"),
                8 => (CodecId.Pcm, "pcm_mulaw"),
                10 => (CodecId.Aac, "mp4a"),
                11 => (new CodecId("speex"), "speex"),
                _ => (CodecId.Unknown, "flv" + fmt.ToString(CultureInfo.InvariantCulture)),
            };
            if (fmt == 4)
            {
                (rate, channels) = (16000, 1);
            }
            else if (fmt == 5)
            {
                (rate, channels) = (8000, 1);
            }
            else if (fmt == 11)
            {
                (rate, channels) = (16000, 1);
            }
            else if (fmt == 14)
            {
                rate = 8000;
            }
            else if (fmt is 7 or 8)
            {
                rate = 8000; // G.711: 8 kHz in practice; the flags are ignored by most readers
            }

            if (fmt == 10)
            {
                if (t.Size < 2)
                {
                    return false;
                }

                aacType = _payload[1];
                header = 2;
                if (aacType == 0)
                {
                    if (!_audio.ConfigSeen)
                    {
                        BuildAudioTrack(codec, tag, "CodecPrivate", _payload.AsSpan(header, t.Size - header).ToArray(), rate, channels, 0, fmt);
                    }

                    return false;
                }

                if (aacType != 1)
                {
                    return false;
                }
            }

            if (!_audio.Ready)
            {
                BuildAudioTrack(codec, tag, format, null, rate, channels, fmt is 0 or 3 ? (sixteen ? 16 : 8) : 0, fmt, firstFrame: header <= t.Size ? _payload.AsSpan(header, t.Size - header).ToArray() : null);
            }
        }

        if (_audio.Track < 0 || header > t.Size)
        {
            return false;
        }

        MediaTime ts = new(t.Timestamp, 1000);
        skip = header;
        info = new PacketInfo(_audio.Track, ts, ts, MediaTime.Unknown, PacketAttributes.Keyframe, new ByteRange(t.Offset + TagHeaderSize + header, t.Size - header), t.Size - header);
        return true;
    }

    private void BuildAudioTrack(CodecId codec, string? tag, string? format, byte[]? config, int rate, int channels, int bits, int fmt, byte[]? firstFrame = null)
    {
        if (_audio.Ready && _audio.ConfigSeen)
        {
            return;
        }

        CodecConfiguration cfg = config is null ? default : new CodecConfiguration(config, format);
        if (config is not null && codec == CodecId.Aac && AudioSpecificConfig.TryParse(config, out AudioSpecificConfig asc))
        {
            // The sample rate and channel count of an AAC stream come from the AudioSpecificConfig, not from the tag's flags (which are fixed at 44.1 kHz stereo).
            rate = asc.OutputSamplingFrequency > 0 ? asc.OutputSamplingFrequency : asc.SamplingFrequency;
            channels = asc.Channels > 0 ? asc.Channels : channels;
        }
        else if (config is not null && CodecParameterReader.TryDescribe(codec, cfg, out CodecParameters? p) && p is not null && p.SampleRate > 0)
        {
            (rate, channels) = (p.SampleRate, p.Channels > 0 ? p.Channels : channels);
        }
        else if (codec == CodecId.Mp3 && firstFrame is not null && MpegAudioFrameHeader.TryParse(firstFrame, out MpegAudioFrameHeader mh))
        {
            // The flags of an MP3 tag can be wrong for 32 and 48 kHz (they only offer 5.5, 11, 22 and 44 kHz), so the frame header decides.
            (rate, channels) = (mh.SampleRate, mh.Channels);
        }

        _audio.Format = fmt;
        _audio.ConfigSeen = config is not null;
        _audio.Ready = config is not null || codec != CodecId.Aac;
        MediaTrack track = new()
        {
            Index = _audio.Track >= 0 ? _audio.Track : _tracks.Count,
            Id = 8,
            Kind = TrackKind.Audio,
            Codec = codec,
            CodecTag = tag,
            Timescale = 1000,
            Configuration = cfg,
            Audio = new AudioTrackInfo(rate, channels, bits),
        };
        if (_audio.Track < 0)
        {
            _audio.Track = track.Index;
            _tracks.Add(track);
        }
        else
        {
            _tracks[_audio.Track] = track;
        }
    }

    // ---- reading -----------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public bool TryReadPacket(out PacketView packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingHead < _pending.Count)
        {
            Pending p = _pending[_pendingHead++];
            packet = new PacketView(p.Info, p.Data);
            return true;
        }

        while (!_ended)
        {
            if (!ReadTagHeader(out TagHeader t, CancellationToken.None))
            {
                _ended = true;
                break;
            }

            bool media = t.Type is 8 or 9;
            int trackKind = t.Type == 9 ? _video.Track : _audio.Track;
            if (!media || (trackKind >= 0 && _disabled[trackKind]))
            {
                if (!SkipTagBody(t, CancellationToken.None))
                {
                    _ended = true;
                }

                continue;
            }

            if (!ReadTagBody(t, CancellationToken.None))
            {
                _ended = true;
                break;
            }

            if (Interpret(t, discovery: false, out PacketInfo info, out int skip))
            {
                if (_disabled.Length > info.TrackIndex && _disabled[info.TrackIndex])
                {
                    continue;
                }

                packet = new PacketView(info, _payload.AsSpan(skip, t.Size - skip));
                return true;
            }
        }

        packet = default;
        return false;
    }

    // ---- duration ----------------------------------------------------------------------------------------------

    private void ReadTailDuration(CancellationToken ct)
    {
        if (_random is null || _duration.IsKnown)
        {
            return;
        }

        // The last four bytes of a complete file are the size of the last tag (spec "PreviousTagSize"), which locates its header.
        long length = _random.Length;
        Span<byte> tail = stackalloc byte[4];
        if (length < _firstTag + TagHeaderSize + 4 || _random.Read(length - 4, tail, ct) < 4)
        {
            return;
        }

        uint size = BinaryPrimitives.ReadUInt32BigEndian(tail);
        long at = length - 4 - size;
        if (size < TagHeaderSize || at < _firstTag)
        {
            return;
        }

        Span<byte> h = stackalloc byte[TagHeaderSize];
        if (_random.Read(at, h, ct) < TagHeaderSize)
        {
            return;
        }

        int bodySize = (h[1] << 16) | (h[2] << 8) | h[3];
        if (TagHeaderSize + bodySize != size)
        {
            return;
        }

        long ts = (int)(((uint)h[7] << 24) | ((uint)h[4] << 16) | ((uint)h[5] << 8) | h[6]);
        if (ts > 0)
        {
            _duration = new MediaTime(ts, 1000);
        }
    }

    // ---- seeking -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Seeks to the last video keyframe at or before <paramref name="target"/> (the last tag of any kind for an audio-only file), using a table
    /// built once by walking the tag headers of the whole file, which costs one small read per tag.
    /// </summary>
    /// <param name="target">The time to seek to.</param>
    /// <param name="mode"><see cref="SeekMode.PreviousKeyframe"/> or <see cref="SeekMode.NextKeyframe"/>.</param>
    /// <returns>The time of the tag the next read starts at.</returns>
    /// <exception cref="NotSupportedException">The source cannot seek.</exception>
    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_random is null)
        {
            throw new NotSupportedException("The FLV source cannot seek.");
        }

        BuildIndex();
        long ms = target.Rescale(1000, MediaRounding.Floor).Ticks;
        int best = 0;
        for (int i = 0; i < _keyframes.Count; i++)
        {
            if (_keyframes[i].Time <= ms)
            {
                best = i;
            }
            else
            {
                if (mode == SeekMode.NextKeyframe && best == i - 1 && _keyframes[best].Time < ms)
                {
                    best = i;
                }

                break;
            }
        }

        _pendingHead = _pending.Count;
        _ended = false;
        if (_keyframes.Count == 0)
        {
            _input.Seek(_firstTag);
            return MediaTime.Zero;
        }

        _input.Seek(_keyframes[best].Offset);
        return new MediaTime(_keyframes[best].Time, 1000);
    }

    private void BuildIndex()
    {
        if (_indexBuilt)
        {
            return;
        }

        _indexBuilt = true;
        long saved = _input.Position;
        _input.Seek(_firstTag);
        bool videoOnly = _video.Track >= 0;
        Span<byte> first = stackalloc byte[1];
        long lastAudioKey = -1;
        while (ReadTagHeader(out TagHeader t, CancellationToken.None))
        {
            bool key = false;
            if (t.Type == 9 && t.Size > 0 && !t.Filtered)
            {
                _input.Read(first, CancellationToken.None);
                byte b0 = first[0];
                key = ((b0 & 0x80) != 0 ? (b0 >> 4) & 7 : b0 >> 4) == 1;
                _input.Skip(t.Size - 1 + 4L, CancellationToken.None);
            }
            else
            {
                _input.Skip(t.Size + 4L, CancellationToken.None);
                if (t.Type == 8 && !videoOnly)
                {
                    lastAudioKey = t.Offset;
                }
            }

            if (key || (!videoOnly && t.Type == 8 && _keyframes.Count % 64 == 0 && t.Timestamp > (_keyframes.Count == 0 ? -1 : _keyframes[^1].Time)))
            {
                _keyframes.Add((t.Timestamp, t.Offset));
            }
        }

        _ = lastAudioKey;
        _input.Seek(saved);
    }

    // ---- metadata ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns the properties of <c>onMetaData</c> as tags (numbers, booleans, strings and dates one tag each; arrays as a binary tag over their bytes
    /// plus their first elements) and the length as a fact.
    /// </summary>
    /// <param name="cancellationToken">Not used; the tags were read at open.</param>
    /// <returns>The tags and facts.</returns>
    public MediaMetadata ReadMetadata(CancellationToken cancellationToken = default)
    {
        DefectLog defects = new(_limits);
        List<ITagContainer> containers = [];
        if (_meta.Count > 0)
        {
            IMediaSource source = _random ?? new SparseSource(_scriptCopies);
            TagIo io = new(source, _limits, defects);
            List<NativeTag> tags = [];
            foreach (AmfProperty p in _meta)
            {
                tags.Add(p.Kind switch
                {
                    AmfValueKind.Number => new NativeTag(io, TagSystems.Flv, p.Key, 0, TagValueKind.Real, 0, 0, null, p.Range, p.Range) { RealValue = p.Number },
                    AmfValueKind.Boolean => new NativeTag(io, TagSystems.Flv, p.Key, 0, TagValueKind.Number, 0, 0, null, p.Range, p.Range) { Number = (long)p.Number },
                    AmfValueKind.Date => new NativeTag(io, TagSystems.Flv, p.Key, 0, TagValueKind.Text, 0, 0, null, p.Range, p.Range) { Encoding = TagTextEncoding.Utf8 }.WithText(
                        DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Clamp(p.Number, -62135596800000d, 253402300799000d)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                    AmfValueKind.Text => new NativeTag(io, TagSystems.Flv, p.Key, 0, TagValueKind.Text, 0, 0, null, p.Range, p.Range) { Encoding = TagTextEncoding.Utf8 }.WithText(p.Text ?? string.Empty),
                    _ => new NativeTag(io, TagSystems.Flv, p.Key, 0, TagValueKind.Binary, 0, 0, null, p.Range, p.Range),
                });
            }

            (long off, int len) = _scriptTags[0];
            containers.Add(new FlvScriptTags(new ByteRange(off, len), tags, "onMetaData"));
        }

        FactsBuilder facts = new(defects);
        foreach (ITagContainer c in containers)
        {
            foreach (NativeTag t in c.Tags)
            {
                facts.AddNative(t);
            }
        }

        if (_duration.IsKnown)
        {
            facts.Duration.Add(0, new FactCandidate<MediaTime>(_duration, "flv", _duration.ToString(), "the onMetaData duration, or the timestamp of the last tag"));
        }

        foreach (MediaTrack t in _tracks)
        {
            if (t.Video is { Width: > 0 } v)
            {
                facts.FrameSize.Add(0, new FactCandidate<FrameSize>(new FrameSize(v.Width, v.Height, v.Width, v.Height), "flv:video", $"{v.Width}x{v.Height}", "picture size from the video configuration"));
                break;
            }
        }

        return new MediaMetadata(containers, [], facts.Build(), defects, true);
    }

    // The leading script tags of a forward-only source, kept in memory at their file offsets.
    private sealed class SparseSource(List<(long Offset, byte[] Data)> chunks) : IMediaSource
    {
        public long Length => chunks.Count == 0 ? 0 : chunks[^1].Offset + chunks[^1].Data.Length;

        public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
        {
            foreach ((long off, byte[] data) in chunks)
            {
                if (offset >= off && offset < off + data.Length)
                {
                    int n = (int)Math.Min(destination.Length, off + data.Length - offset);
                    data.AsSpan((int)(offset - off), n).CopyTo(destination);
                    return n;
                }
            }

            return 0;
        }

        public void Dispose()
        {
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _input.Dispose();
        _owned?.Dispose();
    }
}

internal static class NativeTagTextExtensions
{
    public static NativeTag WithText(this NativeTag tag, string text)
    {
        tag.SetText(text);
        return tag;
    }
}

/// <summary>Opens FLV files for the format registry.</summary>
public sealed class FlvFormat : IMediaFormat, IHeadSeededFormat
{
    private readonly Sniffing.FlvSniffer _sniffer = new();

    /// <inheritdoc />
    public MediaFormatId Id => _sniffer.Id;

    /// <inheritdoc />
    public string DisplayName => _sniffer.DisplayName;

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) => _sniffer.Sniff(head);

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => FlvDemuxer.Open(source, options, cancellationToken);

    IDemuxer IHeadSeededFormat.Open(IMediaSource source, ReadOnlySpan<byte> head, MediaOpenOptions options, CancellationToken cancellationToken) =>
        FlvDemuxer.Open(source, head, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => FlvDemuxer.Open(source, options, cancellationToken);
}
