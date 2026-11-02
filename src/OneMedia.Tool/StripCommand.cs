using System;
using System.Globalization;
using System.IO;
using RecursiveAI.OneMedia.Privacy;

namespace RecursiveAI.OneMedia.Tool;

/// <summary>The <c>strip</c> command: thin wrapper over <see cref="PrivacyStrip"/>.</summary>
internal static class StripCommand
{
    public const string Usage =
        "       onemedia strip <in> <out> [--location] [--device-ids] [--overwrite] [--in-place]\n"
        + "                      write a copy without location and/or device identifiers (both when neither flag is given); with --in-place <out> is\n"
        + "                      ignored and only the changed ranges of <in> are overwritten. Exit 3 when something was found and could not be removed.";

    public static int Run(string[] args)
    {
        bool location = false, ids = false, overwrite = false, inPlace = false;
        string? input = null, output = null;
        foreach (string a in args)
        {
            switch (a)
            {
                case "--location": location = true; break;
                case "--device-ids": ids = true; break;
                case "--overwrite": overwrite = true; break;
                case "--in-place": inPlace = true; break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine("unknown option " + a);
                        return 2;
                    }

                    if (input is null)
                    {
                        input = a;
                    }
                    else
                    {
                        output ??= a;
                    }

                    break;
            }
        }

        if (input is null || (output is null && !inPlace))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        StripCategories cats = (location ? StripCategories.Location : 0) | (ids ? StripCategories.DeviceIds : 0);
        StripOptions options = new() { Categories = cats == StripCategories.None ? StripCategories.All : cats };
        try
        {
            StripReport report = inPlace ? PrivacyStrip.SaveInPlace(input, options) : PrivacyStrip.SaveAs(input, output!, options, overwrite);
            foreach (StripEntry e in report.Entries)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{e.Action,-19} {e.Category,-9} {e.System}:{e.Name} track {e.TrackId} x{e.Count} {e.Bytes} bytes at {e.Range.Offset}+{e.Range.Length}"));
            }

            foreach (StripLeftover l in report.NotRemoved)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"NOT REMOVED        {l.Category,-9} {l.System}:{l.Name} track {l.TrackId} at {l.Range.Offset}+{l.Range.Length}: {l.Reason}"));
            }

            foreach (string n in report.Notes)
            {
                Console.WriteLine("note: " + n);
            }

            return report.IsComplete ? 0 : 3;
        }
        catch (MediaException ex)
        {
            Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
