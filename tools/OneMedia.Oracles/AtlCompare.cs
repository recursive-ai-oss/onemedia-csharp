using System.Globalization;
using System.Text;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>
/// Compares the common tag view we read with what ATL.NET (a managed comparator, used as a black box through its public API) reports for the
/// same files. One row per field and file; <c>same</c> when both give the same text, <c>ours-only</c>/<c>atl-only</c> when only one reports the field,
/// <c>differ</c> otherwise. Writes atl-compare.tsv.
/// </summary>
internal static class AtlCompare
{
    public static int Run(IReadOnlyList<string> files, string outDir)
    {
        Directory.CreateDirectory(outDir);
        MediaFormatRegistry registry = Readers.Create();
        StringBuilder tsv = new("file\tfield\tours\tatl\tverdict\n");
        Dictionary<string, int> counts = [];
        foreach (string file in files)
        {
            Dictionary<string, string?> ours;
            try
            {
                using FileMediaSource source = FileMediaSource.Open(file);
                using IDemuxer d = registry.Open(source, new MediaOpenOptions { LeaveOpen = true });
                MediaMetadata m = ((IMetadataProvider)d).ReadMetadata();
                CommonTags t = m.Tags;
                ours = new()
                {
                    ["Title"] = t.Title,
                    ["Artist"] = t.Artist ?? t.Author,
                    ["Album"] = t.Album,
                    ["AlbumArtist"] = t.AlbumArtist,
                    ["Composer"] = t.Composer,
                    ["Genre"] = t.Genre,
                    ["Comment"] = t.Comment ?? t.Description,
                    ["Copyright"] = t.Copyright,
                    ["Year"] = t.Date is { Length: >= 4 } date ? date[..4] : null,
                    ["TrackNumber"] = t.TrackNumber?.ToString(CultureInfo.InvariantCulture),
                    ["TrackTotal"] = t.TrackTotal?.ToString(CultureInfo.InvariantCulture),
                    ["DiscNumber"] = t.DiscNumber?.ToString(CultureInfo.InvariantCulture),
                    ["Pictures"] = t.Pictures.Count > 0 ? t.Pictures.Count.ToString(CultureInfo.InvariantCulture) : null,
                };
            }
            catch (Exception e) when (e is MediaException or IOException)
            {
                tsv.Append(Path.GetFileName(file)).Append("\terror\t").Append(e.Message.Replace('\t', ' ')).Append("\t\terror\n");
                continue;
            }

            Dictionary<string, string?> atl;
            try
            {
                ATL.Track track = new(file);
                atl = new()
                {
                    ["Title"] = track.Title,
                    ["Artist"] = track.Artist,
                    ["Album"] = track.Album,
                    ["AlbumArtist"] = track.AlbumArtist,
                    ["Composer"] = track.Composer,
                    ["Genre"] = track.Genre,
                    ["Comment"] = track.Comment,
                    ["Copyright"] = track.Copyright,
                    ["Year"] = track.Year is > 0 and int year ? year.ToString(CultureInfo.InvariantCulture) : null,
                    ["TrackNumber"] = track.TrackNumber is > 0 ? track.TrackNumber.Value.ToString(CultureInfo.InvariantCulture) : null,
                    ["TrackTotal"] = track.TrackTotal is > 0 ? track.TrackTotal.Value.ToString(CultureInfo.InvariantCulture) : null,
                    ["DiscNumber"] = track.DiscNumber is > 0 ? track.DiscNumber.Value.ToString(CultureInfo.InvariantCulture) : null,
                    ["Pictures"] = track.EmbeddedPictures.Count > 0 ? track.EmbeddedPictures.Count.ToString(CultureInfo.InvariantCulture) : null,
                };
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                atl = [];
                tsv.Append(Path.GetFileName(file)).Append("\tatl-error\t\t").Append(e.GetType().Name).Append("\tatl-error\n");
            }

            foreach ((string field, string? rawOurs) in ours)
            {
                atl.TryGetValue(field, out string? rawAtl);
                string? mine = Normal(rawOurs), theirs = Normal(rawAtl);
                if (field == "Title" && theirs == Path.GetFileNameWithoutExtension(file))
                {
                    theirs = null; // ATL reports the file name when a file has no title
                }

                if (mine is null && theirs is null)
                {
                    continue;
                }

                string verdict = mine is null ? "atl-only" : theirs is null ? "ours-only" : mine == theirs ? "same" : "differ";
                counts[verdict] = counts.GetValueOrDefault(verdict) + 1;
                tsv.Append(Path.GetFileName(file)).Append('\t').Append(field).Append('\t').Append(Clean(mine)).Append('\t').Append(Clean(theirs)).Append('\t').Append(verdict).Append('\n');
            }
        }

        File.WriteAllText(Path.Combine(outDir, "atl-compare.tsv"), tsv.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"files {files.Count}: " + string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{c.Key} {c.Value}")));
        return 0;
    }

    private static string? Normal(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().TrimEnd('\0');

    private static string Clean(string? s) => (s ?? string.Empty).Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
