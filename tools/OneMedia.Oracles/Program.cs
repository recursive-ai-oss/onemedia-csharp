using System.Globalization;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>
/// Differential and I/O harness. Commands: diff, structure, io, meta, rangeserver, selftest, list.
/// Run with no arguments for usage.
/// </summary>
internal static class Program
{
    private const string Usage = """
        OneMedia.Oracles: packet differential, structure listing and I/O harness for the reference tools.

          diff [--family isobmff|matroska|asf|all] [--root DIR] [--out DIR] [--exceptions FILE]
               [--jobs N] [--budget MINUTES] [--resume] [--audio] [FILES...]
              Compare our demuxer's packets with ffprobe's (track, pts, dts, duration, size, pos, key, payload hash).
              --audio adds OneAudio's Mp4PacketSource (MP4/3GP) and Ogg comparisons for audio tracks.
              --budget stops starting files after N minutes and exits 75 when work remains (rerun with --resume).
          structure [--family F] [FILES...]    Compare box and element listings with MP4Box and mkvinfo.
          io [--family F] [--root DIR] [--out DIR] [--no-http] [--no-strace] [--metadata] [FILES...]
              Bytes read and request counts to probe each file: ffprobe, mediainfo, exiftool and our reader.
          infocheck [--family F] [FILES...]
              Compare Media.Open (tracks, codecs, sizes, rates, languages, duration) with ffprobe.
          audiodecode [--family F] [--out DIR] [FILES...]
              Decode the first audio stream through the OneMedia adapter and through OneAudio's own path; compare samples.
          drain FILES...                       Read every packet through Media.Open; packets, bytes, allocation after warm-up, peak working set.
          packetset FILES...                   Compare which stored samples (file position and size) ffprobe and we list, ignoring time.
          audiopcm FILE OUT.f32                Decode the first audio stream through the OneMedia adapter into raw little-endian float32.
          meta [--family F] [--out DIR] [FILES...]
              Compare our tags and basic facts with ExifTool and MediaInfo field by field; writes metadata-compare.tsv.
          atlcompare [--family F] [--out DIR] [FILES...]
              Compare the common tag view with ATL.NET field by field; writes atl-compare.tsv.
          rangeserver DIR [--port N]           Serve DIR with HTTP range support until Enter is pressed.
          selftest [FILES...]                  Check the harness against exact and deliberately wrong fake formats.
          list [--family F] [--root DIR]       Print the files a family selects.

        Tools are found under $ONEMEDIA_HOME/oracles/bin; the corpus under $ONEMEDIA_HOME/corpus.
        """;

    private static readonly string[] IsoExtensions = [".mp4", ".m4a", ".m4v", ".mov", ".3gp", ".3g2", ".ismv", ".qt"];
    private static readonly string[] OggExtensions = [".ogg", ".opus", ".oga"];
    private static readonly string[] MatroskaExtensions = [".mkv", ".mka", ".webm", ".mk3d", ".mks"];

    private static readonly string[] AsfExtensions = [".asf", ".wma", ".wmv", ".wm", ".dvr-ms"];

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        Options o = Options.Parse(args.Skip(1));
        switch (args[0])
        {
            case "diff": return Diff(o);
            case "structure": return Structure(o);
            case "io": return Io(o);
            case "infocheck": return InfoCheck.Run(o.Files.Count > 0 ? o.Files : Select(o, o.Family), o.Exceptions is null ? Oracles.Exceptions.None : Oracles.Exceptions.Load(o.Exceptions));
            case "audiodecode": return AudioDecodeCheck.Run(o.Files.Count > 0 ? o.Files : Select(o, o.Family), Path.Combine(o.Out, "audiodecode-work"));
            case "drain": return DrainCheck.Run(o.Files);
            case "packetset": return PacketSets.Run(o.Files);
            case "atlcompare": return AtlCompare.Run(o.Files.Count > 0 ? [.. o.Files.Select(Path.GetFullPath)] : Select(o, o.Family), o.Out);
            case "asfinfo": return AsfInfo.Run(o.Files);
            case "asfseek": return AsfSeekCheck.Run(o.Files.Count > 0 ? o.Files : Select(o, "asf"));
            case "atl": return AtlSummary.Run(o.Files);
            case "oneaudio-ogg": return OggCompare.Run(o.Files);
            case "defects": return AllocCheck.Defects(o.Files);
            case "alloc": return AllocCheck.Run(o.Files.Count > 0 ? o.Files : Select(o, o.Family));
            case "audiopcm": return AudioDecodeCheck.DumpPcm(o.Files[0], o.Files[1]);
            case "meta": return MetadataCompare.Run(o.Files.Count > 0 ? [.. o.Files.Select(Path.GetFullPath)] : Select(o, o.Family), o.Out);
            case "metadump": return MetadataCompare.Dump(o.Files.Select(Path.GetFullPath).ToList());
            case "mapping-doc": return MetadataCompare.WriteMappingDoc(o.Out);
            case "rangeserver": return Serve(o);
            case "selftest": return SelfTest.Run(o.Files.Count > 0 ? o.Files : Select(o, "isobmff").Take(3).Concat(Select(o, "matroska").Take(3)).ToList());
            case "list":
                foreach (string f in Select(o, o.Family)) { Console.WriteLine(f); }
                return 0;
            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }

    private sealed class Options
    {
        public string Family { get; private set; } = "all";
        public string Root { get; private set; } = Oracle.CorpusRoot;
        public string Out { get; private set; } = "oracle-results";
        public string? Exceptions { get; private set; }
        public int Jobs { get; private set; } = 4;
        public double BudgetMinutes { get; private set; }
        public bool Resume { get; private set; }
        public bool Audio { get; private set; }
        public bool Http { get; private set; } = true;
        public bool Strace { get; private set; } = true;
        public bool Metadata { get; private set; }
        public int Port { get; private set; }
        public List<string> Files { get; } = [];

        public static Options Parse(IEnumerable<string> args)
        {
            Options o = new();
            using IEnumerator<string> e = args.GetEnumerator();
            string Next() => e.MoveNext() ? e.Current : throw new ArgumentException("missing option value");
            while (e.MoveNext())
            {
                switch (e.Current)
                {
                    case "--family": o.Family = Next(); break;
                    case "--root": o.Root = Next(); break;
                    case "--out": o.Out = Next(); break;
                    case "--exceptions": o.Exceptions = Next(); break;
                    case "--jobs": o.Jobs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--budget": o.BudgetMinutes = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--resume": o.Resume = true; break;
                    case "--audio": o.Audio = true; break;
                    case "--no-http": o.Http = false; break;
                    case "--no-strace": o.Strace = false; break;
                    case "--metadata": o.Metadata = true; break;
                    default: o.Files.Add(e.Current); break;
                }
            }

            return o;
        }
    }

    /// <summary>Corpus files for a family, in a stable order. Directories starting with an underscore hold sources and are skipped.</summary>
    private static List<string> Select(Options o, string family)
    {
        string[] ext = family switch
        {
            "isobmff" => IsoExtensions,
            "matroska" => MatroskaExtensions,
            "asf" => AsfExtensions,
            "mpa" => [".mp3", ".mp2", ".mpa"],
            "adts" => [".aac"],
            "ac3" => [".ac3", ".eac3", ".ec3"],
            "amr" => [".amr", ".awb"],
            "audio-es" => [".mp3", ".mp2", ".mpa", ".aac", ".ac3", ".eac3", ".ec3", ".amr", ".awb"],
            "xiph" => [".ogg", ".oga", ".ogv", ".opus", ".spx", ".flac"],
            "flv" => [".flv"],
            _ => [.. IsoExtensions, .. MatroskaExtensions, .. AsfExtensions],
        };
        List<string> files = [];
        foreach (string f in Directory.EnumerateFiles(o.Root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(o.Root, f);
            if (rel.Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('_') || p.StartsWith('.')) || rel.Contains(".part.", StringComparison.Ordinal))
            {
                continue;
            }

            if (ext.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            {
                files.Add(f);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static int Diff(Options o)
    {
        List<string> files = o.Files.Count > 0 ? o.Files : Select(o, o.Family);
        Exceptions exceptions = o.Exceptions is null ? Oracles.Exceptions.None : Oracles.Exceptions.Load(o.Exceptions);
        MediaFormatRegistry registry = Readers.Create();
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        int remaining = 0;
        int done = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = o.Jobs }, file =>
        {
            if (o.Resume && Reports.IsDone(o.Out, file))
            {
                return;
            }

            if (o.BudgetMinutes > 0 && clock.Elapsed.TotalMinutes > o.BudgetMinutes)
            {
                Interlocked.Increment(ref remaining);
                return;
            }

            FileReport report;
            try
            {
                List<ProbeStream> streams = FfprobeReader.ReadStreams(file);
                if (streams.Count == 0)
                {
                    // A file ffprobe finds no streams in is agreed on when we reject it too (typed, without an exception).
                    OurResult rejected = OurReader.Read(registry, file);
                    report = rejected.Status == "ok"
                        ? new FileReport { File = file, Status = "error", Message = "ffprobe reports no streams" }
                        : new FileReport { File = file, Status = "ok", Message = $"neither reads the file: ffprobe finds no streams, ours: {rejected.Message}" };
                    // A scoped "open" rule accepts files the oracle itself cannot open.
                    if (report.Status != "ok" && exceptions.Match("*", "open", file, null, "", "", "", false, false, 0) is not null)
                    {
                        report = new FileReport { File = file, Status = "ok" };
                        report.Excepted["open"] = 1;
                    }
                }
                else
                {
                    List<PacketRecord> theirs = [.. FfprobeReader.ReadPackets(file, streams)];
                    OurResult ours = OurReader.Read(registry, file);
                    report = Comparer.Compare(file, streams, theirs, ours, exceptions);
                    if (o.Audio && ours.Status == "ok")
                    {
                        AudioComparisons(o, file, ours, exceptions);
                    }
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or FormatException or System.ComponentModel.Win32Exception)
            {
                report = new FileReport { File = file, Status = "error", Message = e.GetType().Name + ": " + e.Message };
            }

            Reports.WriteFile(o.Out, report);
            int n = Interlocked.Increment(ref done);
            if (n % 25 == 0)
            {
                Console.Error.WriteLine($"diff: {n} files");
            }
        });

        Console.Write(Reports.Summarize(o.Out));
        if (o.Audio)
        {
            Console.WriteLine("OneAudio comparison:");
            Console.Write(Reports.Summarize(Path.Combine(o.Out, "oneaudio")));
        }

        Console.WriteLine($"files this run: {done}, left over: {remaining}");
        return remaining > 0 ? 75 : 0;
    }

    private static void AudioComparisons(Options o, string file, OurResult ours, Exceptions exceptions)
    {
        string audioDir = Path.Combine(o.Out, "oneaudio");
        string ext = Path.GetExtension(file).ToLowerInvariant();
        FileReport? report = null;
        if (ext is ".mp4" or ".m4a" or ".3gp" or ".3g2" or ".mov")
        {
            try
            {
                report = OneAudioCompare.CompareMp4(file, ours, exceptions);
            }
            catch (OneAudio.OneAudioException e)
            {
                report = new FileReport { File = file, Status = "unsupported", Message = e.Message };
            }
        }
        else if (ext is ".mka" or ".mkv")
        {
            // The Ogg twin of a Matroska audio file lives in ../ogg with the same stem.
            string? dir = Path.GetDirectoryName(Path.GetDirectoryName(file));
            string stem = Path.GetFileNameWithoutExtension(file);
            string? twin = dir is null ? null : OggExtensions.Select(x => Path.Combine(dir, "ogg", stem + x)).FirstOrDefault(File.Exists);
            if (twin is not null)
            {
                try
                {
                    report = OneAudioCompare.CompareOgg(file, twin, ours, exceptions);
                }
                catch (OneAudio.OneAudioException e)
                {
                    report = new FileReport { File = file, Status = "unsupported", Message = e.Message };
                }
            }
        }

        if (report is not null)
        {
            Reports.WriteFile(audioDir, report);
        }
    }

    private static int Structure(Options o)
    {
        List<string> files = o.Files.Count > 0 ? o.Files : Select(o, o.Family);
        int bad = 0;
        foreach (string f in files)
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            string? message;
            if (IsoExtensions.Contains(ext))
            {
                message = StructureCompare.CompareIso(f);
            }
            else
            {
                message = StructureCompare.CompareMatroska(f);
            }

            Console.WriteLine($"{(message is null ? "same" : "note")} {Path.GetRelativePath(o.Root, f)}{(message is null ? "" : ": " + message)}");
            bad += message is null || message.StartsWith("no structure provider", StringComparison.Ordinal) ? 0 : 1;
        }

        return bad == 0 ? 0 : 1;
    }

    private static int Io(Options o)
    {
        List<string> files = o.Files.Count > 0 ? [.. o.Files.Select(Path.GetFullPath)] : Select(o, o.Family);
        IoHarness.ReadMetadata = o.Metadata;
        List<IoRow> rows = IoHarness.Run(files, o.Root, o.Out, o.Http, o.Strace, o.Resume, Readers.Create());
        IoHarness.Write(o.Out, rows);
        return 0;
    }

    private static int Serve(Options o)
    {
        if (o.Files.Count == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        using RangeServer server = new(o.Files[0], o.Port);
        Console.WriteLine($"serving {o.Files[0]} on http://127.0.0.1:{server.Port}/ ; press Enter to stop");
        ServerCounters counters = server.Begin();
        _ = Console.ReadLine();
        Console.WriteLine($"requests {counters.Requests}, ranged {counters.RangeRequests}, body bytes {counters.BodyBytes}");
        return 0;
    }
}
