using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Metadata.Telemetry.Camm;
using RecursiveAI.OneMedia.Metadata.Telemetry.Gpmf;
using RecursiveAI.OneMedia.Metadata.Telemetry.Mebx;

namespace RecursiveAI.OneMedia.Metadata.Telemetry;

/// <summary>
/// Reads the samples of one telemetry track (<c>gpmd</c>, <c>camm</c> or <c>mebx</c>) one at a time, in constant memory: only the track's own
/// packets are read, one packet is decoded at a time, and <see cref="TryRead(out TelemetryItem)"/> allocates nothing per sample once the
/// buffers have grown to the largest packet (a new channel allocates its name and units when it first appears). Not thread-safe.
/// </summary>
/// <remarks>
/// A reader owns the file's read position while it is alive: other tracks are switched off, and the file's other readers fail with
/// <see cref="MediaReadStateException"/> if they continue after this one started. Dispose the reader to switch the tracks back on.
/// </remarks>
public sealed class TelemetryTrackReader : IDisposable
{
    private readonly MediaFile _file;
    private readonly IDemuxer _demuxer;
    private readonly int _generation;
    private readonly TelemetryCollector _collector;
    private readonly GpmfDecoder? _gpmf;
    private readonly CammDecoder? _camm;
    private readonly MebxDecoder? _mebx;
    private byte[] _payload = new byte[4096];
    private int _payloadLength;
    private int _next;
    private bool _finished;
    private bool _disposed;

    private TelemetryTrackReader(MediaFile file, IDemuxer demuxer, MediaTrack track, TelemetryFormat format, MebxKeyTable[] tables, int generation)
    {
        _file = file;
        _demuxer = demuxer;
        Track = track;
        Format = format;
        _generation = generation;
        _collector = new TelemetryCollector(demuxer.Defects, track.Id);
        switch (format)
        {
            case TelemetryFormat.Gpmf:
                _gpmf = new GpmfDecoder(_collector);
                break;
            case TelemetryFormat.Camm:
                _camm = new CammDecoder(_collector);
                break;
            default:
                _mebx = new MebxDecoder(_collector, tables);
                break;
        }
    }

    /// <summary>The track being read.</summary>
    public MediaTrack Track { get; }

    /// <summary>The track's telemetry encoding.</summary>
    public TelemetryFormat Format { get; }

    /// <summary>The channels found so far. It grows while reading; a channel is added the first time one of its samples is delivered.</summary>
    public IReadOnlyList<TelemetryChannelInfo> Channels => _collector.Channels;

    /// <summary>Which telemetry encoding a track carries, from its sample entry; <see cref="TelemetryFormat.None"/> for any other track.</summary>
    /// <param name="track">The track.</param>
    /// <returns>The encoding.</returns>
    public static TelemetryFormat FormatOf(MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Kind != TrackKind.Data)
        {
            return TelemetryFormat.None;
        }

        return track.CodecTag switch
        {
            "gpmd" => TelemetryFormat.Gpmf,
            "camm" => TelemetryFormat.Camm,
            "mebx" => TelemetryFormat.Mebx,
            _ => TelemetryFormat.None,
        };
    }

    internal static TelemetryTrackReader Open(MediaFile file, MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(track);
        TelemetryFormat format = FormatOf(track);
        if (format == TelemetryFormat.None)
        {
            throw new ArgumentException("The track is not a GPMF, CAMM or mebx telemetry track.", nameof(track));
        }

        IDemuxer demuxer = file.Demuxer ?? throw new MediaReadStateException(MediaReadStateReason.AsynchronousSource, "This file was opened from an asynchronous source; telemetry is read from synchronous sources.");
        MebxKeyTable[] tables = [];
        if (format == TelemetryFormat.Mebx)
        {
            tables = KeyTables(demuxer, track);
        }

        foreach (MediaTrack t in demuxer.Tracks)
        {
            demuxer.SetTrackEnabled(t.Index, t.Index == track.Index);
        }

        int generation = file.BeginRead();
        return new TelemetryTrackReader(file, demuxer, track, format, tables, generation);
    }

    // One table per sample description of the track, in description order.
    private static MebxKeyTable[] KeyTables(IDemuxer demuxer, MediaTrack track)
    {
        List<MebxKeyTable> tables = [];
        if (demuxer is IsoBmffDemuxer iso)
        {
            foreach (IsoTrack t in iso.Movie.Tracks)
            {
                if (t.Header.TrackId != track.Id)
                {
                    continue;
                }

                foreach (SampleEntry e in t.SampleEntries)
                {
                    _ = MebxKeyTable.TryParse(e.Format == "mebx" ? e.Configuration.Data.Span : default, out MebxKeyTable table);
                    tables.Add(table);
                }

                break;
            }
        }

        if (tables.Count == 0)
        {
            _ = MebxKeyTable.TryParse(track.Configuration.Data.Span, out MebxKeyTable table);
            tables.Add(table);
        }

        return [.. tables];
    }

    /// <summary>
    /// Reads the next sample of the track. Samples come in the order of the track's packets, and within a packet in the order the format stores them
    /// (a GPMF payload lists its channels one after the other, so sample times interleave across channels but rise within each).
    /// </summary>
    /// <param name="item">The sample, when the result is true.</param>
    /// <returns>False at the end of the track.</returns>
    /// <exception cref="MediaReadStateException">Another read of the file started after this one.</exception>
    /// <exception cref="ObjectDisposedException">The reader was disposed.</exception>
    public bool TryRead(out TelemetryItem item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_next >= _collector.Count)
        {
            if (_finished || !ReadPacket())
            {
                _finished = true;
                item = default;
                return false;
            }
        }

        item = _collector.Items[_next++];
        return true;
    }

    /// <summary>The bytes of a raw item: its payload exactly as stored. Valid until the next call to <see cref="TryRead(out TelemetryItem)"/>.</summary>
    /// <param name="item">An item returned by the latest successful <see cref="TryRead(out TelemetryItem)"/>.</param>
    /// <returns>The payload; empty for a typed item.</returns>
    public ReadOnlySpan<byte> RawData(in TelemetryItem item) =>
        item.RawLength > 0 && item.RawOffset >= 0 && item.RawOffset + item.RawLength <= _payloadLength ? _payload.AsSpan(item.RawOffset, item.RawLength) : default;

    private bool ReadPacket()
    {
        while (true)
        {
            _file.EnsureCurrentRead(_generation);
            if (!_demuxer.TryReadPacket(out PacketView view))
            {
                return false;
            }

            if (view.Data.Length > _payload.Length)
            {
                _payload = new byte[Math.Max(view.Data.Length, _payload.Length * 2)];
            }

            view.Data.CopyTo(_payload);
            _payloadLength = view.Data.Length;
            _collector.Clear();
            _collector.Overflowed = false;
            _next = 0;
            long offset = view.Info.SourceRange.IsNone ? -1 : view.Info.SourceRange.Offset;
            ReadOnlySpan<byte> bytes = _payload.AsSpan(0, _payloadLength);
            if (_gpmf is not null)
            {
                _gpmf.Decode(bytes, offset, view.Info.Pts, view.Info.Duration);
            }
            else if (_camm is not null)
            {
                _camm.Decode(bytes, offset, view.Info.Pts, view.Info.Duration);
            }
            else
            {
                int entry = _demuxer is IsoBmffDemuxer iso ? iso.SampleDescriptionIndex - 1 : 0;
                _mebx!.Decode(bytes, offset, view.Info.Pts, view.Info.Duration, entry);
            }

            if (_collector.Overflowed)
            {
                _demuxer.Defects.Add(DefectKind.LimitReached, DefectSeverity.Warning, Math.Max(offset, 0), $"A telemetry sample holds more than {TelemetryCollector.MaxItems} items; the extra items were dropped.", "telemetry.items");
            }

            if (_collector.Count > 0)
            {
                return true;
            }
        }
    }

    /// <summary>Switches the file's other tracks back on.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _file.EnsureCurrentRead(_generation);
        }
        catch (MediaReadStateException)
        {
            return; // a newer read owns the track switches now.
        }

        foreach (MediaTrack t in _demuxer.Tracks)
        {
            _demuxer.SetTrackEnabled(t.Index, true);
        }
    }
}
