using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using RecursiveAI.OneMedia.Bench;

// Speed rows of the performance bar. See docs/bench.md.
//
//   run     [--rows 0.1|id,id] [--corpus DIR] [--out DIR] [--reps N] [--tool-reps N] [--files a,b] [--no-tools]
//           [--resume] [--budget MIN]      run the rows, append to OUT/results.tsv, write OUT/report.md
//   report  [--out DIR] [--corpus DIR]     rebuild OUT/report.md from OUT/results.tsv
//   prepare [--corpus DIR]                 derive the extra corpus files the rows use (fragmented MP4)
//   list                                   print the rows and their files
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: bench run|report|prepare|list ...");
    return 2;
}

string home = Environment.GetEnvironmentVariable("ONEMEDIA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onemedia");

if (args[0] == "child")
{
    // child ROW IMPL-INDEX RELATIVE-PATH CORPUS REPS: measure one cell and print it.
    Row row = Rows.All.First(r => r.Id == args[1]);
    Impl impl = row.Impls[int.Parse(args[2], CultureInfo.InvariantCulture)];
    string corpus = args[4];
    string path = Path.IsPathRooted(args[3]) ? args[3] : Path.Combine(corpus, args[3]);
    Result result = Child.Run(row, impl, path, args[3], int.Parse(args[5], CultureInfo.InvariantCulture));
    Console.Out.WriteLine("RESULT\t" + result.ToLine());
    return 0;
}

if (args[0] == "pullalloc")
{
    // pullalloc RELATIVE-PATH...: bytes allocated per packet by the second half of a pull, on a warm demuxer.
    foreach (string rel in args.Skip(1))
    {
        string path = Path.IsPathRooted(rel) ? rel : Path.Combine(home, "corpus", rel);
        (long packets, long bytes) = Impls.PullAllocation(path);
        Console.WriteLine($"{rel}: {packets} packets, {bytes} bytes allocated, {(packets == 0 ? 0 : (double)bytes / packets):0.000} B per packet");
    }

    return 0;
}

if (args[0] == "once")
{
    // once ROW IMPL-INDEX RELATIVE-PATH [CORPUS [TIMES]]: run one cell, for tracing, profiling and I/O counts.
    Row row = Rows.All.First(r => r.Id == args[1]);
    Impl impl = row.Impls[int.Parse(args[2], CultureInfo.InvariantCulture)];
    string corpus = args.Length > 4 ? args[4] : Path.Combine(home, "corpus");
    string path = Path.IsPathRooted(args[3]) ? args[3] : Path.Combine(corpus, args[3]);
    int times = args.Length > 5 ? int.Parse(args[5], CultureInfo.InvariantCulture) : 1;
    Work w = default;
    long started = System.Diagnostics.Stopwatch.GetTimestamp();
    for (int i = 0; i < times; i++)
    {
        w = impl.Run(path);
    }

    Console.Out.WriteLine($"{impl.Name}: packets {w.Packets}, bytes {w.Bytes}, aux {w.Aux}; {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds / times:0.000} ms per run over {times}");
    return 0;
}

Dictionary<string, string> opt = [];
HashSet<string> flags = [];
for (int i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("unexpected argument " + args[i]);
        return 2;
    }

    string name = args[i][2..];
    if (name is "no-tools" or "resume")
    {
        flags.Add(name);
    }
    else if (i + 1 < args.Length)
    {
        opt[name] = args[++i];
    }
    else
    {
        Console.Error.WriteLine("missing value for --" + name);
        return 2;
    }
}

string corpusDir = opt.GetValueOrDefault("corpus") ?? Path.Combine(home, "corpus");
string outDir = opt.GetValueOrDefault("out") ?? Path.Combine(Environment.CurrentDirectory, "bench-results");

switch (args[0])
{
    case "list":
        foreach (Row r in Rows.All)
        {
            Console.WriteLine($"{r.Id} (stage {r.Stage}): {r.Title}");
            Console.WriteLine("  implementations: " + string.Join(", ", r.Impls.Select(i => i.Name)));
            Console.WriteLine("  tools: " + string.Join(", ", r.Natives.Select(n => n.Name)));
            foreach (string f in r.Files)
            {
                Console.WriteLine((File.Exists(Path.Combine(corpusDir, f)) ? "  [x] " : "  [ ] ") + f);
            }
        }

        return 0;

    case "prepare":
        return Prepare(corpusDir, home);

    case "report":
        File.WriteAllText(Path.Combine(outDir, "report.md"), Report.Build(outDir, corpusDir));
        Console.WriteLine("wrote " + Path.Combine(outDir, "report.md"));
        return 0;

    case "run":
    {
        string sel = opt.GetValueOrDefault("rows") ?? "0.1";
        IEnumerable<Row> rows = sel.Split(',').SelectMany(s => Rows.All.Where(r => r.Stage == s || r.Id == s)).Distinct().ToList();
        if (!rows.Any())
        {
            Console.Error.WriteLine("no rows match " + sel);
            return 2;
        }

        int reps = int.Parse(opt.GetValueOrDefault("reps") ?? "5", CultureInfo.InvariantCulture);
        int toolReps = int.Parse(opt.GetValueOrDefault("tool-reps") ?? "3", CultureInfo.InvariantCulture);
        double budget = double.Parse(opt.GetValueOrDefault("budget") ?? "1e6", CultureInfo.InvariantCulture);
        Prepare(corpusDir, home);
        Runner runner = new(corpusDir, home, outDir, reps, toolReps, !flags.Contains("no-tools"), flags.Contains("resume"), TimeSpan.FromMinutes(budget));
        int code = runner.Run(rows, opt.GetValueOrDefault("files")?.Split(','));
        File.WriteAllText(Path.Combine(outDir, "report.md"), Report.Build(outDir, corpusDir));
        Console.WriteLine("wrote " + Path.Combine(outDir, "report.md"));
        return code;
    }

    default:
        Console.Error.WriteLine("unknown command " + args[0]);
        return 2;
}

// Writes a fragmented copy of the 1080p open-movie file with the pinned ffmpeg, once.
static int Prepare(string corpus, string home)
{
    string source = Path.Combine(corpus, "blender/big_buck_bunny_1080p_h264.mov");
    string target = Path.Combine(corpus, "generated/bench/frag-bbb-1080p.mp4");
    if (File.Exists(target) || !File.Exists(source))
    {
        return 0;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    string ffmpeg = Path.Combine(home, "oracles", "bin", "ffmpeg");
    ProcessStartInfo psi = new(File.Exists(ffmpeg) ? ffmpeg : "ffmpeg") { RedirectStandardError = true };
    foreach (string a in new[] { "-v", "error", "-nostdin", "-i", source, "-map", "0:v", "-map", "0:a", "-c", "copy", "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", target + ".part" })
    {
        psi.ArgumentList.Add(a);
    }

    using Process p = Process.Start(psi)!;
    p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0)
    {
        File.Delete(target + ".part");
        Console.Error.WriteLine("could not derive " + target);
        return 1;
    }

    File.Move(target + ".part", target);
    return 0;
}
