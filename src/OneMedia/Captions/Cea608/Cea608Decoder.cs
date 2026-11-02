using System.Linq;
using System.Text;

namespace RecursiveAI.OneMedia.Captions.Cea608;

/// <summary>
/// Decodes CEA-608 byte pairs into caption screens: pop-on, roll-up (2–4 rows) and paint-on captions on CC1–CC4, text services Text1–Text4
/// and XDS packets. Feed pairs in presentation order with <see cref="Push"/>, then call <see cref="Finish"/>.
/// </summary>
/// <remarks>
/// Convention for rows that grow after they appear (roll-up and paint-on typing): a new screen that only extends the open screen's rows
/// (every row of the open screen is a prefix of the same row now) is merged into it, so a typed line is one caption, not one per character.
/// Anything else (a scroll, an erase, a replaced row) ends the open screen at that pair's time and starts a new one.
/// </remarks>
public sealed class Cea608Decoder
{
    private const int Rows = 15;
    private const int Cols = 32;

    private enum Mode { None, PopOn, RollUp, PaintOn, Text }

    private struct Cell
    {
        public char Ch; // 0 = empty
        public byte Attr; // bits 0-2 colour, 3 italic, 4 underline, 5 flash
    }

    private sealed class Channel
    {
        public Cell[,] Shown = new Cell[Rows, Cols];
        public Cell[,] Hidden = new Cell[Rows, Cols];
        public Mode Mode;
        public int RollRows;
        public int BaseRow = Rows - 1;
        public int Row;
        public int Col;
        public bool WroteLast; // the last character went into column 32, where the cursor stays
        public byte Attr;
        public bool Open;
        public CaptionMode OpenMode;
        public MediaTime OpenStart;
        public List<Cea608Row> OpenRows = [];
        public MediaTime TextStart = MediaTime.Unknown;
    }

    private sealed class FieldState
    {
        public int DataChannel;
        public bool TextMode;
        public int PrevB1 = -1;
        public int PrevB2 = -1;
        public bool InXds;
        public int XdsClass;
        public int XdsType;
        public List<byte> XdsData = [];
        public int XdsSum;
    }

    private readonly Channel[] _channels = new Channel[8];
    private readonly FieldState[] _fields = [new FieldState(), new FieldState()];
    private readonly List<Cea608Caption> _captions = [];
    private readonly List<XdsPacket> _xds = [];

    /// <summary>Creates a decoder.</summary>
    public Cea608Decoder()
    {
        for (int i = 0; i < _channels.Length; i++)
        {
            _channels[i] = new Channel();
        }
    }

    /// <summary>When true (the default) a pair whose bytes do not both have odd parity is dropped (CEA-608 §4.1); when false the parity bit is ignored.</summary>
    public bool RequireParity { get; init; } = true;

    /// <summary>The finished caption screens of every channel in the order they ended.</summary>
    public IReadOnlyList<Cea608Caption> Captions => _captions;

    /// <summary>The extended data services packets found on field 2.</summary>
    public IReadOnlyList<XdsPacket> Xds => _xds;

    /// <summary>Takes a byte pair.</summary>
    /// <param name="time">When the pair is displayed or processed.</param>
    /// <param name="field">0 for field 1 (<c>cc_type</c> 0), 1 for field 2.</param>
    /// <param name="b1">First byte, with its parity bit.</param>
    /// <param name="b2">Second byte, with its parity bit.</param>
    public void Push(MediaTime time, int field, byte b1, byte b2)
    {
        FieldState f = _fields[field & 1];
        if (RequireParity && (!OddParity(b1) || !OddParity(b2)))
        {
            return;
        }

        int c1 = b1 & 0x7f;
        int c2 = b2 & 0x7f;
        if (c1 == 0 && c2 == 0)
        {
            return;
        }

        // CEA-608 §4.2: control codes are sent twice; the second copy is ignored.
        if (c1 is >= 0x10 and <= 0x1f)
        {
            if (c1 == f.PrevB1 && c2 == f.PrevB2)
            {
                f.PrevB1 = f.PrevB2 = -1;
                return;
            }

            f.PrevB1 = c1;
            f.PrevB2 = c2;
        }
        else
        {
            f.PrevB1 = f.PrevB2 = -1;
        }

        if (c1 < 0x10)
        {
            if (field == 1)
            {
                HandleXds(f, time, c1, c2);
                return;
            }

            // 47 CFR 79.101(i)(1): a first byte of 00h–0Fh is ignored alone and the second byte is treated normally.
            c1 = c2 >= 0x20 ? c2 : 0;
            c2 = 0;
            if (c1 == 0)
            {
                return;
            }
        }

        if (c1 >= 0x20 && field == 1 && f.InXds)
        {
            f.XdsSum += c1 + c2;
            f.XdsData.Add((byte)c1);
            if (c2 >= 0x20)
            {
                f.XdsData.Add((byte)c2);
            }

            return;
        }

        if (c1 >= 0x20)
        {
            Channel ch = Current(field, f);
            if (ch.Mode == Mode.None)
            {
                return;
            }

            Put(ch, time, Cea608Text.Basic(c1));
            if (c2 >= 0x20)
            {
                Put(ch, time, Cea608Text.Basic(c2));
            }

            Update(ch, time);
            return;
        }

        f.InXds = false;
        int dc = (c1 >> 3) & 1;
        if (c1 == 0x10 || c1 == 0x18)
        {
            // 0x10/0x18 with 0x20–0x2F: background attribute (no effect on text); with 0x40–0x7F: a PAC for row 11.
            // 47 CFR 79.101(i), PAC table: first byte 10h carries row 11 with second bytes 40h–5Fh only.
            if (c2 < 0x40 || c2 >= 0x60)
            {
                return;
            }
        }

        f.DataChannel = dc;
        int low = c1 & 7;
        if (c2 >= 0x40)
        {
            // Preamble address code.
            Channel pc = ChannelFor(field, dc, f.TextMode);
            Pac(pc, c1, c2);
            return;
        }

        switch (low)
        {
            case 1 when c2 is >= 0x20 and <= 0x2f:
                MidRow(ChannelFor(field, dc, f.TextMode), f, time, c2);
                break;
            case 1 when c2 >= 0x30:
                {
                    Channel ch = ChannelFor(field, dc, f.TextMode);
                    if (ch.Mode != Mode.None)
                    {
                        Put(ch, time, Cea608Text.SpecialChar(c2));
                        Update(ch, time);
                    }

                    break;
                }

            case 2 or 3 when c2 >= 0x20:
                {
                    Channel ch = ChannelFor(field, dc, f.TextMode);
                    if (ch.Mode != Mode.None)
                    {
                        // CEA-608 §6.4: an extended character replaces the basic character sent before it as a fallback.
                        // In column 32 the cursor did not move after the fallback character, so there is nothing to step back over.
                        if (ch.Col > 0 && !ch.WroteLast)
                        {
                            ch.Col--;
                        }

                        Put(ch, time, Cea608Text.ExtendedChar(c1, c2));
                        Update(ch, time);
                    }

                    break;
                }

            case 4 or 5 when c2 is >= 0x20 and <= 0x2f:
                Misc(field, dc, f, time, c2);
                break;
            case 7 when c2 is >= 0x21 and <= 0x23:
                {
                    Channel ch = ChannelFor(field, dc, f.TextMode);
                    ch.Col = Math.Min(Cols - 1, ch.Col + (c2 - 0x20)); // tab offset
                    ch.WroteLast = false;
                    break;
                }

            default:
                break;
        }
    }

    /// <summary>Closes every open screen at <paramref name="end"/>.</summary>
    /// <param name="end">The time of the end of the stream.</param>
    public void Finish(MediaTime end)
    {
        for (int i = 0; i < _channels.Length; i++)
        {
            Close(_channels[i], (Cea608Channel)i, end);
        }
    }

    private static bool OddParity(byte b) => (System.Numerics.BitOperations.PopCount(b) & 1) == 1;

    private Channel ChannelFor(int field, int dc, bool text) => _channels[(text ? 4 : 0) + (field * 2) + dc];

    private Channel Current(int field, FieldState f) => ChannelFor(field, f.DataChannel, f.TextMode);

    private static Cea608Channel Id(int field, int dc, bool text) => (Cea608Channel)((text ? 4 : 0) + (field * 2) + dc);

    private static void Pac(Channel ch, int c1, int c2)
    {
        // CEA-608 Table 53: first byte (low 3 bits, 0x10 = 0 → row 11) and bit 5 of the second byte select the row.
        int pair = (c1 & 7) switch { 1 => 0, 2 => 1, 5 => 2, 6 => 3, 7 => 4, 0 => 5, 3 => 6, _ => 7 };
        int row = pair switch { 0 => 1, 1 => 3, 2 => 5, 3 => 7, 4 => 9, 5 => 11, 6 => 12, _ => 14 };
        if ((c2 & 0x20) != 0 && pair != 5)
        {
            row++;
        }

        int style = (c2 >> 1) & 0xf;
        byte attr = 0;
        int indent = 0;
        if (style < 7)
        {
            attr = (byte)style;
        }
        else if (style == 7)
        {
            attr = 8;
        }
        else
        {
            indent = (style - 8) * 4;
        }

        if ((c2 & 1) != 0)
        {
            attr |= 16;
        }

        ch.Attr = attr;
        ch.Col = indent;
        ch.WroteLast = false;
        if (ch.Mode == Mode.RollUp)
        {
            ch.BaseRow = Math.Max(row - 1, ch.RollRows - 1);
            ch.Row = ch.BaseRow;
        }
        else
        {
            ch.Row = row - 1;
        }
    }

    private void MidRow(Channel ch, FieldState f, MediaTime time, int c2)
    {
        if (ch.Mode == Mode.None)
        {
            return;
        }

        int style = (c2 >> 1) & 7;
        byte attr = style == 7 ? (byte)8 : (byte)style;
        if ((c2 & 1) != 0)
        {
            attr |= 16;
        }

        ch.Attr = attr;
        Put(ch, time, ' '); // a mid-row code occupies one column
        ch.Attr = attr;
        Update(ch, time);
    }

    private static void Put(Channel ch, MediaTime time, char c)
    {
        if (ch.Mode == Mode.Text && !ch.TextStart.IsKnown)
        {
            ch.TextStart = time;
        }

        Cell[,] grid = ch.Mode == Mode.PopOn ? ch.Hidden : ch.Shown;
        int row = Math.Clamp(ch.Row, 0, Rows - 1);
        grid[row, ch.Col] = new Cell { Ch = c, Attr = ch.Attr };
        ch.WroteLast = ch.Col == Cols - 1;
        if (ch.Col < Cols - 1)
        {
            ch.Col++;
        }
    }

    private void Misc(int field, int dc, FieldState f, MediaTime time, int c2)
    {
        Channel ch;
        switch (c2)
        {
            case 0x20: // RCL: resume caption loading
                f.TextMode = false;
                ch = ChannelFor(field, dc, false);
                ch.Mode = Mode.PopOn;
                return;
            case 0x25 or 0x26 or 0x27: // RU2–RU4
                f.TextMode = false;
                ch = ChannelFor(field, dc, false);
                if (ch.Mode != Mode.RollUp)
                {
                    Clear(ch.Shown);
                    Clear(ch.Hidden);
                    ch.Col = 0;
                    ch.BaseRow = Rows - 1;
                }

                ch.Mode = Mode.RollUp;
                ch.RollRows = c2 - 0x23;
                Update(ch, time);
                return;
            case 0x29: // RDC: resume direct captioning
                f.TextMode = false;
                ch = ChannelFor(field, dc, false);
                ch.Mode = Mode.PaintOn;
                return;
            case 0x2a: // TR: text restart
                f.TextMode = true;
                ch = ChannelFor(field, dc, true);
                Clear(ch.Shown);
                ch.Mode = Mode.Text;
                ch.Row = 0;
                ch.Col = 0;
                ch.TextStart = MediaTime.Unknown;
                return;
            case 0x2b: // RTD: resume text display
                f.TextMode = true;
                ch = ChannelFor(field, dc, true);
                if (ch.Mode != Mode.Text)
                {
                    ch.Mode = Mode.Text;
                    ch.Row = 0;
                    ch.Col = 0;
                }

                return;
        }

        ch = Current(field, f);
        switch (c2)
        {
            case 0x21: // BS
                if (ch.Col > 0)
                {
                    ch.Col--;
                    ch.WroteLast = false;
                    (ch.Mode == Mode.PopOn ? ch.Hidden : ch.Shown)[Math.Clamp(ch.Row, 0, Rows - 1), ch.Col] = default;
                    Update(ch, time);
                }

                break;
            case 0x24: // DER: delete to end of row
                {
                    Cell[,] grid = ch.Mode == Mode.PopOn ? ch.Hidden : ch.Shown;
                    for (int c = ch.Col; c < Cols; c++)
                    {
                        grid[Math.Clamp(ch.Row, 0, Rows - 1), c] = default;
                    }

                    Update(ch, time);
                    break;
                }

            case 0x28: // FON: flash on
                // 47 CFR 79.101(h)(1)(i): Flash On is a spacing attribute, shown as a standard space.
                ch.Attr |= 32;
                if (ch.Mode != Mode.None)
                {
                    Put(ch, time, ' ');
                    ch.Attr |= 32;
                    Update(ch, time);
                }

                break;
            case 0x2c: // EDM: erase displayed memory
                Clear(ch.Shown);
                Update(ch, time);
                break;
            case 0x2d: // CR
                CarriageReturn(ch, field, f, time);
                break;
            case 0x2e: // ENM
                Clear(ch.Hidden);
                break;
            case 0x2f: // EOC: end of caption, swap memories
                (ch.Shown, ch.Hidden) = (ch.Hidden, ch.Shown);
                ch.Mode = Mode.PopOn;
                ch.Col = 0;
                Update(ch, time);
                break;
        }
    }

    private void CarriageReturn(Channel ch, int field, FieldState f, MediaTime time)
    {
        if (ch.Mode == Mode.Text)
        {
            EmitTextLine(ch, Id(field, f.DataChannel, true), time);
            ch.Row = Math.Min(ch.Row + 1, Rows - 1);
            ch.Col = 0;
            return;
        }

        if (ch.Mode != Mode.RollUp)
        {
            return;
        }

        int top = Math.Max(0, ch.BaseRow - ch.RollRows + 1);
        for (int r = top; r < ch.BaseRow; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                ch.Shown[r, c] = ch.Shown[r + 1, c];
            }
        }

        for (int c = 0; c < Cols; c++)
        {
            ch.Shown[ch.BaseRow, c] = default;
        }

        ch.Col = 0;
        Update(ch, time);
    }

    private void EmitTextLine(Channel ch, Cea608Channel id, MediaTime time)
    {
        List<Cea608Row> rows = Snapshot(ch.Shown, ch.Row, ch.Row);
        if (rows.Count > 0)
        {
            _captions.Add(new Cea608Caption(id, ch.TextStart.IsKnown ? ch.TextStart : time, time, [rows[0]]));
        }

        for (int c = 0; c < Cols; c++)
        {
            ch.Shown[ch.Row, c] = default;
        }

        ch.TextStart = MediaTime.Unknown;
    }

    private static void Clear(Cell[,] grid) => Array.Clear(grid);

    private void Update(Channel ch, MediaTime time)
    {
        if (ch.Mode == Mode.Text)
        {
            return;
        }

        int idx = Array.IndexOf(_channels, ch);
        List<Cea608Row> rows = Snapshot(ch.Shown, 0, Rows - 1);
        if (rows.Count == 0)
        {
            Close(ch, (Cea608Channel)idx, time);
            return;
        }

        if (ch.Open)
        {
            if (SameRows(ch.OpenRows, rows))
            {
                return;
            }

            if (Extends(ch.OpenRows, rows))
            {
                ch.OpenRows = rows;
                return;
            }

            Close(ch, (Cea608Channel)idx, time);
        }

        ch.Open = true;
        ch.OpenMode = ch.Mode switch { Mode.PopOn => CaptionMode.PopOn, Mode.RollUp => CaptionMode.RollUp, Mode.PaintOn => CaptionMode.PaintOn, _ => CaptionMode.None };
        ch.OpenStart = time;
        ch.OpenRows = rows;
    }

    private void Close(Channel ch, Cea608Channel id, MediaTime end)
    {
        if (ch.Mode == Mode.Text && ch.TextStart.IsKnown)
        {
            EmitTextLine(ch, id, end);
        }

        if (!ch.Open)
        {
            return;
        }

        _captions.Add(new Cea608Caption(id, ch.OpenStart, end, ch.OpenRows) { Mode = ch.OpenMode });
        ch.Open = false;
        ch.OpenRows = [];
    }

    private static bool SameRows(List<Cea608Row> a, List<Cea608Row> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Row != b[i].Row || a[i].Column != b[i].Column || a[i].Text != b[i].Text || !a[i].Spans.SequenceEqual(b[i].Spans))
            {
                return false;
            }
        }

        return true;
    }

    // True when every open row is still present at the same place and the new text starts with the old text.
    private static bool Extends(List<Cea608Row> open, List<Cea608Row> now)
    {
        foreach (Cea608Row o in open)
        {
            bool found = false;
            foreach (Cea608Row n in now)
            {
                if (n.Row == o.Row)
                {
                    found = n.Column == o.Column && n.Text.StartsWith(o.Text, StringComparison.Ordinal);
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static List<Cea608Row> Snapshot(Cell[,] grid, int from, int to)
    {
        var rows = new List<Cea608Row>();
        for (int r = from; r <= to; r++)
        {
            int first = -1;
            int last = -1;
            for (int c = 0; c < Cols; c++)
            {
                if (grid[r, c].Ch != 0 && (grid[r, c].Ch != ' ' || first >= 0))
                {
                    if (first < 0)
                    {
                        first = c;
                    }

                    if (grid[r, c].Ch != ' ')
                    {
                        last = c;
                    }
                }
            }

            if (first < 0 || last < 0)
            {
                continue;
            }

            var sb = new StringBuilder(last - first + 1);
            var spans = new List<Cea608Span>();
            int spanStart = -1;
            byte spanAttr = 0;
            for (int c = first; c <= last; c++)
            {
                Cell cell = grid[r, c];
                sb.Append(cell.Ch == 0 ? ' ' : cell.Ch);
                byte a = cell.Ch == 0 ? (byte)0 : cell.Attr;
                if (a != spanAttr)
                {
                    if (spanAttr != 0)
                    {
                        spans.Add(MakeSpan(spanStart, c - first - spanStart, spanAttr));
                    }

                    spanStart = c - first;
                    spanAttr = a;
                }
            }

            if (spanAttr != 0)
            {
                spans.Add(MakeSpan(spanStart, last - first + 1 - spanStart, spanAttr));
            }

            rows.Add(new Cea608Row(r + 1, first + 1, sb.ToString(), spans));
        }

        return rows;
    }

    private static Cea608Span MakeSpan(int start, int length, byte attr) =>
        new(start, length, (Cea608Color)(attr & 7), (attr & 8) != 0, (attr & 16) != 0, (attr & 32) != 0);

    private void HandleXds(FieldState f, MediaTime time, int c1, int c2)
    {
        // CEA-608 §9: 0x01–0x0E start (odd) or continue (even) a packet of class (c1+1)/2; 0x0F ends it and carries the checksum.
        if (c1 == 0x0f)
        {
            if (f.InXds)
            {
                int sum = (f.XdsSum + c1 + c2) & 0x7f;
                _xds.Add(new XdsPacket(time, f.XdsClass, f.XdsType, [.. f.XdsData], sum == 0));
                f.InXds = false;
            }

            return;
        }

        if ((c1 & 1) == 1)
        {
            f.InXds = true;
            f.XdsClass = (c1 + 1) / 2;
            f.XdsType = c2;
            f.XdsData.Clear();
            f.XdsSum = c1 + c2;
            return;
        }

        // A continue code resumes the packet of its class after an interruption.
        if (f.XdsClass == c1 / 2 && f.XdsData.Count > 0)
        {
            f.InXds = true;
        }
    }
}
