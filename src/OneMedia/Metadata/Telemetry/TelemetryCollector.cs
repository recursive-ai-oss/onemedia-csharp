namespace RecursiveAI.OneMedia.Metadata.Telemetry;

/// <summary>What the decoders write into while a packet is decoded: the items of the packet and the streams discovered so far.</summary>
internal sealed class TelemetryCollector
{
    private TelemetryItem[] _items = new TelemetryItem[64];

    public TelemetryCollector(DefectLog defects, long trackId)
    {
        Defects = defects;
        TrackId = trackId;
    }

    public List<TelemetryChannelInfo> Channels { get; } = [];

    public DefectLog Defects { get; }

    public long TrackId { get; }

    /// <summary>Most items kept from one packet.</summary>
    public const int MaxItems = 65536;

    public int Count { get; private set; }

    public bool Overflowed { get; set; }

    public TelemetryItem[] Items => _items;

    public void Clear() => Count = 0;

    /// <summary>Appends a zeroed item for <paramref name="stream"/>, growing the buffer when needed (it keeps its size for later packets).</summary>
    public ref TelemetryItem Add(TelemetryChannelInfo stream)
    {
        if (Count == MaxItems)
        {
            // A packet holding more samples than the cap keeps the first ones; the rest overwrite the last slot and are reported once by the reader.
            Overflowed = true;
            ref TelemetryItem last = ref _items[Count - 1];
            last = default;
            last.ChannelIndex = stream.Index;
            last.Format = stream.Format;
            last.Kind = stream.Kind;
            return ref last;
        }

        if (Count == _items.Length)
        {
            Array.Resize(ref _items, Math.Min(_items.Length * 2, MaxItems));
        }

        ref TelemetryItem item = ref _items[Count++];
        item = default;
        item.ChannelIndex = stream.Index;
        item.Format = stream.Format;
        item.Kind = stream.Kind;
        stream.SampleCount++;
        return ref item;
    }

    public TelemetryChannelInfo? Find(TelemetryFormat format, uint tag, uint key, int variant)
    {
        foreach (TelemetryChannelInfo s in Channels)
        {
            if (s.Format == format && s.Tag == tag && s.Key == key && s.Variant == variant)
            {
                return s;
            }
        }

        return null;
    }

    public TelemetryChannelInfo Create(TelemetryFormat format, TelemetryKind kind, string name, uint tag, uint key, int variant)
    {
        TelemetryChannelInfo s = new(Channels.Count, format, kind, name) { Tag = tag, Key = key, Variant = variant, };
        s.TrackId = TrackId;
        Channels.Add(s);
        return s;
    }
}

/// <summary>Sample times inside a packet.</summary>
internal static class TelemetryTime
{
    /// <summary>
    /// Spreads the <paramref name="count"/> samples of a packet evenly over the packet's duration: sample <paramref name="index"/> starts at
    /// <c>pts + duration × index / count</c>, exactly, in a timescale of <c>packet timescale × count</c>.
    /// </summary>
    public static void Spread(MediaTime pts, MediaTime duration, int index, int count, out MediaTime start, out MediaTime each)
    {
        if (!pts.IsKnown)
        {
            start = MediaTime.Unknown;
            each = MediaTime.Unknown;
            return;
        }

        if (!duration.IsKnown || count <= 0 || duration.Timescale != pts.Timescale)
        {
            start = pts;
            each = count > 0 && duration.IsKnown ? new MediaTime(duration.Ticks, duration.Timescale * count) : MediaTime.Unknown;
            if (duration.IsKnown && duration.Timescale != pts.Timescale && count > 0)
            {
                start = pts + new MediaTime(duration.Ticks * index, duration.Timescale * count);
            }

            return;
        }

        long scale = pts.Timescale;
        if (scale > int.MaxValue / 2 || count > 65536)
        {
            start = pts;
            each = new MediaTime(duration.Ticks, scale * count);
            return;
        }

        // Fits easily: ticks stay below 2^47 for any real file, so the products below cannot overflow a long.
        start = new MediaTime((pts.Ticks * count) + (duration.Ticks * index), scale * count);
        each = new MediaTime(duration.Ticks, scale * count);
    }
}
