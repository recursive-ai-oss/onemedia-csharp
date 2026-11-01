using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RecursiveAI.OneMedia.Tags;
using Atl = ATL;

namespace RecursiveAI.OneMedia.Bench;

/// <summary>
/// Compares the common tag view of audio files with ExifTool (run as a binary), ATL.NET and TagLib# (NuGet comparators) field by field.
/// A comparator "agrees" when one of the values it reports for the field is ours; "none" when it reports nothing; "disagrees" otherwise.
/// ExifTool is asked for every ID3/APE/Lyrics3 group it knows (<c>-G1 -a</c>), so a value from any tag in the file counts.
/// </summary>
internal static class TagCompare
{
    private static readonly (string Field, string[] Et)[] Fields =
    [
        ("Title", ["Title"]),
        ("Artist", ["Artist"]),
        ("Album", ["Album"]),
        ("AlbumArtist", ["Band", "AlbumArtist"]),
        ("Composer", ["Composer"]),
        ("Genre", ["Genre"]),
        ("Date", ["Year", "RecordingTime", "ReleaseTime", "OriginalReleaseTime", "Date"]),
        ("Comment", ["Comment"]),
        ("Track", ["Track"]),
        ("Disc", ["PartOfSet", "Disc"]),
        ("Lyrics", ["Lyrics", "Unsynchronised-lyrics", "UnsynchronisedLyricsText"]),
    ];

    public static int Run(IReadOnlyList<string> files, string exiftool, string outFile)
    {
        List<string> lines = ["file\tfield\tours\texiftool\tatl\ttaglib\tet\tatl_state\ttaglib_state"];
        Dictionary<(string, string), int> counts = [];
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            Dictionary<string, List<string>> et = ExifToolValues(exiftool, file);
            Dictionary<string, string> atl = AtlValues(file);
            Dictionary<string, string> tl = TagLibValues(file);
            using MediaFile media = Media.Open(file);
            foreach ((string field, string[] etKeys) in Fields)
            {
                string? ours = Ours(media.Tags, field);
                List<string> etVals = [.. et.Where(kv => etKeys.Contains(kv.Key[(kv.Key.IndexOf(':') + 1)..], StringComparer.Ordinal) && IsTagGroup(kv.Key)).SelectMany(kv => kv.Value)];
                atl.TryGetValue(field, out string? a);
                tl.TryGetValue(field, out string? t);
                if (ours is null && etVals.Count == 0 && string.IsNullOrEmpty(a) && string.IsNullOrEmpty(t))
                {
                    continue;
                }

                string etState = State(ours, etVals, field);
                string atlState = State(ours, string.IsNullOrEmpty(a) ? [] : [a], field);
                string tlState = State(ours, string.IsNullOrEmpty(t) ? [] : [t], field);
                lines.Add(string.Join('\t', name, field, Clean(ours), Clean(string.Join(" | ", etVals)), Clean(a), Clean(t), etState, atlState, tlState));
                foreach ((string who, string st) in new[] { ("exiftool", etState), ("atl", atlState), ("taglib", tlState) })
                {
                    counts[(who, st)] = counts.GetValueOrDefault((who, st)) + 1;
                }
            }
        }

        File.WriteAllLines(outFile, lines);
        Console.WriteLine($"files {files.Count}, rows {lines.Count - 1}");
        foreach (string who in new[] { "exiftool", "atl", "taglib" })
        {
            Console.WriteLine($"{who,-9} agree {counts.GetValueOrDefault((who, "agree"))}, disagree {counts.GetValueOrDefault((who, "disagree"))}, none {counts.GetValueOrDefault((who, "none"))}");
        }

        foreach (string l in lines.Skip(1).Where(l => l.Split('\t') is { Length: > 8 } p && (p[6] == "disagree" || p[7] == "disagree" || p[8] == "disagree")).Take(60))
        {
            Console.WriteLine("  " + l);
        }

        return 0;
    }

    private static string Clean(string? s) => (s ?? "-").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static bool IsTagGroup(string key) => key.StartsWith("ID3", StringComparison.Ordinal) || key.StartsWith("APE", StringComparison.Ordinal) || key.StartsWith("Lyrics3", StringComparison.Ordinal);

    private static string? Ours(CommonTags tags, string field) => field switch
    {
        "Title" => tags.Title,
        "Artist" => tags.Artist,
        "Album" => tags.Album,
        "AlbumArtist" => tags.AlbumArtist,
        "Composer" => tags.Composer,
        "Genre" => tags.Genre,
        "Date" => tags.Date,
        "Comment" => tags.Comment,
        "Track" => tags.TrackNumber?.ToString(CultureInfo.InvariantCulture),
        "Disc" => tags.DiscNumber?.ToString(CultureInfo.InvariantCulture),
        _ => tags.Lyrics,
    };

    private static string State(string? ours, List<string> theirs, string field)
    {
        if (theirs.Count == 0)
        {
            return "none";
        }

        if (ours is null)
        {
            return "disagree";
        }

        foreach (string v in theirs)
        {
            if (Same(ours, v, field))
            {
                return "agree";
            }
        }

        return "disagree";
    }

    private static bool Same(string ours, string theirs, string field)
    {
        string a = Norm(ours), b = Norm(theirs);
        if (a == b)
        {
            return true;
        }

        if (field is "Track" or "Disc")
        {
            // "3/12" and "3" are the same number.
            return LeadingNumber(a) == LeadingNumber(b);
        }

        if (field == "Date")
        {
            return a.Length >= 4 && b.Length >= 4 && a[..4] == b[..4] && (a.Length <= 4 || b.Length <= 4 || a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal));
        }

        if (field == "Genre")
        {
            return a.Replace(" ", "", StringComparison.Ordinal) == b.Replace(" ", "", StringComparison.Ordinal) || b.Split(';', ',').Select(Norm).Contains(a) || a.Split(';').Select(Norm).Any(x => b.Contains(x, StringComparison.Ordinal));
        }

        // Multi-valued text: the oracles join with ", ", "/" or ";".
        string[] ours1 = a.Split(';', '/').Select(Norm).Where(x => x.Length > 0).ToArray();
        return ours1.Length > 1 && ours1.All(x => b.Contains(x, StringComparison.Ordinal));
    }

    private static string LeadingNumber(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsDigit(s[i]))
        {
            i++;
        }

        return i == 0 ? s : s[..i].TrimStart('0');
    }

    private static string Norm(string s) => s.Trim().Trim('\0').Normalize(NormalizationForm.FormC);

    private static Dictionary<string, List<string>> ExifToolValues(string exiftool, string file)
    {
        ProcessStartInfo psi = new(exiftool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-j", "-G1", "-a", "-api", "largefilesupport=1", file })
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string json = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        err.Wait();
        Dictionary<string, List<string>> d = [];
        using JsonDocument doc = JsonDocument.Parse(json);
        foreach (JsonProperty prop in doc.RootElement[0].EnumerateObject())
        {
            if (!d.TryGetValue(prop.Name, out List<string>? list))
            {
                d[prop.Name] = list = [];
            }

            list.Add(prop.Value.ValueKind == JsonValueKind.Array ? string.Join(", ", prop.Value.EnumerateArray().Select(e => e.ToString())) : prop.Value.ToString());
        }

        return d;
    }

    private static Dictionary<string, string> AtlValues(string file)
    {
        Dictionary<string, string> d = [];
        try
        {
            Atl.Track t = new(file);
            d["Title"] = t.Title ?? "";
            d["Artist"] = t.Artist ?? "";
            d["Album"] = t.Album ?? "";
            d["AlbumArtist"] = t.AlbumArtist ?? "";
            d["Composer"] = t.Composer ?? "";
            d["Genre"] = t.Genre ?? "";
            string year = Convert.ToString(t.Year, CultureInfo.InvariantCulture) ?? "";
            d["Date"] = year == "0" ? "" : year;
            d["Comment"] = t.Comment ?? "";
            d["Track"] = t.TrackNumber is > 0 ? t.TrackNumber.Value.ToString(CultureInfo.InvariantCulture) : "";
            d["Disc"] = t.DiscNumber is > 0 ? t.DiscNumber.Value.ToString(CultureInfo.InvariantCulture) : "";
            d["Lyrics"] = string.Join("; ", t.Lyrics.Select(l => l.UnsynchronizedLyrics));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            d["error"] = e.Message;
        }

        return d;
    }

    private static Dictionary<string, string> TagLibValues(string file)
    {
        Dictionary<string, string> d = [];
        try
        {
            using TagLib.File f = TagLib.File.Create(file);
            TagLib.Tag t = f.Tag;
            d["Title"] = t.Title ?? "";
            d["Artist"] = string.Join("; ", t.Performers ?? []);
            d["Album"] = t.Album ?? "";
            d["AlbumArtist"] = string.Join("; ", t.AlbumArtists ?? []);
            d["Composer"] = string.Join("; ", t.Composers ?? []);
            d["Genre"] = string.Join("; ", t.Genres ?? []);
            d["Date"] = t.Year > 0 ? t.Year.ToString(CultureInfo.InvariantCulture) : "";
            d["Comment"] = t.Comment ?? "";
            d["Track"] = t.Track > 0 ? t.Track.ToString(CultureInfo.InvariantCulture) : "";
            d["Disc"] = t.Disc > 0 ? t.Disc.ToString(CultureInfo.InvariantCulture) : "";
            d["Lyrics"] = t.Lyrics ?? "";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            d["error"] = e.Message;
        }

        return d;
    }
}
