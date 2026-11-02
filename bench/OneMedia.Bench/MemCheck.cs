using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using RecursiveAI.OneMedia.Remuxing;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Bench;

/// <summary>
/// Peak resident memory of one operation on one file, for the constant-memory table: the same operation on a small file and on one past
/// 4 GiB should peak at about the same figure. The output goes to a directory the caller deletes.
/// </summary>
internal static class MemCheck
{
    public static int Run(string area, string input, string outDir)
    {
        Directory.CreateDirectory(outDir);
        long baseline = Process.GetCurrentProcess().WorkingSet64;
        long started = Stopwatch.GetTimestamp();
        string ext = Path.GetExtension(input);
        long packets = 0;
        switch (area)
        {
            case "demux":
                packets = Impls.OursDemux(input).Packets;
                break;
            case "remux":
                packets = Media.Remux(input, Path.Combine(outDir, "out.mkv"), RemuxFormat.Matroska).Tracks.Count;
                break;
            case "package":
                packets = Media.PackageHls(input, Path.Combine(outDir, "hls")).Tracks.Count;
                break;
            case "repair":
                packets = Media.Repair(input, Path.Combine(outDir, "repaired" + ext)).Tracks.Count;
                break;
            case "tag":
                packets = TagWriter.SaveAs(input, Path.Combine(outDir, "tagged" + ext), WriteImpls.Edits(), null, true).Systems.Count;
                break;
            default:
                Console.Error.WriteLine("unknown area " + area);
                return 2;
        }

        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        long written = 0;
        foreach (string f in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
        {
            written += new FileInfo(f).Length;
        }

        double peak = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{area}\t{Path.GetFileName(input)}\t{new FileInfo(input).Length / 1048576.0:0.0} MiB in\t{written / 1048576.0:0.0} MiB out\tpeak {peak:0.0} MiB (baseline {baseline / 1048576.0:0.0})\t{seconds:0.0} s\t{packets}"));
        return 0;
    }
}
