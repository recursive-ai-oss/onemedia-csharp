namespace RecursiveAI.OneMedia.Privacy;

/// <summary>
/// The tables that decide which native keys, telemetry keys and XMP properties hold location or a device identifier. Plain static tables: the
/// strip is data-driven and nothing here is looked up by reflection. Matching is on the key as the tag layer spells it (<see cref="Tags.NativeTag.Key"/>).
/// </summary>
internal static class StripRules
{
    // Atoms directly under udta whose whole payload is a location (QuickTime ©xyz, 3GPP TS 26.244 §8.3 loci).
    private static readonly string[] LocationAtoms = ["\u00A9xyz", "loci"];

    // GoPro udta atoms, observed in files from HERO5 to HERO8, MAX and Fusion: LENS holds the lens serial number as text, CAME the 16-byte hash of the
    // camera serial number, MUID and GUMI the media unique id, BCID a 32-byte id that was all zeros in every sample (kept in the list as a precaution).
    private static readonly string[] GoProDeviceAtoms = ["LENS", "CAME", "MUID", "GUMI", "BCID"];

    private static readonly string[] LocationWords =
        ["location", "iso6709", "gps", "latitude", "longitude", "geotag", "geolocation", "geo_location", "coordinates", "detected-face"];

    private static readonly string[] DeviceWords =
    [
        "serial", "uniqueid", "unique_id", "unique id", "contentid", "content.identifier", "content_identifier", "owner",
        "deviceid", "device_id", "device.id", "imei", "udid", "camera.identifier", "cameraid", "camera_id",
    ];

    // RIFF INFO IARL is the "archival location" of a work: a place, by definition (Microsoft RIFF INFO list).
    private static readonly string[] LocationRiff = ["IARL"];

    /// <summary>The category a native tag falls in, or <see cref="StripCategories.None"/>.</summary>
    public static StripCategories Classify(string system, string key)
    {
        if (system == Tags.TagSystems.QuickTimeUserData)
        {
            if (Array.IndexOf(LocationAtoms, key) >= 0)
            {
                return StripCategories.Location;
            }

            if (Array.IndexOf(GoProDeviceAtoms, key) >= 0)
            {
                return StripCategories.DeviceIds;
            }

            return StripCategories.None;
        }

        if (system == Tags.TagSystems.ITunes && Array.IndexOf(LocationAtoms, key) >= 0)
        {
            return StripCategories.Location;
        }

        if (system == Tags.TagSystems.ThreeGpp)
        {
            return Array.IndexOf(LocationAtoms, key) >= 0 ? StripCategories.Location : StripCategories.None;
        }

        if (system == Tags.TagSystems.RiffInfo && Array.IndexOf(LocationRiff, key) >= 0)
        {
            return StripCategories.Location;
        }

        if (system is Tags.TagSystems.Xmp or Tags.TagSystems.MatroskaInfo or Tags.TagSystems.Id3Chunk or Tags.TagSystems.Flac or Tags.TagSystems.Flv or Tags.TagSystems.Dv)
        {
            return StripCategories.None; // XMP has its own rules; the others hold no keyed location or identifier fields.
        }

        return ClassifyWords(key);
    }

    /// <summary>The category of a name by the words in it.</summary>
    public static StripCategories ClassifyWords(string name)
    {
        StripCategories c = StripCategories.None;
        if (ContainsAny(name, LocationWords))
        {
            c |= StripCategories.Location;
        }

        if (ContainsAny(name, DeviceWords))
        {
            c |= StripCategories.DeviceIds;
        }

        return c;
    }

    private static bool ContainsAny(string name, string[] words)
    {
        foreach (string w in words)
        {
            if (name.Contains(w, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // GPMF keys (GoPro's published GPMF documentation, plus the keys ExifTool's GoPro table names). GPS5 and GPS9 are the fixes, GPSU the UTC time of
    // the fix, GPSF the fix mode, GPSP the precision, GPSA the altitude system, GPRI and GLPI the raw and fused position streams, LNED the local
    // north-east-down position. CASN is the camera serial number, MUID the media unique id, LINF the lens serial number, CINF the 16-byte hash of the camera serial number.
    private static readonly string[] GpmfLocation = ["GPS5", "GPS9", "GPSU", "GPSF", "GPSP", "GPSA", "GPRI", "GLPI", "LNED"];
    private static readonly string[] GpmfDevice = ["CASN", "MUID", "LINF", "CINF"];

    /// <summary>The category of a GPMF key.</summary>
    public static StripCategories ClassifyGpmf(string key)
    {
        if (Array.IndexOf(GpmfLocation, key) >= 0)
        {
            return StripCategories.Location;
        }

        return Array.IndexOf(GpmfDevice, key) >= 0 ? StripCategories.DeviceIds : StripCategories.None;
    }

    /// <summary>The category of an XMP property.</summary>
    public static StripCategories ClassifyXmp(string ns, string name)
    {
        StripCategories c = ClassifyWords(name);
        // Photoshop and IPTC core place names.
        if (ns == "http://ns.adobe.com/photoshop/1.0/" && name is "City" or "State" or "Country")
        {
            c |= StripCategories.Location;
        }

        if (ns == "http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/" && name is "Location" or "CountryCode")
        {
            c |= StripCategories.Location;
        }

        // exif:ImageUniqueID is a number the camera generates per picture.
        if (ns == "http://ns.adobe.com/exif/1.0/" && name == "ImageUniqueID")
        {
            c |= StripCategories.DeviceIds;
        }

        return c;
    }
}
