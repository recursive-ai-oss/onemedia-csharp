using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Tags.Id3;
using RecursiveAI.OneMedia.TimedEvents;

namespace RecursiveAI.OneMedia.Tool;

/// <summary><c>onemedia events</c>: lists the timed events of a file (timecode, SCTE 35, ID3 timed metadata, emsg).</summary>
internal static class EventsCommand
{
    public const string Usage = "       onemedia events <file> [--json] [--limit N]   list timecode tracks, SCTE 35 splice messages, ID3 timed metadata and emsg events with their times (a transport stream is read through once; --limit caps the events shown per stream, default 200)";

    public static int Run(string[] args)
    {
        bool json = args.Contains("--json");
        int limit = 200;
        string? path = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                limit = n;
                i++;
            }
            else if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                path = args[i];
            }
        }

        if (path is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            using MediaFile media = Media.Open(path);
            if (json)
            {
                WriteJson(media, limit);
            }
            else
            {
                WriteText(media, limit);
            }

            return 0;
        }
        catch (UnsupportedMediaException ex)
        {
            Console.Error.WriteLine("unsupported: " + ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is MediaException or IOException or UnauthorizedAccessException or MediaReadStateException)
        {
            Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    /// <summary>One line per track-based stream for <c>onemedia info</c>: what it carries, where, and for timecode the start. Reads nothing beyond the listing and a timecode track's few samples; <c>emsg</c> events need a walk over the top-level boxes and are left to <c>onemedia events</c>.</summary>
    public static IEnumerable<string> Summary(MediaFile media)
    {
        foreach (EventChannel c in MediaEvents.List(media, null, includeMessages: false))
        {
            string where = c.Track is { } t ? $"track {t.Index}" : $"emsg value \"{c.Value}\"";
            string text = $"events: {Name(c.Kind)} ({c.Scheme}, {where})";
            if (c.Kind == MediaEventKind.Timecode && c.Events.Count > 0 && c.Events[0] is TimecodeEvent first)
            {
                text += $" start {first.Start}{(c.Timecode!.SourceName is { } n ? $" source \"{n}\"" : string.Empty)}";
            }
            else if (c.Track is null)
            {
                text += string.Create(CultureInfo.InvariantCulture, $" {c.Events.Count} event(s)");
            }

            yield return text;
        }
    }

    private static string Name(MediaEventKind k) => k switch
    {
        MediaEventKind.Timecode => "timecode",
        MediaEventKind.Scte35 => "scte-35",
        MediaEventKind.TimedId3 => "timed-id3",
        _ => "message",
    };

    private static string Time(MediaTime t) => t.IsKnown ? t.ToTimeSpan().ToString("c", CultureInfo.InvariantCulture) : "unknown";

    private static void WriteText(MediaFile media, int limit)
    {
        IReadOnlyList<EventChannel> channels = media.Events;
        if (channels.Count == 0)
        {
            Console.WriteLine("no timed events");
        }

        foreach (EventChannel c in channels)
        {
            string where = c.Track is { } t ? $"track {t.Index}" : $"emsg value \"{c.Value}\"";
            Console.WriteLine($"{Name(c.Kind)}: {c.Scheme}, {where}");
            if (c.Timecode is { } e)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {(e.IsDropFrame ? "drop-frame" : "non-drop")}, {e.FramesPerSecond} frames/s counted, frame duration {e.FrameDuration}/{e.Timescale} s{(e.SourceName is { } n ? $", source \"{n}\"" : string.Empty)}"));
            }

            IReadOnlyList<MediaEvent> events = c.Events;
            for (int i = 0; i < events.Count && i < limit; i++)
            {
                Console.WriteLine("  " + Time(events[i].Time) + " " + Describe(events[i]));
            }

            if (events.Count > limit)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  … {events.Count - limit} more ({events.Count} in all)"));
            }
        }

        foreach (MediaDefect d in media.Defects)
        {
            if (d.Code is { } code && (code.StartsWith("scte35.", StringComparison.Ordinal) || code.StartsWith("emsg.", StringComparison.Ordinal) || code.StartsWith("tmcd.", StringComparison.Ordinal) || code.StartsWith("id3-event.", StringComparison.Ordinal)))
            {
                Console.WriteLine("defect: " + d);
            }
        }
    }

    private static string Describe(MediaEvent ev)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        switch (ev)
        {
            case TimecodeEvent t:
                return string.Create(inv, $"{t.Start} (frame {t.FrameNumber})");
            case Scte35Event s:
            {
                StringBuilder b = new();
                Scte35Section sec = s.Section;
                b.Append(sec.Command switch
                {
                    Scte35SpliceInsert { IsCancel: true } i => string.Create(inv, $"splice_insert id=0x{i.EventId:X} cancel"),
                    Scte35SpliceInsert i => string.Create(inv, $"splice_insert id=0x{i.EventId:X} {(i.OutOfNetwork ? "out" : "in")}{(i.SpliceImmediate ? " immediate" : string.Empty)}{(i.BreakDuration is { } d ? " duration " + Time(new MediaTime(d.Duration, 90000)) + (d.AutoReturn ? " auto-return" : string.Empty) : string.Empty)} avail {i.AvailNum}/{i.AvailsExpected}"),
                    Scte35TimeSignal => "time_signal",
                    Scte35SpliceSchedule sch => string.Create(inv, $"splice_schedule {sch.Splices.Count} event(s)"),
                    Scte35PrivateCommand p => string.Create(inv, $"private_command 0x{p.Identifier:X8} {p.Data.Length} bytes"),
                    Scte35EmptyCommand => sec.CommandType == Scte35CommandType.SpliceNull ? "splice_null" : "bandwidth_reservation",
                    _ => string.Create(inv, $"command 0x{sec.CommandTypeCode:X2}"),
                });
                if (sec.IsEncrypted)
                {
                    b.Append(" (encrypted)");
                }

                if (sec.PtsAdjustment != 0)
                {
                    b.Append(string.Create(inv, $" pts_adjustment {sec.PtsAdjustment}"));
                }

                foreach (Scte35Descriptor d in sec.Descriptors)
                {
                    b.Append(d switch
                    {
                        Scte35SegmentationDescriptor g when g.IsCancel => string.Create(inv, $" segmentation 0x{g.EventId:X} cancel"),
                        Scte35SegmentationDescriptor g => string.Create(inv, $" segmentation 0x{g.EventId:X} type 0x{g.TypeId:X2} {g.SegmentNum}/{g.SegmentsExpected}{(g.Duration is long ticks ? " duration " + Time(new MediaTime(ticks, 90000)) : string.Empty)}"),
                        Scte35AvailDescriptor a => string.Create(inv, $" avail {a.ProviderAvailId}"),
                        _ => string.Create(inv, $" descriptor 0x{d.Tag:X2}"),
                    });
                }

                if (!sec.CrcValid)
                {
                    b.Append(" [CRC mismatch]");
                }

                return b.ToString();
            }

            case TimedId3Event t:
            {
                string frames = string.Join(", ", t.Tag.Frames.Select(f => f.Values.Count > 0 ? $"{f.Id}={Shorten(f.Values[0])}" : f.Id));
                return string.Create(inv, $"ID3v2.{t.Tag.Header.MajorVersion} {frames}");
            }

            case MessageEvent m:
                return string.Create(inv, $"id {m.Message.Id}, {m.Message.MessageData.Length} bytes{(m.TimeIsAbsolute ? string.Empty : " (time is a delta from the segment start)")}");
            default:
                return ev.GetType().Name;
        }
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "…";

    private static void WriteJson(MediaFile media, int limit)
    {
        using Stream stdout = Console.OpenStandardOutput();
        using Utf8JsonWriter w = new(stdout, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteStartArray("streams");
        foreach (EventChannel c in media.Events)
        {
            w.WriteStartObject();
            w.WriteString("kind", Name(c.Kind));
            w.WriteString("scheme", c.Scheme);
            if (c.Value is not null)
            {
                w.WriteString("value", c.Value);
            }

            if (c.Track is { } t)
            {
                w.WriteNumber("track", t.Index);
            }

            if (c.Timecode is { } e)
            {
                w.WriteBoolean("dropFrame", e.IsDropFrame);
                w.WriteNumber("framesPerSecond", e.FramesPerSecond);
            }

            w.WriteNumber("count", c.Events.Count);
            w.WriteStartArray("events");
            for (int i = 0; i < c.Events.Count && i < limit; i++)
            {
                MediaEvent ev = c.Events[i];
                w.WriteStartObject();
                if (ev.Time.IsKnown)
                {
                    w.WriteNumber("timeTicks", ev.Time.Ticks);
                    w.WriteNumber("timeScale", ev.Time.Timescale);
                }

                w.WriteString("text", Describe(ev));
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }
}
