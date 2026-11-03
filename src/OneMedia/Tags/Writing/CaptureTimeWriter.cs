using System.Globalization;
using System.Linq;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Tags.Xmp;
using RecursiveAI.OneMedia.Writing;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>Options of a capture-time write.</summary>
public sealed class CaptureTimeOptions
{
    /// <summary>The defaults: header times as the device profile says (UTC when no profile applies), missing canonical fields added, telemetry checked for disagreements.</summary>
    public static CaptureTimeOptions Default { get; } = new();

    /// <summary>
    /// True to store the wall clock of the given offset in the binary header times (<c>mvhd</c>, <c>tkhd</c>, <c>mdhd</c>) as some cameras do, false to store UTC as
    /// the specification says, null (default) to follow the device profile that matches the file and use UTC when none does. The report says which was used and why.
    /// </summary>
    public bool? HeaderTimesAreLocal { get; init; }

    /// <summary>
    /// True (default) to add the format's canonical capture-date field when the file has none: Matroska <c>DateUTC</c>, a RIFF INFO <c>ICRD</c> (WAVE) or <c>IDIT</c> (AVI),
    /// an ID3 <c>TDRC</c> (MP3), a Vorbis comment <c>DATE</c> (Ogg, FLAC). MP4/MOV/3GP and ASF always have their header times, so nothing is added to them.
    /// </summary>
    public bool AddMissing { get; init; } = true;

    /// <summary>True (default) to read GPS time from telemetry tracks and list it in the report's disagreements; false skips that read.</summary>
    public bool CheckTelemetry { get; init; } = true;

    /// <summary>Padding to leave behind a tag region that has to grow; see <see cref="TagWriteOptions.PaddingBytes"/>.</summary>
    public int PaddingBytes { get; init; } = 1024;

    /// <summary>Limits applied while reading the file.</summary>
    public MediaLimits? Limits { get; init; }
}

/// <summary>A date that still differs from the capture time written, left in the file on purpose.</summary>
/// <param name="Source">Where it is (<c>telemetry:GPS</c>, <c>mkv:DATE_ENCODED</c>…).</param>
/// <param name="RawValue">What the file holds.</param>
/// <param name="Interpretation">How it reads as a date and time.</param>
/// <param name="Note">Why it was not changed.</param>
public sealed record DateDisagreement(string Source, string RawValue, string Interpretation, string Note);

/// <summary>What a capture-time write did: every field set with its old and new text, the fields left and why, and the dates that still disagree.</summary>
public sealed class CaptureTimeReport
{
    internal CaptureTimeReport(DateTimeOffset when, bool headerTimesLocal, string headerTimesBasis, IReadOnlyList<DateFieldWrite> written, IReadOnlyList<DateFieldLeft> left, IReadOnlyList<DateDisagreement> disagreements, TagWriteReport tags, IReadOnlyList<string> warnings)
    {
        When = when;
        HeaderTimesLocal = headerTimesLocal;
        HeaderTimesBasis = headerTimesBasis;
        Written = written;
        Left = left;
        Disagreements = disagreements;
        Tags = tags;
        Warnings = warnings;
    }

    /// <summary>The capture time that was written.</summary>
    public DateTimeOffset When { get; }

    /// <summary>True when the binary header times were written as the local wall clock instead of UTC.</summary>
    public bool HeaderTimesLocal { get; }

    /// <summary>Why the header times were written as they were: the specification, the matching device profile, or the option.</summary>
    public string HeaderTimesBasis { get; }

    /// <summary>Every field set, in file order: where, which field, old text, new text, and the zone semantics of the field.</summary>
    public IReadOnlyList<DateFieldWrite> Written { get; }

    /// <summary>Capture-date fields found and not changed, with the reason.</summary>
    public IReadOnlyList<DateFieldLeft> Left { get; }

    /// <summary>Dates the file still carries that differ from the one written (telemetry GPS times, fields left alone). They are evidence that may disagree, not errors.</summary>
    public IReadOnlyList<DateDisagreement> Disagreements { get; }

    /// <summary>The tag-level report of the same write.</summary>
    public TagWriteReport Tags { get; }

    /// <summary>Remarks that a reader of the result may not expect.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The byte-level report of the save; null until the plan was saved.</summary>
    public WriteReport? Write { get; internal set; }

    /// <summary>A one-line summary. The data is in the properties.</summary>
    public override string ToString() => $"Capture time: {Written.Count} field{(Written.Count == 1 ? "" : "s")} written, {Left.Count} left, {Warnings.Count} warning{(Warnings.Count == 1 ? "" : "s")} (see Written, Left, Warnings)";
}

/// <summary>A planned capture-time write: the byte-level plan and the report.</summary>
public sealed class CaptureTimePlan
{
    internal CaptureTimePlan(EditPlan plan, CaptureTimeReport report)
    {
        Plan = plan;
        Report = report;
    }

    /// <summary>The plan, applied with <see cref="SafeWriter"/> or the <c>Save</c> helpers of <see cref="CaptureTimeWriter"/>.</summary>
    public EditPlan Plan { get; }

    /// <summary>What was written where.</summary>
    public CaptureTimeReport Report { get; }
}

/// <summary>
/// Sets the capture time of a recording in every date field the file carries, each in its own notation and zone semantics, so that file managers, players and
/// metadata tools show the instant that was written. Media payload bytes are copied, never re-serialised; the write goes to a new file unless
/// <see cref="Save"/> or <see cref="SaveInPlace"/> is used.
/// </summary>
/// <remarks>
/// Fields written, where the file has them: MP4/MOV/3GP movie, track and media header times (UTC, or local wall clock when a device profile or the options say the
/// device stores local time), <c>©day</c> in the item list and user data, <c>creationdate</c> and <c>creation_time</c> in key-table metadata, XMP
/// <c>xmp:CreateDate</c>, <c>photoshop:DateCreated</c> and <c>exif:DateTimeOriginal</c>, Exif blocks in user-data atoms; Matroska <c>DateUTC</c>, <c>DATE_RECORDED</c> and <c>DATE_DIGITIZED</c>;
/// RIFF INFO <c>ICRD</c> and <c>IDIT</c>; BWF <c>OriginationDate</c> and <c>OriginationTime</c>; ID3 <c>TDRC</c> (and v2.3 <c>TYER</c>/<c>TDAT</c>/<c>TIME</c>) and <c>TDOR</c>;
/// Vorbis <c>DATE</c>; the ASF File Properties creation date. Telemetry GPS times are never rewritten: the report lists them as disagreements.
/// Fields that hold a year only, release or tagging dates and other dates that are not the capture are left and listed with the reason.
/// </remarks>
public static class CaptureTimeWriter
{
    private static readonly DateTime MinDate = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MaxDate = new(2262, 4, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Plans a capture-time write for the file at <paramref name="path"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="when">The capture time with the offset the recorder was in; the offset decides the wall clock written to the zone-less fields.</param>
    /// <param name="options">Options; <see cref="CaptureTimeOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels planning.</param>
    /// <returns>The plan and report.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="when"/> is before 1904 or after the year 2262, the range every date field can hold.</exception>
    /// <exception cref="UnsupportedMediaException">The format has no capture-date writer, or its layout cannot be edited safely.</exception>
    public static CaptureTimePlan Plan(string path, DateTimeOffset when, CaptureTimeOptions? options = null, CancellationToken cancellationToken = default)
    {
        using FileMediaSource source = FileMediaSource.Open(path);
        return Plan(source, when, options, cancellationToken);
    }

    /// <summary>Plans a capture-time write for a source.</summary>
    /// <param name="source">The source; read only during planning.</param>
    /// <param name="when">The capture time. A <see cref="MediaDateTime"/> without a known zone is taken as UTC.</param>
    /// <param name="options">Options.</param>
    /// <param name="cancellationToken">Cancels planning.</param>
    /// <returns>The plan and report.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The time is outside 1904 to 2262.</exception>
    public static CaptureTimePlan Plan(IMediaSource source, MediaDateTime when, CaptureTimeOptions? options = null, CancellationToken cancellationToken = default) =>
        Plan(source, when.ToDateTimeOffset() ?? new DateTimeOffset(DateTime.SpecifyKind(when.Value, DateTimeKind.Utc)), options, cancellationToken);

    /// <summary>Plans a capture-time write for a source.</summary>
    /// <param name="source">The source; read only during planning.</param>
    /// <param name="when">The capture time with its offset.</param>
    /// <param name="options">Options.</param>
    /// <param name="cancellationToken">Cancels planning.</param>
    /// <returns>The plan and report.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The time is outside 1904 to 2262.</exception>
    /// <exception cref="UnsupportedMediaException">The format has no capture-date writer, or its layout cannot be edited safely.</exception>
    public static CaptureTimePlan Plan(IMediaSource source, DateTimeOffset when, CaptureTimeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= CaptureTimeOptions.Default;
        if (when.UtcDateTime < MinDate || when.UtcDateTime >= MaxDate)
        {
            throw new ArgumentOutOfRangeException(nameof(when), "The capture time must lie between 1904 and 2262, the range the date fields can hold.");
        }

        MediaLimits limits = options.Limits ?? MediaLimits.Default;
        using MediaFile file = MediaFile.Open(source, null, new MediaOpenOptions { Limits = limits, LeaveOpen = true }, cancellationToken);
        if (options.CheckTelemetry)
        {
            try
            {
                _ = file.Telemetry;
            }
            catch (MediaException)
            {
                // Telemetry the reader cannot decode contributes no evidence.
            }
        }

        MediaFacts facts = file.Facts;
        bool local;
        string basis;
        if (options.HeaderTimesAreLocal is { } asked)
        {
            local = asked;
            basis = asked ? "requested: header times hold the local wall clock" : "requested: header times hold UTC";
        }
        else if (facts.Profile is { HeaderTimeIsLocal: true } profile)
        {
            local = true;
            basis = $"device profile '{profile.Id}': header times hold the camera's local wall clock";
        }
        else
        {
            local = false;
            basis = "specification: header times are UTC";
        }

        CaptureDateEdit header = new(when, local);
        TagEdits edits = new() { HeaderDate = header };
        List<DateFieldWrite> written = header.Written;
        List<DateFieldLeft> left = header.Left;
        // Candidate sources spell keys as the file does (a Vorbis "date" is a "DATE"), so the comparison ignores case.
        HashSet<string> done = new(StringComparer.OrdinalIgnoreCase);
        BuildTagEdits(file, when, options, edits, written, left, done);

        // Native writes are reported here; the header-field writes were added by the format writers while the plan was built.
        List<DateFieldWrite> native = [.. written];
        written.Clear();
        TagWriteOptions tagOptions = new() { PaddingBytes = options.PaddingBytes, Limits = limits };
        TagWritePlan planned = TagWriter.Plan(source, edits, tagOptions, cancellationToken);
        List<DateFieldWrite> all = [.. written, .. native];
        foreach (DateFieldWrite w in written)
        {
            done.Add(SourceOf(w));
        }

        List<DateDisagreement> disagreements = [];
        foreach (FactCandidate<MediaDateTime> c in file.CaptureTime.Candidates)
        {
            if (done.Contains(c.Source) || IsDoneSource(c.Source, done))
            {
                continue;
            }

            if (!Differs(c.Value, when))
            {
                continue;
            }

            bool telemetry = c.Source.StartsWith("telemetry", StringComparison.OrdinalIgnoreCase) || c.Source.Contains("GPS", StringComparison.Ordinal);
            disagreements.Add(new DateDisagreement(c.Source, c.RawValue, c.Value.ToString(), telemetry
                ? "GPS time from telemetry; recorded by the device and never rewritten"
                : "not a field this write sets"));
        }

        List<string> warnings = [];
        foreach (DateDisagreement d in disagreements)
        {
            warnings.Add($"{d.Source} still reads {d.RawValue} ({d.Note}).");
        }

        CaptureTimeReport report = new(when, local, basis, all, left, disagreements, planned.Report, warnings);
        return new CaptureTimePlan(planned.Plan, report);
    }

    /// <summary>Writes the capture time to a new file at <paramref name="destination"/>; the original is untouched.</summary>
    /// <param name="path">The source file.</param>
    /// <param name="destination">The new file.</param>
    /// <param name="when">The capture time with its offset.</param>
    /// <param name="options">Options.</param>
    /// <param name="overwrite">True to replace an existing destination.</param>
    /// <param name="cancellationToken">Cancels before the file is complete.</param>
    /// <returns>The report, with <see cref="CaptureTimeReport.Write"/> set.</returns>
    /// <exception cref="IOException">The destination exists and <paramref name="overwrite"/> is false, or the disk failed.</exception>
    public static CaptureTimeReport SaveAs(string path, string destination, DateTimeOffset when, CaptureTimeOptions? options = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        CaptureTimePlan plan = Plan(path, when, options, cancellationToken);
        plan.Report.Write = SafeWriter.SaveAs(plan.Plan, path, destination, overwrite, cancellationToken);
        return plan.Report;
    }

    /// <summary>Writes the capture time by atomic replacement of the file (a temporary file in the same directory, then a rename).</summary>
    /// <param name="path">The file.</param>
    /// <param name="when">The capture time with its offset.</param>
    /// <param name="options">Options.</param>
    /// <param name="cancellationToken">Cancels before the rename.</param>
    /// <returns>The report, with <see cref="CaptureTimeReport.Write"/> set.</returns>
    public static CaptureTimeReport Save(string path, DateTimeOffset when, CaptureTimeOptions? options = null, CancellationToken cancellationToken = default)
    {
        CaptureTimePlan plan = Plan(path, when, options, cancellationToken);
        plan.Report.Write = SafeWriter.Save(plan.Plan, path, cancellationToken);
        return plan.Report;
    }

    /// <summary>Writes the capture time into the file itself, only when the result fits the padding the file already has.</summary>
    /// <param name="path">The file.</param>
    /// <param name="when">The capture time with its offset.</param>
    /// <param name="options">Options.</param>
    /// <param name="cancellationToken">Cancels before the first byte is written.</param>
    /// <returns>The report, with <see cref="CaptureTimeReport.Write"/> set.</returns>
    /// <exception cref="PaddingRequiredException">The edit does not fit; the file is untouched.</exception>
    public static CaptureTimeReport SaveInPlace(string path, DateTimeOffset when, CaptureTimeOptions? options = null, CancellationToken cancellationToken = default)
    {
        CaptureTimePlan plan = Plan(path, when, options, cancellationToken);
        plan.Report.Write = SafeWriter.SaveInPlace(plan.Plan, path, cancellationToken);
        return plan.Report;
    }

    // The candidate source a header write replaces.
    private static string SourceOf(DateFieldWrite w) => w.System switch
    {
        "mvhd" or "tkhd" or "mdhd" => w.System,
        "mkv-info" => "mkv-info:DateUTC",
        "asf" => "asf:FileProperties.CreationDate",
        _ => w.System + ":" + w.Field,
    };

    private static bool IsDoneSource(string source, HashSet<string> done)
    {
        // Exif candidates read exif:QVMI:ExifIFD.DateTimeOriginal; the write is listed as system exif:QVMI, field ExifIFD.DateTimeOriginal.
        foreach (string d in done)
        {
            if (d.StartsWith("exif:", StringComparison.Ordinal) && source == d)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Differs(MediaDateTime c, DateTimeOffset when)
    {
        if (c.ToDateTimeOffset() is { } instant)
        {
            return Math.Abs((instant - when).TotalSeconds) >= 1;
        }

        // A time without a zone is compared as a wall clock against the written wall clock.
        return Math.Abs((c.Value - when.DateTime).TotalSeconds) >= 1;
    }

    private static void BuildTagEdits(MediaFile file, DateTimeOffset when, CaptureTimeOptions options, TagEdits edits, List<DateFieldWrite> written, List<DateFieldLeft> left, HashSet<string> done)
    {
        Dictionary<string, string> present = new(StringComparer.Ordinal);
        foreach (NativeTag t in file.Metadata.NativeTags)
        {
            if (t.Kind is not (TagValueKind.Text or TagValueKind.Number))
            {
                continue;
            }

            string system = t.System == TagSystems.Id3Chunk ? TagSystems.Id3v2 : t.System;
            present.TryAdd(system + "\0" + (system is TagSystems.Matroska or TagSystems.VorbisComment ? t.Key.ToUpperInvariant() : t.Key) + "\0" + (t.TrackId == 0 ? "" : "t"), (t.Text ?? "").Trim());
        }

        string? Old(string system, string key) => present.TryGetValue(system + "\0" + key + "\0", out string? v) ? v : null;

        void Native(string system, string key, string value, string semantics)
        {
            string? old = Old(system, key);
            edits.SetNative(system, key, value);
            written.Add(new DateFieldWrite(system, key, old, value, semantics));
            done.Add(system + ":" + key);
        }

        void LeaveIfPresent(string system, string key, string reason)
        {
            if (Old(system, key) is { } v)
            {
                left.Add(new DateFieldLeft(system, key, v, reason));
            }
        }

        string iso = DateText.Offset(when, colon: true, zulu: true);
        const string withOffset = "ISO 8601 with the offset";
        const string zoneless = "local wall clock of the stated offset; the field has no zone";

        // ISO BMFF / QuickTime
        // Windows reads the user-data ©day as a UTC clock and mis-applies a trailing offset (observed: "+02:00" moved the shown time by two minutes, "+0200" by
        // two hours the wrong way), so ©day is written as UTC with a Z. The offset stays in the mdta creationdate.
        const string utcZone = "UTC with a Z; Windows mis-reads an offset in this field";
        if (Old(TagSystems.ITunes, "\u00a9day") is not null)
        {
            Native(TagSystems.ITunes, "\u00a9day", DateText.Utc(when), utcZone);
        }

        if (Old(TagSystems.QuickTimeUserData, "\u00a9day") is not null)
        {
            Native(TagSystems.QuickTimeUserData, "\u00a9day", DateText.Utc(when), utcZone);
        }

        const string qt = "com.apple.quicktime.";
        if (Old(TagSystems.QuickTimeKeys, qt + "creationdate") is not null)
        {
            Native(TagSystems.QuickTimeKeys, qt + "creationdate", DateText.Offset(when, colon: false, zulu: false), "ISO 8601 with the offset, as Apple cameras write it");
        }

        foreach (string k in new[] { "creation_time", qt + "creation_time" })
        {
            if (Old(TagSystems.QuickTimeKeys, k) is not null)
            {
                Native(TagSystems.QuickTimeKeys, k, DateText.Utc(when), "UTC");
            }
        }

        // ffmpeg's own key for the date given with -metadata date=; ExifTool lists it as a capture date.
        foreach (string k in new[] { "date", qt + "date" })
        {
            if (Old(TagSystems.QuickTimeKeys, k) is not null)
            {
                Native(TagSystems.QuickTimeKeys, k, iso, withOffset);
            }
        }

        LeaveIfPresent(TagSystems.QuickTimeKeys, "year", "holds a year only");
        LeaveIfPresent(TagSystems.ThreeGpp, "yrrc", "holds a recording year only");

        // Matroska: DateUTC is handled by the writer; Tags are named here.
        foreach (string k in new[] { "DATE_RECORDED", "DATE_DIGITIZED" })
        {
            if (Old(TagSystems.Matroska, k) is not null)
            {
                Native(TagSystems.Matroska, k, iso, withOffset);
            }
        }

        foreach (string k in new[] { "DATE_ENCODED", "DATE_TAGGED", "DATE_RELEASED", "DATE_PURCHASED", "DATE" })
        {
            LeaveIfPresent(TagSystems.Matroska, k, "not the capture date (encoding, tagging, release, purchase or a free date)");
        }

        // ID3: one common-view write handles TDRC, and the v2.3 TYER/TDAT/TIME trio; TDOR is the original release time.
        bool isMp3 = file.Format == MediaFormatId.MpegAudio || file.Format == MediaFormatId.Id3;
        bool id3Date = Old(TagSystems.Id3v2, "TDRC") is not null || Old(TagSystems.Id3v2, "TYER") is not null;
        if (id3Date || (isMp3 && options.AddMissing))
        {
            string local = DateText.Local(when);
            edits.Set(CommonField.Date, local);
            string? old = Old(TagSystems.Id3v2, "TDRC") ?? Old(TagSystems.Id3v2, "TYER");
            written.Add(new DateFieldWrite(TagSystems.Id3v2, "TDRC", old, local, zoneless + " (ID3v2.3 files get TYER, TDAT and TIME)"));
            done.Add("id3v2:TDRC");
            done.Add("id3v2:TYER");
            done.Add("id3v2:TDAT");
            done.Add("id3v2:TIME");
            done.Add("id3v2:TYER+TDAT");
            done.Add("id3v2:TYER+TDAT+TIME");
        }

        if (Old(TagSystems.Id3v2, "TDOR") is not null)
        {
            Native(TagSystems.Id3v2, "TDOR", DateText.Local(when), zoneless);
        }

        foreach (string k in new[] { "TDEN", "TDTG", "TDRL", "TORY" })
        {
            LeaveIfPresent(TagSystems.Id3v2, k, "not the capture date (encoding, tagging, release or original-release year)");
        }

        LeaveIfPresent(TagSystems.Id3v1, "year", "holds a year only");

        // Vorbis comments (Ogg, FLAC)
        bool vorbis = file.Format == MediaFormatId.Ogg || file.Format == MediaFormatId.Flac;
        if (Old(TagSystems.VorbisComment, "DATE") is not null || (vorbis && options.AddMissing))
        {
            Native(TagSystems.VorbisComment, "DATE", DateText.Local(when), zoneless);
        }

        // ffmpeg writes the creation time of Ogg and FLAC files as CREATION_TIME, in UTC; ExifTool and ffprobe list it as the creation time.
        if (Old(TagSystems.VorbisComment, "CREATION_TIME") is not null)
        {
            Native(TagSystems.VorbisComment, "CREATION_TIME", DateText.Utc(when), "UTC");
        }

        // RIFF: INFO and bext
        if (Old(TagSystems.RiffInfo, "ICRD") is not null || (file.Format == MediaFormatId.Wav && options.AddMissing && Old(TagSystems.RiffInfo, "IDIT") is null && Old(TagSystems.Bext, "OriginationDate") is null))
        {
            Native(TagSystems.RiffInfo, "ICRD", DateText.Local(when), zoneless);
        }

        if (Old(TagSystems.RiffInfo, "IDIT") is not null || (file.Format == MediaFormatId.Avi && options.AddMissing))
        {
            Native(TagSystems.RiffInfo, "IDIT", DateText.Ctime(when), "ctime layout, local wall clock of the stated offset; the field has no zone");
        }

        if (Old(TagSystems.Bext, "OriginationDate") is not null || Old(TagSystems.Bext, "OriginationTime") is not null)
        {
            Native(TagSystems.Bext, "OriginationDate", when.DateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "local wall clock of the originator (EBU Tech 3285); no zone");
            Native(TagSystems.Bext, "OriginationTime", when.DateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture), "local wall clock of the originator (EBU Tech 3285); no zone");
        }

        // XMP (ISO BMFF): the properties the capture-time candidates come from.
        foreach (FactCandidate<MediaDateTime> c in file.CaptureTime.Candidates)
        {
            if (!c.Source.StartsWith("xmp:", StringComparison.Ordinal))
            {
                continue;
            }

            // Sources read xmp:<namespace URI>#<property>.
            string uri = c.Source["xmp:".Length..];
            string key = uri switch
            {
                XmpNamespaces.Xmp + "#CreateDate" => "xmp:CreateDate",
                XmpNamespaces.Photoshop + "#DateCreated" => "photoshop:DateCreated",
                XmpNamespaces.Exif + "#DateTimeOriginal" => "exif:DateTimeOriginal",
                _ => "",
            };
            if (key.Length > 0)
            {
                done.Add(c.Source);
            }

            if (key.Length > 0 && done.Add("xmp:" + key))
            {
                edits.SetNative(TagSystems.Xmp, key, iso);
                written.Add(new DateFieldWrite(TagSystems.Xmp, key, c.RawValue, iso, withOffset));
            }
        }

        foreach (NativeTag t in file.Metadata.NativeTags.Where(t => t.System == TagSystems.Asf && t.TrackId == 0 && t.Key is "WM/EncodingTime" or "WM/MediaOriginalBroadcastDateTime"))
        {
            left.Add(new DateFieldLeft(TagSystems.Asf, t.Key, t.Text, "the ASF attribute writer keeps typed attributes as they are; only the File Properties creation date is set"));
        }
    }
}
