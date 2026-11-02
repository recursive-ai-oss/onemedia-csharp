using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RecursiveAI.OneMedia.Captions;

// Text tracks that carry the same text as a sidecar format (SubRip blocks, ASS events, WebVTT cues, TTML documents) are read and written by
// handing small synthesised sidecar text to the sidecar readers and writers, so the cue model comes out the same whichever way a cue arrived.
// Times in the synthesised text are the real ones where the sidecar format has the precision, and are overridden from the packet otherwise.

internal static class SidecarBridge
{
    public static string Millis(MediaTime t, char separator)
    {
        long ms = t.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks;
        if (ms < 0)
        {
            ms = 0;
        }

        long h = ms / 3_600_000;
        long m = ms / 60_000 % 60;
        long s = ms / 1000 % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00}{separator}{ms % 1000:000}");
    }

    public static string DecodeText(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            data = data[3..];
        }

        return Encoding.UTF8.GetString(data);
    }

    /// <summary>Line breaks to LF, and blank lines inside one block made non-blank so that a sidecar reader sees one cue.</summary>
    public static string OneBlock(string s)
    {
        s = s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');
        while (s.Contains("\n\n", StringComparison.Ordinal))
        {
            s = s.Replace("\n\n", "\n \n", StringComparison.Ordinal);
        }

        return s.TrimStart('\n');
    }

    public static string Language(MediaTrack track) => track.Language is { Length: > 0 } l && l != "und" ? l : string.Empty;

    public static string? LanguageOrNull(MediaTrack track) => track.Language is { Length: > 0 } l && l != "und" ? l : null;

    /// <summary>The cues of a document that can sit on a timeline, in start order, as a document to write.</summary>
    public static SubtitleDocument Playable(SubtitleDocument doc, LossCounter losses, out List<SubtitleCue> cues)
    {
        cues = TextTrackTimes.Playable(doc, out int dropped);
        for (int i = 0; i < dropped; i++)
        {
            losses.Add("untimed-cue", "Cues without a start time, or whose end is not after their start, have no place in a timed text track and were left out.");
        }

        return doc with { Cues = SubtitleList.From(cues) };
    }

    public static void AddLosses(ConversionReport report, LossCounter losses)
    {
        foreach (ConversionLoss l in report.Losses)
        {
            losses.Add(l.Feature.ToString(), l.Message);
        }

        if (report.Omitted > 0)
        {
            losses.AddTrackWide("more-losses", $"{report.Omitted} further losses were not listed.");
        }
    }

    public static TextTrackPacket Packet(long pts, long duration, long timescale, byte[] bytes, byte[]? additional = null) => new(
        new PacketInfo(0, new MediaTime(pts, timescale), new MediaTime(pts, timescale), new MediaTime(duration, timescale), PacketAttributes.Keyframe, ByteRange.None, bytes.Length),
        bytes,
        additional);

    /// <summary>Packets for cues, one per cue, each starting at its cue's start; ties keep cue order.</summary>
    public static void PerCuePackets(List<SubtitleCue> cues, TextTrackOptions options, Func<int, SubtitleCue, (byte[] Data, byte[]? Additional)> make, List<TextTrackPacket> packets, out long lastEnd, out bool inexact)
    {
        inexact = false;
        lastEnd = 0;
        for (int i = 0; i < cues.Count; i++)
        {
            long s = TextTrackTimes.ToTicks(cues[i].Start, options.Timescale, ref inexact);
            long e = TextTrackTimes.ToTicks(cues[i].End, options.Timescale, ref inexact);
            if (e <= s)
            {
                e = s + 1;
            }

            (byte[] data, byte[]? additional) = make(i, cues[i]);
            packets.Add(Packet(s, e - s, options.Timescale, data, additional));
            lastEnd = Math.Max(lastEnd, e);
        }
    }

    public static MediaTrack Track(TextTrackOptions options, SubtitleDocument doc, CodecId codec, string tag, byte[] config, long lastEnd, int packets) => new()
    {
        Index = 0,
        Id = options.TrackId,
        Kind = TrackKind.Subtitle,
        Codec = codec,
        CodecTag = tag,
        Configuration = config.Length == 0 ? default : new CodecConfiguration(config),
        Timescale = options.Timescale,
        Duration = new MediaTime(lastEnd, options.Timescale),
        Language = options.Language ?? doc.Language,
        Name = options.Name,
        Disposition = options.Disposition,
        PacketCount = packets,
    };

    public static List<string> Notes(bool inexact, long timescale)
    {
        List<string> notes = [];
        if (inexact)
        {
            notes.Add($"Cue times were rounded to the track timescale of {timescale} ticks per second.");
        }

        return notes;
    }

    // WebVTT in Matroska counts the timestamps inside cue text from the start of the block (Matroska subtitles page); sidecar files count from the start of the media.
    private static readonly Regex Inner = new(@"<(?:(\d{1,}):)?(\d{2}):(\d{2})\.(\d{3})>", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string ShiftInnerTimestamps(string payload, long deltaMs)
    {
        if (deltaMs == 0 || !payload.Contains('<', StringComparison.Ordinal))
        {
            return payload;
        }

        return Inner.Replace(payload, m =>
        {
            long h = m.Groups[1].Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            long ms = (h * 3600 + long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60 + long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)) * 1000
                + long.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) + deltaMs;
            if (ms < 0)
            {
                ms = 0;
            }

            return string.Create(CultureInfo.InvariantCulture, $"<{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}>");
        });
    }

    public static long StartMs(SubtitleCue cue) => cue.Start.Rescale(1000, MediaRounding.NearestAwayFromZero).Ticks;
}

/// <summary>A WebVTT file taken apart into its header and cue blocks.</summary>
internal sealed class VttBlocks
{
    public string Header = "WEBVTT";
    public readonly List<VttCueBlock> Cues = [];

    public static VttBlocks Parse(string text)
    {
        VttBlocks result = new();
        string[] blocks = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split("\n\n", StringSplitOptions.None);
        StringBuilder header = new();
        List<string> notes = [];
        bool first = true;
        foreach (string raw in blocks)
        {
            string block = raw.Trim('\n');
            if (block.Length == 0)
            {
                continue;
            }

            bool timing = block.Contains("-->", StringComparison.Ordinal);
            if (first)
            {
                header.Append(block);
                first = false;
                if (!timing)
                {
                    continue;
                }

                // A file with no blank line after the signature: the signature line is the header.
                header.Clear();
                header.Append("WEBVTT");
            }

            if (!timing)
            {
                if (block.StartsWith("NOTE", StringComparison.Ordinal))
                {
                    notes.Add(block);
                }
                else if (result.Cues.Count == 0)
                {
                    header.Append("\n\n").Append(block);
                }

                continue;
            }

            string[] lines = block.Split('\n');
            int t = Array.FindIndex(lines, static l => l.Contains("-->", StringComparison.Ordinal));
            string id = t > 0 ? lines[0] : string.Empty;
            string timeLine = lines[t];
            int arrow = timeLine.IndexOf("-->", StringComparison.Ordinal);
            string rest = timeLine[(arrow + 3)..].Trim();
            int sp = rest.IndexOfAny([' ', '\t']);
            string settings = sp < 0 ? string.Empty : rest[(sp + 1)..].Trim();
            result.Cues.Add(new VttCueBlock(id, settings, string.Join('\n', lines, t + 1, lines.Length - t - 1), string.Join("\n", notes)));
            notes.Clear();
        }

        result.Header = header.ToString();
        return result;
    }
}

internal sealed record VttCueBlock(string Id, string Settings, string Payload, string Comments);
