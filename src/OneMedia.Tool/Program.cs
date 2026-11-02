using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Codecs;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tool;

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 1 && args[0] is "--version" or "-v")
    {
        string? version = typeof(MediaFormatRegistry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Console.WriteLine(version?.Split('+')[0] ?? "unknown");
        return 0;
    }

    if (args.Length >= 1 && args[0] == "remux")
    {
        return RemuxCommand.Run(args[1..]);
    }

    if (args.Length >= 1 && args[0] == "repair")
    {
        return RepairCommand.Run(args[1..]);
    }

    if (args.Length >= 1 && args[0] == "package")
    {
        return PackageCommand.Run(args[1..]);
    }

    if (args.Length >= 1 && args[0] == "playable")
    {
        return PlayableCommand.Run(args[1..]);
    }

    if (args.Length >= 2 && args[0] == "info")
    {
        bool json = args.Contains("--json");
        bool sidecars = !args.Contains("--no-sidecars");
        bool telemetry = args.Contains("--telemetry");
        bool fileTimes = args.Contains("--file-times");
        string? path = args.Skip(1).FirstOrDefault(a => a is not ("--json" or "--no-sidecars" or "--telemetry" or "--file-times"));
        if (path is not null)
        {
            return Info(path, json, sidecars, telemetry, fileTimes);
        }
    }

    if (args.Length >= 2 && args[0] == "tags")
    {
        return TagsCommand.Run(args[1..]);
    }

    if (args.Length >= 1 && args[0] == "subs")
    {
        return SubsCommand.Run(args[1..]);
    }

    if (args.Length >= 1 && args[0] == "events")
    {
        return EventsCommand.Run(args[1..]);
    }

    if (args.Length >= 1 && args[0] == "strip")
    {
        return StripCommand.Run(args[1..]);
    }

    if (RecursiveAI.OneMedia.Tool.EditCommands.Handles(args))
    {
        return RecursiveAI.OneMedia.Tool.EditCommands.Run(args);
    }

    Console.Error.WriteLine("usage: onemedia --version");
    Console.Error.WriteLine("       onemedia info <file> [--json] [--no-sidecars] [--telemetry] [--file-times]   format, duration, tracks, facts with every candidate, and defects (sidecars beside the file count as evidence; --telemetry also decodes GPS telemetry into the facts; --file-times adds the file's last-write time as a last-resort capture time)");
    Console.Error.WriteLine(RemuxCommand.Usage);
    Console.Error.WriteLine(PlayableCommand.Usage);
    Console.Error.WriteLine(RepairCommand.Usage);
    Console.Error.WriteLine(PackageCommand.Usage);
    Console.Error.WriteLine("       onemedia tags <file> [--set field=value]... [--remove field]... [--cover image [--cover-type name]] [--remove-covers]");
    Console.Error.WriteLine("                     [--system name]... [--out new-file | --replace | --in-place]   show or change tags (without an output option: show the plan)");
    Console.Error.WriteLine("       onemedia tags <file> --check [--absent field,field]   look for leftover bytes in ID3v2 padding (exit 1 if found; with no edits, --replace/--out/--in-place zero them);");
    Console.Error.WriteLine("                                                         --absent also exits 1 and names each listed field that is still present in any tag system");
    Console.Error.WriteLine("       onemedia tags <file> --capture-time <iso8601> [--header-times utc|local] [--out new-file | --replace | --in-place]   set the capture time in every date field");
    Console.Error.WriteLine(RecursiveAI.OneMedia.Tool.EditCommands.Usage);
    Console.Error.WriteLine(StripCommand.Usage);
    Console.Error.WriteLine(SubsCommand.Usage);
    Console.Error.WriteLine(EventsCommand.Usage);
    return 2;
}

static int Info(string path, bool json, bool sidecars, bool telemetry, bool fileTimes)
{
    try
    {
        using MediaFile media = Media.Open(path);
        // The last-write time changes with every copy or save, so it is offered as evidence only when asked for (--file-times).
        media.UseFileSystemTime = fileTimes;
        if (sidecars)
        {
            media.AddSidecarsBesideFile();
        }

        if (telemetry)
        {
            // Decodes every telemetry sample into memory, so only on request; the GPS time and first fix become capture-time and location candidates.
            _ = media.Telemetry;
        }

        if (json)
        {
            WriteJson(media);
        }
        else
        {
            WriteText(media);
        }

        return 0;
    }
    catch (UnsupportedMediaException ex)
    {
        Console.Error.WriteLine("unsupported: " + ex.Message);
        return 1;
    }
    catch (MediaException ex)
    {
        Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
        return 1;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static string Time(MediaTime t) => t.IsKnown ? t.ToTimeSpan().ToString("c", CultureInfo.InvariantCulture) : "unknown";

static string Describe(MediaTrack t) => t.Rfc6381Codec() ?? "";

static void WriteText(MediaFile media)
{
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"format: {media.Format}"));
    Console.WriteLine("duration: " + Time(media.ExactDuration));
    if (media.ContainerDuration.IsKnown && media.ContainerDuration != media.ExactDuration)
    {
        Console.WriteLine("container duration: " + Time(media.ContainerDuration));
    }

    foreach (MediaTrack t in media.Tracks)
    {
        StringBuilder sb = new();
        sb.Append(CultureInfo.InvariantCulture, $"track {t.Index}: {t.Kind.ToString().ToLowerInvariant()} {(t.Codec.IsUnknown ? "unknown" : t.Codec.Name)}");
        string rfc = Describe(t);
        if (rfc.Length > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" ({rfc})");
        }

        if (t.CodecTag is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $" [{t.CodecTag}]");
        }

        if (t.Video is { } v)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {v.Width}x{v.Height}");
            if (v.RotationDegrees != 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $" rotate {v.RotationDegrees}");
            }

            if (v.FrameRateNumerator > 0 && v.FrameRateDenominator > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $" {CliFormat.FrameRate(v.FrameRateNumerator, v.FrameRateDenominator)} fps");
            }
        }

        if (t.Audio is { } a)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {a.SampleRate} Hz {a.Channels} ch");
            if (CliFormat.HeAac(t) is { } heaac)
            {
                sb.Append(CultureInfo.InvariantCulture, $" [{heaac}]");
            }
        }

        if (t.Name is { Length: > 0 } name)
        {
            sb.Append(CultureInfo.InvariantCulture, $" name=\"{name}\"");
        }

        if (t.Language is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $" lang={t.Language}");
        }

        if (t.Disposition != TrackDisposition.None)
        {
            sb.Append(CultureInfo.InvariantCulture, $" disposition={t.Disposition}");
        }

        if (t.IsProtected)
        {
            sb.Append(" protected");
        }

        Console.WriteLine(sb.ToString());
    }

    foreach (string line in EventsCommand.Summary(media))
    {
        Console.WriteLine(line);
    }

    foreach (CommonField field in Enum.GetValues<CommonField>())
    {
        foreach (CommonValue v in media.Tags.Values(field))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"tag {field}: {v.Text}"));
        }
    }

    WriteFactText("captureTime", media.CaptureTime, v => v.ToString());
    WriteFactText("location", media.Location, ShowLocation);
    WriteFactText("make", media.Device.Make, v => v);
    WriteFactText("model", media.Device.Model, v => v);
    WriteFactText("software", media.Device.Software, v => v);
    WriteFactText("serial", media.Device.Serial, v => v);
    WriteFactText("rotation", media.Rotation, ShowRotation);

    foreach (MediaDefect d in media.Defects)
    {
        Console.WriteLine("defect: " + d);
    }
}

static string ShowLocation(GeoLocation v) => string.Create(CultureInfo.InvariantCulture, $"{v.Latitude:0.######},{v.Longitude:0.######}") + (v.Altitude is { } alt ? string.Create(CultureInfo.InvariantCulture, $",{alt:0.##}") : "");

static string ShowRotation(VideoOrientation v) => v.RotationDegrees.ToString(CultureInfo.InvariantCulture) + (v.MirrorHorizontal ? " mirrored" : "");

static void WriteFactText<T>(string name, Fact<T> fact, Func<T, string> show)
{
    if (fact.Best is { } best)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: {show(best.Value)} ({best.Source}, {fact.Candidates.Count} candidate(s))"));
        if (fact.Resolution is { } why)
        {
            Console.WriteLine($"  why: {why.Reason}");
        }

        if (fact.Candidates.Count > 1)
        {
            foreach (FactCandidate<T> c in fact.Candidates)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {(ReferenceEquals(c, best) ? "*" : "-")} {show(c.Value)} from {c.Source} (raw \"{c.RawValue}\", {c.Certainty})"));
            }
        }
    }
    else
    {
        Console.WriteLine(name + ": none");
    }
}

static void WriteFactJson<T>(Utf8JsonWriter w, string name, Fact<T> fact, Func<T, string> show)
{
    if (!fact.HasValue)
    {
        w.WriteNull(name);
        return;
    }

    w.WriteStartObject(name);
    w.WriteString("value", show(fact.Best!.Value));
    if (fact.Resolution is { } why)
    {
        w.WriteString("rule", why.Rule);
        w.WriteString("reason", why.Reason);
    }

    w.WriteStartArray("candidates");
    foreach (FactCandidate<T> c in fact.Candidates)
    {
        w.WriteStartObject();
        w.WriteString("value", show(c.Value));
        w.WriteString("source", c.Source);
        w.WriteString("raw", c.RawValue);
        w.WriteString("interpretation", c.Interpretation);
        w.WriteString("timezone", c.Certainty.ToString());
        if (!c.Range.IsNone)
        {
            w.WriteNumber("offset", c.Range.Offset);
            w.WriteNumber("length", c.Range.Length);
        }

        w.WriteEndObject();
    }

    w.WriteEndArray();
    w.WriteEndObject();
}

static void WriteJson(MediaFile media)
{
    using Stream stdout = Console.OpenStandardOutput();
    using Utf8JsonWriter w = new(stdout, new JsonWriterOptions { Indented = true });
    w.WriteStartObject();
    w.WriteString("format", media.Format.Name);
    if (media.ExactDuration.IsKnown)
    {
        w.WriteNumber("durationTicks", media.ExactDuration.Ticks);
        w.WriteNumber("durationTimescale", media.ExactDuration.Timescale);
    }

    if (media.ContainerDuration.IsKnown)
    {
        w.WriteNumber("containerDurationTicks", media.ContainerDuration.Ticks);
        w.WriteNumber("containerDurationTimescale", media.ContainerDuration.Timescale);
    }

    w.WriteStartArray("tracks");
    foreach (MediaTrack t in media.Tracks)
    {
        w.WriteStartObject();
        w.WriteNumber("index", t.Index);
        w.WriteString("kind", t.Kind.ToString().ToLowerInvariant());
        w.WriteString("codec", t.Codec.Name);
        if (t.CodecTag is not null)
        {
            w.WriteString("codecTag", t.CodecTag);
        }

        string rfc = Describe(t);
        if (rfc.Length > 0)
        {
            w.WriteString("rfc6381", rfc);
        }

        if (t.Name is { Length: > 0 } name)
        {
            w.WriteString("name", name);
        }

        w.WriteNumber("timescale", t.Timescale);
        if (t.Video is { } v)
        {
            w.WriteNumber("width", v.Width);
            w.WriteNumber("height", v.Height);
            w.WriteNumber("rotation", v.RotationDegrees);
            if (v.FrameRateNumerator > 0 && v.FrameRateDenominator > 0)
            {
                (long fn, long fd) = CliFormat.FrameRateOf(v.FrameRateNumerator, v.FrameRateDenominator);
                w.WriteNumber("frameRateNumerator", fn);
                w.WriteNumber("frameRateDenominator", fd);
            }
        }

        if (t.Audio is { } a)
        {
            w.WriteNumber("sampleRate", a.SampleRate);
            w.WriteNumber("channels", a.Channels);
            if (CliFormat.HeAac(t) is { } heaac)
            {
                w.WriteString("aacNote", heaac);
            }
        }

        if (t.Language is not null)
        {
            w.WriteString("language", t.Language);
        }

        w.WriteString("disposition", t.Disposition.ToString());
        w.WriteBoolean("protected", t.IsProtected);
        w.WriteEndObject();
    }

    w.WriteEndArray();
    w.WriteStartArray("tags");
    foreach (CommonField field in Enum.GetValues<CommonField>())
    {
        foreach (CommonValue v in media.Tags.Values(field))
        {
            w.WriteStartObject();
            w.WriteString("field", field.ToString());
            w.WriteString("text", v.Text);
            w.WriteEndObject();
        }
    }

    w.WriteEndArray();
    w.WriteStartObject("facts");
    WriteFactJson(w, "captureTime", media.CaptureTime, v => v.ToString());
    WriteFactJson(w, "location", media.Location, ShowLocation);
    WriteFactJson(w, "make", media.Device.Make, v => v);
    WriteFactJson(w, "model", media.Device.Model, v => v);
    WriteFactJson(w, "software", media.Device.Software, v => v);
    WriteFactJson(w, "serial", media.Device.Serial, v => v);
    WriteFactJson(w, "rotation", media.Rotation, ShowRotation);
    w.WriteEndObject();
    w.WriteStartArray("defects");
    foreach (MediaDefect d in media.Defects)
    {
        w.WriteStartObject();
        w.WriteString("kind", d.Kind.ToString());
        w.WriteString("severity", d.Severity.ToString());
        w.WriteNumber("offset", d.Offset);
        w.WriteString("message", d.Message);
        w.WriteEndObject();
    }

    w.WriteEndArray();
    w.WriteEndObject();
    w.Flush();
    Console.WriteLine();
}
