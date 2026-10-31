using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace RecursiveAI.OneMedia.Oracles;

/// <summary>One measurement: how many bytes and calls a tool needed to probe a file.</summary>
internal sealed record IoRow(string File, long FileSize, string Tool, string Mode, long Bytes, long Calls, long Requests, string Note);

/// <summary>Counts what a reader asks of its source.</summary>
internal sealed class CountingObserver : IMediaReadObserver
{
    public long Calls;
    public long Bytes;

    public void OnRead(long offset, int requested, int returned)
    {
        Calls++;
        Bytes += returned;
    }
}

/// <summary>
/// Probe-cost measurements. Over HTTP the numbers come from the range server (requests, body bytes written).
/// For tools that cannot read URLs, and for comparison on local files, they come from strace: read-class
/// system calls on the file's descriptor (mapped length is reported separately in the note).
/// </summary>
internal static class IoHarness
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(5);
    private static string _logDir = ".";

    public static List<IoRow> Run(IReadOnlyList<string> files, string root, string outDir, bool http, bool strace, bool resume, MediaFormatRegistry registry)
    {
        Directory.CreateDirectory(outDir);
        _logDir = outDir;
        List<IoRow> rows = resume ? Read(Path.Combine(outDir, "io.tsv")) : [];
        HashSet<string> done = [.. rows.Select(r => r.File)];
        using RangeServer server = new(root);
        using HttpClient client = new();
        foreach (string file in files)
        {
            if (done.Contains(file))
            {
                continue;
            }

            string rel = Path.GetRelativePath(root, file);
            long size = new FileInfo(file).Length;
            Console.Error.WriteLine("io: " + rel);
            if (http)
            {
                string url = server.UrlFor(rel.Replace('\\', '/'));
                rows.Add(HttpTool(server, file, size, "ffprobe", ["-v", "error", "-show_format", "-show_streams", "-of", "json", url]));
                rows.Add(HttpTool(server, file, size, "mediainfo", ["--Output=JSON", url]));
                rows.Add(OursHttp(server, client, registry, file, size, url));
            }

            if (strace)
            {
                rows.Add(Strace(file, size, "ffprobe", ["-v", "error", "-show_format", "-show_streams", "-of", "json", file]));
                rows.Add(Strace(file, size, "mediainfo", ["--Output=JSON", file]));
                rows.Add(Strace(file, size, "exiftool", ["-json", file]));
                rows.Add(OursLocal(registry, file, size));
            }

            // Written after every file so an interrupted run can resume.
            WriteTsv(outDir, rows);
        }

        return rows;
    }

    private static List<IoRow> Read(string path)
    {
        List<IoRow> rows = [];
        if (!File.Exists(path))
        {
            return rows;
        }

        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] f = line.Split('\t');
            rows.Add(new IoRow(f[0], long.Parse(f[1], CultureInfo.InvariantCulture), f[2], f[3], long.Parse(f[4], CultureInfo.InvariantCulture), long.Parse(f[5], CultureInfo.InvariantCulture), long.Parse(f[6], CultureInfo.InvariantCulture), f.Length > 7 ? f[7] : ""));
        }

        return rows;
    }

    private static void WriteTsv(string outDir, IReadOnlyList<IoRow> rows)
    {
        StringBuilder tsv = new("file\tsize\ttool\tmode\tbytes\tcalls\trequests\tnote\n");
        foreach (IoRow r in rows)
        {
            tsv.Append(CultureInfo.InvariantCulture, $"{r.File}\t{r.FileSize}\t{r.Tool}\t{r.Mode}\t{r.Bytes}\t{r.Calls}\t{r.Requests}\t{r.Note}\n");
        }

        File.WriteAllText(Path.Combine(outDir, "io.tsv"), tsv.ToString());
    }

    public static void Write(string outDir, IReadOnlyList<IoRow> rows)
    {
        Directory.CreateDirectory(outDir);
        WriteTsv(outDir, rows);

        // One line per file with a column per tool and mode: bytes (calls).
        StringBuilder table = new();
        string[] columns = [.. rows.Select(r => r.Tool + "/" + r.Mode).Distinct().Order(StringComparer.Ordinal)];
        table.Append("file (size)");
        foreach (string c in columns)
        {
            table.Append(" | ").Append(c);
        }

        table.Append('\n');
        foreach (IGrouping<string, IoRow> g in rows.GroupBy(r => r.File))
        {
            table.Append(CultureInfo.InvariantCulture, $"{Path.GetFileName(g.Key)} ({g.First().FileSize})");
            foreach (string c in columns)
            {
                IoRow? r = g.FirstOrDefault(x => x.Tool + "/" + x.Mode == c);
                table.Append(" | ").Append(r is null ? "-" : r.Note.StartsWith("failed", StringComparison.Ordinal) ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{r.Bytes} B / {(r.Mode == "http" ? r.Requests : r.Calls)}"));
            }

            table.Append('\n');
        }

        File.WriteAllText(Path.Combine(outDir, "io-table.txt"), table.ToString());
        Console.Write(table.ToString());
    }

    private static IoRow HttpTool(RangeServer server, string file, long size, string tool, string[] args)
    {
        ServerCounters c = server.Begin();
        (int exit, string stdout, string stderr, bool timedOut) = RunTool(Oracle.Tool(tool), args);
        Thread.Sleep(200); // let the server finish counting the last response
        string note = timedOut ? "failed: timeout" : exit != 0 || (tool == "mediainfo" && !stdout.Contains("\"track\"", StringComparison.Ordinal)) ? "failed: " + FirstLine(stderr.Length > 0 ? stderr : stdout) : "";
        return new IoRow(file, size, tool, "http", Interlocked.Read(ref c.BodyBytes), Interlocked.Read(ref c.Requests), Interlocked.Read(ref c.Requests), note + (c.RangeRequests > 0 ? $" ranged={c.RangeRequests}" : ""));
    }

    private static IoRow Strace(string file, long size, string tool, string[] args)
    {
        string log = Path.Combine(_logDir, ".strace-" + Environment.ProcessId + ".log");
        string[] straceArgs = ["-f", "-qq", "-s", "0", "-o", log, "-e", "trace=openat,open,close,read,pread64,readv,preadv,mmap", Oracle.Tool(tool), .. args];
        (int exit, _, string stderr, bool timedOut) = RunTool("strace", straceArgs);
        if (!File.Exists(log))
        {
            return new IoRow(file, size, tool, "strace", 0, 0, 0, "failed: " + FirstLine(stderr));
        }

        (long bytes, long calls, long mapped) = ParseStrace(log, Path.GetFullPath(file));
        File.Delete(log);
        string note = timedOut ? "failed: timeout" : exit != 0 && tool != "mediainfo" ? "failed: exit " + exit : mapped > 0 ? $"mapped={mapped}" : "";
        return new IoRow(file, size, tool, "strace", bytes, calls, 0, note);
    }

    /// <summary>Sums bytes returned and counts calls for read-class syscalls on descriptors opened on the target file.</summary>
    internal static (long Bytes, long Calls, long Mapped) ParseStrace(string logPath, string target)
    {
        HashSet<int> fds = [];
        long bytes = 0, calls = 0, mapped = 0;
        string quoted = "\"" + target + "\"";
        foreach (string line in File.ReadLines(logPath))
        {
            // "<pid> syscall(args) = ret"
            int sp = line.IndexOf(' ', StringComparison.Ordinal);
            int paren = line.IndexOf('(', StringComparison.Ordinal);
            int eq = line.LastIndexOf(" = ", StringComparison.Ordinal);
            if (sp < 0 || paren < 0 || eq < 0)
            {
                continue;
            }

            string call = line[(sp + 1)..paren];
            string args = line[(paren + 1)..eq];
            if (!long.TryParse(line[(eq + 3)..].Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ret))
            {
                continue;
            }

            switch (call)
            {
                case "openat" or "open":
                    if (ret >= 0 && args.Contains(quoted, StringComparison.Ordinal))
                    {
                        fds.Add((int)ret);
                    }

                    break;
                case "close":
                    if (int.TryParse(args.TrimEnd(')'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cfd))
                    {
                        fds.Remove(cfd);
                    }

                    break;
                case "read" or "pread64" or "readv" or "preadv":
                    if (ret >= 0 && int.TryParse(args.Split(',')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rfd) && fds.Contains(rfd))
                    {
                        calls++;
                        bytes += ret;
                    }

                    break;
                case "mmap":
                    string[] a = args.Split(',');
                    if (a.Length >= 5 && int.TryParse(a[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mfd) && fds.Contains(mfd) && long.TryParse(a[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long len))
                    {
                        mapped += len;
                    }

                    break;
            }
        }

        return (bytes, calls, mapped);
    }

    private static IoRow OursLocal(MediaFormatRegistry registry, string file, long size)
    {
        CountingObserver observer = new();
        string note = "";
        try
        {
            using ObservedMediaSource source = new(FileMediaSource.Open(file), null, observer);
            note = ProbeOurs(registry, source);
        }
        catch (MediaException e)
        {
            note = "failed: " + e.GetType().Name;
        }

        return new IoRow(file, size, "ours", "strace", observer.Bytes, observer.Calls, 0, note);
    }

    private static IoRow OursHttp(RangeServer server, HttpClient client, MediaFormatRegistry registry, string file, long size, string url)
    {
        ServerCounters c = server.Begin();
        CountingObserver observer = new();
        string note = "";
        try
        {
            using HttpRequestMessage head = new(HttpMethod.Head, url);
            long length = client.Send(head).Content.Headers.ContentLength ?? size;
            RangedMediaSource ranged = new(length, async (offset, memory, ct) =>
            {
                using HttpRequestMessage req = new(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(offset, offset + memory.Length - 1);
                using HttpResponseMessage resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                await using Stream body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                int total = 0;
                while (total < memory.Length)
                {
                    int n = await body.ReadAsync(memory[total..], ct).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    total += n;
                }

                return total;
            });
            using ObservedMediaSource source = new(null, ranged, observer);
            note = ProbeOursAsync(registry, source).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is MediaException or HttpRequestException)
        {
            note = "failed: " + e.GetType().Name;
        }

        Thread.Sleep(100);
        return new IoRow(file, size, "ours", "http", Interlocked.Read(ref c.BodyBytes), observer.Calls, Interlocked.Read(ref c.Requests), note);
    }

    /// <summary>Sniffs, then opens and reads the track list when a reader is registered. Returns a note.</summary>
    private static string ProbeOurs(MediaFormatRegistry registry, ObservedMediaSource source)
    {
        if (!registry.TrySniff(source, out FormatMatch match))
        {
            return "no format recognised";
        }

        if (match.Format is not IMediaFormat format)
        {
            return "sniffed " + match.Format.Id + " only (no reader registered)";
        }

        using IDemuxer demuxer = format.Open(source, new MediaOpenOptions { LeaveOpen = true });
        return "opened " + match.Format.Id + ", " + demuxer.Tracks.Count + " tracks";
    }

    private static async Task<string> ProbeOursAsync(MediaFormatRegistry registry, ObservedMediaSource source)
    {
        if (await registry.SniffAsync(source).ConfigureAwait(false) is not { } match)
        {
            return "no format recognised";
        }

        if (match.Format is not IMediaFormat format)
        {
            return "sniffed " + match.Format.Id + " only (no reader registered)";
        }

        await using IAsyncDemuxer demuxer = await format.OpenAsync(source, new MediaOpenOptions { LeaveOpen = true }).ConfigureAwait(false);
        return "opened " + match.Format.Id + ", " + demuxer.Tracks.Count + " tracks";
    }

    private static (int Exit, string StdOut, string StdErr, bool TimedOut) RunTool(string exe, IEnumerable<string> args)
    {
        ProcessStartInfo psi = new(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + exe);
        Task<string> o = p.StandardOutput.ReadToEndAsync(), e = p.StandardError.ReadToEndAsync();
        bool exited = p.WaitForExit(ToolTimeout);
        if (!exited)
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit();
        }

        return (exited ? p.ExitCode : -1, o.Result, e.Result, !exited);
    }

    private static string FirstLine(string text) => text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
}
