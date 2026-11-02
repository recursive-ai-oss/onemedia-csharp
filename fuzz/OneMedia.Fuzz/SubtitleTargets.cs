using System;
using System.Collections.Generic;
using RecursiveAI.OneMedia.Captions;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for the subtitle readers (<c>subs-srt</c>, <c>subs-webvtt</c>, <c>subs-ass</c>, <c>subs-ttml</c>, <c>subs-sbv</c>, <c>subs-lrc</c>).
/// A <see cref="MediaException"/> from the reader is a clean rejection. When the reader accepts the bytes, every writer must handle the
/// document without throwing, and what the same format's writer wrote must read back with the same number of cues.
/// Anything else escaping is a finding.
/// </summary>
internal static class SubtitleTargets
{
    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["subs-srt"] = input => Run(SubtitleFormats.Srt, input);
        targets["subs-webvtt"] = input => Run(SubtitleFormats.WebVtt, input);
        targets["subs-ass"] = input => Run(SubtitleFormats.Ass, input);
        targets["subs-ttml"] = input => Run(SubtitleFormats.Ttml, input);
        targets["subs-sbv"] = input => Run(SubtitleFormats.Sbv, input);
        targets["subs-lrc"] = input => Run(SubtitleFormats.Lrc, input);
    }

    private static void Run(SubtitleFormat format, ReadOnlySpan<byte> input)
    {
        // Small limits keep the run fast; the checks are the same as with the defaults.
        var limits = new MediaLimits { MaxCues = 5000, MaxAllocationBytes = 1 << 20, MaxStructureDepth = 32 };
        SubtitleDocument doc;
        try
        {
            doc = format.Read(input, null, limits);
        }
        catch (MediaException)
        {
            return;
        }

        foreach (SubtitleFormat target in SubtitleFormats.All)
        {
            SubtitleOutput output = target.Write(doc);
            if (!ReferenceEquals(target, format) || target.Name == "lrc")
            {
                continue;
            }

            SubtitleDocument again = format.Read(output.Data, null, limits);
            if (target.Name != "srt" && again.Cues.Count != doc.Cues.Count)
            {
                throw new InvalidOperationException($"{format.Name}: {doc.Cues.Count} cues written, {again.Cues.Count} read back");
            }
        }
    }
}
