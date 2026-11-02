namespace RecursiveAI.OneMedia;

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
