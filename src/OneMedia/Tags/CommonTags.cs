using System.Text;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>The fields of the common tag view.</summary>
public enum CommonField
{
    /// <summary>Title of the work or recording.</summary>
    Title,

    /// <summary>Artist or performer.</summary>
    Artist,

    /// <summary>Artist of the whole album.</summary>
    AlbumArtist,

    /// <summary>Album title.</summary>
    Album,

    /// <summary>Composer.</summary>
    Composer,

    /// <summary>Author (QuickTime and 3GPP name the writer of the content this way).</summary>
    Author,

    /// <summary>Director.</summary>
    Director,

    /// <summary>Genre name.</summary>
    Genre,

    /// <summary>Free comment.</summary>
    Comment,

    /// <summary>Description or synopsis.</summary>
    Description,

    /// <summary>Release or recording date as written (a year, a date or a date-time).</summary>
    Date,

    /// <summary>Copyright notice.</summary>
    Copyright,

    /// <summary>The tool that encoded the file.</summary>
    Encoder,

    /// <summary>Who encoded the file.</summary>
    EncodedBy,

    /// <summary>Grouping or work name.</summary>
    Grouping,

    /// <summary>Lyrics.</summary>
    Lyrics,

    /// <summary>Content rating as text.</summary>
    Rating,

    /// <summary>Keywords.</summary>
    Keywords,

    /// <summary>Track number within the disc.</summary>
    TrackNumber,

    /// <summary>Number of tracks on the disc.</summary>
    TrackTotal,

    /// <summary>Disc number.</summary>
    DiscNumber,

    /// <summary>Number of discs.</summary>
    DiscTotal,

    /// <summary>ReplayGain track gain, as written (for example <c>-6.50 dB</c>).</summary>
    ReplayGainTrackGain,

    /// <summary>ReplayGain track peak, as written (for example <c>0.988525</c>).</summary>
    ReplayGainTrackPeak,

    /// <summary>ReplayGain album gain, as written.</summary>
    ReplayGainAlbumGain,

    /// <summary>ReplayGain album peak, as written.</summary>
    ReplayGainAlbumPeak,
}

/// <summary>One value of a common field and the native tag it came from.</summary>
/// <param name="Field">The field.</param>
/// <param name="Text">The value as text (numbers in decimal).</param>
/// <param name="Source">The native tag; its <see cref="NativeTag.System"/>, <see cref="NativeTag.Key"/> and ranges say where in the file.</param>
public sealed record CommonValue(CommonField Field, string Text, NativeTag Source);

/// <summary>The role of a picture (the ID3v2 attached-picture list, which Vorbis comments and FLAC share).</summary>
public enum PictureType
{
    /// <summary>Other.</summary>
    Other = 0,

    /// <summary>32×32 file icon.</summary>
    FileIcon,

    /// <summary>Other file icon.</summary>
    OtherFileIcon,

    /// <summary>Front cover.</summary>
    FrontCover,

    /// <summary>Back cover.</summary>
    BackCover,

    /// <summary>Leaflet page.</summary>
    Leaflet,

    /// <summary>Media (CD label side).</summary>
    Media,

    /// <summary>Lead artist.</summary>
    LeadArtist,

    /// <summary>Artist.</summary>
    Artist,

    /// <summary>Conductor.</summary>
    Conductor,

    /// <summary>Band.</summary>
    Band,

    /// <summary>Composer.</summary>
    Composer,

    /// <summary>Lyricist.</summary>
    Lyricist,

    /// <summary>Recording location.</summary>
    RecordingLocation,

    /// <summary>During recording.</summary>
    DuringRecording,

    /// <summary>During performance.</summary>
    DuringPerformance,

    /// <summary>Movie or video screen capture.</summary>
    VideoCapture,

    /// <summary>A bright coloured fish.</summary>
    BrightFish,

    /// <summary>Illustration.</summary>
    Illustration,

    /// <summary>Band logotype.</summary>
    BandLogo,

    /// <summary>Publisher logotype.</summary>
    PublisherLogo,
}

/// <summary>A picture in the file (cover art). The bytes are read only by <see cref="ReadData"/>.</summary>
public sealed class TagPicture
{
    internal TagPicture(NativeTag source, PictureType type, string mimeType)
    {
        Source = source;
        Type = type;
        MimeType = mimeType;
    }

    /// <summary>The native tag that holds the picture.</summary>
    public NativeTag Source { get; }

    /// <summary>The role; MP4 and QuickTime do not say, so this is <see cref="PictureType.FrontCover"/>.</summary>
    public PictureType Type { get; }

    /// <summary>The media type (<c>image/jpeg</c>, <c>image/png</c>…); <c>application/octet-stream</c> when the file does not declare one.</summary>
    public string MimeType { get; }

    /// <summary>The picture's size in bytes.</summary>
    public long Length => Source.ValueRange.Length;

    /// <summary>Reads the picture bytes.</summary>
    /// <returns>The bytes.</returns>
    /// <exception cref="MediaLimitExceededException">The picture is larger than <see cref="MediaLimits.MaxAttachmentBytes"/>.</exception>
    public byte[] ReadData() => Source.ReadValue();
}

/// <summary>
/// The common tag view: typed access to the usual fields, picked from the native tags by the published mapping table
/// (<see cref="TagMappingTable"/>). It is a view: nothing is copied out of the native layers and nothing here is stored in the file.
/// Only movie-level tags feed it; tags of a single track stay on the native list.
/// </summary>
public sealed class CommonTags
{
    private readonly IReadOnlyList<NativeTag> _native;
    private readonly bool _singleTrack;
    private readonly Dictionary<CommonField, List<CommonValue>> _cache = [];

    /// <summary>Builds the view over native tags.</summary>
    /// <param name="nativeTags">The native tags of the movie (track-level tags are ignored).</param>
    /// <exception cref="ArgumentNullException"><paramref name="nativeTags"/> is null.</exception>
    public CommonTags(IReadOnlyList<NativeTag> nativeTags) : this(nativeTags, false)
    {
    }

    /// <summary>Builds the view over native tags, optionally treating the tags of a file's only track as the file's own.</summary>
    /// <param name="nativeTags">The native tags.</param>
    /// <param name="singleTrack">
    /// True when the file has exactly one track: Matroska tags that target that track (an audio file tagged per track, say) then feed the view,
    /// as ExifTool and MediaInfo show them. With several tracks they stay on the native list.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="nativeTags"/> is null.</exception>
    public CommonTags(IReadOnlyList<NativeTag> nativeTags, bool singleTrack)
    {
        ArgumentNullException.ThrowIfNull(nativeTags);
        _native = nativeTags;
        _singleTrack = singleTrack;
    }

    /// <summary>
    /// Every value of a field in precedence order: mapping rows in table order, and within a row the keys in the order listed.
    /// Languages other than the first are kept as further values.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <returns>The values; empty when the file has none.</returns>
    public IReadOnlyList<CommonValue> Values(CommonField field)
    {
        if (_cache.TryGetValue(field, out List<CommonValue>? cached))
        {
            return cached;
        }

        List<CommonValue> list = [];
        foreach (TagMappingRow row in TagMappingTable.Rows)
        {
            if (row.Field != field)
            {
                continue;
            }

            foreach (string key in row.Keys)
            {
                List<CommonValue> found = [];
                foreach (NativeTag tag in _native)
                {
                    bool keyMatches = row.System is TagSystems.Matroska or TagSystems.MatroskaInfo or TagSystems.Ape or TagSystems.Id3v2 or TagSystems.VorbisComment or TagSystems.Flv ? string.Equals(tag.Key, key, StringComparison.OrdinalIgnoreCase) : tag.Key == key;
                    if (IsFileLevel(tag, field) && tag.System == row.System && keyMatches && ValueOf(field, tag) is { } text && !(text.Length == 0 && tag.System == TagSystems.Asf))
                    {
                        found.Add(new CommonValue(field, text, tag));
                    }
                }

                if (row.System == TagSystems.Matroska)
                {
                    // Matroska levels: 30 (track) and above are preferred, lowest first, so a track title beats its album's; levels below 30 come last.
                    // Within a level, a value with no language or "und" comes before the language-specific ones (stable order).
                    SortStable(found);
                }

                list.AddRange(found);
            }
        }

        _cache[field] = list;
        return list;
    }

    /// <summary>The first value of a field, or <c>null</c>.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The text.</returns>
    public string? First(CommonField field)
    {
        IReadOnlyList<CommonValue> v = Values(field);
        return v.Count > 0 ? v[0].Text : null;
    }

    /// <summary>The title.</summary>
    public string? Title => First(CommonField.Title);

    /// <summary>The artist.</summary>
    public string? Artist => First(CommonField.Artist);

    /// <summary>The album artist.</summary>
    public string? AlbumArtist => First(CommonField.AlbumArtist);

    /// <summary>The album.</summary>
    public string? Album => First(CommonField.Album);

    /// <summary>The composer.</summary>
    public string? Composer => First(CommonField.Composer);

    /// <summary>The author.</summary>
    public string? Author => First(CommonField.Author);

    /// <summary>The director.</summary>
    public string? Director => First(CommonField.Director);

    /// <summary>The genre.</summary>
    public string? Genre => First(CommonField.Genre);

    /// <summary>The comment.</summary>
    public string? Comment => First(CommonField.Comment);

    /// <summary>The description.</summary>
    public string? Description => First(CommonField.Description);

    /// <summary>The date as written.</summary>
    public string? Date => First(CommonField.Date);

    /// <summary>The copyright notice.</summary>
    public string? Copyright => First(CommonField.Copyright);

    /// <summary>The encoding tool.</summary>
    public string? Encoder => First(CommonField.Encoder);

    /// <summary>Who encoded the file.</summary>
    public string? EncodedBy => First(CommonField.EncodedBy);

    /// <summary>The grouping.</summary>
    public string? Grouping => First(CommonField.Grouping);

    /// <summary>The lyrics.</summary>
    public string? Lyrics => First(CommonField.Lyrics);

    /// <summary>The rating text.</summary>
    public string? Rating => First(CommonField.Rating);

    /// <summary>The keywords, one per value.</summary>
    public IReadOnlyList<CommonValue> Keywords => Values(CommonField.Keywords);

    /// <summary>The track number, or <c>null</c>.</summary>
    public int? TrackNumber => Number(CommonField.TrackNumber);

    /// <summary>The track total, or <c>null</c>.</summary>
    public int? TrackTotal => Number(CommonField.TrackTotal);

    /// <summary>The disc number, or <c>null</c>.</summary>
    public int? DiscNumber => Number(CommonField.DiscNumber);

    /// <summary>The disc total, or <c>null</c>.</summary>
    public int? DiscTotal => Number(CommonField.DiscTotal);

    /// <summary>The pictures in the movie-level tags, in file order.</summary>
    public IReadOnlyList<TagPicture> Pictures
    {
        get
        {
            List<TagPicture> list = [];
            foreach (NativeTag tag in _native)
            {
                if (IsFileLevel(tag, null) && tag.Kind == TagValueKind.Image)
                {
                    // MP4 and QuickTime do not name the picture's role; Matroska names cover art by file name (cover.jpg, small_cover.png…).
                    bool cover = tag.System != TagSystems.Matroska || tag.Key.StartsWith("cover", StringComparison.OrdinalIgnoreCase) || tag.Key.StartsWith("small_cover", StringComparison.OrdinalIgnoreCase);
                    PictureType type = tag.PictureRole ?? (tag.PictureTypeNumber is >= 0 and <= (int)PictureType.PublisherLogo ? (PictureType)tag.PictureTypeNumber.Value : cover ? PictureType.FrontCover : PictureType.Other);
                    list.Add(new TagPicture(tag, type, tag.MimeType ?? "application/octet-stream"));
                }
            }

            return list;
        }
    }

    // Insertion sort: stable, and the lists are a handful of entries.
    private static void SortStable(List<CommonValue> list)
    {
        for (int i = 1; i < list.Count; i++)
        {
            CommonValue item = list[i];
            (int scope, long level, int language) = (ScopeRank(item.Source), LevelRank(item.Source), LanguageRank(item.Source));
            int j = i - 1;
            while (j >= 0 && (ScopeRank(list[j].Source), LevelRank(list[j].Source), LanguageRank(list[j].Source)).CompareTo((scope, level, language)) > 0)
            {
                list[j + 1] = list[j];
                j--;
            }

            list[j + 1] = item;
        }
    }

    // A track's tags count as the file's own only for a single-track Matroska file.
    // The encoder tag of a track names that stream's encoder, not the file's, so it never stands in for the file's.
    private bool IsFileLevel(NativeTag t, CommonField? field) => t.TrackId == 0 || (_singleTrack && t.TrackId > 0 && t.System == TagSystems.Matroska && field != CommonField.Encoder);

    // Tags of the file itself come before tags of its only track.
    private static int ScopeRank(NativeTag t) => t.TrackId == 0 ? 0 : 1;

    private static int LanguageRank(NativeTag t) => string.IsNullOrEmpty(t.Language) || t.Language == "und" ? 0 : t.Language is "eng" or "en" ? 1 : 2;

    private static long LevelRank(NativeTag t) => t.TargetTypeValue is { } v ? (v < 30 ? 1000 + v : v) : 50;

    private int? Number(CommonField field)
    {
        IReadOnlyList<CommonValue> v = Values(field);
        return v.Count > 0 && int.TryParse(v[0].Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : null;
    }

    // Turns one native tag into the field's text. Returns null when the tag does not carry a usable value for the field.
    private static string? ValueOf(CommonField field, NativeTag tag)
    {
        switch (field)
        {
            case CommonField.TrackNumber or CommonField.TrackTotal or CommonField.DiscNumber or CommonField.DiscTotal:
                return PairValue(field, tag);
            case CommonField.Genre when tag.System == TagSystems.ITunes && tag.Key == "gnre":
                // Apple's gnre item holds the ID3v1 genre number plus one in a 16-bit integer.
                return tag.Kind == TagValueKind.Binary && tag.ValueRange.Length == 2 && tag.Number is null ? GenreFromBinary(tag) : tag.Number is { } n ? Id3Genres.Name((int)n - 1) : null;
            case CommonField.Genre when tag.System == TagSystems.ThreeGpp && tag.Kind == TagValueKind.Text:
                return tag.Text;
            case CommonField.Genre when tag.System == TagSystems.Id3v1:
                return tag.Number is { } g ? Id3Genres.Name((int)g) : null;
            case CommonField.Genre when tag.System == TagSystems.Id3v2 && tag.Kind == TagValueKind.Text:
                return Id3.Id3Genre.Resolve(tag.Text);
        }

        return tag.Kind switch
        {
            TagValueKind.Text or TagValueKind.Number or TagValueKind.Real => tag.Text,
            TagValueKind.Binary when tag.DataType == 0 && tag.System == TagSystems.ITunes => ImplicitText(tag),
            _ => null,
        };
    }

    private static string? GenreFromBinary(NativeTag tag)
    {
        byte[] v = tag.ReadValue();
        return v.Length == 2 ? Id3Genres.Name(((v[0] << 8) | v[1]) - 1) : null;
    }

    // Some writers store text with the implicit type 0; accept it when it is valid UTF-8 without control characters.
    private static string? ImplicitText(NativeTag tag)
    {
        if (tag.ValueRange.Length is 0 or > 4096)
        {
            return null;
        }

        byte[] v = tag.ReadValue();
        foreach (byte b in v)
        {
            if (b < 0x20 && b is not (9 or 10 or 13))
            {
                return null;
            }
        }

        return System.Text.Unicode.Utf8.IsValid(v) ? Encoding.UTF8.GetString(v) : null;
    }

    // trkn and disk hold eight bytes: two reserved, the number, the total, two reserved (all 16-bit big-endian).
    private static string? PairValue(CommonField field, NativeTag tag)
    {
        if (tag.System == TagSystems.ThreeGpp)
        {
            return tag.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (tag.Kind is TagValueKind.Text or TagValueKind.Number && tag.System is TagSystems.Matroska or TagSystems.Asf or TagSystems.Id3v2 or TagSystems.Id3v1 or TagSystems.Ape or TagSystems.VorbisComment or TagSystems.RiffInfo or TagSystems.CafInfo)
        {
            // Matroska writes the number as text, sometimes as "3/12" (ffmpeg's track tag); the total fields read the number after the slash or the whole text.
            string t = tag.Text ?? "";
            int slash = t.IndexOf('/', StringComparison.Ordinal);
            string part = field is CommonField.TrackNumber or CommonField.DiscNumber ? (slash < 0 ? t : t[..slash]) : (slash >= 0 ? t[(slash + 1)..] : tag.Key.Contains("TOTAL", StringComparison.OrdinalIgnoreCase) ? t : "");
            return int.TryParse(part.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value) && value > 0 ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        if (tag.Kind != TagValueKind.Binary || tag.ValueRange.Length < 6)
        {
            return null;
        }

        byte[] v = tag.ReadValue();
        if (v.Length < 6)
        {
            return null;
        }

        int index = field is CommonField.TrackNumber or CommonField.DiscNumber ? 2 : 4;
        int n = (v[index] << 8) | v[index + 1];
        return n == 0 ? null : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
