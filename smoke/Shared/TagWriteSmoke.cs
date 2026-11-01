using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>Writes a title, an artist and a picture into an MP4 audio file, a WebM file and an MP3 in memory and reads them back. Part of the NativeAOT and browser-WebAssembly smoke runs.</summary>
internal static class TagWriteSmoke
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4, 5, 6];

    public static void Run(Action<string, bool> check)
    {
        foreach (string name in new[] { "aac.m4a", "opus.webm", "lame-info.mp3" })
        {
            byte[] input = Resource(name);
            TagEdits edits = new();
            edits.Set(CommonField.Title, "Smoke title");
            edits.Set(CommonField.Artist, "Smoke artist");
            edits.SetPicture(Png, "image/png");
            MemoryMediaSource src = new(input);
            TagWritePlan plan = TagWriter.Plan(src, edits);
            using MemoryStream output = new();
            SafeWriter.Write(plan.Plan, src, output);
            using MemoryMediaSource back = new(output.ToArray());
            using MediaFile m = MediaFile.Open(back);
            check("tag write " + name, m.Tags.Title == "Smoke title" && m.Tags.Artist == "Smoke artist" && HasPicture(m) && plan.Report.Systems.Count > 0);
        }
    }

    private static bool HasPicture(MediaFile m)
    {
        foreach (TagPicture p in m.Tags.Pictures)
        {
            if (p.Length == Png.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Resource(string name)
    {
        using Stream s = typeof(TagWriteSmoke).Assembly.GetManifestResourceStream(name)!;
        using MemoryStream ms = new();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
