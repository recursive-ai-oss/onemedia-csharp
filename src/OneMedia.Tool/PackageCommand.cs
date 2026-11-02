using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Captions;
using RecursiveAI.OneMedia.Streaming;

namespace RecursiveAI.OneMedia.Tool;

/// <summary>The <c>package</c> command: CMAF segments with HLS playlists and a DASH manifest, on demand or live.</summary>
internal static class PackageCommand
{
    public const string Usage =
        "       onemedia package <in>... <dir> [--hls | --dash] [--segment <seconds>] [--single-file] [--subs <file>[:<lang>[:<name>]]]... [--no-container-subs] [--overwrite]\n"
        + "       onemedia package <in|-> <dir> --live [--window <segments>] [--segment <seconds>] [--hls | --dash]\n"
        + "                     cut CMAF segments and write HLS playlists and a DASH manifest (both unless one is named); several inputs make a bit-rate ladder (the first supplies the audio);\n"
        + "                     --live publishes each segment as it completes, keeping a sliding window, and reads '-' as a stream from standard input";

    public static int Run(string[] args)
    {
        List<string> positional = [];
        PackagingFormats formats = PackagingFormats.None;
        double segment = 6;
        bool single = false, live = false, overwrite = false, containerSubs = true;
        int window = 6;
        List<PackagingSubtitle> subs = [];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--hls": formats |= PackagingFormats.Hls; break;
                    case "--dash": formats |= PackagingFormats.Dash; break;
                    case "--single-file": single = true; break;
                    case "--live": live = true; break;
                    case "--overwrite": overwrite = true; break;
                    case "--no-container-subs": containerSubs = false; break;
                    case "--segment": segment = double.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--window": window = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--subs": subs.Add(Subtitle(Next(args, ref i))); break;
                    default:
                        if (args[i].StartsWith("--", StringComparison.Ordinal))
                        {
                            throw new ArgumentException($"unknown option {args[i]}");
                        }

                        positional.Add(args[i]);
                        break;
                }
            }

            if (positional.Count < 2)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            if (formats == PackagingFormats.None)
            {
                formats = PackagingFormats.Both;
            }

            string dir = positional[^1];
            positional.RemoveAt(positional.Count - 1);
            if (segment <= 0)
            {
                throw new ArgumentException("--segment must be positive");
            }

            PackagingOptions options = new()
            {
                Formats = formats,
                SegmentDuration = new MediaTime((long)Math.Round(segment * 1000), 1000),
                SingleFile = single,
                Overwrite = overwrite,
                IncludeContainerSubtitles = containerSubs,
                Subtitles = subs,
                Live = live ? new LiveOptions { StartTime = DateTimeOffset.UtcNow, WindowSegments = window } : null,
                ManifestWritten = null,
            };
            PackagingReport report;
            if (positional.Count == 1 && positional[0] == "-")
            {
                using MediaFile file = Media.Open(Console.OpenStandardInput());
                report = Packager.Package(file, dir, options);
            }
            else
            {
                report = Packager.Package(positional, dir, options);
            }

            foreach (PackagedTrack t in report.Tracks)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{t.Name,-8} {t.Kind,-8} {t.Codecs ?? "-",-14} {t.Segments} segments, {t.Bytes} bytes, {t.DurationSeconds:F3} s"));
            }

            foreach (string m in report.Manifests)
            {
                Console.WriteLine("wrote " + m);
            }

            foreach (string n in report.Notes)
            {
                Console.WriteLine("note: " + n);
            }

            foreach (RecursiveAI.OneMedia.Remuxing.RemuxLoss l in report.Losses)
            {
                Console.WriteLine($"not carried: {l.Kind} {l.Reason} {l.Detail}");
            }

            return 0;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or IOException or MediaException or OverflowException)
        {
            Console.Error.WriteLine("package: " + e.Message);
            return 2;
        }
    }

    private static string Next(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");

    private static PackagingSubtitle Subtitle(string spec)
    {
        string[] parts = spec.Split(':', 3);
        SubtitleDocument doc = Subtitles.Read(parts[0]);
        return new PackagingSubtitle(doc, parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null, parts.Length > 2 ? parts[2] : null);
    }
}
