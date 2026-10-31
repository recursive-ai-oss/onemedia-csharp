using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Codecs;

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 1 && args[0] is "--version" or "-v")
    {
        string? version = typeof(MediaFormatRegistry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Console.WriteLine(version?.Split('+')[0] ?? "unknown");
        return 0;
    }

    if (args.Length >= 2 && args[0] == "info")
    {
        bool json = args.Contains("--json");
        string? path = args.Skip(1).FirstOrDefault(a => a != "--json");
        if (path is not null)
        {
            return Info(path, json);
        }
    }

    Console.Error.WriteLine("usage: onemedia --version");
    Console.Error.WriteLine("       onemedia info <file> [--json]   format, duration, tracks and defects of a media file");
    return 2;
}

static int Info(string path, bool json)
{
    try
    {
        using MediaFile media = Media.Open(path);
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

static string Describe(MediaTrack t) => CodecParameterReader.TryDescribe(t.Codec, t.Configuration, out CodecParameters? p) && p?.Rfc6381 is { } s ? s : "";

static void WriteText(MediaFile media)
{
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"format: {media.Format}"));
    Console.WriteLine("duration: " + Time(media.ContainerDuration));
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
                sb.Append(CultureInfo.InvariantCulture, $" {v.FrameRateNumerator}/{v.FrameRateDenominator} fps");
            }
        }

        if (t.Audio is { } a)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {a.SampleRate} Hz {a.Channels} ch");
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

    foreach (MediaDefect d in media.Defects)
    {
        Console.WriteLine("defect: " + d);
    }
}

static void WriteJson(MediaFile media)
{
    using Stream stdout = Console.OpenStandardOutput();
    using Utf8JsonWriter w = new(stdout, new JsonWriterOptions { Indented = true });
    w.WriteStartObject();
    w.WriteString("format", media.Format.Name);
    if (media.ContainerDuration.IsKnown)
    {
        w.WriteNumber("durationTicks", media.ContainerDuration.Ticks);
        w.WriteNumber("durationTimescale", media.ContainerDuration.Timescale);
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

        w.WriteNumber("timescale", t.Timescale);
        if (t.Video is { } v)
        {
            w.WriteNumber("width", v.Width);
            w.WriteNumber("height", v.Height);
            w.WriteNumber("rotation", v.RotationDegrees);
        }

        if (t.Audio is { } a)
        {
            w.WriteNumber("sampleRate", a.SampleRate);
            w.WriteNumber("channels", a.Channels);
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
