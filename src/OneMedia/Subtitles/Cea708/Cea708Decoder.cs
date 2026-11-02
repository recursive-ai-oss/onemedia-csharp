using System.Linq;
using System.Text;

namespace RecursiveAI.OneMedia.Subtitles.Cea708;

/// <summary>Where a window is anchored (CTA-708 §8.4.2 anchor point).</summary>
public enum Cea708AnchorPoint
{
    /// <summary>Top left.</summary>
    TopLeft,

    /// <summary>Top centre.</summary>
    TopCenter,

    /// <summary>Top right.</summary>
    TopRight,

    /// <summary>Middle left.</summary>
    MiddleLeft,

    /// <summary>Middle centre.</summary>
    MiddleCenter,

    /// <summary>Middle right.</summary>
    MiddleRight,

    /// <summary>Bottom left.</summary>
    BottomLeft,

    /// <summary>Bottom centre.</summary>
    BottomCenter,

    /// <summary>Bottom right.</summary>
    BottomRight,
}

/// <summary>A run of characters with pen attributes other than the defaults.</summary>
/// <param name="Start">Index of the first character in the row's text.</param>
/// <param name="Length">Number of characters.</param>
/// <param name="Italic">Italics.</param>
/// <param name="Underline">Underline.</param>
/// <param name="Foreground">Foreground colour as 0xRRGGBB scaled from the 2-bit-per-channel code; null when the default (white).</param>
/// <param name="Size">Pen size: 0 small, 1 standard, 2 large.</param>
public readonly record struct Cea708Span(int Start, int Length, bool Italic, bool Underline, int? Foreground, int Size);

/// <summary>One row of text in a window.</summary>
/// <param name="Text">The characters without trailing spaces.</param>
/// <param name="Spans">Styled runs; empty when every character has default attributes.</param>
public sealed record Cea708Row(string Text, IReadOnlyList<Cea708Span> Spans);

/// <summary>A visible window's content.</summary>
/// <param name="Id">Window id 0–7.</param>
/// <param name="AnchorPoint">Which point of the window the anchor names.</param>
/// <param name="RelativePosition">True when <paramref name="AnchorVertical"/> and <paramref name="AnchorHorizontal"/> are percentages (0–99), else grid units (0–74 rows, 0–209 columns on the 16:9 grid).</param>
/// <param name="AnchorVertical">Vertical anchor.</param>
/// <param name="AnchorHorizontal">Horizontal anchor.</param>
/// <param name="Rows">Rows from top to bottom, trailing empty rows removed.</param>
public sealed record Cea708Window(int Id, Cea708AnchorPoint AnchorPoint, bool RelativePosition, int AnchorVertical, int AnchorHorizontal, IReadOnlyList<Cea708Row> Rows);

/// <summary>What one caption service displayed from <see cref="Start"/> to <see cref="End"/>.</summary>
/// <param name="Service">Service number 1–63.</param>
/// <param name="Start">When the content appeared.</param>
/// <param name="End">When it was replaced or erased.</param>
/// <param name="Windows">The visible windows with text, by window id.</param>
public sealed record Cea708Caption(int Service, MediaTime Start, MediaTime End, IReadOnlyList<Cea708Window> Windows)
{
    /// <summary>All rows' text, windows in id order, joined with line feeds.</summary>
    public string Text => string.Join('\n', Windows.SelectMany(w => w.Rows).Select(r => r.Text));
}

/// <summary>
/// Decodes CTA-708 (DTVCC) caption services from the cc_data triplets of types 2 and 3 into caption screens. Windows, pen locations and the
/// pen attributes italics, underline, size and foreground colour are modelled; the rest of the pen and window styling (opacity, edges, fonts
/// beyond the style number, justification, print and scroll direction, effects, delays) is parsed and dropped (see <c>docs/captions.md</c>).
/// </summary>
public sealed class Cea708Decoder
{
    private struct Cell
    {
        public char Ch;
        public uint Attr; // bit 0 italic, 1 underline, 2-3 size, 4 has-colour, 8-13 colour (RGB 2 bits each)
    }

    private sealed class Window
    {
        public bool Defined;
        public bool Visible;
        public int AnchorPoint;
        public bool Relative;
        public int AnchorV;
        public int AnchorH;
        public int RowCount;
        public int ColCount;
        public Cell[][] Cells = [];
        public int Row;
        public int Col;
    }

    private const uint DefaultPen = 4; // standard size, no italics or underline, default colour

    private sealed class Service
    {
        public readonly Window[] Windows = [new(), new(), new(), new(), new(), new(), new(), new()];
        public int Current = -1;
        public uint Pen = DefaultPen;
        public bool Open;
        public MediaTime OpenStart;
        public List<Cea708Window> OpenWindows = [];
    }

    private readonly Dictionary<int, Service> _services = [];
    private readonly List<Cea708Caption> _captions = [];
    private readonly byte[] _packet = new byte[256];
    private int _packetLength;
    private int _packetSize;
    private bool _inPacket;

    /// <summary>The finished caption screens in the order they ended.</summary>
    public IReadOnlyList<Cea708Caption> Captions => _captions;

    /// <summary>Takes one DTVCC triplet.</summary>
    /// <param name="time">When the triplet is processed.</param>
    /// <param name="type">2 for packet data, 3 for a packet start (<c>cc_type</c>).</param>
    /// <param name="data1">First byte.</param>
    /// <param name="data2">Second byte.</param>
    public void Push(MediaTime time, int type, byte data1, byte data2)
    {
        if (type == 3)
        {
            // CTA-708 §5: the first byte holds a 2-bit sequence number and a 6-bit packet_size_code; size 0 means 128 data words (127 + header).
            Flush(time);
            int code = data1 & 0x3f;
            _packetSize = code == 0 ? 127 : (code * 2) - 1;
            _packetLength = 0;
            _inPacket = true;
            Append(data2);
        }
        else if (type == 2 && _inPacket)
        {
            Append(data1);
            Append(data2);
        }
        else
        {
            return;
        }

        if (_packetLength >= _packetSize)
        {
            Flush(time);
        }
    }

    /// <summary>Closes every open screen.</summary>
    /// <param name="end">The end of the stream.</param>
    public void Finish(MediaTime end)
    {
        Flush(end);
        foreach (KeyValuePair<int, Service> kv in _services)
        {
            Close(kv.Key, kv.Value, end);
        }
    }

    private void Append(byte b)
    {
        if (_packetLength < _packet.Length)
        {
            _packet[_packetLength++] = b;
        }
    }

    private void Flush(MediaTime time)
    {
        if (!_inPacket)
        {
            return;
        }

        _inPacket = false;
        ReadOnlySpan<byte> data = _packet.AsSpan(0, Math.Min(_packetLength, _packetSize));
        int pos = 0;
        while (pos < data.Length)
        {
            // Service block header (CTA-708 §6.2): service number (3 bits) and block size (5 bits); service 7 has an extended number byte.
            int header = data[pos++];
            int number = header >> 5;
            int size = header & 0x1f;
            if (number == 0 && size == 0)
            {
                break;
            }

            if (number == 7)
            {
                if (pos >= data.Length)
                {
                    break;
                }

                number = data[pos++] & 0x3f;
            }

            size = Math.Min(size, data.Length - pos);
            if (number >= 1)
            {
                if (!_services.TryGetValue(number, out Service? svc))
                {
                    svc = new Service();
                    _services[number] = svc;
                }

                Run(svc, data.Slice(pos, size));
                Update(number, svc, time);
            }

            pos += size;
        }
    }

    private static void Run(Service s, ReadOnlySpan<byte> d)
    {
        int i = 0;
        while (i < d.Length)
        {
            byte b = d[i++];
            if (b < 0x20)
            {
                i = C0(s, b, d, i);
            }
            else if (b < 0x80)
            {
                PutChar(s, b == 0x7f ? '♪' : (char)b);
            }
            else if (b < 0xa0)
            {
                i = C1(s, b, d, i);
            }
            else
            {
                PutChar(s, (char)b);
            }
        }
    }

    private static int C0(Service s, byte b, ReadOnlySpan<byte> d, int i)
    {
        switch (b)
        {
            case 0x08: // BS
                if (Cur(s) is Window w && w.Col > 0)
                {
                    w.Col--;
                    SetCell(w, w.Row, w.Col, default);
                }

                return i;
            case 0x0c: // FF: clear window, pen to top left
                if (Cur(s) is Window f)
                {
                    ClearWindow(f);
                }

                return i;
            case 0x0d: // CR
                if (Cur(s) is Window c)
                {
                    c.Col = 0;
                    if (c.Row + 1 < c.RowCount)
                    {
                        c.Row++;
                    }
                    else
                    {
                        // Last row: the window scrolls up one row.
                        for (int r = 0; r + 1 < c.Cells.Length; r++)
                        {
                            c.Cells[r] = c.Cells[r + 1];
                        }

                        if (c.Cells.Length > 0)
                        {
                            c.Cells[^1] = new Cell[Math.Max(c.ColCount, 1)];
                        }
                    }
                }

                return i;
            case 0x0e: // HCR: horizontal carriage return, clears the row
                if (Cur(s) is Window h)
                {
                    h.Col = 0;
                    if (h.Row < h.Cells.Length)
                    {
                        h.Cells[h.Row] = new Cell[Math.Max(h.ColCount, 1)];
                    }
                }

                return i;
            case 0x10: // EXT1: the next byte is from C2, G2, C3 or G3
                return Ext(s, d, i);
            case 0x18: // P16
                return Math.Min(d.Length, i + 2);
        }

        // C0 codes 0x11–0x17 carry one extra byte, 0x19–0x1f two, 0x00–0x0f none (CTA-708 §7.1.1).
        return b switch { >= 0x11 and <= 0x17 => Math.Min(d.Length, i + 1), >= 0x19 => Math.Min(d.Length, i + 2), _ => i };
    }

    private static int Ext(Service s, ReadOnlySpan<byte> d, int i)
    {
        if (i >= d.Length)
        {
            return i;
        }

        byte b = d[i++];
        if (b < 0x20)
        {
            return Math.Min(d.Length, i + (b >> 3)); // C2: 0–3 extra bytes by range
        }

        if (b < 0x80)
        {
            PutChar(s, G2(b));
            return i;
        }

        if (b < 0xa0)
        {
            return Math.Min(d.Length, i + (b < 0x88 ? 4 : 5)); // C3
        }

        PutChar(s, b == 0xa0 ? '¢' : '?'); // G3 0xA0 is the [CC] icon; shown as a placeholder
        return i;
    }

    private static char G2(byte b) => b switch
    {
        0x20 or 0x21 => ' ',
        0x25 => '…',
        0x2a => 'Š',
        0x2c => 'Œ',
        0x30 => '█',
        0x31 => '‘',
        0x32 => '’',
        0x33 => '“',
        0x34 => '”',
        0x35 => '•',
        0x39 => '™',
        0x3a => 'š',
        0x3c => 'œ',
        0x3d => '℠',
        0x3f => 'Ÿ',
        0x76 => '⅛',
        0x77 => '⅜',
        0x78 => '⅝',
        0x79 => '⅞',
        0x7a => '│',
        0x7b => '┐',
        0x7c => '└',
        0x7d => '─',
        0x7e => '┘',
        0x7f => '┌',
        _ => '_',
    };

    private static int C1(Service s, byte b, ReadOnlySpan<byte> d, int i)
    {
        int remaining = d.Length - i;
        if (b <= 0x87)
        {
            // CWn: set current window
            s.Current = b - 0x80;
            return i;
        }

        switch (b)
        {
            case 0x88 or 0x89 or 0x8a or 0x8b or 0x8c: // CLW DSW HDW TGW DLW: one bitmap byte
                {
                    if (i >= d.Length)
                    {
                        return i;
                    }

                    int map = d[i];
                    for (int w = 0; w < 8; w++)
                    {
                        if ((map & (1 << w)) == 0)
                        {
                            continue;
                        }

                        Window win = s.Windows[w];
                        switch (b)
                        {
                            case 0x88:
                                if (win.Defined)
                                {
                                    ClearWindow(win);
                                }

                                break;
                            case 0x89:
                                win.Visible = win.Defined;
                                break;
                            case 0x8a:
                                win.Visible = false;
                                break;
                            case 0x8b:
                                win.Visible = win.Defined && !win.Visible;
                                break;
                            default:
                                s.Windows[w] = new Window();
                                if (s.Current == w)
                                {
                                    s.Current = -1;
                                }

                                break;
                        }
                    }

                    return i + 1;
                }

            case 0x8d: // DLY
                return i + Math.Min(1, remaining);
            case 0x8e: // DLC
                return i;
            case 0x8f: // RST
                for (int w = 0; w < 8; w++)
                {
                    s.Windows[w] = new Window();
                }

                s.Current = -1;
                s.Pen = DefaultPen;
                return i;
            case 0x90: // SPA: pen attributes (2 bytes)
                {
                    if (i + 2 > d.Length)
                    {
                        return d.Length;
                    }

                    uint size = (uint)(d[i] & 3);
                    uint pen = s.Pen & ~0xcu & ~3u;
                    pen |= size << 2;
                    pen |= (d[i + 1] & 0x80) != 0 ? 1u : 0;
                    pen |= (d[i + 1] & 0x40) != 0 ? 2u : 0;
                    s.Pen = pen;
                    return i + 2;
                }

            case 0x91: // SPC: pen colour (3 bytes): foreground, background, edge
                {
                    if (i + 3 > d.Length)
                    {
                        return d.Length;
                    }

                    // foreground: opacity (2 bits) then red, green, blue (2 bits each).
                    uint rgb = (uint)(d[i] & 0x3f);
                    s.Pen = (s.Pen & ~0x3f10u) | 0x10u | (rgb << 8);
                    return i + 3;
                }

            case 0x92: // SPL: pen location row, column
                {
                    if (i + 2 > d.Length)
                    {
                        return d.Length;
                    }

                    if (Cur(s) is Window w)
                    {
                        w.Row = Math.Min(d[i] & 0x0f, Math.Max(w.RowCount - 1, 0));
                        w.Col = Math.Min(d[i + 1] & 0x3f, Math.Max(w.ColCount - 1, 0));
                    }

                    return i + 2;
                }

            case 0x97: // SWA: window attributes (4 bytes)
                return i + Math.Min(4, remaining);
            case >= 0x98 and <= 0x9f: // DF0–DF7: define window (6 bytes)
                {
                    if (i + 6 > d.Length)
                    {
                        return d.Length;
                    }

                    Define(s, b - 0x98, d.Slice(i, 6));
                    return i + 6;
                }

            default:
                return i; // reserved C1 codes
        }
    }

    private static void Define(Service s, int id, ReadOnlySpan<byte> p)
    {
        Window w = s.Windows[id];
        bool fresh = !w.Defined;
        w.Visible = (p[0] & 0x20) != 0;
        w.Relative = (p[1] & 0x80) != 0;
        w.AnchorV = p[1] & 0x7f;
        w.AnchorH = p[2];
        w.AnchorPoint = Math.Min(p[3] >> 4, 8);
        int rows = (p[3] & 0x0f) + 1;
        int cols = (p[4] & 0x3f) + 1;
        if (fresh || rows != w.RowCount || cols != w.ColCount)
        {
            Cell[][] old = w.Cells;
            w.Cells = new Cell[rows][];
            for (int r = 0; r < rows; r++)
            {
                w.Cells[r] = r < old.Length && old[r].Length >= cols ? old[r] : new Cell[cols];
            }
        }

        w.RowCount = rows;
        w.ColCount = cols;
        if (fresh)
        {
            w.Row = 0;
            w.Col = 0;
        }

        w.Defined = true;
        s.Current = id;
    }

    private static Window? Cur(Service s) => s.Current is >= 0 and < 8 && s.Windows[s.Current].Defined ? s.Windows[s.Current] : null;

    private static void ClearWindow(Window w)
    {
        for (int r = 0; r < w.Cells.Length; r++)
        {
            Array.Clear(w.Cells[r]);
        }

        w.Row = 0;
        w.Col = 0;
    }

    private static void SetCell(Window w, int row, int col, Cell cell)
    {
        if (row >= 0 && row < w.Cells.Length && col >= 0 && col < w.Cells[row].Length)
        {
            w.Cells[row][col] = cell;
        }
    }

    private static void PutChar(Service s, char c)
    {
        if (Cur(s) is not Window w)
        {
            return;
        }

        if (c == 0x03)
        {
            return;
        }

        SetCell(w, w.Row, w.Col, new Cell { Ch = c, Attr = s.Pen });
        if (w.Col < w.ColCount - 1)
        {
            w.Col++;
        }
    }

    private void Update(int number, Service s, MediaTime time)
    {
        var wins = new List<Cea708Window>();
        for (int id = 0; id < 8; id++)
        {
            Window w = s.Windows[id];
            if (!w.Defined || !w.Visible)
            {
                continue;
            }

            var rows = new List<Cea708Row>();
            for (int r = 0; r < w.Cells.Length; r++)
            {
                rows.Add(MakeRow(w.Cells[r]));
            }

            while (rows.Count > 0 && rows[^1].Text.Length == 0)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            if (rows.Any(r => r.Text.Length > 0))
            {
                wins.Add(new Cea708Window(id, (Cea708AnchorPoint)w.AnchorPoint, w.Relative, w.AnchorV, w.AnchorH, rows));
            }
        }

        if (wins.Count == 0)
        {
            Close(number, s, time);
            return;
        }

        if (s.Open)
        {
            if (Same(s.OpenWindows, wins))
            {
                return;
            }

            if (Extends(s.OpenWindows, wins))
            {
                s.OpenWindows = wins;
                return;
            }

            Close(number, s, time);
        }

        s.Open = true;
        s.OpenStart = time;
        s.OpenWindows = wins;
    }

    private void Close(int number, Service s, MediaTime end)
    {
        if (!s.Open)
        {
            return;
        }

        _captions.Add(new Cea708Caption(number, s.OpenStart, end, s.OpenWindows));
        s.Open = false;
        s.OpenWindows = [];
    }

    private static bool Same(List<Cea708Window> a, List<Cea708Window> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id || a[i].Rows.Count != b[i].Rows.Count || a[i].AnchorVertical != b[i].AnchorVertical || a[i].AnchorHorizontal != b[i].AnchorHorizontal)
            {
                return false;
            }

            for (int r = 0; r < a[i].Rows.Count; r++)
            {
                if (a[i].Rows[r].Text != b[i].Rows[r].Text || !a[i].Rows[r].Spans.SequenceEqual(b[i].Rows[r].Spans))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool Extends(List<Cea708Window> open, List<Cea708Window> now)
    {
        foreach (Cea708Window o in open)
        {
            Cea708Window? n = now.FirstOrDefault(x => x.Id == o.Id);
            if (n is null || n.AnchorVertical != o.AnchorVertical || n.AnchorHorizontal != o.AnchorHorizontal || n.Rows.Count < o.Rows.Count)
            {
                return false;
            }

            for (int r = 0; r < o.Rows.Count; r++)
            {
                if (!n.Rows[r].Text.StartsWith(o.Rows[r].Text, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static Cea708Row MakeRow(Cell[] cells)
    {
        int last = -1;
        for (int c = 0; c < cells.Length; c++)
        {
            if (cells[c].Ch != 0 && cells[c].Ch != ' ')
            {
                last = c;
            }
        }

        if (last < 0)
        {
            return new Cea708Row(string.Empty, []);
        }

        var sb = new StringBuilder(last + 1);
        var spans = new List<Cea708Span>();
        int start = 0;
        uint attr = DefaultPen;
        for (int c = 0; c <= last; c++)
        {
            uint a = cells[c].Ch == 0 || cells[c].Ch == ' ' ? attr : cells[c].Attr;
            sb.Append(cells[c].Ch == 0 ? ' ' : cells[c].Ch);
            if (a != attr)
            {
                AddSpan(spans, start, c - start, attr);
                start = c;
                attr = a;
            }
        }

        AddSpan(spans, start, last + 1 - start, attr);
        return new Cea708Row(sb.ToString(), spans);
    }

    private static void AddSpan(List<Cea708Span> spans, int start, int length, uint attr)
    {
        // Size 1 (standard) and no flags is the default and is not a span.
        if (attr == DefaultPen)
        {
            return;
        }

        int? fg = (attr & 0x10) != 0 ? ScaleColor((attr >> 8) & 0x3f) : null;
        spans.Add(new Cea708Span(start, length, (attr & 1) != 0, (attr & 2) != 0, fg, (int)((attr >> 2) & 3)));
    }

    private static int ScaleColor(uint rgb2)
    {
        int r = (int)((rgb2 >> 4) & 3) * 85;
        int g = (int)((rgb2 >> 2) & 3) * 85;
        int b = (int)(rgb2 & 3) * 85;
        return (r << 16) | (g << 8) | b;
    }
}
