using System.Collections;

namespace RecursiveAI.OneMedia;

/// <summary>How serious a <see cref="MediaDefect"/> is.</summary>
public enum DefectSeverity
{
    /// <summary>Unusual but legal, or a harmless quirk of a known writer.</summary>
    Info,

    /// <summary>A deviation from the specification that does not change what is read.</summary>
    Warning,

    /// <summary>Data was lost, skipped or had to be guessed.</summary>
    Error,
}

/// <summary>What kind of deviation a <see cref="MediaDefect"/> describes. Format layers add detail in <see cref="MediaDefect.Code"/>.</summary>
public enum DefectKind
{
    /// <summary>None of the other kinds.</summary>
    Other = 0,

    /// <summary>The data ends before a structure is complete.</summary>
    Truncated,

    /// <summary>A size field is larger than the structure that contains it.</summary>
    SizeExceedsParent,

    /// <summary>A size field reaches past the end of the file.</summary>
    SizeExceedsFile,

    /// <summary>A size field is impossible (smaller than its own header, or otherwise invalid).</summary>
    InvalidSize,

    /// <summary>A field holds a value the specification does not allow.</summary>
    InvalidValue,

    /// <summary>A field holds a reserved or unassigned value.</summary>
    ReservedValue,

    /// <summary>A required structure is missing.</summary>
    MissingRequired,

    /// <summary>A structure that must appear once appears more than once.</summary>
    Duplicate,

    /// <summary>Structures appear in an order the specification forbids.</summary>
    InvalidOrder,

    /// <summary>Two parts of the file disagree (a count against a table, a duration against its samples).</summary>
    Inconsistent,

    /// <summary>A checksum or CRC does not match.</summary>
    ChecksumMismatch,

    /// <summary>Bytes follow the last structure, or fill a gap between structures, that no structure accounts for.</summary>
    UnaccountedBytes,

    /// <summary>A resource limit made the reader skip part of the file.</summary>
    LimitReached,

    /// <summary>A structure was recognised but is not supported, and was preserved as opaque bytes.</summary>
    Unsupported,
}

/// <summary>
/// One deviation from the specification found while reading, with where it was found. Readers collect these instead of
/// throwing, and keep going where they safely can.
/// </summary>
/// <param name="Kind">The category.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Offset">Absolute byte offset in the source of the offending structure, or -1 when it has no position.</param>
/// <param name="Message">A short human-readable description.</param>
/// <param name="Code">An optional stable, format-specific identifier such as <c>isobmff.stco.count</c>, for filtering.</param>
public readonly record struct MediaDefect(DefectKind Kind, DefectSeverity Severity, long Offset, string Message, string? Code = null)
{
    /// <inheritdoc />
    public override string ToString() => $"{Severity} {Kind} at {Offset}: {Message}";
}

/// <summary>
/// Collects the <see cref="MediaDefect"/>s a reader finds. Keeps at most <see cref="MediaLimits.MaxDefects"/>; further ones are
/// counted in <see cref="Dropped"/>. Not thread-safe.
/// </summary>
public sealed class DefectLog : IReadOnlyList<MediaDefect>
{
    private readonly List<MediaDefect> _items = [];
    private readonly int _capacity;

    /// <summary>Creates a log with the default capacity.</summary>
    public DefectLog() : this(MediaLimits.Default) { }

    /// <summary>Creates a log whose capacity comes from <paramref name="limits"/>.</summary>
    /// <param name="limits">Supplies <see cref="MediaLimits.MaxDefects"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="limits"/> is null.</exception>
    public DefectLog(MediaLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _capacity = limits.MaxDefects;
    }

    /// <summary>Number of defects kept.</summary>
    public int Count => _items.Count;

    /// <summary>Number of defects reported after the log was full.</summary>
    public int Dropped { get; private set; }

    /// <summary>True when any kept defect has severity <see cref="DefectSeverity.Error"/>, or any was dropped.</summary>
    public bool HasErrors { get; private set; }

    /// <summary>The defect at <paramref name="index"/>, in the order reported.</summary>
    public MediaDefect this[int index] => _items[index];

    /// <summary>Records a defect, or counts it as dropped when the log is full.</summary>
    /// <param name="defect">The defect.</param>
    public void Add(MediaDefect defect)
    {
        if (defect.Severity == DefectSeverity.Error)
        {
            HasErrors = true;
        }

        if (_items.Count < _capacity)
        {
            _items.Add(defect);
        }
        else
        {
            Dropped++;
        }
    }

    /// <summary>Records a defect from its parts.</summary>
    /// <param name="kind">The category.</param>
    /// <param name="severity">How serious it is.</param>
    /// <param name="offset">Absolute byte offset, or -1.</param>
    /// <param name="message">A short description.</param>
    /// <param name="code">An optional stable format-specific identifier.</param>
    public void Add(DefectKind kind, DefectSeverity severity, long offset, string message, string? code = null) =>
        Add(new MediaDefect(kind, severity, offset, message, code));

    /// <inheritdoc />
    public IEnumerator<MediaDefect> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
