using System.Globalization;
using RecursiveAI.OneMedia.Captions;
using System.Linq;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Streaming;

/// <summary>One run of the packager: owns the output directory, the per-track bookkeeping and the manifests.</summary>
internal sealed class PackagingRun : ISegmentSink, IDisposable
{
    internal readonly record struct SegmentRecord(SegmentInfo Info, long Bytes, long Offset, string File);

    internal sealed class TrackOut(SegmentedTrack track, string name)
    {
        public SegmentedTrack Track { get; } = track;

        public string Name { get; } = name;

        public List<SegmentRecord> Segments { get; } = [];

        public byte[] Init { get; set; } = [];

        public FileStream? Spool { get; set; }

        public string? SpoolPath { get; set; }

        public long SpoolBytes { get; set; }

        public long SidxOffset { get; set; }

        public long SidxLength { get; set; }

        public long InitLength { get; set; }

        public long FirstNumber { get; set; } = 1;

        public Queue<string> Expired { get; } = new();

        /// <summary>The first segment's start, kept after it leaves a live window.</summary>
        public long FirstStartTicks { get; set; } = -1;

        public long EndTicks { get; set; }
    }

    internal sealed class TextOut(string name, PackagingSubtitle subtitle)
    {
        public string Name { get; } = name;

        public PackagingSubtitle Subtitle { get; } = subtitle;

        public int Segments { get; set; }

        /// <summary>The track whose segment boundaries the text segments follow.</summary>
        public TrackOut? Reference { get; set; }
    }

    private readonly string _dir;
    private readonly PackagingOptions _options;
    private readonly int _inputCount;
    private readonly List<TrackOut> _tracks = [];
    private readonly List<TextOut> _text = [];
    private readonly Dictionary<SegmentedTrack, TrackOut> _byTrack = [];
    private readonly List<Remuxing.RemuxLoss> _losses = [];
    private readonly List<string> _notes = [];
    private readonly List<string> _manifests = [];
    private readonly List<SubtitleTrack> _containerText = [];
    private readonly List<(SubtitleDocument Doc, string? Language, string? Name, bool Default, bool Forced)> _subtitleDocs = [];
    private int _inputIndex;
    private int _videoCount, _audioCount;
    private bool _liveMasterWritten;
    private bool _anySegments;

    public PackagingRun(string directory, PackagingOptions options, int inputCount)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _dir = Path.GetFullPath(directory);
        _options = options;
        _inputCount = inputCount;
        if (options.Live is not null)
        {
            if (inputCount != 1)
            {
                throw new ArgumentException("Live packaging takes exactly one input.", nameof(inputCount));
            }

            if (options.SingleFile)
            {
                throw new ArgumentException("Single-file output is for video on demand; live output writes a file per segment.", nameof(options));
            }

            if (options.Live.WindowSegments < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "The live window holds at least one segment.");
            }

            if (options.Subtitles.Count > 0)
            {
                throw new ArgumentException("Subtitle renditions are packaged for video on demand only.", nameof(options));
            }
        }

        if (options.Formats == PackagingFormats.None)
        {
            return;
        }

        Directory.CreateDirectory(_dir);
        if (!options.Overwrite)
        {
            foreach (string name in new[] { "master.m3u8", "manifest.mpd" })
            {
                if (File.Exists(Path.Combine(_dir, name)))
                {
                    throw new IOException($"'{name}' already exists in '{_dir}'; set PackagingOptions.Overwrite to replace it.");
                }
            }
        }
    }

    public void AddInput(MediaFile file, CancellationToken cancellationToken)
    {
        bool first = _inputIndex++ == 0;
        IDemuxer demuxer = file.Demuxer ?? throw new MediaReadStateException(MediaReadStateReason.AsynchronousSource, "This file was opened from an asynchronous source; packaging reads synchronously.");
        bool videoTaken = false;
        CmafSegmenter segmenter = new(
            file.Tracks,
            this,
            new SegmenterOptions
            {
                SegmentDuration = _options.SegmentDuration,
                Limits = _options.Limits,
                // The first input gives every track; ladder inputs give one video track each.
                Select = t => first || (t.Kind == TrackKind.Video && !videoTaken && (videoTaken = true)),
                SessionIsoOptions = file.Demuxer is IsoBmffDemuxer iso
                    ? (s, t) => Remuxing.Remuxer.IsoTrackOptionsFor(iso, t, s)
                    : (_, t) => Remuxing.Remuxer.PcmOptionsFor(demuxer, t),
            });
        using (segmenter)
        {
            foreach (SegmentedTrack t in segmenter.Tracks)
            {
                string name = t.Kind == TrackKind.Video ? $"video{_videoCount++}" : $"audio{_audioCount++}";
                TrackOut o = new(t, name);
                _tracks.Add(o);
                _byTrack[t] = o;
            }

            HashSet<int> used = [];
            foreach (SegmentedTrack t in segmenter.Tracks)
            {
                used.Add(t.Input.Index);
            }

            foreach (MediaTrack t in file.Tracks)
            {
                if (!used.Contains(t.Index))
                {
                    demuxer.SetTrackEnabled(t.Index, false);
                    if (first && t.Kind is not (TrackKind.Subtitle or TrackKind.Data) )
                    {
                        _notes.Add($"Input track {t.Index} ({t.Kind} {t.Codec.Name}) was not packaged.");
                    }
                }
            }

            int generation = file.BeginRead();
            while (demuxer.TryReadPacket(out PacketView view))
            {
                file.EnsureCurrentRead(generation);
                segmenter.WritePacket(view.Info, view.Data, cancellationToken);
            }

            SegmenterReport report = segmenter.Finish(cancellationToken);
            _losses.AddRange(report.Losses);
            foreach (SegmentedTrack t in report.Tracks)
            {
                if (t.SegmentCount == 0)
                {
                    _notes.Add($"Input track {t.Input.Index} ({t.Kind} {t.Input.Codec.Name}) produced no segments and was left out.");
                }
            }
        }

        if (first && _options.Live is null && _options.IncludeContainerSubtitles)
        {
            try
            {
                foreach (SubtitleTrack s in file.ContainerSubtitles)
                {
                    if (s.Kind == SubtitleKind.Text)
                    {
                        _subtitleDocs.Add((s.Document, s.Language, s.Name, (s.Disposition & TrackDisposition.Default) != 0, (s.Disposition & TrackDisposition.Forced) != 0));
                    }
                }
            }
            catch (MediaReadStateException e)
            {
                _notes.Add("The input's text tracks were not packaged: " + e.Message);
            }
        }
    }

    // ---- sink -------------------------------------------------------------------------------------------------

    public void OnInitSegment(SegmentedTrack track, ReadOnlySpan<byte> data)
    {
        TrackOut t = _byTrack[track];
        Directory.CreateDirectory(Path.Combine(_dir, t.Name));
        t.InitLength = data.Length;
        if (_options.SingleFile)
        {
            t.Init = data.ToArray();
            t.SpoolPath = Path.Combine(_dir, t.Name, "media.mp4.part");
            t.Spool = new FileStream(t.SpoolPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        }
        else
        {
            WriteFile(Path.Combine(t.Name, "init.mp4"), data);
        }
    }

    public void OnMediaSegment(SegmentedTrack track, in SegmentInfo segment, ReadOnlySpan<byte> data)
    {
        TrackOut t = _byTrack[track];
        _anySegments = true;
        string file;
        long offset = 0;
        if (_options.SingleFile)
        {
            file = $"{t.Name}/media.mp4";
            offset = t.SpoolBytes;
            t.Spool!.Write(data);
            t.SpoolBytes += data.Length;
        }
        else
        {
            file = $"{t.Name}/seg-{segment.Number.ToString("D5", CultureInfo.InvariantCulture)}.m4s";
            WriteFile(file, data);
        }

        if (t.FirstStartTicks < 0)
        {
            t.FirstStartTicks = segment.StartTicks;
        }

        t.EndTicks = segment.StartTicks + segment.DurationTicks;
        t.Segments.Add(new SegmentRecord(segment, data.Length, offset, file));
        _options.SegmentWritten?.Invoke(new PackagedSegment(file, track, segment, data.Length));
        if (_options.Live is { } live)
        {
            LiveStep(t, live);
        }
    }

    private void WriteFile(string relative, ReadOnlySpan<byte> data)
    {
        using FileStream f = new(Path.Combine(_dir, relative), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        f.Write(data);
    }

    // Playlists and the MPD are replaced atomically: a client that reads while a segment lands sees the old or the new one, never half of one.
    private void WriteManifest(string relative, string text)
    {
        string path = Path.Combine(_dir, relative);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        if (!_manifests.Contains(relative))
        {
            _manifests.Add(relative);
        }

        _options.ManifestWritten?.Invoke(relative);
    }

    // ---- live -------------------------------------------------------------------------------------------------

    private void LiveStep(TrackOut t, LiveOptions live)
    {
        while (t.Segments.Count > live.WindowSegments)
        {
            SegmentRecord gone = t.Segments[0];
            t.Segments.RemoveAt(0);
            t.FirstNumber = gone.Info.Number + 1;
            if (live.DeleteExpired)
            {
                t.Expired.Enqueue(gone.File);
                while (t.Expired.Count > live.ExpiredSegmentsKept)
                {
                    string old = Path.Combine(_dir, t.Expired.Dequeue());
                    if (File.Exists(old))
                    {
                        File.Delete(old);
                    }
                }
            }
        }

        if (_options.Formats.HasFlag(PackagingFormats.Hls))
        {
            WriteManifest($"{t.Name}/index.m3u8", PackagingManifests.HlsMedia(t, _options, ended: false).ToString());
            if (!_liveMasterWritten && AllInitialised())
            {
                WriteManifest("master.m3u8", PackagingManifests.HlsMaster(_tracks, _text, _options).ToString());
                _liveMasterWritten = true;
            }
        }

        if (_options.Formats.HasFlag(PackagingFormats.Dash) && AllInitialised())
        {
            WriteManifest("manifest.mpd", PackagingManifests.Mpd(_tracks, _text, _options, ended: false, _notes).ToString());
        }
    }

    private bool AllInitialised()
    {
        foreach (TrackOut t in _tracks)
        {
            if (t.Track.Declared is null && t.Track.SegmentCount == 0 && t.Segments.Count == 0)
            {
                return false;
            }
        }

        return true;
    }

    // ---- finish -----------------------------------------------------------------------------------------------

    public PackagingReport Finish(CancellationToken cancellationToken)
    {
        // A track that never produced a segment has nothing to list.
        _tracks.RemoveAll(t => t.Track.SegmentCount == 0);
        if (_tracks.Count == 0 || !_anySegments)
        {
            throw new MuxRefusedException(MuxRefusalReason.UnsupportedCodec, "No track of the input could be packaged." + (_losses.Count > 0 ? " " + string.Join("; ", _losses.Select(l => $"track {l.InputTrack}: {l.Reason}")) : string.Empty));
        }

        if (_options.SingleFile)
        {
            foreach (TrackOut t in _tracks)
            {
                AssembleSingleFile(t, cancellationToken);
            }
        }

        CheckAlignment();
        if (_options.Live is null)
        {
            AddSubtitles();
        }

        if (_options.Formats.HasFlag(PackagingFormats.Hls))
        {
            foreach (TrackOut t in _tracks)
            {
                WriteManifest($"{t.Name}/index.m3u8", PackagingManifests.HlsMedia(t, _options, ended: true).ToString());
            }

            foreach (TextOut x in _text)
            {
                WriteManifest($"{x.Name}/index.m3u8", PackagingManifests.HlsText(x, x.Reference!).ToString());
            }

            WriteManifest("master.m3u8", PackagingManifests.HlsMaster(_tracks, _text, _options).ToString());
        }

        if (_options.Formats.HasFlag(PackagingFormats.Dash))
        {
            WriteManifest("manifest.mpd", PackagingManifests.Mpd(_tracks, _text, _options, ended: true, _notes).ToString());
        }

        List<PackagedTrack> tracks = [];
        foreach (TrackOut t in _tracks)
        {
            double seconds = t.Track.Timescale > 0 ? t.Track.DurationTicks / (double)t.Track.Timescale : 0;
            tracks.Add(new PackagedTrack(t.Name, t.Track.Kind, t.Track.Codecs, t.Track.SegmentCount, t.Track.MediaBytes, seconds));
        }

        foreach (TextOut x in _text)
        {
            tracks.Add(new PackagedTrack(x.Name, TrackKind.Subtitle, "wvtt", x.Segments, 0, 0));
        }

        return new PackagingReport(_dir, tracks, _manifests, _losses, _notes);
    }

    private void CheckAlignment()
    {
        // Ladder renditions of one kind should cut at the same keyframes for a player to switch between them.
        TrackOut? reference = _tracks.Find(t => t.Track.Kind == TrackKind.Video);
        if (reference is null)
        {
            return;
        }

        foreach (TrackOut t in _tracks)
        {
            if (t == reference || t.Track.Kind != TrackKind.Video)
            {
                continue;
            }

            if (!SameBoundaries(reference, t))
            {
                _notes.Add($"{t.Name}: segment boundaries differ from {reference.Name}; the renditions do not switch on segment boundaries (their keyframes are not aligned).");
            }
        }
    }

    internal static bool SameBoundaries(TrackOut a, TrackOut b)
    {
        if (a.Segments.Count != b.Segments.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Segments.Count; i++)
        {
            MediaTime x = new(a.Segments[i].Info.StartTicks, a.Track.Timescale);
            MediaTime y = new(b.Segments[i].Info.StartTicks, b.Track.Timescale);
            // Equal to within a millisecond: the encoders cut at the same frame, and the tracks' timescales differ.
            MediaTime delta = x > y ? x - y : y - x;
            if (delta > new MediaTime(1, 1000))
            {
                return false;
            }
        }

        return true;
    }

    // One file per track: the init segment, a segment index, then the fragments (ISO/IEC 23009-1 §6.3.4.2 on-demand layout, ISO/IEC 14496-12 §8.16.3).
    private void AssembleSingleFile(TrackOut t, CancellationToken cancellationToken)
    {
        FileStream spool = t.Spool!;
        spool.Flush();
        byte[] sidx = BuildSegmentIndex(t);
        string finalPath = Path.Combine(_dir, t.Name, "media.mp4");
        using (FileStream output = new(finalPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
        {
            output.Write(t.Init);
            t.SidxOffset = output.Position;
            output.Write(sidx);
            t.SidxLength = sidx.Length;
            spool.Position = 0;
            spool.CopyTo(output, 1 << 20);
        }

        spool.Dispose();
        File.Delete(t.SpoolPath!);
        t.Spool = null;
        long fragmentsStart = t.InitLength + sidx.Length;
        for (int i = 0; i < t.Segments.Count; i++)
        {
            SegmentRecord r = t.Segments[i];
            t.Segments[i] = r with { Offset = r.Offset + fragmentsStart };
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static byte[] BuildSegmentIndex(TrackOut t)
    {
        if (t.Segments.Count > ushort.MaxValue)
        {
            throw new MuxRefusedException(MuxRefusalReason.LimitExceeded, $"A segment index holds at most 65535 references; {t.Name} has {t.Segments.Count} segments. Use longer segments.");
        }

        using MemoryStream ms = new();
        void U32(uint v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U64(ulong v) { U32((uint)(v >> 32)); U32((uint)v); }
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        int count = t.Segments.Count;
        U32((uint)(40 + (12 * count)));
        U32(0x73696478); // 'sidx'
        U32(0x01000000); // version 1, flags 0
        U32(1); // reference_ID: the track id the muxer wrote
        U32((uint)t.Track.Timescale);
        U64((ulong)Math.Max(0, t.Segments[0].Info.EarliestPresentationTicks));
        U64(0); // first_offset: the first fragment follows the index
        U16(0);
        U16(count);
        foreach (SegmentRecord r in t.Segments)
        {
            U32((uint)r.Bytes); // reference_type 0: a movie fragment
            U32((uint)Math.Min(r.Info.DurationTicks, uint.MaxValue));
            // starts_with_SAP(1) SAP_type(3) SAP_delta_time(28): type 1 at delta 0 for a segment that starts at a sync sample.
            U32(r.Info.StartsWithSync ? 0x90000000u : 0u);
        }

        return ms.ToArray();
    }

    // ---- subtitles --------------------------------------------------------------------------------------------

    private void AddSubtitles()
    {
        List<(SubtitleDocument Doc, string? Language, string? Name, bool Default, bool Forced)> docs = [];
        foreach (PackagingSubtitle s in _options.Subtitles)
        {
            docs.Add((s.Document, s.Language, s.Name, s.Default, s.Forced));
        }

        docs.AddRange(_subtitleDocs);
        if (docs.Count == 0)
        {
            return;
        }

        TrackOut? reference = _tracks.Find(t => t.Track.Kind == TrackKind.Video) ?? _tracks[0];
        int n = 0;
        foreach ((SubtitleDocument doc, string? lang, string? name, bool isDefault, bool forced) in docs)
        {
            string dir = $"text{n++}";
            TextOut x = new(dir, new PackagingSubtitle(doc, lang ?? doc.Language, name) { Default = isDefault, Forced = forced }) { Reference = reference };
            Directory.CreateDirectory(Path.Combine(_dir, dir));
            if (_options.Formats.HasFlag(PackagingFormats.Hls))
            {
                for (int i = 0; i < reference.Segments.Count; i++)
                {
                    SegmentRecord r = reference.Segments[i];
                    MediaTime start = new(r.Info.StartTicks, reference.Track.Timescale);
                    MediaTime end = i + 1 < reference.Segments.Count ? new MediaTime(reference.Segments[i + 1].Info.StartTicks, reference.Track.Timescale) : MediaTime.Unknown;
                    // The first segment also takes cues that precede the media's first sample.
                    MediaTime from = i == 0 ? new MediaTime(long.MinValue / 4, 1) : start;
                    string text = Packager.WebVttSegment(doc, from, end);
                    File.WriteAllText(Path.Combine(_dir, dir, $"seg-{(i + 1).ToString("D5", CultureInfo.InvariantCulture)}.vtt"), text, new System.Text.UTF8Encoding(false));
                    x.Segments++;
                }
            }

            if (_options.Formats.HasFlag(PackagingFormats.Dash))
            {
                string whole = Packager.WebVttSegment(doc, new MediaTime(long.MinValue / 4, 1), MediaTime.Unknown);
                File.WriteAllText(Path.Combine(_dir, dir, "subs.vtt"), whole, new System.Text.UTF8Encoding(false));
            }

            _text.Add(x);
        }
    }

    public void Dispose()
    {
        foreach (TrackOut t in _tracks)
        {
            if (t.Spool is not null)
            {
                t.Spool.Dispose();
                t.Spool = null;
                if (t.SpoolPath is not null && File.Exists(t.SpoolPath))
                {
                    File.Delete(t.SpoolPath);
                }
            }
        }
    }
}
