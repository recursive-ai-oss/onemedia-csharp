using System;
using System.Collections.Generic;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Streaming;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the manifest readers. <c>hls-playlist</c> takes a playlist and <c>dash-mpd</c> an MPD. Neither may throw anything but a
/// <see cref="MediaException"/>; what parses must write out and parse again to the same text, and a DASH template must expand within its limit.
/// </summary>
internal static class ManifestTargets
{
    private static readonly MediaLimits s_small = new() { MaxAllocationBytes = 1 << 20, MaxElementCount = 20_000 };

    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["hls-playlist"] = Hls;
        targets["dash-mpd"] = Dash;
    }

    private static void Hls(ReadOnlySpan<byte> input)
    {
        try
        {
            // The lenient read takes anything; its defects point inside the text and what it returns writes out.
            string raw = System.Text.Encoding.UTF8.GetString(input);
            HlsParseResult lenient = HlsPlaylist.ParseLenient(raw, s_small);
            foreach (ManifestDefect d in lenient.Defects)
            {
                if (d.Line < 1 || d.Offset < 0 || d.Offset > raw.Length + 1)
                {
                    throw new InvalidOperationException("defect position outside the text");
                }
            }

            _ = lenient.Playlist.ToString();

            HlsPlaylist p = HlsPlaylist.Parse(input, s_small);
            string text = p.ToString();
            if (HlsPlaylist.Parse(text, s_small).ToString() != text)
            {
                throw new InvalidOperationException("playlist does not rewrite to itself");
            }

            if (p is HlsMediaPlaylist m)
            {
                _ = m.TotalDuration;
                _ = m.EffectiveTargetDuration;
            }
        }
        catch (MediaException)
        {
        }
    }

    private static void Dash(ReadOnlySpan<byte> input)
    {
        try
        {
            DashMpd m = DashMpd.Parse(input, s_small);
            string text = m.ToString();
            if (DashMpd.Parse(text, s_small).ToString() != text)
            {
                throw new InvalidOperationException("MPD does not rewrite to itself");
            }

            foreach (DashPeriod period in m.Periods)
            {
                foreach (DashAdaptationSet set in period.AdaptationSets)
                {
                    foreach (DashRepresentation r in set.Representations)
                    {
                        DashSegmentTemplate? t = r.SegmentTemplate ?? set.SegmentTemplate;
                        if (t?.Media is not null)
                        {
                            _ = t.Segments(r.Id, r.Bandwidth, period.Duration ?? m.MediaPresentationDuration ?? MediaTime.Unknown, s_small);
                        }
                    }
                }
            }
        }
        catch (MediaException)
        {
        }
    }
}
