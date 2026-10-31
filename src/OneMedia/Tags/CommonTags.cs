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
    private readonly Dictionary<CommonField, List<CommonValue>> _cache = [];

    /// <summary>Builds the view over native tags.</summary>
    /// <param name="nativeTags">The native tags of the movie (track-level tags are ignored).</param>
    /// <exception cref="ArgumentNullException"><paramref name="nativeTags"/> is null.</exception>
    public CommonTags(IReadOnlyList<NativeTag> nativeTags)
    {
        ArgumentNullException.ThrowIfNull(nativeTags);
        _native = nativeTags;
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
                foreach (NativeTag tag in _native)
                {
                    if (tag.TrackId == 0 && tag.System == row.System && tag.Key == key && ValueOf(field, tag) is { } text)
                    {
                        list.Add(new CommonValue(field, text, tag));
                    }
                }
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
                if (tag.TrackId == 0 && tag.Kind == TagValueKind.Image)
                {
                    list.Add(new TagPicture(tag, PictureType.FrontCover, tag.MimeType ?? "application/octet-stream"));
                }
            }

            return list;
        }
    }

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
