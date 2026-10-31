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
        registry.Add(new IsoBmff.IsoBmffFormat());
        registry.Add(new Matroska.MatroskaFormat());
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
            OurResult result = new() { Format = demuxer.Format.Name, Tracks = [.. demuxer.Tracks] };
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            while (demuxer.TryReadPacket(out PacketView view))
            {
                SHA256.HashData(view.Data, digest);
                PacketInfo i = view.Info;
                result.Packets.Add(new PacketRecord(i.TrackIndex, i.Pts, i.Dts, i.Duration, view.Data.Length, i.SourceRange.IsNone ? -1 : i.SourceRange.Offset, i.IsKeyframe, Convert.ToHexStringLower(digest)));
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
    public Rule? Match(string format, string field, string file, MediaTrack? track, string codecName, string theirs, string ours, bool theirsUnknown, bool oursUnknown, long delta)
    {
        foreach (Rule r in _rules)
        {
            if ((r.Format != "*" && r.Format != format) || (r.Field != "*" && r.Field != field) || !ScopeMatches(r.Scope, file, track, codecName))
            {
                continue;
            }

            if (r.Kind == "ignore" ||
                (r.Kind == "ffprobe-na" && theirsUnknown) ||
                (r.Kind == "ours-na" && oursUnknown) ||
                (r.Kind.StartsWith("tolerance=", StringComparison.Ordinal) && Math.Abs(delta) <= long.Parse(r.Kind.AsSpan(10), CultureInfo.InvariantCulture)))
            {
                return r;
            }
        }

        _ = theirs;
        _ = ours;
        return null;
    }

    private static bool ScopeMatches(string scope, string file, MediaTrack? track, string codecName)
    {
        if (scope == "*")
        {
            return true;
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
        Dictionary<int, List<PacketRecord>> a = Group(theirs), b = Group(ours.Packets);
        Dictionary<(int, string), int> shown = [];
        foreach (int track in a.Keys.Union(b.Keys).Order())
        {
            List<PacketRecord> x = a.GetValueOrDefault(track) ?? [], y = b.GetValueOrDefault(track) ?? [];
            report.PacketCounts[track] = (x.Count, y.Count);
            MediaTrack? t = ours.Tracks.FirstOrDefault(q => q.Index == track);
            string codec = streams.FirstOrDefault(s => s.Index == track)?.CodecName ?? "";
            if (x.Count != y.Count)
            {
                Note(report, shown, exceptions, ours.Format, file, t, codec, track, -1, "count", x.Count.ToString(CultureInfo.InvariantCulture), y.Count.ToString(CultureInfo.InvariantCulture), false, false, 0);
            }

            for (int i = 0; i < Math.Min(x.Count, y.Count); i++)
            {
                PacketRecord p = x[i], q = y[i];
                CompareTime(report, shown, exceptions, ours.Format, file, t, codec, track, i, "pts", p.Pts, q.Pts);
                CompareTime(report, shown, exceptions, ours.Format, file, t, codec, track, i, "dts", p.Dts, q.Dts);
                CompareTime(report, shown, exceptions, ours.Format, file, t, codec, track, i, "duration", p.Duration, q.Duration);
                if (p.Size != q.Size)
                {
                    Note(report, shown, exceptions, ours.Format, file, t, codec, track, i, "size", p.Size.ToString(CultureInfo.InvariantCulture), q.Size.ToString(CultureInfo.InvariantCulture), false, false, p.Size - q.Size);
                }

                if (p.Pos != q.Pos)
                {
                    Note(report, shown, exceptions, ours.Format, file, t, codec, track, i, "pos", p.Pos.ToString(CultureInfo.InvariantCulture), q.Pos.ToString(CultureInfo.InvariantCulture), p.Pos < 0, q.Pos < 0, p.Pos - q.Pos);
                }

                if (p.Key != q.Key)
                {
                    Note(report, shown, exceptions, ours.Format, file, t, codec, track, i, "key", p.Key.ToString(), q.Key.ToString(), false, false, 0);
                }

                if (p.Hash != q.Hash)
                {
                    Note(report, shown, exceptions, ours.Format, file, t, codec, track, i, "hash", p.Hash, q.Hash, false, false, 0);
                }
            }
        }

        report.Status = report.Differences.Count == 0 ? "ok" : "diff";
        return report;
    }

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

    private static void CompareTime(FileReport r, Dictionary<(int, string), int> shown, Exceptions ex, string format, string file, MediaTrack? t, string codec, int track, long index, string field, MediaTime theirs, MediaTime ours)
    {
        if (theirs.IsKnown != ours.IsKnown || (theirs.IsKnown && theirs != ours))
        {
            long delta = theirs.IsKnown && ours.IsKnown ? (theirs - ours).Ticks * 1 : 0;
            // Tolerance rules count ticks of the finer of the two scales; keep the difference in ffprobe's scale.
            if (theirs.IsKnown && ours.IsKnown)
            {
                delta = ours.Rescale(theirs.Timescale, MediaRounding.NearestAwayFromZero).Ticks - theirs.Ticks;
            }

            Note(r, shown, ex, format, file, t, codec, track, index, field, theirs.ToString(), ours.ToString(), !theirs.IsKnown, !ours.IsKnown, delta);
        }
    }

    private static void Note(FileReport r, Dictionary<(int, string), int> shown, Exceptions ex, string format, string file, MediaTrack? t, string codec, int track, long index, string field, string theirs, string ours, bool theirsUnknown, bool oursUnknown, long delta)
    {
        Exceptions.Rule? rule = ex.Match(format, field, file, t, codec, theirs, ours, theirsUnknown, oursUnknown, delta);
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
