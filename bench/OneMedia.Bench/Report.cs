using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RecursiveAI.OneMedia.Bench;

/// <summary>Turns results.tsv into the markdown tables: per row, the ratios, and the verdict against the bar.</summary>
internal static class Report
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Time(double ns) => ns switch
    {
        <= 0 => "-",
        < 1e3 => ns.ToString("0", C) + " ns",
        < 1e6 => (ns / 1e3).ToString("0.0", C) + " µs",
        < 1e9 => (ns / 1e6).ToString("0.00", C) + " ms",
        _ => (ns / 1e9).ToString("0.00", C) + " s",
    };

    private static string Mb(long bytes) => bytes <= 0 ? "-" : (bytes / 1048576.0).ToString("0.0", C);

    private static string Size(string path)
    {
        long n = new FileInfo(path).Length;
        return n >= 1L << 30 ? (n / 1073741824.0).ToString("0.00", C) + " GiB" : (n / 1048576.0).ToString("0.0", C) + " MiB";
    }

    private static string Bytes(double b) => b switch
    {
        < 1024 => b.ToString("0", C) + " B",
        < 1048576 => (b / 1024).ToString("0.0", C) + " KiB",
        _ => (b / 1048576).ToString("0.0", C) + " MiB",
    };

    public static string Build(string outDir, string corpus)
    {
        List<Result> all = File.ReadLines(Path.Combine(outDir, "results.tsv")).Select(Result.Parse).OfType<Result>().ToList();
        StringBuilder sb = new();
        sb.Append("# OneMedia speed rows\n\n");
        string meta = Path.Combine(outDir, "meta.txt");
        if (File.Exists(meta))
        {
            sb.Append(File.ReadAllText(meta));
        }

        sb.Append("Ratio is OneMedia's median over the fastest managed comparator's median on the same file (below 1.00× is faster). ")
          .Append("Peak is the process's peak resident set; Baseline is its resident set before any work, so Peak − Baseline is what the work added.\n\n");

        List<string> summary = [];
        foreach (Row row in Rows.All)
        {
            List<Result> mine = all.Where(r => r.Row == row.Id).ToList();
            if (mine.Count == 0)
            {
                continue;
            }

            sb.Append(C, $"## {row.Id}: {row.Title}\n\n");
            sb.Append("| File | Size | Implementation | Median | Ratio | Alloc / run | Alloc / packet | Packets | Payload MiB | Tracks or tags | Peak MiB | Baseline MiB | Note |\n");
            sb.Append("|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|\n");
            List<double> ratios = [];
            int oursOk = 0, files = 0;
            foreach (string file in row.Files)
            {
                List<Result> cells = mine.Where(r => r.File == file && r.Role != "Native").ToList();
                if (cells.Count == 0)
                {
                    continue;
                }

                files++;
                string full = Path.IsPathRooted(file) ? file : Path.Combine(corpus, file);
                double best = cells.Where(r => r.Ok && r.Role == "Gate").Select(r => r.MedianNs).DefaultIfEmpty(0).Min();
                string bestName = cells.Where(r => r.Ok && r.Role == "Gate" && r.MedianNs == best).Select(r => r.Impl).FirstOrDefault() ?? string.Empty;
                foreach (Result r in cells.OrderBy(r => r.Role == "Ours" ? 0 : 1).ThenBy(r => r.Impl, StringComparer.Ordinal))
                {
                    string ratio = "-";
                    if (r.Ok && best > 0)
                    {
                        double q = r.MedianNs / best;
                        ratio = q.ToString("0.00", C) + "×";
                        if (r.Role == "Ours")
                        {
                            ratios.Add(q);
                            oursOk++;
                        }
                    }

                    string note = r.Note;
                    Result? ours = cells.FirstOrDefault(x => x.Ok && x.Role == "Ours");
                    if (r.Ok && r.Role == "Gate" && ours is not null && ours.Packets > 0 && r.Packets != ours.Packets)
                    {
                        note = $"packet count differs from OneMedia's {ours.Packets}" + (note.Length > 0 ? "; " + note : string.Empty);
                    }

                    if (r.Role == "Gate" && r.Ok && r.Impl == bestName && cells.Count(x => x.Ok && x.Role == "Gate") > 1)
                    {
                        note = "fastest comparator" + (note.Length > 0 ? "; " + note : string.Empty);
                    }

                    string size = File.Exists(full) ? Size(full) : "?";
                    string time = r.Ok ? Time(r.MedianNs) : r.Status;
                    string alloc = r.Ok ? Bytes(r.AllocPerOp) : "-";
                    string perPacket = r.Ok && r.Packets > 0 ? (r.AllocPerOp / r.Packets).ToString("0.0", C) + " B" : "-";
                    string packets = r.Ok && r.Packets > 0 ? r.Packets.ToString(C) : "-";
                    string aux = r.Ok && row.Id.EndsWith("probe", StringComparison.Ordinal) ? r.Aux.ToString(C) : "-";
                    string payload = r.Ok && r.Bytes > 0 ? Mb(r.Bytes) : "-";
                    sb.Append(C, $"| {file} | {size} | {r.Impl} | {time} | {ratio} | {alloc} | {perPacket} | {packets} | {payload} | {aux} | {Mb(r.PeakRss)} | {Mb(r.BaselineRss)} | {note} |\n");
                }
            }

            sb.Append('\n');
            List<Result> natives = mine.Where(r => r.Role == "Native").ToList();
            if (natives.Count > 0)
            {
                sb.Append("Reference tools, run as processes (wall time including start-up, peak resident set); information only:\n\n");
                sb.Append("| File | Tool | Wall median | Wall min – max | Peak MiB | Note |\n|---|---|---:|---:|---:|---|\n");
                foreach (Result r in natives.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Impl, StringComparer.Ordinal))
                {
                    string range = r.Ok ? Time(r.MinNs) + " – " + Time(r.MaxNs) : "-";
                    sb.Append(C, $"| {r.File} | {r.Impl} | {(r.Ok ? Time(r.MedianNs) : r.Status)} | {range} | {Mb(r.PeakRss)} | {r.Note} |\n");
                }

                sb.Append('\n');
            }

            if (ratios.Count > 0)
            {
                double geo = Math.Exp(ratios.Average(Math.Log));
                double max = ratios.Max();
                bool pass = geo <= 1.0 && max <= 1.25;
                summary.Add($"| {row.Id} | {oursOk}/{files} | {geo.ToString("0.00", C)}× | {max.ToString("0.00", C)}× | {(pass ? "at the bar" : "misses the bar")} |");
            }
            else
            {
                summary.Add($"| {row.Id} | 0/{files} | - | - | no managed comparator, so the row passes (times are information) |");
            }
        }

        sb.Append("## Against the bar\n\nBar: geometric mean of the ratios ≤ 1.00× and no file worse than 1.25×, against the fastest managed comparator per file.\n\n");
        sb.Append("| Row | Files with a ratio | Geomean | Worst | Verdict |\n|---|---:|---:|---:|---|\n");
        foreach (string s in summary)
        {
            sb.Append(s).Append('\n');
        }

        return sb.ToString();
    }
}
