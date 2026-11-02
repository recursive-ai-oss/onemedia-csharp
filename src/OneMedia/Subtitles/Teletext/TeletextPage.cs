using System.Text;

namespace RecursiveAI.OneMedia.Captions.Teletext;

/// <summary>The eight display colours of teletext (ETS 300 706 table 26 and §12.4, CLUT 0).</summary>
public enum TeletextColor
{
    /// <summary>Black.</summary>
    Black = 0,

    /// <summary>Red.</summary>
    Red,

    /// <summary>Green.</summary>
    Green,

    /// <summary>Yellow.</summary>
    Yellow,

    /// <summary>Blue.</summary>
    Blue,

    /// <summary>Magenta.</summary>
    Magenta,

    /// <summary>Cyan.</summary>
    Cyan,

    /// <summary>White.</summary>
    White,
}

/// <summary>Display attributes of one character cell.</summary>
[Flags]
public enum TeletextCellAttributes : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Double height (also set for double size): the cell stretches into the row below.</summary>
    DoubleHeight = 1,

    /// <summary>Double width (also set for double size): the cell stretches into the column to its right.</summary>
    DoubleWidth = 2,

    /// <summary>Inside a start-box/end-box area. Only boxed cells of a subtitle or newsflash page are shown over the picture.</summary>
    Boxed = 4,

    /// <summary>Flashes.</summary>
    Flash = 8,

    /// <summary>Concealed until revealed.</summary>
    Conceal = 16,

    /// <summary>A block mosaic character; <see cref="TeletextCell.Character"/> is a space.</summary>
    Mosaic = 32,
}

/// <summary>One character cell of a teletext page.</summary>
/// <param name="Character">The character (a UTF-16 code unit); a space for spacing attributes and mosaics.</param>
/// <param name="Foreground">Foreground colour.</param>
/// <param name="Background">Background colour.</param>
/// <param name="Flags">Display attributes.</param>
public readonly record struct TeletextCell(char Character, TeletextColor Foreground, TeletextColor Background, TeletextCellAttributes Flags);

/// <summary>The page control bits C4 to C11 of a page header (ETS 300 706 §9.3.1.3).</summary>
[Flags]
public enum TeletextControlBits
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>C4: erase the stored page before this transmission.</summary>
    Erase = 1,

    /// <summary>C5: a newsflash page.</summary>
    Newsflash = 2,

    /// <summary>C6: a subtitle page.</summary>
    Subtitle = 4,

    /// <summary>C7: row 0 is not displayed.</summary>
    SuppressHeader = 8,

    /// <summary>C8: content changed since the previous transmission.</summary>
    Update = 16,

    /// <summary>C9: the page is out of numerical sequence.</summary>
    InterruptedSequence = 32,

    /// <summary>C10: rows 1 to 24 are not displayed.</summary>
    InhibitDisplay = 64,

    /// <summary>C11: the service is in serial mode.</summary>
    Serial = 128,
}

/// <summary>Where the character set of a page came from.</summary>
public enum TeletextCharacterSetSource
{
    /// <summary>The page header's C12 to C14 bits (Latin national option only).</summary>
    Header = 0,

    /// <summary>A packet M/29/0 for the magazine.</summary>
    Magazine,

    /// <summary>A packet X/28/0 for the page.</summary>
    Page,
}

/// <summary>
/// One transmission of a teletext page, rendered to a 25 by 40 grid of cells.
/// </summary>
public sealed class TeletextPage
{
    /// <summary>Rows of a page: row 0 is the header, rows 1 to 24 are the body.</summary>
    public const int Rows = 25;

    /// <summary>Columns of a page.</summary>
    public const int Columns = 40;

    private readonly TeletextCell[] _cells;

    internal TeletextPage(int magazine, int number, int subcode, TeletextControlBits flags, TeletextCharacterSet characterSet, TeletextCharacterSetSource characterSetSource, int headerNationalBits, MediaTime time, MediaTime completedAt, TeletextCell[] cells, bool[] rowPresent, long sourceOffset)
    {
        Magazine = magazine;
        Number = number;
        Subcode = subcode;
        Flags = flags;
        CharacterSet = characterSet;
        CharacterSetSource = characterSetSource;
        HeaderNationalBits = headerNationalBits;
        Time = time;
        CompletedAt = completedAt;
        _cells = cells;
        RowPresent = rowPresent;
        SourceOffset = sourceOffset;
    }

    /// <summary>The magazine, 1 to 8.</summary>
    public int Magazine { get; }

    /// <summary>The page number within the magazine as two BCD-style hex digits (tens in the high nibble): page 777 has magazine 7 and number 0x77.</summary>
    public int Number { get; }

    /// <summary>The page sub-code, 0 to 0x3F7F (§9.3.1.2).</summary>
    public int Subcode { get; }

    /// <summary>The page number as read on screen: the magazine digit followed by the two page digits, for example "777".</summary>
    public string PageNumber => string.Create(3, (Magazine, Number), static (span, state) =>
    {
        span[0] = (char)('0' + state.Magazine);
        span[1] = HexDigit(state.Number >> 4);
        span[2] = HexDigit(state.Number & 0xF);
    });

    /// <summary>The control bits of the header.</summary>
    public TeletextControlBits Flags { get; }

    /// <summary>The G0 character set that applied (from X/28/0, M/29/0 or the header's C12 to C14).</summary>
    public TeletextCharacterSet CharacterSet { get; }

    /// <summary>Which packet chose <see cref="CharacterSet"/>.</summary>
    public TeletextCharacterSetSource CharacterSetSource { get; }

    /// <summary>The header's control bits C12 (bit 0), C13 (bit 1) and C14 (bit 2).</summary>
    public int HeaderNationalBits { get; }

    /// <summary>When the header of this transmission arrived: the presentation time of the PES packet that carried it.</summary>
    public MediaTime Time { get; }

    /// <summary>When the transmission ended: the time of the packet that terminated it (the next header, or the end of the data).</summary>
    public MediaTime CompletedAt { get; }

    /// <summary>The offset of the data unit that carried the header, for defect and debugging use; -1 when unknown.</summary>
    public long SourceOffset { get; }

    /// <summary>True for each row that had at least one packet in this transmission.</summary>
    public IReadOnlyList<bool> RowPresent { get; }

    /// <summary>The cell at <paramref name="row"/> (0 to 24) and <paramref name="column"/> (0 to 39).</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The column.</param>
    /// <exception cref="ArgumentOutOfRangeException">The position is outside the page.</exception>
    public TeletextCell this[int row, int column]
    {
        get
        {
            if ((uint)row >= Rows || (uint)column >= Columns)
            {
                throw new ArgumentOutOfRangeException(nameof(row));
            }

            return _cells[(row * Columns) + column];
        }
    }

    /// <summary>The text of a row with trailing spaces removed; mosaics and spacing attributes read as spaces. Not limited to boxed cells.</summary>
    /// <param name="row">The row, 0 to 24.</param>
    public string RowText(int row)
    {
        StringBuilder sb = new(Columns);
        for (int c = 0; c < Columns; c++)
        {
            sb.Append(_cells[(row * Columns) + c].Character);
        }

        return sb.ToString().TrimEnd(' ');
    }

    /// <summary>True when no cell of rows 1 to 24 holds a visible character.</summary>
    public bool IsBlank
    {
        get
        {
            for (int i = Columns; i < _cells.Length; i++)
            {
                if (_cells[i].Character != ' ')
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal TeletextCell[] Cells => _cells;

    private static char HexDigit(int v) => (char)(v < 10 ? '0' + v : 'A' + v - 10);
}
