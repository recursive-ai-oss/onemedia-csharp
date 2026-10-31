using System.Text;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>
/// One line of the mapping table: which native keys of one tag system feed a common field. Keys are tried in the order listed; rows of the
/// same field are tried in table order, so the table is also the precedence.
/// </summary>
/// <param name="Field">The common field.</param>
/// <param name="System">The native tag system, one of <see cref="TagSystems"/>.</param>
/// <param name="Keys">The native keys, as <see cref="NativeTag.Key"/> spells them, in precedence order.</param>
/// <param name="Document">The document the row comes from.</param>
/// <param name="Note">How the value is read, or why the row differs from the document.</param>
public sealed record TagMappingRow(CommonField Field, string System, IReadOnlyList<string> Keys, string Document, string Note = "");

/// <summary>
/// The published mapping from native tags to the common view for MP4, MOV and 3GP. The table is data: the common view reads it, and
/// <see cref="RenderMarkdown"/> renders it for the documentation, so they cannot disagree.
/// </summary>
/// <remarks>
/// Documents: the MusicBrainz Picard tag-mapping table (iTunes MP4 column, CC0), the ExifTool QuickTime tag documentation (ItemList, UserData and Keys
/// tables, read as a table of facts), Apple's QuickTime File Format specification and 3GPP TS 26.244 §8.
/// </remarks>
public static class TagMappingTable
{
    private const string Picard = "Picard tag map, iTunes MP4";
    private const string ExifToolKeys = "ExifTool QuickTime Keys tags";
    private const string ExifToolUser = "ExifTool QuickTime UserData tags";
    private const string ThreeGppDoc = "3GPP TS 26.244 §8";
    private const string Mdta = "com.apple.quicktime.";

    private static TagMappingRow Row(CommonField f, string system, string document, string note, params string[] keys) => new(f, system, keys, document, note);

    /// <summary>The rows in precedence order: for each field, iTunes items, then key-table metadata, then QuickTime user data, then 3GPP boxes.</summary>
    public static IReadOnlyList<TagMappingRow> Rows { get; } =
    [
        Row(CommonField.Title, TagSystems.ITunes, Picard, "", "©nam"),
        Row(CommonField.Title, TagSystems.QuickTimeKeys, ExifToolKeys, "", Mdta + "title", Mdta + "displayname"),
        Row(CommonField.Title, TagSystems.QuickTimeUserData, ExifToolUser, "", "©nam"),
        Row(CommonField.Title, TagSystems.ThreeGpp, ThreeGppDoc, "", "titl"),

        Row(CommonField.Artist, TagSystems.ITunes, Picard, "", "©ART"),
        Row(CommonField.Artist, TagSystems.QuickTimeKeys, ExifToolKeys, "", Mdta + "artist"),
        Row(CommonField.Artist, TagSystems.QuickTimeUserData, ExifToolUser, "©prf is QuickTime's performer", "©ART", "©prf"),
        Row(CommonField.Artist, TagSystems.ThreeGpp, ThreeGppDoc, "performer", "perf"),

        Row(CommonField.AlbumArtist, TagSystems.ITunes, Picard, "", "aART"),

        Row(CommonField.Album, TagSystems.ITunes, Picard, "", "©alb"),
        Row(CommonField.Album, TagSystems.QuickTimeKeys, ExifToolKeys, "", Mdta + "album"),
        Row(CommonField.Album, TagSystems.QuickTimeUserData, ExifToolUser, "", "©alb"),
        Row(CommonField.Album, TagSystems.ThreeGpp, ThreeGppDoc, "album title; the optional track number feeds TrackNumber", "albm"),

        Row(CommonField.Composer, TagSystems.ITunes, Picard, "", "©wrt"),
        Row(CommonField.Composer, TagSystems.QuickTimeUserData, ExifToolUser, "©com is QuickTime's composer", "©wrt", "©com"),

        Row(CommonField.Author, TagSystems.QuickTimeKeys, ExifToolKeys, "", Mdta + "author"),
        Row(CommonField.Author, TagSystems.QuickTimeUserData, ExifToolUser, "", "©aut"),
        Row(CommonField.Author, TagSystems.ThreeGpp, ThreeGppDoc, "author", "auth"),

        Row(CommonField.Director, TagSystems.ITunes, Picard, "", "©dir"),
        Row(CommonField.Director, TagSystems.QuickTimeKeys, ExifToolKeys, "", Mdta + "director"),
        Row(CommonField.Director, TagSystems.QuickTimeUserData, ExifToolUser, "", "©dir"),

        Row(CommonField.Genre, TagSystems.ITunes, Picard, "gnre holds the ID3v1 genre number plus one", "©gen", "gnre"),
        Row(CommonField.Genre, TagSystems.QuickTimeKeys, ExifToolKeys, "", Mdta + "genre"),
        Row(CommonField.Genre, TagSystems.QuickTimeUserData, ExifToolUser, "", "©gen"),
        Row(CommonField.Genre, TagSystems.ThreeGpp, ThreeGppDoc, "", "gnre"),

        Row(CommonField.Comment, TagSystems.ITunes, Picard, "", "©cmt"),
        Row(CommonField.Comment, TagSystems.QuickTimeKeys, ExifToolKeys, "", Mdta + "comment"),
        Row(CommonField.Comment, TagSystems.QuickTimeUserData, ExifToolUser, "", "©cmt"),

        Row(CommonField.Description, TagSystems.ITunes, ExifToolUser, "desc is the short description, ldes the long one", "desc", "ldes"),
        Row(CommonField.Description, TagSystems.QuickTimeKeys, ExifToolKeys, "", Mdta + "description"),
        Row(CommonField.Description, TagSystems.QuickTimeUserData, ExifToolUser, "©inf is QuickTime's information text", "©inf"),
        Row(CommonField.Description, TagSystems.ThreeGpp, ThreeGppDoc, "", "dscp"),

        Row(CommonField.Date, TagSystems.ITunes, Picard, "stored as written, usually a year or an ISO 8601 date", "©day"),
        Row(CommonField.Date, TagSystems.QuickTimeKeys, ExifToolKeys, "", Mdta + "year"),
        Row(CommonField.Date, TagSystems.QuickTimeUserData, ExifToolUser, "", "©day"),
        Row(CommonField.Date, TagSystems.ThreeGpp, ThreeGppDoc, "recording year", "yrrc"),

        Row(CommonField.Copyright, TagSystems.ITunes, Picard, "", "cprt"),
        Row(CommonField.Copyright, TagSystems.QuickTimeKeys, ExifToolKeys, "", Mdta + "copyright"),
        Row(CommonField.Copyright, TagSystems.QuickTimeUserData, ExifToolUser, "", "©cpy", "cprt"),
        Row(CommonField.Copyright, TagSystems.ThreeGpp, ThreeGppDoc, "", "cprt"),

        Row(CommonField.Encoder, TagSystems.ITunes, ExifToolUser, "Picard files ©too under Encoded By; the atom names the writing tool, so it is the encoder here", "©too"),
        Row(CommonField.Encoder, TagSystems.QuickTimeKeys, ExifToolKeys, "", Mdta + "encoder"),

        Row(CommonField.EncodedBy, TagSystems.QuickTimeUserData, ExifToolUser, "", "©enc"),

        Row(CommonField.Grouping, TagSystems.ITunes, Picard, "", "©grp"),

        Row(CommonField.Lyrics, TagSystems.ITunes, Picard, "", "©lyr"),

        Row(CommonField.Rating, TagSystems.QuickTimeKeys, ExifToolKeys, "user rating as a number", Mdta + "rating.user"),
        Row(CommonField.Rating, TagSystems.ThreeGpp, ThreeGppDoc, "rating text", "rtng"),

        Row(CommonField.Keywords, TagSystems.QuickTimeKeys, ExifToolKeys, "", Mdta + "keywords"),
        Row(CommonField.Keywords, TagSystems.QuickTimeUserData, ExifToolUser, "", "©key"),
        Row(CommonField.Keywords, TagSystems.ThreeGpp, ThreeGppDoc, "one value per keyword", "kywd"),

        Row(CommonField.TrackNumber, TagSystems.ITunes, Picard, "trkn: 16-bit number at byte 2", "trkn"),
        Row(CommonField.TrackNumber, TagSystems.ThreeGpp, ThreeGppDoc, "optional last byte of albm", "albm:track"),
        Row(CommonField.TrackTotal, TagSystems.ITunes, Picard, "trkn: 16-bit total at byte 4", "trkn"),
        Row(CommonField.DiscNumber, TagSystems.ITunes, Picard, "disk: 16-bit number at byte 2", "disk"),
        Row(CommonField.DiscTotal, TagSystems.ITunes, Picard, "disk: 16-bit total at byte 4", "disk"),
    ];

    /// <summary>Renders the table as the Markdown published in the documentation.</summary>
    /// <returns>Markdown with one row per field and system.</returns>
    public static string RenderMarkdown()
    {
        StringBuilder sb = new();
        sb.Append("| Field | System | Keys (in precedence order) | Document | Note |\n|---|---|---|---|---|\n");
        foreach (TagMappingRow r in Rows)
        {
            sb.Append("| ").Append(r.Field).Append(" | ").Append(r.System).Append(" | ");
            for (int i = 0; i < r.Keys.Count; i++)
            {
                sb.Append(i > 0 ? ", " : string.Empty).Append('`').Append(r.Keys[i]).Append('`');
            }

            sb.Append(" | ").Append(r.Document).Append(" | ").Append(r.Note).Append(" |\n");
        }

        return sb.ToString();
    }
}
