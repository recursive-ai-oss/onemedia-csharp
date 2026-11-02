namespace RecursiveAI.OneMedia.Metadata;

/// <summary>What a telemetry stream measures.</summary>
public enum TelemetryKind
{
    /// <summary>The stream's samples are <see cref="RawTelemetrySample"/>s of a kind this library does not decode.</summary>
    Unknown = 0,

    /// <summary>Position fixes: <see cref="GpsSample"/>.</summary>
    Gps,

    /// <summary>Linear acceleration: <see cref="Vector3Sample"/> in metres per second squared unless <see cref="TelemetryChannel.Units"/> says otherwise.</summary>
    Accelerometer,

    /// <summary>Angular velocity: <see cref="Vector3Sample"/> in radians per second unless <see cref="TelemetryChannel.Units"/> says otherwise.</summary>
    Gyroscope,

    /// <summary>Magnetic field: <see cref="Vector3Sample"/> in microtesla unless <see cref="TelemetryChannel.Units"/> says otherwise.</summary>
    Magnetometer,

    /// <summary>Gravity direction: <see cref="Vector3Sample"/> (unit vector, device frame).</summary>
    Gravity,

    /// <summary>Device attitude: <see cref="OrientationSample"/>.</summary>
    Orientation,

    /// <summary>Exposure and lens settings per frame or interval: <see cref="CameraSettingsSample"/>.</summary>
    CameraSettings,

    /// <summary>Other scalar measurements (temperature, barometric pressure, face boxes…) as <see cref="ScalarSample"/>.</summary>
    Scalar,
}

/// <summary>Quality of a position fix.</summary>
public enum GpsFixKind
{
    /// <summary>The device did not say, or the fix is invalid.</summary>
    None = 0,

    /// <summary>Latitude and longitude only.</summary>
    Fix2D = 2,

    /// <summary>Latitude, longitude and altitude.</summary>
    Fix3D = 3,
}

/// <summary>
/// A channel of timed samples of one kind decoded from a file (a GoPro GPMF stream, a CAMM track, an Apple <c>mebx</c> track, a DJI subtitle
/// track or sidecar). Decoding is lazy where the decoder can manage it: <see cref="Samples"/> may be backed by the file and read on enumeration.
/// </summary>
public sealed class TelemetryChannel
{
    /// <summary>Creates a stream description.</summary>
    /// <param name="kind">What the samples measure.</param>
    /// <param name="format">Where the data comes from: <c>gpmf</c>, <c>camm</c>, <c>mebx</c>, <c>dji-srt</c>, <c>mdpm</c>.</param>
    /// <param name="name">A short name for the stream as the format calls it (<c>GPS5</c>, <c>ACCL</c>, <c>camm type 6</c>).</param>
    /// <param name="samples">The samples in time order.</param>
    public TelemetryChannel(TelemetryKind kind, string format, string name, IReadOnlyList<TelemetrySample> samples)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(samples);
        Kind = kind;
        Format = format;
        Name = name;
        Samples = samples;
    }

    /// <summary>What the samples measure.</summary>
    public TelemetryKind Kind { get; }

    /// <summary>The data format: <c>gpmf</c>, <c>camm</c>, <c>mebx</c>, <c>dji-srt</c> or <c>mdpm</c>.</summary>
    public string Format { get; }

    /// <summary>The format's own name for the stream (a GPMF four-character key, a CAMM packet type, a <c>mebx</c> key).</summary>
    public string Name { get; }

    /// <summary>The container track the data came from, or -1 for data inside a track's samples or in a sidecar. Defaults to -1.</summary>
    public long TrackId { get; init; } = -1;

    /// <summary>A human-readable label (the GPMF <c>STNM</c> text, a <c>mebx</c> key description), or empty.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Unit of each component, in the order the sample type documents (for example <c>deg</c>, <c>deg</c>, <c>m</c>); empty when the defaults of <see cref="Kind"/> apply.</summary>
    public IReadOnlyList<string> Units { get; init; } = [];

    /// <summary>The nominal sample rate in hertz as the format states it, or 0 when it does not. A display value, never used for timing.</summary>
    public double NominalRateHz { get; init; }

    /// <summary>Where the stream's data lies in the file (or sidecar), or <see cref="ByteRange.None"/> when it is scattered.</summary>
    public ByteRange Range { get; init; } = ByteRange.None;

    /// <summary>The samples in time order. Every <see cref="TelemetrySample.Time"/> is on the media timeline of the file.</summary>
    public IReadOnlyList<TelemetrySample> Samples { get; }

    /// <summary>Deviations found while decoding this stream (stream-level; file-level defects go to the file's log).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>One telemetry sample. The derived types carry the typed values.</summary>
/// <param name="Time">Where the sample starts on the media timeline of the file.</param>
/// <param name="Duration">How long the sample applies, or <see cref="MediaTime.Unknown"/> when the format does not say.</param>
/// <param name="Range">The bytes the sample was decoded from, or <see cref="ByteRange.None"/>.</param>
public abstract record TelemetrySample(MediaTime Time, MediaTime Duration, ByteRange Range);

/// <summary>A position fix.</summary>
/// <param name="Time">Sample start on the media timeline.</param>
/// <param name="Duration">Sample duration, or unknown.</param>
/// <param name="Range">Source bytes.</param>
/// <param name="Latitude">Degrees north (negative south).</param>
/// <param name="Longitude">Degrees east (negative west).</param>
/// <param name="Altitude">Metres above the ellipsoid or sea level as the format states (see the stream's notes), or <c>null</c>.</param>
/// <param name="Speed2D">Ground speed in metres per second, or <c>null</c>.</param>
/// <param name="Speed3D">Speed in three dimensions in metres per second, or <c>null</c>.</param>
/// <param name="Fix">The fix quality.</param>
/// <param name="Dop">Dilution of precision (lower is better), or <c>null</c>.</param>
/// <param name="GpsTime">The UTC time of the fix as the GPS reported it, or <c>null</c>. This is the sample that makes the telemetry a capture-time candidate.</param>
public sealed record GpsSample(MediaTime Time, MediaTime Duration, ByteRange Range, double Latitude, double Longitude, double? Altitude = null, double? Speed2D = null,
    double? Speed3D = null, GpsFixKind Fix = GpsFixKind.None, double? Dop = null, DateTime? GpsTime = null) : TelemetrySample(Time, Duration, Range);

/// <summary>A three-axis reading: acceleration, angular velocity, magnetic field or gravity, as the stream's <see cref="TelemetryChannel.Kind"/> says.</summary>
/// <param name="Time">Sample start on the media timeline.</param>
/// <param name="Duration">Sample duration, or unknown.</param>
/// <param name="Range">Source bytes.</param>
/// <param name="X">First axis, in the stream's units and the format's axis order.</param>
/// <param name="Y">Second axis.</param>
/// <param name="Z">Third axis.</param>
public sealed record Vector3Sample(MediaTime Time, MediaTime Duration, ByteRange Range, double X, double Y, double Z) : TelemetrySample(Time, Duration, Range);

/// <summary>A device attitude, as a unit quaternion and/or Euler angles; whichever the format gives is set, the other is <c>null</c>.</summary>
/// <param name="Time">Sample start on the media timeline.</param>
/// <param name="Duration">Sample duration, or unknown.</param>
/// <param name="Range">Source bytes.</param>
/// <param name="W">Quaternion scalar part, or <c>null</c>.</param>
/// <param name="X">Quaternion x, or <c>null</c>.</param>
/// <param name="Y">Quaternion y, or <c>null</c>.</param>
/// <param name="Z">Quaternion z, or <c>null</c>.</param>
/// <param name="YawDegrees">Rotation about the vertical axis in degrees, or <c>null</c>.</param>
/// <param name="PitchDegrees">Rotation about the lateral axis in degrees, or <c>null</c>.</param>
/// <param name="RollDegrees">Rotation about the longitudinal axis in degrees, or <c>null</c>.</param>
public sealed record OrientationSample(MediaTime Time, MediaTime Duration, ByteRange Range, double? W = null, double? X = null, double? Y = null, double? Z = null,
    double? YawDegrees = null, double? PitchDegrees = null, double? RollDegrees = null) : TelemetrySample(Time, Duration, Range);

/// <summary>Exposure and lens settings. Only what the format states is set.</summary>
/// <param name="Time">Sample start on the media timeline.</param>
/// <param name="Duration">Sample duration, or unknown.</param>
/// <param name="Range">Source bytes.</param>
/// <param name="Iso">Sensor sensitivity, or <c>null</c>.</param>
/// <param name="ExposureTime">Shutter time as an exact fraction of a second (for example 1/1000), or <c>null</c>.</param>
/// <param name="FNumber">The aperture f-number, or <c>null</c>.</param>
/// <param name="ExposureCompensationEv">Exposure compensation in EV, or <c>null</c>.</param>
/// <param name="ColorTemperatureKelvin">White balance in kelvin, or <c>null</c>.</param>
/// <param name="FocalLengthMillimetres">Focal length in millimetres (35 mm equivalent only if <see cref="Notes"/> says so), or <c>null</c>.</param>
/// <param name="FocusDistanceMetres">Focus distance in metres, or <c>null</c>.</param>
/// <param name="Notes">Free text for settings the typed fields do not cover (a picture profile, a lens name).</param>
public sealed record CameraSettingsSample(MediaTime Time, MediaTime Duration, ByteRange Range, int? Iso = null, MediaTime? ExposureTime = null, double? FNumber = null,
    double? ExposureCompensationEv = null, int? ColorTemperatureKelvin = null, double? FocalLengthMillimetres = null, double? FocusDistanceMetres = null, string Notes = "")
    : TelemetrySample(Time, Duration, Range);

/// <summary>A scalar or small vector measurement of a kind with no dedicated sample type (temperature, pressure, a face rectangle).</summary>
/// <param name="Time">Sample start on the media timeline.</param>
/// <param name="Duration">Sample duration, or unknown.</param>
/// <param name="Range">Source bytes.</param>
/// <param name="Values">The components in the order the format documents; unit in <see cref="TelemetryChannel.Units"/>.</param>
public sealed record ScalarSample(MediaTime Time, MediaTime Duration, ByteRange Range, IReadOnlyList<double> Values) : TelemetrySample(Time, Duration, Range);

/// <summary>
/// The escape hatch: a sample the decoder found but does not interpret (an unknown GPMF key, a CAMM packet type from a newer revision, a
/// <c>mebx</c> key with an unknown data type). Nothing is lost: the type code and the bytes are kept.
/// </summary>
/// <param name="Time">Sample start on the media timeline, or <see cref="MediaTime.Unknown"/> when the format gives the item no time.</param>
/// <param name="Duration">Sample duration, or unknown.</param>
/// <param name="Range">Source bytes of <paramref name="Data"/> in the file, or none.</param>
/// <param name="Key">The format's key: a GPMF four-character code, a <c>mebx</c> key, a CAMM <c>type N</c>.</param>
/// <param name="TypeCode">The format's data type code (a GPMF type character, a <c>mebx</c> data-type tag), or empty.</param>
/// <param name="Data">The payload bytes as stored (not byte-swapped); owned by the sample.</param>
public sealed record RawTelemetrySample(MediaTime Time, MediaTime Duration, ByteRange Range, string Key, string TypeCode, ReadOnlyMemory<byte> Data) : TelemetrySample(Time, Duration, Range);
