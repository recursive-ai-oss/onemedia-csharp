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
    private const string DvbDoc = "ETSI EN 300 468 §6.2.33 (service descriptor) and §6.2.37 (short event descriptor)";
    private const string ExifToolH264 = "ExifTool H264 tags (MDPM)";
    private const string FlvDoc = "ExifTool FLV tag documentation (Flash group: onMetaData properties) as a table of names; Adobe Flash Video File Format Specification 10.1, Annex E";
    private const string VorbisDoc = "Vorbis comment field names (Xiph, Ogg Vorbis I §5.2.2); Picard tag map, Vorbis comments";
    private const string XmpDoc = "XMP specification part 1 and 2 (Dublin Core and XMP basic properties)";
    private const string AsfDoc = "ExifTool ASF tags (ContentDescr, ExtendedDescr); Picard tag map, ASF/Windows Media";
    private const string PicardId3 = "Picard tag map, ID3v2.3/2.4 column (CC0)";
    private const string PicardApe = "Picard tag map, APEv2 column (CC0)";
    private const string ExifToolId3 = "ExifTool ID3 tags";
    private const string Id3v1Doc = "ID3v1 and v1.1 (id3.org)";
    private const string Lyrics3Doc = "Lyrics3 v2.00 (id3.org)";
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
        Row(CommonField.Title, TagSystems.Asf, AsfDoc, "Content Description title", "Title"),
        Row(CommonField.Artist, TagSystems.Asf, AsfDoc, "Content Description author; WM/Author is the same attribute in the extended description", "Author", "WM/Author"),
        Row(CommonField.AlbumArtist, TagSystems.Asf, AsfDoc, "", "WM/AlbumArtist"),
        Row(CommonField.Album, TagSystems.Asf, AsfDoc, "", "WM/AlbumTitle"),
        Row(CommonField.Composer, TagSystems.Asf, AsfDoc, "", "WM/Composer"),
        Row(CommonField.Director, TagSystems.Asf, AsfDoc, "", "WM/Director"),
        Row(CommonField.Genre, TagSystems.Asf, AsfDoc, "", "WM/Genre"),
        Row(CommonField.Description, TagSystems.Asf, AsfDoc, "Content Description description; ExifTool and MediaInfo also call it Description", "Description"),
        Row(CommonField.Date, TagSystems.Asf, AsfDoc, "WM/Year holds a year or a date as the writer typed it", "WM/Year"),
        Row(CommonField.Copyright, TagSystems.Asf, AsfDoc, "Content Description copyright", "Copyright"),
        Row(CommonField.Encoder, TagSystems.Asf, AsfDoc, "", "WM/ToolName", "WM/EncodingSettings"),
        Row(CommonField.EncodedBy, TagSystems.Asf, AsfDoc, "", "WM/EncodedBy"),
        Row(CommonField.Grouping, TagSystems.Asf, AsfDoc, "", "WM/ContentGroupDescription"),
        Row(CommonField.Lyrics, TagSystems.Asf, AsfDoc, "", "WM/Lyrics"),
        Row(CommonField.Rating, TagSystems.Asf, AsfDoc, "WM/SharedUserRating is a number from 0 to 99; Rating is the Content Description text", "WM/SharedUserRating", "Rating"),
        Row(CommonField.TrackNumber, TagSystems.Asf, AsfDoc, "WM/TrackNumber, optionally written as number/total", "WM/TrackNumber"),
        Row(CommonField.TrackTotal, TagSystems.Asf, AsfDoc, "the part after the slash of WM/TrackNumber", "WM/TrackNumber"),
        Row(CommonField.DiscNumber, TagSystems.Asf, AsfDoc, "WM/PartOfSet, optionally written as number/total", "WM/PartOfSet"),
        Row(CommonField.DiscTotal, TagSystems.Asf, AsfDoc, "the part after the slash of WM/PartOfSet", "WM/PartOfSet"),

        Row(CommonField.Title, MpegTs.MpegTsTagSystems.ServiceInformation, DvbDoc, "the present event's name first, then the service name", "event_name", "service_name"),
        Row(CommonField.Description, MpegTs.MpegTsTagSystems.ServiceInformation, DvbDoc, "the present event's short text", "event_text"),
        Row(CommonField.Date, MpegTs.MpegTsTagSystems.CameraData, ExifToolH264, "the camera clock in AVCHD; local time of unknown zone", "DateTimeOriginal"),

        // ID3v2. The v2.2 identifier of each frame follows the v2.3/2.4 one; text values of several strings are joined with "; ".
        Row(CommonField.Title, TagSystems.Id3v2, PicardId3, "", "TIT2", "TT2"),
        Row(CommonField.Artist, TagSystems.Id3v2, PicardId3, "", "TPE1", "TP1"),
        Row(CommonField.AlbumArtist, TagSystems.Id3v2, PicardId3, "", "TPE2", "TP2"),
        Row(CommonField.Album, TagSystems.Id3v2, PicardId3, "", "TALB", "TAL"),
        Row(CommonField.Composer, TagSystems.Id3v2, PicardId3, "", "TCOM", "TCM"),
        Row(CommonField.Genre, TagSystems.Id3v2, PicardId3, "content type: (17) references, numbers and RX/CR are resolved to names (ID3v2.3 §4.2.1)", "TCON", "TCO"),
        Row(CommonField.Comment, TagSystems.Id3v2, PicardId3, "the comment with an empty description; other descriptions stay on the native list", "COMM", "COM"),
        Row(CommonField.Date, TagSystems.Id3v2, PicardId3, "2.4 recording time; 2.3 and 2.2 hold only the year here (the day and month are in TDAT)", "TDRC", "TYER", "TYE"),
        Row(CommonField.Copyright, TagSystems.Id3v2, PicardId3, "", "TCOP", "TCR"),
        Row(CommonField.Encoder, TagSystems.Id3v2, ExifToolId3, "TSSE is the software and settings used for encoding", "TSSE", "TSS"),
        Row(CommonField.EncodedBy, TagSystems.Id3v2, PicardId3, "", "TENC", "TEN"),
        Row(CommonField.Grouping, TagSystems.Id3v2, PicardId3, "iTunes writes GRP1; TIT1 is the content group description", "GRP1", "GP1", "TIT1", "TT1"),
        Row(CommonField.Lyrics, TagSystems.Id3v2, PicardId3, "unsynchronised lyrics", "USLT", "ULT"),
        Row(CommonField.TrackNumber, TagSystems.Id3v2, PicardId3, "the part before the slash of TRCK", "TRCK", "TRK"),
        Row(CommonField.TrackTotal, TagSystems.Id3v2, PicardId3, "the part after the slash of TRCK", "TRCK", "TRK"),
        Row(CommonField.DiscNumber, TagSystems.Id3v2, PicardId3, "the part before the slash of TPOS", "TPOS", "TPA"),
        Row(CommonField.DiscTotal, TagSystems.Id3v2, PicardId3, "the part after the slash of TPOS", "TPOS", "TPA"),
        Row(CommonField.ReplayGainTrackGain, TagSystems.Id3v2, PicardId3, "user text frames; the description is compared without regard to case", "TXXX:REPLAYGAIN_TRACK_GAIN", "TXX:REPLAYGAIN_TRACK_GAIN"),
        Row(CommonField.ReplayGainTrackPeak, TagSystems.Id3v2, PicardId3, "", "TXXX:REPLAYGAIN_TRACK_PEAK", "TXX:REPLAYGAIN_TRACK_PEAK"),
        Row(CommonField.ReplayGainAlbumGain, TagSystems.Id3v2, PicardId3, "", "TXXX:REPLAYGAIN_ALBUM_GAIN", "TXX:REPLAYGAIN_ALBUM_GAIN"),
        Row(CommonField.ReplayGainAlbumPeak, TagSystems.Id3v2, PicardId3, "", "TXXX:REPLAYGAIN_ALBUM_PEAK", "TXX:REPLAYGAIN_ALBUM_PEAK"),

        // APEv2. Keys are compared without regard to case, as the specification recommends for readers.
        Row(CommonField.Title, TagSystems.Ape, PicardApe, "", "Title"),
        Row(CommonField.Artist, TagSystems.Ape, PicardApe, "", "Artist"),
        Row(CommonField.AlbumArtist, TagSystems.Ape, PicardApe, "", "Album Artist", "AlbumArtist"),
        Row(CommonField.Album, TagSystems.Ape, PicardApe, "", "Album"),
        Row(CommonField.Composer, TagSystems.Ape, PicardApe, "", "Composer"),
        Row(CommonField.Genre, TagSystems.Ape, PicardApe, "", "Genre"),
        Row(CommonField.Comment, TagSystems.Ape, PicardApe, "", "Comment"),
        Row(CommonField.Date, TagSystems.Ape, PicardApe, "", "Year", "Date"),
        Row(CommonField.Copyright, TagSystems.Ape, PicardApe, "", "Copyright"),
        Row(CommonField.Grouping, TagSystems.Ape, PicardApe, "", "Grouping"),
        Row(CommonField.Lyrics, TagSystems.Ape, PicardApe, "", "Lyrics", "Unsynced Lyrics"),
        Row(CommonField.EncodedBy, TagSystems.Ape, PicardApe, "", "EncodedBy"),
        Row(CommonField.TrackNumber, TagSystems.Ape, PicardApe, "the part before the slash", "Track"),
        Row(CommonField.TrackTotal, TagSystems.Ape, PicardApe, "the part after the slash", "Track"),
        Row(CommonField.DiscNumber, TagSystems.Ape, PicardApe, "the part before the slash", "Disc"),
        Row(CommonField.DiscTotal, TagSystems.Ape, PicardApe, "the part after the slash", "Disc"),
        Row(CommonField.ReplayGainTrackGain, TagSystems.Ape, PicardApe, "", "REPLAYGAIN_TRACK_GAIN"),
        Row(CommonField.ReplayGainTrackPeak, TagSystems.Ape, PicardApe, "", "REPLAYGAIN_TRACK_PEAK"),
        Row(CommonField.ReplayGainAlbumGain, TagSystems.Ape, PicardApe, "", "REPLAYGAIN_ALBUM_GAIN"),
        Row(CommonField.ReplayGainAlbumPeak, TagSystems.Ape, PicardApe, "", "REPLAYGAIN_ALBUM_PEAK"),

        // ID3v1 and v1.1: the fixed fields. After APEv2, so that its longer, untruncated values win when both tags are present.
        Row(CommonField.Title, TagSystems.Id3v1, Id3v1Doc, "", "title"),
        Row(CommonField.Artist, TagSystems.Id3v1, Id3v1Doc, "", "artist"),
        Row(CommonField.Album, TagSystems.Id3v1, Id3v1Doc, "", "album"),
        Row(CommonField.Comment, TagSystems.Id3v1, Id3v1Doc, "", "comment"),
        Row(CommonField.Date, TagSystems.Id3v1, Id3v1Doc, "the year", "year"),
        Row(CommonField.Genre, TagSystems.Id3v1, Id3v1Doc, "the genre number, resolved through the ID3v1 list with the Winamp extensions", "genre"),
        Row(CommonField.TrackNumber, TagSystems.Id3v1, Id3v1Doc, "ID3v1.1 only", "track"),

        // Lyrics3 v2.00 fields.
        Row(CommonField.Title, TagSystems.Lyrics3, Lyrics3Doc, "extended title", "ETT"),
        Row(CommonField.Artist, TagSystems.Lyrics3, Lyrics3Doc, "extended artist", "EAR"),
        Row(CommonField.Album, TagSystems.Lyrics3, Lyrics3Doc, "extended album", "EAL"),
        Row(CommonField.Lyrics, TagSystems.Lyrics3, Lyrics3Doc, "version 2.00 field LYR; the whole block in version 1", "LYR", "LYRICS"),

        Row(CommonField.Title, TagSystems.VorbisComment, VorbisDoc, "", "TITLE"),
        Row(CommonField.Artist, TagSystems.VorbisComment, VorbisDoc, "PERFORMER is the spec's name for the performing artist", "ARTIST", "PERFORMER"),
        Row(CommonField.AlbumArtist, TagSystems.VorbisComment, VorbisDoc, "", "ALBUMARTIST", "ALBUM ARTIST"),
        Row(CommonField.Album, TagSystems.VorbisComment, VorbisDoc, "", "ALBUM"),
        Row(CommonField.Composer, TagSystems.VorbisComment, VorbisDoc, "", "COMPOSER"),
        Row(CommonField.Author, TagSystems.VorbisComment, VorbisDoc, "lyricist", "LYRICIST"),
        Row(CommonField.Director, TagSystems.VorbisComment, VorbisDoc, "", "DIRECTOR"),
        Row(CommonField.Genre, TagSystems.VorbisComment, VorbisDoc, "", "GENRE"),
        Row(CommonField.Comment, TagSystems.VorbisComment, VorbisDoc, "", "COMMENT"),
        Row(CommonField.Description, TagSystems.VorbisComment, VorbisDoc, "the spec's short description of the contents", "DESCRIPTION"),
        Row(CommonField.Date, TagSystems.VorbisComment, VorbisDoc, "a year or an ISO 8601 date, as written", "DATE"),
        Row(CommonField.Copyright, TagSystems.VorbisComment, VorbisDoc, "", "COPYRIGHT"),
        Row(CommonField.Encoder, TagSystems.VorbisComment, VorbisDoc, "", "ENCODER"),
        Row(CommonField.EncodedBy, TagSystems.VorbisComment, VorbisDoc, "", "ENCODEDBY", "ENCODED-BY"),
        Row(CommonField.Grouping, TagSystems.VorbisComment, VorbisDoc, "", "GROUPING"),
        Row(CommonField.Lyrics, TagSystems.VorbisComment, VorbisDoc, "", "LYRICS", "UNSYNCEDLYRICS"),
        Row(CommonField.Rating, TagSystems.VorbisComment, VorbisDoc, "", "RATING"),
        Row(CommonField.TrackNumber, TagSystems.VorbisComment, VorbisDoc, "the spec's TRACKNUMBER may read number/total", "TRACKNUMBER", "TRACK"),
        Row(CommonField.TrackTotal, TagSystems.VorbisComment, VorbisDoc, "TRACKTOTAL, TOTALTRACKS, or the part after the slash of TRACKNUMBER", "TRACKTOTAL", "TOTALTRACKS", "TRACKNUMBER"),
        Row(CommonField.DiscNumber, TagSystems.VorbisComment, VorbisDoc, "may read number/total", "DISCNUMBER"),
        Row(CommonField.DiscTotal, TagSystems.VorbisComment, VorbisDoc, "DISCTOTAL, TOTALDISCS, or the part after the slash of DISCNUMBER", "DISCTOTAL", "TOTALDISCS", "DISCNUMBER"),
        Row(CommonField.Title, TagSystems.Flv, FlvDoc, "", "title"),
        Row(CommonField.Artist, TagSystems.Flv, FlvDoc, "", "artist"),
        Row(CommonField.Author, TagSystems.Flv, FlvDoc, "", "author"),
        Row(CommonField.Album, TagSystems.Flv, FlvDoc, "", "album"),
        Row(CommonField.Genre, TagSystems.Flv, FlvDoc, "", "genre"),
        Row(CommonField.Comment, TagSystems.Flv, FlvDoc, "", "comment"),
        Row(CommonField.Description, TagSystems.Flv, FlvDoc, "", "description"),
        Row(CommonField.Copyright, TagSystems.Flv, FlvDoc, "", "copyright"),
        Row(CommonField.Date, TagSystems.Flv, FlvDoc, "as written by the muxer", "creationdate", "date"),
        Row(CommonField.Encoder, TagSystems.Flv, FlvDoc, "the muxer's name and version", "encoder", "metadatacreator"),
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
