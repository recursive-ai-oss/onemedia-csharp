namespace RecursiveAI.OneMedia.Captions;

// DVD sub-picture units (SPU). Layout from the public prose descriptions of the DVD-Video sub-picture format: a two-word header, two
// run-length coded fields (even and odd lines) and a chain of display control sequences, each with a delay and a list of commands.

/// <summary>
/// Decodes DVD sub-picture units (VobSub) into image cues. One unit yields one cue per start/stop pair of its control sequences,
/// normally one. Pixel codes 0-3 map to the unit's four palette choices (SET_COLOR) and contrasts (SET_CONTR). Stateless apart from its
/// options and defect log, so one instance may decode many units, but not from several threads at once.
/// </summary>
public sealed class VobSubDecoder
{
    private readonly MediaLimits _limits;
    private readonly DefectLog _defects;
    private readonly SubtitleColor[] _palette = new SubtitleColor[16];
    private readonly int _displayWidth, _displayHeight;
    private readonly string? _language;

    /// <summary>Creates a decoder.</summary>
    /// <param name="options">Palette, display size, language and limits; null for the defaults (a grey ramp palette, a 720 by 576 display).</param>
    /// <param name="defects">Receives every deviation found; null to discard them.</param>
    public VobSubDecoder(BitmapSubtitleOptions? options = null, DefectLog? defects = null)
    {
        options ??= new BitmapSubtitleOptions();
        _limits = options.Limits ?? MediaLimits.Default;
        _defects = defects ?? new DefectLog(_limits);
        _displayWidth = options.DisplayWidth > 0 ? options.DisplayWidth : 720;
        _displayHeight = options.DisplayHeight > 0 ? options.DisplayHeight : 576;
        _language = options.Language;
        for (int i = 0; i < 16; i++)
        {
            byte g = (byte)(i * 17);
            _palette[i] = options.Palette is { } p && i < p.Count ? p[i] : new SubtitleColor(g, g, g);
        }
    }

    /// <summary>Decodes one sub-picture unit.</summary>
    /// <param name="spu">The unit: size word, control table offset word, pixel data and control sequences.</param>
    /// <param name="time">The unit's presentation time (PES time stamp, index time stamp or container block time). Control-sequence delays are added to it.</param>
    /// <param name="stream">The VobSub stream number to put on the cues (0 for none).</param>
    /// <param name="offset">Source offset of the unit, for defect positions.</param>
    /// <returns>The cues; empty when the unit shows nothing. Never throws on malformed data: it records defects and returns what decoded.</returns>
    /// <exception cref="MediaLimitExceededException">The display area is larger than <see cref="MediaLimits.MaxAllocationBytes"/> allows.</exception>
    public IReadOnlyList<SubtitleCue> Decode(ReadOnlySpan<byte> spu, MediaTime time, int stream = 0, long offset = 0)
    {
        var cues = new List<SubtitleCue>();
        if (spu.Length < 4)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "DVD sub-picture unit shorter than its header.", "vobsub.spu.short");
            return cues;
        }

        int size = (spu[0] << 8) | spu[1];
        int table = (spu[2] << 8) | spu[3];
        if (size == 0 || size > spu.Length)
        {
            if (size != 0)
            {
                _defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Warning, offset, "Sub-picture size field is larger than the data given.", "vobsub.spu.size");
            }

            size = spu.Length;
        }

        spu = spu[..size];
        if (table < 4 || table >= size - 3)
        {
            _defects.Add(DefectKind.InvalidValue, DefectSeverity.Error, offset + 2, "Sub-picture control table offset is outside the unit.", "vobsub.spu.table");
            return cues;
        }

        int colorWord = 0x3210, contrastWord = 0; // code 0..3 -> palette entry nibble / alpha nibble, packed as in SET_COLOR / SET_CONTR
        int x1 = 0, x2 = -1, y1 = 0, y2 = -1, topOffset = 0, bottomOffset = 0;
        bool haveArea = false, havePixels = false;
        SubtitleCue? open = null;
        int at = table;
        for (int guard = 0; guard < 64; guard++)
        {
            if (at + 4 > size)
            {
                _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset + at, "Sub-picture control sequence runs past the unit.", "vobsub.dcsq.short");
                break;
            }

            // The delay is in units of 1024 / 90000 s.
            MediaTime when = BitmapSupport.Plus(time, BitmapSupport.Ticks90k(((spu[at] << 8) | spu[at + 1]) * 1024L));
            bool pendingStart = false, pendingForced = false;
            int next = (spu[at + 2] << 8) | spu[at + 3];
            int p = at + 4;
            bool done = false;
            while (p < size && !done)
            {
                int cmd = spu[p++];
                switch (cmd)
                {
                    case 0x00:
                    case 0x01:
                        // The commands that say where and how follow in the same sequence (or came before it), so the picture is built when the sequence ends.
                        pendingStart = true;
                        pendingForced = cmd == 0;
                        break;
                    case 0x02:
                        if (open is not null)
                        {
                            cues.Add(open with { End = MediaTime.Max(when, open.Start) });
                            open = null;
                        }

                        break;
                    case 0x03 when p + 2 <= size:
                        colorWord = (spu[p] << 8) | spu[p + 1];
                        p += 2;
                        break;
                    case 0x04 when p + 2 <= size:
                        contrastWord = (spu[p] << 8) | spu[p + 1];
                        p += 2;
                        break;
                    case 0x05 when p + 6 <= size:
                        x1 = (spu[p] << 4) | (spu[p + 1] >> 4);
                        x2 = ((spu[p + 1] & 0xF) << 8) | spu[p + 2];
                        y1 = (spu[p + 3] << 4) | (spu[p + 4] >> 4);
                        y2 = ((spu[p + 4] & 0xF) << 8) | spu[p + 5];
                        haveArea = true;
                        p += 6;
                        break;
                    case 0x06 when p + 4 <= size:
                        topOffset = (spu[p] << 8) | spu[p + 1];
                        bottomOffset = (spu[p + 2] << 8) | spu[p + 3];
                        havePixels = true;
                        p += 4;
                        break;
                    case 0x07 when p + 2 <= size:
                        // Colour and contrast changes inside the picture are not applied: the picture keeps the colours set before it starts.
                        _defects.Add(DefectKind.Unsupported, DefectSeverity.Info, offset + p, "Sub-picture colour change command (CHG_COLCON) not applied.", "vobsub.chgcolcon");
                        p += (spu[p] << 8) | spu[p + 1];
                        break;
                    case 0xFF:
                        done = true;
                        break;
                    default:
                        _defects.Add(DefectKind.ReservedValue, DefectSeverity.Error, offset + p - 1, $"Unknown sub-picture command 0x{cmd:x2}; control sequence abandoned.", "vobsub.command");
                        done = true;
                        break;
                }
            }

            if (pendingStart)
            {
                if (open is not null)
                {
                    cues.Add(open with { End = MediaTime.Max(when, open.Start) });
                }

                open = haveArea && havePixels
                    ? Render(spu, topOffset, bottomOffset, table, x1, y1, x2, y2, colorWord, contrastWord, when, pendingForced, stream, offset)
                    : null;
            }

            // The last sequence points at itself.
            if (next == at || next <= at || next >= size)
            {
                break;
            }

            at = next;
        }

        if (open is not null)
        {
            cues.Add(open);
        }

        return cues;
    }

    private SubtitleCue? Render(ReadOnlySpan<byte> spu, int top, int bottom, int table, int x1, int y1, int x2, int y2, int colors, int contrasts, MediaTime start, bool forced, int stream, long offset)
    {
        int w = x2 - x1 + 1, h = y2 - y1 + 1;
        if (w < 1 || h < 1)
        {
            _defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, "Sub-picture display area is empty or inverted.", "vobsub.area");
            return null;
        }

        BitmapSupport.CheckImage(_limits, w, h, 0, offset);
        if (top < 4 || top >= spu.Length || bottom < 4 || bottom >= spu.Length)
        {
            _defects.Add(DefectKind.InvalidValue, DefectSeverity.Error, offset, "Sub-picture pixel data offsets are outside the unit.", "vobsub.pixels.offset");
            return null;
        }

        // Pixel codes (0 background, 1 pattern, 2 and 3 emphasis) -> RGBA. The nibbles of the command words are e2 e1 p b.
        var lut = new SubtitleColor[4];
        for (int code = 0; code < 4; code++)
        {
            int shift = code * 4;
            int entry = (colors >> shift) & 0xF;
            int alpha = ((contrasts >> shift) & 0xF) * 17;
            SubtitleColor c = _palette[entry];
            lut[code] = new SubtitleColor(c.R, c.G, c.B, (byte)alpha);
        }

        byte[] codes = new byte[w * h];
        // The even-numbered lines (counting from 0) come from the first field, the odd ones from the second. Each field ends where the
        // next data starts: the top field at the bottom field, the bottom field at the control table.
        int topEnd = bottom > top ? bottom : table;
        int bottomEnd = table > bottom ? table : spu.Length;
        DecodeField(spu[..Math.Min(topEnd, spu.Length)], top, codes, w, h, 0, offset);
        DecodeField(spu[..Math.Min(bottomEnd, spu.Length)], bottom, codes, w, h, 1, offset);

        byte[] rgba = new byte[4 * w * h];
        for (int i = 0; i < codes.Length; i++)
        {
            BitmapSupport.Put(rgba, i, lut[codes[i]]);
        }

        var image = new SubtitleImage(x1, y1, w, h, w * 4, rgba) { DisplayWidth = _displayWidth, DisplayHeight = _displayHeight };
        return BitmapSubtitleCues.Make(BitmapSubtitleCues.VobSub, start, MediaTime.Unknown, [image], forced, _language, stream);
    }

    // Run-length code, in nibbles: 1-3 pixels in 4 bits (nncc), 4-15 in 8, 16-63 in 12, 64-255 in 16, count 0 meaning "to the end of the line".
    // Every line ends on a byte boundary.
    private void DecodeField(ReadOnlySpan<byte> data, int start, byte[] codes, int w, int h, int firstLine, long offset)
    {
        int nib = start * 2;
        int limit = data.Length * 2;
        for (int y = firstLine; y < h; y += 2)
        {
            int x = 0;
            while (x < w)
            {
                if (nib >= limit)
                {
                    _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, offset + start, "Sub-picture pixel data ends before the last line.", "vobsub.rle.truncated");
                    return;
                }

                int v = Nibble(data, nib++);
                if (v < 4)
                {
                    v = (v << 4) | Nibble(data, nib++);
                    if (v < 16)
                    {
                        v = (v << 4) | Nibble(data, nib++);
                        if (v < 64)
                        {
                            v = (v << 4) | Nibble(data, nib++);
                        }
                    }
                }

                int count = v >> 2;
                int code = v & 3;
                if (count == 0)
                {
                    count = w - x;
                }

                if (count > w - x)
                {
                    _defects.Add(DefectKind.Inconsistent, DefectSeverity.Info, offset + start, "Sub-picture run goes past the end of its line.", "vobsub.rle.overrun");
                    count = w - x;
                }

                if (code != 0)
                {
                    codes.AsSpan((y * w) + x, count).Fill((byte)code);
                }

                x += count;
            }

            if ((nib & 1) != 0)
            {
                nib++;
            }
        }
    }

    private static int Nibble(ReadOnlySpan<byte> d, int index) => index >> 1 < d.Length ? ((index & 1) == 0 ? d[index >> 1] >> 4 : d[index >> 1] & 0xF) : 0;
}
