using System.Globalization;
using System.Text;

namespace RecursiveAI.OneMedia.Captions;

// The VobSub sidecar pair: a text index (.idx) with the display size, the 16-colour palette and one time stamp / file position line per
// sub-picture, and an MPEG program stream (.sub) holding the sub-picture units in private stream 1 packets. The index layout is taken from
// the public prose description of the format and from indexes written by mkvextract.

/// <summary>One line of a VobSub index: where a sub-picture starts.</summary>
/// <param name="Time">Presentation time (the index states milliseconds).</param>
/// <param name="FilePosition">Byte position of the first packet of the unit in the <c>.sub</c> file.</param>
public readonly record struct VobSubEntry(MediaTime Time, long FilePosition);

/// <summary>A language stream listed in a VobSub index.</summary>
/// <param name="Language">The <c>id:</c> language code (for example <c>en</c>), or null when it is blank.</param>
/// <param name="Index">The <c>index:</c> value; the sub-stream id is <c>0x20 + Index</c>.</param>
/// <param name="Entries">The sub-pictures of the stream in file order.</param>
public sealed record VobSubLanguage(string? Language, int Index, IReadOnlyList<VobSubEntry> Entries);

/// <summary>A parsed VobSub <c>.idx</c> file (also the CodecPrivate text of a Matroska <c>S_VOBSUB</c> track, which has no entries).</summary>
public sealed class VobSubIndex
{
    internal VobSubIndex(int width, int height, SubtitleColor[] palette, bool forcedOnly, long originX, long originY, IReadOnlyList<VobSubLanguage> streams)
    {
        Width = width;
        Height = height;
        Palette = palette;
        ForcedOnly = forcedOnly;
        OriginX = originX;
        OriginY = originY;
        Streams = streams;
    }

    /// <summary>Display width from the <c>size:</c> line; 0 when absent.</summary>
    public int Width { get; }

    /// <summary>Display height from the <c>size:</c> line; 0 when absent.</summary>
    public int Height { get; }

    /// <summary>The 16-colour palette from the <c>palette:</c> line (RGB, opaque); empty when absent.</summary>
    public IReadOnlyList<SubtitleColor> Palette { get; }

    /// <summary>True when the <c>forced subs:</c> line says ON (the author meant only forced pictures to show).</summary>
    public bool ForcedOnly { get; }

    /// <summary>Horizontal origin from the <c>org:</c> line. The sub-picture positions already include it; it is reported, not applied.</summary>
    public long OriginX { get; }

    /// <summary>Vertical origin from the <c>org:</c> line.</summary>
    public long OriginY { get; }

    /// <summary>The language streams with their entries.</summary>
    public IReadOnlyList<VobSubLanguage> Streams { get; }

    /// <summary>Parses an index. Unknown lines and comments are skipped; malformed lines are recorded as defects.</summary>
    /// <param name="idx">The file bytes (read as Latin-1: the format is ASCII apart from comments).</param>
    /// <param name="defects">Receives deviations; null to discard.</param>
    /// <param name="limits">Resource limits; null for the defaults. The text size is checked against <see cref="MediaLimits.MaxAllocationBytes"/> and the entry count against <see cref="MediaLimits.MaxSampleTableEntries"/>.</param>
    /// <exception cref="MediaLimitExceededException">A limit was exceeded.</exception>
    public static VobSubIndex Parse(ReadOnlySpan<byte> idx, DefectLog? defects = null, MediaLimits? limits = null)
    {
        limits ??= MediaLimits.Default;
        defects ??= new DefectLog(limits);
        limits.CheckAllocation(idx.Length, 0);
        string text = Encoding.Latin1.GetString(idx);
        int width = 0, height = 0;
        long ox = 0, oy = 0;
        bool forcedOnly = false;
        var palette = new List<SubtitleColor>();
        var streams = new List<VobSubLanguage>();
        string? lang = null;
        int index = 0;
        List<VobSubEntry>? entries = null;
        long total = 0;
        long lineStart = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '\t');
            long here = lineStart;
            lineStart += raw.Length + 1;
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                defects.Add(DefectKind.InvalidValue, DefectSeverity.Info, here, "VobSub index line without a colon.", "vobsub.idx.line");
                continue;
            }

            string key = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "size":
                    {
                        int x = value.IndexOf('x', StringComparison.OrdinalIgnoreCase);
                        if (x < 0 || !int.TryParse(value[..x], NumberStyles.None, CultureInfo.InvariantCulture, out width) || !int.TryParse(value[(x + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out height))
                        {
                            defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, here, "VobSub size line is not WxH.", "vobsub.idx.size");
                            width = height = 0;
                        }

                        break;
                    }

                case "org":
                    {
                        string[] parts = value.Split(',');
                        if (parts.Length == 2 && long.TryParse(parts[0].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long a) && long.TryParse(parts[1].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long b))
                        {
                            ox = a;
                            oy = b;
                        }

                        break;
                    }

                case "forced subs":
                    forcedOnly = value.StartsWith("ON", StringComparison.OrdinalIgnoreCase);
                    break;
                case "palette":
                    palette.Clear();
                    foreach (string part in value.Split(','))
                    {
                        string hex = part.Trim();
                        if (hex.Length == 0)
                        {
                            continue;
                        }

                        if (palette.Count < 16 && uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb) && hex.Length <= 6)
                        {
                            palette.Add(new SubtitleColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
                        }
                        else
                        {
                            defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, here, "VobSub palette entry is not six hex digits, or there are more than 16.", "vobsub.idx.palette");
                        }
                    }

                    break;
                case "id":
                    {
                        // "id: en, index: 0" starts a stream; the entries that follow belong to it.
                        string[] parts = value.Split(',');
                        lang = parts[0].Trim();
                        if (lang.Length == 0 || lang == "--")
                        {
                            lang = null;
                        }

                        index = 0;
                        for (int i = 1; i < parts.Length; i++)
                        {
                            string part = parts[i].Trim();
                            if (part.StartsWith("index:", StringComparison.OrdinalIgnoreCase) && int.TryParse(part[6..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                            {
                                index = n;
                            }
                        }

                        entries = [];
                        streams.Add(new VobSubLanguage(lang, index, entries));
                        break;
                    }

                case "timestamp":
                    {
                        // "timestamp: 00:00:01:234, filepos: 000000000"
                        if (entries is null || !TryEntry(value, out VobSubEntry entry))
                        {
                            defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, here, entries is null ? "VobSub time stamp before any id line." : "VobSub time stamp line is malformed.", "vobsub.idx.timestamp");
                            break;
                        }

                        limits.CheckElementCount(++total, here);
                        entries.Add(entry);
                        break;
                    }
            }
        }

        return new VobSubIndex(width, height, palette.ToArray(), forcedOnly, ox, oy, streams);
    }

    /// <summary>Like <see cref="Parse"/> but returns false instead of throwing on a limit.</summary>
    /// <param name="idx">The file bytes.</param>
    /// <param name="index">The index, or null on failure.</param>
    /// <param name="defects">Receives deviations; null to discard.</param>
    /// <param name="limits">Resource limits; null for the defaults.</param>
    public static bool TryParse(ReadOnlySpan<byte> idx, out VobSubIndex? index, DefectLog? defects = null, MediaLimits? limits = null)
    {
        try
        {
            index = Parse(idx, defects, limits);
            return true;
        }
        catch (MediaException)
        {
            index = null;
            return false;
        }
    }

    private static bool TryEntry(string value, out VobSubEntry entry)
    {
        entry = default;
        int comma = value.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return false;
        }

        string time = value[..comma].Trim();
        bool negative = time.StartsWith('-');
        string[] t = time.TrimStart('-').Split(':');
        if (t.Length != 4 || !long.TryParse(t[0], NumberStyles.None, CultureInfo.InvariantCulture, out long hh) || !long.TryParse(t[1], NumberStyles.None, CultureInfo.InvariantCulture, out long mm)
            || !long.TryParse(t[2], NumberStyles.None, CultureInfo.InvariantCulture, out long ss) || !long.TryParse(t[3], NumberStyles.None, CultureInfo.InvariantCulture, out long ms)
            || hh > 100_000 || mm > 59 || ss > 59 || ms > 999)
        {
            return false;
        }

        string rest = value[(comma + 1)..].Trim();
        int c = rest.IndexOf(':', StringComparison.Ordinal);
        if (c < 0 || !rest[..c].Trim().Equals("filepos", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(rest[(c + 1)..].Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long pos))
        {
            return false;
        }

        long millis = ((((hh * 60) + mm) * 60) + ss) * 1000 + ms;
        entry = new VobSubEntry(new MediaTime(negative ? -millis : millis, 1000), pos);
        return true;
    }
}

/// <summary>Reads VobSub sub-picture units out of MPEG program stream data (a <c>.sub</c> file, or a VOB).</summary>
internal static class DvdSubPictureStream
{
    // ISO/IEC 13818-1 §2.5.3.4 pack header, §2.4.3.6 PES packet; DVD sub-pictures sit in private stream 1 (0xBD), the first payload byte
    // is the sub-stream id (0x20-0x3F) and the unit follows directly.
    internal readonly record struct Unit(long Offset, MediaTime Time, byte[] Data, int SubStream);

    // Reads the unit that starts at `position`, for the sub-stream `subStream` (-1: the first sub-picture sub-stream found).
    internal static bool TryReadUnit(ReadOnlySpan<byte> sub, long position, int subStream, MediaLimits limits, DefectLog defects, out Unit unit)
    {
        unit = default;
        if (position < 0)
        {
            // A hexadecimal file position of 16 digits can read as negative; there is no unit before the start of the file.
            return false;
        }

        int p = (int)Math.Min(position, sub.Length);
        List<byte>? data = null;
        int expected = 0;
        int found = subStream;
        MediaTime pts = MediaTime.Unknown;
        for (int packets = 0; packets < 4096 && p + 6 <= sub.Length; packets++)
        {
            if (!NextPacket(sub, ref p, out int code, out int payloadStart, out int payloadEnd, out MediaTime packetPts))
            {
                break;
            }

            if (code != 0xBD || payloadEnd - payloadStart < 1)
            {
                continue;
            }

            int id = sub[payloadStart];
            if (id is < 0x20 or > 0x3F || (found >= 0 && id != found))
            {
                continue;
            }

            ReadOnlySpan<byte> body = sub[(payloadStart + 1)..payloadEnd];
            if (data is null)
            {
                if (body.Length < 2)
                {
                    continue;
                }

                found = id;
                expected = (body[0] << 8) | body[1];
                if (expected == 0)
                {
                    expected = body.Length;
                }

                limits.CheckPacket(expected, p);
                data = new List<byte>(expected);
                pts = packetPts;
            }
            else if (packetPts.IsKnown)
            {
                // A new unit begins before this one is complete.
                defects.Add(DefectKind.Truncated, DefectSeverity.Warning, p, "A sub-picture unit ends before its declared size.", "vobsub.sub.truncated");
                break;
            }

            data.AddRange(body);
            if (data.Count >= expected)
            {
                break;
            }
        }

        if (data is null)
        {
            return false;
        }

        if (data.Count > expected)
        {
            data.RemoveRange(expected, data.Count - expected);
        }

        unit = new Unit(position, pts, data.ToArray(), found);
        return true;
    }

    // Moves `p` to the next packet and describes it. Skips garbage between packets by looking for the next start code.
    internal static bool NextPacket(ReadOnlySpan<byte> d, ref int p, out int code, out int payloadStart, out int payloadEnd, out MediaTime pts)
    {
        code = 0;
        payloadStart = payloadEnd = 0;
        pts = MediaTime.Unknown;
        while (p + 4 <= d.Length)
        {
            int rel = d[p..].IndexOf((ReadOnlySpan<byte>)[0, 0, 1]);
            if (rel < 0 || p + rel + 4 > d.Length)
            {
                p = d.Length;
                return false;
            }

            p += rel;
            code = d[p + 3];
            if (code == 0xBA)
            {
                // MPEG-2 pack header is 14 bytes plus stuffing; MPEG-1's is 12.
                int len = p + 5 <= d.Length && (d[p + 4] >> 6) == 1 ? (p + 14 <= d.Length ? 14 + (d[p + 13] & 7) : d.Length - p) : 12;
                p = Math.Min(d.Length, p + len);
                continue;
            }

            if (code < 0xB9)
            {
                p += 4;
                continue;
            }

            if (p + 6 > d.Length)
            {
                p = d.Length;
                return false;
            }

            int length = (d[p + 4] << 8) | d[p + 5];
            int bodyStart = p + 6;
            int bodyEnd = Math.Min(d.Length, bodyStart + length);
            p = bodyEnd;
            if (code != 0xBD)
            {
                continue; // Only private stream 1 carries sub-pictures; other packets are skipped by their length.
            }

            payloadStart = bodyStart;
            payloadEnd = bodyEnd;
            if (code == 0xBD && bodyEnd - bodyStart >= 3 && (d[bodyStart] >> 6) == 2)
            {
                // §2.4.3.7: flags byte, PTS/DTS flags, header data length.
                int hdl = d[bodyStart + 2];
                if ((d[bodyStart + 1] & 0x80) != 0 && bodyStart + 3 + 5 <= bodyEnd)
                {
                    int s = bodyStart + 3;
                    long t = ((long)(d[s] >> 1) & 7) << 30 | (long)d[s + 1] << 22 | ((long)(d[s + 2] >> 1) << 15) | ((long)d[s + 3] << 7) | (long)(d[s + 4] >> 1);
                    pts = BitmapSupport.Ticks90k(t);
                }

                payloadStart = Math.Min(bodyEnd, bodyStart + 3 + hdl);
            }

            return true;
        }

        return false;
    }
}

/// <summary>Reads VobSub sidecars into image cues.</summary>
public static class VobSubReader
{
    /// <summary>
    /// Reads an index and its sub-picture file. Each index entry is read at its file position; the cue starts at the entry's time stamp
    /// and, when the unit has no stop command, ends where the next entry of the stream starts. The palette and display size come from the
    /// index unless <paramref name="options"/> overrides the size.
    /// </summary>
    /// <param name="idx">The <c>.idx</c> file bytes.</param>
    /// <param name="sub">The <c>.sub</c> file bytes.</param>
    /// <param name="options">Stream selection, limits and language; null for the defaults (every stream).</param>
    /// <param name="defects">Receives deviations; null to discard.</param>
    /// <returns>The cues in index order, stream by stream.</returns>
    /// <exception cref="MediaLimitExceededException">A limit was exceeded.</exception>
    public static IReadOnlyList<SubtitleCue> Read(ReadOnlySpan<byte> idx, ReadOnlySpan<byte> sub, BitmapSubtitleOptions? options = null, DefectLog? defects = null)
    {
        options ??= new BitmapSubtitleOptions();
        MediaLimits limits = options.Limits ?? MediaLimits.Default;
        defects ??= new DefectLog(limits);
        VobSubIndex index = VobSubIndex.Parse(idx, defects, limits);
        var cues = new List<SubtitleCue>();
        foreach (VobSubLanguage stream in index.Streams)
        {
            if (options.Stream is { } want && want != stream.Index)
            {
                continue;
            }

            var decoder = new VobSubDecoder(WithIndex(options, index, stream), defects);
            for (int i = 0; i < stream.Entries.Count; i++)
            {
                VobSubEntry e = stream.Entries[i];
                if (!DvdSubPictureStream.TryReadUnit(sub, e.FilePosition, 0x20 + stream.Index, limits, defects, out DvdSubPictureStream.Unit unit))
                {
                    defects.Add(DefectKind.MissingRequired, DefectSeverity.Error, e.FilePosition, "No sub-picture unit at an index file position.", "vobsub.sub.missing");
                    continue;
                }

                MediaTime next = i + 1 < stream.Entries.Count ? stream.Entries[i + 1].Time : MediaTime.Unknown;
                foreach (SubtitleCue cue in decoder.Decode(unit.Data, e.Time, stream.Index, e.FilePosition))
                {
                    cues.Add(!cue.End.IsKnown && next.IsKnown && next > cue.Start ? cue with { End = next } : cue);
                }
            }
        }

        return cues;
    }

    /// <summary>
    /// Reads a <c>.sub</c> (or any MPEG program stream) without an index: every sub-picture unit of sub-streams 0x20-0x3F, timed by the
    /// PES time stamp of its first packet. Uses <see cref="BitmapSubtitleOptions.Palette"/> or the grey ramp.
    /// </summary>
    /// <param name="sub">The program stream bytes.</param>
    /// <param name="options">Palette, display size, stream selection and limits; null for the defaults.</param>
    /// <param name="defects">Receives deviations; null to discard.</param>
    /// <returns>The cues in stream order.</returns>
    /// <exception cref="MediaLimitExceededException">A limit was exceeded.</exception>
    public static IReadOnlyList<SubtitleCue> ReadProgramStream(ReadOnlySpan<byte> sub, BitmapSubtitleOptions? options = null, DefectLog? defects = null)
    {
        options ??= new BitmapSubtitleOptions();
        MediaLimits limits = options.Limits ?? MediaLimits.Default;
        defects ??= new DefectLog(limits);
        var cues = new List<SubtitleCue>();
        var decoder = new VobSubDecoder(options, defects);
        int p = 0;
        long count = 0;
        while (p < sub.Length)
        {
            int before = p;
            if (!DvdSubPictureStream.NextPacket(sub, ref p, out int code, out int start, out int end, out MediaTime pts))
            {
                break;
            }

            if (code != 0xBD || !pts.IsKnown || end - start < 3 || sub[start] is < 0x20 or > 0x3F)
            {
                continue;
            }

            int id = sub[start];
            if (options.Stream is { } want && want != id - 0x20)
            {
                continue;
            }

            // Re-read from the start of this packet so continuation packets are gathered with it.
            if (DvdSubPictureStream.TryReadUnit(sub, before, id, limits, defects, out DvdSubPictureStream.Unit unit))
            {
                limits.CheckElementCount(++count, before);
                cues.AddRange(decoder.Decode(unit.Data, unit.Time, id - 0x20, before));
            }
        }

        return cues;
    }

    internal static BitmapSubtitleOptions WithIndex(BitmapSubtitleOptions options, VobSubIndex index, VobSubLanguage? stream) => options with
    {
        Palette = index.Palette.Count > 0 ? index.Palette : options.Palette,
        DisplayWidth = options.DisplayWidth > 0 ? options.DisplayWidth : index.Width,
        DisplayHeight = options.DisplayHeight > 0 ? options.DisplayHeight : index.Height,
        Language = options.Language ?? stream?.Language,
    };
}
