using System.Buffers.Binary;
using System.Numerics;

namespace RecursiveAI.OneMedia.IsoBmff;

public sealed partial class IsoMuxer
{
    private const long MacEpochOffsetSeconds = 2_082_844_800; // 1904-01-01 to 1970-01-01

    // What the movie box states about one track, worked out once at the end (the movie box may be built several times to settle the
    // size-dependent chunk offsets).
    private sealed class TrackSummary
    {
        public List<(long Duration, long MediaTime)> Edits { get; } = [];

        public long MediaDuration { get; set; }

        public long MovieDuration { get; set; }
    }

    private long _movieTimescale;
    private TrackSummary[] _summaries = [];

    private byte[] BuildFtyp()
    {
        bool h264 = false;
        bool anyVideo = false;
        foreach (MuxTrack t in _tracks)
        {
            h264 |= t.Declared.Codec == CodecId.H264;
            anyVideo |= t.Declared.Kind == TrackKind.Video;
        }

        bool fragmented = _options.Layout == IsoLayout.Fragmented;
        string major;
        uint minor = 0;
        List<string> brands = [];
        switch (_options.Kind)
        {
            case IsoFileKind.Mov:
                major = "qt  ";
                brands.Add("qt  ");
                break;
            case IsoFileKind.M4a:
                major = "M4A ";
                brands.AddRange(["M4A ", "mp42", "isom"]);
                break;
            case IsoFileKind.M4v:
                major = "M4V ";
                brands.AddRange(["M4V ", "mp42", "isom"]);
                break;
            case IsoFileKind.ThreeGp:
                // 3GPP TS 26.244 §5.4.1: Release 5 added H.264; Release 4 is the baseline brand.
                major = h264 ? "3gp5" : "3gp4";
                brands.AddRange([major, "3gp4", "isom"]);
                break;
            case IsoFileKind.ThreeG2:
                major = "3g2a";
                brands.AddRange(["3g2a", "isom"]);
                break;
            default:
                major = "isom";
                minor = 512;
                brands.AddRange(["isom", "iso2"]);
                if (h264)
                {
                    brands.Add("avc1");
                }

                brands.Add("mp41");
                break;
        }

        if (fragmented)
        {
            // Fragments addressed from their movie fragment box (default-base-is-moof) are the iso5 and later brands.
            if (_options.Kind == IsoFileKind.Mp4)
            {
                major = "iso6";
                minor = 0;
            }

            brands.Insert(0, "iso6");
            if (_options.Cmaf)
            {
                brands.Add("cmfc");
            }
        }

        if (_tracks.Exists(t => t.Declared.Codec == CodecId.Opus) && _options.Kind != IsoFileKind.Mov && !brands.Contains("iso2"))
        {
            // Opus needs the roll-group support the iso2 brand stands for.
            brands.Add("iso2");
        }

        if (_options.MajorBrand is { } m)
        {
            major = m;
        }

        if (_options.MinorVersion is { } mv)
        {
            minor = mv;
        }

        List<string> list = _options.CompatibleBrands is { } given ? [.. given] : brands;
        if (!list.Contains(major))
        {
            list.Insert(0, major);
        }

        _ = anyVideo;
        BoxStack b = new();
        b.Begin("ftyp");
        b.Code(major);
        b.U32(minor);
        HashSet<string> seen = [];
        foreach (string brand in list)
        {
            if (seen.Add(brand))
            {
                b.Code(brand);
            }
        }

        b.End();
        return b.ToArray();
    }

    private void FinishProgressive(CancellationToken ct)
    {
        EnsureStarted();
        foreach (MuxTrack t in _tracks)
        {
            if (t.Samples > 0)
            {
                // The last sample's duration is the one it declared, else the gap before it, else nothing.
                t.CommitDuration(t.PreviousDeclared >= 0 ? t.PreviousDeclared : Math.Max(t.LastDelta, 0));
            }

            t.FlushDurationRun();
            t.FlushCompositionRun();
            t.CloseChunk();
            CompleteEntry(t);
        }

        Summarize(fragmented: false);
        long payload = _payloadPosition;
        if (_direct)
        {
            long moovAt = _out.Position;
            long baseOffset = _ftyp!.Length + 16;
            BoxStack moov = BuildMoov(baseOffset, fragmented: false);
            moov.WriteTo(_out, ct);
            // Fits 32 bits: the placeholder becomes `free` (8 bytes) + `mdat` (8 bytes); else the 64-bit form stays and gets its size.
            Span<byte> header = stackalloc byte[16]; // fixed small buffer
            if (payload + 8 <= uint.MaxValue)
            {
                BinaryPrimitives.WriteUInt32BigEndian(header, 8);
                BinaryPrimitives.WriteUInt32BigEndian(header[4..], BoxTypes.Free);
                BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)(payload + 8));
                BinaryPrimitives.WriteUInt32BigEndian(header[12..], BoxTypes.Mdat);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(header, 1);
                BinaryPrimitives.WriteUInt32BigEndian(header[4..], BoxTypes.Mdat);
                BinaryPrimitives.WriteUInt64BigEndian(header[8..], (ulong)payload + 16);
            }

            _ = moovAt;
            _out.Patch(_ftyp.Length, header);
            return;
        }

        int mdatHeader = payload + 8 <= uint.MaxValue ? 8 : 16;
        Span<byte> mdat = stackalloc byte[16]; // fixed small buffer
        if (mdatHeader == 8)
        {
            BinaryPrimitives.WriteUInt32BigEndian(mdat, (uint)(payload + 8));
            BinaryPrimitives.WriteUInt32BigEndian(mdat[4..], BoxTypes.Mdat);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(mdat, 1);
            BinaryPrimitives.WriteUInt32BigEndian(mdat[4..], BoxTypes.Mdat);
            BinaryPrimitives.WriteUInt64BigEndian(mdat[8..], (ulong)payload + 16);
        }

        if (_options.Layout == IsoLayout.MoovLast)
        {
            _out.Write(_ftyp!);
            _out.Write(mdat[..mdatHeader]);
            _spool!.CopyTo(_out, ct);
            BuildMoov(_ftyp!.Length + mdatHeader, fragmented: false).WriteTo(_out, ct);
            return;
        }

        // Movie box first: its size moves the chunk offsets, and the offsets can move its size (stco to co64), so build until it settles.
        // Tracks that need 64-bit offsets keep them, so the size only grows and the loop ends.
        long guess = 0;
        BoxStack built = BuildMoov(_ftyp!.Length + mdatHeader, fragmented: false);
        for (int pass = 0; pass < _tracks.Count + 2 && built.Length != guess; pass++)
        {
            guess = built.Length;
            built = BuildMoov(_ftyp.Length + guess + mdatHeader, fragmented: false);
        }

        if (built.Length != guess)
        {
            throw new InvalidOperationException("The movie box size did not settle.");
        }

        _out.Write(_ftyp);
        built.WriteTo(_out, ct);
        _out.Write(mdat[..mdatHeader]);
        _spool!.CopyTo(_out, ct);
    }

    private void CompleteEntry(MuxTrack t)
    {
        if (t.Plan.Entry is null)
        {
            IsoSampleEntries.Complete(t.Plan, t.Declared, t.Declared.Index, _options.Kind, t.Options, t.Head ?? []);
        }
    }

    private void ChooseMovieTimescale()
    {
        if (_options.MovieTimescale > 0)
        {
            _movieTimescale = Math.Min(_options.MovieTimescale, uint.MaxValue);
            return;
        }

        _movieTimescale = 1000;
        foreach (MuxTrack t in _tracks)
        {
            if (t.Declared.Edit.Edits is { Count: > 0 } edits && edits[0].Duration.Timescale is > 0 and <= uint.MaxValue)
            {
                _movieTimescale = edits[0].Duration.Timescale;
                return;
            }
        }

        // An edit list made from priming or end padding is only as exact as the movie timescale: take the timescale of the first track that
        // needs one, which for audio is its sample rate, so the padding comes back to the sample.
        foreach (MuxTrack t in _tracks)
        {
            TrackEditInfo e = t.Declared.Edit;
            if (!e.PacketTimesIncludePriming && ((e.Priming.IsKnown && !e.Priming.IsZero) || (e.EndPadding.IsKnown && !e.EndPadding.IsZero)))
            {
                _movieTimescale = Math.Min(t.Timescale, uint.MaxValue);
                return;
            }
        }
    }

    private long ToMovie(long trackTicks, MuxTrack t) => Rescale(trackTicks, t.Timescale, _movieTimescale);

    private static long Rescale(long ticks, long from, long to)
    {
        if (from == to)
        {
            return ticks;
        }

        BigInteger n = (BigInteger)ticks * to;
        BigInteger q = BigInteger.DivRem(n, from, out BigInteger r);
        if (!r.IsZero && BigInteger.Abs(r) * 2 >= from)
        {
            q += n.Sign;
        }

        return q > long.MaxValue ? long.MaxValue : q < long.MinValue ? long.MinValue : (long)q;
    }

    // Works out each track's edit list and durations. An edit list the track brought is written as given (its media times moved by the
    // start shift); otherwise one is made from the priming, the end padding and the start shift, so the presentation times a reader
    // computes from the output equal the ones the packets had.
    private void Summarize(bool fragmented)
    {
        ChooseMovieTimescale();
        _summaries = new TrackSummary[_tracks.Count];
        for (int i = 0; i < _tracks.Count; i++)
        {
            MuxTrack t = _tracks[i];
            TrackSummary s = new() { MediaDuration = t.DurationSum };
            _summaries[i] = s;
            TrackEditInfo edit = t.Declared.Edit;
            long shift = t.Started ? t.Shift : 0;
            if (edit.Edits is { Count: > 0 } edits)
            {
                for (int e = 0; e < edits.Count; e++)
                {
                    EditSegment seg = edits[e];
                    long duration = seg.Duration.TryRescale(_movieTimescale, MediaRounding.NearestAwayFromZero, out MediaTime d) ? d.Ticks : 0;
                    long mediaTime = -1;
                    if (!seg.IsEmpty)
                    {
                        mediaTime = (seg.MediaStart.TryRescale(t.Timescale, MediaRounding.NearestAwayFromZero, out MediaTime m) ? m.Ticks : 0) - shift;
                        if (duration == 0 && e == edits.Count - 1 && !fragmented)
                        {
                            // A last segment of length zero means "to the end" in a fragmented source; a complete file states it.
                            duration = ToMovie(Math.Max(0, t.DurationSum - mediaTime), t);
                        }
                    }

                    s.Edits.Add((duration, mediaTime));
                }
            }
            else
            {
                long priming = edit.Priming.IsKnown && !edit.Priming.IsZero && edit.Priming.TryRescale(t.Timescale, MediaRounding.NearestAwayFromZero, out MediaTime p) ? p.Ticks : 0;
                long padding = edit.EndPadding.IsKnown && !edit.EndPadding.IsZero && edit.EndPadding.TryRescale(t.Timescale, MediaRounding.NearestAwayFromZero, out MediaTime q) ? q.Ticks : 0;
                if (edit.PacketTimesIncludePriming)
                {
                    priming = 0;
                }

                long mediaStart = Math.Max(0, priming - shift);
                long lead = Math.Max(0, shift - priming);
                if (mediaStart != 0 || lead != 0 || padding != 0)
                {
                    if (lead > 0)
                    {
                        s.Edits.Add((ToMovie(lead, t), -1));
                    }

                    // Zero means "to the end" while the length is not known (fragmented output).
                    long length = fragmented ? 0 : ToMovie(Math.Max(0, t.DurationSum - mediaStart - padding), t);
                    s.Edits.Add((length, mediaStart));
                }
            }

            long sum = 0;
            foreach ((long duration, _) in s.Edits)
            {
                sum += duration;
            }

            s.MovieDuration = fragmented ? 0 : s.Edits.Count > 0 ? sum : ToMovie(t.DurationSum, t);
        }
    }

    private static ulong MacSeconds(DateTimeOffset? time) => time is { } t ? (ulong)Math.Max(0, t.ToUnixTimeSeconds() + MacEpochOffsetSeconds) : 0;

    private static bool IsLowerLetter(char c) => c is >= 'a' and <= 'z';

    private static ushort PackLanguage(string? language, out bool valid)
    {
        valid = true;
        if (language is { Length: 3 } && IsLowerLetter(language[0]) && IsLowerLetter(language[1]) && IsLowerLetter(language[2]))
        {
            return (ushort)(((language[0] - 0x60) << 10) | ((language[1] - 0x60) << 5) | (language[2] - 0x60));
        }

        valid = language is null or "und" or "";
        return 0x55C4; // "und"
    }

    /// <summary>Builds the movie box for the current state; <paramref name="baseOffset"/> is the file offset of the first payload byte.</summary>
    private BoxStack BuildMoov(long baseOffset, bool fragmented)
    {
        BoxStack b = new(4096);
        ulong created = MacSeconds(_options.CreationTime);
        long movieDuration = 0;
        foreach (TrackSummary s in _summaries)
        {
            movieDuration = Math.Max(movieDuration, s.MovieDuration);
        }

        uint nextTrack = 1;
        foreach (MuxTrack t in _tracks)
        {
            nextTrack = Math.Max(nextTrack, t.TrackId + 1);
        }

        b.Begin("moov");
        bool v1 = movieDuration > uint.MaxValue || created > uint.MaxValue;
        b.BeginFull("mvhd", v1 ? 1 : 0, 0);
        if (v1)
        {
            b.U64(created);
            b.U64(created);
            b.U32((uint)_movieTimescale);
            b.U64((ulong)movieDuration);
        }
        else
        {
            b.U32((uint)created);
            b.U32((uint)created);
            b.U32((uint)_movieTimescale);
            b.U32((uint)movieDuration);
        }

        b.U32(0x00010000); // rate 1.0
        b.U16(0x0100); // volume 1.0
        b.Zeros(10);
        WriteMatrix(b, 0);
        b.Zeros(24); // pre_defined
        b.U32(nextTrack);
        b.End();

        for (int i = 0; i < _tracks.Count; i++)
        {
            WriteTrack(b, _tracks[i], _summaries[i], baseOffset, fragmented, created);
        }

        if (fragmented)
        {
            b.Begin("mvex");
            foreach (MuxTrack t in _tracks)
            {
                b.BeginFull("trex", 0, 0);
                b.U32(t.TrackId);
                b.U32(1); // default_sample_description_index
                b.U32(0);
                b.U32(0);
                b.U32(0);
                b.End();
            }

            b.End();
        }

        b.End();
        return b;
    }

    // ISO/IEC 14496-12 §6.2.2: the 3x3 matrix is stored as a b u c d v x y w, a/b/c/d/x/y in 16.16 and u/v/w in 2.30.
    private static void WriteMatrix(BoxStack b, int rotation)
    {
        const int one = 0x10000;
        (int a, int bb, int c, int d) = rotation switch
        {
            90 => (0, one, -one, 0),
            180 => (-one, 0, 0, -one),
            270 => (0, -one, one, 0),
            _ => (one, 0, 0, one),
        };
        b.I32(a);
        b.I32(bb);
        b.U32(0);
        b.I32(c);
        b.I32(d);
        b.U32(0);
        b.U32(0);
        b.U32(0);
        b.U32(0x40000000);
    }

    private void WriteTrack(BoxStack b, MuxTrack t, TrackSummary s, long baseOffset, bool fragmented, ulong created)
    {
        MediaTrack d = t.Declared;
        bool qt = _options.Kind == IsoFileKind.Mov;
        b.Begin("trak");

        // tkhd (§8.3.2): flags 1 enabled, 2 in movie, 4 in preview.
        bool v1 = s.MovieDuration > uint.MaxValue || created > uint.MaxValue;
        b.BeginFull("tkhd", v1 ? 1 : 0, (d.Disposition & TrackDisposition.Disabled) != 0 ? 6u : 7u);
        if (v1)
        {
            b.U64(created);
            b.U64(created);
            b.U32(t.TrackId);
            b.U32(0);
            b.U64((ulong)s.MovieDuration);
        }
        else
        {
            b.U32((uint)created);
            b.U32((uint)created);
            b.U32(t.TrackId);
            b.U32(0);
            b.U32((uint)s.MovieDuration);
        }

        b.Zeros(8);
        b.U16(0); // layer
        b.U16(0); // alternate_group
        b.U16(d.Kind == TrackKind.Audio ? 0x0100 : 0);
        b.U16(0);
        WriteMatrix(b, d.Kind == TrackKind.Video ? d.Video?.RotationDegrees ?? 0 : 0);
        if (d.Kind == TrackKind.Video && d.Video is { } v)
        {
            // The track header states the size to display: the coded width stretched by the pixel aspect ratio.
            long width = v.PixelAspectNumerator > 0 && v.PixelAspectDenominator > 0 ? (long)v.Width * v.PixelAspectNumerator / v.PixelAspectDenominator : v.Width;
            b.U32((uint)width << 16);
            b.U32((uint)v.Height << 16);
        }
        else
        {
            b.U32(0);
            b.U32(0);
        }

        b.End();

        if (s.Edits.Count > 0)
        {
            WriteEdits(b, s);
        }

        b.Begin("mdia");
        ushort language = PackLanguage(d.Language, out bool languageValid);
        if (!languageValid && !_notes.Exists(n => n.StartsWith($"Track {d.Index}: the language", StringComparison.Ordinal)))
        {
            _notes.Add($"Track {d.Index}: the language '{d.Language}' is not a three-letter code and was written as 'und'.");
        }

        bool mv1 = s.MediaDuration > uint.MaxValue || created > uint.MaxValue;
        b.BeginFull("mdhd", mv1 ? 1 : 0, 0);
        if (mv1)
        {
            b.U64(created);
            b.U64(created);
            b.U32((uint)t.Timescale);
            b.U64((ulong)s.MediaDuration);
        }
        else
        {
            b.U32((uint)created);
            b.U32((uint)created);
            b.U32((uint)t.Timescale);
            b.U32((uint)s.MediaDuration);
        }

        b.U16(language);
        b.U16(0);
        b.End();

        b.BeginFull("hdlr", 0, 0);
        if (qt)
        {
            b.Code("mhlr");
            b.Code(t.Plan.Handler);
            b.Zeros(12); // manufacturer, flags, flags mask
        }
        else
        {
            b.U32(0);
            b.Code(t.Plan.Handler);
            b.Zeros(12);
        }

        string name = d.Name ?? string.Empty;
        if (qt)
        {
            // QuickTime stores the name as a length-prefixed string.
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
            int length = Math.Min(bytes.Length, 255);
            b.U8(length);
            b.Bytes(bytes.AsSpan(0, length));
        }
        else
        {
            b.Utf8(name);
            b.U8(0);
        }

        b.End();

        b.Begin("minf");
        switch (t.Plan.Header)
        {
            case MediaHeaderKind.Video:
                b.BeginFull("vmhd", 0, 1);
                b.Zeros(8);
                b.End();
                break;
            case MediaHeaderKind.Sound:
                b.BeginFull("smhd", 0, 0);
                b.Zeros(4);
                b.End();
                break;
            case MediaHeaderKind.Subtitle:
                b.BeginFull("sthd", 0, 0);
                b.End();
                break;
            case MediaHeaderKind.QuickTimeGeneric:
                // QuickTime timecode media: a generic header holding a media-information atom and the text-style atom of the timecode.
                b.Begin("gmhd");
                b.BeginFull("gmin", 0, 0);
                b.U16(0x40);
                b.U16(0x8000);
                b.U16(0x8000);
                b.U16(0x8000);
                b.U16(0);
                b.U16(0);
                b.End();
                b.Begin("tmcd");
                b.BeginFull("tcmi", 0, 0);
                b.U16(0);
                b.U16(0);
                b.U16(12);
                b.U16(0);
                b.Zeros(6);
                b.U16(0xFFFF);
                b.U16(0xFFFF);
                b.U16(0xFFFF);
                b.U8(0);
                b.End();
                b.End();
                b.End();
                break;
            default:
                b.BeginFull("nmhd", 0, 0);
                b.End();
                break;
        }

        if (qt)
        {
            b.BeginFull("hdlr", 0, 0);
            b.Code("dhlr");
            b.Code("alis");
            b.Zeros(12);
            b.U8(0);
            b.End();
        }

        b.Begin("dinf");
        b.BeginFull("dref", 0, 0);
        b.U32(1);
        b.BeginFull("url ", 0, 1); // flag 1: the media data is in this file
        b.End();
        b.End();
        b.End();

        WriteSampleTable(b, t, baseOffset, fragmented);
        b.End(); // minf
        b.End(); // mdia

        if (t.Options?.TrackBoxes is { } extra)
        {
            foreach (ReadOnlyMemory<byte> box in extra)
            {
                if (!BoxBuilder.IsSingleBox(box.Span, out _))
                {
                    throw new MuxRefusedException(MuxRefusalReason.InvalidTrack, $"Track {d.Index}: a track box given in the track options is not one complete box.", d.Index, d.Codec);
                }

                b.Bytes(box.Span);
            }
        }

        b.End(); // trak
    }

    // §8.6.5 and §8.6.6. Version 1 when a duration or media time needs 64 bits.
    private static void WriteEdits(BoxStack b, TrackSummary s)
    {
        bool wide = false;
        foreach ((long duration, long mediaTime) in s.Edits)
        {
            wide |= duration > uint.MaxValue || mediaTime > int.MaxValue;
        }

        b.Begin("edts");
        b.BeginFull("elst", wide ? 1 : 0, 0);
        b.U32((uint)s.Edits.Count);
        foreach ((long duration, long mediaTime) in s.Edits)
        {
            if (wide)
            {
                b.U64((ulong)duration);
                b.U64(unchecked((ulong)mediaTime));
            }
            else
            {
                b.U32((uint)duration);
                b.I32((int)mediaTime);
            }

            b.U16(1); // media_rate_integer
            b.U16(0);
        }

        b.End();
        b.End();
    }

    private void WriteSampleTable(BoxStack b, MuxTrack t, long baseOffset, bool fragmented)
    {
        b.Begin("stbl");
        b.BeginFull("stsd", 0, 0);
        b.U32(1);
        b.Bytes(t.Plan.Entry!);
        b.End();

        if (fragmented)
        {
            // A fragmented file's movie box describes no samples; the tables are present and empty.
            foreach (string empty in new[] { "stts", "stsc" })
            {
                b.BeginFull(empty, 0, 0);
                b.U32(0);
                b.End();
            }

            b.BeginFull("stsz", 0, 0);
            b.U32(0);
            b.U32(0);
            b.End();
            b.BeginFull("stco", 0, 0);
            b.U32(0);
            b.End();
            WriteRollDescription(b, t);
            b.End();
            return;
        }

        b.BeginFull("stts", 0, 0);
        b.U32((uint)(t.Durations.Count / 2));
        b.Defer(new DeferredTable(TableKind.Pairs, t.Durations, t.Durations.Count / 2));
        b.End();

        if (t.AnyComposition)
        {
            // §8.6.1.3: version 1 lets the offsets be negative; version 0 keeps them unsigned for readers that treat them so.
            b.BeginFull("ctts", t.AnyNegativeComposition ? 1 : 0, 0);
            b.U32((uint)(t.Compositions.Count / 2));
            b.Defer(new DeferredTable(TableKind.Pairs, t.Compositions, t.Compositions.Count / 2));
            b.End();
        }

        if (t.KeyCount < t.Samples)
        {
            b.BeginFull("stss", 0, 0);
            b.U32((uint)t.SyncSamples.Count);
            b.Defer(new DeferredTable(TableKind.Words, t.SyncSamples, t.SyncSamples.Count));
            b.End();
        }

        if (t.Dependencies is { } dependencies)
        {
            b.BeginFull("sdtp", 0, 0);
            b.Defer(new DeferredTable(dependencies, dependencies.Count));
            b.End();
        }

        b.BeginFull("stsc", 0, 0);
        b.U32((uint)(t.SampleToChunk.Count / 3));
        b.Defer(new DeferredTable(TableKind.Triples, t.SampleToChunk, t.SampleToChunk.Count / 3));
        b.End();

        bool constant = t.Samples > 0 && t.MinSize == t.MaxSize;
        bool compact = _options.CompactSampleSizes && _options.Kind != IsoFileKind.Mov && !constant && t.Samples > 0 && t.MaxSize <= ushort.MaxValue;
        if (compact)
        {
            bool eight = t.MaxSize <= byte.MaxValue;
            b.BeginFull("stz2", 0, 0);
            b.Zeros(3);
            b.U8(eight ? 8 : 16);
            b.U32((uint)t.Samples);
            b.Defer(new DeferredTable(eight ? TableKind.Narrow8 : TableKind.Narrow16, t.Sizes, t.Samples));
            b.End();
        }
        else
        {
            b.BeginFull("stsz", 0, 0);
            b.U32(constant ? t.MinSize : 0);
            b.U32((uint)t.Samples);
            if (!constant)
            {
                b.Defer(new DeferredTable(TableKind.Words, t.Sizes, t.Samples));
            }

            b.End();
        }

        // Chunk offsets are 32-bit unless the last one (the largest) does not fit; then this track gets 64-bit ones for good.
        long last = t.ChunkOffsets.Count > 0 ? (long)t.ChunkOffsets[t.ChunkOffsets.Count - 1] : 0;
        if (last + baseOffset > uint.MaxValue)
        {
            t.ForceLargeOffsets = true;
        }

        bool large = t.ForceLargeOffsets;
        b.BeginFull(large ? "co64" : "stco", 0, 0);
        b.U32((uint)t.ChunkOffsets.Count);
        b.Defer(new DeferredTable(large ? TableKind.Offsets64 : TableKind.Offsets32, t.ChunkOffsets, t.ChunkOffsets.Count, baseOffset));
        b.End();
        if (t.Declared.Codec == CodecId.Opus && t.Samples > 0)
        {
            WriteRollDescription(b, t);
            b.BeginFull("sbgp", 0, 0);
            b.Code("roll");
            b.U32(1);
            b.U32((uint)t.Samples);
            b.U32(1);
            b.End();
        }

        b.End(); // stbl
    }

    // Opus in ISO base media (Xiph's encapsulation document, section on random access): every sample is a sync sample but needs 80 ms of
    // pre-roll, stated as an audio roll-recovery group (ISO/IEC 14496-12 §10.1) whose distance counts samples.
    private static void WriteRollDescription(BoxStack b, MuxTrack t)
    {
        if (t.Declared.Codec != CodecId.Opus)
        {
            return;
        }

        long duration = t.Durations.Count >= 2 ? t.Durations[1] : t.Fragment.Length > 0 && t.Fragment[0].DeclaredDuration > 0 ? t.Fragment[0].DeclaredDuration : t.Timescale / 50;
        long samples = Math.Max(1, ((t.Timescale * 4 / 50) + duration - 1) / Math.Max(duration, 1)); // 80 ms in samples of this duration, rounded up
        b.BeginFull("sgpd", 1, 0);
        b.Code("roll");
        b.U32(2); // default_length: one 16-bit roll_distance
        b.U32(1);
        b.U16((int)-Math.Min(samples, short.MaxValue));
        b.End();
    }
}
