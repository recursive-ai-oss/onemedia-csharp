using System.Runtime.CompilerServices;
using System.Buffers.Binary;

namespace RecursiveAI.OneMedia.Matroska;

/// <summary>
/// The Matroska/WebM reader. One implementation serves the synchronous and asynchronous demuxers: every method that touches the source is
/// a <see cref="ValueTask"/> method, and sources that never wait return completed tasks, so the synchronous path neither blocks nor allocates.
/// </summary>
internal sealed class MatroskaReader
{
    private const int MaxHeaderBytes = Ebml.MaxIdLength + Ebml.MaxSizeLength;
    private const int ReadChunk = 1 << 20;
    private const int InitialBlockBuffer = 1 << 20;

    private enum HeaderStatus { Ok, Eof, Invalid }

    private readonly record struct ElementHeader(HeaderStatus Status, long Offset, uint Id, long Size, int HeaderLength)
    {
        public long PayloadOffset => Offset + HeaderLength;

        public long End => Size < 0 ? long.MaxValue : Offset + HeaderLength + Size;
    }

    private sealed class TrackState
    {
        public required MatroskaTrackEntry Entry { get; init; }

        public required bool IsVideo { get; init; }

        public bool Enabled { get; set; }

        public byte[] Prefix { get; init; } = [];

        public bool Zlib { get; init; }

        public long DefaultDurationNs { get; init; }

        /// <summary>CodecDelay in ticks, subtracted from every Block timestamp of the track.</summary>
        public long CodecDelayTicks { get; init; }
    }

    private readonly EbmlInput _in;
    private readonly MediaLimits _limits;
    private readonly DefectLog _defects;

    private TrackState[] _states = [];
    private readonly Dictionary<long, int> _trackByNumber = [];
    private readonly HashSet<long> _warnedTracks = [];
    private List<MatroskaSeekEntry> _seekEntries = [];
    private long _firstClusterPos = -1;
    private long _cuesPos = -1;
    private bool _seekPointsLoaded;
    private long[] _seekTimes = [];
    private long[] _seekPositions = [];

    // Reading position inside the Segment.
    private bool _inCluster;
    private long _clusterEnd;
    private long _clusterTimestamp;
    private bool _warnedFileLength;

    // The block being turned into packets.
    private byte[] _blk = [];
    private long _blkFileOffset;
    private (int Offset, int Length)[] _slots = new (int, int)[8];
    private int _frameCount;
    private int _frameIndex;
    private int _bTrack;
    private long _bPtsTicks;
    private long _bDurationTicks = -1;
    private long _bDiscardPaddingNs;
    private bool _bKey;
    private bool _bInvisible;
    private bool _bDisposable;
    private byte[] _out = [];
    private long[] _sizes = new long[8];
    private bool _seekHeadChainFollowed;

    public MatroskaReader(EbmlInput input, MediaLimits limits, DefectLog defects)
    {
        _in = input;
        _limits = limits;
        _defects = defects;
    }

    public MatroskaHeader Header { get; private set; } = new();

    public MatroskaInfo Info { get; private set; } = new();

    public IReadOnlyList<MatroskaTrackEntry> Entries { get; private set; } = [];

    public MediaTrack[] Tracks { get; private set; } = [];

    public MatroskaClock Clock { get; private set; } = MatroskaClock.FromTimestampScale(1_000_000);

    public long SegmentStart { get; private set; }

    public long SegmentEnd { get; private set; } = long.MaxValue;

    public bool SegmentSizeUnknown { get; private set; }

    public IReadOnlyList<MatroskaSeekEntry> SeekEntries => _seekEntries;

    public bool CanSeek => _in.CanSeek;

    public ByteRange Chapters { get; private set; } = ByteRange.None;

    public ByteRange Tags { get; private set; } = ByteRange.None;

    public ByteRange Attachments { get; private set; } = ByteRange.None;

    public PacketInfo CurrentInfo { get; private set; }

    private byte[] _currentBuffer = [];
    private int _currentOffset;
    private int _currentLength;

    public ReadOnlySpan<byte> CurrentData => new(_currentBuffer, _currentOffset, _currentLength);

    // ---------------------------------------------------------------------------------------------------------------------------
    // Element headers

    // Almost always the next header is already buffered, and then there is nothing to wait for: answer without an asynchronous method.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private ValueTask<ElementHeader> PeekHeaderAsync(CancellationToken ct)
    {
        if (_in.Buffered.Length >= MaxHeaderBytes)
        {
            return new ValueTask<ElementHeader>(ParseHeader(_in.Buffered, _in.Position));
        }

        return PeekHeaderSlowAsync(ct);
    }

    private async ValueTask<ElementHeader> PeekHeaderSlowAsync(CancellationToken ct)
    {
        long offset = _in.Position;
        await _in.EnsureAsync(MaxHeaderBytes, ct).ConfigureAwait(false);
        return ParseHeader(_in.Buffered, offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ElementHeader ParseHeader(ReadOnlySpan<byte> window, long offset)
    {
        if (window.IsEmpty)
        {
            return new ElementHeader(HeaderStatus.Eof, offset, 0, 0, 0);
        }

        if (!Ebml.TryReadId(window, out uint id, out int idLen) || !Ebml.TryReadSize(window[idLen..], out long size, out int sizeLen))
        {
            return new ElementHeader(HeaderStatus.Invalid, offset, 0, 0, 0);
        }

        return new ElementHeader(HeaderStatus.Ok, offset, id, size, idLen + sizeLen);
    }

    /// <summary>
    /// Reads a master element's payload into memory. On damage it logs a defect and returns null, having moved past the element (or to
    /// the end of the data when it claims more than is left), so that callers walking the file always make progress.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private ValueTask<byte[]?> ReadPayloadAsync(ElementHeader h, string what, CancellationToken ct)
    {
        // The element, header and payload, is already buffered: copy it out without an asynchronous method.
        if (h.Size >= 0 && h.HeaderLength + h.Size <= _in.Buffered.Length)
        {
            _in.Advance(h.HeaderLength);
            _limits.CheckAllocation(h.Size, h.Offset);
            byte[] data = new byte[h.Size];
            _in.Buffered[..data.Length].CopyTo(data);
            _in.Advance(data.Length);
            return new ValueTask<byte[]?>(data);
        }

        return ReadPayloadSlowAsync(h, what, ct);
    }

    private async ValueTask<byte[]?> ReadPayloadSlowAsync(ElementHeader h, string what, CancellationToken ct)
    {
        _in.Advance(h.HeaderLength);
        if (h.Size < 0)
        {
            _defects.Add(DefectKind.InvalidSize, DefectSeverity.Error, h.Offset, $"The {what} element has an unknown size, which RFC 9559 only allows for Segment and Cluster.");
            return null;
        }

        _limits.CheckAllocation(h.Size, h.Offset);
        long length = _in.Length;
        if (length >= 0 && h.PayloadOffset + h.Size > length)
        {
            _defects.Add(DefectKind.SizeExceedsFile, DefectSeverity.Error, h.Offset, $"The {what} element claims {h.Size} bytes but the file ends {Math.Max(0, length - h.PayloadOffset)} bytes in.");
            await _in.SkipAsync(Math.Max(0, length - h.PayloadOffset), ct).ConfigureAwait(false);
            return null;
        }

        byte[] data = new byte[h.Size];
        int n = await _in.ReadAsync(data, ct).ConfigureAwait(false);
        if (n < data.Length)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Error, h.Offset, $"The data ends inside the {what} element.");
            return null;
        }

        return data;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private ValueTask SkipElementAsync(ElementHeader h, CancellationToken ct)
    {
        // An empty element, or one whose payload is buffered or can be seeked past, is skipped without an asynchronous method.
        if (h.Size <= 0 || (_in.CanSeek && h.PayloadOffset + h.Size <= _in.Length))
        {
            _in.Advance(h.HeaderLength);
            if (h.Size > 0)
            {
                _in.SkipSeekable(h.Size);
            }

            return default;
        }

        return SkipElementSlowAsync(h, ct);
    }

    private async ValueTask SkipElementSlowAsync(ElementHeader h, CancellationToken ct)
    {
        _in.Advance(h.HeaderLength);
        if (h.Size <= 0)
        {
            return;
        }

        long length = _in.Length;
        if (length >= 0 && h.PayloadOffset + h.Size > length)
        {
            if (!_warnedFileLength)
            {
                _warnedFileLength = true;
                _defects.Add(DefectKind.SizeExceedsFile, DefectSeverity.Warning, h.Offset, $"An element claims {h.Size} bytes but the file ends first; the data is treated as cut off.");
            }

            await _in.SkipAsync(Math.Max(0, length - h.PayloadOffset), ct).ConfigureAwait(false);
            return;
        }

        await _in.SkipAsync(h.Size, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Open

    public async ValueTask OpenAsync(CancellationToken ct)
    {
        ElementHeader h = await PeekHeaderAsync(ct).ConfigureAwait(false);
        if (h.Status != HeaderStatus.Ok || h.Id != MatroskaIds.Ebml)
        {
            throw new MediaFormatException("The data does not start with an EBML header.") { Offset = h.Offset, Kind = DefectKind.InvalidValue };
        }

        if (h.Size < 0 || h.Size > 1 << 20)
        {
            throw new MediaFormatException($"The EBML header claims {h.Size} bytes.") { Offset = h.Offset, Kind = DefectKind.InvalidSize };
        }

        byte[]? headerBytes = await ReadPayloadAsync(h, "EBML header", ct).ConfigureAwait(false);
        if (headerBytes is null)
        {
            throw new MediaFormatException("The EBML header is cut off.") { Offset = h.Offset, Kind = DefectKind.Truncated };
        }

        MatroskaParser.VerifyCrc(headerBytes, h.PayloadOffset, _defects, "EBML header");
        Header = MatroskaParser.ParseHeader(headerBytes, h.PayloadOffset, _defects);
        if (Header.EbmlReadVersion > 1 || Header.MaxIdLength > Ebml.MaxIdLength || Header.MaxSizeLength > Ebml.MaxSizeLength)
        {
            throw new UnsupportedMediaException("The EBML header asks for a newer EBML version or longer IDs or sizes than this reader supports.");
        }

        if (Header.DocType is not ("matroska" or "webm"))
        {
            _defects.Add(DefectKind.Unsupported, DefectSeverity.Warning, h.Offset, $"The DocType is \"{Header.DocType}\", not matroska or webm; it is read as Matroska.");
        }
        else if (Header.DocTypeReadVersion > 4)
        {
            _defects.Add(DefectKind.Unsupported, DefectSeverity.Warning, h.Offset, $"The file needs Matroska version {Header.DocTypeReadVersion}; this reader implements version 4.");
        }

        // The Segment follows the header.
        h = await PeekHeaderAsync(ct).ConfigureAwait(false);
        if (h.Status != HeaderStatus.Ok || h.Id != MatroskaIds.Segment)
        {
            long from = h.Offset;
            _defects.Add(DefectKind.InvalidOrder, DefectSeverity.Error, from, "The element after the EBML header is not a Segment; scanning for one.");
            if (!await ScanForAsync(SegmentPattern, ct).ConfigureAwait(false))
            {
                throw new MediaFormatException("The file has no Segment.") { Offset = from, Kind = DefectKind.MissingRequired };
            }

            h = await PeekHeaderAsync(ct).ConfigureAwait(false);
        }

        _in.Advance(h.HeaderLength);
        SegmentStart = h.PayloadOffset;
        long fileLength = _in.Length;
        SegmentSizeUnknown = h.Size < 0;
        if (h.Size < 0)
        {
            SegmentEnd = fileLength >= 0 ? fileLength : long.MaxValue;
        }
        else
        {
            SegmentEnd = h.PayloadOffset + h.Size;
            if (fileLength >= 0 && SegmentEnd > fileLength)
            {
                _defects.Add(DefectKind.SizeExceedsFile, DefectSeverity.Warning, h.Offset, $"The Segment claims {h.Size} bytes but the file has {fileLength - h.PayloadOffset}; it is read as far as the file goes.");
                SegmentEnd = fileLength;
            }
        }

        await WalkSegmentHeadAsync(ct).ConfigureAwait(false);
        _in.ReadAhead = EbmlInput.MaxEnsure;
        BuildTracks();
        if (_in.CanSeek && _firstClusterPos >= 0)
        {
            _in.SeekTo(_firstClusterPos);
        }
    }

    private async ValueTask WalkSegmentHeadAsync(CancellationToken ct)
    {
        byte[]? tracksBytes = null;
        long tracksOffset = 0;
        bool haveInfo = false;
        bool triedSeekHead = false;
        while (_in.Position < SegmentEnd)
        {
            ElementHeader h = await PeekHeaderAsync(ct).ConfigureAwait(false);
            if (h.Status == HeaderStatus.Eof)
            {
                break;
            }

            if (h.Status == HeaderStatus.Invalid)
            {
                _defects.Add(DefectKind.InvalidSize, DefectSeverity.Error, h.Offset, "An element header directly under the Segment is not valid EBML; scanning for the next Segment-level element.");
                if (!await ScanForLevel1Async(ct).ConfigureAwait(false))
                {
                    break;
                }

                continue;
            }

            if (h.Id == MatroskaIds.Cluster)
            {
                if (_firstClusterPos < 0)
                {
                    _firstClusterPos = h.Offset;
                }

                if (tracksBytes is not null)
                {
                    break;
                }

                if (!_in.CanSeek)
                {
                    throw new MediaFormatException("The first Cluster comes before the Tracks element, which a forward-only source cannot go back for.") { Offset = h.Offset, Kind = DefectKind.InvalidOrder };
                }

                // Tracks are somewhere else: the SeekHead says where, otherwise the walk below goes on past the Clusters, reading just
                // each Cluster's header (a small read) before hopping over its body.
                _in.ReadAhead = 512;
                if (!triedSeekHead)
                {
                    triedSeekHead = true;
                    (tracksBytes, tracksOffset, haveInfo) = await LoadFromSeekHeadAsync(haveInfo, ct).ConfigureAwait(false);
                    if (tracksBytes is not null)
                    {
                        break;
                    }

                    // The SeekHead lookups moved the input and gave nothing; the header peeked above is stale. Look again from the Cluster.
                    continue;
                }
            }

            switch (h.Id)
            {
                case MatroskaIds.SeekHead:
                    byte[]? seekHead = await ReadPayloadAsync(h, "SeekHead", ct).ConfigureAwait(false);
                    if (seekHead is not null)
                    {
                        MatroskaParser.VerifyCrc(seekHead, h.PayloadOffset, _defects, "SeekHead");
                        _seekEntries.AddRange(MatroskaParser.ParseSeekHead(seekHead, h.PayloadOffset, _defects, _limits));
                        ApplyCuesEntry();
                    }

                    break;
                case MatroskaIds.Info:
                    byte[]? info = await ReadPayloadAsync(h, "Info", ct).ConfigureAwait(false);
                    if (info is not null)
                    {
                        MatroskaParser.VerifyCrc(info, h.PayloadOffset, _defects, "Info");
                        Info = MatroskaParser.ParseInfo(info, h.PayloadOffset, _defects);
                        haveInfo = true;
                    }

                    break;
                case MatroskaIds.Tracks:
                    tracksBytes = await ReadPayloadAsync(h, "Tracks", ct).ConfigureAwait(false);
                    tracksOffset = h.PayloadOffset;
                    break;
                case MatroskaIds.Cues:
                    if (_cuesPos < 0)
                    {
                        _cuesPos = h.Offset;
                    }

                    await SkipElementAsync(h, ct).ConfigureAwait(false);
                    break;
                case MatroskaIds.Chapters:
                    Chapters = new ByteRange(h.Offset, h.HeaderLength + h.Size);
                    await SkipElementAsync(h, ct).ConfigureAwait(false);
                    break;
                case MatroskaIds.Tags:
                    Tags = new ByteRange(h.Offset, h.HeaderLength + h.Size);
                    await SkipElementAsync(h, ct).ConfigureAwait(false);
                    break;
                case MatroskaIds.Attachments:
                    Attachments = new ByteRange(h.Offset, h.HeaderLength + h.Size);
                    await SkipElementAsync(h, ct).ConfigureAwait(false);
                    break;
                default:
                    // Void, CRC-32, a Cluster being walked past, and elements this reader does not know. An unknown-size element
                    // is entered rather than skipped, so its children are met one by one.
                    await SkipElementAsync(h, ct).ConfigureAwait(false);
                    break;
            }
        }

        if (tracksBytes is null && _in.CanSeek && !triedSeekHead)
        {
            (tracksBytes, tracksOffset, haveInfo) = await LoadFromSeekHeadAsync(haveInfo, ct).ConfigureAwait(false);
        }

        if (tracksBytes is null)
        {
            throw new MediaFormatException("The Segment has no Tracks element.") { Offset = SegmentStart, Kind = DefectKind.MissingRequired };
        }

        MatroskaParser.VerifyCrc(tracksBytes, tracksOffset, _defects, "Tracks");
        List<MatroskaTrackEntry> entries = MatroskaParser.ParseTracks(tracksBytes, tracksOffset, _defects, _limits);
        Entries = entries;
    }

    /// <summary>Notes where the Cues are from the SeekHead entries read so far.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ApplyCuesEntry()
    {
        if (_cuesPos >= 0)
        {
            return;
        }

        foreach (MatroskaSeekEntry e in _seekEntries)
        {
            if (e.Id == MatroskaIds.Cues)
            {
                _cuesPos = SegmentStart + e.Position;
                return;
            }
        }
    }

    /// <summary>Reads SeekHeads that other SeekHeads point at (a second SeekHead at the end of the file is common), once, and restores the position.</summary>
    private async ValueTask FollowSeekHeadChainAsync(CancellationToken ct)
    {
        if (_seekHeadChainFollowed)
        {
            return;
        }

        _seekHeadChainFollowed = true;
        long resume = _in.Position;
        try
        {
            HashSet<long> visited = [];
            for (int index = 0; index < _seekEntries.Count && visited.Count < 8; index++)
            {
                MatroskaSeekEntry e = _seekEntries[index];
                if (e.Id != MatroskaIds.SeekHead || !visited.Add(e.Position))
                {
                    continue;
                }

                ElementHeader sh = await SeekHeaderAsync(e.Position, MatroskaIds.SeekHead, ct).ConfigureAwait(false);
                if (sh.Status == HeaderStatus.Ok)
                {
                    byte[]? bytes = await ReadPayloadAsync(sh, "SeekHead", ct).ConfigureAwait(false);
                    if (bytes is not null)
                    {
                        _seekEntries.AddRange(MatroskaParser.ParseSeekHead(bytes, sh.PayloadOffset, _defects, _limits));
                    }
                }
            }
        }
        finally
        {
            _in.SeekTo(resume);
        }

        ApplyCuesEntry();
    }

    /// <summary>Loads Tracks and Info from the positions the SeekHeads give.</summary>
    private async ValueTask<(byte[]? Tracks, long TracksOffset, bool HaveInfo)> LoadFromSeekHeadAsync(bool haveInfo, CancellationToken ct)
    {
        await FollowSeekHeadChainAsync(ct).ConfigureAwait(false);
        long resume = _in.Position;
        byte[]? tracks = null;
        long tracksOffset = 0;
        try
        {
            int count = _seekEntries.Count;
            for (int i = 0; i < count; i++)
            {
                MatroskaSeekEntry e = _seekEntries[i];
                if (e.Id == MatroskaIds.Tracks && tracks is null)
                {
                    ElementHeader th = await SeekHeaderAsync(e.Position, MatroskaIds.Tracks, ct).ConfigureAwait(false);
                    if (th.Status == HeaderStatus.Ok)
                    {
                        tracks = await ReadPayloadAsync(th, "Tracks", ct).ConfigureAwait(false);
                        tracksOffset = th.PayloadOffset;
                    }
                }
                else if (e.Id == MatroskaIds.Info && !haveInfo)
                {
                    ElementHeader ih = await SeekHeaderAsync(e.Position, MatroskaIds.Info, ct).ConfigureAwait(false);
                    if (ih.Status == HeaderStatus.Ok)
                    {
                        byte[]? bytes = await ReadPayloadAsync(ih, "Info", ct).ConfigureAwait(false);
                        if (bytes is not null)
                        {
                            MatroskaParser.VerifyCrc(bytes, ih.PayloadOffset, _defects, "Info");
                            Info = MatroskaParser.ParseInfo(bytes, ih.PayloadOffset, _defects);
                            haveInfo = true;
                        }
                    }
                }
            }
        }
        finally
        {
            _in.SeekTo(resume);
        }

        return (tracks, tracksOffset, haveInfo);
    }

    /// <summary>Moves to a SeekPosition (relative to the Segment data) and reads the header there, checking it is the element the SeekHead promised.</summary>
    private async ValueTask<ElementHeader> SeekHeaderAsync(long segmentRelative, uint expectedId, CancellationToken ct)
    {
        long target = SegmentStart + segmentRelative;
        long length = _in.Length;
        if (segmentRelative < 0 || (length >= 0 && target >= length))
        {
            _defects.Add(DefectKind.SizeExceedsFile, DefectSeverity.Warning, target, "A SeekHead entry points past the end of the file and is ignored.");
            return new ElementHeader(HeaderStatus.Invalid, target, 0, 0, 0);
        }

        _in.SeekTo(target);
        ElementHeader h = await PeekHeaderAsync(ct).ConfigureAwait(false);
        if (h.Status != HeaderStatus.Ok || h.Id != expectedId)
        {
            _defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, target, $"A SeekHead entry for {MatroskaIds.NameOf(expectedId)} points at something else; it is ignored.");
            return new ElementHeader(HeaderStatus.Invalid, target, 0, 0, 0);
        }

        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void BuildTracks()
    {
        Clock = MatroskaClock.FromTimestampScale(Info.TimestampScale);
        List<TrackState> states = new(Entries.Count);
        List<MediaTrack> tracks = new(Entries.Count);
        for (int i = 0; i < Entries.Count; i++)
        {
            MatroskaTrackEntry e = Entries[i];
            MatroskaCodecs.Map(e.CodecId, out CodecId codec, out string? format, out string? tag);
            if (codec.IsUnknown && format == "bitmapinfoheader" && MatroskaCodecs.TryFromBitmapInfoHeader(e.CodecPrivate.Span, out CodecId vfwCodec, out string? fourcc))
            {
                codec = vfwCodec;
                tag = fourcc;
            }

            TrackKind kind = MatroskaCodecs.KindOf(e.TrackType);
            TrackDisposition disposition = TrackDisposition.None;
            if (e.Default) { disposition |= TrackDisposition.Default; }
            if (e.Forced) { disposition |= TrackDisposition.Forced; }
            if (e.Original) { disposition |= TrackDisposition.Original; }
            if (e.Commentary) { disposition |= TrackDisposition.Commentary; }
            if (e.HearingImpaired) { disposition |= TrackDisposition.HearingImpaired; }
            if (e.VisualImpaired) { disposition |= TrackDisposition.VisualImpaired; }
            if (!e.Enabled) { disposition |= TrackDisposition.Disabled; }

            VideoTrackInfo? video = null;
            if (e.Video is { } v)
            {
                (int parN, int parD) = PixelAspect(v);
                (int fpsN, int fpsD) = FrameRate(e.DefaultDurationNs);
                video = new VideoTrackInfo(Clamp(v.PixelWidth), Clamp(v.PixelHeight), 0, parN, parD, fpsN, fpsD);
            }

            AudioTrackInfo? audio = e.Audio is { } a ? new AudioTrackInfo(Clamp(a.OutputSamplingFrequency > 0 ? a.OutputSamplingFrequency : a.SamplingFrequency), Clamp(a.Channels), Clamp(a.BitDepth)) : null;
            tracks.Add(new MediaTrack
            {
                Index = i,
                Id = e.TrackNumber,
                Kind = kind,
                Codec = codec,
                CodecTag = tag,
                Configuration = e.CodecPrivate.IsEmpty && format is null ? default : new CodecConfiguration(e.CodecPrivate, format),
                Timescale = Clock.Timescale,
                Edit = e.CodecDelayNs > 0 ? new TrackEditInfo(Priming: new MediaTime(e.CodecDelayNs, MatroskaClock.NanosecondsPerSecond)) : default,
                Language = e.LanguageBcp47 ?? e.Language,
                Name = e.Name,
                Disposition = disposition,
                Audio = audio,
                Video = video,
                IsProtected = e.IsEncrypted,
            });
            states.Add(new TrackState { Entry = e, IsVideo = kind == TrackKind.Video, Enabled = true, Prefix = e.HeaderStripPrefix.ToArray(), Zlib = e.UsesZlib, DefaultDurationNs = e.DefaultDurationNs, CodecDelayTicks = Clock.NanosecondsToNearestTicks(e.CodecDelayNs) });
            if (!_trackByNumber.TryAdd(e.TrackNumber, i))
            {
                _defects.Add(DefectKind.Duplicate, DefectSeverity.Warning, e.Offset, $"TrackNumber {e.TrackNumber} is used by more than one track; Blocks go to the first.");
            }
        }

        _states = [.. states];
        Tracks = [.. tracks];
    }

    private static int Clamp(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    // RFC 9559 §5.1.4.1.28.14-.16: the display size over the pixel size gives the pixel aspect ratio (a DisplayUnit of 3 is the display aspect ratio).
    private static (int, int) PixelAspect(MatroskaVideo v)
    {
        if (v.DisplayWidth > 0 && v.DisplayHeight > 0 && v.PixelWidth > 0 && v.PixelHeight > 0 && v.DisplayUnit is 0 or 3)
        {
            long n = v.DisplayWidth * v.PixelHeight;
            long d = v.DisplayHeight * v.PixelWidth;
            long g = Gcd(n, d);
            n /= g;
            d /= g;
            if (n <= int.MaxValue && d <= int.MaxValue)
            {
                return ((int)n, (int)d);
            }
        }

        return (1, 1);
    }

    private static (int, int) FrameRate(long defaultDurationNs)
    {
        if (defaultDurationNs <= 0)
        {
            return (0, 0);
        }

        long g = Gcd(MatroskaClock.NanosecondsPerSecond, defaultDurationNs);
        long n = MatroskaClock.NanosecondsPerSecond / g;
        long d = defaultDurationNs / g;
        return n <= int.MaxValue && d <= int.MaxValue ? ((int)n, (int)d) : (0, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a == 0 ? 1 : a;
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Resync

    private static readonly byte[][] SegmentPattern = [[0x18, 0x53, 0x80, 0x67]];

    // The IDs of the elements directly under a Segment (RFC 9559 §5.1): SeekHead, Info, Tracks, Cues, Cluster, Chapters, Tags, Attachments.
    private static readonly byte[][] Level1Patterns =
    [
        [0x11, 0x4D, 0x9B, 0x74],
        [0x15, 0x49, 0xA9, 0x66],
        [0x16, 0x54, 0xAE, 0x6B],
        [0x1C, 0x53, 0xBB, 0x6B],
        [0x1F, 0x43, 0xB6, 0x75],
        [0x10, 0x43, 0xA7, 0x70],
        [0x12, 0x54, 0xC3, 0x67],
        [0x19, 0x41, 0xA4, 0x69],
    ];

    private static int FindEarliest(ReadOnlySpan<byte> window, byte[][] patterns)
    {
        int best = -1;
        foreach (byte[] pattern in patterns)
        {
            int index = window.IndexOf(pattern);
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
            }
        }

        return best;
    }

    /// <summary>Scans forward for one of the given 4-byte IDs, leaving the input positioned at the earliest match.</summary>
    private async ValueTask<bool> ScanForAsync(byte[][] patterns, CancellationToken ct)
    {
        while (true)
        {
            if (!await _in.EnsureAsync(MaxHeaderBytes, ct).ConfigureAwait(false) && _in.Buffered.Length < 4)
            {
                return false;
            }

            int index = FindEarliest(_in.Buffered, patterns);
            if (index >= 0)
            {
                _in.Advance(index);
                return true;
            }

            // Keep the last three bytes: an ID may straddle the buffer end.
            _in.Advance(Math.Max(1, _in.Buffered.Length - 3));
        }
    }

    /// <summary>
    /// After damage, finds the next plausible element directly under the Segment (RFC 9559 §5.1; a Cluster, usually): one of their IDs
    /// followed by a size that fits the Segment. Reports the skipped bytes as a defect.
    /// </summary>
    private async ValueTask<bool> ScanForLevel1Async(CancellationToken ct)
    {
        long start = _in.Position;
        while (true)
        {
            if (!await ScanForAsync(Level1Patterns, ct).ConfigureAwait(false))
            {
                _defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Error, start, "No further Segment-level element was found after damaged data; the rest of the file is ignored.");
                _in.Advance(_in.Buffered.Length);
                _inCluster = false;
                return false;
            }

            ElementHeader h = await PeekHeaderAsync(ct).ConfigureAwait(false);
            if (h.Status == HeaderStatus.Ok && h.Offset > start - 1 && (h.Size < 0 ? h.Id == MatroskaIds.Cluster : h.End <= SegmentEnd))
            {
                _defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Warning, start, $"Skipped {h.Offset - start} bytes of damaged data and resumed at the {MatroskaIds.NameOf(h.Id)} at offset {h.Offset}.");
                _inCluster = false;
                return true;
            }

            _in.Advance(1);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Packets

    public async ValueTask<bool> ReadPacketAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_frameIndex < _frameCount)
            {
                EmitFrame();
                return true;
            }

            if (!await StepAsync(ct).ConfigureAwait(false))
            {
                return false;
            }
        }
    }

    /// <summary>Reads one element at the current position and acts on it. Returns false at the end of the data.</summary>
    private async ValueTask<bool> StepAsync(CancellationToken ct)
    {
        if (_inCluster && _clusterEnd != long.MaxValue && _in.Position >= _clusterEnd)
        {
            _inCluster = false;
        }

        ElementHeader h = await PeekHeaderAsync(ct).ConfigureAwait(false);
        if (h.Status == HeaderStatus.Eof)
        {
            return false;
        }

        if (h.Status == HeaderStatus.Invalid)
        {
            _defects.Add(DefectKind.InvalidSize, DefectSeverity.Error, h.Offset, "An element header is not valid EBML; scanning for the next Segment-level element.");
            return await ScanForLevel1Async(ct).ConfigureAwait(false);
        }

        bool topLevel = MatroskaIds.IsTopLevelOrSegmentChild(h.Id);
        if (!_inCluster || topLevel)
        {
            if (_inCluster && _clusterEnd != long.MaxValue && h.Offset < _clusterEnd)
            {
                _defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, h.Offset, $"A {MatroskaIds.NameOf(h.Id)} starts inside a Cluster that was declared to run to {_clusterEnd}; the Cluster is treated as ended.");
            }

            _inCluster = false;
            if (h.Id == MatroskaIds.Cluster)
            {
                _in.Advance(h.HeaderLength);
                _inCluster = true;
                _clusterEnd = h.End;
                _clusterTimestamp = 0;
                return true;
            }

            if (h.Id == MatroskaIds.Segment)
            {
                // A second Segment (concatenated recordings): enter it and keep reading its Clusters.
                _in.Advance(h.HeaderLength);
                return true;
            }

            if (h.Size < 0)
            {
                _in.Advance(h.HeaderLength);
                return true;
            }

            await SkipElementAsync(h, ct).ConfigureAwait(false);
            return true;
        }

        if (h.Size >= 0 && _clusterEnd != long.MaxValue && h.End > _clusterEnd)
        {
            _defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Error, h.Offset, $"An element inside a Cluster runs to {h.End}, past the Cluster's end at {_clusterEnd}; scanning for the next Segment-level element.");
            return await ScanForLevel1Async(ct).ConfigureAwait(false);
        }

        switch (h.Id)
        {
            case MatroskaIds.Timestamp:
                if (h.Size is < 0 or > 8 || !await _in.EnsureAsync(h.HeaderLength + (int)h.Size, ct).ConfigureAwait(false))
                {
                    _defects.Add(DefectKind.InvalidValue, DefectSeverity.Error, h.Offset, "The Cluster Timestamp is not a valid unsigned integer.");
                    await SkipElementAsync(h, ct).ConfigureAwait(false);
                    return true;
                }

                Ebml.TryReadUnsigned(_in.Buffered.Slice(h.HeaderLength, (int)h.Size), out ulong ts);
                _clusterTimestamp = (long)Math.Min(ts, long.MaxValue);
                _in.Advance(h.HeaderLength + (int)h.Size);
                return true;
            case MatroskaIds.SimpleBlock:
                return await ReadBlockElementAsync(h, group: false, ct).ConfigureAwait(false);
            case MatroskaIds.BlockGroup:
                return await ReadBlockElementAsync(h, group: true, ct).ConfigureAwait(false);
            default:
                if (h.Size < 0)
                {
                    _defects.Add(DefectKind.InvalidSize, DefectSeverity.Error, h.Offset, $"An element inside a Cluster has an unknown size; scanning for the next Segment-level element.");
                    return await ScanForLevel1Async(ct).ConfigureAwait(false);
                }

                await SkipElementAsync(h, ct).ConfigureAwait(false);
                return true;
        }
    }

    private async ValueTask<bool> ReadBlockElementAsync(ElementHeader h, bool group, CancellationToken ct)
    {
        if (h.Size < 0)
        {
            _defects.Add(DefectKind.InvalidSize, DefectSeverity.Error, h.Offset, "A block element has an unknown size; scanning for the next Segment-level element.");
            return await ScanForLevel1Async(ct).ConfigureAwait(false);
        }

        _limits.CheckPacket(h.Size, h.Offset);
        if (!group)
        {
            // A disabled or undeclared track's blocks are skipped without reading their payload.
            await _in.EnsureAsync(h.HeaderLength + (int)Math.Min(h.Size, 8), ct).ConfigureAwait(false);
            if (SkipBlockOfTrack(_in.Buffered[h.HeaderLength..], h))
            {
                await SkipElementAsync(h, ct).ConfigureAwait(false);
                return true;
            }
        }

        _in.Advance(h.HeaderLength);
        int size = (int)h.Size;
        long length = _in.Length;
        if (length >= 0 && h.PayloadOffset + size > length)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Error, h.Offset, $"The file ends {Math.Max(0, length - h.PayloadOffset)} bytes into a {size}-byte block; the partial block is dropped.");
            await _in.SkipAsync(Math.Max(0, length - h.PayloadOffset), ct).ConfigureAwait(false);
            return true;
        }

        if (!await FillBlockBufferAsync(size, ct).ConfigureAwait(false))
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Error, h.Offset, "The data ends inside a block; the partial block is dropped.");
            return true;
        }

        _blkFileOffset = h.PayloadOffset;
        if (group)
        {
            ParseBlockGroup(size);
        }
        else
        {
            SetUpBlock(0, size, key: null, durationUnits: -1, discardPaddingNs: 0, simple: true);
        }

        return true;
    }

    /// <summary>True when a block should be dropped before its payload is read: its track number is unknown or the track is disabled.</summary>
    private bool SkipBlockOfTrack(ReadOnlySpan<byte> payload, ElementHeader h)
    {
        if (!Ebml.TryReadVint(payload, out ulong number, out _))
        {
            return false; // let the full parse report the problem
        }

        if (_trackByNumber.TryGetValue((long)number, out int index))
        {
            return !_states[index].Enabled;
        }

        WarnUnknownTrack((long)number, h.Offset);
        return true;
    }

    private void WarnUnknownTrack(long number, long offset)
    {
        if (_warnedTracks.Count < 16 && _warnedTracks.Add(number))
        {
            _defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, offset, $"A block refers to track {number}, which the Tracks element does not declare; its blocks are skipped.");
        }
    }

    /// <summary>Reads <paramref name="size"/> bytes into the block buffer, growing it as bytes actually arrive so that a false size cannot force a huge allocation.</summary>
    private async ValueTask<bool> FillBlockBufferAsync(int size, CancellationToken ct)
    {
        int have = 0;
        if (_blk.Length < size)
        {
            // One megabyte up front holds the blocks of nearly every stream, so the buffer is allocated once, at the first block; a block
            // that is larger grows it by doubling, and only as far as bytes actually arrive.
            int target = size <= InitialBlockBuffer ? InitialBlockBuffer : Math.Min(size, Math.Max(_blk.Length * 2, InitialBlockBuffer));
            Array.Resize(ref _blk, target);
        }

        while (have < size)
        {
            if (_blk.Length < size && have == _blk.Length)
            {
                Array.Resize(ref _blk, (int)Math.Min(size, (long)_blk.Length * 2));
            }

            int want = Math.Min(Math.Min(_blk.Length, size) - have, ReadChunk);
            int n = await _in.ReadAsync(_blk.AsMemory(have, want), ct).ConfigureAwait(false);
            if (n < want)
            {
                return false;
            }

            have += n;
        }

        return true;
    }

    private void ParseBlockGroup(int size)
    {
        // RFC 9559 §5.1.3.5: a BlockGroup holds one Block plus the facts about it. The Block can come before or after BlockDuration and
        // ReferenceBlock, so the whole group is read first.
        EbmlChildren c = new(_blk.AsSpan(0, size), _blkFileOffset, _defects);
        int blockStart = -1, blockLength = 0;
        long duration = -1;
        long discardPadding = 0;
        bool hasReference = false;
        while (c.MoveNext())
        {
            switch (c.Id)
            {
                case MatroskaIds.Block when blockStart < 0:
                    blockStart = (int)(c.PayloadOffset - _blkFileOffset);
                    blockLength = c.Payload.Length;
                    break;
                case MatroskaIds.BlockDuration:
                    if (Ebml.TryReadUnsigned(c.Payload, out ulong d) && d <= long.MaxValue)
                    {
                        duration = (long)d;
                    }

                    break;
                case MatroskaIds.ReferenceBlock:
                    hasReference = true;
                    break;
                case MatroskaIds.DiscardPadding:
                    Ebml.TryReadSigned(c.Payload, out discardPadding);
                    break;
                default: break;
            }
        }

        if (blockStart < 0)
        {
            _defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, _blkFileOffset - 1, "A BlockGroup holds no Block.");
            return;
        }

        SetUpBlock(blockStart, blockLength, key: !hasReference, duration, discardPadding, simple: false);
    }

    /// <summary>
    /// Parses a Block header (RFC 9559 §10.1: track number, 16-bit relative timestamp, flags) and the lacing (§10.3) into frame slots.
    /// <paramref name="key"/> is null for a SimpleBlock, whose own flag says.
    /// </summary>
    private void SetUpBlock(int start, int length, bool? key, long durationUnits, long discardPaddingNs, bool simple)
    {
        ReadOnlySpan<byte> block = _blk.AsSpan(start, length);
        long blockOffset = _blkFileOffset + start;
        if (!Ebml.TryReadVint(block, out ulong number, out int numberLength) || block.Length < numberLength + 3)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, blockOffset, "A block is too short for its header and is dropped.");
            return;
        }

        if (!_trackByNumber.TryGetValue((long)number, out int trackIndex))
        {
            WarnUnknownTrack((long)number, blockOffset);
            return;
        }

        TrackState state = _states[trackIndex];
        if (!state.Enabled)
        {
            return;
        }

        short relative = BinaryPrimitives.ReadInt16BigEndian(block[numberLength..]);
        byte flags = block[numberLength + 2];
        int dataStart = start + numberLength + 3;
        int dataLength = length - numberLength - 3;

        // RFC 9559 §10.3: the two lacing bits of the flags (0x06).
        int lacing = (flags >> 1) & 3;
        int frames = 1;
        if (lacing == 0)
        {
            EnsureSlots(1);
            _slots[0] = (dataStart, dataLength);
        }
        else if (!TryLace(lacing, dataStart, dataLength, blockOffset, out frames))
        {
            return;
        }

        _bTrack = trackIndex;
        // RFC 9559 §11.2: a track's CodecDelay is subtracted from each of its Block timestamps, so the first packet of an Opus or AAC
        // track starts before zero by the delay the decoder will discard.
        _bPtsTicks = Clock.ToTicks(_clusterTimestamp + relative) - state.CodecDelayTicks;
        _bDurationTicks = durationUnits >= 0 ? Clock.ToTicks(durationUnits) : -1;
        _bDiscardPaddingNs = discardPaddingNs;
        _bKey = key ?? ((flags & 0x80) != 0);
        _bInvisible = simple && (flags & 0x08) != 0;
        _bDisposable = simple && (flags & 0x01) != 0;
        _frameCount = frames;
        _frameIndex = 0;
    }

    private void EnsureSlots(int count)
    {
        if (_slots.Length < count)
        {
            Array.Resize(ref _slots, Math.Max(count, _slots.Length * 2));
        }
    }

    private bool TryLace(int lacing, int dataStart, int dataLength, long blockOffset, out int frames)
    {
        frames = 0;
        ReadOnlySpan<byte> data = _blk.AsSpan(dataStart, dataLength);
        if (data.IsEmpty)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, blockOffset, "A laced block has no lacing header and is dropped.");
            return false;
        }

        int count = data[0] + 1;
        int pos = 1;
        if (count > _limits.MaxLacedFrames)
        {
            throw new MediaLimitExceededException(nameof(MediaLimits.MaxLacedFrames), _limits.MaxLacedFrames, count, blockOffset);
        }

        EnsureSlots(count);
        long consumed = 0;
        if (lacing == 2)
        {
            // Fixed-size lacing (§10.3.4): the payload divides evenly.
            int rest = dataLength - pos;
            if (rest % count != 0)
            {
                _defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, blockOffset, $"A fixed-size laced block of {rest} bytes does not divide into {count} frames; it is dropped.");
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                _slots[i] = (dataStart + pos + (i * (rest / count)), rest / count);
            }

            frames = count;
            return true;
        }

        if (_sizes.Length < count)
        {
            _sizes = new long[Math.Max(count, _sizes.Length * 2)];
        }

        long[] sizes = _sizes;
        if (lacing == 1)
        {
            // Xiph lacing (§10.3.2): sizes in runs of 255.
            for (int i = 0; i < count - 1; i++)
            {
                long size = 0;
                while (true)
                {
                    if (pos >= data.Length)
                    {
                        _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, blockOffset, "A Xiph-laced block ends inside its lacing header and is dropped.");
                        return false;
                    }

                    byte b = data[pos++];
                    size += b;
                    if (b != 255)
                    {
                        break;
                    }
                }

                sizes[i] = size;
                consumed += size;
            }
        }
        else
        {
            // EBML lacing (§10.3.3): the first size is a vint, the rest are signed differences.
            long previous = 0;
            for (int i = 0; i < count - 1; i++)
            {
                if (!Ebml.TryReadVint(data[pos..], out ulong raw, out int len))
                {
                    _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, blockOffset, "An EBML-laced block ends inside its lacing header and is dropped.");
                    return false;
                }

                pos += len;
                long size = i == 0 ? (long)raw : previous + ((long)raw - ((1L << ((7 * len) - 1)) - 1));
                if (size < 0)
                {
                    _defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, blockOffset, "An EBML-laced block has a negative frame size and is dropped.");
                    return false;
                }

                sizes[i] = size;
                previous = size;
                consumed += size;
            }
        }

        long last = dataLength - pos - consumed;
        if (last < 0)
        {
            _defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, blockOffset, "The frame sizes of a laced block add up to more than the block holds; it is dropped.");
            return false;
        }

        sizes[count - 1] = last;
        int at = dataStart + pos;
        for (int i = 0; i < count; i++)
        {
            _slots[i] = (at, (int)sizes[i]);
            at += (int)sizes[i];
        }

        frames = count;
        return true;
    }

    /// <summary>Turns frame <c>_frameIndex</c> of the current block into the current packet.</summary>
    private void EmitFrame()
    {
        int i = _frameIndex++;
        TrackState state = _states[_bTrack];
        (int offset, int length) = _slots[i];
        int count = _frameCount;

        // Timing. RFC 9559 §10.3.5: only the first frame of a lace has a timestamp; the others follow contiguously, so their offsets
        // come from the frame duration when the track declares one, or from sharing the BlockDuration out evenly. Offsets are cumulative
        // roundings, so frame durations add up exactly to the block's.
        long ptsTicks = _bPtsTicks;
        long durationTicks;
        if (count == 1)
        {
            durationTicks = _bDurationTicks >= 0 ? _bDurationTicks : state.DefaultDurationNs > 0 ? Clock.NanosecondsToTicks(state.DefaultDurationNs) : -1;
        }
        else if (_bDurationTicks >= 0 || state.DefaultDurationNs > 0)
        {
            // The lace's total is the BlockDuration, else the frame count times DefaultDuration; frames split it by cumulative floor,
            // so their durations add up to the total exactly. This is the split ffprobe shows for the same files.
            long total = _bDurationTicks >= 0 ? _bDurationTicks : Clock.NanosecondsToTicks(count * state.DefaultDurationNs);
            long start = i * total / count;
            long end = (i + 1L) * total / count;
            ptsTicks += start;
            durationTicks = end - start;
        }
        else
        {
            durationTicks = -1;
        }

        PacketAttributes attributes = PacketAttributes.None;
        if (_bKey) { attributes |= PacketAttributes.Keyframe; }
        if (_bInvisible) { attributes |= PacketAttributes.Discard; }
        if (_bDisposable) { attributes |= PacketAttributes.Disposable; }

        // RFC 9559 §5.1.3.5.7: positive DiscardPadding trims the end of the block's output, negative the start.
        MediaTime discard = default;
        if ((_bDiscardPaddingNs > 0 && i == count - 1) || (_bDiscardPaddingNs < 0 && i == 0))
        {
            discard = new MediaTime(_bDiscardPaddingNs, MatroskaClock.NanosecondsPerSecond);
        }

        MediaTime pts = new(ptsTicks, Clock.Timescale);
        ByteRange range;
        int size;
        if (state.Prefix.Length > 0)
        {
            // Header stripping (RFC 9559 §5.1.4.1.31.5): the removed bytes go back in front of the frame. The packet's bytes are then not a contiguous
            // range of the source; its source range still names the stored frame.
            size = state.Prefix.Length + length;
            if (_out.Length < size)
            {
                Array.Resize(ref _out, Math.Max(size, _out.Length * 2));
            }

            state.Prefix.CopyTo(_out, 0);
            Buffer.BlockCopy(_blk, offset, _out, state.Prefix.Length, length);
            _currentBuffer = _out;
            _currentOffset = 0;
            range = new ByteRange(_blkFileOffset + offset, length);
        }
        else if (state.Zlib && TryInflate(offset, length, _blkFileOffset + offset, out size))
        {
            // ContentCompAlgo 0 (RFC 9559 §5.1.4.1.31.6): the stored frame is a zlib stream. Undoing it is container framing; the source range names the stored bytes.
            _currentBuffer = _out;
            _currentOffset = 0;
            range = new ByteRange(_blkFileOffset + offset, length);
        }
        else
        {
            size = length;
            _currentBuffer = _blk;
            _currentOffset = offset;
            range = new ByteRange(_blkFileOffset + offset, length);
        }

        _currentLength = size;
        CurrentInfo = new PacketInfo(
            _bTrack,
            pts,
            // Matroska stores presentation times only. Without reordering (audio, subtitles, data) decode time is the same; for video
            // it is not stored, and is left unknown for the muxer to derive.
            state.IsVideo ? MediaTime.Unknown : pts,
            durationTicks >= 0 ? new MediaTime(durationTicks, Clock.Timescale) : MediaTime.Unknown,
            attributes,
            range,
            size,
            discard);
    }

    // Inflates one stored frame into _out, never past the packet size limit. A frame that is not valid zlib is reported and delivered as stored.
    private bool TryInflate(int offset, int length, long fileOffset, out int inflated)
    {
        inflated = 0;
        try
        {
            using MemoryStream stored = new(_blk, offset, length, writable: false);
            using System.IO.Compression.ZLibStream z = new(stored, System.IO.Compression.CompressionMode.Decompress);
            int total = 0;
            while (true)
            {
                if (total == _out.Length)
                {
                    Array.Resize(ref _out, (int)Math.Min(Math.Max(256L, _out.Length * 2L), _limits.MaxPacketBytes + 1L));
                }

                int n = z.Read(_out, total, _out.Length - total);
                if (n == 0)
                {
                    break;
                }

                total += n;
                _limits.CheckPacket(total, fileOffset);
            }

            inflated = total;
            return true;
        }
        catch (InvalidDataException)
        {
            _defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, fileOffset, "A frame of a zlib-compressed track is not a valid zlib stream; it is delivered as stored.");
            return false;
        }
    }

    public void SetTrackEnabled(int trackIndex, bool enabled)
    {
        if ((uint)trackIndex >= (uint)_states.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIndex));
        }

        _states[trackIndex].Enabled = enabled;
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Seeking

    public async ValueTask<MediaTime> SeekAsync(MediaTime target, SeekMode mode, CancellationToken ct)
    {
        if (!_in.CanSeek)
        {
            throw new NotSupportedException("The source is forward-only, so the file cannot be seeked.");
        }

        if (_firstClusterPos < 0)
        {
            return MediaTime.Unknown;
        }

        if (!_seekPointsLoaded)
        {
            await LoadSeekPointsAsync(ct).ConfigureAwait(false);
        }

        long ticks = target.ToTicks(Clock.Timescale, mode == SeekMode.PreviousKeyframe ? MediaRounding.Floor : MediaRounding.Ceiling);
        long units = ticks / Clock.TicksPerUnit;
        if (ticks % Clock.TicksPerUnit < 0)
        {
            units--; // floor, not truncation, for times before zero
        }


        _frameCount = 0;
        _frameIndex = 0;
        _inCluster = false;
        if (_seekTimes.Length == 0)
        {
            _in.SeekTo(_firstClusterPos);
            return MediaTime.Unknown;
        }

        int index = Array.BinarySearch(_seekTimes, units);
        if (index < 0)
        {
            index = ~index; // first point after the target
        }
        else
        {
            // Several points may share a time; any of them starts a valid decode.
            while (index > 0 && _seekTimes[index - 1] == units)
            {
                index--;
            }
        }

        if (mode == SeekMode.PreviousKeyframe)
        {
            if (index >= _seekTimes.Length || _seekTimes[index] != units)
            {
                index = Math.Max(0, index - 1);
            }
        }
        else if (index >= _seekTimes.Length)
        {
            // Nothing at or after the target: the end of the data.
            _in.SeekTo(Math.Min(SegmentEnd, _in.Length >= 0 ? _in.Length : SegmentEnd));
            return MediaTime.Unknown;
        }

        _in.SeekTo(_seekPositions[index]);
        return Clock.Time(_seekTimes[index]);
    }

    /// <summary>
    /// Builds the seek table: the Cues when the file has them, otherwise a walk over the Cluster headers (one small read per Cluster).
    /// </summary>
    private async ValueTask LoadSeekPointsAsync(CancellationToken ct)
    {
        _seekPointsLoaded = true;
        if (_cuesPos < 0)
        {
            await FollowSeekHeadChainAsync(ct).ConfigureAwait(false);
        }

        if (_cuesPos >= 0 && await TryLoadCuesAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        await WalkClustersAsync(ct).ConfigureAwait(false);
    }

    private async ValueTask<bool> TryLoadCuesAsync(CancellationToken ct)
    {
        long length = _in.Length;
        if (length >= 0 && _cuesPos >= length)
        {
            _defects.Add(DefectKind.SizeExceedsFile, DefectSeverity.Warning, _cuesPos, "The Cues position is past the end of the file; seek points come from the Clusters instead.");
            return false;
        }

        _in.SeekTo(_cuesPos);
        ElementHeader h = await PeekHeaderAsync(ct).ConfigureAwait(false);
        if (h.Status != HeaderStatus.Ok || h.Id != MatroskaIds.Cues)
        {
            _defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, _cuesPos, "No Cues element is at the position the SeekHead gave; seek points come from the Clusters instead.");
            return false;
        }

        byte[]? bytes = await ReadPayloadAsync(h, "Cues", ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return false;
        }

        MatroskaParser.VerifyCrc(bytes, h.PayloadOffset, _defects, "Cues");
        List<MatroskaCuePoint> cues = MatroskaParser.ParseCues(bytes, h.PayloadOffset, _defects, _limits);

        // Seek by the first video track that has cues, otherwise the first track that has any.
        long chosen = -1;
        foreach (TrackState s in _states)
        {
            if (!s.Enabled)
            {
                continue;
            }

            if (cues.Exists(c => c.Track == s.Entry.TrackNumber))
            {
                if (s.IsVideo)
                {
                    chosen = s.Entry.TrackNumber;
                    break;
                }

                if (chosen < 0)
                {
                    chosen = s.Entry.TrackNumber;
                }
            }
        }

        List<(long Time, long Position)> points = [];
        foreach (MatroskaCuePoint c in cues)
        {
            if (c.Track == chosen)
            {
                points.Add((c.Time, SegmentStart + c.ClusterPosition));
            }
        }

        if (points.Count == 0)
        {
            return false;
        }

        points.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Position.CompareTo(b.Position));
        _seekTimes = new long[points.Count];
        _seekPositions = new long[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            _seekTimes[i] = points[i].Time;
            _seekPositions[i] = points[i].Position;
        }

        return true;
    }

    /// <summary>
    /// The fallback when a file has no usable Cues: visit each Cluster header, read its Timestamp, and hop to the next by its size. A
    /// Cluster of unknown size is walked child by child. Seeking then lands on a Cluster start, which a writer need not align with a keyframe.
    /// </summary>
    private async ValueTask WalkClustersAsync(CancellationToken ct)
    {
        List<long> times = [];
        List<long> positions = [];
        int savedReadAhead = _in.ReadAhead;
        _in.ReadAhead = 512;
        try
        {
            _in.SeekTo(_firstClusterPos);
            while (_in.Position < SegmentEnd)
            {
                ElementHeader h = await PeekHeaderAsync(ct).ConfigureAwait(false);
                if (h.Status != HeaderStatus.Ok)
                {
                    break;
                }

                if (h.Id != MatroskaIds.Cluster)
                {
                    if (h.Size < 0)
                    {
                        break;
                    }

                    await SkipElementAsync(h, ct).ConfigureAwait(false);
                    continue;
                }

                _limits.CheckSampleTable(times.Count + 1, h.Offset);
                _in.Advance(h.HeaderLength);
                long timestamp = 0;
                for (int child = 0; child < 8; child++)
                {
                    ElementHeader c = await PeekHeaderAsync(ct).ConfigureAwait(false);
                    if (c.Status != HeaderStatus.Ok || c.Size < 0)
                    {
                        break;
                    }

                    if (c.Id == MatroskaIds.Timestamp && c.Size <= 8 && await _in.EnsureAsync(c.HeaderLength + (int)c.Size, ct).ConfigureAwait(false))
                    {
                        Ebml.TryReadUnsigned(_in.Buffered.Slice(c.HeaderLength, (int)c.Size), out ulong ts);
                        timestamp = (long)Math.Min(ts, long.MaxValue);
                        break;
                    }

                    await SkipElementAsync(c, ct).ConfigureAwait(false);
                }

                times.Add(timestamp);
                positions.Add(h.Offset);
                if (h.Size >= 0)
                {
                    _in.SeekTo(Math.Min(h.End, SegmentEnd));
                }
            }
        }
        finally
        {
            _in.ReadAhead = savedReadAhead;
        }

        long[] t = [.. times];
        long[] p = [.. positions];
        Array.Sort(t, p);
        _seekTimes = t;
        _seekPositions = p;
    }
}
