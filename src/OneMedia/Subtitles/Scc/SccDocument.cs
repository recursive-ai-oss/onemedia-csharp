using System.Globalization;
using System.Linq;
using System.Text;

namespace RecursiveAI.OneMedia.Subtitles.Scc;

/// <summary>One timecoded line of an SCC file: where it starts and the CEA-608 byte pairs it carries.</summary>
/// <param name="Frame">The line's first frame as a count of frames since 00:00:00:00 at 29.97 fps (drop-frame numbering resolved), i.e. real time is <c>Frame × 1001 / 30000</c> seconds.</param>
/// <param name="DropFrame">True when the timecode was written with a semicolon before the frame count.</param>
/// <param name="Bytes">The pairs as stored (parity bits included), two bytes each.</param>
public sealed record SccLine(long Frame, bool DropFrame, byte[] Bytes)
{
    /// <summary>The number of byte pairs.</summary>
    public int PairCount => Bytes.Length / 2;
}

/// <summary>
/// A Scenarist Closed Caption file: a <c>Scenarist_SCC V1.0</c> header and timecoded lines of hexadecimal CEA-608 byte pairs. Each pair occupies
/// one frame at 29.97 fps starting at its line's timecode. Drop-frame (<c>;</c>) and non-drop (<c>:</c>) timecodes are both read and kept.
/// </summary>
public sealed class SccDocument
{
    /// <summary>The header line.</summary>
    public const string Header = "Scenarist_SCC V1.0";

    /// <summary>Creates a document.</summary>
    /// <param name="lines">The lines in file order.</param>
    public SccDocument(IReadOnlyList<SccLine> lines) => Lines = lines;

    /// <summary>The timecoded lines.</summary>
    public IReadOnlyList<SccLine> Lines { get; }

    /// <summary>Converts a frame count to media time (30000/1001 frames per second).</summary>
    /// <param name="frame">Frames since zero.</param>
    /// <returns>The exact time.</returns>
    public static MediaTime FrameToTime(long frame) => new(frame * 1001, 30000);

    /// <summary>Converts a time to the nearest frame at 29.97 fps (ties away from zero).</summary>
    /// <param name="time">A known time.</param>
    /// <returns>The frame count.</returns>
    public static long TimeToFrame(MediaTime time)
    {
        long t = time.Rescale(30000, MediaRounding.NearestAwayFromZero).Ticks;
        return t <= 0 ? 0 : (t + 500) / 1001;
    }

    /// <summary>Parses SCC text.</summary>
    /// <param name="text">The file contents. Line ends may be LF, CRLF or CR; the header is optional on read.</param>
    /// <param name="document">The document on success.</param>
    /// <returns>False when a timecode or a byte pair is malformed.</returns>
    public static bool TryParse(string text, out SccDocument? document)
    {
        ArgumentNullException.ThrowIfNull(text);
        document = null;
        var lines = new List<SccLine>();
        foreach (string raw in text.Split(['\n', '\r']))
        {
            string line = raw.Trim().Trim('\uFEFF');
            if (line.Length == 0 || line.StartsWith("Scenarist_SCC", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int split = line.IndexOfAny(['\t', ' ']);
            if (split < 0 || !TryTimecode(line.AsSpan(0, split), out long frame, out bool drop))
            {
                return false;
            }

            var bytes = new List<byte>();
            foreach (string word in line[split..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length != 4 || !ushort.TryParse(word, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort v))
                {
                    return false;
                }

                bytes.Add((byte)(v >> 8));
                bytes.Add((byte)v);
            }

            lines.Add(new SccLine(frame, drop, [.. bytes]));
        }

        document = new SccDocument(lines);
        return true;
    }

    /// <summary>Parses SCC text.</summary>
    /// <param name="text">The file contents.</param>
    /// <returns>The document.</returns>
    /// <exception cref="FormatException">A timecode or byte pair is malformed.</exception>
    public static SccDocument Parse(string text) =>
        TryParse(text, out SccDocument? d) ? d! : throw new FormatException("Malformed SCC timecode or byte pair.");

    private static bool TryTimecode(ReadOnlySpan<char> s, out long frame, out bool drop)
    {
        frame = 0;
        drop = false;
        if (s.Length != 11 || s[2] != ':' || s[5] != ':' || (s[8] != ':' && s[8] != ';'))
        {
            return false;
        }

        drop = s[8] == ';';
        if (!int.TryParse(s[..2], NumberStyles.None, CultureInfo.InvariantCulture, out int h)
            || !int.TryParse(s.Slice(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int m)
            || !int.TryParse(s.Slice(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int sec)
            || !int.TryParse(s.Slice(9, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int f)
            || m > 59 || sec > 59 || f > 29)
        {
            return false;
        }

        long total = (((h * 60L) + m) * 60 + sec) * 30 + f;
        if (drop)
        {
            // SMPTE 12M drop-frame: frame numbers 0 and 1 are skipped at the start of every minute except every tenth.
            long minutes = (h * 60L) + m;
            total -= 2 * (minutes - (minutes / 10));
        }

        frame = total;
        return true;
    }

    private static string Timecode(long frame, bool drop)
    {
        long n = frame;
        if (drop)
        {
            // Inverse of the numbering above: 17982 frames per ten minutes, 1798 in each of the nine dropping minutes.
            long d = n / 17982;
            long r = n % 17982;
            n += (18 * d) + (r < 2 ? 0 : 2 * ((r - 2) / 1798));
        }

        long f = n % 30;
        long s = n / 30 % 60;
        long m = n / 1800 % 60;
        long h = n / 108000;
        return string.Create(CultureInfo.InvariantCulture, $"{h:D2}:{m:D2}:{s:D2}{(drop ? ';' : ':')}{f:D2}");
    }

    /// <summary>
    /// Writes the canonical form: the header, a blank line, then each line as its timecode, a tab and the pairs as four lowercase hex digits
    /// separated by spaces, each followed by a blank line. Line ends are LF.
    /// </summary>
    /// <returns>The file text.</returns>
    public string Write()
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append("\n\n");
        foreach (SccLine l in Lines)
        {
            sb.Append(Timecode(l.Frame, l.DropFrame)).Append('\t');
            for (int i = 0; i + 1 < l.Bytes.Length; i += 2)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(l.Bytes[i].ToString("x2", CultureInfo.InvariantCulture)).Append(l.Bytes[i + 1].ToString("x2", CultureInfo.InvariantCulture));
            }

            sb.Append("\n\n");
        }

        return sb.ToString();
    }

    /// <summary>Lists every byte pair with the time it is sent (its line's timecode plus one frame per earlier pair).</summary>
    /// <param name="offset">Added to every time, for media that does not start at zero.</param>
    /// <returns>The pairs in file order.</returns>
    public IEnumerable<(MediaTime Time, byte B1, byte B2)> Pairs(MediaTime offset = default)
    {
        foreach (SccLine l in Lines)
        {
            for (int i = 0; i < l.PairCount; i++)
            {
                yield return (FrameToTime(l.Frame + i) + offset, l.Bytes[2 * i], l.Bytes[(2 * i) + 1]);
            }
        }
    }

    /// <summary>Runs the pairs through the CEA-608 decoder as field 1 data, one pair per frame from each line's timecode.</summary>
    /// <returns>The decoded caption screens (channel CC1 and CC2 as addressed in the data).</returns>
    public IReadOnlyList<Cea608.Cea608Caption> Decode()
    {
        var dec = new Cea608.Cea608Decoder();
        long last = 0;
        foreach (SccLine l in Lines)
        {
            for (int i = 0; i < l.PairCount; i++)
            {
                long fr = l.Frame + i;
                dec.Push(FrameToTime(fr), 0, l.Bytes[2 * i], l.Bytes[(2 * i) + 1]);
                last = Math.Max(last, fr + 1);
            }
        }

        dec.Finish(FrameToTime(last));
        return dec.Captions;
    }

    /// <summary>
    /// Builds a document from caption screens: each caption is loaded as pop-on pairs timed so that the end-of-caption pair lands on the
    /// caption's start frame, and erased at its end frame. A pair never shares a frame with another: when two would collide, the load of the
    /// later caption moves earlier.
    /// </summary>
    /// <param name="captions">Caption screens (CC1/CC2 are written; other channels are skipped).</param>
    /// <param name="dropFrame">Write drop-frame timecodes (the usual choice at 29.97 fps).</param>
    /// <returns>The document.</returns>
    public static SccDocument FromCaptions(IEnumerable<Cea608.Cea608Caption> captions, bool dropFrame = true)
    {
        ArgumentNullException.ThrowIfNull(captions);
        var slots = new SortedDictionary<long, (byte, byte)>();
        long ceiling = long.MaxValue; // loads must end before the next used slot
        var ordered = captions.Where(c => c.Channel is Cea608.Cea608Channel.CC1 or Cea608.Cea608Channel.CC2).OrderByDescending(c => c.Start).ToList();
        foreach (Cea608.Cea608Caption c in ordered)
        {
            IReadOnlyList<(byte, byte)> load = Cea608.Cea608Encoder.PopOn(c.Rows, c.Channel);
            long show = Math.Min(TimeToFrame(c.Start), ceiling - 1);
            long hide = Math.Max(TimeToFrame(c.End), show + 1);
            // Erase pair (sent twice) at the end frame, unless the next caption's load needs the frames.
            IReadOnlyList<(byte, byte)> erase = Cea608.Cea608Encoder.Erase(c.Channel);
            long e = Math.Min(hide, ceiling - 2);
            if (e > show + 1)
            {
                slots[e] = erase[0];
                slots[e + 1] = erase[1];
            }

            // Load pairs end with the EOC pair at `show` (the last two pairs are the doubled EOC).
            long first = show - (load.Count - 2);
            for (int i = 0; i < load.Count; i++)
            {
                slots[first + i] = load[i];
            }

            ceiling = first;
        }

        var lines = new List<SccLine>();
        long start = -1;
        long prev = -2;
        var buf = new List<byte>();
        foreach (KeyValuePair<long, (byte, byte)> kv in slots)
        {
            if (kv.Key != prev + 1 && buf.Count > 0)
            {
                lines.Add(new SccLine(start, dropFrame, [.. buf]));
                buf.Clear();
            }

            if (buf.Count == 0)
            {
                start = kv.Key;
            }

            buf.Add(kv.Value.Item1);
            buf.Add(kv.Value.Item2);
            prev = kv.Key;
        }

        if (buf.Count > 0)
        {
            lines.Add(new SccLine(start, dropFrame, [.. buf]));
        }

        return new SccDocument(lines);
    }
}
