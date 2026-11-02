namespace RecursiveAI.OneMedia.Captions.Teletext;

/// <summary>The alphabet of a G0/G2 character set pair (ETS 300 706 §15.2, table 32).</summary>
public enum TeletextAlphabet
{
    /// <summary>Latin, with a national option sub-set.</summary>
    Latin = 0,

    /// <summary>Cyrillic option 1, Serbian/Croatian (table 38).</summary>
    CyrillicSerbianCroatian,

    /// <summary>Cyrillic option 2, Russian/Bulgarian (table 39).</summary>
    CyrillicRussianBulgarian,

    /// <summary>Cyrillic option 3, Ukrainian (table 40).</summary>
    CyrillicUkrainian,

    /// <summary>Greek (table 42).</summary>
    Greek,

    /// <summary>Arabic (table 44). Not mapped: characters come out as U+FFFD.</summary>
    Arabic,

    /// <summary>Hebrew (table 46). Not mapped: characters come out as U+FFFD.</summary>
    Hebrew,
}

/// <summary>The Latin national option sub-sets of ETS 300 706 table 36.</summary>
public enum TeletextNationalOption
{
    /// <summary>English.</summary>
    English = 0,

    /// <summary>German.</summary>
    German,

    /// <summary>Swedish, Finnish and Hungarian.</summary>
    SwedishFinnishHungarian,

    /// <summary>Italian.</summary>
    Italian,

    /// <summary>French.</summary>
    French,

    /// <summary>Portuguese and Spanish.</summary>
    PortugueseSpanish,

    /// <summary>Czech and Slovak.</summary>
    CzechSlovak,

    /// <summary>Polish.</summary>
    Polish,

    /// <summary>Turkish.</summary>
    Turkish,

    /// <summary>Serbian, Croatian and Slovenian.</summary>
    SerbianCroatianSlovenian,

    /// <summary>Rumanian.</summary>
    Rumanian,

    /// <summary>Estonian.</summary>
    Estonian,

    /// <summary>Lettish and Lithuanian.</summary>
    LettishLithuanian,
}

/// <summary>A G0 character set choice: an alphabet and, for Latin, a national option sub-set.</summary>
/// <param name="Alphabet">The alphabet.</param>
/// <param name="National">The national option sub-set; meaningful for <see cref="TeletextAlphabet.Latin"/> only.</param>
public readonly record struct TeletextCharacterSet(TeletextAlphabet Alphabet, TeletextNationalOption National = TeletextNationalOption.English)
{
    /// <summary>
    /// Resolves the 7-bit "default G0 and G2 character set designation and national option selection" value of packets X/28/0,
    /// X/28/4, M/29/0 and M/29/4 (ETS 300 706 §15.2, table 32).
    /// </summary>
    /// <param name="designation">Triplet 1 bits 8 to 14 as a number (bit 8 is the least significant).</param>
    /// <returns>The set; reserved values give Latin with the English option.</returns>
    public static TeletextCharacterSet FromDesignation(int designation)
    {
        int row = (designation >> 3) & 0xF;
        int col = designation & 7;
        return row switch
        {
            0b0000 => Latin(col, 0),
            0b0001 => col == 0 ? new(TeletextAlphabet.Latin, TeletextNationalOption.Polish) : Latin(col, 0),
            0b0010 => col == 6 ? new(TeletextAlphabet.Latin, TeletextNationalOption.Turkish) : Latin(col, 0),
            0b0011 => col switch
            {
                5 => new(TeletextAlphabet.Latin, TeletextNationalOption.SerbianCroatianSlovenian),
                7 => new(TeletextAlphabet.Latin, TeletextNationalOption.Rumanian),
                _ => default,
            },
            0b0100 => col switch
            {
                0 => new(TeletextAlphabet.CyrillicSerbianCroatian),
                1 => new(TeletextAlphabet.Latin, TeletextNationalOption.German),
                2 => new(TeletextAlphabet.Latin, TeletextNationalOption.Estonian),
                3 => new(TeletextAlphabet.Latin, TeletextNationalOption.LettishLithuanian),
                4 => new(TeletextAlphabet.CyrillicRussianBulgarian),
                5 => new(TeletextAlphabet.CyrillicUkrainian),
                6 => new(TeletextAlphabet.Latin, TeletextNationalOption.CzechSlovak),
                _ => default,
            },
            0b0110 => col switch
            {
                6 => new(TeletextAlphabet.Latin, TeletextNationalOption.Turkish),
                7 => new(TeletextAlphabet.Greek),
                _ => default,
            },
            0b1000 => col switch
            {
                4 => new(TeletextAlphabet.Latin, TeletextNationalOption.French),
                7 => new(TeletextAlphabet.Arabic),
                _ => default,
            },
            0b1010 => col switch
            {
                5 => new(TeletextAlphabet.Hebrew),
                7 => new(TeletextAlphabet.Arabic),
                _ => default,
            },
            _ => default,
        };

        // Rows 0000 to 0010 share the first six sub-sets; the C12-C14 column picks one.
        static TeletextCharacterSet Latin(int column, int _) => column switch
        {
            0 => new(TeletextAlphabet.Latin, TeletextNationalOption.English),
            1 => new(TeletextAlphabet.Latin, TeletextNationalOption.German),
            2 => new(TeletextAlphabet.Latin, TeletextNationalOption.SwedishFinnishHungarian),
            3 => new(TeletextAlphabet.Latin, TeletextNationalOption.Italian),
            4 => new(TeletextAlphabet.Latin, TeletextNationalOption.French),
            5 => new(TeletextAlphabet.Latin, TeletextNationalOption.PortugueseSpanish),
            6 => new(TeletextAlphabet.Latin, TeletextNationalOption.CzechSlovak),
            _ => default,
        };
    }

    /// <summary>
    /// The set a broadcaster in the language usually transmits, for streams that do not say (a guess made from the PMT language).
    /// </summary>
    /// <param name="iso639">A three-letter ISO 639-2 code, either the bibliographic or the terminology form.</param>
    /// <returns>The set, or null for languages with no entry.</returns>
    public static TeletextCharacterSet? ForLanguage(string iso639)
    {
        ArgumentNullException.ThrowIfNull(iso639);
        TeletextNationalOption? latin = iso639.ToLowerInvariant() switch
        {
            "eng" => TeletextNationalOption.English,
            "deu" or "ger" => TeletextNationalOption.German,
            "swe" or "fin" or "hun" => TeletextNationalOption.SwedishFinnishHungarian,
            "ita" => TeletextNationalOption.Italian,
            "fra" or "fre" => TeletextNationalOption.French,
            "spa" or "por" or "cat" => TeletextNationalOption.PortugueseSpanish,
            "ces" or "cze" or "slk" or "slo" => TeletextNationalOption.CzechSlovak,
            "pol" => TeletextNationalOption.Polish,
            "tur" => TeletextNationalOption.Turkish,
            "hrv" or "slv" or "scr" => TeletextNationalOption.SerbianCroatianSlovenian,
            "ron" or "rum" => TeletextNationalOption.Rumanian,
            "est" => TeletextNationalOption.Estonian,
            "lav" or "lit" => TeletextNationalOption.LettishLithuanian,
            _ => null,
        };
        if (latin is { } option)
        {
            return new TeletextCharacterSet(TeletextAlphabet.Latin, option);
        }

        return iso639.ToLowerInvariant() switch
        {
            "rus" or "bul" => new TeletextCharacterSet(TeletextAlphabet.CyrillicRussianBulgarian),
            "ukr" => new TeletextCharacterSet(TeletextAlphabet.CyrillicUkrainian),
            "srp" => new TeletextCharacterSet(TeletextAlphabet.CyrillicSerbianCroatian),
            "ell" or "gre" => new TeletextCharacterSet(TeletextAlphabet.Greek),
            _ => null,
        };
    }

    /// <summary>
    /// Maps a G0 code (0x20 to 0x7F) of this set to a character.
    /// </summary>
    /// <param name="code">The 7-bit code.</param>
    /// <returns>The character; U+FFFD where the set is not mapped.</returns>
    public char MapG0(int code) => TeletextCharacters.G0(this, code);
}

/// <summary>The code tables of ETS 300 706 §15.6, transcribed from the published tables.</summary>
internal static class TeletextCharacters
{
    // Table 36: the thirteen positions a national option sub-set replaces, in code order.
    private static readonly byte[] NationalPositions = [0x23, 0x24, 0x40, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F, 0x60, 0x7B, 0x7C, 0x7D, 0x7E];

    private static readonly string[] National =
    [
        "£$@←½→↑#─¼‖¾÷",                        // English
        "#$§ÄÖÜ^_°äöüß",                        // German
        "#¤ÉÄÖÅÜ_éäöåü",                        // Swedish/Finnish/Hungarian
        "£$é°ç→↑#ùàòèì",                        // Italian
        "éïàëêùî#èâôûç",                        // French
        "ç$¡áéíóú¿üñèà",                        // Portuguese/Spanish
        "#ůčťžýířéáěúš",                        // Czech/Slovak
        "#ńąŻŚŁćóężśłź",                        // Polish
        "₺ğİŞÖÇÜĞışöçü",                        // Turkish
        "#ËČĆŽĐŠëčćžđš",                        // Serbian/Croatian/Slovenian
        "#¤ŢÂŞĂÎıţâşăî",                        // Rumanian
        "#õŠÄÖŽÜÕšäöžü",                        // Estonian
        "#$ŠėęŽčūšąųžį",                        // Lettish/Lithuanian
    ];

    // Table 39 columns 2 to 7 for Cyrillic option 2 (Russian/Bulgarian); 0x7F is the block.
    private const string CyrillicRussian =
        " !\"#$%ы'()Ж+,-./" +
        "0123456789:;<=>?" +
        "ЮАБЦДЕФГХИЙКЛМНО" +
        "ПЯРСТУЖВЬЪЗШЭЩЧЫ" +
        "юабцдефгхийклмно" +
        "пярстужвьъзшэщч■";

    // Table 37, columns 2 and 3 and 5 to 7 of the Latin G2 set. Column 4 (diacritical marks) is handled by composition.
    private static readonly string G2Column2 = " ¡¢£$¥#§¤‘“«←↑→↓";
    private static readonly string G2Column3 = "°±²³×µ¶·÷’”»¼½¾¿";
    private static readonly string G2Column5 = "―¹®©™♪€‰α\0\0\0⅛⅜⅝⅞";
    private static readonly string G2Column6 = "ΩÆĐªĦ\0ĲĿŁØŒºÞŦŊŉ";
    private static readonly string G2Column7 = "ĸæđðħıĳŀłøœßþŧŋ■";

    /// <summary>A G0 character (0x20 to 0x7F) of <paramref name="set"/>.</summary>
    public static char G0(TeletextCharacterSet set, int code)
    {
        if (code < 0x20 || code > 0x7F)
        {
            return ' ';
        }

        switch (set.Alphabet)
        {
            case TeletextAlphabet.Latin:
                int slot = Array.IndexOf(NationalPositions, (byte)code);
                if (slot >= 0)
                {
                    return National[(int)set.National][slot];
                }

                return code == 0x7F ? '■' : (char)code;
            case TeletextAlphabet.CyrillicRussianBulgarian:
                return CyrillicRussian[code - 0x20];
            default:
                // Other alphabets share ASCII for the digits and punctuation they do not redefine; letters are unmapped.
                return code is >= 0x20 and <= 0x40 ? (char)code : '\uFFFD';
        }
    }

    /// <summary>A character of the Latin G2 supplementary set (table 37); U+FFFD when unassigned.</summary>
    public static char G2(int code)
    {
        if (code < 0x20 || code > 0x7F)
        {
            return '\uFFFD';
        }

        int column = code >> 4, row = code & 0xF;
        char c = column switch
        {
            2 => G2Column2[row],
            3 => G2Column3[row],
            5 => G2Column5[row],
            6 => G2Column6[row],
            7 => G2Column7[row],
            _ => '\0',
        };
        return c == '\0' ? '\uFFFD' : c;
    }
}
