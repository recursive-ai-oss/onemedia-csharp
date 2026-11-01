using System.Globalization;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>Prints a per-field tally of the common tag view against ATL.NET for each file (the `atl` command).</summary>
internal static class AtlSummary
{
    public static int Run(IReadOnlyList<string> files)
    {
        int same = 0, fileName = 0, atlEmpty = 0, oursEmpty = 0, differs = 0;
        foreach (string file in files)
        {
            MediaFormatRegistry registry = Readers.Create();
            using IDemuxer d = registry.Open(FileMediaSource.Open(file));
            MediaMetadata m = ((IMetadataProvider)d).ReadMetadata();
            ATL.Track t = new(file);
            CommonTags c = m.Tags;
            foreach ((string field, string? ours, string? theirs) in new (string, string?, string?)[]
            {
                ("Title", c.Title, t.Title),
                ("Artist", c.Artist, t.Artist),
                ("Album", c.Album, t.Album),
                ("AlbumArtist", c.AlbumArtist, t.AlbumArtist),
                ("Genre", c.Genre, t.Genre),
                ("Comment", c.Comment, t.Comment),
                ("Composer", c.Composer, t.Composer),
                ("Copyright", c.Copyright, t.Copyright),
                ("Description", c.Description, t.Description),
                ("Year", Year(c.Date), t.Year is > 0 ? t.Year.Value.ToString(CultureInfo.InvariantCulture) : null),
                ("TrackNumber", Number(c.First(CommonField.TrackNumber)), t.TrackNumber is > 0 ? t.TrackNumber.Value.ToString(CultureInfo.InvariantCulture) : null),
                ("DiscNumber", Number(c.First(CommonField.DiscNumber)), t.DiscNumber is > 0 ? t.DiscNumber.Value.ToString(CultureInfo.InvariantCulture) : null),
            })
            {
                // ATL.NET trims its values; ours keep the stored text, so compare trimmed. ATL also falls back to the file name when a file has no title.
                string o = (ours ?? string.Empty).Trim(), a = (theirs ?? string.Empty).Trim();
                string verdict = o.Length == 0 && a.Length == 0 ? "" : o == a ? "same" : field == "Title" && o.Length == 0 && a == Path.GetFileNameWithoutExtension(file) ? "atl-file-name" : a.Length == 0 ? "atl-empty" : o.Length == 0 ? "ours-empty" : "differs";
                switch (verdict)
                {
                    case "same": same++; break;
                    case "atl-file-name": fileName++; break;
                    case "atl-empty": atlEmpty++; break;
                    case "ours-empty": oursEmpty++; break;
                    case "differs": differs++; break;
                }

                if (verdict is "atl-empty" or "ours-empty" or "differs")
                {
                    Console.WriteLine($"{verdict}\t{Path.GetFileName(file)}\t{field}\tours={Cut(o)}\tatl={Cut(a)}");
                }
            }
        }

        Console.WriteLine($"atl-compare: {files.Count} files: same {same}, atl-file-name-title {fileName}, atl-empty {atlEmpty}, ours-empty {oursEmpty}, differs {differs}");
        return 0;
    }

    private static string Cut(string s) => s.Length > 60 ? s[..60] + "..." : s;

    // The common view keeps a date as written; the library reports the year.
    private static string? Year(string? date) => date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int y) ? y.ToString(CultureInfo.InvariantCulture) : null;

    // "3/12" reads as 3.
    private static string? Number(string? s) => s is null ? null : int.TryParse(s.Split('/')[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n.ToString(CultureInfo.InvariantCulture) : null;
}
