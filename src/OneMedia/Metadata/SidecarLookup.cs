namespace RecursiveAI.OneMedia.Metadata;

/// <summary>
/// The rule for finding sidecars beside a media file, used only by <c>MediaFile.AddSidecarsBesideFile</c>. In the file's own directory, with names compared
/// ignoring case: <c>stem.xmp</c> and <c>name.ext.xmp</c> (XMP), <c>stemM01.XML</c> (Sony clip metadata, as written beside XAVC-S clips) and <c>stem.srt</c> (DJI).
/// Nothing is looked for in other directories, and a match is still checked by content before it counts.
/// </summary>
public static class SidecarLookup
{
    /// <summary>Lists the candidate sidecar files that exist beside a media file.</summary>
    /// <param name="mediaPath">The media file.</param>
    /// <returns>Full paths in rule order; empty when none exist or the directory cannot be listed.</returns>
    public static IReadOnlyList<string> Find(string mediaPath)
    {
        ArgumentNullException.ThrowIfNull(mediaPath);
        string full = Path.GetFullPath(mediaPath);
        string? dir = Path.GetDirectoryName(full);
        if (dir is null)
        {
            return [];
        }

        string name = Path.GetFileName(full);
        string stem = Path.GetFileNameWithoutExtension(full);
        string[] wanted = [stem + ".xmp", name + ".xmp", stem + "M01.XML", stem + ".srt"];
        List<string> found = [];
        try
        {
            foreach (string w in wanted)
            {
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    if (string.Equals(Path.GetFileName(f), w, StringComparison.OrdinalIgnoreCase) && !found.Contains(f))
                    {
                        found.Add(f);
                    }
                }
            }
        }
        catch (IOException)
        {
            return found;
        }
        catch (UnauthorizedAccessException)
        {
            return found;
        }

        return found;
    }
}
