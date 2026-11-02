using System.Globalization;
using System.Xml;
using RecursiveAI.OneMedia.Metadata.Telemetry.Dji;
using RecursiveAI.OneMedia.Tags.Xmp;

namespace RecursiveAI.OneMedia.Metadata;

/// <summary>
/// A bundle of fact candidates from one place outside the container's own tag layers: an XMP, Sony XML or DJI SRT sidecar, or an embedded Exif block.
/// Pass it to <see cref="FactEvidence.With(EvidenceSet)"/>. Byte ranges of candidates refer to the sidecar's own bytes, which the <c>sidecar:</c> source
/// prefix makes visible.
/// </summary>
public sealed class EvidenceSet
{
    /// <summary>Creates an empty set.</summary>
    /// <param name="name">Where the readings came from (<c>sidecar:xmp</c>, <c>exif</c>).</param>
    public EvidenceSet(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>Where the readings came from; also the prefix of every candidate's <see cref="FactCandidate{T}.Source"/>.</summary>
    public string Name { get; }

    /// <summary>Capture-time readings.</summary>
    public IReadOnlyList<FactCandidate<MediaDateTime>> CaptureTime { get; set; } = [];

    /// <summary>Location readings.</summary>
    public IReadOnlyList<FactCandidate<GeoLocation>> Location { get; set; } = [];

    /// <summary>Device maker readings.</summary>
    public IReadOnlyList<FactCandidate<string>> Make { get; set; } = [];

    /// <summary>Device model readings.</summary>
    public IReadOnlyList<FactCandidate<string>> Model { get; set; } = [];

    /// <summary>Recording software readings.</summary>
    public IReadOnlyList<FactCandidate<string>> Software { get; set; } = [];

    /// <summary>Device serial number readings.</summary>
    public IReadOnlyList<FactCandidate<string>> Serial { get; set; } = [];

    /// <summary>Decoded telemetry the sidecar carries; empty for the sidecars read here.</summary>
    public IReadOnlyList<TelemetryChannel> Telemetry { get; set; } = [];

    /// <summary>Deviations found while reading, as the file-format layers report them.</summary>
    public DefectLog Defects { get; init; } = new();
}

/// <summary>Which sidecar format a file is.</summary>
public enum SidecarKind
{
    /// <summary>Not a recognised sidecar.</summary>
    Unknown = 0,

    /// <summary>An XMP packet in its own file (<c>.xmp</c>).</summary>
    Xmp,

    /// <summary>A Sony <c>NonRealTimeMeta</c> XML file written beside XAVC-S, XAVC and XDCAM clips.</summary>
    SonyXml,

    /// <summary>A DJI drone subtitle file (<c>.srt</c>) with per-frame exposure and position.</summary>
    DjiSrt,
}

/// <summary>
/// Reads sidecar files as evidence. Detection is by content (an XMP packet, the <c>NonRealTimeMeta</c> root, the DJI per-frame line), never by extension alone.
/// Parsers read at most <see cref="MediaLimits.MaxTagBytes"/> (SRT: the first 64 KiB, which holds the first frames), keep what they understand, and report
/// the rest as defects; they never throw on bad content.
/// </summary>
public static class Sidecars
{
    private const int SrtHeadBytes = 64 * 1024;

    /// <summary>Tells which sidecar format a content head is.</summary>
    /// <param name="head">The first bytes of the file (at least a few hundred are useful).</param>
    /// <returns>The kind, or <see cref="SidecarKind.Unknown"/>.</returns>
    public static SidecarKind Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            head = head[3..];
        }

        ReadOnlySpan<byte> view = head.Length > 4096 ? head[..4096] : head;
        if (view.IndexOf("NonRealTimeMeta"u8) >= 0)
        {
            return SidecarKind.SonyXml;
        }

        if (view.IndexOf("xmpmeta"u8) >= 0 || view.IndexOf("<?xpacket"u8) >= 0 || view.IndexOf("http://ns.adobe.com/xap/1.0/"u8) >= 0)
        {
            return SidecarKind.Xmp;
        }

        // DJI: an SRT cue whose text holds the bracketed fields.
        if (view.IndexOf("-->"u8) >= 0 && (view.IndexOf("[latitude"u8) >= 0 || view.IndexOf("[iso"u8) >= 0 || view.IndexOf("SrtCnt"u8) >= 0 || view.IndexOf("FrameCnt"u8) >= 0))
        {
            return SidecarKind.DjiSrt;
        }

        return SidecarKind.Unknown;
    }

    /// <summary>Reads a sidecar from a stream, detecting its kind from the content.</summary>
    /// <param name="stream">The sidecar, positioned at its start. At most <see cref="MediaLimits.MaxTagBytes"/> bytes are read.</param>
    /// <param name="limits">Resource limits; <see cref="MediaLimits.Default"/> when <c>null</c>.</param>
    /// <param name="cancellationToken">Observed before reading.</param>
    /// <returns>The evidence, or <c>null</c> when the content is not a recognised sidecar.</returns>
    public static EvidenceSet? Read(Stream stream, MediaLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        limits ??= MediaLimits.Default;
        cancellationToken.ThrowIfCancellationRequested();
        int max = (int)Math.Min(limits.MaxTagBytes, int.MaxValue / 2);
        byte[] buffer = new byte[Math.Min(max, 1 << 20)];
        int n = 0;
        while (n < max)
        {
            if (n == buffer.Length)
            {
                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, max));
            }

            int r = stream.Read(buffer, n, Math.Min(buffer.Length - n, max - n));
            if (r <= 0)
            {
                break;
            }

            n += r;
        }

        return Read(buffer.AsMemory(0, n), limits);
    }

    /// <summary>Reads a sidecar held in memory, detecting its kind from the content.</summary>
    /// <param name="data">The sidecar bytes.</param>
    /// <param name="limits">Resource limits; <see cref="MediaLimits.Default"/> when <c>null</c>.</param>
    /// <returns>The evidence, or <c>null</c> when the content is not a recognised sidecar.</returns>
    public static EvidenceSet? Read(ReadOnlyMemory<byte> data, MediaLimits? limits = null) => Sniff(data.Span) switch
    {
        SidecarKind.Xmp => ReadXmp(data, limits),
        SidecarKind.SonyXml => ReadSonyXml(data, limits),
        SidecarKind.DjiSrt => ReadDjiSrt(data, limits),
        _ => null,
    };

    /// <summary>
    /// Reads an XMP sidecar: <c>exif:DateTimeOriginal</c>, <c>xmp:CreateDate</c>, <c>photoshop:DateCreated</c>, the Exif GPS pair, <c>tiff:Make</c>/<c>Model</c>,
    /// <c>xmp:CreatorTool</c> and the camera serial number (<c>aux:SerialNumber</c>, <c>exifEX:BodySerialNumber</c>), through the same mapping as XMP inside a movie.
    /// </summary>
    /// <param name="data">The packet bytes (with or without the <c>xpacket</c> wrapper).</param>
    /// <param name="limits">Resource limits; <see cref="MediaLimits.Default"/> when <c>null</c>.</param>
    /// <returns>The evidence; malformed XML is a defect in <see cref="EvidenceSet.Defects"/> and what was read before it is kept.</returns>
    public static EvidenceSet ReadXmp(ReadOnlyMemory<byte> data, MediaLimits? limits = null)
    {
        limits ??= MediaLimits.Default;
        EvidenceSet set = new("sidecar:xmp") { Defects = new DefectLog(limits) };
        using MemoryMediaSource source = new(data);
        ByteRange all = new(0, data.Length);
        XmpPacket packet = XmpPacket.Read(source, all, all, limits, set.Defects);
        FactsBuilder b = new(set.Defects);
        b.AddXmp(packet);
        Copy(b.Build(), set);
        return set;
    }

    /// <summary>
    /// Reads a Sony <c>NonRealTimeMeta</c> file: <c>CreationDate/@value</c> (an ISO 8601 date-time with an offset or <c>Z</c>) and
    /// <c>Device/@manufacturer</c>, <c>@modelName</c> and <c>@serialNo</c>. A serial number of 4294967295 (all ones) is how cameras say "none" and is skipped.
    /// </summary>
    /// <param name="data">The XML bytes.</param>
    /// <param name="limits">Resource limits; <see cref="MediaLimits.Default"/> when <c>null</c>.</param>
    /// <returns>The evidence; malformed XML is a defect and what was read before it is kept.</returns>
    public static EvidenceSet ReadSonyXml(ReadOnlyMemory<byte> data, MediaLimits? limits = null)
    {
        limits ??= MediaLimits.Default;
        EvidenceSet set = new("sidecar:sony-xml") { Defects = new DefectLog(limits) };
        List<FactCandidate<MediaDateTime>> times = [];
        List<FactCandidate<string>> make = [];
        List<FactCandidate<string>> model = [];
        List<FactCandidate<string>> serial = [];
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = limits.MaxTagBytes,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
        };
        try
        {
            using MemoryStream ms = new(data.ToArray(), writable: false);
            using XmlReader r = XmlReader.Create(ms, settings);
            int elements = 0;
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                // A clip file is small and flat; anything with this many elements is not one.
                if (++elements > 20_000)
                {
                    set.Defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, -1, "The Sony XML file has too many elements; the rest are skipped.", "sidecar.limit");
                    break;
                }

                if (r.LocalName == "CreationDate" && r.GetAttribute("value") is { } v)
                {
                    if (MediaDateTime.TryParse(v, out MediaDateTime dt, out bool dateOnly) && !dateOnly)
                    {
                        times.Add(new FactCandidate<MediaDateTime>(dt, "sidecar:sony-xml:CreationDate", v, "Sony NonRealTimeMeta CreationDate, written with the camera's UTC offset", dt.Certainty));
                    }
                    else
                    {
                        set.Defects.Add(DefectKind.InvalidValue, DefectSeverity.Info, -1, "The Sony XML CreationDate is not a date-time.", "sidecar.sony-date");
                    }
                }
                else if (r.LocalName == "Device")
                {
                    AddText(make, r.GetAttribute("manufacturer"), "sidecar:sony-xml:Device.manufacturer");
                    AddText(model, r.GetAttribute("modelName"), "sidecar:sony-xml:Device.modelName");
                    if (r.GetAttribute("serialNo") is { Length: > 0 } sn && sn != "4294967295")
                    {
                        serial.Add(new FactCandidate<string>(sn, "sidecar:sony-xml:Device.serialNo", sn, "camera serial number as written"));
                    }
                }
            }
        }
        catch (Exception e) when (e is XmlException or ArgumentException)
        {
            // XmlReader raises ArgumentException (not XmlException) from its encoding detection on some malformed declarations; found by fuzzing.
            set.Defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, -1, "The Sony XML file is not well-formed XML: " + e.Message, "sidecar.xml-malformed");
        }

        set.CaptureTime = times;
        set.Make = make;
        set.Model = model;
        set.Serial = serial;
        return set;
    }

    private static void AddText(List<FactCandidate<string>> into, string? text, string source)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            into.Add(new FactCandidate<string>(text.Trim(), source, text, "text as written"));
        }
    }

    /// <summary>
    /// Reads a DJI SRT file's first frames: the wall-clock time of frame 1 (<c>YYYY-MM-DD HH:MM:SS.mmm</c>, no zone) moved back by the frame's cue start,
    /// and the first non-zero position with its absolute altitude. The text is read by <see cref="DjiSrt"/>, the same parser that
    /// decodes the full telemetry, so every layout it knows (bracketed pairs, <c>GPS(lat,lon,alt)</c>) gives evidence.
    /// </summary>
    /// <param name="data">The SRT bytes; only the first 64 KiB are read.</param>
    /// <param name="limits">Resource limits; <see cref="MediaLimits.Default"/> when <c>null</c>.</param>
    /// <returns>The evidence.</returns>
    public static EvidenceSet ReadDjiSrt(ReadOnlyMemory<byte> data, MediaLimits? limits = null)
    {
        limits ??= MediaLimits.Default;
        EvidenceSet set = new("sidecar:dji-srt") { Defects = new DefectLog(limits) };
        ReadOnlySpan<byte> head = data.Span;
        if (head.Length > SrtHeadBytes)
        {
            head = head[..SrtHeadBytes];
        }

        // One parser serves both the telemetry channels and this evidence: the first frames' channels give the clock and the position.
        foreach (TelemetryChannel c in DjiSrt.Read(head))
        {
            if (c.Name == "dji clock" && set.CaptureTime.Count == 0)
            {
                foreach (TelemetrySample sample in c.Samples)
                {
                    if (sample is ScalarSample { Values.Count: >= 7 } k && !double.IsNaN(k.Values[0]))
                    {
                        int year = (int)k.Values[0], month = (int)k.Values[1], day = (int)k.Values[2];
                        int hour = (int)k.Values[3], minute = (int)k.Values[4], second = Math.Min((int)k.Values[5], 59), milli = (int)k.Values[6];
                        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
                            || hour is < 0 or > 23 || minute is < 0 or > 59 || second < 0 || milli is < 0 or > 999)
                        {
                            continue; // The text is not a calendar time (30 February, hour 99): not evidence.
                        }

                        DateTime wall = new(year, month, day, hour, minute, second, milli, DateTimeKind.Unspecified);
                        long cueTicks = 0;
                        if (sample.Time.IsKnown)
                        {
                            if (!sample.Time.TryRescale(10_000_000, MediaRounding.NearestEven, out MediaTime cueTime))
                            {
                                continue; // A cue start too large for 64-bit ticks is not a clock offset.
                            }

                            cueTicks = cueTime.Ticks;
                        }

                        long start = wall.Ticks - cueTicks;
                        if (start < 0 || start > DateTime.MaxValue.Ticks)
                        {
                            // A cue start so large that frame 1's clock would fall outside the calendar: this reading is not evidence.
                            continue;
                        }

                        string rawTime = string.Create(CultureInfo.InvariantCulture, $"{wall:yyyy-MM-dd HH:mm:ss.fff}");
                        set.CaptureTime = [new FactCandidate<MediaDateTime>(new MediaDateTime(new DateTime(start, DateTimeKind.Unspecified), null, TimeZoneCertainty.Unknown), "sidecar:dji-srt:timestamp", rawTime,
                            "wall-clock time of frame 1 in the SRT text, moved back by the cue start; no time zone is written")];
                        break;
                    }
                }
            }
            else if (c.Name == "dji gps" && set.Location.Count == 0)
            {
                foreach (TelemetrySample sample in c.Samples)
                {
                    if (sample is GpsSample g && Math.Abs(g.Latitude) <= 90 && Math.Abs(g.Longitude) <= 180 && !(g.Latitude == 0 && g.Longitude == 0))
                    {
                        set.Location = [new FactCandidate<GeoLocation>(new GeoLocation(g.Latitude, g.Longitude, g.Altitude), "sidecar:dji-srt:position", string.Create(CultureInfo.InvariantCulture, $"{g.Latitude} {g.Longitude}"),
                            "first non-zero position of the file; altitude is abs_alt (metres above sea level) or the third GPS() value")];
                        break;
                    }
                }
            }
        }

        return set;
    }

    private static void Copy(MediaFacts f, EvidenceSet set)
    {
        set.CaptureTime = Prefix(f.CaptureTime.Candidates);
        set.Location = Prefix(f.Location.Candidates);
        set.Make = Prefix(f.Make.Candidates);
        set.Model = Prefix(f.Model.Candidates);
        set.Software = Prefix(f.Software.Candidates);
        set.Serial = Prefix(f.Serial.Candidates);
    }

    private static FactCandidate<T>[] Prefix<T>(IReadOnlyList<FactCandidate<T>> list)
    {
        FactCandidate<T>[] r = new FactCandidate<T>[list.Count];
        for (int i = 0; i < r.Length; i++)
        {
            r[i] = list[i] with { Source = "sidecar:" + list[i].Source };
        }

        return r;
    }
}
