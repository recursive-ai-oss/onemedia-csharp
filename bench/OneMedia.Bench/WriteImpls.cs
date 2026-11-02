using System;
using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneMedia.Remuxing;
using RecursiveAI.OneMedia.Tags;
using Atl = ATL;

namespace RecursiveAI.OneMedia.Bench;

/// <summary>
/// Tag writing, large remux and MakePlayable work. Tag writes run on a file held in memory: each implementation starts from the same
/// bytes and ends with the edited file in a memory stream, so the figure is the edit and not the disk. OneMedia plans the edit and
/// writes a new copy (<c>SaveAs</c> without the disk); the comparators edit the stream they were opened on, as their READMEs show.
/// </summary>
internal static class WriteImpls
{
    private static readonly Dictionary<string, byte[]> Files = [];

    // Read once per process, outside the measured work.
    private static byte[] Load(string path)
    {
        lock (Files)
        {
            if (!Files.TryGetValue(path, out byte[]? data))
            {
                data = File.ReadAllBytes(path);
                Files[path] = data;
            }

            return data;
        }
    }

    // The same four common fields everywhere.
    private static TagEdits Edits()
    {
        TagEdits edits = new();
        edits.Set(CommonField.Title, "Bench title, edited");
        edits.Set(CommonField.Artist, "Bench artist");
        edits.Set(CommonField.Album, "Bench album");
        edits.Set(CommonField.Comment, "Edited by the bench");
        return edits;
    }

    public static Work OursTagWrite(string path)
    {
        byte[] data = Load(path);
        using MemoryMediaSource source = new(data);
        TagWritePlan plan = TagWriter.Plan(source, Edits());
        using MemoryStream output = new(data.Length + 4096);
        SafeWriter.Write(plan.Plan, source, output);
        return new Work(1, 0, 1);
    }

    private static MemoryStream Copy(byte[] data)
    {
        MemoryStream ms = new(data.Length + 4096);
        ms.Write(data);
        ms.Position = 0;
        return ms;
    }

    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".m4a" => "audio/mp4",
        ".mp4" => "audio/mp4",
        ".wav" => "audio/x-wav",
        ".mkv" => "video/x-matroska",
        ".mka" => "audio/x-matroska",
        _ => throw new NotAvailableException("no MIME type for " + path),
    };

    public static Work AtlTagWrite(string path)
    {
        using MemoryStream ms = Copy(Load(path));
        Atl.Track t = new(ms, Mime(path));
        t.Title = "Bench title, edited";
        t.Artist = "Bench artist";
        t.Album = "Bench album";
        t.Comment = "Edited by the bench";
        if (!t.Save())
        {
            throw new NotAvailableException("ATL.NET did not save " + Path.GetFileName(path));
        }

        return new Work(1, 0, 1);
    }

    private sealed class MemoryAbstraction(string name, Stream stream) : TagLib.File.IFileAbstraction
    {
        public string Name { get; } = name;

        public Stream ReadStream => stream;

        public Stream WriteStream => stream;

        public void CloseStream(Stream s) => s.Position = 0;
    }

    public static Work TagLibTagWrite(string path)
    {
        using MemoryStream ms = Copy(Load(path));
        using TagLib.File f = TagLib.File.Create(new MemoryAbstraction(Path.GetFileName(path), ms));
        f.Tag.Title = "Bench title, edited";
        f.Tag.Performers = ["Bench artist"];
        f.Tag.Album = "Bench album";
        f.Tag.Comment = "Edited by the bench";
        f.Save();
        return new Work(1, 0, 1);
    }

    // ---- Remux of a file past 4 GiB: constant memory is the point, so the output goes to a real file (deleted after) ------------------

    private static Work RemuxToFile(string path, RemuxFormat format, string extension)
    {
        string output = Path.Combine(Path.GetTempPath(), "onemedia-bench-" + Environment.ProcessId + extension);
        try
        {
            RemuxReport report = Remuxer.Remux(path, output, format);
            long packets = 0;
            foreach (RemuxTrackReport t in report.Tracks)
            {
                packets += t.Packets;
            }

            return new Work(packets, new FileInfo(output).Length);
        }
        finally
        {
            File.Delete(output);
        }
    }

    public static Work OursRemuxToMp4(string path) => RemuxToFile(path, RemuxFormat.Mp4, ".mp4");

    public static Work OursRemuxToMatroska(string path) => RemuxToFile(path, RemuxFormat.Matroska, ".mkv");

    // ---- MakePlayable on one file (information row) ------------------------------------------------------------------------------

    public static Work OursMakePlayable(string path)
    {
        string output = Path.Combine(Path.GetTempPath(), "onemedia-bench-playable-" + Environment.ProcessId + ".mp4");
        try
        {
            Playback.MakePlayableReport report = Media.MakePlayable(path, output, Playback.BrowserProfiles.GenericMp4);
            return new Work(1, File.Exists(output) ? new FileInfo(output).Length : 0, (long)report.Status);
        }
        finally
        {
            File.Delete(output);
        }
    }
}
