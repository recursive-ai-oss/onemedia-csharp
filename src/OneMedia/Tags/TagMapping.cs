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
    private const string MatroskaDoc = "Matroska tag names (matroska.org/technical/tagging.html), RFC 9559 §5.1.8";
    private const string XmpDoc = "XMP specification part 1 and 2 (Dublin Core and XMP basic properties)";
    private const string Dc = "http://purl.org/dc/elements/1.1/";
    private const string Mdta = "com.apple.quicktime.";
    private const string ExifToolRiff = "ExifTool RIFF Info tags";
    private const string ExifToolAiff = "ExifTool AIFF tags";
    private const string Ebu3285 = "EBU Tech 3285 (BWF) §2";
    private const string CafSpec = "Apple Core Audio Format Specification, Information chunk";

    private static TagMappingRow Row(CommonField f, string system, string document, string note, params string[] keys) => new(f, system, keys, document, note);

    /// <summary>The rows in precedence order: for each field, iTunes items, then key-table metadata, then QuickTime user data, then 3GPP boxes, then XMP, then Matroska tags.</summary>
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
        Row(CommonField.Encoder, TagSystems.QuickTimeKeys, ExifToolKeys, "ffmpeg writes the bare key encoder", "encoder", Mdta + "encoder"),
        Row(CommonField.Encoder, TagSystems.QuickTimeUserData, ExifToolUser, "", "©too"),

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
        Row(CommonField.Title, TagSystems.Xmp, XmpDoc, "language alternative: the x-default item", Dc + "#title"),
        Row(CommonField.Artist, TagSystems.Xmp, XmpDoc, "first creator", Dc + "#creator"),
        Row(CommonField.Description, TagSystems.Xmp, XmpDoc, "", Dc + "#description"),
        Row(CommonField.Copyright, TagSystems.Xmp, XmpDoc, "", Dc + "#rights"),
        Row(CommonField.Keywords, TagSystems.Xmp, XmpDoc, "", Dc + "#subject"),
        Row(CommonField.Date, TagSystems.Xmp, XmpDoc, "", "http://ns.adobe.com/xap/1.0/#CreateDate"),
        Row(CommonField.Title, TagSystems.Matroska, MatroskaDoc, "lowest level of 30 or above first (track before album)", "TITLE"),
        Row(CommonField.Title, TagSystems.MatroskaInfo, MatroskaDoc, "Segment Info Title, after the tags", "Title"),
        Row(CommonField.Artist, TagSystems.Matroska, MatroskaDoc, "", "ARTIST", "LEAD_PERFORMER"),
        Row(CommonField.AlbumArtist, TagSystems.Matroska, MatroskaDoc, "not in the tag list; ffmpeg and others write it", "ALBUM_ARTIST"),
        Row(CommonField.Album, TagSystems.Matroska, MatroskaDoc, "not in the tag list (an album title is TITLE at level 50); ffmpeg and others write ALBUM", "ALBUM"),
        Row(CommonField.Composer, TagSystems.Matroska, MatroskaDoc, "", "COMPOSER"),
        Row(CommonField.Author, TagSystems.Matroska, MatroskaDoc, "", "WRITTEN_BY"),
        Row(CommonField.Director, TagSystems.Matroska, MatroskaDoc, "", "DIRECTOR"),
        Row(CommonField.Genre, TagSystems.Matroska, MatroskaDoc, "", "GENRE"),
        Row(CommonField.Comment, TagSystems.Matroska, MatroskaDoc, "", "COMMENT"),
        Row(CommonField.Description, TagSystems.Matroska, MatroskaDoc, "", "DESCRIPTION", "SUMMARY", "SYNOPSIS"),
        Row(CommonField.Date, TagSystems.Matroska, MatroskaDoc, "DATE is written by ffmpeg and others", "DATE_RELEASED", "DATE_RECORDED", "DATE"),
        Row(CommonField.Copyright, TagSystems.Matroska, MatroskaDoc, "", "COPYRIGHT"),
        Row(CommonField.Encoder, TagSystems.Matroska, MatroskaDoc, "Info WritingApp last", "ENCODER"),
        Row(CommonField.Encoder, TagSystems.MatroskaInfo, MatroskaDoc, "", "WritingApp"),
        Row(CommonField.EncodedBy, TagSystems.Matroska, MatroskaDoc, "", "ENCODED_BY"),
        Row(CommonField.Lyrics, TagSystems.Matroska, MatroskaDoc, "", "LYRICS"),
        Row(CommonField.Rating, TagSystems.Matroska, MatroskaDoc, "", "RATING"),
        Row(CommonField.Keywords, TagSystems.Matroska, MatroskaDoc, "", "KEYWORDS"),
        Row(CommonField.TrackNumber, TagSystems.Matroska, MatroskaDoc, "part number at the lowest level of 30 or above; ffmpeg writes TRACK, optionally as number/total", "PART_NUMBER", "TRACK"),
        Row(CommonField.DiscNumber, TagSystems.Matroska, MatroskaDoc, "ffmpeg writes DISC, optionally as number/total", "DISC"),
        Row(CommonField.DiscTotal, TagSystems.Matroska, MatroskaDoc, "the part after the slash of DISC", "DISC"),
        Row(CommonField.TrackTotal, TagSystems.Matroska, MatroskaDoc, "the part after the slash of TRACK when TOTAL_PARTS is absent", "TOTAL_PARTS", "TRACK"),
        Row(CommonField.Title, TagSystems.RiffInfo, ExifToolRiff, "", "INAM"),
        Row(CommonField.Artist, TagSystems.RiffInfo, ExifToolRiff, "", "IART"),
        Row(CommonField.Album, TagSystems.RiffInfo, ExifToolRiff, "IPRD is the product, which music files use for the album", "IPRD"),
        Row(CommonField.Composer, TagSystems.RiffInfo, ExifToolRiff, "IMUS is Music By", "IMUS"),
        Row(CommonField.Author, TagSystems.RiffInfo, ExifToolRiff, "IWRI is Written By", "IWRI"),
        Row(CommonField.Genre, TagSystems.RiffInfo, ExifToolRiff, "", "IGNR", "GENR"),
        Row(CommonField.Comment, TagSystems.RiffInfo, ExifToolRiff, "", "ICMT", "CMNT", "COMM"),
        Row(CommonField.Description, TagSystems.RiffInfo, ExifToolRiff, "ISBJ is the subject", "ISBJ"),
        Row(CommonField.Description, TagSystems.Bext, Ebu3285, "the free description of the recording", "Description"),
        Row(CommonField.Date, TagSystems.RiffInfo, ExifToolRiff, "as written, usually a year or an ISO 8601 date", "ICRD"),
        Row(CommonField.Date, TagSystems.Bext, Ebu3285, "the origination date; the time of day is the OriginationTime tag", "OriginationDate"),
        Row(CommonField.Copyright, TagSystems.RiffInfo, ExifToolRiff, "", "ICOP"),
        Row(CommonField.Encoder, TagSystems.RiffInfo, ExifToolRiff, "the software that wrote the file", "ISFT"),
        Row(CommonField.EncodedBy, TagSystems.RiffInfo, ExifToolRiff, "ITCH is the technician", "ITCH", "IENC", "CODE"),
        Row(CommonField.Keywords, TagSystems.RiffInfo, ExifToolRiff, "", "IKEY"),
        Row(CommonField.TrackNumber, TagSystems.RiffInfo, ExifToolRiff, "ITRK, optionally as number/total", "ITRK", "IPRT"),
        Row(CommonField.TrackTotal, TagSystems.RiffInfo, ExifToolRiff, "the part after the slash of ITRK", "ITRK"),
        Row(CommonField.Title, TagSystems.Aiff, ExifToolAiff, "", "NAME"),
        Row(CommonField.Artist, TagSystems.Aiff, ExifToolAiff, "AUTH names the author; players show it as the artist", "AUTH"),
        Row(CommonField.Author, TagSystems.Aiff, ExifToolAiff, "", "AUTH"),
        Row(CommonField.Copyright, TagSystems.Aiff, ExifToolAiff, "", "(c) "),
        Row(CommonField.Comment, TagSystems.Aiff, ExifToolAiff, "ANNO, then the text of the first COMT comment", "ANNO", "COMT"),
        Row(CommonField.Title, TagSystems.CafInfo, CafSpec, "", "title"),
        Row(CommonField.Artist, TagSystems.CafInfo, CafSpec, "", "artist"),
        Row(CommonField.Album, TagSystems.CafInfo, CafSpec, "", "album"),
        Row(CommonField.Composer, TagSystems.CafInfo, CafSpec, "", "composer"),
        Row(CommonField.Genre, TagSystems.CafInfo, CafSpec, "", "genre"),
        Row(CommonField.Comment, TagSystems.CafInfo, CafSpec, "", "comments", "comment"),
        Row(CommonField.Date, TagSystems.CafInfo, CafSpec, "the recording date, else the year", "recorded date", "year"),
        Row(CommonField.Copyright, TagSystems.CafInfo, CafSpec, "", "copyright"),
        Row(CommonField.Encoder, TagSystems.CafInfo, CafSpec, "", "encoding application"),
        Row(CommonField.TrackNumber, TagSystems.CafInfo, CafSpec, "the track number, optionally as number/total", "track number"),
        Row(CommonField.TrackTotal, TagSystems.CafInfo, CafSpec, "the part after the slash of the track number", "track number"),
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
