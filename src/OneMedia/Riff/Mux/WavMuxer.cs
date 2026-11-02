using System.Buffers.Binary;
using System.Text;
using System.Xml;
using RecursiveAI.OneMedia.Codecs.Pcm;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Riff;

/// <summary>
/// Writes WAVE files: RIFF WAVE, Broadcast Wave (<c>bext</c>, <c>iXML</c>, <c>cart</c> and other chunks) and RF64/BW64 (EBU Tech 3306, ITU-R BS.2088).
/// The file holds one audio track whose sample bytes are copied unchanged, apart from the two representation changes the formats define: big-endian
/// samples are byte-swapped to little-endian and signed 8-bit samples are offset to the unsigned form WAVE uses. No sample is decoded, converted
/// between widths or rates, or encoded.
/// </summary>
/// <remarks>
/// <para><b>Layout.</b> <c>RIFF</c> header, a 28-byte <c>JUNK</c> chunk that reserves the room of a <c>ds64</c> chunk, <c>fmt </c>, <c>fact</c> (for every
/// format other than integer PCM, as Microsoft's WAVE documentation asks), the chunks from <see cref="WavMuxOptions"/>, then <c>data</c>. When the
/// finished file is larger than a 32-bit RIFF size can state, the header is rewritten as RF64 in place (see <see cref="WavLayout.Auto"/>), so a
/// file that grows past 4 GiB needs no second pass over the samples.</para>
/// <para><b>Format chunk.</b> A track that comes from WAVE, W64 or AVI and carries its <c>fmt </c> bytes keeps them exactly (PCM of any width,
/// float, A-law, µ-law, ADPCM, MP3, AC-3 and any other format tag: the packets are the file's <c>data</c> bytes). Other PCM is described from the
/// track: WAVEFORMATEX for 8/16-bit integer PCM with at most two channels (and float or G.711), WAVE_FORMAT_EXTENSIBLE (Microsoft) with a channel
/// mask for more channels, wider integers, or samples with fewer valid bits than their container.</para>
/// <para><b>Output.</b> The destination must seek: the sizes in the header are written when the last packet is known. A destination that cannot is
/// refused with <see cref="MuxRefusalReason.OutputNotSuitable"/> by the constructor; write to a file or a <see cref="MemoryStream"/> and copy. Use
/// <see cref="CreateFile"/> for a file that appears under its name only when it is complete.</para>
/// <para><b>Memory.</b> Constant: packets go to the destination as they arrive; byte-swapping works in a 64 KiB scratch buffer.</para>
/// <para><b>Times.</b> A WAVE file has no timestamps; a packet's position is the sum of the sizes before it. The muxer checks that decode times do not
/// decrease and reports in <see cref="MuxReport.Notes"/> a start offset and the number of jumps in the source's times that the layout cannot store.</para>
/// </remarks>
public sealed class WavMuxer : IMuxer
{
    private const uint MaxRiffSize = 0xFFFF_FFFE; // 0xFFFFFFFF marks RF64 (EBU Tech 3306 §3)
    private const int Ds64PayloadSize = 28; // riffSize, dataSize, sampleCount (8 each), tableLength (4): Tech 3306 §3.1
    private const int ScratchSize = 1 << 16;
    private static readonly byte[] SubFormatTail = [0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    private enum State
    {
        Declaring,
        Writing,
        Finished,
        Failed,
    }

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly AtomicOutput? _atomic;
    private readonly WavMuxOptions _options;
    private readonly MediaLimits _limits;
    private readonly long _origin;
    private readonly List<string> _notes = [];
    private readonly List<MediaTrack> _declared = [];
    private readonly byte[] _carry = new byte[8];
    private readonly byte[][] _leading;
    private byte[]? _scratch;
    private State _state;
    private bool _disposed;

    // The declared track.
    private byte[] _fmt = [];
    private int _channels;
    private int _rate;
    private int _blockAlign;
    private bool _isPcm;
    private bool _needsFact;
    private long _averageBytes;
    private int _swapWidth; // 0, 2, 3, 4 or 8: bytes per sample to reverse
    private bool _offsetEightBit;

    // What was written.
    private long _dataStart; // absolute position of the first sample byte
    private long _factOffset = -1;
    private long _dataBytes;
    private long _packets;
    private int _carryLength;
    private byte[][] _trailing = [];
    private long _trailingLength;
    private MediaTime _lastDts = MediaTime.Unknown;
    private MediaTime _firstPts = MediaTime.Unknown;
    private long _jumps;
    private long _durationTicks;
    private long _durationScale;
    private bool _durationMixed;
    private long _durationFrames;
    private bool _durationKnown = true;

    /// <summary>Creates a muxer that writes to <paramref name="output"/> from its current position.</summary>
    /// <param name="output">The destination: writable and seekable.</param>
    /// <param name="options">Settings; null for the defaults (RIFF with an RF64 reservation, no metadata chunks).</param>
    /// <param name="ownsOutput">True to dispose <paramref name="output"/> with the muxer. Default false.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="output"/> is not writable, <see cref="WavMuxOptions.PaddingBytes"/> is odd or below 8, a chunk id is not four printable ASCII characters or names a chunk the muxer writes itself, or a text of the metadata options does not fit its field.</exception>
    /// <exception cref="MuxRefusedException"><paramref name="output"/> cannot seek (<see cref="MuxRefusalReason.OutputNotSuitable"/>),.</exception>
    /// <exception cref="MediaLimitExceededException">A chunk built from the options is larger than <see cref="MediaLimits.MaxTagBytes"/>.</exception>
    public WavMuxer(Stream output, WavMuxOptions? options = null, bool ownsOutput = false)
        : this(output, options, ownsOutput, null)
    {
    }

    private WavMuxer(Stream output, WavMuxOptions? options, bool ownsOutput, AtomicOutput? atomic)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The destination stream is not writable.", nameof(output));
        }

        if (!output.CanSeek)
        {
            throw new MuxRefusedException(MuxRefusalReason.OutputNotSuitable, "A WAVE file states its size in its header, so the destination must be able to seek. Write to a file or a MemoryStream and copy the result.");
        }

        _options = options ?? new WavMuxOptions();
        _limits = _options.Limits;
        if (_options.PaddingBytes != 0 && (_options.PaddingBytes < 8 || (_options.PaddingBytes & 1) != 0))
        {
            throw new ArgumentException("Padding is at least 8 bytes (a chunk header) and even.", nameof(options));
        }

        _stream = output;
        _ownsStream = ownsOutput;
        _atomic = atomic;
        _origin = output.Position;
        // Chunks are built now so that a bad option is refused before anything is written.
        _leading = BuildLeadingChunks(out byte[][] trailing);
        _trailing = trailing;
        foreach (byte[] t in trailing)
        {
            _trailingLength += t.Length;
        }
    }

    /// <summary>
    /// Creates a muxer that writes a new file which appears under <paramref name="path"/> only when <see cref="Finish"/> has completed it: the data goes to a
    /// temporary file next to it, which is flushed to disk and renamed. A process killed earlier leaves the target as it was (absent, or the old file when
    /// <paramref name="overwrite"/>), and a muxer disposed without <see cref="Finish"/> deletes its temporary file.
    /// </summary>
    /// <param name="path">The file to create.</param>
    /// <param name="options">Settings; null for the defaults.</param>
    /// <param name="overwrite">Replace an existing file at <paramref name="path"/> (default false: refuse).</param>
    /// <returns>The muxer, which owns the file.</returns>
    /// <exception cref="IOException"><paramref name="path"/> exists and <paramref name="overwrite"/> is false, or the folder cannot be written.</exception>
    /// <exception cref="ArgumentException">See the constructor.</exception>
    /// <exception cref="MuxRefusedException">See the constructor.</exception>
    public static WavMuxer CreateFile(string path, WavMuxOptions? options = null, bool overwrite = false)
    {
        AtomicOutput atomic = AtomicOutput.Create(path, overwrite);
        try
        {
            return new WavMuxer(atomic.Stream, options, ownsOutput: false, atomic);
        }
        catch
        {
            atomic.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public MediaFormatId Format => MediaFormatId.Wav;

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> Tracks => _declared;

    /// <summary>The speaker position mask Microsoft's convention (and the usual WAVE writers) give a channel count: 1 centre, 2 left and right, 3 front left, right and centre, 4 quad, 5 five channels, 6 5.1, 7 6.1, 8 7.1; 0 beyond that.</summary>
    /// <param name="channels">The channel count.</param>
    /// <returns>The mask.</returns>
    public static uint DefaultChannelMask(int channels) => channels switch
    {
        1 => 0x4,
        2 => 0x3,
        3 => 0x7,
        4 => 0x33,
        5 => 0x37,
        6 => 0x3F,
        7 => 0x70F,
        8 => 0x63F,
        _ => 0,
    };

    /// <inheritdoc />
    public int AddTrack(MediaTrack track) => AddTrack(track, null);

    /// <summary>
    /// Declares the track. WAVE holds one audio track: video, subtitles and a second audio track are refused.
    /// </summary>
    /// <param name="track">The track. A track with a WAVEFORMAT-style configuration (<see cref="CodecConfiguration.Format"/> <c>fmt </c> or
    /// <c>waveformatex</c>) is written with that configuration unchanged. Other PCM tracks are described from <see cref="MediaTrack.Audio"/> and
    /// the layout resolved from <paramref name="options"/>, the codec tag (<c>sowt</c>, <c>twos</c>, <c>in24</c>, <c>fl32</c>, <c>pcm_s16be</c>, Matroska
    /// <c>A_PCM/…</c>, a WAVE tag such as <c>0x0003</c>) or, last, signed little-endian integers of the declared bit depth (16 when undeclared).</param>
    /// <param name="options">Settings for this track; null for none.</param>
    /// <returns>0.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="track"/> is null.</exception>
    /// <exception cref="MuxRefusedException"><see cref="MuxRefusalReason.UnsupportedCodec"/> (not audio, or a codec without a format record to pass through, or a PCM layout WAVE cannot state),
    /// <see cref="MuxRefusalReason.InvalidTrack"/> (a second track, no channels or rate, a frame larger than 65,535 bytes) or <see cref="MuxRefusalReason.InvalidState"/> (after the first packet).</exception>
    public int AddTrack(MediaTrack track, WavTrackOptions? options)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (_state != State.Declaring)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "Tracks are declared before the first packet.");
        }

        if (_declared.Count > 0)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, "A WAVE file holds one audio stream.", 1, track.Codec);
        }

        if (track.Kind != TrackKind.Audio)
        {
            throw new MuxRefusedException(MuxRefusalReason.UnsupportedCodec, $"A WAVE file holds audio, not {track.Kind.ToString().ToLowerInvariant()}.", 0, track.Codec);
        }

        if (!TryPassThrough(track, options))
        {
            Describe(track, options);
        }

        _declared.Add(track with { Index = 0 });
        return 0;
    }

    // A fmt chunk the source wrote is the best description of its own data: keep it byte for byte.
    private bool TryPassThrough(MediaTrack track, WavTrackOptions? options)
    {
        if (track.Configuration.Format is not ("fmt " or "waveformatex"))
        {
            return false;
        }

        if (!WaveFormat.TryParse(track.Configuration.Data.Span, out WaveFormat? f) || f is null || f.Channels <= 0 || f.SampleRate <= 0)
        {
            return false;
        }

        CodecId codec = WaveCodecs.Describe(f, out PcmSampleFormat? pcm);
        if (codec != track.Codec)
        {
            return false;
        }

        if ((pcm is not null && f.BlockAlign <= 0) || (options?.Pcm is { } given && given != pcm))
        {
            return false; // the caller states a layout the record does not describe
        }

        _fmt = track.Configuration.Data.ToArray();
        _channels = f.Channels;
        _rate = f.SampleRate;
        _blockAlign = Math.Max(1, f.BlockAlign);
        _isPcm = pcm is not null;
        _averageBytes = f.AverageBytesPerSecond;
        _needsFact = f.EffectiveTag != 1;
        return true;
    }

    private void Describe(MediaTrack track, WavTrackOptions? options)
    {
        int index = 0;
        if (track.Codec != CodecId.Pcm)
        {
            throw new MuxRefusedException(MuxRefusalReason.UnsupportedCodec,
                $"A WAVE file needs a 'fmt ' record for {(track.Codec.IsUnknown ? "this codec" : track.Codec.ToString())}, and the track has none; only PCM, A-law and µ-law can be described from the track. Remux from a WAVE, W64 or AVI file to carry other codecs unchanged.", index, track.Codec);
        }

        int channels = track.Audio?.Channels ?? 0;
        int rate = track.Audio?.SampleRate ?? 0;
        if (channels <= 0 || channels > ushort.MaxValue || rate <= 0)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, $"The track states {channels} channel(s) at {rate} Hz.", index, track.Codec);
        }

        PcmSampleFormat p = options?.Pcm ?? DescribePcm(track);
        int container = p.BitsPerSample;
        int valid = p.ValidBits > 0 && p.ValidBits <= container ? p.ValidBits : container;
        ushort tag;
        switch (p.Encoding)
        {
            case PcmEncoding.ALaw or PcmEncoding.MuLaw:
                tag = p.Encoding == PcmEncoding.ALaw ? (ushort)6 : (ushort)7;
                container = valid = 8;
                break;
            case PcmEncoding.FloatingPoint:
                if (container is not (32 or 64))
                {
                    throw new MuxRefusedException(MuxRefusalReason.UnsupportedCodec, $"WAVE stores IEEE float samples of 32 or 64 bits, not {container}.", index, track.Codec);
                }

                tag = 3;
                break;
            default:
                if (container is not (8 or 16 or 24 or 32) || (p.Encoding == PcmEncoding.UnsignedInteger && container != 8))
                {
                    throw new MuxRefusedException(MuxRefusalReason.UnsupportedCodec,
                        $"WAVE stores integer samples as signed 16, 24 or 32 bits or unsigned 8 bits; the track has {p.Name}. Changing the sample width is a conversion this muxer does not do.", index, track.Codec);
                }

                tag = 1;
                break;
        }

        int bytes = container / 8;
        long block = (long)channels * bytes;
        long average = block * rate;
        if (block > ushort.MaxValue || average > uint.MaxValue)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, $"{channels} channels of {container}-bit samples at {rate} Hz are more than a WAVE format record can state.", index, track.Codec);
        }

        // Representation changes the formats define; the samples themselves are untouched (see the class remarks).
        if (p.BigEndian && bytes > 1)
        {
            _swapWidth = bytes;
            _notes.Add($"Samples were stored big-endian ({p.Name}) and were byte-swapped to little-endian, as WAVE stores them.");
        }

        if (p.Encoding == PcmEncoding.SignedInteger && container == 8)
        {
            _offsetEightBit = true;
            _notes.Add("8-bit samples were signed and were offset by 128 to the unsigned form WAVE uses.");
        }

        uint defaultMask = DefaultChannelMask(channels);
        uint mask = options?.ChannelMask ?? defaultMask;
        if (options?.ChannelMask is null && channels > 2)
        {
            _notes.Add($"The channel mask {FormatMask(mask)} was assigned from the channel count ({channels}); the source states none.");
        }

        if (mask != 0 && System.Numerics.BitOperations.PopCount(mask) != channels)
        {
            _notes.Add($"The channel mask {FormatMask(mask)} names {System.Numerics.BitOperations.PopCount(mask)} positions for {channels} channels and was replaced by 0 (no positions assigned).");
            mask = 0;
        }

        bool extensible = channels > 2 || (tag == 1 && container > 16) || valid != container || (options?.ChannelMask is { } given && given != defaultMask);
        byte[] fmt = new byte[extensible ? 40 : tag == 1 ? 16 : 18];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, extensible ? WaveFormat.Extensible : tag);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), (uint)average);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)block);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)container);
        if (extensible)
        {
            // Microsoft WAVE_FORMAT_EXTENSIBLE: cbSize 22, then valid bits, channel mask and the sub-format GUID (the format tag plus the standard base).
            BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(16), 22);
            BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(18), (ushort)valid);
            BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(20), mask);
            BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(24), tag);
            SubFormatTail.CopyTo(fmt.AsSpan(28));
        }

        _fmt = fmt;
        _channels = channels;
        _rate = rate;
        _blockAlign = (int)block;
        _isPcm = true;
        _averageBytes = average;
        _needsFact = tag != 1;
    }

    private static string FormatMask(uint mask) => "0x" + mask.ToString("X", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The sample layout a PCM track's codec tag and audio parameters state; see <see cref="AddTrack(MediaTrack, WavTrackOptions?)"/> for the order of the rules.</summary>
    /// <param name="track">A PCM track.</param>
    /// <returns>The layout of its packets.</returns>
    internal static PcmSampleFormat DescribePcm(MediaTrack track)
    {
        int bits = track.Audio?.BitsPerSample ?? 0;
        switch (track.CodecTag)
        {
            // Matroska codec mapping (RFC 9559 / codec specs): A_PCM/INT/LIT, A_PCM/INT/BIG, A_PCM/FLOAT/IEEE; floats are little-endian.
            case "A_PCM/INT/LIT":
                return bits == 8 ? new PcmSampleFormat(PcmEncoding.UnsignedInteger, 8, 8, false) : new PcmSampleFormat(PcmEncoding.SignedInteger, bits <= 0 ? 16 : bits, bits <= 0 ? 16 : bits, false);
            case "A_PCM/INT/BIG":
                return bits == 8 ? new PcmSampleFormat(PcmEncoding.UnsignedInteger, 8, 8, false) : new PcmSampleFormat(PcmEncoding.SignedInteger, bits <= 0 ? 16 : bits, bits <= 0 ? 16 : bits, true);
            case "A_PCM/FLOAT/IEEE":
                return new PcmSampleFormat(PcmEncoding.FloatingPoint, bits <= 0 ? 32 : bits, bits <= 0 ? 32 : bits, false);
            case "0x0003":
                return new PcmSampleFormat(PcmEncoding.FloatingPoint, bits <= 0 ? 32 : bits, bits <= 0 ? 32 : bits, false);
            case "0x0006" or "pcm_alaw":
                return new PcmSampleFormat(PcmEncoding.ALaw, 8, 8, false);
            case "0x0007" or "pcm_mulaw":
                return new PcmSampleFormat(PcmEncoding.MuLaw, 8, 8, false);
            case "pcm_s16be":
                return new PcmSampleFormat(PcmEncoding.SignedInteger, 16, 16, true);
            case "pcm_s16le":
                return new PcmSampleFormat(PcmEncoding.SignedInteger, 16, 16, false);
            case "pcm_u8":
                return new PcmSampleFormat(PcmEncoding.UnsignedInteger, 8, 8, false);
            default:
                // QuickTime and ISO sample entries (sowt, twos, in24, in32, fl32, fl64, raw, ipcm/fpcm with pcmC) and the declared bit depth.
                return IsoSampleEntries.ResolvePcm(track, null);
        }
    }

    /// <inheritdoc />
    public void WritePacket(in PacketInfo info, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_state == State.Declaring)
        {
            if (_declared.Count == 0)
            {
                throw new MuxRefusedException(MuxRefusalReason.InvalidState, "No track was declared.");
            }

            WriteHead();
            _state = State.Writing;
        }

        if (_state != State.Writing)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "The muxer has finished or failed and accepts no more packets.");
        }

        if (info.TrackIndex != 0)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, $"Packet for track {info.TrackIndex}, which was never declared.", info.TrackIndex);
        }

        _limits.CheckPacket(data.Length, _stream.Position - _origin);
        MediaTime dts = info.Dts.IsKnown ? info.Dts : info.Pts;
        if (dts.IsKnown && _lastDts.IsKnown && dts < _lastDts)
        {
            throw new MuxRefusedException(MuxRefusalReason.NonMonotonicDts, $"A packet's decode time {dts} is earlier than the previous packet's {_lastDts}.", 0, _declared[0].Codec);
        }

        if (_options.Layout == WavLayout.RiffOnly)
        {
            long total = _dataStart - _origin + _dataBytes + data.Length + ((_dataBytes + data.Length) & 1) + _trailingLength;
            if (total - 8 > MaxRiffSize)
            {
                throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, "The file would pass 4 GiB, which a plain RIFF size cannot state; use WavLayout.Auto or WavLayout.Rf64.", 0, _declared[0].Codec);
            }
        }

        try
        {
            Account(info, dts);
            WriteSamples(data);
        }
        catch
        {
            _state = State.Failed;
            throw;
        }

        if (dts.IsKnown)
        {
            _lastDts = dts;
        }

        _packets++;
    }

    // Times a WAVE file cannot store are noted, not refused: a gap or a late start is a property of the source.
    private void Account(in PacketInfo info, MediaTime dts)
    {
        MediaTime pts = info.Pts.IsKnown ? info.Pts : dts;
        if (pts.IsKnown)
        {
            if (!_firstPts.IsKnown)
            {
                _firstPts = pts;
            }
            else if (_isPcm)
            {
                // One tick of the packet's own clock is the rounding a source with coarse timestamps (milliseconds) is allowed.
                long scale = pts.Timescale;
                if (_firstPts.TryRescale(scale, MediaRounding.NearestAwayFromZero, out MediaTime first)
                    && new MediaTime(_dataBytes / _blockAlign, _rate).TryRescale(scale, MediaRounding.NearestAwayFromZero, out MediaTime elapsed))
                {
                    Int128 diff = (Int128)pts.Ticks - ((Int128)first.Ticks + elapsed.Ticks);
                    if (diff > 1 || diff < -1)
                    {
                        _jumps++;
                    }
                }
            }
        }

        if (info.Duration.IsKnown)
        {
            if (_durationScale == 0)
            {
                _durationScale = info.Duration.Timescale;
            }

            if (info.Duration.Timescale == _durationScale && !_durationMixed)
            {
                _durationTicks += info.Duration.Ticks;
            }
            else
            {
                if (!_durationMixed)
                {
                    _durationFrames = RoundedFrames(_durationTicks, _durationScale);
                    _durationMixed = true;
                }

                _durationFrames += RoundedFrames(info.Duration.Ticks, info.Duration.Timescale);
            }
        }
        else
        {
            _durationKnown = false;
        }
    }

    private long RoundedFrames(long ticks, long scale)
    {
        if (new MediaTime(ticks, scale).TryRescale(_rate, MediaRounding.NearestAwayFromZero, out MediaTime frames))
        {
            return frames.Ticks;
        }

        _durationKnown = false; // a duration too large to count; fall back to the data rate
        return 0;
    }

    private void WriteSamples(ReadOnlySpan<byte> data)
    {
        if (_swapWidth == 0 && !_offsetEightBit)
        {
            _stream.Write(data);
            _dataBytes += data.Length;
            return;
        }

        int w = _swapWidth == 0 ? 1 : _swapWidth;
        _scratch ??= new byte[ScratchSize - (ScratchSize % 24)]; // a multiple of 2, 3, 4 and 8
        while (!data.IsEmpty)
        {
            if (_carryLength > 0)
            {
                // A sample split over two packets: finish it from the carry.
                int need = Math.Min(w - _carryLength, data.Length);
                data[..need].CopyTo(_carry.AsSpan(_carryLength));
                _carryLength += need;
                data = data[need..];
                if (_carryLength == w)
                {
                    Convert(_carry.AsSpan(0, w), w);
                    _stream.Write(_carry.AsSpan(0, w));
                    _dataBytes += w;
                    _carryLength = 0;
                }

                continue;
            }

            int whole = data.Length / w * w;
            if (whole == 0)
            {
                data.CopyTo(_carry);
                _carryLength = data.Length;
                return;
            }

            int n = Math.Min(whole, _scratch.Length);
            Span<byte> s = _scratch.AsSpan(0, n);
            data[..n].CopyTo(s);
            Convert(s, w);
            _stream.Write(s);
            _dataBytes += n;
            data = data[n..];
        }
    }

    private void Convert(Span<byte> samples, int width)
    {
        if (_offsetEightBit)
        {
            // Signed 8-bit to unsigned: adding 128 modulo 256 flips the top bit.
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] ^= 0x80;
            }

            return;
        }

        switch (width)
        {
            case 2:
                for (int i = 0; i + 1 < samples.Length; i += 2)
                {
                    (samples[i], samples[i + 1]) = (samples[i + 1], samples[i]);
                }

                break;
            case 3:
                for (int i = 0; i + 2 < samples.Length; i += 3)
                {
                    (samples[i], samples[i + 2]) = (samples[i + 2], samples[i]);
                }

                break;
            case 4:
                for (int i = 0; i + 3 < samples.Length; i += 4)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(samples[i..], BinaryPrimitives.ReadUInt32BigEndian(samples[i..]));
                }

                break;
            case 8:
                for (int i = 0; i + 7 < samples.Length; i += 8)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(samples[i..], BinaryPrimitives.ReadUInt64BigEndian(samples[i..]));
                }

                break;
        }
    }

    /// <inheritdoc />
    public void Flush(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_state is State.Declaring or State.Writing)
        {
            _stream.Flush();
        }
    }

    /// <inheritdoc />
    /// <remarks>Writes the pad byte and the chunks that follow the sample data, then the sizes, the RF64 header when it is needed, and the frame count.
    /// A packet that ended inside a sample of a byte-swapped or offset stream leaves those last bytes as they were (they cannot be converted) and the
    /// report says so. A track with no packets gives a valid file with an empty <c>data</c> chunk.</remarks>
    public MuxReport Finish(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_state == State.Declaring)
        {
            if (_declared.Count == 0)
            {
                throw new MuxRefusedException(MuxRefusalReason.InvalidState, "No track was declared.");
            }

            WriteHead();
            _state = State.Writing;
        }

        if (_state != State.Writing)
        {
            throw new MuxRefusedException(MuxRefusalReason.InvalidState, "The muxer has already finished or failed.");
        }

        try
        {
            if (_carryLength > 0)
            {
                _stream.Write(_carry.AsSpan(0, _carryLength));
                _dataBytes += _carryLength;
                _notes.Add($"The last packet ended {_carryLength} byte(s) into a sample; those bytes were written as they were.");
                _carryLength = 0;
            }

            if ((_dataBytes & 1) != 0)
            {
                _stream.WriteByte(0); // RIFF pads an odd chunk to an even length; the data size does not count the pad
            }

            foreach (byte[] chunk in _trailing)
            {
                _stream.Write(chunk);
            }

            long end = _stream.Position;
            long length = end - _origin;
            long frames = Frames();
            bool rf64 = _options.Layout is WavLayout.Rf64 or WavLayout.Bw64 || (length - 8) > MaxRiffSize;
            Span<byte> eight = stackalloc byte[8]; // a size field or two, patched in place
            long dataHeader = _dataStart - 8;
            if (rf64)
            {
                if (_options.Layout == WavLayout.RiffOnly)
                {
                    throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, "The file would pass 4 GiB without an RF64 reservation.");
                }

                // Tech 3306 §3: RF64 + size 0xFFFFFFFF; the real sizes are in ds64; the data chunk size is 0xFFFFFFFF as well.
                Patch(_origin, _options.Layout == WavLayout.Bw64 ? "BW64"u8 : "RF64"u8);
                BinaryPrimitives.WriteUInt32LittleEndian(eight, 0xFFFF_FFFF);
                Patch(_origin + 4, eight[..4]);
                Span<byte> ds64 = stackalloc byte[8 + Ds64PayloadSize]; // id, size and the 28-byte payload
                "ds64"u8.CopyTo(ds64);
                BinaryPrimitives.WriteUInt32LittleEndian(ds64[4..], Ds64PayloadSize);
                BinaryPrimitives.WriteUInt64LittleEndian(ds64[8..], (ulong)(length - 8));
                BinaryPrimitives.WriteUInt64LittleEndian(ds64[16..], (ulong)_dataBytes);
                BinaryPrimitives.WriteUInt64LittleEndian(ds64[24..], (ulong)frames);
                BinaryPrimitives.WriteUInt32LittleEndian(ds64[32..], 0);
                Patch(_origin + 12, ds64);
                BinaryPrimitives.WriteUInt32LittleEndian(eight, 0xFFFF_FFFF);
                Patch(dataHeader + 4, eight[..4]);
                _notes.Add(_options.Layout is WavLayout.Rf64 or WavLayout.Bw64 ? "Written as RF64 as requested." : "The file is larger than 4 GiB and its header was rewritten as RF64.");
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(eight, (uint)(length - 8));
                Patch(_origin + 4, eight[..4]);
                BinaryPrimitives.WriteUInt32LittleEndian(eight, (uint)_dataBytes);
                Patch(dataHeader + 4, eight[..4]);
            }

            if (_factOffset >= 0)
            {
                // The 32-bit fact count saturates in a file whose real count is in ds64.
                BinaryPrimitives.WriteUInt32LittleEndian(eight, (uint)Math.Min(frames, 0xFFFF_FFFFL));
                Patch(_factOffset, eight[..4]);
            }

            if (_jumps > 0)
            {
                _notes.Add($"The packets' times jump {_jumps} time(s) relative to their sizes (gaps or overlaps); a WAVE file stores none, so the samples are written back to back.");
            }

            if (_firstPts.IsKnown && !_firstPts.IsZero)
            {
                _notes.Add($"The first packet starts at {_firstPts}; a WAVE file cannot store a start offset, so the samples start at zero.");
            }

            _stream.Seek(end, SeekOrigin.Begin);
            _stream.Flush();
            _state = State.Finished;
            if (_atomic is not null)
            {
                _atomic.Commit();
            }

            MediaTime duration = new(frames, _rate);
            return new MuxReport(length, [new TrackMuxReport(0, _packets, _dataBytes, duration)], _notes.ToArray());
        }
        catch
        {
            if (_state == State.Writing)
            {
                _state = State.Failed;
            }

            throw;
        }
    }

    private long Frames()
    {
        if (_isPcm)
        {
            return _dataBytes / _blockAlign;
        }

        if (_durationKnown && _packets > 0)
        {
            return _durationMixed ? _durationFrames : RoundedFrames(_durationTicks, _durationScale == 0 ? 1 : _durationScale);
        }

        // Without durations on the packets, the format's own average data rate states how long the data plays.
        return _averageBytes > 0 ? _dataBytes * _rate / _averageBytes : 0;
    }

    private void Patch(long position, ReadOnlySpan<byte> bytes)
    {
        _stream.Seek(position, SeekOrigin.Begin);
        _stream.Write(bytes);
    }

    private void WriteHead()
    {
        bool rf64 = _options.Layout is WavLayout.Rf64 or WavLayout.Bw64;
        bool reserve = _options.Layout != WavLayout.RiffOnly;
        List<byte[]> parts = [];
        byte[] riff = new byte[12];
        (rf64 ? (_options.Layout == WavLayout.Bw64 ? "BW64"u8 : "RF64"u8) : "RIFF"u8).CopyTo(riff);
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), 0xFFFF_FFFF); // patched at Finish
        "WAVE"u8.CopyTo(riff.AsSpan(8));
        parts.Add(riff);
        long length = riff.Length;
        if (reserve)
        {
            // Room for ds64 (id + size + 28 bytes). Until Finish decides, it is a JUNK chunk, which every RIFF reader skips.
            byte[] junk = new byte[8 + Ds64PayloadSize];
            (rf64 ? "ds64"u8 : "JUNK"u8).CopyTo(junk);
            BinaryPrimitives.WriteUInt32LittleEndian(junk.AsSpan(4), Ds64PayloadSize);
            parts.Add(junk);
            length += junk.Length;
        }

        byte[] fmt = ChunkBuilder.Create(ChunkLayout.Riff, "fmt ", _fmt);
        parts.Add(fmt);
        length += fmt.Length;
        if (_needsFact)
        {
            byte[] fact = ChunkBuilder.Create(ChunkLayout.Riff, "fact", new byte[4]);
            _factOffset = _origin + length + 8;
            parts.Add(fact);
            length += fact.Length;
        }

        foreach (byte[] c in _leading)
        {
            parts.Add(c);
            length += c.Length;
        }

        byte[] data = new byte[8];
        "data"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0xFFFF_FFFF);
        parts.Add(data);
        length += data.Length;
        foreach (byte[] p in parts)
        {
            _stream.Write(p);
        }

        _dataStart = _origin + length;
    }

    // bext, cart, iXML, INFO, the caller's chunks, then padding, in that order; those marked AfterData come back separately.
    private byte[][] BuildLeadingChunks(out byte[][] trailing)
    {
        List<byte[]> before = [];
        List<byte[]> after = [];
        void Add(string id, byte[] payload, bool afterData)
        {
            _limits.CheckTag(payload.Length, 0);
            (afterData ? after : before).Add(ChunkBuilder.Create(ChunkLayout.Riff, id, payload));
        }

        if (_options.Bext is { } bext)
        {
            Add("bext", bext.ToBytes(), false);
        }

        if (_options.Cart is { } cart)
        {
            Add("cart", cart.ToBytes(), false);
        }

        if (_options.IXml is { } xml)
        {
            CheckWellFormed(xml);
            Add("iXML", Encoding.UTF8.GetBytes(xml), false);
        }

        if (_options.Info is { Count: > 0 } info)
        {
            using MemoryStream children = new();
            foreach (KeyValuePair<string, string> kv in info)
            {
                CheckId(kv.Key);
                byte[] text = Encoding.UTF8.GetBytes(kv.Value ?? string.Empty);
                byte[] payload = new byte[text.Length + 1]; // INFO strings end in a zero byte
                text.CopyTo(payload, 0);
                children.Write(ChunkBuilder.Create(ChunkLayout.Riff, kv.Key, payload));
            }

            byte[] list = ChunkBuilder.CreateList(ChunkLayout.Riff, "INFO", children.ToArray());
            _limits.CheckTag(list.Length, 0);
            before.Add(list);
        }

        if (_options.Chunks is { } extra)
        {
            foreach (WavChunk c in extra)
            {
                ArgumentNullException.ThrowIfNull(c);
                CheckId(c.Id);
                if (c.Id is "RIFF" or "RF64" or "BW64" or "ds64" or "fmt " or "fact" or "data")
                {
                    throw new ArgumentException($"The '{c.Id}' chunk is written by the muxer itself.");
                }

                Add(c.Id, c.Payload.ToArray(), c.AfterData);
            }
        }

        if (_options.PaddingBytes > 0)
        {
            before.Add(ChunkBuilder.CreatePadding(ChunkLayout.Riff, _options.PaddingBytes));
        }

        trailing = after.ToArray();
        return before.ToArray();
    }

    private static void CheckId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length != 4)
        {
            throw new ArgumentException($"A chunk id is four characters, not '{id}'.", nameof(id));
        }

        foreach (char c in id)
        {
            if (c < 0x20 || c > 0x7E)
            {
                throw new ArgumentException($"A chunk id is printable ASCII, not '{id}'.", nameof(id));
            }
        }
    }

    private static void CheckWellFormed(string xml)
    {
        try
        {
            using XmlReader r = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (r.Read())
            {
            }
        }
        catch (XmlException e)
        {
            throw new ArgumentException("The iXML text is not well-formed XML: " + e.Message, e);
        }
    }

    /// <summary>Abandons an unfinished output: nothing more is written, and the temporary file of <see cref="CreateFile"/> is deleted.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_state is State.Declaring or State.Writing)
        {
            _state = State.Failed;
        }

        _atomic?.Dispose();
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
