using System.Globalization;

namespace RecursiveAI.OneMedia.Metadata.Telemetry;

/// <summary>
/// Decodes a file's telemetry tracks into <see cref="TelemetryChannel"/>s with typed samples. This reads every telemetry sample into memory;
/// use <see cref="TelemetryTrackReader"/> to stream a long recording in constant memory.
/// </summary>
public static class TelemetryChannels
{
    /// <summary>The tracks of a file that carry GPMF, CAMM or <c>mebx</c> telemetry.</summary>
    /// <param name="file">The open file.</param>
    /// <returns>The tracks in container order.</returns>
    public static IReadOnlyList<MediaTrack> TracksOf(MediaFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        List<MediaTrack> tracks = [];
        foreach (MediaTrack t in file.Tracks)
        {
            if (TelemetryTrackReader.FormatOf(t) != TelemetryFormat.None)
            {
                tracks.Add(t);
            }
        }

        return tracks;
    }

    /// <summary>
    /// Decodes every telemetry track. Channels appear in the order their first sample is met; samples of one channel are in time order.
    /// Raw samples (unknown GPMF keys, CAMM types and <c>mebx</c> values this library does not interpret, GPMF device fields such as the camera model)
    /// form their own channels of kind <see cref="TelemetryKind.Unknown"/>.
    /// </summary>
    /// <param name="file">The open file (synchronous source).</param>
    /// <returns>The channels; empty when the file has no telemetry track.</returns>
    /// <exception cref="MediaReadStateException">The file was opened from an asynchronous source.</exception>
    public static IReadOnlyList<TelemetryChannel> Read(MediaFile file)
    {
        List<TelemetryChannel> all = [];
        foreach (MediaTrack track in TracksOf(file))
        {
            all.AddRange(ReadTrack(file, track));
        }

        return all;
    }

    /// <summary>Decodes one telemetry track.</summary>
    /// <param name="file">The open file.</param>
    /// <param name="track">A track for which <see cref="TelemetryTrackReader.FormatOf"/> is not <see cref="TelemetryFormat.None"/>.</param>
    /// <returns>The track's channels.</returns>
    public static IReadOnlyList<TelemetryChannel> ReadTrack(MediaFile file, MediaTrack track)
    {
        using TelemetryTrackReader reader = TelemetryTrackReader.Open(file, track);
        List<List<TelemetrySample>> typed = [];
        List<List<TelemetrySample>> raw = [];
        while (reader.TryRead(out TelemetryItem item))
        {
            int ch = item.ChannelIndex;
            List<List<TelemetrySample>> target = item.IsRaw ? raw : typed;
            while (target.Count <= ch)
            {
                target.Add([]);
            }

            target[ch].Add(ToSample(reader, item, reader.Channels[ch]));
        }

        List<TelemetryChannel> result = [];
        for (int i = 0; i < reader.Channels.Count; i++)
        {
            TelemetryChannelInfo info = reader.Channels[i];
            if (i < typed.Count && typed[i].Count > 0)
            {
                result.Add(Build(track, info, info.Kind == TelemetryKind.Unknown ? TelemetryKind.Scalar : info.Kind, typed[i]));
            }

            if (i < raw.Count && raw[i].Count > 0)
            {
                result.Add(Build(track, info, TelemetryKind.Unknown, raw[i]));
            }
        }

        return result;
    }

    private static TelemetryChannel Build(MediaTrack track, TelemetryChannelInfo info, TelemetryKind kind, List<TelemetrySample> samples)
    {
        double rate = 0;
        if (samples.Count > 1 && samples[0].Time.IsKnown && samples[^1].Time.IsKnown)
        {
            // A display value only: samples per second between the first and last sample.
            MediaTime span = samples[^1].Time - samples[0].Time;
            double seconds = span.Ticks / (double)span.Timescale;
            if (seconds > 0)
            {
                rate = (samples.Count - 1) / seconds;
            }
        }

        string format = info.Format switch { TelemetryFormat.Gpmf => "gpmf", TelemetryFormat.Camm => "camm", _ => "mebx" };
        return new TelemetryChannel(kind, format, info.Name, samples)
        {
            TrackId = track.Id,
            Label = info.Label,
            Units = info.Units,
            NominalRateHz = rate,
        };
    }

    private static TelemetrySample ToSample(TelemetryTrackReader reader, in TelemetryItem item, TelemetryChannelInfo info)
    {
        if (item.IsRaw)
        {
            string type = item.Format == TelemetryFormat.Gpmf ? ((char)item.TypeCode).ToString() : item.TypeCode >= 0 ? item.TypeCode.ToString(CultureInfo.InvariantCulture) : string.Empty;
            return new RawTelemetrySample(item.Time, item.Duration, item.Range, info.Name, type, reader.RawData(item).ToArray());
        }

        ReadOnlySpan<double> v = item.Values;
        switch (item.Kind)
        {
            case TelemetryKind.Gps when v.Length >= 2:
                return new GpsSample(item.Time, item.Duration, item.Range, v[0], v[1], Opt(v, 2), Opt(v, 3), Opt(v, 4), item.Fix, Opt(v, 5),
                    item.GpsTimeTicks > 0 ? new DateTime(item.GpsTimeTicks, DateTimeKind.Utc) : null);
            case TelemetryKind.Accelerometer or TelemetryKind.Gyroscope or TelemetryKind.Magnetometer or TelemetryKind.Gravity when v.Length >= 3:
                return new Vector3Sample(item.Time, item.Duration, item.Range, v[0], v[1], v[2]);
            case TelemetryKind.Orientation when v.Length >= 4:
                return new OrientationSample(item.Time, item.Duration, item.Range, v[0], v[1], v[2], v[3]);
            case TelemetryKind.CameraSettings:
                return Settings(item, info, v);
            default:
                return new ScalarSample(item.Time, item.Duration, item.Range, v.ToArray());
        }
    }

    private static CameraSettingsSample Settings(in TelemetryItem item, TelemetryChannelInfo info, ReadOnlySpan<double> v)
    {
        double first = v.Length > 0 ? v[0] : double.NaN;
        if (item.Format == TelemetryFormat.Camm)
        {
            // CAMM type 1: pixel exposure time and rolling-shutter skew, both in nanoseconds.
            MediaTime exposure = new((long)first, 1_000_000_000);
            string note = v.Length > 1 ? "rolling-shutter-skew-ns=" + ((long)v[1]).ToString(CultureInfo.InvariantCulture) : string.Empty;
            return new CameraSettingsSample(item.Time, item.Duration, item.Range, ExposureTime: exposure, Notes: note);
        }

        return info.Name switch
        {
            // Shutter time in seconds as a float; nanosecond resolution is finer than the stored precision.
            "SHUT" => new CameraSettingsSample(item.Time, item.Duration, item.Range, ExposureTime: new MediaTime((long)Math.Round(first * 1e9), 1_000_000_000)),
            "ISOG" or "ISOE" => new CameraSettingsSample(item.Time, item.Duration, item.Range, Iso: (int)Math.Round(first)),
            _ => new CameraSettingsSample(item.Time, item.Duration, item.Range, ColorTemperatureKelvin: (int)Math.Round(first)),
        };
    }

    private static double? Opt(ReadOnlySpan<double> v, int i) => i < v.Length && !double.IsNaN(v[i]) ? v[i] : null;
}

/// <summary>Turns decoded telemetry into candidates for the facts of a file (the resolver in <c>MediaFacts</c> ranks them).</summary>
public static class TelemetryFacts
{
    /// <summary>
    /// The first position fix of the file's GPS channels that has a usable fix: a fix quality of 2D or 3D, or, when the device states no quality,
    /// a position that is not exactly 0°, 0°. One candidate per telemetry format that has GPS.
    /// </summary>
    /// <param name="channels">Channels from <see cref="TelemetryChannels.Read"/>.</param>
    /// <returns>Location candidates, earliest sample first within a format.</returns>
    public static IReadOnlyList<FactCandidate<GeoLocation>> LocationCandidates(IReadOnlyList<TelemetryChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        List<FactCandidate<GeoLocation>> result = [];
        HashSet<string> seen = [];
        foreach (TelemetryChannel c in channels)
        {
            if (c.Kind != TelemetryKind.Gps || seen.Contains(c.Format))
            {
                continue;
            }

            foreach (TelemetrySample s in c.Samples)
            {
                if (s is GpsSample g && (g.Fix != GpsFixKind.None || (c.Format != "gpmf" && (g.Latitude != 0 || g.Longitude != 0))))
                {
                    seen.Add(c.Format);
                    string raw = string.Create(CultureInfo.InvariantCulture, $"{g.Latitude:0.######} {g.Longitude:0.######}");
                    result.Add(new FactCandidate<GeoLocation>(new GeoLocation(g.Latitude, g.Longitude, g.Altitude), "telemetry:" + c.Format + ":" + c.Name, raw,
                        "first position fix of the telemetry track", TimeZoneCertainty.Utc, g.Range));
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>The earliest UTC time a GPS channel reports with a usable fix, as a capture-time candidate: the camera clock cannot say it better than the satellites.</summary>
    /// <param name="channels">Channels from <see cref="TelemetryChannels.Read"/>.</param>
    /// <returns>One candidate per telemetry format that reports GPS time.</returns>
    public static IReadOnlyList<FactCandidate<MediaDateTime>> CaptureTimeCandidates(IReadOnlyList<TelemetryChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        List<FactCandidate<MediaDateTime>> result = [];
        HashSet<string> seen = [];
        foreach (TelemetryChannel c in channels)
        {
            if (c.Kind != TelemetryKind.Gps || seen.Contains(c.Format))
            {
                continue;
            }

            foreach (TelemetrySample s in c.Samples)
            {
                if (s is GpsSample { GpsTime: { } t, Fix: not GpsFixKind.None } g)
                {
                    seen.Add(c.Format);
                    result.Add(new FactCandidate<MediaDateTime>(new MediaDateTime(t, null, TimeZoneCertainty.Utc), "telemetry:" + c.Format + ":" + c.Name,
                        t.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z", "GPS time of the first fix; the file time is the time the fix was taken, not the clip start", TimeZoneCertainty.Utc, g.Range));
                    break;
                }
            }
        }

        return result;
    }
}
