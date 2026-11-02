using System.Buffers.Binary;
using System.Globalization;
using System.Linq;
using RecursiveAI.OneMedia.Exif;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>One capture-date field a capture-time write set.</summary>
/// <param name="System">The place: a tag system (<see cref="TagSystems"/>) or a header (<c>mvhd</c>, <c>tkhd</c>, <c>mdhd</c>, <c>mkv-info</c>, <c>asf</c>, <c>exif</c>).</param>
/// <param name="Field">The field's own name (<c>creation_time</c>, <c>©day</c>, <c>DateUTC</c>, <c>ExifIFD.DateTimeOriginal</c>).</param>
/// <param name="OldValue">What the file held, as text, or null when the field was created.</param>
/// <param name="NewValue">What was written, as text, in the field's own notation.</param>
/// <param name="Semantics">How the field reads the time: UTC, local wall clock of the stated offset, or with an offset.</param>
public sealed record DateFieldWrite(string System, string Field, string? OldValue, string NewValue, string Semantics);

/// <summary>A capture-date field a capture-time write did not change, with the reason.</summary>
/// <param name="System">The place, as in <see cref="DateFieldWrite"/>.</param>
/// <param name="Field">The field's name.</param>
/// <param name="Value">What the file holds, as text.</param>
/// <param name="Reason">Why it was left.</param>
public sealed record DateFieldLeft(string System, string Field, string? Value, string Reason);

/// <summary>The capture time and how the binary header fields are to read it; the writers of the families that have such fields fill the logs.</summary>
internal sealed class CaptureDateEdit
{
    public CaptureDateEdit(DateTimeOffset when, bool headerTimesLocal)
    {
        When = when;
        HeaderTimesLocal = headerTimesLocal;
    }

    public DateTimeOffset When { get; }

    /// <summary>True when the binary header times hold the wall clock of <see cref="When"/>'s offset instead of UTC.</summary>
    public bool HeaderTimesLocal { get; }

    /// <summary>The instant as the header fields store it: UTC, or the local wall clock written as if it were UTC.</summary>
    public DateTime HeaderClock => HeaderTimesLocal ? When.DateTime : When.UtcDateTime;

    public string HeaderSemantics => HeaderTimesLocal ? "local wall clock of the stated offset, stored as if it were UTC" : "UTC";

    public List<DateFieldWrite> Written { get; } = [];

    public List<DateFieldLeft> Left { get; } = [];
}

/// <summary>Formats a capture time in the notations the date fields use.</summary>
internal static class DateText
{
    public static string Offset(DateTimeOffset t, bool colon, bool zulu)
    {
        string wall = t.DateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (zulu && t.Offset == TimeSpan.Zero)
        {
            return wall + "Z";
        }

        int m = (int)t.Offset.TotalMinutes;
        string sign = m < 0 ? "-" : "+";
        m = Math.Abs(m);
        return wall + sign + (m / 60).ToString("00", CultureInfo.InvariantCulture) + (colon ? ":" : "") + (m % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    public static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string Local(DateTimeOffset t) => t.DateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    // The C library's ctime layout, as AVI IDIT chunks carry it: "Wed May  1 12:00:00 2024" (day of the month padded with a space to two characters).
    public static string Ctime(DateTimeOffset t) =>
        t.DateTime.ToString("ddd MMM ", CultureInfo.InvariantCulture) + t.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2) + t.DateTime.ToString(" HH:mm:ss yyyy", CultureInfo.InvariantCulture);

    public static string Exif(DateTime wall) => wall.ToString("yyyy':'MM':'dd HH':'mm':'ss", CultureInfo.InvariantCulture);

    public static string ExifOffset(DateTimeOffset t)
    {
        int m = (int)t.Offset.TotalMinutes;
        return (m < 0 ? "-" : "+") + (Math.Abs(m) / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (Math.Abs(m) % 60).ToString("00", CultureInfo.InvariantCulture);
    }
}

/// <summary>Plans the same-size patches of binary date fields inside the format writers' own edit sessions.</summary>
internal static class CaptureDatePatches
{
    // ISO/IEC 14496-12 §8.2.2: times are seconds since 1904-01-01 00:00 UTC (a QuickTime File Format convention carried into the ISO file format).
    private static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MatroskaEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static string MacText(ulong seconds) => seconds > (ulong)(DateTime.MaxValue - MacEpoch).TotalSeconds ? "(out of range) " + seconds.ToString(CultureInfo.InvariantCulture) : MacEpoch.AddSeconds(seconds).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    public static void Iso(IMediaSource src, BoxNode moov, CaptureDateEdit d, BoxEditor editor, MediaLimits limits)
    {
        if (moov.Find(BoxTypes.Mvhd) is { } mvhd)
        {
            Header(src, mvhd, "mvhd", "", true, d, editor, limits);
        }

        int index = 0;
        foreach (BoxNode trak in moov.Children.Where(c => c.Type == BoxTypes.Trak))
        {
            index++;
            if (trak.Find(BoxTypes.Tkhd) is { } tkhd)
            {
                Header(src, tkhd, "tkhd", $" (track {index})", false, d, editor, limits);
            }

            if (trak.Find(BoxTypes.Mdia)?.Find(BoxTypes.Mdhd) is { } mdhd)
            {
                Header(src, mdhd, "mdhd", $" (track {index})", false, d, editor, limits);
            }
        }

        if (moov.Find(BoxTypes.Udta) is { } udta)
        {
            foreach (BoxNode atom in udta.Children.Where(c => c.TypeName is "QVMI" or "MVTG" or "RMKN" or "SIGM"))
            {
                ExifAtom(src, atom, d, editor, limits);
            }
        }
    }

    // A movie, track or media header: full box, then creation and modification times of 4 bytes (version 0) or 8 (version 1).
    private static void Header(IMediaSource src, BoxNode box, string name, string suffix, bool always, CaptureDateEdit d, BoxEditor editor, MediaLimits limits)
    {
        long total = box.HeaderSize + box.PayloadSize;
        if (box.PayloadSize < 12 || total > 4096)
        {
            d.Left.Add(new DateFieldLeft(name, "creation_time" + suffix, null, "the box is too short to hold its times"));
            return;
        }

        limits.CheckAllocation(total, box.Offset);
        byte[] b = new byte[total];
        src.ReadExactly(box.Offset, b);
        int at = box.HeaderSize;
        int version = b[at];
        int width = version == 1 ? 8 : 4;
        if (box.PayloadSize < 4 + (2 * width))
        {
            d.Left.Add(new DateFieldLeft(name, "creation_time" + suffix, null, "the box is too short to hold its times"));
            return;
        }

        ulong oldCreation = Read(b, at + 4, width);
        ulong oldModification = Read(b, at + 4 + width, width);
        if (oldCreation == 0 && !always)
        {
            // A zero is how writers say "not set"; it is not a date to correct.
            d.Left.Add(new DateFieldLeft(name, "creation_time" + suffix, "0", "unset (0); left unset"));
            return;
        }

        double secs = (d.HeaderClock - MacEpoch).TotalSeconds;
        ulong value = (ulong)Math.Floor(secs);
        if (version == 0 && value > uint.MaxValue)
        {
            d.Left.Add(new DateFieldLeft(name, "creation_time" + suffix, MacText(oldCreation), "the version 0 box holds 32-bit seconds (to the year 2040) and the time is later"));
            return;
        }

        byte[] next = (byte[])b.Clone();
        Write(next, at + 4, width, value);
        Write(next, at + 4 + width, width, value);
        if (!next.AsSpan().SequenceEqual(b))
        {
            editor.Replace(box, next);
        }

        d.Written.Add(new DateFieldWrite(name, "creation_time" + suffix, MacText(oldCreation), MacText(value), d.HeaderSemantics));
        d.Written.Add(new DateFieldWrite(name, "modification_time" + suffix, MacText(oldModification), MacText(value), d.HeaderSemantics));
    }

    private static ulong Read(byte[] b, int at, int width) => width == 8 ? BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));

    private static void Write(byte[] b, int at, int width, ulong v)
    {
        if (width == 8)
        {
            BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(at), v);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), (uint)v);
        }
    }

    // Exif 2.32 §4.6.5: DateTimeOriginal / DateTimeDigitized are 20 ASCII bytes "YYYY:MM:DD HH:MM:SS" and a NUL; OffsetTimeOriginal / OffsetTimeDigitized are
    // 7 bytes "+HH:MM" and a NUL; SubSecTime* hold digits. All are rewritten in place at their own length, so the block's offsets do not move.
    private static void ExifAtom(IMediaSource src, BoxNode atom, CaptureDateEdit d, BoxEditor editor, MediaLimits limits)
    {
        if (atom.PayloadSize > limits.MaxTagBytes)
        {
            return;
        }

        long total = atom.HeaderSize + atom.PayloadSize;
        limits.CheckAllocation(total, atom.Offset);
        byte[] box = new byte[total];
        src.ReadExactly(atom.Offset, box);
        long payloadAt = atom.Header.PayloadOffset;
        DefectLog scratch = new();
        if (!ExifBlock.TryParse(box.AsMemory(atom.HeaderSize), payloadAt, limits, scratch, out ExifBlock block))
        {
            return;
        }

        bool changed = false;
        string label = "exif:" + atom.TypeName;
        foreach ((ushort tag, ushort offsetTag, ushort subSecTag, string name) in new[] { ((ushort)0x9003, (ushort)0x9011, (ushort)0x9291, "DateTimeOriginal"), ((ushort)0x9004, (ushort)0x9012, (ushort)0x9292, "DateTimeDigitized") })
        {
            if (block.Find("ExifIFD", tag) is not { Type: ExifType.Ascii, Count: 20 } e)
            {
                continue;
            }

            string old = e.AsText() ?? "";
            string wall = DateText.Exif(d.When.DateTime);
            Put(box, e.ValueRange, wall);
            changed = true;
            string newText = wall;
            if (block.Find("ExifIFD", offsetTag) is { Type: ExifType.Ascii, Count: 7 } oe)
            {
                string oldOffset = oe.AsText() ?? "";
                string off = DateText.ExifOffset(d.When);
                Put(box, oe.ValueRange, off);
                old += oldOffset;
                newText += off;
            }
            else
            {
                d.Left.Add(new DateFieldLeft(label, "ExifIFD.OffsetTime" + name["DateTime".Length..], null, "the block has no offset tag and has no room for one; readers see the wall clock without a zone"));
            }

            if (block.Find("ExifIFD", subSecTag) is { Type: ExifType.Ascii } se && se.ValueRange.Length > 0)
            {
                // The old fraction of a second belonged to the old time.
                string zeros = new('0', (int)Math.Max(0, se.ValueRange.Length - 1));
                Put(box, se.ValueRange, zeros);
            }

            d.Written.Add(new DateFieldWrite(label, "ExifIFD." + name, old.TrimEnd('\0'), newText, "local wall clock of the camera, with its offset tag when the block has one"));
        }

        if (block.Find("IFD0", 0x0132) is { } modify)
        {
            d.Left.Add(new DateFieldLeft(label, "IFD0.DateTime", modify.AsText(), "the modification time of the image, not the capture time"));
        }

        if (changed)
        {
            editor.Replace(atom, box);
        }

        void Put(byte[] bytes, ByteRange range, string text)
        {
            int at2 = checked((int)(range.Offset - atom.Offset));
            int len = (int)range.Length;
            Array.Clear(bytes, at2, len);
            System.Text.Encoding.ASCII.GetBytes(text, 0, Math.Min(text.Length, len - 1), bytes, at2);
        }
    }

    /// <summary>Sets or adds the Matroska Info DateUTC: 64-bit nanoseconds since 2001-01-01 UTC (RFC 9559 §5.1.2.9).</summary>
    public static void MatroskaInfo(IMediaSource src, EbmlElement seg, CaptureDateEdit d, EbmlEditor editor, bool add)
    {
        EbmlElement? info = seg.ChildElements.FirstOrDefault(c => c.Id == MatroskaIds.Info);
        if (info is null)
        {
            d.Left.Add(new DateFieldLeft("mkv-info", "DateUTC", null, "the Segment has no Info element"));
            return;
        }

        EbmlElement? existing = info.ChildElements.FirstOrDefault(c => c.Id == MatroskaIds.DateUtc);
        long ns = (d.When.UtcDateTime - MatroskaEpoch).Ticks * 100;
        byte[] element = new byte[3 + 8];
        element[0] = 0x44;
        element[1] = 0x61;
        element[2] = 0x88;
        BinaryPrimitives.WriteInt64BigEndian(element.AsSpan(3), ns);
        string text = Utc(d.When.UtcDateTime);
        if (existing is not null)
        {
            string old = "";
            Span<byte> p = stackalloc byte[8]; // a DateUTC payload is at most 8 bytes
            if (existing.PayloadSize is > 0 and <= 8)
            {
                int n = existing.ReadPayload(p);
                long v = 0;
                for (int i = 0; i < n; i++)
                {
                    v = (v << 8) | p[i];
                }

                if (n < 8 && (p[0] & 0x80) != 0)
                {
                    v -= 1L << (8 * n);
                }

                old = Utc(MatroskaEpoch.AddTicks(v / 100));
            }

            editor.Replace(existing, element);
            d.Written.Add(new DateFieldWrite("mkv-info", "DateUTC", old, text, "UTC"));
        }
        else if (add)
        {
            editor.AppendChild(info, element);
            d.Written.Add(new DateFieldWrite("mkv-info", "DateUTC", null, text, "UTC"));
        }
    }

    private static string Utc(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The ASF File Properties creation date: a FILETIME, 100 ns ticks since 1601-01-01 UTC, at byte 48 of the object (24-byte header, file id, file size).</summary>
    public static byte[] AsfCreation(byte[] fileProperties, CaptureDateEdit d)
    {
        long old = BinaryPrimitives.ReadInt64LittleEndian(fileProperties.AsSpan(48));
        long ticks = d.When.UtcDateTime.ToFileTimeUtc();
        BinaryPrimitives.WriteInt64LittleEndian(fileProperties.AsSpan(48), ticks);
        string oldText = old > 0 && old < DateTime.MaxValue.Ticks - DateTime.FromFileTimeUtc(0).Ticks ? Utc(DateTime.FromFileTimeUtc(old)) : old.ToString(CultureInfo.InvariantCulture);
        d.Written.Add(new DateFieldWrite("asf", "FileProperties.CreationDate", oldText, Utc(d.When.UtcDateTime), "UTC"));
        return fileProperties;
    }
}
