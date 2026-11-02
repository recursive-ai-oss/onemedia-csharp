namespace RecursiveAI.OneMedia;

/// <summary>
/// Applies an <see cref="EditPlan"/> to a file safely. Three ways, in order of how much they promise:
/// <see cref="SaveAs"/> writes a new file and leaves the original alone; <see cref="Save"/> writes a new file next to the original and
/// atomically replaces it, so an interrupted save leaves the original intact; <see cref="SaveInPlace"/> overwrites only the changed ranges
/// and only when the plan keeps every other byte where it is, otherwise it refuses with <see cref="PaddingRequiredException"/>.
/// </summary>
/// <remarks>
/// Memory use is a few buffers whatever the file size. Media payload is copied by range, never held. A new file is written under a
/// temporary name in the destination's folder (<c>.name.onemedia-XXXXXXXX.tmp</c>), flushed to disk and then renamed, so the target name
/// never shows a partly written file. A process killed mid-write can leave the temporary file behind; it is never the target and is safe to
/// delete. An in-place write is the one operation that is not atomic: a crash during it can leave a metadata range half written, which is
/// why it only touches the ranges the plan names and never media payload.
/// </remarks>
public static class SafeWriter
{
    private const int FileBuffer = 128 * 1024;

    /// <summary>Writes the new file to any seekable or non-seekable stream. The original is read through <paramref name="source"/>.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="source">The original file the plan was made for.</param>
    /// <param name="destination">Where the new file goes.</param>
    /// <param name="cancellationToken">Cancels the write; the destination then holds a partial file.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="IOException">The source's length differs from the length the plan was made for, or reading or writing failed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static WriteReport Write(EditPlan plan, IMediaSource source, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        CheckSource(plan, source);
        (long copied, long written) = PieceWriter.Write(plan.Pieces, source, destination, cancellationToken);
        return new WriteReport(WriteMode.SaveAs, plan, copied, written);
    }

    /// <summary>Writes the edited file to a new path and leaves the original untouched.</summary>
    /// <param name="plan">The plan, made for the file at <paramref name="sourcePath"/>.</param>
    /// <param name="sourcePath">The original file.</param>
    /// <param name="destinationPath">The new file. Must differ from the original (use <see cref="Save"/> to replace it).</param>
    /// <param name="overwrite">Replace the destination when it exists; by default an existing destination is an error.</param>
    /// <param name="cancellationToken">Cancels the write; the temporary file is deleted.</param>
    /// <returns>The report, with <see cref="WriteReport.Mode"/> <see cref="WriteMode.SaveAs"/>.</returns>
    /// <exception cref="ArgumentException">The destination is the source file.</exception>
    /// <exception cref="IOException">The destination exists and <paramref name="overwrite"/> is false, the source changed since planning, or the disk failed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static WriteReport SaveAs(EditPlan plan, string sourcePath, string destinationPath, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        string full = Path.GetFullPath(destinationPath);
        if (string.Equals(full, Path.GetFullPath(sourcePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The destination is the source file; use Save to replace it.", nameof(destinationPath));
        }

        if (!overwrite && File.Exists(full))
        {
            throw new IOException($"'{destinationPath}' already exists.");
        }

        return WriteViaTemp(plan, sourcePath, full, WriteMode.SaveAs, copyMode: false, overwrite, cancellationToken);
    }

    /// <summary>
    /// Replaces the file with the edited version atomically: the new file is written next to it under a temporary name, flushed, and renamed over
    /// the original. Until the rename the original is complete and unchanged, so a crash or kill at any point leaves it intact.
    /// </summary>
    /// <param name="plan">The plan, made for the file at <paramref name="path"/>.</param>
    /// <param name="path">The file to replace.</param>
    /// <param name="cancellationToken">Cancels the write; the temporary file is deleted and the original stays.</param>
    /// <returns>The report, with <see cref="WriteReport.Mode"/> <see cref="WriteMode.Save"/>.</returns>
    /// <exception cref="IOException">The file changed since planning, or the disk failed.</exception>
    /// <exception cref="FileInUseException">On Windows, another open handle (such as a <see cref="MediaFile"/> on the same path) prevents the replacement; the original is untouched. Dispose the reader and save again, or use <see cref="SaveAs"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static WriteReport Save(EditPlan plan, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(path);
        // A symbolic link is followed: the edit goes to the file it points to and the link stays a link. Renaming over the link itself
        // would replace the link with a regular file and leave the real file unedited.
        string full = Path.GetFullPath(path);
        string resolved = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        return WriteViaTemp(plan, resolved, resolved, WriteMode.Save, copyMode: true, overwrite: true, cancellationToken);
    }

    /// <summary>
    /// Overwrites only the changed ranges of the file, when the plan keeps every other byte where it is
    /// (<see cref="EditPlan.IsSameLayout"/>): new content fits in existing padding and nothing moved. Otherwise nothing is written.
    /// </summary>
    /// <param name="plan">The plan, made for the file at <paramref name="path"/>.</param>
    /// <param name="path">The file to edit.</param>
    /// <param name="cancellationToken">Cancels before the first write; once writing has started it runs to the end.</param>
    /// <returns>The report, with <see cref="WriteReport.Mode"/> <see cref="WriteMode.InPlace"/> and <see cref="WriteReport.BytesCopied"/> 0.</returns>
    /// <exception cref="PaddingRequiredException">The edit does not fit in the padding the file has; the file is untouched.</exception>
    /// <exception cref="IOException">The file changed since planning, or the disk failed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static WriteReport SaveInPlace(EditPlan plan, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!plan.IsSameLayout)
        {
            throw new PaddingRequiredException(Math.Max(1, plan.PaddingRequired), plan.PaddingAvailable);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using FileMediaSource source = FileMediaSource.Open(path);
        CheckSource(plan, source);
        long written = 0;
        using (FileStream file = new(path, FileMode.Open, FileAccess.Write, FileShare.Read, FileBuffer))
        {
            long at = 0;
            foreach (OutputPiece piece in plan.Pieces)
            {
                if (piece.Kind != OutputPieceKind.Copy)
                {
                    file.Position = at;
                    PieceWriter.Write([piece], source, file, CancellationToken.None);
                    written += piece.Length;
                }

                at += piece.Length;
            }

            file.Flush(true);
        }

        return new WriteReport(WriteMode.InPlace, plan, 0, written);
    }

    private static WriteReport WriteViaTemp(EditPlan plan, string sourcePath, string target, WriteMode mode, bool copyMode, bool overwrite, CancellationToken cancellationToken)
    {
        string dir = Path.GetDirectoryName(target) ?? ".";
        string temp = Path.Combine(dir, $".{Path.GetFileName(target)}.onemedia-{Guid.NewGuid().ToString("N")[..8]}.tmp");
        bool done = false;
        try
        {
            long copied;
            long written;
            // The source handle is closed before the rename: Windows cannot replace a file while any handle to it is open, our own included.
            using (FileMediaSource source = FileMediaSource.Open(sourcePath))
            {
                CheckSource(plan, source);
                using FileStream output = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileBuffer);
                (copied, written) = PieceWriter.Write(plan.Pieces, source, output, cancellationToken);
                output.Flush(true);
            }

            if (copyMode && !OperatingSystem.IsWindows() && !OperatingSystem.IsBrowser())
            {
                // The replacement keeps the original's permissions.
                File.SetUnixFileMode(temp, File.GetUnixFileMode(sourcePath));
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temp, target, overwrite);
            }
            catch (Exception e) when (OperatingSystem.IsWindows() && e is UnauthorizedAccessException or IOException && File.Exists(target))
            {
                // Windows refuses to replace a file another handle (for example a MediaFile opened on the same path) still holds.
                // Nothing has changed: the original is complete and the temporary file is removed below.
                throw new FileInUseException(target, e);
            }

            done = true;
            return new WriteReport(mode, plan, copied, written);
        }
        finally
        {
            if (!done)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // The temporary file is never the target; leaving it behind is harmless.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same: nothing to do.
                }
            }
        }
    }

    private static void CheckSource(EditPlan plan, IMediaSource source)
    {
        if (source.Length != plan.SourceLength)
        {
            throw new IOException($"The source is {source.Length} bytes but the plan was made for {plan.SourceLength}; the file changed after it was read.");
        }
    }
}

/// <summary>
/// The target of a replacing save is held open by another handle and the operating system (Windows) does not allow it to be replaced.
/// The original file is complete and unchanged, and no temporary file is left behind.
/// </summary>
public sealed class FileInUseException : IOException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="path">The file that could not be replaced.</param>
    /// <param name="inner">The operating system's error.</param>
    public FileInUseException(string path, Exception inner)
        : base($"'{path}' is open elsewhere and cannot be replaced; close it and save again, or save to a new file.", inner)
    {
        Path = path;
    }

    /// <summary>The file that could not be replaced.</summary>
    public string Path { get; }
}
