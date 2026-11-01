using System.Globalization;

namespace RecursiveAI.OneMedia;

/// <summary>
/// Base of every exception OneMedia raises for the content of the data it reads. Input that is malformed, too large or not
/// supported never surfaces as any other exception type (argument errors by the caller and cancellation excepted).
/// </summary>
public class MediaException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MediaException() { }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public MediaException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public MediaException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>Absolute byte offset in the source where the problem was found, or -1 when unknown.</summary>
    public long Offset { get; init; } = -1;
}

/// <summary>The data is malformed in a way the reader cannot continue past.</summary>
public class MediaFormatException : MediaException
{
    /// <summary>Creates the exception.</summary>
    public MediaFormatException() { }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What is wrong with the data.</param>
    public MediaFormatException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">What is wrong with the data.</param>
    /// <param name="innerException">The cause.</param>
    public MediaFormatException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>Creates the exception for a defect the reader could not recover from.</summary>
    /// <param name="defect">The defect; its message and offset become the exception's.</param>
    public MediaFormatException(MediaDefect defect) : base(defect.Message)
    {
        Kind = defect.Kind;
        Offset = defect.Offset;
    }

    /// <summary>The category of the problem.</summary>
    public DefectKind Kind { get; init; } = DefectKind.Other;
}

/// <summary>The data is valid as far as can be told, but uses a format or feature this library does not handle.</summary>
public class UnsupportedMediaException : MediaException
{
    /// <summary>Creates the exception.</summary>
    public UnsupportedMediaException() { }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What is not supported.</param>
    public UnsupportedMediaException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">What is not supported.</param>
    /// <param name="innerException">The cause.</param>
    public UnsupportedMediaException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>The data asks for more than a <see cref="MediaLimits"/> value allows.</summary>
public sealed class MediaLimitExceededException : MediaException
{
    /// <summary>Creates the exception.</summary>
    public MediaLimitExceededException() : this("limit", 0, 0, -1) { }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public MediaLimitExceededException(string message) : base(message) { LimitName = "limit"; }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public MediaLimitExceededException(string message, Exception? innerException) : base(message, innerException) { LimitName = "limit"; }

    /// <summary>Creates the exception for a breached limit.</summary>
    /// <param name="limitName">The <see cref="MediaLimits"/> property that was breached.</param>
    /// <param name="limit">Its value.</param>
    /// <param name="requested">What the data asked for.</param>
    /// <param name="offset">Absolute byte offset of the structure that asked, or -1.</param>
    public MediaLimitExceededException(string limitName, long limit, long requested, long offset)
        : base(string.Create(CultureInfo.InvariantCulture, $"{limitName} is {limit}, but the data asks for {requested}."))
    {
        LimitName = limitName;
        Limit = limit;
        Requested = requested;
        Offset = offset;
    }

    /// <summary>The name of the <see cref="MediaLimits"/> property that was breached.</summary>
    public string LimitName { get; }

    /// <summary>The configured value.</summary>
    public long Limit { get; }

    /// <summary>The amount the data asked for.</summary>
    public long Requested { get; }
}

/// <summary>Why a <see cref="MediaReadStateException"/> was raised.</summary>
public enum MediaReadStateReason
{
    /// <summary>The file was already read and its source cannot seek, so it cannot start again from the beginning.</summary>
    CannotRewind = 1,

    /// <summary>A newer read of the same file (packets of another track, a decoder) took over the demuxer, so this reader would no longer see its own position.</summary>
    Superseded,
}

/// <summary>
/// A read of a <see cref="MediaFile"/> cannot proceed from the state the file is in. This is a caller error (not bad data), raised instead
/// of returning no packets: a file reads one track at a time, starts again from the beginning on each new read when its source can seek,
/// and refuses when it cannot.
/// </summary>
public sealed class MediaReadStateException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Why the read cannot proceed.</param>
    /// <param name="message">What happened and what to do.</param>
    public MediaReadStateException(MediaReadStateReason reason, string message) : base(message) { Reason = reason; }

    /// <summary>Why the read cannot proceed.</summary>
    public MediaReadStateReason Reason { get; }
}
