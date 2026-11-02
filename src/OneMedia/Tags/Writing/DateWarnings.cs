using System.Linq;
using System.Globalization;
using RecursiveAI.OneMedia.Metadata;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>
/// Finds the date tags a common <see cref="CommonField.Date"/> write leaves in place. The common field sets one key per system; files usually
/// carry other dates as well (a QuickTime <c>creationdate</c>, a Matroska <c>DATE_RECORDED</c>, an ID3 <c>TDOR</c>, the movie header's creation
/// time), and tools such as MediaInfo or ExifTool may show one of those instead of the date just written. The report names each of them.
/// </summary>
internal static class DateWarnings
{
    // Tags that hold a date or a year, per system. The list is of facts about the formats' tag names, not of what a write touches.
    private static readonly (string System, string[] Keys)[] DateKeys =
    [
        (TagSystems.ITunes, ["\u00a9day"]),
        (TagSystems.QuickTimeUserData, ["\u00a9day"]),
        (TagSystems.QuickTimeKeys, ["com.apple.quicktime.creationdate", "com.apple.quicktime.creation_time", "creation_time", "year"]),
        (TagSystems.ThreeGpp, ["yrrc"]),
        (TagSystems.Matroska, ["DATE_RECORDED", "DATE_ENCODED", "DATE_RELEASED", "DATE_TAGGED", "DATE_DIGITIZED", "DATE_PURCHASED", "DATE"]),
        (TagSystems.MatroskaInfo, ["DateUTC"]),
        (TagSystems.Id3v2, ["TDRC", "TYER", "TDAT", "TIME", "TDOR", "TORY", "TDRL", "TDEN", "TDTG", "TRDA"]),
        (TagSystems.Id3v1, ["year"]),
        (TagSystems.Ape, ["Year", "Date"]),
        (TagSystems.VorbisComment, ["DATE", "ORIGINALDATE", "YEAR"]),
        (TagSystems.Asf, ["WM/Year", "WM/OriginalReleaseTime", "WM/OriginalReleaseYear", "WM/MediaOriginalBroadcastDateTime"]),
        (TagSystems.RiffInfo, ["ICRD", "IDIT"]),
        (TagSystems.Bext, ["OriginationDate", "OriginationTime"]),
        (TagSystems.CafInfo, ["recorded date", "year"]),
        (TagSystems.Flv, ["creationdate", "date"]),
    ];

    /// <summary>Lists the date tags of the file that the planned write does not change.</summary>
    public static IReadOnlyList<string> Find(IMediaSource source, TagEdits edits, TagWriteReport report, MediaLimits limits)
    {
        if (edits.FieldChange(CommonField.Date) is null)
        {
            return [];
        }

        // Keys the Date write itself set or removed, per system; those are not "left".
        HashSet<(string, string)> handled = new();
        foreach (SystemWriteReport s in report.Systems)
        {
            string system = s.System == TagSystems.Id3Chunk ? TagSystems.Id3v2 : s.System;
            if (s.Written.Contains(CommonField.Date))
            {
                handled.Add((system, KeyValueOps.PrimaryKey(CommonField.Date, system) ?? string.Empty));
                if (system == TagSystems.Id3v2)
                {
                    // ID3v2.3 stores the year in TYER and the day and month in TDAT and TIME; a write replaces all of them.
                    foreach (string k in new[] { "TDRC", "TYER", "TDAT", "TIME" })
                    {
                        handled.Add((system, k));
                    }
                }
            }
            else if (s.Removed.Contains(CommonField.Date))
            {
                foreach (string k in KeyValueOps.RowKeys(CommonField.Date, system))
                {
                    handled.Add((system, k));
                }

                if (system == TagSystems.Id3v2)
                {
                    handled.Add((system, "TDAT"));
                    handled.Add((system, "TIME"));
                }
            }
        }

        List<string> warnings = [];
        try
        {
            using MediaFile file = MediaFile.Open(source, null, new MediaOpenOptions { Limits = limits, LeaveOpen = true });
            HashSet<(string, string)> seen = new();
            foreach (NativeTag t in file.Metadata.NativeTags)
            {
                string system = t.System == TagSystems.Id3Chunk ? TagSystems.Id3v2 : t.System;
                if (t.Kind is not (TagValueKind.Text or TagValueKind.Number) || !IsDateKey(system, t.Key) || IsHandled(handled, system, t.Key) || !seen.Add((system, t.Key)))
                {
                    continue;
                }

                string value = (t.Text ?? string.Empty).Trim();
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Date: '{system}:{t.Key}' still reads \"{value}\"; tools that prefer it (MediaInfo, ExifTool) may show it instead of the date written. It was left as it is."));
            }

            foreach (FactCandidate<MediaDateTime> c in file.CaptureTime.Candidates)
            {
                if (c.Source == "mvhd")
                {
                    warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Date: the movie header's creation time ('mvhd', {c.RawValue}) was left as it is; players and file managers may show it instead of the date written."));
                }
            }
        }
        catch (MediaException)
        {
            // A file the reader cannot open has no date tags to report.
        }

        return warnings;
    }

    private static bool IsHandled(HashSet<(string, string)> handled, string system, string key)
    {
        foreach ((string s, string k) in handled)
        {
            if (s == system && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDateKey(string system, string key)
    {
        foreach ((string s, string[] keys) in DateKeys)
        {
            if (s == system)
            {
                foreach (string k in keys)
                {
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
