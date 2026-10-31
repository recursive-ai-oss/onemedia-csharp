using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>
/// Compares what we read as tags and basic facts with ExifTool and MediaInfo, field by field. Three kinds of rows:
/// <c>native</c> (each native text tag's value is looked up among the values the oracle reports in the matching group), <c>common</c> (the common
/// view against the oracle's field of the same meaning) and <c>fact</c> (duration, capture time, location, size, rotation, make/model/software).
/// Rows where only one side has a value are kept and marked, so every difference is visible.
/// </summary>
internal static partial class MetadataCompare
{
    private sealed record Row(string File, string Kind, string Field, string Ours, string ExifTool, string MediaInfo, string Verdict);

    public static int Run(IReadOnlyList<string> files, string outDir)
    {
        Directory.CreateDirectory(outDir);
        List<Row> rows = [];
        MediaFormatRegistry registry = new();
        registry.Add(new IsoBmffFormat());
        registry.Add(new MatroskaFormat());
        foreach (string file in files)
        {
            try
            {
                CompareFile(registry, file, rows);
            }
            catch (Exception e) when (e is MediaException or IOException or JsonException or InvalidOperationException)
            {
                rows.Add(new Row(file, "error", "-", e.GetType().Name + ": " + e.Message, "", "", "error"));
            }
        }

        using StreamWriter w = new(Path.Combine(outDir, "metadata-compare.tsv"), false, new UTF8Encoding(false));
        w.WriteLine("file\tkind\tfield\tours\texiftool\tmediainfo\tverdict");
        foreach (Row r in rows)
        {
            w.WriteLine(string.Join('\t', r.File, r.Kind, r.Field, Clean(r.Ours), Clean(r.ExifTool), Clean(r.MediaInfo), r.Verdict));
        }

        Console.WriteLine($"files {files.Count}");
        foreach (IGrouping<(string, string), Row> g in rows.GroupBy(r => (r.Kind, r.Verdict)).OrderBy(g => g.Key))
        {
            Console.WriteLine($"{g.Key.Item1,-8} {g.Key.Item2,-22} {g.Count()}");
        }

        return 0;
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static void CompareFile(MediaFormatRegistry registry, string file, List<Row> rows)
    {
        string name = Path.GetFileName(file);
        using IDemuxer demuxer = registry.Open(FileMediaSource.Open(file));
        MediaMetadata m = ((IMetadataProvider)demuxer).ReadMetadata();
        Dictionary<string, List<string>> et = ExifToolValues(file, numeric: false);
        Dictionary<string, List<string>> etn = ExifToolValues(file, numeric: true);
        Dictionary<string, List<string>> mi = MediaInfoValues(file);

        // native: every text tag's value among the oracle's values of the corresponding groups
        HashSet<string> miAll = new(mi.Values.SelectMany(v => v).Select(Norm));
        foreach (NativeTag t in m.NativeTags)
        {
            if (t.Kind is not (TagValueKind.Text or TagValueKind.Number) || t.Text is not { } text)
            {
                continue;
            }

            string[] groups = t.System switch
            {
                TagSystems.ITunes => ["ItemList"],
                TagSystems.QuickTimeKeys => ["Keys", "ItemList"],
                TagSystems.QuickTimeUserData or TagSystems.ThreeGpp => ["UserData"],
                TagSystems.Xmp => ["XMP-x", "XMP-xmp", "XMP-dc", "XMP-exif", "XMP-tiff", "XMP-photoshop"],
                _ => ["Matroska"],
            };
            List<string> etVals = [.. et.Where(kv => groups.Any(g => kv.Key.StartsWith(g, StringComparison.Ordinal))).SelectMany(kv => kv.Value)];
            bool inEt = etVals.Exists(v => Same(v, text));
            bool inMi = miAll.Contains(Norm(text)) || mi.Values.SelectMany(v => v).Any(v => Same(v, text));
            rows.Add(new Row(name, "native", $"{t.System}:{t.Key}", text, inEt ? "same" : etVals.Count == 0 ? "-" : "absent", inMi ? "same" : "absent",
                inEt ? (inMi ? "match" : "match-et") : etVals.Count == 0 ? (inMi ? "match-mi" : "unreported") : (inMi ? "match-mi" : "differs")));
        }

        // common view
        CommonTags c = m.Tags;
        Common(rows, name, "Title", c.Title, et, ["Title"], mi, ["Title", "Movie"]);
        Common(rows, name, "Artist", c.Artist, et, ["Artist", "Performer"], mi, ["Performer"]);
        Common(rows, name, "Album", c.Album, et, ["Album"], mi, ["Album"]);
        Common(rows, name, "Genre", c.Genre, et, ["Genre"], mi, ["Genre"]);
        Common(rows, name, "Comment", c.Comment, et, ["Comment"], mi, ["Comment"]);
        Common(rows, name, "Description", c.Description, et, ["Description"], mi, ["Description"]);
        Common(rows, name, "Copyright", c.Copyright, et, ["Copyright"], mi, ["Copyright"]);
        Common(rows, name, "Composer", c.Composer, et, ["Composer"], mi, ["Composer"]);
        Common(rows, name, "Encoder", c.Encoder, et, ["Encoder", "Software", "WritingApp"], mi, ["Encoded_Application", "Writing_Library"]);
        Common(rows, name, "TrackNumber", c.TrackNumber?.ToString(CultureInfo.InvariantCulture), et, ["TrackNumber"], mi, ["Track_Position"], leadingNumber: true);
        Common(rows, name, "Cover", c.Pictures.Count > 0 ? "yes" : null, et, ["CoverArt", "Picture"], mi, ["Cover"], presence: true);

        // facts
        MediaFacts f = m.Facts;
        if (f.Duration.Best is { } d)
        {
            double ours = (double)d.Value.Ticks / d.Value.Timescale;
            double? e = FirstNumber(etn, "Duration"), x = FirstNumber(mi, "Duration");
            rows.Add(new Row(name, "fact", "Duration", ours.ToString("0.000", CultureInfo.InvariantCulture), e?.ToString("0.000", CultureInfo.InvariantCulture) ?? "-", x is null ? "-" : (x.Value / 1000).ToString("0.000", CultureInfo.InvariantCulture),
                Verdict(ours, e, x is null ? null : x / 1000, 0.002)));
        }

        if (f.FrameSize.Best is { } fs)
        {
            double? w = FirstNumber(mi, "Width", "Video"), h = FirstNumber(mi, "Height", "Video");
            string miv = w is null ? "-" : $"{w}x{h}";
            string ours = $"{fs.Value.CodedWidth}x{fs.Value.CodedHeight}";
            rows.Add(new Row(name, "fact", "FrameSize.Coded", ours, "-", miv, miv == "-" ? "unreported" : miv == ours ? "match" : "differs"));
        }

        if (f.Orientation.Best is { } o)
        {
            double? r = FirstNumber(etn, "Rotation"), x = FirstNumber(mi, "Rotation");
            double ours = o.Value.RotationDegrees;
            string verdict = (r is null && x is null) ? (ours == 0 ? "match-absent" : "unreported") : Verdict(ours, r is null ? null : Mod360(r.Value), x is null ? null : Mod360(x.Value), 0.5);
            rows.Add(new Row(name, "fact", "Rotation", ours.ToString(CultureInfo.InvariantCulture) + (o.Value.MirrorHorizontal ? "+mirror" : ""), r?.ToString(CultureInfo.InvariantCulture) ?? "-", x?.ToString(CultureInfo.InvariantCulture) ?? "-", verdict));
        }

        if (f.CaptureTime.HasValue)
        {
            List<DateTime> etDates = [.. DateValues(et, ["CreationDate", "DateTimeOriginal", "CreateDate", "DateUTC", "MediaCreateDate", "TrackCreateDate"])];
            List<DateTime> miDates = [.. DateValues(mi, ["Recorded_Date", "Encoded_Date", "Tagged_Date", "Mastered_Date"])];
            FactCandidate<MediaDateTime> best = f.CaptureTime.Best!;
            DateTime oursUtc = Instant(best.Value);
            bool inEt = etDates.Exists(x => Math.Abs((x - oursUtc).TotalSeconds) < 1 || Math.Abs((x - best.Value.Value).TotalSeconds) < 1);
            bool inMi = miDates.Exists(x => Math.Abs((x - oursUtc).TotalSeconds) < 1 || Math.Abs((x - best.Value.Value).TotalSeconds) < 1);
            bool anyEt = f.CaptureTime.Candidates.Any(cand => etDates.Exists(x => Math.Abs((x - Instant(cand.Value)).TotalSeconds) < 1 || Math.Abs((x - cand.Value.Value).TotalSeconds) < 1));
            rows.Add(new Row(name, "fact", "CaptureTime", $"{best.Value} [{best.Source}] ({f.CaptureTime.Candidates.Count} candidates)", inEt ? "same" : etDates.Count == 0 ? "-" : anyEt ? "other-candidate" : "absent",
                inMi ? "same" : miDates.Count == 0 ? "-" : "absent", inEt || inMi ? "match" : etDates.Count + miDates.Count == 0 ? "unreported" : anyEt ? "match-other-candidate" : "differs"));
        }

        if (f.Location.Best is { } loc)
        {
            List<(double, double)> etLoc = Coordinates(etn, ["GPSCoordinates", "GPSPosition", "LocationISO6709"], ["GPSLatitude", "GPSLongitude"]);
            List<(double, double)> miLoc = Coordinates(mi, ["Recorded_Location", "Location"], []);
            bool Close((double, double) p) => Math.Abs(p.Item1 - loc.Value.Latitude) < 1e-3 && Math.Abs(p.Item2 - loc.Value.Longitude) < 1e-3;
            bool inEt = etLoc.Exists(Close), inMi = miLoc.Exists(Close);
            rows.Add(new Row(name, "fact", "Location", $"{loc.Value.Latitude:0.######} {loc.Value.Longitude:0.######} [{loc.Source}]", etLoc.Count == 0 ? "-" : inEt ? "same" : "absent", miLoc.Count == 0 ? "-" : inMi ? "same" : "absent",
                inEt || inMi ? "match" : etLoc.Count + miLoc.Count == 0 ? "unreported" : "differs"));
        }

        Device(rows, name, "Make", f.Make, et, ["Make", "Manufacturer"]);
        Device(rows, name, "Model", f.Model, et, ["Model"]);
        Device(rows, name, "Software", f.Software, et, ["Software", "CreatorTool", "WritingApp", "MuxingApp", "Encoder"]);
    }

    private static void Device(List<Row> rows, string file, string field, Fact<string> fact, Dictionary<string, List<string>> et, string[] keys)
    {
        List<string> vals = Values(et, keys);
        if (fact.Best is null && vals.Count == 0)
        {
            return;
        }

        string ours = fact.Best?.Value ?? "-";
        bool same = fact.Best is not null && vals.Exists(v => Same(v, ours));
        bool other = fact.Candidates.Any(cd => vals.Exists(v => Same(v, cd.Value)));
        rows.Add(new Row(file, "fact", field, ours, vals.Count == 0 ? "-" : string.Join(" | ", vals.Distinct()), "n/a",
            same ? "match" : fact.Best is null ? "ours-missing" : vals.Count == 0 ? "unreported" : other ? "match-other-candidate" : "differs"));
    }

    private static void Common(List<Row> rows, string file, string field, string? ours, Dictionary<string, List<string>> et, string[] etKeys, Dictionary<string, List<string>> mi, string[] miKeys,
        bool leadingNumber = false, bool presence = false)
    {
        List<string> e = Values(et, etKeys), x = Values(mi, miKeys);
        if (ours is null && e.Count == 0 && x.Count == 0)
        {
            return;
        }

        bool Eq(string a) => presence || (leadingNumber ? LeadingNumber(a) == ours : ours is not null && Same(a, ours));
        bool inEt = ours is not null && e.Exists(Eq), inMi = ours is not null && x.Exists(Eq);
        string verdict = ours is null ? "ours-missing" : inEt || inMi ? ((e.Count > 0 && !inEt) || (x.Count > 0 && !inMi) ? "match-partial" : "match") : e.Count + x.Count == 0 ? "unreported" : "differs";
        rows.Add(new Row(file, "common", field, ours ?? "-", e.Count == 0 ? "-" : string.Join(" | ", e.Distinct()), x.Count == 0 ? "-" : string.Join(" | ", x.Distinct()), verdict));
    }

    private static string LeadingNumber(string s)
    {
        Match m = DigitsRegex().Match(s);
        return m.Success ? m.Value.TrimStart('0').Length == 0 ? "0" : m.Value.TrimStart('0') : s;
    }

    private static string Verdict(double ours, double? a, double? b, double tolerance)
    {
        if (a is null && b is null)
        {
            return "unreported";
        }

        bool ok = (a is null || Math.Abs(a.Value - ours) <= tolerance) && (b is null || Math.Abs(b.Value - ours) <= tolerance);
        return ok ? "match" : "differs";
    }

    private static double Mod360(double v) => ((v % 360) + 360) % 360;

    private static DateTime Instant(MediaDateTime t) => t.Certainty switch
    {
        TimeZoneCertainty.Utc => t.Value,
        TimeZoneCertainty.Offset => t.Value.AddMinutes(-(t.OffsetMinutes ?? 0)),
        _ => t.Value,
    };

    private static string Norm(string s) => s.Trim().Normalize(NormalizationForm.FormC);

    private static bool Same(string a, string b) => Norm(a) == Norm(b) || Norm(a).Replace("\r\n", "\n") == Norm(b).Replace("\r\n", "\n");

    private static List<string> Values(Dictionary<string, List<string>> d, string[] keys)
    {
        List<string> list = [];
        foreach (KeyValuePair<string, List<string>> kv in d)
        {
            string leaf = kv.Key[(kv.Key.LastIndexOf(':') + 1)..];
            if (keys.Contains(leaf, StringComparer.OrdinalIgnoreCase))
            {
                list.AddRange(kv.Value);
            }
        }

        return list;
    }

    private static double? FirstNumber(Dictionary<string, List<string>> d, string key, string? group = null)
    {
        foreach (KeyValuePair<string, List<string>> kv in d)
        {
            string leaf = kv.Key[(kv.Key.LastIndexOf(':') + 1)..];
            bool groupOk = group is null || kv.Key.StartsWith(group, StringComparison.Ordinal);
            if (groupOk && leaf.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value.Count > 0 && double.TryParse(kv.Value[0].Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                return v;
            }
        }

        return null;
    }

    private static IEnumerable<DateTime> DateValues(Dictionary<string, List<string>> d, string[] keys)
    {
        foreach (string v in Values(d, keys))
        {
            Match m = DateRegex().Match(v);
            if (m.Success)
            {
                int[] p = [.. Enumerable.Range(1, 6).Select(i => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture))];
                if (p[0] < 1 || p[1] is < 1 or > 12 || p[2] < 1 || p[2] > DateTime.DaysInMonth(p[0], p[1]) || p[3] > 23 || p[4] > 59 || p[5] > 59)
                {
                    continue; // an unset date such as 0000:00:00 00:00:00
                }

                DateTime dt = new(p[0], p[1], p[2], p[3], p[4], p[5]);
                Match z = OffsetRegex().Match(v[m.Length..]);
                if (z.Success)
                {
                    int sign = z.Groups[1].Value == "-" ? -1 : 1;
                    dt = dt.AddMinutes(-sign * ((int.Parse(z.Groups[2].Value, CultureInfo.InvariantCulture) * 60) + int.Parse(z.Groups[3].Value, CultureInfo.InvariantCulture)));
                }

                yield return dt;
            }
        }
    }

    private static List<(double, double)> Coordinates(Dictionary<string, List<string>> d, string[] pairKeys, string[] latLon)
    {
        List<(double, double)> list = [];
        foreach (string v in Values(d, pairKeys))
        {
            if (GeoLocation.TryParseIso6709(v, out GeoLocation g))
            {
                list.Add((g.Latitude, g.Longitude));
                continue;
            }

            MatchCollection nums = NumberRegex().Matches(v);
            if (nums.Count >= 2 && double.TryParse(nums[0].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) && double.TryParse(nums[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            {
                list.Add((a, b));
            }
        }

        if (latLon.Length == 2 && FirstNumber(d, latLon[0]) is { } lat && FirstNumber(d, latLon[1]) is { } lon)
        {
            list.Add((lat, lon));
        }

        return list;
    }

    private static Dictionary<string, List<string>> ExifToolValues(string file, bool numeric)
    {
        List<string> args = ["-j", "-G1", "-a", "-api", "largefilesupport=1"];
        if (numeric)
        {
            args.Add("-n");
        }

        args.Add(file);
        string json = Oracle.Run("exiftool", [.. args]);
        Dictionary<string, List<string>> d = [];
        using JsonDocument doc = JsonDocument.Parse(json);
        foreach (JsonProperty p in doc.RootElement[0].EnumerateObject())
        {
            if (!d.TryGetValue(p.Name, out List<string>? list))
            {
                d[p.Name] = list = [];
            }

            list.Add(p.Value.ValueKind == JsonValueKind.Array ? string.Join(", ", p.Value.EnumerateArray().Select(e => e.ToString())) : p.Value.ToString());
        }

        return d;
    }

    private static Dictionary<string, List<string>> MediaInfoValues(string file)
    {
        string json = Oracle.Run("mediainfo", "--Output=JSON", file);
        Dictionary<string, List<string>> d = [];
        using JsonDocument doc = JsonDocument.Parse(json);
        foreach (JsonElement track in doc.RootElement.GetProperty("media").GetProperty("track").EnumerateArray())
        {
            string type = track.TryGetProperty("@type", out JsonElement t) ? t.GetString() ?? "" : "";
            Flatten(track, type, d);
        }

        return d;
    }

    private static void Flatten(JsonElement obj, string prefix, Dictionary<string, List<string>> d)
    {
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Object)
            {
                Flatten(p.Value, prefix, d);
                continue;
            }

            string key = prefix + ":" + p.Name;
            if (!d.TryGetValue(key, out List<string>? list))
            {
                d[key] = list = [];
            }

            list.Add(p.Value.ToString());
        }
    }

    [GeneratedRegex(@"(\d{4})[-:](\d\d)[-:](\d\d)[ T](\d\d):(\d\d):(\d\d)")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"^(?:\.\d+)?\s*(?:UTC|Z)?\s*([+-])(\d\d):?(\d\d)")]
    private static partial Regex OffsetRegex();

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^\d+")]
    private static partial Regex DigitsRegex();
}
