using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>The readers the harness tries. Add one line per reader as it lands.</summary>
internal static class Readers
{
    public static MediaFormatRegistry Create()
    {
        MediaFormatRegistry registry = new();
        registry.Add(new RecursiveAI.OneMedia.IsoBmff.IsoBmffFormat());
        registry.Add(new RecursiveAI.OneMedia.Matroska.MatroskaFormat());
        registry.Add(new RecursiveAI.OneMedia.Asf.AsfFormat());
        registry.Add(new RecursiveAI.OneMedia.MpegTs.MpegTsFormat());
        registry.Add(new RecursiveAI.OneMedia.MpegPs.MpegPsFormat());
        registry.Add(new RecursiveAI.OneMedia.ElementaryStreams.IvfFormat());
        foreach (RecursiveAI.OneMedia.ElementaryStreams.VideoStreamKind k in Enum.GetValues<RecursiveAI.OneMedia.ElementaryStreams.VideoStreamKind>())
        {
            registry.Add(new RecursiveAI.OneMedia.ElementaryStreams.VideoStreamFormat(k));
        }
        registry.Add(new RecursiveAI.OneMedia.AudioStreams.Id3AudioFormat());
        registry.Add(new RecursiveAI.OneMedia.AudioStreams.MpegAudioFormat());
        registry.Add(new RecursiveAI.OneMedia.AudioStreams.AdtsFormat());
        registry.Add(new RecursiveAI.OneMedia.AudioStreams.Ac3Format());
        registry.Add(new RecursiveAI.OneMedia.AudioStreams.AmrFormat());
        return registry;
    }
}

/// <summary>What our demuxer produced for one file.</summary>
internal sealed class OurResult
{
    public string Status { get; init; } = "ok"; // ok, unsupported, error
    public string? Message { get; init; }
    public string Format { get; init; } = "";
    public List<MediaTrack> Tracks { get; init; } = [];
    public List<PacketRecord> Packets { get; init; } = [];

    /// <summary>SHA-256 of each picture in the file's tags (ASF only), to compare with the attached pictures ffprobe lists as streams.</summary>
    public List<string> PictureHashes { get; init; } = [];

    /// <summary>True when the demuxer reports the content as protected and so delivers no packets.</summary>
    public bool IsProtected { get; init; }

    /// <summary>Per track, the shift applied to our times to place them where ffprobe puts them (ISO edit lists); empty when none was applied.</summary>
    public MediaTime[] Shifts { get; init; } = [];
}

/// <summary>Reads a file through the registry and reduces every packet to a <see cref="PacketRecord"/>.</summary>
internal static class OurReader
{
    public static OurResult Read(MediaFormatRegistry registry, string path)
    {
        try
        {
            using FileMediaSource source = FileMediaSource.Open(path);
            using IDemuxer demuxer = registry.Open(source, new MediaOpenOptions { LeaveOpen = true });
            MediaTime[] shift = demuxer.Format == MediaFormatId.IsoBmff ? [.. demuxer.Tracks.Select(EditShift)] : [];
            OurResult result = new() { Format = demuxer.Format.Name, Tracks = [.. demuxer.Tracks], Shifts = shift, IsProtected = demuxer.IsProtected };
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            while (demuxer.TryReadPacket(out PacketView view))
            {
                SHA256.HashData(view.Data, digest);
                PacketInfo i = view.Info;
                if (shift.Length > 0)
                {
                    MediaTime s = shift[i.TrackIndex];
                    i = i with { Pts = Shifted(i.Pts, s), Dts = Shifted(i.Dts, s) };
                }

                result.Packets.Add(new PacketRecord(i.TrackIndex, i.Pts, i.Dts, i.Duration, view.Data.Length, i.SourceRange.IsNone ? -1 : i.SourceRange.Offset, i.IsKeyframe, Convert.ToHexStringLower(digest)));
            }

            if (demuxer is RecursiveAI.OneMedia.Metadata.IMetadataProvider provider && demuxer.Format == MediaFormatId.Asf)
            {
                // ffprobe turns WM/Picture attributes into attached-picture streams; we hand them out as tags.
                foreach (RecursiveAI.OneMedia.Tags.TagPicture picture in provider.ReadMetadata().Tags.Pictures)
                {
                    result.PictureHashes.Add(Convert.ToHexStringLower(SHA256.HashData(picture.ReadData())));
                }
            }

            return result;
        }
        catch (UnsupportedMediaException e)
        {
            return new OurResult { Status = "unsupported", Message = e.Message };
        }
        catch (MediaException e)
        {
            return new OurResult { Status = "error", Message = e.GetType().Name + ": " + e.Message };
        }
    }

    // ffprobe places ISO samples on the edited timeline: it subtracts the first non-empty edit's start and adds the leading empty edits.
    // Our packets keep the media timeline (edit lists are reported, not applied), so the comparison applies ffprobe's shift to ours.
    private static MediaTime EditShift(MediaTrack track)
    {
        MediaTime start = MediaTime.Zero;
        foreach (EditSegment e in track.Edit.Edits ?? [])
        {
            if (!e.IsEmpty)
            {
                start = e.MediaStart;
                break;
            }
        }

        MediaTime lead = track.Edit.StartTime.IsKnown ? track.Edit.StartTime : MediaTime.Zero;
        // In the track's own timescale, so the shift is exact for the media times it is added to.
        return lead.Rescale(track.Timescale, MediaRounding.NearestAwayFromZero).Subtract(start, MediaRounding.NearestAwayFromZero);
    }

    internal static MediaTime Shifted(MediaTime t, MediaTime shift) =>
        t.IsKnown && !shift.IsZero ? t.Add(shift, MediaRounding.NearestAwayFromZero) : t;
}

/// <summary>
/// The committed list of accepted differences (<c>exceptions.tsv</c>): format, field, scope, rule, which side
/// follows the specification, and the reason. A difference is excepted only when a line matches it.
/// </summary>
internal sealed class Exceptions
{
    internal sealed record Rule(string Format, string Field, string Scope, string Kind, string Follows, string Reason);

    private readonly List<Rule> _rules = [];

    public static Exceptions None => new();

    public IReadOnlyList<Rule> Rules => _rules;

    public static Exceptions Load(string path)
    {
        Exceptions e = new();
        foreach (string raw in File.ReadLines(path))
        {
            if (raw.Length == 0 || raw[0] == '#')
            {
                continue;
            }

            string[] f = raw.Split('\t');
            if (f.Length < 6)
            {
                throw new FormatException("exceptions line needs 6 tab-separated columns: " + raw);
            }

            e._rules.Add(new Rule(f[0], f[1], f[2], f[3], f[4], f[5]));
        }

        return e;
    }

    /// <summary>The rule excusing this difference, or null.</summary>
    public Rule? Match(string format, string field, string file, MediaTrack? track, string codecName, string theirs, string ours, bool theirsUnknown, bool oursUnknown, long delta, bool isLast = false, long index = 0)
    {
        foreach (Rule r in _rules)
        {
            if ((r.Format != "*" && r.Format != format) || (r.Field != "*" && r.Field != field) || !ScopeMatches(r.Scope, file, track, codecName, isLast))
            {
                continue;
            }

            if (r.Kind == "ignore" ||
                (r.Kind == "ffprobe-na" && theirsUnknown) ||
                (r.Kind == "ours-na" && oursUnknown) ||
                (r.Kind.StartsWith("tolerance=", StringComparison.Ordinal) && Math.Abs(delta) <= long.Parse(r.Kind.AsSpan(10), CultureInfo.InvariantCulture)) ||
                // drift=N: the other side truncates every packet duration to its integer scale, so the error grows by up to N ticks per packet.
                (r.Kind.StartsWith("drift=", StringComparison.Ordinal) && Math.Abs(delta) <= long.Parse(r.Kind.AsSpan(6), CultureInfo.InvariantCulture) * (index + 1)))
            {
                return r;
            }
        }

        _ = theirs;
        _ = ours;
        return null;
    }

    private static bool ScopeMatches(string scope, string file, MediaTrack? track, string codecName, bool isLast)
    {
        if (scope == "*")
        {
            return true;
        }

        if (scope == "last")
        {
            return isLast;
        }

        int eq = scope.IndexOf('=', StringComparison.Ordinal);
        string key = scope[..eq], value = scope[(eq + 1)..];
        return key switch
        {
            "kind" => track is not null && string.Equals(track.Kind.ToString(), value, StringComparison.OrdinalIgnoreCase),
            "codec" => codecName == value,
            "file" => Path.GetFileName(file).Contains(value, StringComparison.OrdinalIgnoreCase) || file.Contains(value, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}

/// <summary>One field mismatch (the first few per track and field are kept as examples).</summary>
internal sealed record FieldDifference(int Track, long PacketIndex, string Field, string Theirs, string Ours, string? ExceptedBy);

/// <summary>The result of comparing one file.</summary>
internal sealed class FileReport
{
    public string File { get; init; } = "";
    public string Format { get; set; } = "";
    public string Status { get; set; } = "ok"; // ok, diff, unsupported, error
    public string? Message { get; set; }
    public Dictionary<int, (long Theirs, long Ours)> PacketCounts { get; } = [];
    public Dictionary<string, long> Differences { get; } = [];
    public Dictionary<string, long> Excepted { get; } = [];
    public List<FieldDifference> Examples { get; } = [];
    public long TheirPackets { get; set; }
    public long OurPackets { get; set; }
}

/// <summary>Field-by-field comparison of two packet lists, per track and in track order.</summary>
internal static class Comparer
{
    private const int ExamplesPerField = 3;

    public static FileReport Compare(string file, IReadOnlyList<ProbeStream> streams, IReadOnlyList<PacketRecord> theirs, OurResult ours, Exceptions exceptions)
    {
        FileReport report = new() { File = file, Format = ours.Format, TheirPackets = theirs.Count };
        if (ours.Status != "ok")
        {
            report.Status = ours.Status;
            report.Message = ours.Message;
            return report;
        }

        report.OurPackets = ours.Packets.Count;
        if (ours.Format == "asf" && streams.Any(q => q.AttachedPicture))
        {
            // Attached pictures are tags for us: take them out of ffprobe's streams (renumbering the rest) and compare the picture bytes instead.
            List<string> pictureHashes = [.. ours.PictureHashes];
            Dictionary<int, int> renumber = [];
            foreach (ProbeStream q in streams.Where(q => !q.AttachedPicture))
            {
                renumber[q.Index] = renumber.Count;
            }

            List<PacketRecord> kept = [];
            foreach (PacketRecord pr in theirs)
            {
                if (renumber.TryGetValue(pr.Track, out int mapped))
                {
                    kept.Add(pr with { Track = mapped });
                }
                else if (!pictureHashes.Remove(pr.Hash))
                {
                    Note(report, new Dictionary<(int, string), int>(), exceptions, ours.Format, file, null, "", -1, pr.Track, -1, "picture", pr.Hash, "none", false, true, 0);
                }
            }

            theirs = kept;
            streams = [.. streams.Where(q => !q.AttachedPicture).Select(q => q with { Index = renumber[q.Index] })];
        }

        if (ours.IsProtected && ours.Packets.Count == 0)
        {
            // Protected content is reported with its tracks and tags and no packets, by design; ffprobe lists the encrypted packets.
            report.Status = "protected";
            report.Message = $"protected: {ours.Tracks.Count} tracks, ffprobe lists {streams.Count} streams and {theirs.Count} packets";
            return report;
        }

        Dictionary<int, List<PacketRecord>> a = Group(theirs), b = Group(ours.Packets);
        Dictionary<(int, string), int> shown = [];
        foreach (int track in a.Keys.Union(b.Keys).Order())
        {
            List<PacketRecord> x = a.GetValueOrDefault(track) ?? [], y = b.GetValueOrDefault(track) ?? [];
            report.PacketCounts[track] = (x.Count, y.Count);
            long last = Math.Min(x.Count, y.Count) - 1;
            MediaTrack? t = ours.Tracks.FirstOrDefault(q => q.Index == track);
            string codec = streams.FirstOrDefault(s => s.Index == track)?.CodecName ?? "";
            // A scoped "leading" rule excuses ffprobe listing the broken start of a stream (the tail of a unit that began before the
            // file did) as packets of their own. How many to drop is the count that pairs the packets best: the surplus may also be at the end.
            int surplus = x.Count - y.Count;
            if (surplus is >= 1 and <= 3 && exceptions.Match(ours.Format, "leading", file, t, codec, "", "", false, false, 0) is not null)
            {
                int best = 0, bestScore = Mismatches(x, y);
                for (int d = 1; d <= surplus; d++)
                {
                    int score = Mismatches(x.Skip(d), y);
                    if (score < bestScore)
                    {
                        (best, bestScore) = (d, score);
                    }
                }

                if (best > 0)
                {
                    x = x.GetRange(best, x.Count - best);
                    report.Excepted["leading"] = report.Excepted.GetValueOrDefault("leading") + best;
                    report.PacketCounts[track] = (x.Count + best, y.Count);
                    last = Math.Min(x.Count, y.Count) - 1;
                }
            }

            if (x.Count != y.Count)
            {
                Note(report, shown, exceptions, ours.Format, file, t, codec, last, track, -1, "count", x.Count.ToString(CultureInfo.InvariantCulture), y.Count.ToString(CultureInfo.InvariantCulture), false, false, 0);
            }

            for (int i = 0; i < Math.Min(x.Count, y.Count); i++)
            {
                PacketRecord p = x[i], q = y[i];
                CompareTime(report, shown, exceptions, ours.Format, file, t, codec, last, track, i, "pts", p.Pts, q.Pts);
                CompareTime(report, shown, exceptions, ours.Format, file, t, codec, last, track, i, "dts", p.Dts, q.Dts);
                CompareTime(report, shown, exceptions, ours.Format, file, t, codec, last, track, i, "duration", p.Duration, q.Duration);
                if (p.Size != q.Size)
                {
                    Note(report, shown, exceptions, ours.Format, file, t, codec, last, track, i, "size", p.Size.ToString(CultureInfo.InvariantCulture), q.Size.ToString(CultureInfo.InvariantCulture), false, false, p.Size - q.Size);
                }

                if (p.Pos != q.Pos)
                {
                    Note(report, shown, exceptions, ours.Format, file, t, codec, last, track, i, "pos", p.Pos.ToString(CultureInfo.InvariantCulture), q.Pos.ToString(CultureInfo.InvariantCulture), p.Pos < 0, q.Pos < 0, p.Pos - q.Pos);
                }

                if (p.Key != q.Key)
                {
                    Note(report, shown, exceptions, ours.Format, file, t, codec, last, track, i, "key", p.Key.ToString(), q.Key.ToString(), false, false, 0);
                }

                if (p.Hash != q.Hash)
                {
                    Note(report, shown, exceptions, ours.Format, file, t, codec, last, track, i, "hash", p.Hash, q.Hash, false, false, 0);
                }
            }
        }

        report.Status = report.Differences.Count == 0 ? "ok" : "diff";
        return report;
    }

    // How many of the first 64 pairs differ in size: picks between a stream that starts with extra packets and one that ends with them.
    private static int Mismatches(IEnumerable<PacketRecord> x, IReadOnlyList<PacketRecord> y) =>
        x.Take(64).Zip(y.Take(64), (p, q) => p.Size != q.Size || p.Hash != q.Hash).Count(b => b);

    private static Dictionary<int, List<PacketRecord>> Group(IEnumerable<PacketRecord> packets)
    {
        Dictionary<int, List<PacketRecord>> d = [];
        foreach (PacketRecord p in packets)
        {
            if (!d.TryGetValue(p.Track, out List<PacketRecord>? list))
            {
                d[p.Track] = list = [];
            }

            list.Add(p);
        }

        return d;
    }

    private static void CompareTime(FileReport r, Dictionary<(int, string), int> shown, Exceptions ex, string format, string file, MediaTrack? t, string codec, long last, int track, long index, string field, MediaTime theirs, MediaTime ours)
    {
        if (theirs.IsKnown != ours.IsKnown || (theirs.IsKnown && theirs != ours))
        {
            long delta = 0;
            // Tolerance rules count ticks of the finer of the two scales; keep the difference in ffprobe's scale.
            if (theirs.IsKnown && ours.IsKnown)
            {
                delta = ours.Rescale(theirs.Timescale, MediaRounding.NearestAwayFromZero).Ticks - theirs.Ticks;
            }

            Note(r, shown, ex, format, file, t, codec, last, track, index, field, theirs.ToString(), ours.ToString(), !theirs.IsKnown, !ours.IsKnown, delta);
        }
    }

    private static void Note(FileReport r, Dictionary<(int, string), int> shown, Exceptions ex, string format, string file, MediaTrack? t, string codec, long last, int track, long index, string field, string theirs, string ours, bool theirsUnknown, bool oursUnknown, long delta)
    {
        Exceptions.Rule? rule = ex.Match(format, field, file, t, codec, theirs, ours, theirsUnknown, oursUnknown, delta, index >= 0 && index == last, index);
        Dictionary<string, long> bucket = rule is null ? r.Differences : r.Excepted;
        bucket[field] = bucket.GetValueOrDefault(field) + 1;
        if (rule is not null)
        {
            return;
        }

        int n = shown.GetValueOrDefault((track, field));
        if (n < ExamplesPerField)
        {
            shown[(track, field)] = n + 1;
            r.Examples.Add(new FieldDifference(track, index, field, theirs, ours, null));
        }
    }
}

/// <summary>Writes one JSON result per file (so a sweep can resume) and a summary over a result directory.</summary>
internal static class Reports
{
    private static string FileName(string outDir, string file) =>
        Path.Combine(outDir, "files", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file)))[..16] + ".json");

    /// <summary>True when a result for this exact file (size and modification time) already exists.</summary>
    public static bool IsDone(string outDir, string file)
    {
        string path = FileName(outDir, file);
        if (!File.Exists(path))
        {
            return false;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
        FileInfo fi = new(file);
        return doc.RootElement.TryGetProperty("size", out JsonElement size) && size.GetInt64() == fi.Length &&
               doc.RootElement.TryGetProperty("mtime", out JsonElement mtime) && mtime.GetInt64() == fi.LastWriteTimeUtc.Ticks;
    }

    public static void WriteFile(string outDir, FileReport r)
    {
        string path = FileName(outDir, r.File);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileInfo fi = new(r.File);
        using FileStream fs = File.Create(path);
        using Utf8JsonWriter w = new(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("file", r.File);
        w.WriteNumber("size", fi.Length);
        w.WriteNumber("mtime", fi.LastWriteTimeUtc.Ticks);
        w.WriteString("format", r.Format);
        w.WriteString("status", r.Status);
        if (r.Message is not null)
        {
            w.WriteString("message", r.Message);
        }

        w.WriteNumber("theirPackets", r.TheirPackets);
        w.WriteNumber("ourPackets", r.OurPackets);
        w.WriteStartObject("packetsPerTrack");
        foreach (KeyValuePair<int, (long Theirs, long Ours)> kv in r.PacketCounts.OrderBy(k => k.Key))
        {
            w.WriteString(kv.Key.ToString(CultureInfo.InvariantCulture), $"{kv.Value.Theirs}/{kv.Value.Ours}");
        }

        w.WriteEndObject();
        WriteCounts(w, "differences", r.Differences);
        WriteCounts(w, "excepted", r.Excepted);
        w.WriteStartArray("examples");
        foreach (FieldDifference d in r.Examples)
        {
            w.WriteStartObject();
            w.WriteNumber("track", d.Track);
            w.WriteNumber("packet", d.PacketIndex);
            w.WriteString("field", d.Field);
            w.WriteString("theirs", d.Theirs);
            w.WriteString("ours", d.Ours);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>Summarises every result in <paramref name="outDir"/> into summary.txt and returns the text.</summary>
    public static string Summarize(string outDir)
    {
        StringBuilder sb = new();
        List<(string File, string Status, string Format, long Theirs, long Ours, string Detail)> rows = [];
        string dir = Path.Combine(outDir, "files");
        if (Directory.Exists(dir))
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*.json"))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(f));
                JsonElement e = doc.RootElement;
                string detail = e.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : string.Join(", ", e.GetProperty("differences").EnumerateObject().Select(p => $"{p.Name}={p.Value.GetInt64()}"));
                rows.Add((e.GetProperty("file").GetString() ?? "", e.GetProperty("status").GetString() ?? "", e.GetProperty("format").GetString() ?? "", e.GetProperty("theirPackets").GetInt64(), e.GetProperty("ourPackets").GetInt64(), detail));
            }
        }

        foreach (IGrouping<string, (string File, string Status, string Format, long Theirs, long Ours, string Detail)> g in rows.GroupBy(r => r.Status).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{g.Key}: {g.Count()} files\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"packets: ffprobe {rows.Sum(r => r.Theirs)}, ours {rows.Sum(r => r.Ours)}\n");
        foreach (var r in rows.Where(r => r.Status != "ok").OrderBy(r => r.File, StringComparer.Ordinal))
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {r.Status} {r.File}: {r.Detail}\n");
        }

        File.WriteAllText(Path.Combine(outDir, "summary.txt"), sb.ToString());
        return sb.ToString();
    }

    private static void WriteCounts(Utf8JsonWriter w, string name, Dictionary<string, long> counts)
    {
        w.WriteStartObject(name);
        foreach (KeyValuePair<string, long> kv in counts.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            w.WriteNumber(kv.Key, kv.Value);
        }

        w.WriteEndObject();
    }
}
