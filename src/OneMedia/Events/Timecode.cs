using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMedia.TimedEvents;

/// <summary>Flags of a QuickTime timecode sample description (<c>tmcd</c>).</summary>
[Flags]
public enum TimecodeAttributes : uint
{
    /// <summary>No flags: non-drop-frame counting.</summary>
    None = 0,

    /// <summary>The timecode is drop-frame (SMPTE 12M): frame numbers 0 and 1 (0 to 3 at 60 fps) are skipped at the start of every minute except every tenth.</summary>
    DropFrame = 0x0001,

    /// <summary>The timecode wraps after 24 hours.</summary>
    Wraps24Hours = 0x0002,

    /// <summary>Negative times are allowed.</summary>
    NegativeTimesAllowed = 0x0004,

    /// <summary>The track holds a counter rather than a time of day: samples are plain counts, shown as numbers.</summary>
    Counter = 0x0008,
}

/// <summary>
/// An SMPTE 12M time address: hours, minutes, seconds and frames, with the drop-frame marker. A value, not a time: it names a frame and
/// says nothing about the frame rate beyond whether counting drops frame numbers.
/// </summary>
/// <param name="IsNegative">True for a time before 00:00:00:00 (only some files write one).</param>
/// <param name="Hours">Hours, 0 to 23 when the timecode wraps; larger values are kept as found.</param>
/// <param name="Minutes">Minutes, 0 to 59.</param>
/// <param name="Seconds">Seconds, 0 to 59.</param>
/// <param name="Frames">Frame within the second, 0 up to the nominal frame rate minus one.</param>
/// <param name="IsDropFrame">True for drop-frame counting, shown with a semicolon before the frames.</param>
public readonly record struct Timecode(bool IsNegative, int Hours, int Minutes, int Seconds, int Frames, bool IsDropFrame)
{
    /// <summary>Formats as <c>hh:mm:ss:ff</c>, or <c>hh:mm:ss;ff</c> for drop-frame, with a leading minus when negative.</summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{(IsNegative ? "-" : string.Empty)}{Hours:00}:{Minutes:00}:{Seconds:00}{(IsDropFrame ? ';' : ':')}{Frames:00}");

    /// <summary>Parses <c>hh:mm:ss:ff</c>, <c>hh:mm:ss;ff</c> or <c>hh:mm:ss.ff</c> (a semicolon or dot marks drop-frame), optionally with a leading minus.</summary>
    /// <param name="text">The text.</param>
    /// <param name="timecode">The value when the result is true.</param>
    /// <returns>False when the text does not have four numeric fields.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out Timecode timecode)
    {
        timecode = default;
        bool negative = false;
        if (!text.IsEmpty && text[0] == '-')
        {
            negative = true;
            text = text[1..];
        }

        int[] f = new int[4];
        bool drop = false;
        int field = 0, start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] is not (':' or ';' or '.'))
            {
                continue;
            }

            if (field >= 4 || !int.TryParse(text[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out f[field]))
            {
                return false;
            }

            if (field == 2 && i < text.Length && text[i] is ';' or '.')
            {
                drop = true;
            }

            field++;
            start = i + 1;
        }

        if (field != 4)
        {
            return false;
        }

        timecode = new Timecode(negative, f[0], f[1], f[2], f[3], drop);
        return true;
    }
}

/// <summary>
/// The QuickTime timecode sample description (<c>tmcd</c> sample entry): how a track's frame counters turn into SMPTE time addresses.
/// </summary>
/// <remarks>
/// Layout (QuickTime File Format, timecode media): four reserved bytes, a 32-bit flags field, the timecode timescale, the frame duration
/// in that timescale, the number of frames counted per second, a reserved byte, then optional atoms of which <c>name</c> (the source
/// name, such as a tape name) is the one read here. Observed in files written by other tools: trailing bytes after the fixed fields are common and ignored.
/// </remarks>
public sealed record TimecodeSampleEntry
{
    /// <summary>The flags.</summary>
    public TimecodeAttributes Flags { get; init; }

    /// <summary>Ticks per second of <see cref="FrameDuration"/> (30000 for 29.97 fps material).</summary>
    public uint Timescale { get; init; }

    /// <summary>Length of one frame in <see cref="Timescale"/> ticks (1001 for 29.97 fps).</summary>
    public uint FrameDuration { get; init; }

    /// <summary>The nominal number of frames counted per second (30 for 29.97 fps, 24 for 23.976).</summary>
    public int FramesPerSecond { get; init; }

    /// <summary>The <c>name</c> atom (the source name), or null when absent.</summary>
    public string? SourceName { get; init; }

    /// <summary>True when <see cref="Flags"/> has <see cref="TimecodeAttributes.DropFrame"/>.</summary>
    public bool IsDropFrame => (Flags & TimecodeAttributes.DropFrame) != 0;

    /// <summary>True when the track holds counters instead of times of day.</summary>
    public bool IsCounter => (Flags & TimecodeAttributes.Counter) != 0;

    /// <summary>The exact duration of one frame, or <see cref="MediaTime.Unknown"/> when the entry states none.</summary>
    public MediaTime FrameTime => Timescale == 0 || FrameDuration == 0 ? MediaTime.Unknown : new MediaTime(FrameDuration, Timescale);

    /// <summary>The frame numbers skipped per dropped minute: 2 at a nominal 30 fps, 4 at 60, 0 when the entry is not drop-frame or the rate has no drop-frame form.</summary>
    public int DroppedFramesPerMinute => IsDropFrame && FramesPerSecond is 30 or 60 ? FramesPerSecond / 15 : 0;

    /// <summary>Parses the bytes of the sample entry after the data reference index (what <see cref="MediaTrack.Configuration"/> holds for a <c>tmcd</c> track).</summary>
    /// <param name="data">Reserved(4), flags, timescale, frame duration, frame count, reserved, optional atoms.</param>
    /// <param name="entry">The entry when the result is true.</param>
    /// <returns>False when fewer than 18 bytes are present or the frame count is zero.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out TimecodeSampleEntry? entry)
    {
        entry = null;
        if (data.Length < 18 || data[16] == 0)
        {
            return false;
        }

        string? name = null;
        // Optional atoms: size, type, payload. The name atom's payload is a 16-bit length, a 16-bit language and the text (QuickTime user data text).
        int p = 18;
        while (p + 8 <= data.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(data[p..]);
            if (size < 8 || p + size > data.Length)
            {
                break;
            }

            if (BinaryPrimitives.ReadUInt32BigEndian(data[(p + 4)..]) == 0x6E616D65 && size >= 12)
            {
                int length = BinaryPrimitives.ReadUInt16BigEndian(data[(p + 8)..]);
                int available = (int)size - 12;
                name = Encoding.UTF8.GetString(data.Slice(p + 12, Math.Min(length, available)));
            }

            p += (int)size;
        }

        entry = new TimecodeSampleEntry
        {
            Flags = (TimecodeAttributes)BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
            Timescale = BinaryPrimitives.ReadUInt32BigEndian(data[8..]),
            FrameDuration = BinaryPrimitives.ReadUInt32BigEndian(data[12..]),
            FramesPerSecond = data[16],
            SourceName = name,
        };
        return true;
    }

    /// <summary>Turns a frame number into the time address it names, counting as SMPTE 12M does (drop-frame included).</summary>
    /// <param name="frameNumber">The 32-bit counter a timecode sample holds: frames since 00:00:00:00.</param>
    /// <returns>The address. Negative frame numbers give a negative address. Hours wrap at 24 when <see cref="TimecodeAttributes.Wraps24Hours"/> is set.</returns>
    public Timecode FromFrameNumber(long frameNumber)
    {
        long n = frameNumber < 0 ? -frameNumber : frameNumber;
        int fps = Math.Max(FramesPerSecond, 1);
        int drop = DroppedFramesPerMinute;
        if (drop > 0)
        {
            // SMPTE 12M drop-frame: every minute but each tenth skips `drop` frame numbers, so ten minutes hold fps*600 - 9*drop frames.
            long perMinute = (fps * 60L) - drop;
            long perTenMinutes = (fps * 600L) - (9L * drop);
            long tens = n / perTenMinutes;
            long rest = n % perTenMinutes;
            n += 9L * drop * tens;
            if (rest >= drop)
            {
                n += drop * ((rest - drop) / perMinute);
            }
        }

        long hours = n / (fps * 3600L);
        if ((Flags & TimecodeAttributes.Wraps24Hours) != 0)
        {
            hours %= 24;
        }

        return new Timecode(
            frameNumber < 0,
            (int)Math.Min(hours, int.MaxValue),
            (int)((n / (fps * 60L)) % 60),
            (int)((n / fps) % 60),
            (int)(n % fps),
            IsDropFrame);
    }

    /// <summary>The inverse of <see cref="FromFrameNumber"/>: the frame number of a time address.</summary>
    /// <param name="timecode">The address.</param>
    /// <returns>Frames since 00:00:00:00, negative for a negative address.</returns>
    public long ToFrameNumber(Timecode timecode)
    {
        long fps = Math.Max(FramesPerSecond, 1);
        long total = ((((timecode.Hours * 60L) + timecode.Minutes) * 60) + timecode.Seconds) * fps + timecode.Frames;
        int drop = DroppedFramesPerMinute;
        if (drop > 0)
        {
            long minutes = (timecode.Hours * 60L) + timecode.Minutes;
            total -= drop * (minutes - (minutes / 10));
        }

        return timecode.IsNegative ? -total : total;
    }
}
