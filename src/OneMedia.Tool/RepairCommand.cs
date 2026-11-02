using System;
using System.IO;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Audio;
using RecursiveAI.OneMedia.Repair;

namespace RecursiveAI.OneMedia.Tool;

/// <summary>The <c>repair</c> command: rebuilds the indexes of a damaged file into a new file and prints what was rebuilt, dropped and kept.</summary>
internal static class RepairCommand
{
    public const string Usage =
        "       onemedia repair <in> <out> [--reference <file>]   rebuild indexes, counts and durations of a damaged Matroska/WebM, AVI, MP4/MOV/3GP, fragmented MP4 or MPEG-TS file into a new file;\n"
        + "                     an MP4/MOV/3GP cut off before its movie box is rebuilt from its media data (--reference gives the codec setup);\n"
        + "                     exit 0 when repaired or nothing to repair (the output is then a byte-identical copy), 1 when only partly repaired, 2 when nothing could be recovered";

    public static int Run(string[] args)
    {
        string? input = null;
        string? output = null;
        string? reference = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--reference" && i + 1 < args.Length)
            {
                reference = args[++i];
            }
            else if (input is null)
            {
                input = args[i];
            }
            else if (output is null)
            {
                output = args[i];
            }
            else
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }
        }

        if (input is null || output is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            RepairReport r = Media.Repair(input, output, reference, AacReconstructionProbe.Options());
            Console.WriteLine($"{r.Status}: {r.Format}, {r.InputLength} bytes in, {r.OutputLength} bytes out, confidence {r.Confidence}, {r.DroppedBytes} bytes dropped");
            foreach (RepairItem i in r.Items)
            {
                Console.WriteLine($"  {i.Action,-8} {i.Structure}{(i.Track >= 0 ? " (track " + i.Track + ")" : string.Empty)} @{i.Range.Offset}+{i.Range.Length} [{i.Confidence}] {i.Detail}");
            }

            foreach (RepairTrackReport t in r.Tracks)
            {
                Console.WriteLine($"  track {t.Track} {t.Codec}: {t.Packets} packets, {t.DroppedPackets} dropped [{t.Confidence}]{(t.Ranges.Count > 0 ? ", " + t.Ranges.Count + " input ranges" : string.Empty)}");
            }

            foreach (string w in r.Warnings)
            {
                Console.WriteLine("  note: " + w);
            }

            return r.Status switch
            {
                RepairStatus.Unrepairable => 2,
                RepairStatus.PartiallyRepaired => 1,
                _ => 0,
            };
        }
        catch (Exception e) when (e is MediaException or IOException)
        {
            Console.Error.WriteLine("repair: " + e.Message);
            return 2;
        }
    }
}
