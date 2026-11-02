using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneMedia.Metadata.Telemetry;

/// <summary>The telemetry encoding a track uses.</summary>
public enum TelemetryFormat
{
    /// <summary>Not a telemetry track.</summary>
    None = 0,

    /// <summary>GoPro GPMF in a <c>gpmd</c> sample entry.</summary>
    Gpmf,

    /// <summary>Google camera motion metadata in a <c>camm</c> sample entry.</summary>
    Camm,

    /// <summary>Apple QuickTime timed metadata in a <c>mebx</c> sample entry.</summary>
    Mebx,

    /// <summary>The per-frame text DJI drones write into subtitle tracks and <c>.SRT</c> sidecars.</summary>
    DjiText,

    /// <summary>DJI protobuf metadata in a <c>djmd</c> (or <c>dbgi</c>) data track.</summary>
    Djmd,

    /// <summary>AVCHD camera data (MDPM) in the SEI messages of an H.264 video track of an MPEG transport stream.</summary>
    Mdpm,
}

/// <summary>Fixed storage for the numeric components of one telemetry item (no allocation per sample).</summary>
[InlineArray(TelemetryItem.MaxComponents)]
public struct TelemetryComponents
{
    private double _element0;
}

/// <summary>
/// One decoded telemetry sample as the allocation-free pull path delivers it. The numeric components sit inline; what each position
/// means depends on <see cref="Kind"/> and is listed in <c>docs/telemetry.md</c> (for <see cref="TelemetryKind.Gps"/>: latitude, longitude,
/// altitude, 2D speed, 3D speed, dilution of precision (NaN when unknown), fix quality, then format extras).
/// </summary>
public struct TelemetryItem
{
    /// <summary>Most numeric components one item carries; a GPMF structure with more components is delivered as a raw item.</summary>
    public const int MaxComponents = 14;

    private TelemetryComponents _values;

    /// <summary>What the sample measures; <see cref="TelemetryKind.Unknown"/> for a raw item.</summary>
    public TelemetryKind Kind { get; internal set; }

    /// <summary>The encoding the item came from.</summary>
    public TelemetryFormat Format { get; internal set; }

    /// <summary>Index into the reader's <see cref="TelemetryTrackReader.Channels"/>.</summary>
    public int ChannelIndex { get; internal set; }

    /// <summary>Where the sample starts on the media timeline of the file, exact.</summary>
    public MediaTime Time { get; internal set; }

    /// <summary>How long the sample applies, or <see cref="MediaTime.Unknown"/>.</summary>
    public MediaTime Duration { get; internal set; }

    /// <summary>The bytes in the file the sample was decoded from (the whole structure for one GPMF sample), or <see cref="ByteRange.None"/>.</summary>
    public ByteRange Range { get; internal set; }

    /// <summary>Number of valid entries in <see cref="Values"/>.</summary>
    public int Count { get; internal set; }

    /// <summary>The UTC time the device reported for this sample (GPS time), as <see cref="DateTime.Ticks"/>, or 0 when there is none.</summary>
    public long GpsTimeTicks { get; internal set; }

    /// <summary>The fix quality of a GPS sample.</summary>
    public GpsFixKind Fix { get; internal set; }

    /// <summary>For a raw item: where its payload sits in the packet delivered by <see cref="TelemetryTrackReader.RawData"/>; else 0.</summary>
    public int RawOffset { get; internal set; }

    /// <summary>For a raw item: payload length, else 0.</summary>
    public int RawLength { get; internal set; }

    /// <summary>For a raw item: the format's data type code (a GPMF type character, a <c>mebx</c> well-known type number), else 0.</summary>
    public int TypeCode { get; internal set; }

    /// <summary>The numeric components, <see cref="Count"/> of them. Valid while the item is in scope.</summary>
    public ReadOnlySpan<double> Values
    {
        [UnscopedRef]
        get => ((ReadOnlySpan<double>)_values)[..Count];
    }

    internal Span<double> Writable
    {
        [UnscopedRef]
        get => _values;
    }

    /// <summary>True when the item carries no typed values but the payload as stored (see <see cref="TelemetryTrackReader.RawData"/>).</summary>
    public readonly bool IsRaw => Kind == TelemetryKind.Unknown;
}

/// <summary>A stream the reader has found: samples of one key from one device (GPMF), one packet type (CAMM) or one declared key (<c>mebx</c>).</summary>
public sealed class TelemetryChannelInfo
{
    internal TelemetryChannelInfo(int index, TelemetryFormat format, TelemetryKind kind, string name)
    {
        Index = index;
        Format = format;
        Kind = kind;
        Name = name;
    }

    /// <summary>Position in <see cref="TelemetryTrackReader.Channels"/>; what <see cref="TelemetryItem.ChannelIndex"/> refers to.</summary>
    public int Index { get; }

    /// <summary>The encoding.</summary>
    public TelemetryFormat Format { get; }

    /// <summary>What its samples measure.</summary>
    public TelemetryKind Kind { get; }

    /// <summary>The format's name: a GPMF four-character key (<c>GPS5</c>), <c>camm type 6</c>, or a <c>mebx</c> key (<c>com.apple.quicktime.location.ISO6709</c>).</summary>
    public string Name { get; }

    /// <summary>Human-readable label (GPMF <c>STNM</c>), or empty.</summary>
    public string Label { get; internal set; } = string.Empty;

    /// <summary>Unit per component as stored (GPMF <c>SIUN</c>/<c>UNIT</c> split by component), or empty.</summary>
    public IReadOnlyList<string> Units { get; internal set; } = [];

    /// <summary>GPMF device id (<c>DVID</c>) the stream belongs to; 0 for other formats.</summary>
    public uint DeviceId { get; internal set; }

    /// <summary>GPMF device name (<c>DVNM</c>), or empty.</summary>
    public string DeviceName { get; internal set; } = string.Empty;

    /// <summary>GPMF input axis order (<c>ORIN</c>, for example <c>ZXY</c>) when the stream states one: the order the components are stored in; else empty.</summary>
    public string AxisOrder { get; internal set; } = string.Empty;

    /// <summary>GPMF output axis order (<c>ORIO</c>, for example <c>ZXY</c>) the camera asks for when the stream states one; else empty. See <see cref="Gpmf.GpmfAxes"/>.</summary>
    public string AxisOutputOrder { get; internal set; } = string.Empty;

    /// <summary>The ISO track id the data came from.</summary>
    public long TrackId { get; internal set; }

    /// <summary>Samples delivered so far.</summary>
    public long SampleCount { get; internal set; }

    internal uint Key { get; init; }

    internal uint Tag { get; init; }

    internal int Variant { get; init; }
}
