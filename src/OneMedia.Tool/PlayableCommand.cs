using System;
using System.Globalization;
using System.Linq;
using RecursiveAI.OneMedia.Audio;
using RecursiveAI.OneMedia.Playback;

namespace RecursiveAI.OneMedia.Tool;

/// <summary>The <c>playable</c> command: makes a file play in a browser profile, re-encoding audio to Opus when that is the only other blocker.</summary>
internal static class PlayableCommand
{
    public const string Usage =
        "       onemedia playable <in> <out> [--profile id] [--no-transcode] [--bitrate bits-per-second] [--overwrite]\n"
        + "                      make a file play in a browser profile (default generic-mp4; ids: chrome chromium firefox safari webkit-linux ios-safari android-chrome generic-mp4):\n"
        + "                      move the movie box first or remux (into the container the output's extension names if the profile plays it), and re-encode audio to Opus if that is what blocks it; video is never re-encoded.\n"
        + "                      exit 0 when the output plays, 1 when it cannot be made playable (blockers are listed)";

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        string input = args[0], output = args[1];
        string profileId = "generic-mp4";
        bool transcode = true, overwrite = false;
        long bitRate = 0;
        try
        {
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--profile": profileId = Next(args, ref i); break;
                    case "--no-transcode": transcode = false; break;
                    case "--overwrite": overwrite = true; break;
                    case "--bitrate": bitRate = long.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    default: throw new ArgumentException($"unknown option {args[i]}");
                }
            }

            BrowserProfile profile = BrowserProfiles.Find(profileId) ?? throw new ArgumentException($"no profile '{profileId}'");
            MakePlayableReport r = PlayableMaker.MakePlayable(input, output, profile, new MakePlayableOptions
            {
                AudioTranscoder = transcode ? AudioTranscoder.Default : null,
                AudioBitRate = bitRate,
                Overwrite = overwrite,
                PreferredTarget = Remuxing.Remuxer.FormatForPath(output),
            });
            Print(r);
            return r.OutputWritten ? 0 : 1;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or System.IO.IOException or MediaException)
        {
            Console.Error.WriteLine("onemedia playable: " + e.Message);
            return e is ArgumentException or FormatException ? 2 : 1;
        }
    }

    private static string Next(string[] args, ref int i) => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value");

    private static void Print(MakePlayableReport r)
    {
        Console.WriteLine($"profile {r.ProfileId}: {r.Status}{(r.Target is { } t ? $" ({t})" : string.Empty)}");
        Console.WriteLine("before: " + Describe(r.Before));
        if (r.After is { } after)
        {
            Console.WriteLine("after:  " + Describe(after));
        }

        foreach (MakePlayableAction a in r.Actions)
        {
            Console.WriteLine($"  {a.Kind}: {a.Detail}");
        }

        foreach (PlaybackBlocker b in r.Remaining)
        {
            Console.WriteLine($"  blocked: {b.Code} (track {b.TrackIndex}): {b.Message}");
        }

        foreach (string n in r.Notes)
        {
            Console.WriteLine("  note: " + n);
        }

        if (r.Remux is { } remux)
        {
            foreach (Remuxing.RemuxLoss l in remux.Losses.Where(l => l.Reason != Remuxing.LossReason.Deselected))
            {
                Console.WriteLine($"  lost {l.Kind}: {l.Subject}: {l.Detail}");
            }
        }
    }

    private static string Describe(PlaybackVerdict v) =>
        v.Progressive.Plays ? "plays" : "blocked by " + string.Join(", ", v.Progressive.Blockers.Select(b => $"{b.Code}[{b.TrackIndex}]"));
}
