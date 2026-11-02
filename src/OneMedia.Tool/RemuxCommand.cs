using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RecursiveAI.OneMedia.Remuxing;

namespace RecursiveAI.OneMedia.Tool;

/// <summary>The <c>remux</c> command: copies tracks into another container and prints what did not carry over.</summary>
internal static class RemuxCommand
{
    public const string Usage =
        "       onemedia remux <in> <out> [--format mp4|m4a|m4v|mov|3gp|3g2|fmp4|cmaf|mkv|webm|ts|m2ts] [--no-faststart] [--drop <track>]... [--only video|audio]\n"
        + "                      [--language <track>=<code>]... [--rotate <track>=<0|90|180|270>]... [--strict] [--overwrite]\n"
        + "                      copy tracks into another container without touching the compressed data; lists what was left out\n"
        + "       onemedia remux <in> <out> --faststart   move the movie box of an MP4/MOV/3GP file to the front, copying everything else unchanged";

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        string input = args[0], output = args[1];
        RemuxFormat? format = Remuxer.FormatForPath(output);
        bool faststartOnly = false, noFaststart = false, strict = false, overwrite = false;
        HashSet<int> drop = [];
        TrackKind? only = null;
        Dictionary<int, string> languages = [];
        Dictionary<int, int> rotations = [];
        try
        {
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--format": format = ParseFormat(Next(args, ref i)); break;
                    case "--faststart": faststartOnly = true; break;
                    case "--no-faststart": noFaststart = true; break;
                    case "--strict": strict = true; break;
                    case "--overwrite": overwrite = true; break;
                    case "--drop": drop.Add(int.Parse(Next(args, ref i), CultureInfo.InvariantCulture)); break;
                    case "--only": only = Next(args, ref i) == "audio" ? TrackKind.Audio : TrackKind.Video; break;
                    case "--language": { (int t, string v) = Pair(Next(args, ref i)); languages[t] = v; break; }
                    case "--rotate": { (int t, string v) = Pair(Next(args, ref i)); rotations[t] = int.Parse(v, CultureInfo.InvariantCulture); break; }
                    default: throw new ArgumentException($"unknown option {args[i]}");
                }
            }

            if (faststartOnly)
            {
                FaststartReport f = Remuxer.Faststart(input, output, overwrite);
                Console.WriteLine(f.Relocated
                    ? string.Create(CultureInfo.InvariantCulture, $"moved the movie box ({f.MovieBoxBytes} bytes) to the front; {f.OffsetsShifted} chunk offsets moved, {f.PromotedTables} tables made 64-bit")
                    : "the movie box is already in front; the file was copied unchanged");
                return 0;
            }

            if (format is null)
            {
                Console.Error.WriteLine("the extension of the output names no container; pass --format");
                return 2;
            }

            Dictionary<int, TrackEdit> edits = [];
            foreach (int t in drop)
            {
                edits[t] = new TrackEdit { Drop = true };
            }

            foreach ((int t, string lang) in languages)
            {
                edits[t] = (edits.TryGetValue(t, out TrackEdit? e) ? e : new TrackEdit()) with { Language = lang };
            }

            foreach ((int t, int deg) in rotations)
            {
                edits[t] = (edits.TryGetValue(t, out TrackEdit? e) ? e : new TrackEdit()) with { RotationDegrees = deg };
            }

            RemuxOptions options = new()
            {
                Faststart = !noFaststart,
                AllowDrop = !strict,
                Overwrite = overwrite,
                Edits = edits,
                Select = only is { } kind ? t => t.Kind == kind : null,
            };
            RemuxReport report = Remuxer.Remux(input, output, format.Value, options);
            Print(report);
            if (report.LostMediaTracks.Count > 0)
            {
                Console.Error.WriteLine("warning: input track(s) " + string.Join(", ", report.LostMediaTracks) + " were not written; the output lacks media the input had (see the list above)");
                return 3;
            }

            return 0;
        }
        catch (Exception ex) when (ex is MediaException or ArgumentException or FormatException or IOException or UnauthorizedAccessException or MediaReadStateException)
        {
            Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static string Next(string[] args, ref int i) => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value");

    private static (int, string) Pair(string text)
    {
        int eq = text.IndexOf('=', StringComparison.Ordinal);
        return eq < 0 ? throw new ArgumentException("expected <track>=<value>") : (int.Parse(text.AsSpan(0, eq), CultureInfo.InvariantCulture), text[(eq + 1)..]);
    }

    private static RemuxFormat ParseFormat(string name) => name.ToLowerInvariant() switch
    {
        "mp4" => RemuxFormat.Mp4,
        "m4a" => RemuxFormat.M4a,
        "m4v" => RemuxFormat.M4v,
        "mov" => RemuxFormat.Mov,
        "3gp" => RemuxFormat.ThreeGp,
        "3g2" => RemuxFormat.ThreeG2,
        "fmp4" => RemuxFormat.FragmentedMp4,
        "cmaf" => RemuxFormat.Cmaf,
        "mkv" or "matroska" => RemuxFormat.Matroska,
        "webm" => RemuxFormat.WebM,
        "ts" or "mpegts" => RemuxFormat.MpegTs,
        "m2ts" or "mts" => RemuxFormat.M2ts,
        _ => throw new ArgumentException($"unknown format {name}"),
    };

    private static void Print(RemuxReport report)
    {
        foreach (RemuxTrackReport t in report.Tracks)
        {
            Console.WriteLine(t.OutputTrack >= 0
                ? string.Create(CultureInfo.InvariantCulture, $"track {t.InputTrack} -> {t.OutputTrack}: {t.Packets} packets{(t.Repackaging == "none" ? string.Empty : ", " + t.Repackaging)}")
                : string.Create(CultureInfo.InvariantCulture, $"track {t.InputTrack}: not written"));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {report.Mux.BytesWritten} bytes"));
        foreach (string c in report.Changes)
        {
            Console.WriteLine("changed: " + c);
        }

        foreach (RemuxLoss l in report.Losses)
        {
            string where = l.InputTrack >= 0 ? string.Create(CultureInfo.InvariantCulture, $" (track {l.InputTrack})") : string.Empty;
            Console.WriteLine($"not carried over{where}: {l.Kind.ToString().ToLowerInvariant()} {l.Subject}: {l.Reason} - {l.Detail}");
        }

        foreach (string n in report.Mux.Notes)
        {
            Console.WriteLine("note: " + n);
        }
    }
}
