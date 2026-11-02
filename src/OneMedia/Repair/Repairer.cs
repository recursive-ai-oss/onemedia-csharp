namespace RecursiveAI.OneMedia.Repair;

/// <summary>
/// Repairs damaged media into a new file and says what it did. The input is never modified; media payload bytes are copied unchanged,
/// and only index structures, counts and durations are rebuilt. Healthy input is reported as needing nothing and copied byte for byte.
/// </summary>
public static class Repairer
{
    /// <summary>Repairs a file into a new file.</summary>
    /// <param name="input">The damaged file.</param>
    /// <param name="output">The new file; refused if it exists. Removed again when the status is <see cref="RepairStatus.Unrepairable"/>.</param>
    /// <param name="reference">
    /// A healthy file from the same device, used by the truncated-file reconstruction for formats that need one; ignored by index repair.
    /// </param>
    /// <param name="cancellationToken">Cancels the repair; the partly written output is removed.</param>
    /// <returns>What was rebuilt, dropped and kept.</returns>
    /// <exception cref="IOException">The output exists, or a file cannot be read or written.</exception>
    /// <exception cref="UnsupportedMediaException">The format is not recognised, or has no repairer.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static RepairReport Repair(string input, string output, string? reference = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(input);
        ArgumentException.ThrowIfNullOrEmpty(output);
        if (File.Exists(output))
        {
            throw new IOException("The output file already exists: " + output);
        }

        using FileMediaSource source = FileMediaSource.Open(input);
        string name;
        if (MediaFormats.Default.TrySniff(source, out FormatMatch match, cancellationToken))
        {
            name = match.Format.Id.Name;
        }
        else if (TsRepair.LooksLikeTransportStream(source, cancellationToken))
        {
            // a transport stream cut at an arbitrary byte does not start with a sync byte, so the sniffer cannot recognise it
            name = "mpegts";
        }
        else
        {
            throw new UnsupportedMediaException("The format of " + input + " is not recognised.");
        }

        RepairReport report;
        try
        {
            report = RunEngine(name, source, output, MediaLimits.Default, cancellationToken);
        }
        catch
        {
            DeleteQuietly(output);
            throw;
        }

        if (report.Status == RepairStatus.Unrepairable)
        {
            DeleteQuietly(output);
        }

        return report;
    }

    // Runs the repairer for a format by name; the fuzz targets use it to reach one engine without going through the sniffer.
    internal static RepairReport RunEngine(string name, IMediaSource source, string output, MediaLimits limits, CancellationToken cancellationToken)
    {
        using var reader = new RepairInput(source, cancellationToken);
        return name switch
        {
            "matroska" => MatroskaRepair.Run(reader, output, limits, cancellationToken),
            "avi" => AviRepair.Run(reader, output, limits, cancellationToken),
            "mpegts" => TsRepair.Run(reader, output, limits, cancellationToken),
            "isobmff" => IsoRepair.Run(reader, output, limits, cancellationToken),
            _ => throw new UnsupportedMediaException("There is no repairer for " + name + " yet."),
        };
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
