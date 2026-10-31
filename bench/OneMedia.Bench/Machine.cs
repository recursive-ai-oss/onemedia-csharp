using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace RecursiveAI.OneMedia.Bench;

/// <summary>What the numbers were measured on, and how busy the machine was while they were taken.</summary>
internal static class Machine
{
    public static string Cpu()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.Ordinal) || line.StartsWith("Model", StringComparison.Ordinal))
                    {
                        return line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                    }
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                return Capture("sysctl", "-n", "machdep.cpu.brand_string").Trim();
            }
        }
        catch (IOException)
        {
        }

        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    public static string IsaFlags()
    {
        List<string> f = [];
        if (RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86)
        {
            if (Sse42.IsSupported) { f.Add("sse4.2"); }
            if (Avx2.IsSupported) { f.Add("avx2"); }
            if (Bmi2.IsSupported) { f.Add("bmi2"); }
            if (Avx512F.IsSupported) { f.Add("avx512f"); }
            if (Avx512BW.IsSupported) { f.Add("avx512bw"); }
        }
        else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            if (AdvSimd.IsSupported) { f.Add("neon"); }
            if (Crc32.Arm64.IsSupported) { f.Add("crc32"); }
            if (System.Runtime.Intrinsics.Arm.Aes.IsSupported) { f.Add("aes"); }
            if (Dp.IsSupported) { f.Add("dotprod"); }
        }

        return string.Join(' ', f);
    }

    /// <summary>One-minute load average, or NaN where the platform gives none.</summary>
    public static double Load()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                string[] parts = File.ReadAllText("/proc/loadavg").Split(' ');
                return double.Parse(parts[0], CultureInfo.InvariantCulture);
            }

            if (OperatingSystem.IsMacOS())
            {
                string s = Capture("sysctl", "-n", "vm.loadavg").Trim('{', '}', ' ', '\n');
                return double.Parse(s.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture);
            }
        }
        catch (Exception e) when (e is IOException or FormatException)
        {
        }

        return double.NaN;
    }

    public static string Storage(string path)
    {
        try
        {
            return Capture("df", "-P", path).Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }

    public static string Capture(string tool, params string[] args)
    {
        ProcessStartInfo psi = new(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException(tool);
        string err = p.StandardError.ReadToEndAsync().GetAwaiter().GetResult();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Length > 0 ? output : err;
    }
}

/// <summary>Samples the load average while a run is in progress.</summary>
internal sealed class LoadSampler : IDisposable
{
    private readonly Timer _timer;
    private readonly List<double> _samples = [];

    public LoadSampler() => _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));

    private void Sample()
    {
        double l = Machine.Load();
        lock (_samples)
        {
            _samples.Add(l);
        }
    }

    public (double Mean, double Max, int Count) Summary()
    {
        lock (_samples)
        {
            double[] v = _samples.Where(x => !double.IsNaN(x)).ToArray();
            return v.Length == 0 ? (double.NaN, double.NaN, 0) : (v.Average(), v.Max(), v.Length);
        }
    }

    public void Dispose() => _timer.Dispose();
}
