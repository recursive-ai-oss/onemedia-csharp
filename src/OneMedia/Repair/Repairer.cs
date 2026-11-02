namespace RecursiveAI.OneMedia.Repair;

/// <summary>
/// Repairs damaged media into a new file and says what it did. The input is never modified; media payload bytes are copied unchanged,
/// and only index structures, counts and durations are rebuilt. Healthy input is reported as needing nothing and copied byte for byte.
/// </summary>
public static class Repairer
{
    /// <summary>Repairs a file into a new file.</summary>
    /// <param name="input">The damaged file.</param>
    /// <param name="output">The new file; refused if it exists. Removed again when the status is <see cref="RepairStatus.Unrepairable"/> or the repair fails.</param>
    /// <param name="reference">
    /// A healthy file from the same device, used by the truncated-file reconstruction for formats that need one; ignored by index repair.
    /// </param>
    /// <param name="cancellationToken">Cancels the repair; the partly written output is removed.</param>
    /// <returns>What was rebuilt, dropped and kept.</returns>
    /// <exception cref="IOException">The output exists, or a file cannot be read or written.</exception>
    /// <exception cref="UnsupportedMediaException">The format is not recognised, or has no repairer.</exception>
    /// <exception cref="MediaLimitExceededException">The file exceeds a default limit (table sizes, allocation).</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static RepairReport Repair(string input, string output, string? reference = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(input);
        ArgumentException.ThrowIfNullOrEmpty(output);
        _ = reference;
        using FileMediaSource source = FileMediaSource.Open(input);
        RepairReport report;
        FileStream destination = new(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        try
        {
            using (destination)
            {
                report = Repair(source, destination, null, cancellationToken);
            }
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

    /// <summary>Repairs media from any random-access source into a seekable stream (a file, a memory stream; the browser's blobs go through <see cref="RangedMediaSource"/>).</summary>
    /// <param name="input">The damaged media.</param>
    /// <param name="output">A writable, seekable, empty stream. Not closed. Nothing is written for <see cref="RepairStatus.Unrepairable"/>, but it may hold partial data after an exception.</param>
    /// <param name="limits">Resource limits; <see cref="MediaLimits.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels the repair.</param>
    /// <returns>What was rebuilt, dropped and kept.</returns>
    /// <exception cref="ArgumentException"><paramref name="output"/> cannot seek or write.</exception>
    /// <exception cref="UnsupportedMediaException">The format is not recognised, or has no repairer.</exception>
    /// <exception cref="MediaLimitExceededException">The data exceeds a limit.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static RepairReport Repair(IMediaSource input, Stream output, MediaLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanSeek || !output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable and seekable.", nameof(output));
        }

        string name;
        if (MediaFormats.Default.TrySniff(input, out FormatMatch match, cancellationToken))
        {
            name = match.Format.Id.Name;
        }
        else if (TsRepair.LooksLikeTransportStream(input, cancellationToken))
        {
            // a transport stream cut at an arbitrary byte does not start with a sync byte, so the sniffer cannot recognise it
            name = "mpegts";
        }
        else
        {
            throw new UnsupportedMediaException("The format is not recognised.");
        }

        return RunEngine(name, input, output, limits ?? MediaLimits.Default, cancellationToken);
    }

    // Runs the repairer for a format by name; the fuzz targets use it to reach one engine without going through the sniffer.
    internal static RepairReport RunEngine(string name, IMediaSource source, Stream destination, MediaLimits limits, CancellationToken cancellationToken)
    {
        using var reader = new RepairInput(source, cancellationToken);
        return name switch
        {
            "matroska" => MatroskaRepair.Run(reader, destination, limits, cancellationToken),
            "avi" => AviRepair.Run(reader, destination, limits, cancellationToken),
            "mpegts" => TsRepair.Run(reader, destination, limits, cancellationToken),
            "isobmff" => IsoRepair.Run(reader, destination, limits, cancellationToken),
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
