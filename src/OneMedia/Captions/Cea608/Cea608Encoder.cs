using System.Linq;

namespace RecursiveAI.OneMedia.Captions.Cea608;

/// <summary>Turns caption rows into CEA-608 byte pairs (the inverse of <see cref="Cea608Decoder"/> for pop-on captions).</summary>
public static class Cea608Encoder
{
    private const string Special = "®°½¿™¢£♪à èâêîôû";
    private const string Extended1 = "ÁÉÓÚÜü‘¡*’—©℠•“”ÀÂÇÈÊËëÎÏïÔÙùÛ«»";
    private const string Extended2 = "ÃãÍÌìÒòÕõ{}\\^_|~ÄäÖöß¥¤¦ÅåØø┌┐└┘";

    /// <summary>Sets the odd-parity bit (bit 7) of a 7-bit value (CEA-608 §4.1).</summary>
    /// <param name="b">A value below 0x80.</param>
    /// <returns>The byte as transmitted.</returns>
    public static byte WithParity(int b)
    {
        b &= 0x7f;
        return (byte)((System.Numerics.BitOperations.PopCount((uint)b) & 1) == 1 ? b : b | 0x80);
    }

    /// <summary>
    /// Pairs that load and display a pop-on caption: resume caption loading and erase non-displayed memory (each sent twice), a preamble
    /// address code and the text for every row, then end of caption (twice). Characters CEA-608 cannot carry become '?'.
    /// </summary>
    /// <param name="rows">The rows, top to bottom. Rows beyond the 15 on the grid are dropped.</param>
    /// <param name="channel">CC1–CC4. Field and data channel select the control code prefix.</param>
    /// <returns>Byte pairs with parity; the last two are the end-of-caption pair.</returns>
    public static IReadOnlyList<(byte, byte)> PopOn(IReadOnlyList<Cea608Row> rows, Cea608Channel channel = Cea608Channel.CC1)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int dc = (int)channel & 1;
        var pairs = new List<(byte, byte)>();
        Misc(pairs, dc, 0x20); // RCL
        Misc(pairs, dc, 0x2e); // ENM
        foreach (Cea608Row row in rows.Take(15))
        {
            Row(pairs, dc, row);
        }

        Misc(pairs, dc, 0x2f); // EOC
        return pairs;
    }

    /// <summary>The pair that erases the displayed memory, sent twice.</summary>
    /// <param name="channel">CC1–CC4.</param>
    /// <returns>Two identical pairs.</returns>
    public static IReadOnlyList<(byte, byte)> Erase(Cea608Channel channel = Cea608Channel.CC1)
    {
        var pairs = new List<(byte, byte)>();
        Misc(pairs, (int)channel & 1, 0x2c);
        return pairs;
    }

    private static void Misc(List<(byte, byte)> pairs, int dc, int code)
    {
        // Miscellaneous control codes use 0x14 (data channel 1) or 0x1C (data channel 2); the second byte selects the command.
        var p = (WithParity(0x14 | (dc << 3)), WithParity(code));
        pairs.Add(p);
        pairs.Add(p);
    }

    private static void Row(List<(byte, byte)> pairs, int dc, Cea608Row row)
    {
        // Preamble address code (CEA-608 Table 53).
        int r = Math.Clamp(row.Row, 1, 15);
        int b1 = r switch { 1 or 2 => 0x11, 3 or 4 => 0x12, 5 or 6 => 0x15, 7 or 8 => 0x16, 9 or 10 => 0x17, 11 => 0x10, 12 or 13 => 0x13, _ => 0x14 };
        int b2 = (r is 2 or 4 or 6 or 8 or 10 or 13 or 15) ? 0x60 : 0x40;
        int col = Math.Clamp(row.Column - 1, 0, 31);
        Cea608Span? first = row.Spans.Count > 0 && row.Spans[0].Start == 0 ? row.Spans[0] : null;
        // A styled first run in an indented row cannot ride on the PAC (an indent PAC has no colour), so the cursor goes one column early
        // and a mid-row code, which takes that column, sets the style.
        bool lead = first is not null && col > 0;
        int target = lead ? col - 1 : col;
        int indent = target / 4 * 4;
        int style = indent > 0 || first is null || lead ? 8 + (indent / 4) : (first.Value.Italic ? 7 : (int)first.Value.Color);
        b2 |= style << 1;
        if (first?.Underline == true && !lead && indent == 0)
        {
            b2 |= 1;
        }

        var pac = (WithParity(b1 | (dc << 3)), WithParity(b2));
        pairs.Add(pac);
        pairs.Add(pac);

        // Tab offsets for the columns between the indent and the target.
        int tabs = target - indent;
        if (tabs > 0)
        {
            var to = (WithParity(0x17 | (dc << 3)), WithParity(0x20 + tabs));
            pairs.Add(to);
            pairs.Add(to);
        }

        int pending = -1;
        void Text(int c)
        {
            if (pending < 0)
            {
                pending = c;
            }
            else
            {
                pairs.Add((WithParity(pending), WithParity(c)));
                pending = -1;
            }
        }

        void Flush()
        {
            if (pending >= 0)
            {
                pairs.Add((WithParity(pending), WithParity(0)));
                pending = -1;
            }
        }

        // A mid-row code is itself a space column (CEA-608 §6.3): where the style changes after a space in the text, that space is
        // replaced by the code; otherwise the code adds one space.
        (Cea608Color, bool, bool, bool) StyleAt(int at)
        {
            foreach (Cea608Span sp in row.Spans)
            {
                if (at >= sp.Start && at < sp.Start + sp.Length)
                {
                    return (sp.Color, sp.Italic, sp.Underline, sp.Flash);
                }
            }

            return (Cea608Color.White, false, false, false);
        }

        void MidRow((Cea608Color Color, bool Italic, bool Underline, bool Flash) st)
        {
            Flush();
            int code = st.Italic ? 7 : (int)st.Color;
            var mid = (WithParity(0x11 | (dc << 3)), WithParity(0x20 + (code << 1) + (st.Underline ? 1 : 0)));
            pairs.Add(mid);
            pairs.Add(mid);
        }

        if (lead)
        {
            MidRow(StyleAt(0));
        }

        int handled = -1;
        for (int i = 0; i < row.Text.Length; i++)
        {
            if (row.Text[i] == ' ' && i + 1 < row.Text.Length && StyleAt(i + 1) != StyleAt(i))
            {
                MidRow(StyleAt(i + 1)); // stands in for this space
                handled = i + 1;
                continue;
            }

            if (row.Text[i] == ' ' && i > 0 && i != handled && StyleAt(i) != StyleAt(i - 1))
            {
                MidRow(StyleAt(i)); // the style changes on a space: the code takes its column
                continue;
            }

            if (i > 0 && i != handled && StyleAt(i) != StyleAt(i - 1))
            {
                MidRow(StyleAt(i));
            }

            char ch = row.Text[i];
            int basic = BasicCode(ch);
            if (basic >= 0)
            {
                Text(basic);
                continue;
            }

            int sp = Special.IndexOf(ch, StringComparison.Ordinal);
            int e1 = Extended1.IndexOf(ch, StringComparison.Ordinal);
            int e2 = Extended2.IndexOf(ch, StringComparison.Ordinal);
            if (sp >= 0)
            {
                Flush();
                pairs.Add((WithParity(0x11 | (dc << 3)), WithParity(0x30 + sp)));
            }
            else if (e1 >= 0 || e2 >= 0)
            {
                // CEA-608 §6.4: an extended character is preceded by a basic fallback that the decoder replaces.
                Text('?');
                Flush();
                pairs.Add(e1 >= 0 ? (WithParity(0x12 | (dc << 3)), WithParity(0x20 + e1)) : (WithParity(0x13 | (dc << 3)), WithParity(0x20 + e2)));
            }
            else
            {
                Text('?');
            }
        }

        Flush();
    }

    /// <summary>True when the character is in the CEA-608 basic, special or extended sets.</summary>
    /// <param name="c">The character.</param>
    /// <returns>Whether <see cref="PopOn"/> can write it (otherwise it writes '?').</returns>
    public static bool CanEncode(char c) => BasicCode(c) >= 0 || Special.Contains(c, StringComparison.Ordinal) || Extended1.Contains(c, StringComparison.Ordinal) || Extended2.Contains(c, StringComparison.Ordinal);

    private static int BasicCode(char c)
    {
        // The basic set is ASCII except the codes in Cea608Text.Basic.
        if (c is < ' ' or > '~')
        {
            return c switch { 'á' => 0x2a, 'é' => 0x5c, 'í' => 0x5e, 'ó' => 0x5f, 'ú' => 0x60, 'ç' => 0x7b, '÷' => 0x7c, 'Ñ' => 0x7d, 'ñ' => 0x7e, '■' => 0x7f, _ => -1 };
        }

        return c is '*' or '\\' or '^' or '_' or '`' or '{' or '|' or '}' or '~' ? -1 : c;
    }
}
