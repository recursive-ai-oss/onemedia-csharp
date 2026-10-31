using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace RecursiveAI.OneMedia.Bench;

/// <summary>Runs the rows: one fresh process per (row, implementation, file), reference tools as processes, results appended to a file.</summary>
internal sealed class Runner
{
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromMinutes(25);

    private readonly string _corpus;
    private readonly string _home;
    private readonly string _out;
    private readonly int _reps;
    private readonly int _nativeReps;
    private readonly bool _native;
    private readonly bool _resume;
    private readonly TimeSpan _budget;
    private readonly HashSet<string> _warmed = [];

    public Runner(string corpus, string home, string output, int reps, int nativeReps, bool native, bool resume, TimeSpan budget)
    {
        _corpus = corpus;
        _home = home;
        _out = output;
        _reps = reps;
        _nativeReps = nativeReps;
        _native = native;
        _resume = resume;
        _budget = budget;
        Directory.CreateDirectory(output);
    }

    private string ResultsPath => Path.Combine(_out, "results.tsv");

    public static string ChildPath => typeof(Runner).Assembly.Location;

    /// <summary>Returns 0 when everything selected ran, 75 when the time budget stopped it early (run again with --resume).</summary>
    public int Run(IEnumerable<Row> rows, string[]? fileFilter)
    {
        Stopwatch clock = Stopwatch.StartNew();
        double loadBefore = Machine.Load();
        using LoadSampler sampler = new();
        HashSet<string> done = _resume && File.Exists(ResultsPath)
            ? File.ReadLines(ResultsPath).Select(Result.Parse).OfType<Result>().Select(r => Key(r.Row, r.Impl, r.File)).ToHashSet()
            : [];
        bool stopped = false;

        foreach (Row row in rows)
        {
            foreach (string rel in row.Files)
            {
                if (fileFilter is not null && !fileFilter.Any(f => rel.Contains(f, StringComparison.Ordinal)))
                {
                    continue;
                }

                string path = Path.IsPathRooted(rel) ? rel : Path.Combine(_corpus, rel);
                if (!File.Exists(path))
                {
                    Console.WriteLine($"{row.Id} {rel}: not in the corpus, skipped");
                    continue;
                }

                foreach (Impl impl in row.Impls)
                {
                    if (done.Contains(Key(row.Id, impl.Name, rel)))
                    {
                        continue;
                    }

                    if (clock.Elapsed > _budget)
                    {
                        stopped = true;
                        break;
                    }

                    Warm(path);
                    Result r = RunChild(row, Array.IndexOf(row.Impls, impl), impl, rel);
                    Append(r);
                    Console.WriteLine($"{row.Id,-10} {rel,-48} {impl.Name,-26} {r.Status,-5} {Report.Time(r.MedianNs),10} {r.Note}");
                }

                if (_native && !stopped)
                {
                    foreach (NativeTool tool in row.Natives)
                    {
                        if (done.Contains(Key(row.Id, tool.Name, rel)))
                        {
                            continue;
                        }

                        if (clock.Elapsed > _budget)
                        {
                            stopped = true;
                            break;
                        }

                        Warm(path);
                        Result r = RunNative(row, tool, path, rel);
                        Append(r);
                        Console.WriteLine($"{row.Id,-10} {rel,-48} {tool.Name,-26} {r.Status,-5} {Report.Time(r.MedianNs),10} {r.Note}");
                    }
                }

                if (stopped)
                {
                    break;
                }
            }

            if (stopped)
            {
                break;
            }
        }

        (double mean, double max, int n) = sampler.Summary();
        File.AppendAllText(Path.Combine(_out, "meta.txt"), MetaBlock(loadBefore, mean, max, n, Machine.Load(), clock.Elapsed, stopped));
        return stopped ? 75 : 0;
    }

    private static string Key(string row, string impl, string file) => row + "|" + impl + "|" + file;

    private void Append(Result r) => File.AppendAllText(ResultsPath, r.ToLine() + "\n");

    /// <summary>Reads the file once so every implementation starts with it in the page cache.</summary>
    private void Warm(string path)
    {
        if (!_warmed.Add(path))
        {
            return;
        }

        byte[] buffer = new byte[4 << 20];
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        while (fs.Read(buffer, 0, buffer.Length) > 0)
        {
        }
    }

    private Result RunChild(Row row, int implIndex, Impl impl, string rel)
    {
        ProcessStartInfo psi = new(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!) == "dotnet")
        {
            psi.ArgumentList.Add(ChildPath);
        }

        foreach (string a in new[] { "child", row.Id, implIndex.ToString(CultureInfo.InvariantCulture), rel, _corpus, _reps.ToString(CultureInfo.InvariantCulture) })
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["DOTNET_gcServer"] = "0";
        psi.Environment["DOTNET_TieredPGO"] = "1";
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start child");
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(ChildTimeout))
        {
            p.Kill(true);
            return Crash(row, impl, rel, "timeout after " + ChildTimeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " min");
        }

        p.WaitForExit();
        foreach (string line in output.Split('\n'))
        {
            if (line.StartsWith("RESULT\t", StringComparison.Ordinal) && Result.Parse(line[7..]) is { } r)
            {
                return r;
            }
        }

        string tail = err.GetAwaiter().GetResult().Trim();
        return Crash(row, impl, rel, $"exit {p.ExitCode}; {(tail.Length > 150 ? tail[..150] : tail)}");
    }

    private static Result Crash(Row row, Impl impl, string rel, string note) =>
        new(row.Id, impl.Name, impl.Role.ToString(), rel, "crash", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, note.Replace('\n', ' '));

    private Result RunNative(Row row, NativeTool tool, string path, string rel)
    {
        bool big = new FileInfo(path).Length > (1L << 30);
        int reps = big ? 1 : _nativeReps;
        string exe = Path.Combine(_home, "oracles", "bin", tool.Tool);
        if (!File.Exists(exe))
        {
            exe = tool.Tool;
        }

        List<double> wall = [];
        long peak = 0;
        string note = string.Empty;
        for (int i = 0; i < reps; i++)
        {
            ProcessStartInfo psi = new("/usr/bin/time") { RedirectStandardOutput = true, RedirectStandardError = true };
            if (OperatingSystem.IsMacOS())
            {
                psi.ArgumentList.Add("-l");
            }
            else
            {
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("BENCHTIME %e %M");
            }

            psi.ArgumentList.Add(exe);
            foreach (string a in tool.Args(path))
            {
                psi.ArgumentList.Add(a);
            }

            psi.Environment["PATH"] = Path.Combine(_home, "oracles", "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            long started = Stopwatch.GetTimestamp();
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start " + exe);
            Task drain = p.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            drain.GetAwaiter().GetResult();
            double wallNs = Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1e6;
            if (!ParseTime(err, out _, out long rss))
            {
                return new Result(row.Id, tool.Name, "Native", rel, "fail", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, $"exit {p.ExitCode}; no time output");
            }

            wall.Add(wallNs);
            peak = Math.Max(peak, rss);
            if (p.ExitCode != 0)
            {
                note = "exit " + p.ExitCode.ToString(CultureInfo.InvariantCulture);
            }
        }

        wall.Sort();
        return new Result(row.Id, tool.Name, "Native", rel, "ok", wall[wall.Count / 2], wall[0], wall[^1], 0, 0, 0, 0, peak, 0, 1, reps, note);
    }

    private static bool ParseTime(string stderr, out double seconds, out long rssBytes)
    {
        seconds = 0;
        rssBytes = 0;
        bool got = false;
        foreach (string raw in stderr.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("BENCHTIME ", StringComparison.Ordinal))
            {
                string[] f = line.Split(' ');
                seconds = double.Parse(f[1], CultureInfo.InvariantCulture);
                rssBytes = long.Parse(f[2], CultureInfo.InvariantCulture) * 1024;
                got = true;
            }
            else if (line.EndsWith(" real", StringComparison.Ordinal) || line.Contains(" real ", StringComparison.Ordinal))
            {
                seconds = double.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture);
                got = true;
            }
            else if (line.EndsWith("maximum resident set size", StringComparison.Ordinal))
            {
                rssBytes = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture);
            }
        }

        return got;
    }

    private string MetaBlock(double before, double mean, double max, int samples, double after, TimeSpan elapsed, bool stopped)
    {
        StringBuilder sb = new();
        CultureInfo c = CultureInfo.InvariantCulture;
        sb.Append(c, $"## Run {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z{(stopped ? " (stopped at the time budget)" : string.Empty)}\n\n");
        sb.Append(c, $"- Host: {Environment.MachineName}; {Machine.Cpu()}; {Environment.ProcessorCount} logical cores; {RuntimeInformation_Os()}\n");
        sb.Append(c, $"- ISA: {Machine.IsaFlags()}\n");
        sb.Append(c, $"- Storage of the corpus: {Machine.Storage(_corpus)}; every file is read once before its rows, so it is in the page cache for all of them\n");
        sb.Append(c, $"- .NET: {Environment.Version} ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription})\n");
        sb.Append(c, $"- Load average (1 min): before {before:0.00}; during mean {mean:0.00}, max {max:0.00} over {samples} samples at 5 s; after {after:0.00}\n");
        sb.Append(c, $"- Elapsed: {elapsed.TotalMinutes:0.0} min; repetitions {_reps} (managed), {_nativeReps} (tools); files over 1 GiB get one warm-up run and 3 samples of one run each, and tools once\n");
        sb.Append(c, $"- Packages as restored: {Versions()}\n");
        sb.Append(c, $"- Tools: {ToolVersions()}\n\n");
        return sb.ToString();
    }

    private static string RuntimeInformation_Os() => System.Runtime.InteropServices.RuntimeInformation.OSDescription + " " + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;

    private static string Versions()
    {
        (string name, Type type)[] marks =
        [
            ("SharpMP4", typeof(SharpMP4.Readers.VideoReader)),
            ("RecursiveAI.OneAudio", typeof(global::OneAudio.Mp4.Mp4PacketSource)),
            ("MetadataExtractor", typeof(MetadataExtractor.ImageMetadataReader)),
            ("TagLibSharp", typeof(TagLib.File)),
            ("z440.atl.core", typeof(ATL.Track)),
            ("Matroska", typeof(global::Matroska.MatroskaSerializer)),
        ];
        return string.Join("; ", marks.Select(m =>
        {
            string v = m.type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? m.type.Assembly.GetName().Version?.ToString() ?? "?";
            int plus = v.IndexOf('+', StringComparison.Ordinal);
            return $"{m.name} {(plus > 0 ? v[..plus] : v)}";
        }));
    }

    private string ToolVersions()
    {
        string bin = Path.Combine(_home, "oracles", "bin");
        string First(string tool, params string[] args)
        {
            try
            {
                string exe = File.Exists(Path.Combine(bin, tool)) ? Path.Combine(bin, tool) : tool;
                string s = Machine.Capture(exe, args).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "?";
                return s.Length > 70 ? s[..70] : s;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                return "not installed";
            }
        }

        return $"{First("ffmpeg", "-version")}; {First("MP4Box", "-version")}; {First("mkvmerge", "--version")}; MediaInfo {First("mediainfo", "--Version")}; ExifTool {First("exiftool", "-ver")}";
    }
}
