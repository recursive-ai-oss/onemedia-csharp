using System.Buffers.Binary;
using System.Globalization;
using RecursiveAI.OneMedia.Tags;
using RecursiveAI.OneMedia.Tags.ThreeGpp;
using RecursiveAI.OneMedia.Tags.Xmp;

namespace RecursiveAI.OneMedia.Metadata;

/// <summary>Collects facts from native tags and XMP into candidate lists, using the documented interim precedence.</summary>
internal sealed class FactsBuilder(DefectLog defects)
{
    // Interim precedence ranks; lower wins. The gaps leave room for sources that arrive later (Matroska DateUTC is 50).
    public const int RankAppleCreationDate = 10;
    public const int RankXmpWithZone = 20;
    public const int RankVendorCreationTime = 25;
    public const int RankDayTag = 30;
    public const int RankXmpWithoutZone = 40;
    public const int RankDateUtc = 50;
    public const int RankMovieHeader = 60;

    public CandidateList<MediaTime> Duration { get; } = new();

    public CandidateList<MediaDateTime> CaptureTime { get; } = new();

    public CandidateList<GeoLocation> Location { get; } = new();

    public CandidateList<string> Make { get; } = new();

    public CandidateList<string> Model { get; } = new();

    public CandidateList<string> Software { get; } = new();

    public CandidateList<VideoOrientation> Orientation { get; } = new();

    public CandidateList<FrameSize> FrameSize { get; } = new();

    public MediaFacts Build() => new(Duration.ToFact(), CaptureTime.ToFact(), Location.ToFact(), Make.ToFact(), Model.ToFact(), Software.ToFact(), Orientation.ToFact(), FrameSize.ToFact());

    public void AddNative(NativeTag t)
    {
        string? text = t.Kind is TagValueKind.Text ? t.Text : null;
        if (text is null)
        {
            return;
        }

        string source = $"{t.System}:{t.Key}";
        const string qt = "com.apple.quicktime.";
        switch (t.System)
        {
            case TagSystems.QuickTimeKeys:
                switch (t.Key)
                {
                    case qt + "creationdate":
                        AddTime(RankAppleCreationDate, t, text, source, "ISO 8601 date-time written by the Apple camera, with its UTC offset");
                        break;
                    case "creation_time":
                    case qt + "creation_time":
                        AddTime(RankVendorCreationTime, t, text, source, "ISO 8601 creation time");
                        break;
                    case qt + "location.ISO6709":
                        AddIso6709(10, t, text, source);
                        break;
                    case qt + "make":
                        AddString(Make, 10, t, text, source);
                        break;
                    case "com.android.manufacturer":
                        AddString(Make, 12, t, text, source);
                        break;
                    case qt + "model":
                        AddString(Model, 10, t, text, source);
                        break;
                    case "com.android.model":
                        AddString(Model, 12, t, text, source);
                        break;
                    case qt + "software":
                        AddString(Software, 10, t, text, source);
                        break;
                    case "com.android.version":
                        AddString(Software, 12, t, text, source);
                        break;
                }

                break;
            case TagSystems.QuickTimeUserData:
                switch (t.Key)
                {
                    case "©day":
                        AddTime(RankDayTag, t, text, source, "user-data date text");
                        break;
                    case "©xyz":
                        AddIso6709(20, t, text, source);
                        break;
                    case "©mak":
                        AddString(Make, 20, t, text, source);
                        break;
                    case "©mod":
                        AddString(Model, 20, t, text, source);
                        break;
                    case "©swr":
                        AddString(Software, 20, t, text, source);
                        break;
                }

                break;
            case TagSystems.Matroska when t.TrackId == 0 && string.Equals(t.Key, "DATE_RECORDED", StringComparison.OrdinalIgnoreCase):
                AddTime(45, t, text, source, "Matroska DATE_RECORDED tag (ISO 8601 or a partial date)");
                break;
            case TagSystems.ITunes when t.Key == "©day":
                AddTime(RankDayTag, t, text, source, "iTunes date text");
                break;
            case TagSystems.RiffInfo:
                switch (t.Key)
                {
                    case "IDIT":
                        // The digitisation time, written either in the C library's ctime layout or as an ISO-like date; neither has a time zone.
                        if (TryParseCtime(text, out MediaDateTime ctime))
                        {
                            CaptureTime.Add(RankDateUtc - 5, new FactCandidate<MediaDateTime>(ctime, source, text.Trim(), "IDIT in the ctime layout (weekday month day time year), local time of the writer", TimeZoneCertainty.Unknown, t.ValueRange));
                        }
                        else
                        {
                            AddTime(RankDateUtc - 5, t, text, source, "IDIT date-time, local time of the writer");
                        }

                        break;
                    case "ICRD":
                        AddTime(RankDayTag, t, text, source, "RIFF INFO creation date");
                        break;
                    case "ISFT":
                        AddString(Software, 20, t, text.Trim(), source);
                        break;
                }

                break;
        }
    }

    public void AddLocationBox(ThreeGppLocation loc)
    {
        // A location box with all zero coordinates is how some writers say "unknown".
        if (loc.Latitude == 0 && loc.Longitude == 0 && loc.Altitude == 0)
        {
            return;
        }

        if (Math.Abs(loc.Latitude) > 90 || Math.Abs(loc.Longitude) > 180)
        {
            defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, loc.Range.Offset, "The 3GPP location box has coordinates out of range.", "tags.3gpp-location");
            return;
        }

        Location.Add(30, new FactCandidate<GeoLocation>(new GeoLocation(loc.Latitude, loc.Longitude, loc.Altitude), "3gpp:loci",
            string.Create(CultureInfo.InvariantCulture, $"{loc.Latitude} {loc.Longitude} {loc.Altitude}"), "16.16 fixed-point degrees and metres of the location box (role " + loc.Role + ")", Range: loc.Range));
    }

    public void AddXmp(XmpPacket x)
    {
        foreach ((string ns, string name) in new[] { (XmpNamespaces.Exif, "DateTimeOriginal"), (XmpNamespaces.Xmp, "CreateDate"), (XmpNamespaces.Photoshop, "DateCreated") })
        {
            if (x.Find(ns, name) is { } p && MediaDateTime.TryParse(p.Value, out MediaDateTime dt, out bool dateOnly))
            {
                bool zone = dt.Certainty is TimeZoneCertainty.Offset or TimeZoneCertainty.Utc;
                CaptureTime.Add(zone ? RankXmpWithZone : RankXmpWithoutZone, new FactCandidate<MediaDateTime>(dt, $"xmp:{p.Key}", p.Value,
                    dateOnly ? "XMP date without a time of day" : zone ? "XMP date-time with a zone" : "XMP date-time without a zone", dt.Certainty, x.PacketRange));
            }
        }

        if (x.Find(XmpNamespaces.Exif, "GPSLatitude") is { } lat && x.Find(XmpNamespaces.Exif, "GPSLongitude") is { } lon)
        {
            if (GeoLocation.TryParseXmp(lat.Value, lon.Value, out GeoLocation g))
            {
                Location.Add(40, new FactCandidate<GeoLocation>(g, "xmp:exif:GPSLatitude+GPSLongitude", lat.Value + " " + lon.Value, "XMP degrees, minutes and hemisphere", Range: x.PacketRange));
            }
            else
            {
                defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, x.PacketRange.Offset, "The XMP GPS position is not a valid coordinate pair.", "tags.xmp-gps");
            }
        }

        AddXmpString(Make, x, XmpNamespaces.Tiff, "Make");
        AddXmpString(Model, x, XmpNamespaces.Tiff, "Model");
        AddXmpString(Software, x, XmpNamespaces.Xmp, "CreatorTool");
    }

    private static void AddXmpString(CandidateList<string> list, XmpPacket x, string ns, string name)
    {
        if (x.Find(ns, name) is { } p && p.Value.Length > 0)
        {
            list.Add(30, new FactCandidate<string>(p.Value, "xmp:" + p.Key, p.Value, "XMP property text", Range: x.PacketRange));
        }
    }

    private static void AddString(CandidateList<string> list, int rank, NativeTag t, string text, string source)
    {
        if (text.Length > 0)
        {
            list.Add(rank, new FactCandidate<string>(text, source, text, "text as stored", Range: t.ValueRange));
        }
    }

    private void AddTime(int rank, NativeTag t, string text, string source, string how)
    {
        if (!MediaDateTime.TryParse(text, out MediaDateTime dt, out bool dateOnly))
        {
            // A bare year, or free text: not a capture time. The common view still shows it as the date.
            if (text.Length > 4)
            {
                defects.Add(DefectKind.InvalidValue, DefectSeverity.Info, t.ValueRange.Offset, $"'{t.Key}' is not a date-time: '{Shorten(text)}'.", "tags.datetime");
            }

            return;
        }

        CaptureTime.Add(rank, new FactCandidate<MediaDateTime>(dt, source, text, dateOnly ? how + "; date only, midnight assumed" : how, dt.Certainty, t.ValueRange));
    }

    private void AddIso6709(int rank, NativeTag t, string text, string source)
    {
        if (GeoLocation.TryParseIso6709(text, out GeoLocation g))
        {
            Location.Add(rank, new FactCandidate<GeoLocation>(g, source, text, "ISO 6709 latitude, longitude and optional altitude", Range: t.ValueRange));
        }
        else
        {
            defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, t.ValueRange.Offset, $"'{t.Key}' is not an ISO 6709 position: '{Shorten(text)}'.", "tags.iso6709");
        }
    }

    private static readonly string[] MonthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    // "Tue Mar 08 21:11:35 2005": weekday, month name, day, time, year (the layout of the C library's ctime).
    private static bool TryParseCtime(string text, out MediaDateTime value)
    {
        value = default;
        string[] parts = text.Split([' ', '\n', '\r', '\0', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            return false;
        }

        int month = Array.FindIndex(MonthNames, m => string.Equals(m, parts[1], StringComparison.OrdinalIgnoreCase)) + 1;
        string[] hms = parts[3].Split(':');
        if (month == 0 || hms.Length != 3
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int day)
            || !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out int year)
            || !int.TryParse(hms[0], NumberStyles.None, CultureInfo.InvariantCulture, out int h)
            || !int.TryParse(hms[1], NumberStyles.None, CultureInfo.InvariantCulture, out int m)
            || !int.TryParse(hms[2], NumberStyles.None, CultureInfo.InvariantCulture, out int sec)
            || year is < 1 or > 9999 || day < 1 || day > DateTime.DaysInMonth(year, month) || h > 23 || m > 59 || sec > 59)
        {
            return false;
        }

        value = new MediaDateTime(new DateTime(year, month, day, h, m, sec, DateTimeKind.Unspecified), null, TimeZoneCertainty.Unknown);
        return true;
    }

    private static string Shorten(string s) => s.Length <= 60 ? s : s[..60] + "…";
}
