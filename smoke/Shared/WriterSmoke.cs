using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Runs the three structure editors in memory: an untouched file comes back byte-identical, and a padding element appended to each
/// family reads back. Part of the NativeAOT and browser-WebAssembly smoke runs.
/// </summary>
internal static class WriterSmoke
{
    public static void Run(Action<string, bool> check)
    {
        byte[] iso = Resource("aac.m4a");
        MemoryMediaSource isoSource = new(iso);
        check("writer iso untouched", Write(new BoxEditor(new BoxTree(isoSource), isoSource).Build(), isoSource).AsSpan().SequenceEqual(iso));
        BoxEditor isoEdit = new(new BoxTree(isoSource), isoSource);
        isoEdit.AppendChild(null, BoxBuilder.CreateFree(100));
        byte[] isoOut = Write(isoEdit.Build(), isoSource);
        check("writer iso append", isoOut.Length == iso.Length + 100 && new BoxTree(new MemoryMediaSource(isoOut)).TopLevel.Count > 0);

        byte[] mkv = Resource("opus.webm");
        MemoryMediaSource mkvSource = new(mkv);
        check("writer ebml untouched", Write(new EbmlEditor(EbmlDocument.Open(mkvSource), mkvSource).Build(), mkvSource).AsSpan().SequenceEqual(mkv));
        EbmlDocument doc = EbmlDocument.Open(mkvSource);
        EbmlEditor mkvEdit = new(doc, mkvSource);
        mkvEdit.AppendChild(doc.Segment, EbmlWriter.Void(100));
        byte[] mkvOut = Write(mkvEdit.Build(), mkvSource);
        check("writer ebml append", EbmlDocument.Open(new MemoryMediaSource(mkvOut)).Segment is not null && mkvOut.Length > mkv.Length);

        byte[] avi = Resource("mjpeg-pcm.avi");
        MemoryMediaSource aviSource = new(avi);
        ChunkFile file = ChunkFile.TryOpen(aviSource, MediaLimits.Default, new DefectLog())!;
        check("writer riff untouched", Write(new ChunkEditor(file, aviSource).Build(), aviSource).AsSpan().SequenceEqual(avi));
        ChunkEditor aviEdit = new(file, aviSource);
        aviEdit.AppendChild(null, ChunkBuilder.CreatePadding(file.Layout, 100));
        byte[] aviOut = Write(aviEdit.Build(), aviSource);
        // The AVI already holds a JUNK chunk big enough, so the new padding is taken in without growing the file.
        check("writer riff append", aviOut.Length >= avi.Length && ChunkFile.TryOpen(new MemoryMediaSource(aviOut), MediaLimits.Default, new DefectLog()) is not null);
    }

    private static byte[] Write(EditPlan plan, IMediaSource source)
    {
        using MemoryStream output = new();
        SafeWriter.Write(plan, source, output);
        return output.ToArray();
    }

    private static byte[] Resource(string name)
    {
        using Stream s = typeof(WriterSmoke).Assembly.GetManifestResourceStream(name)!;
        using MemoryStream ms = new();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
