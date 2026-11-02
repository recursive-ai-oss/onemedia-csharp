using System.Text;

namespace RecursiveAI.OneMedia.Captions.Teletext;

/// <summary>The type of a page announced by a teletext descriptor (ETSI EN 300 468 §6.2.43, table 94).</summary>
public enum TeletextPageType
{
    /// <summary>Initial teletext page (1).</summary>
    Initial = 1,

    /// <summary>Teletext subtitle page (2).</summary>
    Subtitle = 2,

    /// <summary>Additional information page (3).</summary>
    AdditionalInformation = 3,

    /// <summary>Programme schedule page (4).</summary>
    ProgrammeSchedule = 4,

    /// <summary>Teletext subtitle page for hearing-impaired people (5).</summary>
    SubtitleHearingImpaired = 5,
}

/// <summary>One entry of a PMT teletext descriptor: a page a service announces.</summary>
/// <param name="Language">ISO 639-2 language code as written, lower case (three letters), or null when blank.</param>
/// <param name="Type">The announced page type; values outside <see cref="TeletextPageType"/> are kept as numbers.</param>
/// <param name="Page">The announced page address.</param>
public readonly record struct TeletextService(string? Language, TeletextPageType Type, TeletextPageAddress Page)
{
    /// <summary>True for the subtitle types (2 and 5).</summary>
    public bool IsSubtitle => Type is TeletextPageType.Subtitle or TeletextPageType.SubtitleHearingImpaired;

    /// <summary>
    /// Reads the teletext descriptors (tag 0x56) and VBI teletext descriptors (tag 0x46) of an ES_info loop.
    /// </summary>
    /// <param name="descriptors">The descriptor bytes of one elementary stream.</param>
    /// <returns>The announced pages, in order; malformed trailing bytes are ignored.</returns>
    public static IReadOnlyList<TeletextService> Parse(ReadOnlySpan<byte> descriptors)
    {
        List<TeletextService> list = [];
        int pos = 0;
        while (pos + 2 <= descriptors.Length)
        {
            int tag = descriptors[pos];
            int length = descriptors[pos + 1];
            pos += 2;
            if (pos + length > descriptors.Length)
            {
                break;
            }

            if (tag is 0x56 or 0x46)
            {
                // EN 300 468 §6.2.43: per entry ISO_639_language_code (24 bits), teletext_type (5), teletext_magazine_number (3),
                // teletext_page_number (8, two BCD digits). Magazine number 0 means magazine 8.
                ReadOnlySpan<byte> body = descriptors.Slice(pos, length);
                for (int i = 0; i + 5 <= body.Length; i += 5)
                {
                    string lang = Encoding.ASCII.GetString(body.Slice(i, 3)).ToLowerInvariant();
                    bool blank = lang.Trim('\0', ' ').Length == 0;
                    int type = body[i + 3] >> 3;
                    int magazine = body[i + 3] & 7;
                    list.Add(new TeletextService(blank ? null : lang, (TeletextPageType)type, new TeletextPageAddress(magazine == 0 ? 8 : magazine, body[i + 4])));
                }
            }

            pos += length;
        }

        return list;
    }
}
