namespace RecursiveAI.OneMedia;

/// <summary>
/// A new file written under a temporary name next to its target and renamed over it when complete, so the target name never shows a partly written
/// file. A process killed before <see cref="Commit"/> leaves only the temporary file (named as <see cref="SafeWriter"/> names its own, and removed by
/// the next write to the same target).
/// </summary>
internal sealed class AtomicOutput : IDisposable
{
    private readonly string _temp;
    private readonly string _target;
    private readonly bool _overwrite;
    private bool _committed;
    private bool _disposed;

    private AtomicOutput(string temp, string target, bool overwrite, FileStream stream)
    {
        _temp = temp;
        _target = target;
        _overwrite = overwrite;
        Stream = stream;
    }

    /// <summary>The temporary file, open for writing and seeking.</summary>
    public FileStream Stream { get; }

    /// <summary>Opens the temporary file for <paramref name="path"/>.</summary>
    /// <exception cref="IOException">The target exists and <paramref name="overwrite"/> is false, or the folder cannot be written.</exception>
    public static AtomicOutput Create(string path, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.GetFullPath(path);
        if (!overwrite && File.Exists(full))
        {
            throw new IOException($"'{path}' already exists.");
        }

        string dir = Path.GetDirectoryName(full) ?? ".";
        SafeWriter.RemoveStaleTemporaryFiles(dir, Path.GetFileName(full));
        string temp = Path.Combine(dir, SafeWriter.TemporaryName(Path.GetFileName(full)));
        FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024);
        return new AtomicOutput(temp, full, overwrite, stream);
    }

    /// <summary>The path of the temporary file, for work that has to open it by name before <see cref="Commit"/> (it is closed first by <see cref="CloseStream"/>).</summary>
    public string TempPath => _temp;

    /// <summary>Flushes the data to the disk and closes the temporary file, leaving it in place.</summary>
    public void CloseStream()
    {
        if (Stream.CanWrite)
        {
            Stream.Flush(true);
        }

        Stream.Dispose();
    }

    /// <summary>Flushes the data to the disk, closes the temporary file and renames it to the target.</summary>
    /// <exception cref="IOException">The rename failed; the target is unchanged.</exception>
    public void Commit()
    {
        CloseStream();
        File.Move(_temp, _target, _overwrite);
        _committed = true;
    }

    /// <summary>Closes and deletes the temporary file unless <see cref="Commit"/> succeeded.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stream.Dispose();
        if (_committed)
        {
            return;
        }

        try
        {
            File.Delete(_temp);
        }
        catch (IOException)
        {
            // The temporary file is never the target; leaving it behind is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
