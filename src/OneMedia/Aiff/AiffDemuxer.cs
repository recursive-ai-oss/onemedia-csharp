using System.Buffers.Binary;
using RecursiveAI.OneMedia.Codecs.Pcm;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Riff;
using RecursiveAI.OneMedia.Sniffing;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.Riff;

namespace RecursiveAI.OneMedia.Aiff;

/// <summary>The <c>COMM</c> chunk of an AIFF or AIFF-C file.</summary>
/// <param name="Channels">Number of channels.</param>
/// <param name="FrameCount">Sample frames in the sound data as stated.</param>
/// <param name="SampleSize">Bits per sample (1 to 32).</param>
/// <param name="SampleRate">Frames per second, from the 80-bit extended-precision field, rounded to a whole number.</param>
/// <param name="CompressionType">For AIFF-C, the four-character compression type (<c>NONE</c>, <c>sowt</c>, <c>fl32</c>, <c>ulaw</c>…); <c>NONE</c> for plain AIFF.</param>
/// <param name="CompressionName">For AIFF-C, the human-readable compression name; empty for AIFF.</param>
public sealed record AiffCommon(int Channels, long FrameCount, int SampleSize, int SampleRate, string CompressionType, string CompressionName)
{
    /// <summary>Reads the chunk payload.</summary>
    /// <param name="data">The payload of the <c>COMM</c> chunk.</param>
    /// <param name="isAifc">True for a FORM of type AIFC, whose chunk carries a compression type.</param>
    /// <param name="common">The result.</param>
    /// <returns>False when the payload is shorter than the fixed 18 bytes (22 for AIFF-C).</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, bool isAifc, out AiffCommon? common)
    {
        common = null;
        if (data.Length < (isAifc ? 22 : 18))
        {
            return false;
        }

        string type = "NONE", name = string.Empty;
        if (isAifc)
        {
            type = System.Text.Encoding.Latin1.GetString(data.Slice(18, 4));
            if (data.Length > 22)
            {
                // The name is a Pascal string: a length byte, then that many characters.
                int n = Math.Min(data[22], data.Length - 23);
                name = System.Text.Encoding.Latin1.GetString(data.Slice(23, n));
            }
        }

        common = new AiffCommon(BinaryPrimitives.ReadInt16BigEndian(data), BinaryPrimitives.ReadUInt32BigEndian(data[2..]), BinaryPrimitives.ReadInt16BigEndian(data[6..]), ExtendedToRate(data.Slice(8, 10)), type, name);
        return true;
    }

    // IEEE 754 80-bit extended: a sign bit and 15-bit exponent (bias 16383), then a 64-bit mantissa with an explicit leading one. Exact integer arithmetic;
    // the rate is rounded to the nearest whole frame rate.
    internal static int ExtendedToRate(ReadOnlySpan<byte> e)
    {
        int exponent = ((e[0] & 0x7F) << 8) | e[1];
        ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(e[2..]);
        if ((e[0] & 0x80) != 0 || mantissa == 0 || exponent == 0x7FFF)
        {
            return 0;
        }

        int shift = 63 - (exponent - 16383);
        if (shift > 64)
        {
            return 0;
        }

        if (shift <= 0)
        {
            return int.MaxValue;
        }

        ulong value = shift == 64 ? 0 : mantissa >> shift;
        ulong half = (mantissa >> (shift - 1)) & 1;
        value += half;
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }
}

/// <summary>
/// Reads AIFF and AIFF-C files. The file has one audio track. PCM packets are cut from the <c>SSND</c> chunk in blocks of 4096 bytes rounded down to
/// whole frames; block-based compression types (<c>ima4</c>) give one block per packet. Every packet is a keyframe; times are frame counts at the sample rate.
/// </summary>
public sealed class AiffDemuxer : AudioFileDemuxer
{
    private readonly IReadOnlyList<ChunkNode> _chunks;

    private AiffDemuxer(IMediaSource source, IDisposable? owner, MediaTrack track, PacketPlan plan, ChunkFile file, IReadOnlyList<ChunkNode> chunks, AiffCommon common, PcmSampleFormat? pcm,
        long dataStart, long dataEnd, bool endIsGuess, DefectLog defects, MediaLimits limits)
        : base(source, owner, track, plan, dataStart, dataEnd, endIsGuess, defects, limits)
    {
        ChunkFile = file;
        _chunks = chunks;
        Common = common;
        Pcm = pcm;
    }

    /// <inheritdoc />
    public override MediaFormatId Format => MediaFormatId.Aiff;

    /// <summary>The chunk file (always the IFF layout).</summary>
    public ChunkFile ChunkFile { get; }

    /// <summary>The top-level chunks seen while opening.</summary>
    public IReadOnlyList<ChunkNode> Chunks => _chunks;

    /// <summary>The <c>COMM</c> chunk.</summary>
    public AiffCommon Common { get; }

    /// <summary>The sample layout for PCM, float and G.711 data; <c>null</c> for compressed types.</summary>
    public PcmSampleFormat? Pcm { get; }

    /// <summary>True for AIFF-C (form type <c>AIFC</c>).</summary>
    public bool IsAifc => ChunkFile.FormType == "AIFC";

    /// <summary>Opens a seekable source: chunk headers and the <c>COMM</c> chunk are read, never the sound data.</summary>
    /// <param name="source">The source, from offset 0.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Observed while the chunks are read.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The data is not AIFF, or has no usable <c>COMM</c> chunk.</exception>
    /// <exception cref="MediaLimitExceededException">The data asks for more than the limits allow.</exception>
    public static AiffDemuxer Open(IMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        return Create(source, null, options.LeaveOpen ? null : source, options, defects, cancellationToken);
    }

    /// <summary>Opens a source that can only be read once; chunks before the sound data are read, those after it are not seen. The result cannot seek.</summary>
    /// <param name="source">The source.</param>
    /// <param name="options">Limits, the defect log to fill, and whether to leave the source open on disposal.</param>
    /// <param name="cancellationToken">Observed while the chunks are read.</param>
    /// <returns>The demuxer.</returns>
    /// <exception cref="MediaFormatException">The data is not AIFF, or has no <c>COMM</c> chunk before its sound data.</exception>
    public static AiffDemuxer Open(IForwardMediaSource source, MediaOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= MediaOpenOptions.Default;
        DefectLog defects = options.Defects ?? new DefectLog(options.Limits);
        ForwardAsRandomSource shim = new(source, options.Limits);
        return Create(shim, shim, options.LeaveOpen ? null : source, options, defects, cancellationToken);
    }

    private static AiffDemuxer Create(IMediaSource media, ForwardAsRandomSource? forward, IDisposable? owner, MediaOpenOptions options, DefectLog defects, CancellationToken ct)
    {
        try
        {
            MediaLimits limits = options.Limits;
            ChunkFile file = ChunkFile.TryOpen(media, limits, defects) ?? throw new MediaFormatException("The data is not an IFF file.") { Offset = 0 };
            string form = file.FormType;
            if (file.Layout != ChunkLayout.Iff || form is not ("AIFF" or "AIFC"))
            {
                throw new MediaFormatException($"The file is a '{form}' form, not AIFF.") { Offset = file.Root.Offset, Kind = DefectKind.InvalidValue };
            }

            List<ChunkNode> chunks = [];
            ChunkNode? comm = null, ssnd = null;
            if (forward is null)
            {
                foreach (ChunkNode c in file.Chunks)
                {
                    ct.ThrowIfCancellationRequested();
                    chunks.Add(c);
                    comm ??= c.Id == "COMM" ? c : null;
                    ssnd ??= c.Id == "SSND" ? c : null;
                }
            }
            else
            {
                long pos = file.FirstChunkOffset;
                while (file.TryReadNext(ref pos, out ChunkNode? c) && c is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    chunks.Add(c);
                    if (c.Id == "SSND")
                    {
                        ssnd = c;
                        // The 8 bytes before the sound data (offset and block size) decide where it starts.
                        media.Read(c.Payload.Offset, new byte[8], ct);
                        break;
                    }

                    if (c.PayloadSize <= limits.MaxTagBytes)
                    {
                        media.Read(c.Payload.Offset, new byte[c.PayloadSize], ct);
                    }

                    comm ??= c.Id == "COMM" ? c : null;
                }
            }

            if (comm is null)
            {
                throw new MediaFormatException("The AIFF file has no 'COMM' chunk.") { Offset = file.Root.Offset, Kind = DefectKind.MissingRequired };
            }

            limits.CheckTag(comm.PayloadSize, comm.Offset);
            byte[] commBytes = new byte[comm.PayloadSize];
            media.Read(comm.Payload.Offset, commBytes, ct);
            if (!AiffCommon.TryParse(commBytes, form == "AIFC", out AiffCommon? common) || common is null)
            {
                throw new MediaFormatException("The 'COMM' chunk is too short.") { Offset = comm.Offset, Kind = DefectKind.InvalidSize };
            }

            if (common.Channels <= 0 || common.SampleRate <= 0 || common.SampleSize <= 0)
            {
                throw new MediaFormatException($"The 'COMM' chunk states {common.Channels} channel(s), {common.SampleSize}-bit samples at {common.SampleRate} Hz.") { Offset = comm.Offset, Kind = DefectKind.InvalidValue };
            }

            limits.CheckTracks(1, comm.Offset);
            CodecId codec = Describe(common, out PcmSampleFormat? pcm);
            long dataStart, dataEnd;
            if (ssnd is null)
            {
                defects.Add(DefectKind.MissingRequired, DefectSeverity.Error, file.Root.Offset, "The AIFF file has no 'SSND' chunk.", "aiff.no-data");
                dataStart = dataEnd = Math.Max(0, media.Length);
            }
            else
            {
                // AIFF 1.3 "Sound Data Chunk": an offset to the first sample and a block size precede the samples.
                Span<byte> eight = stackalloc byte[8]; // offset and block size
                long skip = 0;
                if (ssnd.PayloadSize >= 8 && media.Read(ssnd.Payload.Offset, eight, ct) == 8)
                {
                    skip = BinaryPrimitives.ReadUInt32BigEndian(eight);
                }

                skip = Math.Min(8 + skip, ssnd.PayloadSize);
                dataStart = ssnd.Payload.Offset + skip;
                dataEnd = ssnd.Payload.Offset + ssnd.PayloadSize;
            }

            bool guess = forward is not null && ssnd is { IsSizeInferred: true };
            int frameBytes = Math.Max(1, common.Channels * ((common.SampleSize + 7) / 8));
            long bytes = dataEnd - dataStart;
            FixedPlan plan;
            long frames;
            if (pcm is not null)
            {
                int unit = pcm.Value.Encoding is PcmEncoding.ALaw or PcmEncoding.MuLaw ? common.Channels : frameBytes;
                plan = new FixedPlan(unit, 1, Math.Max(1, 4096 / unit));
                frames = bytes / unit;
                if (common.FrameCount > 0 && !guess)
                {
                    // The stated frame count wins when it is shorter than the chunk (padding or trailing data inside SSND).
                    long stated = common.FrameCount * unit;
                    if (stated < bytes)
                    {
                        dataEnd = dataStart + stated;
                        frames = common.FrameCount;
                    }
                    else if (stated != bytes)
                    {
                        defects.Add(DefectKind.Inconsistent, DefectSeverity.Info, comm.Offset, $"The 'COMM' chunk states {common.FrameCount} frames but the sound data holds {frames}.", "aiff.frame-count");
                    }
                }
            }
            else
            {
                // ima4: 34 bytes per channel hold 64 frames.
                int unit = codec == CodecId.AdpcmImaQt ? 34 * common.Channels : frameBytes;
                long perUnit = codec == CodecId.AdpcmImaQt ? 64 : 1;
                plan = new FixedPlan(unit, perUnit, 1);
                frames = bytes / unit * perUnit;
            }

            MediaTrack track = new()
            {
                Index = 0,
                Id = 1,
                Kind = TrackKind.Audio,
                Codec = codec,
                CodecTag = common.CompressionType,
                Configuration = new CodecConfiguration(commBytes, "COMM"),
                Timescale = common.SampleRate,
                Duration = guess ? MediaTime.Unknown : new MediaTime(frames, common.SampleRate),
                Audio = new AudioTrackInfo(common.SampleRate, common.Channels, common.SampleSize),
                BitRate = (long)common.SampleRate * common.Channels * common.SampleSize,
                PacketCount = -1,
                Disposition = TrackDisposition.Default,
            };
            forward?.Keep = false;
            return new AiffDemuxer(media, owner, track, plan, file, chunks, common, pcm, dataStart, dataEnd, guess, defects, limits);
        }
        catch
        {
            owner?.Dispose();
            throw;
        }
    }

    // AIFF-C compression types (Apple's AIFF-C specification and the QuickTime sound types that appear in practice).
    private static CodecId Describe(AiffCommon c, out PcmSampleFormat? pcm)
    {
        pcm = null;
        int bits = (c.SampleSize + 7) / 8 * 8;
        switch (c.CompressionType)
        {
            case "NONE" or "twos":
                pcm = new PcmSampleFormat(PcmEncoding.SignedInteger, bits, c.SampleSize, true);
                return CodecId.Pcm;
            case "sowt":
                pcm = new PcmSampleFormat(PcmEncoding.SignedInteger, bits, c.SampleSize, false);
                return CodecId.Pcm;
            case "in24" or "in32":
                pcm = new PcmSampleFormat(PcmEncoding.SignedInteger, c.CompressionType == "in24" ? 24 : 32, c.SampleSize, true);
                return CodecId.Pcm;
            case "raw ":
                pcm = new PcmSampleFormat(PcmEncoding.UnsignedInteger, 8, 8, false);
                return CodecId.Pcm;
            case "fl32" or "FL32":
                pcm = new PcmSampleFormat(PcmEncoding.FloatingPoint, 32, 32, true);
                return CodecId.Pcm;
            case "fl64" or "FL64":
                pcm = new PcmSampleFormat(PcmEncoding.FloatingPoint, 64, 64, true);
                return CodecId.Pcm;
            case "ulaw" or "ULAW":
                pcm = new PcmSampleFormat(PcmEncoding.MuLaw, 8, 8, false);
                return CodecId.Pcm;
            case "alaw" or "ALAW":
                pcm = new PcmSampleFormat(PcmEncoding.ALaw, 8, 8, false);
                return CodecId.Pcm;
            case "ima4":
                return CodecId.AdpcmImaQt;
            case "alac":
                return CodecId.Alac;
            default:
                return CodecId.Unknown;
        }
    }

    /// <summary>The markers of the <c>MARK</c> chunk: id, position in frames and name.</summary>
    /// <returns>The markers in file order; empty when there is no <c>MARK</c> chunk.</returns>
    public IReadOnlyList<(int Id, long Position, string Name)> ReadMarkers()
    {
        List<(int, long, string)> list = [];
        foreach (ChunkNode c in _chunks)
        {
            if (c.Id != "MARK")
            {
                continue;
            }

            Limits.CheckTag(c.PayloadSize, c.Offset);
            byte[] b = new byte[c.PayloadSize];
            if (Source.Read(c.Payload.Offset, b) < 2)
            {
                break;
            }

            int count = BinaryPrimitives.ReadUInt16BigEndian(b);
            int p = 2;
            for (int i = 0; i < count && p + 7 <= b.Length; i++)
            {
                int id = BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(p));
                long position = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p + 2));
                int n = Math.Min(b[p + 6], b.Length - p - 7);
                list.Add((id, position, System.Text.Encoding.Latin1.GetString(b, p + 7, n)));
                // The name is a Pascal string, padded so that the whole string takes an even number of bytes.
                p += 6 + (((1 + b[p + 6]) + 1) & ~1);
            }

            break;
        }

        return list;
    }

    /// <summary>Reads the text chunks (<c>NAME</c>, <c>AUTH</c>, <c>(c) </c>, <c>ANNO</c>, <c>COMT</c>), ID3 chunks, the markers as chapters and the basic facts.</summary>
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
                    case "NAME" or "AUTH" or "(c) " or "ANNO":
                        containers.Add(RiffTagReader.ReadAiffText(Source, c, Limits, Defects));
                        break;
                    case "COMT":
                        containers.Add(RiffTagReader.ReadAiffComments(Source, c, Limits, Defects));
                        break;
                    case "ID3 " or "id3 ":
                        containers.Add(RiffTagReader.ReadId3Chunk(Source, c, Limits, Defects));
                        break;
                }
            }

            foreach ((int id, long position, string name) in ReadMarkers())
            {
                chapters.Add(new Chapter(new MediaTime(position, Tracks[0].Timescale), name, default));
                _ = id;
            }
        }
        catch (MediaLimitExceededException e)
        {
            Defects.Add(DefectKind.LimitReached, DefectSeverity.Error, e.Offset, e.Message, "tags.limit");
        }

        FactsBuilder facts = new(Defects);
        if (Duration.IsKnown)
        {
            facts.Duration.Add(0, new FactCandidate<MediaTime>(Duration, "container", Duration.ToString(), "sound data length divided by the frame size"));
        }

        return new MediaMetadata(containers, chapters, facts.Build(), Defects, singleTrack: true);
    }
}

/// <summary>The registry entry for AIFF and AIFF-C files: content sniffing and opening through <see cref="AiffDemuxer"/>.</summary>
public sealed class AiffFileFormat : IMediaFormat
{
    private readonly AiffSniffer _sniffer = new();

    /// <inheritdoc />
    public MediaFormatId Id => _sniffer.Id;

    /// <inheritdoc />
    public string DisplayName => _sniffer.DisplayName;

    /// <inheritdoc />
    public SniffResult Sniff(ReadOnlySpan<byte> head) => _sniffer.Sniff(head);

    /// <inheritdoc />
    public IDemuxer Open(IMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => AiffDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public IDemuxer Open(IForwardMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) => AiffDemuxer.Open(source, options, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<IAsyncDemuxer> OpenAsync(IAsyncMediaSource source, MediaOpenOptions options, CancellationToken cancellationToken = default) =>
        await AudioFileAsyncDemuxer.OpenAsync(source, options, (media, o) => AiffDemuxer.Open(media, o, cancellationToken), cancellationToken).ConfigureAwait(false);
}
