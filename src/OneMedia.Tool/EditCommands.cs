using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Editing;

namespace RecursiveAI.OneMedia.Tool;

/// <summary>The <c>trim</c>, <c>split</c> and <c>concat</c> commands: thin wrappers over <see cref="Media.Trim"/>, <see cref="Media.Split"/> and <see cref="Media.Concat(string, string[])"/>.</summary>
internal static class EditCommands
{
    public const string Usage =
        "       onemedia trim <in> <out> --from <time> --to <time> [--keyframe]   cut a time range without re-encoding\n" +
        "       onemedia split <in> <out-pattern> (--at <time>[,<time>...] | --size <bytes>)   split on keyframes ({0:000} = part number)\n" +
        "       onemedia concat <out> <in> <in>...   join files whose tracks match, without re-encoding\n" +
        "       times: seconds (12.5) or [hh:]mm:ss[.fff]";

    public static bool Handles(string[] args) => args.Length > 0 && args[0] is "trim" or "split" or "concat";

    public static int Run(string[] args)
    {
        try
        {
            switch (args[0])
            {
                case "trim":
                    return Trim(args);
                case "split":
                    return Split(args);
                default:
                    return Concat(args);
            }
        }
        catch (EditRefusedException ex)
        {
            Console.Error.WriteLine($"refused ({ex.Reason}): {ex.Message}");
            return 1;
        }
        catch (MediaException ex)
        {
            Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Trim(string[] args)
    {
        string? from = Option(args, "--from"), to = Option(args, "--to");
        if (args.Length < 3 || from is null || to is null)
        {
            return Bad();
        }

        TrimMode mode = Array.IndexOf(args, "--keyframe") >= 0 ? TrimMode.Keyframe : TrimMode.FrameAccurate;
        Print(Media.Trim(args[1], args[2], ParseTime(from), ParseTime(to), mode));
        return 0;
    }

    private static int Split(string[] args)
    {
        if (args.Length < 3)
        {
            return Bad();
        }

        IReadOnlyList<ClipReport> parts;
        if (Option(args, "--at") is { } at)
        {
            List<TimeSpan> times = [];
            foreach (string t in at.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                times.Add(ParseTime(t));
            }

            parts = Media.Split(args[1], args[2], times);
        }
        else if (Option(args, "--size") is { } size)
        {
            parts = Media.SplitBySize(args[1], args[2], long.Parse(size, CultureInfo.InvariantCulture));
        }
        else
        {
            return Bad();
        }

        for (int i = 0; i < parts.Count; i++)
        {
            Console.Write($"part {i + 1}: ");
            Print(parts[i]);
        }

        return 0;
    }

    private static int Concat(string[] args)
    {
        if (args.Length < 3)
        {
            return Bad();
        }

        Print(Media.Concat(args[1], args[2..]));
        return 0;
    }

    private static void Print(ClipReport r)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"start {r.SourceStart.ToTimeSpan():c} length {r.Duration.ToTimeSpan():c} bytes {r.Mux.BytesWritten} hidden-video-packets {r.HiddenVideoPackets}"));
        foreach (string n in r.Notes)
        {
            Console.WriteLine("  note: " + n);
        }

        foreach (string n in r.Mux.Notes)
        {
            Console.WriteLine("  mux note: " + n);
        }
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Bad()
    {
        Console.Error.WriteLine("usage:");
        Console.Error.WriteLine(Usage);
        return 2;
    }

    /// <summary>Seconds ("12.5") or [hh:]mm:ss[.fff].</summary>
    private static TimeSpan ParseTime(string text)
    {
        if (text.Contains(':', StringComparison.Ordinal))
        {
            string[] parts = text.Split(':');
            string normalized = parts.Length == 2 ? "00:" + text : text;
            return TimeSpan.Parse(normalized, CultureInfo.InvariantCulture);
        }

        decimal seconds = decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return TimeSpan.FromTicks((long)decimal.Round(seconds * TimeSpan.TicksPerSecond));
    }
}
