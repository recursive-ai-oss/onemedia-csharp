using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Tags.Id3;
using RecursiveAI.OneMedia.TimedEvents;

// Usage: EventsCheck <file> [--kind tmcd|scte|id3|emsg|all]
// Prints one line per event in a stable text form that tools/events-compare.py compares with the oracles' output.
string path = args[0];
string kind = "all";
for (int i = 1; i + 1 < args.Length; i++)
{
    if (args[i] == "--kind")
    {
        kind = args[i + 1];
    }
}

for (int i = 1; i + 1 < args.Length; i++)
{
    if (args[i] == "--remux")
    {
        // Remux first (container chosen by the output's extension), then report the events of the result.
        RecursiveAI.OneMedia.Remuxing.RemuxReport report = Media.Remux(path, args[i + 1]);
        Console.Error.WriteLine($"remux losses: {report.Losses.Count}");
        path = args[i + 1];
    }
}

using MediaFile file = MediaFile.Open(FileMediaSource.Open(path));
CultureInfo inv = CultureInfo.InvariantCulture;
foreach (EventChannel c in file.Events)
{
    string scheme = c.Scheme;
    if (c.Kind == MediaEventKind.Timecode && (kind is "all" or "tmcd"))
    {
        TimecodeSampleEntry e = c.Timecode!;
        Console.WriteLine(string.Create(inv, $"tmcd-entry track={c.Track!.Id} drop={(e.IsDropFrame ? 1 : 0)} fps={e.FramesPerSecond} timescale={e.Timescale} frameduration={e.FrameDuration} name={e.SourceName}"));
        foreach (MediaEvent ev in c.Events)
        {
            TimecodeEvent t = (TimecodeEvent)ev;
            Console.WriteLine(string.Create(inv, $"tmcd frame={t.FrameNumber} start={t.Start} time={t.Time.Ticks}/{t.Time.Timescale}"));
        }
    }
    else if (c.Kind == MediaEventKind.Scte35 && (kind is "all" or "scte"))
    {
        foreach (MediaEvent ev in c.Events)
        {
            Scte35Event s = (Scte35Event)ev;
            Console.WriteLine(Scte(s));
        }
    }
    else if (c.Kind == MediaEventKind.TimedId3 && (kind is "all" or "id3"))
    {
        foreach (MediaEvent ev in c.Events)
        {
            TimedId3Event t = (TimedId3Event)ev;
            StringBuilder frames = new();
            foreach (Id3Frame f in t.Tag.Frames)
            {
                frames.Append(f.Id).Append('=').Append(string.Join("|", f.Values)).Append(';');
            }

            Console.WriteLine(string.Create(inv, $"id3 pts={t.Time.Ticks}/{t.Time.Timescale} ver=2.{t.Tag.Header.MajorVersion} size={t.Tag.Header.TotalLength} frames={frames}"));
        }

        if (c.Track is not null)
        {
            // The packets themselves, for comparison with ffprobe's -show_packets -show_data_hash.
            foreach (OwnedPacket p in file.Packets(c.Track))
            {
                using (p)
                {
                    Console.WriteLine(string.Create(inv, $"id3-packet pts={p.Info.Pts.Ticks} size={p.Data.Length} sha256={Convert.ToHexString(SHA256.HashData(p.Data.Span)).ToLowerInvariant()}"));
                }
            }
        }
    }
    else if (kind is "all" or "emsg")
    {
        if (c.Track is null)
        {
            foreach (MediaEvent ev in c.Events)
            {
                Console.WriteLine(string.Create(inv, $"emsg scheme={c.Scheme} value={c.Value} kind={c.Kind} time={ev.Time.Ticks}/{ev.Time.Timescale} duration={(ev.Duration.IsKnown ? ev.Duration.Ticks + "/" + ev.Duration.Timescale : "unknown")} id={Id(ev)} data={Convert.ToHexString(Data(ev).Span)} at={ev.Range.Offset}"));
            }
        }
    }
}

return 0;

static ReadOnlyMemory<byte> Data(MediaEvent ev) => ev switch
{
    Scte35Event { Message: { } m } => m.MessageData,
    TimedId3Event { Message: { } m } => m.MessageData,
    MessageEvent m => m.Message.MessageData,
    _ => default,
};

static uint Id(MediaEvent ev) => ev switch
{
    Scte35Event { Message: { } m } => m.Id,
    TimedId3Event { Message: { } m } => m.Id,
    MessageEvent m => m.Message.Id,
    _ => 0,
};

static string Scte(Scte35Event e)
{
    Scte35Section s = e.Section;
    CultureInfo inv = CultureInfo.InvariantCulture;
    StringBuilder b = new();
    b.Append(inv, $"scte crc={(s.CrcValid ? "ok" : "bad")} adj={s.PtsAdjustment} tier={s.Tier} cw={s.CwIndex} time={e.Time.Ticks}/{e.Time.Timescale} specified={(e.IsSpliceTimeSpecified ? 1 : 0)} |");
    switch (s.Command)
    {
        case Scte35EmptyCommand:
            b.Append(s.CommandTypeCode == 0 ? " null" : " bw");
            break;
        case Scte35PrivateCommand p:
            b.Append(inv, $" priv id={p.Identifier} data={Convert.ToHexString(p.Data.Span)}");
            break;
        case Scte35SpliceInsert i:
            b.Append(inv, $" insert evt={i.EventId} cancel={(i.IsCancel ? 1 : 0)}");
            if (!i.IsCancel)
            {
                b.Append(inv, $" oon={(i.OutOfNetwork ? 1 : 0)} imm={(i.SpliceImmediate ? 1 : 0)} prog={(i.ProgramSplice ? 1 : 0)}");
                if (i is { ProgramSplice: true, SpliceTime.IsSpecified: true })
                {
                    b.Append(inv, $" pts={i.SpliceTime.PtsTime}");
                }

                if (i.BreakDuration is { } d)
                {
                    b.Append(inv, $" dur={d.Duration} auto={(d.AutoReturn ? 1 : 0)}");
                }

                b.Append(inv, $" uid={i.UniqueProgramId} avail={i.AvailNum} exp={i.AvailsExpected}");
            }

            break;
        case Scte35TimeSignal t:
            b.Append(" ts");
            if (t.SpliceTime.IsSpecified)
            {
                b.Append(inv, $" pts={t.SpliceTime.PtsTime}");
            }

            break;
        case Scte35SpliceSchedule sch:
            b.Append(" sched");
            foreach (Scte35ScheduledSplice x in sch.Splices)
            {
                b.Append(inv, $" [evt={x.EventId} cancel={(x.IsCancel ? 1 : 0)}");
                if (!x.IsCancel)
                {
                    b.Append(inv, $" oon={(x.OutOfNetwork ? 1 : 0)} utc={x.UtcSpliceTime}");
                    if (x.BreakDuration is { } d)
                    {
                        b.Append(inv, $" dur={d.Duration} auto={(d.AutoReturn ? 1 : 0)}");
                    }

                    b.Append(inv, $" uid={x.UniqueProgramId} avail={x.AvailNum} exp={x.AvailsExpected}");
                }

                b.Append(']');
            }

            break;
        default:
            b.Append(inv, $" unknown-type={s.CommandTypeCode}");
            break;
    }

    foreach (Scte35Descriptor d in s.Descriptors)
    {
        switch (d)
        {
            case Scte35AvailDescriptor a:
                b.Append(inv, $" | avail prov={a.ProviderAvailId}");
                break;
            case Scte35SegmentationDescriptor g:
                b.Append(inv, $" | seg evt={g.EventId} cancel={(g.IsCancel ? 1 : 0)}");
                if (!g.IsCancel)
                {
                    b.Append(inv, $" type={g.TypeId} num={g.SegmentNum} exp={g.SegmentsExpected}");
                    if (g.SubSegmentNum >= 0)
                    {
                        b.Append(inv, $" sub={g.SubSegmentNum} subexp={g.SubSegmentsExpected}");
                    }

                    if (g.Duration is long dur)
                    {
                        b.Append(inv, $" dur={dur}");
                    }

                    b.Append(inv, $" upidtype={g.UpidType} upid={Convert.ToHexString(g.Upid.Span)}");
                }

                break;
            default:
                b.Append(inv, $" | desc tag={d.Tag}");
                break;
        }
    }

    return b.ToString();
}
