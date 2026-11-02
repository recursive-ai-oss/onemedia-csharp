using System.Buffers;
using System.Buffers.Binary;
using System.Linq;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Codecs.Av1;
using RecursiveAI.OneMedia.IsoBmff;
using H264 = RecursiveAI.OneMedia.Codecs.H264;
using Hevc = RecursiveAI.OneMedia.Codecs.Hevc;

namespace RecursiveAI.OneMedia.Repair.Reconstruction;

/// <summary>
/// Rebuilds a playable file from an MP4, MOV or 3GP recording whose movie box was never written (the camera or phone stopped
/// mid-recording) or is unusable: the media data box is walked sample by sample, the samples are framed from their own syntax
/// (NAL units and slice headers, OBUs, audio frame headers), the timing is rebuilt, and a new file with a fresh <c>moov</c> is written.
/// </summary>
/// <remarks>
/// <para><b>With a reference file</b> from the same device and settings, the sample entries, timescales, frame durations and priming
/// come from it, so the output opens with the same codec setup as a healthy recording. <b>Without one</b>, video needs parameter sets
/// in the stream (cameras that repeat SPS/PPS before every key frame, or write them in the first sample); a stream that keeps them only
/// in the lost <c>moov</c> cannot be rebuilt, and the method says so. Audio is found by its frame headers.</para>
/// <para>Nothing is decoded. Video access units are cut by NAL unit and slice-header rules, and pictures are put in output order from
/// the picture order count fields of their slice headers. The partial last sample is dropped and reported.</para>
/// <para>Memory is constant in the size of the input: the media data is read through a window and copied to the output once; only the
/// sample tables (about 24 bytes a sample) grow.</para>
/// </remarks>
public static class IsoReconstructor
{
    private const int MaxHeaderSearch = 64 << 20;

    /// <summary>Reconstructs a file from paths. See <see cref="Reconstruct(IMediaSource, Stream, IMediaSource?, ReconstructionOptions?, CancellationToken)"/>.</summary>
    /// <param name="inputPath">The damaged file.</param>
    /// <param name="outputPath">The new file; it is created or replaced only after the reconstruction succeeded.</param>
    /// <param name="referencePath">A healthy file from the same device and settings, or null.</param>
    /// <param name="options">Settings, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the work; a partial output is deleted.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentNullException">A path is null.</exception>
    /// <exception cref="ReconstructionException">Nothing could be rebuilt.</exception>
    public static ReconstructionReport Reconstruct(string inputPath, string outputPath, string? referencePath = null, ReconstructionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(outputPath);
        using FileMediaSource input = FileMediaSource.Open(inputPath);
        using FileMediaSource? reference = referencePath is null ? null : FileMediaSource.Open(referencePath);
        string temp = outputPath + ".part";
        try
        {
            ReconstructionReport report;
            using (FileStream output = new(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            {
                report = Reconstruct(input, output, reference, options, cancellationToken);
            }

            File.Move(temp, outputPath, overwrite: true);
            return report;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Reconstructs a file.</summary>
    /// <param name="input">The damaged file.</param>
    /// <param name="output">Where the new file goes: writable, and seekable to avoid a temporary spool.</param>
    /// <param name="reference">A healthy file from the same device and settings, or null.</param>
    /// <param name="options">Settings, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Per-track confidence, which byte ranges became samples and which were skipped, and every guess made.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is null.</exception>
    /// <exception cref="ReconstructionException">The input has no media data, no track could be set up, or no sample could be framed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static ReconstructionReport Reconstruct(IMediaSource input, Stream output, IMediaSource? reference = null, ReconstructionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new ReconstructionOptions();
        List<string> notes = [];
        long length = input.Length;
        MediaDataLocation loc = Locate(input, length, notes, cancellationToken);
        if (loc.PayloadStart < 0 || loc.PayloadEnd <= loc.PayloadStart)
        {
            throw new ReconstructionException(ReconstructionFailure.NoMediaData, "The file holds no media data to rebuild samples from.");
        }

        List<TrackSetup> setups = [];
        if (reference is not null)
        {
            LoadReference(reference, setups, options, notes, cancellationToken);
            if (setups.Count == 0)
            {
                throw new ReconstructionException(ReconstructionFailure.NoTracks, "The reference file has no track this reconstruction can frame (H.264, HEVC, AV1 video; AAC with the audio adapter, AMR, MP3, AC-3, PCM audio; GoPro metadata).");
            }
        }
        else
        {
            DiscoverVideo(input, loc, setups, options, notes, cancellationToken);
        }

        List<TrackFramer> framers = [];
        for (int i = 0; i < setups.Count; i++)
        {
            TrackSetup s = setups[i];
            s.Framer.Track = i;
            s.Framer.MaxSample = (int)Math.Min(options.Limits.MaxPacketBytes, int.MaxValue / 2);
            framers.Add(s.Framer);
        }

        PcmFramer? pcm = null;
        int pcmTrack = -1;
        for (int i = 0; i < setups.Count; i++)
        {
            if (setups[i].Framer is PcmFramer p)
            {
                pcm = p;
                pcmTrack = i;
            }
        }

        bool discoverAudio = reference is null;
        List<IAudioFrameProbe> probes = [.. options.AudioProbes];
        MediaDataScanner scanner = new(input, loc.PayloadStart, loc.PayloadEnd, framers, probes, options.Limits, discoverAudio, options.GuessConfirmSamples, pcm, pcmTrack, cancellationToken);
        scanner.OnTrackDiscovered = f =>
        {
            f.MaxSample = (int)Math.Min(options.Limits.MaxPacketBytes, int.MaxValue / 2);
            setups.Add(new TrackSetup { Framer = f, Source = ReconstructionSetupSource.Guessed });
            return setups.Count - 1;
        };
        if (setups.Count == 0 && !discoverAudio)
        {
            throw new ReconstructionException(ReconstructionFailure.NoTracks, "No track could be set up.");
        }

        if (setups.Count == 0)
        {
            // Without a reference and without parameter sets in the stream, only audio can be found.
            notes.Add("The media data holds no video parameter sets (SPS/PPS, sequence header): the video track's codec setup lived in the lost movie box. A reference file from the same device would supply it.");
        }

        scanner.Run();
        if (scanner.Samples.Count == 0)
        {
            throw new ReconstructionException(ReconstructionFailure.NoSamples, setups.Count == 0
                ? "No video parameter sets were found in the media data and no audio could be framed; a reference file from the same device is needed."
                : "No sample could be framed from the media data; the tracks of the reference file do not match it.");
        }

        foreach ((TrackFramer framer, GuessedAudioSetup setup, CodecId codec, int rate, int channels) in scanner.Discovered)
        {
            TrackSetup s = setups[framer.Track];
            s.GuessedSetup = setup;
            s.Codec = codec;
            s.SampleRate = rate;
            s.Channels = channels;
            if (codec == CodecId.Aac)
            {
                s.Notes.Add($"No reference: the AudioSpecificConfig was guessed ({rate} Hz, {channels} channel(s)); the frames parse under it, but the true sample rate cannot be told from the frames, so the timing may be off.");
            }
        }

        // Timing and the track list.
        List<TrackTiming> timing = ComputeTiming(setups, scanner.Samples, options, notes);
        (FileTypeInfo ftyp, IsoFileKind kind) = ReadFileType(input, options);
        if (setups.Count > 0 && ftyp.Brands is not null)
        {
            notes.Add($"The output keeps the input's brands ({ftyp.Major}).");
        }

        IsoMuxOptions muxOptions = new()
        {
            Kind = kind,
            Layout = IsoLayout.MoovLast,
            MajorBrand = ftyp.Major,
            CompatibleBrands = ftyp.Compatible,
            MinorVersion = ftyp.Minor,
            Limits = options.Limits,
        };
        MuxReport muxReport = Mux(input, output, setups, timing, scanner.Samples, muxOptions, loc.PayloadEnd, cancellationToken);

        List<ReconstructedTrack> tracks = [];
        long[] counts = new long[setups.Count];
        long[] bytes = new long[setups.Count];
        foreach (SampleRec r in scanner.Samples)
        {
            counts[r.Track]++;
            bytes[r.Track] += r.Length;
        }

        long unframed = 0;
        foreach (SkippedRange sk in scanner.Skipped)
        {
            if (sk.Reason == SkippedReason.Unframed)
            {
                unframed += sk.Range.Length;
            }
        }

        bool largeGap = scanner.SkippedCount <= ReconstructionReport.MaxListedSkips && unframed * 50 > loc.PayloadEnd - loc.PayloadStart;
        if (largeGap)
        {
            notes.Add($"{unframed} bytes ({100.0 * unframed / (loc.PayloadEnd - loc.PayloadStart):F1} %) between samples were not recognised as samples of any known track: another track whose framing is not known (supply a reference file), or damage. Tracks may contain misframed samples.");
        }

        for (int i = 0; i < setups.Count; i++)
        {
            TrackSetup s = setups[i];
            TrackTiming t = timing[i];
            ReconstructionConfidence confidence = counts[i] == 0 ? ReconstructionConfidence.None : s.Confidence(t);
            if (largeGap && confidence > ReconstructionConfidence.Medium)
            {
                confidence = ReconstructionConfidence.Medium;
            }

            tracks.Add(new ReconstructedTrack(
                i,
                s.Kind,
                s.Track?.Codec ?? s.Codec,
                counts[i],
                bytes[i],
                counts[i] > 0 && muxReport.Tracks.Count > i ? muxReport.Tracks[i].Duration : MediaTime.Unknown,
                confidence,
                s.Source,
                [.. s.Notes, .. t.Notes]));
        }

        foreach (string n in muxReport.Notes)
        {
            notes.Add(n);
        }

        return new ReconstructionReport(
            length,
            loc.HeaderOffset,
            loc.PayloadStart,
            loc.PayloadEnd,
            muxReport.BytesWritten,
            tracks,
            scanner.Used,
            scanner.Skipped,
            scanner.SkippedCount,
            scanner.UsedBytes,
            notes);
    }

    private sealed class TrackSetup
    {
        public required TrackFramer Framer { get; set; }

        public MediaTrack? Track { get; set; }

        public IsoTrackOptions? Options { get; set; }

        public ReconstructionSetupSource Source { get; set; }

        public List<string> Notes { get; } = [];

        public TrackKind Kind => Framer.Kind;

        public CodecId Codec { get; set; }

        public int SampleRate { get; set; }

        public int Channels { get; set; }

        public GuessedAudioSetup GuessedSetup { get; set; }

        /// <summary>Frame rate the stream itself states (numerator, denominator), or (0, 0).</summary>
        public (long N, long D) StreamRate { get; set; }

        /// <summary>Median sample duration of the reference, in the reference track's timescale; 0 when there is no reference.</summary>
        public long ReferenceFrameTicks { get; set; }

        public bool IsVideo => Framer.Kind == TrackKind.Video;

        public ReconstructionConfidence Confidence(TrackTiming t)
        {
            if (Source == ReconstructionSetupSource.Guessed)
            {
                return ReconstructionConfidence.Low;
            }

            if (Framer is PcmFramer || t.TimingAssumed)
            {
                return t.TimingAssumed ? ReconstructionConfidence.Low : ReconstructionConfidence.Medium;
            }

            return t.TimingEstimated || t.OrderUnknown ? ReconstructionConfidence.Medium : ReconstructionConfidence.High;
        }
    }

    private sealed class TrackTiming
    {
        public long Timescale { get; set; }

        public long FrameTicks { get; set; }

        public List<string> Notes { get; } = [];

        public bool TimingEstimated { get; set; }

        public bool TimingAssumed { get; set; }

        public bool OrderUnknown { get; set; }

        /// <summary>Per sample of the track (decode order): presentation time in ticks.</summary>
        public List<long> Pts { get; } = [];

        public List<long> Dts { get; } = [];

        public List<long> Duration { get; } = [];

        public long Delay { get; set; }
    }

    // ----- Locating the media data -------------------------------------------------------------------------------------------

    private readonly record struct MediaDataLocation(long HeaderOffset, long PayloadStart, long PayloadEnd);

    private readonly record struct FileTypeInfo(string? Major, uint? Minor, IReadOnlyList<string>? Compatible, IReadOnlyList<string>? Brands);

    private static MediaDataLocation Locate(IMediaSource src, long length, List<string> notes, CancellationToken ct)
    {
        Span<byte> h = stackalloc byte[16]; // fixed small buffer: one box header
        long pos = 0;
        bool sawBox = false;
        long afterFtyp = -1;
        while (pos + 8 <= length)
        {
            int got = src.Read(pos, h, ct);
            if (got < 8)
            {
                break;
            }

            ulong size = BinaryPrimitives.ReadUInt32BigEndian(h);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
            int headerSize = 8;
            if (size == 1)
            {
                if (got < 16)
                {
                    break;
                }

                size = BinaryPrimitives.ReadUInt64BigEndian(h[8..]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = (ulong)(length - pos);
            }

            if (!KnownTopLevel(type) || size < (ulong)headerSize)
            {
                break;
            }

            if (type == BoxTypes.Mdat)
            {
                long end = size > (ulong)(length - pos) ? length : pos + (long)size;
                if (size > (ulong)(length - pos))
                {
                    notes.Add("The media data box declares more bytes than the file holds (the recording was cut); everything up to the end of the file was scanned.");
                }
                else if (end < length)
                {
                    notes.Add($"{length - end} bytes follow the media data box and were not scanned.");
                }

                return new MediaDataLocation(pos, pos + headerSize, end);
            }

            sawBox = true;
            if (type == 0x66747970 /* ftyp */)
            {
                afterFtyp = pos + (long)size;
            }

            if (size > (ulong)(length - pos))
            {
                break;
            }

            pos += (long)size;
        }

        // The box chain broke (a zeroed or damaged movie box). Look for the media data box header itself.
        long found = FindMediaDataHeader(src, length, ct);
        if (found >= 0)
        {
            int got = src.Read(found, h, ct);
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(h);
            int headerSize = 8;
            if (size == 1 && got >= 16)
            {
                size = BinaryPrimitives.ReadUInt64BigEndian(h[8..]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = (ulong)(length - found);
            }

            long end = size > (ulong)(length - found) || size < (ulong)headerSize ? length : found + (long)size;
            notes.Add($"The box chain before the media data is damaged; the media data box was found at offset {found} by its type.");
            return new MediaDataLocation(found, found + headerSize, end);
        }

        if (sawBox && afterFtyp > 0 && length - afterFtyp > 4096)
        {
            notes.Add("No media data box was found; the bytes after the file type box were scanned as media data.");
            return new MediaDataLocation(-1, afterFtyp, length);
        }

        return new MediaDataLocation(-1, -1, -1);
    }

    private static bool KnownTopLevel(uint type) => type is
        0x66747970 or 0x6D6F6F76 or BoxTypes.Mdat or 0x66726565 or 0x736B6970 or 0x77696465 or 0x706E6F74 or 0x75756964 or 0x6D657461 or 0x6A756E6B or 0x73747970 or 0x73696478 or 0x70726674 or 0x7064696E or 0x6D667261 or 0x6D6F6F66;

    private static long FindMediaDataHeader(IMediaSource src, long length, CancellationToken ct)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent((1 << 20) + 16);
        try
        {
            long limit = Math.Min(length, MaxHeaderSearch);
            long at = 0;
            while (at < limit)
            {
                ct.ThrowIfCancellationRequested();
                int n = src.Read(at, buf.AsSpan(0, 1 << 20), ct);
                if (n < 8)
                {
                    break;
                }

                int i = 0;
                ReadOnlySpan<byte> span = buf.AsSpan(0, n);
                while (true)
                {
                    int j = span[i..].IndexOf("mdat"u8);
                    if (j < 0)
                    {
                        break;
                    }

                    int p = i + j;
                    if (p >= 4)
                    {
                        uint size = BinaryPrimitives.ReadUInt32BigEndian(span[(p - 4)..]);
                        if (size is 0 or 1 || size >= 8)
                        {
                            return at + p - 4;
                        }
                    }

                    i = p + 1;
                }

                at += n - 8;
            }

            return -1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static (FileTypeInfo, IsoFileKind) ReadFileType(IMediaSource src, ReconstructionOptions options)
    {
        Span<byte> b = stackalloc byte[256]; // fixed small buffer: a file type box is short
        int n = src.Read(0, b);
        FileTypeInfo info = default;
        IsoFileKind kind = IsoFileKind.Mp4;
        if (n >= 16 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p')
        {
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(b), (uint)n);
            string major = AsciiBrand(b.Slice(8, 4));
            uint minor = BinaryPrimitives.ReadUInt32BigEndian(b[12..]);
            List<string> compat = [];
            for (int p = 16; p + 4 <= size; p += 4)
            {
                compat.Add(AsciiBrand(b.Slice(p, 4)));
            }

            if (major.Length == 4 && compat.TrueForAll(c => c.Length == 4))
            {
                info = new FileTypeInfo(major, minor, compat, compat);
            }

            kind = major switch
            {
                "qt  " => IsoFileKind.Mov,
                "M4A " => IsoFileKind.M4a,
                "M4V " => IsoFileKind.M4v,
                _ when major.StartsWith("3g2", StringComparison.Ordinal) => IsoFileKind.ThreeG2,
                _ when major.StartsWith("3gp", StringComparison.Ordinal) || major.StartsWith("3ge", StringComparison.Ordinal) || major.StartsWith("3gg", StringComparison.Ordinal) => IsoFileKind.ThreeGp,
                _ => IsoFileKind.Mp4,
            };
        }

        return (info, options.Kind ?? kind);
    }

    private static string AsciiBrand(ReadOnlySpan<byte> b)
    {
        char[] c = new char[4];
        for (int i = 0; i < 4; i++)
        {
            c[i] = b[i] is >= 0x20 and < 0x7f ? (char)b[i] : '?';
        }

        return new string(c);
    }

    // ----- The reference file -------------------------------------------------------------------------------------------------

    private static readonly HashSet<string> s_carriedEntryBoxes = ["colr", "clap", "fiel", "gama", "btrt", "chnl", "srat", "mdcv", "clli", "pasp"];

    private static void LoadReference(IMediaSource reference, List<TrackSetup> setups, ReconstructionOptions options, List<string> notes, CancellationToken ct)
    {
        IsoBmffDemuxer d;
        try
        {
            d = IsoBmffDemuxer.Open(reference, new MediaOpenOptions { Limits = options.Limits }, ct);
        }
        catch (MediaException e)
        {
            throw new ReconstructionException(ReconstructionFailure.NoTracks, "The reference file could not be read: " + e.Message);
        }

        using (d)
        {
            Dictionary<int, List<long>> durations = [];
            Dictionary<int, Dictionary<int, int>> sizeHistogram = [];
            int total = 0;
            while (total < 4000 && d.TryReadPacket(out PacketView p))
            {
                total++;
                int ti = p.Info.TrackIndex;
                if (!durations.TryGetValue(ti, out List<long>? list))
                {
                    durations[ti] = list = [];
                    sizeHistogram[ti] = [];
                }

                if (list.Count < 400)
                {
                    MediaTrack t = d.Tracks[ti];
                    list.Add(p.Info.Duration.IsKnown && p.Info.Duration.TryRescale(t.Timescale, MediaRounding.NearestAwayFromZero, out MediaTime r) ? r.Ticks : 0);
                    sizeHistogram[ti][p.Info.Size] = sizeHistogram[ti].GetValueOrDefault(p.Info.Size) + 1;
                }

                if (durations.Count == d.Tracks.Count && durations.Values.All(l => l.Count >= 300))
                {
                    break;
                }
            }

            foreach (MediaTrack t in d.Tracks)
            {
                TrackSetup? s = SetupFromReference(d, t, options, notes);
                if (s is null)
                {
                    continue;
                }

                List<long> list = durations.GetValueOrDefault(t.Index) ?? [];
                list.RemoveAll(x => x <= 0);
                list.Sort();
                s.ReferenceFrameTicks = list.Count > 0 ? list[list.Count / 2] : 0;
                if (s.Framer is PcmFramer && sizeHistogram.TryGetValue(t.Index, out Dictionary<int, int>? hist) && hist.Count > 0)
                {
                    int best = 0, bestCount = 0;
                    foreach (KeyValuePair<int, int> kv in hist)
                    {
                        if (kv.Value > bestCount)
                        {
                            best = kv.Key;
                            bestCount = kv.Value;
                        }
                    }

                    int histTotal = 0;
                    foreach (int c in hist.Values)
                    {
                        histTotal += c;
                    }

                    s.Framer = new PcmFramer(((PcmFramer)s.Framer).BlockAlign, Math.Max(1, best / ((PcmFramer)s.Framer).BlockAlign), bestCount * 10 >= histTotal * 8);
                }

                setups.Add(s);
            }
        }
    }

    private static TrackSetup? SetupFromReference(IsoBmffDemuxer d, MediaTrack t, ReconstructionOptions options, List<string> notes)
    {
        TrackFramer? framer = null;
        if (t.Kind == TrackKind.Video)
        {
            if (t.Codec == CodecId.H264 && H264.AvcDecoderConfigurationRecord.TryParse(t.Configuration.Data, out H264.AvcDecoderConfigurationRecord? avc) && avc is not null)
            {
                NalVideoFramer f = new(NalCodec.H264, avc.LengthSize);
                foreach (ReadOnlyMemory<byte> u in avc.SequenceParameterSets)
                {
                    f.LearnParameterSet(u.Span);
                }

                foreach (ReadOnlyMemory<byte> u in avc.PictureParameterSets)
                {
                    f.LearnParameterSet(u.Span);
                }

                framer = f;
            }
            else if (t.Codec == CodecId.Hevc && Hevc.HevcDecoderConfigurationRecord.TryParse(t.Configuration.Data, out Hevc.HevcDecoderConfigurationRecord? hevc) && hevc is not null)
            {
                NalVideoFramer f = new(NalCodec.Hevc, hevc.LengthSize);
                foreach (Hevc.HevcNalArray a in hevc.Arrays)
                {
                    foreach (ReadOnlyMemory<byte> u in a.Units)
                    {
                        f.LearnParameterSet(u.Span);
                    }
                }

                framer = f;
            }
            else if (t.Codec == CodecId.Av1)
            {
                framer = new Av1ObuFramer();
            }
        }
        else if (t.Kind == TrackKind.Audio)
        {
            int rate = t.Audio?.SampleRate ?? 0;
            if (t.Codec == CodecId.AmrNb || t.Codec == CodecId.AmrWb)
            {
                framer = AudioFramers.Amr(t.Codec == CodecId.AmrWb);
            }
            else if ((t.Codec == CodecId.Mp3 || t.Codec == CodecId.Mp2) && rate > 0)
            {
                framer = AudioFramers.MpegAudio(t.Codec == CodecId.Mp2 || rate >= 32000 ? 1152 : 576, rate, t.Codec == CodecId.Mp3 ? 3 : 2);
            }
            else if ((t.Codec == CodecId.Ac3 || t.Codec == CodecId.Eac3) && rate > 0)
            {
                framer = AudioFramers.Ac3(rate, 1536);
            }
            else if (t.Codec == CodecId.Pcm && t.Audio is { Channels: > 0, BitsPerSample: > 0 } pa)
            {
                framer = new PcmFramer(pa.Channels * ((pa.BitsPerSample + 7) / 8), 1024);
            }
            else if (t.Codec != CodecId.Pcm)
            {
                // MP4 stores AAC setup inside an esds descriptor; the probe wants the AudioSpecificConfig it holds.
                ReadOnlyMemory<byte> setup = t.Configuration.Data;
                if (t.Codec == CodecId.Aac && Codecs.Aac.EsDescriptor.TryParse(setup, out Codecs.Aac.EsDescriptor? es) && es is not null && !es.DecoderSpecificInfo.IsEmpty)
                {
                    setup = es.DecoderSpecificInfo;
                }

                foreach (IAudioFrameProbe probe in options.AudioProbes)
                {
                    if (probe.Codec == t.Codec && probe.CreateFramer(setup.Span) is { } plug)
                    {
                        framer = AudioFramers.FromPlugin(plug);
                        break;
                    }
                }

                if (framer is null && t.Codec == CodecId.Aac)
                {
                    notes.Add($"Reference track {t.Index} is AAC: framing it needs the audio adapter (RecursiveAI.OneMedia.Audio). Its samples were not recovered.");
                    return null;
                }
            }
        }
        else if (t.CodecTag == "gpmd")
        {
            framer = new GpmfFramer();
        }

        if (framer is null)
        {
            notes.Add($"Reference track {t.Index} ({(t.CodecTag ?? t.Codec.Name)}) cannot be framed without its index and was left out.");
            return null;
        }

        TrackSetup s = new() { Framer = framer, Source = ReconstructionSetupSource.Reference, Codec = t.Codec, SampleRate = t.Audio?.SampleRate ?? 0, Channels = t.Audio?.Channels ?? 0 };
        framer.Track = -1;
        s.Track = t with { Duration = MediaTime.Unknown, PacketCount = -1, DurationIsPartial = false, Edit = new TrackEditInfo(Priming: t.Edit.Priming) };
        s.Options = CarriedEntryBoxes(d, t);
        if (framer is PcmFramer && t.Audio is { } pcmInfo)
        {
            s.Notes.Add($"PCM has no framing to check: samples are the bytes between the other tracks' samples, cut into packets of the reference's usual size ({pcmInfo.Channels} channel(s), {pcmInfo.BitsPerSample}-bit).");
        }

        return s;
    }

    private static IsoTrackOptions? CarriedEntryBoxes(IsoBmffDemuxer d, MediaTrack t)
    {
        if ((uint)t.Index >= (uint)d.Movie.Tracks.Count || d.Movie.Tracks[t.Index].SampleEntries.Count == 0)
        {
            return null;
        }

        List<ReadOnlyMemory<byte>> boxes = [];
        foreach (ExtensionBox e in d.Movie.Tracks[t.Index].SampleEntries[0].Extensions)
        {
            if (s_carriedEntryBoxes.Contains(e.TypeName))
            {
                boxes.Add(BoxBuilder.Create(e.Type, e.Payload.Span));
            }
        }

        return boxes.Count == 0 ? null : new IsoTrackOptions { SampleEntryBoxes = boxes };
    }

    // ----- Without a reference: parameter sets in the stream ------------------------------------------------------------------

    private static void DiscoverVideo(IMediaSource src, MediaDataLocation loc, List<TrackSetup> setups, ReconstructionOptions options, List<string> notes, CancellationToken ct)
    {
        long end = Math.Min(loc.PayloadEnd, loc.PayloadStart + (8L << 20));
        ByteWindow w = new(src, loc.PayloadEnd, ct);
        for (long p = loc.PayloadStart; p < end; p++)
        {
            ct.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> head = w.Get(p, 12);
            if (head.Length < 8)
            {
                break;
            }

            // AV1: a sequence header OBU (type 1, size field) or a temporal delimiter followed by one.
            if (head[0] is 0x0A or 0x12 && TryAv1(w, p, options, out TrackSetup? av1))
            {
                setups.Add(av1!);
                av1!.Framer.Track = setups.Count - 1;
                return;
            }

            for (int len = 4; len >= 1; len--)
            {
                int v = 0;
                for (int i = 0; i < len; i++)
                {
                    v = (v << 8) | head[i];
                }

                if (v is < 4 or > 4096)
                {
                    continue;
                }

                byte hdr = head[len];
                NalCodec? codec = (hdr & 0x9F) == 0x07 ? NalCodec.H264 : (hdr & 0x81) == 0 && (((hdr >> 1) & 0x3f) is 32 or 33) ? NalCodec.Hevc : null;
                if (codec is null)
                {
                    continue;
                }

                NalVideoFramer f = new(codec.Value, len) { MaxSample = (int)Math.Min(options.Limits.MaxPacketBytes, 1 << 28) };
                ReadOnlySpan<byte> data = w.Get(p, 1 << 20);
                if (f.TryFrame(data, w.ReachesEnd(p, data.Length), out FrameInfo info) != FrameStatus.Ok)
                {
                    continue;
                }

                f.Accept(data[..info.Length]);
                if (!f.HasParameterSets)
                {
                    continue;
                }

                TrackSetup s = BuildInStreamVideo(f, notes);
                setups.Add(s);
                f.Track = setups.Count - 1;
                return;
            }
        }
    }

    private static bool TryAv1(ByteWindow w, long p, ReconstructionOptions options, out TrackSetup? setup)
    {
        setup = null;
        Av1ObuFramer f = new() { MaxSample = (int)Math.Min(options.Limits.MaxPacketBytes, 1 << 28) };
        ReadOnlySpan<byte> data = w.Get(p, 1 << 20);
        if (f.TryFrame(data, w.ReachesEnd(p, data.Length), out FrameInfo info) != FrameStatus.Ok)
        {
            return false;
        }

        f.Accept(data[..info.Length]);
        if (f.SequenceHeaderObu.IsEmpty || !SequenceHeader.TryFind(f.SequenceHeaderObu.Span, out SequenceHeader sh, out _))
        {
            return false;
        }

        ArrayBufferWriter<byte> cfg = new();
        Av1CodecConfigurationRecord.FromSequenceHeader(sh, f.SequenceHeaderObu).Write(cfg);
        setup = new TrackSetup
        {
            Framer = f,
            Source = ReconstructionSetupSource.InStream,
            Codec = CodecId.Av1,
            Track = new MediaTrack
            {
                Index = 0,
                Kind = TrackKind.Video,
                Codec = CodecId.Av1,
                CodecTag = "av01",
                Configuration = new CodecConfiguration(cfg.WrittenMemory.ToArray(), "av1C"),
                Timescale = 1000,
                Video = new VideoTrackInfo(sh.MaxFrameWidth, sh.MaxFrameHeight),
            },
        };
        if (sh.HasTimingInfo && sh.EqualPictureInterval && sh.NumUnitsInDisplayTick > 0 && sh.NumTicksPerPicture > 0)
        {
            setup.StreamRate = ((long)sh.TimeScale, (long)sh.NumUnitsInDisplayTick * sh.NumTicksPerPicture);
        }

        return true;
    }

    private static TrackSetup BuildInStreamVideo(NalVideoFramer f, List<string> notes)
    {
        ArrayBufferWriter<byte> cfg = new();
        List<ReadOnlyMemory<byte>> sets = [];
        foreach (byte[] raw in f.ParameterSetNals)
        {
            sets.Add(raw);
        }

        byte[] annexB = NalRepackager.ParameterSetsToAnnexB(sets);
        int width, height;
        (long, long) rate = (0, 0);
        string tag, format;
        CodecId codec;
        if (f.Codec == NalCodec.H264)
        {
            H264.AvcDecoderConfigurationRecord.TryCreateFromAnnexB(annexB, out H264.AvcDecoderConfigurationRecord? rec, f.LengthSize);
            rec!.Write(cfg);
            f.TryGetAvcSps(out Codecs.H264.SequenceParameterSet sps);
            width = sps.Width;
            height = sps.Height;
            if (sps.FrameRateNumerator > 0 && sps.FrameRateDenominator > 0)
            {
                rate = (sps.FrameRateNumerator, sps.FrameRateDenominator);
            }

            tag = "avc1";
            format = "avcC";
            codec = CodecId.H264;
        }
        else
        {
            Hevc.HevcDecoderConfigurationRecord.TryCreateFromAnnexB(annexB, out Hevc.HevcDecoderConfigurationRecord? rec, f.LengthSize);
            rec!.Write(cfg);
            f.TryGetHevcSps(out Hevc.SequenceParameterSet sps);
            width = sps.Width;
            height = sps.Height;
            if (sps.HasTimingInfo && sps.NumUnitsInTick > 0 && sps.TimeScale > 0)
            {
                rate = (sps.TimeScale, sps.NumUnitsInTick);
            }

            tag = "hvc1";
            format = "hvcC";
            codec = CodecId.Hevc;
        }

        TrackSetup s = new()
        {
            Framer = f,
            Source = ReconstructionSetupSource.InStream,
            Codec = codec,
            Track = new MediaTrack
            {
                Index = 0,
                Kind = TrackKind.Video,
                Codec = codec,
                CodecTag = tag,
                Configuration = new CodecConfiguration(cfg.WrittenMemory.ToArray(), format),
                Timescale = 1000,
                Video = new VideoTrackInfo(width, height),
            },
        };
        s.StreamRate = rate;
        s.Notes.Add($"Codec setup built from the parameter sets found in the stream (NAL length size {f.LengthSize}).");
        _ = notes;
        return s;
    }

    // ----- Timing --------------------------------------------------------------------------------------------------------------

    private static readonly (int N, int D)[] s_commonRates =
    [
        (24000, 1001), (24, 1), (25, 1), (30000, 1001), (30, 1), (50, 1), (60000, 1001), (60, 1), (15, 1), (12, 1), (10, 1), (20, 1), (48, 1), (100, 1), (120, 1), (12000, 1001), (15000, 1001),
    ];

    private static List<TrackTiming> ComputeTiming(List<TrackSetup> setups, List<SampleRec> samples, ReconstructionOptions options, List<string> notes)
    {
        int n = setups.Count;
        List<TrackTiming> timing = [];
        List<List<int>> byTrack = [];
        for (int i = 0; i < n; i++)
        {
            timing.Add(new TrackTiming());
            byTrack.Add([]);
        }

        for (int i = 0; i < samples.Count; i++)
        {
            byTrack[samples[i].Track].Add(i);
        }

        // Audio first: it fixes the clock the video rate may be measured against.
        double audioSeconds = 0;
        int audioTrack = -1;
        for (int t = 0; t < n; t++)
        {
            if (setups[t].Kind == TrackKind.Audio && byTrack[t].Count > 0)
            {
                TimeAudio(setups[t], timing[t], samples, byTrack[t]);
                if (audioTrack < 0 || byTrack[t].Count > byTrack[audioTrack].Count)
                {
                    audioTrack = t;
                }
            }
            else if (setups[t].Kind == TrackKind.Data && byTrack[t].Count > 0)
            {
                TimeData(setups[t], timing[t], byTrack[t].Count);
            }
        }

        _ = audioSeconds;
        for (int t = 0; t < n; t++)
        {
            if (setups[t].IsVideo && byTrack[t].Count > 0)
            {
                TimeVideo(t, setups, timing[t], samples, byTrack[t], audioTrack, timing, options, notes);
            }
        }

        return timing;
    }

    private static void TimeAudio(TrackSetup s, TrackTiming t, List<SampleRec> samples, List<int> idx)
    {
        MediaTrack? rt = s.Track;
        long timescale = rt?.Timescale ?? s.SampleRate;
        long rate = s.SampleRate > 0 ? s.SampleRate : timescale;
        if (timescale <= 0)
        {
            timescale = rate = 48000;
            t.Notes.Add("The sample rate is unknown; 48000 Hz was assumed for the timing.");
            t.TimingAssumed = true;
        }

        t.Timescale = timescale;
        long cum = 0;
        long prev = 0;
        foreach (int i in idx)
        {
            SampleRec r = samples[i];
            int frames = r.Samples > 0 ? r.Samples : 0;
            if (frames == 0 && s.ReferenceFrameTicks > 0)
            {
                frames = (int)Math.Max(1, s.ReferenceFrameTicks * rate / timescale);
            }

            long dts = cum * timescale / rate;
            cum += frames;
            long next = cum * timescale / rate;
            t.Dts.Add(dts);
            t.Pts.Add(dts);
            t.Duration.Add(next - dts);
            prev = next;
        }

        _ = prev;
        if (s.Source == ReconstructionSetupSource.Reference && rt!.Edit.Priming.IsKnown && !rt.Edit.Priming.IsZero)
        {
            t.Notes.Add("Priming (encoder delay) was taken from the reference file.");
        }
    }

    private static void TimeData(TrackSetup s, TrackTiming t, int count)
    {
        t.Timescale = s.Track?.Timescale ?? 1000;
        long d = s.ReferenceFrameTicks > 0 ? s.ReferenceFrameTicks : t.Timescale;
        for (int i = 0; i < count; i++)
        {
            t.Dts.Add(i * d);
            t.Pts.Add(i * d);
            t.Duration.Add(d);
        }

        if (s.ReferenceFrameTicks == 0)
        {
            t.Notes.Add("One sample per second was assumed.");
            t.TimingAssumed = true;
        }
    }

    private static void TimeVideo(int track, List<TrackSetup> setups, TrackTiming t, List<SampleRec> samples, List<int> idx, int audioTrack, List<TrackTiming> timing, ReconstructionOptions options, List<string> notes)
    {
        TrackSetup s = setups[track];
        long timescale;
        long frame;
        if (s.ReferenceFrameTicks > 0 && s.Track is { Timescale: > 0 })
        {
            timescale = s.Track.Timescale;
            frame = s.ReferenceFrameTicks;
        }
        else
        {
            (long n, long d) = s.StreamRate;
            if (n > 0 && d > 0)
            {
                t.Notes.Add($"Frame rate {n}/{d} comes from the timing information in the stream.");
            }
            else if (audioTrack >= 0 && EstimateRate(samples, idx, audioTrack, setups[audioTrack], timing[audioTrack], out n, out d))
            {
                t.Notes.Add($"No frame rate in the stream: {n}/{d} frames per second was measured against the audio clock and rounded to a common rate.");
                t.TimingEstimated = true;
            }
            else
            {
                (n, d) = (options.AssumedFrameRate.Numerator, options.AssumedFrameRate.Denominator);
                t.Notes.Add($"No frame rate in the stream and no audio to measure against: {n}/{d} was assumed.");
                t.TimingAssumed = true;
            }

            long g = Gcd(n, d);
            timescale = n / g;
            frame = d / g;
            while (timescale < 600 && timescale * 2 <= int.MaxValue)
            {
                timescale *= 2;
                frame *= 2;
            }
        }

        t.Timescale = timescale;
        t.FrameTicks = frame;
        int count = idx.Count;
        long[] display = new long[count];
        bool ordered = ComputeDisplayOrder(setups[track], samples, idx, display, t, out long delay);
        t.Delay = delay;
        for (int k = 0; k < count; k++)
        {
            t.Dts.Add(k * frame);
            t.Pts.Add((ordered ? display[k] + delay : k) * frame);
            t.Duration.Add(frame);
        }

        if (!ordered)
        {
            t.OrderUnknown = setups[track].Framer is NalVideoFramer;
        }

        _ = notes;
    }

    // The slice headers' picture order counts give output order (H.264 §8.2.1, H.265 §8.3.1): a count that grows with display time,
    // restarted at each IDR picture. The distance between neighbouring counts in a stream is a fixed step (2 for H.264 frames, 1 for H.265).
    private static bool ComputeDisplayOrder(TrackSetup s, List<SampleRec> samples, List<int> idx, long[] display, TrackTiming t, out long delay)
    {
        delay = 0;
        int count = idx.Count;
        for (int k = 0; k < count; k++)
        {
            display[k] = k;
        }

        if (s.Framer is not NalVideoFramer)
        {
            return false; // AV1 temporal units are in output order already
        }

        bool any = false;
        bool unsupported = false;
        for (int k = 0; k < count; k++)
        {
            SampleRec r = samples[idx[k]];
            if ((r.Flags & SampleRec.HasOrder) != 0)
            {
                any = true;
                int type = (r.Flags >> SampleRec.OrderTypeShift) & 3;
                if (type != 0 || (r.Flags & SampleRec.FieldPicture) != 0)
                {
                    unsupported = true;
                }
            }
        }

        if (!any)
        {
            t.Notes.Add("Slice headers could not be read (parameter sets missing from the stream and reference); samples are assumed to be in output order.");
            return false;
        }

        // Pictures with picture_order_cnt_type 2 are never reordered; type 1 and field pictures are not handled.
        bool noReorder = true;
        for (int k = 0; k < count; k++)
        {
            SampleRec r = samples[idx[k]];
            if ((r.Flags & SampleRec.HasOrder) != 0 && ((r.Flags >> SampleRec.OrderTypeShift) & 3) == 0 && (r.Flags & SampleRec.FieldPicture) == 0)
            {
                noReorder = false;
                break;
            }
        }

        if (noReorder)
        {
            return true; // type 2 (or no order fields): output order is decode order
        }

        if (unsupported)
        {
            t.Notes.Add("The stream codes field pictures or uses picture_order_cnt_type 1; output order was not worked out and samples are assumed to be in decode order.");
            return false;
        }

        long[] poc = new long[count];
        long prevMsb = 0, prevLsb = 0;
        for (int k = 0; k < count; k++)
        {
            SampleRec r = samples[idx[k]];
            if ((r.Flags & SampleRec.HasOrder) == 0)
            {
                poc[k] = k == 0 ? 0 : poc[k - 1] + 1;
                continue;
            }

            bool reset = (r.Flags & SampleRec.ResetsOrder) != 0 || k == 0;
            if (reset)
            {
                prevMsb = 0;
                prevLsb = 0;
            }

            long maxLsb = 1L << r.OrderBits;
            long lsb = r.OrderLsb;
            long msb = prevMsb;
            if (lsb < prevLsb && prevLsb - lsb >= maxLsb / 2)
            {
                msb = prevMsb + maxLsb;
            }
            else if (lsb > prevLsb && lsb - prevLsb > maxLsb / 2)
            {
                msb = prevMsb - maxLsb;
            }

            poc[k] = msb + lsb;
            if ((r.Flags & SampleRec.IsReference) != 0)
            {
                prevMsb = msb;
                prevLsb = lsb;
            }
        }

        // The step between display positions: the gcd of the count differences inside each coded video sequence.
        long step = 0;
        for (int k = 1; k < count; k++)
        {
            if ((samples[idx[k]].Flags & SampleRec.ResetsOrder) == 0)
            {
                step = Gcd(step, Math.Abs(poc[k] - poc[k - 1]));
            }
        }

        if (step == 0)
        {
            step = 1;
        }

        int baseIndex = 0;
        // Display position inside a coded video sequence: its first picture's count anchors the position of the sequence's first index.
        int seqStart = 0;
        for (int k = 0; k < count; k++)
        {
            if (k == 0 || (samples[idx[k]].Flags & SampleRec.ResetsOrder) != 0)
            {
                seqStart = k;
                baseIndex = k;
            }

            display[k] = baseIndex + ((poc[k] - poc[seqStart]) / step);
        }

        long maxLead = 0;
        for (int k = 0; k < count; k++)
        {
            maxLead = Math.Max(maxLead, k - display[k]);
        }

        delay = maxLead;
        if (delay > 0)
        {
            t.Notes.Add($"Pictures are reordered; presentation is delayed by {delay} frame(s) after decoding (an edit list hides the delay).");
        }

        return true;
    }

    private static bool EstimateRate(List<SampleRec> samples, List<int> videoIdx, int audioTrack, TrackSetup audio, TrackTiming audioTiming, out long n, out long d)
    {
        n = d = 0;
        if (videoIdx.Count < 8 || audioTiming.Timescale <= 0)
        {
            return false;
        }

        // Audio time (ticks) elapsed before each video sample, in file order: the two tracks are interleaved in step, so the slope is the frame period.
        long audioTicks = 0;
        int a = 0;
        int sampleIndexOfAudio = 0;
        List<(int k, long ticks)> points = [];
        int vi = 0;
        for (int i = 0; i < samples.Count && vi < videoIdx.Count; i++)
        {
            if (samples[i].Track == audioTrack)
            {
                audioTicks = audioTiming.Dts[sampleIndexOfAudio] + audioTiming.Duration[sampleIndexOfAudio];
                sampleIndexOfAudio++;
                a++;
            }
            else if (i == videoIdx[vi])
            {
                points.Add((vi, audioTicks));
                vi++;
            }
        }

        _ = a;
        if (points.Count < 8)
        {
            return false;
        }

        // Least-squares slope over the second half (the interleave settles after the first chunks).
        int from = points.Count / 4;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        int m = points.Count - from;
        for (int i = from; i < points.Count; i++)
        {
            double x = points[i].k;
            double y = points[i].ticks;
            sx += x;
            sy += y;
            sxx += x * x;
            sxy += x * y;
        }

        double denom = (m * sxx) - (sx * sx);
        if (denom <= 0)
        {
            return false;
        }

        double slopeTicks = ((m * sxy) - (sx * sy)) / denom; // audio ticks per video frame
        if (slopeTicks <= 0)
        {
            return false;
        }

        double fps = audioTiming.Timescale / slopeTicks;
        double best = double.MaxValue;
        foreach ((int cn, int cd) in s_commonRates)
        {
            double r = (double)cn / cd;
            double err = Math.Abs(r - fps) / fps;
            if (err < best)
            {
                best = err;
                n = cn;
                d = cd;
            }
        }

        _ = audio;
        return best < 0.06;
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return Math.Max(1, a);
    }

    // ----- Writing -------------------------------------------------------------------------------------------------------------

    private static MuxReport Mux(IMediaSource input, Stream output, List<TrackSetup> setups, List<TrackTiming> timing, List<SampleRec> samples, IsoMuxOptions options, long limit, CancellationToken ct)
    {
        using IsoMuxer mux = new(output, options);
        int[] next = new int[setups.Count];
        bool[] declared = new bool[setups.Count];
        long[] counts = new long[setups.Count];
        foreach (SampleRec r in samples)
        {
            counts[r.Track]++;
        }

        // Tracks that recovered nothing are not declared (an empty track would be refused or written empty).
        int[] outIndex = new int[setups.Count];
        for (int i = 0; i < setups.Count; i++)
        {
            outIndex[i] = -1;
            if (counts[i] == 0)
            {
                continue;
            }

            MediaTrack track = BuildTrack(setups[i], timing[i], samples);
            outIndex[i] = mux.AddTrack(track, setups[i].Options);
            declared[i] = true;
        }

        ByteWindow window = new(input, limit, ct);
        foreach (SampleRec r in samples)
        {
            int ti = r.Track;
            if (!declared[ti])
            {
                continue;
            }

            int k = next[ti]++;
            TrackTiming t = timing[ti];
            ReadOnlySpan<byte> data = window.Get(r.Offset, r.Length);
            if (data.Length != r.Length)
            {
                throw new MediaFormatException($"The input ended while the sample at offset {r.Offset} was copied.") { Offset = r.Offset, Kind = DefectKind.Truncated };
            }

            PacketInfo info = new(
                outIndex[ti],
                new MediaTime(t.Pts[k], t.Timescale),
                new MediaTime(t.Dts[k], t.Timescale),
                new MediaTime(t.Duration[k], t.Timescale),
                r.Key ? PacketAttributes.Keyframe : PacketAttributes.None,
                new ByteRange(r.Offset, r.Length),
                r.Length);
            mux.WritePacket(info, data, ct);
        }

        return mux.Finish(ct);
    }

    private static MediaTrack BuildTrack(TrackSetup s, TrackTiming t, List<SampleRec> samples)
    {
        MediaTrack track = s.Track ?? BuildDiscoveredAudio(s);
        TrackEditInfo edit = track.Edit;
        if (s.IsVideo && t.Delay > 0)
        {
            edit = new TrackEditInfo(Priming: new MediaTime(t.Delay * t.FrameTicks, t.Timescale));
        }
        else if (s.IsVideo)
        {
            edit = default;
        }

        _ = samples;
        return track with { Timescale = t.Timescale, Edit = edit, Id = 0 };
    }

    private static MediaTrack BuildDiscoveredAudio(TrackSetup s)
    {
        string? tag = null;
        ReadOnlyMemory<byte> config = default;
        string? format = null;
        if (s.Codec == CodecId.Aac)
        {
            tag = "mp4a";
            config = s.GuessedSetup.Config;
            format = "asc";
        }
        else if (s.Codec == CodecId.AmrNb)
        {
            tag = "samr";
        }
        else if (s.Codec == CodecId.AmrWb)
        {
            tag = "sawb";
        }

        return new MediaTrack
        {
            Index = 0,
            Kind = TrackKind.Audio,
            Codec = s.Codec,
            CodecTag = tag,
            Configuration = new CodecConfiguration(config, format),
            Timescale = s.SampleRate,
            Audio = new AudioTrackInfo(s.SampleRate, s.Channels),
        };
    }
}
