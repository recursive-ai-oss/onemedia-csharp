namespace RecursiveAI.OneMedia.Captions.Teletext;

/// <summary>Turns the packets of one page transmission into a grid of cells (ETS 300 706 §12.2 spacing attributes, §12.3 packet X/26, §15 character sets).</summary>
internal static class TeletextRenderer
{
    public static TeletextPage Render(TeletextPageDecoder.PageBuilder b, TeletextCharacterSet? magazineDefault, MediaTime completedAt, TeletextPageDecoder owner, TeletextOptions options)
    {
        TeletextCharacterSet set = b.Designation ?? magazineDefault ?? HeaderSet(b.NationalBits);
        if (b.Designation is null && magazineDefault is null && b.NationalBits == 0)
        {
            if (options.FallbackCharacterSets is { } byPage && byPage.TryGetValue(new TeletextPageAddress(b.Magazine, b.Number), out TeletextCharacterSet fallback))
            {
                set = fallback;
            }
            else if (options.FallbackCharacterSet is { } any)
            {
                set = any;
            }
        }

        TeletextCharacterSetSource source = b.Designation is not null ? TeletextCharacterSetSource.Page : magazineDefault is not null ? TeletextCharacterSetSource.Magazine : TeletextCharacterSetSource.Header;
        char[] overrides = new char[TeletextPage.Rows * TeletextPage.Columns];
        ApplyEnhancements(b, set, overrides);

        TeletextCell[] cells = new TeletextCell[TeletextPage.Rows * TeletextPage.Columns];
        Array.Fill(cells, new TeletextCell(' ', TeletextColor.White, TeletextColor.Black, TeletextCellAttributes.None));

        for (int row = 0; row < TeletextPage.Rows; row++)
        {
            if (!b.RowPresent[row])
            {
                continue;
            }

            RenderRow(b, row, set, overrides, cells, owner);
        }

        return new TeletextPage(b.Magazine, b.Number, b.Subcode, b.Flags, set, source, b.NationalBits, b.Time, completedAt, cells, b.RowPresent, b.Offset);
    }

    // Level 1 pages with no X/28 or M/29 designation: C12 to C14 pick an option of the Latin set (§15.2).
    // The table lists the column as C12 C13 C14 with C12 the most significant (§15.2 note 1: bit 10 = C12, bit 8 = C14).
    private static TeletextCharacterSet HeaderSet(int nationalBits)
    {
        int c12 = nationalBits & 1, c13 = (nationalBits >> 1) & 1, c14 = (nationalBits >> 2) & 1;
        int column = (c12 << 2) | (c13 << 1) | c14;
        return TeletextCharacterSet.FromDesignation(column);
    }

    private static void RenderRow(TeletextPageDecoder.PageBuilder b, int row, TeletextCharacterSet set, char[] overrides, TeletextCell[] cells, TeletextPageDecoder owner)
    {
        ReadOnlySpan<byte> raw = b.Raw.AsSpan(row * 40, 40);
        int firstColumn = row == 0 ? 8 : 0;

        // §12.2: start-of-row defaults.
        TeletextColor fg = TeletextColor.White, bg = TeletextColor.Black;
        bool mosaic = false, flash = false, conceal = false, boxed = false;
        TeletextCellAttributes size = TeletextCellAttributes.None;
        for (int col = firstColumn; col < 40; col++)
        {
            byte value = raw[col];
            if (!TeletextCodes.TryOddParity(value, out byte code))
            {
                owner.CountError();
                code = 0x20;
            }

            TeletextCellAttributes flags = size;
            if (flash)
            {
                flags |= TeletextCellAttributes.Flash;
            }

            if (conceal)
            {
                flags |= TeletextCellAttributes.Conceal;
            }

            char ch;
            if (code < 0x20)
            {
                // Spacing attributes display as a space. "Set-At" codes change the cell they occupy, "Set-After" codes the cells after it.
                switch (code)
                {
                    case 0x09: // Steady
                        flash = false;
                        flags &= ~TeletextCellAttributes.Flash;
                        break;
                    case 0x0C: // Normal size
                        size = TeletextCellAttributes.None;
                        flags &= ~(TeletextCellAttributes.DoubleHeight | TeletextCellAttributes.DoubleWidth);
                        break;
                    case 0x18: // Conceal
                        conceal = true;
                        flags |= TeletextCellAttributes.Conceal;
                        break;
                    case 0x1C: // Black background
                        bg = TeletextColor.Black;
                        break;
                    case 0x1D: // New background: the current foreground colour
                        bg = fg;
                        break;
                }

                if (boxed)
                {
                    flags |= TeletextCellAttributes.Boxed;
                }

                cells[(row * 40) + col] = new TeletextCell(' ', fg, bg, flags);

                switch (code)
                {
                    case <= 0x07: // Alpha colour
                        fg = (TeletextColor)code;
                        mosaic = false;
                        conceal = false;
                        break;
                    case 0x08:
                        flash = true;
                        break;
                    case 0x0A:
                        boxed = false;
                        break;
                    case 0x0B:
                        boxed = true;
                        break;
                    case 0x0D:
                        size = TeletextCellAttributes.DoubleHeight;
                        break;
                    case 0x0E:
                        size = TeletextCellAttributes.DoubleWidth;
                        break;
                    case 0x0F:
                        size = TeletextCellAttributes.DoubleHeight | TeletextCellAttributes.DoubleWidth;
                        break;
                    case >= 0x10 and <= 0x17: // Mosaic colour
                        fg = (TeletextColor)(code - 0x10);
                        mosaic = true;
                        conceal = false;
                        break;
                }

                continue;
            }

            if (boxed)
            {
                flags |= TeletextCellAttributes.Boxed;
            }

            char over = overrides[(row * 40) + col];
            if (over != '\0')
            {
                ch = over;
            }
            else if (mosaic && (code is < 0x40 or >= 0x60))
            {
                // Columns 2, 3, 6 and 7 of G1 are block mosaics (§12.2 code 1/0 to 1/7); core carries no pixels, so they read as spaces.
                ch = ' ';
                flags |= TeletextCellAttributes.Mosaic;
            }
            else
            {
                ch = set.MapG0(code);
            }

            cells[(row * 40) + col] = new TeletextCell(ch, fg, bg, flags);
        }
    }

    // ETS 300 706 §12.3: packets X/26 overwrite characters of the Level 1 page, addressed by triplets.
    private static void ApplyEnhancements(TeletextPageDecoder.PageBuilder b, TeletextCharacterSet defaultSet, char[] overrides)
    {
        int activeRow = 0;
        TeletextCharacterSet set = defaultSet;
        foreach (byte[] packet in b.Enhancement)
        {
            if (TeletextCodes.DecodeHamming84(packet[0]) < 0)
            {
                continue;
            }

            for (int i = 0; i < 13; i++)
            {
                int triplet = TeletextCodes.DecodeHamming2418(packet[1 + (i * 3)], packet[2 + (i * 3)], packet[3 + (i * 3)]);
                if (triplet < 0)
                {
                    continue;
                }

                int address = triplet & 0x3F;
                int mode = (triplet >> 6) & 0x1F;
                int data = (triplet >> 11) & 0x7F;
                if (address >= 40)
                {
                    // Row address group (Table 28).
                    switch (mode)
                    {
                        case 0x04: // Set active position
                            activeRow = address == 40 ? 24 : address - 40;
                            set = defaultSet; // §12.3.4 mode 01000: a modified designation lasts to the end of the row
                            break;
                        case 0x01: // Full row colour
                            activeRow = address == 40 ? 24 : address - 40;
                            set = defaultSet;
                            break;
                        case 0x07: // Address display row 0
                            activeRow = 0;
                            break;
                        case 0x1F: // Termination marker: no further triplets
                            return;
                    }

                    continue;
                }

                // Column address group (Table 29): the address is the column.
                int column = address;
                char c;
                switch (mode)
                {
                    case 0x08: // Modified G0 and G2 character set designation
                        set = TeletextCharacterSet.FromDesignation(data);
                        continue;
                    case 0x0F: // Character from the G2 set
                        c = data >= 0x20 ? TeletextCharacters.G2(data) : '\0';
                        break;
                    case 0x09: // Character from the G0 set at levels 2.5 and 3.5
                    case 0x10: // G0 character without diacritical mark
                        c = data >= 0x20 ? set.MapG0(data) : '\0';
                        break;
                    case >= 0x11 and <= 0x1F:
                        c = data >= 0x20 ? Compose(set.MapG0(data), mode - 0x10) : '\0';
                        break;
                    default:
                        continue;
                }

                if (c != '\0' && column < 40 && activeRow < TeletextPage.Rows)
                {
                    overrides[(activeRow * 40) + column] = c;
                }
            }
        }
    }

    // Table 37 column 4 lists the sixteen diacritical marks in order; mode 0x11 to 0x1F pick marks 1 to 15 (grave, acute, circumflex,
    // tilde, macron, breve, dot, diaeresis, [unused], ring, cedilla, underline, double acute, ogonek, caron).
    private static readonly string[] Composition =
    [
        string.Empty,
        "AÀ" + "EÈ" + "IÌ" + "OÒ" + "UÙ" + "aà" + "eè" + "iì" + "oò" + "uù",          // grave
        "AÁ" + "CĆ" + "EÉ" + "IÍ" + "LĹ" + "NŃ" + "OÓ" + "RŔ" + "SŚ" + "UÚ" + "YÝ" + "ZŹ" + "aá" + "cć" + "eé" + "ií" + "lĺ" + "nń" + "oó" + "rŕ" + "sś" + "uú" + "yý" + "zź" + "gǵ",  // acute
        "AÂ" + "CĈ" + "EÊ" + "GĜ" + "HĤ" + "IÎ" + "JĴ" + "OÔ" + "SŜ" + "UÛ" + "WŴ" + "YŶ" + "aâ" + "cĉ" + "eê" + "gĝ" + "hĥ" + "iî" + "jĵ" + "oô" + "sŝ" + "uû" + "wŵ" + "yŷ", // circumflex
        "AÃ" + "IĨ" + "NÑ" + "OÕ" + "UŨ" + "aã" + "iĩ" + "nñ" + "oõ" + "uũ",          // tilde
        "AĀ" + "EĒ" + "IĪ" + "OŌ" + "UŪ" + "aā" + "eē" + "iī" + "oō" + "uū",          // macron
        "AĂ" + "EĔ" + "GĞ" + "IĬ" + "OŎ" + "UŬ" + "aă" + "eĕ" + "gğ" + "iĭ" + "oŏ" + "uŭ" + "ийИЙуўУЎ", // breve (Cyrillic й and ў included)
        "CĊ" + "EĖ" + "GĠ" + "Iİ" + "ZŻ" + "cċ" + "eė" + "gġ" + "zż",   // dot above
        "AÄ" + "EË" + "IÏ" + "OÖ" + "UÜ" + "YŸ" + "aä" + "eë" + "iï" + "oö" + "uü" + "yÿ" + "еёЕЁ", // diaeresis (Cyrillic ё included)
        string.Empty,
        "AÅ" + "UŮ" + "aå" + "uů",                                                     // ring
        "CÇ" + "GĢ" + "KĶ" + "LĻ" + "NŅ" + "RŖ" + "SŞ" + "TŢ" + "cç" + "gģ" + "kķ" + "lļ" + "nņ" + "rŗ" + "sş" + "tţ", // cedilla
        string.Empty,
        "OŐ" + "UŰ" + "oő" + "uű",                                                     // double acute
        "AĄ" + "EĘ" + "IĮ" + "UŲ" + "aą" + "eę" + "iį" + "uų",                         // ogonek
        "CČ" + "DĎ" + "EĚ" + "LĽ" + "NŇ" + "RŘ" + "SŠ" + "TŤ" + "ZŽ" + "cč" + "dď" + "eě" + "lľ" + "nň" + "rř" + "sš" + "tť" + "zž", // caron
    ];

    private static char Compose(char baseCharacter, int mark)
    {
        string pairs = Composition[mark];
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            if (pairs[i] == baseCharacter)
            {
                return pairs[i + 1];
            }
        }

        return baseCharacter;
    }
}
