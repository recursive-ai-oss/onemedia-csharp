using System.Linq;
namespace RecursiveAI.OneMedia.Captions.Cea608;

/// <summary>A CEA-608 data channel: caption channels CC1–CC4 and text channels Text1–Text4.</summary>
public enum Cea608Channel
{
    /// <summary>Field 1, data channel 1.</summary>
    CC1,

    /// <summary>Field 1, data channel 2.</summary>
    CC2,

    /// <summary>Field 2, data channel 1.</summary>
    CC3,

    /// <summary>Field 2, data channel 2.</summary>
    CC4,

    /// <summary>Text service on field 1, data channel 1.</summary>
    Text1,

    /// <summary>Text service on field 1, data channel 2.</summary>
    Text2,

    /// <summary>Text service on field 2, data channel 1.</summary>
    Text3,

    /// <summary>Text service on field 2, data channel 2.</summary>
    Text4,
}

/// <summary>The seven foreground colours a 608 control code can select.</summary>
public enum Cea608Color
{
    /// <summary>White (the default).</summary>
    White,

    /// <summary>Green.</summary>
    Green,

    /// <summary>Blue.</summary>
    Blue,

    /// <summary>Cyan.</summary>
    Cyan,

    /// <summary>Red.</summary>
    Red,

    /// <summary>Yellow.</summary>
    Yellow,

    /// <summary>Magenta.</summary>
    Magenta,
}

/// <summary>A run of characters in a <see cref="Cea608Row"/> whose style differs from plain white text.</summary>
/// <param name="Start">Index of the first character in <see cref="Cea608Row.Text"/>.</param>
/// <param name="Length">Number of characters.</param>
/// <param name="Color">Foreground colour.</param>
/// <param name="Italic">Italics.</param>
/// <param name="Underline">Underline.</param>
/// <param name="Flash">Flashing (set by the "flash on" code).</param>
public readonly record struct Cea608Span(int Start, int Length, Cea608Color Color, bool Italic, bool Underline, bool Flash);

/// <summary>One screen row of a caption: its place on the 15 × 32 grid and its text.</summary>
/// <param name="Row">Row, 1 (top) to 15.</param>
/// <param name="Column">Column of the first character, 1 to 32 (CEA-608 numbers columns from 1).</param>
/// <param name="Text">The characters, spaces included, without trailing spaces.</param>
/// <param name="Spans">Styled runs in order; empty for plain white text.</param>
public sealed record Cea608Row(int Row, int Column, string Text, IReadOnlyList<Cea608Span> Spans);

/// <summary>A caption screen: what was displayed from <see cref="Start"/> to <see cref="End"/> on one channel.</summary>
/// <param name="Channel">The channel.</param>
/// <param name="Start">When the screen appeared. For roll-up and paint-on text this is when the first row of the screen was written or when the screen scrolled; text added afterwards to the same screen is merged into it (see <c>docs/captions.md</c>).</param>
/// <param name="End">When the screen was erased or replaced.</param>
/// <param name="Rows">Rows from top to bottom.</param>
public sealed record Cea608Caption(Cea608Channel Channel, MediaTime Start, MediaTime End, IReadOnlyList<Cea608Row> Rows)
{
    /// <summary>How the screen was revealed: pop-on, roll-up or paint-on (<see cref="CaptionMode.None"/> for text channels).</summary>
    public CaptionMode Mode { get; init; }

    /// <summary>The rows' text joined with line feeds.</summary>
    public string Text => string.Join('\n', Rows.Select(r => r.Text));
}

/// <summary>An extended data services packet (CEA-608 §9) from field 2.</summary>
/// <param name="Time">When the packet ended.</param>
/// <param name="Class">Packet class 1–15 (1 = current program, 2 = future, 3 = channel, 4 = miscellaneous, 5 = public service, 7 = reserved, 8 = undefined).</param>
/// <param name="Type">The type code within the class.</param>
/// <param name="Data">The data bytes without class, type, end flag and checksum, parity removed.</param>
/// <param name="ChecksumValid">True when the packet's bytes sum to zero modulo 128.</param>
public sealed record XdsPacket(MediaTime Time, int Class, int Type, byte[] Data, bool ChecksumValid)
{
    /// <summary>The program name for a current-program packet of type 3 (characters 0x20–0x7F), else null.</summary>
    public string? ProgramName => Class == 1 && Type == 3 ? Cea608Text.Ascii(Data) : null;

    /// <summary>The network name for a channel-class packet of type 1, else null.</summary>
    public string? NetworkName => Class == 3 && Type == 1 ? Cea608Text.Ascii(Data) : null;
}

internal static class Cea608Text
{
    public static string Ascii(byte[] data)
    {
        var sb = new System.Text.StringBuilder(data.Length);
        foreach (byte b in data)
        {
            if (b >= 0x20)
            {
                sb.Append(Basic(b));
            }
        }

        return sb.ToString().TrimEnd();
    }

    // 47 CFR 79.101(g), standard characters table: ASCII with these substitutions (7Fh is the solid block).
    public static char Basic(int b) => b switch
    {
        0x2a => 'á',
        0x5c => 'é',
        0x5e => 'í',
        0x5f => 'ó',
        0x60 => 'ú',
        0x7b => 'ç',
        0x7c => '÷',
        0x7d => 'Ñ',
        0x7e => 'ñ',
        0x7f => '■',
        _ => (char)b,
    };

    // 47 CFR 79.101(g), special characters table: b1 = 0x11/0x19, b2 = 0x30–0x3F (39h is the transparent space).
    private const string Special = "®°½¿™¢£♪à èâêîôû";

    // Extended Western European, first byte 0x12/0x1A, second 0x20–0x3F. Not in 47 CFR 79.101; checked against two decoders only (docs/captions.md).
    private const string Extended1 = "ÁÉÓÚÜü‘¡*’—©℠•“”ÀÂÇÈÊËëÎÏïÔÙùÛ«»";

    // Extended Western European, first byte 0x13/0x1B, second 0x20–0x3F. Code 0x37 is ¦: ffmpeg 7.0.2 and ccextractor 0.96.5 both decode it so, and no
    // table we may read says otherwise (see docs/captions.md, clean-room record).
    private const string Extended2 = "ÃãÍÌìÒòÕõ{}\\^_|~ÄäÖöß¥¤¦ÅåØø┌┐└┘";

    public static char SpecialChar(int b2) => Special[b2 - 0x30];

    public static char ExtendedChar(int b1, int b2) => (b1 & 7) == 2 ? Extended1[b2 - 0x20] : Extended2[b2 - 0x20];
}
