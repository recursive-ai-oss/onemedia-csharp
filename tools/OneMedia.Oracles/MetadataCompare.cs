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
    /// <summary>One compared field. <c>EtState</c> and <c>MiState</c> say what each oracle did: <c>agree</c> (its value is ours, or one of our candidates for the fact),
    /// <c>disagree</c> (it reports the field with another value) or <c>none</c> (it does not report the field).</summary>
    private sealed record Row(string File, string Kind, string Field, string Ours, string ExifTool, string MediaInfo, string Verdict, string EtState = "none", string MiState = "none");

    private static string State(bool reported, bool agrees) => !reported ? "none" : agrees ? "agree" : "disagree";

    public static int Run(IReadOnlyList<string> files, string outDir)
    {
        Directory.CreateDirectory(outDir);
        List<Row> rows = [];
        MediaFormatRegistry registry = new();
        registry.Add(new IsoBmffFormat());
        registry.Add(new MatroskaFormat());
        registry.Add(new RecursiveAI.OneMedia.Asf.AsfFormat());
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
        w.WriteLine("file\tkind\tfield\tours\texiftool\tmediainfo\tverdict\tet_state\tmi_state");
        foreach (Row r in rows)
        {
            w.WriteLine(string.Join('\t', r.File, r.Kind, r.Field, Clean(r.Ours), Clean(r.ExifTool), Clean(r.MediaInfo), r.Verdict, r.EtState, r.MiState));
        }

        Console.WriteLine($"files {files.Count}");
        foreach (IGrouping<(string, string), Row> g in rows.GroupBy(r => (r.Kind, r.Verdict)).OrderBy(g => g.Key))
        {
            Console.WriteLine($"{g.Key.Item1,-8} {g.Key.Item2,-22} {g.Count()}");
        }

        return 0;
    }

    /// <summary>Writes docs/tag-mapping.md (the intro text and the rendered table) into the directory given.</summary>
    public static int WriteMappingDoc(string dir)
    {
        const string intro = """
            # Tag mapping

            How the common tag view picks values from the native tag systems. The table is the one the code reads (`TagMappingTable.Rows`); this page is
            generated from it, and a test fails when they differ.

            - Rows of one field are tried in table order, and within a row the keys in the order listed. The first value is the field's value;
              the others stay reachable through `CommonTags.Values`.
            - Native keys are spelled as the system spells them: four-character atom codes for `ilst` and `udta` (`©nam`), reverse-DNS names for
              key-table metadata (`com.apple.quicktime.title`), `namespace#property` for XMP, upper-case names for Matroska (nested SimpleTags as
              `PARENT/CHILD`). Matroska names are matched case-insensitively.
            - Matroska: only tags that target the file (no track, edition, chapter or attachment UID) feed the view, plus the tags of the only
              track of a single-track file (except `ENCODER`, which names the stream's encoder). Within a key the lowest target level of 30 or above
              comes first, then levels below 30; tags with no language or `und` come before language-specific ones; tags of the file itself
              come before tags of the only track.
            - 3GPP boxes and QuickTime user-data atoms share four-character codes with different layouts; the reader tells them apart as described in `docs/tags.md`.
            - Sources: the MusicBrainz Picard tag-mapping table (CC0), the ExifTool QuickTime tag documentation (read as tables of facts), the Matroska
              tag-name list, Apple's QuickTime File Format specification and 3GPP TS 26.244 §8.


            """;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tag-mapping.md"), intro + TagMappingTable.RenderMarkdown(), new UTF8Encoding(false));
        return 0;
    }

    /// <summary>Prints every native tag and the common view of each file, for looking at a difference.</summary>
    public static int Dump(IReadOnlyList<string> files)
    {
        MediaFormatRegistry registry = new();
        registry.Add(new IsoBmffFormat());
        registry.Add(new MatroskaFormat());
        registry.Add(new RecursiveAI.OneMedia.Asf.AsfFormat());
        foreach (string file in files)
        {
            using IDemuxer demuxer = registry.Open(FileMediaSource.Open(file));
            MediaMetadata m = ((IMetadataProvider)demuxer).ReadMetadata();
            Console.WriteLine(file);
            foreach (NativeTag t in m.NativeTags)
            {
                string text = t.Text ?? "";
                Console.WriteLine($"  {t.System}:{t.Key} track={t.TrackId} level={t.TargetTypeValue} lang={t.Language} kind={t.Kind} = {(text.Length > 60 ? text[..60] : text)}");
            }

            foreach (CommonField f in Enum.GetValues<CommonField>())
            {
                if (m.Tags.First(f) is { } v)
                {
                    Console.WriteLine($"  common {f} = {v}");
                }
            }

            foreach (Chapter ch in m.Chapters)
            {
                Console.WriteLine($"  chapter {ch.Start} {ch.Title}");
            }

            foreach (MediaDefect d in m.Defects)
            {
                Console.WriteLine($"  defect {d.Code} @{d.Offset}: {d.Message}");
            }
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
        foreach (string k in mi.Keys.ToList())
        {
            // MediaInfo joins the distinct values of a repeated field with " / ".
            foreach (string v in mi[k].ToList())
            {
                if (v.Contains(" / ", StringComparison.Ordinal))
                {
                    mi[k].AddRange(v.Split(" / "));
                }
            }
        }

        HashSet<string> miAll = new(mi.Values.SelectMany(v => v).Select(Norm));
        foreach (NativeTag t in m.NativeTags)
        {
            if (t.Kind is not (TagValueKind.Text or TagValueKind.Number) || t.Text is not { } text)
            {
                continue;
            }

            // Positions are compared by the Location fact, as numbers: the oracles print degrees, minutes and seconds or decimals.
            if (t.Key.EndsWith("#GPSLatitude", StringComparison.Ordinal) || t.Key.EndsWith("#GPSLongitude", StringComparison.Ordinal) || t is { System: TagSystems.ThreeGpp, Key: "loci" })
            {
                continue;
            }

            string[] groups = t.System switch
            {
                TagSystems.ITunes => ["ItemList"],
                TagSystems.QuickTimeKeys => ["Keys", "ItemList"],
                TagSystems.QuickTimeUserData or TagSystems.ThreeGpp => ["UserData"],
                TagSystems.Xmp => ["XMP-x", "XMP-xmp", "XMP-dc", "XMP-exif", "XMP-tiff", "XMP-photoshop"],
                TagSystems.MatroskaInfo => ["Info", "Matroska"],
                TagSystems.Asf => ["ASF"],
                _ => t.TrackId > 0 ? ["Track"] : ["Matroska"],
            };
            List<string> etVals = [.. et.Where(kv => groups.Any(g => kv.Key.StartsWith(g, StringComparison.Ordinal))).SelectMany(kv => kv.Value)];
            if (t.Key is "com.apple.quicktime.location.ISO6709" or "©xyz")
            {
                // The position is compared as numbers: the oracles print it in their own formats.
                bool parsed = GeoLocation.TryParseIso6709(text, out GeoLocation g);
                bool e = parsed && Coordinates(etn, ["GPSCoordinates", "GPSPosition", "LocationISO6709"], ["GPSLatitude", "GPSLongitude"]).Exists(p => Math.Abs(p.Item1 - g.Latitude) < 1e-3 && Math.Abs(p.Item2 - g.Longitude) < 1e-3);
                bool x = parsed && Coordinates(mi, ["Recorded_Location", "Location"], []).Exists(p => Math.Abs(p.Item1 - g.Latitude) < 1e-3 && Math.Abs(p.Item2 - g.Longitude) < 1e-3);
                rows.Add(new Row(name, "native", $"{t.System}:{t.Key}", text, e ? "same" : "absent", x ? "same" : "absent", e ? (x ? "match" : "match-et") : x ? "match-mi" : parsed ? "differs" : "unparsed",
                    State(parsed && Coordinates(etn, ["GPSCoordinates", "GPSPosition", "LocationISO6709"], ["GPSLatitude", "GPSLongitude"]).Count > 0, e),
                    State(parsed && Coordinates(mi, ["Recorded_Location", "Location"], []).Count > 0, x)));
                continue;
            }

            bool inEt = etVals.Exists(v => Same(v, text));
            bool inMi = miAll.Contains(Norm(text)) || mi.Values.SelectMany(v => v).Any(v => Same(v, text));
            string verdict = inEt ? (inMi ? "match" : "match-et") : etVals.Count == 0 ? (inMi ? "match-mi" : "unreported") : (inMi ? "match-mi" : "differs");
            // ExifTool keeps one value per tag name and group: a name repeated at another level, or by a later track (ExifTool files every track's tags under one Track group), shows only once.
            if (verdict == "differs" && t.System == TagSystems.Matroska && (t.TrackId > 1 || m.NativeTags.Any(o => o != t && o.System == t.System && o.Key == t.Key && o.TrackId == t.TrackId)))
            {
                verdict = "explained-et-collapse";
            }

            rows.Add(new Row(name, "native", $"{t.System}:{t.Key}", text, inEt ? "same" : etVals.Count == 0 ? "-" : "absent", inMi ? "same" : "absent", verdict, State(etVals.Count > 0, inEt), State(inMi, inMi)));
        }

        // Container track properties ("TrackNumber", "Encoder" of a track) are not tags of the movie.
        Dictionary<string, List<string>> etMovie = et.Where(kv => !kv.Key.StartsWith("Track", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value);

        // common view (MediaInfo: the General section only; its other sections describe single streams)
        Dictionary<string, List<string>> miGeneral = mi.Where(kv => kv.Key.StartsWith("General:", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value);
        CommonTags c = m.Tags;
        MediaFacts f0 = m.Facts;
        Common(rows, name, "Title", c.Title, etMovie, ["Title"], miGeneral, ["Title", "Movie"]);
        // QuickTime names the writer "author"; MediaInfo calls it Performer, like the artist.
        Common(rows, name, "Artist", c.Artist ?? c.Author, etMovie, ["Artist", "Performer", "Author"], miGeneral, ["Performer"]);
        Common(rows, name, "Album", c.Album, etMovie, ["Album"], miGeneral, ["Album"]);
        Common(rows, name, "Genre", c.Genre, etMovie, ["Genre"], miGeneral, ["Genre"]);
        // MediaInfo calls an ASF file's Content Description description "Comment"; ExifTool and we call it Description.
        bool asf = m.NativeTags.Any(t => t.System == TagSystems.Asf);
        Common(rows, name, "Comment", c.Comment, etMovie, ["Comment"], miGeneral, asf ? [] : ["Comment"]);
        Common(rows, name, "Description", c.Description, etMovie, ["Description"], miGeneral, asf ? ["Description", "Comment"] : ["Description"]);
        Common(rows, name, "Copyright", c.Copyright, etMovie, ["Copyright"], miGeneral, ["Copyright"]);
        Common(rows, name, "Composer", c.Composer, etMovie, ["Composer"], miGeneral, ["Composer"]);
        // MediaInfo's "Writing application" also carries the QuickTime software atom, which is our Software fact rather than the Encoder field.
        Common(rows, name, "Encoder", c.Encoder ?? (f0.Software.Best is { } sw && sw.Source.EndsWith("©swr", StringComparison.Ordinal) ? sw.Value : null), etMovie, ["Encoder", "Software", "WritingApp"], miGeneral, ["Encoded_Application", "Writing_Library"]);
        Common(rows, name, "TrackNumber", c.TrackNumber?.ToString(CultureInfo.InvariantCulture), etMovie, ["TrackNumber"], miGeneral, ["Track_Position"], leadingNumber: true);
        Common(rows, name, "Cover", c.Pictures.Count > 0 ? "yes" : null, etMovie, ["CoverArt", "Picture"], miGeneral, ["Cover"], presence: true);

        // facts
        MediaFacts f = m.Facts;
        if (f.Duration.Best is { } d)
        {
            double ours = (double)d.Value.Ticks / d.Value.Timescale;
            double? e = FirstNumber(etn, "Duration"), x = FirstNumber(mi, "Duration");
            rows.Add(new Row(name, "fact", "Duration", ours.ToString("0.000", CultureInfo.InvariantCulture), e?.ToString("0.000", CultureInfo.InvariantCulture) ?? "-", x is null ? "-" : x.Value.ToString("0.000", CultureInfo.InvariantCulture),
                DurationVerdict(ours, e, x), State(e is not null, e is not null && Math.Abs(e.Value - ours) <= 0.002), State(x is not null, x is not null && Math.Abs(x.Value - ours) <= 0.002)));
        }

        if (f.FrameSize.Best is { } fs)
        {
            double? w = FirstNumber(mi, "Width", "Video"), h = FirstNumber(mi, "Height", "Video");
            string miv = w is null ? "-" : $"{w}x{h}";
            string ours = $"{fs.Value.CodedWidth}x{fs.Value.CodedHeight}";
            rows.Add(new Row(name, "fact", "FrameSize.Coded", ours, "-", miv, miv == "-" ? "unreported" : miv == ours ? "match" : "differs", "none", State(miv != "-", miv == ours)));
        }

        if (f.Orientation.Best is { } o)
        {
            double? r = FirstNumber(etn, "Rotation"), x = FirstNumber(mi, "Rotation");
            double ours = o.Value.RotationDegrees;
            string verdict = (r is null && x is null) ? (ours == 0 ? "match-absent" : "unreported") : Verdict(ours, r is null ? null : Mod360(r.Value), x is null ? null : Mod360(x.Value), 0.5);
            rows.Add(new Row(name, "fact", "Rotation", ours.ToString(CultureInfo.InvariantCulture) + (o.Value.MirrorHorizontal ? "+mirror" : ""), r?.ToString(CultureInfo.InvariantCulture) ?? "-", x?.ToString(CultureInfo.InvariantCulture) ?? "-", verdict, State(r is not null, r is not null && Math.Abs(Mod360(r.Value) - ours) <= 0.5), State(x is not null, x is not null && Math.Abs(Mod360(x.Value) - ours) <= 0.5)));
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
                inMi ? "same" : miDates.Count == 0 ? "-" : "absent", inEt || inMi ? "match" : etDates.Count + miDates.Count == 0 ? "unreported" : anyEt ? "match-other-candidate" : "differs",
                State(etDates.Count > 0, inEt || anyEt), State(miDates.Count > 0, inMi)));
        }

        if (f.Location.Best is { } loc)
        {
            List<(double, double)> etLoc = Coordinates(etn, ["GPSCoordinates", "GPSPosition", "LocationISO6709"], ["GPSLatitude", "GPSLongitude"]);
            List<(double, double)> miLoc = Coordinates(mi, ["Recorded_Location", "Location"], []);
            bool Close((double, double) p) => Math.Abs(p.Item1 - loc.Value.Latitude) < 1e-3 && Math.Abs(p.Item2 - loc.Value.Longitude) < 1e-3;
            bool inEt = etLoc.Exists(Close), inMi = miLoc.Exists(Close);
            rows.Add(new Row(name, "fact", "Location", $"{loc.Value.Latitude:0.######} {loc.Value.Longitude:0.######} [{loc.Source}]", etLoc.Count == 0 ? "-" : inEt ? "same" : "absent", miLoc.Count == 0 ? "-" : inMi ? "same" : "absent",
                inEt || inMi ? "match" : etLoc.Count + miLoc.Count == 0 ? "unreported" : "differs", State(etLoc.Count > 0, inEt), State(miLoc.Count > 0, inMi)));
        }

        Device(rows, name, "Make", f.Make, etMovie, ["Make", "Manufacturer", "AndroidMake"]);
        Device(rows, name, "Model", f.Model, etMovie, ["Model", "AndroidModel"]);
        Device(rows, name, "Software", f.Software, etMovie, ["Software", "SoftwareVersion", "AndroidVersion", "CreatorTool", "WritingApp", "MuxingApp"]);
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
            same ? "match" : fact.Best is null ? "ours-missing" : vals.Count == 0 ? "unreported" : other ? "match-other-candidate" : "differs", State(vals.Count > 0, same || other)));
    }

    private static void Common(List<Row> rows, string file, string field, string? ours, Dictionary<string, List<string>> et, string[] etKeys, Dictionary<string, List<string>> mi, string[] miKeys,
        bool leadingNumber = false, bool presence = false)
    {
        // An attribute written with no text is the same as no attribute (ExifTool prints it as an empty string).
        List<string> e = Values(et, etKeys).FindAll(v => v.Length > 0), x = Values(mi, miKeys).FindAll(v => v.Length > 0);
        if (ours is null && e.Count == 0 && x.Count == 0)
        {
            return;
        }

        bool Eq(string a) => presence || (leadingNumber ? LeadingNumber(a) == ours : ours is not null && Same(a, ours));
        bool inEt = ours is not null && e.Exists(Eq), inMi = ours is not null && x.Exists(Eq);
        string verdict = ours is null ? "ours-missing" : inEt || inMi ? ((e.Count > 0 && !inEt) || (x.Count > 0 && !inMi) ? "match-partial" : "match") : e.Count + x.Count == 0 ? "unreported" : "differs";
        rows.Add(new Row(file, "common", field, ours ?? "-", e.Count == 0 ? "-" : string.Join(" | ", e.Distinct()), x.Count == 0 ? "-" : string.Join(" | ", x.Distinct()), verdict, State(e.Count > 0 && ours is not null, inEt), State(x.Count > 0 && ours is not null, inMi)));
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

    // MediaInfo derives the duration from the streams where ExifTool and we report the movie header's, so an ExifTool match with another MediaInfo value is explained.
    private static string DurationVerdict(double ours, double? et, double? mi)
    {
        string v = Verdict(ours, et, mi, 0.002);
        return v == "differs" && et is { } e && Math.Abs(e - ours) <= 0.002 && mi is not null ? "match-et-mi-stream-based" : v;
    }

    private static double Mod360(double v) => ((v % 360) + 360) % 360;

    private static DateTime Instant(MediaDateTime t) => t.Certainty switch
    {
        TimeZoneCertainty.Utc => t.Value,
        TimeZoneCertainty.Offset => t.Value.AddMinutes(-(t.OffsetMinutes ?? 0)),
        _ => t.Value,
    };

    private static string Norm(string s) => s.Trim().Normalize(NormalizationForm.FormC);

    private static bool Same(string a, string b)
    {
        if (Norm(a) == Norm(b) || Norm(a).Replace("\r\n", "\n") == Norm(b).Replace("\r\n", "\n"))
        {
            return true;
        }

        // Dates are compared as instants (or as wall-clock readings when one side drops the zone): the oracles reformat them.
        return TryDate(a, out DateTime ia, out DateTime wa) && TryDate(b, out DateTime ib, out DateTime wb) && (Math.Abs((ia - ib).TotalSeconds) < 1 || Math.Abs((wa - wb).TotalSeconds) < 1);
    }

    private static bool TryDate(string v, out DateTime instant, out DateTime wall)
    {
        instant = wall = default;
        foreach (DateTime d in DateValues(new Dictionary<string, List<string>> { ["x"] = [v] }, ["x"]))
        {
            instant = d;
            Match m = DateRegex().Match(v);
            int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
            wall = new DateTime(G(1), G(2), G(3), G(4), G(5), G(6));
            return true;
        }

        return false;
    }

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
                int[] p = [.. Enumerable.Range(1, 6).Select(i => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0)];
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

            // MediaInfo writes hemispheres as letters ("37.7749°N 122.4194°W").
            Match hemi = HemisphereRegex().Match(v);
            if (hemi.Success)
            {
                double la = double.Parse(hemi.Groups[1].Value, CultureInfo.InvariantCulture) * (hemi.Groups[2].Value == "S" ? -1 : 1);
                double lo = double.Parse(hemi.Groups[3].Value, CultureInfo.InvariantCulture) * (hemi.Groups[4].Value == "W" ? -1 : 1);
                list.Add((la, lo));
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

    [GeneratedRegex(@"(\d{4})[-:](\d\d)[-:](\d\d)(?:[ T](\d\d):(\d\d):(\d\d))?")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"^(?:\.\d+)?\s*(?:UTC|Z)?\s*([+-])(\d\d):?(\d\d)")]
    private static partial Regex OffsetRegex();

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\u00b0?\s*([NS])\s+(\d+(?:\.\d+)?)\u00b0?\s*([EW])")]
    private static partial Regex HemisphereRegex();

    [GeneratedRegex(@"^\d+")]
    private static partial Regex DigitsRegex();
}
