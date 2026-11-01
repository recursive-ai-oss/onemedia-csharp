// crossread FILE: prints what ATL.NET and TagLib# read for title, artist, comment and the number of pictures, tab separated, for a file the
// tag-write check wrote (title "Edited Title ü", artist "Edited Artist", comment "An edited comment", one picture). A reader that does not support the
// format, or throws, is reported as such; "ok" per field means it read exactly the written value. Comparators only, run black-box, never shipped.
using System;

namespace RecursiveAI.OneMedia.Bench;

internal static class CrossRead
{
    private const string Title = "Edited Title \u00fc";
    private const string Artist = "Edited Artist";
    private const string Comment = "An edited comment";

    public static int Run(string[] args)
    {
        string path = args[0];
        Console.WriteLine(string.Join('\t', "CROSS", path, "atl:" + Atl(path), "taglib:" + TagLibRead(path)));
        return 0;
    }

    private static string Flag(string? got, string want) => got == want ? "ok" : got is null or "" ? "none" : "DIFF[" + got.Replace(" ", "_") + "]";

    private static string Atl(string path)
    {
        try
        {
            ATL.Track t = new(path);
            if (t.AudioFormat is null || t.AudioFormat.ID < 0)
            {
                return "unsupported";
            }

            // ATL keeps the comment of ID3/APE/Vorbis/MP4 in Comment; ASF's description is also surfaced there.
            return $"title={Flag(t.Title, Title)} artist={Flag(t.Artist, Artist)} comment={Flag(t.Comment, Comment)} pictures={t.EmbeddedPictures.Count}";
        }
        catch (Exception e)
        {
            return "error:" + e.GetType().Name;
        }
    }

    private static string TagLibRead(string path)
    {
        try
        {
            using TagLib.File f = TagLib.File.Create(path);
            TagLib.Tag t = f.Tag;
            return $"title={Flag(t.Title, Title)} artist={Flag(t.FirstPerformer, Artist)} comment={Flag(t.Comment, Comment)} pictures={t.Pictures.Length}";
        }
        catch (TagLib.UnsupportedFormatException)
        {
            return "unsupported";
        }
        catch (Exception e)
        {
            return "error:" + e.GetType().Name;
        }
    }
}
