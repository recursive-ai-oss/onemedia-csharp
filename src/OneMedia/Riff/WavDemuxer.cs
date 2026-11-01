using System.Buffers.Binary;
using System.Numerics;
using RecursiveAI.OneMedia.Codecs.Pcm;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Sniffing;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.Riff;

namespace RecursiveAI.OneMedia.Riff;

/// <summary>
/// Reads WAVE files: RIFF WAVE (including Broadcast Wave), RF64/BW64 with a <c>ds64</c> chunk, and Sony Wave64. The file has one audio track. Packets
/// are cut from the <c>data</c> chunk without touching a byte: PCM, float and G.711 data in blocks of a fixed number of frames that depends on the
/// sample rate (the largest power of two not above rate × 4096 / 44100, so 4096 frames at 44.1 kHz), and block-based codecs (ADPCM and the like) in
/// one block per packet. Every packet is a keyframe and times are exact frame counts at the sample rate.
/// </summary>
public sealed class WavDemuxer : AudioFileDemuxer
{
    private readonly IReadOnlyList<ChunkNode> _chunks;
    private readonly ChunkNode? _data;
    private readonly long _fileLength;

    private WavDemuxer(IMediaSource source, IDisposable? owner, MediaTrack track, PacketPlan plan, ChunkFile file, IReadOnlyList<ChunkNode> chunks, ChunkNode? data, WaveFormat format,
        PcmSampleFormat? pcm, long dataStart, long dataEnd, bool endIsGuess, DefectLog defects, MediaLimits limits)
        : base(source, owner, track, plan, dataStart, dataEnd, endIsGuess, defects, limits)
    {
        ChunkFile = file;
        _chunks = chunks;
        _data = data;
        WaveFormat = format;
        Pcm = pcm;
        _fileLength = source.Length;
    }

    /// <inheritdoc />
    public override MediaFormatId Format => MediaFormatId.Wav;

    /// <summary>The chunk file: layout (RIFF, RF64 or Wave64) and the lazy chunk tree.</summary>
    public ChunkFile ChunkFile { get; }

    /// <summary>The top-level chunks seen while opening (all of them for a seekable source; those before the sample data for a forward-only one).</summary>
    public IReadOnlyList<ChunkNode> Chunks => _chunks;

    /// <summary>The <c>data</c> chunk, or <c>null</c> when the file has none.</summary>
    public ChunkNode? DataChunk => _data;

    /// <summary>The parsed <c>fmt </c> chunk.</summary>
    public WaveFormat WaveFormat { get; }

    /// <summary>The sample layout for PCM, float and G.711 data; <c>null</c> for compressed codecs.</summary>
    public PcmSampleFormat? Pcm { get; }

    /// <summary>
    /// Opens a seekable source: the chunk headers are read (a seek per chunk, never the sample data) and the <c>fmt </c> chunk is parsed.
    /// </summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Observed while the chunks are read.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The data is not WAVE, or has no <c>fmt </c> chunk or a format that cannot work.</exception>
    /// <exception cref="MediaLimitExceededException">The data asks for more than the limits allow.</exception>
    public static WavDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        return Create(source, null, options.LeaveOpen ? null : source, options, defects, cancellationToken);
    }

    /// <summary>
    /// Opens a source that can only be read once. The chunks before the sample data are read (small ones are kept for the metadata); the packets
    /// then stream. The result cannot seek, and chunks after the sample data are not seen.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Observed while the chunks are read.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The data is not WAVE, or has no <c>fmt </c> chunk before its sample data.</exception>
    public static WavDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        ForwardAsRandomSource shim = new(source, options.Limits);
        return Create(shim, shim, options.LeaveOpen ? null : source, options, defects, cancellationToken);
    }

    private static WavDemuxer Create(IMediaSource media, ForwardAsRandomSource? forward, IDisposable? owner, MediaOpenOptions options, DefectLog defects, CancellationToken ct)
    {
        try
        {
            MediaLimits limits = options.Limits;
            ChunkFile file = ChunkFile.TryOpen(media, limits, defects) ?? throw new MediaFormatException("The data is not a RIFF, RF64 or Wave64 file.") { Offset = 0 };
            string form = file.FormType;
            if (!(form == "WAVE" || (file.Layout == ChunkLayout.Wave64 && form == "wave")))
            {
                throw new MediaFormatException($"The file is a '{form}' form, not WAVE.") { Offset = file.Root.Offset, Kind = DefectKind.InvalidValue };
            }

            List<ChunkNode> chunks = [];
            ChunkNode? fmt = null, data = null;
            if (forward is null)
            {
                foreach (ChunkNode c in file.Chunks)
                {
                    ct.ThrowIfCancellationRequested();
                    chunks.Add(c);
                    if (c.Id == "fmt " && fmt is null)
                    {
                        fmt = c;
                    }
                    else if (c.Id == "data" && data is null)
                    {
                        data = c;
                    }
                }
            }
            else
            {
                long pos = file.FirstChunkOffset;
                while (file.TryReadNext(ref pos, out ChunkNode? c) && c is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    chunks.Add(c);
                    if (c.Id == "data")
                    {
                        data = c;
                        break;
                    }

                    if (c.PayloadSize <= limits.MaxTagBytes)
                    {
                        // The stream will not come back: keep what the readers will ask for later.
                        byte[] payload = new byte[c.PayloadSize];
                        media.Read(c.Payload.Offset, payload, ct);
                    }

                    fmt ??= c.Id == "fmt " ? c : null;
                }
            }

            if (fmt is null)
            {
                throw new MediaFormatException("The WAVE file has no 'fmt ' chunk.") { Offset = file.Root.Offset, Kind = DefectKind.MissingRequired };
            }

            limits.CheckTag(fmt.PayloadSize, fmt.Offset);
            byte[] fmtBytes = new byte[fmt.PayloadSize];
            media.Read(fmt.Payload.Offset, fmtBytes, ct);
            if (!WaveFormat.TryParse(fmtBytes, out WaveFormat? format) || format is null)
            {
                throw new MediaFormatException("The 'fmt ' chunk is too short to hold a WAVE format.") { Offset = fmt.Offset, Kind = DefectKind.InvalidSize };
            }

            if (format.Channels <= 0 || format.SampleRate <= 0)
            {
                throw new MediaFormatException($"The 'fmt ' chunk states {format.Channels} channel(s) at {format.SampleRate} Hz.") { Offset = fmt.Offset, Kind = DefectKind.InvalidValue };
            }

            limits.CheckTracks(1, fmt.Offset);
            CodecId codec = WaveCodecs.Describe(format, out PcmSampleFormat? pcm);
            if (data is null)
            {
                defects.Add(DefectKind.MissingRequired, DefectSeverity.Error, file.Root.Offset, "The WAVE file has no 'data' chunk.", "wav.no-data");
            }

            long dataStart = data is null ? Math.Max(0, media.Length) : data.Offset + data.HeaderSize;
            long dataEnd = data is null ? dataStart : dataStart + data.PayloadSize;
            bool guess = forward is not null && data is { IsSizeInferred: true };
            if (data is { PayloadSize: 0 } && forward is null && media.Length > dataStart + format.BlockAlign && !LooksLikeChunk(media, dataStart))
            {
                // A streaming writer that never came back to fill in the size: the sample data runs to the end of the file.
                defects.Add(DefectKind.InvalidSize, DefectSeverity.Warning, data.Offset, "The 'data' chunk states a size of 0 but sample data follows it; the data is taken to run to the end of the file.", "wav.data-size-zero");
                dataEnd = media.Length;
            }

            (PacketPlan plan, long frames) = MakePlan(format, pcm, codec, dataStart, dataEnd - dataStart, guess, chunks, media, defects, fmt.Offset);
            MediaTrack track = new()
            {
                Index = 0,
                Id = 1,
                Kind = TrackKind.Audio,
                Codec = codec,
                CodecTag = WaveCodecs.Tag(format),
                Configuration = new CodecConfiguration(fmtBytes, "fmt "),
                Timescale = format.SampleRate,
                Duration = frames >= 0 ? new MediaTime(frames, format.SampleRate) : MediaTime.Unknown,
                Audio = new AudioTrackInfo(format.SampleRate, format.Channels, format.BitsPerSample),
                BitRate = format.AverageBytesPerSecond * 8,
                PacketCount = -1,
                Disposition = TrackDisposition.Default,
            };
            forward?.Keep = false;
            return new WavDemuxer(media, owner, track, plan, file, chunks, data, format, pcm, dataStart, dataEnd, guess, defects, limits);
        }
        catch
        {
            owner?.Dispose();
            throw;
        }
    }

    // Four printable characters in a row start a chunk header; sample data seldom does.
    private static bool LooksLikeChunk(IMediaSource media, long at)
    {
        Span<byte> id = stackalloc byte[4]; // the four bytes that would be a chunk id
        if (media.Read(at, id) < 4)
        {
            return false;
        }

        foreach (byte b in id)
        {
            if (b < 0x20 || b > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    // How big a packet is: see the class remarks. Returns the plan and the total frames (-1 when the data length is not known).
    private static (PacketPlan Plan, long Frames) MakePlan(WaveFormat f, PcmSampleFormat? pcm, CodecId codec, long dataStart, long dataBytes, bool unknownLength, List<ChunkNode> chunks, IMediaSource media, DefectLog defects, long at)
    {
        // Observed in files read by ffprobe: packets hold the largest power of two of frames not above rate x 4096 / 44100 (about 0.093 s).
        long targetFrames = Math.Max(1, (long)f.SampleRate * 4096 / 44100);
        targetFrames = 1L << BitOperations.Log2((ulong)targetFrames);
        int unit = f.BlockAlign;
        long frames;
        if (pcm is not null)
        {
            int frameBytes = (int)Math.Max(1, (long)f.Channels * pcm.Value.BytesPerSample);
            if (unit <= 0)
            {
                unit = frameBytes;
                defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, at, "The 'fmt ' chunk states a block alignment of 0; the frame size is worked out from the channels and sample width.", "wav.block-align");
            }
            else if (unit != frameBytes && f.BitsPerSample > 0)
            {
                defects.Add(DefectKind.Inconsistent, DefectSeverity.Info, at, $"The block alignment {unit} differs from channels × sample width ({frameBytes}); the block alignment is used.", "wav.block-align");
            }

            frames = unknownLength ? -1 : dataBytes / unit;
            return (new FixedPlan(unit, 1, (int)targetFrames), frames);
        }

        FrameParser? parser = codec == CodecId.Mp3 || codec == CodecId.Mp2 ? FrameParsers.MpegAudio : codec == CodecId.Ac3 ? FrameParsers.Ac3 : null;
        if (codec == CodecId.Aac && media.Length >= 0 && dataBytes >= 7)
        {
            Span<byte> sync = stackalloc byte[2]; // the first two bytes: an ADTS sync word marks framed AAC
            if (media.Read(dataStart, sync) == 2 && sync[0] == 0xFF && (sync[1] & 0xF6) == 0xF0)
            {
                parser = FrameParsers.Adts;
            }
        }

        if (parser is not null && !unknownLength)
        {
            // Self-delimiting frames: one packet per frame. The duration is counted by walking the headers, which is what the codec's own frame count is.
            FramedPlan framed = new(media, dataStart, dataStart + dataBytes, parser, defects);
            long total = 0;
            foreach (ChunkNode c in chunks)
            {
                if (c.Id == "fact" && c.PayloadSize >= 4)
                {
                    Span<byte> b = stackalloc byte[4]; // the 32-bit sample count of the fact chunk
                    if (media.Read(c.Payload.Offset, b) == 4)
                    {
                        total = BinaryPrimitives.ReadUInt32LittleEndian(b);
                    }

                    break;
                }
            }

            if (total == 0 && f.AverageBytesPerSecond > 0)
            {
                total = dataBytes * f.SampleRate / f.AverageBytesPerSecond;
            }

            return (framed, total);
        }

        if (unit < 1)
        {
            unit = 1;
        }

        long perBlock = SamplesPerBlock(f);
        bool blocks = perBlock > 0;
        if (perBlock <= 0)
        {
            perBlock = 1;
        }

        frames = unknownLength ? -1 : dataBytes / unit * perBlock;
        if (!blocks && f.AverageBytesPerSecond > 0 && !unknownLength)
        {
            // No block structure known: the length follows from the average data rate.
            frames = dataBytes * f.SampleRate / f.AverageBytesPerSecond;
        }
        foreach (ChunkNode c in chunks)
        {
            if (c.Id == "fact" && c.PayloadSize >= 4 && !unknownLength)
            {
                Span<byte> b = stackalloc byte[4]; // the 32-bit sample count of the fact chunk
                if (media.Read(c.Payload.Offset, b) == 4)
                {
                    long stated = BinaryPrimitives.ReadUInt32LittleEndian(b);
                    if (frames <= 0 || stated <= frames * 2)
                    {
                        frames = stated;
                    }
                    else
                    {
                        defects.Add(DefectKind.Inconsistent, DefectSeverity.Info, c.Offset, $"The 'fact' chunk states {stated} frames but the data holds about {frames}; the count from the data is used.", "wav.fact-count");
                    }
                }

                break;
            }
        }

        if (!blocks)
        {
            // G.726 comes in 512-byte packets in the reference tool's output; everything else unknown in 4096-byte ones.
            return (new ProportionalPlan(dataBytes, Math.Max(frames, 0), f.EffectiveTag is 0x0040 or 0x0045 or 0x0064 ? 512 : 4096), frames);
        }

        // Observed: packets hold as many whole blocks as fit the same number of frames as a PCM packet (at least one); GSM 06.10 keeps one block per packet.
        int units = f.EffectiveTag == 0x0031 ? 1 : (int)Math.Clamp(targetFrames / perBlock, 1, 1 << 16);
        return (new FixedPlan(unit, perBlock, units), frames);
    }

    // The frames in one block of the ADPCM formats that state it in the format's extra bytes (Microsoft's WAVEFORMATEX extensions).
    private static long SamplesPerBlock(WaveFormat f)
    {
        if (f.EffectiveTag is 0x0002 or 0x0011 or 0x0031 && f.Extra.Length >= 2)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(f.Extra.Span);
        }

        if (f.EffectiveTag == 0x0002 && f.BlockAlign > 7 * f.Channels)
        {
            return ((f.BlockAlign - (7 * f.Channels)) * 2 / f.Channels) + 2;
        }

        if (f.EffectiveTag == 0x0011 && f.BlockAlign > 4 * f.Channels)
        {
            return ((f.BlockAlign - (4 * f.Channels)) * 2 / f.Channels) + 1;
        }

        return 0;
    }

    /// <summary>The cue points of the file, with the labels and notes of its <c>LIST adtl</c> chunks; empty when there is no <c>cue </c> chunk.</summary>
    /// <returns>The cue points in file order.</returns>
    public IReadOnlyList<WaveCuePoint> ReadCues()
    {
        ChunkNode? cue = FindChunk("cue ");
        return cue is null ? [] : WaveChunkReader.ReadCues(Source, cue, Lists("adtl"), Limits, Defects);
    }

    /// <summary>The <c>smpl</c> chunk, or <c>null</c> when there is none.</summary>
    /// <returns>The sampler description.</returns>
    public WaveSampler? ReadSampler() => FindChunk("smpl") is { } c ? WaveChunkReader.ReadSampler(Source, c, Limits, Defects) : null;

    /// <summary>The <c>inst</c> chunk, or <c>null</c> when there is none.</summary>
    /// <returns>The instrument description.</returns>
    public WaveInstrument? ReadInstrument() => FindChunk("inst") is { } c ? WaveChunkReader.ReadInstrument(Source, c) : null;

    private ChunkNode? FindChunk(string id)
    {
        foreach (ChunkNode c in _chunks)
        {
            if (c.Id == id)
            {
                return c;
            }
        }

        return null;
    }

    private IEnumerable<ChunkNode> Lists(string type)
    {
        foreach (ChunkNode c in _chunks)
        {
            if (c.IsContainer && c.Id is "LIST" or "list" && c.FormType == type)
            {
                yield return c;
            }
        }
    }

    /// <summary>
    /// Reads the file's tags (<c>LIST INFO</c>, <c>bext</c>, <c>iXML</c>, <c>axml</c>, <c>cart</c>, ID3 chunks), the cue points as chapters and the basic facts.
    /// Only chunk headers and the small tag chunks are read.
    /// </summary>
    /// <param name="cancellationToken">Observed between chunks.</param>
    /// <returns>The metadata; valid while the source is open.</returns>
    public override MediaMetadata ReadMetadata(CancellationToken cancellationToken = default)
    {
        List<ITagContainer> containers = [];
        List<Chapter> chapters = [];
        try
        {
            foreach (ChunkNode c in _chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (c.Id)
                {
                    case "LIST" or "list" when c.FormType == "INFO":
                        containers.Add(RiffTagReader.ReadInfo(Source, c, Limits, Defects));
                        break;
                    case "bext":
                        containers.Add(RiffTagReader.ReadBext(Source, c, Limits, Defects));
                        break;
                    case "iXML" or "IXML":
                        containers.Add(RiffTagReader.ReadIXml(Source, c, Limits, Defects));
                        break;
                    case "axml":
                        containers.Add(RiffTagReader.ReadAxml(Source, c, Limits, Defects));
                        break;
                    case "cart":
                        containers.Add(RiffTagReader.ReadCart(Source, c, Limits, Defects));
                        break;
                    case "id3 " or "ID3 ":
                        containers.Add(RiffTagReader.ReadId3Chunk(Source, c, Limits, Defects));
                        break;
                }
            }

            MediaTrack track = Tracks[0];
            foreach (WaveCuePoint p in ReadCues())
            {
                // Cue points count frames from the start of the sample data; a label is the chapter title.
                chapters.Add(new Chapter(new MediaTime(p.SampleOffset, track.Timescale), p.Label ?? p.Note ?? string.Empty, new ByteRange(FindChunk("cue ")!.Offset, FindChunk("cue ")!.PayloadSize)));
            }
        }
        catch (MediaLimitExceededException e)
        {
            Defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "tags.limit");
        }

        FactsBuilder facts = new(Defects);
        if (Duration.IsKnown)
        {
            facts.Duration.Add(0, new FactCandidate<MediaTime>(Duration, "container", Duration.ToString(), "sample data length divided by the frame size"));
        }

        foreach (ITagContainer c in containers)
        {
            NativeTag? originationDate = null, originationTime = null;
            foreach (NativeTag t in c.Tags)
            {
                facts.AddNative(t);
                if (t.System == TagSystems.Bext)
                {
                    originationDate = t.Key == "OriginationDate" ? t : originationDate;
                    originationTime = t.Key == "OriginationTime" ? t : originationTime;
                }
            }

            if (originationDate is not null)
            {
                facts.AddBextOrigination(originationDate, originationTime);
            }
        }

        return new MediaMetadata(containers, chapters, facts.Build(), Defects, singleTrack: true);
    }
}

/// <summary>The registry entry for WAVE files (RIFF, RF64/BW64, Wave64): content sniffing and opening through <see cref="WavDemuxer"/>.</summary>
public sealed class WavFileFormat : IMediaFormat
{
    private readonly WavSniffer _sniffer = new();

    /// <inheritdoc />
    public MediaFormatId Id => _sniffer.Id;

    /// <inheritdoc />
    public string DisplayName => _sniffer.DisplayName;

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) => _sniffer.Sniff(head);

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => WavDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => WavDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        await AudioFileAsyncDemuxer.OpenAsync(source, options, (media, o) => WavDemuxer.Open(media, o, cancellationToken), cancellationToken).ConfigureAwait(false);
}
