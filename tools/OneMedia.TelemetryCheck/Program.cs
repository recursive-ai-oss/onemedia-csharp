using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Metadata.Telemetry;

// Usage:
//   TelemetryCheck dump <file> [out.tsv]
//       Decodes every telemetry track through the pull reader and writes one line per sample:
//       <track id> <channel> <device id> <index in channel> <time in seconds> <kind> <fix> <gps time> <values...>   (tab separated; raw items: RAW <length> <type>)
//       and a summary per channel on stderr.
//   TelemetryCheck pull <file>
//       Reads the first telemetry track twice and prints samples, bytes allocated on the second pass per sample, and the managed heap size.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: TelemetryCheck dump <file> [out.tsv] | TelemetryCheck pull <file>");
    return 2;
}

if (args[0] == "dumpsrt")
{
    // A DJI sidecar: the same line format as "dump", one channel after the other.
    foreach (RecursiveAI.OneMedia.Metadata.TelemetryChannel c in RecursiveAI.OneMedia.Metadata.Telemetry.Dji.DjiSrt.Read(args[1]))
    {
        int index = 0;
        foreach (RecursiveAI.OneMedia.Metadata.TelemetrySample smp in c.Samples)
        {
            Console.WriteLine(DumpSample(-1, c.Name, 0, index++, smp));
        }
    }

    return 0;
}

using MediaFile media = Media.Open(args[1]);
List<MediaTrack> tracks = [];
foreach (MediaTrack t in media.Tracks)
{
    if (TelemetryTrackReader.FormatOf(media, t) != TelemetryFormat.None)
    {
        tracks.Add(t);
    }
}

if (tracks.Count == 0)
{
    Console.Error.WriteLine("no telemetry track");
    return 1;
}

if (args[0] == "packets")
{
    // The demuxer alone, for comparison with "pull": the same track, packets only.
    for (int pass = 0; pass < 3; pass++)
    {
        foreach (MediaTrack t in media.Demuxer!.Tracks)
        {
            media.Demuxer.SetTrackEnabled(t.Index, t.Index == tracks[0].Index);
        }

        if (pass > 0)
        {
            media.Demuxer.Seek(new MediaTime(-(1L << 30), 1), SeekMode.PreviousKeyframe);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int n = 0;
        while (media.Demuxer.TryReadPacket(out PacketView p))
        {
            n++;
        }

        Console.WriteLine($"pass {pass}: {n} packets, {GC.GetAllocatedBytesForCurrentThread() - before} bytes allocated");
    }

    return 0;
}

if (args[0] == "pull")
{
    MediaTrack track = tracks[0];
    long samples = 0, total = 0;
    for (int pass = 0; pass < 2; pass++)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        samples = 0;
        using TelemetryTrackReader r = media.OpenTelemetry(track);
        double sum = 0;
        long mark = 0, markSamples = 0, expected = pass == 0 ? long.MaxValue : total / 4;
        while (r.TryRead(out TelemetryItem item))
        {
            samples++;
            if (samples == expected)
            {
                mark = GC.GetAllocatedBytesForCurrentThread();
                markSamples = samples;
            }

            if (item.Count > 0)
            {
                sum += item.Values[0];
            }
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"pass {pass}: {samples} samples, {allocated} bytes allocated including buffer growth; after the first quarter: {(pass == 0 ? 0 : GC.GetAllocatedBytesForCurrentThread() - mark)} bytes over {samples - markSamples} samples, channels {r.Channels.Count}, checksum {sum:R}");
        total = samples;
    }

    Console.WriteLine($"heap {GC.GetTotalMemory(false) / 1024} KiB, working set {Environment.WorkingSet / 1024} KiB");
    return 0;
}

using TextWriter output = args.Length > 2 ? new StreamWriter(args[2], false, new UTF8Encoding(false)) : Console.Out;
foreach (MediaTrack track in tracks)
{
    using TelemetryTrackReader reader = media.OpenTelemetry(track);
    Dictionary<int, int> counts = [];
    StringBuilder sb = new();
    while (reader.TryRead(out TelemetryItem item))
    {
        TelemetryChannelInfo ch = reader.Channels[item.ChannelIndex];
        counts[item.ChannelIndex] = counts.GetValueOrDefault(item.ChannelIndex) + 1;
        sb.Clear();
        sb.Append(track.Id).Append('\t').Append(ch.Name).Append('\t').Append(ch.DeviceId).Append('\t').Append(counts[item.ChannelIndex] - 1).Append('\t');
        sb.Append(item.Time.IsKnown ? ((decimal)item.Time.Ticks / item.Time.Timescale).ToString("0.000000", CultureInfo.InvariantCulture) : "?").Append('\t');
        if (item.IsRaw)
        {
            sb.Append("RAW\t").Append(item.RawLength).Append('\t').Append(item.TypeCode >= 32 ? ((char)item.TypeCode).ToString() : item.TypeCode.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append(item.Kind).Append('\t').Append((int)item.Fix).Append('\t');
            sb.Append(item.GpsTimeTicks > 0 ? new DateTime(item.GpsTimeTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) : "-");
            foreach (double v in item.Values)
            {
                sb.Append('\t').Append(v.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        output.WriteLine(sb.ToString());
    }

    foreach (TelemetryChannelInfo ch in reader.Channels)
    {
        Console.Error.WriteLine($"track {track.Id} {reader.Format} {ch.Name} dev {ch.DeviceId} kind {ch.Kind} samples {counts.GetValueOrDefault(ch.Index)} label '{ch.Label}' units [{string.Join(",", ch.Units)}] axes '{ch.AxisOrder}' out '{ch.AxisOutputOrder}'");
    }
}

return 0;

static string DumpSample(long track, string channel, uint device, int index, RecursiveAI.OneMedia.Metadata.TelemetrySample s)
{
    StringBuilder sb = new();
    sb.Append(track).Append('\t').Append(channel).Append('\t').Append(device).Append('\t').Append(index).Append('\t');
    sb.Append(s.Time.IsKnown ? ((decimal)s.Time.Ticks / s.Time.Timescale).ToString("0.000000", CultureInfo.InvariantCulture) : "?").Append('\t');
    double[] v = s switch
    {
        RecursiveAI.OneMedia.Metadata.GpsSample g => [g.Latitude, g.Longitude, g.Altitude ?? double.NaN],
        RecursiveAI.OneMedia.Metadata.ScalarSample sc => [.. sc.Values],
        RecursiveAI.OneMedia.Metadata.CameraSettingsSample c => [c.Iso ?? double.NaN, c.ExposureTime is { } e ? (double)e.Ticks / e.Timescale : double.NaN, c.FNumber ?? double.NaN, c.ExposureCompensationEv ?? double.NaN, c.ColorTemperatureKelvin ?? double.NaN, c.FocalLengthMillimetres ?? double.NaN],
        RecursiveAI.OneMedia.Metadata.OrientationSample o => [o.YawDegrees ?? double.NaN, o.PitchDegrees ?? double.NaN, o.RollDegrees ?? double.NaN],
        _ => [],
    };
    sb.Append(s.GetType().Name).Append("\t0\t-");
    foreach (double d in v)
    {
        sb.Append('\t').Append(d.ToString("R", CultureInfo.InvariantCulture));
    }

    return sb.ToString();
}
