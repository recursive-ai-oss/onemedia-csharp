using System.Buffers.Binary;
using RecursiveAI.OneMedia.Codecs.Aac;
using RecursiveAI.OneMedia.Codecs.Pcm;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Riff;
using RecursiveAI.OneMedia.Sniffing;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.Riff;

namespace RecursiveAI.OneMedia.Caf;

/// <summary>One chunk of an Apple Core Audio Format file: a four-character type and a signed 64-bit big-endian size. Chunks do not nest.</summary>
public sealed class CafChunk : IStructureNode
{
    internal CafChunk(ulong typeKey, long offset, long payloadSize, bool inferred)
    {
        TypeKey = typeKey;
        Offset = offset;
        PayloadSize = payloadSize;
        IsSizeInferred = inferred;
    }

    /// <inheritdoc />
    public ulong TypeKey { get; }

    /// <summary>The chunk type as text: <c>desc</c>, <c>data</c>, <c>pakt</c>, <c>kuki</c>, <c>chan</c>, <c>info</c>…</summary>
    public string Type => ChunkNode.FourCc(TypeKey);

    /// <inheritdoc />
    public long Offset { get; }

    /// <inheritdoc />
    public int HeaderSize => 12;

    /// <inheritdoc />
    public long PayloadSize { get; }

    /// <summary>The whole chunk, header included.</summary>
    public ByteRange Range => new(Offset, HeaderSize + PayloadSize);

    /// <summary>The chunk's data.</summary>
    public ByteRange Payload => new(Offset + HeaderSize, PayloadSize);

    /// <inheritdoc />
    public bool IsSizeInferred { get; }

    /// <inheritdoc />
    public IReadOnlyList<IStructureNode> Children => [];
}

/// <summary>The <c>desc</c> chunk: the audio format of the file's data (Core Audio Format specification, "Audio Description").</summary>
/// <param name="SampleRate">Frames per second.</param>
/// <param name="FormatId">The four-character format id: <c>lpcm</c>, <c>aac </c>, <c>alac</c>, <c>ulaw</c>, <c>ima4</c>…</param>
/// <param name="FormatFlags">Format-specific flags; for <c>lpcm</c> bit 0 is float and bit 1 little-endian.</param>
/// <param name="BytesPerPacket">Bytes in each packet, or 0 when packets vary and the packet table gives them.</param>
/// <param name="FramesPerPacket">Frames in each packet, or 0 when they vary.</param>
/// <param name="ChannelsPerFrame">Channels.</param>
/// <param name="BitsPerChannel">Bits per sample for PCM, 0 for compressed formats.</param>
public sealed record CafAudioDescription(double SampleRate, string FormatId, uint FormatFlags, uint BytesPerPacket, uint FramesPerPacket, uint ChannelsPerFrame, uint BitsPerChannel);

/// <summary>The fixed part of the <c>pakt</c> chunk: how many packets and frames the file holds and the codec delay.</summary>
/// <param name="PacketCount">Number of packets in the data.</param>
/// <param name="ValidFrames">Frames of real audio, excluding priming and remainder.</param>
/// <param name="PrimingFrames">Frames of encoder delay at the start of the decoded output.</param>
/// <param name="RemainderFrames">Frames of padding at the end of the decoded output.</param>
public sealed record CafPacketTableHeader(long PacketCount, long ValidFrames, int PrimingFrames, int RemainderFrames);

/// <summary>Walks the chunks of a CAF file.</summary>
public static class CafReader
{
    /// <summary>Length of the file header: <c>caff</c>, a 16-bit version and 16-bit flags.</summary>
    public const int FileHeaderSize = 8;

    /// <summary>Reads the chunk header at <paramref name="position"/> and moves past the chunk. A size of -1 (permitted for the last chunk, usually <c>data</c>) runs to the end of the file.</summary>
    /// <param name="source">The source.</param>
    /// <param name="position">Where the chunk starts; starts at <see cref="FileHeaderSize"/>.</param>
    /// <param name="length">Length of the source, or -1 when not known.</param>
    /// <param name="defects">Receives defects.</param>
    /// <param name="chunk">The chunk.</param>
    /// <returns>False at the end of the data or when a header does not fit.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool TryReadNext(IMediaSource source, ref long position, long length, DefectLog defects, out CafChunk? chunk)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(defects);
        chunk = null;
        long end = length < 0 ? long.MaxValue : length;
        if (position >= end)
        {
            return false;
        }

        Span<byte> h = stackalloc byte[12]; // one chunk header: type (4) and size (8)
        if (end - position < 12 || source.Read(position, h) < 12)
        {
            defects.Add(DefectKind.Truncated, DefectSeverity.Warning, position, "The data ends inside a chunk header.", "caf.truncated-header");
            position = end;
            return false;
        }

        long size = BinaryPrimitives.ReadInt64BigEndian(h[4..]);
        long start = position + 12;
        long available = end - start;
        bool inferred = false;
        if (size < 0)
        {
            // Core Audio Format specification: a size of -1 means "to the end of the file" and is allowed for the last chunk.
            if (size != -1)
            {
                defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, position, $"A chunk states the size {size}; it is taken to run to the end of the file.", "caf.size");
            }

            size = available;
            inferred = true;
        }
        else if (size > available)
        {
            defects.Add(DefectKind.SizeExceedsFile, DefectSeverity.Warning, position, $"The chunk '{ChunkNode.FourCc(BinaryPrimitives.ReadUInt32BigEndian(h))}' states {size} bytes but only {available} remain; it is cut short.", "caf.size-exceeds-file");
            size = available;
            inferred = true;
        }

        chunk = new CafChunk(BinaryPrimitives.ReadUInt32BigEndian(h), position, size, inferred);
        position = start + size;
        return true;
    }

    /// <summary>Reads the <c>desc</c> chunk.</summary>
    /// <param name="data">The 32 bytes of the chunk's data.</param>
    /// <param name="description">The result.</param>
    /// <returns>False when fewer than 32 bytes are present.</returns>
    public static bool TryParseDescription(ReadOnlySpan<byte> data, out CafAudioDescription? description)
    {
        description = null;
        if (data.Length < 32)
        {
            return false;
        }

        description = new CafAudioDescription(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data)), ChunkNode.FourCc(BinaryPrimitives.ReadUInt32BigEndian(data[8..])),
            BinaryPrimitives.ReadUInt32BigEndian(data[12..]), BinaryPrimitives.ReadUInt32BigEndian(data[16..]), BinaryPrimitives.ReadUInt32BigEndian(data[20..]),
            BinaryPrimitives.ReadUInt32BigEndian(data[24..]), BinaryPrimitives.ReadUInt32BigEndian(data[28..]));
        return true;
    }
}

/// <summary>
/// Walks the variable-length integers of a <c>pakt</c> table without holding it: each packet takes one entry for its size in bytes when the
/// description gives none, and one for its frames when those vary too. An entry is a big-endian base-128 number whose bytes but the last have the high bit set.
/// </summary>
internal sealed class CafPacketPlan(IMediaSource source, long tableStart, long tableEnd, long packets, uint bytesPerPacket, uint framesPerPacket, DefectLog defects) : PacketPlan
{
    private readonly byte[] _buffer = new byte[4096];
    private long _bufferStart = -1;
    private int _bufferLength;
    private long _at = tableStart;
    private long _read;

    public override bool Next(long remaining, out int bytes, out long frames)
    {
        bytes = 0;
        frames = 0;
        if (_read >= packets)
        {
            return false;
        }

        // A ranged source can ask for the read to be retried half-way through an entry; start again from the same place.
        long entryStart = _at;
        try
        {
            return NextEntry(remaining, out bytes, out frames);
        }
        catch (NotSupportedException)
        {
            _at = entryStart;
            throw;
        }
    }

    private bool NextEntry(long remaining, out int bytes, out long frames)
    {
        bytes = 0;
        frames = 0;

        long b = bytesPerPacket;
        long f = framesPerPacket;
        if ((bytesPerPacket == 0 && !TryVarint(out b)) || (framesPerPacket == 0 && !TryVarint(out f)))
        {
            defects.Add(DefectKind.Truncated, DefectSeverity.Warning, _at, $"The packet table ends after {_read} of {packets} packets.", "caf.pakt-short");
            _read = packets;
            return false;
        }

        _read++;
        if (b > remaining)
        {
            // The data ends inside a packet: hand out what is there and stop.
            b = remaining;
            _read = packets;
        }

        if (b <= 0 || b > int.MaxValue)
        {
            return false;
        }

        bytes = (int)b;
        frames = f;
        return true;
    }

    private bool TryVarint(out long value)
    {
        value = 0;
        for (int i = 0; i < 9; i++)
        {
            if (_at >= tableEnd || !TryByte(out byte x))
            {
                return false;
            }

            value = (value << 7) | (uint)(x & 0x7F);
            if ((x & 0x80) == 0)
            {
                return true;
            }
        }

        defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, _at, "A packet table entry is longer than nine bytes.", "caf.pakt-varint");
        return false;
    }

    private bool TryByte(out byte x)
    {
        x = 0;
        if (_bufferStart < 0 || _at < _bufferStart || _at >= _bufferStart + _bufferLength)
        {
            int want = (int)Math.Min(_buffer.Length, tableEnd - _at);
            _bufferLength = source.Read(_at, _buffer.AsSpan(0, want));
            _bufferStart = _at;
            if (_bufferLength <= 0)
            {
                return false;
            }
        }

        x = _buffer[_at - _bufferStart];
        _at++;
        return true;
    }
}

/// <summary>
/// Reads Apple Core Audio Format files. The file has one audio track. PCM and G.711 data is cut into packets of 4096 bytes rounded down to whole
/// frames; compressed formats (ALAC, AAC…) give the packets the packet table states, read as the demuxer goes so the table is never held. Times
/// are frame counts at the sample rate; the codec's priming and remainder frames are reported as the track's edit information, not applied.
/// </summary>
public sealed class CafDemuxer : AudioFileDemuxer
{
    private readonly IReadOnlyList<CafChunk> _chunks;

    private CafDemuxer(IMediaSource source, IDisposable? owner, MediaTrack track, PacketPlan plan, IReadOnlyList<CafChunk> chunks, CafAudioDescription description, PcmSampleFormat? pcm,
        CafPacketTableHeader? packetTable, long dataStart, long dataEnd, bool endIsGuess, DefectLog defects, MediaLimits limits)
        : base(source, owner, track, plan, dataStart, dataEnd, endIsGuess, defects, limits)
    {
        _chunks = chunks;
        Description = description;
        Pcm = pcm;
        PacketTable = packetTable;
    }

    /// <inheritdoc />
    public override MediaFormatId Format => MediaFormatId.Caf;

    /// <summary>The chunks seen while opening, in file order.</summary>
    public IReadOnlyList<CafChunk> Chunks => _chunks;

    /// <summary>The audio description.</summary>
    public CafAudioDescription Description { get; }

    /// <summary>The sample layout for <c>lpcm</c>, <c>ulaw</c> and <c>alaw</c> data; <c>null</c> for compressed formats.</summary>
    public PcmSampleFormat? Pcm { get; }

    /// <summary>The header of the packet table, or <c>null</c> when the file has none (fixed-size packets).</summary>
    public CafPacketTableHeader? PacketTable { get; }

    /// <summary>Opens a seekable source: the chunk headers, the audio description and the packet table's header are read; the packet table and the data are not.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Observed while the chunks are read.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The data is not CAF, or has no usable <c>desc</c> chunk.</exception>
    /// <exception cref="MediaLimitExceededException">The data asks for more than the limits allow.</exception>
    public static CafDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        return Create(source, null, options.LeaveOpen ? null : source, options, defects, cancellationToken);
    }

    /// <summary>Opens a source that can only be read once. The chunks before the data are read; a variable-packet file needs its packet table before the data, as the specification requires when the data has no stated size.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Observed while the chunks are read.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The data is not CAF, or has no <c>desc</c> chunk before its data.</exception>
    public static CafDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        ForwardAsRandomSource shim = new(source, options.Limits);
        return Create(shim, shim, options.LeaveOpen ? null : source, options, defects, cancellationToken);
    }

    private static CafDemuxer Create(IMediaSource media, ForwardAsRandomSource? forward, IDisposable? owner, MediaOpenOptions options, DefectLog defects, CancellationToken ct)
    {
        try
        {
            MediaLimits limits = options.Limits;
            Span<byte> head = stackalloc byte[CafReader.FileHeaderSize]; // 'caff', version, flags
            if (media.Read(0, head, ct) < head.Length || !head.StartsWith("caff"u8))
            {
                throw new MediaFormatException("The data is not a Core Audio Format file.") { Offset = 0 };
            }

            if (BinaryPrimitives.ReadUInt16BigEndian(head[4..]) != 1)
            {
                throw new UnsupportedMediaException($"CAF version {BinaryPrimitives.ReadUInt16BigEndian(head[4..])} is not supported.");
            }

            List<CafChunk> chunks = [];
            CafChunk? desc = null, data = null, pakt = null, kuki = null;
            byte[]? tableBytes = null; // forward-only sources: the whole packet table, read before the audio goes by
            long pos = CafReader.FileHeaderSize;
            long length = media.Length;
            while (CafReader.TryReadNext(media, ref pos, length, defects, out CafChunk? c) && c is not null)
            {
                ct.ThrowIfCancellationRequested();
                limits.CheckElementCount(chunks.Count + 1, c.Offset);
                chunks.Add(c);
                switch (c.Type)
                {
                    case "desc":
                        desc ??= c;
                        break;
                    case "data":
                        data ??= c;
                        break;
                    case "pakt":
                        pakt ??= c;
                        break;
                    case "kuki":
                        kuki ??= c;
                        break;
                }

                if (forward is not null)
                {
                    if (c.Type == "data")
                    {
                        break;
                    }

                    if (c.PayloadSize <= limits.MaxTagBytes && c.Type != "pakt")
                    {
                        media.Read(c.Offset + 12, new byte[c.PayloadSize], ct);
                    }
                    else if (c.Type == "pakt")
                    {
                        // The table comes before the audio, and the stream cannot come back for it, so it is kept whole (bounded by the tag limit).
                        if (ReferenceEquals(c, pakt)) // only the first table counts; a repeated one is skipped
                        {
                            if (c.PayloadSize > limits.MaxTagBytes)
                            {
                                throw new UnsupportedMediaException($"The packet table of {c.PayloadSize} bytes is too large to hold for a forward-only source.");
                            }

                            tableBytes = new byte[c.PayloadSize];
                            media.Read(c.Offset + 12, tableBytes, ct);
                        }
                    }
                }
            }

            if (desc is null)
            {
                throw new MediaFormatException("The CAF file has no 'desc' chunk.") { Offset = 0, Kind = DefectKind.MissingRequired };
            }

            byte[] descBytes = new byte[Math.Min(desc.PayloadSize, 64)];
            media.Read(desc.Offset + 12, descBytes, ct);
            if (!CafReader.TryParseDescription(descBytes, out CafAudioDescription? d) || d is null)
            {
                throw new MediaFormatException("The 'desc' chunk is shorter than its 32 bytes.") { Offset = desc.Offset, Kind = DefectKind.InvalidSize };
            }

            if (!(d.SampleRate >= 1 && d.SampleRate <= int.MaxValue) || d.ChannelsPerFrame == 0 || d.ChannelsPerFrame > ushort.MaxValue)
            {
                throw new MediaFormatException($"The 'desc' chunk states {d.ChannelsPerFrame} channel(s) at {d.SampleRate} Hz.") { Offset = desc.Offset, Kind = DefectKind.InvalidValue };
            }

            limits.CheckTracks(1, desc.Offset);
            int rate = (int)Math.Round(d.SampleRate);
            CodecId codec = Describe(d, out PcmSampleFormat? pcm);
            CafPacketTableHeader? table = null;
            if (pakt is not null && pakt.PayloadSize >= 24)
            {
                Span<byte> t = stackalloc byte[24]; // the fixed fields of the packet table
                if (tableBytes is not null ? tableBytes.AsSpan(0, 24).TryCopyTo(t) : media.Read(pakt.Offset + 12, t, ct) == 24)
                {
                    table = new CafPacketTableHeader(BinaryPrimitives.ReadInt64BigEndian(t), BinaryPrimitives.ReadInt64BigEndian(t[8..]), BinaryPrimitives.ReadInt32BigEndian(t[16..]), BinaryPrimitives.ReadInt32BigEndian(t[20..]));
                }
            }

            long dataStart, dataEnd;
            bool guess = forward is not null && data is { IsSizeInferred: true };
            if (data is null)
            {
                defects.Add(DefectKind.MissingRequired, DefectSeverity.Error, 0, "The CAF file has no 'data' chunk.", "caf.no-data");
                dataStart = dataEnd = Math.Max(0, media.Length);
            }
            else
            {
                // Core Audio Format specification, "Audio Data": the chunk starts with a 32-bit edit count; the audio follows it.
                dataStart = data.Offset + 12 + Math.Min(4, data.PayloadSize);
                dataEnd = data.Offset + 12 + data.PayloadSize;
            }

            PacketPlan plan;
            long frames;
            long bytes = dataEnd - dataStart;
            bool pcmLike = pcm is not null;
            if (d.BytesPerPacket > 0 && d.FramesPerPacket > 0)
            {
                int unit = (int)Math.Min(d.BytesPerPacket, int.MaxValue / 2);
                plan = new FixedPlan(unit, d.FramesPerPacket, pcmLike ? Math.Max(1, 4096 / unit / (int)d.FramesPerPacket) : 1);
                frames = bytes / unit * d.FramesPerPacket;
            }
            else if (table is not null && pakt is not null)
            {
                limits.CheckSampleTable(table.PacketCount, pakt.Offset);
                plan = tableBytes is not null
                    ? new CafPacketPlan(new MemoryMediaSource(tableBytes), 24, tableBytes.Length, table.PacketCount, d.BytesPerPacket, d.FramesPerPacket, defects)
                    : new CafPacketPlan(media, pakt.Offset + 12 + 24, pakt.Offset + 12 + pakt.PayloadSize, table.PacketCount, d.BytesPerPacket, d.FramesPerPacket, defects);
                frames = table.ValidFrames + table.PrimingFrames + table.RemainderFrames;
            }
            else
            {
                if (forward is not null && data is not null)
                {
                    // A packet table behind the audio cannot be used by a source that cannot come back for it.
                    throw new UnsupportedMediaException("The format has variable packets and no packet table before the audio; a forward-only source cannot read it.");
                }

                defects.Add(DefectKind.MissingRequired, DefectSeverity.Error, 0, "The format has variable packets but the file has no packet table; no packets are delivered.", "caf.no-pakt");
                plan = new FixedPlan(1, 1, 1);
                dataEnd = dataStart;
                frames = 0;
            }

            CodecConfiguration config = new(descBytes.AsMemory(0, 32), "desc");
            if (kuki is not null && kuki.PayloadSize <= limits.MaxTagBytes)
            {
                byte[] cookie = new byte[kuki.PayloadSize];
                media.Read(kuki.Offset + 12, cookie, ct);
                config = new CodecConfiguration(cookie, "kuki");
                if (codec == CodecId.Aac && EsDescriptor.TryParse(cookie, out EsDescriptor? es) && es is not null && !es.DecoderSpecificInfo.IsEmpty)
                {
                    config = new CodecConfiguration(es.DecoderSpecificInfo, "asc");
                }
            }

            MediaTime duration = table is not null ? new MediaTime(table.ValidFrames, rate) : guess ? MediaTime.Unknown : new MediaTime(frames, rate);
            MediaTrack track = new()
            {
                Index = 0,
                Id = 1,
                Kind = TrackKind.Audio,
                Codec = codec,
                CodecTag = d.FormatId,
                Configuration = config,
                Timescale = rate,
                Duration = duration,
                Edit = table is null ? default : new TrackEditInfo(Priming: new MediaTime(table.PrimingFrames, rate), EndPadding: new MediaTime(table.RemainderFrames, rate)),
                Audio = new AudioTrackInfo(rate, (int)d.ChannelsPerFrame, (int)d.BitsPerChannel),
                BitRate = pcm is not null ? (long)rate * d.ChannelsPerFrame * d.BitsPerChannel : 0,
                PacketCount = table?.PacketCount ?? -1,
                Disposition = TrackDisposition.Default,
            };
            forward?.Keep = false;
            return new CafDemuxer(media, owner, track, plan, chunks, d, pcm, table, dataStart, dataEnd, guess, defects, limits);
        }
        catch
        {
            owner?.Dispose();
            throw;
        }
    }

    // Core Audio Format specification, "Audio Description" and the format ids of Core Audio's AudioFormatID list.
    private static CodecId Describe(CafAudioDescription d, out PcmSampleFormat? pcm)
    {
        pcm = null;
        switch (d.FormatId)
        {
            case "lpcm":
                bool isFloat = (d.FormatFlags & 1) != 0;
                bool little = (d.FormatFlags & 2) != 0;
                int bits = (int)d.BitsPerChannel;
                pcm = new PcmSampleFormat(isFloat ? PcmEncoding.FloatingPoint : bits <= 8 ? PcmEncoding.SignedInteger : PcmEncoding.SignedInteger, bits, bits, !little);
                return CodecId.Pcm;
            case "ulaw":
                pcm = new PcmSampleFormat(PcmEncoding.MuLaw, 8, 8, false);
                return CodecId.Pcm;
            case "alaw":
                pcm = new PcmSampleFormat(PcmEncoding.ALaw, 8, 8, false);
                return CodecId.Pcm;
            case "aac " or "aach" or "aacp":
                return CodecId.Aac;
            case "alac":
                return CodecId.Alac;
            case "ima4":
                return CodecId.AdpcmImaQt;
            case ".mp3" or "mp3 ":
                return CodecId.Mp3;
            case "opus":
                return CodecId.Opus;
            case "flac":
                return CodecId.Flac;
            case "ac-3":
                return CodecId.Ac3;
            case "ec-3":
                return CodecId.Eac3;
            default:
                return CodecId.Unknown;
        }
    }

    /// <summary>Reads the <c>info</c> chunk, and the recorded duration as a fact.</summary>
    /// <param name="cancellationToken">Observed between chunks.</param>
    /// <returns>The metadata; valid while the source is open.</returns>
    public override MediaMetadata ReadMetadata(CancellationToken cancellationToken = default)
    {
        List<ITagContainer> containers = [];
        try
        {
            foreach (CafChunk c in _chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (c.Type == "info")
                {
                    containers.Add(RiffTagReader.ReadCafInfo(Source, new ByteRange(c.Offset, 12 + c.PayloadSize), new ByteRange(c.Offset + 12, c.PayloadSize), Limits, Defects));
                }
            }
        }
        catch (MediaLimitExceededException e)
        {
            Defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "tags.limit");
        }

        FactsBuilder facts = new(Defects);
        if (Duration.IsKnown)
        {
            facts.Duration.Add(0, new FactCandidate<MediaTime>(Duration, "container", Duration.ToString(), "the valid frame count of the packet table, or the data length divided by the packet size"));
        }

        foreach (ITagContainer c in containers)
        {
            foreach (NativeTag t in c.Tags)
            {
                facts.AddNative(t);
            }
        }

        return new MediaMetadata(containers, [], facts.Build(), Defects, singleTrack: true);
    }
}

/// <summary>The registry entry for Core Audio Format files: content sniffing and opening through <see cref="CafDemuxer"/>.</summary>
public sealed class CafFileFormat : IMediaFormat
{
    private readonly CafSniffer _sniffer = new();

    /// <inheritdoc />
    public MediaFormatId Id => _sniffer.Id;

    /// <inheritdoc />
    public string DisplayName => _sniffer.DisplayName;

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) => _sniffer.Sniff(head);

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => CafDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => CafDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        await AudioFileAsyncDemuxer.OpenAsync(source, options, (media, o) => CafDemuxer.Open(media, o, cancellationToken), cancellationToken).ConfigureAwait(false);
}
