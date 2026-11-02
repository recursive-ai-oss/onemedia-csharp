using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>Writes a title, an artist and a picture into an MP4 audio file, a WebM file and an MP3 in memory and reads them back. Part of the NativeAOT and browser-WebAssembly smoke runs.</summary>
internal static class TagWriteSmoke
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4, 5, 6];

    public static void Run(Action<string, bool> check)
    {
        foreach (string name in new[] { "aac.m4a", "opus.webm", "lame-info.mp3", "wmav2-tagged.wma" })
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
            check("tag write " + name, m.Tags.Title == "Smoke title" && m.Tags.Artist == "Smoke artist" && (HasPicture(m) || name.EndsWith(".webm", StringComparison.Ordinal)) && plan.Report.Systems.Count > 0);
        }

        // (WebM has no place for a cover, so the picture is not expected there.)
        // An XMP packet is built and parsed with System.Xml.Linq, which the trimmer and the AOT compiler must keep working.
        TagEdits xmp = new();
        xmp.Set(CommonField.Title, "Smoke <xmp>");
        MemoryMediaSource xsrc = new(Resource("aac.m4a"));
        TagWritePlan xplan = TagWriter.Plan(xsrc, xmp, new TagWriteOptions { Systems = [TagSystems.Xmp] });
        using MemoryStream xout = new();
        SafeWriter.Write(xplan.Plan, xsrc, xout);
        using MemoryMediaSource xback = new(xout.ToArray());
        using MediaFile xm = MediaFile.Open(xback);
        bool found = false;
        foreach (CommonValue v in xm.Tags.Values(CommonField.Title))
        {
            found |= v.Source.System == TagSystems.Xmp && v.Text == "Smoke <xmp>";
        }

        check("tag write xmp", found);

        // A capture-time write: header times, a tag and a Matroska element, read back through the resolver.
        DateTimeOffset when = new(2024, 5, 1, 12, 30, 0, TimeSpan.FromHours(2));
        foreach (string name in new[] { "aac.m4a", "opus.webm", "lame-info.mp3", "wmav2-tagged.wma" })
        {
            MemoryMediaSource dsrc = new(Resource(name));
            CaptureTimePlan dplan = CaptureTimeWriter.Plan(dsrc, when);
            using MemoryStream dout = new();
            SafeWriter.Write(dplan.Plan, dsrc, dout);
            using MemoryMediaSource dback = new(dout.ToArray());
            using MediaFile dm = MediaFile.Open(dback);
            check("capture time " + name, dplan.Report.Written.Count > 0 && dm.CaptureTime.Best is { } best && (best.Value.ToDateTimeOffset() is { } i ? i == when : best.Value.Value == when.DateTime));
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
