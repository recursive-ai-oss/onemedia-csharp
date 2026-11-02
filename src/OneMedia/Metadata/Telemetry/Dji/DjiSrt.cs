using System.Globalization;

namespace RecursiveAI.OneMedia.Metadata.Telemetry.Dji;

/// <summary>
/// Reads the telemetry in a DJI <c>.SRT</c> sidecar: SubRip blocks (index, <c>HH:MM:SS,mmm --&gt; HH:MM:SS,mmm</c>, text) whose text is the
/// per-frame telemetry that DJI also embeds in a subtitle track. The result is the same set of channels as for the embedded track, with sample
/// times taken from the cue starts. The parser is tolerant: a block without a time line is skipped with a note on the first channel.
/// </summary>
public static class DjiSrt
{
    /// <summary>Largest sidecar read: 256 MiB (an hour of 60 fps telemetry is about 40 MiB).</summary>
    public const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>Decodes the telemetry of a sidecar held in memory.</summary>
    /// <param name="srt">The file's bytes (UTF-8, with or without a byte order mark).</param>
    /// <returns>The channels (GPS, altitude, camera settings, attitude, clock); empty when no block holds DJI telemetry.</returns>
    public static IReadOnlyList<TelemetryChannel> Read(ReadOnlySpan<byte> srt)
    {
        TelemetryCollector collector = new(new DefectLog(), -1);
        DjiTextDecoder decoder = new(collector);
        List<List<TelemetrySample>> lists = [];
        int pos = srt.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        while (pos < srt.Length)
        {
            // A block runs to the next blank line.
            int blockStart = pos;
            int end = FindBlockEnd(srt, pos, out int next);
            ReadOnlySpan<byte> block = srt[blockStart..end];
            pos = next;
            if (!TryTimes(block, out int textAt, out MediaTime start, out MediaTime duration))
            {
                continue;
            }

            ReadOnlySpan<byte> text = block[textAt..];
            if (!DjiTextDecoder.Looks(text))
            {
                continue;
            }

            collector.Clear();
            decoder.Decode(text, -1, start, duration);
            for (int i = 0; i < collector.Count; i++)
            {
                ref TelemetryItem item = ref collector.Items[i];
                // The sidecar is the source: the range is the cue text inside it.
                item.Range = new ByteRange(blockStart + textAt, text.Length);
                while (lists.Count <= item.ChannelIndex)
                {
                    lists.Add([]);
                }

                lists[item.ChannelIndex].Add(TelemetryChannels.ToSample(null, item, collector.Channels[item.ChannelIndex]));
            }
        }

        List<TelemetryChannel> result = [];
        for (int i = 0; i < lists.Count; i++)
        {
            if (lists[i].Count > 0)
            {
                TelemetryChannelInfo info = collector.Channels[i];
                result.Add(TelemetryChannels.Build(-1, info, info.Kind, lists[i]));
            }
        }

        return result;
    }

    /// <summary>Decodes the telemetry of a sidecar file.</summary>
    /// <param name="path">The <c>.SRT</c> path.</param>
    /// <returns>The channels, as <see cref="Read(ReadOnlySpan{byte})"/>.</returns>
    /// <exception cref="InvalidDataException">The file is larger than <see cref="MaxBytes"/>.</exception>
    public static IReadOnlyList<TelemetryChannel> Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FileInfo info = new(path);
        if (info.Length > MaxBytes)
        {
            throw new InvalidDataException($"A DJI sidecar larger than {MaxBytes} bytes is not read.");
        }

        return Read(File.ReadAllBytes(path));
    }

    private static int FindBlockEnd(ReadOnlySpan<byte> s, int from, out int next)
    {
        int i = from;
        while (i < s.Length)
        {
            int nl = s[i..].IndexOf((byte)'\n');
            if (nl < 0)
            {
                next = s.Length;
                return s.Length;
            }

            int lineEnd = i + nl;
            int contentEnd = lineEnd > i && s[lineEnd - 1] == (byte)'\r' ? lineEnd - 1 : lineEnd;
            if (contentEnd == i && i > from)
            {
                next = lineEnd + 1;
                return i;
            }

            if (contentEnd == i && i == from)
            {
                // Leading blank lines belong to no block.
                from = i = lineEnd + 1;
                continue;
            }

            i = lineEnd + 1;
        }

        next = s.Length;
        return s.Length;
    }

    // Finds "HH:MM:SS,mmm --> HH:MM:SS,mmm" in the first three lines of a block; textAt is where the lines after it begin.
    private static bool TryTimes(ReadOnlySpan<byte> block, out int textAt, out MediaTime start, out MediaTime duration)
    {
        textAt = 0;
        start = MediaTime.Unknown;
        duration = MediaTime.Unknown;
        int arrow = block.IndexOf("-->"u8);
        if (arrow < 0 || arrow > 64)
        {
            return false;
        }

        int lineStart = block[..arrow].LastIndexOf((byte)'\n') + 1;
        int lineEnd = arrow + block[arrow..].IndexOf((byte)'\n');
        if (lineEnd < arrow)
        {
            return false;
        }

        if (!TryTime(block[lineStart..arrow], out long a) || !TryTime(block[(arrow + 3)..lineEnd], out long b))
        {
            return false;
        }

        start = new MediaTime(a, 1000);
        duration = b >= a ? new MediaTime(b - a, 1000) : MediaTime.Unknown;
        textAt = lineEnd + 1;
        return true;
    }

    // HH:MM:SS,mmm (a dot is accepted in place of the comma).
    private static bool TryTime(ReadOnlySpan<byte> s, out long ms)
    {
        ms = 0;
        string t = System.Text.Encoding.ASCII.GetString(s).Trim();
        string[] parts = t.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        string[] sec = parts[2].Replace('.', ',').Split(',');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int h) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int m)
            || !int.TryParse(sec[0], NumberStyles.None, CultureInfo.InvariantCulture, out int sc) || m > 59 || sc > 60)
        {
            return false;
        }

        int milli = 0;
        if (sec.Length > 1 && !int.TryParse(sec[1].PadRight(3, '0')[..3], NumberStyles.None, CultureInfo.InvariantCulture, out milli))
        {
            return false;
        }

        ms = ((((long)h * 60) + m) * 60 + sc) * 1000 + milli;
        return true;
    }
}
