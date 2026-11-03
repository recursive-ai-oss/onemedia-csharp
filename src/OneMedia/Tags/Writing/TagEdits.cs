using System.Linq;
using System.Globalization;
using RecursiveAI.OneMedia.Writing;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>What a <see cref="TagEdit"/> does.</summary>
public enum TagEditKind
{
    /// <summary>Sets a common field to one or more values, replacing what the file holds for it.</summary>
    SetField,

    /// <summary>Removes a common field from every system written.</summary>
    RemoveField,

    /// <summary>Adds a picture, replacing a picture of the same role.</summary>
    SetPicture,

    /// <summary>Removes pictures (all, or of one role).</summary>
    RemovePictures,

    /// <summary>Sets one native key of one system.</summary>
    SetNative,

    /// <summary>Removes one native key of one system.</summary>
    RemoveNative,
}

/// <summary>One requested change to a file's tags. Built by <see cref="TagEdits"/>.</summary>
public sealed class TagEdit
{
    internal TagEdit(TagEditKind kind) => Kind = kind;

    /// <summary>What the edit does.</summary>
    public TagEditKind Kind { get; }

    /// <summary>The common field, for the field edits.</summary>
    public CommonField Field { get; internal init; }

    /// <summary>The new values (one for most fields, several for keywords), for the set edits.</summary>
    public IReadOnlyList<string> Values { get; internal init; } = [];

    /// <summary>The native system (<see cref="TagSystems"/>), for the native edits.</summary>
    public string? System { get; internal init; }

    /// <summary>The native key, for the native edits.</summary>
    public string? Key { get; internal init; }

    /// <summary>For <see cref="TagEditKind.RemoveNative"/> in Matroska: also remove the key from tags aimed at a track, chapter, edition or attachment and from nested SimpleTags (the privacy strip needs every copy gone).</summary>
    internal bool Everywhere { get; init; }

    /// <summary>The picture bytes, for <see cref="TagEditKind.SetPicture"/>.</summary>
    public ReadOnlyMemory<byte> PictureData { get; internal init; }

    /// <summary>The picture's media type, for <see cref="TagEditKind.SetPicture"/>.</summary>
    public string? MimeType { get; internal init; }

    /// <summary>The picture's role, for the picture edits; null on <see cref="TagEditKind.RemovePictures"/> means every role.</summary>
    public PictureType? PictureRole { get; internal init; }

    /// <summary>The picture's description, for <see cref="TagEditKind.SetPicture"/>.</summary>
    public string Description { get; internal init; } = "";
}

/// <summary>
/// An ordered set of tag changes expressed on the common view (or on one native key). Later edits of the same field win. The set holds
/// the new values only, never the file; <see cref="TagWriter"/> turns it into an edit plan for a given file.
/// </summary>
public sealed class TagEdits
{
    private readonly List<TagEdit> _edits = [];

    /// <summary>The edits in the order they were made.</summary>
    public IReadOnlyList<TagEdit> Edits => _edits;

    /// <summary>True when no edit was made.</summary>
    public bool IsEmpty => _edits.Count == 0 && HeaderDate is null;

    /// <summary>The binary capture-date fields (movie, track and media header times, Matroska DateUTC, ASF creation date, Exif blocks) a capture-time write sets; null for plain tag edits.</summary>
    internal CaptureDateEdit? HeaderDate { get; set; }

    /// <summary>Sets a field to one value.</summary>
    /// <param name="field">The field.</param>
    /// <param name="value">The new text; an empty string removes the field.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public void Set(CommonField field, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            Remove(field);
            return;
        }

        _edits.Add(new TagEdit(TagEditKind.SetField) { Field = field, Values = [value] });
    }

    /// <summary>Sets a field to several values (keywords, or a multi-valued artist where the system has them).</summary>
    /// <param name="field">The field.</param>
    /// <param name="values">The values; none removes the field.</param>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    public void Set(CommonField field, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string[] list = [.. values.Where(v => !string.IsNullOrEmpty(v))];
        if (list.Length == 0)
        {
            Remove(field);
            return;
        }

        _edits.Add(new TagEdit(TagEditKind.SetField) { Field = field, Values = list });
    }

    /// <summary>Sets a numeric field (track, disc and their totals).</summary>
    /// <param name="field">The field.</param>
    /// <param name="value">The number; zero or less removes the field.</param>
    public void Set(CommonField field, int value)
    {
        if (value <= 0)
        {
            Remove(field);
            return;
        }

        Set(field, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Removes a field from every system that holds it.</summary>
    /// <param name="field">The field.</param>
    public void Remove(CommonField field) => _edits.Add(new TagEdit(TagEditKind.RemoveField) { Field = field });

    /// <summary>Sets a picture, replacing a picture of the same role.</summary>
    /// <param name="data">The encoded image (JPEG, PNG…).</param>
    /// <param name="mimeType">The media type, for example <c>image/jpeg</c>.</param>
    /// <param name="role">The picture's role.</param>
    /// <param name="description">A free description, for the systems that carry one.</param>
    /// <exception cref="ArgumentException"><paramref name="data"/> is empty or <paramref name="mimeType"/> is empty.</exception>
    public void SetPicture(ReadOnlyMemory<byte> data, string mimeType, PictureType role = PictureType.FrontCover, string description = "")
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("The picture has no bytes.", nameof(data));
        }

        ArgumentException.ThrowIfNullOrEmpty(mimeType);
        _edits.Add(new TagEdit(TagEditKind.SetPicture) { PictureData = data, MimeType = mimeType, PictureRole = role, Description = description ?? "" });
    }

    /// <summary>Removes pictures.</summary>
    /// <param name="role">Only pictures of this role, or every picture when null.</param>
    public void RemovePictures(PictureType? role = null) => _edits.Add(new TagEdit(TagEditKind.RemovePictures) { PictureRole = role });

    /// <summary>Sets one native key of one system, as that system spells it.</summary>
    /// <param name="system">One of <see cref="TagSystems"/>.</param>
    /// <param name="key">The key, as <see cref="NativeTag.Key"/> spells it.</param>
    /// <param name="value">The text.</param>
    public void SetNative(string system, string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(system);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        _edits.Add(new TagEdit(TagEditKind.SetNative) { System = system, Key = key, Values = [value] });
    }

    /// <summary>Removes one native key of one system.</summary>
    /// <param name="system">One of <see cref="TagSystems"/>.</param>
    /// <param name="key">The key.</param>
    public void RemoveNative(string system, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(system);
        ArgumentException.ThrowIfNullOrEmpty(key);
        _edits.Add(new TagEdit(TagEditKind.RemoveNative) { System = system, Key = key });
    }

    /// <summary>Removes one native key wherever the system keeps a copy (Matroska: also tags with targets and nested SimpleTags). Used by the privacy strip.</summary>
    internal void RemoveNativeEverywhere(string system, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(system);
        ArgumentException.ThrowIfNullOrEmpty(key);
        _edits.Add(new TagEdit(TagEditKind.RemoveNative) { System = system, Key = key, Everywhere = true });
    }

    /// <summary>The effective change of a field after all edits: <c>null</c> when untouched, an empty list when removed, else the values.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The change.</returns>
    public IReadOnlyList<string>? FieldChange(CommonField field)
    {
        IReadOnlyList<string>? result = null;
        foreach (TagEdit e in _edits)
        {
            if (e.Field == field && e.Kind == TagEditKind.SetField)
            {
                result = e.Values;
            }
            else if (e.Field == field && e.Kind == TagEditKind.RemoveField)
            {
                result = [];
            }
        }

        return result;
    }

    /// <summary>The fields any edit touches, in first-touched order.</summary>
    public IReadOnlyList<CommonField> TouchedFields
    {
        get
        {
            List<CommonField> list = [];
            foreach (TagEdit e in _edits)
            {
                if (e.Kind is TagEditKind.SetField or TagEditKind.RemoveField && !list.Contains(e.Field))
                {
                    list.Add(e.Field);
                }
            }

            return list;
        }
    }

    /// <summary>The native edits for one system, in order.</summary>
    /// <param name="system">The system.</param>
    /// <returns>The edits.</returns>
    public IEnumerable<TagEdit> NativeFor(string system) => _edits.Where(e => e.Kind is TagEditKind.SetNative or TagEditKind.RemoveNative && e.System == system);

    /// <summary>The picture edits in order.</summary>
    public IEnumerable<TagEdit> PictureEdits => _edits.Where(e => e.Kind is TagEditKind.SetPicture or TagEditKind.RemovePictures);

    /// <summary>The effective change of a numeric pair member as a number, or null when untouched or removed.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The number.</returns>
    public int? NumberChange(CommonField field) => FieldChange(field) is { Count: > 0 } v && int.TryParse(v[0], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
}

/// <summary>What happened to the fields in one tag system.</summary>
/// <param name="System">The system (<see cref="TagSystems"/>).</param>
/// <param name="Written">The fields set.</param>
/// <param name="Removed">The fields removed.</param>
/// <param name="NotRepresentable">Fields the system cannot hold, with the reason; they were not written to it.</param>
/// <param name="Conversions">Conversions applied (for example a date shortened to a year, or text cut to 30 bytes).</param>
/// <param name="Created">True when the system was not in the file and was created.</param>
public sealed record SystemWriteReport(string System, IReadOnlyList<CommonField> Written, IReadOnlyList<CommonField> Removed, IReadOnlyList<(CommonField Field, string Reason)> NotRepresentable, IReadOnlyList<string> Conversions, bool Created)
{
    /// <summary>A one-line summary.</summary>
    public override string ToString() => $"{System}: {Written.Count} written, {Removed.Count} removed, {NotRepresentable.Count} not representable{(Created ? ", created" : "")}";
}

/// <summary>The write-back report of a tag write: per system, what was written, removed and not representable, plus the plan's own report once applied.</summary>
public sealed class TagWriteReport
{
    internal TagWriteReport(IReadOnlyList<SystemWriteReport> systems, IReadOnlyList<string> notes)
    {
        Systems = systems;
        Notes = notes;
    }

    /// <summary>One entry per tag system that was written or created.</summary>
    public IReadOnlyList<SystemWriteReport> Systems { get; }

    /// <summary>Remarks on the file as a whole (an ID3v2.2 tag upgraded, a system that is read-only, a field no present system could hold).</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>
    /// Things the write left as they were that a reader of the result may not expect. A <see cref="CommonField.Date"/> write lists each other date tag
    /// the file keeps (a QuickTime <c>creationdate</c>, a Matroska <c>DATE_RECORDED</c>, an ID3 <c>TDOR</c>, the movie header's creation time) because tools
    /// such as MediaInfo and ExifTool may show one of those instead. Empty when there is nothing to warn about.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; internal set; } = [];

    /// <summary>The byte-level report of the save; null until the plan was saved.</summary>
    public WriteReport? Write { get; internal set; }

    /// <summary>The system's entry, or null when it was not written.</summary>
    /// <param name="system">One of <see cref="TagSystems"/>.</param>
    /// <returns>The entry.</returns>
    public SystemWriteReport? For(string system) => Systems.FirstOrDefault(s => s.System == system);

    /// <summary>A one-line summary. The data is in the properties.</summary>
    public override string ToString() => $"Tag write: {Systems.Count} system{(Systems.Count == 1 ? "" : "s")}, {Warnings.Count} warning{(Warnings.Count == 1 ? "" : "s")} (see Systems, Warnings)";
}

/// <summary>A planned tag write: the byte-level <see cref="EditPlan"/> and the write-back report.</summary>
public sealed class TagWritePlan
{
    internal TagWritePlan(EditPlan plan, TagWriteReport report)
    {
        Plan = plan;
        Report = report;
    }

    /// <summary>The plan, applied with <see cref="SafeWriter"/> (or the <c>Save</c> helpers on <see cref="TagWriter"/>).</summary>
    public EditPlan Plan { get; }

    /// <summary>What was written where.</summary>
    public TagWriteReport Report { get; }
}

/// <summary>Options of a tag write.</summary>
public sealed class TagWriteOptions
{
    /// <summary>The default: write to every system present, and create the format's main system when none is.</summary>
    public static TagWriteOptions Default { get; } = new();

    /// <summary>
    /// Write only to these systems (<see cref="TagSystems"/>), creating them when absent. Null (default) means every system the file has.
    /// </summary>
    public IReadOnlyList<string>? Systems { get; init; }

    /// <summary>The ID3v2 version written when a tag is created or a version 2.2 tag is upgraded: 3 or 4 (default 4).</summary>
    public int Id3v2Version { get; init; } = 4;

    /// <summary>
    /// Padding to leave behind a tag region that has to grow, in bytes (default 1024), so that a later edit can be saved in place. It applies to a rewritten
    /// ID3v2 tag, FLAC metadata, the ASF header, an Opus comment header, and to each MP4 box (<c>free</c>), Matroska element (Void) or RIFF/AIFF chunk
    /// (<c>JUNK</c>) that had to grow. A tag that fits where it was keeps its size, and a file with no edit gains no padding. 0 asks for the smallest output.
    /// </summary>
    public int PaddingBytes { get; init; } = 1024;

    /// <summary>Options for the smallest output: no padding is left behind a growing tag region.</summary>
    public static TagWriteOptions Minimal { get; } = new() { PaddingBytes = 0 };

    /// <summary>Limits applied while reading the file's existing tags.</summary>
    public MediaLimits? Limits { get; init; }

    internal bool Wants(string system) => Systems is null || Systems.Contains(system);
}
