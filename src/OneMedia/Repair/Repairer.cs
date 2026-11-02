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
        if (!MediaFormats.Default.TrySniff(source, out FormatMatch match, cancellationToken))
        {
            throw new UnsupportedMediaException("The format of " + input + " is not recognised.");
        }

        using var reader = new RepairInput(source, cancellationToken);
        MediaLimits limits = MediaLimits.Default;
        string name = match.Format.Id.Name;
        RepairReport report;
        try
        {
            report = name switch
            {
                "matroska" => MatroskaRepair.Run(reader, output, limits, cancellationToken),
                "avi" => AviRepair.Run(reader, output, limits, cancellationToken),
                _ => throw new UnsupportedMediaException("There is no repairer for " + name + " yet."),
            };
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
