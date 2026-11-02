namespace RecursiveAI.OneMedia.Captions;

// Blu-ray Presentation Graphic Stream. Segment layout from the public prose descriptions of the format (segment header, PCS, WDS, PDS,
// ODS, END, and the run-length code), checked against files and ffmpeg's decoded output; there is no free official specification.

/// <summary>
/// Decodes a Presentation Graphic Stream, one segment at a time or one container packet at a time, into image cues. Feed segments in
/// stream order; every END segment completes a display set. A display set that shows objects opens a cue; the next display set (one that
/// clears the screen or shows something else) closes it by giving it its end time. Not thread-safe.
/// </summary>
public sealed class PgsDecoder
{
    private const int MaxObjects = 64; // The format allows 64 objects per epoch; more are a defect.

    private readonly MediaLimits _limits;
    private readonly DefectLog _defects;
    private readonly YCbCrMatrix _matrix;
    private readonly string? _language;
    private readonly List<SubtitleCue> _ready = [];
    private readonly Dictionary<int, PgsPalette> _palettes = [];
    private readonly Dictionary<int, PgsObject> _objects = [];

    private SubtitleCue? _open;
    private bool _pcsSeen;
    private MediaTime _pcsTime;
    private MediaTime _pcsDuration = MediaTime.Unknown;
    private int _width, _height, _paletteId;
    private bool _paletteOnly;
    private List<Placement> _placements = [];
    private long _usedBytes;

    /// <summary>Creates a decoder.</summary>
    /// <param name="options">Matrix, limits and default language; null for the defaults.</param>
    /// <param name="defects">Receives every deviation found; null to discard them.</param>
    public PgsDecoder(BitmapSubtitleOptions? options = null, DefectLog? defects = null)
    {
        options ??= new BitmapSubtitleOptions();
        _limits = options.Limits ?? MediaLimits.Default;
        _defects = defects ?? new DefectLog(_limits);
        _matrix = options.Matrix;
        _language = options.Language;
    }

    /// <summary>
    /// Feeds segments as a container stores them: each is a type byte, a 16-bit size and the payload (no <c>PG</c> header, no timestamps).
    /// Matroska blocks and MPEG-TS payloads hold one or more of these.
    /// </summary>
    /// <param name="data">The segments.</param>
    /// <param name="time">The presentation time of the packet.</param>
    /// <param name="duration">The packet's duration if the container states one, else <see cref="MediaTime.Unknown"/>; it bounds the end of a cue that nothing else closes.</param>
    /// <param name="offset">Source offset of <paramref name="data"/> for defect positions.</param>
    /// <returns>The cues completed by this call (earlier cues whose end became known); the cue still showing is held back until <see cref="Flush"/> or the next display set.</returns>
    /// <exception cref="MediaLimitExceededException">A picture would exceed <see cref="MediaLimits.MaxAllocationBytes"/>.</exception>
    public IReadOnlyList<SubtitleCue> Feed(ReadOnlySpan<byte> data, MediaTime time, MediaTime duration, long offset = 0)
    {
        _ready.Clear();
        int p = 0;
        while (p + 3 <= data.Length)
        {
            int type = data[p];
            int size = (data[p + 1] << 8) | data[p + 2];
            if (p + 3 + size > data.Length)
            {
                _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset + p, "A PGS segment runs past the end of its packet.", "pgs.segment.truncated");
                size = data.Length - p - 3;
            }

            Segment(type, data.Slice(p + 3, size), time, duration, offset + p);
            p += 3 + size;
        }

        if (p < data.Length)
        {
            _defects.Add(DefectKind.UnaccountedBytes, DefectSeverity.Warning, offset + p, "Bytes after the last PGS segment of a packet.", "pgs.trailing");
        }

        return _ready.ToArray();
    }

    /// <summary>
    /// Feeds a whole <c>.sup</c> file: segments each preceded by the <c>PG</c> magic, a 32-bit presentation time and a 32-bit decoding time
    /// (90 kHz). A damaged header makes the reader look for the next <c>PG</c> and record a defect.
    /// </summary>
    /// <param name="sup">The file bytes.</param>
    /// <returns>All cues, the last one flushed.</returns>
    /// <exception cref="MediaLimitExceededException">A picture would exceed <see cref="MediaLimits.MaxAllocationBytes"/>.</exception>
    public IReadOnlyList<SubtitleCue> FeedSup(ReadOnlySpan<byte> sup)
    {
        var all = new List<SubtitleCue>();
        int p = 0;
        while (p + 13 <= sup.Length)
        {
            if (sup[p] != (byte)'P' || sup[p + 1] != (byte)'G')
            {
                _defects.Add(DefectKind.InvalidValue, DefectSeverity.Error, p, "PGS segment header does not start with 'PG'; resynchronising.", "pgs.sup.magic");
                int next = sup[(p + 1)..].IndexOf("PG"u8);
                if (next < 0)
                {
                    break;
                }

                p += 1 + next;
                continue;
            }

            uint pts = ReadU32(sup, p + 2);
            int type = sup[p + 10];
            int size = (sup[p + 11] << 8) | sup[p + 12];
            if (p + 13 + size > sup.Length)
            {
                _defects.Add(DefectKind.Truncated, DefectSeverity.Error, p, "A PGS segment runs past the end of the file.", "pgs.segment.truncated");
                size = sup.Length - p - 13;
            }

            // Segment() takes the type separately, so the payload is passed on directly.
            _ready.Clear();
            Segment(type, sup.Slice(p + 13, size), BitmapSupport.Ticks90k(pts), MediaTime.Unknown, p);
            all.AddRange(_ready);
            p += 13 + size;
        }

        all.AddRange(Flush());
        return all;
    }

    /// <summary>Ends the stream: returns the cue still showing, if any, with an unknown end (or the duration of the last packet when it gave one).</summary>
    public IReadOnlyList<SubtitleCue> Flush()
    {
        if (_open is null)
        {
            return [];
        }

        SubtitleCue c = _open;
        _open = null;
        return [c];
    }

    private static uint ReadU32(ReadOnlySpan<byte> d, int o) => ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];

    private void Segment(int type, ReadOnlySpan<byte> s, MediaTime time, MediaTime duration, long offset)
    {
        switch (type)
        {
            case 0x16:
                Pcs(s, time, duration, offset);
                break;
            case 0x17:
                break; // Window definitions only bound where objects may draw; the objects carry their own positions.
            case 0x14:
                Pds(s, offset);
                break;
            case 0x15:
                Ods(s, offset);
                break;
            case 0x80:
                End(offset);
                break;
            default:
                _defects.Add(DefectKind.ReservedValue, DefectSeverity.Warning, offset, $"Unknown PGS segment type 0x{type:x2}; skipped.", "pgs.segment.type");
                break;
        }
    }

    private void Pcs(ReadOnlySpan<byte> s, MediaTime time, MediaTime duration, long offset)
    {
        if (s.Length < 11)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "PGS composition segment too short.", "pgs.pcs.short");
            _pcsSeen = false;
            return;
        }

        _width = (s[0] << 8) | s[1];
        _height = (s[2] << 8) | s[3];
        int state = s[7];
        _paletteOnly = (s[8] & 0x80) != 0;
        _paletteId = s[9];
        int count = s[10];
        _pcsTime = time;
        _pcsDuration = duration;
        _pcsSeen = true;
        if (state == 0x80)
        {
            // Epoch start: nothing from before survives.
            _objects.Clear();
            _palettes.Clear();
            _usedBytes = 0;
        }

        _placements = [];
        int p = 11;
        for (int i = 0; i < count; i++)
        {
            if (p + 8 > s.Length)
            {
                _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "PGS composition object runs past its segment.", "pgs.pcs.object");
                break;
            }

            // Observed against ffmpeg's decoder: bit 0x80 says a crop rectangle follows, bit 0x40 forces display of the object.
            bool cropped = (s[p + 3] & 0x80) != 0;
            var pl = new Placement
            {
                ObjectId = (s[p] << 8) | s[p + 1],
                X = (s[p + 4] << 8) | s[p + 5],
                Y = (s[p + 6] << 8) | s[p + 7],
                Forced = (s[p + 3] & 0x40) != 0,
            };
            p += 8;
            if (cropped)
            {
                if (p + 8 > s.Length)
                {
                    _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "PGS crop rectangle runs past its segment.", "pgs.pcs.crop");
                    break;
                }

                pl.Crop = true;
                pl.CropX = (s[p] << 8) | s[p + 1];
                pl.CropY = (s[p + 2] << 8) | s[p + 3];
                pl.CropW = (s[p + 4] << 8) | s[p + 5];
                pl.CropH = (s[p + 6] << 8) | s[p + 7];
                p += 8;
            }

            _placements.Add(pl);
        }
    }

    private void Pds(ReadOnlySpan<byte> s, long offset)
    {
        if (s.Length < 2)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "PGS palette segment too short.", "pgs.pds.short");
            return;
        }

        int id = s[0];
        if (!_palettes.TryGetValue(id, out PgsPalette? pal))
        {
            pal = new PgsPalette();
            _palettes[id] = pal;
        }

        // Entries are index, Y, Cr, Cb, alpha. Observed order: Cr before Cb (checked against ffmpeg's decoded colours).
        for (int p = 2; p + 5 <= s.Length; p += 5)
        {
            int e = s[p];
            pal.Y[e] = s[p + 1];
            pal.Cr[e] = s[p + 2];
            pal.Cb[e] = s[p + 3];
            pal.A[e] = s[p + 4];
        }

        if ((s.Length - 2) % 5 != 0)
        {
            _defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, offset, "PGS palette segment has a partial entry.", "pgs.pds.partial");
        }
    }

    private void Ods(ReadOnlySpan<byte> s, long offset)
    {
        if (s.Length < 4)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "PGS object segment too short.", "pgs.ods.short");
            return;
        }

        int id = (s[0] << 8) | s[1];
        int flags = s[3];
        ReadOnlySpan<byte> rest = s[4..];
        if ((flags & 0x80) != 0)
        {
            if (rest.Length < 7)
            {
                _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "PGS object header too short.", "pgs.ods.header");
                return;
            }

            // The data length counts the four bytes of width and height that follow it.
            int dataLength = (rest[0] << 16) | (rest[1] << 8) | rest[2];
            int w = (rest[3] << 8) | rest[4];
            int h = (rest[5] << 8) | rest[6];
            if (w == 0 || h == 0)
            {
                _defects.Add(DefectKind.InvalidValue, DefectSeverity.Error, offset, "PGS object with a zero dimension.", "pgs.ods.size");
                _objects.Remove(id);
                return;
            }

            if (!_objects.ContainsKey(id) && _objects.Count >= MaxObjects)
            {
                _defects.Add(DefectKind.LimitReached, DefectSeverity.Error, offset, "More than 64 PGS objects in one epoch; the object is skipped.", "pgs.ods.count");
                return;
            }

            long old = _objects.TryGetValue(id, out PgsObject? prev) ? prev.Pixels?.LongLength ?? 0 : 0;
            BitmapSupport.CheckImage(_limits, w, h, 0, offset); // A hostile size fails before anything is allocated.
            if (_usedBytes - old + ((long)w * h) > _limits.MaxAllocationBytes)
            {
                throw new MediaLimitExceededException("MaxAllocationBytes", _limits.MaxAllocationBytes, _usedBytes + ((long)w * h), offset);
            }

            var obj = new PgsObject { Width = w, Height = h, Expected = Math.Max(0, dataLength - 4) };
            _objects[id] = obj;
            AppendRle(obj, rest[7..], offset);
        }
        else if (_objects.TryGetValue(id, out PgsObject? open) && open.Pixels is null)
        {
            AppendRle(open, rest, offset);
        }
        else
        {
            _defects.Add(DefectKind.InvalidOrder, DefectSeverity.Warning, offset, "PGS object continuation without a first fragment.", "pgs.ods.order");
            return;
        }

        if ((flags & 0x40) != 0 && _objects.TryGetValue(id, out PgsObject? done))
        {
            Finish(done, offset);
        }
    }

    private void AppendRle(PgsObject obj, ReadOnlySpan<byte> data, long offset)
    {
        // The declared length bounds what we keep; extra bytes are a defect, not memory.
        long room = Math.Max(0, (long)obj.Expected - obj.Rle.Count);
        if (data.Length > room)
        {
            _defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, offset, "PGS object has more data than its declared length.", "pgs.ods.length");
            data = data[..(int)room];
        }

        obj.Rle.AddRange(data);
    }

    private void Finish(PgsObject obj, long offset)
    {
        int w = obj.Width, h = obj.Height;
        byte[] pixels = new byte[w * h];
        _usedBytes += pixels.LongLength;
        ReadOnlySpan<byte> d = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(obj.Rle);
        int p = 0, x = 0, y = 0;
        bool ok = true;

        // Run-length code: C = one pixel; 0,0 = end of line; 0,01LLLLLL / 0,01LLLLLL LLLLLLLL = L pixels of colour 0; 0,1LLLLLL C / 0,11...
        // = L pixels of colour C; the top bits of the second byte say which.
        while (p < d.Length && y < h)
        {
            int b = d[p++];
            int run = 1, color = b;
            if (b == 0)
            {
                if (p >= d.Length)
                {
                    ok = false;
                    break;
                }

                int b2 = d[p++];
                if (b2 == 0)
                {
                    // End of line. A short line is padded with colour 0 by the zeroed buffer.
                    if (x < w)
                    {
                        _defects.Add(DefectKind.Inconsistent, DefectSeverity.Info, offset, "PGS line shorter than the object width.", "pgs.rle.short");
                    }

                    x = 0;
                    y++;
                    continue;
                }

                int kind = b2 >> 6;
                run = b2 & 0x3F;
                if ((kind & 1) != 0)
                {
                    if (p >= d.Length)
                    {
                        ok = false;
                        break;
                    }

                    run = (run << 8) | d[p++];
                }

                color = 0;
                if ((kind & 2) != 0)
                {
                    if (p >= d.Length)
                    {
                        ok = false;
                        break;
                    }

                    color = d[p++];
                }
            }

            int fit = Math.Min(run, w - x);
            if (fit < run)
            {
                _defects.Add(DefectKind.Inconsistent, DefectSeverity.Warning, offset, "PGS run goes past the end of its line.", "pgs.rle.overrun");
            }

            if (color != 0)
            {
                pixels.AsSpan((y * w) + x, fit).Fill((byte)color);
            }

            x += fit;
            // A line that reaches the width wraps only at the end-of-line code, as the encoders write it.
        }

        if (!ok || y < h)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Warning, offset, "PGS object data ends before the last line.", "pgs.rle.truncated");
        }

        obj.Pixels = pixels;
        obj.Rle.Clear();
        obj.Rle.TrimExcess();
    }

    private void End(long offset)
    {
        if (!_pcsSeen)
        {
            _defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, offset, "PGS display set end without a composition segment.", "pgs.end.nopcs");
            return;
        }

        _pcsSeen = false;
        var images = new List<SubtitleImage>();
        bool forced = false;
        bool bt709 = BitmapSupport.UseBt709(_matrix, _height);
        _palettes.TryGetValue(_paletteId, out PgsPalette? pal);
        if (pal is null && _placements.Count > 0)
        {
            _defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, offset, $"PGS palette {_paletteId} was never defined; objects are transparent.", "pgs.palette.missing");
        }

        long bytes = 0;
        foreach (Placement pl in _placements)
        {
            if (!_objects.TryGetValue(pl.ObjectId, out PgsObject? obj) || obj.Pixels is null)
            {
                _defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, offset, $"PGS composition names object {pl.ObjectId}, which has no data.", "pgs.object.missing");
                continue;
            }

            int cx = 0, cy = 0, cw = obj.Width, ch = obj.Height;
            if (pl.Crop)
            {
                cx = Math.Clamp(pl.CropX, 0, obj.Width);
                cy = Math.Clamp(pl.CropY, 0, obj.Height);
                cw = Math.Clamp(pl.CropW, 0, obj.Width - cx);
                ch = Math.Clamp(pl.CropH, 0, obj.Height - cy);
                if (cw == 0 || ch == 0)
                {
                    continue;
                }
            }

            BitmapSupport.CheckImage(_limits, cw, ch, bytes, offset);
            bytes += 4L * cw * ch;
            byte[] rgba = new byte[4 * cw * ch];
            if (pal is not null)
            {
                // Palette entries are converted once per image; an unset entry stays fully transparent.
                var lut = new SubtitleColor[256];
                for (int i = 0; i < 256; i++)
                {
                    lut[i] = pal.A[i] == 0 ? default : BitmapSupport.FromYCbCr(pal.Y[i], pal.Cb[i], pal.Cr[i], pal.A[i], bt709);
                }

                for (int row = 0; row < ch; row++)
                {
                    int src = ((cy + row) * obj.Width) + cx;
                    for (int col = 0; col < cw; col++)
                    {
                        BitmapSupport.Put(rgba, (row * cw) + col, lut[obj.Pixels[src + col]]);
                    }
                }
            }

            forced |= pl.Forced;
            images.Add(new SubtitleImage(pl.X, pl.Y, cw, ch, cw * 4, rgba)
            {
                DisplayWidth = _width,
                DisplayHeight = _height,
                Extras = SubtitleList.Of(new SubtitleExtra("pgs", "object", pl.ObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            });
        }

        // The new display set ends whatever was showing.
        if (_open is not null)
        {
            _ready.Add(_open with { End = BitmapSupport.ClosedAt(_open, _pcsTime, _open.End, _defects, offset) });
            _open = null;
        }

        if (images.Count == 0)
        {
            return;
        }

        MediaTime end = _pcsDuration.IsKnown ? BitmapSupport.Plus(_pcsTime, _pcsDuration) : MediaTime.Unknown;
        _open = BitmapSubtitleCues.Make(BitmapSubtitleCues.Pgs, _pcsTime, end, images, forced, _language);
    }

    private sealed class PgsPalette
    {
        internal readonly byte[] Y = new byte[256], Cr = new byte[256], Cb = new byte[256], A = new byte[256];
    }

    private sealed class PgsObject
    {
        internal int Width, Height, Expected;
        internal List<byte> Rle = [];
        internal byte[]? Pixels;
    }

    private sealed class Placement
    {
        internal int ObjectId, X, Y, CropX, CropY, CropW, CropH;
        internal bool Crop, Forced;
    }
}
