namespace RecursiveAI.OneMedia.Captions;

// Bitmap subtitles (Blu-ray PGS, DVD VobSub, DVB subtitles) decoded to RGBA images with timing and position. This is the one place the
// library produces pixels: run-length data becomes an image, nothing more. No scaling, no text, no rendering.

/// <summary>Helpers for the cues the bitmap subtitle decoders produce.</summary>
public static class BitmapSubtitleCues
{
    /// <summary>The <see cref="SubtitleExtra.Format"/> of cues from Blu-ray PGS.</summary>
    public const string Pgs = "pgs";

    /// <summary>The <see cref="SubtitleExtra.Format"/> of cues from DVD sub-pictures.</summary>
    public const string VobSub = "vobsub";

    /// <summary>The <see cref="SubtitleExtra.Format"/> of cues from DVB subtitles.</summary>
    public const string DvbSub = "dvbsub";

    /// <summary>True for a cue shown even when the viewer has subtitles turned off (DVD forced display, PGS forced objects): it carries the extra <c>forced=true</c>.</summary>
    /// <param name="cue">A cue from a bitmap subtitle decoder.</param>
    public static bool IsForced(SubtitleCue cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        foreach (SubtitleExtra e in cue.Extras)
        {
            if (e.Key == "forced" && e.Value == "true")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The bitmap system a cue came from (<see cref="Pgs"/>, <see cref="VobSub"/>, <see cref="DvbSub"/>), or null for any other cue.</summary>
    /// <param name="cue">A cue.</param>
    public static string? SystemOf(SubtitleCue cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        foreach (SubtitleExtra e in cue.Extras)
        {
            if (e.Key == "system")
            {
                return e.Format;
            }
        }

        return null;
    }

    internal static SubtitleCue Make(string system, MediaTime start, MediaTime end, List<SubtitleImage> images, bool forced, string? language, int stream = 0)
    {
        var extras = new List<SubtitleExtra> { new(system, "system", system) };
        if (forced)
        {
            extras.Add(new SubtitleExtra(system, "forced", "true"));
        }

        if (stream != 0)
        {
            extras.Add(new SubtitleExtra(system, "stream", stream.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return new SubtitleCue
        {
            Start = start,
            End = end,
            Language = language,
            Images = SubtitleList.From(images),
            Extras = SubtitleList.From(extras),
        };
    }
}

/// <summary>Which matrix turns YCbCr palette entries into RGB.</summary>
public enum YCbCrMatrix
{
    /// <summary>ITU-R BT.709 for pictures taller than 576 lines (high definition), BT.601 otherwise. The default.</summary>
    Auto = 0,

    /// <summary>ITU-R BT.601, standard definition.</summary>
    Bt601,

    /// <summary>ITU-R BT.709, high definition.</summary>
    Bt709,
}

/// <summary>Options for decoding bitmap subtitles.</summary>
public sealed record BitmapSubtitleOptions
{
    /// <summary>The matrix for palettes stored as YCbCr (PGS, DVB). Default <see cref="YCbCrMatrix.Auto"/>.</summary>
    public YCbCrMatrix Matrix { get; init; }

    /// <summary>
    /// The 16 colours of the DVD palette, for sub-pictures whose palette is not stored with them (a VOB carries it in the IFO file; a
    /// VobSub index carries it itself and wins). Null uses a fixed grey ramp. Entries beyond 16 are ignored.
    /// </summary>
    public IReadOnlyList<SubtitleColor>? Palette { get; init; }

    /// <summary>Display width for sources that do not state one (DVD). 0 uses 720.</summary>
    public int DisplayWidth { get; init; }

    /// <summary>Display height for sources that do not state one (DVD). 0 uses 576.</summary>
    public int DisplayHeight { get; init; }

    /// <summary>The DVB composition page to decode; null takes the first one the stream declares.</summary>
    public int? CompositionPageId { get; init; }

    /// <summary>The DVB ancillary page (shared CLUTs and objects); null also accepts pages that no composition uses.</summary>
    public int? AncillaryPageId { get; init; }

    /// <summary>Resource limits; null for <see cref="MediaLimits.Default"/>. Image size is checked against <see cref="MediaLimits.MaxAllocationBytes"/> before any pixel buffer is allocated.</summary>
    public MediaLimits? Limits { get; init; }

    /// <summary>The language to put on cues when the stream does not say.</summary>
    public string? Language { get; init; }

    /// <summary>For a multi-stream VobSub index, the stream to decode (its <c>index:</c> value); null decodes every stream.</summary>
    public int? Stream { get; init; }
}

// Shared helpers: limit checks before allocation, YCbCr to RGB, and the 90 kHz clock.
internal static class BitmapSupport
{
    // Every image dimension must stay below this; the allocation limit then bounds the area.
    internal const int MaxDimension = 16384;

    internal static void CheckImage(MediaLimits limits, long width, long height, long usedBytes, long offset)
    {
        if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension)
        {
            throw new MediaLimitExceededException("ImageDimension", MaxDimension, Math.Max(width, height), offset);
        }

        limits.CheckAllocation((width * height * 4) + usedBytes, offset);
    }

    internal static MediaTime Ticks90k(long ticks) => new(ticks, 90000);

    // Time arithmetic that does not fail on mixed time bases: exact in 90 kHz when the left base divides it, else rounded in the left base.
    internal static MediaTime Plus(MediaTime a, MediaTime b)
    {
        if (!a.IsKnown || !b.IsKnown)
        {
            return MediaTime.Unknown;
        }

        if (a.Timescale == b.Timescale)
        {
            return new MediaTime(a.Ticks + b.Ticks, a.Timescale);
        }

        return 90000 % a.Timescale == 0 ? a.Rescale(90000, MediaRounding.NearestAwayFromZero) + b.Rescale(90000, MediaRounding.NearestAwayFromZero) : a.Add(b, MediaRounding.NearestAwayFromZero);
    }

    // Straight alpha RGBA from limited-range YCbCr (ITU-R BT.601 / BT.709), fixed point with 16 fractional bits, clamped.
    internal static SubtitleColor FromYCbCr(int y, int cb, int cr, int alpha, bool bt709)
    {
        int yy = 76309 * (Math.Max(y, 16) - 16); // 255 / 219
        int d = cb - 128;
        int e = cr - 128;
        int r, g, b;
        if (bt709)
        {
            r = yy + (117489 * e);
            g = yy - (13975 * d) - (34925 * e);
            b = yy + (138438 * d);
        }
        else
        {
            r = yy + (104597 * e);
            g = yy - (25675 * d) - (53279 * e);
            b = yy + (132201 * d);
        }

        return new SubtitleColor(Clamp8((r + 32768) >> 16), Clamp8((g + 32768) >> 16), Clamp8((b + 32768) >> 16), (byte)alpha);
    }

    internal static bool UseBt709(YCbCrMatrix matrix, int displayHeight) => matrix switch
    {
        YCbCrMatrix.Bt601 => false,
        YCbCrMatrix.Bt709 => true,
        _ => displayHeight > 576,
    };

    private static byte Clamp8(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    internal static void Put(byte[] rgba, int index, SubtitleColor c)
    {
        int o = index * 4;
        rgba[o] = c.R;
        rgba[o + 1] = c.G;
        rgba[o + 2] = c.B;
        rgba[o + 3] = c.A;
    }
}
