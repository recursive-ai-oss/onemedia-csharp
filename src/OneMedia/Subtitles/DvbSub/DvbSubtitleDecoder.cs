namespace RecursiveAI.OneMedia.Captions;

// DVB subtitles, ETSI EN 300 743 (V1.6.1). Segment syntax: clause 7.2; pixel code strings: 7.2.5.2; default CLUTs and map tables: clause 10.

/// <summary>
/// Decodes the subtitle stream of an MPEG transport stream PID (ETSI EN 300 743) into image cues. Feed the payload of each PES packet
/// with its presentation time; a display set that shows regions opens a cue, which ends at the next display set or after the page's
/// time-out, whichever comes first. One cue holds one image per region. Not thread-safe.
/// </summary>
public sealed class DvbSubtitleDecoder
{
    private readonly MediaLimits _limits;
    private readonly DefectLog _defects;
    private readonly YCbCrMatrix _matrix;
    private readonly string? _language;
    private readonly int? _wantedPage;
    private readonly int? _ancillaryPage;
    private readonly List<SubtitleCue> _ready = [];
    private readonly Dictionary<int, Region> _regions = [];
    private readonly Dictionary<int, Clut> _cluts = [];
    private readonly Dictionary<int, DvbObject> _objects = [];
    private readonly HashSet<int> _compositionPages = [];

    private int? _page;
    private List<(int Id, int X, int Y)> _pageRegions = [];
    private int _timeOut = 5;
    private bool _pageSeen;
    private MediaTime _pageTime;
    private int _displayW = 720, _displayH = 576, _windowX, _windowY;
    private long _objectBytes;
    private SubtitleCue? _open;
    private MediaTime _openTimeOut;

    /// <summary>Creates a decoder.</summary>
    /// <param name="options">Page selection, matrix, limits and language; null for the defaults.</param>
    /// <param name="defects">Receives every deviation found; null to discard them.</param>
    public DvbSubtitleDecoder(BitmapSubtitleOptions? options = null, DefectLog? defects = null)
    {
        options ??= new BitmapSubtitleOptions();
        _limits = options.Limits ?? MediaLimits.Default;
        _defects = defects ?? new DefectLog(_limits);
        _matrix = options.Matrix;
        _language = options.Language;
        _wantedPage = options.CompositionPageId;
        _ancillaryPage = options.AncillaryPageId;
    }

    /// <summary>Feeds the data of one PES packet: <c>data_identifier</c> 0x20, <c>subtitle_stream_id</c> 0, segments, and the 0xFF end marker.</summary>
    /// <param name="pes">The PES payload (without the PES header).</param>
    /// <param name="time">The PES presentation time.</param>
    /// <param name="offset">Source offset for defect positions.</param>
    /// <returns>The cues completed by this call; the one still showing is held until the next display set or <see cref="Flush"/>.</returns>
    /// <exception cref="MediaLimitExceededException">A region or the stored objects would exceed <see cref="MediaLimits.MaxAllocationBytes"/>.</exception>
    public IReadOnlyList<SubtitleCue> Feed(ReadOnlySpan<byte> pes, MediaTime time, long offset = 0)
    {
        _ready.Clear();
        int p = 0;
        if (pes.Length >= 2 && pes[0] == 0x20)
        {
            p = 2; // data_identifier, subtitle_stream_id
        }
        else if (pes.Length >= 1 && pes[0] != 0x0F)
        {
            _defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, "DVB subtitle PES data does not start with data_identifier 0x20.", "dvbsub.data_identifier");
        }

        while (p + 6 <= pes.Length)
        {
            if (pes[p] != 0x0F)
            {
                if (pes[p] != 0xFF)
                {
                    _defects.Add(DefectKind.InvalidValue, DefectSeverity.Error, offset + p, "DVB subtitle segment without the sync byte 0x0F.", "dvbsub.sync");
                }

                break;
            }

            int type = pes[p + 1];
            int page = (pes[p + 2] << 8) | pes[p + 3];
            int len = (pes[p + 4] << 8) | pes[p + 5];
            if (p + 6 + len > pes.Length)
            {
                _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset + p, "A DVB subtitle segment runs past its PES packet.", "dvbsub.segment.truncated");
                break;
            }

            Segment(type, page, pes.Slice(p + 6, len), time, offset + p);
            p += 6 + len;
        }

        return _ready.ToArray();
    }

    /// <summary>Ends the stream and returns the cue still showing, if any, ending after the page time-out.</summary>
    public IReadOnlyList<SubtitleCue> Flush()
    {
        if (_open is null)
        {
            return [];
        }

        SubtitleCue c = _open with { End = _openTimeOut };
        _open = null;
        return [c];
    }

    private bool Accept(int type, int page)
    {
        if (type is 0x10 or 0x11)
        {
            _compositionPages.Add(page);
            _page ??= _wantedPage ?? page;
            return page == _page;
        }

        if (page == _page || page == _ancillaryPage)
        {
            return true;
        }

        // Data in a page nobody composes (the ancillary page) is shared by every service unless the caller named it.
        return _ancillaryPage is null && !_compositionPages.Contains(page);
    }

    private void Segment(int type, int page, ReadOnlySpan<byte> s, MediaTime time, long offset)
    {
        if (type == 0x80)
        {
            EndOfDisplaySet(offset);
            return;
        }

        if (type is not (0x10 or 0x11 or 0x12 or 0x13 or 0x14) || !Accept(type, page))
        {
            return; // Disparity, alternative CLUT, private and reserved segments are ignored (7.2.0.1: unknown segments shall be ignored).
        }

        switch (type)
        {
            case 0x14:
                if (s.Length >= 5)
                {
                    _displayW = ((s[1] << 8) | s[2]) + 1;
                    _displayH = ((s[3] << 8) | s[4]) + 1;
                    _windowX = _windowY = 0;
                    if ((s[0] & 0x08) != 0 && s.Length >= 13)
                    {
                        _windowX = (s[5] << 8) | s[6];
                        _windowY = (s[9] << 8) | s[10];
                    }
                }

                break;
            case 0x10:
                PageComposition(s, time, offset);
                break;
            case 0x11:
                RegionComposition(s, offset);
                break;
            case 0x12:
                ClutDefinition(s, offset);
                break;
            case 0x13:
                ObjectData(s, offset);
                break;
        }
    }

    private void PageComposition(ReadOnlySpan<byte> s, MediaTime time, long offset)
    {
        if (s.Length < 2)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "DVB page composition segment too short.", "dvbsub.pcs.short");
            return;
        }

        _timeOut = s[0];
        int state = (s[1] >> 2) & 3;
        if (state == 2)
        {
            // Mode change: a new epoch starts from default CLUTs and no regions or objects.
            _regions.Clear();
            _cluts.Clear();
            _objects.Clear();
            _objectBytes = 0;
        }

        _pageRegions = [];
        for (int p = 2; p + 6 <= s.Length; p += 6)
        {
            _pageRegions.Add((s[p], (s[p + 2] << 8) | s[p + 3], (s[p + 4] << 8) | s[p + 5]));
        }

        _pageTime = time;
        _pageSeen = true;
    }

    private void RegionComposition(ReadOnlySpan<byte> s, long offset)
    {
        if (s.Length < 10)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "DVB region composition segment too short.", "dvbsub.rcs.short");
            return;
        }

        var r = new Region
        {
            Id = s[0],
            Fill = (s[1] & 0x08) != 0,
            Width = (s[2] << 8) | s[3],
            Height = (s[4] << 8) | s[5],
            Depth = (s[6] >> 2) & 7,
            ClutId = s[7],
            Code8 = s[8],
            Code4 = s[9] >> 4,
            Code2 = (s[9] >> 2) & 3,
        };
        if (r.Depth is < 1 or > 3)
        {
            _defects.Add(DefectKind.ReservedValue, DefectSeverity.Error, offset, "DVB region with a reserved pixel depth; region ignored.", "dvbsub.rcs.depth");
            return;
        }

        if (r.Width == 0 || r.Height == 0)
        {
            _defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, offset, "DVB region with a zero dimension; region ignored.", "dvbsub.rcs.size");
            return;
        }

        BitmapSupport.CheckImage(_limits, r.Width, r.Height, 0, offset);
        for (int p = 10; p + 6 <= s.Length;)
        {
            int id = (s[p] << 8) | s[p + 1];
            int type = s[p + 2] >> 6;
            int x = ((s[p + 2] & 0x0F) << 8) | s[p + 3];
            int y = ((s[p + 4] & 0x0F) << 8) | s[p + 5];
            p += 6;
            if (type is 1 or 2)
            {
                p += 2; // foreground and background pixel codes of a character object; character objects are not decoded
            }

            if (r.Objects.Count < 1024)
            {
                r.Objects.Add((id, type, x, y));
            }
        }

        _regions[r.Id] = r;
    }

    private void ClutDefinition(ReadOnlySpan<byte> s, long offset)
    {
        if (s.Length < 2)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "DVB CLUT definition segment too short.", "dvbsub.cds.short");
            return;
        }

        int id = s[0];
        if (!_cluts.TryGetValue(id, out Clut? clut))
        {
            clut = Clut.Defaults();
            _cluts[id] = clut;
        }

        // EN 300 743 7.2.4 note 2 defines Y, Cr, Cb as ITU-R BT.601 whatever the display size, and ffmpeg agrees (observed on 1080-line streams), so Auto means BT.601 here.
        bool bt709 = _matrix == YCbCrMatrix.Bt709;
        int p = 2;
        while (p + 2 <= s.Length)
        {
            int entry = s[p];
            int flags = s[p + 1];
            p += 2;
            int y, cr, cb, t;
            if ((flags & 1) != 0)
            {
                if (p + 4 > s.Length)
                {
                    _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "DVB CLUT entry runs past its segment.", "dvbsub.cds.entry");
                    break;
                }

                y = s[p];
                cr = s[p + 1];
                cb = s[p + 2];
                t = s[p + 3];
                p += 4;
            }
            else
            {
                if (p + 2 > s.Length)
                {
                    _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "DVB CLUT entry runs past its segment.", "dvbsub.cds.entry");
                    break;
                }

                // Reduced range: the most significant bits only (7.2.4); expanded by shifting.
                y = (s[p] >> 2) << 2;
                cr = (((s[p] & 3) << 2) | (s[p + 1] >> 6)) << 4;
                cb = ((s[p + 1] >> 2) & 0xF) << 4;
                t = (s[p + 1] & 3) << 6;
                p += 2;
            }

            // 7.2.4: a Y of zero is full transparency; T is transparency, 0 meaning none. Y = 0 is not a legal BT.601 value, so it is raised to 16 only for colour.
            SubtitleColor c = y == 0 ? default : BitmapSupport.FromYCbCr(y, cb, cr, 255 - t, bt709);
            if ((flags & 0x80) != 0 && entry < 4)
            {
                clut.C2[entry] = c;
            }

            if ((flags & 0x40) != 0 && entry < 16)
            {
                clut.C4[entry] = c;
            }

            if ((flags & 0x20) != 0)
            {
                clut.C8[entry] = c;
            }
        }
    }

    private void ObjectData(ReadOnlySpan<byte> s, long offset)
    {
        if (s.Length < 3)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "DVB object data segment too short.", "dvbsub.ods.short");
            return;
        }

        int id = (s[0] << 8) | s[1];
        int method = (s[2] >> 2) & 3;
        if (method != 0)
        {
            _defects.Add(DefectKind.Unsupported, DefectSeverity.Info, offset, $"DVB object coding method {method} (characters or progressive pixels) is not decoded.", "dvbsub.ods.method");
            return;
        }

        if (s.Length < 7)
        {
            _defects.Add(DefectKind.Truncated, DefectSeverity.Error, offset, "DVB object data segment too short.", "dvbsub.ods.short");
            return;
        }

        int topLen = (s[3] << 8) | s[4];
        int botLen = (s[5] << 8) | s[6];
        if (7 + topLen > s.Length)
        {
            _defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Error, offset, "DVB top field length runs past the segment.", "dvbsub.ods.top");
            topLen = s.Length - 7;
        }

        if (7 + topLen + botLen > s.Length)
        {
            _defects.Add(DefectKind.SizeExceedsParent, DefectSeverity.Error, offset, "DVB bottom field length runs past the segment.", "dvbsub.ods.bottom");
            botLen = s.Length - 7 - topLen;
        }

        long old = _objects.TryGetValue(id, out DvbObject? prev) ? prev.Top.Length + prev.Bottom.Length : 0;
        if (_objectBytes - old + topLen + botLen > _limits.MaxAllocationBytes)
        {
            throw new MediaLimitExceededException("MaxAllocationBytes", _limits.MaxAllocationBytes, _objectBytes + topLen + botLen, offset);
        }

        _objectBytes += topLen + botLen - old;
        // A bottom field of length zero means the top field data is repeated for the bottom lines (7.2.5.1, progressive-in-one-field objects).
        _objects[id] = new DvbObject(s.Slice(7, topLen).ToArray(), s.Slice(7 + topLen, botLen).ToArray(), (s[2] & 0x02) != 0);
    }

    private void EndOfDisplaySet(long offset)
    {
        if (!_pageSeen)
        {
            return;
        }

        _pageSeen = false;
        var images = new List<SubtitleImage>();
        long bytes = 0;
        foreach ((int id, int px, int py) in _pageRegions)
        {
            if (!_regions.TryGetValue(id, out Region? region))
            {
                _defects.Add(DefectKind.MissingRequired, DefectSeverity.Warning, offset, $"DVB page names region {id}, which has no composition.", "dvbsub.region.missing");
                continue;
            }

            BitmapSupport.CheckImage(_limits, region.Width, region.Height, bytes, offset);
            bytes += 4L * region.Width * region.Height;
            if (!_cluts.TryGetValue(region.ClutId, out Clut? clut))
            {
                clut = Clut.Defaults();
                _cluts[region.ClutId] = clut;
            }

            byte[] rgba = new byte[4 * region.Width * region.Height];
            if (region.Fill)
            {
                SubtitleColor fill = region.Depth switch { 1 => clut.C2[region.Code2], 2 => clut.C4[region.Code4], _ => clut.C8[region.Code8] };
                for (int i = 0; i < region.Width * region.Height; i++)
                {
                    BitmapSupport.Put(rgba, i, fill);
                }
            }

            foreach ((int objId, int type, int ox, int oy) in region.Objects)
            {
                if (type == 0 && _objects.TryGetValue(objId, out DvbObject? obj))
                {
                    DrawObject(obj, region, clut, rgba, ox, oy, offset);
                }
            }

            images.Add(new SubtitleImage(_windowX + px, _windowY + py, region.Width, region.Height, region.Width * 4, rgba)
            {
                DisplayWidth = _displayW,
                DisplayHeight = _displayH,
                Extras = SubtitleList.Of(new SubtitleExtra("dvbsub", "region", id.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            });
        }

        if (_open is not null)
        {
            _ready.Add(_open with { End = BitmapSupport.ClosedAt(_open, _pageTime, _openTimeOut, _defects, offset) });
            _open = null;
        }

        if (images.Count > 0)
        {
            MediaTime timeOut = BitmapSupport.Plus(_pageTime, new MediaTime(_timeOut, 1));
            _open = BitmapSubtitleCues.Make(BitmapSubtitleCues.DvbSub, _pageTime, timeOut, images, false, _language);
            _openTimeOut = timeOut;
        }
    }

    private void DrawObject(DvbObject obj, Region region, Clut clut, byte[] rgba, int ox, int oy, long offset)
    {
        DrawField(obj.Top, region, clut, rgba, ox, oy, offset, obj.NonModifying);
        // 7.2.5.1: when the bottom field block has length zero, the top field data serves both fields.
        DrawField(obj.Bottom.Length == 0 ? obj.Top : obj.Bottom, region, clut, rgba, ox, oy + 1, offset, obj.NonModifying);
    }

    private void DrawField(byte[] field, Region region, Clut clut, byte[] rgba, int ox, int firstY, long offset, bool nonModifying)
    {
        int y = firstY;
        int x = ox;
        var map2to4 = new byte[] { 0, 7, 8, 15 };
        var map2to8 = new byte[] { 0, 0x77, 0x88, 0xFF };
        byte[]? map4to8 = null;
        int p = 0;
        while (p < field.Length)
        {
            int dataType = field[p++];
            switch (dataType)
            {
                case 0x10:
                case 0x11:
                case 0x12:
                    {
                        int bits = dataType == 0x10 ? 2 : dataType == 0x11 ? 4 : 8;
                        var reader = new BitReader(field, p);
                        while (DecodeString(ref reader, bits, out int code, out int run))
                        {
                            int regionBits = region.Depth == 1 ? 2 : region.Depth == 2 ? 4 : 8;
                            int mapped = bits == regionBits ? code : MapCode(code, bits, region.Depth, map2to4, map2to8, map4to8);
                            Paint(rgba, region, clut, x, y, run, mapped, nonModifying);
                            x += run;
                        }

                        p = reader.AlignedPosition;
                        break;
                    }

                case 0x20 when p + 2 <= field.Length:
                    map2to4 = [(byte)(field[p] >> 4), (byte)(field[p] & 0xF), (byte)(field[p + 1] >> 4), (byte)(field[p + 1] & 0xF)];
                    p += 2;
                    break;
                case 0x21 when p + 4 <= field.Length:
                    map2to8 = field.AsSpan(p, 4).ToArray();
                    p += 4;
                    break;
                case 0x22 when p + 16 <= field.Length:
                    map4to8 = field.AsSpan(p, 16).ToArray();
                    p += 16;
                    break;
                case 0xF0:
                    x = ox;
                    y += 2;
                    break;
                default:
                    _defects.Add(DefectKind.ReservedValue, DefectSeverity.Warning, offset, $"Unknown DVB pixel-data sub-block type 0x{dataType:x2}; the rest of the field is skipped.", "dvbsub.pixel.type");
                    return;
            }
        }
    }

    private static int MapCode(int code, int bits, int regionDepth, byte[] map2to4, byte[] map2to8, byte[]? map4to8)
    {
        int regionBits = regionDepth == 1 ? 2 : regionDepth == 2 ? 4 : 8;
        if (bits > regionBits)
        {
            return code & ((1 << regionBits) - 1); // More bits than the region's depth: keep the low bits.
        }

        return (bits, regionBits) switch
        {
            (2, 4) => map2to4[code & 3],
            (2, 8) => map2to8[code & 3],
            (4, 8) => map4to8 is null ? code : map4to8[code & 15],
            _ => code,
        };
    }

    private static void Paint(byte[] rgba, Region region, Clut clut, int x, int y, int run, int code, bool nonModifying)
    {
        if (y < 0 || y >= region.Height || x >= region.Width || run <= 0)
        {
            return;
        }

        int n = Math.Min(run, region.Width - x);
        if (x < 0)
        {
            n += x;
            x = 0;
        }

        if (n <= 0 || (nonModifying && code == 1))
        {
            return;
        }

        SubtitleColor c = region.Depth switch { 1 => clut.C2[code & 3], 2 => clut.C4[code & 15], _ => clut.C8[code & 255] };
        int start = (y * region.Width) + x;
        for (int i = 0; i < n; i++)
        {
            BitmapSupport.Put(rgba, start + i, c);
        }
    }

    // Reads one pixel code string element (7.2.5.2.1-3). Returns false at the end of the string.
    private static bool DecodeString(ref BitReader r, int bits, out int code, out int run)
    {
        code = 0;
        run = 1;
        if (r.Remaining < bits)
        {
            return false;
        }

        if (bits == 2)
        {
            int c = r.Read(2);
            if (c != 0)
            {
                code = c;
                return true;
            }

            if (r.Read(1) == 1)
            {
                run = r.Read(3) + 3;
                code = r.Read(2);
                return true;
            }

            if (r.Read(1) == 1)
            {
                run = 1; // one pixel of code 0
                return true;
            }

            switch (r.Read(2))
            {
                case 0:
                    return false;
                case 1:
                    run = 2;
                    return true;
                case 2:
                    run = r.Read(4) + 12;
                    code = r.Read(2);
                    return true;
                default:
                    run = r.Read(8) + 29;
                    code = r.Read(2);
                    return true;
            }
        }

        if (bits == 4)
        {
            int c = r.Read(4);
            if (c != 0)
            {
                code = c;
                return true;
            }

            if (r.Read(1) == 0)
            {
                int n = r.Read(3);
                if (n == 0)
                {
                    return false;
                }

                run = n + 2;
                return true;
            }

            if (r.Read(1) == 0)
            {
                run = r.Read(2) + 4;
                code = r.Read(4);
                return true;
            }

            switch (r.Read(2))
            {
                case 0:
                    run = 1;
                    return true;
                case 1:
                    run = 2;
                    return true;
                case 2:
                    run = r.Read(4) + 9;
                    code = r.Read(4);
                    return true;
                default:
                    run = r.Read(8) + 25;
                    code = r.Read(4);
                    return true;
            }
        }

        int v = r.Read(8);
        if (v != 0)
        {
            code = v;
            return true;
        }

        if (r.Read(1) == 0)
        {
            int n = r.Read(7);
            if (n == 0)
            {
                return false;
            }

            run = n;
            return true;
        }

        run = r.Read(7);
        code = r.Read(8);
        return true;
    }

    // MSB-first bit reader over a byte array; reads past the end return zero bits.
    private struct BitReader
    {
        private readonly byte[] _d;
        private int _bit;

        internal BitReader(byte[] data, int bytePosition)
        {
            _d = data;
            _bit = bytePosition * 8;
        }

        internal readonly int Remaining => (_d.Length * 8) - _bit;

        internal readonly int AlignedPosition => Math.Min(_d.Length, (_bit + 7) / 8);

        internal int Read(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++)
            {
                int byteIndex = _bit >> 3;
                int bit = byteIndex < _d.Length ? (_d[byteIndex] >> (7 - (_bit & 7))) & 1 : 0;
                v = (v << 1) | bit;
                _bit++;
            }

            return v;
        }
    }

    private sealed class Region
    {
        internal int Id, Width, Height, Depth, ClutId, Code8, Code4, Code2;
        internal bool Fill;
        internal List<(int Id, int Type, int X, int Y)> Objects = [];
    }

    private sealed record DvbObject(byte[] Top, byte[] Bottom, bool NonModifying);

    // Colour tables of one CLUT family, starting from the default contents of clause 10.
    private sealed class Clut
    {
        internal readonly SubtitleColor[] C2 = new SubtitleColor[4];
        internal readonly SubtitleColor[] C4 = new SubtitleColor[16];
        internal readonly SubtitleColor[] C8 = new SubtitleColor[256];

        internal static Clut Defaults()
        {
            var c = new Clut();
            // 10.3: 4-entry CLUT.
            c.C2[0] = default;
            c.C2[1] = new SubtitleColor(255, 255, 255);
            c.C2[2] = new SubtitleColor(0, 0, 0);
            c.C2[3] = new SubtitleColor(127, 127, 127);

            // 10.2: 16-entry CLUT; b1 is the most significant bit.
            for (int i = 0; i < 16; i++)
            {
                int b1 = (i >> 3) & 1, b2 = (i >> 2) & 1, b3 = (i >> 1) & 1, b4 = i & 1;
                if (b1 == 0)
                {
                    c.C4[i] = (b2 | b3 | b4) == 0 ? default : new SubtitleColor((byte)(255 * b4), (byte)(255 * b3), (byte)(255 * b2));
                }
                else
                {
                    c.C4[i] = new SubtitleColor((byte)(127 * b4), (byte)(127 * b3), (byte)(127 * b2));
                }
            }

            // 10.1: 256-entry CLUT. Intensities are given in thirds and sixths of full scale; transparency of 50 % is alpha 127.
            for (int i = 0; i < 256; i++)
            {
                int b1 = (i >> 7) & 1, b2 = (i >> 6) & 1, b3 = (i >> 5) & 1, b4 = (i >> 4) & 1, b5 = (i >> 3) & 1, b6 = (i >> 2) & 1, b7 = (i >> 1) & 1, b8 = i & 1;
                static byte Level(int low, int high, int baseLevel) => (byte)Math.Min(255, ((255 * low) / 3) + ((255 * 2 * high) / 3) + baseLevel);
                static byte Level2(int low, int mid, int baseLevel) => (byte)Math.Min(255, ((255 * low) / 6) + ((255 * mid) / 3) + baseLevel);
                if (b1 == 0 && b5 == 0)
                {
                    if (b2 == 0 && b3 == 0 && b4 == 0)
                    {
                        c.C8[i] = (b6 | b7 | b8) == 0 ? default : new SubtitleColor((byte)(255 * b8), (byte)(255 * b7), (byte)(255 * b6), 64); // 75 % transparent
                    }
                    else
                    {
                        c.C8[i] = new SubtitleColor(Level(b8, b4, 0), Level(b7, b3, 0), Level(b6, b2, 0));
                    }
                }
                else if (b1 == 0)
                {
                    c.C8[i] = new SubtitleColor(Level(b8, b4, 0), Level(b7, b3, 0), Level(b6, b2, 0), 127); // 50 % transparent
                }
                else if (b5 == 0)
                {
                    c.C8[i] = new SubtitleColor(Level2(b8, b4, 127), Level2(b7, b3, 127), Level2(b6, b2, 127));
                }
                else
                {
                    c.C8[i] = new SubtitleColor(Level2(b8, b4, 0), Level2(b7, b3, 0), Level2(b6, b2, 0));
                }
            }

            return c;
        }
    }
}
