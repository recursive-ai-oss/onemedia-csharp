using System;
using System.IO;
using System.Globalization;
using System.Reflection;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;
using RecursiveAI.OneMedia.Sniffing;

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 1 && args[0] is "--version" or "-v")
    {
        string? version = typeof(MediaFormatRegistry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Console.WriteLine(version?.Split('+')[0] ?? "unknown");
        return 0;
    }

    if (args.Length == 2 && args[0] == "info")
    {
        return Info(args[1]);
    }

    Console.Error.WriteLine("usage: onemedia --version");
    Console.Error.WriteLine("       onemedia info <file>    names the container format by its content");
    return 2;
}

static int Info(string path)
{
    MediaFormatRegistry registry = new();
    registry.Add(new IsoBmffFormat());
    registry.Add(new EbmlSniffer());
    registry.Add(new WavSniffer());
    registry.Add(new AviSniffer());
    registry.Add(new AsfSniffer());
    registry.Add(new MpegTsSniffer());
    registry.Add(new MpegPsSniffer());
    registry.Add(new FlvSniffer());
    registry.Add(new OggSniffer());
    registry.Add(new FlacSniffer());
    registry.Add(new AiffSniffer());
    registry.Add(new CafSniffer());
    registry.Add(new Id3Sniffer());
    registry.Add(new MpegAudioSniffer());
    registry.Add(new AdtsSniffer());
    registry.Add(new AmrSniffer());
    registry.Add(new IvfSniffer());

    try
    {
        using FileMediaSource source = FileMediaSource.Open(path);
        if (!registry.TrySniff(source, out FormatMatch match))
        {
            Console.WriteLine("format: unknown");
            return 1;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"format: {match.Format.Id} ({match.Format.DisplayName})"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"confidence: {match.Result.Confidence}"));
        if (match.Result.Detail is not null)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"detail: {match.Result.Detail}"));
        }

        return 0;
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
    catch (UnauthorizedAccessException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}
