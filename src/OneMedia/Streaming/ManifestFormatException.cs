namespace RecursiveAI.OneMedia.Streaming;

/// <summary>An HLS playlist or a DASH MPD is malformed in a way the parser cannot read past.</summary>
/// <remarks>The <c>TryParse</c> methods of the manifest types return false instead of throwing this. <see cref="MediaException.Offset"/> is -1: manifests are text, so <see cref="Line"/> (HLS) is the position.</remarks>
public sealed class ManifestFormatException : MediaFormatException
{
    /// <summary>Creates the exception.</summary>
    public ManifestFormatException() { }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What was wrong.</param>
    public ManifestFormatException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the line it was found on.</summary>
    /// <param name="message">What was wrong.</param>
    /// <param name="line">The 1-based line of an HLS playlist, or 0 when not applicable.</param>
    public ManifestFormatException(string message, int line) : base(line > 0 ? $"line {line}: {message}" : message) { Line = line; }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    /// <param name="message">What was wrong.</param>
    /// <param name="innerException">The cause.</param>
    public ManifestFormatException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>The 1-based line of an HLS playlist where the problem was found; 0 when the manifest is XML or the line is unknown.</summary>
    public int Line { get; }
}
