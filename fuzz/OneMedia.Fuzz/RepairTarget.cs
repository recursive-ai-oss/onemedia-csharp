using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using RecursiveAI.OneMedia.Repair;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for index repair, one per format: the input is a damaged file given straight to that format's repairer. Anything but a
/// <see cref="MediaException"/> or an <see cref="IOException"/> escaping is a finding, and so is a report whose output length disagrees with the file.
/// </summary>
internal static class RepairTarget
{
    private static readonly MediaLimits Limits = new() { MaxAllocationBytes = 4 << 20, MaxElementCount = 100_000, MaxStructureDepth = 24, MaxSampleTableEntries = 200_000, MaxCues = 100_000, MaxTracks = 64 };
    private static readonly string OutputPath = Path.Combine(Path.GetTempPath(), "onemedia-fuzz-repair-" + Environment.ProcessId + ".bin");

    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["repair-matroska"] = span => Run("matroska", span);
        targets["repair-avi"] = span => Run("avi", span);
        targets["repair-isobmff"] = span => Run("isobmff", span);
        targets["repair-mpegts"] = span => Run("mpegts", span);
    }

    private static void Run(string format, ReadOnlySpan<byte> input)
    {
        File.Delete(OutputPath);
        try
        {
            RepairReport r = Repairer.RunEngine(format, new MemoryMediaSource(input.ToArray()), OutputPath, Limits, CancellationToken.None);
            if (r.OutputWritten != File.Exists(OutputPath) || (r.OutputWritten && new FileInfo(OutputPath).Length != r.OutputLength))
            {
                throw new InvalidOperationException($"the report says {r.Status} with {r.OutputLength} bytes but the output file does not agree");
            }
        }
        catch (MediaException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            File.Delete(OutputPath);
        }
    }
}
