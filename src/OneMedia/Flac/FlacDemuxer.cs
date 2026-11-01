using System.Buffers.Binary;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Flac;
using RecursiveAI.OneMedia.Input;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.VorbisComment;

namespace RecursiveAI.OneMedia.Flac;

/// <summary>The type of a FLAC metadata block (RFC 9639 §8.1).</summary>
public enum FlacBlockType
{
    /// <summary>STREAMINFO (type 0): the stream's fixed properties.</summary>
    StreamInfo = 0,

    /// <summary>PADDING (type 1): empty space left for edits.</summary>
    Padding = 1,

    /// <summary>APPLICATION (type 2): data of a registered application, led by its four-byte ID.</summary>
    Application = 2,

    /// <summary>SEEKTABLE (type 3).</summary>
    SeekTable = 3,

    /// <summary>VORBIS_COMMENT (type 4).</summary>
    VorbisComment = 4,

    /// <summary>CUESHEET (type 5).</summary>
    CueSheet = 5,

    /// <summary>PICTURE (type 6).</summary>
    Picture = 6,
}

/// <summary>Where one metadata block of a native FLAC file sits.</summary>
/// <param name="TypeCode">The block type code, 0 to 126 (127 is invalid).</param>
/// <param name="IsLast">Whether the block header marks it as the last metadata block.</param>
/// <param name="Offset">Offset of the four-byte block header.</param>
/// <param name="Length">Length of the block body, which starts at <c>Offset + 4</c>.</param>
public readonly record struct FlacMetadataBlock(int TypeCode, bool IsLast, long Offset, int Length)
{
    /// <summary>The block type when it is one of the seven defined in RFC 9639; <see cref="TypeCode"/> tells for reserved ones.</summary>
    public FlacBlockType? Type => TypeCode is >= 0 and <= 6 ? (FlacBlockType)TypeCode : null;

    /// <summary>Offset of the block body.</summary>
    public long BodyOffset => Offset + FlacBlockHeader.Size;

    /// <summary>The block as a byte range, header included.</summary>
    public ByteRange Range => new(Offset, FlacBlockHeader.Size + (long)Length);
}

/// <summary>One point of a SEEKTABLE block (RFC 9639 §8.5).</summary>
/// <param name="SampleNumber">The first sample of the target frame.</param>
/// <param name="Offset">Offset of the target frame from the first frame of the stream.</param>
/// <param name="Samples">Number of samples in the target frame.</param>
public readonly record struct FlacSeekPoint(long SampleNumber, long Offset, int Samples);

/// <summary>The FLAC blocks other than the Vorbis comment, as tags of system <see cref="TagSystems.Flac"/>.</summary>
public sealed class FlacBlockTags : ITagContainer
{
    internal FlacBlockTags(ByteRange range, IReadOnlyList<NativeTag> tags)
    {
        Range = range;
        Tags = tags;
    }

    /// <inheritdoc />
    public string System => TagSystems.Flac;

    /// <inheritdoc />
    public long TrackId => 0;

    /// <inheritdoc />
    public ByteRange Range { get; }

    /// <inheritdoc />
    public IReadOnlyList<NativeTag> Tags { get; }
}

/// <summary>
/// Reads native FLAC files (RFC 9639): the metadata blocks with their ranges, then the audio frames one packet each, found by sync code and checked
/// by the header CRC-8 and, where the next frame confirms it, the frame CRC-16. Works on forward-only sources.
/// </summary>
/// <remarks>
/// An ID3v2 tag in front of the stream is skipped and exposed as a binary tag. The padding is recorded in <see cref="PaddingBlocks"/> for in-place edits.
/// Packet times are sample counts at the sample rate of STREAMINFO. Seeking uses the SEEKTABLE where there is one and otherwise bisects on frame headers.
/// The length is STREAMINFO's total sample count; when that is 0 a tail read of at most 64 KiB finds the last frame.
/// With a forward-only source the metadata area is held in memory (up to <see cref="MediaLimits.MaxAllocationBytes"/>) so that tag values can be read later.
/// </remarks>
public sealed class FlacDemuxer : IDemuxer, IMetadataProvider
{
    private const int ReadChunk = 32 * 1024;
    private const int TailBytes = 64 * 1024;

    private readonly SequentialInput _input;
    private readonly IMediaSource? _random;
    private readonly IDisposable? _owned;
    private readonly MediaLimits _limits;
    private readonly List<FlacMetadataBlock> _blocks = [];
    private readonly List<FlacSeekPoint> _seekPoints = [];
    private readonly List<ITagContainer> _containers = [];
    private IMediaSource? _retained;
    private byte[] _meta = [];
    private int _metaLength;
    private bool _retainMeta;
    private MediaTrack _track = null!;
    private FlacStreamInfo _info;
    private long _audioStart;
    private ByteRange _id3 = ByteRange.None;
    private ReadOnlyMemory<byte> _id3Bytes;
    private ByteRange _comment = ByteRange.None;

    // Frame reading state: _frame[0.._length) holds bytes from file offset _frameOffset.
    private byte[] _frame = new byte[ReadChunk * 2];
    private int _length;
    private long _frameOffset;
    private bool _eof;
    private long _nextSample = -1;
    private bool _disposed;
    private MediaTime _duration = MediaTime.Unknown;

    private FlacDemuxer(SequentialInput input, IMediaSource? random, IDisposable? owned, MediaOpenOptions options, DefectLog defects)
    {
        _input = input;
        _random = random;
        _owned = owned;
        _limits = options.Limits;
        Defects = defects;
        _retainMeta = random is null;
    }

    /// <summary>Opens a seekable source.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the metadata is read.</param>
    /// <returns>The demuxer, positioned at the first frame.</returns>
    /// <exception cref="MediaFormatException">There is no <c>fLaC</c> marker, or STREAMINFO is missing or invalid.</exception>
    public static FlacDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        return Create(new SequentialInput(source), source, options.LeaveOpen ? null : source, options, cancellationToken);
    }

    /// <summary>Opens a source that can only be read once, front to back.</summary>
    /// <param name="source">The source, at the start of the FLAC data.</param>
    /// <param name="options">Limits, defect log and ownership.</param>
    /// <param name="cancellationToken">Observed while the metadata is read.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">There is no <c>fLaC</c> marker, or STREAMINFO is missing or invalid.</exception>
    public static FlacDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        return Create(new SequentialInput(source), null, options.LeaveOpen ? null : source, options, cancellationToken);
    }

    internal static FlacDemuxer Open(IMediaSource source, ReadOnlySpan<byte> head, MediaOpenOptions options, CancellationToken cancellationToken)
    {
        SequentialInput input = new(source);
        input.Seed(head);
        return Create(input, source, options.LeaveOpen ? null : source, options, cancellationToken);
    }

    private static FlacDemuxer Create(SequentialInput input, IMediaSource? random, IDisposable? owned, MediaOpenOptions options, CancellationToken ct)
    {
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        FlacDemuxer d = new(input, random, owned, options, defects);
        try
        {
            d.ReadMetadataBlocks(ct);
            d.ReadTailLength(ct);
        }
        catch
        {
            d.Dispose();
            throw;
        }

        return d;
    }

    /// <inheritdoc />
    public MediaFormatId Format => MediaFormatId.Flac;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => [_track];

    /// <summary>The length of the audio: STREAMINFO's sample count over the rate, or from the last frame when STREAMINFO states none.</summary>
    public MediaTime Duration => _duration;

    /// <inheritdoc />
    public DefectLog Defects { get; }

    /// <inheritdoc />
    public bool CanSeek => _random is not null;

    /// <summary>The STREAMINFO block (RFC 9639 §8.2).</summary>
    public FlacStreamInfo StreamInfo => _info;

    /// <summary>Every metadata block in file order, STREAMINFO first.</summary>
    public IReadOnlyList<FlacMetadataBlock> MetadataBlocks => _blocks;

    /// <summary>The PADDING blocks, which an in-place edit may use.</summary>
    public IReadOnlyList<FlacMetadataBlock> PaddingBlocks
    {
        get
        {
            List<FlacMetadataBlock> list = [];
            foreach (FlacMetadataBlock b in _blocks)
            {
                if (b.TypeCode == (int)FlacBlockType.Padding)
                {
                    list.Add(b);
                }
            }

            return list;
        }
    }

    /// <summary>The SEEKTABLE's real points (placeholders are left out); empty when there is none.</summary>
    public IReadOnlyList<FlacSeekPoint> SeekPoints => _seekPoints;

    /// <summary>Offset of the first audio frame, after the last metadata block.</summary>
    public long AudioStartOffset => _audioStart;

    /// <summary>The ID3v2 tag in front of the <c>fLaC</c> marker, or <see cref="ByteRange.None"/>.</summary>
    public ByteRange Id3v2Prefix => _id3;

    /// <inheritdoc />
    public void SetTrackEnabled(int trackIndex, bool enabled)
    {
        // One track: disabling it leaves nothing to read, which callers can do by not reading.
    }

    // ---- metadata ----------------------------------------------------------------------------------------------

    // Reads count bytes at the current position; in forward mode they are also kept so tag values can be read later.
    private int Take(Span<byte> destination, CancellationToken ct)
    {
        int got = _input.Read(destination, ct);
        Retain(destination[..got]);
        return got;
    }

    private void Retain(ReadOnlySpan<byte> bytes)
    {
        if (!_retainMeta)
        {
            return;
        }

        if (_metaLength + (long)bytes.Length > _limits.MaxAllocationBytes)
        {
            _retainMeta = false;
            Defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, _metaLength, "The metadata area is larger than the allocation limit; values after this point cannot be read from a forward-only source.", "flac.metadata-too-large");
            return;
        }

        if (_metaLength + bytes.Length > _meta.Length)
        {
            Array.Resize(ref _meta, Math.Max(_metaLength + bytes.Length, _meta.Length * 2));
        }

        bytes.CopyTo(_meta.AsSpan(_metaLength));
        _metaLength += bytes.Length;
    }

    private void SkipBytes(long count, CancellationToken ct)
    {
        if (_random is not null)
        {
            _input.Skip(count, ct);
            return;
        }

        byte[] scratch = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            long left = count;
            while (left > 0)
            {
                int n = Take(scratch.AsSpan(0, (int)Math.Min(scratch.Length, left)), ct);
                if (n == 0)
                {
                    break;
                }

                left -= n;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private void ReadMetadataBlocks(CancellationToken ct)
    {
        Span<byte> hdr = stackalloc byte[10]; // an ID3v2 header or a block header (4 bytes)
        // ID3v2 §3.1: a tag in front of the stream starts with 'ID3', version, flags and a 28-bit syncsafe size (header 10 bytes, footer 10 more if flagged).
        if (Take(hdr[..4], ct) < 4)
        {
            throw new MediaFormatException("The data is too short to be a FLAC stream.") { Kind = DefectKind.Truncated, Offset = 0 };
        }

        if (hdr[0] == 'I' && hdr[1] == 'D' && hdr[2] == '3')
        {
            if (Take(hdr.Slice(4, 6), ct) < 6)
            {
                throw new MediaFormatException("The ID3v2 header is cut off.") { Kind = DefectKind.Truncated, Offset = 0 };
            }

            long size = 10 + (((hdr[6] & 0x7F) << 21) | ((hdr[7] & 0x7F) << 14) | ((hdr[8] & 0x7F) << 7) | (hdr[9] & 0x7F)) + ((hdr[5] & 0x10) != 0 ? 10 : 0);
            if (_random is not null && size > _random.Length)
            {
                throw new MediaFormatException("The ID3v2 tag is longer than the file.") { Kind = DefectKind.SizeExceedsFile, Offset = 0 };
            }

            if (size > _limits.MaxTagBytes)
            {
                _retainMeta = false;
            }

            _id3 = new ByteRange(0, size);
            if (size <= _limits.MaxTagBytes)
            {
                // The tag is kept so that it can be handed out as a binary tag without reading the source again.
                byte[] tag = new byte[size];
                hdr[..10].CopyTo(tag);
                int got = Take(tag.AsSpan(10), ct);
                _id3Bytes = tag.AsMemory(0, 10 + got);
            }
            else
            {
                SkipBytes(size - 10, ct);
            }

            if (Take(hdr[..4], ct) < 4)
            {
                throw new MediaFormatException("The data ends after the ID3v2 tag.") { Kind = DefectKind.Truncated, Offset = size };
            }
        }

        if (!(hdr[0] == 'f' && hdr[1] == 'L' && hdr[2] == 'a' && hdr[3] == 'C'))
        {
            throw new MediaFormatException("The FLAC marker 'fLaC' is not where the stream should start.") { Kind = DefectKind.MissingRequired, Offset = _id3.IsNone ? 0 : _id3.Length };
        }

        // Block bodies are processed as they are met; the tag source is the real source, or the retained prefix for a forward-only one.
        List<NativeTag> blockTags = [];
        TagIo? io = null;
        bool streamInfoSeen = false;
        VorbisCommentBlock? comment = null;
        bool last = false;
        byte[] body = [];
        while (!last)
        {
            ct.ThrowIfCancellationRequested();
            long at = _input.Position;
            if (Take(hdr[..FlacBlockHeader.Size], ct) < FlacBlockHeader.Size)
            {
                Defects.Add(DefectKind.Truncated, DefectSeverity.Error, at, "The data ends inside the metadata block headers.", "flac.truncated");
                break;
            }

            // RFC 9639 §8.1: one bit "last block", seven bits type, 24 bits body length.
            last = (hdr[0] & 0x80) != 0;
            int type = hdr[0] & 0x7F;
            int length = (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
            FlacMetadataBlock block = new(type, last, at, length);
            if (_random is not null && block.BodyOffset + length > _random.Length)
            {
                Defects.Add(DefectKind.SizeExceedsFile, DefectSeverity.Error, at, $"Metadata block type {type} claims {length} bytes but the file ends first.", "flac.block-size");
                break;
            }

            if (_blocks.Count == 0 && type != 0)
            {
                throw new MediaFormatException("The first metadata block is not STREAMINFO.") { Kind = DefectKind.MissingRequired, Offset = at };
            }

            if (type == 127)
            {
                Defects.Add(DefectKind.ReservedValue, DefectSeverity.Error, at, "Metadata block type 127 is invalid (RFC 9639 §8.1).", "flac.block-type");
            }

            _blocks.Add(block);
            long take = type switch
            {
                0 => length,
                3 => length,
                4 => length,
                6 => Math.Min(length, 64 * 1024),
                _ => 0,
            };
            if (type == 4 && length > _limits.MaxTagBytes || type == 3 && length > _limits.MaxTagBytes)
            {
                Defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, at, $"Metadata block type {type} is larger than the tag limit and is not read.", "flac.block-too-large");
                take = 0;
            }

            if (take > 0)
            {
                if (body.Length < take)
                {
                    body = new byte[take];
                }

                int got = Take(body.AsSpan(0, (int)take), ct);
                if (got < take)
                {
                    Defects.Add(DefectKind.Truncated, DefectSeverity.Error, at, $"The data ends inside metadata block type {type}.", "flac.truncated");
                    break;
                }

                io ??= new TagIo(TagSourceFor(), _limits, Defects);
                ReadOnlySpan<byte> span = body.AsSpan(0, (int)take);
                switch (type)
                {
                    case 0:
                        if (length != FlacStreamInfo.BodyLength || !FlacStreamInfo.TryParse(span, out _info))
                        {
                            throw new MediaFormatException("STREAMINFO is not valid.") { Kind = DefectKind.InvalidValue, Offset = at };
                        }

                        streamInfoSeen = true;
                        break;
                    case 3:
                        ReadSeekTable(span, at);
                        blockTags.Add(BinaryTag(io, "SEEKTABLE", block));
                        break;
                    case 4:
                        if (comment is null)
                        {
                            comment = VorbisCommentReader.ReadAt(span, TagSourceFor(), block.BodyOffset, 0, _limits, Defects);
                            _comment = block.Range;
                        }
                        else
                        {
                            Defects.Add(DefectKind.Duplicate, DefectSeverity.Warning, at, "A second VORBIS_COMMENT block is ignored (RFC 9639 §8.6 allows one).", "flac.duplicate-comment");
                        }

                        break;
                    case 6:
                        if (VorbisCommentReader.ReadPictureBlock(span, TagSourceFor(), block.BodyOffset, _limits, Defects) is { } pic)
                        {
                            blockTags.Add(pic);
                        }

                        break;
                }

                if (take < length)
                {
                    SkipBytes(length - take, ct);
                }
            }
            else
            {
                io ??= new TagIo(TagSourceFor(), _limits, Defects);
                SkipBytes(length, ct);
                switch (type)
                {
                    case 1:
                        break;
                    case 2:
                        blockTags.Add(BinaryTag(io, "APPLICATION", block));
                        break;
                    case 5:
                        blockTags.Add(BinaryTag(io, "CUESHEET", block));
                        break;
                    case 0 or 3 or 4 or 6:
                        break;
                    default:
                        blockTags.Add(BinaryTag(io, "BLOCK" + type.ToString(System.Globalization.CultureInfo.InvariantCulture), block));
                        break;
                }
            }
        }

        if (!streamInfoSeen)
        {
            throw new MediaFormatException("STREAMINFO is missing.") { Kind = DefectKind.MissingRequired, Offset = 0 };
        }

        if (!last)
        {
            Defects.Add(DefectKind.Truncated, DefectSeverity.Warning, _input.Position, "The metadata blocks do not end with a block marked last.", "flac.no-last-block");
        }

        _audioStart = _input.Position;
        if (!_id3Bytes.IsEmpty)
        {
            _containers.Add(RawId3Prefix.Make(_id3Bytes, _limits, Defects));
        }

        if (comment is not null)
        {
            _containers.Add(comment);
        }

        if (blockTags.Count > 0)
        {
            _containers.Add(new FlacBlockTags(new ByteRange(_blocks[0].Offset, _audioStart - _blocks[0].Offset), blockTags));
        }

        if (_info.SampleRate <= 0 || _info.Channels <= 0)
        {
            throw new MediaFormatException("STREAMINFO states no sample rate or channel count.") { Kind = DefectKind.InvalidValue, Offset = _blocks[0].Offset };
        }

        byte[] config = new byte[4 + FlacBlockHeader.Size + FlacStreamInfo.BodyLength];
        "fLaC"u8.CopyTo(config);
        config[4] = 0x80;
        config[7] = FlacStreamInfo.BodyLength;
        FlacStreamInfoBytes(config.AsSpan(8));
        _track = new MediaTrack
        {
            Index = 0,
            Id = 1,
            Kind = TrackKind.Audio,
            Codec = CodecId.Flac,
            Configuration = new CodecConfiguration(config, "CodecPrivate"),
            Timescale = _info.SampleRate,
            Duration = _info.TotalSamples > 0 ? new MediaTime(_info.TotalSamples, _info.SampleRate) : MediaTime.Unknown,
            Audio = new AudioTrackInfo(_info.SampleRate, _info.Channels, _info.BitsPerSample),
        };
        _duration = _track.Duration;
        _frameOffset = _audioStart;
        // The forward-only tag source covers the metadata area; the audio is never retained.
        _retainMeta = false;
    }

    private IMediaSource TagSourceFor() => _random ?? (_retained ??= new RetainedSource(this));

    // The metadata area of a forward-only source, kept in memory as it was read. It reads the live buffer, so tags made early still work after it grows.
    private sealed class RetainedSource(FlacDemuxer owner) : IMediaSource
    {
        public long Length => owner._metaLength;

        public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
        {
            if (offset < 0 || offset >= owner._metaLength)
            {
                return 0;
            }

            int n = (int)Math.Min(destination.Length, owner._metaLength - offset);
            owner._meta.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }

        public void Dispose()
        {
        }
    }

    private static NativeTag BinaryTag(TagIo io, string key, FlacMetadataBlock b) =>
        new(io, TagSystems.Flac, key, 0, TagValueKind.Binary, 0, 0, null, b.Range, new ByteRange(b.BodyOffset, b.Length));

    // Rebuilds the 34 STREAMINFO bytes from the parsed fields would lose bits the parser ignores, so the original bytes are read again.
    private void FlacStreamInfoBytes(Span<byte> destination)
    {
        byte[] raw = new byte[FlacStreamInfo.BodyLength];
        IMediaSource source = TagSourceFor();
        source.Read(_blocks[0].BodyOffset, raw);
        raw.CopyTo(destination);
    }

    // RFC 9639 §8.5: 18-byte points of sample number (64 bits), offset (64 bits) and samples (16 bits); sample number 0xFFFFFFFFFFFFFFFF marks a placeholder.
    private void ReadSeekTable(ReadOnlySpan<byte> body, long at)
    {
        if (body.Length % 18 != 0)
        {
            Defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, at, "The SEEKTABLE length is not a multiple of 18.", "flac.seektable-size");
        }

        long previous = -1;
        for (int p = 0; p + 18 <= body.Length; p += 18)
        {
            ulong sample = BinaryPrimitives.ReadUInt64BigEndian(body[p..]);
            if (sample == ulong.MaxValue)
            {
                continue;
            }

            long offset = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(body[(p + 8)..]), long.MaxValue);
            if ((long)sample <= previous)
            {
                Defects.Add(DefectKind.InvalidOrder, DefectSeverity.Warning, at + p, "SEEKTABLE points are not in ascending order.", "flac.seektable-order");
                continue;
            }

            previous = (long)sample;
            _seekPoints.Add(new FlacSeekPoint((long)sample, offset, BinaryPrimitives.ReadUInt16BigEndian(body[(p + 16)..])));
        }
    }

    // ---- frames ------------------------------------------------------------------------------------------------

    private void ReadTailLength(CancellationToken ct)
    {
        if (_random is null || _duration.IsKnown)
        {
            return;
        }

        long length = _random.Length;
        int size = (int)Math.Min(length - _audioStart, TailBytes);
        if (size <= 0)
        {
            return;
        }

        byte[] tail = new byte[size];
        int got = _random.Read(length - size, tail, ct);
        long bestEnd = -1;
        for (int i = 0; i + 6 < got; i++)
        {
            if (tail[i] == 0xFF && (tail[i + 1] & 0xFE) == 0xF8 && FlacFrameHeader.TryParse(tail.AsSpan(i, got - i), out FlacFrameHeader h) && h.SampleRate is 0 or > 0)
            {
                long first = h.VariableBlockSize ? h.Number : h.Number * _info.MinBlockSize;
                bestEnd = first + h.BlockSize;
            }
        }

        if (bestEnd > 0)
        {
            _duration = new MediaTime(bestEnd, _info.SampleRate);
            _track = _track with { Duration = _duration };
        }
    }

    private void FillFrameBuffer(int atLeast, CancellationToken ct)
    {
        while (!_eof && _length < atLeast)
        {
            if (_frame.Length - _length < ReadChunk)
            {
                _limits.CheckPacket((long)_frame.Length * 2, _frameOffset);
                Array.Resize(ref _frame, _frame.Length * 2);
            }

            int n = _input.Read(_frame.AsSpan(_length, ReadChunk), ct);
            if (n <= 0)
            {
                _eof = true;
                break;
            }

            _length += n;
        }
    }

    // RFC 9639 §9.1: frame header = sync code, fields, coded number, optional block size/rate, CRC-8. Returns the first sample of the frame.
    private long FirstSample(in FlacFrameHeader h) => h.VariableBlockSize ? h.Number : h.Number * _info.MinBlockSize;

    /// <inheritdoc />
    public bool TryReadPacket(out PacketView packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        packet = default;
        if (_pendingView)
        {
            // The previous packet's bytes are no longer needed.
            DropFront(_pendingDrop);
            _pendingView = false;
        }

        FillFrameBuffer(ReadChunk, CancellationToken.None);
        int at = FindHeader(out FlacFrameHeader header);
        if (at < 0)
        {
            if (_length > 0)
            {
                Defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Warning, _frameOffset, $"{_length} bytes at the end hold no valid FLAC frame.", "flac.trailing");
                DropFront(_length);
            }

            return false;
        }

        if (at > 0)
        {
            Defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Warning, _frameOffset, $"{at} bytes before a FLAC frame are not part of a frame.", "flac.junk");
            DropFront(at);
        }

        // The frame ends where the next header starts that either closes a frame whose CRC-16 matches or carries the sample number we expect.
        long firstSample = FirstSample(header);
        long expectedNext = firstSample + header.BlockSize;
        ushort crc = 0;
        int crcAt = 0;
        int scan = header.HeaderSize;
        int end;
        bool crcOk;
        while (true)
        {
            FillFrameBuffer(scan + ReadChunk, CancellationToken.None);
            int limit = _length;
            int found = -1;
            bool ok = false;
            while (scan < limit)
            {
                int rel = _frame.AsSpan(scan, limit - scan).IndexOf((byte)0xFF);
                if (rel < 0)
                {
                    scan = limit;
                    break;
                }

                scan += rel;
                if (scan + 1 >= limit && !_eof)
                {
                    break;
                }

                if (scan + 1 < limit && (_frame[scan + 1] & 0xFE) == 0xF8 && scan >= header.HeaderSize + 2
                    && FlacFrameHeader.TryParse(_frame.AsSpan(scan, limit - scan), out FlacFrameHeader next)
                    && next.Channels > 0 && (next.SampleRate == 0 || next.SampleRate == _info.SampleRate))
                {
                    // RFC 9639 §9.3: a CRC-16 of the whole frame sits in the two bytes before the next header.
                    crc = Crc16Update(crc, _frame.AsSpan(crcAt, scan - 2 - crcAt));
                    crcAt = scan - 2;
                    ok = (ushort)((_frame[scan - 2] << 8) | _frame[scan - 1]) == crc;
                    if (ok || FirstSample(next) == expectedNext)
                    {
                        found = scan;
                        break;
                    }
                }

                scan++;
            }

            if (found >= 0)
            {
                end = found;
                crcOk = ok;
                break;
            }

            if (_eof && scan >= _length - 1)
            {
                end = _length;
                crcOk = end >= header.HeaderSize + 2 && Crc16Update(crc, _frame.AsSpan(crcAt, end - 2 - crcAt)) == (ushort)((_frame[end - 2] << 8) | _frame[end - 1]);
                break;
            }

            _limits.CheckPacket(scan, _frameOffset);
        }

        if (!crcOk)
        {
            Defects.Add(DefectKind.ChecksumMismatch, DefectSeverity.Warning, _frameOffset, "The CRC-16 of a FLAC frame does not match; the frame is delivered flagged as corrupt.", "flac.frame-crc");
        }

        if (_nextSample >= 0 && _nextSample != firstSample)
        {
            Defects.Add(DefectKind.Inconsistent, DefectSeverity.Info, _frameOffset, $"A FLAC frame starts at sample {firstSample}, not {_nextSample}.", "flac.sample-gap");
        }

        _nextSample = firstSample + header.BlockSize;
        MediaTime pts = new(firstSample, _info.SampleRate);
        PacketInfo info = new(0, pts, pts, new MediaTime(header.BlockSize, _info.SampleRate),
            PacketAttributes.Keyframe | (crcOk ? PacketAttributes.None : PacketAttributes.Corrupt), new ByteRange(_frameOffset, end), end);
        _pendingDrop = end;
        _pendingView = true;
        packet = new PacketView(info, _frame.AsSpan(0, end));
        return true;
    }

    private int _pendingDrop;
    private bool _pendingView;

    private void DropFront(int count)
    {
        _frameOffset += count;
        if (count >= _length)
        {
            _length = 0;
            return;
        }

        Buffer.BlockCopy(_frame, count, _frame, 0, _length - count);
        _length -= count;
    }

    // The first valid frame header in the buffer, reading more as needed; its index, or -1 at the end of the data.
    private int FindHeader(out FlacFrameHeader header)
    {
        header = default;
        int p = 0;
        while (true)
        {
            while (p < _length)
            {
                int rel = _frame.AsSpan(p, _length - p).IndexOf((byte)0xFF);
                if (rel < 0)
                {
                    p = _length;
                    break;
                }

                p += rel;
                if (p + 16 > _length && !_eof)
                {
                    FillFrameBuffer(p + ReadChunk, CancellationToken.None);
                }

                if (p + 1 < _length && (_frame[p + 1] & 0xFE) == 0xF8 && FlacFrameHeader.TryParse(_frame.AsSpan(p, _length - p), out header)
                    && header.Channels > 0 && (header.SampleRate == 0 || header.SampleRate == _info.SampleRate))
                {
                    return p;
                }

                p++;
            }

            if (_eof)
            {
                return -1;
            }

            FillFrameBuffer(_length + ReadChunk, CancellationToken.None);
        }
    }

    // CRC-16 of RFC 9639 §9.3: polynomial 0x8005, initial value 0, not reflected.
    private static readonly ushort[] Crc16Table = BuildCrc16();

    private static ushort[] BuildCrc16()
    {
        ushort[] t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int r = i << 8;
            for (int k = 0; k < 8; k++)
            {
                r = (r & 0x8000) != 0 ? (r << 1) ^ 0x8005 : r << 1;
            }

            t[i] = (ushort)r;
        }

        return t;
    }

    private static ushort Crc16Update(ushort crc, ReadOnlySpan<byte> data)
    {
        ushort[] table = Crc16Table;
        int c = crc;
        foreach (byte b in data)
        {
            c = ((c << 8) & 0xFFFF) ^ table[(c >> 8) ^ b];
        }

        return (ushort)c;
    }

    // ---- seeking -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Seeks to the frame at or before <paramref name="target"/> using the SEEKTABLE, or bisection on frame headers when there is none.
    /// Every FLAC frame is a keyframe, so both modes land on a frame; <see cref="SeekMode.NextKeyframe"/> lands on the frame after.
    /// </summary>
    /// <param name="target">The time to seek to.</param>
    /// <param name="mode">Frame at or before, or the next frame.</param>
    /// <returns>The time of the first packet that will be read.</returns>
    /// <exception cref="NotSupportedException">The source cannot seek.</exception>
    public MediaTime Seek(MediaTime target, SeekMode mode = SeekMode.PreviousKeyframe)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_random is null)
        {
            throw new NotSupportedException("The FLAC source cannot seek.");
        }

        long sample = target.Rescale(_info.SampleRate, MediaRounding.Floor).Ticks;
        long offset = _audioStart;
        long at = 0;
        if (sample > 0)
        {
            foreach (FlacSeekPoint p in _seekPoints)
            {
                if (p.SampleNumber > sample)
                {
                    break;
                }

                // A seek point is only a hint: one that points outside the file (a damaged table) is skipped rather than followed.
                if (p.Offset < 0 || (_input.Length >= 0 && p.Offset > _input.Length - _audioStart))
                {
                    continue;
                }

                offset = _audioStart + p.Offset;
                at = p.SampleNumber;
            }

            if (_seekPoints.Count == 0)
            {
                (offset, at) = Bisect(sample);
            }
        }

        _input.Seek(offset);
        _length = 0;
        _frameOffset = offset;
        _eof = false;
        _pendingView = false;
        _nextSample = -1;
        if (mode == SeekMode.NextKeyframe && sample > 0 && TryReadPacket(out PacketView first))
        {
            // Step over frames until one starts after the target; the next call returns that one.
            _ = first;
            while (_nextSample <= sample && TryReadPacket(out PacketView _))
            {
            }

            return new MediaTime(_nextSample, _info.SampleRate);
        }

        return new MediaTime(at, _info.SampleRate);
    }

    // Finds the last frame starting at or before `sample` by bisecting on file offsets; each probe reads the first frame header after the offset.
    private (long Offset, long Sample) Bisect(long sample)
    {
        long lo = _audioStart;
        long hi = _random!.Length;
        long bestOffset = _audioStart;
        long bestSample = 0;
        byte[] buf = new byte[4096];
        while (hi - lo > 4096)
        {
            long mid = lo + ((hi - lo) / 2);
            int got = _random.Read(mid, buf);
            int found = -1;
            FlacFrameHeader h = default;
            for (int i = 0; i + 16 < got; i++)
            {
                if (buf[i] == 0xFF && (buf[i + 1] & 0xFE) == 0xF8 && FlacFrameHeader.TryParse(buf.AsSpan(i, got - i), out h) && h.Channels > 0)
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                hi = mid;
                continue;
            }

            long s = FirstSample(h);
            if (s <= sample)
            {
                bestOffset = mid + found;
                bestSample = s;
                lo = mid + found + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return (bestOffset, bestSample);
    }

    // ---- metadata ----------------------------------------------------------------------------------------------

    /// <summary>Returns the tags read when the file was opened: Vorbis comments, other blocks, an ID3v2 prefix as one binary tag; no further I/O.</summary>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>The tags and the duration as a fact.</returns>
    public MediaMetadata ReadMetadata(CancellationToken cancellationToken = default)
    {
        DefectLog defects = new(_limits);
        FactsBuilder facts = new(defects);
        foreach (ITagContainer c in _containers)
        {
            foreach (NativeTag t in c.Tags)
            {
                facts.AddNative(t);
            }
        }

        if (_duration.IsKnown)
        {
            facts.Duration.Add(0, new FactCandidate<MediaTime>(_duration, "flac:streaminfo", _duration.ToString(), _info.TotalSamples > 0 ? "STREAMINFO total samples over the sample rate" : "end of the last frame"));
        }

        return new MediaMetadata(_containers, [], facts.Build(), defects, true);
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

/// <summary>Opens native FLAC files for the format registry. An ID3v2 tag in front of the stream is looked through.</summary>
public sealed class FlacFormat : IMediaFormat, IHeadSeededFormat
{
    /// <inheritdoc />
    public MediaFormatId Id => MediaFormatId.Flac;

    /// <inheritdoc />
    public string DisplayName => "FLAC";

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 4 && head[..4].SequenceEqual("fLaC"u8))
        {
            return new SniffResult(SniffConfidence.Certain);
        }

        // An ID3v2 tag (ID3v2 §3.1) may come first: look for the marker right after it, within the bytes at hand.
        if (head.Length >= 14 && head[..3].SequenceEqual("ID3"u8) && head[3] != 0xFF && ((head[6] | head[7] | head[8] | head[9]) & 0x80) == 0)
        {
            long size = 10 + (((head[6] & 0x7F) << 21) | ((head[7] & 0x7F) << 14) | ((head[8] & 0x7F) << 7) | (head[9] & 0x7F)) + ((head[5] & 0x10) != 0 ? 10 : 0);
            if (size + 4 <= head.Length && head.Slice((int)size, 4).SequenceEqual("fLaC"u8))
            {
                return new SniffResult(SniffConfidence.Certain, "id3");
            }
        }

        return SniffResult.None;
    }

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => FlacDemuxer.Open(source, options, cancellationToken);

    IDemuxer IHeadSeededFormat.Open(IMediaSource source, ReadOnlySpan<byte> head, MediaOpenOptions options, CancellationToken cancellationToken) =>
        FlacDemuxer.Open(source, head, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => FlacDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        await BlockingAsyncDemuxer.OpenAsync(source, options, static (s, o, ct) => FlacDemuxer.Open(s, o, ct), cancellationToken).ConfigureAwait(false);
}
