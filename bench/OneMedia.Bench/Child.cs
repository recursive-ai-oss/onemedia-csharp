using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RecursiveAI.OneMedia.Bench;

/// <summary>One measured (row, implementation, file) cell. Stored as a tab-separated line so a run can resume and be re-reported.</summary>
internal sealed record Result(
    string Row, string Impl, string Role, string File, string Status,
    double MedianNs, double MinNs, double MaxNs, double AllocPerOp,
    long Packets, long Bytes, long Aux, long PeakRss, long BaselineRss, int Inner, int Reps, string Note)
{
    public bool Ok => Status == "ok";

    public string ToLine() => string.Join('\t',
        Row, Impl, Role, File, Status,
        MedianNs.ToString("R", CultureInfo.InvariantCulture), MinNs.ToString("R", CultureInfo.InvariantCulture),
        MaxNs.ToString("R", CultureInfo.InvariantCulture), AllocPerOp.ToString("R", CultureInfo.InvariantCulture),
        Packets, Bytes, Aux, PeakRss, BaselineRss, Inner, Reps, Note.Replace('\t', ' ').Replace('\n', ' '));

    public static Result? Parse(string line)
    {
        string[] f = line.Split('\t');
        if (f.Length < 17)
        {
            return null;
        }

        CultureInfo c = CultureInfo.InvariantCulture;
        return new Result(f[0], f[1], f[2], f[3], f[4],
            double.Parse(f[5], c), double.Parse(f[6], c), double.Parse(f[7], c), double.Parse(f[8], c),
            long.Parse(f[9], c), long.Parse(f[10], c), long.Parse(f[11], c), long.Parse(f[12], c), long.Parse(f[13], c),
            int.Parse(f[14], c), int.Parse(f[15], c), f[16]);
    }
}

/// <summary>
/// Measures one implementation on one file inside a fresh process, so peak memory belongs to that work alone. Time is the median
/// of <c>reps</c> samples; each sample averages <c>inner</c> back-to-back runs so that millisecond-sized work is not timer noise.
/// Allocation is the bytes the measuring thread allocated per run.
/// </summary>
internal static class Child
{
    private const long BigFile = 1L << 30;
    private const double TargetSampleNs = 50e6;

    public static Result Run(Row row, Impl impl, string path, string relative, int reps)
    {
        long baseline = Process.GetCurrentProcess().WorkingSet64;
        long size = new FileInfo(path).Length;
        bool big = size > BigFile;
        Work work = default;
        int inner = 1;
        double cold = 0;
        try
        {
            // The first run pays JIT and type loading and is reported apart as the cold figure; the second sizes the samples.
            long c0 = Stopwatch.GetTimestamp();
            work = impl.Run(path);
            cold = Stopwatch.GetElapsedTime(c0).TotalMilliseconds * 1e6;
            if (!big)
            {
                long t0 = Stopwatch.GetTimestamp();
                work = impl.Run(path);
                double once = Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1e6;
                inner = once >= TargetSampleNs ? 1 : (int)Math.Min(2000, Math.Ceiling(TargetSampleNs / Math.Max(once, 1)));
            }
            else
            {
                reps = Math.Min(reps, 3);
            }

            double[] ns = new double[reps];
            double[] alloc = new double[reps];
            for (int r = 0; r < reps; r++)
            {
                GC.Collect();
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                long t = Stopwatch.GetTimestamp();
                for (int i = 0; i < inner; i++)
                {
                    work = impl.Run(path);
                }

                double elapsed = Stopwatch.GetElapsedTime(t).TotalMilliseconds * 1e6;
                alloc[r] = (GC.GetAllocatedBytesForCurrentThread() - a0) / (double)inner;
                ns[r] = elapsed / inner;
            }

            Array.Sort(ns);
            Array.Sort(alloc);
            long peak = Process.GetCurrentProcess().PeakWorkingSet64;
            return new Result(row.Id, impl.Name, impl.Role.ToString(), relative, "ok",
                ns[reps / 2], ns[0], ns[^1], alloc[reps / 2], work.Packets, work.Bytes, work.Aux, peak, baseline, inner, reps, "cold first run " + Report.Time(cold));
        }
        catch (NotAvailableException e)
        {
            return Failed(row, impl, relative, "skip", e.Message, baseline);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Failed(row, impl, relative, "fail", e.GetType().Name + ": " + e.Message, baseline);
        }
    }

    private static Result Failed(Row row, Impl impl, string relative, string status, string note, long baseline) =>
        new(row.Id, impl.Name, impl.Role.ToString(), relative, status, 0, 0, 0, 0, 0, 0, 0,
            Process.GetCurrentProcess().PeakWorkingSet64, baseline, 0, 0, note.Length > 200 ? note[..200] : note);
}
