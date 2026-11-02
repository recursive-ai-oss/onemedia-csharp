using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Streaming;

namespace RecursiveAI.OneMedia.LiveCheck;

/// <summary>
/// Watches the output directory of a live packaging run the way a player would: it polls each media playlist and the MPD, parses every new version
/// with the library's own parsers and checks it. Usage: LiveCheck &lt;directory&gt; &lt;windowSegments&gt; &lt;maxSeconds&gt;. Exit status 1 when any check failed.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string dir = args[0];
        int window = int.Parse(args[1], CultureInfo.InvariantCulture);
        double maxSeconds = double.Parse(args[2], CultureInfo.InvariantCulture);
        string[] playlists = ["video0/index.m3u8", "audio0/index.m3u8"];
        Dictionary<string, string> lastText = [];
        Dictionary<string, HlsMediaPlaylist> last = [];
        Dictionary<string, int> updates = [];
        int mpdUpdates = 0, failures = 0, masterReads = 0;
        string lastMpd = "";
        bool ended = false;
        Stopwatch clock = Stopwatch.StartNew();
        void Fail(string message)
        {
            failures++;
            if (failures <= 20)
            {
                Console.WriteLine($"FAIL at {clock.Elapsed.TotalSeconds:F1} s: {message}");
            }
        }

        while (clock.Elapsed.TotalSeconds < maxSeconds && !ended)
        {
            string master = Path.Combine(dir, "master.m3u8");
            if (File.Exists(master) && !lastText.ContainsKey("master"))
            {
                lastText["master"] = File.ReadAllText(master);
                masterReads++;
                if (HlsPlaylist.Parse(lastText["master"]) is not HlsMultivariantPlaylist { Variants.Count: > 0 })
                {
                    Fail("master playlist has no variants");
                }
            }

            foreach (string name in playlists)
            {
                string path = Path.Combine(dir, name);
                if (!File.Exists(path))
                {
                    continue;
                }

                string text = File.ReadAllText(path);
                if (lastText.TryGetValue(name, out string? before) && before == text)
                {
                    continue;
                }

                lastText[name] = text;
                updates[name] = updates.GetValueOrDefault(name) + 1;
                if (!HlsPlaylist.TryParse(text, out HlsPlaylist? parsed, null) || parsed is not HlsMediaPlaylist media)
                {
                    Fail($"{name}: the playlist does not parse");
                    continue;
                }

                if (media.ToString() != HlsPlaylist.Parse(media.ToString()).ToString())
                {
                    Fail($"{name}: the playlist does not rewrite to itself");
                }

                if (media.Segments.Count > window)
                {
                    Fail($"{name}: {media.Segments.Count} segments listed, window is {window}");
                }

                if (last.TryGetValue(name, out HlsMediaPlaylist? old))
                {
                    long advance = media.MediaSequence - old.MediaSequence;
                    if (advance < 0)
                    {
                        Fail($"{name}: media sequence went back from {old.MediaSequence} to {media.MediaSequence}");
                    }
                    else if (advance < old.Segments.Count && media.Segments.Count > 0 && media.Segments[0].Uri != old.Segments[(int)advance].Uri)
                    {
                        Fail($"{name}: the window moved by {advance} but the first segment is not the one that followed");
                    }
                }

                foreach (HlsSegment segment in media.Segments)
                {
                    if (!File.Exists(Path.Combine(dir, Path.GetDirectoryName(name)!, segment.Uri)))
                    {
                        Fail($"{name}: segment {segment.Uri} is listed but not on disk");
                    }

                    if (segment.Duration > new MediaTime(media.TargetDuration, 1))
                    {
                        Fail($"{name}: segment {segment.Uri} is {segment.Duration} long, target duration {media.TargetDuration}");
                    }
                }

                HlsMap? initSegment = media.Segments.Count > 0 ? media.Segments[0].Map : null;
                if (initSegment is not null && !File.Exists(Path.Combine(dir, Path.GetDirectoryName(name)!, initSegment.Uri)))
                {
                    Fail($"{name}: the init segment {initSegment.Uri} is missing");
                }

                last[name] = media;
                if (media.EndList && name == playlists[0])
                {
                    ended = true;
                }
            }

            string mpdPath = Path.Combine(dir, "manifest.mpd");
            if (File.Exists(mpdPath))
            {
                string mpd = File.ReadAllText(mpdPath);
                if (mpd != lastMpd)
                {
                    lastMpd = mpd;
                    mpdUpdates++;
                    if (!DashMpd.TryParse(mpd, out DashMpd? parsedMpd, null) || parsedMpd is null)
                    {
                        Fail("the MPD does not parse");
                    }
                    else if (parsedMpd.ToString() != DashMpd.Parse(parsedMpd.ToString()).ToString())
                    {
                        Fail("the MPD does not rewrite to itself");
                    }
                }
            }

            Thread.Sleep(100);
        }

        Console.WriteLine($"{clock.Elapsed.TotalSeconds:F0} s watched; master reads {masterReads}; video playlist versions {updates.GetValueOrDefault(playlists[0])}, audio {updates.GetValueOrDefault(playlists[1])}; MPD versions {mpdUpdates}; ended {ended}; failures {failures}");
        return failures == 0 && ended ? 0 : 1;
    }
}
