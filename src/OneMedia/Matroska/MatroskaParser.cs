using System.Text;

namespace RecursiveAI.OneMedia.Matroska;

/// <summary>How a file's TimestampScale maps onto <see cref="MediaTime"/> ticks.</summary>
/// <param name="Timescale">Ticks per second used for every time in the file.</param>
/// <param name="TicksPerUnit">Ticks per timestamp unit: 1 when the scale divides 10^9 evenly, else the scale itself.</param>
/// <param name="UnitNs">Nanoseconds per timestamp unit (the TimestampScale).</param>
internal readonly record struct MatroskaClock(long Timescale, long TicksPerUnit, long UnitNs)
{
    public const long NanosecondsPerSecond = 1_000_000_000;

    /// <summary>
    /// A TimestampScale that divides a second evenly (the usual 1,000,000 gives milliseconds) keeps one tick per timestamp unit and a
    /// timescale of 10^9 / scale. Any other scale falls back to nanosecond ticks, which is exact for every scale.
    /// </summary>
    public static MatroskaClock FromTimestampScale(long scale)
    {
        if (scale <= 0 || scale > NanosecondsPerSecond)
        {
            scale = 1_000_000;
        }

        return NanosecondsPerSecond % scale == 0
            ? new MatroskaClock(NanosecondsPerSecond / scale, 1, scale)
            : new MatroskaClock(NanosecondsPerSecond, scale, scale);
    }

    /// <summary>Nanoseconds as ticks, to the nearest tick with halves away from zero (RFC 9559 §11.3 asks for nearest rounding of timestamps).</summary>
    public long NanosecondsToNearestTicks(long nanoseconds) => Timescale == NanosecondsPerSecond ? nanoseconds : (nanoseconds + (UnitNs / 2)) / UnitNs;

    /// <summary>Converts timestamp units to ticks, saturating instead of overflowing.</summary>
    public long ToTicks(long units)
    {
        if (TicksPerUnit == 1)
        {
            return units;
        }

        long high = Math.BigMul(units, TicksPerUnit, out long low);
        return (high == 0 && low >= 0) || (high == -1 && low < 0) ? low : (units < 0 ? long.MinValue + 1 : long.MaxValue);
    }

    /// <summary>Nanoseconds as ticks, rounded down: a DefaultDuration that is not a whole number of ticks loses its fraction, as in files' own timestamps.</summary>
    public long NanosecondsToTicks(long nanoseconds) => Timescale == NanosecondsPerSecond ? nanoseconds : nanoseconds / UnitNs;

    public MediaTime Time(long units) => new(ToTicks(units), Timescale);
}

/// <summary>Reads the children of a master element held in memory.</summary>
internal ref struct EbmlChildren
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly long _baseOffset;
    private readonly DefectLog _defects;
    private int _pos;

    public EbmlChildren(ReadOnlySpan<byte> data, long baseOffset, DefectLog defects)
    {
        _data = data;
        _baseOffset = baseOffset;
        _defects = defects;
        _pos = 0;
        Id = 0;
        Payload = default;
        Offset = 0;
    }

    public uint Id { get; private set; }

    public ReadOnlySpan<byte> Payload { get; private set; }

    /// <summary>Absolute file offset of the current child's header.</summary>
    public long Offset { get; private set; }

    /// <summary>Absolute file offset of the current child's payload.</summary>
    public long PayloadOffset { get; private set; }

    public bool MoveNext()
    {
        if (_pos >= _data.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> rest = _data[_pos..];
        long here = _baseOffset + _pos;
        if (!Ebml.TryReadId(rest, out uint id, out int idLen) || !Ebml.TryReadSize(rest[idLen..], out long size, out int sizeLen))
        {
            _defects.Add(DefectKind.InvalidSize, DefectSeverity.Error, here, "An element header inside a master element is not a valid EBML ID and size; the rest of the parent is skipped.");
            _pos = _data.Length;
            return false;
        }

        int header = idLen + sizeLen;
        long available = rest.Length - header;
        if (size == Ebml.UnknownSize || size > available)
        {
            _defects.Add(
                size == Ebml.UnknownSize ? DefectKind.InvalidSize : DefectKind.SizeExceedsParent,
                DefectSeverity.Error,
                here,
                size == Ebml.UnknownSize ? "A child of a fixed-size master element has an unknown size; it is cut at the end of its parent." : $"A child element claims {size} bytes but only {available} remain in its parent; it is cut at the end of the parent.");
            size = available;
        }

        Id = id;
        Offset = here;
        PayloadOffset = here + header;
        Payload = rest.Slice(header, (int)size);
        _pos += header + (int)size;
        return true;
    }
}

/// <summary>Parsers for the Matroska elements the reader needs in memory. They are tolerant: damage becomes a defect, not an exception.</summary>
internal static class MatroskaParser
{
    public static readonly Encoding Utf8 = new UTF8Encoding(false, false);

    /// <summary>
    /// RFC 8794 §11.3.1: when a master element's first child is a CRC-32 element, it holds the CRC-32 of everything after it in the
    /// master. A mismatch is a defect; the data is still used.
    /// </summary>
    public static void VerifyCrc(ReadOnlySpan<byte> payload, long payloadOffset, DefectLog defects, string name)
    {
        if (payload.Length < 6 || payload[0] != MatroskaIds.Crc32 || payload[1] != 0x84)
        {
            return;
        }

        uint stored = (uint)(payload[2] | (payload[3] << 8) | (payload[4] << 16) | (payload[5] << 24));
        uint actual = EbmlCrc32.Compute(payload[6..]);
        if (stored != actual)
        {
            defects.Add(DefectKind.ChecksumMismatch, DefectSeverity.Warning, payloadOffset, $"The CRC-32 of the {name} element is {stored:X8} but its content hashes to {actual:X8}.");
        }
    }

    public static string ReadString(ReadOnlySpan<byte> payload)
    {
        int nul = payload.IndexOf((byte)0);
        if (nul >= 0)
        {
            payload = payload[..nul];
        }

        return Utf8.GetString(payload);
    }

    private static long Unsigned(ReadOnlySpan<byte> payload, long fallback, DefectLog defects, long offset)
    {
        if (Ebml.TryReadUnsigned(payload, out ulong v) && v <= long.MaxValue)
        {
            return (long)v;
        }

        defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, "An unsigned integer element is longer than 8 bytes or out of range; its default is used.");
        return fallback;
    }

    // UIDs are random 64-bit numbers (RFC 9559 §5.1.4.1.2): values above 2^63 are normal and are kept bit for bit.
    private static long UidBits(ReadOnlySpan<byte> payload) => Ebml.TryReadUnsigned(payload, out ulong v) ? unchecked((long)v) : 0;

    private static long Signed(ReadOnlySpan<byte> payload, long fallback, DefectLog defects, long offset)
    {
        if (Ebml.TryReadSigned(payload, out long v))
        {
            return v;
        }

        defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, "A signed integer element is longer than 8 bytes; its default is used.");
        return fallback;
    }

    private static long Float(ReadOnlySpan<byte> payload, long fallback, DefectLog defects, long offset)
    {
        if (Ebml.TryReadFloatRounded(payload, out long v))
        {
            return v;
        }

        defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, "A float element has an unusable length or value (NaN, infinity, out of range); its default is used.");
        return fallback;
    }

    public static MatroskaHeader ParseHeader(ReadOnlySpan<byte> payload, long payloadOffset, DefectLog defects)
    {
        long version = 1, readVersion = 1, maxId = 4, maxSize = 8, docVersion = 1, docRead = 1;
        string docType = string.Empty;
        EbmlChildren c = new(payload, payloadOffset, defects);
        while (c.MoveNext())
        {
            switch (c.Id)
            {
                case MatroskaIds.EbmlVersion: version = Unsigned(c.Payload, 1, defects, c.Offset); break;
                case MatroskaIds.EbmlReadVersion: readVersion = Unsigned(c.Payload, 1, defects, c.Offset); break;
                case MatroskaIds.EbmlMaxIdLength: maxId = Unsigned(c.Payload, 4, defects, c.Offset); break;
                case MatroskaIds.EbmlMaxSizeLength: maxSize = Unsigned(c.Payload, 8, defects, c.Offset); break;
                case MatroskaIds.DocType: docType = ReadString(c.Payload); break;
                case MatroskaIds.DocTypeVersion: docVersion = Unsigned(c.Payload, 1, defects, c.Offset); break;
                case MatroskaIds.DocTypeReadVersion: docRead = Unsigned(c.Payload, 1, defects, c.Offset); break;
                default: break;
            }
        }

        return new MatroskaHeader
        {
            EbmlVersion = version,
            EbmlReadVersion = readVersion,
            MaxIdLength = maxId,
            MaxSizeLength = maxSize,
            DocType = docType,
            DocTypeVersion = docVersion,
            DocTypeReadVersion = docRead,
        };
    }

    public static MatroskaInfo ParseInfo(ReadOnlySpan<byte> payload, long payloadOffset, DefectLog defects)
    {
        long scale = 1_000_000;
        long durationUnits = -1;
        bool hasDuration = false;
        DateTimeOffset? date = null;
        string? title = null, muxing = null, writing = null;
        byte[] uuid = [];
        EbmlChildren c = new(payload, payloadOffset, defects);
        while (c.MoveNext())
        {
            switch (c.Id)
            {
                case MatroskaIds.TimestampScale:
                    scale = Unsigned(c.Payload, 1_000_000, defects, c.Offset);
                    if (scale == 0)
                    {
                        defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, c.Offset, "TimestampScale is 0; the default of 1,000,000 is used.");
                        scale = 1_000_000;
                    }

                    break;
                case MatroskaIds.Duration:
                    if (Ebml.TryReadFloatRounded(c.Payload, out durationUnits) && durationUnits >= 0)
                    {
                        hasDuration = true;
                    }
                    else
                    {
                        defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, c.Offset, "The Segment Duration is negative, NaN or out of range and is ignored.");
                    }

                    break;
                case MatroskaIds.DateUtc:
                    if (Ebml.TryReadSigned(c.Payload, out long ns))
                    {
                        // RFC 9559 §5.1.2.9: nanoseconds since 2001-01-01T00:00:00 UTC.
                        try
                        {
                            date = new DateTimeOffset(new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).AddTicks(ns / 100);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            defects.Add(DefectKind.InvalidValue, DefectSeverity.Info, c.Offset, "DateUTC is outside the range of dates this library represents.");
                        }
                    }

                    break;
                case MatroskaIds.Title: title = ReadString(c.Payload); break;
                case MatroskaIds.MuxingApp: muxing = ReadString(c.Payload); break;
                case MatroskaIds.WritingApp: writing = ReadString(c.Payload); break;
                case MatroskaIds.SegmentUuid: uuid = c.Payload.ToArray(); break;
                default: break;
            }
        }

        MatroskaClock clock = MatroskaClock.FromTimestampScale(scale);
        return new MatroskaInfo
        {
            TimestampScale = scale,
            Duration = hasDuration ? clock.Time(durationUnits) : MediaTime.Unknown,
            DateUtc = date,
            Title = title,
            MuxingApp = muxing,
            WritingApp = writing,
            SegmentUuid = uuid,
        };
    }

    public static List<MatroskaTrackEntry> ParseTracks(ReadOnlySpan<byte> payload, long payloadOffset, DefectLog defects, MediaLimits limits)
    {
        List<MatroskaTrackEntry> entries = [];
        EbmlChildren c = new(payload, payloadOffset, defects);
        while (c.MoveNext())
        {
            if (c.Id != MatroskaIds.TrackEntry)
            {
                continue;
            }

            limits.CheckTracks(entries.Count + 1, c.Offset);
            entries.Add(ParseTrackEntry(c.Payload, c.Offset, c.PayloadOffset, defects, limits));
        }

        return entries;
    }

    private static MatroskaTrackEntry ParseTrackEntry(ReadOnlySpan<byte> payload, long offset, long payloadOffset, DefectLog defects, MediaLimits limits)
    {
        long number = 0, uid = 0, type = 0, defaultDuration = 0, delay = 0, preRoll = 0;
        bool enabled = true, isDefault = true, forced = false, hearing = false, visual = false, original = false, commentary = false, lacing = true;
        string codecId = string.Empty, language = "eng";
        string? codecName = null, name = null, bcp47 = null;
        byte[] codecPrivate = [];
        MatroskaVideo? video = null;
        MatroskaAudio? audio = null;
        List<MatroskaContentEncoding> encodings = [];

        EbmlChildren c = new(payload, payloadOffset, defects);
        while (c.MoveNext())
        {
            switch (c.Id)
            {
                case MatroskaIds.TrackNumber: number = Unsigned(c.Payload, 0, defects, c.Offset); break;
                case MatroskaIds.TrackUid: uid = UidBits(c.Payload); break;
                case MatroskaIds.TrackType: type = Unsigned(c.Payload, 0, defects, c.Offset); break;
                case MatroskaIds.FlagEnabled: enabled = Unsigned(c.Payload, 1, defects, c.Offset) != 0; break;
                case MatroskaIds.FlagDefault: isDefault = Unsigned(c.Payload, 1, defects, c.Offset) != 0; break;
                case MatroskaIds.FlagForced: forced = Unsigned(c.Payload, 0, defects, c.Offset) != 0; break;
                case MatroskaIds.FlagHearingImpaired: hearing = Unsigned(c.Payload, 0, defects, c.Offset) != 0; break;
                case MatroskaIds.FlagVisualImpaired: visual = Unsigned(c.Payload, 0, defects, c.Offset) != 0; break;
                case MatroskaIds.FlagOriginal: original = Unsigned(c.Payload, 0, defects, c.Offset) != 0; break;
                case MatroskaIds.FlagCommentary: commentary = Unsigned(c.Payload, 0, defects, c.Offset) != 0; break;
                case MatroskaIds.FlagLacing: lacing = Unsigned(c.Payload, 1, defects, c.Offset) != 0; break;
                case MatroskaIds.DefaultDuration: defaultDuration = Unsigned(c.Payload, 0, defects, c.Offset); break;
                case MatroskaIds.CodecIdElement: codecId = ReadString(c.Payload); break;
                case MatroskaIds.CodecPrivate:
                    limits.CheckAllocation(c.Payload.Length, c.Offset);
                    codecPrivate = c.Payload.ToArray();
                    break;
                case MatroskaIds.CodecName: codecName = ReadString(c.Payload); break;
                case MatroskaIds.Name: name = ReadString(c.Payload); break;
                case MatroskaIds.Language: language = ReadString(c.Payload); break;
                case MatroskaIds.LanguageBcp47: bcp47 = ReadString(c.Payload); break;
                case MatroskaIds.CodecDelay: delay = Unsigned(c.Payload, 0, defects, c.Offset); break;
                case MatroskaIds.SeekPreRoll: preRoll = Unsigned(c.Payload, 0, defects, c.Offset); break;
                case MatroskaIds.Video: video = ParseVideo(c.Payload, c.PayloadOffset, defects); break;
                case MatroskaIds.Audio: audio = ParseAudio(c.Payload, c.PayloadOffset, defects); break;
                case MatroskaIds.ContentEncodings: ParseContentEncodings(c.Payload, c.PayloadOffset, defects, limits, encodings); break;
                default: break;
            }
        }

        if (number == 0)
        {
            defects.Add(DefectKind.MissingRequired, DefectSeverity.Error, offset, "A TrackEntry has no TrackNumber (or 0), so no Block can refer to it.");
        }

        // RFC 9559 §5.1.4.1.31: only an encoding whose scope includes frame contents changes the frames. One header-stripping
        // encoding is restored; any other frame compression is reported and the frames are delivered as stored.
        byte[] prefix = [];
        bool zlib = false;
        bool unsupported = false;
        bool encrypted = false;
        int frameEncodings = 0;
        foreach (MatroskaContentEncoding e in encodings)
        {
            if (e.Type == 1)
            {
                encrypted = true;
            }

            if ((e.Scope & 1) == 0)
            {
                continue;
            }

            frameEncodings++;
            if (e.Type == 0 && e.CompressionAlgorithm == 3 && frameEncodings == 1)
            {
                prefix = e.CompressionSettings.ToArray();
            }
            else if (e.Type == 0 && e.CompressionAlgorithm == 0 && frameEncodings == 1)
            {
                zlib = true;
            }
            else if (e.Type == 0)
            {
                unsupported = true;
            }
        }

        if (frameEncodings > 1)
        {
            unsupported = true;
            prefix = [];
            zlib = false;
        }

        if (unsupported)
        {
            defects.Add(DefectKind.Unsupported, DefectSeverity.Warning, offset, $"Track {number} uses a frame compression this reader does not undo (bzlib, lzo1x or stacked encodings); its frames are delivered as stored.");
        }

        return new MatroskaTrackEntry
        {
            Offset = offset,
            TrackNumber = number,
            TrackUid = uid,
            TrackType = type,
            Enabled = enabled,
            Default = isDefault,
            Forced = forced,
            HearingImpaired = hearing,
            VisualImpaired = visual,
            Original = original,
            Commentary = commentary,
            Lacing = lacing,
            DefaultDurationNs = defaultDuration,
            CodecId = codecId,
            CodecPrivate = codecPrivate,
            CodecName = codecName,
            Name = name,
            Language = language,
            LanguageBcp47 = bcp47,
            CodecDelayNs = delay,
            SeekPreRollNs = preRoll,
            Video = video,
            Audio = audio,
            ContentEncodings = encodings,
            HeaderStripPrefix = prefix,
            UsesZlib = zlib,
            HasUnsupportedFrameCompression = unsupported,
            IsEncrypted = encrypted,
        };
    }

    private static MatroskaVideo ParseVideo(ReadOnlySpan<byte> payload, long payloadOffset, DefectLog defects)
    {
        long w = 0, h = 0, dw = 0, dh = 0, unit = 0, interlaced = 0, stereo = 0;
        EbmlChildren c = new(payload, payloadOffset, defects);
        while (c.MoveNext())
        {
            switch (c.Id)
            {
                case MatroskaIds.PixelWidth: w = Unsigned(c.Payload, 0, defects, c.Offset); break;
                case MatroskaIds.PixelHeight: h = Unsigned(c.Payload, 0, defects, c.Offset); break;
                case MatroskaIds.DisplayWidth: dw = Unsigned(c.Payload, 0, defects, c.Offset); break;
                case MatroskaIds.DisplayHeight: dh = Unsigned(c.Payload, 0, defects, c.Offset); break;
                case MatroskaIds.DisplayUnit: unit = Unsigned(c.Payload, 0, defects, c.Offset); break;
                case MatroskaIds.FlagInterlaced: interlaced = Unsigned(c.Payload, 0, defects, c.Offset); break;
                case MatroskaIds.StereoMode: stereo = Unsigned(c.Payload, 0, defects, c.Offset); break;
                default: break;
            }
        }

        return new MatroskaVideo(w, h, dw, dh, unit, interlaced, stereo);
    }

    private static MatroskaAudio ParseAudio(ReadOnlySpan<byte> payload, long payloadOffset, DefectLog defects)
    {
        long rate = 8000, outRate = -1, channels = 1, depth = 0;
        EbmlChildren c = new(payload, payloadOffset, defects);
        while (c.MoveNext())
        {
            switch (c.Id)
            {
                case MatroskaIds.SamplingFrequency: rate = Float(c.Payload, 8000, defects, c.Offset); break;
                case MatroskaIds.OutputSamplingFrequency: outRate = Float(c.Payload, -1, defects, c.Offset); break;
                case MatroskaIds.Channels: channels = Unsigned(c.Payload, 1, defects, c.Offset); break;
                case MatroskaIds.BitDepth: depth = Unsigned(c.Payload, 0, defects, c.Offset); break;
                default: break;
            }
        }

        return new MatroskaAudio(rate, outRate < 0 ? rate : outRate, channels, depth);
    }

    private static void ParseContentEncodings(ReadOnlySpan<byte> payload, long payloadOffset, DefectLog defects, MediaLimits limits, List<MatroskaContentEncoding> into)
    {
        EbmlChildren c = new(payload, payloadOffset, defects);
        while (c.MoveNext())
        {
            if (c.Id != MatroskaIds.ContentEncoding)
            {
                continue;
            }

            long order = 0, scope = 1, type = 0, algo = -1;
            byte[] settings = [];
            EbmlChildren e = new(c.Payload, c.PayloadOffset, defects);
            while (e.MoveNext())
            {
                switch (e.Id)
                {
                    case MatroskaIds.ContentEncodingOrder: order = Unsigned(e.Payload, 0, defects, e.Offset); break;
                    case MatroskaIds.ContentEncodingScope: scope = Unsigned(e.Payload, 1, defects, e.Offset); break;
                    case MatroskaIds.ContentEncodingType: type = Unsigned(e.Payload, 0, defects, e.Offset); break;
                    case MatroskaIds.ContentCompression:
                        algo = 0; // RFC 9559 §5.1.4.1.31.6: ContentCompAlgo defaults to 0 (zlib).
                        EbmlChildren k = new(e.Payload, e.PayloadOffset, defects);
                        while (k.MoveNext())
                        {
                            if (k.Id == MatroskaIds.ContentCompAlgo)
                            {
                                algo = Unsigned(k.Payload, 0, defects, k.Offset);
                            }
                            else if (k.Id == MatroskaIds.ContentCompSettings)
                            {
                                limits.CheckAllocation(k.Payload.Length, k.Offset);
                                settings = k.Payload.ToArray();
                            }
                        }

                        break;
                    default: break;
                }
            }

            limits.CheckElementCount(into.Count + 1, c.Offset);
            into.Add(new MatroskaContentEncoding(order, scope, type, type == 0 ? algo : -1, settings));
        }
    }

    public static List<MatroskaSeekEntry> ParseSeekHead(ReadOnlySpan<byte> payload, long payloadOffset, DefectLog defects, MediaLimits limits)
    {
        List<MatroskaSeekEntry> entries = [];
        EbmlChildren c = new(payload, payloadOffset, defects);
        while (c.MoveNext())
        {
            if (c.Id != MatroskaIds.Seek)
            {
                continue;
            }

            uint id = 0;
            long position = -1;
            EbmlChildren s = new(c.Payload, c.PayloadOffset, defects);
            while (s.MoveNext())
            {
                if (s.Id == MatroskaIds.SeekId)
                {
                    Ebml.TryReadId(s.Payload, out id, out _);
                }
                else if (s.Id == MatroskaIds.SeekPosition)
                {
                    position = Unsigned(s.Payload, -1, defects, s.Offset);
                }
            }

            if (id != 0 && position >= 0)
            {
                limits.CheckElementCount(entries.Count + 1, c.Offset);
                entries.Add(new MatroskaSeekEntry(id, position));
            }
            else
            {
                defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, c.Offset, "A Seek entry lacks its SeekID or SeekPosition and is ignored.");
            }
        }

        return entries;
    }

    public static List<MatroskaCuePoint> ParseCues(ReadOnlySpan<byte> payload, long payloadOffset, DefectLog defects, MediaLimits limits)
    {
        List<MatroskaCuePoint> cues = [];
        EbmlChildren c = new(payload, payloadOffset, defects);
        while (c.MoveNext())
        {
            if (c.Id != MatroskaIds.CuePoint)
            {
                continue;
            }

            long time = 0;
            bool hasTime = false;
            EbmlChildren p = new(c.Payload, c.PayloadOffset, defects);
            int firstOfPoint = cues.Count;
            while (p.MoveNext())
            {
                if (p.Id == MatroskaIds.CueTime)
                {
                    time = Unsigned(p.Payload, 0, defects, p.Offset);
                    hasTime = true;
                }
                else if (p.Id == MatroskaIds.CueTrackPositions)
                {
                    long track = 0, cluster = -1, relative = -1;
                    EbmlChildren t = new(p.Payload, p.PayloadOffset, defects);
                    while (t.MoveNext())
                    {
                        switch (t.Id)
                        {
                            case MatroskaIds.CueTrack: track = Unsigned(t.Payload, 0, defects, t.Offset); break;
                            case MatroskaIds.CueClusterPosition: cluster = Unsigned(t.Payload, -1, defects, t.Offset); break;
                            case MatroskaIds.CueRelativePosition: relative = Unsigned(t.Payload, -1, defects, t.Offset); break;
                            default: break;
                        }
                    }

                    if (cluster >= 0)
                    {
                        limits.CheckSampleTable(cues.Count + 1, p.Offset);
                        cues.Add(new MatroskaCuePoint(0, track, cluster, relative));
                    }
                }
            }

            if (!hasTime)
            {
                defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, c.Offset, "A CuePoint has no CueTime; its positions are ignored.");
                cues.RemoveRange(firstOfPoint, cues.Count - firstOfPoint);
            }
            else
            {
                for (int i = firstOfPoint; i < cues.Count; i++)
                {
                    cues[i] = cues[i] with { Time = time };
                }
            }
        }

        return cues;
    }
}
